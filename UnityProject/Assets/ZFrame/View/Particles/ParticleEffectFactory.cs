using UnityEngine;
using UnityEngine.Rendering;

namespace ZFrame.View
{
    /// <summary>
    /// Builds the two particle systems (TASK-A-R9-05) entirely at runtime:
    /// no prefab, no material asset, no texture. Every knob lives here so the
    /// public seam (<see cref="ParticleRuntime"/>) stays about policy
    /// (on/off, pooling, shader fallback) and not about art numbers.
    ///
    /// Both systems are driven by the built-in ParticleSystem module, which is
    /// already in <c>manifest.json</c> (<c>com.unity.modules.particlesystem</c>).
    /// No VFX Graph, no new package.
    /// </summary>
    internal static class ParticleEffectFactory
    {
        /// <summary>Hard cap for one spark system (the pool is 4 of these).</summary>
        internal const int SparkMaxParticles = 64;

        /// <summary>Hard cap for the ambient dust system.</summary>
        internal const int AmbientMaxParticles = 200;

        /// <summary>Ambient emission rate; the brief allows at most 20/s.</summary>
        internal const float AmbientRatePerSecond = 12f;

        /// <summary>
        /// Spark gradient: white-hot -> orange -> dark ember, alpha collapsing.
        /// "颜色从亮到暗" in the brief is exactly this curve.
        /// </summary>
        private static Gradient SparkGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.97f, 0.85f), 0f),
                    new GradientColorKey(new Color(1f, 0.62f, 0.16f), 0.45f),
                    new GradientColorKey(new Color(0.42f, 0.10f, 0.02f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.92f, 0.2f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        /// <summary>Dust: fades in, holds, fades out - never fully opaque.</summary>
        private static Gradient DustGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.78f, 0.85f, 0.98f), 0f),
                    new GradientColorKey(new Color(0.78f, 0.85f, 0.98f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.5f, 0.25f),
                    new GradientAlphaKey(0.5f, 0.7f),
                    new GradientAlphaKey(0f, 1f),
                });
            return gradient;
        }

        /// <summary>
        /// One burst emitter. Looping with a zero rate: particles only ever come
        /// from explicit <c>Emit</c> calls, so the system can idle forever and
        /// still simulate every burst without a per-frame script.
        /// </summary>
        internal static ParticleSystem CreateSpark(string name, Transform parent, Shader shader, Color tint)
        {
            GameObject go = NewSystemObject(name, parent);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.45f); // <= 0.5 s, brief
            main.startSpeed = new ParticleSystem.MinMaxCurve(3.2f, 6.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.12f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = Color.white;
            main.gravityModifier = 1.1f; // sparks fall
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = SparkMaxParticles;
            main.stopAction = ParticleSystemStopAction.None;
            ps.useAutoRandomSeed = false;
            ps.randomSeed = 20261030u; // deterministic bursts: the pixel gate is reproducible

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f; // bursts only

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.02f;
            shape.radiusThickness = 1f; // emit anywhere inside, not just the shell

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(SparkGradient());

            // Velocity decay on top of gravity ("有重力/衰减").
            ParticleSystem.LimitVelocityOverLifetimeModule limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = new ParticleSystem.MinMaxCurve(2.0f);
            limit.dampen = 0.55f;

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, ShrinkCurve());

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sharedMaterial = CreateBlendMaterial(shader, tint, "~Particles/Spark");
            renderer.sortingFudge = 2f;

            return ps;
        }

        /// <summary>
        /// Camera-anchored dust volume. The transform is parented to the anchor
        /// camera (see <see cref="ParticleRuntime.SetAmbientAnchor"/>), so the
        /// volume follows the viewer with zero per-frame script; particles
        /// simulate in world space, so they stay put once emitted ("环境浮尘").
        /// </summary>
        internal static ParticleSystem CreateDust(string name, Transform parent, Shader shader, Color tint)
        {
            GameObject go = NewSystemObject(name, parent);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = ps.main;
            main.duration = 10f;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.09f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.085f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = Color.white;
            main.gravityModifier = -0.004f; // a hair of upward drift, dust never falls
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = AmbientMaxParticles;
            ps.useAutoRandomSeed = true;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = AmbientRatePerSecond;

            // 7 x 3.5 x 4 m box in FRONT of the camera (1.5 .. 5.5 m). The near
            // end starts well past the lens: a mote emitted 20 cm from the
            // camera is a screen-filling smudge, not dust.
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(7f, 3.5f, 4f);
            shape.position = new Vector3(0f, 0f, 3.5f);

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(DustGradient());

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sharedMaterial = CreateBlendMaterial(shader, tint, "~Particles/Dust");
            renderer.sortingFudge = 4f;

            return ps;
        }

        /// <summary>1.0 -> 0.15 shrink so the spark reads as "burning out".</summary>
        private static AnimationCurve ShrinkCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.6f, 0.55f),
                new Keyframe(1f, 0.15f));
            return curve;
        }

        private static GameObject NewSystemObject(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            // Configure before the system ever simulates: an inactive object
            // never runs playOnAwake, so nothing is emitted mid-setup.
            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// Transparent, double-sided, ZWrite-off material over whichever particle
        /// shader resolution survived (see <see cref="ParticleRuntime"/>). The
        /// float properties are the URP convention; they are set only when the
        /// shader actually declares them, so the legacy fallbacks keep working.
        /// </summary>
        private static Material CreateBlendMaterial(Shader shader, Color tint, string name)
        {
            var material = new Material(shader) { name = name };

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", tint);
            }

            if (material.HasProperty("_TintColor"))
            {
                material.SetColor("_TintColor", tint);
            }

            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f); // transparent
                material.SetFloat("_Blend", 0f); // alpha
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
                material.SetFloat("_Cull", (float)CullMode.Off);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
            }

            return material;
        }
    }
}
