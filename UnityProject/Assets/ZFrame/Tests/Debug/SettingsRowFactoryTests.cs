using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="SettingsRowFactory"/> (TASK-A-M3-02): the
    /// type → control mapping (Toggle/Slider/Enum/Action) built with REAL UGUI
    /// components, plus the layout discipline proof — rows position themselves
    /// purely through layout groups (布局零手摆): after a forced layout rebuild
    /// the rows stack top-down without any manual rect positioning.
    /// </summary>
    public sealed class SettingsRowFactoryTests
    {
        private GameObject _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
                _host = null;
            }
        }

        private RectTransform Host()
        {
            if (_host == null)
            {
                _host = new GameObject("RowFactoryTestHost", typeof(RectTransform));
            }

            return _host.GetComponent<RectTransform>();
        }

        // ---------- type → control mapping ----------

        [Test]
        public void ToggleRow_MapsToToggle_AndReportsChanges()
        {
            var calls = new List<object>();
            SettingDefinition definition = SettingDefinition.Toggle("t.on", "开关", false, v => calls.Add(v));
            GameObject row = SettingsRowFactory.CreateRow(definition, Host(), (d, v) => calls.Add(v));

            Toggle toggle = row.GetComponentInChildren<Toggle>();
            Assert.IsNotNull(toggle, "Toggle definition must generate a UGUI Toggle");
            Assert.IsNull(row.GetComponentInChildren<Slider>());
            Assert.IsNull(row.GetComponentInChildren<Dropdown>());
            Assert.IsFalse(toggle.isOn, "initial state mirrors the definition's current value");

            toggle.isOn = true;

            CollectionAssert.AreEqual(new object[] { true }, calls, "the toggle change must surface as (definition, true)");
        }

        [Test]
        public void SliderRow_MapsToSlider_WithRange_AndValueText()
        {
            var calls = new List<object>();
            SettingDefinition definition = SettingDefinition.Slider("t.vol", "音量", 0.5f, 0.2f, 0.8f, v => calls.Add(v));
            GameObject row = SettingsRowFactory.CreateRow(definition, Host(), (d, v) => calls.Add(v));

            Slider slider = row.GetComponentInChildren<Slider>();
            Assert.IsNotNull(slider);
            Assert.AreEqual(0.2f, slider.minValue);
            Assert.AreEqual(0.8f, slider.maxValue);
            Assert.AreEqual(0.5f, slider.value, "initial value mirrors the definition");

            slider.value = 0.7f;

            Assert.AreEqual(0.7f, (float)calls[0]);
            Text valueText = row.transform.Find("Value").GetComponent<Text>();
            Assert.IsNotNull(valueText, "slider rows carry a live value text");
            Assert.AreEqual("0.70", valueText.text);
        }

        [Test]
        public void EnumRow_MapsToDropdown_WithOptions()
        {
            var calls = new List<object>();
            SettingDefinition definition = SettingDefinition.EnumSetting("t.mode", "模式", 1, new[] { "甲", "乙", "丙" }, v => calls.Add(v));
            GameObject row = SettingsRowFactory.CreateRow(definition, Host(), (d, v) => calls.Add(v));

            Dropdown dropdown = row.GetComponentInChildren<Dropdown>();
            Assert.IsNotNull(dropdown, "Enum definition must generate a UGUI Dropdown");
            Assert.AreEqual(3, dropdown.options.Count, "dropdown options come straight from EnumOptions");
            Assert.AreEqual("乙", dropdown.captionText.text, "caption mirrors the current index");
            Assert.AreEqual(1, dropdown.value);

            dropdown.value = 2;
            dropdown.RefreshShownValue();

            CollectionAssert.AreEqual(new object[] { 2 }, calls, "enum changes surface as the 0-based option index");
            Assert.AreEqual("丙", dropdown.captionText.text);
        }

        [Test]
        public void ActionRow_MapsToButton_InvokesTrigger_NoPersistedValue()
        {
            int triggers = 0;
            var calls = new List<object>();
            SettingDefinition definition = SettingDefinition.Action("t.act", "执行", _ => triggers++);
            GameObject row = SettingsRowFactory.CreateRow(definition, Host(), (d, v) => calls.Add(v));

            Button button = row.GetComponentInChildren<Button>();
            Assert.IsNotNull(button, "Action definition must generate a UGUI Button");
            Assert.IsNull(row.GetComponentInChildren<Toggle>());
            Assert.IsNull(row.GetComponentInChildren<Slider>());
            Assert.IsNull(row.GetComponentInChildren<Dropdown>());

            button.onClick.Invoke();

            Assert.AreEqual(1, triggers, "clicking runs the definition's apply callback directly");
            CollectionAssert.AreEqual(new object[] { null }, calls);
        }

        [Test]
        public void ActionRow_WithoutApply_IsInertButClickable()
        {
            var definition = SettingDefinition.Action("t.inert", "即将支持");
            GameObject row = SettingsRowFactory.CreateRow(definition, Host(), null);

            Assert.DoesNotThrow(() => row.GetComponentInChildren<Button>().onClick.Invoke());
        }

        // ---------- dropdown template validity (TASK-A-R9-11) ----------

        // UGUI's Dropdown.SetupTemplate (com.unity.ugui Dropdown.cs) hard-fails
        // a template that has no child GameObject with a Toggle serving as the
        // item, and refuses an itemText that is not a child of that Toggle —
        // the failure is the user-reported console error
        // "The dropdown template is not valid. The template must have a child
        // GameObject with a Toggle component serving as the item." and the
        // dropdown never opens. These tests pin the factory's enum rows to that
        // contract so a hand-built skeleton cannot drift from it again.

        [Test]
        public void EnumRow_DropdownTemplate_FollowsUguiSetupTemplateContract()
        {
            SettingDefinition definition = SettingDefinition.EnumSetting("t.ddstruct", "下拉", 0, new[] { "甲", "乙", "丙" });
            GameObject row = SettingsRowFactory.CreateRow(definition, Host(), null);

            Dropdown dropdown = row.GetComponentInChildren<Dropdown>();
            Assert.IsNotNull(dropdown, "enum definition must generate a UGUI Dropdown");
            RectTransform template = dropdown.template;
            Assert.IsNotNull(template, "enum dropdown must assign its template");

            // Standard skeleton: Template → Viewport → Content → Item.
            // ScrollRect lives on the template (DefaultControls.CreateDropdown),
            // the Viewport carries the Mask that clips the list.
            Assert.IsNotNull(template.GetComponent<ScrollRect>(), "template hosts the option list through a ScrollRect");
            Transform viewport = template.Find("Viewport");
            Assert.IsNotNull(viewport, "template must own a Viewport");
            Assert.IsNotNull(viewport.GetComponent<Mask>(), "viewport clips the list through a Mask");
            Transform content = viewport.Find("Content");
            Assert.IsNotNull(content, "viewport must own a Content");
            Transform item = content.Find("Item");
            Assert.IsNotNull(item, "content must own the Item prototype");

            // The exact contract that killed the panel: a child Toggle as the item.
            Toggle itemToggle = item.GetComponent<Toggle>();
            Assert.IsNotNull(itemToggle, "Item must carry a Toggle component (UGUI SetupTemplate contract)");
            Toggle templateToggle = template.GetComponentInChildren<Toggle>();
            Assert.IsNotNull(templateToggle, "template must contain a Toggle");
            Assert.AreNotEqual(template.transform, templateToggle.transform,
                "the Toggle must be on a child of the template, never on the template itself");
            Assert.IsTrue(templateToggle.transform.parent is RectTransform,
                "the item Toggle's parent must be a RectTransform (UGUI SetupTemplate contract)");

            // itemText renders every option and must live under the item Toggle.
            Assert.IsNotNull(dropdown.itemText, "enum dropdown must assign itemText");
            Assert.IsTrue(dropdown.itemText.transform.IsChildOf(templateToggle.transform),
                "itemText must be on the item GameObject or its children (UGUI SetupTemplate contract)");

            // Item toggle visuals (mirrors the Toggle row: dark box + green check).
            Assert.IsNotNull(itemToggle.targetGraphic, "item toggle needs its box graphic");
            Assert.IsNotNull(itemToggle.graphic, "item toggle needs its checkmark graphic");

            // The template must start hidden — the list only opens on Show().
            Assert.IsFalse(template.gameObject.activeSelf, "dropdown template must start inactive");
        }

        [Test]
        public void EnumRow_DropdownShow_InstantiatesListWithTogglePerOption()
        {
            // Show() resolves the root canvas from the dropdown's parents and
            // returns silently when there is none — the host must be a Canvas,
            // otherwise this test would prove nothing about the template.
            GameObject canvasHost = new GameObject("DropdownShowHost", typeof(Canvas));
            _host = canvasHost; // TearDown destroys the whole generated tree

            SettingDefinition definition = SettingDefinition.EnumSetting("t.ddshow", "下拉", 0, new[] { "甲", "乙", "丙" });
            GameObject row = SettingsRowFactory.CreateRow(definition, canvasHost.transform, null);

            Dropdown dropdown = row.GetComponentInChildren<Dropdown>();
            Assert.IsNotNull(dropdown);

            // Play-mode fidelity: Dropdown.Start() (which creates the fade tween
            // runner) is engine-driven in play mode but never runs in EditMode —
            // invoke it once exactly as the engine would, otherwise Show() dies
            // inside UGUI's AlphaFadeList on a null runner (edit-mode artifact,
            // not a product path).
            typeof(Dropdown)
                .GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                .Invoke(dropdown, null);

            // The user-reported failure hit exactly here: Show() → SetupTemplate
            // logged the invalid-template error and returned without opening.
            // An unexpected LogError fails this test on its own; the assertions
            // below additionally prove the list really opened.
            Assert.DoesNotThrow(() => dropdown.Show());

            Transform list = dropdown.transform.Find("Dropdown List");
            Assert.IsNotNull(list, "Show() must instantiate the option list from the template");
            Toggle[] openToggles = list.GetComponentsInChildren<Toggle>();
            Assert.AreEqual(definition.EnumOptions.Length, openToggles.Length,
                "the opened list must offer one Toggle item per option");

            // After Show() the template is hidden again (the list replaced it).
            Assert.IsFalse(dropdown.template.gameObject.activeSelf, "template hidden again after Show()");

            CloseDropdownInEditMode(dropdown);
        }

        /// <summary>
        /// EditMode exit protocol for an OPEN UGUI dropdown: its OnDisable (and
        /// Hide) destroy the open list, items and blocker through
        /// <see cref="UnityEngine.Object.Destroy"/> — forbidden in edit mode and
        /// logged as an error, which would fail the test from the teardown side.
        /// Clearing the open-state fields (the same state
        /// ImmediateDestroyDropdownList would consume) leaves the objects to be
        /// removed by TearDown's DestroyImmediate with no Object.Destroy call.
        /// </summary>
        private static void CloseDropdownInEditMode(Dropdown dropdown)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Dropdown).GetField("m_Dropdown", flags)?.SetValue(dropdown, null);
            typeof(Dropdown).GetField("m_Blocker", flags)?.SetValue(dropdown, null);
            (typeof(Dropdown).GetField("m_Items", flags)?.GetValue(dropdown) as System.Collections.IList)?.Clear();
        }

        // ---------- layout discipline: 布局零手摆 ----------

        [Test]
        public void PageContainer_IsVerticalLayoutGroup_FittingContent()
        {
            GameObject container = SettingsRowFactory.CreatePageContainer(Host());

            VerticalLayoutGroup layout = container.GetComponent<VerticalLayoutGroup>();
            Assert.IsNotNull(layout, "the page container stacks rows via VerticalLayoutGroup — no manual Y positions");
            Assert.IsFalse(layout.childForceExpandHeight);
            Assert.IsTrue(layout.childControlWidth);

            ContentSizeFitter fitter = container.GetComponent<ContentSizeFitter>();
            Assert.IsNotNull(fitter);
            Assert.AreEqual(ContentSizeFitter.FitMode.PreferredSize, fitter.verticalFit);
        }

        [Test]
        public void Rows_PositionPurelyThroughLayoutGroups()
        {
            GameObject container = SettingsRowFactory.CreatePageContainer(Host());
            SettingsRowFactory.CreateRow(SettingDefinition.Toggle("a", "A", true), container.transform, null);
            SettingsRowFactory.CreateRow(SettingDefinition.Slider("b", "B", 0.5f, 0f, 1f), container.transform, null);
            SettingsRowFactory.CreateRow(SettingDefinition.EnumSetting("c", "C", 0, new[] { "x" }), container.transform, null);
            SettingsRowFactory.CreateRow(SettingDefinition.Action("d", "D"), container.transform, null);

            // One forced rebuild replaces ALL manual layout work — the rows land
            // stacked top-down without a single hand-written anchoredPosition.
            LayoutRebuilder.ForceRebuildLayoutImmediate(container.GetComponent<RectTransform>());

            float previousY = float.MaxValue;
            for (int i = 0; i < container.transform.childCount; i++)
            {
                RectTransform row = (RectTransform)container.transform.GetChild(i);
                Assert.Less(row.anchoredPosition.y, previousY, $"row {i} must sit below row {i - 1} (layout-driven stacking)");
                previousY = row.anchoredPosition.y;
            }
        }

        [Test]
        public void CreateRow_NullDefinition_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SettingsRowFactory.CreateRow(null, Host(), null));
        }

        [Test]
        public void EveryRow_CarriesItsDisplayName_Label()
        {
            var definition = SettingDefinition.Toggle("t.labelled", "屏幕特效", true);
            GameObject row = SettingsRowFactory.CreateRow(definition, Host(), null);

            Text label = row.GetComponentInChildren<Text>();
            Assert.IsNotNull(label);
            Assert.AreEqual("屏幕特效", label.text);
        }
    }
}
