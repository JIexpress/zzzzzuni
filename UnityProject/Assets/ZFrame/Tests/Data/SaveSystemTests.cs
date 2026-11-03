using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Data.Save;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// EditMode tests for the save system (task A-M1-03). Every fixture runs
    /// against an injected temp root directory — the real
    /// Application.persistentDataPath is never touched.
    /// </summary>
    public class SaveSystemTests
    {
        private string _root;
        private SaveSystem _system;
        private FakePlayer _player;
        private FakeInventory _inventory;

        private sealed class FakePlayer : ISaveable
        {
            public int LoadCalls { get; private set; }
            public int Health { get; set; }
            public float PositionX { get; set; }

            public string SaveKey
            {
                get { return "player"; }
            }

            public void Save(SaveWriter writer)
            {
                writer.Set("health", Health);
                writer.Set("posX", PositionX);
            }

            public void Load(SaveReader reader)
            {
                LoadCalls++;
                Health = reader.GetInt("health", 1);
                PositionX = reader.GetFloat("posX", 0f);
            }
        }

        private sealed class FakeInventory : ISaveable
        {
            public bool WasLoaded { get; private set; }
            public List<string> Items { get; } = new List<string>();

            public string SaveKey
            {
                get { return "inventory"; }
            }

            public void Save(SaveWriter writer)
            {
                var items = new JArray();
                foreach (string item in Items)
                {
                    items.Add(item);
                }

                writer.Set("items", items);
                writer.Set("count", Items.Count);
            }

            public void Load(SaveReader reader)
            {
                WasLoaded = true;
                Items.Clear();
                if (reader.TryGet("items", out JToken token) && token is JArray array)
                {
                    foreach (JToken entry in array)
                    {
                        Items.Add((string)entry);
                    }
                }
            }
        }

        /// <summary>Module whose Load always throws — used to prove one bad module cannot sink the whole load.</summary>
        private sealed class ExplodingModule : ISaveable
        {
            public string SaveKey
            {
                get { return "exploding"; }
            }

            public void Save(SaveWriter writer)
            {
                writer.Set("marker", 1);
            }

            public void Load(SaveReader reader)
            {
                throw new InvalidOperationException("module exploded on purpose");
            }
        }

        [SetUp]
        public void SetUp()
        {
            // Injected temp root — never the real persistentDataPath.
            _root = Path.Combine(Path.GetTempPath(), "zframe_save_tests_" + Guid.NewGuid().ToString("N"));
            _system = new SaveSystem(_root);
            _player = new FakePlayer { Health = 42, PositionX = 1.5f };
            _inventory = new FakeInventory();
            _inventory.Items.Add("sword");
            _inventory.Items.Add("potion");
            _system.Register(_player);
            _system.Register(_inventory);
        }

        [TearDown]
        public void TearDown()
        {
            ZFrameData.Shutdown(); // clean Services state for other suites
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private void SaveWithHealth(int slot, int health)
        {
            _player.Health = health;
            _system.SaveToSlot(slot);
        }

        private static string ReadText(string path)
        {
            return File.ReadAllText(path);
        }

        private static string[] CorruptFilesFor(string mainPath)
        {
            string dir = Path.GetDirectoryName(mainPath);
            string name = Path.GetFileName(mainPath);
            if (!Directory.Exists(dir))
            {
                return new string[0];
            }

            // Covers both "slot_0.json.corrupt_*" and "slot_0.json.bak1.corrupt_*".
            return Directory.GetFiles(dir, name + "*.corrupt_*");
        }

        // ---------- roundtrip ----------

        [Test]
        public void SaveLoad_Roundtrip_RestoresBothModules()
        {
            _system.SaveToSlot(0);

            // Fresh system + fresh module instances simulate a new session.
            var player2 = new FakePlayer();
            var inventory2 = new FakeInventory();
            var system2 = new SaveSystem(_root);
            system2.Register(player2);
            system2.Register(inventory2);

            SaveData loaded = system2.LoadFromSlot(0);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(SaveSystem.CurrentVersion, loaded.Version);
            Assert.AreEqual(42, player2.Health);
            Assert.AreEqual(1.5f, player2.PositionX);
            Assert.AreEqual(new[] { "sword", "potion" }, inventory2.Items.ToArray());
        }

        [Test]
        public void SaveSystem_SlotCount_IsThree()
        {
            Assert.AreEqual(3, _system.SlotCount);
            Assert.AreEqual(3, SaveSystem.DefaultSlotCount);
        }

        // ---------- atomic write + backup rolling ----------

        [Test]
        public void Save_LeavesNoTmpFileBehind()
        {
            _system.SaveToSlot(0);

            Assert.IsTrue(File.Exists(_system.SlotPath(0)));
            Assert.IsFalse(File.Exists(_system.SlotPath(0) + ".tmp"));
        }

        [Test]
        public void Save_ThreeConsecutiveSaves_BackupsRollCorrectly()
        {
            SaveWithHealth(0, 1);
            SaveWithHealth(0, 2);
            SaveWithHealth(0, 3);

            string main = _system.SlotPath(0);
            Assert.IsFalse(File.Exists(main + ".bak3"));

            // bak2 = first save, bak1 = second save, main = third save.
            JObject mainJson = JObject.Parse(ReadText(main));
            JObject bak1Json = JObject.Parse(ReadText(main + ".bak1"));
            JObject bak2Json = JObject.Parse(ReadText(main + ".bak2"));

            Assert.AreEqual(3, ((JObject)mainJson["modules"]["player"])["health"].Value<int>());
            Assert.AreEqual(2, ((JObject)bak1Json["modules"]["player"])["health"].Value<int>());
            Assert.AreEqual(1, ((JObject)bak2Json["modules"]["player"])["health"].Value<int>());
        }

        /// <summary>
        /// TASK-A-AUDIT-03 (B-1): the payload must be written (and flushed) to .tmp BEFORE
        /// the old main rolls into .bak1. The old order moved main away first, so any write
        /// failure (disk full / permissions / AV lock) left the slot with no main file.
        ///
        /// The failure is injected for real — no test-only seam in SaveSystem: a FILE cannot
        /// be created where a DIRECTORY of the same name exists, so WriteDurable throws at
        /// the .tmp stage while main and .bak1 are still untouched.
        ///
        /// Mutation-verified: restoring the old order (roll first, then WriteDurable) turns
        /// this case red on <c>File.Exists(main)</c>.
        /// </summary>
        [Test]
        public void SaveToSlot_TmpWriteFails_KeepsThePreviousMainReadable()
        {
            SaveWithHealth(0, 7);
            string main = _system.SlotPath(0);
            string tmp = main + ".tmp";
            Assert.IsTrue(File.Exists(main), "precondition: the first save must have produced a main file");

            Directory.CreateDirectory(tmp); // <- the injected write failure
            _player.Health = 99;            // the value the failing save would have written

            Exception failure = Assert.Catch<Exception>(
                () => _system.SaveToSlot(0),
                "a failing .tmp write must surface to the caller, never be swallowed");
            Assert.IsNotNull(failure);
            Assert.IsTrue(failure is IOException || failure is UnauthorizedAccessException,
                "unexpected failure type at the .tmp stage: " + failure.GetType().FullName);

            Assert.IsTrue(File.Exists(main),
                "the previous main save disappeared after a failed write — the old order moved main into .bak1 " +
                "BEFORE writing .tmp, so a write failure left the slot without a main file");
            Assert.IsTrue(File.Exists(main) || File.Exists(main + ".bak1"),
                "a failed save must leave at least one readable generation (main or .bak1)");

            JObject mainJson = JObject.Parse(ReadText(main));
            Assert.AreEqual(7, ((JObject)mainJson["modules"]["player"])["health"].Value<int>(),
                "main must still hold the PREVIOUS generation (the failing save carried health=99)");

            SaveData reloaded = _system.LoadFromSlot(0);
            Assert.IsNotNull(reloaded, "the slot must still load after a failed write");
            Assert.AreEqual(7, _player.Health, "loading after the failure must restore the previous generation");
        }

        // ---------- corruption containment chain ----------

        [Test]
        public void Load_CorruptMain_ReturnsNull_QuarantinesFile_FiresEvent()
        {
            _system.SaveToSlot(0);
            string main = _system.SlotPath(0);
            File.WriteAllText(main, "{ this is not json ");

            SaveCorruptEvent? received = null;
            using (EventBus.Subscribe<SaveCorruptEvent>(e => received = e))
            {
                SaveData loaded = _system.LoadFromSlot(0);

                Assert.IsNull(loaded);
                Assert.IsFalse(File.Exists(main), "corrupt main should have been renamed away");
                string[] corruptFiles = CorruptFilesFor(main);
                Assert.AreEqual(1, corruptFiles.Length, "exactly one .corrupt_* quarantine file expected");
                StringAssert.Contains(".corrupt_", corruptFiles[0]);
                Assert.IsNotNull(received, "SaveCorruptEvent must fire via EventBus");
                Assert.AreEqual(0, received.Value.Slot);
                StringAssert.Contains("invalid JSON", received.Value.Reason);
                Assert.AreEqual(corruptFiles[0], received.Value.QuarantinedPath);
            }
        }

        [Test]
        public void Load_CorruptMain_RecoversFromBak1_InSameCall()
        {
            SaveWithHealth(0, 1); // -> bak1 after next save
            SaveWithHealth(0, 2); // -> main
            string main = _system.SlotPath(0);
            File.WriteAllText(main, "not json at all");

            // Recovery happens inside a single call: gameplay loads once and would treat
            // a null as "empty slot" (starting fresh and overwriting the backups).
            SaveData recovered = _system.LoadFromSlot(0);

            Assert.IsNotNull(recovered, "corrupt main must fall back to .bak1 within the same call");
            Assert.AreEqual(1, _player.Health, "player state must be restored from .bak1");
            Assert.IsFalse(File.Exists(main), "corrupt main should have been quarantined");
        }

        [Test]
        public void Load_CorruptMainAndBak1_RecoversFromBak2_InSameCall()
        {
            SaveWithHealth(0, 1); // -> bak2
            SaveWithHealth(0, 2); // -> bak1
            SaveWithHealth(0, 3); // -> main
            string main = _system.SlotPath(0);
            File.WriteAllText(main, "garbage");
            File.WriteAllText(main + ".bak1", "also garbage");

            SaveData recovered = _system.LoadFromSlot(0);

            Assert.IsNotNull(recovered, "two corrupt generations must still recover from .bak2");
            Assert.AreEqual(1, _player.Health, "player state must be restored from .bak2");
            Assert.AreEqual(2, CorruptFilesFor(main).Length, "main and bak1 both quarantined");
        }

        [Test]
        public void GetSlotInfo_CorruptMain_FallsBackToBackup()
        {
            SaveWithHealth(0, 1); // -> bak1 after next save
            SaveWithHealth(0, 2); // -> main
            string main = _system.SlotPath(0);
            File.WriteAllText(main, "{ broken ");

            // The main-menu "Continue" button is driven by this query. A corrupt main file
            // must not hide an intact .bak1 - otherwise the player cannot reach the backup
            // at all and the next auto-save wipes it.
            SlotInfo? info = _system.GetSlotInfo(0);

            Assert.IsTrue(info.HasValue, "an intact .bak1 must keep the slot visible to the main menu");
            Assert.AreEqual(0, info.Value.Slot);
        }

        [Test]
        public void SaveToSlot_ConcurrentCalls_SerialisedByWriteLock()
        {
            _system.SaveToSlot(0); // seed a main file so the rolling-backup path is exercised

            var errors = new List<Exception>();
            var threads = new List<System.Threading.Thread>();
            for (int i = 0; i < 4; i++)
            {
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        _system.SaveToSlot(0);
                    }
                    catch (Exception e)
                    {
                        lock (errors)
                        {
                            errors.Add(e);
                        }
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (System.Threading.Thread thread in threads)
            {
                thread.Join();
            }

            Assert.IsEmpty(errors, "concurrent saves must be serialised, not throw on the shared .tmp path");
            Assert.IsFalse(File.Exists(_system.SlotPath(0) + ".tmp"), "no .tmp leftovers after the race");
            Assert.IsNotNull(_system.LoadFromSlot(0), "slot must still parse after concurrent saves");
        }

        [Test]
        public void Load_ModuleThrows_IsIsolated_OtherModulesStillRestore()
        {
            _system.Register(new ExplodingModule());
            SaveWithHealth(0, 5);

            // The JSON is valid; only one module cannot handle its section. Throwing out of
            // LoadFromSlot would leave a half-applied load and hide the failure from gameplay.
            SaveData data = _system.LoadFromSlot(0);

            Assert.IsNotNull(data, "a single exploding module must not fail the whole load");
            Assert.AreEqual(5, _player.Health, "healthy modules must still restore");
        }

        [Test]
        public void Load_EmptySlot_ReturnsNull()
        {
            Assert.IsNull(_system.LoadFromSlot(2));
            Assert.IsNull(_system.GetSlotInfo(2));
        }

        // ---------- version migration ----------

        [Test]
        public void Load_V1Save_MigratesToCurrent_AndRestoresModules()
        {
            // Handcraft a v1 save with the OLD field layout (hp instead of health).
            string main = _system.SlotPath(0);
            Directory.CreateDirectory(_root);
            File.WriteAllText(main, new JObject
            {
                ["version"] = 1,
                ["meta"] = new JObject { ["displayName"] = "old-hero" },
                ["modules"] = new JObject
                {
                    ["player"] = new JObject { ["hp"] = 7 },
                },
            }.ToString());

            _system.RegisterMigration(new TestMigration(1, data =>
            {
                // v1 -> v2: rename hp -> health inside every module section.
                JObject modules = (JObject)data["modules"];
                JObject player = (JObject)modules["player"];
                if (player != null && player["hp"] != null)
                {
                    player["health"] = player["hp"];
                    player.Remove("hp");
                }

                return data;
            }));

            SaveData loaded = _system.LoadFromSlot(0);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(SaveSystem.CurrentVersion, loaded.Version);
            Assert.AreEqual(7, _player.Health);
            Assert.AreEqual("old-hero", loaded.Meta.DisplayName);
        }

        [Test]
        public void Load_UnknownNewerVersion_IsRejected_AndQuarantined()
        {
            string main = _system.SlotPath(0);
            Directory.CreateDirectory(_root);
            File.WriteAllText(main, new JObject
            {
                ["version"] = SaveSystem.CurrentVersion + 100,
                ["meta"] = new JObject(),
                ["modules"] = new JObject(),
            }.ToString());

            SaveCorruptEvent? received = null;
            using (EventBus.Subscribe<SaveCorruptEvent>(e => received = e))
            {
                Assert.IsNull(_system.LoadFromSlot(0), "newer save versions must be rejected");
                Assert.IsFalse(File.Exists(main));
                Assert.AreEqual(1, CorruptFilesFor(main).Length);
                Assert.IsNotNull(received);
                StringAssert.Contains("newer than supported", received.Value.Reason);
            }
        }

        [Test]
        public void Load_MissingMigrationStep_FailsLikeCorrupt()
        {
            string main = _system.SlotPath(0);
            Directory.CreateDirectory(_root);
            File.WriteAllText(main, new JObject
            {
                ["version"] = 1,
                ["meta"] = new JObject(),
                ["modules"] = new JObject(),
            }.ToString());

            SaveCorruptEvent? received = null;
            using (EventBus.Subscribe<SaveCorruptEvent>(e => received = e))
            {
                Assert.IsNull(_system.LoadFromSlot(0));
                Assert.IsNotNull(received);
                StringAssert.Contains("no migration registered", received.Value.Reason);
            }
        }

        [Test]
        public void Load_ThrowingMigration_FailsLikeCorrupt()
        {
            string main = _system.SlotPath(0);
            Directory.CreateDirectory(_root);
            File.WriteAllText(main, new JObject
            {
                ["version"] = 1,
                ["meta"] = new JObject(),
                ["modules"] = new JObject(),
            }.ToString());

            _system.RegisterMigration(new TestMigration(1, _ => throw new InvalidOperationException("boom")));

            SaveCorruptEvent? received = null;
            using (EventBus.Subscribe<SaveCorruptEvent>(e => received = e))
            {
                Assert.IsNull(_system.LoadFromSlot(0));
                Assert.IsNotNull(received);
                StringAssert.Contains("boom", received.Value.Reason);
                Assert.AreEqual(1, CorruptFilesFor(main).Length);
            }
        }

        private sealed class TestMigration : IMigration
        {
            private readonly Func<JObject, JObject> _step;

            public TestMigration(int fromVersion, Func<JObject, JObject> step)
            {
                FromVersion = fromVersion;
                _step = step;
            }

            public int FromVersion { get; }

            public JObject Migrate(JObject data)
            {
                return _step(data);
            }
        }

        // ---------- slot metadata ----------

        [Test]
        public void GetSlotInfo_ReflectsHeaderMetadata()
        {
            _system.CurrentDisplayName = "hero";
            _system.CurrentPlaytimeSeconds = 123.5;
            DateTime before = DateTime.UtcNow.AddSeconds(-5);
            _system.SaveToSlot(1);
            DateTime after = DateTime.UtcNow.AddSeconds(5);

            SlotInfo? info = _system.GetSlotInfo(1);

            Assert.IsTrue(info.HasValue);
            Assert.AreEqual(1, info.Value.Slot);
            Assert.AreEqual(SaveSystem.CurrentVersion, info.Value.Version);
            Assert.AreEqual("hero", info.Value.DisplayName);
            Assert.AreEqual(123.5, info.Value.PlaytimeSeconds, 0.001);
            Assert.GreaterOrEqual(info.Value.SavedAtUtc, before);
            Assert.LessOrEqual(info.Value.SavedAtUtc, after);
        }

        [Test]
        public void GetSlotInfo_EmptySlot_ReturnsNull()
        {
            Assert.IsFalse(_system.GetSlotInfo(0).HasValue);
        }

        [Test]
        public void DeleteSlot_RemovesMainAndBackups()
        {
            SaveWithHealth(0, 1);
            SaveWithHealth(0, 2);
            string main = _system.SlotPath(0);
            Assert.IsTrue(File.Exists(main));
            Assert.IsTrue(File.Exists(main + ".bak1"));

            _system.DeleteSlot(0);

            Assert.IsFalse(File.Exists(main));
            Assert.IsFalse(File.Exists(main + ".bak1"));
            Assert.IsFalse(File.Exists(main + ".bak2"));
            Assert.IsFalse(_system.GetSlotInfo(0).HasValue);
            Assert.IsNull(_system.LoadFromSlot(0));
        }

        // ---------- registration discipline ----------

        [Test]
        public void Register_DuplicateSaveKey_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => _system.Register(new FakePlayer()));
        }

        [Test]
        public void Register_NullOrEmptySaveKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => _system.Register(new NullKeyModule()));
        }

        private sealed class NullKeyModule : ISaveable
        {
            public string SaveKey
            {
                get { return null; }
            }

            public void Save(SaveWriter writer)
            {
            }

            public void Load(SaveReader reader)
            {
            }
        }

        [Test]
        public void RegisterMigration_DuplicateFromVersion_Throws()
        {
            _system.RegisterMigration(new TestMigration(1, d => d));
            Assert.Throws<InvalidOperationException>(() => _system.RegisterMigration(new TestMigration(1, d => d)));
        }

        [Test]
        public void Unregister_RemovesModule()
        {
            Assert.IsTrue(_system.Unregister("inventory"));
            _system.SaveToSlot(0);

            JObject root = JObject.Parse(ReadText(_system.SlotPath(0)));
            Assert.IsNotNull(root["modules"]["player"]);
            Assert.IsNull(root["modules"]["inventory"]);
        }

        // ---------- null safety ----------

        [Test]
        public void SaveReader_MissingFields_ReturnDefaults()
        {
            var reader = new SaveReader(new JObject());

            Assert.IsFalse(reader.TryGet("anything", out _));
            Assert.AreEqual("dflt", reader.GetString("s", "dflt"));
            Assert.AreEqual(-7, reader.GetInt("i", -7));
            Assert.AreEqual(2.5f, reader.GetFloat("f", 2.5f));
            Assert.AreEqual(9.75, reader.GetDouble("d", 9.75));
            Assert.IsTrue(reader.GetBool("b", true));
        }

        [Test]
        public void SaveReader_NullReader_And_JsonNull_AreSafe()
        {
            var empty = new SaveReader(null);
            Assert.AreEqual(5, empty.GetInt("i", 5));

            var nullJson = new SaveReader(new JObject { ["v"] = JValue.CreateNull() });
            Assert.IsFalse(nullJson.TryGet("v", out _));
            Assert.AreEqual("x", nullJson.GetString("v", "x"));
        }

        [Test]
        public void SaveReader_WrongTypedField_ReturnsDefault()
        {
            var reader = new SaveReader(new JObject
            {
                ["num"] = "not-a-number",
                ["flag"] = "not-a-bool",
            });

            Assert.AreEqual(3, reader.GetInt("num", 3));
            Assert.IsFalse(reader.GetBool("flag", false));
        }

        [Test]
        public void SaveWriter_SetNull_StoresJsonNull()
        {
            var section = new JObject();
            var writer = new SaveWriter(section);
            writer.Set("gone", (string)null);

            Assert.AreEqual(JTokenType.Null, section["gone"].Type);
        }

        [Test]
        public void Load_MissingModuleSection_LoadsWithEmptyReader_NoThrow()
        {
            // Save contains only "player"; then a new session also registers
            // "inventory", whose section is absent from the old save.
            _system.Unregister("inventory");
            _system.SaveToSlot(0);

            var inventory2 = new FakeInventory();
            var system2 = new SaveSystem(_root);
            system2.Register(new FakePlayer());
            system2.Register(inventory2);

            Assert.DoesNotThrow(() => system2.LoadFromSlot(0));
            Assert.IsTrue(inventory2.WasLoaded);
            Assert.AreEqual(0, inventory2.Items.Count);
        }

        // ---------- slot range / auto-save ----------

        [Test]
        public void Slot_OutOfRange_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _system.SaveToSlot(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => _system.SaveToSlot(SaveSystem.DefaultSlotCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => _system.LoadFromSlot(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => _system.GetSlotInfo(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => _system.DeleteSlot(3));
        }

        [Test]
        public void AutoSave_WritesConfiguredSlot()
        {
            _system.AutoSaveSlot = 2;
            _system.AutoSave();

            Assert.IsTrue(File.Exists(_system.SlotPath(2)));
            Assert.IsFalse(File.Exists(_system.SlotPath(0)));
        }

        [Test]
        public void AutoSave_OutOfRangeSlot_NoThrowNoWrite()
        {
            _system.AutoSaveSlot = 5;
            Assert.DoesNotThrow(() => _system.AutoSave());
            Assert.IsFalse(File.Exists(_system.SlotPath(5)));
        }

        // ---------- boot registration ----------

        [Test]
        public void ZFrameData_Init_RegistersSaveSystem_Idempotently()
        {
            ZFrameData.Init();
            Assert.IsTrue(ZFrameData.IsInitialized);
            Assert.IsTrue(Services.TryGet(out ISaveSystem registered));

            Assert.DoesNotThrow(() => ZFrameData.Init()); // idempotent

            ZFrameData.Shutdown();
            Assert.IsFalse(Services.TryGet(out ISaveSystem _));
        }

        [Test]
        public void DefaultRoot_IsPersistentDataPath_WhenNotInjected()
        {
            // Default root must live under persistentDataPath (never arbitrary cwd).
            var saveSystem = new SaveSystem();
            StringAssert.Contains(Application.persistentDataPath, saveSystem.SlotPath(0));
        }
    }
}
