using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Fuel tank module of the assembly system (TASK-A-TECH-05): the tank's
    /// fuel is REAL mass — while a burn is running the remaining fuel is
    /// subtracted from the host <see cref="Rigidbody.mass"/> every step, so
    /// the same thrust produces a growing acceleration (F/m with a shrinking
    /// m). Drives exactly like <see cref="AssemblyThruster"/>: FixedUpdate in
    /// normal play, explicit <see cref="Tick"/> inside manual simulation
    /// harnesses (tests, benches) so the burn stays deterministic.
    ///
    /// The fuel state resolves lazily (uninitialised = full tank) so the
    /// module behaves identically with or without Awake having run — the
    /// factory calls <see cref="SetFuel"/> to materialise the initial load.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class AssemblyFuelTank : MonoBehaviour
    {
        [SerializeField] private float _fuelCapacityKg = 2f;
        [SerializeField] private float _dryMassKg = 1f;
        [SerializeField] private float _burnRateKgPerSecond = 0.5f;

        private Rigidbody _body;
        private float _fuelKg = UninitialisedFuel;
        private bool _burning;

        private const float UninitialisedFuel = -1f;

        /// <summary>Initial (full) fuel load, in kg.</summary>
        public float CapacityKg => _fuelCapacityKg;

        /// <summary>Hull mass without fuel, in kg.</summary>
        public float DryMassKg => _dryMassKg;

        /// <summary>Fuel consumption while burning, in kg/s.</summary>
        public float BurnRateKgPerSecond => _burnRateKgPerSecond;

        /// <summary>Fuel remaining, in kg (uninitialised state reads as full).</summary>
        public float FuelKg => _fuelKg >= 0f ? _fuelKg : _fuelCapacityKg;

        /// <summary>True while a burn is running.</summary>
        public bool IsBurning => _burning;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
        }

        /// <summary>Starts (or resumes) the burn.</summary>
        public void StartBurn()
        {
            _burning = true;
        }

        /// <summary>Stops the burn immediately (fuel is kept).</summary>
        public void StopBurn()
        {
            _burning = false;
        }

        /// <summary>
        /// Sets the fuel load directly and re-applies body mass = dry + fuel.
        /// Clamped to [0, capacity]. This also materialises the lazy state.
        /// </summary>
        public void SetFuel(float kg)
        {
            _fuelKg = Mathf.Clamp(kg, 0f, _fuelCapacityKg);
            ApplyMass();
        }

        /// <summary>
        /// Configures capacity / dry mass / burn rate in one call (factory and
        /// test configuration) — SetFuel clamps against the capacity, so the
        /// capacity must be set BEFORE the initial fuel load.
        /// </summary>
        public void Configure(float fuelCapacityKg, float dryMassKg, float burnRateKgPerSecond)
        {
            _fuelCapacityKg = Mathf.Max(0f, fuelCapacityKg);
            _dryMassKg = Mathf.Max(0f, dryMassKg);
            _burnRateKgPerSecond = Mathf.Max(0f, burnRateKgPerSecond);
        }

        /// <summary>
        /// One fixed step of the burn: consumes fuel and re-applies the host
        /// mass. Called by FixedUpdate in normal play; called explicitly by
        /// manual simulation harnesses (with the same delta they pass to
        /// Physics.Simulate).
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!_burning || FuelKg <= 0f)
            {
                return;
            }

            _fuelKg = Mathf.Max(0f, FuelKg - _burnRateKgPerSecond * deltaTime);
            if (_fuelKg <= 0f)
            {
                _burning = false; // dry: the burn self-extinguishes
            }

            ApplyMass();
        }

        private void FixedUpdate()
        {
            // Manual simulation harnesses drive Tick themselves; the engine-
            // side fixed update must not double-consume then.
            if (Physics.simulationMode == SimulationMode.FixedUpdate)
            {
                Tick(Time.fixedDeltaTime);
            }
        }

        private void ApplyMass()
        {
            if (_body == null)
            {
                _body = GetComponent<Rigidbody>();
            }

            if (_body != null)
            {
                _body.mass = _dryMassKg + FuelKg;
            }
        }
    }
}
