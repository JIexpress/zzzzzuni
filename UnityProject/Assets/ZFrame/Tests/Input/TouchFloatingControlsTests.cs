using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace ZFrame.View.Input.Tests
{
    /// <summary>
    /// EditMode tests of the TASK-A-AND-10 touch polish (floating origin /
    /// parameterisation / mode switch) layered ON TOP of the A-AND-04 widget
    /// tests (<see cref="TouchControlsTests"/>, untouched): the press seams are
    /// driven through <see cref="TouchControlsView.FloatPressAtLocal"/> and
    /// <see cref="VirtualJoystick.ApplyLocalOffset"/> — the same assembly-internal
    /// geometry seams A-AND-04 established, because batch-mode EditMode has no
    /// reliable screen space. The five falsifiable PLAYMODE cases (real screen
    /// space, real canvas scaling) live in TouchFloatingControlsPlayModeTests.
    /// </summary>
    public sealed class TouchFloatingControlsTests
    {
        private ZFrameControls _controls;
        private PlayerInputGate _gate;
        private PlayerInputBridge _bridge;
        private UIRoot _root;
        private TouchControlsView _view;

        private readonly List<IDisposable> _tokens = new List<IDisposable>();
        private readonly List<MoveInputEvent> _moves = new List<MoveInputEvent>();

        [SetUp]
        public void SetUp()
        {
            InputBlocker.Reset();
            TouchControlSettingOptions.ResetToDefaults();
            TouchHaptics.ResetCounters();

            _controls = ZFrameControls.FromResources();
            Assert.IsNotNull(_controls, "the committed ZFrameControls asset must resolve from Resources");
            _gate = new PlayerInputGate(_controls);
            _bridge = new PlayerInputBridge(_controls);
            _root = UIRoot.EnsureCreated();
            _moves.Clear();
            _tokens.Add(EventBus.Subscribe<MoveInputEvent>(evt => _moves.Add(evt)));

            TouchControlsView.ForceVisible = true;
            _view = TouchControlsView.Spawn(_root, _bridge);
            Assert.IsNotNull(_view, "precondition: ForceVisible must spawn the overlay");
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
            TouchControlSettingOptions.ResetToDefaults();

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

        // ---------- floating defaults at spawn ----------

        [Test]
        public void Spawn_DefaultsToFloating_BaseHidden_ZoneActive()
        {
            Assert.IsFalse(TouchControlSettingOptions.FixedStick, "默认浮动（设计要点 1）");

            FloatingStickZone zone = _view.FloatZone;
            Assert.IsNotNull(zone, "浮动模式必须建 FloatZone");
            Assert.IsTrue(zone.gameObject.activeSelf, "浮动模式下命中区必须激活");

            Image baseImage = _view.StickBase.GetComponent<Image>();
            Assert.IsFalse(baseImage.enabled, "浮动模式空闲时底座必须隐藏（按下生成）");
            Assert.IsFalse(baseImage.raycastTarget, "浮动模式底座纯视觉，命中交给 FloatZone");
            Assert.IsFalse(_view.Stick.IsHeld);
        }

        [Test]
        public void FloatZone_CoversTheLowerHalf_OfTheSafeArea()
        {
            RectTransform zone = _view.FloatZone.GetComponent<RectTransform>();
            Assert.AreEqual(0f, zone.anchorMin.x, 1e-5);
            Assert.AreEqual(0f, zone.anchorMin.y, 1e-5);
            Assert.AreEqual(1f, zone.anchorMax.x, 1e-5);
            Assert.AreEqual(0.5f, zone.anchorMax.y, 1e-5, "浮动命中区必须覆盖安全区下半屏（设计要点 1）");

            Assert.Less(_view.FloatZone.transform.GetSiblingIndex(), _view.InteractButton.transform.GetSiblingIndex(),
                "FloatZone 必须先建（后建的同层兄弟赢得 raycast——按钮永远优先）");
        }

        // ---------- floating press / anchor / release ----------

        [Test]
        public void FloatPressAtLocal_AnchorsTheBase_HoldsTheStick_AtZero()
        {
            Vector2 press = new Vector2(80f, 120f);
            _view.FloatPressAtLocal(11, press);

            Assert.IsTrue(_view.Stick.IsHeld);
            Assert.AreEqual(11, _view.Stick.PointerId);
            Assert.AreEqual(0f, _view.Stick.Value.Magnitude, 1e-5, "浮动按压点 == 底座圆心，必须读作零");

            Image baseImage = _view.StickBase.GetComponent<Image>();
            Assert.IsTrue(baseImage.enabled, "按下必须显示底座");
            Assert.IsFalse(baseImage.raycastTarget, "浮动模式底座不参与 raycast");

            // 锚点断言：底座圆心必须落在按压点（安全区局部坐标，夹取后一致）。
            Vector2 anchor = _view.StickBase.anchoredPosition;
            Rect safe = _view.SafeArea.rect;
            float half = 150f; // StickDiameter/2，拖拽半径倍率默认 1
            Vector2 expected = new Vector2(
                Mathf.Clamp(press.x, safe.xMin + half, Mathf.Max(safe.xMin + half, safe.xMax - half)) - half - safe.xMin,
                Mathf.Clamp(press.y, safe.yMin + half, Mathf.Max(safe.yMin + half, safe.yMax - half)) - half - safe.yMin);
            Assert.AreEqual(expected.x, anchor.x, 1e-3, "锚点必须跟随按压点（局部坐标）");
            Assert.AreEqual(expected.y, anchor.y, 1e-3);

            _view.FloatRelease(11);
            Assert.IsFalse(_view.Stick.IsHeld);
            Assert.IsFalse(baseImage.enabled, "浮动模式松开必须消失（设计要点 1）");
            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5, "松手必须归零");
        }

        [Test]
        public void FloatPressAtLocal_AnchorFollowsDifferentPressPoints()
        {
            Assume.That(_view.SafeArea.rect.width > 400f,
                "EditMode 屏幕过小（游戏视图未初始化）时锚点跟随断言交给 PlayMode 用例①");

            _view.FloatPressAtLocal(21, new Vector2(120f, 200f));
            Vector2 anchorA = _view.StickBase.anchoredPosition;
            _view.FloatRelease(21);

            _view.FloatPressAtLocal(21, new Vector2(-90f, -60f));
            Vector2 anchorB = _view.StickBase.anchoredPosition;
            _view.FloatRelease(21);

            Vector2 delta = anchorB - anchorA;
            Assert.Less(delta.x, 0f, "按压点向左下移动，锚点必须跟随向左下");
            Assert.Less(delta.y, 0f);
            Assert.Greater(delta.magnitude, 1f, "不同按压点必须产生不同的锚点（跟随）");
        }

        [Test]
        public void FloatPressAtLocal_SecondFingerIsIgnored()
        {
            _view.FloatPressAtLocal(31, new Vector2(60f, 90f));
            Vector2 anchorA = _view.StickBase.anchoredPosition;

            _view.FloatPressAtLocal(32, new Vector2(-100f, -80f)); // 第二根手指落在浮动区

            Assert.AreEqual(31, _view.Stick.PointerId, "第二根手指不得抢占摇杆（A-AND-04 语义保持）");
            Assert.AreEqual(anchorA, _view.StickBase.anchoredPosition, "第二指不得移动锚点");
            Assert.IsTrue(_view.Stick.IsHeld);

            _view.FloatRelease(32);
            Assert.IsTrue(_view.Stick.IsHeld, "非持有者松手不得释放摇杆");

            _view.FloatRelease(31);
            Assert.IsFalse(_view.Stick.IsHeld);
        }

        [Test]
        public void FloatDrag_AfterFloatingPress_PublishesThroughTheExistingChain()
        {
            _view.FloatPressAtLocal(41, new Vector2(70f, 110f));
            _moves.Clear();

            Vector2 centre = _view.Stick.Area.rect.center; // 按压点 == 圆心
            _view.Stick.ApplyLocalOffset(new Vector2(centre.x, centre.y + 60f)); // 向上推

            Assert.Greater(_bridge.LastMove.Y, 0.5f, "浮动按压后的拖动必须走同一条移动链");
            Assert.AreEqual(1, _moves.Count);
            Assert.AreEqual(_bridge.LastMove.Y, _moves[0].Value.Y, 1e-5);

            _view.FloatRelease(41);
            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5);
        }

        [Test]
        public void FloatRelease_WrongPointerDoesNotRelease()
        {
            _view.FloatPressAtLocal(51, new Vector2(50f, 70f));

            _view.FloatRelease(52);
            Assert.IsTrue(_view.Stick.IsHeld, "非持有者的松手必须被忽略");
            Assert.IsTrue(_view.StickBase.GetComponent<Image>().enabled, "底座必须保持显示");

            _view.FloatRelease(51);
            Assert.IsFalse(_view.Stick.IsHeld);
            Assert.IsFalse(_view.StickBase.GetComponent<Image>().enabled);
        }

        // ---------- fixed-stick mode (A-AND-04 semantics kept) ----------

        [Test]
        public void FixedStickSwitch_RestoresTheAAnd04Presentation()
        {
            TouchControlSettingOptions.ApplyFixedStick(true);

            Assert.IsFalse(_view.FloatZone.gameObject.activeSelf, "固定模式没有浮动命中区");
            Image baseImage = _view.StickBase.GetComponent<Image>();
            Assert.IsTrue(baseImage.enabled, "固定模式底座常显（A-AND-04）");
            Assert.IsTrue(baseImage.raycastTarget, "固定模式底座本身是命中目标（A-AND-04 语义保持）");
            Assert.AreEqual(new Vector2(36f, 36f), _view.StickBase.anchoredPosition, "固定模式回到 A-AND-04 锚点");

            // 固定摇杆照旧工作：直接指针路径（A-AND-04 的交互方式）。
            _view.Stick.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 61 });
            Vector2 centre = _view.Stick.Area.rect.center;
            _view.Stick.ApplyLocalOffset(new Vector2(centre.x, centre.y + 60f));
            Assert.Greater(_bridge.LastMove.Y, 0.5f, "固定模式摇杆照旧产生移动");
            _view.Stick.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 61 });

            Assert.IsTrue(baseImage.enabled, "固定模式松手后底座必须仍在（不消失）");

            TouchControlSettingOptions.ApplyFixedStick(false);
            Assert.IsTrue(_view.FloatZone.gameObject.activeSelf);
            Assert.IsFalse(baseImage.enabled, "切回浮动重新隐藏底座");
            Assert.IsFalse(baseImage.raycastTarget);
        }

        // ---------- tuning (死区 / 灵敏度 / 拖拽半径) ----------

        [Test]
        public void Tuning_DeadZoneMultiplier_ScalesTheThreshold()
        {
            VirtualJoystick stick = _view.Stick;

            // 默认（倍率 1）：20px 的偏移（幅值 0.227）越过死区 0.15，产生非零输出。
            stick.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 71 });
            stick.ApplyLocalOffset(AtCentre(stick, 20f, 0f));
            float defaultOutput = _bridge.LastMove.X;
            Assert.Greater(defaultOutput, 0.05f, "默认死区下 20px 必须产生非零输入");
            stick.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 71 });

            // 死区半径倍率 2.0：有效死区 0.3，同一位移落回死区内 → 零。
            stick.SetTuning(2f, 1f);
            stick.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 71 });
            stick.ApplyLocalOffset(AtCentre(stick, 20f, 0f));
            Assert.AreEqual(0f, _bridge.LastMove.Magnitude, 1e-5, "死区倍率放大后微动必须读作零（可证伪用例②的几何步）");

            // 幅值 0.4545 的位移在 0.3 死区之上，方向保持。
            stick.ApplyLocalOffset(AtCentre(stick, 40f, 0f));
            Assert.Greater(_bridge.LastMove.X, 0.05f);
            stick.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 71 });

            stick.SetTuning(1f, 1f); // 复原
            stick.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 71 });
            stick.ApplyLocalOffset(AtCentre(stick, 20f, 0f));
            Assert.AreEqual(defaultOutput, _bridge.LastMove.X, 1e-4, "复原后输出必须与默认一致");
            stick.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 71 });
        }

        [Test]
        public void Tuning_SensitivityMultiplier_DoublesOutput_AndCapsAtUnit()
        {
            VirtualJoystick stick = _view.Stick;

            stick.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 72 });
            stick.ApplyLocalOffset(AtCentre(stick, 40f, 0f));
            float baseline = _bridge.LastMove.X;
            Assert.Greater(baseline, 0.1f);
            Assert.Less(baseline, 0.5f, "基线取未饱和区间，翻倍后不得触顶");
            stick.ApplyLocalOffset(AtCentre(stick, 5000f, 0f));
            Assert.AreEqual(1f, _bridge.LastMove.Magnitude, 1e-4, "默认灵敏度满推夹到单位长度");
            stick.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 72 });

            stick.SetTuning(1f, 2f);
            stick.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 72 });
            stick.ApplyLocalOffset(AtCentre(stick, 40f, 0f));
            Assert.AreEqual(baseline * 2f, _bridge.LastMove.X, 1e-3, "灵敏度 2.0 下同位移输出必须翻倍（可证伪用例④的几何步）");
            stick.ApplyLocalOffset(AtCentre(stick, 5000f, 0f));
            Assert.AreEqual(1f, _bridge.LastMove.Magnitude, 1e-4, "灵敏度放大后满推仍必须夹到单位长度");
            stick.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 72 });
        }

        [Test]
        public void DragRadiusOption_ResizesTheBaseLive_ThroughTheChangedEvent()
        {
            Assert.AreEqual(new Vector2(300f, 300f), _view.StickBase.sizeDelta, "默认拖拽半径倍率 = A-AND-04 几何");

            TouchControlSettingOptions.ApplyDragRadius(2f);
            Assert.AreEqual(new Vector2(600f, 600f), _view.StickBase.sizeDelta,
                "拖拽半径倍率必须经 Changed 事件即时缩放底座（视觉与拖拽上限同一圆）");

            TouchControlSettingOptions.ApplyDragRadius(0.5f);
            Assert.AreEqual(new Vector2(150f, 150f), _view.StickBase.sizeDelta);

            TouchControlSettingOptions.ApplyDragRadius(1f);
            Assert.AreEqual(new Vector2(300f, 300f), _view.StickBase.sizeDelta);
        }

        // ---------- helpers ----------

        private static Vector2 AtCentre(VirtualJoystick stick, float dx = 0f, float dy = 0f)
        {
            Vector2 centre = stick.Area.rect.center;
            return new Vector2(centre.x + dx, centre.y + dy);
        }
    }
}
