using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace ZFrame.View.Input.Tests
{
    /// <summary>
    /// EditMode tests of the on-screen touch controls (TASK-A-AND-04):
    /// the stick must publish non-zero <see cref="MoveInputEvent"/> values into
    /// the SAME command chain the keyboard uses, zero them on release, keep one
    /// pointer per widget (left thumb + right thumb), respect the
    /// <see cref="InputBlocker"/> gate, sit inside the safe area, and leave
    /// nothing behind on a desktop platform.
    ///
    /// UGUI pointer dispatch itself cannot be synthesized in batch mode
    /// (legacy Input has no scriptable device), so the tests drive the exact
    /// geometry step the handlers call — <see cref="VirtualJoystick"/>'s
    /// internal <c>ApplyLocalOffset</c>, reachable through the assembly's
    /// InternalsVisibleTo — plus the public pointer handlers for everything
    /// that is about pointer ownership.
    /// </summary>
    public sealed class TouchControlsTests
    {
        private ZFrameControls _controls;
        private PlayerInputGate _gate;
        private PlayerInputBridge _bridge;
        private UIRoot _root;
        private TouchControlsView _view;

        private readonly List<IDisposable> _tokens = new List<IDisposable>();
        private readonly List<MoveInputEvent> _moves = new List<MoveInputEvent>();
        private int _interactEvents;
        private int _pauseEvents;

        [SetUp]
        public void SetUp()
        {
            InputBlocker.Reset();
            _controls = ZFrameControls.FromResources();
            Assert.IsNotNull(_controls, "the committed ZFrameControls asset must resolve from Resources");
            _gate = new PlayerInputGate(_controls);
            _bridge = new PlayerInputBridge(_controls);
            _root = UIRoot.EnsureCreated();

            _moves.Clear();
            _interactEvents = 0;
            _pauseEvents = 0;
            _tokens.Add(EventBus.Subscribe<MoveInputEvent>(evt => _moves.Add(evt)));
            _tokens.Add(EventBus.Subscribe<InteractInputEvent>(_ => _interactEvents++));
            _tokens.Add(EventBus.Subscribe<PauseInputEvent>(_ => _pauseEvents++));

            TouchControlsView.ForceVisible = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_view != null)
            {
                _view.Shutdown();
                _view = null;
            }

            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();
            TouchControlsView.ForceVisible = false;

            _bridge?.Dispose();
            _bridge = null;
            _gate?.Dispose();
            _gate = null;
            _controls?.Dispose();
            _controls = null;

            InputBlocker.Reset();

            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root.gameObject);
                _root = null;
            }

            foreach (EventSystem eventSystem in UnityEngine.Object.FindObjectsOfType<EventSystem>())
            {
                UnityEngine.Object.DestroyImmediate(eventSystem.gameObject);
            }
        }

        [Test]
        public void Spawn_CreatesNothing_OnDesktopWithoutTheDebugOverride()
        {
            TouchControlsView.ForceVisible = false;

            TouchControlsView view = TouchControlsView.Spawn(_root, _bridge);

            Assert.IsNull(view, "非触摸平台必须不建控件");
            Assert.IsNull(UnityEngine.Object.FindObjectOfType<TouchControlsView>());
            Assert.AreEqual(0, _root.GetLayerCanvas(UIConsts.UILayer.HUD).transform.childCount, "桌面零残留：HUD canvas 不留空壳");
        }

        [Test]
        public void Spawn_BuildsOverlayOnHudLayer_InsideTheSafeAreaHost()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);

            Assert.IsNotNull(_view);
            Assert.AreSame(_root.GetLayerCanvas(UIConsts.UILayer.HUD).transform, _view.transform.parent);
            Assert.AreSame(_view.transform, _view.SafeArea.parent, "控件必须挂在安全区宿主下");
            Assert.AreSame(_view.SafeArea, _view.Stick.transform.parent);
            Assert.AreSame(_view.SafeArea, _view.InteractButton.transform.parent);
            Assert.AreSame(_view.SafeArea, _view.PauseButton.transform.parent);

            Vector2 min = _view.SafeArea.anchorMin;
            Vector2 max = _view.SafeArea.anchorMax;
            Assert.GreaterOrEqual(min.x, 0f);
            Assert.GreaterOrEqual(min.y, 0f);
            Assert.LessOrEqual(max.x, 1f);
            Assert.LessOrEqual(max.y, 1f);
            Assert.LessOrEqual(min.x, max.x);
            Assert.LessOrEqual(min.y, max.y);
        }

        [Test]
        public void Spawn_IsIdempotent_ForTheSameRoot()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);
            TouchControlsView second = TouchControlsView.Spawn(_root, _bridge);

            Assert.AreSame(_view, second);
            Assert.AreEqual(1, _root.GetLayerCanvas(UIConsts.UILayer.HUD).GetComponentsInChildren<TouchControlsView>(true).Length);
        }

        [Test]
        public void Stick_DragPublishesNonZeroMove_AndReleasePublishesZero()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);
            VirtualJoystick stick = _view.Stick;

            stick.OnPointerDown(Pointer(1));
            _moves.Clear();

            stick.ApplyLocalOffset(AtCentre(stick, 0f, 60f)); // push up = forward

            Assert.Greater(_bridge.LastMove.Y, 0.5f, "向上推必须产生 +Y 的移动量");
            Assert.AreEqual(0f, _bridge.LastMove.X, 1e-4);
            Assert.AreEqual(1, _moves.Count, "既有链路：EventBus 必须收到 MoveInputEvent");
            Assert.AreEqual(_bridge.LastMove.Y, _moves[0].Value.Y, 1e-5, "事件里的值与 bridge 暴露的值一致");

            _moves.Clear();
            stick.OnPointerUp(Pointer(1));

            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5, "松手必须归零");
            Assert.AreEqual(1, _moves.Count);
            Assert.IsFalse(stick.IsHeld);
        }

        [Test]
        public void Stick_PressOnTheVisualCentre_ReadsAsZero_NotAsACornerPush()
        {
            // Regression (found on the OXF-AN00): the touch rect's pivot is
            // bottom-left, so the raw local point of the VISUAL centre is
            // (w/2, h/2), not (0,0). Treating it as an offset made a finger
            // resting in the middle read as a full diagonal push.
            _view = TouchControlsView.Spawn(_root, _bridge);
            VirtualJoystick stick = _view.Stick;

            stick.OnPointerDown(Pointer(5));
            stick.ApplyLocalOffset(stick.Area.rect.center);

            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5, "按在圆心必须是零");

            stick.ApplyLocalOffset(AtCentre(stick, 0f, 60f));
            Assert.Greater(_bridge.LastMove.Y, 0.5f);
            Assert.AreEqual(0f, _bridge.LastMove.X, 1e-4, "正上方推杆只应有 +Y 分量");

            stick.OnPointerUp(Pointer(5));
        }

        [Test]
        public void Stick_ClampsToUnitLength_AndHonoursTheDeadZone()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);
            VirtualJoystick stick = _view.Stick;

            stick.OnPointerDown(Pointer(2));
            stick.ApplyLocalOffset(AtCentre(stick, 5000f, 5000f));

            Assert.AreEqual(1f, _bridge.LastMove.Magnitude, 0.001f, "超出半径必须夹到单位长度");

            stick.ApplyLocalOffset(AtCentre(stick, 1f, 1f));
            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5, "死区内必须读作零");

            stick.OnPointerUp(Pointer(2));
        }

        [Test]
        public void Stick_KeepsTheFirstPointer_AndIgnoresASecondFinger()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);
            VirtualJoystick stick = _view.Stick;

            stick.OnPointerDown(Pointer(11));
            stick.ApplyLocalOffset(AtCentre(stick, 0f, 60f));
            Vec2 held = _bridge.LastMove;
            Assert.Greater(held.Y, 0.5f);

            stick.OnPointerDown(Pointer(12)); // second finger lands on the stick zone

            Assert.AreEqual(11, stick.PointerId, "第二根手指不得抢占摇杆");
            Assert.AreEqual(held.Y, _bridge.LastMove.Y, 1e-5);

            stick.OnPointerUp(Pointer(12)); // the wrong finger lifts
            Assert.IsTrue(stick.IsHeld, "非持有者松手不得释放摇杆");
            Assert.AreEqual(held.Y, _bridge.LastMove.Y, 1e-5);

            stick.OnPointerUp(Pointer(11));
            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5);
        }

        [Test]
        public void MultiTouch_StickAndInteractButton_AreBothLive()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);
            VirtualJoystick stick = _view.Stick;

            // left thumb: hold the stick up
            stick.OnPointerDown(Pointer(21));
            stick.ApplyLocalOffset(AtCentre(stick, 0f, 60f));
            Assert.Greater(_bridge.LastMove.Y, 0.5f);

            // right thumb: press 互动 while the stick is still held
            _view.InteractButton.OnPointerDown(Pointer(22));

            Assert.AreEqual(1, _bridge.InteractCount, "摇杆按住时交互键必须生效");
            Assert.AreEqual(1, _interactEvents, "既有链路：InteractInputEvent 必须发出");
            Assert.IsTrue(stick.IsHeld);
            Assert.Greater(_bridge.LastMove.Y, 0.5f, "右手指按下不得打断左手方向");

            _view.InteractButton.OnPointerUp(Pointer(22));
            stick.OnPointerUp(Pointer(21));

            Assert.AreEqual(1, _bridge.InteractCount);
            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5);
        }

        [Test]
        public void PauseButton_PublishesTheExistingPauseCommand()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);

            _view.PauseButton.OnPointerDown(Pointer(31));
            _view.PauseButton.OnPointerDown(Pointer(32)); // second finger on the same button

            Assert.AreEqual(1, _bridge.PauseCount, "一次按下只发一次暂停");
            Assert.AreEqual(1, _pauseEvents);

            _view.PauseButton.OnPointerUp(Pointer(31));
        }

        [Test]
        public void VirtualInput_IsSwallowedWhileThePlayerMapIsBlocked_ButReleaseStillLands()
        {
            _bridge.ApplyVirtualMove(new Vec2(0f, 1f));
            Assert.AreEqual(1f, _bridge.LastMove.Y, 1e-4);

            using (InputBlocker.Acquire())
            {
                _bridge.ApplyVirtualMove(new Vec2(-1f, 0f));
                Assert.AreEqual(0f, _bridge.LastMove.X, 1e-4, "门禁关着时推杆不得穿透");
                Assert.AreEqual(1f, _bridge.LastMove.Y, 1e-4);

                _bridge.ApplyVirtualInteract();
                Assert.AreEqual(0, _bridge.InteractCount, "门禁关着时交互键不得穿透");

                _bridge.ApplyVirtualMove(Vec2.Zero);
                Assert.AreEqual(0f, _bridge.LastMove.Y, 1e-4, "松手的归零必须放行，否则解锁后角色会继续跑");
            }

            _bridge.ApplyVirtualMove(new Vec2(1f, 0f));
            Assert.AreEqual(1f, _bridge.LastMove.X, 1e-4, "解锁后恢复");
        }

        [Test]
        public void Shutdown_ReleasesTheStick_AndRemovesTheWholeOverlay()
        {
            _view = TouchControlsView.Spawn(_root, _bridge);
            _view.Stick.OnPointerDown(Pointer(41));
            _view.Stick.ApplyLocalOffset(AtCentre(_view.Stick, 0f, 60f));
            Assert.Greater(_bridge.LastMove.Y, 0.5f);

            _view.Shutdown();
            _view = null;

            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5, "拆控件必须先归零（场景切换时手指可能还按着）");
            Assert.IsNull(UnityEngine.Object.FindObjectOfType<TouchControlsView>());
            Assert.AreEqual(0, _root.GetLayerCanvas(UIConsts.UILayer.HUD).transform.childCount, "overlay 必须整体移除");
        }

        [Test]
        public void ApplySafeArea_MapsTheInsetOntoNormalizedAnchors_AndFallsBackWhenDegenerate()
        {
            var go = new GameObject("safe-probe", typeof(RectTransform));
            var rect = (RectTransform)go.transform;

            try
            {
                // 1080x2400 portrait with a 120px bottom gesture inset and 120px top notch.
                TouchControlsView.ApplySafeArea(rect, new Rect(0f, 120f, 1080f, 2160f), new Vector2(1080f, 2400f));

                Assert.AreEqual(0f, rect.anchorMin.x, 1e-5);
                Assert.AreEqual(0.05f, rect.anchorMin.y, 1e-5);
                Assert.AreEqual(1f, rect.anchorMax.x, 1e-5);
                Assert.AreEqual(0.95f, rect.anchorMax.y, 1e-5);
                Assert.AreEqual(Vector2.zero, rect.offsetMin);
                Assert.AreEqual(Vector2.zero, rect.offsetMax);

                TouchControlsView.ApplySafeArea(rect, new Rect(0f, 0f, 0f, 0f), Vector2.zero);

                Assert.AreEqual(Vector2.zero, rect.anchorMin, "退化输入必须回落到整屏，而不是塌成 0");
                Assert.AreEqual(Vector2.one, rect.anchorMax);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Point in the stick's pivot-relative local space: rect centre + offset.
        /// The pointer handlers receive this same space from
        /// RectTransformUtility.ScreenPointToLocalPointInRectangle.
        /// </summary>
        private static Vector2 AtCentre(VirtualJoystick stick, float dx = 0f, float dy = 0f)
        {
            Vector2 centre = stick.Area.rect.center;
            return new Vector2(centre.x + dx, centre.y + dy);
        }

        private static PointerEventData Pointer(int pointerId)
        {
            return new PointerEventData(EventSystem.current)
            {
                pointerId = pointerId,
                position = Vector2.zero
            };
        }
    }
}
