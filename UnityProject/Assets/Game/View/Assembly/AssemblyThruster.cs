using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Thruster module of the assembly slice (TASK-A-TECH-02): burns for a
    /// limited duration and pushes its rigid body along transform.forward
    /// (+Z — the exhaust points -Z). Force is applied at the body centre so
    /// the slice stays torque-free.
    ///
    /// The per-step work is factored into <see cref="Tick"/> so that a manual
    /// physics harness (Physics.Simulate loop in PlayMode tests / benches)
    /// can drive the burn deterministically; FixedUpdate delegates to it and
    /// is skipped while physics is stepped manually.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class AssemblyThruster : MonoBehaviour
    {
        [SerializeField] private float _thrustNewtons = 6f;
        [SerializeField] private float _defaultBurnSeconds = 2f;

        private Rigidbody _body;
        private float _remainingBurn;

        /// <summary>Thrust magnitude in newtons.</summary>
        public float ThrustNewtons => _thrustNewtons;

        /// <summary>Sets the thrust magnitude (factory/test configuration).</summary>
        public void SetThrust(float newtons)
        {
            _thrustNewtons = Mathf.Max(0f, newtons);
        }

        /// <summary>World-space thrust direction (the module's +Z).</summary>
        public Vector3 ThrustDirection => transform.forward;

        /// <summary>True while the burn lasts.</summary>
        public bool IsBurning => _remainingBurn > 0f;

        /// <summary>Burn time left, in seconds.</summary>
        public float RemainingBurn => Mathf.Max(0f, _remainingBurn);

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
        }

        /// <summary>Starts (or extends) a burn; <paramref name="seconds"/> null = default.</summary>
        public void Fire(float? seconds = null)
        {
            _remainingBurn = seconds ?? _defaultBurnSeconds;
        }

        /// <summary>Stops the burn immediately.</summary>
        public void StopBurn()
        {
            _remainingBurn = 0f;
        }

        /// <summary>
        /// One fixed step of the burn: applies the thrust force while burning.
        /// Called by FixedUpdate in normal play; called explicitly by manual
        /// simulation harnesses (with the same delta they pass to
        /// Physics.Simulate).
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (_remainingBurn <= 0f || _body == null)
            {
                return;
            }

            _remainingBurn = Mathf.Max(0f, _remainingBurn - deltaTime);
            _body.AddForce(ThrustDirection * _thrustNewtons, ForceMode.Force);
        }

        private void FixedUpdate()
        {
            // Manual simulation harnesses (tests, benches) drive Tick
            // themselves alongside Physics.Simulate; the engine-side fixed
            // update must not double-burn then.
            if (Physics.simulationMode == SimulationMode.FixedUpdate)
            {
                Tick(Time.fixedDeltaTime);
            }
        }
    }
}
