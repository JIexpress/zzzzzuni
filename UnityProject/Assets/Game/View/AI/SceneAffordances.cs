using Game.Domain.AI.Affordances;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.View.AI
{
    /// <summary>
    /// TASK-A-AI-04：把 SCN_Game 里**已有的**场景物件登记成可供性（数据）。
    ///
    /// 设计边界（很重要）：
    ///  - **只注册数据，不建任何可见物**：本类不 new Renderer / 不 CreatePrimitive /
    ///    不挂材质，注册前后场景 Renderer 数量必须一致（V2）。像素证据用例零影响。
    ///  - 锚点只在场景加载时按名字查一次（<see cref="GameObject.Find"/>），**绝不在
    ///    Update 里查**；查不到就退到固定兜底坐标并打一条 warning（兜底是显式契约，
    ///    不是静默降级）。
    ///  - 挂载范式与 SurvivalLoop / BuildMode 一致：RuntimeInitializeOnLoadMethod +
    ///    sceneLoaded 门控，只有 SCN_Game 会生成宿主；宿主 DontDestroyOnLoad，
    ///    每次 SCN_Game 重新加载都会重扫一遍（旧条目与占用一并丢弃）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneAffordances : MonoBehaviour
    {
        /// <summary>挂载门控：只有这个场景会登记可供性。</summary>
        public const string GameSceneName = "SCN_Game";

        /// <summary>工作锚点：场景里的木桶（修东西/干活的地方）。</summary>
        public const string WorkAnchorName = "ENV_Barrel";

        /// <summary>休息锚点：场景里的树（靠树歇脚）。</summary>
        public const string RestAnchorName = "ENV_Tree_01";

        /// <summary>社交锚点：场景里 NPC 站的地方（聚集点）。</summary>
        public const string SocialAnchorName = "NPC";

        // ---- 兜底坐标（锚点找不到时使用；与场景实际位置无关，只保证不重叠）----
        /// <summary>Work 兜底坐标（米）。</summary>
        public static readonly Vector3 WorkFallback = new Vector3(3.2f, 0f, 2.9f);

        /// <summary>Rest 兜底坐标（米）。</summary>
        public static readonly Vector3 RestFallback = new Vector3(-4f, 0f, -3f);

        /// <summary>Social 兜底坐标（米）。</summary>
        public static readonly Vector3 SocialFallback = new Vector3(-2.2f, 0f, 2.2f);

        /// <summary>Work 交互半径（米）。</summary>
        public const float WorkRadius = 1.2f;

        /// <summary>Rest 交互半径（米）。</summary>
        public const float RestRadius = 1.5f;

        /// <summary>Social 交互半径（米）。</summary>
        public const float SocialRadius = 2f;

        /// <summary>Work 容量（一个人干活）。</summary>
        public const int WorkCapacity = 1;

        /// <summary>Rest 容量（一个位置）。</summary>
        public const int RestCapacity = 1;

        /// <summary>Social 容量（两个人聊天）。</summary>
        public const int SocialCapacity = 2;

        private static SceneAffordances _instance;

        private AffordanceRegistry _registry;

        /// <summary>存活实例（未挂载/已卸载为 null）。</summary>
        public static SceneAffordances Instance => _instance;

        /// <summary>场景可供性注册表（未挂载为 null）。</summary>
        public AffordanceRegistry Registry => _registry;

        /// <summary>已登记条目数（诊断/测试）。</summary>
        public int RegisteredCount => _registry != null ? _registry.Count : 0;

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
            if (!scene.IsValid() || scene.name != GameSceneName)
            {
                return;
            }

            if (_instance != null)
            {
                // 场景重载：锚点是新对象，旧条目（含占用）必须整体作废。
                _instance.RegisterSceneAffordances();
                return;
            }

            var host = new GameObject("~SceneAffordances");
            DontDestroyOnLoad(host);
            host.AddComponent<SceneAffordances>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _registry = new AffordanceRegistry();
            RegisterSceneAffordances();
        }

        /// <summary>
        /// 重新扫描并登记（幂等入口：测试 V2 用它对比注册前后 Renderer 数量；
        /// 场景重载时也走这里）。会清空旧条目，因此旧占用也随之失效——这是
        /// 「重扫 = 世界换了」的语义，不是 bug。
        /// </summary>
        public void RegisterSceneAffordances()
        {
            if (_registry == null)
            {
                _registry = new AffordanceRegistry();
            }

            for (int i = _registry.Count - 1; i >= 0; i--)
            {
                _registry.Unregister(_registry.Affordances[i]);
            }

            Register("scene.work.barrel", AffordanceKind.Work, ResolveAnchor(WorkAnchorName, WorkFallback), WorkRadius, WorkCapacity);
            Register("scene.rest.tree", AffordanceKind.Rest, ResolveAnchor(RestAnchorName, RestFallback), RestRadius, RestCapacity);
            Register("scene.social.gathering", AffordanceKind.Social, ResolveAnchor(SocialAnchorName, SocialFallback), SocialRadius, SocialCapacity);

            Debug.Log($"[SceneAffordances] SCN_Game registered {_registry.Count} affordances (Work/Rest/Social), no renderer created.");
        }

        private void Register(string id, AffordanceKind kind, Vector3 position, float radius, int capacity)
        {
            _registry.Register(new Affordance(id, kind, position, radius, capacity));
        }

        /// <summary>按名字找锚点；找不到打日志并退到兜底坐标。</summary>
        private static Vector3 ResolveAnchor(string anchorName, Vector3 fallback)
        {
            GameObject anchor = GameObject.Find(anchorName);
            if (anchor == null)
            {
                Debug.LogWarning(
                    $"[SceneAffordances] anchor '{anchorName}' not found in {GameSceneName}; " +
                    $"using fallback ({fallback.x:0.##}, {fallback.y:0.##}, {fallback.z:0.##}).");
                return fallback;
            }

            Vector3 position = anchor.transform.position;
            position.y = 0f; // 可供性在 XZ 平面上判定
            return position;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
