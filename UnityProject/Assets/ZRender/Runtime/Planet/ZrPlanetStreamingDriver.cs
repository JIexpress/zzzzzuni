// ZrPlanetStreamingDriver.cs —— 行星 chunk 流式的 MonoBehaviour 装配面（TASK-B-PLANET-07）。
//
// 分工（与 WorldOriginPlanetLink 的接缝）：
//   WorldOriginPlanetLink（Game.View）：门控 + 每帧 SetCameraOrigin（double 绝对原点）
//     + viewRel 纯旋转视图发布（构造在 WorldOriginMath，唯一边界）。
//   本类（ZFrame.Render）：门控 + SetWorld（经 streamer.Initialize，G6 全进程唯一托管
//     调用点）+ 从原生 GetCameraOrigin 读回当帧原点（与 Link 的推送构成"经原生汇合"的
//     解耦——本程序集不引用 Game.View/Game.Domain）→ streamer.Tick（预算内流式决策）。
//
// 门控纪律（设计稿 §3.5/D6）：EnsurePlanetChannelReady 不过（dll 缺失 / ApiVersion<3）→
// 本组件整体停用 + 一行日志，不创建任何 chunk、不碰任何 ZR_Planet_* 导出——不许半启用。
//
// 场景内至多一个 Driver：第二个实例会被拒用（单行星上下文，设计稿 §12-11）。

using System;
using UnityEngine;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Planet
{
    [DisallowMultipleComponent]
    public sealed class ZrPlanetStreamingDriver : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("行星中心（绝对坐标，米）。默认世界原点即行星中心。")]
        private double _centerX;

        [SerializeField]
        private double _centerY;

        [SerializeField]
        private double _centerZ;

        [SerializeField]
        [Tooltip("地形噪声种子（与 chunk 顶点/SampleHeight 同源；B-TECH-04 默认 1337）。")]
        private uint _seed = 1337u;

        [SerializeField]
        [Tooltip("每 tick 估算工作量上限（微秒）。0 = 用默认 9000。")]
        private int _maxWorkMicrosPerTick;

        [SerializeField]
        [Tooltip("面内 chunk 格数。0 = 用默认 32。")]
        private uint _cellsPerEdge;

        internal Func<bool> GateProbeForTests; // 测试注入门控失败（null = 走真实门控）

        private ZrPlanetChunkStreamer _streamer;
        private bool _gateFailed;
        private bool _gateChecked;
        private bool _gatePassedLogged;

        /// <summary>流式管理器（TickOnce 后可用；诊断/测试入口）。</summary>
        public ZrPlanetChunkStreamer Streamer => _streamer;

        /// <summary>门控是否已失败（整体停用，本会话不再尝试）。</summary>
        public bool GateFailed => _gateFailed;

        private void Update()
        {
            TickOnce();
        }

        private void OnEnable()
        {
            ActivateForRuntime();
        }

        /// <summary>
        /// 单驱动守卫（OnEnable 与 EditMode 测试共用——EditMode 下 AddComponent 不触发
        /// OnEnable，测试需显式调用本方法完成注册）。
        /// 行星上下文是进程级单例（SetWorld 单快照），两个 Driver 会互相覆盖。
        /// </summary>
        internal bool ActivateForRuntime()
        {
            if (_active != null && _active != this)
            {
                Debug.LogWarning($"[ZRender] 已存在行星流式 Driver（{_active.name}），本实例（{name}）拒用" +
                                 "——行星上下文为进程级单例。");
                enabled = false;
                return false;
            }

            _active = this;
            return true;
        }

        private void OnDisable()
        {
            if (_active == this)
            {
                _active = null;
            }
        }

        private void OnDestroy()
        {
            if (_active == this)
            {
                _active = null;
            }

            _streamer?.Dispose(); // 场景卸载兜底：ClearAll（GPU 下一 RenderFrame 释放）
            _streamer = null;
        }

        /// <summary>
        /// 推进一步（Update 与 EditMode 测试共用）：门控 → 初始化（SetWorld 一次）→
        /// 经原生读回当帧相机绝对原点 → 预算内 Tick。门控不过时整体静默（日志只在判定时打一行）。
        /// </summary>
        public void TickOnce()
        {
            if (_gateFailed || !_activeGuardOk)
            {
                return;
            }

            if (!_gateChecked)
            {
                _gateChecked = true;
                if (!RunGate())
                {
                    _gateFailed = true;
                    ZrPlanetChannel.Disable("ZrPlanetStreamingDriver 版本门控不过（ApiVersion<3 或 dll 缺失）——" +
                                            "chunk 流式整体停用。");
                    return;
                }

                if (!_gatePassedLogged)
                {
                    _gatePassedLogged = true;
                    Debug.Log("[ZRender] 行星流式 Driver 门控通过（ApiVersion≥3），chunk 流式启用。");
                }
            }

            try
            {
                _streamer ??= CreateStreamer();
            }
            catch (Exception error)
            {
                // SetWorld/配置失败：一次性初始化错误——整体停用（不重试，避免逐帧刷错）。
                _gateFailed = true;
                ZrPlanetChannel.Disable($"行星流式初始化失败（{error.GetType().Name}: {error.Message}）。");
                return;
            }

            // 相机绝对原点：经原生汇合（WorldOriginPlanetLink 每帧推送 SetCameraOrigin，
            // 这里读回同一值）。链路尚未接通时为 (0,0,0)——合法初始态（行星中心=世界原点，
            // 设计稿 §3.3），此时流式按原点位置工作。
            int originResult = ZrPlanetNative.GetCameraOrigin(_originReadback);
            if (originResult != ZrPlanetNative.Ok)
            {
                return; // 读不回（理论不可能）：本帧不动，下一帧再试
            }

            _streamer.Tick(_originReadback[0], _originReadback[1], _originReadback[2]);
        }

        /// <summary>测试钩子：重置门控判定（EditMode 用例用；产品代码不调）。</summary>
        internal void ResetGateForTests()
        {
            _gateChecked = false;
            _gateFailed = false;
        }

        private bool RunGate()
        {
            if (GateProbeForTests != null)
            {
                return GateProbeForTests();
            }

            try
            {
                ZrPlanetNative.EnsurePlanetChannelReady();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private ZrPlanetChunkStreamer CreateStreamer()
        {
            ZrPlanetStreamConfig config = ZrPlanetStreamConfig.CreateDefault(_centerX, _centerY, _centerZ, _seed);
            if (_cellsPerEdge > 0u)
            {
                config.CellsPerEdge = _cellsPerEdge;
            }

            if (_maxWorkMicrosPerTick > 0)
            {
                config.MaxWorkMicrosPerTick = _maxWorkMicrosPerTick;
            }

            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(config);
            return streamer;
        }

        private bool _activeGuardOk => _active == null || _active == this;

        private static ZrPlanetStreamingDriver _active;

        private readonly double[] _originReadback = new double[3];
    }
}
