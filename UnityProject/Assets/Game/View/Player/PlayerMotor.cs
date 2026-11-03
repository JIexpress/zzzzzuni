using System;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.View.Player
{
    /// <summary>
    /// Minimal camera-relative player mover for the M4 slice (TASK-A-M4-01).
    /// It is the consumer of <see cref="MoveInputEvent"/> — without one the
    /// Move half of the input bridge would publish into the void — and does
    /// nothing else: no jumping, no rotation, no animation.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMotor : MonoBehaviour
    {
        [SerializeField] private float _speed = 3.5f;
        [SerializeField] private Transform _camera;
        [SerializeField] private float _gravity = 9.81f;

        private CharacterController _controller;
        private Vec2 _move;
        private float _verticalSpeed;
        private IDisposable _moveToken;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private float _nextMoveLogTime;
#endif

        /// <summary>Current input vector (screen space, dead-zoned by InputSystem).</summary>
        public Vec2 MoveInput => _move;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (_camera == null && Camera.main != null)
            {
                _camera = Camera.main.transform;
            }
        }

        private void OnEnable()
        {
            _moveToken = EventBus.Subscribe<MoveInputEvent>(OnMove);
        }

        private void OnDisable()
        {
            _moveToken?.Dispose();
            _moveToken = null;
            _move = Vec2.Zero;
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;

            Vector3 forward = _camera != null ? Vector3.ProjectOnPlane(_camera.forward, Vector3.up) : Vector3.forward;
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 direction = (forward * _move.Y) + (right * _move.X);
            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            _verticalSpeed = _controller.isGrounded ? 0f : _verticalSpeed - (_gravity * deltaTime);
            Vector3 delta = (direction * _speed * deltaTime) + (Vector3.up * _verticalSpeed * deltaTime);
            _controller.Move(delta);
        }

        private void OnMove(MoveInputEvent evt)
        {
            _move = evt.Value;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // TASK-A-AND-04 device diagnosis: proves whether the touch stick's
            // release (Vec2.Zero) really lands on the consumer.
            if (Time.unscaledTime >= _nextMoveLogTime)
            {
                _nextMoveLogTime = Time.unscaledTime + 0.25f;
                ZFrame.View.Diagnostics.DeviceFileLogger.Step(
                    $"[motor] move=({_move.X:F2},{_move.Y:F2}) at {Time.unscaledTime:F1}s");
            }
#endif
        }
    }
}
