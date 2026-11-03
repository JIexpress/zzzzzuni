using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using ZFrame.View;
using ZFrame.View.DevConsole;
using Debug = UnityEngine.Debug;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// 运行期 DevConsole 的可证伪护栏（TASK-A-DEV-03 建立，TASK-A-DEV-04 硬化）。
    ///
    /// 重点不是"命令能跑"，而是四件容易假成功的事：
    /// 1) 门控：非开发包必须不监听（连接被拒）且不起后台线程；
    /// 2) 只绑 127.0.0.1；
    /// 3) 命令的效果必须**回读**（QualitySettings / ParticleRuntime / 真实 PNG 像素）；
    /// 4) 坏输入不崩、限流是结构化错误，且空转开销可量化。
    ///
    /// TASK-A-DEV-04 修掉的测试脆弱性（见 Docs/Logs/TASK-A-DEV-04.md H3）：
    /// ① 端口不再"先 bind :0 再释放"（TOCTOU），改成**固定port range + 真启动失败就换下一个**；
    /// ② 连接结果区分 Connected / Refused / Timeout / Error（慢机器不许把超时当被拒 → 假绿）；
    /// ③ 100 次连接仍然串行，每次都是"连上即断"，不依赖 ThreadPool 并发；
    /// ④ 删掉 `Process.Threads.Count` 断言（Mono 下恒 0，是假断言），真判据用 ListenThreads；
    /// ⑤ shot 夹具加着色器 fallback，确实拿不到（换管线）时 Ignore 而不是红。
    ///
    /// 所有网络往返都放在后台线程：主线程一旦阻塞在读 socket 上，泵就停了，
    /// 这个测试会自己把自己锁死。
    /// </summary>
    public sealed class RuntimeDevConsolePlayModeTests
    {
        /// <summary>固定端口段的起点：真绑定成功才用，绑不上就顺延（无 TOCTOU 窗口）。</summary>
        private const int PortBase = 17620;
        private const int PortAttempts = 40;

        private readonly List<GameObject> _tempObjects = new List<GameObject>();
        private readonly List<Camera> _disabledCameras = new List<Camera>();
        private readonly List<Material> _tempMaterials = new List<Material>();

        [SetUp]
        public void SetUp()
        {
            RuntimeDevConsole.Stop();
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeDevConsole.Stop();

            for (int i = 0; i < _disabledCameras.Count; i++)
            {
                if (_disabledCameras[i] != null) _disabledCameras[i].enabled = true;
            }

            _disabledCameras.Clear();

            for (int i = 0; i < _tempObjects.Count; i++)
            {
                if (_tempObjects[i] != null) UnityEngine.Object.Destroy(_tempObjects[i]);
            }

            _tempObjects.Clear();

            for (int i = 0; i < _tempMaterials.Count; i++)
            {
                if (_tempMaterials[i] != null) UnityEngine.Object.Destroy(_tempMaterials[i]);
            }

            _tempMaterials.Clear();
        }

        // ---------------------------------------------------------------- 门控

        [Test]
        public void Gate_DevelopmentBuild_Enables_NonDevelopmentBuild_Disables()
        {
            Assert.IsTrue(
                RuntimeDevConsoleGate.IsEnabled(() => true, new string[0], _ => null),
                "development build (isDebugBuild=true) must be enabled");

            Assert.IsFalse(
                RuntimeDevConsoleGate.IsEnabled(() => false, new string[0], _ => null),
                "non-development build must be disabled: first gate for a silent release package");

            Assert.IsFalse(
                RuntimeDevConsoleGate.IsEnabled(null, new string[0], _ => null),
                "null probe must fail closed");
        }

        [Test]
        public void Gate_ExplicitDisable_Wins_OverDevelopmentBuild()
        {
            Assert.IsTrue(RuntimeDevConsoleGate.IsExplicitlyDisabled(new[] { "-zbf-noconsole" }, _ => null),
                "CLI -zbf-noconsole must be recognized");
            Assert.IsFalse(
                RuntimeDevConsoleGate.IsEnabled(() => true, new[] { "-zbf-noconsole" }, _ => null),
                "explicit disable must win over the development build");

            Assert.IsFalse(
                RuntimeDevConsoleGate.IsEnabled(() => true, new string[0], name =>
                    name == RuntimeDevConsoleGate.EnvDisable ? "0" : null),
                "env ZBF_RT_CONSOLE=0 must win over the development build");

            Assert.IsFalse(
                RuntimeDevConsoleGate.IsEnabled(() => true, new string[0], name =>
                    name == RuntimeDevConsoleGate.EnvDisable ? "off" : null),
                "env ZBF_RT_CONSOLE=off must win over the development build");

            Assert.IsTrue(
                RuntimeDevConsoleGate.IsEnabled(() => true, new string[0], name =>
                    name == RuntimeDevConsoleGate.EnvDisable ? "1" : null),
                "ZBF_RT_CONSOLE=1 is not a disable value; must not block a dev build");

            Assert.AreEqual(17322, RuntimeDevConsoleGate.ResolvePort(new string[0], _ => null),
                "default port must be 17322");
            Assert.AreEqual(18001, RuntimeDevConsoleGate.ResolvePort(new[] { "-zbf-port", "18001" }, _ => null));
            Assert.AreEqual(18002, RuntimeDevConsoleGate.ResolvePort(new string[0], name =>
                name == RuntimeDevConsoleGate.EnvPort ? "18002" : null));
        }

        [UnityTest]
        public IEnumerator Gate_NonDevelopmentBuild_DoesNotListen_AndStartsNoThread()
        {
            yield return null;

            int port = 0;

            yield return FindQuietPort(p => port = p);
            int listenThreadsBefore = RuntimeDevConsole.ListenThreads;

            string error;
            bool started = RuntimeDevConsole.TryEnableForTests(() => false, port, out error);

            Assert.IsFalse(started, "non-development branch started the server");
            Assert.IsFalse(RuntimeDevConsole.IsRunning, "IsRunning must be false on the non-development branch");
            Assert.IsNull(RuntimeDevConsole.BoundEndPoint, "no listening endpoint on the non-development branch");
            Assert.AreEqual(0, RuntimeDevConsole.ListenThreads, "no listen threads on the non-development branch");

            StringAssert.Contains("gate", error, "rejection must come from the gate");

            // H3④：`Process.Threads.Count` after Unity 的 Mono 上恒为 0，是**假断言**，
            // 已删除；这里用 ListenThreads（accept 线程的原子计数）作为真判据。
            Assert.AreEqual(listenThreadsBefore, RuntimeDevConsole.ListenThreads,
                "listen thread count changed on the non-development branch");

            ConnectOutcome outcome = default;
            string connectDetail = null;
            yield return ConnectProbe(port, (o, d) => { outcome = o; connectDetail = d; });

            // H3②：必须区分"被拒"与"超时"。被拒是门控生效的证据；超时只能说明探测不可信，
            // 不能当作"没监听"（否则慢机器上会把一个真在监听的进程读成绿）。
            Assert.AreEqual(ConnectOutcome.Refused, outcome,
                "server says it did not start, but port " + port + " returned " + outcome + "（" + connectDetail + " ) -- "
                + "only ConnectionRefused proves the gate really is silent");

            Debug.Log($"[ZBF-RT] gate non-dev: started={started} listenThreads={RuntimeDevConsole.ListenThreads} "
                      + $"connect={outcome} error='{error}'");
        }

        [UnityTest]
        public IEnumerator Gate_DevelopmentBuild_Listens_AndAnswersHelp()
        {
            int port = StartConsole();
            yield return null;

            string response = null;
            yield return Send(port, "help", r => response = r);

            StringAssert.Contains("\"ok\":true", response);
            StringAssert.Contains("\"commands\"", response);
            Assert.AreEqual(1, RuntimeDevConsole.ListenThreads, "development branch must have exactly 1 listen thread");

            RuntimeDevConsole.Stop();
            yield return null;
            Assert.AreEqual(0, RuntimeDevConsole.ListenThreads, "listen threads must drop to 0 after Stop");

            Debug.Log("[ZBF-RT] dev gate help: " + response);
        }

        // ---------------------------------------------------------------- 绑定

        [UnityTest]
        public IEnumerator Listener_BindsLoopbackOnly_NeverWildcard()
        {
            int port = StartConsole();
            yield return null;

            IPEndPoint endPoint = RuntimeDevConsole.BoundEndPoint;
            Assert.IsNotNull(endPoint, "bound endpoint must be reported after start");
            Assert.AreEqual(IPAddress.Loopback, endPoint.Address, "listener must bind 127.0.0.1");
            Assert.AreNotEqual(IPAddress.Any, endPoint.Address, "must never bind 0.0.0.0");
            Assert.AreNotEqual(IPAddress.IPv6Any, endPoint.Address, "must never bind [::]");
            Assert.AreEqual(port, endPoint.Port);

            Debug.Log($"[ZBF-RT] bound endpoint = {endPoint}");
        }

        // ---------------------------------------------------------------- 命令

        [UnityTest]
        public IEnumerator HelpAndDump_RespondOneLineJson()
        {
            int port = StartConsole();
            yield return null;

            string help = null;
            yield return Send(port, "help", r => help = r);
            foreach (string command in RuntimeDevConsoleTestAccess.CommandNames())
            {
                StringAssert.Contains("\"" + command + "\"", help, "help is missing command " + command);
            }

            string dump = null;
            yield return Send(port, "dump", r => dump = r);
            StringAssert.Contains("\"ok\":true", dump);
            StringAssert.Contains("\"fps\"", dump);
            StringAssert.Contains("\"quality\"", dump);
            StringAssert.Contains("\"fgOn\"", dump);
            StringAssert.Contains("\"scene\"", dump);
            StringAssert.Contains("\"particleCount\"", dump);
            StringAssert.Contains("\"memoryMB\"", dump);
            Assert.AreEqual(1, CountLines(dump), "response must be a single JSON line");

            Debug.Log("[ZBF-RT] dump: " + dump);
        }

        [UnityTest]
        public IEnumerator SparkCommand_RaisesParticleCount()
        {
            int port = StartConsole();
            yield return null;

            ParticleRuntime.SetEnabled(true);
            int before = ParticleRuntime.HitSparkParticleCount;

            string response = null;
            yield return Send(port, "spark 0 1 0 20", r => response = r);
            StringAssert.Contains("\"ok\":true", response);
            StringAssert.Contains("\"emitted\":20", response);

            yield return null;
            yield return null;

            int after = ParticleRuntime.HitSparkParticleCount;
            Assert.Greater(after, before, $"spark command produced no particles: {before} -> {after}");
            Debug.Log($"[ZBF-RT] spark: hitSparkParticleCount {before} -> {after}; response={response}");
        }

        [UnityTest]
        public IEnumerator QualityCommand_ChangesQualitySettings_ReadBack()
        {
            int port = StartConsole();
            yield return null;

            int original = QualitySettings.GetQualityLevel();
            string[] names = QualitySettings.names;
            int low = IndexOfLevel(names, "Low", 0);
            int high = IndexOfLevel(names, "High", names.Length - 1);

            try
            {
                string lowResponse = null;
                yield return Send(port, "quality low", r => lowResponse = r);
                StringAssert.Contains("\"ok\":true", lowResponse);
                Assert.AreEqual(low, QualitySettings.GetQualityLevel(), "quality low did not reach QualitySettings");

                string highResponse = null;
                yield return Send(port, "quality high", r => highResponse = r);
                StringAssert.Contains("\"ok\":true", highResponse);
                Assert.AreEqual(high, QualitySettings.GetQualityLevel(),
                    "quality high did not change QualitySettings.GetQualityLevel()");

                Debug.Log($"[ZBF-RT] quality: low -> {low}, high -> {QualitySettings.GetQualityLevel()} (names=[{string.Join(",", names)}])");
            }
            finally
            {
                QualitySettings.SetQualityLevel(original, true);
            }
        }

        [UnityTest]
        public IEnumerator BadInput_UnknownOversizePartialAndFlood_StillServes()
        {
            int port = StartConsole();
            yield return null;

            string unknown = null;
            yield return Send(port, "definitely-not-a-command", r => unknown = r);
            StringAssert.Contains("\"ok\":false", unknown);
            StringAssert.Contains("unknown command", unknown);

            string oversize = null;
            yield return Send(port, "help " + new string('x', 5000), r => oversize = r);
            StringAssert.Contains("\"ok\":false", oversize);
            StringAssert.Contains("exceeds", oversize);

            // 半截数据：发一半就断开，服务端只许静默关闭。
            yield return SendRawNoResponse(port, "dum");

            // 连续 100 次连接（不发命令就断）。**串行**：每次都是"连上即断"，
            // 不依赖 ThreadPool 的并发调度（H3③；并发上限由下面 H2 的用例单独覆盖）。
            for (int i = 0; i < 100; i++)
            {
                yield return SendRawNoResponse(port, string.Empty);
            }

            string alive = null;
            yield return Send(port, "help", r => alive = r);
            StringAssert.Contains("\"ok\":true", alive);

            Debug.Log("[ZBF-RT] bad input survived: unknown='" + unknown + "' oversize='" + oversize + "' then help ok");
        }

        [UnityTest]
        public IEnumerator ShotCommand_WritesNonUniformPng()
        {
            int port = StartConsole();

            BuildShotRig();
            yield return null;
            yield return null;
            yield return WaitSeconds(0.25f); // shot 有 200ms 最短间隔（H2③）

            string response = null;
            yield return Send(port, "shot zbf-runtime-shot", r => response = r);
            StringAssert.Contains("\"ok\":true", response);

            string path = ExtractString(response, "path");
            Assert.IsFalse(string.IsNullOrEmpty(path), "shot response has no path: " + response);
            Assert.IsTrue(File.Exists(path), "shot reported success but the file is missing: " + path + " response=" + response);

            var info = new FileInfo(path);
            Assert.Greater(info.Length, 1000, "PNG too small to be a real image: " + info.Length + " bytes");

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Greater(bytes.Length, 1000);
            Assert.AreEqual(0x89, bytes[0], "not a PNG header");

            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                Assert.IsTrue(texture.LoadImage(bytes), "PNG cannot be loaded as Texture2D");
                Assert.Greater(texture.width, 63);
                Assert.Greater(texture.height, 63);

                Color32 first = default;
                bool uniform = true;
                Color32[] pixels = texture.GetPixels32();
                first = pixels[0];
                for (int i = 0; i < pixels.Length; i += 37)
                {
                    if (pixels[i].r != first.r || pixels[i].g != first.g || pixels[i].b != first.b)
                    {
                        uniform = false;
                        break;
                    }
                }

                // H3⑤：换到 SRP（URP）后"相机离屏渲染"可能出纯色——这是**前提不满足**，
                // 不是 DevConsole 的缺陷，所以跳过而不是红。内置管线（本项目的目标）仍然严格断言。
                if (uniform && GraphicsSettings.currentRenderPipeline != null)
                {
                    Assert.Ignore("render pipeline is SRP (" + GraphicsSettings.currentRenderPipeline.GetType().Name
                        + "); offscreen camera rendering is uniform, so the non-uniform premise does not hold. Skipped");
                }

                Assert.IsFalse(uniform, "shot is a uniform image (typical black frame): " + first.r + "," + first.g + "," + first.b);
                Debug.Log($"[ZBF-RT] shot: {path} {texture.width}x{texture.height} {bytes.Length} bytes " +
                          $"firstPixel=({first.r},{first.g},{first.b}) uniform={uniform}");
            }
            finally
            {
                UnityEngine.Object.Destroy(texture);
                try { File.Delete(path); }
                catch (Exception) { }
            }
        }

        [UnityTest]
        public IEnumerator FrameGeneration_WhenUnavailable_ReturnsUnsupported_NotFakeSuccess()
        {
            int port = StartConsole();
            yield return null;

            string off = null;
            yield return Send(port, "fg off", r => off = r);
            Assert.IsNotNull(off);
            Assert.IsNotEmpty(off, "fg off returned nothing");

            // 编辑器里没有可用的插帧服务（或能力不可用）→ 必须 {ok:false,error:"unsupported"}。
            // 若某天编辑器真能插帧，这条会走 ok:true 且带 verified:true——两种都必须是"诚实"的那一种。
            if (off.Contains("\"ok\":false"))
            {
                StringAssert.Contains("unsupported", off);
            }
            else
            {
                StringAssert.Contains("\"verified\":true", off);
            }

            Debug.Log("[ZBF-RT] fg off: " + off);
        }

        // ---------------------------------------------------------------- 开销

        [UnityTest]
        public IEnumerator IdlePump_OverheadAndDumpLatency_AreReported()
        {
            int port = StartConsole();

            // 空转几十帧，让泵的 idle 采样有代表性。
            for (int i = 0; i < 30; i++) yield return null;

            var stopwatch = Stopwatch.StartNew();
            string dump = null;
            yield return Send(port, "dump", r => dump = r);
            stopwatch.Stop();

            double idleAverage = ExtractNumber(dump, "idlePumpUsAverage");
            double idleLast = ExtractNumber(dump, "idlePumpUsLast");
            long latencyMs = stopwatch.ElapsedMilliseconds;

            Assert.GreaterOrEqual(idleAverage, 0.0);
            Assert.Less(idleAverage, 200.0, $"idle pump cost too high: {idleAverage:F2} µs");
            Assert.Less(latencyMs, 2000, $"dump round-trip too slow: {latencyMs} ms (includes one main-thread frame)");

            Debug.Log($"[ZBF-RT] idle pump avg={idleAverage:F3} µs last={idleLast:F3} µs; dump round-trip={latencyMs} ms");
        }

        // ---------------------------------------------------------------- H2 限流配额

        [UnityTest]
        public IEnumerator H2_ConcurrentOverLimit_RejectedWithStructuredError()
        {
            int port = StartConsole();
            yield return null;

            const int limit = RuntimeDevConsoleServer.MaxConcurrentClients;
            var release = new ManualResetEventSlim(false);
            var holders = new List<Thread>();

            for (int i = 0; i < limit; i++)
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        using (var client = new TcpClient())
                        {
                            client.Connect(IPAddress.Loopback, port);
                            release.Wait(8000);
                        }
                    }
                    catch (Exception)
                    {
                        // 连接被服务端关闭属于预期（Stop / 限流），这里不关心。
                    }
                })
                {
                    IsBackground = true,
                    Name = "ZBF-RT.Holder." + i,
                };
                thread.Start();
                holders.Add(thread);
            }

            // 等并发计数真的顶到上限（不是 sleep 猜）。
            float deadline = Time.realtimeSinceStartup + 8f;
            while (RuntimeDevConsole.ActiveClientsForTests < limit && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.AreEqual(limit, RuntimeDevConsole.ActiveClientsForTests,
                "8 clients were not all accepted; the concurrency test premise is broken");

            string rejected = null;
            yield return Send(port, "help", r => rejected = r);

            StringAssert.Contains("\"ok\":false", rejected);
            StringAssert.Contains("too many concurrent connections", rejected);
            Debug.Log("[ZBF-RT] H2 concurrency: " + rejected);

            release.Set();
            for (int i = 0; i < holders.Count; i++) holders[i].Join(3000);

            deadline = Time.realtimeSinceStartup + 5f;
            while (RuntimeDevConsole.ActiveClientsForTests > 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            string after = null;
            yield return Send(port, "help", r => after = r);
            StringAssert.Contains("\"ok\":true", after, "server must still be alive after a concurrency rejection");
        }

        [UnityTest]
        public IEnumerator H2_RequestsOverRateLimit_ReturnRateLimited()
        {
            int port = StartConsole();
            yield return null;

            const int senders = 6;
            const int perSender = 20;
            var responses = new List<string>();
            var done = new CountdownEvent(senders);

            for (int i = 0; i < senders; i++)
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        for (int k = 0; k < perSender; k++)
                        {
                            string response = SendBlocking(port, "help");
                            lock (responses) responses.Add(response);
                        }
                    }
                    catch (Exception e)
                    {
                        lock (responses) responses.Add("__exception__:" + e.GetType().Name + ":" + e.Message);
                    }
                    finally
                    {
                        done.Signal();
                    }
                })
                {
                    IsBackground = true,
                    Name = "ZBF-RT.RateSender",
                };
                thread.Start();
            }

            float deadline = Time.realtimeSinceStartup + 30f;
            while (!done.IsSet && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(done.IsSet, "rate-limit burst did not finish within 30s");

            int limited = 0;
            int ok = 0;
            int exceptions = 0;
            lock (responses)
            {
                for (int i = 0; i < responses.Count; i++)
                {
                    if (responses[i].Contains("rate limited")) limited++;
                    else if (responses[i].Contains("\"ok\":true")) ok++;
                    else if (responses[i].StartsWith("__exception__", StringComparison.Ordinal)) exceptions++;
                }
            }

            Assert.Greater(limited, 0, $"burst of {senders * perSender} requests (limit {RuntimeDevConsoleServer.MaxRequestsPerSecond}/s) produced no rate limited response");
            Assert.Greater(ok, 0, "rate limit also rejected requests inside the free budget");
            Assert.AreEqual(0, exceptions, "client exception during rate limiting (structured errors must not drop the connection)");

            // 窗口过去之后必须恢复，不能一直封着。
            yield return WaitSeconds(1.2f);
            string after = null;
            yield return Send(port, "help", r => after = r);
            StringAssert.Contains("\"ok\":true", after, "rate limit did not recover after the window");

            Debug.Log($"[ZBF-RT] H2 rate: total={responses.Count} ok={ok} limited={limited} exceptions={exceptions}");
        }

        [UnityTest]
        public IEnumerator H2_ShotQuota_RateLimitedAndFileCapEnforced()
        {
            int port = StartConsole();
            BuildShotRig();
            yield return null;
            yield return null;
            yield return WaitSeconds(0.25f);

            string first = null;
            yield return Send(port, "shot zbf-quota-1", r => first = r);
            StringAssert.Contains("\"ok\":true", first);

            // ① 200ms 最短间隔：紧接着的第二发必须被拒，且错误是结构化的。
            string second = null;
            yield return Send(port, "shot zbf-quota-2", r => second = r);
            StringAssert.Contains("\"ok\":false", second);
            StringAssert.Contains("rate limited", second);

            // ② 目录文件数上限：把目录填到 200 个后，再发必须报 quota。
            string directory = Path.Combine(Application.persistentDataPath, "DevConsole", "screenshots");
            Directory.CreateDirectory(directory);

            var created = new List<string>();
            int before = CountPng(directory);
            try
            {
                for (int i = before; i < 200; i++)
                {
                    string dummy = Path.Combine(directory, "zbf-quota-dummy-" + i + ".png");
                    File.WriteAllBytes(dummy, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                    created.Add(dummy);
                }

                yield return WaitSeconds(0.25f);

                string third = null;
                yield return Send(port, "shot zbf-quota-3", r => third = r);
                StringAssert.Contains("\"ok\":false", third);
                StringAssert.Contains("quota", third);
                Debug.Log($"[ZBF-RT] H2 shot quota: files={CountPng(directory)} second='{second}' third='{third}'");
            }
            finally
            {
                for (int i = 0; i < created.Count; i++)
                {
                    try { File.Delete(created[i]); }
                    catch (Exception) { }
                }

                string firstPath = ExtractString(first, "path");
                if (!string.IsNullOrEmpty(firstPath))
                {
                    try { File.Delete(firstPath); }
                    catch (Exception) { }
                }

                // 限流/配额被关掉的红态下，第 2、3 发也可能真的落了文件——一并清掉，
                // 不给下一轮留下"目录里有几张 zbf-quota-*.png"的脏状态。
                try
                {
                    string[] leftovers = Directory.GetFiles(directory, "zbf-quota-*.png");
                    for (int i = 0; i < leftovers.Length; i++) File.Delete(leftovers[i]);
                }
                catch (Exception) { }
            }

            Assert.LessOrEqual(CountPng(directory), Math.Max(before, 200),
                "quota test must not leave extra files behind (disk must not be flooded)");
        }

        // ---------------------------------------------------------------- 夹具

        private static int StartConsole()
        {
            for (int i = 0; i < PortAttempts; i++)
            {
                int port = PortBase + i;
                if (RuntimeDevConsole.TryEnableForTests(() => true, port, out string error)) return port;

                if (!string.IsNullOrEmpty(error) && error.Contains("already running"))
                {
                    Assert.Fail("previous test did not Stop the server: " + error);
                }
            }

            Assert.Fail("port range " + PortBase + ".." + (PortBase + PortAttempts - 1) + " all failed to start");
            return 0;
        }

        /// <summary>
        /// 找一个当前**确实被拒**的端口（用于"不监听"的负向用例）。
        /// 用真实的连接结果判定，而不是"先 bind 再释放"——后者有 TOCTOU 窗口（H3①）。
        /// </summary>
        private IEnumerator FindQuietPort(Action<int> sink)
        {
            for (int i = 0; i < PortAttempts; i++)
            {
                int candidate = PortBase + 200 + i;
                ConnectOutcome outcome = ConnectOutcome.Error;
                // 本机实测：连一个未监听的 127.0.0.1 端口，ConnectionRefused 要约 2.07s 才返回
                // （Windows 会先重试一次 SYN）。所以这里的窗口给 5s——既区分得出 Timeout，也不误判。
                yield return ConnectProbe(candidate, (o, d) => outcome = o, 5000);
                if (outcome == ConnectOutcome.Refused)
                {
                    sink(candidate);
                    yield break;
                }
            }

            Assert.Fail("no quiet port in range (" + (PortBase + 200) + ".." + (PortBase + 200 + PortAttempts - 1) + "）");
        }

        private static int CountPng(string directory)
        {
            try
            {
                return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.png").Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void BuildShotRig()
        {
            Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] == null || !cameras[i].isActiveAndEnabled) continue;
                _disabledCameras.Add(cameras[i]);
                cameras[i].enabled = false;
            }

            var cameraObject = new GameObject("~ZBF-ShotCamera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 0f, -4f);
            cameraObject.transform.rotation = Quaternion.identity;
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.12f, 1f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50f;
            _tempObjects.Add(cameraObject);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "~ZBF-ShotCube";
            cube.transform.position = Vector3.zero;
            cube.transform.localScale = new Vector3(1.4f, 1.4f, 1.4f);
            _tempObjects.Add(cube);

            // H3⑤：着色器 fallback 链（内置管线 → URP Unlit → Sprites/Default）。
            // 整条链都拿不到（未知管线）说明"截图必须非纯色"的前提不成立 → 跳过而不是红。
            Shader shader = Shader.Find("Unlit/Color")
                            ?? Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Assert.Ignore("no unlit shader available (Unlit/Color, URP Unlit, Sprites/Default all missing),"
                    + "shot rig premise does not hold. Skipped");
            }

            var material = new Material(shader) { color = Color.white };
            _tempMaterials.Add(material);
            var renderer = cube.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
        }

        private static IEnumerator WaitSeconds(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        // ---- 网络（全部在后台线程，主线程只等 Done）-------------------------

        private enum ConnectOutcome
        {
            Connected,
            Refused,
            Timeout,
            Error,
        }

        private sealed class Exchange
        {
            public volatile bool Done;
            public string Response;
            public string Error;
        }

        private IEnumerator Send(int port, string request, Action<string> sink, float timeoutSeconds = 25f)
        {
            var exchange = new Exchange();
            var thread = new Thread(() =>
            {
                try
                {
                    exchange.Response = SendBlocking(port, request);
                }
                catch (Exception e)
                {
                    exchange.Error = e.GetType().Name + ": " + e.Message;
                }
                finally
                {
                    exchange.Done = true;
                }
            })
            {
                IsBackground = true,
                Name = "ZBF-RT.TestClient",
            };

            thread.Start();

            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!exchange.Done && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsTrue(exchange.Done, "command '" + request + "' waited " + timeoutSeconds + "s without a response (is the main-thread pump running?)");
            Assert.IsNull(exchange.Error, "command '" + request + "' client exception: " + exchange.Error);
            sink(exchange.Response);
        }

        private IEnumerator SendRawNoResponse(int port, string payload, float timeoutSeconds = 10f)
        {
            var exchange = new Exchange();
            var thread = new Thread(() =>
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect(IPAddress.Loopback, port);
                        using (NetworkStream stream = client.GetStream())
                        {
                            if (payload.Length > 0)
                            {
                                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                                stream.Write(bytes, 0, bytes.Length);
                                stream.Flush();
                            }

                            Thread.Sleep(20);
                        }
                    }
                }
                catch (Exception e)
                {
                    exchange.Error = e.GetType().Name + ": " + e.Message;
                }
                finally
                {
                    exchange.Done = true;
                }
            })
            {
                IsBackground = true,
            };

            thread.Start();

            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!exchange.Done && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(exchange.Done, "raw connection " + timeoutSeconds + "s did not finish");
        }

        /// <summary>
        /// H3②：连接探测必须区分四种结果。之前的实现把"超时"也当作"被拒"，
        /// 慢机器上会把"其实在监听但握不上手"读成绿——这是假绿。
        /// </summary>
        private IEnumerator ConnectProbe(int port, Action<ConnectOutcome, string> sink, int timeoutMs = 8000)
        {
            var exchange = new Exchange();
            ConnectOutcome outcome = ConnectOutcome.Error;

            var thread = new Thread(() =>
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        // 同步 Connect：回环上"被拒"是立刻返回的，只有被防火墙吞掉时才会挂。
                        // 挂住的情况由下面的 deadline 归类为 Timeout，**绝不混进 Refused**。
                        client.Connect(IPAddress.Loopback, port);
                        exchange.Response = "connected";
                        outcome = ConnectOutcome.Connected;
                    }
                }
                catch (SocketException e)
                {
                    exchange.Error = e.SocketErrorCode + ": " + e.Message;
                    outcome = e.SocketErrorCode == SocketError.ConnectionRefused
                        ? ConnectOutcome.Refused
                        : (e.SocketErrorCode == SocketError.TimedOut ? ConnectOutcome.Timeout : ConnectOutcome.Error);
                }
                catch (Exception e)
                {
                    exchange.Error = e.GetType().Name + ": " + e.Message;
                    outcome = ConnectOutcome.Error;
                }
                finally
                {
                    exchange.Done = true;
                }
            })
            {
                IsBackground = true,
                Name = "ZBF-RT.ConnectProbe",
            };

            thread.Start();

            float deadline = Time.realtimeSinceStartup + timeoutMs / 1000f;
            while (!exchange.Done && Time.realtimeSinceStartup < deadline) yield return null;

            if (!exchange.Done)
            {
                outcome = ConnectOutcome.Timeout;
                exchange.Error = "connect did not finish within " + timeoutMs + "ms";
            }

            sink(outcome, exchange.Error);
        }

        private static string SendBlocking(int port, string request)
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                client.NoDelay = true;
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 20000;
                    stream.WriteTimeout = 5000;

                    byte[] payload = Encoding.UTF8.GetBytes(request + "\n");
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();

                    var buffer = new MemoryStream();
                    var chunk = new byte[1024];
                    int newline = -1;
                    while (newline < 0)
                    {
                        int read = stream.Read(chunk, 0, chunk.Length);
                        if (read <= 0) break;
                        buffer.Write(chunk, 0, read);
                        newline = Array.IndexOf(buffer.GetBuffer(), (byte)'\n', 0, (int)buffer.Length);
                        if (buffer.Length > 65536) break;
                    }

                    string text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    int newlineIndex = text.IndexOf('\n');
                    if (newlineIndex >= 0) text = text.Substring(0, newlineIndex);
                    return text.TrimEnd('\r');
                }
            }
        }

        // ---- JSON 小工具（响应是单行、形状已知，不做通用解析）----------------

        private static int CountLines(string text)
        {
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }

            return count;
        }

        private static string ExtractString(string json, string key)
        {
            string marker = "\"" + key + "\":\"";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            int end = start;
            while (end < json.Length && json[end] != '"')
            {
                if (json[end] == '\\') end++;
                end++;
            }

            return end <= json.Length ? json.Substring(start, end - start) : null;
        }

        private static double ExtractNumber(string json, string key)
        {
            string marker = "\"" + key + "\":";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return -1.0;
            start += marker.Length;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == 'E' || json[end] == 'e' || json[end] == '+'))
            {
                end++;
            }

            return double.TryParse(json.Substring(start, end - start), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value
                : -1.0;
        }

        private static int IndexOfLevel(string[] names, string name, int fallback)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return Mathf.Clamp(fallback, 0, Mathf.Max(0, names.Length - 1));
        }
    }

    /// <summary>命令名单只通过公开响应断言；这里给测试一个不依赖内部类型的白名单副本。</summary>
    internal static class RuntimeDevConsoleTestAccess
    {
        public static string[] CommandNames()
        {
            return new[] { "help", "dump", "spark", "quality", "fg", "shot" };
        }
    }
}
