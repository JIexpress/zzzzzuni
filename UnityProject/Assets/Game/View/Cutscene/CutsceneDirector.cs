using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using ZFrame.Core;
using Game.View.Crowd;

namespace Game.View.Cutscene
{
    /// <summary>
    /// TASK-A-CUT-01：固定机位过场（L3 播片）的门闸 + 播片链路。
    ///
    /// 分工：
    ///  - **门闸**：<see cref="Begin"/> / <see cref="End"/> 是唯一的开关，幂等；
    ///    <see cref="IsActive"/> 供玩法侧查询（例如冻结玩家输入、切摄像机）。
    ///  - **人群让位**：过场期间调用 <see cref="CrowdLODPipeline.SetAllSuppressed"/>，
    ///    L0 隐藏、L1/L2 不提交、kernel 不读——看不见的群体不再消耗 CPU。
    ///    L3 **不通过逐实体档位产出**，所以 A-AND-09 的硬上限语义（L0≤20/L1≤50/L2≤512）
    ///    与「分配器永不产出 L3」的契约完全不变。
    ///  - **播片**：VideoPlayer → RenderTexture → 全屏 RawImage（Screen Space Overlay，
    ///    sortingOrder 30000）。音频默认关闭（占位片源无音轨）。
    ///
    /// 片源：`Resources/Cutscenes/cutscene_crowd`（Blender 5.2 Workbench 生成的占位片段，
    /// 640×360 / 24fps / 3s）。换正式片源只需替换该资源或给 <see cref="_clip"/> 赋值。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CutsceneDirector : MonoBehaviour
    {
        /// <summary>默认片源的 Resources 路径（不含扩展名）。</summary>
        public const string DefaultClipResourcePath = "Cutscenes/cutscene_crowd";

        private const int kTargetWidth = 1280;
        private const int kTargetHeight = 720;
        private const int kScreenSortingOrder = 30000;

        [Tooltip("留空则从 Resources 加载默认片源。")]
        [SerializeField] private VideoClip _clip;

        [Tooltip("循环播放（占位片源默认循环，便于观察）。")]
        [SerializeField] private bool _loop = true;

        [Tooltip("过场期间抑制人群表示层。")]
        [SerializeField] private bool _suppressCrowd = true;

        private static CutsceneDirector _instance;

        private VideoPlayer _player;
        private RenderTexture _target;
        private Canvas _canvas;
        private RawImage _screen;
        private bool _active;

        /// <summary>当前实例（可能为 null）。</summary>
        public static CutsceneDirector Instance => _instance;

        /// <summary>门闸是否处于过场中（无实例时为 false）。</summary>
        public static bool IsActive => _instance != null && _instance._active;

        /// <summary>本实例是否处于过场中。</summary>
        public bool Active => _active;

        /// <summary>播片器是否在播（诊断与测试用）。</summary>
        public bool IsPlaying => _player != null && _player.isPlaying;

        /// <summary>播片器是否已 prepare 完成。</summary>
        public bool PlayerPrepared => _player != null && _player.isPrepared;

        /// <summary>当前视频帧号（未创建播片器时为 -1）。</summary>
        public long Frame => _player != null ? (long)_player.frame : -1L;

        /// <summary>当前使用的片源（可能为 null = 资源缺失）。</summary>
        public VideoClip Clip => _clip;

        /// <summary>创建一个常驻导演宿主（幂等）。</summary>
        public static CutsceneDirector EnsureInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var host = new GameObject("~CutsceneDirector");
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            return host.AddComponent<CutsceneDirector>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        /// <summary>
        /// 进入过场：抑制人群 + 显示全屏播片。幂等；重复调用不会重启播放。
        /// 片源缺失时仍然进入门闸（画面保持场景原样），并给出明确警告——
        /// 静默失败会让调用方以为过场已经播了。
        /// </summary>
        public void Begin()
        {
            if (_active)
            {
                return;
            }

            _active = true;

            if (_suppressCrowd)
            {
                CrowdLODPipeline.SetAllSuppressed(true);
            }

            ResolveClip();
            if (_clip == null)
            {
                ZLog.Warn("Cutscene",
                    $"片源缺失（Resources/{DefaultClipResourcePath} 与序列化字段都为空）——过场只开门闸，不播片。");
                return;
            }

            EnsurePlayer();
            EnsureScreen();
            if (_screen != null)
            {
                _screen.gameObject.SetActive(true);
            }

            _player.clip = _clip;
            _player.Play();
        }

        /// <summary>退出过场：停播 + 隐藏屏幕 + 恢复人群。幂等。</summary>
        public void End()
        {
            if (!_active)
            {
                return;
            }

            _active = false;

            if (_player != null)
            {
                _player.Stop();
            }

            if (_screen != null)
            {
                _screen.gameObject.SetActive(false);
            }

            if (_suppressCrowd)
            {
                CrowdLODPipeline.SetAllSuppressed(false);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            // 宿主被销毁时不能让群体永久消失。
            if (_active && _suppressCrowd)
            {
                CrowdLODPipeline.SetAllSuppressed(false);
            }

            _active = false;

            if (_target != null)
            {
                _target.Release();
                DestroyObject(_target);
                _target = null;
            }
        }

        private void ResolveClip()
        {
            if (_clip != null)
            {
                return;
            }

            _clip = Resources.Load<VideoClip>(DefaultClipResourcePath);
        }

        private void EnsurePlayer()
        {
            if (_player != null)
            {
                return;
            }

            _target = new RenderTexture(kTargetWidth, kTargetHeight, 0, RenderTextureFormat.ARGB32);
            _target.name = "~CutsceneTarget";
            _target.Create();

            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = _loop;
            _player.source = VideoSource.VideoClip;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _target;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.skipOnDrop = true;
        }

        private void EnsureScreen()
        {
            if (_screen != null)
            {
                return;
            }

            var canvasGo = new GameObject("~CutsceneCanvas");
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = kScreenSortingOrder;
            canvasGo.AddComponent<CanvasScaler>();

            var imageGo = new GameObject("~CutsceneScreen");
            imageGo.transform.SetParent(canvasGo.transform, false);

            _screen = imageGo.AddComponent<RawImage>();
            _screen.texture = _target;
            _screen.raycastTarget = false;

            var rect = _screen.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            imageGo.SetActive(false);
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}
