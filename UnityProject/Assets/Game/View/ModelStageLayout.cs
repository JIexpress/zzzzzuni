using System.Collections.Generic;
using UnityEngine;

namespace Game.View
{
    /// <summary>
    /// TASK-A-R9-10: the placement plan for the ten real models
    /// (<c>Assets/Art/Models/</c>) in <c>SCN_Game</c> — 4 characters standing
    /// in a ring around the player activity area, 6 environment props as
    /// dressing (trees on the ground edges, rock / barrel / fence / road as
    /// accents), and the pickup parked next to one character so the R9-06
    /// pickup→spark chain blooms beside a real model.
    ///
    /// Pure data + math on purpose: no UnityEditor, no scene access —
    /// <c>Game.View.Editor.GameSceneSetup</c> consumes it to build the scene
    /// and <c>Game.View.Tests.ModelStageLayoutTests</c> pins it in EditMode,
    /// so the plan has exactly one source of truth.
    ///
    /// Conventions
    /// -----------
    /// * Ring positions are (radius·sin θ, 0, radius·cos θ) — θ measured from
    ///   +Z towards +X — and the face-center yaw is θ + 180° plus a
    ///   per-model <see cref="ForwardYawOffset"/> that calibrates where the
    ///   FBX's own "forward" points after the Blender export.
    /// * Every placement carries a target size in metres; the scene builder
    ///   divides the measured original height by it. Sizes recorded in the
    ///   task report (original FBX bounds → applied scale).
    /// * Angles/positions dodge the existing M4 slice content: the door at
    ///   (0, 1, 3.2), the NPC at (-2.2, 1, 2.2) and the enemy patrol corridor
    ///   x ∈ [0.5, 3] / z ∈ [3, 6] stay walkable and visible.
    /// </summary>
    public static class ModelStageLayout
    {
        /// <summary>Ring radius for the four characters (metres from origin).</summary>
        public const float CharacterRingRadius = 4.5f;

        /// <summary>The pickup sits inside this distance of a character.</summary>
        public const float PickupNeighborRadius = 1.5f;

        /// <summary>Half-size of the ground plane (Ground localScale 2 → 20 m).</summary>
        public const float GroundHalfExtent = 10f;

        /// <summary>One model placement. Model = FBX stem under Assets/Art/Models/.</summary>
        public sealed class Placement
        {
            /// <summary>FBX file stem, e.g. "CHR_Robot" (same string the MAT_ plan uses).</summary>
            public readonly string Model;

            /// <summary>World position of the instance root (y = 0 pre-settle).</summary>
            public readonly Vector3 Position;

            /// <summary>Yaw in degrees (face-center yaw for characters).</summary>
            public readonly float YawDeg;

            /// <summary>Desired world height in metres (0 when <see cref="TargetLength"/> drives).</summary>
            public readonly float TargetHeight;

            /// <summary>
            /// Desired world size on the model's long axis (used for the flat
            /// road strip; 0 when <see cref="TargetHeight"/> drives).
            /// </summary>
            public readonly float TargetLength;

            /// <summary>True when the builder should shift the instance so its renderers rest on y=0.</summary>
            public readonly bool SitOnGround;

            /// <summary>
            /// True for ground-inset flat props (the road strip): the builder
            /// sinks the instance so only ~2 cm of its top stays above y=0
            /// (the road mesh is 0.5 m thick; sitting on it would wall the
            /// player area off).
            /// </summary>
            public readonly bool SinkTopAboveGround;

            /// <summary>Creates a placement.</summary>
            public Placement(string model, Vector3 position, float yawDeg,
                float targetHeight, float targetLength = 0f, bool sitOnGround = true,
                bool sinkTopAboveGround = false)
            {
                Model = model;
                Position = position;
                YawDeg = yawDeg;
                TargetHeight = targetHeight;
                TargetLength = targetLength;
                SitOnGround = sitOnGround;
                SinkTopAboveGround = sinkTopAboveGround;
            }
        }

        // ---- characters: ring around the activity area, facing the centre ----

        /// <summary>The four characters (one per FBX), standing on the ring.</summary>
        public static readonly Placement[] Characters =
        {
            MakeRing("CHR_Kenney_CharacterMedium", 30f, 1.75f), // front-right, beside the pickup
            MakeRing("CHR_Robot", 110f, 1.70f),                 // east
            MakeRing("CHR_Human", 200f, 1.80f),                 // back
            MakeRing("CHR_DarkElf", 290f, 1.90f),               // west, facing the NPC side
        };

        // ---- props: edges and accents ----------------------------------------

        /// <summary>Environment props (both tree FBX used twice).</summary>
        public static readonly Placement[] Props =
        {
            new Placement("ENV_Tree_01", new Vector3(8.6f, 0f, 4.2f), 20f, 4.6f),
            new Placement("ENV_Tree_02", new Vector3(-8.2f, 0f, 6.8f), 140f, 5.2f),
            new Placement("ENV_Tree_01", new Vector3(-7.4f, 0f, -7.2f), 80f, 4.2f),
            new Placement("ENV_Tree_02", new Vector3(7.8f, 0f, -8.4f), 250f, 4.8f),
            new Placement("ENV_Rock", new Vector3(5.8f, 0f, -2.6f), 15f, 1.0f),
            new Placement("ENV_Barrel", new Vector3(3.2f, 0f, 2.9f), 35f, 0.95f), // beside the Kenney character
            new Placement("ENV_Fence", new Vector3(1.2f, 0f, 8.7f), 0f, 1.1f),
            new Placement("ENV_Fence", new Vector3(3.4f, 0f, 8.7f), 0f, 1.1f),
            // The road's long axis is X natively; yaw 90 runs it along Z behind
            // the player start, and SinkTopAboveGround keeps its 0.5 m-thick
            // cobble body from walling the area off (only the top shows).
            new Placement("ENV_Road", new Vector3(0f, 0f, -5.8f), 90f, 0f, 9f, true, true),
        };

        /// <summary>
        /// The pickup's world position: beside the Kenney character, clear of
        /// the door footprint (door spans x ∈ [-0.7, 0.7] at z = 3.2) so the
        /// M4 interaction ray still meets the door unobstructed.
        /// </summary>
        public static readonly Vector3 PickupPosition = new Vector3(1.45f, 0.4f, 2.95f);

        /// <summary>All ten placements (characters then props).</summary>
        public static IEnumerable<Placement> All
        {
            get
            {
                foreach (Placement character in Characters)
                {
                    yield return character;
                }

                foreach (Placement prop in Props)
                {
                    yield return prop;
                }
            }
        }

        /// <summary>
        /// Ring position helper: (r·sin θ, 0, r·cos θ) with the yaw that points
        /// the model's calibrated forward at the origin (θ + 180° + offset).
        /// </summary>
        public static Placement MakeRing(string model, float angleDeg, float targetHeight)
        {
            float radians = angleDeg * Mathf.Deg2Rad;
            var position = new Vector3(
                CharacterRingRadius * Mathf.Sin(radians), 0f, CharacterRingRadius * Mathf.Cos(radians));
            return new Placement(model, position, angleDeg + 180f, targetHeight);
        }

        /// <summary>
        /// Per-model forward calibration (degrees): added to the placement yaw
        /// so the model visually faces where the plan says. CALIBRATED
        /// 2026-10-31 against the ring-centre facing shots
        /// (<c>Docs/Parity/demo_stage_face_*.png</c>): all four character FBX
        /// natively face -Z after the Blender export, so the face-center yaw
        /// (θ + 180°) needs NO extra offset — the first guess of -90° was
        /// proven wrong on pixels and reverted to 0 (see the task report's
        /// real-pitfall list). Props are rotation-symmetric enough not to care.
        /// </summary>
        public static float ForwardYawOffset(string model)
        {
            switch (model)
            {
                case "CHR_Kenney_CharacterMedium": return 0f;
                case "CHR_Robot": return 0f;
                case "CHR_Human": return 0f;
                case "CHR_DarkElf": return 0f;
                case "ENV_Tree_01": return 0f;
                case "ENV_Tree_02": return 0f;
                case "ENV_Barrel": return 0f;
                case "ENV_Fence": return 0f;
                case "ENV_Rock": return 0f;
                case "ENV_Road": return 0f;
                default: return 0f;
            }
        }

        /// <summary>
        /// Uniform scale that lifts an original height (metres at scale 1) to
        /// <paramref name="targetHeight"/>. Non-positive originals return 1
        /// (never divide by zero, never shrink a broken mesh away).
        /// </summary>
        public static float ScaleForHeight(float originalHeight, float targetHeight)
        {
            return originalHeight > 0f && targetHeight > 0f ? targetHeight / originalHeight : 1f;
        }

        /// <summary>Same contract for a long-axis size (the road strip).</summary>
        public static float ScaleForLength(float originalLength, float targetLength)
        {
            return originalLength > 0f && targetLength > 0f ? targetLength / originalLength : 1f;
        }
    }
}
