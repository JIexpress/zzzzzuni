using System;
using System.Collections.Generic;
using Game.Domain.AI;
using Game.Domain.Enemy;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Enemy behaviour tree tests (TASK-A-R8-03): the brain is driven purely by
    /// blackboard pumps — distance sequences assert the state transitions the
    /// brief asks for (vision enter → attack distance → event), plus hysteresis,
    /// mid-chase death, runtime retuning through blackboard keys, delegate
    /// cache identity of the shipped tree, and the Fsm/BehaviorTree mode switch
    /// of <see cref="EnemyController"/> (the FSM version stays as control group).
    /// TASK-A-R9-09 adds the unified death announcement: Kill raises
    /// <see cref="EnemyStateChanged"/> exactly once with the FSM brain's payload
    /// (previous behaviour → Dead → position), and both brains exit death
    /// symmetrically through the controller's forwarding chain.
    /// </summary>
    public sealed class EnemyBehaviorTreeTests
    {
        private static readonly Vec2 FarAway = new Vec2(100f, 100f);

        private static EnemyTuning Tuning => new EnemyTuning(4f, 6f, 1.5f, 3f, 1f, 0.1f);

        private static EnemyBehaviorTree CreateEnemy(Vec2 start, Vec2[] waypoints, Vec2 player, float attackRange = 1.5f, float cooldown = 1f)
        {
            var enemy = new EnemyBehaviorTree("bt-enemy", start, waypoints, Tuning, attackRange, cooldown);
            enemy.SetPlayerPosition(player);
            return enemy;
        }

        /// <summary>Walks the shipped tree and collects every leaf delegate (cache-identity assertions).</summary>
        private static void CollectLeaves(BtNode node, List<BtCondition> conditions, List<BtAction> actions)
        {
            switch (node)
            {
                case BtCondition condition:
                    conditions.Add(condition);
                    break;
                case BtAction action:
                    actions.Add(action);
                    break;
                case Sequence sequence:
                    foreach (BtNode child in sequence.Children)
                    {
                        CollectLeaves(child, conditions, actions);
                    }

                    break;
                case Selector selector:
                    foreach (BtNode child in selector.Children)
                    {
                        CollectLeaves(child, conditions, actions);
                    }

                    break;
                case Parallel parallel:
                    foreach (BtNode child in parallel.Children)
                    {
                        CollectLeaves(child, conditions, actions);
                    }

                    break;
                case Invert invert:
                    CollectLeaves(invert.Child, conditions, actions);
                    break;
                case Repeat repeat:
                    CollectLeaves(repeat.Child, conditions, actions);
                    break;
                case Cooldown cooldown:
                    CollectLeaves(cooldown.Child, conditions, actions);
                    break;
                case Succeeder succeeder:
                    CollectLeaves(succeeder.Child, conditions, actions);
                    break;
            }
        }

        [Test]
        public void FreshEnemy_NoWaypoints_FarPlayer_TicksIdle()
        {
            EnemyBehaviorTree enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);

            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyBehaviorState.Idle, enemy.CurrentBehaviour);
            Assert.AreEqual(BTStatus.Success, enemy.LastStatus);
            Assert.IsFalse(enemy.IsDead);
        }

        [Test]
        public void Patrol_WalksWaypoints_AndAdvancesIndexOnArrival()
        {
            var enemy = CreateEnemy(new Vec2(1.95f, 0f), new[] { new Vec2(2f, 0f), new Vec2(0f, 2f) }, FarAway);

            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyBehaviorState.Patrol, enemy.CurrentBehaviour);
            Assert.AreEqual(1, enemy.WaypointIndex, "0.15s·1.5m/s 跨过 0.05m 剩余距离：路点应前进");
            Assert.That(enemy.Position.X, Is.EqualTo(2f).Within(1e-3f));
        }

        [Test]
        public void PlayerEntersVision_Chase_AndMovesTowardPlayer()
        {
            EnemyBehaviorTree enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));

            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyBehaviorState.Chase, enemy.CurrentBehaviour);
            Assert.AreEqual(BTStatus.Running, enemy.LastStatus, "追击未完成：树必须保持 Running");
            Assert.That(enemy.Position.X, Is.EqualTo(0.3f).Within(1e-3f), "3m/s·0.1s 朝玩家移动");
        }

        [Test]
        public void DistanceSequence_Patrol_ThenChase_ThenAttack_FiresEvent()
        {
            var ownerEvents = new List<EnemyAttackRequested>();
            var busEvents = new List<EnemyAttackRequested>();
            var enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(2f, 0f) }, new Vec2(10f, 0f));
            enemy.EnemyAttackRequested += ownerEvents.Add;

            using (EventBus.Subscribe<EnemyAttackRequested>(busEvents.Add))
            {
                enemy.Tick(0.1f); // 10m：视野外 → 巡逻
                Assert.AreEqual(EnemyBehaviorState.Patrol, enemy.CurrentBehaviour);

                enemy.SetPlayerPosition(new Vec2(3f, 0f));
                enemy.Tick(0.1f); // 2.85m：视野内 → 追击
                Assert.AreEqual(EnemyBehaviorState.Chase, enemy.CurrentBehaviour);

                enemy.SetPlayerPosition(new Vec2(1f, 0f));
                enemy.Tick(0.1f); // 0.55m：攻击距离 → 同一 tick 内 chase 完成、attack 触发
                Assert.AreEqual(EnemyBehaviorState.Attack, enemy.CurrentBehaviour);
            }

            Assert.AreEqual(1, ownerEvents.Count, "进入攻击距离恰好触发一次挥击");
            Assert.AreEqual(1, busEvents.Count, "事件同时上 EventBus（EnemyStateChanged 同款纪律）");
            Assert.AreEqual("bt-enemy", ownerEvents[0].EnemyId);
            Assert.That(ownerEvents[0].EnemyPosition.X, Is.EqualTo(0.45f).Within(1e-3f), "挥击发生在追击中的当前位置");
            Assert.That(ownerEvents[0].PlayerPosition.X, Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void AttackCooldown_ThrottlesSwings_WhileHoldingPosition()
        {
            var events = new List<EnemyAttackRequested>();
            var enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(1f, 0f), 1.5f, 0.5f);
            enemy.EnemyAttackRequested += events.Add;

            enemy.Tick(0.25f); // 就绪 → 挥击
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(EnemyBehaviorState.Attack, enemy.CurrentBehaviour);

            enemy.Tick(0.25f); // 窗口内：不挥击，但分支保持占用（原地攻击硬直）
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(EnemyBehaviorState.Attack, enemy.CurrentBehaviour, "冷却期应保持攻击行为，不许掉回巡逻");

            enemy.Tick(0.25f); // 累计到 0.5s 的入口检查 tick，仍未重开
            Assert.AreEqual(1, events.Count);

            enemy.Tick(0.25f); // 窗口重开 → 第二次挥击
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(EnemyBehaviorState.Attack, enemy.CurrentBehaviour);
        }

        [Test]
        public void Chase_MidRunning_UsesLoseRangeHysteresis_ThenGivesUp()
        {
            var enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(2f, 0f) }, new Vec2(3f, 0f));

            enemy.Tick(0.1f); // 进入追击（Running，位置 0.3）
            Assert.AreEqual(EnemyBehaviorState.Chase, enemy.CurrentBehaviour);

            enemy.SetPlayerPosition(new Vec2(5f, 0f)); // 4.7m：超出视野 4m，但未超放弃 6m
            enemy.Tick(0.1f);
            Assert.AreEqual(EnemyBehaviorState.Chase, enemy.CurrentBehaviour, "Running 分支的放弃判定走 LoseRange（迟滞）");
            Assert.That(enemy.Position.X, Is.EqualTo(0.6f).Within(1e-3f), "迟滞期间继续追");

            enemy.SetPlayerPosition(new Vec2(7f, 0f)); // 6.4m > LoseRange
            enemy.Tick(0.1f);
            Assert.AreEqual(EnemyBehaviorState.Patrol, enemy.CurrentBehaviour, "彻底放弃 → 回到巡逻");
        }

        [Test]
        public void KillMidChase_DeadTakesOver_AndTheCorpseNeverMoves()
        {
            var events = new List<EnemyAttackRequested>();
            var enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(2f, 0f) }, new Vec2(3f, 0f));
            enemy.EnemyAttackRequested += events.Add;

            enemy.Tick(0.1f); // 追击中（Running，位置 0.3）
            Assert.AreEqual(EnemyBehaviorState.Chase, enemy.CurrentBehaviour);

            enemy.Kill();
            Assert.AreEqual(EnemyBehaviorState.Dead, enemy.CurrentBehaviour, "Kill 后读数立刻反映死亡（最多一个 stale tick 被掩蔽）");

            enemy.Tick(0.1f); // resume 到 chase 动作 → 死亡快速失败 → 本 tick 落空
            enemy.Tick(0.1f); // 重新评估 → Dead 分支接管
            Assert.AreEqual(EnemyBehaviorState.Dead, enemy.CurrentBehaviour);
            Assert.AreEqual(BTStatus.Success, enemy.LastStatus, "Dead 分支最终接管并成功");
            Assert.That(enemy.Position.X, Is.EqualTo(0.3f).Within(1e-3f), "尸体不许移动");
            Assert.AreEqual(0, events.Count, "死人不挥击");
        }

        [Test]
        public void BlackboardRetune_ChangesBehaviour_DataDriven()
        {
            var enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(2f, 0f) }, new Vec2(3f, 0f));

            // 3m 原本在视野(4m)内；把黑板里的视野键收紧到 1m → 立刻不再追击。
            enemy.Blackboard.Set(EnemyBehaviorTree.Keys.ChaseRange, 1f);
            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyBehaviorState.Patrol, enemy.CurrentBehaviour, "条件只读黑板：改键即改行为");
        }

        [Test]
        public void ShippedTree_DelegatesAreCached_ReferenceStableAcrossTicks()
        {
            var enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(2f, 0f) }, FarAway);
            var conditions = new List<BtCondition>();
            var actions = new List<BtAction>();
            CollectLeaves(enemy.Root, conditions, actions);

            Assert.AreEqual(3, conditions.Count, "树里有三个条件叶子：Dead/PlayerVisible/InAttackRange");
            Assert.AreEqual(4, actions.Count, "树里有四个动作叶子：EnterDead/Chase/Attack/Patrol");

            var beforeConditions = new List<Func<BtContext, bool>>(conditions.ConvertAll(c => c.Predicate));
            var beforeActions = new List<Func<BtContext, BTStatus>>(actions.ConvertAll(a => a.Action));

            enemy.Tick(0.1f);
            enemy.SetPlayerPosition(new Vec2(3f, 0f));
            enemy.Tick(0.1f);
            enemy.SetPlayerPosition(new Vec2(1f, 0f));
            enemy.Tick(0.1f);

            for (int i = 0; i < conditions.Count; i++)
            {
                Assert.AreSame(beforeConditions[i], conditions[i].Predicate, "条件委托必须构造期缓存，逐 tick 复用同一实例");
            }

            for (int i = 0; i < actions.Count; i++)
            {
                Assert.AreSame(beforeActions[i], actions[i].Action, "动作委托必须构造期缓存，逐 tick 复用同一实例");
            }
        }

        [Test]
        public void Controller_DefaultMode_IsBehaviorTree_AndForwardsAttackEvents()
        {
            var events = new List<EnemyAttackRequested>();
            var controller = new EnemyController("ctrl-1", Vec2.Zero, new[] { new Vec2(2f, 0f) }, Tuning);
            controller.EnemyAttackRequested += events.Add;

            Assert.AreEqual(EnemyDriveMode.BehaviorTree, controller.Mode, "默认驱动模式必须是行为树");
            Assert.IsNotNull(controller.BehaviorTree);
            Assert.IsNull(controller.Fsm);

            controller.SetPlayerPosition(new Vec2(1f, 0f)); // 直接进入攻击距离
            controller.Tick(0.1f);

            Assert.AreEqual(EnemyBehaviorState.Attack, controller.CurrentBehaviour);
            Assert.AreEqual(1, events.Count, "树的事件必须穿透控制器");
            Assert.IsFalse(controller.IsDead);

            controller.Kill();
            controller.Tick(0.1f);
            Assert.IsTrue(controller.IsDead);
            Assert.AreEqual(EnemyBehaviorState.Dead, controller.CurrentBehaviour);
        }

        [Test]
        public void Controller_FsmMode_KeepsFsmAlive_NeverFiresAttack()
        {
            var events = new List<EnemyAttackRequested>();
            var controller = new EnemyController(
                "ctrl-2", Vec2.Zero, new[] { new Vec2(2f, 0f) }, Tuning, EnemyDriveMode.Fsm);
            controller.EnemyAttackRequested += events.Add;

            Assert.AreEqual(EnemyDriveMode.Fsm, controller.Mode);
            Assert.IsNotNull(controller.Fsm, "Fsm 版保留：模式可切回");
            Assert.IsNull(controller.BehaviorTree);

            controller.SetPlayerPosition(new Vec2(1f, 0f)); // FSM 语义：距离 1m 直接触发追击
            controller.Tick(0.1f);

            Assert.AreEqual(EnemyStateId.Chase, controller.Fsm.StateId);
            Assert.AreEqual(EnemyBehaviorState.Chase, controller.CurrentBehaviour, "FSM 的 Chase 映射到统一读数");
            Assert.AreEqual(0, events.Count, "FSM 版没有攻击行为，绝不发攻击事件");

            controller.Kill();
            Assert.IsTrue(controller.IsDead);
            Assert.AreEqual(EnemyBehaviorState.Dead, controller.CurrentBehaviour);
        }

        // ---- TASK-A-R9-09: the unified death announcement -------------------

        [Test]
        public void Kill_AnnouncesEnemyStateChangedOnce_WithFsmPayload()
        {
            var ownerEvents = new List<EnemyStateChanged>();
            var busEvents = new List<EnemyStateChanged>();
            var enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(2f, 0f) }, new Vec2(3f, 0f));
            enemy.EnemyStateChanged += ownerEvents.Add;

            using (EventBus.Subscribe<EnemyStateChanged>(busEvents.Add))
            {
                enemy.Tick(0.1f); // 追击中（3 m/s · 0.1 s = 0.3 m）
                Assert.AreEqual(EnemyBehaviorState.Chase, enemy.CurrentBehaviour);
                Assert.AreEqual(0, ownerEvents.Count, "树脑平时不发状态事件：死亡是唯一的宣告");

                enemy.Kill();

                Assert.AreEqual(1, ownerEvents.Count, "Kill 首次执行分支恰好补发一次死亡事件");
                Assert.AreEqual(1, busEvents.Count, "与 Fsm 版同款纪律：死亡事件同时上 EventBus（火花侧听总线）");

                enemy.Kill(); // 幂等（既有）：不得重发
                Assert.AreEqual(1, ownerEvents.Count, "Kill 幂等 → 死亡事件只发一次");
                Assert.AreEqual(1, busEvents.Count);
            }

            Assert.AreEqual("bt-enemy", ownerEvents[0].EnemyId);
            Assert.AreEqual(EnemyStateId.Chase, ownerEvents[0].Previous, "载荷与 Fsm 版同语义：死亡前在追击");
            Assert.AreEqual(EnemyStateId.Dead, ownerEvents[0].Current);
            Assert.That(ownerEvents[0].Position.X, Is.EqualTo(0.3f).Within(1e-3f), "载荷携带死亡时的平面位置（A-R9-07 同款字段）");
            Assert.AreEqual(EnemyStateId.Dead, busEvents[0].Current);
            Assert.That(busEvents[0].Position.X, Is.EqualTo(0.3f).Within(1e-3f), "C# 事件与 EventBus 载荷一致");
        }

        [Test]
        public void Kill_Twice_DoesNotRefire_EvenWithoutAnyTick()
        {
            var ownerEvents = new List<EnemyStateChanged>();
            var busEvents = new List<EnemyStateChanged>();
            var enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);

            using (EventBus.Subscribe<EnemyStateChanged>(busEvents.Add))
            {
                enemy.EnemyStateChanged += ownerEvents.Add;
                enemy.Kill();
                enemy.Kill();
                enemy.Kill();

                Assert.AreEqual(1, ownerEvents.Count, "三次 Kill 只允许一次宣告");
                Assert.AreEqual(1, busEvents.Count, "EventBus 侧同样不重发");
            }

            Assert.AreEqual(EnemyStateId.Idle, ownerEvents[0].Previous, "未 Tick 即杀：前状态是构造期初始 Idle（与 Fsm 版 Idle 起步同语义）");
            Assert.AreEqual(EnemyStateId.Dead, ownerEvents[0].Current);
            Assert.AreEqual(Vec2.Zero, ownerEvents[0].Position, "未移动即杀：死亡位置就是出生点");
        }

        [Test]
        public void Controller_DeathEvent_IsSymmetricAcrossBothBrains()
        {
            // 同 id、同起点、无 Tick 直接击杀：两脑经控制器转发链发出的死亡
            // 载荷必须逐字段一致（TASK-A-R9-09 的对称性要求）。
            var btEvents = new List<EnemyStateChanged>();
            var btController = new EnemyController("ctrl-both", Vec2.Zero, new Vec2[0], Tuning);
            btController.EnemyStateChanged += btEvents.Add;
            btController.Kill();
            btController.Kill(); // BT 脑内部幂等

            var fsmEvents = new List<EnemyStateChanged>();
            var fsmController = new EnemyController("ctrl-both", Vec2.Zero, new Vec2[0], Tuning, EnemyDriveMode.Fsm);
            fsmController.EnemyStateChanged += fsmEvents.Add;
            fsmController.Kill();
            fsmController.Kill(); // FSM 转换表拒绝 Dead→Dead，不重发

            Assert.AreEqual(1, btEvents.Count, "BT 脑死亡走控制器转发链（OnTreeAttackRequested 同链）恰好一次");
            Assert.AreEqual(1, fsmEvents.Count, "FSM 脑死亡经同一出口恰好一次（二次 Kill 被拒绝）");

            Assert.AreEqual(btEvents[0].EnemyId, fsmEvents[0].EnemyId);
            Assert.AreEqual(btEvents[0].Previous, fsmEvents[0].Previous, "双脑前状态读数一致（Idle 起步）");
            Assert.AreEqual(EnemyStateId.Dead, btEvents[0].Current);
            Assert.AreEqual(EnemyStateId.Dead, fsmEvents[0].Current);
            Assert.AreEqual(btEvents[0].Position, fsmEvents[0].Position, "双脑死亡位置一致");
        }
    }
}
