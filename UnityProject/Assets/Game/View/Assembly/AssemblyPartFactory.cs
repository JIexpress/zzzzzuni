using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Procedural part factory for the assembly slice (TASK-A-TECH-02): the
    /// three demo part kinds built from primitives only — zero imported
    /// assets. Shared by the SCN_Assembly setup script and the PlayMode
    /// tests, so the scene and the test rigs use the same geometry, docking
    /// layout and materials.
    /// </summary>
    public static class AssemblyPartFactory
    {
        /// <summary>Half size of the standard 1 m cube block.</summary>
        public const float CubeHalfSize = 0.5f;

        /// <summary>
        /// A 1 m structural cube with two docking points on its +/-Z faces
        /// (outward normals). Total mass 1 kg.
        /// </summary>
        public static AssemblyPart CreateCube(string id, Vector3 position, Color? color = null)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            go.transform.position = position;

            AssemblyPart part = ConfigurePart(go, id, color ?? new Color(0.72f, 0.72f, 0.75f, 1f), mass: 1f);
            part.AddAttachmentPoint(AttachmentPointType.Structural,
                new Vector3(0f, 0f, CubeHalfSize), Vector3.forward);
            part.AddAttachmentPoint(AttachmentPointType.Structural,
                new Vector3(0f, 0f, -CubeHalfSize), Vector3.back);
            return part;
        }

        /// <summary>
        /// A thruster block (0.45 x 0.45 x 0.9, exhaust at -Z): thrust pushes
        /// along +Z, the mounting face sits at +Z with an outward +Z normal.
        /// Total mass 0.5 kg. Note the mount's local position is expressed in
        /// the SCALED local space (z scale 0.9): 0.5 x 0.9 = 0.45 m world.
        /// </summary>
        public static AssemblyPart CreateThruster(string id, Vector3 position,
            float thrustNewtons = 6f, Color? color = null)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = id;
            go.transform.position = position;
            go.transform.localScale = new Vector3(0.45f, 0.45f, 0.9f);

            AssemblyPart part = ConfigurePart(go, id, color ?? new Color(0.25f, 0.27f, 0.3f, 1f), mass: 0.5f);
            AssemblyThruster thruster = go.AddComponent<AssemblyThruster>();
            thruster.SetThrust(thrustNewtons);
            part.AddAttachmentPoint(AttachmentPointType.ThrusterMount,
                new Vector3(0f, 0f, 0.5f), Vector3.forward);
            return part;
        }

        /// <summary>
        /// A wheel: cylinder visual (r 0.4, thickness 0.3, axle along X) over
        /// a sphere collider, with one WheelHub point at the centre whose
        /// outward normal is the axle (+X in world at the default pose — the
        /// wheel body is z-rotated 90 deg, so the part-local direction is
        /// -Y). Mass 0.4 kg, damped so it settles instead of rolling away.
        /// </summary>
        public static AssemblyPart CreateWheel(string id, Vector3 position, Color? color = null)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = id;
            go.transform.position = position;
            go.transform.localScale = new Vector3(0.8f, 0.15f, 0.8f); // r=0.4, half-thickness 0.15
            go.transform.rotation = Quaternion.Euler(0f, 0f, 90f);    // axle: local Y -> world X

            // The cylinder primitive's collider does not match a rolling
            // wheel; a sphere collider keeps ground contact smooth. Destroy
            // must be immediate because the factory also runs in EDITOR
            // context (scene setup) where Destroy is not pumped.
            Collider primitive = go.GetComponent<Collider>();
            if (primitive != null)
            {
                DestroyObject(primitive);
            }

            SphereCollider sphere = go.AddComponent<SphereCollider>();
            sphere.radius = 0.5f; // local space: 0.5 * scale.x(0.8) = 0.4 m world

            AssemblyPart part = ConfigurePart(go, id, color ?? new Color(0.15f, 0.15f, 0.16f, 1f), mass: 0.4f);
            part.Body.angularDrag = 0.5f;
            part.AddAttachmentPoint(AttachmentPointType.WheelHub, Vector3.zero, Vector3.down);
            return part;
        }

        private static AssemblyPart ConfigurePart(GameObject go, string id, Color color, float mass)
        {
            var part = go.AddComponent<AssemblyPart>();
            part.name = go.name;
            Rigidbody body = part.Body;
            body.mass = mass;

            // Per-part material instance (the primitive default is shared):
            // deterministic base colour + independent snap highlighting.
            Shader shader = ResolveShader();
            var material = new Material(shader) { name = $"MAT_{id}" };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
            }

            return part;
        }

        private static Shader ResolveShader()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            return shader != null ? shader : Shader.Find("Sprites/Default");
        }

        private static void DestroyObject(Object target)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
