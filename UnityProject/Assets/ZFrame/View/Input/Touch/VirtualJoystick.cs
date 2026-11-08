using System;
using UnityEngine;
using UnityEngine.EventSystems;
using ZFrame.Gameplay;

namespace ZFrame.View
{
    /// <summary>
    /// On-screen analog stick (TASK-A-AND-04): a UGUI widget that turns
    /// pointer presses/drags into a normalized <see cref="Vec2"/> and raises
    /// <see cref="Moved"/> — it knows nothing about gameplay, the EventBus or
    /// InputSystem, so it stays testable without a device.
    ///
    /// MULTI-TOUCH: exactly one pointer owns the stick at a time
    /// (<see cref="PointerId"/>). A second finger landing on the same zone is
    /// ignored instead of hijacking the value, which is what makes "left
    /// thumb on the stick + right thumb on a button" work on a real device:
    /// <c>StandaloneInputModule</c> dispatches one pointer stream per finger
    /// (pointerId == Touch.fingerId) and each widget only handles its own.
    ///
    /// DEAD ZONE: values below <see cref="DeadZone"/> read as zero; above it
    /// the magnitude is remapped linearly onto [0,1] so the first pixel of
    /// travel after the dead zone does not jump. Direction is preserved.
    ///
    /// TASK-A-AND-10 (打磨不重写): the same widget now carries the polish
    /// parameters — <see cref="SetTuning"/> scales the dead zone and the
    /// output vector (灵敏度), the owning overlay scales the area rect for the
    /// 拖拽半径 row, and <see cref="HoldChanged"/> drives the press visuals /
    /// floating base visibility. Pointer-ownership, dead-zone math and event
    /// semantics of A-AND-04 are untouched.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VirtualJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>Fraction of the radius that reads as "not pushed" (default).</summary>
        public const float DefaultDeadZone = 0.15f;

        /// <summary>Runtime bounds of the dead-zone multiplier (the settings row clamps 0.5~2.0; wider here for safety).</summary>
        public const float MinDeadZoneScale = 0.1f;

        /// <summary>Runtime upper bound of the dead-zone multiplier.</summary>
        public const float MaxDeadZoneScale = 4f;

        /// <summary>Runtime bounds of the sensitivity multiplier (the settings row clamps 0.5~2.0).</summary>
        public const float MinSensitivity = 0.1f;

        /// <summary>Runtime upper bound of the sensitivity multiplier (the value itself is still capped at unit length).</summary>
        public const float MaxSensitivity = 4f;

        private RectTransform _area;
        private RectTransform _knob;
        private float _deadZone = DefaultDeadZone;
        private float _deadZoneScale = 1f;
        private float _sensitivity = 1f;
        private int _pointerId = -1;
        private Vec2 _value;

        /// <summary>Raised whenever the produced vector changes (zero on release/reset).</summary>
        public event Action<Vec2> Moved;

        /// <summary>
        /// TASK-A-AND-10: raised after an accepted pointer down with
        /// <c>true</c> and after every <see cref="Release"/> with <c>false</c>.
        /// The owning overlay uses it for the press-brighten/hide visual
        /// feedback; the widget itself stays graphics-free.
        /// </summary>
        public event Action<bool> HoldChanged;

        /// <summary>Current stick vector, x = right, y = forward, magnitude &lt;= 1.</summary>
        public Vec2 Value => _value;

        /// <summary>Pointer currently owning the stick, or -1 when idle.</summary>
        public int PointerId => _pointerId;

        /// <summary>True while a pointer holds the stick.</summary>
        public bool IsHeld => _pointerId >= 0;

        /// <summary>
        /// TASK-A-AND-10: applies the 死区半径 and 灵敏度 multipliers from the
        /// settings rows at runtime. The 拖拽半径 multiplier is NOT taken here —
        /// the owning overlay scales the area rect itself (the visual base and
        /// the drag limit must stay the same circle), this widget only reads
        /// <see cref="Radius"/> from the rect. Defaults 1/1 reproduce the
        /// A-AND-04 geometry bit for bit.
        /// </summary>
        public void SetTuning(float deadZoneMultiplier, float sensitivity)
        {
            _deadZoneScale = Mathf.Clamp(deadZoneMultiplier, MinDeadZoneScale, MaxDeadZoneScale);
            _sensitivity = Mathf.Clamp(sensitivity, MinSensitivity, MaxSensitivity);
        }

        /// <summary>The hit/limit rect (tests express points in the same pivot-relative space the handlers do).</summary>
        internal RectTransform Area => _area;

        /// <summary>
        /// Wires the widget: <paramref name="area"/> is the touch/limit circle,
        /// <paramref name="knob"/> the visual thumb that follows the pointer.
        /// </summary>
        public void Configure(RectTransform area, RectTransform knob, float deadZone = DefaultDeadZone)
        {
            _area = area != null ? area : throw new ArgumentNullException(nameof(area));
            _knob = knob;
            _deadZone = Mathf.Clamp01(deadZone);
        }

        /// <summary>
        /// Test/automation seam (TASK-A-AND-10, same discipline as
        /// <see cref="ApplyLocalOffset"/>): takes ownership and raises
        /// <see cref="HoldChanged"/>(true) WITHOUT reading a pointer position.
        /// The floating local-space press path uses it because batch-mode
        /// EditMode has no reliable screen space; the value is then normalized
        /// through <see cref="ApplyLocalOffset"/>. No-op while owned.
        /// </summary>
        internal void BeginPointer(int pointerId)
        {
            if (_pointerId >= 0)
            {
                return; // already owned by another finger
            }

            _pointerId = pointerId;
            HoldChanged?.Invoke(true);
        }

        /// <inheritdoc />
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData == null || _pointerId >= 0)
            {
                return; // already owned by another finger
            }

            _pointerId = eventData.pointerId;
            HoldChanged?.Invoke(true);
            UpdateFromPointer(eventData);
        }

        /// <inheritdoc />
        public void OnDrag(PointerEventData eventData)
        {
            if (eventData == null || eventData.pointerId != _pointerId)
            {
                return;
            }

            UpdateFromPointer(eventData);
        }

        /// <inheritdoc />
        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData == null || eventData.pointerId != _pointerId)
            {
                return;
            }

            Release();
        }

        /// <summary>
        /// Publishes <see cref="Vec2.Zero"/> and frees the pointer. Safe to
        /// call when idle (the widget is torn down or disabled while held).
        /// Always raises <see cref="HoldChanged"/>(false) — the owner's visual
        /// reset is idempotent by design.
        /// </summary>
        public void Release()
        {
            _pointerId = -1;

            if (_knob != null)
            {
                _knob.anchoredPosition = Vector2.zero;
            }

            HoldChanged?.Invoke(false);
            SetValue(Vec2.Zero);
        }

        /// <summary>
        /// Test/automation seam: applies a point already expressed in the
        /// area's local units — PIVOT-RELATIVE, exactly what
        /// <c>RectTransformUtility.ScreenPointToLocalPointInRectangle</c> hands
        /// the pointer handlers. The disc centre is the rect centre, so the
        /// offset is taken from there (a rect whose pivot is bottom-left would
        /// otherwise read its own corner as "full push").
        /// </summary>
        internal void ApplyLocalOffset(Vector2 localPoint)
        {
            if (float.IsNaN(localPoint.x) || float.IsNaN(localPoint.y))
            {
                return; // degenerate canvas (screen not yet sized) — never propagate NaN into UGUI
            }

            float radius = Radius;
            Vector2 offset = localPoint - _area.rect.center;
            Vector2 clamped = Vector2.ClampMagnitude(offset, radius);
            float magnitude = clamped.magnitude / radius;

            if (_knob != null)
            {
                _knob.anchoredPosition = clamped;
            }

            // TASK-A-AND-10: the dead-zone threshold is the A-AND-04 fraction
            // scaled by the 死区半径 row; the output strength is the same
            // linear remap scaled by the 灵敏度 row and re-capped at unit
            // length. With both multipliers at 1 this is bit-for-bit the
            // A-AND-04 behaviour (existing tests keep passing unchanged).
            float deadZone = Mathf.Clamp(_deadZone * _deadZoneScale, 0f, 0.95f);
            if (magnitude <= deadZone)
            {
                SetValue(Vec2.Zero);
                return;
            }

            float scaled = Mathf.InverseLerp(deadZone, 1f, magnitude) * _sensitivity;
            if (scaled > 1f)
            {
                scaled = 1f;
            }

            Vector2 direction = clamped.normalized;
            SetValue(new Vec2(direction.x * scaled, direction.y * scaled));
        }

        private void OnDisable()
        {
            Release();
        }

        /// <summary>Distance the knob may travel: area half-width minus knob half-width (never below 1).</summary>
        private float Radius
        {
            get
            {
                if (_area == null)
                {
                    return 1f;
                }

                float knobHalf = _knob != null ? _knob.rect.width * 0.5f : 0f;
                return Mathf.Max(1f, (_area.rect.width * 0.5f) - knobHalf);
            }
        }

        private void UpdateFromPointer(PointerEventData eventData)
        {
            if (_area == null)
            {
                return;
            }

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _area, eventData.position, eventData.pressEventCamera, out Vector2 local))
            {
                ApplyLocalOffset(local);
            }
        }

        private void SetValue(Vec2 value)
        {
            if (Mathf.Abs(value.X - _value.X) < 1e-5f && Mathf.Abs(value.Y - _value.Y) < 1e-5f)
            {
                return;
            }

            _value = value;
            Moved?.Invoke(value);
        }
    }
}
