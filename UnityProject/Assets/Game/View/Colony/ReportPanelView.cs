// ReportPanelView.cs —— TASK-A-COL-05：月度报告面板视图（MVP 的 V 层）。
// UIManager 的 pure-view 通道（UIManager.Push 对非 Component 视图跳过 prefab 加载）：
// 本类不进 prefab 资产，OnInit 时**代码构建** UGUI 层级（简报「若需 prefab 则代码构建 UI 元素」），
// 挂在 UIRoot 的 Panel 层画布下；OnDestroy 销毁自己的 GameObject（CachePolicy.None，Pop 即拆）。
// 呈现只吃 <see cref="ReportPanelVm"/>；「查原始记录」toggle 与「关闭」按钮只上抛事件，
// 业务决定权在 <see cref="ReportPresenter"/>（MVP：V 不做决策）。
// EditMode 可裸实例化（不调 OnInit 就没有 GameObject）——P 层测试走 Attach→Render 全链路。

using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.View;

namespace Game.View.Colony
{
    /// <summary>月度报告面板视图：代码构建的 UGUI 面板，纯 C# IUIView 实现。</summary>
    public sealed class ReportPanelView : IUIView
    {
        /// <summary>「查原始记录」toggle 变化（用户点击或程序切换）→ P 层。</summary>
        public event Action<bool> RawToggleChanged;

        /// <summary>「关闭」请求 → P 层（宿主注入的 Close 意图）。</summary>
        public event Action CloseRequested;

        private const string HighlightColorTag = "<color=#D8A24A>"; // 异常项高亮（琥珀色）

        private GameObject _root;
        private Text _titleText;
        private Text _bodyText;      // Summary 视野（默认）
        private Text _rawText;       // Raw 视野（查原始记录）
        private Text _toggleLabel;
        private Toggle _rawToggle;
        private Button _closeButton;
        private bool _suppressToggleEvent;

        /// <summary>最近一次 Render 的 VM（测试断言口径；未 Render 前 null）。</summary>
        public ReportPanelVm CurrentVm { get; private set; }

        /// <summary>当前显示档位（true = Raw；跟随 CurrentVm.ShowRaw）。</summary>
        public bool IsRawDisplayed => CurrentVm != null && CurrentVm.ShowRaw;

        /// <summary>
        /// 当前显示行数：Summary 档 = SummaryLines.Count（异常项行已含其中，以 "▲ " 标出）；
        /// Raw 档 = RawLines.Count。PlayMode「Raw 切换后行数 &gt; Summary」的断言口径。
        /// </summary>
        public int DisplayedLineCount { get; private set; }

        /// <summary>UGUI 层级是否已构建（OnInit 后 true；裸实例 = false）。</summary>
        public bool IsBuilt => _root != null;

        /// <summary>面板 GameObject 是否处于显示状态（未构建 = false）。</summary>
        public bool IsVisible => _root != null && _root.activeSelf;

        /// <summary>「查原始记录」UGUI Toggle（PlayMode 走真实控件路径；裸实例 = null）。</summary>
        public Toggle RawToggle => _rawToggle;

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
            _bodyText = null;
            _rawText = null;
            _toggleLabel = null;
            _rawToggle = null;
            _closeButton = null;
        }

        // ---- 渲染与交互（P 层入口）-------------------------------------------------

        /// <summary>呈现一帧 VM：更新 VM 状态（含裸实例）+ 有 UGUI 时刷新文本与档位显示。</summary>
        public void Render(ReportPanelVm vm)
        {
            CurrentVm = vm;
            bool showRaw = vm != null && vm.ShowRaw && vm.HasReport;
            DisplayedLineCount = vm == null
                ? 0
                : showRaw ? vm.RawLines.Count : vm.SummaryLines.Count;

            if (_titleText == null || vm == null)
            {
                return; // 裸实例（EditMode P 层测试）或空 VM：只更新状态
            }

            _titleText.text = vm.TitleLine;
            _bodyText.text = RenderSummaryBody(vm);
            _rawText.text = string.Join("\n", vm.RawLines);
            _toggleLabel.text = showRaw
                ? "☑ 查原始记录（Raw · Truth 全量，含 Covert）"
                : "□ 查原始记录（默认只看 Summary）";
            _rawText.gameObject.SetActive(showRaw);
            _bodyText.gameObject.SetActive(!showRaw);
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

            // 标题（顶部条）
            _titleText = CreateText(_root.transform, "Title", 30, FontStyle.Bold, TextAnchor.UpperLeft);
            Anchor(_titleText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f));
            _titleText.rectTransform.offsetMin = new Vector2(28f, -64f);
            _titleText.rectTransform.offsetMax = new Vector2(-28f, -14f);

            // Summary / Raw 两个正文体（同位互斥）
            _bodyText = CreateText(_root.transform, "SummaryBody", 20, FontStyle.Normal, TextAnchor.UpperLeft);
            Anchor(_bodyText.rectTransform, Vector2.zero, Vector2.one);
            _bodyText.rectTransform.offsetMin = new Vector2(28f, 72f);
            _bodyText.rectTransform.offsetMax = new Vector2(-28f, -72f);

            _rawText = CreateText(_root.transform, "RawBody", 16, FontStyle.Normal, TextAnchor.UpperLeft);
            Anchor(_rawText.rectTransform, Vector2.zero, Vector2.one);
            _rawText.rectTransform.offsetMin = new Vector2(28f, 72f);
            _rawText.rectTransform.offsetMax = new Vector2(-28f, -72f);
            _rawText.gameObject.SetActive(false);

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

        /// <summary>Summary 正文：逐行拼接；"▲ " 开头的异常项行包高亮色（Rich Text）。</summary>
        private static string RenderSummaryBody(ReportPanelVm vm)
        {
            var sb = new StringBuilder();
            foreach (string line in vm.SummaryLines)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                if (line.StartsWith("▲ ", StringComparison.Ordinal))
                {
                    sb.Append(HighlightColorTag).Append(line).Append("</color>"); // 异常项高亮行
                }
                else
                {
                    sb.Append(line);
                }
            }

            return sb.ToString();
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
