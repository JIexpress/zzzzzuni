using System;
using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.View.Input.Tests
{
    /// <summary>
    /// EditMode tests of the InputBlocker → Player action map bridge
    /// (TASK-A-M3-03): <see cref="PlayerInputGate"/> must disable the Player
    /// map while <see cref="InputBlocker"/> holds a blocking span, keep the UI
    /// map open, and restore the Player map when the counter returns to zero.
    /// The assertions read the real Input System <c>InputActionMap.enabled</c>
    /// flag — no play mode needed.
    /// </summary>
    public sealed class PlayerInputGateTests
    {
        private ZFrameControls _controls;
        private PlayerInputGate _gate;

        [SetUp]
        public void SetUp()
        {
            InputBlocker.Reset();
            _controls = ZFrameControls.FromResources();
            Assert.IsNotNull(_controls);
        }

        [TearDown]
        public void TearDown()
        {
            _gate?.Dispose();
            _gate = null;
            _controls?.Dispose();
            _controls = null;
            InputBlocker.Reset(); // never leak a block into other suites
        }

        [Test]
        public void Gate_EnablesPlayerAndUi_WhenNothingIsBlocked()
        {
            _gate = new PlayerInputGate(_controls);

            Assert.IsTrue(_gate.IsPlayerEnabled, "空闲态：Player map 必须启用");
            Assert.IsTrue(_gate.IsUiEnabled, "UI map 常开");
            Assert.IsTrue(_controls.Player.enabled);
            Assert.IsTrue(_controls.UI.enabled);
        }

        [Test]
        public void Acquire_DisablesPlayerMap_ReleaseRestoresIt()
        {
            _gate = new PlayerInputGate(_controls);

            IDisposable block = InputBlocker.Acquire();

            Assert.IsTrue(InputBlocker.Active);
            Assert.IsFalse(_controls.Player.enabled, "InputBlocker.Active 时 Player map 必须 Disable");
            Assert.IsTrue(_controls.UI.enabled, "UI map 不受玩法冻结影响");
            Assert.IsFalse(_gate.IsPlayerEnabled);

            block.Dispose();

            Assert.IsFalse(InputBlocker.Active);
            Assert.IsTrue(_controls.Player.enabled, "归零后 Player map 必须恢复 Enable");
            Assert.IsTrue(_gate.IsPlayerEnabled);
        }

        [Test]
        public void NestedAcquires_KeepPlayerDisabled_UntilTheLastRelease()
        {
            _gate = new PlayerInputGate(_controls);

            IDisposable first = InputBlocker.Acquire();
            IDisposable second = InputBlocker.Acquire();
            Assert.AreEqual(2, InputBlocker.Count);

            first.Dispose();
            Assert.IsTrue(InputBlocker.Active, "还有一层未释放");
            Assert.IsFalse(_controls.Player.enabled, "嵌套计数未归零，Player 仍禁用");

            second.Dispose();
            Assert.IsTrue(_controls.Player.enabled);
        }

        [Test]
        public void Gate_AppliesTheCurrentStateOnConstruction()
        {
            using (InputBlocker.Acquire())
            {
                _gate = new PlayerInputGate(_controls);

                Assert.IsFalse(_controls.Player.enabled, "构造时就必须应用当前阻塞态（不是等下一次变更）");
                Assert.IsTrue(_controls.UI.enabled);
            }

            Assert.IsTrue(_controls.Player.enabled, "释放后由 Changed 事件恢复");
        }

        [Test]
        public void Sync_IsIdempotent_AndPollable()
        {
            _gate = new PlayerInputGate(_controls);

            IDisposable block = InputBlocker.Acquire();
            block.Dispose();
            _gate.Sync();
            _gate.Sync();

            Assert.IsTrue(_controls.Player.enabled);
            Assert.IsTrue(_controls.UI.enabled);
        }

        [Test]
        public void Dispose_UnsubscribesFromTheStaticCounter()
        {
            _gate = new PlayerInputGate(_controls);
            _gate.Dispose();
            _gate = null;

            using (InputBlocker.Acquire())
            {
                Assert.IsTrue(InputBlocker.Active);
                Assert.IsFalse(_controls.Player.enabled, "Dispose 后 map 保持关闭（资产已 Disable）");
            }

            // The disposed gate must not have thrown from the Changed event, and
            // nothing re-enabled the map behind our back.
            Assert.IsFalse(_controls.Player.enabled);
            Assert.DoesNotThrow(() => InputBlocker.Acquire().Dispose());
        }

        [Test]
        public void Gate_RejectsNullControls()
        {
            Assert.Throws<ArgumentNullException>(() => new PlayerInputGate(null));
        }
    }
}
