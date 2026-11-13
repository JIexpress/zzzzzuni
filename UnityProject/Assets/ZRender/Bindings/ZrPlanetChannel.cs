// ZrPlanetChannel.cs —— 行星通道的托管集成缝（TASK-B-PLANET-07）。
//
// 为什么住在 Bindings：行星接线的生产者（WorldOriginPlanetLink，Game.View）与消费者
// （ZRenderSceneBridge / ZrPlanetStreamingDriver，ZFrame.Render）分属两个程序集，而
// ZFrame.Render 不能反向引用 Game.View/Game.Domain（Track B 依赖 Track A 属层级倒置），
// Game.View 只引用了 ZFrame.Render.Bindings（A-AND-08 起的既有 GUID 引用）——
// Bindings 是两侧共同可达的唯一叶程序集，故通道状态缝落这里。
//
// 职责刻意极小：只承载「门控结果 + 每帧 viewRel 交接」两件状态。
//  - 门控纪律（设计稿 §3.5/D6 + 简报）：ApiVersion ≥ 3 才允许 Enable；门控不过 =
//    整体关闭 + 一行日志，不许半启用。真正的版本断言在 ZrPlanetNative.
//    EnsurePlanetChannelReady()，本类只记录其结果。
//  - viewRel（纯旋转视图）的构造只有一份实现：Game.Domain.World.WorldOriginMath.
//    ToCameraRelativeView（A-WORLD-01 顺序铁律延伸）——本类只搬运矩阵，不构造矩阵。

using UnityEngine;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// 行星通道启用状态 + 每帧相机相对视图（viewRel）的交接缝。
    /// 生产者：WorldOriginPlanetLink（每帧 LateUpdate 推送）；消费者：ZRenderSceneBridge
    /// （本帧 RenderFrame 之前取走，用于 SetCamera 与物件世界矩阵的相机相对化）。
    /// </summary>
    public static class ZrPlanetChannel
    {
        private static bool _enabled;
        private static Matrix4x4 _cameraRelativeView;
        private static bool _hasCameraRelativeView;
        private static bool _disableLogged;

        /// <summary>行星通道是否已启用（门控通过且接线成功）。false 时场景桥走全量视图旧路径。</summary>
        public static bool IsEnabled => _enabled;

        /// <summary>
        /// 启用通道（仅在门控 <see cref="ZrPlanetNative.EnsurePlanetChannelReady"/> 通过且
        /// 当帧 SetCameraOrigin 成功后调用；幂等，重复调用无副作用）。
        /// </summary>
        public static void Enable()
        {
            if (!_enabled)
            {
                _enabled = true;
                _disableLogged = false;
                UnityEngine.Debug.Log(
                    "[ZRender] 行星通道启用：ApiVersion≥3 门控通过，WorldOrigin 接线成功" +
                    "（chunk 流式 + SetCameraOrigin 每帧推送 + viewRel 纯旋转视图）。");
            }
        }

        /// <summary>
        /// 整体关闭通道（门控不过 / SetCameraOrigin 失败 / 接线方显式停用）。
        /// 「一行日志」纪律：关闭原因只在状态真正翻转时打一次，不逐帧刷屏。
        /// </summary>
        public static void Disable(string reason)
        {
            if (!_enabled && _disableLogged)
            {
                return;
            }

            _enabled = false;
            _hasCameraRelativeView = false;
            _disableLogged = true;
            UnityEngine.Debug.LogWarning($"[ZRender] 行星通道整体关闭：{reason}");
        }

        /// <summary>
        /// 发布本帧的相机相对视图（纯旋转，由 WorldOriginMath.ToCameraRelativeView 构造）。
        /// 每帧由接线方发布一次；同帧多次发布最后一个生效。**通道关闭时的发布被丢弃**
        /// （不许半启用：关闭后发布的 viewRel 不得在重新启用后复活）。
        /// </summary>
        public static void PublishCameraRelativeView(Matrix4x4 viewRel)
        {
            if (!_enabled)
            {
                return;
            }

            _cameraRelativeView = viewRel;
            _hasCameraRelativeView = true;
        }

        /// <summary>
        /// 场景桥取走本帧的 viewRel。**取走即清**（consume-once）：发布方缺席（接线方
        /// 未运行/被销毁）时下一帧自然落回 false，场景桥回退全量视图旧路径，不会拿着
        /// 陈旧矩阵继续渲染。v1 单相机契约：管线只提交 cameras[0]（B-UNITY-03 既有口径）。
        /// </summary>
        public static bool TryConsumeCameraRelativeView(out Matrix4x4 viewRel)
        {
            viewRel = _cameraRelativeView;
            bool has = _hasCameraRelativeView && _enabled;
            _hasCameraRelativeView = false;
            return has;
        }

        /// <summary>测试隔离：清空全部通道状态（EditMode 用例 SetUp/TearDown 用）。</summary>
        internal static void ResetForTests()
        {
            _enabled = false;
            _hasCameraRelativeView = false;
            _disableLogged = false;
            _cameraRelativeView = Matrix4x4.zero;
        }
    }
}
