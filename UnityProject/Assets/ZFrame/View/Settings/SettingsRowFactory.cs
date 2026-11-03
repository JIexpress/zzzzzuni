using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ZFrame.View
{
    /// <summary>
    /// Builds the UGUI rows of a settings page from <see cref="SettingDefinition"/>
    /// data (TASK-A-M3-02). Type → control mapping:
    /// Toggle → Toggle+Label, Slider → Slider+Label+value Text, Enum → Dropdown,
    /// Action → Button, Binding → Label+key Button+恢复默认 Button (A-M3-03),
    /// BindingReset → one wide 全部恢复默认 Button, Note → one read-only Text
    /// line (A-M3-04). Rows are pure layout-group
    /// children: a page container is a VerticalLayoutGroup, each row a
    /// HorizontalLayoutGroup child with LayoutElements — ZERO manual rect
    /// positioning (加选项不改布局).
    ///
    /// Everything is created in code (same acceptance-asset discipline as
    /// <c>UISetup</c>) and works in EditMode tests, so the mapping itself is
    /// unit-testable with real UGUI components.
    /// </summary>
    public static class SettingsRowFactory
    {
        /// <summary>Preferred height of one row (LayoutElement).</summary>
        public const float RowHeight = 48f;

        /// <summary>Preferred width of the row label column.</summary>
        public const float LabelWidth = 340f;

        /// <summary>Preferred width of the slider value text.</summary>
        public const float SliderValueWidth = 110f;

        /// <summary>Preferred width of the per-row "恢复默认" button (Binding rows).</summary>
        public const float BindingResetWidth = 140f;

        /// <summary>Key-button caption while waiting for the user to press a key (TASK-A-M3-03).</summary>
        public const string WaitingForInputText = "按任意键…";

        /// <summary>Caption of the per-row binding reset button.</summary>
        public const string ResetBindingText = "恢复默认";

        /// <summary>Caption shown when no input asset is available (EditMode/tests without bindings).</summary>
        public const string NoBindingServiceText = "—";

        /// <summary>Name prefix of a generated row ("Row_" + setting id) — pages look rows up by it.</summary>
        public const string RowNamePrefix = "Row_";

        /// <summary>Name of the per-action key button inside a Binding row.</summary>
        public const string KeyButtonName = "KeyButton";

        /// <summary>Name of the caption Text inside any generated button.</summary>
        public const string ButtonLabelName = "Text";

        /// <summary>Builtin font all generated controls use (same as UISetup).</summary>
        private const string FontName = "LegacyRuntime.ttf";

        /// <summary>
        /// Creates the page container a page's rows are parented to: a
        /// VerticalLayoutGroup + ContentSizeFitter that stacks rows top-down —
        /// the caller only parents rows into it, never positions anything.
        /// </summary>
        public static GameObject CreatePageContainer(Transform parent)
        {
            var go = new GameObject("PageContainer", typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            Stretch(rect);

            VerticalLayoutGroup layout = go.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 8f;
            layout.padding = new RectOffset(24, 24, 16, 16);

            ContentSizeFitter fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return go;
        }

        /// <summary>
        /// Creates the row for <paramref name="definition"/> under
        /// <paramref name="parent"/> (usually a <see cref="CreatePageContainer"/>
        /// result) and reports committed value changes through
        /// <paramref name="onValueChanged"/> — the presenter forwards them to
        /// <see cref="SettingsModel.Set{T}"/>. Returns the row root (tests
        /// assert on the generated control component).
        /// </summary>
        /// <param name="bindings">
        /// Binding backend for <see cref="SettingType.Binding"/>/<see cref="SettingType.BindingReset"/>
        /// rows (TASK-A-M3-03). Null = rows render disabled controls, which keeps
        /// every non-input page testable without an Input System asset.
        /// </param>
        /// <param name="onFeedback">
        /// Receives the human-readable outcome of a rebind attempt
        /// ("已绑定…"/"键位冲突…"/"已取消"/"重绑定不可用…") — the panel writes it
        /// into its status line (Toast 文本行).
        /// </param>
        public static GameObject CreateRow(
            SettingDefinition definition,
            Transform parent,
            Action<SettingDefinition, object> onValueChanged,
            IBindingSettings bindings = null,
            Action<string> onFeedback = null)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var row = new GameObject(RowNamePrefix + definition.Id, typeof(RectTransform));
            RectTransform rowRect = row.GetComponent<RectTransform>();
            rowRect.SetParent(parent, false);

            LayoutElement rowElement = row.AddComponent<LayoutElement>();
            rowElement.preferredHeight = RowHeight;
            rowElement.flexibleWidth = 1f;

            HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.spacing = 16f;

            // Label column (shared by every row type; Action and BindingReset rows
            // use their button as the label, Note rows are a single full-width text).
            if (definition.Type != SettingType.Action &&
                definition.Type != SettingType.BindingReset &&
                definition.Type != SettingType.Note)
            {
                GameObject labelGo = CreateText("Label", definition.DisplayName, 30, Color.white);
                labelGo.GetComponent<RectTransform>().SetParent(rowRect, false);
                LayoutElement labelElement = labelGo.AddComponent<LayoutElement>();
                labelElement.preferredWidth = LabelWidth;
                labelElement.flexibleWidth = 0f;
            }

            switch (definition.Type)
            {
                case SettingType.Toggle:
                    BuildToggleRow(definition, rowRect, onValueChanged);
                    break;
                case SettingType.Slider:
                    BuildSliderRow(definition, rowRect, onValueChanged);
                    break;
                case SettingType.Enum:
                    BuildEnumRow(definition, rowRect, onValueChanged);
                    break;
                case SettingType.Action:
                    BuildActionRow(definition, rowRect, onValueChanged);
                    break;
                case SettingType.Binding:
                    BuildBindingRow(definition, rowRect, bindings, onFeedback);
                    break;
                case SettingType.BindingReset:
                    BuildBindingResetRow(definition, rowRect, bindings, onValueChanged, onFeedback);
                    break;
                case SettingType.Note:
                    BuildNoteRow(definition, rowRect);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(definition.Id);
            }

            return row;
        }

        // ---- control builders ------------------------------------------------

        private static void BuildToggleRow(
            SettingDefinition definition, RectTransform row, Action<SettingDefinition, object> onValueChanged)
        {
            var toggleGo = new GameObject("Toggle", typeof(RectTransform));
            toggleGo.GetComponent<RectTransform>().SetParent(row, false);
            LayoutElement element = toggleGo.AddComponent<LayoutElement>();
            element.preferredHeight = 32f;
            element.flexibleWidth = 1f;

            // Background box (targetGraphic).
            var backgroundGo = new GameObject("Background", typeof(RectTransform));
            RectTransform backgroundRect = backgroundGo.GetComponent<RectTransform>();
            backgroundRect.SetParent(toggleGo.transform, false);
            backgroundRect.sizeDelta = new Vector2(32f, 32f);
            backgroundRect.anchorMin = new Vector2(0f, 0.5f);
            backgroundRect.anchorMax = new Vector2(0f, 0.5f);
            backgroundRect.anchoredPosition = Vector2.zero;
            Image background = backgroundGo.AddComponent<Image>();
            background.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

            // Checkmark (graphic).
            var checkGo = new GameObject("Checkmark", typeof(RectTransform));
            RectTransform checkRect = checkGo.GetComponent<RectTransform>();
            checkRect.SetParent(backgroundRect, false);
            Stretch(checkRect);
            Image check = checkGo.AddComponent<Image>();
            check.color = new Color(0.35f, 0.75f, 0.4f);

            Toggle toggle = toggleGo.AddComponent<Toggle>();
            toggle.targetGraphic = background;
            toggle.graphic = check;
            // TASK-B-R9-06：可选的交互门控（插帧行在"必然不生效"的环境里禁用，
            // 与 Binding 行按 IsAvailable 置灰同一条原则；null = 恒可交互）。
            if (definition.Interactable != null && !definition.Interactable())
            {
                toggle.interactable = false;
            }

            toggle.SetIsOnWithoutNotify(definition.CurrentValue is bool on && on);
            toggle.onValueChanged.AddListener(value => onValueChanged?.Invoke(definition, value));
        }

        private static void BuildSliderRow(
            SettingDefinition definition, RectTransform row, Action<SettingDefinition, object> onValueChanged)
        {
            var sliderGo = new GameObject("Slider", typeof(RectTransform));
            sliderGo.GetComponent<RectTransform>().SetParent(row, false);
            LayoutElement element = sliderGo.AddComponent<LayoutElement>();
            element.preferredHeight = 32f;
            element.flexibleWidth = 1f;

            // Track background (targetGraphic).
            var backgroundGo = new GameObject("Background", typeof(RectTransform));
            RectTransform backgroundRect = backgroundGo.GetComponent<RectTransform>();
            backgroundRect.SetParent(sliderGo.transform, false);
            Stretch(backgroundRect);
            Image backgroundImage = backgroundGo.AddComponent<Image>();
            backgroundImage.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

            // Fill area + fill.
            var fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
            RectTransform fillAreaRect = fillAreaGo.GetComponent<RectTransform>();
            fillAreaRect.SetParent(sliderGo.transform, false);
            StretchWithPadding(fillAreaRect, 6f);
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            RectTransform fillRect = fillGo.GetComponent<RectTransform>();
            fillRect.SetParent(fillAreaRect, false);
            Stretch(fillRect);
            Image fillImage = fillGo.AddComponent<Image>();
            fillImage.color = new Color(0.35f, 0.55f, 0.8f);

            // Handle area + handle.
            var handleAreaGo = new GameObject("Handle Slide Area", typeof(RectTransform));
            RectTransform handleAreaRect = handleAreaGo.GetComponent<RectTransform>();
            handleAreaRect.SetParent(sliderGo.transform, false);
            StretchWithPadding(handleAreaRect, 12f);
            var handleGo = new GameObject("Handle", typeof(RectTransform));
            RectTransform handleRect = handleGo.GetComponent<RectTransform>();
            handleRect.SetParent(handleAreaRect, false);
            handleRect.sizeDelta = new Vector2(20f, 20f);
            Image handleImage = handleGo.AddComponent<Image>();
            handleImage.color = Color.white;

            Slider slider = sliderGo.AddComponent<Slider>();
            slider.targetGraphic = handleImage;
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = definition.Min;
            slider.maxValue = definition.Max;

            // Value text column.
            GameObject valueTextGo = CreateText("Value", string.Empty, 28, Color.white);
            valueTextGo.GetComponent<RectTransform>().SetParent(row, false);
            LayoutElement valueElement = valueTextGo.AddComponent<LayoutElement>();
            valueElement.preferredWidth = SliderValueWidth;
            valueElement.flexibleWidth = 0f;
            Text valueText = valueTextGo.GetComponent<Text>();

            float current = definition.CurrentValue is float f ? f : definition.Min;
            slider.SetValueWithoutNotify(current);
            valueText.text = FormatSliderValue(current);
            slider.onValueChanged.AddListener(value =>
            {
                valueText.text = FormatSliderValue(value);
                onValueChanged?.Invoke(definition, value);
            });
        }

        private static void BuildEnumRow(
            SettingDefinition definition, RectTransform row, Action<SettingDefinition, object> onValueChanged)
        {
            Dropdown dropdown = CreateStandardDropdownControl();
            dropdown.GetComponent<RectTransform>().SetParent(row, false);
            LayoutElement element = dropdown.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = 40f;
            element.flexibleWidth = 1f;

            // Options/value/callback wiring (TASK-A-R9-11 keeps the old data
            // path untouched — only the template skeleton was rebuilt).
            dropdown.options.Clear(); // drop DefaultControls' Option A/B/C placeholders
            List<string> options = new List<string>(definition.EnumOptions ?? Array.Empty<string>());
            dropdown.AddOptions(options);
            dropdown.SetValueWithoutNotify(definition.CurrentValue is int index ? index : 0);
            dropdown.RefreshShownValue();
            dropdown.onValueChanged.AddListener(value => onValueChanged?.Invoke(definition, value));
        }

        /// <summary>
        /// Builds one structurally valid UGUI <see cref="Dropdown"/> (TASK-A-R9-11).
        ///
        /// The skeleton is NOT hand-built: it comes straight from
        /// <see cref="DefaultControls.CreateDropdown"/> — the same builder the
        /// editor's GameObject → UI → Dropdown menu uses — so the
        /// Template → Viewport → Content → Item(Toggle + Item Label) hierarchy
        /// satisfies <c>Dropdown.SetupTemplate</c>'s hidden contract by
        /// construction (a child Toggle serving as the item, itemText under
        /// that Toggle). The previous hand-built skeleton lacked the item
        /// Toggle, which is exactly the user-reported editor error
        /// "The dropdown template is not valid. The template must have a child
        /// GameObject with a Toggle component serving as the item." — Show()
        /// logged it and the dropdown never opened. Styling (colors/fonts) is
        /// applied afterwards and cannot affect template validity; the visual
        /// language follows the panel's other rows (plain colored images,
        /// LegacyRuntime.ttf, sprite-free).
        /// </summary>
        /// <returns>The Dropdown component on a fresh "Dropdown" root GameObject.</returns>
        private static Dropdown CreateStandardDropdownControl()
        {
            GameObject dropdownGo = DefaultControls.CreateDropdown(new DefaultControls.Resources());
            dropdownGo.name = "Dropdown"; // rows are looked up as Row_<id>/Dropdown

            // The decorative arrow is sprite-based (this panel is asset-free,
            // so the resources carry null sprites) and nothing references it —
            // remove it instead of shipping a white placeholder block.
            Transform arrow = dropdownGo.transform.Find("Arrow");
            if (arrow != null)
            {
                DestroyGenerated(arrow.gameObject);
            }

            // Caption text (shows the selected option).
            Text caption = dropdownGo.transform.Find("Label").GetComponent<Text>();
            caption.fontSize = 28;
            caption.color = Color.white;

            // Template list — recolored to the panel's dropdown look, item rows
            // sized for the 28pt option font (DefaultControls' 20pt rows would
            // clip it).
            RectTransform templateRect = (RectTransform)dropdownGo.transform.Find("Template");
            templateRect.sizeDelta = new Vector2(0f, 240f);
            templateRect.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 0.98f);

            RectTransform itemRect = (RectTransform)templateRect.Find("Viewport/Content/Item");
            itemRect.sizeDelta = new Vector2(0f, 40f);
            Toggle itemToggle = itemRect.GetComponent<Toggle>();
            itemToggle.targetGraphic.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);  // Item Background box
            itemToggle.graphic.color = new Color(0.35f, 0.75f, 0.4f);            // Item Checkmark
            Text itemLabel = itemRect.Find("Item Label").GetComponent<Text>();
            itemLabel.fontSize = 28;
            itemLabel.color = Color.white;

            // Scrollbar only shows when the options overflow (AutoHideAndExpand
            // viewport); recolor it out of the plain-white DefaultControls look.
            Transform scrollbar = templateRect.Find("Scrollbar");
            scrollbar.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f, 0.98f);
            scrollbar.Find("Sliding Area/Handle").GetComponent<Image>().color = new Color(0.45f, 0.45f, 0.45f, 0.95f);

            // DefaultControls leaves the template visible (the editor menu relies
            // on Dropdown.Awake to hide it, which never runs in EditMode) — hide
            // it here so the closed dropdown never renders its option list.
            templateRect.gameObject.SetActive(false);

            return dropdownGo.GetComponent<Dropdown>();
        }

        /// <summary>Destroy helper (same discipline as SettingsPanelView/UIManager): play mode defers, edit mode destroys immediately.</summary>
        private static void DestroyGenerated(GameObject instance)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(instance);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void BuildActionRow(
            SettingDefinition definition, RectTransform row, Action<SettingDefinition, object> onValueChanged)
        {
            var buttonGo = new GameObject("Button", typeof(RectTransform));
            buttonGo.GetComponent<RectTransform>().SetParent(row, false);
            LayoutElement element = buttonGo.AddComponent<LayoutElement>();
            element.preferredHeight = 44f;
            element.flexibleWidth = 1f;

            Image image = buttonGo.AddComponent<Image>();
            image.color = new Color(0.3f, 0.35f, 0.45f);
            Button button = buttonGo.AddComponent<Button>();

            GameObject labelGo = CreateText("Text", definition.DisplayName, 30, Color.white);
            labelGo.GetComponent<RectTransform>().SetParent(buttonGo.transform, false);
            Stretch(labelGo.GetComponent<RectTransform>());

            // Action rows are imperative triggers: clicking runs the definition's
            // callback directly — there is no persisted value (SettingsModel
            // refuses to store Action rows by design).
            button.onClick.AddListener(() =>
            {
                definition.Apply?.Invoke(null);
                onValueChanged?.Invoke(definition, null);
            });
        }

        /// <summary>
        /// Key-binding row (TASK-A-M3-03): [动作名] [当前键位] [恢复默认].
        /// Clicking the key button enters the WAITING state (caption
        /// "按任意键…", button disabled so a second click cannot stack
        /// rebinds) and the rebind outcome is reported through
        /// <paramref name="onFeedback"/>; the caption always falls back to the
        /// authoritative display string, so a conflict (rolled back) or a
        /// cancellation shows the OLD key again.
        ///
        /// TASK-A-M3-04: every backend call carries
        /// <see cref="SettingDefinition.BindingIndex"/> (-1 = the action's
        /// primary binding), so the four 移动 rows each own ONE part of the
        /// WASD composite — display, rebind AND per-row reset stay scoped to
        /// that part (恢复默认 of 移动·上 must not wipe 左/下/右).
        /// </summary>
        private static void BuildBindingRow(
            SettingDefinition definition,
            RectTransform row,
            IBindingSettings bindings,
            Action<string> onFeedback)
        {
            bool available = bindings != null && bindings.IsAvailable;
            int bindingIndex = definition.BindingIndex;

            Button keyButton = CreateButton(
                "KeyButton",
                row,
                available ? bindings.GetBindingDisplay(definition.ActionKey, bindingIndex) : NoBindingServiceText,
                30,
                out Text keyText);
            keyButton.interactable = available;

            Button resetButton = CreateButton("ResetButton", row, ResetBindingText, 26, out _);
            LayoutElement resetElement = resetButton.GetComponent<LayoutElement>();
            resetElement.preferredWidth = BindingResetWidth;
            resetElement.flexibleWidth = 0f;
            resetButton.interactable = available;

            keyButton.onClick.AddListener(() =>
            {
                if (bindings == null || !bindings.IsAvailable)
                {
                    return;
                }

                keyText.text = WaitingForInputText;
                keyButton.interactable = false;

                bindings.Rebind(definition.ActionKey, bindingIndex, (outcome, conflict) =>
                {
                    // The row may have been rebuilt while the rebind was pending
                    // (page switch / refresh) — never touch a destroyed control.
                    if (keyButton == null || keyText == null)
                    {
                        return;
                    }

                    keyButton.interactable = true;
                    keyText.text = bindings.GetBindingDisplay(definition.ActionKey, bindingIndex);
                    onFeedback?.Invoke(DescribeOutcome(definition, outcome, conflict));
                });
            });

            resetButton.onClick.AddListener(() =>
            {
                if (bindings == null || !bindings.IsAvailable)
                {
                    return;
                }

                bindings.ResetBinding(definition.ActionKey, bindingIndex);
                keyText.text = bindings.GetBindingDisplay(definition.ActionKey, bindingIndex);
                onFeedback?.Invoke($"已恢复 {definition.DisplayName} 的默认键位");
            });
        }

        /// <summary>
        /// Read-only explanatory line (TASK-A-M3-04): one full-width muted Text,
        /// no Button/Toggle/Slider and no value — a limitation is stated, not
        /// offered as a disabled control the user might try to click.
        /// </summary>
        private static void BuildNoteRow(SettingDefinition definition, RectTransform row)
        {
            GameObject noteGo = CreateText("Note", definition.DisplayName, 26, new Color(0.72f, 0.72f, 0.72f));
            noteGo.GetComponent<RectTransform>().SetParent(row, false);
            LayoutElement element = noteGo.AddComponent<LayoutElement>();
            element.preferredHeight = 32f;
            element.flexibleWidth = 1f;
        }

        /// <summary>
        /// Map-wide 全部恢复默认 row (TASK-A-M3-03): one wide button that drops
        /// every override, then raises <c>onValueChanged</c> so the presenter
        /// can rebuild the page (the other rows must show the restored keys).
        /// </summary>
        private static void BuildBindingResetRow(
            SettingDefinition definition,
            RectTransform row,
            IBindingSettings bindings,
            Action<SettingDefinition, object> onValueChanged,
            Action<string> onFeedback)
        {
            bool available = bindings != null && bindings.IsAvailable;
            Button button = CreateButton("Button", row, definition.DisplayName, 30, out _);
            button.interactable = available;

            button.onClick.AddListener(() =>
            {
                if (bindings != null && bindings.IsAvailable)
                {
                    bindings.ResetAll();
                    onFeedback?.Invoke("已恢复全部默认键位");
                }

                onValueChanged?.Invoke(definition, null);
            });
        }

        /// <summary>Rebind outcome → one Chinese feedback line (Toast 文本行).</summary>
        private static string DescribeOutcome(
            SettingDefinition definition, RebindOutcome outcome, BindingSnapshot conflict)
        {
            switch (outcome)
            {
                case RebindOutcome.Done:
                    return $"已绑定 {definition.DisplayName}";
                case RebindOutcome.Conflict:
                    return $"键位冲突：{conflict?.ActionName} 已使用该键，已回滚";
                case RebindOutcome.Cancelled:
                    return "已取消重绑定";
                default:
                    return "重绑定不可用（需在游戏运行中操作）";
            }
        }

        // ---- helpers ---------------------------------------------------------

        /// <summary>
        /// One-line feedback sink of the 操作 tab (Toast 文本行): the panel adds
        /// it once per generated page and writes rebind outcomes into it.
        /// </summary>
        public static GameObject CreateStatusLine(Transform parent)
        {
            GameObject go = CreateText("Status", string.Empty, 28, new Color(0.85f, 0.85f, 0.6f));
            go.GetComponent<RectTransform>().SetParent(parent, false);
            LayoutElement element = go.AddComponent<LayoutElement>();
            element.preferredHeight = 36f;
            element.flexibleWidth = 1f;
            return go;
        }

        /// <summary>Creates a caption button with an Image + centered Text; the Text is returned through <paramref name="label"/>.</summary>
        private static Button CreateButton(
            string name, RectTransform parent, string caption, int fontSize, out Text label)
        {
            var buttonGo = new GameObject(name, typeof(RectTransform));
            buttonGo.GetComponent<RectTransform>().SetParent(parent, false);
            LayoutElement element = buttonGo.AddComponent<LayoutElement>();
            element.preferredHeight = 44f;
            element.flexibleWidth = 1f;

            Image image = buttonGo.AddComponent<Image>();
            image.color = new Color(0.3f, 0.35f, 0.45f);
            Button button = buttonGo.AddComponent<Button>();

            GameObject labelGo = CreateText(ButtonLabelName, caption, fontSize, Color.white);
            labelGo.GetComponent<RectTransform>().SetParent(buttonGo.transform, false);
            Stretch(labelGo.GetComponent<RectTransform>());
            label = labelGo.GetComponent<Text>();
            label.alignment = TextAnchor.MiddleCenter;
            return button;
        }

        /// <summary>Creates a non-raycast Text GO (caller parents + sizes it).</summary>
        private static GameObject CreateText(string name, string content, int fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>(FontName);
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            text.text = content;
            return go;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void StretchWithPadding(RectTransform rect, float padding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
        }

        private static string FormatSliderValue(float value)
        {
            // Whole numbers show as such; otherwise two decimals.
            return Mathf.Approximately(value, Mathf.Round(value))
                ? Mathf.RoundToInt(value).ToString()
                : value.ToString("0.00");
        }
    }
}
