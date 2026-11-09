using System.Collections.Generic;
using Game.Domain.Survival;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.View.Survival
{
    /// <summary>
    /// TASK-A-LOOP-01：最小可玩循环的**表现层编排**——守住火堆 90 秒。
    /// TASK-A-LOOP-02 补上战斗：按 J 用身前扇形打死敌人，击杀数进 HUD。
    ///
    /// 设计边界（很重要）：
    ///  - 规则全部在 <see cref="SurvivalLoopState"/>（纯逻辑，EditMode 覆盖）；
    ///    本类只做：计时泵、刷怪、移动、受击、HUD、重开。
    ///  - 只有 SCN_Game 会挂载；**默认待机**（不建任何可见物、不刷怪），按 F 才开始。
    ///    这样既有像素证据用例（后处理/开门火花/相机差分）在场景里跑时画面完全不受影响。
    ///  - HUD 用 IMGUI：这是刻意的占位选择——不依赖 Canvas/字体资产，也不会被相机
    ///    RenderTexture 捕获，避免污染像素用例。正式 UI 后续接 UIFramework。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SurvivalLoop : MonoBehaviour
    {
        /// <summary>挂载门控：只有这个场景会生成循环宿主。</summary>
        public const string GameSceneName = "SCN_Game";

        /// <summary>火堆位置（场景原点，便于复用现有的中央空地）。</summary>
        public static readonly Vector3 FirePosition = new Vector3(0f, 0f, 0f);

        private const float AttackerSpeed = 3.2f;
        private const float AttackerReach = 2.2f;
        private const int AttackerDamage = 8;
        private const float SpawnRadius = 18f;

        // ---- TASK-A-LOOP-02 战斗数值（纯 View 侧几何参数，规则仍归状态机）----
        /// <summary>攻击距离（米，水平面）。</summary>
        public const float AttackRange = 2.5f;

        /// <summary>扇形半角余弦：cos(60°) = 0.5，即前方 ±60°。</summary>
        private const float AttackHalfAngleCos = 0.5f;

        /// <summary>攻击冷却（秒）。</summary>
        public const float AttackCooldownSeconds = 0.35f;

        /// <summary>每次攻击伤害（敌人 2 点血 → 两下致死）。</summary>
        public const int AttackDamage = 1;

        /// <summary>攻击键（旧输入；刻意不新增 Input System action，避免牵动重绑定矩阵）。</summary>
        private const KeyCode AttackKey = KeyCode.J;

        private static SurvivalLoop _instance;

        private readonly List<GameObject> _attackers = new List<GameObject>();
        private GameObject _fire;
        private SurvivalLoopState _state;
        private Transform _player;
        private bool _playerResolved;
        private float _nextAttackAt;

        /// <summary>存活实例（未挂载/已卸载为 null）。</summary>
        public static SurvivalLoop Instance => _instance;

        /// <summary>当前状态机（未挂载为 null）。</summary>
        public SurvivalLoopState State => _state;

        /// <summary>当前敌人数量（诊断/测试）。</summary>
        public int AttackerCount => _attackers.Count;

        /// <summary>火堆对象（未开始为 null）。</summary>
        public GameObject Fire => _fire;

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
                return;
            }

            var host = new GameObject("~SurvivalLoop");
            DontDestroyOnLoad(host);
            host.AddComponent<SurvivalLoop>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _state = new SurvivalLoopState();
        }

        private void Update()
        {
            if (_state == null || _state.Phase != SurvivalPhase.Running)
            {
                if (_state != null && _state.Phase == SurvivalPhase.Idle && Input.GetKeyDown(KeyCode.F))
                {
                    StartLoop();
                }
                else if (_state != null && _state.IsFinished && Input.GetKeyDown(KeyCode.R))
                {
                    Restart();
                }

                return;
            }

            _state.Tick(Time.deltaTime);

            while (_state.TryConsumeWave())
            {
                SpawnWave(SurvivalLoopState.WaveSize(_state.WaveIndex));
            }

            MoveAttackers(Time.deltaTime);
            HandleAttackInput();
        }

        /// <summary>
        /// 攻击输入（J）：冷却门在输入路径上，<see cref="Attack"/> 本身只做几何判定，
        /// 这样测试可以直接连打两下而不受 Time.time 影响。
        /// </summary>
        private void HandleAttackInput()
        {
            if (!Input.GetKeyDown(AttackKey) || Time.time < _nextAttackAt)
            {
                return;
            }

            _nextAttackAt = Time.time + AttackCooldownSeconds;
            Attack();
        }

        /// <summary>
        /// 立刻判定一次攻击（不含冷却）：向玩家身前 ±60°、2.5m 内的存活敌人各造成 1 点伤害。
        /// 打断致死的敌人交给 <see cref="SurvivalLoopState.RegisterKill"/> 记账后由本类销毁。
        /// 返回命中的敌人数（测试/诊断用）。
        /// </summary>
        public int Attack()
        {
            if (_state == null || _state.Phase != SurvivalPhase.Running)
            {
                return 0;
            }

            Transform origin = ResolvePlayer();
            if (origin == null)
            {
                return 0;
            }

            Vector3 originPoint = origin.position;
            originPoint.y = 0f;

            Vector3 forward = origin.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();

            int hits = 0;
            for (int i = _attackers.Count - 1; i >= 0; i--)
            {
                GameObject attacker = _attackers[i];
                if (attacker == null)
                {
                    _attackers.RemoveAt(i);
                    continue;
                }

                var health = attacker.GetComponent<SurvivalAttacker>();
                if (health == null || health.IsDead)
                {
                    continue;
                }

                Vector3 toTarget = attacker.transform.position;
                toTarget.y = 0f;
                Vector3 delta = toTarget - originPoint;
                float distance = delta.magnitude;
                if (distance > AttackRange)
                {
                    continue;
                }

                // 贴脸时方向无意义，直接算命中。
                if (distance > 1e-4f && Vector3.Dot(forward, delta / distance) < AttackHalfAngleCos)
                {
                    continue;
                }

                hits++;
                if (health.TakeDamage(AttackDamage))
                {
                    _state.RegisterKill();
                    _attackers.RemoveAt(i);
                    Destroy(attacker);
                }
            }

            return hits;
        }

        /// <summary>测试/调试：在指定位置生成一个带血量的敌人，返回其对象。</summary>
        public GameObject SpawnAttackerForTests(Vector3 position)
        {
            return SpawnAttacker(position);
        }

        /// <summary>开始一轮（建火堆 + 清空敌人 + Running）。幂等：非 Idle 不重复开始。</summary>
        public void StartLoop()
        {
            if (_state == null || _state.Phase != SurvivalPhase.Idle)
            {
                return;
            }

            EnsureFire();
            ClearAttackers();
            _nextAttackAt = 0f;
            _state.Start();
        }

        /// <summary>重开：清敌人、状态回零、再次开始。</summary>
        public void Restart()
        {
            if (_state == null)
            {
                return;
            }

            ClearAttackers();
            _state.Reset();
            StartLoop();
        }

        /// <summary>测试/调试：直接推进状态机（不产生敌人，不碰渲染）。</summary>
        public void SimulateForTests(float seconds)
        {
            if (_state == null)
            {
                return;
            }

            const float step = 0.25f;
            for (float t = 0f; t < seconds; t += step)
            {
                _state.Tick(step);
            }
        }

        /// <summary>测试/调试：直接对火堆造成伤害。</summary>
        public bool DamageFire(int amount)
        {
            return _state != null && _state.ApplyFireDamage(amount);
        }

        private void EnsureFire()
        {
            if (_fire != null)
            {
                return;
            }

            _fire = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _fire.name = "~SurvivalFire";
            _fire.transform.position = FirePosition + Vector3.up * 0.35f;
            _fire.transform.localScale = new Vector3(1.4f, 0.7f, 1.4f);

            var renderer = _fire.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader != null)
                {
                    var material = new Material(shader) { name = "~SurvivalFireMat" };
                    material.SetColor("_BaseColor", new Color(1f, 0.45f, 0.12f));
                    material.EnableKeyword("_EMISSION");
                    material.SetColor("_EmissionColor", new Color(1f, 0.35f, 0.05f) * 2f);
                    material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    renderer.sharedMaterial = material;
                }
            }
        }

        private void SpawnWave(int count)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);
                Vector3 position = FirePosition
                    + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SpawnRadius;

                SpawnAttacker(position);
            }
        }

        private GameObject SpawnAttacker(Vector3 position)
        {
            var attacker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            attacker.name = "~SurvivalAttacker";
            attacker.transform.position = position + Vector3.up * 0.6f;
            attacker.transform.localScale = new Vector3(0.6f, 1.2f, 0.6f);

            var renderer = attacker.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader != null)
                {
                    var material = new Material(shader) { name = "~SurvivalAttackerMat" };
                    material.SetColor("_BaseColor", new Color(0.55f, 0.12f, 0.12f));
                    renderer.sharedMaterial = material;
                }
            }

            // 必须在材质之后挂载：组件在 Awake 里把当前颜色记为闪白还原色。
            attacker.AddComponent<SurvivalAttacker>();

            _attackers.Add(attacker);
            return attacker;
        }

        /// <summary>攻击原点：场景里的 Player（没有就退到主相机，仍没有则视为无玩家）。</summary>
        private Transform ResolvePlayer()
        {
            if (_playerResolved)
            {
                return _player;
            }

            GameObject player = GameObject.Find("Player");
            if (player != null)
            {
                _player = player.transform;
            }
            else if (Camera.main != null)
            {
                _player = Camera.main.transform;
            }

            _playerResolved = true;
            return _player;
        }

        private void MoveAttackers(float deltaTime)
        {
            for (int i = _attackers.Count - 1; i >= 0; i--)
            {
                GameObject attacker = _attackers[i];
                if (attacker == null)
                {
                    _attackers.RemoveAt(i);
                    continue;
                }

                Vector3 flat = FirePosition - attacker.transform.position;
                flat.y = 0f;
                float distance = flat.magnitude;

                if (distance <= AttackerReach)
                {
                    // 只有活着的敌人才啃火堆；已被打死的（理论上当帧就被移除了）绝不扣耐久。
                    var health = attacker.GetComponent<SurvivalAttacker>();
                    if (health == null || !health.IsDead)
                    {
                        _state.ApplyFireDamage(AttackerDamage);
                    }

                    _attackers.RemoveAt(i);
                    Destroy(attacker);
                    continue;
                }

                Vector3 step = flat.normalized * (AttackerSpeed * deltaTime);
                attacker.transform.position += step;
                if (step.sqrMagnitude > 1e-8f)
                {
                    attacker.transform.rotation = Quaternion.LookRotation(flat.normalized);
                }
            }
        }

        private void ClearAttackers()
        {
            for (int i = 0; i < _attackers.Count; i++)
            {
                if (_attackers[i] != null)
                {
                    Destroy(_attackers[i]);
                }
            }

            _attackers.Clear();
        }

        private void OnDestroy()
        {
            ClearAttackers();

            if (_fire != null)
            {
                Destroy(_fire);
                _fire = null;
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnGUI()
        {
            if (_state == null)
            {
                return;
            }

            var style = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            style.normal.textColor = Color.white;

            if (_state.Phase == SurvivalPhase.Idle)
            {
                GUI.Label(new Rect(20f, 20f, 520f, 32f), "按 F 开始：守住火堆 90 秒", style);
                return;
            }

            GUI.Label(new Rect(20f, 20f, 520f, 32f),
                $"剩余 {Mathf.CeilToInt(_state.RemainingSeconds)}s   第 {Mathf.Max(1, _state.WaveIndex)} 波   击杀 {_state.Kills}",
                style);

            var bar = new Rect(20f, 58f, 260f, 20f);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.45f, 0.15f, 0.95f);
            float fill = Mathf.Clamp01(_state.FireHp / (float)_state.MaxFireHp);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * fill, bar.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(20f, 82f, 300f, 26f), $"火堆耐久 {_state.FireHp}/{_state.MaxFireHp}", style);

            if (_state.Phase == SurvivalPhase.Won)
            {
                GUI.Label(new Rect(20f, 120f, 520f, 32f), $"胜利：守住了！击杀 {_state.Kills}。按 R 重开", style);
            }
            else if (_state.Phase == SurvivalPhase.Lost)
            {
                GUI.Label(new Rect(20f, 120f, 520f, 32f), $"失败：火堆熄灭了。击杀 {_state.Kills}。按 R 重开", style);
            }
        }
    }
}
