using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.InputSystem;
using ZFrame.View;

namespace ZFrame.View.Input.Tests
{
    /// <summary>
    /// EditMode tests of <see cref="RebindService"/> (TASK-A-M3-03): the
    /// persistence roundtrip against an INJECTED temp root (the real
    /// <c>Application.persistentDataPath</c> is never touched), the tolerant
    /// load path (missing/empty file), reset semantics, the display format and
    /// the EditMode guard of the interactive rebind.
    ///
    /// The interactive capture itself cannot run in EditMode (no device loop,
    /// no play mode) — that is exactly why <see cref="RebindService.Rebind"/>
    /// reports <see cref="RebindOutcome.Failed"/> synchronously there, which is
    /// asserted below.
    /// </summary>
    public sealed class RebindServiceTests
    {
        private ZFrameControls _controls;
        private RebindService _service;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _controls = ZFrameControls.FromResources();
            Assert.IsNotNull(_controls, "the committed ZFrameControls.inputactions asset must load from Resources");

            _root = Path.Combine(Path.GetTempPath(), "zframe_input_tests_" + Guid.NewGuid().ToString("N"));
            _service = new RebindService(_controls, _root);
        }

        [TearDown]
        public void TearDown()
        {
            _service?.Dispose();
            _controls?.Dispose();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void ApplyOverride(InputAction action, int bindingIndex, string path)
        {
            // ApplyBindingOverride copies overridePath/overrideProcessors/
            // overrideInteractions — `path` alone would be ignored.
            action.ApplyBindingOverride(bindingIndex, new InputBinding { overridePath = path });
        }

        // ---------- persistence roundtrip (injected root) ----------

        [Test]
        public void Save_ThenLoad_RestoresTheOverride_OnAFreshAsset()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");
            Assert.AreEqual("<Keyboard>/f", _controls.Interact.bindings[0].effectivePath);

            _service.Save();
            Assert.IsTrue(File.Exists(_service.FilePath), "Save must write input_bindings.json into the injected root");

            ZFrameControls reloaded = ZFrameControls.FromResources();
            try
            {
                var reloadedService = new RebindService(reloaded, _root);
                Assert.IsTrue(reloadedService.Load(), "a saved overrides file must be applied on load");
                Assert.AreEqual("<Keyboard>/f", reloaded.Interact.bindings[0].effectivePath,
                    "the reloaded asset must carry the persisted override");
                Assert.AreEqual("F", reloadedService.GetBindingDisplay("Player/Interact"),
                    "the display string follows the persisted override");
            }
            finally
            {
                reloaded.Dispose();
            }
        }

        [Test]
        public void Save_WritesUtf8WithoutBom()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");
            _service.Save();

            byte[] bytes = File.ReadAllBytes(_service.FilePath);
            Assert.Greater(bytes.Length, 0);
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            Assert.IsFalse(hasBom, "persisted files are UTF-8 without BOM (file-conventions)");
        }

        [Test]
        public void Load_MissingFile_IsSilentNoOp()
        {
            Assert.IsFalse(File.Exists(_service.FilePath));
            Assert.DoesNotThrow(() => _service.Load());
            Assert.IsFalse(_service.Load(), "no file → nothing loaded, no exception (幂等)");
            Assert.AreEqual("<Keyboard>/e", _controls.Interact.bindings[0].effectivePath, "asset defaults stay untouched");
        }

        [Test]
        public void Load_EmptyFile_IsSilentNoOp()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_service.FilePath, string.Empty);

            Assert.DoesNotThrow(() => _service.Load());
            Assert.IsFalse(_service.Load());
            Assert.AreEqual("<Keyboard>/e", _controls.Interact.bindings[0].effectivePath);
        }

        [Test]
        public void Load_CorruptFile_IsTolerated()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_service.FilePath, "not an overrides file");

            Assert.DoesNotThrow(() => _service.Load());
            Assert.IsFalse(_service.Load());
            Assert.AreEqual("<Keyboard>/e", _controls.Interact.bindings[0].effectivePath, "corrupt file → defaults kept");
        }

        // ---------- reset ----------

        [Test]
        public void ResetAll_DropsEveryOverride_AndPersistsTheEmptyState()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");
            ApplyOverride(_controls.Pause, 0, "<Keyboard>/p");

            _service.ResetAll();

            Assert.AreEqual("<Keyboard>/e", _controls.Interact.bindings[0].effectivePath, "恢复默认 restores the asset default");
            Assert.AreEqual("<Keyboard>/escape", _controls.Pause.bindings[0].effectivePath);
            Assert.IsTrue(File.Exists(_service.FilePath), "ResetAll persists (an empty overrides file)");

            ZFrameControls reloaded = ZFrameControls.FromResources();
            try
            {
                var reloadedService = new RebindService(reloaded, _root);
                reloadedService.Load();
                Assert.AreEqual("<Keyboard>/e", reloaded.Interact.bindings[0].effectivePath,
                    "the persisted empty state must not resurrect the old override");
            }
            finally
            {
                reloaded.Dispose();
            }
        }

        [Test]
        public void ResetBinding_TouchesOnlyThatAction()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");
            ApplyOverride(_controls.Pause, 0, "<Keyboard>/p");

            _service.ResetBinding("Player/Interact");

            Assert.AreEqual("<Keyboard>/e", _controls.Interact.bindings[0].effectivePath);
            Assert.AreEqual("<Keyboard>/p", _controls.Pause.bindings[0].effectivePath, "per-row reset must not clear other actions");
        }

        [Test]
        public void ResetBinding_UnknownAction_IsNoOp()
        {
            Assert.DoesNotThrow(() => _service.ResetBinding("Player/Nope"));
            Assert.DoesNotThrow(() => _service.ResetBinding(null));
        }

        // ---------- display ----------

        [Test]
        public void GetBindingDisplay_IsNonEmpty_AndOmitsTheDeviceName()
        {
            string display = _service.GetBindingDisplay("Player/Interact");

            Assert.IsFalse(string.IsNullOrEmpty(display), "键位显示串必须非空");
            Assert.AreEqual("E", display);
            StringAssert.DoesNotContain("Keyboard", display, "HumanReadableStringOptions.OmitDevice 必须去掉设备名");
            StringAssert.DoesNotContain("<", display);
        }

        [Test]
        public void GetBindingDisplay_CompositeAction_ShowsItsPrimaryPart()
        {
            // Move is a WASD composite: the primary (first non-header) binding is the "up" part.
            Assert.AreEqual("W", _service.GetBindingDisplay("Player/Move"));
        }

        [Test]
        public void GetBindingDisplay_UnknownAction_ReturnsUnboundPlaceholder()
        {
            Assert.AreEqual(RebindService.UnboundDisplay, _service.GetBindingDisplay("Player/Nope"));
            Assert.AreEqual(RebindService.UnboundDisplay, _service.GetBindingDisplay(null));
            Assert.AreEqual(RebindService.UnboundDisplay, RebindService.FormatBindingDisplay(string.Empty));
        }

        // ---------- EditMode guard ----------

        [Test]
        public void Rebind_InEditMode_FailsImmediately_WithoutThrowing()
        {
            Assert.IsFalse(UnityEngine.Application.isPlaying, "this suite runs in EditMode");

            RebindOutcome? outcome = null;
            BindingSnapshot snapshot = null;
            Assert.DoesNotThrow(() => _service.Rebind("Player/Interact", (o, s) =>
            {
                outcome = o;
                snapshot = s;
            }));

            Assert.AreEqual(RebindOutcome.Failed, outcome, "EditMode 下必须立即回调 Failed，不得抛异常");
            Assert.IsNull(snapshot);
            Assert.IsFalse(_service.IsRebinding);
        }

        [Test]
        public void Rebind_UnknownAction_Fails()
        {
            RebindOutcome? outcome = null;
            _service.Rebind("Player/Nope", (o, _) => outcome = o);

            Assert.AreEqual(RebindOutcome.Failed, outcome);
        }

        [Test]
        public void Rebind_NullCallback_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _service.Rebind("Player/Interact", null));
        }

        [Test]
        public void Cancel_WithoutARunningRebind_ReturnsFalse()
        {
            Assert.IsFalse(_service.Cancel());
        }

        // ---------- completion path: conflict rollback / persist ----------
        //
        // The interactive capture cannot run in EditMode (no device loop), so the whole
        // CompleteCaptured path used to be untested - including the rollback that protects
        // the player's old key. Drive it directly instead of pretending the detector test
        // covers it.

        private void InvokeCompleteCaptured(
            string actionKey,
            InputAction action,
            int bindingIndex,
            Action<RebindOutcome, BindingSnapshot> callback)
        {
            FieldInfo pending = typeof(RebindService).GetField(
                "_pending", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(pending, "RebindService._pending should exist (reflection harness)");
            pending.SetValue(_service, callback);

            MethodInfo complete = typeof(RebindService).GetMethod(
                "CompleteCaptured", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(complete, "RebindService.CompleteCaptured should exist (reflection harness)");
            complete.Invoke(_service, new object[] { actionKey, action, bindingIndex });
        }

        [Test]
        public void CompleteCaptured_OnConflict_RollsBackOverride_AndDoesNotPersist()
        {
            string originalPath = _controls.Interact.bindings[0].effectivePath;
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/escape"); // Pause already owns Esc
            Assert.AreEqual("<Keyboard>/escape", _controls.Interact.bindings[0].effectivePath);

            RebindOutcome? outcome = null;
            BindingSnapshot snapshot = null;
            InvokeCompleteCaptured("Player/Interact", _controls.Interact, 0, (o, s) =>
            {
                outcome = o;
                snapshot = s;
            });

            Assert.AreEqual(RebindOutcome.Conflict, outcome);
            Assert.IsNotNull(snapshot, "a conflict must report the occupying action");
            Assert.AreEqual("Player/Pause", snapshot.ActionKey);
            Assert.AreEqual(originalPath, _controls.Interact.bindings[0].effectivePath,
                "conflict must roll the override back so the old key keeps working");
            Assert.IsFalse(File.Exists(_service.FilePath), "a rolled-back rebind must not be persisted");
        }

        [Test]
        public void CompleteCaptured_OnFreeKey_KeepsOverride_PersistsAndReportsDone()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");

            RebindOutcome? outcome = null;
            BindingSnapshot snapshot = null;
            InvokeCompleteCaptured("Player/Interact", _controls.Interact, 0, (o, s) =>
            {
                outcome = o;
                snapshot = s;
            });

            Assert.AreEqual(RebindOutcome.Done, outcome);
            Assert.IsNotNull(snapshot);
            Assert.AreEqual("<Keyboard>/f", snapshot.EffectivePath);
            Assert.AreEqual("<Keyboard>/f", _controls.Interact.bindings[0].effectivePath,
                "a free key must keep the override");
            Assert.IsTrue(File.Exists(_service.FilePath), "a successful rebind must be persisted");
        }

        // ---------- persist failure (TASK-H-01 D1: no UI soft-lock) ----------

        /// <summary>Reads one private field of the service (reflection harness — same discipline as <see cref="InvokeCompleteCaptured"/>).</summary>
        private object ReadServiceField(string name)
        {
            FieldInfo field = typeof(RebindService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"RebindService.{name} should exist (reflection harness)");
            return field.GetValue(_service);
        }

        /// <summary>Locked overrides file → <c>Save()</c> throws <see cref="IOException"/> exactly like a full/read-only profile directory.</summary>
        private FileStream LockBindingsFile()
        {
            return new FileStream(_service.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        [Test]
        public void CompleteCaptured_WhenPersistFails_ReportsFailed_AndUnlocksTheRow()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");
            Directory.CreateDirectory(_root);
            _service.Save(); // seed a real file: the failure below must leave it intact
            Assert.AreEqual("<Keyboard>/f", _controls.Interact.bindings[0].effectivePath);

            RebindOutcome? outcome = null;
            BindingSnapshot snapshot = null;

            using (LockBindingsFile())
            {
                ApplyOverride(_controls.Interact, 0, "<Keyboard>/g"); // the newly captured key

                Assert.DoesNotThrow(
                    () => InvokeCompleteCaptured("Player/Interact", _controls.Interact, 0, (o, s) =>
                    {
                        outcome = o;
                        snapshot = s;
                    }),
                    "a failed persist must be reported through the callback, not thrown into the Input System");

                Assert.AreEqual(RebindOutcome.Failed, outcome,
                    "the row must be told Failed — otherwise it stays on 「按任意键…」 with interactable=false forever");
                Assert.IsNull(snapshot);
                Assert.IsFalse(_service.IsRebinding, "no rebind operation may survive the failure");
                Assert.IsNull(ReadServiceField("_pending"),
                    "the pending callback must be cleared: a stale one would never be invoked again");
                Assert.AreEqual("<Keyboard>/g", _controls.Interact.bindings[0].effectivePath,
                    "the captured key stays live for this session (the failure was persistence, not the capture)");
            }

            // The old file was never truncated/rewritten: the previous override still loads.
            ZFrameControls reloaded = ZFrameControls.FromResources();
            try
            {
                var reloadedService = new RebindService(reloaded, _root);
                Assert.IsTrue(reloadedService.Load(), "the pre-existing overrides file must still be readable");
                Assert.AreEqual("<Keyboard>/f", reloaded.Interact.bindings[0].effectivePath,
                    "a failed save must not truncate the file that was already on disk");
            }
            finally
            {
                reloaded.Dispose();
            }

            // And the service accepts the next rebind instead of refusing it forever.
            RebindOutcome? next = null;
            Assert.DoesNotThrow(() => _service.Rebind("Player/Interact", (o, _) => next = o));
            Assert.AreEqual(RebindOutcome.Failed, next,
                "the entry point answers again (EditMode guard only) — no leftover operation short-circuits it");
        }

        [Test]
        public void StartCapture_WhenTheCaptureCannotStart_ReportsFailed_AndLeavesNoResidue()
        {
            // An action without any binding makes InputSystem refuse the operation setup —
            // the only deterministic way to drive StartCapture's failure path (a real rebind
            // needs play mode + a device). The assertion is on the recovery contract, which
            // is identical for every reason a capture can fail to start.
            InputActionAsset scratch = UnityEngine.ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                InputActionMap map = scratch.AddActionMap("Scratch");
                InputAction unbound = map.AddAction("Unbound", InputActionType.Button);
                Assert.AreEqual(0, unbound.bindings.Count, "precondition: the scratch action carries no binding");

                MethodInfo startCapture = typeof(RebindService).GetMethod(
                    "StartCapture", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(startCapture, "RebindService.StartCapture should exist (reflection harness)");

                RebindOutcome? outcome = null;
                int callbacks = 0;
                var callback = new Action<RebindOutcome, BindingSnapshot>((o, _) =>
                {
                    callbacks++;
                    outcome = o;
                });

                Assert.DoesNotThrow(
                    () => startCapture.Invoke(_service, new object[] { "Scratch/Unbound", unbound, 0, callback }),
                    "a capture that cannot start must report through the callback, not escape into the UI");

                Assert.AreEqual(RebindOutcome.Failed, outcome);
                Assert.AreEqual(1, callbacks, "the callback fires exactly once");
                Assert.IsNull(ReadServiceField("_operation"),
                    "_operation must be cleared — a leftover one makes every later rebind take the 「already running」 early exit");
                Assert.IsNull(ReadServiceField("_pending"));
                Assert.IsFalse(_service.IsRebinding);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scratch);
            }
        }

        // ---------- reset persist failure (TASK-H-05 F3: no throw into the UI) ----------

        [Test]
        public void ResetPaths_WhenPersistFails_DoNotThrow_AndResetStaysInMemory()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");
            ApplyOverride(_controls.Pause, 0, "<Keyboard>/p");
            Directory.CreateDirectory(_root);
            _service.Save(); // seed a real file: the failed writes below must leave it intact

            using (LockBindingsFile())
            {
                // (1) 单个 binding（ResetBinding(actionKey, bindingIndex)）
                Assert.DoesNotThrow(() => _service.ResetBinding("Player/Move", 1),
                    "per-binding reset must contain its write failure (button callback contract)");

                // (2) 整个 action（ResetBinding(actionKey)）
                Assert.DoesNotThrow(() => _service.ResetBinding("Player/Interact"),
                    "per-action reset must contain its write failure");
                Assert.AreEqual("<Keyboard>/e", _controls.Interact.bindings[0].effectivePath,
                    "the reset itself stays applied in memory — only the write failed");

                // (3) 整个 asset（ResetAll）
                Assert.DoesNotThrow(() => _service.ResetAll(),
                    "ResetAll must contain its write failure");
                Assert.AreEqual("<Keyboard>/escape", _controls.Pause.bindings[0].effectivePath,
                    "ResetAll still drops every override in memory");
            }

            // The seeded file was never truncated/rewritten: the old overrides still load.
            ZFrameControls reloaded = ZFrameControls.FromResources();
            try
            {
                var reloadedService = new RebindService(reloaded, _root);
                Assert.IsTrue(reloadedService.Load(), "the pre-existing overrides file must still be readable");
                Assert.AreEqual("<Keyboard>/f", reloaded.Interact.bindings[0].effectivePath,
                    "a failed reset write must not truncate the file that was already on disk");
                Assert.AreEqual("<Keyboard>/p", reloaded.Pause.bindings[0].effectivePath);
            }
            finally
            {
                reloaded.Dispose();
            }
        }

        // ---------- capture projection + end-to-end conflict ----------
        [Test]
        public void CaptureMap_ProjectsEveryBinding_WithCompositeFlags()
        {
            IReadOnlyList<BindingSnapshot> map = BindingConflictDetector.CaptureMap(_controls.Player);

            Assert.AreEqual(10, map.Count, "Player map: WASD header + 4 parts + leftStick + 2 Interact + 2 Pause");
            Assert.AreEqual("Player/Move", map[0].ActionKey);
            Assert.IsTrue(map[0].IsComposite, "WASD header is a composite");
            Assert.AreEqual("2DVector", map[0].EffectivePath);
            Assert.IsTrue(map[1].IsCompositePart);
            Assert.AreEqual("<Keyboard>/w", map[1].EffectivePath);
        }

        [Test]
        public void CaptureMap_UsesOverridePath_NotTheAssetPath()
        {
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/f");

            IReadOnlyList<BindingSnapshot> map = BindingConflictDetector.CaptureMap(_controls.Player);
            BindingSnapshot interact = null;
            for (int i = 0; i < map.Count; i++)
            {
                if (map[i].ActionKey == "Player/Interact" && map[i].BindingIndex == 0)
                {
                    interact = map[i];
                }
            }

            Assert.IsNotNull(interact);
            Assert.AreEqual("<Keyboard>/f", interact.EffectivePath, "有效路径 = overridePath ?? path");
        }

        [Test]
        public void RealRebindToAnOccupiedKey_IsDetectedAsConflict()
        {
            // Simulates the end of an interactive rebind: Interact captured Esc,
            // which Pause already owns — the detector must point at Pause.
            ApplyOverride(_controls.Interact, 0, "<Keyboard>/escape");

            IReadOnlyList<BindingSnapshot> map = BindingConflictDetector.CaptureMap(_controls.Player);
            BindingSnapshot conflict = BindingConflictDetector.FindConflict(
                map, _controls.Interact.bindings[0].effectivePath, "Player/Interact", 0);

            Assert.IsNotNull(conflict);
            Assert.AreEqual("Player/Pause", conflict.ActionKey);
        }
    }
}
