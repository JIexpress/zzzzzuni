// WorldOriginPlanetLink.cs —— WorldOrigin(4000m) → 行星通道的每帧接线（TASK-B-PLANET-07）。
//
// 这是 WorldOrigin 自 A-WORLD-01 落地以来的**首个生产消费者**（HANDOFF 渲染体检：
// 此前从未挂任何场景）。每帧做两件事：
//   1) 相机绝对原点（double）：UnityToWorld(camera.position)——经 WorldOriginMath.ToWorld
//      （float→double 精确加宽，唯一互转入口）→ ZR_Unity_SetCameraOrigin(x,y,z)。
//      double 直传原生，全程不做大坐标 float 化（A-WORLD-01 顺序铁律）。
//   2) viewRel（纯旋转视图）：WorldOriginMath.ToCameraRelativeView(camera.worldToCameraMatrix)
//      ——构造只有这一份实现（唯一边界延伸），发布到 ZrPlanetChannel 供 ZRenderSceneBridge
//      本帧消费；同时把物件世界矩阵的相机相对化配套交给桥（本类不碰渲染）。
//
// 为什么住 Game.View/World：本类要引用 WorldOrigin（Game.View）与 WorldOriginMath
// （Game.Domain），并调用 ZrPlanetNative/ZrPlanetChannel（Game.View 已引用
// ZFrame.Render.Bindings——A-AND-08 既有 GUID 引用）。不新增任何 asmdef 引用。
//
// 门控纪律：EnsurePlanetChannelReady 不过（dll 缺失 / ApiVersion<3）→ 通道整体关闭 +
// 一行日志，之后每帧静默退出（不重试、不半启用）。SetCameraOrigin 失败同理。
// G6 责任边界：本类**绝不调用 SetWorld**（那是 ZrPlanetStreamingDriver/Streamer 的
// 初始化期唯一托管调用点）；本类只推每帧原点。

using Game.Domain.World;
using UnityEngine;
using ZFrame.Render.Bindings;

namespace Game.View.World
{
    [DisallowMultipleComponent]
    public sealed class WorldOriginPlanetLink : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("浮点原点权威（double OriginOffset 的持有者）。为空时静默不动作。")]
        private WorldOrigin _origin;

        [SerializeField]
        [Tooltip("驱动相机的绝对原点来源；为空时退回 Camera.main。")]
        private Camera _targetCamera;

        private bool _gateSettled;
        private bool _gatePassed;

        /// <summary>门控是否已通过（诊断/测试）。</summary>
        public bool GatePassed => _gatePassed;

        /// <summary>最近一次推送是否成功（诊断/测试；门控未过时恒 false）。</summary>
        public bool LastPushOk { get; private set; }

        private void LateUpdate()
        {
            PushOriginAndViewRel();
        }

        /// <summary>
        /// 每帧推送（Update 后、渲染前；EditMode 测试直接调用）。无相机/无 WorldOrigin 时
        /// 静默不动作（幂等、可重复调用——与 WorldOrigin 的哲学一致）。
        /// </summary>
        public void PushOriginAndViewRel()
        {
            if (!_gateSettled)
            {
                _gateSettled = true;
                try
                {
                    ZrPlanetNative.EnsurePlanetChannelReady();
                    _gatePassed = true;
                }
                catch (System.Exception error)
                {
                    _gatePassed = false;
                    LastPushOk = false;
                    ZrPlanetChannel.Disable(
                        $"WorldOriginPlanetLink 版本门控不过（{error.Message}）——行星通道整体关闭。");
                    return;
                }
            }

            if (!_gatePassed)
            {
                return; // 门控已判死：整体关闭，每帧静默
            }

            Camera camera = _targetCamera != null ? _targetCamera : Camera.main;
            if (camera == null || _origin == null)
            {
                LastPushOk = false;
                return; // 静默：接线前提未就绪（无相机/无原点），不影响场景其余部分
            }

            // 相机绝对原点（double，行星中心系语义由场景装配决定——本链路只负责把
            // WorldOrigin 权威状态直传原生）。float→double 是精确加宽（ToWorld）。
            WorldPosition cameraWorld = _origin.UnityToWorld(camera.transform.position);

            int result = ZrPlanetNative.SetCameraOrigin(cameraWorld.X, cameraWorld.Y, cameraWorld.Z);
            if (result != ZrPlanetNative.Ok)
            {
                LastPushOk = false;
                ZrPlanetChannel.Disable($"ZR_Unity_SetCameraOrigin 失败 rc={result}——行星通道整体关闭。");
                return;
            }

            LastPushOk = true;
            ZrPlanetChannel.Enable();

            // viewRel：纯旋转（平移列归零）——唯一构造点在 WorldOriginMath（顺序铁律延伸）。
            // 本类只发布，不构造、不存副本。
            ZrPlanetChannel.PublishCameraRelativeView(
                WorldOriginMath.ToCameraRelativeView(camera.worldToCameraMatrix));
        }
    }
}
