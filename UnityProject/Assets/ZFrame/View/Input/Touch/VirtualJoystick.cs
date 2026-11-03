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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VirtualJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>Fraction of the radius that reads as "not pushed" (default).</summary>
        public const float DefaultDeadZone = 0.15f;

        private RectTransform _area;
        private RectTransform _knob;
        private float _deadZone = DefaultDeadZone;
        private int _pointerId = -1;
        private Vec2 _value;

        /// <summary>Raised whenever the produced vector changes (zero on release/reset).</summary>
        public event Action<Vec2> Moved;

        /// <summary>Current stick vector, x = right, y = forward, magnitude &lt;= 1.</summary>
        public Vec2 Value => _value;

        /// <summary>Pointer currently owning the stick, or -1 when idle.</summary>
        public int PointerId => _pointerId;

        /// <summary>True while a pointer holds the stick.</summary>
        public bool IsHeld => _pointerId >= 0;

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

        /// <inheritdoc />
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData == null || _pointerId >= 0)
            {
                return; // already owned by another finger
            }

            _pointerId = eventData.pointerId;
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
        /// </summary>
        public void Release()
        {
            _pointerId = -1;

            if (_knob != null)
            {
                _knob.anchoredPosition = Vector2.zero;
            }

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

            if (magnitude <= _deadZone)
            {
                SetValue(Vec2.Zero);
                return;
            }

            float scaled = Mathf.InverseLerp(_deadZone, 1f, magnitude);
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
