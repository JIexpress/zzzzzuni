using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// The five falsifiable PlayMode cases of TASK-A-AND-10 (触屏打磨包), driven
    /// through the production seams with REAL screen space and REAL canvas
    /// scaling (the EditMode twins use the internal local-space seams because
    /// batch mode has no reliable screen there):
    /// ① 浮动原点 — different press points follow the anchor (exact linearity
    ///    against the canvas scale factor, non-circular) and the base disappears
    ///    on release;
    /// ② 死区 — a micro drag inside the dead zone produces ZERO movement input,
    ///    a macro drag beyond it produces a full one;
    /// ③ 多点 — left thumb floats the stick while the right thumb presses 互动,
    ///    both effects land in the gameplay command chain simultaneously;
    /// ④ 参数化 — with 灵敏度 = 2.0 the SAME drag displacement doubles the
    ///    movement vector (uncapped band, measured against the sens=1 baseline);
    /// ⑤ 触觉 — with 震动反馈 off, the 互动 press makes ZERO haptic pulse calls
    ///    (mock counting via the <see cref="TouchHaptics.Pulse"/> seam) while the
    ///    command chain still fires.
    /// </summary>
    public sealed class TouchFloatingControlsPlayModeTests
    {
        private const float BudgetSeconds = 30f;

        private UIRoot _root;
        private TouchControlsView _view;
        private readonly List<IDisposable> _tokens = new List<IDisposable>();
        private int _interactEvents;
        private int _pulseCount;
        private Action _originalPulse;

        [SetUp]
        public void SetUp()
        {
            TouchControlsView.ForceVisible = true;
            TouchControlSettingOptions.ResetToDefaults();
            TouchHaptics.ResetCounters();
            _originalPulse = TouchHaptics.Pulse;
            _pulseCount = 0;
            TouchHaptics.Pulse = () => _pulseCount++; // 用例⑤ mock 计数接缝
            _interactEvents = 0;
            _tokens.Add(EventBus.Subscribe<InteractInputEvent>(_ => _interactEvents++));

            _root = UIRoot.EnsureCreated();
            Assert.IsNotNull(_root, "precondition: UIRoot.EnsureCreated must build the root");
            Assert.IsNotNull(InputRuntime.Bridge, "precondition: the input stack must be booted (InputRuntime.AutoBoot)");

            _view = TouchControlsView.Spawn(_root, InputRuntime.Bridge);
            Assert.IsNotNull(_view, "precondition: ForceVisible must spawn the overlay");
            Assert.IsNotNull(_view.FloatZone, "precondition: the floating zone must exist");
        }

        [TearDown]
        public void TearDown()
        {
            TouchHaptics.Pulse = _originalPulse;
            TouchHaptics.ResetCounters();
            TouchControlSettingOptions.ResetToDefaults();

            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();

            // The overlay hangs on the persistent UIRoot canvas: never leak it into another case.
            foreach (TouchControlsView overlay in UnityEngine.Object.FindObjectsOfType<TouchControlsView>())
            {
                overlay.Shutdown();
                UnityEngine.Object.Destroy(overlay.gameObject);
            }

            _view = null;
            TouchControlsView.ForceVisible = false;
        }

        // ---------------- ① 浮动原点 ----------------

        [UnityTest]
        public IEnumerator Case1_FloatPress_AnchorFollowsThePressPoint_AndTheBaseDisappearsOnRelease()
        {
            Vector2 pressA = ScreenPoint(0.25f, 0.18f);
            Vector2 pressB = ScreenPoint(0.55f, 0.38f);

            _view.FloatPress(101, pressA);
            yield return null;

            Assert.IsTrue(_view.Stick.IsHeld, "按压后摇杆必须被持有");
            Assert.AreEqual(101, _view.Stick.PointerId);
            Assert.AreEqual(0f, _view.Stick.Value.Magnitude, 1e-4, "浮动按压点 == 底座圆心 → 读作零");
            Image baseImage = _view.StickBase.GetComponent<Image>();
            Assert.IsTrue(baseImage.enabled, "按下必须在该处生成底座（生成）");

            Vector2 anchorA = _view.StickBase.anchoredPosition;
            _view.FloatRelease(101);
            yield return null;

            Assert.IsFalse(_view.Stick.IsHeld);
            Assert.IsFalse(baseImage.enabled, "松开必须消失（设计要点 1）");
            Assert.AreEqual(0f, InputRuntime.Bridge.LastMove.Magnitude, 1e-5, "松手归零");

            _view.FloatPress(101, pressB);
            yield return null;
            Vector2 anchorB = _view.StickBase.anchoredPosition;

            // 非循环的可证伪断言：锚点位移必须等于屏幕位移除以画布缩放（线性跟随），
            // 缩放系数独立取自 HUD Canvas，而不是用被测代码的换算结果。
            float scale = Mathf.Max(1e-3f, HudCanvas().scaleFactor);
            Vector2 expectedDelta = (pressB - pressA) / scale;
            Assert.Less((anchorB - anchorA - expectedDelta).magnitude, 0.5f,
                "摇杆锚点必须跟随不同的按压点（锚点坐标断言，可证伪用例①）");

            _view.FloatRelease(101);
        }

        // ---------------- ② 死区 ----------------

        [UnityTest]
        public IEnumerator Case2_DeadZone_MicroDragsProduceZeroInput_MacroDragsProduceMovement()
        {
            Vector2 press = ScreenPoint(0.3f, 0.2f);
            float scale = Mathf.Max(1e-3f, HudCanvas().scaleFactor);
            float radius = StickTravelRadius();
            float deadZoneLocal = VirtualJoystick.DefaultDeadZone * radius; // 默认倍率 1
            Vector2 microDelta = new Vector2(0f, deadZoneLocal * 0.3f * scale); // 死区内的 30%
            Vector2 macroDelta = new Vector2(0f, radius * 0.85f * scale); // 远超死区

            _view.FloatPress(102, press);
            yield return null;

            _view.FloatDrag(102, press + microDelta);
            yield return null;
            Assert.AreEqual(0f, _view.Stick.Value.Magnitude, 1e-5, "死区内微动不得产生位移输入（可证伪用例②）");
            Assert.AreEqual(0f, InputRuntime.Bridge.LastMove.Magnitude, 1e-5, "移动链路也必须保持零");

            _view.FloatDrag(102, press + macroDelta);
            yield return null;
            Assert.Greater(_view.Stick.Value.Y, 0.3f, "超出死区的拖动必须产生位移输入");
            Assert.Greater(InputRuntime.Bridge.LastMove.Y, 0.3f, "移动链路必须收到该输入");

            _view.FloatRelease(102);
            yield return null;
            Assert.AreEqual(0f, _view.Stick.Value.Magnitude, 1e-5, "松手归零");
        }

        // ---------------- ③ 多点 ----------------

        [UnityTest]
        public IEnumerator Case3_MultiTouch_FloatStickAndInteractButtonAreBothLive()
        {
            Vector2 press = ScreenPoint(0.22f, 0.2f);
            float scale = Mathf.Max(1e-3f, HudCanvas().scaleFactor);
            Vector2 holdUp = press + new Vector2(0f, StickTravelRadius() * 0.8f * scale);

            _view.FloatPress(201, press);
            _view.FloatDrag(201, holdUp);
            yield return null;

            Assert.Greater(_view.Stick.Value.Y, 0.3f, "precondition: 左手拇指已推住摇杆");
            int eventsBefore = _interactEvents;
            int bridgeBefore = InputRuntime.Bridge.InteractCount; // 持久单例，跨用例累计——只断言增量

            // 右手拇指：独立 pointerId 的交互键按下（真机上 StandaloneInputModule
            // 每根手指一条 pointer 流；测试里用不同 pointerId 表达同一语义）。
            _view.InteractButton.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 202 });
            yield return null;

            Assert.AreEqual(1, _view.InteractButton.PressCount);
            Assert.AreEqual(eventsBefore + 1, _interactEvents, "摇杆按住时交互键必须同时生效（多点触控，可证伪用例③）");
            Assert.AreEqual(bridgeBefore + 1, InputRuntime.Bridge.InteractCount, "交互命令恰好一条进桥");
            Assert.Greater(_view.Stick.Value.Y, 0.3f, "右键按下不得打断左手的方向");

            _view.InteractButton.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 202 });
            _view.FloatRelease(201);
            yield return null;

            Assert.AreEqual(0f, _view.Stick.Value.Magnitude, 1e-5, "两个手指都松开后摇杆归零");
            Assert.AreEqual(eventsBefore + 1, _interactEvents, "交互事件不重复");
        }

        // ---------------- ④ 参数化 ----------------

        [UnityTest]
        public IEnumerator Case4_SensitivityDoubling_SameDragDisplacementDoublesTheVector()
        {
            Vector2 press = ScreenPoint(0.25f, 0.2f);
            float scale = Mathf.Max(1e-3f, HudCanvas().scaleFactor);
            float localOffset = StickTravelRadius() * 0.45f; // 未饱和区间：×2 后仍 < 1
            Vector2 dragScreen = press + new Vector2(0f, localOffset * scale);

            _view.FloatPress(301, press);
            _view.FloatDrag(301, dragScreen);
            yield return null;

            float baseline = InputRuntime.Bridge.LastMove.Y;
            Assert.Greater(baseline, 0.05f, "precondition: 灵敏度 1.0 下该位移产生非零基线");
            Assert.Less(baseline, 0.45f, "precondition: 基线必须未饱和");
            _view.FloatRelease(301);
            yield return null;

            TouchControlSettingOptions.ApplySensitivity(2f); // 设置行 → Changed → 活动摇杆即时调参
            Assert.AreEqual(2f, TouchControlSettingOptions.SensitivityMultiplier);

            _view.FloatPress(302, press);
            _view.FloatDrag(302, dragScreen); // 完全相同的屏幕位移
            yield return null;

            float doubled = InputRuntime.Bridge.LastMove.Y;
            Assert.AreEqual(baseline * 2f, doubled, 0.02f, "灵敏度 2.0 下同位移的输入向量必须翻倍（可证伪用例④）");
            Assert.LessOrEqual(doubled, 1f + 1e-4f, "翻倍后仍夹在单位长度内");

            _view.FloatRelease(302);
        }

        // ---------------- ⑤ 触觉开关 ----------------

        [UnityTest]
        public IEnumerator Case5_HapticsToggleOff_ProducesZeroPulseCalls_CommandsStillFire()
        {
            int commandsBefore = InputRuntime.Bridge.InteractCount;

            _view.InteractButton.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 401 });
            yield return null;
            Assert.AreEqual(1, _pulseCount, "开启触觉：一次按下必须恰好一次脉冲调用");
            Assert.AreEqual(1, TouchHaptics.PulseCount);
            _view.InteractButton.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 401 });
            yield return null;

            TouchControlSettingOptions.ApplyHaptics(false);
            Assert.IsFalse(TouchHaptics.Enabled, "震动反馈行关闭必须落到闸门");

            _view.InteractButton.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 402 });
            yield return null;
            Assert.AreEqual(1, _pulseCount, "震动反馈关闭时脉冲调用计数必须不变（可证伪用例⑤）");
            Assert.AreEqual(1, TouchHaptics.PulseCount);
            Assert.AreEqual(commandsBefore + 2, InputRuntime.Bridge.InteractCount, "开关只影响震动，交互命令照常发出");
            _view.InteractButton.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 402 });
            yield return null;

            TouchControlSettingOptions.ApplyHaptics(true);
            _view.InteractButton.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 403 });
            yield return null;
            Assert.AreEqual(2, _pulseCount, "重新开启后脉冲恢复");
            _view.InteractButton.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 403 });
        }

        // ---------------- helpers ----------------

        private Canvas HudCanvas()
        {
            Canvas hud = _root.GetLayerCanvas(UIConsts.UILayer.HUD);
            Assert.IsNotNull(hud, "HUD layer canvas must exist");
            return hud;
        }

        private float StickTravelRadius()
        {
            RectTransform knob = _view.StickBase.Find("Knob") as RectTransform;
            Assert.IsNotNull(knob, "the stick knob must exist");
            return (_view.StickBase.rect.width * 0.5f) - (knob.rect.width * 0.5f);
        }

        private Vector2 ScreenPoint(float fx, float fy)
        {
            return new Vector2(Screen.width * fx, Screen.height * fy);
        }
    }
}
