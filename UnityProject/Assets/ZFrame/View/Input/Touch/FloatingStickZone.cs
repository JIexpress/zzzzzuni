using UnityEngine;
using UnityEngine.EventSystems;

namespace ZFrame.View
{
    /// <summary>
    /// The invisible lower-half hit zone of the FLOATING stick
    /// (TASK-A-AND-10): a stretch rect over the bottom half of the safe area
    /// with a fully transparent <see cref="Image"/> so the GraphicRaycaster
    /// actually hits it. Every accepted press/drag/release is forwarded to the
    /// owning <see cref="TouchControlsView"/>, which re-anchors the stick base
    /// under the finger and drives <see cref="VirtualJoystick"/> through its
    /// normal public pointer handlers — the zone adds no input semantics of
    /// its own.
    ///
    /// SIBLING ORDER MATTERS: the zone is created BEFORE the two action
    /// buttons, so within the same canvas the buttons' graphics sort on top —
    /// a finger landing on 互动/暂停 never reaches the zone, while any other
    /// point of the lower half spawns the stick there. That split (zone left/
    /// bottom, buttons right) plus one-pointer ownership per widget is what
    /// makes "left thumb on the stick + right thumb on a button" work.
    ///
    /// In the FIXED-stick mode the view deactivates this GameObject entirely:
    /// the small always-visible base becomes the hit target again (A-AND-04
    /// semantics, unchanged).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FloatingStickZone : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        /// <summary>GameObject name of the floating hit zone.</summary>
        public const string ZoneName = "FloatZone";

        /// <summary>The overlay that owns the stick; null = inert.</summary>
        public TouchControlsView Owner { get; set; }

        /// <inheritdoc />
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData == null || Owner == null)
            {
                return;
            }

            Owner.FloatPress(eventData);
        }

        /// <inheritdoc />
        public void OnDrag(PointerEventData eventData)
        {
            if (eventData == null || Owner == null)
            {
                return;
            }

            Owner.FloatDrag(eventData);
        }

        /// <inheritdoc />
        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData == null || Owner == null)
            {
                return;
            }

            Owner.FloatRelease(eventData.pointerId);
        }
    }
}
