using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ZFrame.View
{
    /// <summary>
    /// On-screen action button (TASK-A-AND-04): fires <see cref="Pressed"/>
    /// once per pointer DOWN (touch buttons act on press, not on tap-release —
    /// that is what a player expects from a gamepad-style face button) and
    /// shows a pressed tint while the finger is down.
    ///
    /// Like <see cref="VirtualJoystick"/> it is a pure UGUI widget: it does not
    /// know what the press means, so the caller wires it to the gameplay
    /// command path.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TouchActionButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        private Image _background;
        private Color _idleColor = Color.white;
        private Color _pressedColor = Color.white;

        /// <summary>Raised on every pointer down (one per press, never per frame).</summary>
        public event Action Pressed;

        /// <summary>How many presses this widget forwarded (diagnostics/tests).</summary>
        public int PressCount { get; private set; }

        /// <summary>Pointer currently holding the button down, or -1.</summary>
        public int PointerId { get; private set; } = -1;

        /// <summary>Wires the visual feedback (idle/pressed tints of the button image).</summary>
        public void Configure(Image background, Color idleColor, Color pressedColor)
        {
            _background = background;
            _idleColor = idleColor;
            _pressedColor = pressedColor;
            ApplyColor(_idleColor);
        }

        /// <inheritdoc />
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData == null || PointerId >= 0)
            {
                return; // ignore extra fingers on the same button
            }

            PointerId = eventData.pointerId;
            PressCount++;
            ApplyColor(_pressedColor);
            Pressed?.Invoke();
        }

        /// <inheritdoc />
        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData == null || eventData.pointerId != PointerId)
            {
                return;
            }

            Reset();
        }

        /// <summary>Clears the held state and the pressed tint (idempotent).</summary>
        public void Reset()
        {
            PointerId = -1;
            ApplyColor(_idleColor);
        }

        private void OnDisable()
        {
            Reset();
        }

        private void ApplyColor(Color color)
        {
            if (_background != null)
            {
                _background.color = color;
            }
        }
    }
}
