using System;
using System.Collections.Generic;
using Game.Domain.AI.Affordances;
using Game.Domain.AI.Utility;
using Game.View.Npc;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.View.AI
{
    /// <summary>
    /// TASK-A-AI-05：NPC 自主模式管理器（默认关，G 键经 <see cref="Game.View.Colony.ColonyAutonomyBridge"/>
    /// 路由到 <see cref="Toggle"/>）。开：给场景里最多 <see cref="DefaultSeekerLimit"/> 个闲置 NPC
    /// 挂 <see cref="AffordanceSeeker"/> 并 <see cref="AffordanceSeeker.StartSeeking"/>（自主移动的可见
    /// 部分）；关：全部 <see cref="AffordanceSeeker.Stop"/> + Destroy seeker，并把挂载期间借走的
    /// NPC 状态原样归还（位置复位 + NpcUtilityView 启用态复位）——无残留。
    ///
    /// 候选解析（简报「优先 npc-anim-*」，开工复验记录见施工报告）：
    ///  ① 带 <see cref="NpcUtilityView"/> 的闲置 NPC（名字以 npc-anim- 开头的优先，其余按名排序）；
    ///    自主期间**停用**其 NpcUtilityView（避免双脑抢驱动，AffordanceSeeker 既有设计边界），
    ///    关闭时复位启用态；
    ///  ② 数量不足时补 <see cref="SceneAffordances.SocialAnchorName"/> 指向的场景 NPC（SCN_Game
    ///    现状：无 NpcUtilityView、无 npc-anim-*，命中此兜底 → 实际 seeker 数 = 1）。
    /// 名单是**场景实况探测**而非写死名单：场景以后增删 NPC 不需要改本类。
    ///
    /// 挂载范式与 SceneAffordances / ColonyWorldHost 一致：RuntimeInitializeOnLoadMethod +
    /// sceneLoaded 门控，只有 SCN_Game 会生成宿主（DontDestroyOnLoad）。默认关：不挂任何
    /// seeker、不建任何可见物（V5/V6——像素用例与场景 NPC 位置零影响）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NpcAutonomyMode : MonoBehaviour
    {
        /// <summary>挂载门控：只有这个场景会生成宿主（与 SceneAffordances 同口径）。</summary>
        public const string GameSceneName = "SCN_Game";

        /// <summary>默认 seeker 上限（简报「默认 3 个」；实际数量 = min(上限, 场景闲置 NPC 数)）。</summary>
        public const int DefaultSeekerLimit = 3;

        /// <summary>闲置 NPC 的优先名单前缀（简报「优先 npc-anim-*」；SCN_Game 现状为 0 命中，见施工报告）。</summary>
        public const string AnimNpcNamePrefix = "npc-anim-";

        private static NpcAutonomyMode _instance;

        private readonly List<AffordanceSeeker> _seekers = new List<AffordanceSeeker>();
        private readonly Dictionary<int, Vector3> _capturedPositions = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, bool> _capturedViewEnabled = new Dictionary<int, bool>();
        private readonly List<NpcUtilityView> _suspendedViews = new List<NpcUtilityView>();
        private bool _remounting;

        /// <summary>存活实例（未挂载/已卸载为 null）。</summary>
        public static NpcAutonomyMode Instance => _instance;

        /// <summary>自主模式当前是否开启（默认 false；开启前场景 NPC 位置逐位不变）。</summary>
        public bool IsEnabled { get; private set; }

        /// <summary>seeker 数上限（默认 <see cref="DefaultSeekerLimit"/>；测试可收窄）。</summary>
        public int SeekerLimit { get; set; } = DefaultSeekerLimit;

        /// <summary>当前存活的 seeker 数（关闭后恒 0；开启后 = min(上限, 场景闲置 NPC 数)）。</summary>
        public int ActiveSeekerCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _seekers.Count; i++)
                {
                    if (_seekers[i] != null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>本次会话候选解析能找到的闲置 NPC 数（诊断/测试锚：ActiveSeekerCount == min(SeekerLimit, IdleCandidateCount)）。</summary>
        public int IdleCandidateCount => ResolveIdleCandidates(SeekerLimit).Count;

        /// <summary>开关变化事件（Colony 投影/HUD 同键同状态的同步源；参数 = 新状态）。</summary>
        public event Action<bool> EnabledChanged;

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

            if (_instance == null)
            {
                if (!scene.IsValid() || !string.Equals(scene.name, GameSceneName, StringComparison.Ordinal))
                {
                    return;
                }

                var host = new GameObject("~NpcAutonomyMode");
                DontDestroyOnLoad(host);
                host.AddComponent<NpcAutonomyMode>();
                return;
            }

            // 场景重载/切换：场景 NPC 是旧对象——关态无事可做；开态先卸（旧 seeker 已随场景
            // 卸载销毁，OnDestroy 会 Stop 清 stake）再对新场景重新解析挂载（自愈）。
            if (string.Equals(scene.name, GameSceneName, StringComparison.Ordinal))
            {
                _instance.RemountIfEnabled();
            }
            else
            {
                _instance.SetEnabled(false);
            }
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
                if (IsEnabled)
                {
                    UnmountSeekers();
                    IsEnabled = false;
                }

                _instance = null;
            }
        }

        /// <summary>翻转自主模式（开 → 关 / 关 → 开）。返回新状态。</summary>
        public bool Toggle()
        {
            return SetEnabled(!IsEnabled);
        }

        /// <summary>
        /// 显式设置自主模式（幂等：同值 no-op）。开 = 挂 seeker + 捕获 NPC 状态；
        /// 关 = seeker 归零（Stop + Destroy）+ NPC 状态复位（位置/启用态）。
        /// 场景没有可供性注册表（SceneAffordances 缺席）时拒绝开启并报错——fail-soft，不崩游戏。
        /// </summary>
        public bool SetEnabled(bool value)
        {
            if (value == IsEnabled)
            {
                return IsEnabled;
            }

            if (value)
            {
                if (SceneAffordances.Instance == null || SceneAffordances.Instance.Registry == null)
                {
                    Debug.LogError(
                        $"[NpcAutonomyMode] {GameSceneName}/SceneAffordances 不可用，自主模式拒绝开启（挂载 seeker 无注册表）。");
                    return IsEnabled;
                }

                MountSeekers();
                if (ActiveSeekerCount == 0)
                {
                    Debug.LogWarning("[NpcAutonomyMode] 开启请求没有解析到任何闲置 NPC 候选，保持关闭。");
                    return IsEnabled;
                }
            }
            else
            {
                UnmountSeekers();
            }

            IsEnabled = value;
            EnabledChanged?.Invoke(value);
            return IsEnabled;
        }

        /// <summary>场景重载后的自愈入口：开态下先卸（旧 stake 已清）再重挂到新场景的 NPC。</summary>
        private void RemountIfEnabled()
        {
            if (!IsEnabled || _remounting)
            {
                return;
            }

            _remounting = true;
            try
            {
                SetEnabled(false);
                SetEnabled(true);
            }
            finally
            {
                _remounting = false;
            }
        }

        // ---- 挂载 / 卸载 ---------------------------------------------------------

        private void MountSeekers()
        {
            UnmountSeekers(); // 幂等防重入

            AffordanceRegistry registry = SceneAffordances.Instance.Registry;
            List<GameObject> candidates = ResolveIdleCandidates(SeekerLimit);
            for (int i = 0; i < candidates.Count; i++)
            {
                GameObject npc = candidates[i];
                Transform npcTransform = npc.transform;
                _capturedPositions[npc.GetInstanceID()] = npcTransform.position;

                NpcUtilityView view = npc.GetComponent<NpcUtilityView>();
                if (view != null && view.enabled)
                {
                    // 自主期间停用原驱动（双脑冲突）；关闭时按捕获值复位。
                    _capturedViewEnabled[view.GetInstanceID()] = view.enabled;
                    _suspendedViews.Add(view);
                    view.enabled = false;
                }

                var seeker = npc.AddComponent<AffordanceSeeker>();
                seeker.ManualDrive = false; // 自主模式走真实 Update（玩家可见的自主移动）
                seeker.Configure(registry, null);
                // 演示口径（可见化的必要条件，Round B 实测修正）：设备损坏起手（DeviceBroken=true
                // → Work 满权重 1.0）。SCN_Game 真实锚点几何：ENV_Tree_01 在 (8.6,4.2)/(-7.4,-7.2)，
                // 距唯一候选（社交锚点 NPC，它坐在自家社交点上）约 10.7m——满疲劳下 Rest 仅
                // 1.0×0.46=0.46，输给 Social 0.6，NPC 会原地社交、移动不可见；DeviceBroken 让
                // Work = 1.0×prox(ENV_Barrel@3.1m)=0.845 决定性胜出，走向木桶的自主移动真正可见。
                // NPCWorldState 是 seeker 自有状态（A-AI-04 测试同样直写），不是模拟数据。
                seeker.State.Set(NPCWorldState.Keys.DeviceBroken, true);
                seeker.StartSeeking();
                _seekers.Add(seeker);
            }

            if (candidates.Count > 0)
            {
                Debug.Log($"[NpcAutonomyMode] 自主模式开启：{candidates.Count} 个闲置 NPC 已挂 seeker（上限 {SeekerLimit}）。");
            }
        }

        private void UnmountSeekers()
        {
            for (int i = 0; i < _seekers.Count; i++)
            {
                AffordanceSeeker seeker = _seekers[i];
                if (seeker == null)
                {
                    continue; // 场景卸载已带走（OnDestroy → Stop 已清 stake）
                }

                seeker.Stop();
                Destroy(seeker);
            }

            _seekers.Clear();

            for (int i = 0; i < _suspendedViews.Count; i++)
            {
                NpcUtilityView view = _suspendedViews[i];
                if (view == null)
                {
                    continue;
                }

                if (_capturedViewEnabled.TryGetValue(view.GetInstanceID(), out bool wasEnabled))
                {
                    view.enabled = wasEnabled;
                }
            }

            _suspendedViews.Clear();
            _capturedViewEnabled.Clear();

            foreach (KeyValuePair<int, Vector3> entry in _capturedPositions)
            {
                // 位置复位：挂载期间 seeker 只写 XZ，这里整位还原（含 Y）——无残留。
                if (FindTransformById(entry.Key, out Transform transform))
                {
                    transform.position = entry.Value;
                }
            }

            _capturedPositions.Clear();
        }

        private static bool FindTransformById(int instanceId, out Transform transform)
        {
            // 候选规模 ≤ 3：直接全场扫一次换取「不持有场景对象引用」（场景卸载安全）。
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.GetInstanceID() == instanceId)
                {
                    transform = all[i];
                    return true;
                }
            }

            transform = null;
            return false;
        }

        // ---- 候选解析 -------------------------------------------------------------

        /// <summary>
        /// 闲置 NPC 候选解析（场景实况探测，确定性排序）：① NpcUtilityView 携带者
        /// （名字以 <see cref="AnimNpcNamePrefix"/> 开头的优先，同组按名字序）；② 不足时补
        /// <see cref="SceneAffordances.SocialAnchorName"/> 场景 NPC。已带 seeker 的对象跳过。
        /// 返回数量 ≤ <paramref name="limit"/> 的新列表（调用方可改写）。
        /// </summary>
        internal List<GameObject> ResolveIdleCandidates(int limit)
        {
            var candidates = new List<GameObject>();
            if (limit <= 0)
            {
                return candidates;
            }

            var used = new HashSet<int>();

            NpcUtilityView[] views = FindObjectsOfType<NpcUtilityView>();
            Array.Sort(views, (a, b) =>
            {
                bool aAnim = AnimPrefixed(a.gameObject.name);
                bool bAnim = AnimPrefixed(b.gameObject.name);
                if (aAnim != bAnim)
                {
                    return aAnim ? -1 : 1; // npc-anim-* 优先
                }

                return string.CompareOrdinal(a.gameObject.name, b.gameObject.name);
            });

            for (int i = 0; i < views.Length && candidates.Count < limit; i++)
            {
                GameObject npc = views[i].gameObject;
                if (npc.GetComponent<AffordanceSeeker>() != null || !used.Add(npc.GetInstanceID()))
                {
                    continue;
                }

                candidates.Add(npc);
            }

            if (candidates.Count < limit)
            {
                GameObject anchor = GameObject.Find(SceneAffordances.SocialAnchorName);
                if (anchor != null
                    && anchor.GetComponent<AffordanceSeeker>() == null
                    && anchor.GetComponent<NpcUtilityView>() == null
                    && used.Add(anchor.GetInstanceID()))
                {
                    candidates.Add(anchor);
                }
            }

            return candidates;
        }

        private static bool AnimPrefixed(string name)
        {
            return !string.IsNullOrEmpty(name) && name.StartsWith(AnimNpcNamePrefix, StringComparison.Ordinal);
        }
    }
}
