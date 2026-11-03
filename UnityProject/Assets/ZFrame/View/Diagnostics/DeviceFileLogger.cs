using System;
using System.IO;
using System.Text;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.View.Diagnostics
{
    /// <summary>
    /// Device file logger (TASK-A-AND-03): mirrors the whole boot chain into
    /// <c>Application.persistentDataPath/zframe_boot.log</c> so a device whose
    /// ROM suppresses Unity logcat output (Huawei/HarmonyOS — the A-AND-03
    /// symptom) can still be diagnosed over <c>adb shell run-as ... cat</c>.
    ///
    /// MOUNTING: subscribes to <see cref="ZLog.OnLog"/> — the Core-side firehose
    /// that fires BEFORE channel/level filtering, so every ZLog message is
    /// captured even though ZLog's channels are whitelist-disabled by default.
    /// Additionally captures UnityEngine.Debug output via
    /// <see cref="Application.logMessageReceived"/> (engine errors, exceptions
    /// from any assembly — and every engine log line, which is exactly the
    /// stream the suppressed logcat would have carried) and
    /// <see cref="AppDomain.UnhandledException"/>.
    ///
    /// ARMING (self-healing — the A-AND-03 round-1 lesson): the first attempt
    /// happens at <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>,
    /// but every public entry point re-attempts arming until it succeeds, so a
    /// too-early player init (persistentDataPath not ready, sandbox not yet
    /// writable — observed on device) cannot silently disable the logger. If
    /// the primary path fails, <see cref="Application.temporaryCachePath"/> is
    /// tried before giving up; the last init error is kept in
    /// <see cref="LastError"/> for on-screen display by the boot chain.
    ///
    /// LIFECYCLE: the file is TRUNCATED once per process start and each run
    /// writes a header (device/OS/graphics/screen/version) — one file = one
    /// run, which is exactly the diagnostic contract.
    ///
    /// COST: compiled only for Development builds and the editor
    /// (<c>#if DEVELOPMENT_BUILD || UNITY_EDITOR</c>); in a non-development
    /// player build every public entry point is a JIT-folded no-op (an empty
    /// method body), so release keeps zero file IO and zero allocations.
    /// Recursion-safe (a Debug.Log raised while writing is dropped, not
    /// re-captured) and self-disarming (a write failure disables the logger
    /// instead of throwing during boot).
    /// </summary>
    public static class DeviceFileLogger
    {
        /// <summary>File name inside the arming directory.</summary>
        public const string FileName = "zframe_boot.log";

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private static readonly object _gate = new object();
        private static StreamWriter _writer;
        private static bool _armed;
        private static bool _inCapture; // recursion guard for Debug.Log reentry
        private static bool _hooksAttached;
        private static int _stepCount;
        private static string _lastError = "never attempted";

        /// <summary>Absolute path of the device log (empty until armed).</summary>
        public static string FilePath { get; private set; } = string.Empty;

        /// <summary>True once the file is open and the hooks are attached.</summary>
        public static bool IsArmed => _armed;

        /// <summary>How many Step markers were written (boot progress meter for tests).</summary>
        public static int StepCount => _stepCount;

        /// <summary>
        /// The reason arming failed so far (empty when armed). GameBootstrap
        /// surfaces this on the BootErrorOverlay — with logcat suppressed the
        /// screen is the only error channel a device diagnosis can trust.
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>Earliest managed player hook. Failure is recoverable: every entry point re-arms.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void AutoBoot()
        {
            TryArm();
        }

        /// <summary>
        /// Attempts to open the log file (truncate) and attach capture hooks.
        /// Returns false (without throwing) when arming is impossible right
        /// now; callers simply try again on their next output.
        /// </summary>
        private static bool TryArm()
        {
            lock (_gate)
            {
                if (_armed)
                {
                    return true;
                }

                try
                {
                    string dir = Application.persistentDataPath;
                    string path = Path.Combine(dir, FileName);
                    _writer = new StreamWriter(path, append: false, encoding: new UTF8Encoding(false));
                    _writer.AutoFlush = true; // every line survives a hang/crash — that is the point
                    _armed = true;
                    FilePath = path;
                    _lastError = string.Empty;

                    AttachHooks();

                    WriteLine("HDR", "=== zframe_boot.log opened (one file = one process run) ===");
                    WriteLine("HDR", $"unity={Application.unityVersion} product={Application.productName}"
                                     + $" version={Application.version}");
                    WriteLine("HDR", $"device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem}"
                                     + $" graphics={SystemInfo.graphicsDeviceType}/{SystemInfo.graphicsDeviceName}"
                                     + $" mem={SystemInfo.systemMemorySize}MB");
                    WriteLine("HDR", $"screen={Screen.currentResolution} dpi={Screen.dpi}"
                                     + $" persistentDataPath={dir}");
                    WriteLine("HDR", $"internet={Application.internetReachability}"
                                     + $" battery={SystemInfo.batteryStatus}"
                                     + $" deviceType={SystemInfo.deviceType}");
                    return true;
                }
                catch (Exception error)
                {
                    DisarmLocked();
                    _lastError = $"{error.GetType().Name}: {error.Message}";

                    // One best-effort fallback: the internal cache directory
                    // (same sandbox, different mount semantics on some ROMs).
                    try
                    {
                        string dir = Application.temporaryCachePath;
                        string path = Path.Combine(dir, FileName);
                        _writer = new StreamWriter(path, append: false, encoding: new UTF8Encoding(false));
                        _writer.AutoFlush = true;
                        _armed = true;
                        FilePath = path;
                        _lastError = string.Empty;

                        AttachHooks();

                        WriteLine("HDR", "=== zframe_boot.log opened (FALLBACK to temporaryCachePath) ===");
                        WriteLine("HDR", $"persistentDataPath attempt failed with: {_lastError}");
                        WriteLine("HDR", $"unity={Application.unityVersion} device={SystemInfo.deviceModel}"
                                         + $" os={SystemInfo.operatingSystem}"
                                         + $" graphics={SystemInfo.graphicsDeviceType}");
                        return true;
                    }
                    catch (Exception fallbackError)
                    {
                        DisarmLocked();
                        _lastError = $"pdp: {_lastError} | cache: {fallbackError.GetType().Name}: {fallbackError.Message}";
                        return false;
                    }
                }
            }
        }

        private static void AttachHooks()
        {
            if (_hooksAttached)
            {
                return;
            }

            ZLog.OnLog += OnZLog;
            Application.logMessageReceived += OnUnityLog;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            _hooksAttached = true;
        }

        private static void DisarmLocked()
        {
            _armed = false;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // teardown of a diagnostic must never throw
            }

            _writer = null;
        }

        /// <summary>
        /// Boot-chain step marker written straight to the file (bypasses ZLog
        /// channel filtering by design — steps must be visible unconditionally).
        /// </summary>
        public static void Step(string message)
        {
            _stepCount++;
            WriteLine("STEP", $"#{_stepCount:D2} {message}");
        }

        /// <summary>Dumps a caught exception with type, message and stack into the file.</summary>
        public static void Exception(string where, Exception error)
        {
            if (error == null)
            {
                WriteLine("EXC", $"{where}: <null exception>");
                return;
            }

            WriteLine("EXC", $"{where}: {error.GetType().FullName}: {error.Message}");
            WriteLine("EXC", $"{where} stack:\n{error.StackTrace}");
        }

        /// <summary>Flushes and detaches hooks (tests / shutdown). Safe to call repeatedly.</summary>
        public static void Shutdown()
        {
            lock (_gate)
            {
                if (!_armed && !_hooksAttached)
                {
                    return;
                }

                try
                {
                    if (_hooksAttached)
                    {
                        ZLog.OnLog -= OnZLog;
                        Application.logMessageReceived -= OnUnityLog;
                        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                        _hooksAttached = false;
                    }

                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch
                {
                    // teardown of a diagnostic must never throw
                }

                _writer = null;
                _armed = false;
            }
        }

        private static void OnZLog(LogLevel level, string channel, string message)
        {
            WriteLine(level switch
            {
                LogLevel.Error => "ERR",
                LogLevel.Warn => "WRN",
                _ => "INF"
            }, $"[{channel}] {message}");
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (_inCapture)
            {
                return; // a log raised while we were writing — drop, never recurse
            }

            string tag = type switch
            {
                LogType.Exception => "EXC",
                LogType.Error or LogType.Assert => "ERR",
                LogType.Warning => "WRN",
                _ => "LOG"
            };

            // Unity stack traces are noisy; keep them only for failures.
            string text = type == LogType.Log ? condition : condition + "\n" + stackTrace;
            WriteLine(tag, text);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            WriteLine("EXC", $"AppDomain.UnhandledException (terminating={args.IsTerminating}): {args.ExceptionObject}");
        }

        private static void WriteLine(string tag, string message)
        {
            if (!_armed && !TryArm())
            {
                return;
            }

            lock (_gate)
            {
                if (!_armed)
                {
                    return;
                }

                try
                {
                    _inCapture = true; // Debug.Log inside the catch below must not recurse
                    string time = DateTime.UtcNow.ToString("HH:mm:ss.fff");
                    float up = Time.realtimeSinceStartup;
                    _writer.WriteLine($"{time} +{up,8:0.000}s {tag,-3} {message}");
                }
                catch (Exception)
                {
                    // disk full / IO failure: disarm instead of breaking the boot;
                    // the next call re-arms (fresh file) if the failure was transient
                    DisarmLocked();
                }
                finally
                {
                    _inCapture = false;
                }
            }
        }
#else
        /// <summary>
        /// Release-build stub: the whole file-logging body is compiled out
        /// (<c>#if DEVELOPMENT_BUILD || UNITY_EDITOR</c>) and this no-op is
        /// JIT-folded at every call site — zero IO, zero allocations in a
        /// non-development player build.
        /// </summary>
        public static void Step(string message)
        {
        }

        /// <summary>Release-build stub (no-op, see class doc).</summary>
        public static void Exception(string where, Exception error)
        {
        }

        /// <summary>
        /// Release-build stub (no-op, see class doc). Constantly false —
        /// semantically safe: the only call site that REACTS to
        /// <c>IsArmed == false</c> (GameBootstrap's "device file log NOT
        /// armed" BootErrorOverlay.Show) is itself compiled out under the
        /// same <c>#if DEVELOPMENT_BUILD || UNITY_EDITOR</c> gate, so this
        /// stub value can never surface a user-visible boot error in a
        /// release build (TASK-A-AND-03 V2-F1). Remaining consumers are
        /// dev-only (the on-screen status line in GameBootstrap).
        /// </summary>
        public static bool IsArmed => false;

        /// <summary>Release-build stub (no-op, see class doc).</summary>
        public static string FilePath => string.Empty;

        /// <summary>Release-build stub (no-op, see class doc).</summary>
        public static string LastError => string.Empty;

        /// <summary>Release-build stub (no-op, see class doc).</summary>
        public static int StepCount => 0;

        /// <summary>Release-build stub (no-op, see class doc).</summary>
        public static void Shutdown()
        {
        }
#endif
    }
}
