using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="SettingsModel"/> (TASK-A-M3-02): snapshot /
    /// revert / reset / immediate-apply semantics and the tolerant
    /// settings.json persistence — always against an INJECTED temp root, the
    /// real Application.persistentDataPath is never touched.
    /// </summary>
    public sealed class SettingsModelTests
    {
        private List<string> _applyLog;
        private SettingsRegistry _registry;
        private string _root;
        private SettingsModel _model;

        [SetUp]
        public void SetUp()
        {
            _applyLog = new List<string>();
            _registry = new SettingsRegistry(new[]
            {
                new SettingsPage("test", "测试", new[]
                {
                    SettingDefinition.Toggle("test.toggle", "开关", true, v => _applyLog.Add($"toggle={v}")),
                    SettingDefinition.Slider("test.slider", "滑条", 0.5f, 0f, 1f, v => _applyLog.Add($"slider={v}")),
                    SettingDefinition.EnumSetting("test.enum", "枚举", 1, new[] { "甲", "乙", "丙" }, v => _applyLog.Add($"enum={v}")),
                }),
            });
            _root = Path.Combine(Path.GetTempPath(), "zframe_settings_tests_" + Guid.NewGuid().ToString("N"));
            _model = new SettingsModel(_registry, _root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // ---------- Set / Get: immediate apply semantics ----------

        [Test]
        public void Set_AppliesImmediately_InOrder_AndMarksDirty()
        {
            _model.Set("test.toggle", false);
            _model.Set("test.slider", 0.25f);
            _model.Set("test.enum", 2);

            CollectionAssert.AreEqual(
                new[] { "toggle=False", "slider=0.25", "enum=2" },
                _applyLog.ToArray(),
                "every Set must invoke the definition's apply callback exactly once, in order");

            Assert.IsTrue(_model.IsDirty);
            Assert.IsFalse(_model.Get<bool>("test.toggle"));
            Assert.AreEqual(0.25f, _model.Get<float>("test.slider"));
            Assert.AreEqual(2, _model.Get<int>("test.enum"));
            Assert.IsTrue(_registry.TryGetDefinition("test.enum", out SettingDefinition enumDef));
            Assert.AreEqual(2, enumDef.CurrentValue, "the display mirror on the definition must track the model");
        }

        [Test]
        public void Set_UnknownId_Throws()
        {
            Assert.Throws<ArgumentException>(() => _model.Set("test.nope", true));
            Assert.Throws<ArgumentException>(() => _model.Get<bool>("test.nope"));
        }

        [Test]
        public void Set_OrGet_OnActionRow_IsRejected()
        {
            var registry = new SettingsRegistry(new[]
            {
                new SettingsPage("p", "P", new[] { SettingDefinition.Action("p.act", "按钮") }),
            });
            var model = new SettingsModel(registry, _root);

            Assert.Throws<InvalidOperationException>(() => model.Set("p.act", true));
            Assert.Throws<InvalidOperationException>(() => model.Get<object>("p.act"));
        }

        [Test]
        public void Set_Slider_ValueClampedToMinMax()
        {
            _model.Set("test.slider", 9f);
            Assert.AreEqual(1f, _model.Get<float>("test.slider"));

            _model.Set("test.slider", -9f);
            Assert.AreEqual(0f, _model.Get<float>("test.slider"));
        }

        [Test]
        public void Set_Enum_OutOfRangeIndex_Throws()
        {
            Assert.Throws<ArgumentException>(() => _model.Set("test.enum", 3));
            Assert.Throws<ArgumentException>(() => _model.Set("test.enum", -1));
        }

        [Test]
        public void Set_WrongTypeForToggle_Throws()
        {
            Assert.Throws<ArgumentException>(() => _model.Set("test.toggle", 1.5f));
        }

        [Test]
        public void Set_NullValue_Throws()
        {
            Assert.Throws<ArgumentException>(() => _model.Set<string>("test.toggle", null));
        }

        // ---------- snapshot / revert / reset ----------

        [Test]
        public void Snapshot_Revert_RestoresValues_AndReapplies()
        {
            _model.Set("test.toggle", false);           // apply: toggle=False
            _model.TakeSnapshot();
            _applyLog.Clear();
            _model.Set("test.toggle", true);            // apply: toggle=True (dirty)
            _model.Set("test.slider", 1f);

            Assert.IsTrue(_model.IsDirty);

            _model.RevertToSnapshot();

            CollectionAssert.AreEqual(
                new[] { "toggle=True", "slider=1", "toggle=False", "slider=0.5", "enum=1" },
                _applyLog.ToArray(),
                "revert re-applies EVERY snapshot value through the definitions (live engine state rolls back too)");
            Assert.IsFalse(_model.Get<bool>("test.toggle"));
            Assert.AreEqual(0.5f, _model.Get<float>("test.slider"));
            Assert.IsFalse(_model.IsDirty, "after revert the state equals the snapshot again");
        }

        [Test]
        public void Revert_WithoutSnapshot_IsNoOp()
        {
            Assert.DoesNotThrow(() => _model.RevertToSnapshot());
            Assert.IsFalse(_model.IsDirty);
        }

        [Test]
        public void ResetAll_RestoresDefaults_AndApplies_AndMarksDirty()
        {
            _model.Set("test.toggle", false);
            _model.Set("test.enum", 0);
            _applyLog.Clear();

            _model.ResetAll();

            CollectionAssert.AreEqual(
                new[] { "toggle=True", "slider=0.5", "enum=1" },
                _applyLog.ToArray(),
                "ResetAll must apply every definition's default, page order");
            Assert.IsTrue(_model.Get<bool>("test.toggle"));
            Assert.AreEqual(1, _model.Get<int>("test.enum"));
            Assert.IsTrue(_model.IsDirty, "reset changes live state — persisting still needs Save");
        }

        // ---------- persistence (injected temp root) ----------

        [Test]
        public void FilePath_LivesUnderInjectedRoot_NeverRealUserDirectory()
        {
            StringAssert.StartsWith(Path.GetTempPath(), _model.FilePath);
            StringAssert.DoesNotContain(Application.persistentDataPath, _model.FilePath);
            StringAssert.Contains("settings.json", _model.FilePath);
        }

        [Test]
        public void Save_WritesVersionAndSettings_AndClearsDirty()
        {
            _model.Set("test.slider", 0.75f);
            _model.Save();

            Assert.IsFalse(_model.IsDirty);
            Assert.IsTrue(File.Exists(_model.FilePath));

            JObject root = JObject.Parse(File.ReadAllText(_model.FilePath));
            Assert.AreEqual(SettingsModel.CurrentVersion, root.Value<int>("version"));
            Assert.AreEqual(true, root["settings"]["test.toggle"].Value<bool>());
            Assert.AreEqual(0.75f, root["settings"]["test.slider"].Value<float>());
            Assert.AreEqual(1, root["settings"]["test.enum"].Value<int>());
            Assert.IsNull(root["settings"]["p.act"], "Action rows must never be persisted");
        }

        [Test]
        public void Load_Roundtrip_RestoresValues_AndAppliesAtBoot()
        {
            _model.Set("test.toggle", false);
            _model.Set("test.slider", 0.25f);
            _model.Set("test.enum", 2);
            _model.Save();

            var model2 = new SettingsModel(_registry, _root);
            int applied = model2.Load();

            Assert.AreEqual(3, applied, "every persisted known value must be applied on load");
            Assert.IsFalse(model2.IsDirty);
            Assert.IsFalse(model2.Get<bool>("test.toggle"));
            Assert.AreEqual(0.25f, model2.Get<float>("test.slider"));
            Assert.AreEqual(2, model2.Get<int>("test.enum"));
        }

        [Test]
        public void Load_UnknownIds_AreToleratedAndSkipped()
        {
            _model.Set("test.enum", 2);
            _model.Save();

            // Simulate a future version persisting ids this build doesn't know.
            JObject root = JObject.Parse(File.ReadAllText(_model.FilePath));
            root["settings"]["legacy.removedOption"] = 7;
            root["settings"]["future.newThing"] = "whatever";
            File.WriteAllText(_model.FilePath, root.ToString());

            int applied = _model.Load();

            Assert.AreEqual(3, applied, "the two legacy ids are skipped, all three known persisted values still applied");
            Assert.AreEqual(2, _model.Get<int>("test.enum"));
            Assert.IsFalse(_registry.TryGetDefinition("legacy.removedOption", out _), "unknown ids must not enter the registry/model");
        }

        [Test]
        public void Load_CorruptFile_IsTolerated_ReturnsZero_KeepsDefaults()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_model.FilePath, "{ this is not json ");

            Assert.AreEqual(0, _model.Load());
            Assert.IsTrue(_model.Get<bool>("test.toggle"), "defaults kept");
            Assert.IsFalse(_model.IsDirty);
        }

        [Test]
        public void Load_MissingFile_ReturnsZero()
        {
            Assert.AreEqual(0, _model.Load());
            Assert.IsFalse(File.Exists(_model.FilePath));
        }

        [Test]
        public void Load_NewerVersion_IsIgnored()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_model.FilePath, new JObject
            {
                ["version"] = SettingsModel.CurrentVersion + 10,
                ["settings"] = new JObject { ["test.toggle"] = false },
            }.ToString());

            Assert.AreEqual(0, _model.Load());
            Assert.IsTrue(_model.Get<bool>("test.toggle"), "newer schema versions must not half-load");
        }

        [Test]
        public void Load_WrongTypedValue_SkipsOnlyThatItem()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_model.FilePath, new JObject
            {
                ["version"] = 1,
                ["settings"] = new JObject
                {
                    ["test.toggle"] = 42,          // bool stored as number → skipped
                    ["test.slider"] = "not-a-number", // wrong type → skipped
                    ["test.enum"] = 2,             // correct → applied
                },
            }.ToString());

            Assert.AreEqual(1, _model.Load());
            Assert.AreEqual(2, _model.Get<int>("test.enum"));
            Assert.IsTrue(_model.Get<bool>("test.toggle"), "wrong-typed item fell back to default");
        }

        [Test]
        public void Save_ThenSet_DirtiesAgain()
        {
            _model.Save();
            Assert.IsFalse(_model.IsDirty);
            _model.Set("test.toggle", false);
            Assert.IsTrue(_model.IsDirty);
        }

        [Test]
        public void Set_SameValue_IsNoOp_NotDirty_NoApplyCallback()
        {
            // TASK-A-R9-11: a value that equals the current one is NOT a change.
            // UGUI's editor-only Toggle.Rebuild re-announces the live control
            // value during canvas rebuilds (DefaultControls forces one mid-build
            // through Undo.SetTransformParent) — that echo must not re-apply or
            // re-dirty the model, otherwise 撤销更改 + the row rebuild it
            // triggers would immediately re-dirty the panel.
            _applyLog.Clear();
            Assert.IsTrue(_model.Get<bool>("test.toggle"), "precondition: default is true");

            _model.Set("test.toggle", true); // echo of the current value

            Assert.IsFalse(_model.IsDirty, "a Set that changes nothing must not dirty the model");
            CollectionAssert.IsEmpty(_applyLog, "no apply callback for an unchanged value");
        }

        // ---------- atomic write (TASK-H-01 D2) ----------

        [Test]
        public void Save_WritesThroughATempFile_AndLeavesNoResidue()
        {
            _model.Set("test.slider", 0.75f);
            _model.Save();

            string[] files = Directory.GetFiles(_root);
            Assert.AreEqual(1, files.Length,
                "the atomic write must leave exactly one file behind (the .tmp is renamed, not copied)");
            StringAssert.EndsWith("settings.json", files[0]);

            var reloaded = new SettingsModel(_registry, _root);
            Assert.AreEqual(3, reloaded.Load(), "the renamed file must still be a complete, readable settings.json (3 value rows)");
            Assert.AreEqual(0.75f, reloaded.Get<float>("test.slider"));
        }

        [Test]
        public void Save_WhenTheWriteFails_KeepsThePreviousMainFileIntact()
        {
            _model.Set("test.slider", 0.25f);
            _model.Save(); // first generation on disk

            // Inject the failure the atomic write exists for: the temp path cannot be
            // created (a directory squatting on it is the portable stand-in for a full
            // disk or a lost handle). The main file must stay exactly as it was.
            string blockedTemp = _model.FilePath + ".tmp";
            Directory.CreateDirectory(blockedTemp);

            try
            {
                _model.Set("test.slider", 1f);

                bool threw = false;
                try
                {
                    _model.Save();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    threw = true; // the failure must surface — the caller decides what to do
                }

                Assert.IsTrue(threw, "a blocked temp path must fail the save instead of silently dropping it");
                Assert.IsTrue(_model.IsDirty, "a failed save must keep the model dirty so the next attempt retries");

                var reloaded = new SettingsModel(_registry, _root);
                Assert.AreEqual(3, reloaded.Load(), "the previous settings.json must still be readable (3 value rows)");
                Assert.AreEqual(0.25f, reloaded.Get<float>("test.slider"),
                    "an interrupted write must never truncate the file already on disk");
            }
            finally
            {
                Directory.Delete(blockedTemp);
            }
        }

        [Test]
        public void Save_Twice_KeepsASingleCompleteFile()
        {
            _model.Set("test.slider", 0.1f);
            _model.Save();
            _model.Set("test.slider", 0.9f);
            _model.Save();

            Assert.IsFalse(_model.IsDirty);
            Assert.AreEqual(1, Directory.GetFiles(_root).Length, "successive atomic saves must not accumulate files");

            var reloaded = new SettingsModel(_registry, _root);
            Assert.AreEqual(3, reloaded.Load());
            Assert.AreEqual(0.9f, reloaded.Get<float>("test.slider"), "the second save must win (rename replaces the main file)");
        }
    }
}
