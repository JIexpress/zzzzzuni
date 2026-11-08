using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using ZFrame.Gameplay;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
using ZFrame.View.Diagnostics;
#endif

namespace ZFrame.View
{
    /// <summary>
    /// On-screen touch controls of the gameplay HUD (TASK-A-AND-04): a stick
    /// in the bottom-left corner and two action buttons (互动 / 暂停) in the
    /// bottom-right, all drawn with plain UGUI — no prefab, no art assets.
    ///
    /// WHY THIS INSTEAD OF AN InputSystemUIInputModule SWAP: see the phase-A
    /// gate in <c>Docs/Logs/TASK-A-AND-04.md</c> §2. Short version — the
    /// committed <c>ZFrameControls</c> UI map binds Point/Click to
    /// <c>&lt;Mouse&gt;</c> only, and InputSystemUIInputModule needs both of
    /// them for a pointer to exist at all, so swapping the module without a
    /// touch-binding rewrite kills every on-device tap. The widgets therefore
    /// ride the already device-proven EventSystem path (StandaloneInputModule
    /// + legacy touch) and drive the SAME gameplay command chain via
    /// <see cref="PlayerInputBridge.ApplyVirtualMove"/> and friends.
    ///
    /// VISIBILITY: only on touch hardware (<see cref="ShouldShow"/>); on
    /// desktop <see cref="Spawn"/> returns null and creates NOTHING, so there
    /// is zero residue. <see cref="ForceVisible"/> is the debug/tests override.
    ///
    /// SAFE AREA: the widgets live under a rect anchored to
    /// <see cref="Screen.safeArea"/> and re-anchor when the inset changes
    /// (rotation, gesture-bar), so the stick never sits under the notch or the
    /// system gesture strip.
    ///
    /// TASK-A-AND-10 (打磨不重写): adds the polish layer ON TOP of the
    /// A-AND-04 structure —
    /// - FLOATING ORIGIN: an invisible <see cref="FloatingStickZone"/> over the
    ///   bottom half of the safe area spawns the stick at the press point and
    ///   hides it on release (默认). The A-AND-04 fixed bottom-left stick stays
    ///   as the 触屏·固定摇杆 settings row; the spawn de-dup and EventSystem
    ///   handling are untouched.
    /// - PARAMETERISATION: 死区/拖拽半径/灵敏度 multipliers arrive through
    ///   <see cref="TouchControlSettingOptions"/> (apply on change, read on build).
    /// - VISUAL FEEDBACK: base/knob brighten while held; buttons tween their
    ///   scale in <see cref="TouchActionButton"/>.
    /// - HAPTICS: the 互动 press pulses <see cref="TouchHaptics"/> (the
    ///   震动反馈 row owns the switch; 暂停 opens a menu and deliberately
    ///   stays pulse-free).
    /// - MULTI-TOUCH: unchanged A-AND-04 architecture — StandaloneInputModule
    ///   gives one pointer stream per finger, the zone forwards to the stick
    ///   (single-pointer ownership) and the buttons take their own pointers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TouchControlsView : MonoBehaviour
    {
        /// <summary>GameObject name of the overlay root.</summary>
        public const string RootName = "[TouchControls]";

        /// <summary>GameObject name of the safe-area host rect.</summary>
        public const string SafeAreaName = "[TouchControls.SafeArea]";

        private const float StickDiameter = 300f;
        private const float KnobDiameter = 124f;
        private const float EdgeMargin = 36f;
        private const float InteractSize = 160f;
        private const float PauseSize = 104f;
        private const float ButtonGap = 22f;

        /// <summary>How often the safe-area inset is re-read (Screen.safeArea is a native getter; once per frame is waste).</summary>
        private const float SafeAreaPollInterval = 0.5f;

        private static readonly Color StickBaseColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color KnobColor = new Color(1f, 1f, 1f, 0.72f);
        private static readonly Color InteractColor = new Color(0.25f, 0.45f, 0.7f, 0.85f);
        private static readonly Color InteractPressedColor = new Color(0.35f, 0.58f, 0.85f, 0.95f);
        private static readonly Color PauseColor = new Color(0.16f, 0.17f, 0.2f, 0.85f);
        private static readonly Color PausePressedColor = new Color(0.32f, 0.33f, 0.36f, 0.95f);

        // TASK-A-AND-10: held variants of the stick visuals ("半透明 + 按下增亮").
        private static readonly Color StickBaseHeldColor = new Color(1f, 1f, 1f, 0.34f);
        private static readonly Color KnobHeldColor = new Color(1f, 1f, 1f, 0.92f);

        private static Sprite _circleSprite;

        private PlayerInputBridge _bridge;
        private RectTransform _safeArea;
        private RectTransform _stickZone;
        private Image _stickBaseImage;
        private Image _knobImage;
        private Image _floatZoneImage;
        private Rect _appliedSafeArea = new Rect(-1f, -1f, 0f, 0f);
        private Vector2 _appliedScreen;
        private float _nextSafeAreaPoll;
        private bool _shutdown;
        private int _moveCount;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private float _nextStickLogTime;
#endif

        /// <summary>
        /// Debug/automation override: forces the overlay on non-touch platforms
        /// (Editor play, tests). Never set in a shipping build.
        /// </summary>
        public static bool ForceVisible { get; set; }

        /// <summary>True when this platform should show on-screen controls.</summary>
        public static bool ShouldShow =>
            ForceVisible || Application.isMobilePlatform || Touchscreen.current != null;

        /// <summary>The stick (null before <see cref="Spawn"/>).</summary>
        public VirtualJoystick Stick { get; private set; }

        /// <summary>互动 button (null before <see cref="Spawn"/>).</summary>
        public TouchActionButton InteractButton { get; private set; }

        /// <summary>暂停 button (null before <see cref="Spawn"/>).</summary>
        public TouchActionButton PauseButton { get; private set; }

        /// <summary>Safe-area host the widgets are anchored inside.</summary>
        public RectTransform SafeArea => _safeArea;

        /// <summary>
        /// The invisible lower-half floating hit zone (null before
        /// <see cref="Spawn"/>; the GameObject is deactivated in the fixed-stick
        /// mode). <see cref="FloatingStickZone"/> forwards its pointer stream to
        /// <see cref="FloatPress"/> / <see cref="FloatDrag"/> / <see cref="FloatRelease"/>.
        /// </summary>
        public FloatingStickZone FloatZone { get; private set; }

        /// <summary>
        /// The stick base rect (touch/limit circle). Tests and the floating
        /// anchor logic read its <c>anchoredPosition</c>; its size follows the
        /// 拖拽半径 multiplier.
        /// </summary>
        public RectTransform StickBase => _stickZone;

        /// <summary>
        /// The command bridge this overlay currently writes into (null after shutdown).
        /// Exposed so the scene-reentry regression test can assert the re-wiring directly
        /// (TASK-A-AUDIT-02 A-3): a "reused" overlay used to keep the previous bridge.
        /// </summary>
        public PlayerInputBridge Bridge => _bridge;

        /// <summary>Number of stick values forwarded to the bridge (diagnostics/tests).</summary>
        public int MoveCount => _moveCount;

        /// <summary>
        /// Builds the overlay on the HUD layer canvas of <paramref name="root"/>
        /// and wires it to <paramref name="bridge"/>. Returns null (and creates
        /// nothing) when the platform has no touch input, when the UI root or
        /// the input stack is missing, or when the HUD canvas is absent.
        /// </summary>
        public static TouchControlsView Spawn(UIRoot root, PlayerInputBridge bridge)
        {
            if (root == null || bridge == null || !ShouldShow)
            {
                return null;
            }

            Canvas hud = root.GetLayerCanvas(UIConsts.UILayer.HUD);
            if (hud == null)
            {
                return null;
            }

            // TASK-A-AUDIT-02 A-3:只把"活着"的实例当去重命中。
            //
            // 曾经的判据是"场景里还找得到 TouchControlsView 吗"，而 Shutdown() 在运行期走的是
            // Destroy（**延迟到帧末**）。只要有一次 Spawn 落在这个窗口里（本任务的 PlayMode 用例
            // 用"同一帧 Shutdown + Spawn"精确构造了它），这里就会命中一个**本帧待销毁**的旧
            // overlay：返回它 → 帧末它被销毁 → 新 HUD 的 _touchControls 变成 fake-null，而
            // Spawn 每场景只调一次，控件此后再也不会重建。
            //
            // _shutdown 由 Teardown 在**排队 Destroy 之前**置位，所以它恰好就是"待销毁"的判据：
            // 命中它就等于命中了一个即将消失的对象。跳过它，本次会老老实实重建一套。
            TouchControlsView existing = hud.GetComponentInChildren<TouchControlsView>(true);
            if (existing != null && !existing._shutdown)
            {
                // 同一个 root 重复 Spawn：控件在位，只需把 bridge 换成当前这个（旧实例可能已 Dispose）。
                existing._bridge = bridge;
                return existing;
            }

            var host = new GameObject(RootName, typeof(RectTransform));
            var rect = (RectTransform)host.transform;
            rect.SetParent(hud.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.SetAsLastSibling();

            var view = host.AddComponent<TouchControlsView>();
            view.Build(bridge);
            return view;
        }

        /// <summary>
        /// Re-anchors <paramref name="target"/> onto the normalized
        /// <paramref name="safeArea"/> of a <paramref name="screenSize"/> sized
        /// screen. Degenerate inputs fall back to "no inset" instead of
        /// collapsing the rect.
        /// </summary>
        public static void ApplySafeArea(RectTransform target, Rect safeArea, Vector2 screenSize)
        {
            if (target == null)
            {
                return;
            }

            bool usable = screenSize.x > 0f && screenSize.y > 0f
                && safeArea.width > 0f && safeArea.height > 0f;

            if (!usable)
            {
                target.anchorMin = Vector2.zero;
                target.anchorMax = Vector2.one;
                target.offsetMin = Vector2.zero;
                target.offsetMax = Vector2.zero;
                return;
            }

            Vector2 min = new Vector2(
                Mathf.Clamp01(safeArea.xMin / screenSize.x),
                Mathf.Clamp01(safeArea.yMin / screenSize.y));
            Vector2 max = new Vector2(
                Mathf.Clamp01(safeArea.xMax / screenSize.x),
                Mathf.Clamp01(safeArea.yMax / screenSize.y));

            target.anchorMin = min;
            target.anchorMax = max;
            target.offsetMin = Vector2.zero;
            target.offsetMax = Vector2.zero;
        }

        /// <summary>Tears the overlay down (idempotent): releases the stick, drops the bindings, destroys the hierarchy.</summary>
        public void Shutdown()
        {
            Teardown(destroySelf: true);
        }

        // ---- TASK-A-AND-10: floating-origin seams ----------------------------

        /// <summary>
        /// Floating-press seam, camera-free form for tests/tools (the overlay
        /// canvas is screen-space). Same body the UGUI handlers run.
        /// </summary>
        public void FloatPress(int pointerId, Vector2 screenPosition)
        {
            FloatPress(pointerId, screenPosition, null);
        }

        /// <summary>
        /// Floating-press: re-anchors the stick base under the finger (clamped
        /// into the safe area) and hands the pointer to the stick. Second
        /// fingers are ignored (A-AND-04 one-pointer ownership); the fixed-stick
        /// mode has no floating gesture at all.
        /// </summary>
        public void FloatPress(int pointerId, Vector2 screenPosition, Camera pressCamera)
        {
            if (_shutdown || Stick == null || TouchControlSettingOptions.FixedStick || Stick.IsHeld)
            {
                return;
            }

            if (_safeArea == null ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(_safeArea, screenPosition, pressCamera, out Vector2 local))
            {
                return;
            }

            PlaceStickBaseAt(local);
            // Synthesized event (test/automation seam): carries pointer id +
            // screen point only. The UIRoot canvas is ScreenSpaceOverlay, so
            // the camera-less conversion the stick performs is exact here; the
            // UGUI production path goes through <see cref="FloatPress(PointerEventData)"/>
            // which preserves the original event object untouched.
            //
            // Forward the ORIGINAL screen point: when the base was clamped away
            // from the finger, the press must land at its true offset from the
            // base centre (edge presses read as a partial push, like a real
            // floating stick).
            Stick.OnPointerDown(new PointerEventData(EventSystem.current)
            {
                pointerId = pointerId,
                position = screenPosition,
            });
        }

        /// <summary>UGUI entry: the <see cref="FloatingStickZone"/> forwards its original event object.</summary>
        internal void FloatPress(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            FloatPress(eventData.pointerId, eventData.position, eventData.pressEventCamera);
        }

        /// <summary>
        /// Test/automation seam (same discipline as
        /// <see cref="VirtualJoystick.ApplyLocalOffset"/>): the floating press
        /// with the point ALREADY in safe-area local units — batch-mode EditMode
        /// has no reliable screen space, so tests express the press in the exact
        /// coordinates <see cref="PlaceStickBaseAt"/> consumes and normalize the
        /// press to the base centre afterwards (press point == centre == zero
        /// value). The screen-space production path is <see cref="FloatPress(int, Vector2, Camera)"/>.
        /// </summary>
        internal void FloatPressAtLocal(int pointerId, Vector2 safeAreaLocalPoint)
        {
            if (_shutdown || Stick == null || TouchControlSettingOptions.FixedStick || Stick.IsHeld || _stickZone == null)
            {
                return;
            }

            PlaceStickBaseAt(safeAreaLocalPoint);
            Stick.BeginPointer(pointerId); // ownership without a screen-point read (batch EditMode has none)
            Stick.ApplyLocalOffset(_stickZone.rect.center); // press point == base centre → deterministic zero
        }

        /// <summary>Floating-drag seam (camera-free form). Forwards to the stick only for the owning pointer.</summary>
        public void FloatDrag(int pointerId, Vector2 screenPosition)
        {
            FloatDrag(pointerId, screenPosition, null);
        }

        /// <summary>Floating-drag: same geometry step as <see cref="VirtualJoystick.OnDrag"/> for the owning pointer.</summary>
        public void FloatDrag(int pointerId, Vector2 screenPosition, Camera pressCamera)
        {
            if (_shutdown || Stick == null || !Stick.IsHeld || Stick.PointerId != pointerId)
            {
                return;
            }

            // Synthesized event (test/automation seam; ScreenSpaceOverlay →
            // camera-less conversion is exact). Production uses the overload below.
            Stick.OnDrag(new PointerEventData(EventSystem.current)
            {
                pointerId = pointerId,
                position = screenPosition,
            });
        }

        /// <summary>UGUI entry: the <see cref="FloatingStickZone"/> forwards its original event object.</summary>
        internal void FloatDrag(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            FloatDrag(eventData.pointerId, eventData.position, eventData.pressEventCamera);
        }

        /// <summary>
        /// Floating-release: hands the pointer back to the stick; the base
        /// disappears through <see cref="OnStickHoldChanged"/>(false).
        /// </summary>
        public void FloatRelease(int pointerId)
        {
            if (_shutdown || Stick == null || Stick.PointerId != pointerId)
            {
                return;
            }

            Stick.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = pointerId });
        }

        /// <summary>
        /// Places the base centre at <paramref name="safeAreaLocalPoint"/>
        /// (pivot-relative safe-area units), clamped so the base circle stays
        /// inside the safe area.
        /// </summary>
        private void PlaceStickBaseAt(Vector2 safeAreaLocalPoint)
        {
            if (_stickZone == null || _safeArea == null)
            {
                return;
            }

            Rect safe = _safeArea.rect; // pivot-relative: centre (0,0)
            float half = StickDiameter * 0.5f * TouchControlSettingOptions.DragRadiusMultiplier;
            float xMin = safe.xMin + half;
            float xMax = safe.xMax - half;
            float yMin = safe.yMin + half;
            float yMax = safe.yMax - half;

            float cx = Mathf.Clamp(safeAreaLocalPoint.x, xMin, Mathf.Max(xMin, xMax));
            float cy = Mathf.Clamp(safeAreaLocalPoint.y, yMin, Mathf.Max(yMin, yMax));

            // Base anchors/pivot are (0,0): anchoredPosition = base bottom-left
            // corner minus the safe area's bottom-left corner.
            _stickZone.anchoredPosition = new Vector2(cx - half - safe.xMin, cy - half - safe.yMin);
        }

        /// <summary>Restores the A-AND-04 fixed anchor (bottom-left corner margin).</summary>
        private void ResetStickBaseAnchor()
        {
            if (_stickZone != null)
            {
                _stickZone.anchoredPosition = new Vector2(EdgeMargin, EdgeMargin);
            }
        }

        /// <summary>
        /// Press-visual feedback (TASK-A-AND-10): brighten the base/knob while
        /// held; in the floating mode the base only EXISTS while held (spawn on
        /// press, disappear on release).
        /// </summary>
        private void OnStickHoldChanged(bool held)
        {
            if (TouchControlSettingOptions.FixedStick)
            {
                ShowStickBase(held); // fixed: always visible, dim ↔ bright
                return;
            }

            if (held)
            {
                ShowStickBase(held: true);
            }
            else
            {
                HideStickBase(); // floating: 松开消失
            }
        }

        private void ShowStickBase(bool held)
        {
            if (_stickBaseImage == null)
            {
                return;
            }

            _stickBaseImage.enabled = true;
            ApplyStickColors(held);
        }

        private void HideStickBase()
        {
            if (_stickBaseImage == null)
            {
                return;
            }

            _stickBaseImage.enabled = false; // no rendering, no raycast
            ApplyStickColors(held: false);
        }

        private void ApplyStickColors(bool held)
        {
            if (_stickBaseImage != null)
            {
                _stickBaseImage.color = held ? StickBaseHeldColor : StickBaseColor;
            }

            if (_knobImage != null)
            {
                _knobImage.color = held ? KnobHeldColor : KnobColor;
            }
        }

        /// <summary>
        /// TASK-A-AND-10: re-applies every touch-polish setting to the live
        /// widgets — the 死区/灵敏度 multipliers go to <see cref="VirtualJoystick.SetTuning"/>,
        /// the 拖拽半径 multiplier resizes the base rect (visual and drag limit
        /// stay the same circle) and the 固定摇杆 row switches the floating/fixed
        /// presentation.
        /// </summary>
        private void ApplyTouchSettings()
        {
            if (Stick != null)
            {
                Stick.SetTuning(
                    TouchControlSettingOptions.DeadZoneMultiplier,
                    TouchControlSettingOptions.SensitivityMultiplier);
            }

            if (_stickZone != null)
            {
                float radiusScale = TouchControlSettingOptions.DragRadiusMultiplier;
                _stickZone.sizeDelta = new Vector2(StickDiameter * radiusScale, StickDiameter * radiusScale);
            }

            ApplyStickMode();
        }

        private void ApplyStickMode()
        {
            bool floating = !TouchControlSettingOptions.FixedStick;

            if (FloatZone != null)
            {
                FloatZone.gameObject.SetActive(floating);
            }

            if (_floatZoneImage != null)
            {
                _floatZoneImage.raycastTarget = floating;
            }

            if (_stickBaseImage != null)
            {
                // 浮动：底座纯视觉（FloatZone 命中）；固定：底座本身是命中目标（A-AND-04）。
                _stickBaseImage.raycastTarget = !floating;
            }

            if (floating)
            {
                if (Stick != null && Stick.IsHeld)
                {
                    ShowStickBase(held: true);
                }
                else
                {
                    HideStickBase();
                }
            }
            else
            {
                ResetStickBaseAnchor();
                ShowStickBase(held: Stick != null && Stick.IsHeld);
            }
        }

        private void Teardown(bool destroySelf)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;

            if (Stick != null)
            {
                Stick.Release(); // publishes Vec2.Zero while still subscribed (held finger on scene unload)
                Stick.Moved -= OnStickMoved;
                Stick.HoldChanged -= OnStickHoldChanged;
            }

            if (InteractButton != null)
            {
                InteractButton.Pressed -= OnInteractPressed;
                InteractButton.Reset();
            }

            if (PauseButton != null)
            {
                PauseButton.Pressed -= OnPausePressed;
                PauseButton.Reset();
            }

            TouchControlSettingOptions.Changed -= ApplyTouchSettings;
            _bridge = null;

            if (!destroySelf)
            {
                return; // already being destroyed by the engine — do not re-enter
            }

            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                // EditMode: Destroy is deferred to the next editor tick, tests assert immediately.
                DestroyImmediate(gameObject);
            }
        }

        private void Build(PlayerInputBridge bridge)
        {
            _bridge = bridge;

            var root = (RectTransform)transform;
            _safeArea = CreateRect(root, SafeAreaName);
            ApplySafeArea(_safeArea, Screen.safeArea, new Vector2(Screen.width, Screen.height));
            RememberSafeArea();

            // SIBLING ORDER: FloatZone FIRST — within one canvas later siblings
            // win the raycast, so 互动/暂停 (built after) stay on top of the
            // lower-half zone while every other bottom-half point floats the stick.
            BuildFloatZone(_safeArea);
            BuildStick(_safeArea);
            BuildButtons(_safeArea);

            // TASK-A-AND-10: live overlays follow the settings rows immediately
            // (the boot applier / panel writes them through Apply* → Changed).
            TouchControlSettingOptions.Changed += ApplyTouchSettings;
            ApplyTouchSettings();
        }

        private void BuildFloatZone(RectTransform parent)
        {
            RectTransform zone = CreateRect(parent, FloatingStickZone.ZoneName);
            zone.anchorMin = Vector2.zero;
            zone.anchorMax = new Vector2(1f, 0.5f); // 下半屏（安全区内）
            zone.pivot = new Vector2(0.5f, 0.5f);
            zone.offsetMin = Vector2.zero;
            zone.offsetMax = Vector2.zero;

            // A raycast needs a Graphic: fully transparent, raycast-target only.
            Image hit = zone.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;

            _floatZoneImage = hit;
            FloatZone = zone.gameObject.AddComponent<FloatingStickZone>();
            FloatZone.Owner = this;
        }

        private void BuildStick(RectTransform parent)
        {
            RectTransform zone = CreateRect(parent, "JoystickZone");
            zone.anchorMin = Vector2.zero;
            zone.anchorMax = Vector2.zero;
            zone.pivot = Vector2.zero;
            zone.anchoredPosition = new Vector2(EdgeMargin, EdgeMargin);
            zone.sizeDelta = new Vector2(StickDiameter, StickDiameter);

            Image baseImage = zone.gameObject.AddComponent<Image>();
            baseImage.sprite = CircleSprite;
            baseImage.color = StickBaseColor;
            baseImage.raycastTarget = true; // the touch zone is the hit target (fixed mode; floating flips this)

            RectTransform knob = CreateRect(zone, "Knob");
            knob.anchorMin = new Vector2(0.5f, 0.5f);
            knob.anchorMax = new Vector2(0.5f, 0.5f);
            knob.pivot = new Vector2(0.5f, 0.5f);
            knob.anchoredPosition = Vector2.zero;
            knob.sizeDelta = new Vector2(KnobDiameter, KnobDiameter);

            Image knobImage = knob.gameObject.AddComponent<Image>();
            knobImage.sprite = CircleSprite;
            knobImage.color = KnobColor;
            knobImage.raycastTarget = false;

            _stickZone = zone;
            _stickBaseImage = baseImage;
            _knobImage = knobImage;

            Stick = zone.gameObject.AddComponent<VirtualJoystick>();
            Stick.Configure(zone, knob);
            Stick.Moved += OnStickMoved;
            Stick.HoldChanged += OnStickHoldChanged;
        }

        private void BuildButtons(RectTransform parent)
        {
            InteractButton = BuildButton(
                parent, "InteractButton", "互动", 44, InteractSize,
                new Vector2(-EdgeMargin, EdgeMargin), InteractColor, InteractPressedColor);
            InteractButton.Pressed += OnInteractPressed;

            PauseButton = BuildButton(
                parent, "PauseButton", "暂停", 30, PauseSize,
                new Vector2(-EdgeMargin, EdgeMargin + InteractSize + ButtonGap), PauseColor, PausePressedColor);
            PauseButton.Pressed += OnPausePressed;
        }

        private TouchActionButton BuildButton(
            RectTransform parent, string name, string caption, int fontSize, float size,
            Vector2 offset, Color idleColor, Color pressedColor)
        {
            RectTransform rect = CreateRect(parent, name);
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(size, size);

            // Same visual language as the menu buttons (flat blue/dark rect + white label).
            Image background = rect.gameObject.AddComponent<Image>();
            background.color = idleColor;
            background.raycastTarget = true;

            Text label = CreateLabel(rect, caption, fontSize);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = Vector2.zero;
            label.rectTransform.offsetMax = Vector2.zero;

            var button = rect.gameObject.AddComponent<TouchActionButton>();
            button.Configure(background, idleColor, pressedColor);
            return button;
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static Text CreateLabel(RectTransform parent, string caption, int fontSize)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);

            var label = go.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = caption;
            label.fontSize = fontSize;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        private void Update()
        {
            if (_shutdown || _safeArea == null || Time.unscaledTime < _nextSafeAreaPoll)
            {
                return;
            }

            _nextSafeAreaPoll = Time.unscaledTime + SafeAreaPollInterval;

            Vector2 screen = new Vector2(Screen.width, Screen.height);
            Rect safeArea = Screen.safeArea;
            if (screen != _appliedScreen || safeArea != _appliedSafeArea)
            {
                ApplySafeArea(_safeArea, safeArea, screen);
                RememberSafeArea();
            }
        }

        private void OnDestroy()
        {
            Teardown(destroySelf: false);
        }

        private void RememberSafeArea()
        {
            _appliedSafeArea = Screen.safeArea;
            _appliedScreen = new Vector2(Screen.width, Screen.height);
        }

        private void OnStickMoved(Vec2 value)
        {
            _moveCount++;
            _bridge?.ApplyVirtualMove(value);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (Time.unscaledTime >= _nextStickLogTime)
            {
                _nextStickLogTime = Time.unscaledTime + 0.25f;
                DeviceFileLogger.Step(
                    $"touch stick: x={value.X:F2} y={value.Y:F2} ptr={Stick.PointerId} moves={_moveCount}");
            }
#endif
        }

        private void OnInteractPressed()
        {
            // TASK-A-AND-10: 互动 = the haptic action button. The pulse is
            // gated by TouchHaptics.Enabled (震动反馈 row) and is a safe no-op
            // without vibration hardware. 暂停 opens a menu and deliberately
            // stays pulse-free (one meaningful haptic per action verb).
            TouchHaptics.Trigger();
            _bridge?.ApplyVirtualInteract();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            DeviceFileLogger.Step("touch button: 互动 pressed -> InteractInputEvent");
#endif
        }

        private void OnPausePressed()
        {
            _bridge?.ApplyVirtualPause();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            DeviceFileLogger.Step("touch button: 暂停 pressed -> PauseInputEvent");
#endif
        }

        private static Sprite CircleSprite
        {
            get
            {
                if (_circleSprite == null)
                {
                    _circleSprite = BuildCircleSprite(128);
                }

                return _circleSprite;
            }
        }

        /// <summary>
        /// Procedural antialiased disc — "纯 UGUI 图形，不用美术资源": the one
        /// shape the stick needs that a plain Image rect cannot give.
        /// </summary>
        private static Sprite BuildCircleSprite(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "TouchControls.Circle",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            float half = size * 0.5f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) - half;
                    float dy = (y + 0.5f) - half;
                    float distance = Mathf.Sqrt((dx * dx) + (dy * dy)) / (half - 1f);
                    byte alpha = (byte)(Mathf.Clamp01((1f - distance) * half) * 255f);
                    pixels[(y * size) + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            sprite.name = "TouchControls.Circle";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
