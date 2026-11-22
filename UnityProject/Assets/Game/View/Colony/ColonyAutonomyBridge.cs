// ColonyAutonomyBridge.cs —— TASK-A-AI-05：Colony 自主行为的场景投影 + G 键路由 + HUD（跨域桥）。
// 分层纪律：Game.View 同时引用 Game.Domain 与 Game.Domain.Colony（arch 矩阵既有许可），
// 本类是 Game.View 内的跨域桥——场景层（NpcAutonomyMode 的 seeker）与 Colony 层（GameWorld
// 每日自主行为计数）由同一颗 G 键同状态呈现，**Colony 侧零改动**（AutonomyEnabled/
// AutonomousActionsToday/AutonomousChoicesToday 只读，每日清零语义不受影响，绝不写回）。
//
// 投影形态（简报「施工前探查后定」，选择与理由见施工报告）：**HUD 行为分布标签**（IMGUI）——
// 把 AutonomousChoicesToday 按 ColonyAction 聚合成「Work a · Protest b · …」一行可见文本，
// 附 AutonomousActionsToday 总数。不做「具名 NPC 场景角色化」：Colony 具名 npcId 与 SCN_Game
// 场景对象之间没有任何身份映射（场景 NPC 是交互锚点，不是模拟具名 NPC），把某个模拟 NPC 的
// 行为标到某个场景对象头上是伪归因——登记为后续任务候选，本刀只做诚实聚合。
//
// 键位路由（核对记录见施工报告）：G 在 SCN_Game 既有键位（Tab 报告/F/J/R 生存/B/E/Q/K/L 建造/
// WASD 移动/Z+Ctrl/Esc）中空闲；DebugFastForward 是方法无热键。G 采样走 Update（旧 Input，
// 与 SurvivalLoop 同范式；暂停态下 Update 仍运行，与 Tab 的 Input 段 tick 可用性一致）。
//
// HUD：IMGUI（参照 SurvivalLoop 先例；ReportPanelView 是面板栈 UGUI 通道，HUD 不走面板栈）。
// **只在自主模式开启时绘制**——关闭时 OnGUI 首行即 return，不产出任何像素（V5 默认零影响）。

using System.Collections.Generic;
using System.Text;
using Game.Domain.Colony.Autonomy;
using Game.Domain.Colony.Sim;
using Game.View.AI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.View.Colony
{
    /// <summary>
    /// Colony 自主行为投影桥：G 键翻转「自主模式」（seeker + 投影同键同状态）、IMGUI HUD
    /// 呈现两层状态（模式/seeker 数/今日自主行为数/行为分布）、全部 Colony 数据只读。
    /// 挂载范式与 ColonyWorldHost 一致：RuntimeInitializeOnLoadMethod + sceneLoaded 门控，
    /// 只有 SCN_Game 会生成宿主（DontDestroyOnLoad）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ColonyAutonomyBridge : MonoBehaviour
    {
        /// <summary>挂载门控：只有这个场景会生成宿主（与 ColonyWorldHost 同口径）。</summary>
        public const string GameSceneName = "SCN_Game";

        /// <summary>自主模式热键：G（键位冲突核对见施工报告；简报点名）。</summary>
        public const KeyCode AutonomyHotKey = KeyCode.G;

        private const float HudWidth = 520f;
        private const float HudHeight = 100f;

        private static ColonyAutonomyBridge _instance;

        private NpcAutonomyMode _hookedMode;

        /// <summary>存活实例（未挂载/已卸载为 null）。</summary>
        public static ColonyAutonomyBridge Instance => _instance;

        /// <summary>
        /// Colony 投影/HUD 是否可见（跟随 <see cref="NpcAutonomyMode.IsEnabled"/>——同键同状态；
        /// 关闭后恒 false，OnGUI 不产出任何像素）。
        /// </summary>
        public bool IsProjectionVisible { get; private set; }

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
                return; // 编辑器编辑态不挂载（与 ColonyWorldHost 同防线）
            }

            if (!scene.IsValid() || !string.Equals(scene.name, GameSceneName, System.StringComparison.Ordinal))
            {
                return;
            }

            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("~ColonyAutonomyBridge");
            DontDestroyOnLoad(host);
            host.AddComponent<ColonyAutonomyBridge>();
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

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            _hookedMode = null;
        }

        // ---- 键路由与状态同步 --------------------------------------------------------

        private void Update()
        {
            if (Input.GetKeyDown(AutonomyHotKey))
            {
                ToggleAutonomyMode();
            }

            NpcAutonomyMode mode = NpcAutonomyMode.Instance;
            if (!ReferenceEquals(mode, _hookedMode))
            {
                if (_hookedMode != null)
                {
                    _hookedMode.EnabledChanged -= OnModeEnabledChanged;
                }

                _hookedMode = mode;
                if (_hookedMode != null)
                {
                    _hookedMode.EnabledChanged += OnModeEnabledChanged;
                }
            }

            // 每拍幂等同步（事件之外的双保险：测试/异常路径直接改模式状态也能收敛）。
            SyncProjection();
        }

        /// <summary>翻转「自主模式」（G 键入口/测试直调）：seeker 挂卸与 Colony 投影同键同状态。</summary>
        public void ToggleAutonomyMode()
        {
            NpcAutonomyMode mode = NpcAutonomyMode.Instance;
            if (mode == null)
            {
                Debug.LogError($"[ColonyAutonomyBridge] {GameSceneName}/NpcAutonomyMode 缺席，G 键无法开启自主模式。");
                return;
            }

            mode.Toggle();
            SyncProjection();
        }

        private void OnModeEnabledChanged(bool enabled)
        {
            SyncProjection();
        }

        private void SyncProjection()
        {
            NpcAutonomyMode mode = NpcAutonomyMode.Instance;
            IsProjectionVisible = mode != null && mode.IsEnabled;
        }

        // ---- HUD（IMGUI，只在开启时产出像素）-----------------------------------------

        private void OnGUI()
        {
            if (!IsProjectionVisible)
            {
                return; // 默认关：零像素（V5）
            }

            Rect rect = new Rect(10f, 10f, HudWidth, HudHeight);
            GUI.Box(rect, string.Empty);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f), BuildHudText());
        }

        /// <summary>
        /// HUD 文本（测试断言口径）：行 1 = 模式 + seeker 数；行 2 = Colony 今日自主行为数；
        /// 行 3 = Colony 行为分布（只读投影）；行 4 = 建局失败提示（fail-soft 时）。
        /// </summary>
        public string BuildHudText()
        {
            NpcAutonomyMode mode = NpcAutonomyMode.Instance;
            GameWorld world = ColonyWorldHost.Instance != null ? ColonyWorldHost.Instance.World : null;

            int seekerCount = mode != null ? mode.ActiveSeekerCount : 0;
            var builder = new StringBuilder(256);
            builder.Append("自主模式: 开 [G]   Seeker: ").Append(seekerCount).Append('/')
                .Append(mode != null ? mode.SeekerLimit : 0);
            builder.Append('\n');
            builder.Append("Colony 自主行为(今日): ").Append(world != null ? world.AutonomousActionsToday : 0).Append(" 次");

            if (world == null)
            {
                builder.Append('\n').Append("Colony 模拟不可用（fail-soft：建局失败，投影无数据）");
                return builder.ToString();
            }

            if (!world.AutonomyEnabled)
            {
                builder.Append('\n').Append("Colony 自主行为: 模拟层开关已关闭（AutonomyEnabled=false）");
                return builder.ToString();
            }

            builder.Append('\n').Append("Colony 行为分布(今日·只读投影): ")
                .Append(BuildDistributionText(world.AutonomousChoicesToday));
            return builder.ToString();
        }

        /// <summary>
        /// 把「今日每个具名 NPC 的自主选择」按 <see cref="ColonyAction"/> 聚合成一行分布文本
        /// （纯函数，测试锚）。空字典 = 「（今日暂无自主行为）」。**只读**：不改传入字典。
        /// </summary>
        public static string BuildDistributionText(IReadOnlyDictionary<int, ColonyAction> choices)
        {
            if (choices == null || choices.Count == 0)
            {
                return "（今日暂无自主行为）";
            }

            var counts = new Dictionary<ColonyAction, int>();
            foreach (KeyValuePair<int, ColonyAction> entry in choices)
            {
                counts.TryGetValue(entry.Value, out int count);
                counts[entry.Value] = count + 1;
            }

            var builder = new StringBuilder(128);
            var ordered = new List<ColonyAction> { ColonyAction.Work, ColonyAction.Protest, ColonyAction.Recruit, ColonyAction.Gossip, ColonyAction.Rest };
            bool first = true;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!counts.TryGetValue(ordered[i], out int count))
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(" · ");
                }

                builder.Append(LabelOf(ordered[i])).Append(' ').Append(count);
                first = false;
            }

            return builder.ToString();
        }

        /// <summary>行为中文名（与 ColonyDailyBehavior/ColonyAction 注释口径一致）。</summary>
        public static string LabelOf(ColonyAction action)
        {
            switch (action)
            {
                case ColonyAction.Work: return "劳作";
                case ColonyAction.Protest: return "抗议";
                case ColonyAction.Recruit: return "串联";
                case ColonyAction.Gossip: return "传闻";
                case ColonyAction.Rest: return "休整";
                default: return action.ToString();
            }
        }
    }
}
