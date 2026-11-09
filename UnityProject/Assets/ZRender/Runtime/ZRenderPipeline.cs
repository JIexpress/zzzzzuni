// ZRenderPipeline.cs —— 自研管线入口（TASK-B-UNITY-02：采样 ZRender 的共享纹理贴屏）。
//
// 分工（架构由主控定死，别改成"直接画 Unity backbuffer"）：
//   原生：ZRender 渲染到**自己的** BGRA8 共享纹理 → 转 PIXEL_SHADER_RESOURCE → fence 等完成
//         → 导出 NT 句柄（ZR_Unity_RenderFrame / GetSharedTargetHandle）
//   托管：把句柄包成外部纹理 → CommandBuffer.Blit 到 CameraTarget → Submit
//
// 为什么不是画 backbuffer：flip-model 下帧边界是 PRESENT（PRESENT→RENDER_TARGET 无隐式
// 提升），且 ZRender 自建 DIRECT 队列、WaitIdle 不覆盖 Unity 队列——状态与同步都会漏。
// 自己拥有纹理后，这两条同时消失：纹理只由 ZRender 的队列写，写完等完成，Unity 只采样。
//
// 门控：只在 D3D12 下启用。非 D3D12 明确回退（不渲染 + 明确日志），绝不在 D3D11 下硬跑。

using System;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>自研渲染管线实例；由 <see cref="ZRenderPipelineAsset"/> 创建。</summary>
    public sealed class ZRenderPipeline : RenderPipeline
    {
        // 目标尺寸随屏幕走（1:1 贴屏，不变形）；尺寸变化才重建共享纹理。
        private const int kMinDimension = 16;

        // 初始化失败后的重试间隔（帧）：60 帧 ≈ 1 秒，够用又不刷屏
        private const int kRetryIntervalFrames = 60;

        // 原生目标槽位数（TASK-B-UNITY-04 双缓冲）；与 zrhi_d3d12_unity_target.cpp 的
        // kTargetSlots 一一对应，改一处必须改另一处。
        private const int kTargetSlotCount = 2;

        // 双槽：ZRender 画 A 的同一帧里，宿主采样的是 B（= 上一帧画完的那份）。
        // 单槽做不到"不等本帧"——下一帧必然覆盖宿主还在读的那张纹理。
        private readonly Texture2D[] _externalTextures = new Texture2D[kTargetSlotCount];
        private int _readableTextureIndex;
        private int _createdExternalTextures;
        private int _destroyedExternalTextures;
        private CommandBuffer _commandBuffer;
        private Material _blitMaterial;
        private ZRenderSceneBridge _sceneBridge;
        private int _targetWidth;
        private int _targetHeight;
        private uint _lastLoggedFrameCount;
        private float _smoothedFps;
        private int _nextAttemptFrame;
        private bool _nativeReady;
        private bool _gateLogged;
        private bool _renderErrorLogged;

        /// <summary>已创建的外部纹理总数（诊断/测试：与销毁数之差必须 ≤ 槽位数）。</summary>
        internal int CreatedExternalTextureCount => _createdExternalTextures;

        /// <summary>已销毁的外部纹理总数。</summary>
        internal int DestroyedExternalTextureCount => _destroyedExternalTextures;

        /// <summary>存活的外部纹理数（Normal 时恒等于槽位数，尺寸变化后不增长）。</summary>
        internal int LiveExternalTextureCount => _createdExternalTextures - _destroyedExternalTextures;

        /// <summary>
        /// 测试接缝：跑一遍真实的释放路径。Unity 的 RenderPipeline 没有把 Dispose()
        /// 暴露成公开面（只有 protected Dispose(bool)），EditMode 用例需要一个入口。
        /// </summary>
        internal void DisposeForTests()
        {
            Dispose(true);
        }

        public ZRenderPipeline()
        {
            // 管线创建即触发原生库加载校验（失败快速抛出，带清晰信息）。
            _ = ZrNativeBridge.PluginInfo;
        }

        /// <summary>最近一次成功提交的帧号（原生 RenderFrame 真调用计数；诊断用）。</summary>
        public uint LastNativeFrameCount => _lastLoggedFrameCount;

        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            // ---- 门控：只在 D3D12 下跑 -------------------------------------------------
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                if (!_gateLogged)
                {
                    _gateLogged = true;
                    Debug.LogWarning(
                        $"[ZRender] 当前图形 API = {SystemInfo.graphicsDeviceType}，不是 D3D12：" +
                        "ZRender 不启用，画面回退 URP（请在 ProjectSettings 里把 Windows 图形 API 设为 D3D12）。");
                }

                context.Submit();
                return;
            }

            if (cameras == null || cameras.Length == 0)
            {
                context.Submit();
                return;
            }

            if (!EnsureNativeTarget())
            {
                context.Submit();
                return;
            }

            // ---- 把本帧的场景内容交给原生（TASK-B-UNITY-03）-----------------------------
            // 没有可见物件时原生回落画 cube 演示（B-UNITY-02 的既有行为，--draw 同源），
            // 因此这里的失败不阻断渲染：只影响"画什么"。
            _sceneBridge ??= new ZRenderSceneBridge();
            _sceneBridge.Submit(cameras[0]);

            int renderResult = ZrUnityTargetNative.RenderFrame();
            if (renderResult != ZrUnityTargetNative.Ok)
            {
                if (!_renderErrorLogged)
                {
                    _renderErrorLogged = true;
                    Debug.LogError(
                        $"[ZRender] ZR_Unity_RenderFrame 失败（{renderResult}）；本帧不贴屏。");
                }

                context.Submit();
                return;
            }

            // ---- 采样外部纹理 → 贴到相机目标 -------------------------------------------
            if (_blitMaterial == null || _commandBuffer == null)
            {
                context.Submit();
                return;
            }

            // 采样哪一张：原生说"上一帧画完的那个槽位"（TASK-B-UNITY-04）。
            // 本帧正在写另一槽，所以这里读到的永远是完整画面（不撕裂）。
            if (ZrUnityTargetNative.GetReadableTargetIndex(out uint readableSlot)
                == ZrUnityTargetNative.Ok && readableSlot < kTargetSlotCount)
            {
                _readableTextureIndex = (int)readableSlot;
            }

            Texture2D source = _externalTextures[_readableTextureIndex]
                ?? _externalTextures[0];
            if (source == null)
            {
                context.Submit();
                return;
            }

            // SRP 约定：提交任何绘制/拷贝前先建立相机属性（否则 CameraTarget 解析不到
            // 有效的渲染目标，Unity 侧会在内部空指针——实测直接崩在 UnityPlayer）。
            context.SetupCameraProperties(cameras[0]);

            _commandBuffer.Clear();
            _commandBuffer.Blit(source, BuiltinRenderTextureType.CameraTarget, _blitMaterial);
            context.ExecuteCommandBuffer(_commandBuffer);
            context.Submit();

            LogFrameProgress();
        }

        /// <summary>
        /// 首次使用时接入宿主设备 + 建共享目标 + 建外部纹理；之后只在屏幕尺寸变化时重建。
        /// 外部纹理**必须只在建目标时创建**：每帧 CreateExternalTexture 会泄漏（任务判据 V4）。
        /// </summary>
        private bool EnsureNativeTarget()
        {
            return EnsureNativeTargetForSize(Mathf.Max(kMinDimension, Screen.width),
                Mathf.Max(kMinDimension, Screen.height));
        }

        /// <summary>
        /// 按给定尺寸确保共享目标 + 外部纹理就绪（尺寸变化则重建）。
        /// 尺寸由调用方给（而不是直接读 Screen.*）是为了让 EditMode 能验证"连续改尺寸
        /// 不涨纹理对象数"——见 ZRenderPipelineTextureLifecycleTests。
        /// </summary>
        internal bool EnsureNativeTargetForSize(int width, int height)
        {
            if (_externalTextures[0] != null && _targetWidth == width && _targetHeight == height)
            {
                return true;
            }

            // 失败退避：每 60 帧才再试一次，日志因此不会每帧刷屏（日志在失败点打）
            if (Time.frameCount < _nextAttemptFrame)
            {
                return false;
            }

            try
            {
                if (!AttachHostDevice())
                {
                    _nextAttemptFrame = Time.frameCount + kRetryIntervalFrames;
                    return false;
                }

                if (_externalTextures[0] != null)
                {
                    // 尺寸变化（TASK-B-UNITY-04 修泄漏）：**先销毁旧的外部纹理，再放原生资源**。
                    // 顺序不能反：Unity 这张 Texture2D 还引用着旧 ID3D12Resource，先放原生资源
                    // 会让 Unity 持有悬空指针；反过来（先 Destroy 纹理）Unity 才会松开引用。
                    ReleaseExternalTextures();
                    ZrUnityTargetNative.ReleaseSharedTarget();
                }

                int createResult = ZrUnityTargetNative.CreateSharedTarget((uint)width, (uint)height);
                if (createResult != ZrUnityTargetNative.Ok)
                {
                    Debug.LogError($"[ZRender] ZR_Unity_CreateSharedTarget({width}x{height}) 失败：{createResult}");
                    _nextAttemptFrame = Time.frameCount + kRetryIntervalFrames;
                    return false;
                }

                // 每个槽位一张外部纹理：同进程内 Unity 需要的是 ID3D12Resource*（NT 句柄
                // 另存，供跨进程/跨 API 使用；见 zplugin.h 的说明）
                ulong handle;
                int handleResult = ZrUnityTargetNative.GetSharedTargetHandle(out handle);
                for (int slot = 0; slot < kTargetSlotCount; slot++)
                {
                    int resourceResult = ZrUnityTargetNative.GetTargetResourceAt(
                        (uint)slot, out IntPtr resource);
                    if (handleResult != ZrUnityTargetNative.Ok || handle == 0UL
                        || resourceResult != ZrUnityTargetNative.Ok || resource == IntPtr.Zero)
                    {
                        Debug.LogError($"[ZRender] 取共享目标句柄/资源失败：slot={slot} " +
                                       $"handle={handleResult}(0x{handle:X}) " +
                                       $"resource={resourceResult}(0x{resource.ToInt64():X})");
                        ReleaseExternalTextures();
                        ZrUnityTargetNative.ReleaseSharedTarget();
                        _nextAttemptFrame = Time.frameCount + kRetryIntervalFrames;
                        return false;
                    }

                    // linear: true —— 共享目标是 BGRA8 **UNORM**（原生 RTV 以 UNORM 写）。
                    // 在 Linear 色彩空间下传 false 会让 Unity 建 **sRGB** SRV，与 UNORM 资源
                    // 不兼容（D3D12: dxgi-fmt 91 vs 87，日志刷 "non compatible format"）。
                    // 取 UNORM 视图后采样不做 sRGB 解码：Gamma 工程下与旧行为完全一致；
                    // Linear 工程下这条桥的画面会比原生窗口直出略暗（ZRender 非活跃管线，
                    // 活跃的是 URP）。彻底方案是把共享资源改成 TYPELESS，让两种视图都合法，
                    // 但实测该改法会触发 D3D12 设备移除，需单独定位（TASK-A-R9-19 P1-b）。
                    _externalTextures[slot] = Texture2D.CreateExternalTexture(
                        width, height, TextureFormat.BGRA32, false, true, resource);
                    if (_externalTextures[slot] == null)
                    {
                        Debug.LogError("[ZRender] Texture2D.CreateExternalTexture 返回 null（D3D12 资源指针不被接受？）");
                        ReleaseExternalTextures();
                        ZrUnityTargetNative.ReleaseSharedTarget();
                        _nextAttemptFrame = Time.frameCount + kRetryIntervalFrames;
                        return false;
                    }

                    _createdExternalTextures++;
                    _externalTextures[slot].name = "ZRenderSharedTarget";
                }

                _readableTextureIndex = 0;
                _targetWidth = width;
                _targetHeight = height;
                _nativeReady = true;
                _commandBuffer ??= new CommandBuffer { name = "ZRender.BlitToCameraTarget" };
                EnsureBlitMaterial();

                Debug.Log($"[ZRender] 共享目标就绪：{width}x{height} BGRA8 × {kTargetSlotCount} 槽" +
                          $"，NT handle=0x{handle:X}，外部纹理存活={LiveExternalTextureCount}" +
                          $"（创建 {_createdExternalTextures} / 销毁 {_destroyedExternalTextures}）");
                return true;
            }
            catch (Exception error)
            {
                // dll 缺失 / 符号缺失（老版本 dll）：不炸播放器，本帧不贴屏并明确报错
                Debug.LogError($"[ZRender] 原生共享目标不可用（{error.GetType().Name}: {error.Message}）——本帧不贴屏。");
                ReleaseExternalTextures();
                _nextAttemptFrame = Time.frameCount + kRetryIntervalFrames;
                return false;
            }
        }

        /// <summary>
        /// 销毁全部外部纹理并清空槽位（尺寸变化 / 释放 / 失败清理共用）。
        /// 为什么用 DestroyImmediate：编辑器（EditMode 测试）里 Destroy 是延迟到帧末，
        /// "销毁后立刻重建"会在同一帧里同时存在新旧两张同名纹理，计数与引用都不好判定。
        /// 播放时的渲染回调里仍走 Destroy（延迟），避免在 GPU 提交途中同步销毁资源。
        /// </summary>
        private void ReleaseExternalTextures()
        {
            for (int slot = 0; slot < _externalTextures.Length; slot++)
            {
                Texture2D texture = _externalTextures[slot];
                if (texture == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(texture);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                _externalTextures[slot] = null;
                _destroyedExternalTextures++;
            }
        }

        /// <summary>
        /// 编辑器（EditMode 测试）里必须 DestroyImmediate，否则 Unity 直接报错
        /// "Destroy may not be called from edit mode"。播放时仍走 Destroy。
        /// </summary>
        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// 托管侧拿不到 ID3D12Device*，因此借一张自己的临时**Texture2D**反查设备
        /// （原生侧 ID3D12Resource::GetDevice）。只做一次。
        /// 为什么不用 RenderTexture.GetTemporary：SRP 里它可能尚未真正分配，
        /// GetNativeTexturePtr 返回 0（实测）；Texture2D + Apply 会立刻建出资源。
        /// </summary>
        private bool AttachHostDevice()
        {
            if (_nativeReady)
            {
                return true;
            }

            Texture2D probe = null;
            try
            {
                probe = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
                probe.Apply(false, true); // 建出原生资源；makeNoLongerReadable 省一份 CPU 拷贝
                IntPtr nativeTexture = probe.GetNativeTexturePtr();
                if (nativeTexture == IntPtr.Zero)
                {
                    LogAttachFailure("[ZRender] 取不到探针纹理的原生指针，无法接入设备。");
                    return false;
                }

                int attachResult = ZrUnityTargetNative.AttachDeviceFromTexture(nativeTexture);
                if (attachResult != ZrUnityTargetNative.Ok)
                {
                    LogAttachFailure($"[ZRender] ZR_Unity_AttachDeviceFromTexture 失败：{attachResult}");
                    return false;
                }

                return true;
            }
            finally
            {
                if (probe != null)
                {
                    DestroyObject(probe);
                }
            }
        }

        /// <summary>
        /// 自带的 blit 材质（Resources 下随包走）。内置 CommandBuffer.Blit 的内置材质在
        /// Player 里可能被剥离，实测会崩在 UnityPlayer 内部——因此必须显式给材质。
        /// </summary>
        private void EnsureBlitMaterial()
        {
            if (_blitMaterial != null)
            {
                return;
            }

            Shader shader = Resources.Load<Shader>("ZRenderBlit");
            if (shader == null)
            {
                Debug.LogError("[ZRender] 找不到 Resources/ZRenderBlit.shader（构建里被剥离？）——本帧不贴屏。");
                return;
            }

            _blitMaterial = new Material(shader) { name = "ZRenderBlit" };
        }

        private void LogAttachFailure(string message)
        {
            Debug.LogError(message);
        }

        private void LogFrameProgress()
        {            // 每 120 帧打一次真实计数与中心像素回读（纹理里的实际颜色，不是屏幕抓取）
            ZrUnityTargetStatsNative stats;
            if (ZrUnityTargetNative.GetTargetStats(out stats) != ZrUnityTargetNative.Ok)
            {
                return;
            }

            _lastLoggedFrameCount = stats.RenderFrameCount;

            // 平滑帧率（每帧一次乘加，代价可忽略；用于"不低于 URP 一半"的现场判据）
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime > 1e-5f)
            {
                float instant = 1f / deltaTime;
                _smoothedFps = (_smoothedFps <= 0f) ? instant : Mathf.Lerp(_smoothedFps, instant, 0.05f);
            }

            if (stats.RenderFrameCount % 120u != 0u)
            {
                return;
            }

            var center = stats.CenterRGBA;
            string sceneInfo = string.Empty;
            if (ZrUnitySceneNative.GetSceneStats(out ZrUnitySceneStatsNative scene)
                == ZrUnitySceneNative.Ok)
            {
                // drawCount = 最近一帧交给原生的物件数；uploadCount = 累计 mesh 上传次数
                // （它只应在发现新 mesh 时增长——"只上传一次"的现场证据）
                sceneInfo = $" 场景: 绘制={scene.DrawCount} 帧数={scene.SceneFrameCount} " +
                            $"mesh 种类={scene.MeshCount} 上传累计={scene.UploadCount}";
            }

            // 帧同步（TASK-B-UNITY-04）：等待耗时 = CPU 真的在等 GPU 的时间。
            // fence 双缓冲后它应该远小于帧时间（旧 WaitIdle 版等于整帧 GPU 时间）。
            string syncInfo = string.Empty;
            if (ZrUnityTargetNative.GetSyncStats(out ZrUnitySyncStatsNative sync)
                == ZrUnityTargetNative.Ok)
            {
                syncInfo = $" 帧同步: 等待平均={sync.AvgWaitMs:F3}ms 最近={sync.LastWaitMs:F3}ms " +
                           $"阻塞帧={sync.BlockedFrames}/{sync.Frames} 在途={sync.FramesInFlight}";
            }

            Debug.Log($"[ZRender] RenderFrame 计数={stats.RenderFrameCount} " +
                      $"帧率≈{_smoothedFps:F1} " +
                      $"目标={stats.Width}x{stats.Height} handle=0x{stats.SharedHandle:X} " +
                      $"中心像素回读=({center[0]},{center[1]},{center[2]}) 回读次数={stats.ReadbackCount}" +
                      $" 外部纹理存活={LiveExternalTextureCount}" +
                      $"（创建 {_createdExternalTextures} / 销毁 {_destroyedExternalTextures}）" +
                      syncInfo + sceneInfo);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _sceneBridge?.Dispose(); // 先放 mesh 句柄，再放共享目标（顺序与创建相反）
                    _sceneBridge = null;
                    // 宿主纹理先松手，再放原生资源（与尺寸重建同一顺序）
                    ReleaseExternalTextures();
                    ZrUnityTargetNative.ReleaseSharedTarget();
                }
                catch (Exception)
                {
                    // dll 已卸载 / 符号缺失：管线销毁路径不该再抛
                }

                _commandBuffer?.Release();
                _commandBuffer = null;
                if (_blitMaterial != null)
                {
                    DestroyObject(_blitMaterial);
                    _blitMaterial = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
