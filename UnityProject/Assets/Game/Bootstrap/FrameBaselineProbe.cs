// FrameBaselineProbe.cs —— 第一版帧耗基线探针（TASK-A-PERF-01，清单第 1 条「跑通工程 + 记帧耗基线」）
//
// 激活口径（与 PlanetRuntimeProbe 同一惰性范式）：只在 Player 命令行带 -framebaseline 时
// 启用；编辑器、测试、正常启动（无该参数）一律零对象、零日志、零开销。
//
// 采什么：
//   1. FrameTimingManager —— cpuFrameTime / cpuMainThreadFrameTime / cpuRenderThreadFrameTime
//      / gpuFrameTime（毫秒，引擎口径）；
//   2. ProfilerRecorder —— SetPass Calls Count / Batches Count / Triangles Count /
//      Vertices Count / GC Allocated In Frame（计数，引擎口径）。
//   统计给 p50/p95/p99/mean/max（+min/count），不只有平均值——抖动用分位看。
//
// 怎么到 SCN_Game：走生产路径 GameBootstrap.StartNewGame()（= 主菜单"开始"按钮的
// 同一个 public 入口 → EnterGame → SceneLoader.LoadSceneAsync）。探针不直接
// SceneManager.LoadScene，否则测的就不是玩家真走的那条装配链。
//
// 取不到的数一律 null + "MISSING: 原因"；不填 0、不填估计值。写档路径
// Tools/Probes/out/frame-baseline-<UTC>.json，文件名白名单 + out 目录边界断言
// （与 env-probe / linear-bridge 同口径），写完 Application.Quit。
//
// 自限：固定 warmup + 样本帧数后写档退出，不长期驻留。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Game.View.UI;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using ZFrame.Render;
using ZFrame.View;

namespace Game.Bootstrap
{
    [DisallowMultipleComponent]
    public sealed class FrameBaselineProbe : MonoBehaviour
    {
        /// <summary>asShipped 口径开关（玩家实际体验：质量档原样的 vsync）。</summary>
        public const string CommandLineFlag = "-framebaseline";

        /// <summary>cost 口径开关（回归对比：采样期关 vsync，量渲染/线程成本而非刷新节拍）。</summary>
        public const string CostModeFlag = "-framebaseline-vsync0";

        private const string GameSceneName = "SCN_Game";
        private const int WarmupFrames = 120;
        private const int SampleFrames = 600;
        private const int SampleHardCapFrames = SampleFrames * 5; // 帧计时短收时的止损上限
        private const int MenuSettleFrames = 30;
        private const float BootTimeoutSeconds = 180f;

        private enum Phase
        {
            WaitMenu,
            WaitGameScene,
            Warmup,
            Sampling,
            Done,
        }

        // ---- 采样缓冲（各 600 条上限；帧计时按引擎返回顺序，多收不取）----
        private readonly List<double> _cpuFrameTime = new List<double>(SampleFrames + 8);
        private readonly List<double> _cpuMainThread = new List<double>(SampleFrames + 8);
        private readonly List<double> _cpuRenderThread = new List<double>(SampleFrames + 8);
        private readonly List<double> _gpuFrameTime = new List<double>(SampleFrames + 8);
        private readonly List<long> _setPassCalls = new List<long>(SampleFrames);
        private readonly List<long> _batches = new List<long>(SampleFrames);
        private readonly List<long> _triangles = new List<long>(SampleFrames);
        private readonly List<long> _vertices = new List<long>(SampleFrames);
        private readonly List<long> _gcAllocated = new List<long>(SampleFrames);
        private readonly FrameTiming[] _timings = new FrameTiming[8];
        private readonly List<string> _notes = new List<string>();
        private readonly List<string> _missing = new List<string>();
        private readonly Dictionary<string, string> _recorderState = new Dictionary<string, string>();

        private ProfilerRecorder _recSetPass;
        private ProfilerRecorder _recBatches;
        private ProfilerRecorder _recTriangles;
        private ProfilerRecorder _recVertices;
        private ProfilerRecorder _recGc;

        private Phase _phase = Phase.WaitMenu;
        private float _phaseStartRealtime;
        private int _phaseFrames;
        private int _invalidCpuFrame;
        private int _invalidCpuMain;
        private int _invalidCpuRender;
        private int _invalidGpu;
        private int _zeroSetPass;
        private int _zeroBatches;
        private int _zeroTriangles;
        private int _zeroVertices;
        private int _zeroGc;
        private Camera _gameCamera;
        private bool _finished;

        /// <summary>true = cost 口径（采样期 vSyncCount=0 / targetFrameRate=-1，结束后恢复）。</summary>
        public bool CostMode { get; private set; }

        // vsync 两侧留痕：原值（质量档给的）与采样期的实际值
        private int _vsyncOriginal;
        private int _targetFrameRateOriginal;
        private int _vsyncSampling;
        private int _targetFrameRateSampling;
        private bool _focusAtSamplingStart;
        private int _focusLostFrames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Application.isEditor)
            {
                return; // 编辑器零行为变化
            }

            bool cost = HasCommandLineFlag(CostModeFlag);
            if (!cost && !HasCommandLineFlag(CommandLineFlag))
            {
                return; // 正常启动零行为变化：无对象、无日志
            }

            var host = new GameObject("~FrameBaselineProbe");
            DontDestroyOnLoad(host);
            FrameBaselineProbe probe = host.AddComponent<FrameBaselineProbe>();
            probe.CostMode = cost;
        }

        private static bool HasCommandLineFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; ++i)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void Start()
        {
            _phaseStartRealtime = Time.realtimeSinceStartup;
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Debug.Log($"[FrameBaseline] armed: mode={(CostMode ? "cost" : "asShipped")} unity={Application.unityVersion} " +
                      $"screen={Screen.width}x{Screen.height} " +
                      $"quality={QualitySettings.GetQualityLevel()}({QualityName(QualitySettings.GetQualityLevel())}) vSync={QualitySettings.vSyncCount} " +
                      $"pipeline={(pipeline != null ? pipeline.name : "null")} gpu={SystemInfo.graphicsDeviceName}");
        }

        private void Update()
        {
            if (_finished)
            {
                return;
            }

            switch (_phase)
            {
                case Phase.WaitMenu:
                    TickWaitMenu();
                    break;
                case Phase.WaitGameScene:
                    TickWaitGameScene();
                    break;
                case Phase.Warmup:
                    TickWarmup();
                    break;
                case Phase.Sampling:
                    TickSampling();
                    break;
            }
        }

        // ------------------------------------------------------------------ 阶段
        private void TickWaitMenu()
        {
            if (Time.realtimeSinceStartup - _phaseStartRealtime > BootTimeoutSeconds)
            {
                Finish("aborted", "MISSING: 启动链 180s 内未出现主菜单（GameBootstrap.Instance 或 MainMenuView 缺失）");
                return;
            }

            if (GameBootstrap.Instance == null || SceneLoader.IsLoading)
            {
                return;
            }

            if (FindObjectOfType<MainMenuView>(true) == null)
            {
                return; // 菜单 push 是异步的，等它真在场上再点"开始"
            }

            if (++_phaseFrames < MenuSettleFrames)
            {
                return;
            }

            // 生产路径：与主菜单"开始"按钮同一个入口。
            GameBootstrap.Instance.StartNewGame();
            _notes.Add("sceneEntry: GameBootstrap.StartNewGame() → EnterGame → SceneLoader.LoadSceneAsync(SCN_Game)（生产路径，非探针直载）");
            EnterPhase(Phase.WaitGameScene);
            Debug.Log("[FrameBaseline] main menu ready → StartNewGame() (production path)");
        }

        private void TickWaitGameScene()
        {
            if (Time.realtimeSinceStartup - _phaseStartRealtime > BootTimeoutSeconds)
            {
                Finish("aborted", $"MISSING: 180s 内未激活 {GameSceneName}（SceneLoader.IsLoading={SceneLoader.IsLoading}）");
                return;
            }

            if (SceneLoader.IsLoading)
            {
                return;
            }

            if (!string.Equals(SceneManager.GetActiveScene().name, GameSceneName, StringComparison.Ordinal))
            {
                return;
            }

            Camera[] cameras = FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; ++i)
            {
                if (cameras[i].CompareTag("MainCamera"))
                {
                    _gameCamera = cameras[i];
                    break;
                }
            }

            if (_gameCamera == null && cameras.Length > 0)
            {
                _gameCamera = cameras[0];
            }

            EnterPhase(Phase.Warmup);
            Debug.Log($"[FrameBaseline] {GameSceneName} active → warmup {WarmupFrames} frames, then sample {SampleFrames} frames");
        }

        private void TickWarmup()
        {
            // 预热期也持续抓帧计时：让引擎的环形缓冲进入稳态，丢弃即可。
            FrameTimingManager.CaptureFrameTimings();

            if (++_phaseFrames < WarmupFrames)
            {
                return;
            }

            BeginSampling();
        }

        private void TickSampling()
        {
            FrameTimingManager.CaptureFrameTimings();
            uint got = FrameTimingManager.GetLatestTimings((uint)_timings.Length, _timings);
            for (int i = 0; i < got; ++i)
            {
                FrameTiming t = _timings[i];
                AddTiming(_cpuFrameTime, t.cpuFrameTime, ref _invalidCpuFrame);
                AddTiming(_cpuMainThread, t.cpuMainThreadFrameTime, ref _invalidCpuMain);
                AddTiming(_cpuRenderThread, t.cpuRenderThreadFrameTime, ref _invalidCpuRender);
                AddTiming(_gpuFrameTime, t.gpuFrameTime, ref _invalidGpu);
            }

            AddCounter(_setPassCalls, _recSetPass, ref _zeroSetPass, true);
            AddCounter(_batches, _recBatches, ref _zeroBatches, true);
            AddCounter(_triangles, _recTriangles, ref _zeroTriangles, true);
            AddCounter(_vertices, _recVertices, ref _zeroVertices, true);
            AddCounter(_gcAllocated, _recGc, ref _zeroGc, false);

            if (!Application.isFocused)
            {
                ++_focusLostFrames; // 失焦会降帧，必须留痕（不当作正常样本的理由，但要能解释异常）
            }

            ++_phaseFrames;
            // 某口径从一开始就没有有效样本（recorder 无效 / GPU 计时不支持）时，不以它卡住采样：
            // 这类字段记 0 条 → JSON 里 null + MISSING。
            bool timingReady = Ready(_cpuFrameTime.Count) && Ready(_cpuMainThread.Count) &&
                               Ready(_cpuRenderThread.Count) && Ready(_gpuFrameTime.Count);
            bool countersReady = Ready(_setPassCalls.Count) && Ready(_batches.Count) &&
                                 Ready(_triangles.Count) && Ready(_vertices.Count) && Ready(_gcAllocated.Count);
            if ((_phaseFrames >= SampleFrames && timingReady && countersReady) || _phaseFrames >= SampleHardCapFrames)
            {
                Finish("ok", null);
            }
        }

        private void BeginSampling()
        {
            // ---- vsync 口径：两侧都留痕（原值 / 采样期实际值）；cost 模式结束后恢复原值 ----
            _vsyncOriginal = QualitySettings.vSyncCount;
            _targetFrameRateOriginal = Application.targetFrameRate;
            if (CostMode)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }

            _vsyncSampling = QualitySettings.vSyncCount;
            _targetFrameRateSampling = Application.targetFrameRate;
            _focusAtSamplingStart = Application.isFocused;

            _recSetPass = Start("SetPass Calls Count", new[] { "SetPass Calls Count" },
                ProfilerCategory.Render, ProfilerCategory.Internal);
            _recBatches = Start("Batches Count", new[] { "Batches Count" },
                ProfilerCategory.Render, ProfilerCategory.Internal);
            _recTriangles = Start("Triangles Count", new[] { "Triangles Count" },
                ProfilerCategory.Render, ProfilerCategory.Internal);
            _recVertices = Start("Vertices Count", new[] { "Vertices Count" },
                ProfilerCategory.Render, ProfilerCategory.Internal);
            _recGc = Start("GC Allocated In Frame", new[] { "GC Allocated In Frame", "Allocated In Frame" },
                ProfilerCategory.Memory, ProfilerCategory.Internal, ProfilerCategory.Scripts, ProfilerCategory.Render);

            EnterPhase(Phase.Sampling);
            Debug.Log($"[FrameBaseline] sampling start: mode={(CostMode ? "cost" : "asShipped")} " +
                      $"vSync {_vsyncOriginal}->{_vsyncSampling} targetFrameRate {_targetFrameRateOriginal}->{_targetFrameRateSampling} " +
                      $"focused={_focusAtSamplingStart} " +
                      $"setPass={_recorderState["SetPass Calls Count"]} " +
                      $"batches={_recorderState["Batches Count"]} triangles={_recorderState["Triangles Count"]} " +
                      $"vertices={_recorderState["Vertices Count"]} gc={_recorderState["GC Allocated In Frame"]} " +
                      $"gpuTimerFreq={FrameTimingManager.GetGpuTimerFrequency()} cpuTimerFreq={FrameTimingManager.GetCpuTimerFrequency()}");
        }

        private void EnterPhase(Phase phase)
        {
            _phase = phase;
            _phaseStartRealtime = Time.realtimeSinceStartup;
            _phaseFrames = 0;
        }

        // ------------------------------------------------------------------ 采集
        private static void AddTiming(List<double> target, double value, ref int invalidCount)
        {
            if (target.Count >= SampleFrames)
            {
                return;
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                ++invalidCount; // 引擎返回 0 = 该口径不可用（多为 FrameTimingStats 未开 / GPU 计时不支持）
                return;
            }

            target.Add(value);
        }

        /// <summary>该口径的样本已收够（或从一开始就取不到——0 条即视为不可用，由 JSON 报 MISSING）。</summary>
        private static bool Ready(int count)
        {
            return count == 0 || count >= SampleFrames;
        }

        private static void AddCounter(List<long> target, ProfilerRecorder recorder, ref int zeroCount, bool dropZeros)
        {
            if (target.Count >= SampleFrames || !recorder.Valid)
            {
                return;
            }

            long value = recorder.LastValue;
            if (dropZeros && value <= 0)
            {
                ++zeroCount; // recorder 刚启动/该帧未产出渲染统计：不当作 0 个绘制调用
                return;
            }

            target.Add(value);
        }

        private ProfilerRecorder Start(string label, string[] aliases, params ProfilerCategory[] candidates)
        {
            ProfilerRecorder recorder = StartRecorderAliased(label, aliases, out string resolved, candidates);
            _recorderState[label] = resolved;
            return recorder;
        }

        /// <summary>
        /// 按名字别名 × category 组合逐个试；都无效就记 MISSING（不填 0、不用替代量冒充）。
        /// 别名存在的原因：release Player 的 marker 集合与编辑器不同，同一计数器在不同
        /// 版本里归属的 category 也会变——试过的组合全部写进 missing[]，可复核。
        /// </summary>
        private ProfilerRecorder StartRecorderAliased(string label, string[] aliases, out string resolved,
            params ProfilerCategory[] candidates)
        {
            var tried = new StringBuilder();
            for (int a = 0; a < aliases.Length; ++a)
            {
                for (int i = 0; i < candidates.Length; ++i)
                {
                    ProfilerRecorder recorder = ProfilerRecorder.StartNew(candidates[i], aliases[a], 1);
                    if (recorder.Valid)
                    {
                        resolved = candidates[i].Name + "/" + aliases[a];
                        return recorder;
                    }

                    recorder.Dispose();
                    tried.Append(candidates[i].Name).Append('/').Append(aliases[a]).Append(' ');
                }
            }

            resolved = "MISSING";
            _missing.Add($"MISSING: ProfilerRecorder '{label}' 在候选 [ {tried.ToString().TrimEnd()} ] 下均无效" +
                         "（release Player 未暴露该计数器；未用任何替代量冒充）");
            return default;
        }

        // ------------------------------------------------------------------ 统计
        private static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted.Count == 0)
            {
                return double.NaN;
            }

            int index = (int)(fraction * sorted.Count + 0.5);
            if (index < 0)
            {
                index = 0;
            }

            if (index > sorted.Count - 1)
            {
                index = sorted.Count - 1;
            }

            return sorted[index];
        }

        private static double Mean(List<double> values)
        {
            if (values.Count == 0)
            {
                return double.NaN;
            }

            double sum = 0.0;
            for (int i = 0; i < values.Count; ++i)
            {
                sum += values[i];
            }

            return sum / values.Count;
        }

        private static List<double> SortedDoubles(List<double> source)
        {
            var copy = new List<double>(source);
            copy.Sort();
            return copy;
        }

        private static List<double> SortedLongs(List<long> source)
        {
            var copy = new List<double>(source.Count);
            for (int i = 0; i < source.Count; ++i)
            {
                copy.Add(source[i]);
            }

            copy.Sort();
            return copy;
        }

        private struct Stat
        {
            public int Count;
            public double P50;
            public double P95;
            public double P99;
            public double Mean;
            public double Max;
            public double Min;
        }

        private static Stat Compute(List<double> sorted, double mean)
        {
            var s = new Stat { Count = sorted.Count, Mean = mean };
            if (sorted.Count == 0)
            {
                return s;
            }

            s.P50 = Percentile(sorted, 0.50);
            s.P95 = Percentile(sorted, 0.95);
            s.P99 = Percentile(sorted, 0.99);
            s.Max = sorted[sorted.Count - 1];
            s.Min = sorted[0];
            return s;
        }

        // ------------------------------------------------------------------ 收尾
        private void Finish(string status, string abortReason)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            DisposeRecorders();

            // cost 口径：采样一结束就把 vsync / 帧率上限还原（进程马上退出，但状态不能留在被改过的样子）
            if (CostMode)
            {
                QualitySettings.vSyncCount = _vsyncOriginal;
                Application.targetFrameRate = _targetFrameRateOriginal;
                Debug.Log($"[FrameBaseline] vsync restored: vSyncCount={QualitySettings.vSyncCount} " +
                          $"targetFrameRate={Application.targetFrameRate}");
            }

            int exitCode;
            try
            {
                string outFile = ResolveOutFile();
                WriteJson(outFile, status, abortReason);
                Debug.Log($"[FrameBaseline] JSON written: {outFile}");
                LogSummary();
                exitCode = status == "ok" ? 0 : 4;
            }
            catch (Exception error)
            {
                Debug.LogError($"[FrameBaseline] FAILED to write JSON: {error.GetType().Name}: {error.Message}");
                exitCode = 3;
            }

            Application.Quit(exitCode);
        }

        private void DisposeRecorders()
        {
            if (_recSetPass.Valid)
            {
                _recSetPass.Dispose();
            }

            if (_recBatches.Valid)
            {
                _recBatches.Dispose();
            }

            if (_recTriangles.Valid)
            {
                _recTriangles.Dispose();
            }

            if (_recVertices.Valid)
            {
                _recVertices.Dispose();
            }

            if (_recGc.Valid)
            {
                _recGc.Dispose();
            }
        }

        private void LogSummary()
        {
            List<double> main = SortedDoubles(_cpuMainThread);
            Debug.Log($"[FrameBaseline] SUMMARY cpuMain p50={Percentile(main, 0.50):F3}ms p95={Percentile(main, 0.95):F3}ms " +
                      $"p99={Percentile(main, 0.99):F3}ms mean={Mean(_cpuMainThread):F3}ms n={_cpuMainThread.Count} " +
                      $"setPass={Percentile(SortedLongs(_setPassCalls), 0.50):F0} batches={Percentile(SortedLongs(_batches), 0.50):F0} " +
                      $"tris={Percentile(SortedLongs(_triangles), 0.50):F0} verts={Percentile(SortedLongs(_vertices), 0.50):F0}");
        }

        private string ResolveOutFile()
        {
            // Build/Windows64/ZFrame_Data → Build/Windows64 → Build → 仓库根
            string buildDir = Path.GetDirectoryName(Application.dataPath);
            string buildRoot = buildDir == null ? null : Path.GetDirectoryName(buildDir);
            string repoRoot = buildRoot == null ? null : Path.GetDirectoryName(buildRoot);
            if (string.IsNullOrEmpty(repoRoot))
            {
                throw new InvalidOperationException($"cannot locate repo root from Application.dataPath='{Application.dataPath}'");
            }

            if (!Directory.Exists(Path.Combine(repoRoot, "UnityProject")) || !Directory.Exists(Path.Combine(repoRoot, "Tools", "Probes")))
            {
                throw new InvalidOperationException($"repo root assertion failed: '{repoRoot}' lacks UnityProject/ or Tools/Probes/");
            }

            string outRoot = Path.GetFullPath(Path.Combine(repoRoot, "Tools", "Probes", "out")) + Path.DirectorySeparatorChar;
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture);
            string fileName = "frame-baseline-" + stamp + ".json";

            // 文件名白名单：仅字母/数字/_/- 与单点扩展名；天然排除分隔符与 ".."
            if (!IsWhitelistedFileName(fileName))
            {
                throw new InvalidOperationException($"output file name rejected by whitelist: {fileName}");
            }

            string outFile = Path.GetFullPath(outRoot + fileName);
            if (!outFile.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(outFile), fileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"output path escapes probe out dir: {outFile}");
            }

            Directory.CreateDirectory(outRoot);
            for (int attempt = 2; File.Exists(outFile) && attempt < 100; ++attempt)
            {
                string alt = "frame-baseline-" + stamp + "-r" + attempt + ".json";
                if (!IsWhitelistedFileName(alt))
                {
                    throw new InvalidOperationException($"output file name rejected by whitelist: {alt}");
                }

                string altPath = Path.GetFullPath(outRoot + alt);
                if (!altPath.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"output path escapes probe out dir: {altPath}");
                }

                outFile = altPath;
            }

            return outFile;
        }

        private static bool IsWhitelistedFileName(string name)
        {
            int dots = 0;
            for (int i = 0; i < name.Length; ++i)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                if (!ok)
                {
                    return false;
                }

                if (c == '.')
                {
                    ++dots;
                }
            }

            return dots == 1 && name[name.Length - 1] != '.' && name[0] != '.';
        }

        // ------------------------------------------------------------------ JSON
        private void WriteJson(string outFile, string status, string abortReason)
        {
            var sb = new StringBuilder(16384);
            int qualityIndex = QualitySettings.GetQualityLevel();
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            string urpVersion = UrpPackageVersion();
            double? renderScale = RenderScaleOf(pipeline);
            Resolution current = Screen.currentResolution;

            sb.Append("{\n");
            KeyStr(sb, "probe", "frame-baseline", 1);
            KeyStr(sb, "mode", CostMode ? "cost" : "asShipped", 1);
            KeyStr(sb, "runtime", "player", 1);
            KeyStr(sb, "status", status, 1);
            KeyStr(sb, "generatedAtUtc", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), 1);
            KeyStr(sb, "unityVersion", Application.unityVersion, 1);
            KeyStr(sb, "productVersion", Application.version, 1);

            sb.Append("  \"renderPipeline\": {\n");
            KeyStr(sb, "asset", pipeline != null ? pipeline.name : null, 2,
                "MISSING: GraphicsSettings.currentRenderPipeline 为 null");
            KeyStr(sb, "type", pipeline != null ? pipeline.GetType().FullName : null, 2);
            KeyStr(sb, "urpPackageVersion", urpVersion, 2,
                "MISSING: 运行期读不到 URP 包版本（程序集 InformationalVersion 缺失、FileVersion=0.0.0.0）；口径以仓库 UnityProject/Packages/manifest.json 为准");
            KeyBool(sb, "zRenderActive", ZRenderBoot.IsActive, 2);
            sb.Append("  },\n");

            KeyStr(sb, "colorSpace", QualitySettings.activeColorSpace.ToString(), 1);

            sb.Append("  \"qualityLevel\": {\n");
            KeyInt(sb, "index", qualityIndex, 2);
            KeyStr(sb, "name", QualityName(qualityIndex), 2);
            KeyInt(sb, "vSyncCount", QualitySettings.vSyncCount, 2);
            KeyInt(sb, "targetFrameRate", Application.targetFrameRate, 2);
            KeyInt(sb, "antiAliasing", QualitySettings.antiAliasing, 2);
            KeyNum(sb, "renderScale", renderScale, 2, "MISSING: 活动管线资产无 renderScale 属性");
            sb.Append("  },\n");

            // vsync 两侧留痕：original = 质量档/工程给的值（玩家实际体验），sampling = 采样期实际生效值
            sb.Append("  \"vsync\": {\n");
            sb.Append("    \"original\": {\n");
            KeyInt(sb, "vSyncCount", _vsyncOriginal, 3);
            KeyInt(sb, "targetFrameRate", _targetFrameRateOriginal, 3);
            sb.Append("    },\n");
            sb.Append("    \"sampling\": {\n");
            KeyInt(sb, "vSyncCount", _vsyncSampling, 3);
            KeyInt(sb, "targetFrameRate", _targetFrameRateSampling, 3);
            sb.Append("    },\n");
            KeyBool(sb, "restoredAfterSampling", CostMode, 2);
            sb.Append("  },\n");

            sb.Append("  \"focus\": {\n");
            KeyBool(sb, "atSamplingStart", _focusAtSamplingStart, 2);
            KeyInt(sb, "lostFocusFrames", _focusLostFrames, 2);
            sb.Append("  },\n");

            sb.Append("  \"gpu\": {\n");
            KeyStr(sb, "name", SystemInfo.graphicsDeviceName, 2);
            KeyStr(sb, "vendor", SystemInfo.graphicsDeviceVendor, 2);
            KeyStr(sb, "apiType", SystemInfo.graphicsDeviceType.ToString(), 2);
            KeyStr(sb, "apiVersion", SystemInfo.graphicsDeviceVersion, 2);
            KeyInt(sb, "memoryMB", SystemInfo.graphicsMemorySize, 2);
            KeyStr(sb, "processor", SystemInfo.processorType, 2);
            KeyInt(sb, "processorCount", SystemInfo.processorCount, 2);
            KeyInt(sb, "systemMemoryMB", SystemInfo.systemMemorySize, 2);
            sb.Append("  },\n");
            KeyStr(sb, "driver", null, 1,
                "MISSING: Unity SystemInfo 不暴露显卡驱动版本（graphicsDeviceVersion 只给 API 级别/feature level）；不用宿主侧 WMI 值冒充播放器内取数");

            sb.Append("  \"resolution\": {\n");
            KeyStr(sb, "expected", "1920x1080 windowed", 2);
            KeyInt(sb, "screenWidth", Screen.width, 2);
            KeyInt(sb, "screenHeight", Screen.height, 2);
            KeyStr(sb, "fullScreenMode", Screen.fullScreenMode.ToString(), 2);
            KeyStr(sb, "monitorResolution", current.width + "x" + current.height, 2);
            KeyNum(sb, "monitorRefreshHz", current.refreshRateRatio.value, 2);
            if (_gameCamera != null)
            {
                KeyInt(sb, "cameraPixelWidth", _gameCamera.pixelWidth, 2);
                KeyInt(sb, "cameraPixelHeight", _gameCamera.pixelHeight, 2);
            }
            else
            {
                // 取不到就 null + MISSING，不填 0
                KeyStr(sb, "cameraPixelWidth", null, 2, "MISSING: 场景内未找到相机");
                KeyStr(sb, "cameraPixelHeight", null, 2, "MISSING: 场景内未找到相机");
            }
            sb.Append("  },\n");

            sb.Append("  \"scene\": {\n");
            KeyStr(sb, "name", SceneManager.GetActiveScene().name, 2);
            KeyStr(sb, "path", SceneManager.GetActiveScene().path, 2);
            KeyInt(sb, "loadedSceneCount", SceneManager.sceneCount, 2);
            sb.Append("  },\n");

            KeyInt(sb, "warmup", WarmupFrames, 1);
            KeyInt(sb, "samples", SampleFrames, 1);
            KeyInt(sb, "sampleFramesObserved", _phaseFrames, 1);

            sb.Append("  \"frameTiming\": {\n");
            KeyULong(sb, "gpuTimerFrequency", FrameTimingManager.GetGpuTimerFrequency(), 2);
            KeyULong(sb, "cpuTimerFrequency", FrameTimingManager.GetCpuTimerFrequency(), 2);
            KeyInt(sb, "invalidCpuFrameTime", _invalidCpuFrame, 2);
            KeyInt(sb, "invalidCpuMainThreadFrameTime", _invalidCpuMain, 2);
            KeyInt(sb, "invalidCpuRenderThreadFrameTime", _invalidCpuRender, 2);
            KeyInt(sb, "invalidGpuFrameTime", _invalidGpu, 2);
            KeyInt(sb, "zeroedSetPassSamples", _zeroSetPass, 2);
            KeyInt(sb, "zeroedBatchesSamples", _zeroBatches, 2);
            KeyInt(sb, "zeroedTrianglesSamples", _zeroTriangles, 2);
            KeyInt(sb, "zeroedVerticesSamples", _zeroVertices, 2);
            KeyInt(sb, "zeroedGcSamples", _zeroGc, 2);
            sb.Append("  },\n");

            sb.Append("  \"statistics\": {\n");
            StatsBlockDouble(sb, "cpuFrameTime", "ms", _cpuFrameTime, true);
            StatsBlockDouble(sb, "cpuMainThreadFrameTime", "ms", _cpuMainThread, true);
            StatsBlockDouble(sb, "cpuRenderThreadFrameTime", "ms", _cpuRenderThread, true);
            StatsBlockDouble(sb, "gpuFrameTime", "ms", _gpuFrameTime, true);
            StatsBlockLong(sb, "setPassCalls", "count", _setPassCalls, true);
            StatsBlockLong(sb, "batches", "count", _batches, true);
            StatsBlockLong(sb, "triangles", "count", _triangles, true);
            StatsBlockLong(sb, "vertices", "count", _vertices, true);
            StatsBlockLong(sb, "gcAllocatedInFrame", "bytes", _gcAllocated, false);
            sb.Append("  },\n");

            var notes = new List<string>(_notes)
            {
                "percentileMethod: nearest-rank，sorted[clamp((int)(f*n+0.5),0,n-1)]（与 Tools/Probes 既有探针同式）",
                $"quality: index={qualityIndex}（{QualityName(qualityIndex)}）原样未改；未降档、未关后处理、未改 renderScale",
                "mode: asShipped 用于\"玩家实际体验\"，cost 用于\"回归对比\"——cost 采样期 vSyncCount=0 / targetFrameRate=-1，采样后恢复原值",
                $"vsync: original vSyncCount={_vsyncOriginal} / targetFrameRate={_targetFrameRateOriginal}；" +
                $"sampling vSyncCount={_vsyncSampling} / targetFrameRate={_targetFrameRateSampling}" +
                "（vsync>0 时 p50 被刷新间隔钳制，只有 cpuMainThread/cpuRenderThread/gpuFrameTime 能反映成本）",
                $"display: {current.width}x{current.height}@{current.refreshRateRatio.value:0.##}Hz；窗口 {Screen.width}x{Screen.height}",
                "playerSettings.enableFrameTimingStats 置 1（FrameTimingManager 前置开关；测量设置，非画质改动）",
                "采样期无玩家输入（探针不改相机、不注入输入）；画面为 SCN_Game 出生点静止状态",
                "render 计数器读到 0（recorder 刚启动/该帧未产出统计）不算测量值，已剔除；剔除条数见 frameTiming.zeroed*Samples。" +
                "gcAllocatedInFrame 不剔除 0（0 字节是真实值）",
                "recorders: " + RecorderSummary(),
            };

            if (abortReason != null)
            {
                notes.Add("abortReason: " + abortReason);
            }

            _missing.Add("MISSING: 播放器内无电源/性能模式/GPU 占用查询 API（本章节只覆盖 vsync 口径与窗口焦点；" +
                         "插电状态、性能模式、后台 GPU 占用见 Docs/Planning/帧耗基线-2026-11-15.md 的宿主侧查证）");

            AppendStringArray(sb, "notes", notes);
            AppendStringArray(sb, "missing", _missing);
            sb.Append("}\n");

            // 各键统一以 ",\n" 结尾，收尾时去掉块尾多余逗号（严格 JSON 不接受尾逗号）
            string json = Regex.Replace(sb.ToString(), ",\n([ \t]*[}\\]])", "\n$1");
            File.WriteAllText(outFile, json, new UTF8Encoding(false));
        }

        private string RecorderSummary()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in _recorderState)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }

                sb.Append(pair.Key).Append('=').Append(pair.Value);
            }

            return sb.ToString();
        }

        private void StatsBlockDouble(StringBuilder sb, string name, string unit, List<double> values, bool trailingComma)
        {
            Stat s = Compute(SortedDoubles(values), Mean(values));
            sb.Append("    \"").Append(name).Append("\": {\n");
            KeyStr(sb, "unit", unit, 3);
            if (s.Count == 0)
            {
                KeyNum(sb, "p50", (double?)null, 3);
                KeyNum(sb, "p95", (double?)null, 3);
                KeyNum(sb, "p99", (double?)null, 3);
                KeyNum(sb, "mean", (double?)null, 3);
                KeyNum(sb, "max", (double?)null, 3);
                KeyNum(sb, "min", (double?)null, 3);
                KeyInt(sb, "samples", 0, 3, "MISSING: 该口径 0 条有效样本（见 frameTiming.invalid* 与 notes）");
                _missing.Add($"MISSING: statistics.{name} 无有效样本");
            }
            else
            {
                KeyNum(sb, "p50", s.P50, 3);
                KeyNum(sb, "p95", s.P95, 3);
                KeyNum(sb, "p99", s.P99, 3);
                KeyNum(sb, "mean", s.Mean, 3);
                KeyNum(sb, "max", s.Max, 3);
                KeyNum(sb, "min", s.Min, 3);
                KeyInt(sb, "samples", s.Count, 3);
            }

            sb.Append(trailingComma ? "    },\n" : "    }\n");
        }

        private void StatsBlockLong(StringBuilder sb, string name, string unit, List<long> values, bool trailingComma)
        {
            Stat s = Compute(SortedLongs(values), MeanLong(values));
            sb.Append("    \"").Append(name).Append("\": {\n");
            KeyStr(sb, "unit", unit, 3);
            if (s.Count == 0)
            {
                KeyNum(sb, "p50", (double?)null, 3);
                KeyNum(sb, "p95", (double?)null, 3);
                KeyNum(sb, "p99", (double?)null, 3);
                KeyNum(sb, "mean", (double?)null, 3);
                KeyNum(sb, "max", (double?)null, 3);
                KeyNum(sb, "min", (double?)null, 3);
                KeyInt(sb, "samples", 0, 3, "MISSING: 该口径 0 条有效样本（recorder 无效时见 missing[]）");
                _missing.Add($"MISSING: statistics.{name} 无有效样本");
            }
            else
            {
                KeyNum(sb, "p50", s.P50, 3);
                KeyNum(sb, "p95", s.P95, 3);
                KeyNum(sb, "p99", s.P99, 3);
                KeyNum(sb, "mean", s.Mean, 3);
                KeyNum(sb, "max", s.Max, 3);
                KeyNum(sb, "min", s.Min, 3);
                KeyInt(sb, "samples", s.Count, 3);
            }

            sb.Append(trailingComma ? "    },\n" : "    }\n");
        }

        private static double MeanLong(List<long> values)
        {
            if (values.Count == 0)
            {
                return double.NaN;
            }

            double sum = 0.0;
            for (int i = 0; i < values.Count; ++i)
            {
                sum += values[i];
            }

            return sum / values.Count;
        }

        private static string QualityName(int index)
        {
            string[] names = QualitySettings.names;
            return index >= 0 && index < names.Length ? names[index] : "MISSING";
        }

        private static string UrpPackageVersion()
        {
            try
            {
                Type urp = Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipeline, Unity.RenderPipelines.Universal.Runtime");
                if (urp == null)
                {
                    return null;
                }

                object[] attributes = urp.Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attributes.Length > 0)
                {
                    return ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
                }

                // 包程序集一般不写 InformationalVersion；退到文件版本（真实读盘，不是常量）。
                // 2022.3 的 URP 程序集 FileVersion 是 0.0.0.0（未写入）——这时按"取不到"处理。
                string location = urp.Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    string fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(location).FileVersion;
                    if (!string.IsNullOrEmpty(fileVersion) && fileVersion != "0.0.0.0")
                    {
                        return fileVersion;
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double? RenderScaleOf(RenderPipelineAsset asset)
        {
            if (asset == null)
            {
                return null;
            }

            try
            {
                PropertyInfo property = asset.GetType().GetProperty("renderScale", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.PropertyType != typeof(float))
                {
                    return null;
                }

                return (float)property.GetValue(asset);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- 极简 JSON 写法（零第三方依赖；字符串转义按 RFC 8259）----
        private static void KeyStr(StringBuilder sb, string name, string value, int indent, string missingReason = null)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ");
            if (value == null)
            {
                sb.Append("null");
                if (missingReason != null)
                {
                    sb.Append(", \"").Append(name).Append("Missing\": ");
                    StringValue(sb, missingReason);
                }
            }
            else
            {
                StringValue(sb, value);
            }

            sb.Append(",\n");
        }

        private static void KeyNum(StringBuilder sb, string name, double value, int indent, string missingReason = null)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ");
            sb.Append(double.IsNaN(value) || double.IsInfinity(value)
                ? "null"
                : value.ToString("0.####", CultureInfo.InvariantCulture));
            if (missingReason != null)
            {
                sb.Append(", \"").Append(name).Append("Missing\": ");
                StringValue(sb, missingReason);
            }

            sb.Append(",\n");
        }

        private static void KeyNum(StringBuilder sb, string name, double? value, int indent, string missingReason = null)
        {
            if (!value.HasValue)
            {
                Indent(sb, indent).Append('"').Append(name).Append("\": null");
                if (missingReason != null)
                {
                    sb.Append(", \"").Append(name).Append("Missing\": ");
                    StringValue(sb, missingReason);
                }

                sb.Append(",\n");
                return;
            }

            KeyNum(sb, name, value.Value, indent);
        }

        private static void KeyInt(StringBuilder sb, string name, long value, int indent, string missingReason = null)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ")
                .Append(value.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\n");
        }

        private static void KeyULong(StringBuilder sb, string name, ulong value, int indent, string missingReason = null)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ")
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        }

        private static void KeyBool(StringBuilder sb, string name, bool value, int indent, string missingReason = null)
        {
            Indent(sb, indent).Append('"').Append(name).Append("\": ")
                .Append(value ? "true" : "false").Append(",\n");
        }

        private static StringBuilder Indent(StringBuilder sb, int indent)
        {
            return sb.Append(' ', indent * 2);
        }

        private static void StringValue(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
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

        private static void AppendStringArray(StringBuilder sb, string name, List<string> values)
        {
            sb.Append("  \"").Append(name).Append("\": [");
            if (values.Count == 0)
            {
                sb.Append("],\n");
                return;
            }

            sb.Append('\n');
            for (int i = 0; i < values.Count; ++i)
            {
                sb.Append("    ");
                StringValue(sb, values[i]);
                sb.Append(i == values.Count - 1 ? "\n" : ",\n");
            }

            sb.Append("  ],\n");
        }
    }
}
