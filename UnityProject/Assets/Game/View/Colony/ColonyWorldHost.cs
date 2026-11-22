// ColonyWorldHost.cs —— TASK-A-COL-05：Colony 模拟域接入游戏循环的宿主（垂直切片「打开游戏看见世界在转」）。
// 分层纪律：Game.Domain.Colony 保持零引擎依赖——宿主/驱动/UI 全部在本类所在的 View 层，
// Domain 经纯 C# 接口被消费（GameWorld/AiSteward/MonthlyReport 直接 new，A-COL-01~04 就位）。
//
// 挂载方式（照 SurvivalLoop 先例，**零场景改动**）：RuntimeInitializeOnLoadMethod(AfterSceneLoad)
// 订阅 SceneManager.sceneLoaded，只有 SCN_Game 会生成宿主（DontDestroyOnLoad）。
//
// 循环接线（照 GameLoopDriver 注册 API）：宿主实现 ITickable 注册到 TickQueue 的 Logic 段
// （暂停冻结语义随框架）；按键采样注册在 Input 段（暂停时不冻结，报告随时可开）。
//
// 月度结算链（简报点名顺序）：每推进到满 30 游戏日——
//   ① 第 30/60/… 天开场：AiSteward.ApplyMonthlyPolicy（persona 可配置，默认 Aggressive，
//      让玩家有机会观察到瞒报）——施政先于月结，决策日志（Public《增产令》+ Covert 暗账）
//      落在报告窗口 [Day-30, Day-1] 内；
//   ② 当日推进收尾后：AiSteward.Compose 生成当月 Summary（AI 视野，自我豁免）+
//      MonthlyReport.CompileLastMonth 生成 Raw（诚实口径 Truth 全量，结算时刻快照）；
//   ③ EventBus.Fire(new ColonyReportReady { Month, ReportLines, Anomalies, Summary, Raw })。
//
// 视野纪律在 UI 层体现：ReportPanel 默认 Summary，Raw 需玩家点「查原始记录」。

using System;
using System.Threading.Tasks;
using Game.Domain.Colony;
using Game.Domain.Colony.Player;
using Game.Domain.Colony.Sim;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.Data.Config;
using ZFrame.View;

namespace Game.View.Colony
{
    /// <summary>
    /// Colony 世界宿主：建局（数据驱动 + 固定种子）、按真实时钟推进、月度结算广播、报告面板开合。
    /// 挂载与模拟全部在此；模拟规则不在这里（Domain 三层规模/三种节奏照旧）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ColonyWorldHost : MonoBehaviour, ITickable
    {
        /// <summary>挂载门控：只有这个场景会生成宿主（与 SurvivalLoop 同口径）。</summary>
        public const string GameSceneName = "SCN_Game";

        /// <summary>默认时间比例：1 真实秒 = 1 游戏日（简报口径）。</summary>
        public const float DefaultDaysPerRealSecond = 1f;

        /// <summary>建局种子（A-COL-01~04 全系同款：随机流确定性可复现）。</summary>
        public const ulong DefaultWorldSeed = 20261122UL;

        /// <summary>报告面板热键（Tab：F/J/R/B/K/L 均已被既有切片占用，X 键位扫描 2026-11-22）。</summary>
        public const KeyCode ReportHotKey = KeyCode.Tab;

        private static ColonyWorldHost _instance;

        private GameWorld _world;
        private AiSteward _steward;
        private ColonyEventPump _pump;
        private TickQueue _queue;
        private ColonyReportInputTick _inputTick;
        private UIManager _ui;
        private float _dayAccumulator;
        private float _daysPerRealSecond = DefaultDaysPerRealSecond;
        private AiPersona _persona = AiPersona.Aggressive();
        private string _governedRegion;
        private uint _simulatedDays;

        /// <summary>存活实例（未挂载/已卸载为 null）。</summary>
        public static ColonyWorldHost Instance => _instance;

        /// <summary>世界聚合根（建局失败为 null——fail-soft，不崩游戏）。</summary>
        public GameWorld World => _world;

        /// <summary>GameWorld 的当前游戏日（建局 Day=1；未建局为 0）。</summary>
        public uint Day => _world?.Day ?? 0u;

        /// <summary>宿主累计推进的游戏日数（与 Day 恒等式：Day == 1 + SimulatedDays）。</summary>
        public uint SimulatedDays => _simulatedDays;

        /// <summary>数据驱动建局是否成功（false = 禁用模拟并已报错）。</summary>
        public bool IsWorldReady => _world != null;

        /// <summary>最近一次月度结算的载荷（首月结算前 null）。</summary>
        public ColonyReportReady LatestReport { get; private set; }

        /// <summary>时间比例（游戏日/真实秒）。可配置（简报）；0 = 停表。</summary>
        public float DaysPerRealSecond
        {
            get => _daysPerRealSecond;
            set => _daysPerRealSecond = Mathf.Max(0f, value);
        }

        /// <summary>
        /// AI 执政官人格（简报：persona 可配置，默认 Aggressive）。建议建局后、开跑前配置——
        /// 运行中改人格会重建 steward（施政记录清零，自我豁免过滤只对当月生效）。
        /// </summary>
        public AiPersona Persona
        {
            get => _persona;
            set
            {
                _persona = value;
                if (_world != null)
                {
                    _steward = new AiSteward(value);
                }
            }
        }

        /// <summary>AI 施政地区（null = 自动取人口第一聚落，places.csv → 双河城）。</summary>
        public string GovernedRegion
        {
            get => _governedRegion;
            set => _governedRegion = value;
        }

        /// <summary>报告面板配置：Panel 层、BlockMask、无 prefab（pure-view 代码构建）、Pop 即拆。</summary>
        public static PanelConfig ReportPanelConfig { get; } = PanelConfig.Create(
            prefabKey: null,
            layer: UIConsts.UILayer.Panel,
            blockMask: true);

        // ---- 挂载（SurvivalLoop 同款：零场景改动）-----------------------------------

        /// <summary>Runtime bootstrap：订阅场景加载并对已激活场景做一次门控挂载。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void HookSceneLoad()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryMount(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryMount(scene);
        }

        private static void TryMount(Scene scene)
        {
            if (!Application.isPlaying)
            {
                return; // 编辑器编辑态不挂载（防编辑器脚本加载场景误触发；挂载只发生在 Play 模式）
            }

            if (!scene.IsValid() || !string.Equals(scene.name, GameSceneName, StringComparison.Ordinal))
            {
                return;
            }

            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("~ColonyWorldHost");
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            host.AddComponent<ColonyWorldHost>();
        }

        // ---- 生命周期 ---------------------------------------------------------------

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            Initialize();
        }

        private void Initialize()
        {
            // 循环服务兜底（正常流程 AudioService/GameBootstrap 已 EnsureInstance；直开 SCN_Game
            // 的测试会话走这条防御路径——GameRoot 的文档化 lazy seam）。
            GameRoot.EnsureInstance();
            _queue = Services.TryGet(out TickQueue queue) ? queue : null;

            // 数据服务兜底（正常流程 GameBootstrap 已 Init；同上防御路径）。
            if (!ZFrameData.IsInitialized)
            {
                ZFrameData.Init();
            }

            // MVP 绑定（幂等：Register 同型即替换；ZFrame 测试若 Reset 过，重新挂载时会补回）。
            UIBinding.Register<ReportPanelView>(view => new ReportPresenter((ReportPanelView)view));

            try
            {
                IConfigService config = Services.Get<IConfigService>();
                ColonyWorldData data = ColonyConfigAdapter.LoadWorldData(config);
                _world = ColonyDataLoader.BuildWorld(DefaultWorldSeed, data);
            }
            catch (Exception error)
            {
                // fail-soft：真机 build（无 AssetDatabase）或表资产缺失时禁用模拟并报错，不崩游戏。
                _world = null;
                Debug.LogError($"[ColonyWorldHost] 数据驱动建局失败，模拟禁用：{error.Message}");
            }

            if (_world == null)
            {
                return;
            }

            _steward = new AiSteward(_persona);
            _pump = new ColonyEventPump();
            _inputTick = new ColonyReportInputTick(this);

            if (_queue != null)
            {
                _queue.Register(this, TickStage.Logic);   // 模拟：暂停冻结语义随框架
                _queue.Register(_inputTick, TickStage.Input); // 按键采样：暂停时仍可开报告
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            if (_queue != null)
            {
                if (_inputTick != null)
                {
                    _queue.Unregister(_inputTick);
                    _inputTick = null;
                }

                _queue.Unregister(this);
            }
        }

        // ---- 循环（ITickable，Logic 段）-----------------------------------------------

        /// <summary>
        /// Logic 段帧步：dt × DaysPerRealSecond 累积，每满 1 游戏日推进一日并检查月结。
        /// dt 来自 GameTime（暂停/时间缩放语义随框架）。
        /// </summary>
        public void Tick(float dt)
        {
            if (_world == null || dt <= 0f)
            {
                return;
            }

            _dayAccumulator += dt * _daysPerRealSecond;
            int wholeDays = (int)_dayAccumulator;
            if (wholeDays < 1)
            {
                return;
            }

            _dayAccumulator -= wholeDays;
            for (int i = 0; i < wholeDays; i++)
            {
                AdvanceOneDay();
            }
        }

        /// <summary>
        /// 调试/测试加速钩子（简报点名）：显式推进 N 个游戏日（逐日走同一条
        /// AdvanceOneDay 路径，月结照常触发），不触碰帧累积器。**正式构建无副作用**——
        /// 仅在被显式调用时生效（对齐 FrameBaselineProbe 的惰性启用思路）。
        /// </summary>
        public void DebugFastForward(int days)
        {
            if (_world == null || days <= 0)
            {
                return;
            }

            for (int i = 0; i < days; i++)
            {
                AdvanceOneDay();
            }
        }

        private void AdvanceOneDay()
        {
            if (_world == null)
            {
                return;
            }

            // ① 施政先于月结：第 30/60/… 天开场颁布本月决策——决策日志落在当月窗口 [Day-30, Day-1] 内，
            //    玩家才能在报告里看到《增产令》（Public）而看不到暗账（Covert）。
            if (_world.Day % MonthlyReport.MonthDays == 0)
            {
                _steward?.ApplyMonthlyPolicy(_world, ResolveGovernedRegion());
            }

            _world.AdvanceDays(1);
            _simulatedDays++;
            _pump?.Pump(_world); // 世界日志新事件 → ZFrame EventBus（A-COL-01 既有桥）

            // ② 满月：刚模拟完的那天是 30/60/… → 触发月度结算链（Compose → Fire）。
            if ((_world.Day - 1) % MonthlyReport.MonthDays == 0)
            {
                SettleMonth();
            }
        }

        private string ResolveGovernedRegion()
        {
            if (string.IsNullOrEmpty(_governedRegion) && _world != null && _world.Settlements.Count > 0)
            {
                _governedRegion = _world.Settlements[0].Name;
            }

            return _governedRegion;
        }

        private void SettleMonth()
        {
            if (_steward == null || _world == null)
            {
                return;
            }

            int month = (int)((_world.Day - 1) / MonthlyReport.MonthDays);
            Report summary = _steward.Compose(_world, _steward.Persona, ReportDetail.Summary);
            Report raw = MonthlyReport.CompileLastMonth(_world, ReportDetail.Raw); // 玩家检查口径：Truth 全量

            var payload = new ColonyReportReady
            {
                Month = month,
                ReportLines = summary.Lines,
                Anomalies = summary.Anomalies,
                Summary = summary,
                Raw = raw,
            };
            LatestReport = payload;
            EventBus.Fire(payload);
        }

        // ---- 报告面板（UIFramework 面板栈）---------------------------------------------

        /// <summary>打开月度报告面板（热键 Tab / 测试直调）。重复打开被 UIManager 拒绝。</summary>
        public void OpenReportPanel()
        {
            UIManager ui = EnsureUiManager();
            if (ui == null)
            {
                Debug.LogError("[ColonyWorldHost] UIManager 不可用，报告面板无法打开");
                return;
            }

            _ = PushReportPanelAsync(ui);
        }

        /// <summary>关闭报告面板（回面板栈下层；面板不在栈上时为无害 no-op）。</summary>
        public void CloseReportPanel()
        {
            UIManager ui = EnsureUiManager();
            ui?.Pop(ReportPanelConfig);
        }

        private async Task PushReportPanelAsync(UIManager ui)
        {
            try
            {
                var args = ReportPanelArgs.Create(LatestReport, CloseReportPanel);
                bool pushed = await ui.Push<ReportPanelView>(ReportPanelConfig, args);
                if (!pushed)
                {
                    Debug.Log("[ColonyWorldHost] 报告面板已在栈上（重复打开被拒）");
                }
            }
            catch (Exception error)
            {
                Debug.LogError($"[ColonyWorldHost] 报告面板打开失败：{error}");
            }
        }

        /// <summary>
        /// 复用会话内既有 UIManager（正常流程 GameBootstrap 在建）；直开 SCN_Game 的测试会话
        /// 无 UIManager 时以（ResourceService + UIRoot）现建一个——纯视图面板不加载 prefab，
        /// 不依赖任何面板资产。
        /// </summary>
        private UIManager EnsureUiManager()
        {
            if (_ui != null)
            {
                return _ui;
            }

            if (UIManager.Instance != null && UIRoot.Instance != null)
            {
                _ui = UIManager.Instance;
                return _ui;
            }

            if (!ZFrameData.IsInitialized)
            {
                ZFrameData.Init();
            }

            _ui = new UIManager(Services.Get<IResourceService>(), UIRoot.EnsureCreated());
            return _ui;
        }

        // ---- 输入段 tickable（暂停不冻结，报告随时可开）---------------------------------

        /// <summary>Input 段采样：Tab 打开报告面板。独立小类——同一个 ITickable 无法区分 tick 段。</summary>
        private sealed class ColonyReportInputTick : ITickable
        {
            private readonly ColonyWorldHost _host;

            public ColonyReportInputTick(ColonyWorldHost host)
            {
                _host = host;
            }

            public void Tick(float dt)
            {
                if (Input.GetKeyDown(ReportHotKey))
                {
                    _host.OpenReportPanel();
                }
            }
        }
    }
}
