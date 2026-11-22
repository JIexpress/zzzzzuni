// ColonyReportProbeTests.cs —— TASK-A-COL-08：月度报告面板截图探针（UI 回归护栏）。
//
// 激活口径（FrameBaselineProbe 同一惰性范式）：只在命令行带 -acol08probe 时执行；
// 无该参数的任何运行（编辑器正常 Play、全量 PlayMode 家底轮次）一律 Assert.Ignore
// 跳过——零对象、零日志、零开销。runner 是 Tools/probe_colony_report.ps1。
//
// 场景链（全部只读消费产品 API，零产品文件改动）：
//   ColonyWorldHost 初始化（SCN_Game 运行时装配）→ 残垣城双镇压编排（保证异常项
//   确定性涌现，ColonyVerticalSliceTests.C6 同款）→ DebugFastForward(31) → 月报就绪
//   → OpenReportPanel（宿主 presenter API）→ 内容锚采集 → 截图 ×2 → 锚 JSON 落盘。
//
// 内容锚（B-PLANET-15 先例：杜绝"非黑即过"恒真断言——每条锚都要证实"面板真的
// 渲染了报告内容"，而不是"某个对象存在"）：
//   A1 面板 GameObject 存在：UIRoot Panel 层画布下确有 "~ReportPanel" 活动对象；
//   A2 Summary 行文本非空：场上渲染行数 == VM 行数、≥1 行非空白文本、
//      ≥1 行与 VM.SummaryLines 精确匹配（VM 的一行真的出现在 UGUI Text 里）；
//   A3 异常项高亮元素 ≥1：VM AnomalyLines ≥1 且 场上有 "▲ " 前缀行、其 Text 色 ==
//      高亮琥珀色、且行上真挂 Button（可点击实体）；
//   A4 Raw 切换后行数变化：真 UGUI toggle 路径（情报点付费闸门扣 1）后，
//      Raw 渲染行数 == VM.RawDisplayLines.Count 且 ≠ Summary 行数（差值落 JSON）；
//   A5 截图像素取证：PNG 非黑占比 / 亮色（正文）像素 / 琥珀（高亮）像素 / 两视图
//      差异占比落 JSON——门禁 runner 用地板值判读，杜绝纯黑图蒙混。
//
// 产物：Tools/Probes/out/colony_report-<UTC>.json + -summary.png + -raw.png
//（gitignore，逐轮原始档只做本地证据）；基线快照以
// Tools/Probes/baseline/colony-report-baseline.json 为准（runner -UpdateBaseline 生成）。
// 断言排在写档之后：锚失败时 JSON/PNG 已落盘，门禁能拿到"有数据的 RED"而不是 MISS。
//
// 取证隔离（ColonyVerticalSliceTests 同款纪律）：宿主 DontDestroyOnLoad——前序残留先
// 销毁并断言干净；[UnityTearDown] 面板 Pop + 宿主销毁 + 清理断言 + EventBus.FlushQueued。
// 探针相机不碰任何产品相机/UI 状态：Panel 层画布仅在截图瞬间临时切 ScreenSpaceCamera
//（与像素探针 rig 同思路——batchmode 无 GameView，相机→RT 是唯一确定性出图通道），
// 读回后立即还原 ScreenSpaceOverlay（HANDOFF 登记过 Canvas 模式静默转换坑，显式重申）。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;
using Game.View.Colony;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.PlayMode.Tests
{
    /// <summary>
    /// 月报面板截图探针（-acol08probe 惰性启用）：内容锚 ×4 + 截图像素取证，
    /// 产物供 Tools/probe_colony_report.ps1 与基线快照比对。
    /// </summary>
    public sealed class ColonyReportProbeTests
    {
        /// <summary>惰性启用开关（与 FrameBaselineProbe.CommandLineFlag 同范式）。</summary>
        public const string CommandLineFlag = "-acol08probe";

        /// <summary>探针截图分辨率（batchmode 相机→RT 固定口径，与窗口无关）。</summary>
        public const int ShotWidth = 1280;
        public const int ShotHeight = 720;

        private ProbeCaptureRig _rig;
        private Texture2D _summaryShot;
        private Texture2D _rawShot;

        // ────────────────────────────── 探针用例 ──────────────────────────────

        /// <summary>
        /// R1：全链探针——宿主建局 → 编排异常项 → 快进月结 → 开面板 → 内容锚 ×4 →
        /// 截图 ×2 → 锚 JSON/PNG 落 Tools/Probes/out/。断言在写档之后（失败时证据已在盘）。
        /// </summary>
        [UnityTest]
        public IEnumerator R1_ReportPanelProbe_CapturesContentAnchorsAndScreenshots()
        {
            if (!IsProbeActivated())
            {
                Assert.Ignore("惰性探针（FrameBaselineProbe 同范式）：仅命令行带 -acol08probe 时执行；无参数 = 跳过零开销");
                yield break;
            }

            _rig = new ProbeCaptureRig();

            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;

            Assert.IsTrue(host.IsWorldReady, "前提：宿主完成数据驱动建局（真实五表 + 固定种子）");

            // 异常项编排（C6 口径）：残垣城双镇压 → 组织度越阈 → 第 7 天组织者涌现，
            // Uprising Covert 事件落在月 1 窗口 [1,30] → Summary AnomalyLines 确定性 ≥1。
            Settlement repressed = host.World.SettlementOf("残垣城");
            Assert.IsNotNull(repressed, "前提：残垣城聚落存在（places.csv 人口第二）");
            repressed.Apply(Decision.Harsh);
            repressed.Apply(Decision.Harsh);

            // 冻结时钟（C1 证实的能力）：截图期间世界零推进——跨跑截图逐帧同状态。
            host.DaysPerRealSecond = 0f;
            host.DebugFastForward(31); // 逐日走宿主同一条推进路径（月结照常触发）

            Assert.IsNotNull(host.LatestReport, "月报就绪：满 30 游戏日必须已结算");
            Assert.AreEqual(1, host.LatestReport.Month, "第 1 个月度报告");

            host.OpenReportPanel();
            yield return WaitFor(() =>
                UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>(),
                "报告面板入栈");

            Assert.IsTrue(UIManager.Instance.TryGetView(out ReportPanelView view), "面板栈上必须取回视图");

            // ---- A1：面板 GameObject 存在于 UIRoot Panel 层画布下 ----
            Canvas panelLayerCanvas = UIRoot.EnsureCreated().GetLayerCanvas(UIConsts.UILayer.Panel);
            Transform panelRoot = panelLayerCanvas != null
                ? panelLayerCanvas.transform.Find("~ReportPanel")
                : null;
            bool panelObjectExists = panelRoot != null && panelRoot.gameObject.activeInHierarchy;
            bool underPanelLayerCanvas = panelRoot != null && panelRoot.parent == panelLayerCanvas.transform;
            bool onStack = UIManager.Instance.IsOnStack<ReportPanelView>();
            bool visible = view.IsVisible;
            bool built = view.IsBuilt;

            // ---- A2/A3：Summary 内容锚（VM ↔ 场上渲染行互证）----
            ReportPanelVm summaryVm = view.CurrentVm;
            Assert.IsNotNull(summaryVm, "前提：面板已 Render VM");
            List<string> summaryRenderedTexts = CollectRenderedRowTexts(view);
            List<Color> summaryRenderedColors = CollectRenderedRowColors(view);

            int summaryRowsVm = summaryVm.SummaryLines.Count;
            int summaryRowsRendered = summaryRenderedTexts.Count;
            bool summaryRowsEqualsVm = summaryRowsRendered == summaryRowsVm;
            bool summaryRenderedNonEmpty = HasNonWhitespace(summaryRenderedTexts);
            int summaryExactMatchCount = CountExactMatches(summaryRenderedTexts, summaryVm.SummaryLines);

            int anomalyLinesVm = summaryVm.AnomalyLines.Count;
            int anomalyRowsRendered = CountPrefixed(summaryRenderedTexts, "▲ ");
            int anomalyHighlightedRows = CountHighlightColored(summaryRenderedColors, summaryRenderedTexts, "▲ ");
            int anomalyButtons = CountAnomalyButtons(view);

            // ---- 截图 1：Summary 档 ----
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture);
            string outDir = ResolveOutDir();
            string summaryPng = Path.GetFileName(ProbePath(outDir, stamp, "summary"));
            string rawPng = Path.GetFileName(ProbePath(outDir, stamp, "raw"));
            string jsonPath = Path.Combine(outDir, "colony_report-" + stamp + ".json");

            _rig.Build();
            _summaryShot = new Texture2D(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            _rawShot = new Texture2D(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            yield return _rig.CaptureInto(_summaryShot, panelLayerCanvas);

            // ---- A4：Raw 切换（真 UGUI toggle 路径，情报点付费闸门）----
            int intelBefore = host.World.Governor.IntelPoints;
            bool toggleInteractable = view.RawToggle != null && view.RawToggle.interactable;
            view.RawToggle.isOn = true; // onValueChanged → P 层重渲染（C4/C5 同款真实控件路径）
            yield return null;

            ReportPanelVm rawVm = view.CurrentVm;
            Assert.IsNotNull(rawVm, "前提：Raw 重渲染完成");
            List<string> rawRenderedTexts = CollectRenderedRowTexts(view);
            int rawRowsVm = rawVm.RawDisplayLines.Count;
            int rawRowsRendered = rawRenderedTexts.Count;
            bool rawRowsEqualsVm = rawRowsRendered == rawRowsVm;
            bool rawRowCountChanged = rawRowsRendered != summaryRowsRendered;
            int rawDelta = rawRowsRendered - summaryRowsRendered;
            int intelAfter = host.World.Governor.IntelPoints;
            bool paidGateConsumedIntel = intelAfter == intelBefore - 1;

            // ---- 截图 2：Raw 档 ----
            yield return _rig.CaptureInto(_rawShot, panelLayerCanvas);
            _rig.Dispose();

            // ---- A5：像素取证（sRGB 字节域统计）----
            ProbeCaptureRig.ImageStats summaryStats = ProbeCaptureRig.Measure(_summaryShot);
            ProbeCaptureRig.ImageStats rawStats = ProbeCaptureRig.Measure(_rawShot);
            double diffRatio = ProbeCaptureRig.DiffRatio(_summaryShot, _rawShot);

            WritePng(ProbePath(outDir, stamp, "summary"), _summaryShot.EncodeToPNG());
            WritePng(ProbePath(outDir, stamp, "raw"), _rawShot.EncodeToPNG());

            bool summaryPngWritten = File.Exists(Path.Combine(outDir, summaryPng)) &&
                                     new FileInfo(Path.Combine(outDir, summaryPng)).Length > 0;
            bool rawPngWritten = File.Exists(Path.Combine(outDir, rawPng)) &&
                                 new FileInfo(Path.Combine(outDir, rawPng)).Length > 0;

            // ---- 锚 JSON（先落盘、后断言：失败时门禁仍拿到有数据的 RED）----
            WriteJson(jsonPath, new ProbeRecord
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                UnityVersion = Application.unityVersion,
                Month = summaryVm.Month,
                TitleLine = summaryVm.TitleLine,
                PanelObjectExists = panelObjectExists,
                UnderPanelLayerCanvas = underPanelLayerCanvas,
                OnStack = onStack,
                Visible = visible,
                Built = built,
                SummaryRowsVm = summaryRowsVm,
                SummaryRowsRendered = summaryRowsRendered,
                SummaryRowsEqualsVm = summaryRowsEqualsVm,
                SummaryRenderedNonEmpty = summaryRenderedNonEmpty,
                SummaryExactMatchCount = summaryExactMatchCount,
                AnomalyLinesVm = anomalyLinesVm,
                AnomalyRowsRendered = anomalyRowsRendered,
                AnomalyHighlightedRows = anomalyHighlightedRows,
                AnomalyButtons = anomalyButtons,
                RawRowsVm = rawRowsVm,
                RawRowsRendered = rawRowsRendered,
                RawRowsEqualsVm = rawRowsEqualsVm,
                RawRowCountChanged = rawRowCountChanged,
                RawDelta = rawDelta,
                ToggleInteractable = toggleInteractable,
                IntelBefore = intelBefore,
                IntelAfter = intelAfter,
                PaidGateConsumedIntel = paidGateConsumedIntel,
                SummaryPng = summaryPng,
                RawPng = rawPng,
                SummaryNonBlackRatio = summaryStats.NonBlackRatio,
                SummaryBrightPixels = summaryStats.BrightPixels,
                SummaryAmberPixels = summaryStats.AmberPixels,
                RawNonBlackRatio = rawStats.NonBlackRatio,
                RawBrightPixels = rawStats.BrightPixels,
                RawAmberPixels = rawStats.AmberPixels,
                SummaryVsRawDiffRatio = diffRatio,
                FirstSummaryLine = summaryVm.SummaryLines.Count > 0 ? summaryVm.SummaryLines[0] : "",
                FirstRawLine = rawVm.RawDisplayLines.Count > 0 ? rawVm.RawDisplayLines[0] : "",
            });

            // ---- 断言（写档之后；任何一条红 = UI 回归，runner 拿 JSON 判 RED exit 1）----
            Assert.IsTrue(panelObjectExists, "A1 内容锚：~ReportPanel 必须作为活动对象存在于 Panel 层画布下");
            Assert.IsTrue(underPanelLayerCanvas, "A1 内容锚：面板必须挂在 UIRoot Panel 层画布下");
            Assert.IsTrue(onStack && visible && built, "A1 内容锚：面板必须入栈、显示且已构建");

            Assert.IsTrue(summaryRowsEqualsVm,
                $"A2 内容锚：Summary 渲染行数（{summaryRowsRendered}）必须等于 VM 行数（{summaryRowsVm}）");
            Assert.IsTrue(summaryRenderedNonEmpty, "A2 内容锚：Summary 行文本非空（至少一行非空白）");
            Assert.GreaterOrEqual(summaryExactMatchCount, 1,
                "A2 内容锚：至少一行场上文本与 VM.SummaryLines 精确匹配（内容真被渲染）");

            Assert.GreaterOrEqual(anomalyLinesVm, 1, "A3 内容锚：VM 异常项清单 ≥1（编排保证）");
            Assert.GreaterOrEqual(anomalyRowsRendered, 1, "A3 内容锚：场上 '▲ ' 异常行 ≥1");
            Assert.GreaterOrEqual(anomalyHighlightedRows, 1, "A3 内容锚：琥珀高亮色异常行 ≥1");
            Assert.GreaterOrEqual(anomalyButtons, 1, "A3 内容锚：异常行必须挂真实 Button（可点击实体）");

            Assert.IsTrue(rawRowsEqualsVm, $"A4 内容锚：Raw 渲染行数（{rawRowsRendered}）必须等于 VM 行数（{rawRowsVm}）");
            Assert.IsTrue(rawRowCountChanged, $"A4 内容锚：Raw 行数（{rawRowsRendered}）必须 ≠ Summary 行数（{summaryRowsRendered}）");
            Assert.Greater(rawDelta, 0, "A4 内容锚：Raw 行数必须多于 Summary（Truth 全量 vs 页数预算，C4 口径）");
            Assert.IsTrue(toggleInteractable, "A4 内容锚：情报点足额时 toggle 必须可交互");
            Assert.IsTrue(paidGateConsumedIntel,
                $"A4 内容锚：付费闸门必须真实扣 1 情报点（{intelBefore} → {intelAfter}）");

            Assert.IsTrue(summaryPngWritten, $"A5 取证：Summary PNG 必须落盘且非空（{summaryPng}）");
            Assert.IsTrue(rawPngWritten, $"A5 取证：Raw PNG 必须落盘且非空（{rawPng}）");
        }

        // ────────────────────────────── fixture 纪律 ──────────────────────────────

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // 截图产物只做本地证据（gitignore），引用及时释放防泄漏。
            if (_summaryShot != null) { UnityEngine.Object.Destroy(_summaryShot); _summaryShot = null; }
            if (_rawShot != null) { UnityEngine.Object.Destroy(_rawShot); _rawShot = null; }
            _rig?.Dispose();
            _rig = null;

            if (UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>())
            {
                UIManager.Instance.Pop(ColonyWorldHost.ReportPanelConfig);
            }

            if (ColonyWorldHost.Instance != null)
            {
                UnityEngine.Object.Destroy(ColonyWorldHost.Instance.gameObject);
            }

            yield return null;

            Assert.IsNull(ColonyWorldHost.Instance, "清理断言：宿主必须已销毁（fixture 不泄漏）");
            if (UIManager.Instance != null)
            {
                Assert.IsFalse(UIManager.Instance.IsOnStack<ReportPanelView>(), "清理断言：报告面板必须已回栈");
            }

            EventBus.FlushQueued();
        }

        /// <summary>
        /// 取证隔离入口（ColonyVerticalSliceTests 同款）：销毁前序残留宿主 → 单模式加载
        /// SCN_Game → 等待挂载与世界就绪。世界从 Day=1 干净开始。
        /// </summary>
        private IEnumerator LoadFreshSlice()
        {
            if (ColonyWorldHost.Instance != null)
            {
                UnityEngine.Object.Destroy(ColonyWorldHost.Instance.gameObject);
                yield return null;
                Assert.IsNull(ColonyWorldHost.Instance, "环境不干净：前序宿主未销毁（fixture 泄漏）");
            }

            yield return SceneManager.LoadSceneAsync(ColonyWorldHost.GameSceneName);
            Assert.AreEqual(ColonyWorldHost.GameSceneName, SceneManager.GetActiveScene().name,
                "SCN_Game 必须成为活动场景");
            yield return null;
            yield return null;

            Assert.IsNotNull(ColonyWorldHost.Instance, "SCN_Game 加载后必须自动挂载 ColonyWorldHost（运行时装配）");
            yield return WaitFor(() => ColonyWorldHost.Instance.IsWorldReady, "数据驱动建局完成");
        }

        /// <summary>有界等待（10s）：条件不满足超时即红，绝不挂死轮次。</summary>
        private static IEnumerator WaitFor(Func<bool> done, string what)
        {
            float waited = 0f;
            while (!done() && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.IsTrue(done(), $"超时等待：{what}");
        }

        private static bool IsProbeActivated()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; ++i)
            {
                if (string.Equals(args[i], CommandLineFlag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ────────────────────────────── 内容锚采集 ──────────────────────────────

        /// <summary>场上渲染行正文：ScrollContent 直接子物体上的 UGUI Text.text（渲染真相）。</summary>
        private static List<string> CollectRenderedRowTexts(ReportPanelView view)
        {
            var texts = new List<string>();
            RectTransform content = view.ScrollContent;
            if (content == null)
            {
                return texts;
            }

            for (int i = 0; i < content.childCount; i++)
            {
                Text row = content.GetChild(i).GetComponent<Text>();
                if (row != null)
                {
                    texts.Add(row.text ?? string.Empty);
                }
            }

            return texts;
        }

        /// <summary>场上渲染行颜色（与文本一一对应；无 Text 的子物体补占位保持下标对齐）。</summary>
        private static List<Color> CollectRenderedRowColors(ReportPanelView view)
        {
            var colors = new List<Color>();
            RectTransform content = view.ScrollContent;
            if (content == null)
            {
                return colors;
            }

            for (int i = 0; i < content.childCount; i++)
            {
                Text row = content.GetChild(i).GetComponent<Text>();
                colors.Add(row != null ? row.color : Color.clear);
            }

            return colors;
        }

        private static bool HasNonWhitespace(List<string> texts)
        {
            for (int i = 0; i < texts.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(texts[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountExactMatches(List<string> rendered, IReadOnlyList<string> vmLines)
        {
            int count = 0;
            for (int i = 0; i < rendered.Count; i++)
            {
                for (int j = 0; j < vmLines.Count; j++)
                {
                    if (string.Equals(rendered[i], vmLines[j], StringComparison.Ordinal))
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }

        private static int CountPrefixed(List<string> rendered, string prefix)
        {
            int count = 0;
            for (int i = 0; i < rendered.Count; i++)
            {
                if (rendered[i] != null && rendered[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>琥珀高亮行数：文本带 "▲ " 前缀且颜色 == ReportPanelView 高亮色（容差 0.01）。</summary>
        private static int CountHighlightColored(List<Color> colors, List<string> texts, string prefix)
        {
            const float Epsilon = 0.01f;
            Color highlight = new Color(0.847f, 0.635f, 0.290f); // ReportPanelView.HighlightColor 同值
            int count = 0;
            for (int i = 0; i < colors.Count && i < texts.Count; i++)
            {
                if (texts[i] == null || !texts[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                Color c = colors[i];
                if (Mathf.Abs(c.r - highlight.r) <= Epsilon && Mathf.Abs(c.g - highlight.g) <= Epsilon &&
                    Mathf.Abs(c.b - highlight.b) <= Epsilon)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>场上挂了真实 Button 的异常行数（A3「可点击实体」口径）。</summary>
        private static int CountAnomalyButtons(ReportPanelView view)
        {
            int count = 0;
            RectTransform content = view.ScrollContent;
            if (content == null)
            {
                return count;
            }

            for (int i = 0; i < content.childCount; i++)
            {
                Transform child = content.GetChild(i);
                Text row = child.GetComponent<Text>();
                if (row != null && row.text != null && row.text.StartsWith("▲ ", StringComparison.Ordinal) &&
                    child.GetComponent<Button>() != null)
                {
                    count++;
                }
            }

            return count;
        }

        // ────────────────────────────── 产物落盘 ──────────────────────────────

        private static string ResolveOutDir()
        {
            // 编辑器 PlayMode 测试：Application.dataPath = <repo>/UnityProject/Assets。
            string projectDir = Path.GetDirectoryName(Application.dataPath);
            string repoRoot = projectDir == null ? null : Path.GetDirectoryName(projectDir);
            if (string.IsNullOrEmpty(repoRoot) ||
                !Directory.Exists(Path.Combine(repoRoot, "UnityProject")) ||
                !Directory.Exists(Path.Combine(repoRoot, "Tools", "Probes")))
            {
                throw new InvalidOperationException(
                    $"repo root assertion failed: resolved '{repoRoot}' from '{Application.dataPath}' lacks UnityProject/ or Tools/Probes/");
            }

            string outRoot = Path.GetFullPath(Path.Combine(repoRoot, "Tools", "Probes", "out")) +
                             Path.DirectorySeparatorChar;
            Directory.CreateDirectory(outRoot);
            return outRoot;
        }

        private static string ProbePath(string outDir, string stamp, string viewName)
        {
            string fileName = "colony_report-" + stamp + "-" + viewName + ".png";
            string full = Path.GetFullPath(Path.Combine(outDir, fileName));
            if (!full.StartsWith(outDir, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(full), fileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"output path escapes probe out dir: {full}");
            }

            return full;
        }

        private static void WritePng(string path, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("PNG encode produced no bytes (探针截图编码失败)");
            }

            File.WriteAllBytes(path, bytes);
        }

        private static void WriteJson(string path, ProbeRecord r)
        {
            var sb = new StringBuilder(8192);
            sb.Append("{\n");
            KeyStr(sb, "probe", "colony-report", 1);
            KeyStr(sb, "status", "ok", 1);
            KeyStr(sb, "generatedAtUtc", r.GeneratedAtUtc, 1);
            KeyStr(sb, "unityVersion", r.UnityVersion, 1);
            KeyInt(sb, "seed", (long)ColonyWorldHost.DefaultWorldSeed, 1);
            KeyStr(sb, "scene", ColonyWorldHost.GameSceneName, 1);
            KeyInt(sb, "month", r.Month, 1);
            KeyStr(sb, "titleLine", r.TitleLine, 1);

            sb.Append("  \"panel\": {\n");
            KeyStr(sb, "objectName", "~ReportPanel", 2);
            KeyBool(sb, "exists", r.PanelObjectExists, 2);
            KeyBool(sb, "underPanelLayerCanvas", r.UnderPanelLayerCanvas, 2);
            KeyBool(sb, "onStack", r.OnStack, 2);
            KeyBool(sb, "visible", r.Visible, 2);
            KeyBool(sb, "built", r.Built, 2);
            sb.Append("  },\n");

            sb.Append("  \"summary\": {\n");
            KeyInt(sb, "rowsVm", r.SummaryRowsVm, 2);
            KeyInt(sb, "rowsRendered", r.SummaryRowsRendered, 2);
            KeyBool(sb, "rowsRenderedEqualsVm", r.SummaryRowsEqualsVm, 2);
            KeyBool(sb, "renderedNonEmpty", r.SummaryRenderedNonEmpty, 2);
            KeyInt(sb, "exactMatchCount", r.SummaryExactMatchCount, 2);
            KeyInt(sb, "anomalyLinesVm", r.AnomalyLinesVm, 2);
            KeyInt(sb, "anomalyRowsRendered", r.AnomalyRowsRendered, 2);
            KeyInt(sb, "anomalyHighlightedRows", r.AnomalyHighlightedRows, 2);
            KeyInt(sb, "anomalyButtons", r.AnomalyButtons, 2);
            sb.Append("  },\n");

            sb.Append("  \"raw\": {\n");
            KeyInt(sb, "rowsVm", r.RawRowsVm, 2);
            KeyInt(sb, "rowsRendered", r.RawRowsRendered, 2);
            KeyBool(sb, "rowsRenderedEqualsVm", r.RawRowsEqualsVm, 2);
            KeyBool(sb, "rowCountChanged", r.RawRowCountChanged, 2);
            KeyInt(sb, "delta", r.RawDelta, 2);
            KeyBool(sb, "toggleInteractable", r.ToggleInteractable, 2);
            KeyInt(sb, "intelBefore", r.IntelBefore, 2);
            KeyInt(sb, "intelAfter", r.IntelAfter, 2);
            KeyBool(sb, "paidGateConsumedIntel", r.PaidGateConsumedIntel, 2);
            sb.Append("  },\n");

            sb.Append("  \"images\": {\n");
            KeyStr(sb, "summaryPng", r.SummaryPng, 2);
            KeyStr(sb, "rawPng", r.RawPng, 2);
            KeyInt(sb, "width", ShotWidth, 2);
            KeyInt(sb, "height", ShotHeight, 2);
            KeyNum(sb, "summaryNonBlackRatio", r.SummaryNonBlackRatio, 2);
            KeyInt(sb, "summaryBrightPixels", r.SummaryBrightPixels, 2);
            KeyInt(sb, "summaryAmberPixels", r.SummaryAmberPixels, 2);
            KeyNum(sb, "rawNonBlackRatio", r.RawNonBlackRatio, 2);
            KeyInt(sb, "rawBrightPixels", r.RawBrightPixels, 2);
            KeyInt(sb, "rawAmberPixels", r.RawAmberPixels, 2);
            KeyNum(sb, "summaryVsRawDiffRatio", r.SummaryVsRawDiffRatio, 2);
            sb.Append("  },\n");

            KeyStr(sb, "firstSummaryLine", r.FirstSummaryLine, 1);
            KeyStr(sb, "firstRawLine", r.FirstRawLine, 1);
            sb.Append("  \"notes\": [\n");
            sb.Append("    \"内容锚口径见文件头注释；门禁 = Tools/probe_colony_report.ps1（计数精确比对基线 + 布尔锚全真 + 像素地板值），RED exit 1 / MISS exit 2\",\n");
            sb.Append("    \"截图：探针相机(5000,5000,5000)→RT 1280x720（显式 sRGB），Panel 层画布临时切 ScreenSpaceCamera，读回即还原 Overlay\",\n");
            sb.Append("    \"像素统计在线性字节域（URP 离屏 RT 原样落盘，实测两轮同图）：非黑=max通道>16，亮=RGB 全 >200，琥珀=线性字节(216,162,73) 邻域（r>g>b 色相），Raw 档无异常行 → rawAmberPixels=0 是正确行为\"\n");
            sb.Append("  ]\n");
            sb.Append("}\n");

            // 各键统一以 ",\n" 结尾，收尾时去掉块尾多余逗号（严格 JSON 不接受尾逗号，
            // FrameBaselineProbe.WriteJson 同式）。
            string json = Regex.Replace(sb.ToString(), ",\n([ \t]*[}\\]])", "\n$1");
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private static void KeyStr(StringBuilder sb, string name, string value, int indent)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ");
            if (value == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('"');
                for (int i = 0; i < value.Length; ++i)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }

                            break;
                    }
                }

                sb.Append('"');
            }

            sb.Append(",\n");
        }

        private static void KeyInt(StringBuilder sb, string name, long value, int indent)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ")
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        }

        private static void KeyNum(StringBuilder sb, string name, double value, int indent)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ")
                .Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append(",\n");
        }

        private static void KeyBool(StringBuilder sb, string name, bool value, int indent)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ")
                .Append(value ? "true" : "false").Append(",\n");
        }

        private static StringBuilder Indent(StringBuilder sb, int indent)
        {
            return sb.Append(' ', indent * 2);
        }

        // ────────────────────────────── 探针相机 rig ──────────────────────────────

        /// <summary>
        /// 截图 rig：batchmode 无 GameView，相机→RT 是唯一确定性出图通道（PixelProbeRig
        /// 同思路）。相机停在远离一切场景内容的位置，Panel 层画布临时切 ScreenSpaceCamera
        /// 挂到本相机、planeDistance 内填满视锥——读回后立即还原 Overlay。
        /// </summary>
        private sealed class ProbeCaptureRig : IDisposable
        {
            /// <summary>相机停泊点：远离 SCN_Game 全部内容（PixelProbeRig.FarPark 同位）。</summary>
            public static readonly Vector3 FarPark = new Vector3(5000f, 5000f, 5000f);

            private const float CanvasPlaneDistance = 1000f;
            private const float CameraFarClip = 3000f; // 画布在 1000；场景内容 ≈8660 远——全部被远裁面剔除

            private GameObject _cameraGo;
            private Camera _camera;
            private RenderTexture _target;

            /// <summary>建相机 + RT（幂等：已建则跳过）。</summary>
            public void Build()
            {
                if (_camera != null)
                {
                    return;
                }

                _cameraGo = new GameObject("~ColonyReportProbeCam");
                _camera = _cameraGo.AddComponent<Camera>();
                _camera.transform.position = FarPark;
                _camera.transform.rotation = Quaternion.LookRotation((-FarPark).normalized, Vector3.up);
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
                _camera.cullingMask = ~0;
                _camera.nearClipPlane = 0.5f;
                _camera.farClipPlane = CameraFarClip;
                _camera.fieldOfView = 60f;
                _camera.enabled = true; // SRP 逐帧渲染带 targetTexture 的相机（PixelProbeRig 同口径）

                // 实测（2026-11-23 两轮同图）：URP 往离屏 RT 写线性值原样（显式 sRGB
                // readWrite 也不改变——B-PROBE-02 linear-bridge 同源事实），PNG 字节域 =
                // 线性值。琥珀窗口按实测线性字节定义（见 Measure 注释），不做额外转换。
                _target = new RenderTexture(ShotWidth, ShotHeight, 24);
                _target.name = "~ColonyReportProbeTarget";
                _target.Create();
                _camera.targetTexture = _target;
            }

            /// <summary>切画布到本相机 → 等渲染 → 读回 <paramref name="sink"/> → 还原 Overlay。</summary>
            public IEnumerator CaptureInto(Texture2D sink, Canvas panelLayerCanvas)
            {
                if (_camera == null)
                {
                    throw new InvalidOperationException("ProbeCaptureRig.Build() must run before CaptureInto");
                }

                panelLayerCanvas.renderMode = RenderMode.ScreenSpaceCamera;
                panelLayerCanvas.worldCamera = _camera;
                panelLayerCanvas.planeDistance = CanvasPlaneDistance;
                yield return null; // 画布重排 + 相机首帧
                yield return null; // 布局稳定帧

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _target;
                sink.ReadPixels(new Rect(0f, 0f, ShotWidth, ShotHeight), 0, 0);
                sink.Apply();
                RenderTexture.active = previous;

                // 还原（HANDOFF 登记过 Canvas 模式静默转换坑——显式重申 Overlay）。
                panelLayerCanvas.worldCamera = null;
                panelLayerCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                yield return null; // 还原帧（不把 ScreenSpaceCamera 状态留给任何后续用例）
            }

            public void Dispose()
            {
                if (_camera != null)
                {
                    _camera.targetTexture = null;
                    UnityEngine.Object.Destroy(_cameraGo);
                    _camera = null;
                    _cameraGo = null;
                }

                if (_target != null)
                {
                    _target.Release();
                    UnityEngine.Object.Destroy(_target);
                    _target = null;
                }
            }

            /// <summary>像素统计（sRGB 字节域）：非黑占比 / 亮色像素 / 琥珀像素。</summary>
            public struct ImageStats
            {
                public double NonBlackRatio;
                public int BrightPixels;
                public int AmberPixels;
            }

            public static ImageStats Measure(Texture2D texture)
            {
                Color32[] pixels = texture.GetPixels32();
                ImageStats stats = default;
                int nonBlack = 0;
                int bright = 0;
                int amber = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    int max = Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                    if (max > 16)
                    {
                        nonBlack++;
                    }

                    if (p.r > 200 && p.g > 200 && p.b > 200)
                    {
                        bright++;
                    }

                    // 琥珀高亮窗口（实测线性字节域，2026-11-23）：HighlightColor
                    // (0.847,0.635,0.290) 在离屏 RT 原样落盘 ≈ (216,162,73)，抗锯齿爬坡
                    // 落在 (118,95,65)～(216,162,73) 之间——窗口盖核心+中段爬坡
                    //（System.Drawing 全图扫 4491 个 r>g>b 色相像素，本窗口取其中核心段）。
                    if (p.r >= 150 && p.r <= 230 && p.g >= 110 && p.g <= 175 && p.b >= 45 && p.b <= 95)
                    {
                        amber++;
                    }
                }

                stats.NonBlackRatio = pixels.Length == 0 ? 0.0 : (double)nonBlack / pixels.Length;
                stats.BrightPixels = bright;
                stats.AmberPixels = amber;
                return stats;
            }

            /// <summary>两图差异占比：任一通道差 >8 的像素比例（Summary vs Raw 必须真实不同）。</summary>
            public static double DiffRatio(Texture2D a, Texture2D b)
            {
                Color32[] pa = a.GetPixels32();
                Color32[] pb = b.GetPixels32();
                if (pa.Length == 0 || pa.Length != pb.Length)
                {
                    return 0.0;
                }

                int diff = 0;
                for (int i = 0; i < pa.Length; i++)
                {
                    if (Mathf.Abs(pa[i].r - pb[i].r) > 8 || Mathf.Abs(pa[i].g - pb[i].g) > 8 ||
                        Mathf.Abs(pa[i].b - pb[i].b) > 8)
                    {
                        diff++;
                    }
                }

                return (double)diff / pa.Length;
            }
        }

        /// <summary>锚 JSON 行结构（C# 侧结构化记录；runner 按点路径取数）。</summary>
        private sealed class ProbeRecord
        {
            public string GeneratedAtUtc;
            public string UnityVersion;
            public int Month;
            public string TitleLine;
            public bool PanelObjectExists;
            public bool UnderPanelLayerCanvas;
            public bool OnStack;
            public bool Visible;
            public bool Built;
            public int SummaryRowsVm;
            public int SummaryRowsRendered;
            public bool SummaryRowsEqualsVm;
            public bool SummaryRenderedNonEmpty;
            public int SummaryExactMatchCount;
            public int AnomalyLinesVm;
            public int AnomalyRowsRendered;
            public int AnomalyHighlightedRows;
            public int AnomalyButtons;
            public int RawRowsVm;
            public int RawRowsRendered;
            public bool RawRowsEqualsVm;
            public bool RawRowCountChanged;
            public int RawDelta;
            public bool ToggleInteractable;
            public int IntelBefore;
            public int IntelAfter;
            public bool PaidGateConsumedIntel;
            public string SummaryPng;
            public string RawPng;
            public double SummaryNonBlackRatio;
            public int SummaryBrightPixels;
            public int SummaryAmberPixels;
            public double RawNonBlackRatio;
            public int RawBrightPixels;
            public int RawAmberPixels;
            public double SummaryVsRawDiffRatio;
            public string FirstSummaryLine;
            public string FirstRawLine;
        }
    }
}
