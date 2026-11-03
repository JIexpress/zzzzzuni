using System;
using System.Collections.Generic;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Behaviour tree framework tests (TASK-A-R8-03): composite three-state
    /// progression and interrupted-resume semantics, Selector short circuit,
    /// Parallel both policies, decorators (Invert/Repeat/Cooldown/Succeeder),
    /// the Reset re-entrancy guard, construction-time delegate caching
    /// (reference-equality assertions) and blackboard type safety. Pure C# —
    /// the same nodes the enemy tree ships with.
    /// </summary>
    public sealed class BehaviorTreeTests
    {
        /// <summary>Scripted stub: cycles its status script (last entry repeats), remembers Running across ticks.</summary>
        private sealed class StubNode : BtNode
        {
            private readonly BTStatus[] _script;
            private int _index;

            public StubNode(params BTStatus[] script)
            {
                _script = script != null && script.Length > 0 ? script : new[] { BTStatus.Success };
            }

            public int TickCount { get; private set; }

            /// <summary>Monotonic tick counter — deliberately NOT cleared by Reset, so tests can prove a re-tick after a tree Reset.</summary>
            public int TotalTicks { get; private set; }

            /// <summary>True from the moment the stub returned Running until Reset clears it (re-entrancy probe).</summary>
            public bool LeftRunningMemory { get; private set; }

            public override BTStatus Tick(BtContext ctx)
            {
                TickCount++;
                TotalTicks++;
                BTStatus status = _script[Math.Min(_index, _script.Length - 1)];
                _index++;
                if (status == BTStatus.Running)
                {
                    LeftRunningMemory = true;
                }

                return status;
            }

            public override void Reset()
            {
                _index = 0;
                TickCount = 0;
                LeftRunningMemory = false;
            }
        }

        private static BtContext Ctx(float dt = 0.1f)
        {
            return new BtContext(null, null, dt);
        }

        // ---- Sequence: three-state progression + interrupted resume -------

        [Test]
        public void Sequence_AllSuccess_TicksEveryChildInOrder()
        {
            var a = new StubNode(BTStatus.Success);
            var b = new StubNode(BTStatus.Success);

            BTStatus status = new Sequence(a, b).Tick(Ctx());

            Assert.AreEqual(BTStatus.Success, status);
            Assert.AreEqual(1, a.TickCount);
            Assert.AreEqual(1, b.TickCount);
        }

        [Test]
        public void Sequence_FirstFailure_StopsTickingLaterChildren()
        {
            var a = new StubNode(BTStatus.Failure);
            var b = new StubNode(BTStatus.Success);

            BTStatus status = new Sequence(a, b).Tick(Ctx());

            Assert.AreEqual(BTStatus.Failure, status);
            Assert.AreEqual(0, b.TickCount, "失败必须短路：后面的子节点本 tick 不许被 tick");
        }

        [Test]
        public void Sequence_Empty_SucceedsVacuously()
        {
            Assert.AreEqual(BTStatus.Success, new Sequence().Tick(Ctx()));
        }

        [Test]
        public void Sequence_RunningChild_ResumesFromIt_NotFromTheStart()
        {
            var a = new StubNode(BTStatus.Success);
            var b = new StubNode(BTStatus.Running, BTStatus.Success);
            var c = new StubNode(BTStatus.Success);
            var sequence = new Sequence(a, b, c);

            Assert.AreEqual(BTStatus.Running, sequence.Tick(Ctx()));
            Assert.AreEqual(1, a.TickCount);
            Assert.AreEqual(1, b.TickCount);
            Assert.AreEqual(0, c.TickCount);

            // 中断恢复：第二个 tick 从 Running 的子节点继续，a 不重头。
            Assert.AreEqual(BTStatus.Success, sequence.Tick(Ctx()));
            Assert.AreEqual(1, a.TickCount, "Running 后必须从该子继续，不许重头再 tick 前面的子节点");
            Assert.AreEqual(2, b.TickCount);
            Assert.AreEqual(1, c.TickCount);

            // 完成后内存清零：下一次进入从第一个子节点重新开始。
            Assert.AreEqual(BTStatus.Success, sequence.Tick(Ctx()));
            Assert.AreEqual(2, a.TickCount);
        }

        [Test]
        public void Sequence_ResetWhileRunning_NextTickStartsFromFirstChild()
        {
            var a = new StubNode(BTStatus.Success);
            var b = new StubNode(BTStatus.Running, BTStatus.Failure);
            var sequence = new Sequence(a, b);

            Assert.AreEqual(BTStatus.Running, sequence.Tick(Ctx()));
            Assert.IsTrue(b.LeftRunningMemory, "前置：b 正处于 Running");

            sequence.Reset(); // 树级 Reset（防重入）

            Assert.IsFalse(b.LeftRunningMemory, "Reset 必须清掉子节点的 Running 记忆");
            Assert.AreEqual(BTStatus.Running, sequence.Tick(Ctx()));
            Assert.AreEqual(2, a.TotalTicks, "Reset 后下一个 tick 必须从第一个子节点重新开始（a 被 re-tick），不许续跑陈旧分支");
            Assert.AreEqual(BTStatus.Failure, sequence.Tick(Ctx()), "b 的脚本从头播放而非从中断点继续");
            Assert.AreEqual(2, a.TotalTicks, "Reset 只生效一次：tick2 的 Running 重新建立 resume 记忆，tick3 从 b 续跑（正常语义）");
        }

        // ---- Selector: priority short circuit + resume ---------------------

        [Test]
        public void Selector_FirstSuccess_WinsAndSkipsLowerPriority()
        {
            var a = new StubNode(BTStatus.Failure);
            var b = new StubNode(BTStatus.Success);
            var c = new StubNode(BTStatus.Success);

            BTStatus status = new Selector(a, b, c).Tick(Ctx());

            Assert.AreEqual(BTStatus.Success, status);
            Assert.AreEqual(1, b.TickCount);
            Assert.AreEqual(0, c.TickCount, "前一个 Success 即返回：低优先级子节点不许被 tick");
        }

        [Test]
        public void Selector_AllFail_ReturnsFailureAndRestartsFromTop()
        {
            var a = new StubNode(BTStatus.Failure);
            var b = new StubNode(BTStatus.Failure);
            var selector = new Selector(a, b);

            Assert.AreEqual(BTStatus.Failure, selector.Tick(Ctx()));
            Assert.AreEqual(BTStatus.Failure, selector.Tick(Ctx()));
            Assert.AreEqual(2, a.TickCount, "全部失败后内存清零，每 tick 都从头评估最高优先级");
        }

        [Test]
        public void Selector_Empty_FailsVacuously()
        {
            Assert.AreEqual(BTStatus.Failure, new Selector().Tick(Ctx()));
        }

        [Test]
        public void Selector_RunningChild_ResumesFromIt_LowerPriorityUntouched()
        {
            var high = new StubNode(BTStatus.Failure);
            var mid = new StubNode(BTStatus.Running, BTStatus.Failure);
            var low = new StubNode(BTStatus.Success);
            var selector = new Selector(high, mid, low);

            Assert.AreEqual(BTStatus.Running, selector.Tick(Ctx()));
            Assert.AreEqual(0, low.TickCount);

            // 中断恢复：mid 继续并失败 → 同一 tick 落到 low（短路继续向下找）。
            Assert.AreEqual(BTStatus.Success, selector.Tick(Ctx()));
            Assert.AreEqual(1, high.TickCount, "resume 期间高优先级子节点不许被重 tick");
            Assert.AreEqual(2, mid.TickCount);
            Assert.AreEqual(1, low.TickCount);
        }

        [Test]
        public void Selector_ResetWhileRunning_ReevaluatesHighestPriority()
        {
            var high = new StubNode(BTStatus.Failure); // 永远失败
            var low = new StubNode(BTStatus.Running, BTStatus.Success);
            var selector = new Selector(high, low);

            Assert.AreEqual(BTStatus.Running, selector.Tick(Ctx()));
            selector.Reset();

            Assert.AreEqual(BTStatus.Running, selector.Tick(Ctx()));
            Assert.AreEqual(2, high.TotalTicks, "Reset 后下一个 tick 必须从最高优先级重新评估");
            Assert.AreEqual(2, low.TotalTicks, "低优先级 Running 分支被 Reset 后从自己的开头重放，而非续跑");

            Assert.AreEqual(BTStatus.Success, selector.Tick(Ctx()));
            Assert.AreEqual(2, high.TotalTicks, "Reset 只生效一次：tick2 的 Running 重新建立 resume 记忆，tick3 从 low 续跑并成功");
        }

        // ---- Parallel: both policies ---------------------------------------

        [Test]
        public void Parallel_RequireAll_AllSuccess_ReturnsSuccess()
        {
            var p = new Parallel(ParallelPolicy.RequireAll, new StubNode(BTStatus.Success), new StubNode(BTStatus.Success));
            Assert.AreEqual(BTStatus.Success, p.Tick(Ctx()));
        }

        [Test]
        public void Parallel_RequireAll_AnyRunning_ReturnsRunning()
        {
            var p = new Parallel(ParallelPolicy.RequireAll, new StubNode(BTStatus.Success), new StubNode(BTStatus.Running));
            Assert.AreEqual(BTStatus.Running, p.Tick(Ctx()));
        }

        [Test]
        public void Parallel_RequireAll_AnyFailure_AbortsAndResetsRunningChildren()
        {
            var failing = new StubNode(BTStatus.Failure);
            var running = new StubNode(BTStatus.Running);
            var p = new Parallel(ParallelPolicy.RequireAll, failing, running);

            Assert.AreEqual(BTStatus.Failure, p.Tick(Ctx()));
            Assert.AreEqual(0, running.TickCount, "AND 策略：任一失败立即中止（本 tick 它已被 tick 过又被 Reset 清零）");
            Assert.IsFalse(running.LeftRunningMemory, "中止时必须 Reset 一切还在 Running 的子节点（防重入）");
        }

        [Test]
        public void Parallel_RequireOne_AnySuccess_WinsImmediately()
        {
            var succeeding = new StubNode(BTStatus.Success);
            var running = new StubNode(BTStatus.Running);
            var p = new Parallel(ParallelPolicy.RequireOne, running, succeeding);

            Assert.AreEqual(BTStatus.Success, p.Tick(Ctx()));
            Assert.IsFalse(running.LeftRunningMemory, "OR 策略：任一成功立即完成并 Reset 其余子节点");
        }

        [Test]
        public void Parallel_RequireOne_AllFail_ReturnsFailure()
        {
            var p = new Parallel(ParallelPolicy.RequireOne, new StubNode(BTStatus.Failure), new StubNode(BTStatus.Failure));
            Assert.AreEqual(BTStatus.Failure, p.Tick(Ctx()));
        }

        [Test]
        public void Parallel_Completion_ResetsChildState_SoNextEntryStartsFresh()
        {
            var gated = new Cooldown(new StubNode(BTStatus.Success), 1f); // 完成后进入冷却窗口
            var p = new Parallel(ParallelPolicy.RequireAll, new StubNode(BTStatus.Success), gated);

            Assert.AreEqual(BTStatus.Success, p.Tick(Ctx()));
            Assert.IsTrue(gated.IsReady, "Parallel 完成时必须 Reset 全部子节点：冷却被重新武装，而不是残留半程窗口");
        }

        [Test]
        public void Parallel_Empty_RequireAll_Succeeds_RequireOne_Fails()
        {
            Assert.AreEqual(BTStatus.Success, new Parallel(ParallelPolicy.RequireAll).Tick(Ctx()));
            Assert.AreEqual(BTStatus.Failure, new Parallel(ParallelPolicy.RequireOne).Tick(Ctx()));
        }

        // ---- Decorators -----------------------------------------------------

        [Test]
        public void Invert_FlipsSuccessAndFailure_PassesRunningThrough()
        {
            var success = new Invert(new StubNode(BTStatus.Success));
            var failure = new Invert(new StubNode(BTStatus.Failure));
            var running = new Invert(new StubNode(BTStatus.Running));

            Assert.AreEqual(BTStatus.Failure, success.Tick(Ctx()));
            Assert.AreEqual(BTStatus.Success, failure.Tick(Ctx()));
            Assert.AreEqual(BTStatus.Running, running.Tick(Ctx()), "未完成的子树不许被翻转成完成");
        }

        [Test]
        public void Repeat_Bounded_ChildSucceedsExactlyNTimes_ThenRestarts()
        {
            var child = new StubNode(BTStatus.Success);
            var repeat = new Repeat(child, 3);

            Assert.AreEqual(BTStatus.Running, repeat.Tick(Ctx()));
            Assert.AreEqual(2, repeat.Remaining);
            Assert.AreEqual(BTStatus.Running, repeat.Tick(Ctx()));
            Assert.AreEqual(1, repeat.Remaining);
            Assert.AreEqual(BTStatus.Success, repeat.Tick(Ctx()), "第 n 次成功才完成");
            Assert.AreEqual(3, child.TickCount, "子节点恰好被 tick n 次");

            // 可复用：完成即重置计数，下一个周期重新数 3 次。
            Assert.AreEqual(BTStatus.Running, repeat.Tick(Ctx()));
            Assert.AreEqual(2, repeat.Remaining);
        }

        [Test]
        public void Repeat_Bounded_ChildFailure_PropagatesAndRestartsCounter()
        {
            var child = new StubNode(BTStatus.Failure, BTStatus.Success, BTStatus.Success);
            var repeat = new Repeat(child, 2);

            Assert.AreEqual(BTStatus.Failure, repeat.Tick(Ctx()));
            Assert.AreEqual(2, repeat.Remaining, "失败传播且计数复原");
            Assert.AreEqual(BTStatus.Running, repeat.Tick(Ctx()));
            Assert.AreEqual(BTStatus.Success, repeat.Tick(Ctx()));
        }

        [Test]
        public void Repeat_Forever_NeverCompletes_FailureAndRunningPassThrough()
        {
            var child = new StubNode(BTStatus.Success, BTStatus.Failure, BTStatus.Running);
            var repeat = Repeat.Forever(child);

            Assert.IsTrue(repeat.IsForever);
            Assert.AreEqual(BTStatus.Running, repeat.Tick(Ctx()), "成功只换来再来一圈，永不完成");
            Assert.AreEqual(BTStatus.Failure, repeat.Tick(Ctx()), "失败原样传播");
            Assert.AreEqual(BTStatus.Running, repeat.Tick(Ctx()), "子 Running 原样传播");
        }

        [Test]
        public void Cooldown_RunsChildOnce_ThenGatesUntilWindowElapses()
        {
            var child = new StubNode(BTStatus.Success);
            var cooldown = new Cooldown(child, 0.5f);

            Assert.IsTrue(cooldown.IsReady, "构造即就绪：第一次使用不应有人为延迟");
            Assert.AreEqual(BTStatus.Success, cooldown.Tick(Ctx(0.25f)));
            Assert.AreEqual(1, child.TickCount);

            Assert.AreEqual(BTStatus.Failure, cooldown.Tick(Ctx(0.25f)), "窗口内直接失败，不许 tick 子节点");
            Assert.AreEqual(1, child.TickCount);
            Assert.IsFalse(cooldown.IsReady);

            Assert.AreEqual(BTStatus.Failure, cooldown.Tick(Ctx(0.25f)), "跨过边界的这一 tick 仍按窗口内处理（入口检查先于累计）");
            Assert.AreEqual(1, child.TickCount);

            Assert.AreEqual(BTStatus.Success, cooldown.Tick(Ctx(0.25f)), "累计 0.5s ≥ 0.5s：窗口重开");
            Assert.AreEqual(2, child.TickCount);
        }

        [Test]
        public void Cooldown_RunningChild_KeepsWindowOpen()
        {
            var child = new StubNode(BTStatus.Running, BTStatus.Success);
            var cooldown = new Cooldown(child, 0.5f);

            Assert.AreEqual(BTStatus.Running, cooldown.Tick(Ctx(0.2f)));
            Assert.AreEqual(0.5f, cooldown.Elapsed, "子节点 Running 时窗口不重开");

            Assert.AreEqual(BTStatus.Success, cooldown.Tick(Ctx(0.2f)), "窗口未启动，子节点继续就绪");
            Assert.AreEqual(2, child.TickCount);
        }

        [Test]
        public void Succeeder_ConvertsFailureToSuccess_PassesRunningThrough()
        {
            var failure = new Succeeder(new StubNode(BTStatus.Failure));
            var success = new Succeeder(new StubNode(BTStatus.Success));
            var running = new Succeeder(new StubNode(BTStatus.Running));

            Assert.AreEqual(BTStatus.Success, failure.Tick(Ctx()));
            Assert.AreEqual(BTStatus.Success, success.Tick(Ctx()));
            Assert.AreEqual(BTStatus.Running, running.Tick(Ctx()));
        }

        [Test]
        public void Reset_ReentrancyGuard_RestoresDecoratorsAndCompositeMemory()
        {
            // 组合场景：Sequence 走到第二个子（Repeat 内部还剩 2 次）时整树 Reset。
            var gated = new Repeat(new StubNode(BTStatus.Success), 3);
            var sequence = new Sequence(new StubNode(BTStatus.Success), gated);

            Assert.AreEqual(BTStatus.Running, sequence.Tick(Ctx()));
            Assert.AreEqual(2, gated.Remaining);

            sequence.Reset();
            Assert.AreEqual(3, gated.Remaining, "Reset 必须把装饰器内部状态一并复原");

            Assert.AreEqual(BTStatus.Running, sequence.Tick(Ctx()));
            Assert.AreEqual(2, gated.Remaining, "下一个 tick 从头进入：第一个子先被 tick，Repeat 重新数第一圈");
        }

        // ---- 构造期委托缓存（课程 M14 GC 坑点） -----------------------------

        [Test]
        public void BtCondition_CachesPredicate_ReferenceStableAcrossTicks()
        {
            Func<BtContext, bool> predicate = ctx => true;
            var condition = new BtCondition(predicate);
            var runner = new BehaviorTreeRunner(condition);

            Func<BtContext, bool> before = condition.Predicate;
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(BTStatus.Success, runner.Tick(0.1f));
            }

            Assert.AreSame(predicate, condition.Predicate, "Tick 只许调用构造期缓存的委托，不许换新实例");
            Assert.AreSame(before, condition.Predicate);
        }

        [Test]
        public void BtAction_CachesDelegate_ReferenceStableAcrossTicks()
        {
            Func<BtContext, BTStatus> action = ctx => BTStatus.Running;
            var node = new BtAction(action);
            var runner = new BehaviorTreeRunner(node);

            runner.Tick(0.1f);
            runner.Tick(0.1f);

            Assert.AreSame(action, node.Action, "Action 委托必须构造期缓存、逐 tick 复用");
        }

        [Test]
        public void Runner_NonPositiveDt_IsIgnored()
        {
            var child = new StubNode(BTStatus.Success);
            var runner = new BehaviorTreeRunner(child);

            Assert.AreEqual(BTStatus.Failure, runner.Tick(0f), "尚未 tick 过且被忽略时返回 Failure 缺省");
            Assert.AreEqual(0, child.TickCount);
            Assert.IsNull(runner.LastStatus, "被忽略的 tick 不产生状态");

            Assert.AreEqual(BTStatus.Success, runner.Tick(0.1f));
            Assert.AreEqual(BTStatus.Success, runner.Tick(-1f), "负 dt 同样忽略，返回上一次状态");
            Assert.AreEqual(1, child.TickCount);
        }

        [Test]
        public void Runner_FiresBehaviorTreeRooted_WithRootStatus()
        {
            var statuses = new List<BTStatus>();
            var runner = new BehaviorTreeRunner(new StubNode(BTStatus.Success, BTStatus.Failure, BTStatus.Running));
            runner.BehaviorTreeRooted += statuses.Add;

            runner.Tick(0.1f);
            runner.Tick(0.1f);
            runner.Tick(0f); // 被忽略：不触发事件
            runner.Tick(0.1f);

            CollectionAssert.AreEqual(
                new[] { BTStatus.Success, BTStatus.Failure, BTStatus.Running },
                statuses,
                "每个实际执行的 tick 恰好触发一次事件，携带根状态");
        }

        [Test]
        public void Runner_ReusesSingleContext_ZeroAllocationContract()
        {
            var runner = new BehaviorTreeRunner(new StubNode(BTStatus.Success));

            runner.Tick(0.1f);
            BtContext first = runner.Context;
            runner.Tick(0.2f);

            Assert.AreSame(first, runner.Context, "runner 必须复用同一个 context（零分配契约）");
            Assert.AreEqual(0.2f, runner.Context.Dt, "context 的 Dt 必须就地更新为本次 tick 的 dt");
        }

        [Test]
        public void Runner_Reset_PropagatesToEveryNode()
        {
            var a = new StubNode(BTStatus.Success);
            var b = new StubNode(BTStatus.Running);
            var runner = new BehaviorTreeRunner(new Sequence(a, b));

            runner.Tick(0.1f);
            Assert.IsTrue(b.LeftRunningMemory);

            runner.Reset();
            Assert.IsFalse(b.LeftRunningMemory, "runner.Reset 必须传播到每个节点，清掉全部 Running 记忆");
            Assert.AreEqual(0, a.TickCount, "子节点状态被整体复原（计数归零）");
        }

        // ---- Blackboard: 类型安全 -------------------------------------------

        [Test]
        public void Blackboard_SetTryGet_Roundtrip()
        {
            var board = new Blackboard();
            board.Set("hp", 42);
            board.Set("name", "enemy-1");

            Assert.IsTrue(board.TryGet("hp", out int hp));
            Assert.AreEqual(42, hp);
            Assert.IsTrue(board.TryGet("name", out string name));
            Assert.AreEqual("enemy-1", name);
            Assert.AreEqual(2, board.Count);
        }

        [Test]
        public void Blackboard_TryGet_WrongType_FailsClosed()
        {
            var board = new Blackboard();
            board.Set("score", 7);

            Assert.IsFalse(board.TryGet("score", out float f), "整型存入不许按浮点读出（不做转换，错型即 false）");
            Assert.AreEqual(0f, f);
            Assert.IsFalse(board.TryGet("score", out string s));
            Assert.IsNull(s);
            Assert.IsTrue(board.Has("score"), "错型只是读不出，键仍在");
        }

        [Test]
        public void Blackboard_TryGet_MissingKey_ReturnsFalse()
        {
            var board = new Blackboard();
            Assert.IsFalse(board.TryGet("nope", out int value));
            Assert.AreEqual(0, value);
        }

        [Test]
        public void Blackboard_NullStoredValue_TryGetFailsButHasTrue()
        {
            var board = new Blackboard();
            board.Set<string>("ghost", null);

            Assert.IsTrue(board.Has("ghost"));
            Assert.IsFalse(board.TryGet("ghost", out string value));
            Assert.IsNull(value);
        }

        [Test]
        public void Blackboard_Set_Overwrites_AcrossTypes()
        {
            var board = new Blackboard();
            board.Set("slot", 1);
            board.Set("slot", "one");

            Assert.IsFalse(board.TryGet("slot", out int _));
            Assert.IsTrue(board.TryGet("slot", out string s));
            Assert.AreEqual("one", s);
            Assert.AreEqual(1, board.Count);
        }

        [Test]
        public void Blackboard_RemoveAndClear_Work()
        {
            var board = new Blackboard();
            board.Set("a", 1);
            board.Set("b", 2);

            Assert.IsTrue(board.Remove("a"));
            Assert.IsFalse(board.Remove("a"), "移除不存在的键返回 false");
            Assert.AreEqual(1, board.Count);

            board.Clear();
            Assert.AreEqual(0, board.Count);
            Assert.IsFalse(board.Has("b"));
        }

        [Test]
        public void Blackboard_NullKey_Throws()
        {
            var board = new Blackboard();
            Assert.Throws<ArgumentNullException>(() => board.Set<int>(null, 1));
            Assert.IsFalse(board.TryGet(null, out int _));
            Assert.IsFalse(board.Has(null));
            Assert.IsFalse(board.Remove(null));
        }

        // ---- 叶子守护（构造期 fail fast） -----------------------------------

        [Test]
        public void Nodes_FailFast_OnNullSetup()
        {
            Assert.Throws<ArgumentNullException>(() => new BtCondition(null));
            Assert.Throws<ArgumentNullException>(() => new BtAction(null));
            Assert.Throws<ArgumentNullException>(() => new Sequence((BtNode)null));
            Assert.Throws<ArgumentNullException>(() => new Selector((BtNode)null));
            Assert.Throws<ArgumentNullException>(() => new Invert(null));
            Assert.Throws<ArgumentNullException>(() => new Repeat(null, 1));
            Assert.Throws<ArgumentNullException>(() => new Cooldown(null, 1f));
            Assert.Throws<ArgumentNullException>(() => new Succeeder(null));
            Assert.Throws<ArgumentNullException>(() => new BehaviorTreeRunner(null));
            Assert.Throws<ArgumentException>(() => new Repeat(new StubNode(), 0), "Repeat 次数 0 无意义，必须构造期报错");
            Assert.Throws<ArgumentException>(() => new Cooldown(new StubNode(), -1f), "Cooldown 负窗口必须构造期报错");
            Assert.DoesNotThrow(() => new Cooldown(new StubNode(), 0f), "零秒冷却=始终就绪，合法");
        }
    }
}
