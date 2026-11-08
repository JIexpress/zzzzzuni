using System;
using System.Collections;
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
    ///
    /// TASK-A-AND-10 (打磨不重写): adds the press SCALE feedback on top of the
    /// A-AND-04 tint — a minimal exponential tween towards 0.9 while held and
    /// back to 1 on release (zero assets, no tween library). Outside play mode
    /// (EditMode tests) the scale snaps instantly instead of tweening, because
    /// Update/coroutines are not driven there.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TouchActionButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        /// <summary>Local scale while the finger is down (TASK-A-AND-10 visual feedback).</summary>
        public const float PressedScale = 0.9f;

        /// <summary>Exponential approach speed of the scale tween (per second).</summary>
        public const float TweenSpeed = 14f;

        private Image _background;
        private Color _idleColor = Color.white;
        private Color _pressedColor = Color.white;
        private Coroutine _scaleTween;

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
            StartScaleTween(PressedScale);
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

        /// <summary>Clears the held state, the pressed tint and the press scale (idempotent).</summary>
        public void Reset()
        {
            PointerId = -1;
            ApplyColor(_idleColor);
            StartScaleTween(1f);
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

        private void StartScaleTween(float targetScale)
        {
            if (_background == null)
            {
                return;
            }

            Vector3 target = Vector3.one * targetScale;
            if (!Application.isPlaying || !isActiveAndEnabled)
            {
                // EditMode (tests, editor tooling) or an inactive widget
                // (teardown): no coroutine driver — snap.
                StopScaleTween();
                _background.rectTransform.localScale = target;
                return;
            }

            StopScaleTween();
            _scaleTween = StartCoroutine(TweenScale(target));
        }

        private void StopScaleTween()
        {
            if (_scaleTween != null)
            {
                StopCoroutine(_scaleTween);
                _scaleTween = null;
            }
        }

        private IEnumerator TweenScale(Vector3 target)
        {
            RectTransform rect = _background.rectTransform;
            while ((rect.localScale - target).sqrMagnitude > 1e-6f)
            {
                float step = Mathf.Clamp01(TweenSpeed * Time.unscaledDeltaTime);
                rect.localScale = Vector3.Lerp(rect.localScale, target, step);
                yield return null;
            }

            rect.localScale = target;
            _scaleTween = null;
        }
    }
}
