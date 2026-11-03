using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using ZFrame.View;

namespace ZFrame.View.Input.Tests
{
    /// <summary>
    /// EditMode tests of TASK-A-M3-04 — 移动键四向重绑定. The gap this suite
    /// pins: <c>Player/Move</c> is a 2DVector composite (W/A/S/D parts +
    /// gamepad leftStick), so "one row per action" only ever rebound the W
    /// part. The 操作 tab now ships FOUR direction rows, each carrying the
    /// bindingIndex of its own composite part:
    ///
    /// 1. the registry exposes 6 binding rows (四向 + 交互 + 暂停) + a read-only
    ///    手柄 note + the map-wide reset;
    /// 2. every direction index is RESOLVED FROM THE ASSET BY PATH — asserted
    ///    against <see cref="RebindService.FindBindingIndexByPath"/> and against
    ///    a synthetic composite whose parts are declared in a DIFFERENT order
    ///    (a hardcoded 1/2/3/4 table would fail that one);
    /// 3. rebinding 上 onto A is a real cross-direction conflict with 左
    ///    (<see cref="BindingConflictDetector"/>, pure logic — no device, no
    ///    play mode);
    /// 4. per-row reset drops ONE direction, map-wide reset restores all four.
    /// </summary>
    public sealed class MoveRebindTests
    {
        private const string MoveAction = "Player/Move";
        private const string UpPath = "<Keyboard>/w";
        private const string LeftPath = "<Keyboard>/a";
        private const string DownPath = "<Keyboard>/s";
        private const string RightPath = "<Keyboard>/d";

        private ZFrameControls _controls;
        private RebindService _service;
        private SettingsRegistry _registry;
        private string _root;
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _controls = ZFrameControls.FromResources();
            Assert.IsNotNull(_controls, "the committed ZFrameControls.inputactions asset must load from Resources");

            _root = Path.Combine(Path.GetTempPath(), "zframe_move_rebind_tests_" + Guid.NewGuid().ToString("N"));
            _service = new RebindService(_controls, _root);
            _registry = SettingsRegistry.BuildDefaults();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
                _host = null;
            }

            _service?.Dispose();
            _controls?.Dispose();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RectTransform Host()
        {
            if (_host == null)
            {
                _host = new GameObject("MoveRebindTestHost", typeof(RectTransform));
            }

            return _host.GetComponent<RectTransform>();
        }

        private static void ApplyOverride(InputAction action, int bindingIndex, string path)
        {
            // ApplyBindingOverride copies overridePath/… — a bare `path` would be ignored.
            action.ApplyBindingOverride(bindingIndex, new InputBinding { overridePath = path });
        }

        /// <summary>The four 移动 rows in display order (上/左/下/右).</summary>
        private SettingDefinition[] MoveRows()
        {
            Assert.IsTrue(_registry.TryGetPage("controls", out SettingsPage controls));
            return controls.Settings
                .Where(s => s.Type == SettingType.Binding && s.ActionKey == MoveAction)
                .ToArray();
        }

        private static string AssetPathAt(ZFrameControls controls, int bindingIndex)
        {
            Assert.GreaterOrEqual(bindingIndex, 0, "direction row must carry a resolved binding index");
            Assert.Less(bindingIndex, controls.Move.bindings.Count);
            return controls.Move.bindings[bindingIndex].path;
        }

        // ---------- 1. six binding rows ----------

        [Test]
        public void ControlsPage_HasSixBindingRows_WithTheFourMoveDirections()
        {
            Assert.IsTrue(_registry.TryGetPage("controls", out SettingsPage controls));

            SettingDefinition[] bindingRows = controls.Settings
                .Where(s => s.Type == SettingType.Binding)
                .ToArray();

            Assert.AreEqual(6, bindingRows.Length, "移动四向 + 交互 + 暂停 = 6 个键位行");

            CollectionAssert.AreEqual(
                new[] { "移动·上", "移动·左", "移动·下", "移动·右" },
                MoveRows().Select(s => s.DisplayName).ToArray(),
                "移动必须按方向拆成四行（改上方向不能再牵连其它方向）");

            CollectionAssert.AreEqual(
                new[] { "controls.bind.MoveUp", "controls.bind.MoveLeft", "controls.bind.MoveDown", "controls.bind.MoveRight" },
                MoveRows().Select(s => s.Id).ToArray(),
                "四个方向各自一个稳定 setting id");

            CollectionAssert.AreEqual(
                new[] { "交互", "暂停" },
                bindingRows.Where(s => s.ActionKey != MoveAction).Select(s => s.DisplayName).ToArray());
        }

        // ---------- 2. indices resolved by path, not hardcoded ----------

        [Test]
        public void MoveDirectionRows_ResolveTheirBindingIndexByAssetPath()
        {
            SettingDefinition[] rows = MoveRows();
            string[] expectedPaths = { UpPath, LeftPath, DownPath, RightPath };

            for (int i = 0; i < rows.Length; i++)
            {
                int index = rows[i].BindingIndex;
                Assert.AreEqual(expectedPaths[i], AssetPathAt(_controls, index),
                    $"「{rows[i].DisplayName}」必须指向资产里的 {expectedPaths[i]} 部件");
                Assert.AreEqual(expectedPaths[i], _controls.Move.bindings[index].effectivePath,
                    "无 override 时有效路径 == 资产路径");
                Assert.AreEqual(
                    RebindService.FindBindingIndexByPath(_controls.Move, expectedPaths[i]),
                    index,
                    "索引只能来自按 path 的资产查找（硬编码数字在资产顺序变动后会指错方向）");
            }

            CollectionAssert.AllItemsAreUnique(rows.Select(r => r.BindingIndex).ToArray(),
                "四个方向必须解析到四个不同的部件");
        }

        [Test]
        public void FindBindingIndexByPath_FollowsTheAssetOrder_NotAFixedTable()
        {
            // A composite whose parts are declared left/up/right/down: "up" is the
            // SECOND part (index 2), not the first. A hardcoded up==1 would fail here.
            InputActionAsset asset = ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                InputActionMap map = asset.AddActionMap("Player");
                InputAction move = map.AddAction("Move", InputActionType.Value);
                move.AddCompositeBinding("2DVector")
                    .With("left", "<Keyboard>/j")
                    .With("up", "<Keyboard>/i")
                    .With("right", "<Keyboard>/l")
                    .With("down", "<Keyboard>/k");

                Assert.AreEqual(-1, RebindService.FindBindingIndexByPath(move, "2DVector"),
                    "复合头不是部件，不参与匹配");
                Assert.AreEqual(2, RebindService.FindBindingIndexByPath(move, "<Keyboard>/i"));
                Assert.AreEqual(1, RebindService.FindBindingIndexByPath(move, "<Keyboard>/j"));
                Assert.AreEqual(4, RebindService.FindBindingIndexByPath(move, "<Keyboard>/k"));
                Assert.AreEqual(-1, RebindService.FindBindingIndexByPath(move, "<Keyboard>/nope"),
                    "不存在的 path → -1（调用方据此退回主绑定并告警）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void GetBindingDisplay_ByIndex_ShowsEachDirectionKey()
        {
            SettingDefinition[] rows = MoveRows();

            Assert.AreEqual("W", _service.GetBindingDisplay(rows[0].ActionKey, rows[0].BindingIndex));
            Assert.AreEqual("A", _service.GetBindingDisplay(rows[1].ActionKey, rows[1].BindingIndex));
            Assert.AreEqual("S", _service.GetBindingDisplay(rows[2].ActionKey, rows[2].BindingIndex));
            Assert.AreEqual("D", _service.GetBindingDisplay(rows[3].ActionKey, rows[3].BindingIndex));

            Assert.AreEqual("W", _service.GetBindingDisplay(MoveAction, -1), "-1 仍是「主绑定」（A-M3-03 语义不变）");
            Assert.AreEqual(RebindService.UnboundDisplay, _service.GetBindingDisplay(MoveAction, 99),
                "越界索引显示占位符而不是抛异常（行必须还能渲染）");
        }

        // ---------- 3. cross-direction conflict ----------

        [Test]
        public void RebindingUpOntoA_ConflictsWithLeft_AndRollsBack()
        {
            SettingDefinition[] rows = MoveRows();
            int upIndex = rows[0].BindingIndex;
            int leftIndex = rows[1].BindingIndex;

            // What the interactive rebind leaves on the asset before the check.
            ApplyOverride(_controls.Move, upIndex, LeftPath);
            Assert.AreEqual(LeftPath, _controls.Move.bindings[upIndex].effectivePath);

            IReadOnlyList<BindingSnapshot> map = BindingConflictDetector.CaptureMap(_controls.Player);
            BindingSnapshot conflict = BindingConflictDetector.FindConflict(
                map, _controls.Move.bindings[upIndex].effectivePath, MoveAction, upIndex);

            Assert.IsNotNull(conflict, "把「上」改成 A 必须检出冲突（同 map、同一复合轴）");
            Assert.AreEqual(MoveAction, conflict.ActionKey);
            Assert.AreEqual(leftIndex, conflict.BindingIndex, "冲突方必须是「左」那一行");
            Assert.AreEqual(LeftPath, conflict.EffectivePath);
            Assert.AreEqual(rows[1].DisplayName, "移动·左");

            // Rollback: the rebind flow drops the override, the old key stays valid.
            _controls.Move.RemoveBindingOverride(upIndex);
            Assert.AreEqual(UpPath, _controls.Move.bindings[upIndex].effectivePath, "冲突回滚后「上」仍是 W");
        }

        [Test]
        public void RebindingUpOntoItsOwnKey_IsNotAConflict()
        {
            SettingDefinition[] rows = MoveRows();
            int upIndex = rows[0].BindingIndex;

            IReadOnlyList<BindingSnapshot> map = BindingConflictDetector.CaptureMap(_controls.Player);
            BindingSnapshot conflict = BindingConflictDetector.FindConflict(map, UpPath, MoveAction, upIndex);

            Assert.IsNull(conflict, "重绑到自身键位不是冲突（被重绑的 binding 排除在外）");
        }

        // ---------- 4. reset semantics ----------

        [Test]
        public void PerRowReset_DropsOnlyThatDirection()
        {
            SettingDefinition[] rows = MoveRows();
            int upIndex = rows[0].BindingIndex;
            int leftIndex = rows[1].BindingIndex;

            ApplyOverride(_controls.Move, upIndex, "<Keyboard>/t");
            ApplyOverride(_controls.Move, leftIndex, "<Keyboard>/f");

            _service.ResetBinding(MoveAction, upIndex);

            Assert.AreEqual(UpPath, _controls.Move.bindings[upIndex].effectivePath, "「上」恢复默认");
            Assert.AreEqual("<Keyboard>/f", _controls.Move.bindings[leftIndex].effectivePath,
                "单行恢复默认不得连带清掉「左」的 override");
            Assert.IsTrue(File.Exists(_service.FilePath), "单行恢复默认同样落盘");
        }

        [Test]
        public void ResetAll_RestoresAllFourDirections()
        {
            SettingDefinition[] rows = MoveRows();
            string[] overrides = { "<Keyboard>/t", "<Keyboard>/f", "<Keyboard>/g", "<Keyboard>/h" };

            for (int i = 0; i < rows.Length; i++)
            {
                ApplyOverride(_controls.Move, rows[i].BindingIndex, overrides[i]);
            }

            CollectionAssert.AreEqual(
                new[] { "T", "F", "G", "H" },
                rows.Select(r => _service.GetBindingDisplay(r.ActionKey, r.BindingIndex)).ToArray(),
                "四个方向各自独立显示自己的 override");

            _service.ResetAll();

            string[] expectedPaths = { UpPath, LeftPath, DownPath, RightPath };
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.AreEqual(expectedPaths[i], _controls.Move.bindings[rows[i].BindingIndex].effectivePath,
                    $"ResetAll 后「{rows[i].DisplayName}」必须回到资产默认键位");
            }

            CollectionAssert.AreEqual(
                new[] { "W", "A", "S", "D" },
                rows.Select(r => _service.GetBindingDisplay(r.ActionKey, r.BindingIndex)).ToArray());
        }

        // ---------- 5. read-only gamepad note ----------

        [Test]
        public void ControlsPage_StatesThatTheGamepadStickIsNotRebindable()
        {
            Assert.IsTrue(_registry.TryGetPage("controls", out SettingsPage controls));

            SettingDefinition note = controls.Settings.Single(s => s.Type == SettingType.Note);
            Assert.AreEqual("手柄摇杆使用默认设置，暂不支持重绑定", note.DisplayName);
            Assert.IsNull(note.ActionKey, "说明行不绑定任何动作");
            Assert.IsNull(note.Apply, "说明行没有回调");
            Assert.IsNull(note.DefaultValue, "说明行不携带值");
            Assert.AreEqual(SettingType.Note, controls.Settings[controls.Settings.Count - 2].Type,
                "说明行位于列表末尾（全部恢复默认之前）");
        }

        [Test]
        public void NoteRow_RendersPlainText_WithoutAnyControl()
        {
            SettingDefinition note = SettingDefinition.Note("controls.note.Gamepad", "手柄摇杆使用默认设置，暂不支持重绑定");

            GameObject row = SettingsRowFactory.CreateRow(note, Host(), null, null);

            Assert.AreEqual("手柄摇杆使用默认设置，暂不支持重绑定",
                row.transform.Find("Note").GetComponent<UnityEngine.UI.Text>().text);
            Assert.IsNull(row.GetComponentInChildren<UnityEngine.UI.Button>(), "只读说明不是按钮（不给假的可点承诺）");
            Assert.IsNull(row.GetComponentInChildren<UnityEngine.UI.Toggle>());
            Assert.IsNull(row.GetComponentInChildren<UnityEngine.UI.Slider>());
            Assert.IsNotNull(row.GetComponent<UnityEngine.UI.LayoutElement>(), "说明行同样是布局组的子节点");
        }

        // ---------- 6. row → backend plumbing ----------

        [Test]
        public void BindingRow_PassesItsOwnBindingIndex_ToDisplayRebindAndReset()
        {
            SettingDefinition[] rows = MoveRows();
            SettingDefinition left = rows[1];
            var bindings = new RecordingBindings { Display = "A" };

            GameObject row = SettingsRowFactory.CreateRow(left, Host(), null, bindings);

            Assert.AreEqual(left.BindingIndex, bindings.LastDisplayIndex, "显示必须读自己那个部件");
            Assert.AreEqual(MoveAction, bindings.LastDisplayAction);

            row.transform.Find(SettingsRowFactory.KeyButtonName).GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
            Assert.AreEqual(MoveAction, bindings.LastRebindAction);
            Assert.AreEqual(left.BindingIndex, bindings.LastRebindIndex, "重绑必须带上行自己的 bindingIndex");

            row.transform.Find("ResetButton").GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
            Assert.AreEqual(MoveAction, bindings.LastResetAction);
            Assert.AreEqual(left.BindingIndex, bindings.LastResetIndex, "单行恢复默认也必须带索引");
        }

        /// <summary>Records which action/index each row call carries (no Input System, no play mode).</summary>
        private sealed class RecordingBindings : IBindingSettings
        {
            private Action<RebindOutcome, BindingSnapshot> _pending;

            public bool IsAvailable { get; set; } = true;

            public string Display { get; set; } = "A";

            public string LastDisplayAction { get; private set; }

            public int LastDisplayIndex { get; private set; }

            public string LastRebindAction { get; private set; }

            public int LastRebindIndex { get; private set; }

            public string LastResetAction { get; private set; }

            public int LastResetIndex { get; private set; }

            public string GetBindingDisplay(string actionKey)
            {
                return GetBindingDisplay(actionKey, -1);
            }

            public string GetBindingDisplay(string actionKey, int bindingIndex)
            {
                LastDisplayAction = actionKey;
                LastDisplayIndex = bindingIndex;
                return Display;
            }

            public void Rebind(string actionKey, Action<RebindOutcome, BindingSnapshot> onComplete)
            {
                Rebind(actionKey, -1, onComplete);
            }

            public void Rebind(string actionKey, int bindingIndex, Action<RebindOutcome, BindingSnapshot> onComplete)
            {
                LastRebindAction = actionKey;
                LastRebindIndex = bindingIndex;
                _pending = onComplete;
            }

            public void ResetBinding(string actionKey)
            {
                ResetBinding(actionKey, -1);
            }

            public void ResetBinding(string actionKey, int bindingIndex)
            {
                LastResetAction = actionKey;
                LastResetIndex = bindingIndex;
            }

            public void ResetAll()
            {
            }

            public void Complete(RebindOutcome outcome, BindingSnapshot snapshot)
            {
                Action<RebindOutcome, BindingSnapshot> pending = _pending;
                _pending = null;
                pending?.Invoke(outcome, snapshot);
            }
        }
    }
}
