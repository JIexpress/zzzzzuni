using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// POST /test?mode=editmode|playmode —— 立即返回 runId，绝不阻塞主线程。
    /// GET  /test?id=&lt;runId&gt; —— 轮询运行结果。
    ///
    /// playmode 的关键：进 Play Mode 必然触发 domain reload，内存里的 RunRecord 与 ICallbacks 注册都会蒸发。
    /// 所以：①每个 run 的状态（running / done / abandoned）连同启动时刻落 SessionState；
    ///       ②每个 domain load 时把仍处于 running 的 run 的 callbacks 重新注册一遍（TestRunnerApi 的
    ///         ICallbacks 只挂在当前 AppDomain 上，不重挂就永远收不到 RunFinished）；
    ///       ③只有真收到 RunFinished 才写 done，超时/丢失一律写成 abandoned 且 done:false，绝不伪造完成。
    /// </summary>
    internal static class DevTestRuns
    {
        const string SessionKeyPrefix = "ZFrame.DevServer.TestRun.";
        const string SessionKeyActive = "ZFrame.DevServer.TestRun.Active";

        /// <summary>超过这个岁数还没收到 RunFinished 的 run 判为 abandoned（只报错，不标记完成）。</summary>
        const double AbandonSeconds = 1800.0;

        /// <summary>playmode 已经退出、却仍没收到 RunFinished，且 run 已跑这么久 → 明确报"结果丢失"。</summary>
        const double PlayModeLostSeconds = 300.0;

        sealed class RunRecord
        {
            public string Id;
            public string Mode;
            public string State = "running";
            public bool Done;
            public int Total;
            public int Passed;
            public int Failed;
            public int Skipped;
            public int Inconclusive;
            public long DurationMs;
            public long StartedUnixMs;
            public bool CallbacksRestored;
            public string Note;
            public readonly List<object> Failures = new List<object>();
        }

        static readonly Dictionary<string, RunRecord> _runs = new Dictionary<string, RunRecord>();
        static readonly Dictionary<string, TestRunnerApi> _apis = new Dictionary<string, TestRunnerApi>();
        static readonly Dictionary<string, DevTestCallbacks> _callbacks = new Dictionary<string, DevTestCallbacks>();
        static string _lastRunId;

        public static bool Handle(DevHttpRequest request)
        {
            if (string.Equals(request.Method, "GET", StringComparison.Ordinal)) return Poll(request);
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                request.Complete(DevResponse.Error("method not allowed: " + request.Method + " /test（POST 启动 / GET 轮询）", 405));
                return true;
            }

            string mode = (request.GetQuery("mode", "editmode") ?? "editmode").Trim().ToLowerInvariant();
            TestMode testMode;
            if (mode == "editmode" || mode == "edit") testMode = TestMode.EditMode;
            else if (mode == "playmode" || mode == "play") testMode = TestMode.PlayMode;
            else
            {
                request.Complete(DevResponse.Error("未知 mode '" + mode + "'，只支持 editmode|playmode"));
                return true;
            }

            return Start(request, mode, testMode);
        }

        static bool Start(DevHttpRequest request, string mode, TestMode testMode)
        {
            string runId;
            TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new DevTestCallbacks();

            try
            {
                api.RegisterCallbacks(callbacks);
                runId = api.Execute(new ExecutionSettings(new Filter { testMode = testMode }));
            }
            catch (Exception e)
            {
                request.Complete(DevResponse.Error("启动测试失败: " + e.GetType().Name + ": " + e.Message, 500));
                return true;
            }

            if (string.IsNullOrEmpty(runId))
            {
                request.Complete(DevResponse.Error("TestRunnerApi.Execute 未返回 runId", 500));
                return true;
            }

            long startedUnixMs = NowUnixMs();
            callbacks.RunId = runId;
            _runs[runId] = new RunRecord
            {
                Id = runId,
                Mode = mode,
                State = "running",
                StartedUnixMs = startedUnixMs,
            };
            _apis[runId] = api;
            _callbacks[runId] = callbacks;
            _lastRunId = runId;

            SessionState.SetString(SessionKeyPrefix + runId, "running|" + mode + "|" + startedUnixMs.ToString(CultureInfo.InvariantCulture));
            AddActive(runId);
            Log("test run " + Short(runId) + " started mode=" + mode + " (domain load #" + DevState.DomainReloadCount + ")");

            request.Complete(DevResponse.Ok(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runId"] = runId,
                ["mode"] = mode,
                ["state"] = "running",
                ["poll"] = "/test?id=" + runId,
            }));
            return true;
        }

        /// <summary>
        /// 每次 domain load（含进/出 Play Mode）后调用：把仍 marked running 的 run 的 ICallbacks 重新挂上。
        /// 不重挂的话，playmode 的 RunFinished 会打在"已经没有人监听"的 AppDomain 里，结果永远拿不到。
        /// </summary>
        public static void OnDomainLoad(int domainReloadCount)
        {
            string[] active = ReadActive();
            if (active.Length == 0) return;

            var keep = new List<string>(active.Length);
            for (int i = 0; i < active.Length; i++)
            {
                string id = active[i];
                string persisted = SessionState.GetString(SessionKeyPrefix + id, null);
                if (string.IsNullOrEmpty(persisted) || persisted.StartsWith("done|", StringComparison.Ordinal))
                {
                    continue; // 已经结束，不必重挂
                }

                if (persisted.StartsWith("abandoned|", StringComparison.Ordinal)) continue;

                string[] parts = persisted.Split('|');
                string mode = parts.Length > 1 ? parts[1] : "unknown";
                long startedUnixMs = 0;
                if (parts.Length > 2) long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out startedUnixMs);

                if (AgeSeconds(startedUnixMs) > AbandonSeconds)
                {
                    SessionState.SetString(SessionKeyPrefix + id, "abandoned|" + mode + "|" + startedUnixMs.ToString(CultureInfo.InvariantCulture));
                    Log("test run " + Short(id) + " 超过 " + (int)AbandonSeconds + "s 没有 RunFinished，标记 abandoned（不伪造 done）");
                    continue;
                }

                Restore(id, mode, startedUnixMs, domainReloadCount);
                keep.Add(id);
            }

            WriteActive(keep);
        }

        static void Restore(string id, string mode, long startedUnixMs, int domainReloadCount)
        {
            if (_callbacks.ContainsKey(id)) return;

            TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new DevTestCallbacks { RunId = id };
            try
            {
                api.RegisterCallbacks(callbacks);
            }
            catch (Exception e)
            {
                Log("test run " + Short(id) + " 重挂 callbacks 失败: " + e.GetType().Name + ": " + e.Message);
                return;
            }

            RunRecord record;
            if (!_runs.TryGetValue(id, out record))
            {
                record = new RunRecord { Id = id, Mode = mode, State = "running", StartedUnixMs = startedUnixMs };
                _runs[id] = record;
            }

            record.CallbacksRestored = true;
            _apis[id] = api;
            _callbacks[id] = callbacks;
            _lastRunId = id;

            Log("test run " + Short(id) + " (" + mode + ") callbacks re-registered after domain reload (domain load #" + domainReloadCount + ")");
        }

        static bool Poll(DevHttpRequest request)
        {
            string id = request.GetQuery("id");
            if (string.IsNullOrEmpty(id)) id = _lastRunId;

            if (string.IsNullOrEmpty(id))
            {
                request.Complete(DevResponse.Error("还没有任何测试运行；先 POST /test?mode=editmode", 404));
                return true;
            }

            RunRecord record;
            if (_runs.TryGetValue(id, out record))
            {
                request.Complete(Describe(record));
                return true;
            }

            string persisted = SessionState.GetString(SessionKeyPrefix + id, null);
            if (string.IsNullOrEmpty(persisted))
            {
                request.Complete(DevResponse.Error("run 不存在: " + id, 404));
                return true;
            }

            if (persisted.StartsWith("done|", StringComparison.Ordinal))
            {
                request.Complete(DevResponse.Ok(ParsePersisted(id, persisted)));
                return true;
            }

            if (persisted.StartsWith("abandoned|", StringComparison.Ordinal))
            {
                string[] abandoned = persisted.Split('|');
                request.Complete(DevResponse.Ok(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["id"] = id,
                    ["mode"] = abandoned.Length > 1 ? abandoned[1] : "unknown",
                    ["done"] = false,
                    ["state"] = "abandoned",
                    ["total"] = 0,
                    ["passed"] = 0,
                    ["failed"] = 0,
                    ["failures"] = new List<object>(),
                    ["note"] = "该 run 在 " + (int)AbandonSeconds + "s 内没有收到 RunFinished，已作废（未标记完成）；请重新 POST /test",
                }));
                return true;
            }

            // 走到这里说明本进程内没有记录：通常意味着 callbacks 还没在本次 domain 挂回来（极短的 reload 窗口）。
            request.Complete(DevResponse.Ok(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["id"] = id,
                ["done"] = false,
                ["state"] = "running-after-domain-reload",
                ["total"] = 0,
                ["passed"] = 0,
                ["failed"] = 0,
                ["failures"] = new List<object>(),
                ["note"] = "本次 domain 内暂无该 run 的进度缓存；callbacks 会在 domain load 后重挂，继续轮询即可",
            }));
            return true;
        }

        static DevResponse Describe(RunRecord record)
        {
            // playmode 已经退出却迟迟没有 RunFinished：如实报"结果丢失"，而不是伪造 done。
            if (!record.Done && IsPlayMode(record.Mode) && !EditorApplication.isPlaying
                && AgeSeconds(record.StartedUnixMs) > PlayModeLostSeconds)
            {
                record.State = "lost-after-playmode";
                record.Note = "Play Mode 已退出但从未收到 RunFinished（结果丢失，未标记完成）";
            }

            var payload = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["id"] = record.Id,
                ["mode"] = record.Mode,
                ["done"] = record.Done,
                ["state"] = record.State,
                ["total"] = record.Total,
                ["passed"] = record.Passed,
                ["failed"] = record.Failed,
                ["skipped"] = record.Skipped,
                ["inconclusive"] = record.Inconclusive,
                ["durationMs"] = record.DurationMs,
                ["failures"] = record.Failures,
                ["callbacksRestoredAfterReload"] = record.CallbacksRestored,
                ["elapsedMs"] = (long)Math.Round(AgeSeconds(record.StartedUnixMs) * 1000.0),
            };

            if (!string.IsNullOrEmpty(record.Note)) payload["note"] = record.Note;
            return DevResponse.Ok(payload);
        }

        internal static void OnRunStarted(string runId)
        {
            RunRecord record;
            if (runId == null || !_runs.TryGetValue(runId, out record)) return;
            record.State = "running";
        }

        internal static void OnRunFinished(string runId, ITestResultAdaptor result)
        {
            if (string.IsNullOrEmpty(runId) || result == null) return;

            RunRecord record;
            if (!_runs.TryGetValue(runId, out record))
            {
                // 兜底：即便 record 丢了，也把真结果落盘（模式从 SessionState 续读）。
                record = new RunRecord { Id = runId, Mode = PersistedMode(runId) };
                _runs[runId] = record;
            }

            record.Done = true;
            record.State = "finished";
            record.Passed = result.PassCount;
            record.Failed = result.FailCount;
            record.Skipped = result.SkipCount;
            record.Inconclusive = result.InconclusiveCount;
            record.Total = record.Passed + record.Failed + record.Skipped + record.Inconclusive;
            record.DurationMs = (long)Math.Round(result.Duration * 1000.0);
            record.Failures.Clear();
            CollectFailures(result, record.Failures);

            SessionState.SetString(SessionKeyPrefix + runId, string.Format(CultureInfo.InvariantCulture,
                "done|{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                record.Mode, record.Total, record.Passed, record.Failed, record.Skipped, record.Inconclusive, record.DurationMs));
            RemoveActive(runId);

            Log("test run " + Short(runId) + " (" + record.Mode + ") finished total=" + record.Total
                + " passed=" + record.Passed + " failed=" + record.Failed + " duration=" + record.DurationMs + "ms");

            TestRunnerApi api;
            DevTestCallbacks callbacks;
            if (_apis.TryGetValue(runId, out api) && _callbacks.TryGetValue(runId, out callbacks))
            {
                try { api.UnregisterCallbacks(callbacks); }
                catch { }
            }

            _apis.Remove(runId);
            _callbacks.Remove(runId);
        }

        static void CollectFailures(ITestResultAdaptor node, List<object> into)
        {
            if (node.HasChildren)
            {
                foreach (ITestResultAdaptor child in node.Children) CollectFailures(child, into);
                return;
            }

            if (node.TestStatus != TestStatus.Failed) return;

            into.Add(new Dictionary<string, object>
            {
                ["name"] = node.Name,
                ["fullName"] = node.FullName,
                ["message"] = Truncate(node.Message, 2000),
                ["stack"] = Truncate(node.StackTrace, 2000),
            });
        }

        static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        static Dictionary<string, object> ParsePersisted(string id, string persisted)
        {
            string[] parts = persisted.Split('|');
            if (parts.Length < 8) return new Dictionary<string, object> { ["ok"] = true, ["id"] = id, ["done"] = true };

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["id"] = id,
                ["mode"] = parts[1],
                ["done"] = true,
                ["state"] = "finished",
                ["total"] = ToInt(parts[2]),
                ["passed"] = ToInt(parts[3]),
                ["failed"] = ToInt(parts[4]),
                ["skipped"] = ToInt(parts[5]),
                ["inconclusive"] = ToInt(parts[6]),
                ["durationMs"] = ToInt(parts[7]),
                ["failures"] = new List<object>(),
                ["note"] = "续读自 SessionState（run 结束后发生过 domain reload）",
            };
        }

        static string PersistedMode(string runId)
        {
            string persisted = SessionState.GetString(SessionKeyPrefix + runId, null);
            if (string.IsNullOrEmpty(persisted)) return "unknown";
            string[] parts = persisted.Split('|');
            return parts.Length > 1 ? parts[1] : "unknown";
        }

        // ---------------------------------------------------------------- SessionState 索引

        static string[] ReadActive()
        {
            string raw = SessionState.GetString(SessionKeyActive, string.Empty);
            if (string.IsNullOrEmpty(raw)) return new string[0];
            return raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static void AddActive(string id)
        {
            string[] current = ReadActive();
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] == id) return;
            }

            var next = new List<string>(current.Length + 1);
            next.AddRange(current);
            next.Add(id);
            SessionState.SetString(SessionKeyActive, string.Join(";", next.ToArray()));
        }

        static void RemoveActive(string id)
        {
            string[] current = ReadActive();
            var next = new List<string>(current.Length);
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] != id) next.Add(current[i]);
            }

            WriteActive(next);
        }

        static void WriteActive(List<string> ids)
        {
            SessionState.SetString(SessionKeyActive, string.Join(";", ids.ToArray()));
        }

        // ---------------------------------------------------------------- 工具

        static bool IsPlayMode(string mode)
        {
            return string.Equals(mode, "playmode", StringComparison.OrdinalIgnoreCase);
        }

        static long NowUnixMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        static double AgeSeconds(long startedUnixMs)
        {
            if (startedUnixMs <= 0) return 0.0;
            return (NowUnixMs() - startedUnixMs) / 1000.0;
        }

        static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<null>";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        static void Log(string message)
        {
            Debug.Log("[DevServer] " + message);
        }

        static int ToInt(string text)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }

    internal sealed class DevTestCallbacks : ICallbacks
    {
        public string RunId;

        public void RunStarted(ITestAdaptor testsToRun)
        {
            DevTestRuns.OnRunStarted(RunId);
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            DevTestRuns.OnRunFinished(RunId, result);
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }
    }
}
