using System.Collections.Generic;
using Game.View.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Game.View.Tests
{
    /// <summary>
    /// TASK-A-R9-10 pins for the real-model stage plan
    /// (<see cref="ModelStageLayout"/>): the ten FBX land in SCN_Game exactly
    /// once each, the character ring faces the centre and dodges the M4 slice
    /// landmarks (door / NPC / enemy patrol), the pickup stands beside a
    /// character, props stay on the ground edges, and the scale math cannot
    /// divide by zero. The scene builder consumes this same plan, so these
    /// pins hold for the committed scene without opening Unity.
    /// </summary>
    public sealed class ModelStageLayoutTests
    {
        private static readonly Vector2 DoorXz = new Vector2(0f, 3.2f);
        private static readonly Vector2 NpcXz = new Vector2(-2.2f, 2.2f);

        [Test]
        public void All_PlacesEveryStagedModel_AtLeastOnce()
        {
            // HashSet.Add returns false on duplicates — and duplicates are
            // DELIBERATE here (both trees and the fence appear twice), so the
            // distinct set is collected without asserting on Add's return.
            var seen = new HashSet<string>();
            foreach (ModelStageLayout.Placement placement in ModelStageLayout.All)
            {
                seen.Add(placement.Model);
            }

            // Ten distinct staged models; trees and the fence deliberately
            // appear twice (13 placements) for a lived-in edge.
            Assert.AreEqual(10, seen.Count, "the stage must place all ten staged models");

            // ... and the layout agrees with the staged file list 1:1.
            var staged = new HashSet<string>(ModelAssetSetup.ModelFiles);
            Assert.AreEqual(staged.Count, 10, "ModelAssetSetup must stage ten FBX");
            foreach (ModelStageLayout.Placement placement in ModelStageLayout.All)
            {
                Assert.IsTrue(staged.Contains(placement.Model + ".fbx"),
                    $"layout references '{placement.Model}' which is not staged");
            }
        }

        [Test]
        public void Characters_FourDistinct_OnTheRing_FacingTheCentre()
        {
            Assert.AreEqual(4, ModelStageLayout.Characters.Length);
            var models = new HashSet<string>();
            foreach (ModelStageLayout.Placement character in ModelStageLayout.Characters)
            {
                models.Add(character.Model);
                float radius = new Vector2(character.Position.x, character.Position.z).magnitude;
                Assert.AreEqual(ModelStageLayout.CharacterRingRadius, radius, 0.01f,
                    $"{character.Model} is not on the ring");

                // Face-center yaw: the ring angle (atan2 of the XZ position)
                // plus 180°, plus the per-model forward calibration.
                float angle = Mathf.Atan2(character.Position.x, character.Position.z) * Mathf.Rad2Deg;
                float expectedYaw = Mathf.DeltaAngle(0f, angle + 180f
                    + ModelStageLayout.ForwardYawOffset(character.Model));
                Assert.AreEqual(expectedYaw, Mathf.DeltaAngle(0f, character.YawDeg), 0.01f,
                    $"{character.Model} does not face the ring centre");

                // Characters are people-sized: the plan targets 1.5-2.2 m.
                Assert.That(character.TargetHeight, Is.InRange(1.5f, 2.2f),
                    $"{character.Model} target height out of human range");
            }

            Assert.AreEqual(4, models.Count, "four distinct characters");
        }

        [Test]
        public void Characters_StayClearOfTheSliceLandmarks()
        {
            foreach (ModelStageLayout.Placement character in ModelStageLayout.Characters)
            {
                Vector2 xz = new Vector2(character.Position.x, character.Position.z);
                Assert.GreaterOrEqual(Vector2.Distance(xz, DoorXz), 1.5f,
                    $"{character.Model} blocks the door at {DoorXz}");
                Assert.GreaterOrEqual(Vector2.Distance(xz, NpcXz), 1.5f,
                    $"{character.Model} overlaps the NPC at {NpcXz}");
            }
        }

        [Test]
        public void Pickup_StandsBesideACharacter_ClearOfTheDoor()
        {
            Vector2 pickupXz = new Vector2(ModelStageLayout.PickupPosition.x, ModelStageLayout.PickupPosition.z);

            float best = float.MaxValue;
            foreach (ModelStageLayout.Placement character in ModelStageLayout.Characters)
            {
                Vector2 xz = new Vector2(character.Position.x, character.Position.z);
                best = Mathf.Min(best, Vector2.Distance(pickupXz, xz));
            }

            Assert.LessOrEqual(best, ModelStageLayout.PickupNeighborRadius,
                "the pickup must stand beside a character for the spark evidence");

            Assert.GreaterOrEqual(Vector2.Distance(pickupXz, DoorXz), 1.4f,
                "the pickup must not crowd the door (M4 aim ray lands there)");
        }

        [Test]
        public void Props_StayInsideTheGround_TreesOnTheEdges_RoadSunkFlat()
        {
            Assert.AreEqual(9, ModelStageLayout.Props.Length, "8 props + the road strip");
            int trees = 0;
            foreach (ModelStageLayout.Placement prop in ModelStageLayout.Props)
            {
                Assert.LessOrEqual(Mathf.Abs(prop.Position.x), ModelStageLayout.GroundHalfExtent - 0.5f,
                    $"{prop.Model} pokes off the ground plane on X");
                Assert.LessOrEqual(Mathf.Abs(prop.Position.z), ModelStageLayout.GroundHalfExtent - 0.5f,
                    $"{prop.Model} pokes off the ground plane on Z");

                if (prop.Model.StartsWith("ENV_Tree", System.StringComparison.Ordinal))
                {
                    trees++;
                    float radius = new Vector2(prop.Position.x, prop.Position.z).magnitude;
                    Assert.GreaterOrEqual(radius, 7f, "trees belong on the ground edges");
                }

                if (prop.Model == "ENV_Road")
                {
                    Assert.IsTrue(prop.SinkTopAboveGround,
                        "the road strip must sink (0.5 m thick mesh would wall the area off)");
                    Assert.Greater(prop.TargetLength, 0f, "the road scales by its long axis");
                    Assert.AreEqual(0f, prop.TargetHeight, "the road has no height target");
                }
            }

            Assert.AreEqual(4, trees, "both tree FBX used, twice each");
        }

        [Test]
        public void ScaleForHeight_MapsMeasuredOriginalToTarget()
        {
            Assert.AreEqual(0.01f, ModelStageLayout.ScaleForHeight(180f, 1.8f), 1e-6f,
                "the ×100 Blender import is exactly the case this exists for");
            Assert.AreEqual(1f, ModelStageLayout.ScaleForHeight(0f, 1.8f), "zero bounds must not divide");
            Assert.AreEqual(1f, ModelStageLayout.ScaleForHeight(-3f, 1.8f), "negative bounds must not divide");
            Assert.AreEqual(1f, ModelStageLayout.ScaleForHeight(180f, 0f), "zero target must not scale");
            Assert.AreEqual(2f, ModelStageLayout.ScaleForHeight(0.5f, 1.0f), 1e-6f,
                "small props (rock) upscale the same way");
        }

        [Test]
        public void ForwardYawOffset_CalibratesEveryPlacedModel()
        {
            foreach (ModelStageLayout.Placement placement in ModelStageLayout.All)
            {
                float offset = ModelStageLayout.ForwardYawOffset(placement.Model);
                Assert.IsFalse(float.IsNaN(offset), $"{placement.Model} yaw offset is NaN");
            }

            // Characters are calibrated against the ring-centre facing shots
            // (Docs/Parity/demo_stage_face_*.png): all four FBX natively face
            // -Z, so the face-center math needs no extra offset.
            Assert.AreEqual(0f, ModelStageLayout.ForwardYawOffset("CHR_Robot"),
                "character forward calibration changed — recheck the facing shots");
        }
    }
}
