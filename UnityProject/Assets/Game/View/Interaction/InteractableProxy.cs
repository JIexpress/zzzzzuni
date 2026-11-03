using Game.Domain.Interaction;
using UnityEngine;
using ZFrame.Gameplay;

namespace Game.View.Interaction
{
    /// <summary>
    /// The one adapter between a scene object and the pure-C# interaction
    /// layer (TASK-A-M4-01): it builds the Domain template described by its
    /// serialized fields, registers it with the shared
    /// <see cref="InteractionRegistry"/> while enabled and unregisters it on
    /// disable/destroy — the dependency edge is View → Domain only, and the
    /// Domain never learns that a GameObject exists.
    ///
    /// The component is intentionally thin: no gameplay rules live here, so
    /// every rule stays unit-testable without a scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractableProxy : MonoBehaviour
    {
        [SerializeField] private InteractableKind _kind = InteractableKind.Door;
        [SerializeField] private string _id = "interactable";
        [SerializeField] private string _prompt = "按 E 交互";
        [SerializeField] private string _alternatePrompt = string.Empty;
        [SerializeField] private bool _locked;
        [SerializeField] private string _itemId = "gold";
        [SerializeField] private int _amount = 1;
        [SerializeField] private string _speaker = "NPC";
        [SerializeField] private string[] _lines = { "……" };

        private IInteractable _target;

        /// <summary>The Domain entity this object represents (built on first access).</summary>
        public IInteractable Target
        {
            get
            {
                EnsureTarget();
                return _target;
            }
        }

        /// <summary>Template kind (serialized; also readable by tools).</summary>
        public InteractableKind Kind => _kind;

        private void Awake()
        {
            EnsureTarget();
        }

        private void OnEnable()
        {
            InteractionWorld world = InteractionWorld.Shared;
            world.Registry.Register(Target);
        }

        private void OnDisable()
        {
            if (_target == null)
            {
                return;
            }

            InteractionWorld.Shared.Registry.Unregister(_target);
        }

        private void EnsureTarget()
        {
            if (_target != null)
            {
                return;
            }

            InteractionWorld world = InteractionWorld.Shared;
            // TASK-A-R9-06/R9-07: the View owns the world position — the Domain
            // entities only carry it (2D plane mapping identical to EnemyView).
            var planePosition = new Vec2(transform.position.x, transform.position.z);
            switch (_kind)
            {
                case InteractableKind.Door:
                    string closePrompt = string.IsNullOrEmpty(_alternatePrompt) ? _prompt : _alternatePrompt;
                    _target = new DoorEntity(_id, startsOpen: false, locked: _locked, openPrompt: _prompt,
                        closePrompt: closePrompt, position: planePosition);
                    break;

                case InteractableKind.Pickup:
                    _target = new PickupEntity(
                        _id,
                        _itemId,
                        _amount <= 0 ? 1 : _amount,
                        world.Inventory,
                        _prompt,
                        planePosition);
                    break;

                case InteractableKind.Dialog:
                    _target = new DialogEntity(_id, _lines, _speaker, _prompt, planePosition);
                    break;

                default:
                    Debug.LogError($"InteractableProxy: unknown kind '{_kind}' on '{name}'.", this);
                    break;
            }
        }
    }
}
