using System;
using UnityEngine;
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

        private static Sprite _circleSprite;

        private PlayerInputBridge _bridge;
        private RectTransform _safeArea;
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

            BuildStick(_safeArea);
            BuildButtons(_safeArea);
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
            baseImage.raycastTarget = true; // the touch zone is the hit target

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

            Stick = zone.gameObject.AddComponent<VirtualJoystick>();
            Stick.Configure(zone, knob);
            Stick.Moved += OnStickMoved;
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
