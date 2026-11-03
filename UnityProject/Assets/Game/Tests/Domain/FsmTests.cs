using System;
using System.Collections.Generic;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Full-order tests of the framework FSM (TASK-A-M4-01): Enter/Exit
    /// pairing and ordering, illegal-transition rejection, veto, re-entrancy
    /// and the tick contract.
    /// </summary>
    public sealed class FsmTests
    {
        private sealed class Probe
        {
            public readonly List<string> Log = new List<string>();
            public Fsm<Probe> Fsm;
            public bool NestedResult;
        }

        private class RecordingState : IState<Probe>
        {
            private readonly string _name;

            public RecordingState(string name)
            {
                _name = name;
            }

            public bool Veto { get; set; }

            public string Name => _name;

            public virtual void Enter(Probe owner)
            {
                owner.Log.Add(_name + ".Enter");
            }

            public virtual void Exit(Probe owner)
            {
                owner.Log.Add(_name + ".Exit");
            }

            public virtual void Update(Probe owner, float deltaTime)
            {
                owner.Log.Add(_name + ".Update");
            }

            public virtual bool CanTransition(Probe owner, IState<Probe> next)
            {
                return !Veto;
            }
        }

        private sealed class StateA : RecordingState
        {
            public StateA()
                : base("A")
            {
            }
        }

        private sealed class StateB : RecordingState
        {
            public StateB()
                : base("B")
            {
            }
        }

        private sealed class StateC : RecordingState
        {
            public StateC()
                : base("C")
            {
            }
        }

        private sealed class NestedState : RecordingState
        {
            public NestedState()
                : base("Nested")
            {
            }

            public override void Enter(Probe owner)
            {
                base.Enter(owner);
                owner.NestedResult = owner.Fsm.ChangeState<StateB>();
            }
        }

        private sealed class ThrowingEnterState : RecordingState
        {
            public ThrowingEnterState()
                : base("Boom")
            {
            }

            public override void Enter(Probe owner)
            {
                base.Enter(owner);
                throw new InvalidOperationException("enter failed");
            }
        }

        private Probe _probe;
        private StateA _a;
        private StateB _b;
        private StateC _c;
        private Fsm<Probe> _fsm;

        [SetUp]
        public void SetUp()
        {
            _probe = new Probe();
            _a = new StateA();
            _b = new StateB();
            _c = new StateC();

            _fsm = new Fsm<Probe>(_probe);
            _fsm.AddState(_a);
            _fsm.AddState(_b);
            _fsm.AddState(_c);
            _fsm.AddTransition<StateA, StateB>();
            _fsm.AddTransition<StateB, StateA>();
            _fsm.AddTransition<StateB, StateC>();
            _probe.Fsm = _fsm;
        }

        [Test]
        public void Start_EntersInitialStateOnce_AndAnnouncesIt()
        {
            IState<Probe> previous = _a;
            IState<Probe> current = null;
            _fsm.StateChanged += (from, to) =>
            {
                previous = from;
                current = to;
            };

            Assert.IsTrue(_fsm.Start<StateA>(), "首次 Start 必须成功");
            Assert.AreSame(_a, _fsm.Current);
            Assert.IsTrue(_fsm.IsRunning);
            CollectionAssert.AreEqual(new[] { "A.Enter" }, _probe.Log, "Start 只跑 Enter，不跑 Exit");
            Assert.IsNull(previous, "初始 Enter 的 previous 必须为 null");
            Assert.AreSame(_a, current);
        }

        [Test]
        public void ChangeState_RunsExitOfOldStateBeforeEnterOfNew()
        {
            _fsm.Start<StateA>();
            _probe.Log.Clear();

            Assert.IsTrue(_fsm.ChangeState<StateB>());
            CollectionAssert.AreEqual(new[] { "A.Exit", "B.Enter" }, _probe.Log, "全序必须是 Exit → Enter");
            Assert.AreSame(_b, _fsm.Current);
        }

        [Test]
        public void ChangeState_StateChangedFiresAfterBothCallbacks()
        {
            _fsm.Start<StateA>();
            _probe.Log.Clear();

            _fsm.StateChanged += (from, to) =>
            {
                _probe.Log.Add("StateChanged:" + (from == null ? "null" : ((RecordingState)from).Name) + "->" + ((RecordingState)to).Name);
            };

            _fsm.ChangeState<StateB>();
            CollectionAssert.AreEqual(new[] { "A.Exit", "B.Enter", "StateChanged:A->B" }, _probe.Log);
        }

        [Test]
        public void ChangeState_UnlistedPair_IsRejectedWithoutCallbacks()
        {
            _fsm.Start<StateA>();
            _probe.Log.Clear();

            IState<Probe> rejectedFrom = null;
            IState<Probe> rejectedTo = null;
            _fsm.TransitionRejected += (from, to) =>
            {
                rejectedFrom = from;
                rejectedTo = to;
            };

            Assert.IsFalse(_fsm.ChangeState<StateC>(), "A → C 未登记，必须拒绝");
            CollectionAssert.IsEmpty(_probe.Log, "拒绝的转换不得调用 Exit/Enter");
            Assert.AreSame(_a, _fsm.Current);
            Assert.AreSame(_a, rejectedFrom);
            Assert.AreSame(_c, rejectedTo);
        }

        [Test]
        public void ChangeState_ToSelf_IsRejected()
        {
            _fsm.Start<StateA>();
            _probe.Log.Clear();

            Assert.IsFalse(_fsm.ChangeState<StateA>(), "同状态重入不是转换");
            CollectionAssert.IsEmpty(_probe.Log);
        }

        [Test]
        public void ChangeState_ToUnregisteredState_IsRejected()
        {
            _fsm.Start<StateA>();
            _probe.Log.Clear();

            var stranger = new RecordingState("Stranger");
            Assert.IsFalse(_fsm.ChangeState(stranger), "未注册实例必须拒绝");
            CollectionAssert.IsEmpty(_probe.Log);
            Assert.AreSame(_a, _fsm.Current);
        }

        [Test]
        public void ChangeState_VetoedByCurrentState_IsRejected()
        {
            _fsm.Start<StateA>();
            _probe.Log.Clear();
            _a.Veto = true;

            Assert.IsFalse(_fsm.ChangeState<StateB>(), "当前状态否决必须拒绝");
            CollectionAssert.IsEmpty(_probe.Log, "否决发生在 Exit 之前");
            Assert.AreSame(_a, _fsm.Current);
        }

        [Test]
        public void ChangeState_FromInsideEnter_IsRejected()
        {
            var nested = new NestedState();
            var fsm = new Fsm<Probe>(_probe);
            fsm.AddState(nested);
            fsm.AddState(_b);
            fsm.AddTransition<NestedState, StateB>();
            _probe.Fsm = fsm;

            Assert.IsTrue(fsm.Start<NestedState>());
            Assert.IsFalse(_probe.NestedResult, "Enter 内嵌 ChangeState 必须被拒绝（防止重入破坏 Exit/Enter 配对）");
            Assert.AreSame(nested, fsm.Current);
            CollectionAssert.AreEqual(new[] { "Nested.Enter" }, _probe.Log);
        }

        [Test]
        public void ChangeState_WhenNextEnterThrows_StillRanExitFirst()
        {
            var boom = new ThrowingEnterState();
            var fsm = new Fsm<Probe>(_probe);
            fsm.AddState(_a);
            fsm.AddState(boom);
            fsm.AddTransition<StateA, ThrowingEnterState>();
            fsm.Start<StateA>();
            _probe.Log.Clear();

            Assert.Throws<InvalidOperationException>(() => fsm.ChangeState(boom));
            CollectionAssert.AreEqual(new[] { "A.Exit", "Boom.Enter" }, _probe.Log, "Exit 先于 Enter，Enter 抛异常也不吞 Exit");
        }

        [Test]
        public void Start_Twice_ReturnsFalseAndDoesNotReenter()
        {
            Assert.IsTrue(_fsm.Start<StateA>());
            _probe.Log.Clear();

            Assert.IsFalse(_fsm.Start<StateB>(), "已启动的 FSM 不允许再次 Start");
            CollectionAssert.IsEmpty(_probe.Log);
            Assert.AreSame(_a, _fsm.Current);
        }

        [Test]
        public void Start_UnknownState_ReturnsFalseAndStaysStopped()
        {
            var fsm = new Fsm<Probe>(_probe);

            Assert.IsFalse(fsm.Start<StateA>());
            Assert.IsFalse(fsm.IsRunning);
            Assert.IsNull(fsm.Current);
        }

        [Test]
        public void Tick_OnlyPumpsCurrentState()
        {
            _fsm.Start<StateA>();
            _fsm.ChangeState<StateB>();
            _probe.Log.Clear();

            _fsm.Tick(0.016f);
            CollectionAssert.AreEqual(new[] { "B.Update" }, _probe.Log, "只有当前状态收到 Tick");
        }

        [Test]
        public void Tick_BeforeStart_IsNoOp()
        {
            Assert.DoesNotThrow(() => _fsm.Tick(1f));
            CollectionAssert.IsEmpty(_probe.Log);
        }

        [Test]
        public void AddState_DuplicateType_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => _fsm.AddState(new StateA()));
        }

        [Test]
        public void AddState_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _fsm.AddState(null));
        }

        [Test]
        public void AddTransition_UnregisteredState_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => _fsm.AddTransition(typeof(StateA), typeof(NestedState)));
        }

        [Test]
        public void AddTransition_IsIdempotent()
        {
            int before = _fsm.TransitionCount;
            _fsm.AddTransition<StateA, StateB>();

            Assert.AreEqual(before, _fsm.TransitionCount, "重复登记同一对不得增加计数");
        }

        [Test]
        public void Counts_ReflectRegistrations()
        {
            Assert.AreEqual(3, _fsm.StateCount);
            Assert.AreEqual(3, _fsm.TransitionCount);
        }

        [Test]
        public void CanTransition_HasNoSideEffects()
        {
            _fsm.Start<StateA>();
            _probe.Log.Clear();

            Assert.IsTrue(_fsm.CanTransition(_b));
            Assert.IsFalse(_fsm.CanTransition(_c));
            Assert.IsFalse(_fsm.CanTransition(_a));
            Assert.IsFalse(_fsm.CanTransition(null));
            CollectionAssert.IsEmpty(_probe.Log, "CanTransition 不得调用任何回调");
        }

        [Test]
        public void AllowUnlistedTransitions_StillHonoursVeto()
        {
            var permissive = new Fsm<Probe>(_probe, allowUnlistedTransitions: true);
            permissive.AddState(_a);
            permissive.AddState(_c);
            permissive.Start<StateA>();

            Assert.IsTrue(permissive.ChangeState<StateC>(), "自由模式允许未登记的转换");
            Assert.AreSame(_c, permissive.Current);
        }
    }
}
