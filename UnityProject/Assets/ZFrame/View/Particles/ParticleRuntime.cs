using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Runtime-created particle presentation (TASK-A-R9-05): a pooled hit spark
    /// (effect A) and a camera-anchored ambient dust volume (effect B).
    ///
    /// Contract
    /// --------
    /// * Zero assets: no .prefab / .mat / .vfx / texture. Materials come from
    ///   <c>new Material(Shader.Find(...))</c> at runtime; the repository only
    ///   gains .cs files.
    /// * Zero new packages (built-in ParticleSystem module only).
    /// * Bounded: <c>particleCount &lt;= maxParticles</c> by construction
    ///   (sparks 64 per pooled system, dust 200) and every burst is clamped to
    ///   <see cref="MaxSparkBurst"/>.
    /// * <see cref="Enabled"/> is the single switch. Turning it off stops and
    ///   clears every system, deactivates their GameObjects and returns before
    ///   touching anything, so a disabled runtime costs no particles and no
    ///   per-frame script work (this type has no Update at all: the dust volume
    ///   follows its anchor camera by parenting, not by polling).
    ///
    /// ⚠ The Player-only trap this type exists to survive
    /// -------------------------------------------------
    /// <c>Shader.Find</c> returns <c>null</c> in a player build when nothing
    /// references the shader asset — URP's particle shaders are exactly that
    /// case in a project with zero .mat files ("works in the editor, black in
    /// the build"). So the lookup walks a candidate list
    /// (<see cref="DefaultShaderCandidates"/>), prefers the URP particle shader,
    /// and falls back to built-in shaders that are in
    /// <c>GraphicsSettings.m_AlwaysIncludedShaders</c>. A candidate only counts
    /// when the shader is found, supported on this device AND still has passes
    /// (a partially stripped shader has zero). If every candidate fails the
    /// runtime does not throw: it warns exactly once, sets
    /// <see cref="SelfDisabled"/> and every public entry point becomes a no-op.
    /// </summary>
    public static class ParticleRuntime
    {
        /// <summary>ZLog channel for the one-shot warnings.</summary>
        public const string LogChannel = "Particles";

        /// <summary>Upper bound for one <see cref="SpawnHitSpark"/> call.</summary>
        public const int MaxSparkBurst = 48;

        /// <summary>Pool size: at most this many spark ParticleSystem instances ever exist.</summary>
        public const int SparkPoolSize = 4;

        /// <summary>Hard per-system cap for sparks (mirrors the factory).</summary>
        public const int SparkMaxParticles = ParticleEffectFactory.SparkMaxParticles;

        /// <summary>Hard cap for the ambient dust system (mirrors the factory).</summary>
        public const int AmbientMaxParticles = ParticleEffectFactory.AmbientMaxParticles;

        /// <summary>
        /// Ordered shader candidates. Order = "best looking first, most likely
        /// to survive the build last". The last three are built-in shaders that
        /// this project already ships in Always Included Shaders, so they are
        /// the ones that cannot be stripped.
        /// </summary>
        private static readonly string[] DefaultShaderCandidates =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Universal Render Pipeline/Particles/Lit",
            "Legacy Shaders/Particles/Alpha Blended",
            "Sprites/Default",
        };

        private static string[] _candidateOverride;
        private static Shader _particleShader;
        private static string _resolvedShaderName = string.Empty;
        private static string _shaderProbeReport = "not probed yet";
        private static bool _shaderProbed;
        private static bool _selfDisabled;
        private static int _warningCount;

        private static bool _enabled = true;
        private static bool _ambientRequested = true;
        private static bool _hooked;
        private static Transform _root;
        private static Transform _sparkRoot;
        private static Transform _dustRoot;
        private static readonly ParticleSystem[] Sparks = new ParticleSystem[SparkPoolSize];
        private static int _sparkCreated;
        private static int _sparkCursor;
        private static ParticleSystem _dust;
        private static Camera _anchor;

        /// <summary>Shipped default: particles on.</summary>
        public static bool Enabled => _enabled;

        /// <summary>True when a usable particle shader was resolved.</summary>
        public static bool IsShaderAvailable => _particleShader != null && !_selfDisabled;

        /// <summary>True when every shader candidate failed (particles are inert).</summary>
        public static bool SelfDisabled => _selfDisabled;

        /// <summary>Name of the shader that won, or empty when none did.</summary>
        public static string ResolvedShaderName => _resolvedShaderName;

        /// <summary>"candidate=result" list from the last resolution pass (evidence).</summary>
        public static string ShaderProbeReport => _shaderProbeReport;

        /// <summary>How many one-shot warnings this runtime has emitted (must stay 0 or 1).</summary>
        public static int WarningCount => _warningCount;

        /// <summary>Spark systems instantiated so far (pooled, so &lt;= <see cref="SparkPoolSize"/>).</summary>
        public static int SparkSystemCount => _sparkCreated;

        /// <summary>1 once the dust system exists, else 0.</summary>
        public static int AmbientSystemCount => _dust != null ? 1 : 0;

        /// <summary>Particles emitted by the most recent <see cref="SpawnHitSpark"/> (0 when disabled).</summary>
        public static int LastSpawnEmitted { get; private set; }

        /// <summary>Particles alive in the dust system.</summary>
        public static int AmbientParticleCount => _dust != null ? _dust.particleCount : 0;

        /// <summary>Particles alive across the whole spark pool.</summary>
        public static int HitSparkParticleCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _sparkCreated; i++)
                {
                    if (Sparks[i] != null)
                    {
                        total += Sparks[i].particleCount;
                    }
                }

                return total;
            }
        }

        /// <summary>Particles alive in every system this runtime owns.</summary>
        public static int TotalParticleCount => HitSparkParticleCount + AmbientParticleCount;

        /// <summary>True while the ambient dust volume is requested and able to draw.</summary>
        public static bool IsAmbientDustOn => _ambientRequested && _enabled && _dust != null && _dust.gameObject.activeSelf;

        /// <summary>The camera the dust volume is parented to (null = none).</summary>
        public static Camera AmbientAnchor => _anchor;

        /// <summary>
        /// Self-bootstrap after the first scene load so the dust volume can
        /// attach to that scene's Main Camera. Follows later scene loads too.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            Hook();
            if (_enabled)
            {
                ApplyAmbient();
            }
        }

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            _hooked = true;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // A new scene usually brings a new Main Camera; re-anchor the dust
            // volume to it (no per-frame search, this runs once per load).
            _anchor = null;
            if (_enabled && _ambientRequested)
            {
                ApplyAmbient();
            }
        }

        /// <summary>
        /// The single switch. Off = stop, clear and deactivate everything (no
        /// particles, no simulation cost); on = restore the dust volume and let
        /// <see cref="SpawnHitSpark"/> work again. Idempotent.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
            {
                ApplyEnabledState();
                return;
            }

            _enabled = enabled;
            ApplyEnabledState();
            ZLog.Info(LogChannel, enabled ? "particle runtime enabled" : "particle runtime disabled");
        }

        private static void ApplyEnabledState()
        {
            if (!_enabled)
            {
                StopAndClearAll();
                return;
            }

            if (_sparkRoot != null && !_sparkRoot.gameObject.activeSelf)
            {
                _sparkRoot.gameObject.SetActive(true);
                for (int i = 0; i < _sparkCreated; i++)
                {
                    if (Sparks[i] != null && !Sparks[i].isPlaying)
                    {
                        Sparks[i].Play();
                    }
                }
            }

            ApplyAmbient();
        }

        /// <summary>
        /// Effect A. Emits a short burst at <paramref name="worldPos"/> from a
        /// pooled system (never a new GameObject per hit) and returns the number
        /// of particles actually emitted.
        /// </summary>
        public static int SpawnHitSpark(Vector3 worldPos, int count = 12)
        {
            LastSpawnEmitted = 0;

            // Disabled: return before allocating, probing or touching Unity.
            if (!_enabled)
            {
                return 0;
            }

            if (!EnsureReady())
            {
                return 0;
            }

            EnsureSparkPool();

            ParticleSystem ps = NextSparkSystem();
            if (ps == null)
            {
                return 0;
            }

            int requested = Mathf.Clamp(count, 1, MaxSparkBurst);
            ps.transform.position = worldPos;
            ps.Clear(); // "回收": reuse the instance, drop whatever was left in it
            if (!ps.isPlaying)
            {
                ps.Play();
            }

            ps.Emit(requested);
            LastSpawnEmitted = requested;
            return requested;
        }

        /// <summary>Turns the ambient dust volume on/off without touching the switch.</summary>
        public static void SetAmbientDust(bool enabled)
        {
            _ambientRequested = enabled;
            ApplyAmbient();
        }

        /// <summary>
        /// Anchors the dust volume to <paramref name="camera"/> (null = resolve
        /// Camera.main on the next apply). Runtime-created cameras need this;
        /// scene cameras are picked up automatically after each scene load.
        /// </summary>
        public static void SetAmbientAnchor(Camera camera)
        {
            _anchor = camera;
            ApplyAmbient();
        }

        /// <summary>
        /// Diagnostic seam: replaces the shader candidate list (null restores the
        /// shipped one) and forgets the previous resolution. Used by the Player
        /// harness to prove the fallback chain and the all-fail path on a real
        /// build; production code never calls it.
        /// </summary>
        public static void SetShaderCandidates(params string[] candidates)
        {
            _candidateOverride = candidates == null || candidates.Length == 0 ? null : candidates;
            _shaderProbed = false;
            _particleShader = null;
            _resolvedShaderName = string.Empty;
            _shaderProbeReport = "not probed yet";
        }

        /// <summary>One-line state dump for logs (Player harness / tests).</summary>
        public static string DescribeState()
        {
            return "enabled=" + _enabled
                   + " ambientRequested=" + _ambientRequested
                   + " shader='" + _resolvedShaderName + "'"
                   + " selfDisabled=" + _selfDisabled
                   + " warnings=" + _warningCount
                   + " sparkSystems=" + _sparkCreated
                   + " dustSystems=" + AmbientSystemCount
                   + " sparkParticles=" + HitSparkParticleCount
                   + " dustParticles=" + AmbientParticleCount
                   + " anchor='" + (_anchor != null ? _anchor.name : "none") + "'";
        }

        /// <summary>
        /// Destroys every instance and returns to the shipped defaults. This is
        /// the lifecycle seam used on shutdown and by tests' TearDown; the
        /// resolved shader stays cached (shader availability cannot change).
        /// </summary>
        public static void Shutdown()
        {
            StopAndClearAll();

            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
            }

            _root = null;
            _sparkRoot = null;
            _dustRoot = null;
            _dust = null;
            _anchor = null;
            for (int i = 0; i < Sparks.Length; i++)
            {
                Sparks[i] = null;
            }

            _sparkCreated = 0;
            _sparkCursor = 0;
            _enabled = true;
            _ambientRequested = true;
            LastSpawnEmitted = 0;
        }

        // ---- shader resolution ---------------------------------------------

        /// <summary>
        /// Resolves a usable particle shader once. Returns false when every
        /// candidate failed (in which case the runtime is self-disabled and has
        /// already warned exactly once).
        /// </summary>
        private static bool ResolveShader()
        {
            if (_shaderProbed)
            {
                return _particleShader != null;
            }

            _shaderProbed = true;
            string[] candidates = _candidateOverride ?? DefaultShaderCandidates;
            // "isSupported" only means something when a graphics device exists.
            // A headless run (SystemInfo.graphicsDeviceType == Null) reports
            // every shader as unsupported, which would be a false alarm - there
            // the build's real signal is passCount (a stripped shader has none).
            bool hasGraphicsDevice = UnityEngine.SystemInfo.graphicsDeviceType
                                     != UnityEngine.Rendering.GraphicsDeviceType.Null;
            var report = new StringBuilder();
            for (int i = 0; i < candidates.Length; i++)
            {
                string name = candidates[i];
                Shader shader = string.IsNullOrEmpty(name) ? null : Shader.Find(name);
                string verdict;
                if (shader == null)
                {
                    verdict = "missing";
                }
                else if (hasGraphicsDevice && !shader.isSupported)
                {
                    verdict = "unsupported";
                }
                else if (hasGraphicsDevice && shader.passCount <= 0)
                {
                    verdict = "stripped(0 passes)";
                }
                else
                {
                    verdict = "ok(" + shader.passCount + " passes)";
                    _particleShader = shader;
                    _resolvedShaderName = name;
                }

                if (report.Length > 0)
                {
                    report.Append("; ");
                }

                report.Append(name).Append('=').Append(verdict);

                if (_particleShader != null)
                {
                    break;
                }
            }

            _shaderProbeReport = report.ToString();

            if (_particleShader == null)
            {
                _selfDisabled = true;
                WarnOnce("no usable particle shader for this build (all candidates failed: "
                         + _shaderProbeReport + "). Particle effects are disabled in this session; "
                         + "this warning is emitted once and not retried.");
                return false;
            }

            ZLog.Info(LogChannel, "particle shader resolved: '" + _resolvedShaderName + "' (" + _shaderProbeReport + ")");
            return true;
        }

        private static void WarnOnce(string message)
        {
            if (_warningCount > 0)
            {
                return;
            }

            _warningCount++;
            ZLog.Warn(LogChannel, message);
        }

        // ---- construction ---------------------------------------------------

        private static bool EnsureReady()
        {
            if (_selfDisabled)
            {
                return false;
            }

            if (!ResolveShader())
            {
                return false;
            }

            if (_root == null)
            {
                var go = new GameObject("~ParticleRuntime");
                Object.DontDestroyOnLoad(go);
                _root = go.transform;
                Hook();
            }

            return true;
        }

        private static void EnsureSparkPool()
        {
            if (_sparkRoot == null)
            {
                var go = new GameObject("Sparks");
                go.transform.SetParent(_root, false);
                _sparkRoot = go.transform;
            }

            while (_sparkCreated < SparkPoolSize)
            {
                ParticleSystem ps = ParticleEffectFactory.CreateSpark(
                    "Spark" + _sparkCreated, _sparkRoot, _particleShader, new Color(1f, 0.96f, 0.9f, 1f));
                ps.gameObject.SetActive(true);
                ps.Play();
                Sparks[_sparkCreated] = ps;
                _sparkCreated++;
            }

            if (!_sparkRoot.gameObject.activeSelf)
            {
                _sparkRoot.gameObject.SetActive(true);
            }
        }

        private static ParticleSystem NextSparkSystem()
        {
            // Prefer an idle system so a fresh burst does not cut an older one
            // short; otherwise round-robin (bounded, never allocates).
            for (int i = 0; i < _sparkCreated; i++)
            {
                int index = (_sparkCursor + i) % _sparkCreated;
                ParticleSystem candidate = Sparks[index];
                if (candidate != null && candidate.particleCount == 0)
                {
                    _sparkCursor = (index + 1) % _sparkCreated;
                    return candidate;
                }
            }

            ParticleSystem fallback = Sparks[_sparkCursor];
            _sparkCursor = (_sparkCursor + 1) % _sparkCreated;
            return fallback;
        }

        // ---- ambient dust ---------------------------------------------------

        private static void ApplyAmbient()
        {
            if (!_enabled || !_ambientRequested || _selfDisabled || !ResolveShader())
            {
                ParkDust();
                return;
            }

            if (_anchor == null)
            {
                _anchor = Camera.main;
            }

            if (_anchor == null)
            {
                ParkDust();
                return;
            }

            if (_root == null && !EnsureReady())
            {
                return;
            }

            if (_dust == null)
            {
                if (_dustRoot == null)
                {
                    var go = new GameObject("Dust");
                    go.transform.SetParent(_root, false);
                    _dustRoot = go.transform;
                }

                _dust = ParticleEffectFactory.CreateDust(
                    "AmbientDust", _dustRoot, _particleShader, new Color(0.8f, 0.87f, 1f, 1f));
            }

            // Parenting (not polling) is what makes the volume follow the
            // camera: no Update, no per-frame FindObjectOfType.
            if (_dust.transform.parent != _anchor.transform)
            {
                _dust.transform.SetParent(_anchor.transform, false);
            }

            _dust.transform.localPosition = Vector3.zero;
            if (!_dust.gameObject.activeSelf)
            {
                _dust.gameObject.SetActive(true);
            }

            if (!_dust.isPlaying)
            {
                _dust.Play();
            }
        }

        /// <summary>Stops the dust system without destroying it (cheap to resume).</summary>
        private static void ParkDust()
        {
            if (_dust == null)
            {
                return;
            }

            _dust.Clear();
            if (_dust.gameObject.activeSelf)
            {
                _dust.gameObject.SetActive(false);
            }
        }

        private static void StopAndClearAll()
        {
            for (int i = 0; i < _sparkCreated; i++)
            {
                if (Sparks[i] != null)
                {
                    Sparks[i].Clear();
                    Sparks[i].gameObject.SetActive(false);
                }
            }

            if (_sparkRoot != null && _sparkRoot.gameObject.activeSelf)
            {
                _sparkRoot.gameObject.SetActive(false);
            }

            if (_dust != null)
            {
                _dust.Clear();
                if (_dust.gameObject.activeSelf)
                {
                    _dust.gameObject.SetActive(false);
                }
            }

            if (_dustRoot != null && _dustRoot.gameObject.activeSelf)
            {
                _dustRoot.gameObject.SetActive(false);
            }
        }
    }
}
