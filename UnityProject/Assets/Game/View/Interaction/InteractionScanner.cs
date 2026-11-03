using System;
using System.Collections.Generic;
using Game.Domain.Interaction;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.View.Interaction
{
    /// <summary>
    /// Aiming front-end of the interaction slice (TASK-A-M4-01): casts a short
    /// ray, adapts every hit to an <see cref="InteractableProxy"/>, lets the
    /// DOMAIN pick the winner (<see cref="InteractionRegistry.PickNearest"/>)
    /// and pushes it into the shared registry. It also listens for
    /// <see cref="InteractInputEvent"/> (published by the View input bridge)
    /// and activates the current target.
    ///
    /// Ray geometry: the origin is <see cref="_rayOrigin"/> when set (the
    /// scene uses the player's chest so a 2 m reach actually reaches props in
    /// a third-person setup) and the camera otherwise; the direction is always
    /// the camera forward, i.e. "what the player is looking at".
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionScanner : MonoBehaviour
    {
        [SerializeField] private Camera _camera;
        [SerializeField] private Transform _rayOrigin;
        [SerializeField] private float _maxDistance = 2f;
        [SerializeField] private LayerMask _mask = ~0;

        private readonly List<InteractionCandidate> _candidates = new List<InteractionCandidate>();
        private readonly List<InteractableProxy> _proxies = new List<InteractableProxy>();
        private InteractionWorld _world;
        private IDisposable _interactToken;

        /// <summary>Most recently enabled scanner (single-player slice).</summary>
        public static InteractionScanner Instance { get; private set; }

        /// <summary>Proxy that owns the current aim target, or null.</summary>
        public InteractableProxy CurrentProxy { get; private set; }

        /// <summary>Registry/inventory pair this scanner feeds.</summary>
        public InteractionWorld World => _world;

        /// <summary>Reach of the aim ray, in metres.</summary>
        public float MaxDistance => _maxDistance;

        private void Awake()
        {
            Instance = this;
            _world = InteractionWorld.Shared;
            if (_camera == null)
            {
                _camera = Camera.main;
            }
        }

        private void OnEnable()
        {
            _interactToken = EventBus.Subscribe<InteractInputEvent>(OnInteractPressed);
        }

        private void OnDisable()
        {
            _interactToken?.Dispose();
            _interactToken = null;
            _world?.Registry.ClearNearest();
            CurrentProxy = null;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            Scan();
        }

        private void Scan()
        {
            if (_camera == null || _world == null)
            {
                return;
            }

            Transform origin = _rayOrigin != null ? _rayOrigin : _camera.transform;
            var ray = new Ray(origin.position, _camera.transform.forward);
            RaycastHit[] hits = Physics.RaycastAll(ray, _maxDistance, _mask, QueryTriggerInteraction.Collide);

            _candidates.Clear();
            _proxies.Clear();
            for (int i = 0; i < hits.Length; i++)
            {
                var proxy = hits[i].collider.GetComponentInParent<InteractableProxy>();
                if (proxy == null || _proxies.Contains(proxy))
                {
                    continue; // no adapter, or the same object hit twice through child colliders
                }

                _proxies.Add(proxy);
                _candidates.Add(new InteractionCandidate(proxy.Target, hits[i].distance));
            }

            IInteractable best = InteractionRegistry.PickNearest(_candidates, _maxDistance);
            CurrentProxy = FindProxy(best);
            _world.Registry.SetNearest(best);
        }

        private InteractableProxy FindProxy(IInteractable target)
        {
            if (target == null)
            {
                return null;
            }

            for (int i = 0; i < _proxies.Count; i++)
            {
                if (ReferenceEquals(_proxies[i].Target, target))
                {
                    return _proxies[i];
                }
            }

            return null;
        }

        private void OnInteractPressed(InteractInputEvent evt)
        {
            _world?.Interact();
        }
    }
}
