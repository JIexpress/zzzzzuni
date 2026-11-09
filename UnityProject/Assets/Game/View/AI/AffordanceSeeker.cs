using System;
using System.Collections.Generic;
using Game.Domain.AI.Affordances;
using Game.Domain.AI.Utility;
using UnityEngine;

namespace Game.View.AI
{
    /// <summary>
    /// TASK-A-AI-04：会走路的 NPC——自己的 <see cref="UtilityBrain"/>（Idle + 4 个
    /// 可供性动作）选中一个可供性后，朝它以 <see cref="MoveSpeed"/>（默认 1.5 m/s）
    /// 走过去；到达半径内即 <see cref="AffordanceRegistry.Claim"/> 并停住。
    ///
    /// 设计边界（很重要）：
    ///  - **不自动挂载**：本组件不会被场景/宿主 AddComponent，必须由调用方显式挂到
    ///    NPC 上（默认零影响，V6）。
    ///  - **只改 transform 的 XZ**：不碰 Rigidbody / Animator / 旋转，避免与既有
    ///    NpcUtilityView 或玩家驱动抢同一个对象。
    ///  - 决策仍然完全在 Domain：本类只做「驱动」（tick brain / 平移 / 计时），
    ///    不复制打分公式。
    ///  - 超时保护：同一个目标追 <see cref="AbandonSeconds"/>（默认 10s）未达 →
    ///    释放占用、停止（防卡死）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AffordanceSeeker : MonoBehaviour
    {
        /// <summary>默认移动速度（米/秒）。</summary>
        public const float DefaultMoveSpeed = 1.5f;

        /// <summary>默认单个目标的追逐上限（秒）。</summary>
        public const float DefaultAbandonSeconds = 10f;

        private const float ArriveEpsilon = 1e-4f;

        /// <summary>默认大脑覆盖的四种可供性（顺序 = 同分时的注册顺序）。</summary>
        private static readonly AffordanceKind[] SeekKinds =
        {
            AffordanceKind.Rest,
            AffordanceKind.Work,
            AffordanceKind.Social,
            AffordanceKind.Eat,
        };

        private readonly Dictionary<string, AffordanceAction> _trackedByName =
            new Dictionary<string, AffordanceAction>(StringComparer.Ordinal);

        private AffordanceRegistry _registry;
        private UtilityBrain _brain;
        private NPCWorldState _state;
        private Affordance _target;
        private string _agentId;
        private float _clock;
        private float _chaseStartedAt;
        private bool _seeking;
        private bool _arrived;

        /// <summary>占用身份（注册表里区分 NPC 的唯一 id）。</summary>
        public string AgentId => _agentId;

        /// <summary>移动速度（米/秒），默认 1.5。</summary>
        public float MoveSpeed { get; set; } = DefaultMoveSpeed;

        /// <summary>单个目标追逐上限（秒），默认 10。</summary>
        public float AbandonSeconds { get; set; } = DefaultAbandonSeconds;

        /// <summary>true 时 Update 不驱动（测试用 <see cref="SimulateForTests"/> 确定性推进）。</summary>
        public bool ManualDrive { get; set; }

        /// <summary>注入/解析出的注册表（未配置为 null）。</summary>
        public AffordanceRegistry Registry => _registry;

        /// <summary>当前大脑（未配置为 null）。</summary>
        public UtilityBrain Brain => _brain;

        /// <summary>泵给大脑的世界状态（测试可写入 Fatigue/Trust 等）。</summary>
        public NPCWorldState State => _state;

        /// <summary>当前目标（未选中为 null）。</summary>
        public Affordance CurrentTarget => _target;

        /// <summary>是否正在寻找/占用（<see cref="Stop"/> 后为 false）。</summary>
        public bool IsSeeking => _seeking;

        /// <summary>是否已到达目标半径内。</summary>
        public bool IsArrived => _arrived;

        /// <summary>当前目标已经追了多久（秒；无目标为 0）。</summary>
        public float ChaseSeconds => _target == null ? 0f : _clock - _chaseStartedAt;

        /// <summary>上一拍大脑选中的动作名（或 Idle）。</summary>
        public string LastChoice { get; private set; } = UtilityBrain.IdleActionName;

        /// <summary>内部单调时钟（秒）。</summary>
        public float Clock => _clock;

        private void Awake()
        {
            _agentId = "seeker:" + GetInstanceID();
            _state = new NPCWorldState();

            // 默认需求：疲劳 0.6（想歇）+ 信任 0.2（愿意凑人）。测试可直接改 State。
            _state.Set(NPCWorldState.Keys.Fatigue, 0.6f);
            _state.Set(NPCWorldState.Keys.Trust, 0.2f);
        }

        private void Update()
        {
            if (ManualDrive || !_seeking)
            {
                return;
            }

            Step(Time.deltaTime);
        }

        /// <summary>
        /// 注入注册表与大脑（文档要求的测试入口）。<paramref name="brain"/> 为 null
        /// 时构建默认大脑：Idle + 4 个 <see cref="AffordanceAction"/>（Rest/Work/Social/Eat）。
        /// </summary>
        public void Configure(AffordanceRegistry registry, UtilityBrain brain = null)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            Stop();
            _registry = registry;
            _brain = brain ?? BuildDefaultBrain(registry);
            if (_brain == null)
            {
                throw new ArgumentNullException(nameof(brain));
            }
        }

        /// <summary>开始寻找（幂等；会先释放上一次的占用并立刻选一次目标）。</summary>
        public void StartSeeking()
        {
            if (!EnsureConfigured())
            {
                return;
            }

            ReleaseOwnedExcept(null);
            _target = null;
            _arrived = false;
            _seeking = true;
            _chaseStartedAt = _clock;

            Step(0f); // 立刻选目标（dt = 0，不产生位移）
        }

        /// <summary>停止并释放占用；之后 Update / Simulate 都不再移动。</summary>
        public void Stop()
        {
            _seeking = false;
            _arrived = false;
            _target = null;
            ReleaseOwnedExcept(null);
        }

        /// <summary>测试/调试：以固定步长推进若干秒（不依赖帧率）。</summary>
        public void SimulateForTests(float seconds, float step = 1f / 60f)
        {
            if (seconds <= 0f || step <= 0f)
            {
                return;
            }

            for (float elapsed = 0f; elapsed < seconds; elapsed += step)
            {
                Step(Mathf.Min(step, seconds - elapsed));
            }
        }

        private bool EnsureConfigured()
        {
            if (_registry != null && _brain != null)
            {
                return true;
            }

            SceneAffordances scene = SceneAffordances.Instance;
            if (scene == null || scene.Registry == null)
            {
                Debug.LogError($"[AffordanceSeeker] '{name}' has no registry: SCN_Game/SceneAffordances is not available.");
                return false;
            }

            Configure(scene.Registry, null);
            return true;
        }

        private UtilityBrain BuildDefaultBrain(AffordanceRegistry registry)
        {
            _trackedByName.Clear();

            var brain = new UtilityBrain();
            brain.Add(new UtilityAction(UtilityBrain.IdleActionName, _ => 0f, () => { }));

            for (int i = 0; i < SeekKinds.Length; i++)
            {
                AffordanceAction tracked = AffordanceAction.CreateTracked(
                    registry,
                    SeekKinds[i],
                    _agentId,
                    () => transform.position);

                _trackedByName[tracked.Action.Name] = tracked;
                brain.Add(tracked.Action);
            }

            return brain;
        }

        private void Step(float deltaTime)
        {
            if (!_seeking || _registry == null || _brain == null)
            {
                return;
            }

            float dt = Mathf.Max(0f, deltaTime);
            _clock += dt;

            // 关键顺序：先把自己上一拍的占用放掉，再让大脑打分。
            // AffordanceAction 的 Score 走 FindBest，而 FindBest 会跳过「已满」的条目——
            // 若不放，自己占着的目标在下一拍就变成不可用、分数归零，NPC 会在几种
            // 可供性之间来回抖（永远到不了、也永远不超时）。释放只发生在这一拍内部，
            // 本拍结束时赢家会重新 Claim，外部看到的占用是连续的。
            ReleaseOwnedExcept(null);

            string choice = _brain.Tick(_state, _clock);
            LastChoice = choice;

            Affordance next = TargetFor(choice);
            if (next == null)
            {
                // 没有可用目标（Idle / 全被占）：清空占用，下一拍再挑。
                ReleaseOwnedExcept(null);
                _target = null;
                _arrived = false;
                _chaseStartedAt = _clock;
                return;
            }

            ReleaseOwnedExcept(next);

            if (!ReferenceEquals(next, _target))
            {
                _target = next;
                _arrived = false;
                _chaseStartedAt = _clock;
            }

            if (_arrived)
            {
                // 已到达：保持占用、保持不动（Claim 幂等，防止被别的 NPC 抢走后静默失效）。
                _registry.Claim(_target, _agentId);
                return;
            }

            if (_target.FlatDistanceTo(transform.position) <= _target.Radius + ArriveEpsilon)
            {
                Arrive();
                return;
            }

            if (_clock - _chaseStartedAt >= AbandonSeconds)
            {
                Abandon();
                return;
            }

            MoveFlat(_target.Position, dt);

            if (_target.FlatDistanceTo(transform.position) <= _target.Radius + ArriveEpsilon)
            {
                Arrive();
            }
        }

        private Affordance TargetFor(string choice)
        {
            if (string.IsNullOrEmpty(choice) || choice == UtilityBrain.IdleActionName)
            {
                return null;
            }

            if (_trackedByName.TryGetValue(choice, out AffordanceAction tracked))
            {
                return tracked.LastClaimed;
            }

            // 注入的第三方大脑：没有 tracked 句柄时以「本 agent 实际占用的最近条目」为准。
            return NearestOwnedAffordance();
        }

        private Affordance NearestOwnedAffordance()
        {
            Affordance best = null;
            float bestDistance = float.PositiveInfinity;
            IReadOnlyList<Affordance> all = _registry.Affordances;
            for (int i = 0; i < all.Count; i++)
            {
                Affordance candidate = all[i];
                if (!candidate.IsOccupiedBy(_agentId))
                {
                    continue;
                }

                float distance = candidate.FlatDistanceTo(transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }

        private void ReleaseOwnedExcept(Affordance keep)
        {
            if (_registry == null)
            {
                return;
            }

            IReadOnlyList<Affordance> all = _registry.Affordances;
            for (int i = 0; i < all.Count; i++)
            {
                Affordance candidate = all[i];
                if (!ReferenceEquals(candidate, keep) && candidate.IsOccupiedBy(_agentId))
                {
                    _registry.Release(candidate, _agentId);
                }
            }
        }

        private void Arrive()
        {
            if (_target == null)
            {
                return;
            }

            if (!_registry.Claim(_target, _agentId))
            {
                // 被别人占了：丢掉目标，下一拍重新选。
                _target = null;
                _arrived = false;
                _chaseStartedAt = _clock;
                return;
            }

            _arrived = true;
        }

        private void Abandon()
        {
            if (_target != null)
            {
                Debug.LogWarning(
                    $"[AffordanceSeeker] '{name}' gave up on '{_target.Id}' after {AbandonSeconds:0.#}s without arriving.");
            }

            ReleaseOwnedExcept(null);
            _target = null;
            _arrived = false;
            _seeking = false;
        }

        /// <summary>只改 XZ：Y 与旋转保持原样，避免和别人的驱动打架。</summary>
        private void MoveFlat(Vector3 destination, float deltaTime)
        {
            Vector3 position = transform.position;
            Vector3 delta = destination - position;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance <= ArriveEpsilon)
            {
                return;
            }

            float step = Mathf.Min(MoveSpeed * deltaTime, distance);
            Vector3 next = position + (delta / distance) * step;
            transform.position = new Vector3(next.x, position.y, next.z);
        }

        private void OnDestroy()
        {
            Stop();
        }
    }
}
