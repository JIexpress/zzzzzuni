using System.Collections;
using Game.Domain.AI;
using Game.Domain.Interaction;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-R9-06 guards: the event→spark wiring must be falsifiable on real
    /// rendered pixels, not on "a handler ran". Same defect class as A-R9-04/05
    /// (configuration that reads back fine while the screen does not change): a
    /// bindings adapter that drops events, maps positions wrongly, or spawns
    /// into a disabled runtime must fail here, on pixels.
    ///
    /// Rig = the A-R9-05 probe (shared <see cref="PixelProbeRig"/>): camera
    /// 1.2 m from the spark point, solid 0.02 background, ARGBFloat readback,
    /// control frame uniform — any brighter pixel can only come from a spark.
    /// Thresholds restate the R9-05 calibration (a default 12-particle burst at
    /// this distance measures brighter≈0.0109 / meanAbs≈0.0095; the gates sit
    /// 2.7x/4.7x below, and a reverted build measures exactly 0.0000).
    ///
    /// What makes this the R9-06 evidence and not a re-run of R9-05: the burst
    /// is never requested directly. Every verdict drives the REAL event path —
    /// <c>EventBus.Fire(new EnemyAttackRequested(...))</c> / a real
    /// <c>ItemPickedUp</c> — through <see cref="GameplayFxBindings"/> into the
    /// pooled <see cref="ParticleRuntime.SpawnHitSpark"/>, i.e. the exact
    /// production chain, with zero polling anywhere.
    /// </summary>
    public sealed class GameplayFxPixelEvidencePlayModeTests
    {
        // R9-05 gates, restated (see class doc): below the measured signal, and
        // a broken build reads 0.0000, so "works" and "broken" never overlap.
        private const float MinSparkBrighter = 0.004f;
        private const float MinSparkMeanAbs = 0.002f;
        private const float FadeBrighterTolerance = 0.004f;
        private const float FadeMeanAbsTolerance = 0.002f;
        private const float DisabledMovedTolerance = 0.0005f;

        private PixelProbeRig _rig;
        private GameplayFxBindings _bindings;

        [SetUp]
        public void SetUp()
        {
            // Destroy the auto-booted shared instance (RuntimeInitializeOnLoadMethod
            // ran when the runner entered play mode): every test wires its own,
            // so subscriber counts and merge state are deterministic.
            DestroyAllBindings();

            ParticleRuntime.SetShaderCandidates(null);
            ParticleRuntime.Shutdown(); // shipped defaults (Enabled=true)

            _rig = new PixelProbeRig();
            _bindings = GameplayFxBindings.EnsureInstance();
            _bindings.Build(); // production emitter: straight into ParticleRuntime
        }

        [TearDown]
        public void TearDown()
        {
            DestroyAllBindings();
            ParticleRuntime.SetShaderCandidates(null);
            ParticleRuntime.Shutdown();
            _rig?.Dispose();
            _rig = null;
            _bindings = null;
        }

        [UnityTest]
        public IEnumerator EnemyAttackEvent_BrightensTheFrame_AtTheTargetPosition()
        {
            yield return null;
            BuildRigForSparkSpot();
            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false); // isolate the spark effect, R9-05 rig convention
            yield return null;
            yield return null;

            PixelProbeRig.Frame baseline = default;
            yield return _rig.CaptureInto(f => baseline = f);
            PixelProbeRig.LogFrame("attack.control", baseline);
            Assert.Less(baseline.Stats.MaxLuma, 0.1f,
                "rig sanity: the control frame is not empty - something other than the spark is in front of the probe camera");

            // The REAL event path (exactly what EnemyBehaviorTree.AttackAction does):
            // enemy far away, target where the probe camera looks.
            var enemyPos = new Vec2(4000f, 4000f);
            var targetPos = new Vec2(PixelProbeRig.FarPark.x, PixelProbeRig.FarPark.z);
            EventBus.Fire(new EnemyAttackRequested("enemy-px", enemyPos, targetPos));

            // 2 frames later the burst is at full brightness (lifetime 0.22-0.45 s).
            yield return null;
            yield return null;
            PixelProbeRig.Frame burst = default;
            yield return _rig.CaptureInto(f => burst = f);
            PixelProbeRig.LogFrame("attack.burst", burst);

            PixelProbeRig.FrameDiff burstDiff = PixelProbeRig.FrameDiff.Between(baseline, burst);
            Debug.Log($"[FXBINDINGS] attack burst vs control: meanAbs={burstDiff.MeanAbs:F5} " +
                      $"brighter={burstDiff.BrighterFraction:F4} darker={burstDiff.DarkerFraction:F4} " +
                      $"shader='{ParticleRuntime.ResolvedShaderName}' " +
                      $"events={_bindings.EventsReceived} sparks={_bindings.SparksRequested} merged={_bindings.SparksMerged}");

            Assert.Greater(burstDiff.BrighterFraction, MinSparkBrighter,
                $"the attack event did not put a spark on screen: {burstDiff.BrighterFraction:F4} of pixels " +
                $"got brighter (gate {MinSparkBrighter:F4}). A wired-but-silent adapter (dropped event, " +
                "wrong position, dead emitter) is exactly what this fails on.");
            Assert.Greater(burstDiff.MeanAbs, MinSparkMeanAbs,
                $"the attack spark barely moved the frame: mean |delta| {burstDiff.MeanAbs:F5} (gate {MinSparkMeanAbs:F4})");

            // The adapter-level bookkeeping must agree with the pixels.
            Assert.AreEqual(1, _bindings.EventsReceived, "the attack event did not reach the bindings");
            Assert.AreEqual(1, _bindings.SparksRequested, "the bindings did not forward exactly one spark request");
            Assert.AreEqual(0, _bindings.SparksMerged, "a lone event must not be counted as merged");
            Assert.AreEqual(12, ParticleRuntime.LastSpawnEmitted,
                "the spark did not go through SpawnHitSpark with its default burst");

            // ... and it must be short-lived (<= 0.5 s, R9-05 contract).
            yield return new WaitForSeconds(0.55f);
            yield return null;
            PixelProbeRig.Frame faded = default;
            yield return _rig.CaptureInto(f => faded = f);
            PixelProbeRig.FrameDiff fadeDiff = PixelProbeRig.FrameDiff.Between(baseline, faded);
            Assert.Less(fadeDiff.BrighterFraction, FadeBrighterTolerance,
                $"sparks outlived their lifetime: {fadeDiff.BrighterFraction:F4} of pixels still brighter after 0.55 s");
            Assert.Less(fadeDiff.MeanAbs, FadeMeanAbsTolerance,
                $"frame did not return to the control after 0.55 s: mean |delta| {fadeDiff.MeanAbs:F6}");
        }

        [UnityTest]
        public IEnumerator TenPickupEventsSameFrameSameSpot_YieldExactlyOneSpark_AndStillBrighten()
        {
            yield return null;
            BuildRigForSparkSpot();
            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false);
            yield return null;
            yield return null;

            PixelProbeRig.Frame baseline = default;
            yield return _rig.CaptureInto(f => baseline = f);

            // The anti-spam case from the brief ("100 连发刷屏"): ten pickups
            // landing in the same frame at the same spot are ONE visual event.
            var itemPos = new Vec2(PixelProbeRig.FarPark.x, PixelProbeRig.FarPark.z);
            for (int i = 0; i < 10; i++)
            {
                EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, i + 1, itemPos));
            }

            yield return null;
            yield return null;
            PixelProbeRig.Frame burst = default;
            yield return _rig.CaptureInto(f => burst = f);

            PixelProbeRig.FrameDiff diff = PixelProbeRig.FrameDiff.Between(baseline, burst);
            Debug.Log($"[FXBINDINGS] 10 same-frame pickups: meanAbs={diff.MeanAbs:F5} " +
                      $"brighter={diff.BrighterFraction:F4} events={_bindings.EventsReceived} " +
                      $"sparks={_bindings.SparksRequested} merged={_bindings.SparksMerged} " +
                      $"alive={ParticleRuntime.HitSparkParticleCount}");

            Assert.AreEqual(10, _bindings.EventsReceived, "every pickup event must still reach the bindings");
            Assert.AreEqual(1, _bindings.SparksRequested,
                "ten same-frame same-spot pickups must merge into exactly one spark request");
            Assert.AreEqual(9, _bindings.SparksMerged);
            Assert.Greater(diff.BrighterFraction, MinSparkBrighter,
                $"the merged pickup spark did not draw: {diff.BrighterFraction:F4} of pixels got brighter " +
                $"(gate {MinSparkBrighter:F4})");

            yield return new WaitForSeconds(0.55f);
            yield return null;
            Assert.AreEqual(0, ParticleRuntime.HitSparkParticleCount,
                "the merged burst is still alive after 0.55 s");
        }

        [UnityTest]
        public IEnumerator DoorToggleEvents_BrightenTheFrame_AtTheDoorPosition_BothDirections()
        {
            yield return null;
            BuildRigForSparkSpot();
            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false); // isolate the spark effect, R9-05 rig convention
            yield return null;
            yield return null;

            PixelProbeRig.Frame baseline = default;
            yield return _rig.CaptureInto(f => baseline = f);
            PixelProbeRig.LogFrame("door.control", baseline);
            Assert.Less(baseline.Stats.MaxLuma, 0.1f,
                "rig sanity: the control frame is not empty - something other than the spark is in front of the probe camera");

            // The REAL event path for the door (TASK-A-R9-07): a real
            // DoorEntity holding the probe spot — Toggle() is exactly what the
            // InteractableProxy + interaction slice drive, and it fires
            // DoorStateChanged (with the R9-07 position extension) on the bus.
            var doorSpot = new Vec2(PixelProbeRig.FarPark.x, PixelProbeRig.FarPark.z);
            var door = new DoorEntity("door-px", position: doorSpot);

            door.Toggle(); // OPEN

            // 2 frames later the burst is at full brightness (lifetime 0.22-0.45 s).
            yield return null;
            yield return null;
            PixelProbeRig.Frame openBurst = default;
            yield return _rig.CaptureInto(f => openBurst = f);
            PixelProbeRig.FrameDiff openDiff = PixelProbeRig.FrameDiff.Between(baseline, openBurst);
            Debug.Log($"[FXBINDINGS] door.open burst vs control: meanAbs={openDiff.MeanAbs:F5} " +
                      $"brighter={openDiff.BrighterFraction:F4} darker={openDiff.DarkerFraction:F4} " +
                      $"events={_bindings.EventsReceived} sparks={_bindings.SparksRequested}");

            Assert.Greater(openDiff.BrighterFraction, MinSparkBrighter,
                $"the door OPEN event did not put a spark on screen: {openDiff.BrighterFraction:F4} of pixels " +
                $"got brighter (gate {MinSparkBrighter:F4}). A dropped door event or a lost position " +
                "extension is exactly what this fails on.");
            Assert.Greater(openDiff.MeanAbs, MinSparkMeanAbs,
                $"the door-open spark barely moved the frame: mean |delta| {openDiff.MeanAbs:F5} (gate {MinSparkMeanAbs:F4})");
            Assert.AreEqual(1, _bindings.EventsReceived, "the door event did not reach the bindings");
            Assert.AreEqual(1, _bindings.SparksRequested, "the bindings did not forward the door spark");
            Assert.AreEqual(0, _bindings.SparksMerged, "a lone door event must not be counted as merged");
            Assert.AreEqual(12, ParticleRuntime.LastSpawnEmitted,
                "the door spark did not go through SpawnHitSpark with its default burst");

            // Let the open burst fade so the CLOSE burst is measured against
            // the same control frame again (the two toggles are frames apart,
            // so the same-frame merge cannot absorb either).
            yield return new WaitForSeconds(0.55f);
            yield return null;

            door.Toggle(); // CLOSE

            yield return null;
            yield return null;
            PixelProbeRig.Frame closeBurst = default;
            yield return _rig.CaptureInto(f => closeBurst = f);
            PixelProbeRig.FrameDiff closeDiff = PixelProbeRig.FrameDiff.Between(baseline, closeBurst);
            Debug.Log($"[FXBINDINGS] door.close burst vs control: meanAbs={closeDiff.MeanAbs:F5} " +
                      $"brighter={closeDiff.BrighterFraction:F4} events={_bindings.EventsReceived} " +
                      $"sparks={_bindings.SparksRequested} alive={ParticleRuntime.HitSparkParticleCount}");

            Assert.Greater(closeDiff.BrighterFraction, MinSparkBrighter,
                $"the door CLOSE event did not put a spark on screen: {closeDiff.BrighterFraction:F4} " +
                $"(gate {MinSparkBrighter:F4})");
            Assert.Greater(closeDiff.MeanAbs, MinSparkMeanAbs,
                $"the door-close spark barely moved the frame: mean |delta| {closeDiff.MeanAbs:F5}");
            Assert.AreEqual(2, _bindings.EventsReceived, "both toggles must reach the bindings");
            Assert.AreEqual(2, _bindings.SparksRequested, "open and close each owe one spark");
            Assert.AreEqual(0, _bindings.SparksMerged, "the toggles are frames apart — nothing may merge");

            // ... and the close burst must be short-lived too (<= 0.5 s, R9-05 contract).
            yield return new WaitForSeconds(0.55f);
            yield return null;
            PixelProbeRig.Frame faded = default;
            yield return _rig.CaptureInto(f => faded = f);
            PixelProbeRig.FrameDiff fadeDiff = PixelProbeRig.FrameDiff.Between(baseline, faded);
            Assert.Less(fadeDiff.BrighterFraction, FadeBrighterTolerance,
                $"door sparks outlived their lifetime: {fadeDiff.BrighterFraction:F4} of pixels still brighter after 0.55 s");
            Assert.AreEqual(0, ParticleRuntime.HitSparkParticleCount, "door bursts must be dead after 0.55 s");
        }

        [UnityTest]
        public IEnumerator EventsWhileDisabled_KeepFlowing_ButMoveZeroPixels()
        {
            yield return null;
            BuildRigForSparkSpot();
            ParticleRuntime.SetEnabled(false); // the single switch, bindings stay wired
            yield return null;
            yield return null;

            PixelProbeRig.Frame baseline = default;
            yield return _rig.CaptureInto(f => baseline = f);

            // Events keep firing (real paths, two distinct spots so nothing merges).
            EventBus.Fire(new EnemyAttackRequested("enemy-px", new Vec2(4000f, 4000f),
                new Vec2(PixelProbeRig.FarPark.x, PixelProbeRig.FarPark.z)));
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1,
                new Vec2(PixelProbeRig.FarPark.x + 5f, PixelProbeRig.FarPark.z)));

            yield return null;
            yield return null;

            PixelProbeRig.Frame afterEvents = default;
            yield return _rig.CaptureInto(f => afterEvents = f);
            PixelProbeRig.FrameDiff diff = PixelProbeRig.FrameDiff.Between(baseline, afterEvents);
            Debug.Log($"[FXBINDINGS] disabled events vs baseline: meanAbs={diff.MeanAbs:F6} " +
                      $"moved={diff.MovedFraction:F4} events={_bindings.EventsReceived} " +
                      $"sparks={_bindings.SparksRequested} total={ParticleRuntime.TotalParticleCount} " +
                      $"lastEmitted={ParticleRuntime.LastSpawnEmitted}");

            Assert.AreEqual(2, _bindings.EventsReceived, "events must keep flowing while the runtime is off");
            Assert.AreEqual(2, _bindings.SparksRequested,
                "the bindings forward requests regardless of the switch (single source of truth stays ParticleRuntime)");
            Assert.AreEqual(0, ParticleRuntime.TotalParticleCount,
                "particles exist while ParticleRuntime.Enabled == false");
            Assert.AreEqual(0, ParticleRuntime.LastSpawnEmitted,
                "the emitter reported an emission while the runtime was disabled");
            Assert.Less(diff.MovedFraction, DisabledMovedTolerance,
                $"pixels moved while the runtime was disabled: {diff.MovedFraction:F4} " +
                $"(tolerance {DisabledMovedTolerance:F4}, control frames are bit-identical)");
            Assert.Less(diff.MeanAbs, FadeMeanAbsTolerance,
                $"the frame changed while the runtime was disabled: mean |delta| {diff.MeanAbs:F6}");
        }

        // ---- rig --------------------------------------------------------------

        /// <summary>
        /// Parks the probe camera at the R9-05 geometry: 1.2 m from the spark
        /// spot, solid background. The spark spot is the plane position the
        /// events will report, lifted to the bindings' configured height, so
        /// "event position" and "pixels" are the same point by construction.
        /// </summary>
        private void BuildRigForSparkSpot()
        {
            _bindings.SparkHeight = PixelProbeRig.FarPark.y;
            var sparkSpot = new Vector3(PixelProbeRig.FarPark.x, _bindings.SparkHeight, PixelProbeRig.FarPark.z);
            _rig.Build(sparkSpot + new Vector3(0f, 0f, -PixelProbeRig.BurstDistance), sparkSpot);
        }

        private static void DestroyAllBindings()
        {
            // DestroyImmediate (not Destroy): the shared instance must be gone
            // BEFORE the next SetUp builds a fresh one, and OnDestroy must run
            // synchronously so its EventScope unsubscribes now, not next frame.
            foreach (GameplayFxBindings bindings in Object.FindObjectsOfType<GameplayFxBindings>(true))
            {
                Object.DestroyImmediate(bindings.gameObject);
            }
        }
    }
}
