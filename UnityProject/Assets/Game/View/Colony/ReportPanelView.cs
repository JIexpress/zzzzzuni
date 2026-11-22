// ReportPanelView.cs —— TASK-A-COL-05：月度报告面板视图（MVP 的 V 层）；TASK-A-COL-07 迭代。
// UIManager 的 pure-view 通道（UIManager.Push 对非 Component 视图跳过 prefab 加载）：
// 本类不进 prefab 资产，OnInit 时**代码构建** UGUI 层级（简报「若需 prefab 则代码构建 UI 元素」），
// 挂在 UIRoot 的 Panel 层画布下；OnDestroy 销毁自己的 GameObject（CachePolicy.None，Pop 即拆）。
// 呈现只吃 <see cref="ReportPanelVm"/>；「查原始记录」toggle、异常项行按钮与「关闭」按钮只上抛事件，
// 业务决定权在 <see cref="ReportPresenter"/>（MVP：V 不做决策）。
//
// TASK-A-COL-07 迭代（滚动 + 异常项跳转 + 情报点买 Raw）：
//   · 正文改 ScrollRect（Viewport+RectMask2D 裁剪 + Content 上锚定随 VerticalLayoutGroup/
//     ContentSizeFitter 生长）——Raw 长列表可滚（A-COL-05 登记的"Raw ~80 行超屏"缺口）；
//   · 正文改为逐行 Row（Text 逐行 + VerticalLayoutGroup 排布）：Summary 的 "▲ " 异常项行
//     带 Button（点击上抛 AnomalyClicked → P 层跳转），Raw 命中行文字用高亮色；
//   · 顶部常驻情报点余额；余额不足时 toggle 置灰（interactable=false）+ 底部提示行显示。
// EditMode 可裸实例化（不调 OnInit 就没有 GameObject）——P 层测试走 Attach→Render 全链路。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.View;

namespace Game.View.Colony
{
    /// <summary>月度报告面板视图：代码构建的 UGUI 面板（ScrollRect 行架构），纯 C# IUIView 实现。</summary>
    public sealed class ReportPanelView : IUIView
    {
        /// <summary>「查原始记录」toggle 变化（用户点击或程序切换）→ P 层。</summary>
        public event Action<bool> RawToggleChanged;

        /// <summary>异常项行按钮点击（TASK-A-COL-07）→ P 层；参数 = <see cref="ReportPanelVm.AnomalyLines"/> 的下标。</summary>
        public event Action<int> AnomalyClicked;

        /// <summary>「关闭」请求 → P 层（宿主注入的 Close 意图）。</summary>
        public event Action CloseRequested;

        private static readonly Color HighlightColor = new Color(0.847f, 0.635f, 0.290f); // 异常项/命中行高亮（琥珀色 #D8A24A）
        private static readonly Color NormalRowColor = Color.white;

        private GameObject _root;
        private Text _titleText;
        private Text _intelText;             // 顶部常驻情报点余额（TASK-A-COL-07）
        private ScrollRect _scroll;          // 正文滚动区（Raw 长列表）
        private RectTransform _scrollViewport;
        private RectTransform _scrollContent;
        private Text _hintText;              // 底部提示行（情报渠道不足，TASK-A-COL-07）
        private Text _toggleLabel;
        private Toggle _rawToggle;
        private Button _closeButton;
        private bool _suppressToggleEvent;

        /// <summary>当前渲染的异常项行按钮（anomalyIndex → Button；每次 Render 重建，UGUI 真实点击路径）。</summary>
        private readonly List<KeyValuePair<int, Button>> _anomalyButtons = new List<KeyValuePair<int, Button>>();

        /// <summary>最近一次 Render 的 VM（测试断言口径；未 Render 前 null）。</summary>
        public ReportPanelVm CurrentVm { get; private set; }

        /// <summary>当前显示档位（true = Raw；跟随 CurrentVm.ShowRaw）。</summary>
        public bool IsRawDisplayed => CurrentVm != null && CurrentVm.ShowRaw;

        /// <summary>
        /// 当前显示行数：Summary 档 = SummaryLines.Count（异常项行已含其中，以 "▲ " 标出）；
        /// Raw 档 = RawDisplayLines.Count（过滤态 = 头行 + 命中行，&lt; RawLines.Count）。
        /// PlayMode C4「Raw 切换后行数 &gt; Summary」与 C6「过滤视图行数 &lt; 全量」的断言口径。
        /// </summary>
        public int DisplayedLineCount { get; private set; }

        /// <summary>UGUI 层级是否已构建（OnInit 后 true；裸实例 = false）。</summary>
        public bool IsBuilt => _root != null;

        /// <summary>面板 GameObject 是否处于显示状态（未构建 = false）。</summary>
        public bool IsVisible => _root != null && _root.activeSelf;

        /// <summary>「查原始记录」UGUI Toggle（PlayMode 走真实控件路径；裸实例 = null）。</summary>
        public Toggle RawToggle => _rawToggle;

        /// <summary>正文滚动区（TASK-A-COL-07；裸实例 = null）。</summary>
        public ScrollRect BodyScroll => _scroll;

        /// <summary>滚动 Content（C5「content 高 &gt; viewport 高」断言口径；裸实例 = null）。</summary>
        public RectTransform ScrollContent => _scrollContent;

        /// <summary>滚动 Viewport（C5 断言口径；裸实例 = null）。</summary>
        public RectTransform ScrollViewport => _scrollViewport;

        /// <summary>「情报渠道不足」提示行是否可见（余额不足时常驻；裸实例 = false）。</summary>
        public bool IsRawHintVisible => _hintText != null && _hintText.gameObject.activeSelf;

        /// <summary>顶部情报点标签文本（C7 断言口径；裸实例 = null）。</summary>
        public string IntelPointsLabel => _intelText != null ? _intelText.text : null;

        // ---- IUIView（UIManager 保证 OnInit → OnShow → (OnPause/OnResume)* → OnHide → OnDestroy）----

        /// <inheritdoc />
        public void OnInit(object args)
        {
            Build();
        }

        /// <inheritdoc />
        public void OnShow()
        {
            if (_root != null)
            {
                _root.SetActive(true);
            }
        }

        /// <inheritdoc />
        public void OnPause()
        {
            // 被更高层 BlockMask 面板压住：本切片不做视觉变化（GraphicRaycaster 由 UIManager 关闭）。
        }

        /// <inheritdoc />
        public void OnResume()
        {
        }

        /// <inheritdoc />
        public void OnHide()
        {
            if (_root != null)
            {
                _root.SetActive(false);
            }
        }

        /// <inheritdoc />
        public void OnDestroy()
        {
            if (_closeButton != null)
            {
                _closeButton.onClick.RemoveAllListeners();
            }

            if (_rawToggle != null)
            {
                _rawToggle.onValueChanged.RemoveAllListeners();
            }

            foreach (KeyValuePair<int, Button> pair in _anomalyButtons)
            {
                if (pair.Value != null)
                {
                    pair.Value.onClick.RemoveAllListeners();
                }
            }
            _anomalyButtons.Clear();

            if (_root != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(_root);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(_root);
                }
            }

            _root = null;
            _titleText = null;
            _intelText = null;
            _scroll = null;
            _scrollViewport = null;
            _scrollContent = null;
            _hintText = null;
            _toggleLabel = null;
            _rawToggle = null;
            _closeButton = null;
        }

        // ---- 渲染与交互（P 层入口）-------------------------------------------------

        /// <summary>呈现一帧 VM：更新 VM 状态（含裸实例）+ 有 UGUI 时刷新文本、档位显示与正文行。</summary>
        public void Render(ReportPanelVm vm)
        {
            CurrentVm = vm;
            bool showRaw = vm != null && vm.ShowRaw && vm.HasReport;
            DisplayedLineCount = vm == null
                ? 0
                : showRaw ? vm.RawDisplayLines.Count : vm.SummaryLines.Count;

            if (_titleText == null || vm == null)
            {
                return; // 裸实例（EditMode P 层测试）或空 VM：只更新状态
            }

            _titleText.text = vm.TitleLine;
            _intelText.text = vm.IntelPoints.HasValue ? $"情报点 {vm.IntelPoints.Value}" : "情报点 -";
            _toggleLabel.text = showRaw
                ? "☑ 查原始记录（Raw · Truth 全量，含 Covert）"
                : "□ 查原始记录（消耗 1 情报点）";
            _hintText.gameObject.SetActive(!vm.RawAffordable);
            _rawToggle.interactable = vm.RawAffordable || showRaw;   // Raw 已开（余额归零）时仍可关回去
            RebuildBodyRows(vm, showRaw);
        }

        /// <summary>程序侧同步 toggle 状态（不回流 RawToggleChanged——P 层已知情，避免回路）。</summary>
        public void SetRawToggleState(bool isOn)
        {
            if (_rawToggle == null || _rawToggle.isOn == isOn)
            {
                return;
            }

            _suppressToggleEvent = true;
            _rawToggle.isOn = isOn;
            _suppressToggleEvent = false;
        }

        /// <summary>真实 UGUI 路径与测试共用的 toggle 通知入口（toggle 回调也走这里）。</summary>
        public void NotifyRawToggleChanged(bool isOn)
        {
            if (_suppressToggleEvent)
            {
                return;
            }

            RawToggleChanged?.Invoke(isOn);
        }

        /// <summary>异常项点击通知入口（行按钮回调与测试共用同一入口，NotifyRawToggleChanged 同款）。</summary>
        public void NotifyAnomalyClicked(int anomalyIndex)
        {
            AnomalyClicked?.Invoke(anomalyIndex);
        }

        /// <summary>
        /// 真实 UGUI 点击路径（PlayMode C6）：调用指定异常项行按钮的 onClick——
        /// 与用户点击同一条 UGUI 事件链（RawToggle.isOn 同款口径），裸实例/无该行时无害。
        /// </summary>
        public void ClickAnomalyRow(int anomalyIndex)
        {
            foreach (KeyValuePair<int, Button> pair in _anomalyButtons)
            {
                if (pair.Key == anomalyIndex && pair.Value != null)
                {
                    pair.Value.onClick.Invoke();
                    return;
                }
            }
        }

        /// <summary>「关闭」请求（按钮回调与测试共用同一入口）。</summary>
        public void RequestClose()
        {
            CloseRequested?.Invoke();
        }

        // ---- 代码构建 UGUI ------------------------------------------------------------

        private void Build()
        {
            Canvas layer = UIRoot.EnsureCreated().GetLayerCanvas(UIConsts.UILayer.Panel);
            if (layer == null)
            {
                throw new InvalidOperationException("UIRoot Panel 层画布缺失（UIRoot 未完成 8 层构建）");
            }

            _root = new GameObject("~ReportPanel", typeof(RectTransform), typeof(Image));
            _root.transform.SetParent(layer.transform, false);
            Stretch((RectTransform)_root.transform);
            _root.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.10f, 0.94f);

            // 标题（顶部条左）
            _titleText = CreateText(_root.transform, "Title", 30, FontStyle.Bold, TextAnchor.UpperLeft);
            Anchor(_titleText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f));
            _titleText.rectTransform.offsetMin = new Vector2(28f, -64f);
            _titleText.rectTransform.offsetMax = new Vector2(-300f, -14f);

            // 顶部常驻情报点余额（右）
            _intelText = CreateText(_root.transform, "IntelPoints", 22, FontStyle.Bold, TextAnchor.MiddleRight);
            Anchor(_intelText.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0.5f));
            _intelText.rectTransform.offsetMin = new Vector2(-300f, -60f);
            _intelText.rectTransform.offsetMax = new Vector2(-28f, -14f);

            // 正文滚动区（ScrollRect：Viewport 裁剪 + Content 上锚定生长）
            var scrollGo = new GameObject("BodyScroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(_root.transform, false);
            var scrollRectT = (RectTransform)scrollGo.transform;
            Anchor(scrollRectT, Vector2.zero, Vector2.one);
            scrollRectT.offsetMin = new Vector2(28f, 96f);
            scrollRectT.offsetMax = new Vector2(-28f, -72f);
            scrollGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);   // 极淡底 + 拖拽射线承接

            _scroll = scrollGo.GetComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 24f;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            _scrollViewport = (RectTransform)viewportGo.transform;
            Stretch(_scrollViewport);
            viewportGo.GetComponent<Image>().color = Color.clear;                  // 只承接拖拽射线，不显形
            _scroll.viewport = _scrollViewport;

            var contentGo = new GameObject("Content", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewportGo.transform, false);
            _scrollContent = (RectTransform)contentGo.transform;
            Anchor(_scrollContent, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f)); // 上锚定、向下生长
            _scrollContent.offsetMin = Vector2.zero;
            _scrollContent.offsetMax = Vector2.zero;

            VerticalLayoutGroup layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 6f;
            layout.padding = new RectOffset(12, 12, 10, 10);

            ContentSizeFitter fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;        // 宽随锚点满宽
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;          // 高随行数生长

            _scroll.content = _scrollContent;

            // 底部提示行（情报渠道不足，默认隐藏）
            _hintText = CreateText(_root.transform, "RawHint", 16, FontStyle.Normal, TextAnchor.MiddleCenter);
            Anchor(_hintText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f));
            _hintText.rectTransform.offsetMin = new Vector2(28f, 60f);
            _hintText.rectTransform.offsetMax = new Vector2(620f, 88f);
            _hintText.text = "情报渠道不足（每月 +1）";
            _hintText.color = HighlightColor;
            _hintText.gameObject.SetActive(false);

            // 「查原始记录」toggle（底部左）
            var toggleGo = new GameObject("RawToggle", typeof(RectTransform), typeof(Image), typeof(Toggle));
            toggleGo.transform.SetParent(_root.transform, false);
            var toggleRect = (RectTransform)toggleGo.transform;
            Anchor(toggleRect, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0.5f, 0.5f));
            toggleRect.offsetMin = new Vector2(28f, 18f);
            toggleRect.offsetMax = new Vector2(560f, 56f);
            toggleGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.10f);

            _rawToggle = toggleGo.GetComponent<Toggle>();
            _rawToggle.targetGraphic = toggleGo.GetComponent<Image>();
            _toggleLabel = CreateText(toggleRect, "Label", 18, FontStyle.Normal, TextAnchor.MiddleLeft);
            Anchor(_toggleLabel.rectTransform, Vector2.zero, Vector2.one);
            _toggleLabel.rectTransform.offsetMin = new Vector2(12f, 2f);
            _toggleLabel.rectTransform.offsetMax = new Vector2(-12f, -2f);
            _rawToggle.onValueChanged.AddListener(NotifyRawToggleChanged);

            // 「关闭」按钮（底部右）
            var closeGo = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
            closeGo.transform.SetParent(_root.transform, false);
            var closeRect = (RectTransform)closeGo.transform;
            Anchor(closeRect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0.5f));
            closeRect.offsetMin = new Vector2(-188f, 18f);
            closeRect.offsetMax = new Vector2(-28f, 56f);
            closeGo.GetComponent<Image>().color = new Color(0.85f, 0.85f, 0.85f, 0.18f);

            _closeButton = closeGo.GetComponent<Button>();
            _closeButton.targetGraphic = closeGo.GetComponent<Image>();
            Text closeLabel = CreateText(closeRect, "Label", 18, FontStyle.Normal, TextAnchor.MiddleCenter);
            Anchor(closeLabel.rectTransform, Vector2.zero, Vector2.one);
            closeLabel.rectTransform.offsetMin = new Vector2(4f, 2f);
            closeLabel.rectTransform.offsetMax = new Vector2(-4f, -2f);
            closeLabel.text = "关闭";
            _closeButton.onClick.AddListener(RequestClose);
        }

        /// <summary>
        /// 重建正文行（每次 Render 全量重建——面板月度低频操作，规模可承受）：
        /// Summary 档 = SummaryLines 逐行（"▲ " 异常项行带 Button 上抛跳转）；
        /// Raw 档 = RawDisplayLines 逐行（命中行文字高亮色）。
        /// 行 = Text 直接挂 Content（Text 自身就是 ILayoutElement——VerticalLayoutGroup 只对
        /// 直接子物体做尺寸控制，中间再包裸 RectTransform 会把行高压成 0，PlayMode C5 实证）。
        /// </summary>
        private void RebuildBodyRows(ReportPanelVm vm, bool showRaw)
        {
            ClearBodyRows();

            if (showRaw)
            {
                for (int i = 0; i < vm.RawDisplayLines.Count; i++)
                {
                    bool hit = vm.RawHitFlags != null && i < vm.RawHitFlags.Count && vm.RawHitFlags[i];
                    CreateBodyRow(vm.RawDisplayLines[i], hit, fontSize: 16, anomalyIndex: -1);
                }
            }
            else
            {
                foreach (string line in vm.SummaryLines)
                {
                    bool anomalyRow = line.StartsWith("▲ ", StringComparison.Ordinal);
                    int anomalyIndex = anomalyRow ? IndexOfAnomaly(vm, line) : -1;
                    CreateBodyRow(line, anomalyRow, fontSize: 20, anomalyIndex);
                }
            }

            if (_scrollContent != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_scrollContent);   // 同帧可读 content 高度（C5 断言口径）
            }
        }

        /// <summary>异常项行 → AnomalyLines 下标（Summary 行与高亮清单同为 "▲ " + 原文，精确相等）。</summary>
        private static int IndexOfAnomaly(ReportPanelVm vm, string line)
        {
            for (int i = 0; i < vm.AnomalyLines.Count; i++)
            {
                if (string.Equals(vm.AnomalyLines[i], line, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;   // 页数预算截断等边缘：行在清单外则不可点
        }

        /// <summary>
        /// 创建一行正文：Text 直接挂在 Content 下（布局组按 Text preferredHeight 控行高）；
        /// 异常项行（anomalyIndex ≥ 0）加 Button（targetGraphic=文字本身，可点）；
        /// 其余行 raycastTarget=false 不挡滚动拖拽。
        /// </summary>
        private void CreateBodyRow(string line, bool highlight, int fontSize, int anomalyIndex)
        {
            if (_scrollContent == null)
            {
                return;
            }

            Text label = CreateText(_scrollContent.transform, "Row", fontSize, FontStyle.Normal, TextAnchor.UpperLeft);
            label.text = line;
            label.color = highlight ? HighlightColor : NormalRowColor;
            label.raycastTarget = anomalyIndex >= 0;                           // 仅异常项行承接点击射线
            if (anomalyIndex >= 0)
            {
                Button button = label.gameObject.AddComponent<Button>();
                button.targetGraphic = label;
                button.transition = Selectable.Transition.None;                // 高亮色由文字色承担，不叠悬停染色
                int capturedIndex = anomalyIndex;
                button.onClick.AddListener(() => NotifyAnomalyClicked(capturedIndex));
                _anomalyButtons.Add(new KeyValuePair<int, Button>(capturedIndex, button));
            }
        }

        /// <summary>清空正文行（失活再销毁：布局组同帧不再计入旧行，高度即时正确）。</summary>
        private void ClearBodyRows()
        {
            foreach (KeyValuePair<int, Button> pair in _anomalyButtons)
            {
                if (pair.Value != null)
                {
                    pair.Value.onClick.RemoveAllListeners();
                }
            }
            _anomalyButtons.Clear();

            if (_scrollContent == null)
            {
                return;
            }

            for (int i = _scrollContent.childCount - 1; i >= 0; i--)
            {
                Transform child = _scrollContent.GetChild(i);
                child.gameObject.SetActive(false);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }
        }

        private static void Stretch(RectTransform rect)
        {
            Anchor(rect, Vector2.zero, Vector2.one);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Anchor(RectTransform rect, Vector2 min, Vector2 max, Vector2 pivot)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.pivot = pivot;
        }

        private static void Anchor(RectTransform rect, Vector2 min, Vector2 max)
        {
            Anchor(rect, min, max, new Vector2(0.5f, 0.5f));
        }

        private static Text CreateText(Transform parent, string name, int size, FontStyle style, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = BuiltinFont();
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = Color.white;
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>
        /// 内置字体（无字体资产依赖，照 GameHud「不依赖字体资产」的取舍）：
        /// 2022.2+ 内置名 LegacyRuntime.ttf，旧名 Arial 兜底。
        /// </summary>
        private static Font BuiltinFont()
        {
            try
            {
                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font != null)
                {
                    return font;
                }
            }
            catch (Exception)
            {
                // fall through to legacy name
            }

            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }
}
