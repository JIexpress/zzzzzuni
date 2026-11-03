using System.Collections;
using System.Collections.Generic;
using Game.Domain.Interaction;
using Game.Bootstrap;
using Game.View;
using Game.View.Effects;
using Game.View.Interaction;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-R9-10 pixel evidence: the real-model stage in the committed
    /// <c>SCN_Game</c> must be visible AND feed the R9-06 pickup→spark chain,
    /// measured on real pixels — not on "the objects exist".
    ///
    /// These tests load the actual committed scene (Single mode, straight
    /// through <see cref="SceneManager"/>, no boot chain), park the shared
    /// <see cref="PixelProbeRig"/> in front of the stage and diff frames:
    /// * <see cref="ScnGame_ChristmasModels_ChangeTheCameraImage"/>: the four
    ///   CHR roots off vs on — only the models (and their shadows) sit between
    ///   the two frames (enemy parked, ambient dust off), so a moved/darker
    ///   diff proves the models really draw. The same pass asserts every CHR
    ///   renderer carries a live URP shader (the "no pink" gate, V2).
    /// * <see cref="ScnGame_PickupInteract_SparksBesideTheRealModel"/>: the
    ///   scene's own pickup <see cref="InteractableProxy"/> is interacted with
    ///   through the REAL domain path (the same
    ///   <c>PickupEntity.Interact</c> an E-press reaches) — the
    ///   ItemPickedUp event must light a spark beside the Kenney character,
    ///   on pixels, through the production GameplayFxBindings →
    ///   ParticleRuntime chain.
    ///
    /// Gates restate the R9-05/06 calibration (burst at 1.2 m: brighter ≈
    /// 0.0109; a broken chain measures exactly 0.0000).
    /// </summary>
    public sealed class ModelStagePixelEvidencePlayModeTests
    {
        private const float MinModelMoved = 0.010f;
        private const float MinModelDarker = 0.005f;
        private const float MinSparkBrighter = 0.004f;
        private const float MinSparkMeanAbs = 0.002f;

        private PixelProbeRig _rig;

        [SetUp]
        public void SetUp()
        {
            ParticleRuntime.SetShaderCandidates(null);
            ParticleRuntime.Shutdown(); // shipped defaults (Enabled=true, dust on)
            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false); // isolate the frame, R9-05 rig convention
            _rig = new PixelProbeRig();
        }

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
            ParticleRuntime.SetShaderCandidates(null);
            ParticleRuntime.Shutdown();
        }

        [UnityTest]
        public IEnumerator ScnGame_ChristmasModels_ChangeTheCameraImage()
        {
            yield return LoadGameScene();

            // Park the enemy: it patrols between the characters and would move
            // pixels between the two captures (everything else is static).
            GameObject enemy = GameObject.Find("Enemy");
            Assert.IsNotNull(enemy, "the slice scene lost its enemy");
            enemy.SetActive(false);

            // Collect the four character roots from the SAME plan the scene
            // was built from, and run the no-pink gate on every renderer.
            var characterRoots = new List<GameObject>();
            foreach (ModelStageLayout.Placement placement in ModelStageLayout.Characters)
            {
                GameObject root = GameObject.Find(placement.Model);
                Assert.IsNotNull(root, $"SCN_Game is missing the '{placement.Model}' model");
                characterRoots.Add(root);

                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    Assert.IsTrue(renderer.enabled, $"{root.name}/{renderer.name} renderer disabled");
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        Assert.IsNotNull(material, $"{root.name}/{renderer.name} has a NULL (pink) material slot");
                        Assert.IsNotNull(material.shader, $"{root.name}/{renderer.name} material has no shader");
                        Assert.AreNotEqual("Hidden/InternalErrorShader", material.shader.name,
                            $"{root.name}/{renderer.name} renders pink (error shader)");
                        Assert.IsTrue(material.shader.name.StartsWith("Universal Render Pipeline",
                                System.StringComparison.Ordinal),
                            $"{root.name}/{renderer.name} material '{material.name}' is not a URP shader " +
                            $"(got '{material.shader.name}')");
                    }
                }
            }

            var rig = _rig;
            // Rig geometry: from behind the player start, three ring characters
            // (Kenney centre, Robot right, DarkElf left) are inside the 60° FOV.
            rig.Build(new Vector3(-3f, 1.6f, -5.5f), new Vector3(1.5f, 0.9f, 2.5f));
            yield return null;
            yield return null;

            // Control frame: same scene, models switched off.
            foreach (GameObject root in characterRoots)
            {
                root.SetActive(false);
            }

            yield return null;
            PixelProbeRig.Frame control = default;
            yield return rig.CaptureInto(f => control = f);

            // Evidence frame: models back on.
            foreach (GameObject root in characterRoots)
            {
                root.SetActive(true);
            }

            yield return null;
            yield return null;
            PixelProbeRig.Frame withModels = default;
            yield return rig.CaptureInto(f => withModels = f);

            PixelProbeRig.FrameDiff diff = PixelProbeRig.FrameDiff.Between(control, withModels);
            Debug.Log($"[R910] models vs control: meanAbs={diff.MeanAbs:F5} " +
                      $"brighter={diff.BrighterFraction:F4} darker={diff.DarkerFraction:F4} " +
                      $"moved={diff.MovedFraction:F4} " +
                      $"control(meanLuma={control.Stats.MeanLuma:F5} max={control.Stats.MaxLuma:F4})");

            Assert.Greater(diff.MovedFraction, MinModelMoved,
                $"the four CHR models did not measurably change the camera image: only " +
                $"{diff.MovedFraction:F4} of pixels moved (gate {MinModelMoved:F4}). An invisible " +
                "stage (wrong scale, wrong spot, culled) fails here.");
            Assert.Greater(diff.DarkerFraction, MinModelDarker,
                $"the models only brightened the frame ({diff.BrighterFraction:F4} brighter / " +
                $"{diff.DarkerFraction:F4} darker) — expected the coloured bodies to darken lit ground pixels");
        }

        [UnityTest]
        public IEnumerator ScnGame_PickupInteract_SparksBesideTheRealModel()
        {
            yield return LoadGameScene();

            GameObject enemy = GameObject.Find("Enemy");
            Assert.IsNotNull(enemy, "the slice scene lost its enemy");
            enemy.SetActive(false);

            GameObject pickupGo = GameObject.Find("Pickup");
            Assert.IsNotNull(pickupGo, "SCN_Game is missing the Pickup");
            InteractableProxy proxy = pickupGo.GetComponent<InteractableProxy>();
            Assert.IsNotNull(proxy, "Pickup lost its InteractableProxy");
            Assert.IsInstanceOf<Game.Domain.Interaction.PickupEntity>(proxy.Target,
                "the scene pickup must carry a PickupEntity (the brief's chain)");

            // The spark lands at (pickup.x, SparkHeight, pickup.z); park the
            // probe camera at the R9-05 geometry (1.2 m out, solid framing).
            var bindings = GameplayFxBindings.EnsureInstance();
            bindings.Build(); // heals a torn-down shared instance; idempotent otherwise
            bindings.SparkHeight = 1f;
            Vector3 sparkSpot = new Vector3(pickupGo.transform.position.x,
                bindings.SparkHeight, pickupGo.transform.position.z);
            var rig = _rig;
            rig.Build(sparkSpot + new Vector3(0f, 0f, -PixelProbeRig.BurstDistance), sparkSpot);
            yield return null;
            yield return null;

            PixelProbeRig.Frame baseline = default;
            yield return rig.CaptureInto(f => baseline = f);

            // The REAL chain, exactly as an E-press reaches it: the scene's
            // PickupEntity fires ItemPickedUp (with its own position, the
            // spot beside the Kenney model) → GameplayFxBindings →
            // ParticleRuntime.SpawnHitSpark.
            proxy.Target.Interact(ActorId.Player);

            yield return null;
            yield return null;
            PixelProbeRig.Frame burst = default;
            yield return rig.CaptureInto(f => burst = f);

            PixelProbeRig.FrameDiff diff = PixelProbeRig.FrameDiff.Between(baseline, burst);
            Debug.Log($"[R910] pickup spark vs baseline: meanAbs={diff.MeanAbs:F5} " +
                      $"brighter={diff.BrighterFraction:F4} events={bindings.EventsReceived} " +
                      $"sparks={bindings.SparksRequested} emitted={ParticleRuntime.LastSpawnEmitted} " +
                      $"alive={ParticleRuntime.HitSparkParticleCount}");

            Assert.AreEqual(1, bindings.EventsReceived, "the pickup event did not reach the bindings");
            Assert.AreEqual(1, bindings.SparksRequested, "the bindings did not forward the spark");
            Assert.AreEqual(12, ParticleRuntime.LastSpawnEmitted,
                "the spark did not go through SpawnHitSpark with its default burst");
            Assert.Greater(diff.BrighterFraction, MinSparkBrighter,
                $"the pickup interaction did not put a spark beside the model: {diff.BrighterFraction:F4} " +
                $"of pixels got brighter (gate {MinSparkBrighter:F4})");
            Assert.Greater(diff.MeanAbs, MinSparkMeanAbs,
                $"the pickup spark barely moved the frame: mean |delta| {diff.MeanAbs:F5} (gate {MinSparkMeanAbs:F4})");

            // ... and the burst is short-lived (R9-05 contract, <= 0.5 s).
            yield return new WaitForSeconds(0.55f);
            yield return null;
            Assert.AreEqual(0, ParticleRuntime.HitSparkParticleCount,
                "the pickup spark outlived its lifetime");
        }

        private static IEnumerator LoadGameScene()
        {
            // Single mode: a fresh copy of the committed scene for every test,
            // no boot chain — the stage under test is scene content, not boot.
            yield return SceneManager.LoadSceneAsync(GameBootstrap.GameSceneName);
            Assert.AreEqual(GameBootstrap.GameSceneName, SceneManager.GetActiveScene().name,
                "SCN_Game did not activate");
            yield return null;
            yield return null;
        }
    }
}
