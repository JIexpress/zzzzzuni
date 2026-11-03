using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ZFrame.View.DevConsole
{
    /// <summary>一条待执行的命令。后台线程入队后等待，主线程泵执行并回填 JSON 字符串。</summary>
    internal sealed class RuntimeDevCommand
    {
        private const int StatePending = 0;
        private const int StateExecuting = 1;
        private const int StateAbandoned = 2;

        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        private readonly long _deadlineMs;
        private int _state;
        private string _response;

        public RuntimeDevCommand(string line, int timeoutMs)
        {
            Line = line;
            _deadlineMs = NowMs + Math.Max(0, timeoutMs);
        }

        public string Line { get; }

        public string Response => _response;

        /// <summary>
        /// 客户端等待窗口已过（TASK-A-AUDIT-03 B-5）：此刻再执行，副作用会发生在
        /// "已经告诉调用方失败"之后，重试即变成重复执行。
        /// </summary>
        public bool IsExpired => NowMs >= _deadlineMs;

        /// <summary>主线程取到命令时调用：只有仍处于 Pending 才能执行（已放弃/已执行过则返回 false）。</summary>
        public bool TryBeginExecute()
        {
            return Interlocked.CompareExchange(ref _state, StateExecuting, StatePending) == StatePending;
        }

        /// <summary>客户端超时后放弃：此后主线程绝不会再执行这条命令。</summary>
        public bool TryAbandon()
        {
            return Interlocked.CompareExchange(ref _state, StateAbandoned, StatePending) == StatePending;
        }

        public void Complete(string json)
        {
            _response = json ?? "{\"ok\":false,\"error\":\"empty response\"}";
            _done.Set();
        }

        public bool Wait(int milliseconds) => _done.Wait(milliseconds);

        private static long NowMs => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>
    /// 纯 socket 层：**这里没有一行 Unity API**（连日志都不打——后台线程只把文本塞进
    /// <see cref="_pendingLogs"/>，由主线程泵取出后统一 Debug.Log）。
    ///
    /// 只绑 <see cref="IPAddress.Loopback"/>（127.0.0.1），绝不 0.0.0.0 / [::]；
    /// 监听线程 IsBackground=true，不拦进程退出；任何 socket 异常都吞掉并只记一次。
    /// </summary>
    internal sealed class RuntimeDevConsoleServer
    {
        /// <summary>单行请求上限：超了就回一行错误并断开，防 OOM。</summary>
        public const int MaxLineBytes = 4096;

        /// <summary>并发连接上限（TASK-A-DEV-04 H2）：超了**当场拒绝**，不排队、不饿死主线程。</summary>
        public const int MaxConcurrentClients = 8;

        /// <summary>总请求速率上限（每秒，TASK-A-DEV-04 H2）：超了回 <c>rate limited</c> 结构化错误。</summary>
        public const int MaxRequestsPerSecond = 50;

        private const int MaxQueuedRequests = 64;
        private const int CommandTimeoutMs = 10000;
        private const int SocketTimeoutMs = 5000;
        private const int RejectWriteTimeoutMs = 200;

        private readonly int _port;
        private readonly int _commandTimeoutMs;
        private readonly ConcurrentQueue<RuntimeDevCommand> _queue = new ConcurrentQueue<RuntimeDevCommand>();
        private readonly ConcurrentQueue<string> _pendingLogs = new ConcurrentQueue<string>();

        // H2：并发与速率的原子计数。都在 socket 侧（后台线程），不碰主线程。
        private int _activeClients;
        private readonly object _rateLock = new object();
        private long _rateWindowStartMs;
        private int _rateCount;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private static int _liveAcceptThreads;

        /// <summary>当前存活的 accept 线程数（测试用它断言"非开发包一个线程都没起"）。</summary>
        public static int LiveAcceptThreads => Volatile.Read(ref _liveAcceptThreads);

        public RuntimeDevConsoleServer(int port)
            : this(port, CommandTimeoutMs)
        {
        }

        /// <summary>
        /// 测试接缝（TASK-A-AUDIT-03 B-5）：命令超时可注入——PlayMode 用例不能真等 10 秒。
        /// 生产路径永远走上面的默认构造（<see cref="CommandTimeoutMs"/>）。
        /// </summary>
        internal RuntimeDevConsoleServer(int port, int commandTimeoutMs)
        {
            _port = port;
            _commandTimeoutMs = commandTimeoutMs > 0 ? commandTimeoutMs : CommandTimeoutMs;
        }

        public int Port => _port;

        public int Pending => _queue.Count;

        /// <summary>当前被接住的客户端数（含已 accept、尚未分配的；H2 上限的判据）。</summary>
        public int ActiveClients => Volatile.Read(ref _activeClients);

        public IPEndPoint LocalEndPoint
        {
            get
            {
                try
                {
                    return _listener != null ? _listener.LocalEndpoint as IPEndPoint : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public bool TryBind(out string error)
        {
            error = null;
            try
            {
                // 只绑回环：PC 上只有同机进程可达；设备上只有 adb forward 之后主机可达。
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(16);

                _running = true;
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "ZBF-RT.DevConsole.Accept:" + _port,
                };
                _acceptThread.Start();
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;

            try { _listener?.Stop(); }
            catch (Exception) { /* 关闭期 SocketException 无需处理 */ }
            _listener = null;

            RuntimeDevCommand leftover;
            while (_queue.TryDequeue(out leftover))
            {
                leftover.Complete("{\"ok\":false,\"error\":\"dev console stopped\"}");
            }

            Thread thread = _acceptThread;
            _acceptThread = null;
            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(500); }
                catch (Exception) { }
            }
        }

        public bool TryDequeue(out RuntimeDevCommand command) => _queue.TryDequeue(out command);

        /// <summary>主线程调用：把后台攒下的日志取出来（最多 <paramref name="max"/> 条）。</summary>
        public bool TryDequeueLog(out string message) => _pendingLogs.TryDequeue(out message);

        private void LogOnce(string message)
        {
            // 去重：同一条异常只留一份，避免坏客户端把队列刷爆。
            if (_pendingLogs.Count > 32) return;
            _pendingLogs.Enqueue(message);
        }

        private void AcceptLoop()
        {
            Interlocked.Increment(ref _liveAcceptThreads);
            try
            {
                while (_running)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        if (!_running) return;
                        continue;
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (InvalidOperationException) { return; }
                    catch (Exception)
                    {
                        if (!_running) return;
                        continue;
                    }

                    TcpClient captured = client;

                    // H2①：并发上限。超了立刻回一行结构化错误并关闭——**不做排队**，
                    // 否则恶意进程可以占满 ThreadPool / 队列把主线程泵拖死。
                    int active = Interlocked.Increment(ref _activeClients);
                    if (active > MaxConcurrentClients)
                    {
                        Interlocked.Decrement(ref _activeClients);
                        LogOnce("connection rejected: concurrent limit " + MaxConcurrentClients + " reached");
                        RejectClient(captured, "too many concurrent connections (max " + MaxConcurrentClients + ")");
                        continue;
                    }

                    try
                    {
                        ThreadPool.QueueUserWorkItem(_ => ServeClient(captured));
                    }
                    catch (Exception e)
                    {
                        Interlocked.Decrement(ref _activeClients);
                        LogOnce("client dispatch failed: " + e.GetType().Name);
                        try { captured.Close(); }
                        catch (Exception) { }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _liveAcceptThreads);
            }
        }

        private void ServeClient(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = SocketTimeoutMs;
                    stream.WriteTimeout = SocketTimeoutMs;

                    string line;
                    bool tooLong;
                    string readError;
                    if (!TryReadLine(stream, out line, out tooLong, out readError))
                    {
                        if (tooLong)
                        {
                            WriteLine(stream, ErrorJson("line exceeds " + MaxLineBytes + " bytes; connection closed"));
                            LogOnce("oversize line rejected (> " + MaxLineBytes + " bytes)");
                        }
                        else if (!string.IsNullOrEmpty(readError))
                        {
                            WriteLine(stream, ErrorJson(readError));
                        }

                        return;
                    }

                    if (_queue.Count >= MaxQueuedRequests)
                    {
                        WriteLine(stream, ErrorJson("too many pending commands"));
                        return;
                    }

                    // H2②：总请求速率上限。超了回结构化错误（绝不静默丢弃）。
                    if (!TryConsumeRateBudget(out int retryAfterMs))
                    {
                        WriteLine(stream, ErrorJson("rate limited (max " + MaxRequestsPerSecond
                            + " requests/s; retry in " + retryAfterMs + "ms)"));
                        return;
                    }

                    var command = new RuntimeDevCommand(line, _commandTimeoutMs);
                    _queue.Enqueue(command);

                    if (!command.Wait(_commandTimeoutMs))
                    {
                        // TASK-A-AUDIT-03 (B-5): the client is being told "failed". Mark the
                        // command abandoned so the main-thread pump drops it instead of running
                        // it afterwards — otherwise the side effect lands after the failure
                        // response and a caller retry executes the command twice.
                        command.TryAbandon();
                        WriteLine(stream, ErrorJson("command timeout after " + _commandTimeoutMs + "ms (main thread busy?)"));
                        return;
                    }

                    WriteLine(stream, command.Response);
                }
            }
            catch (Exception)
            {
                // 客户端提前断开/超时/服务停止：本地开发工具，不往 Console 刷噪音。
            }
            finally
            {
                Interlocked.Decrement(ref _activeClients);
                try { client.Close(); }
                catch (Exception) { }
            }
        }

        /// <summary>固定窗口限速（1s 窗口）：返回 false 表示本窗口的额度已用完。</summary>
        private bool TryConsumeRateBudget(out int retryAfterMs)
        {
            long now = NowMs;
            lock (_rateLock)
            {
                if (now - _rateWindowStartMs >= 1000L)
                {
                    _rateWindowStartMs = now;
                    _rateCount = 0;
                }

                if (_rateCount >= MaxRequestsPerSecond)
                {
                    retryAfterMs = (int)Math.Max(1L, 1000L - (now - _rateWindowStartMs));
                    return false;
                }

                _rateCount++;
                retryAfterMs = 0;
                return true;
            }
        }

        private static long NowMs => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

        /// <summary>拒绝连接：一行结构化错误 + 立刻关闭，不占用 ThreadPool 工作线程。</summary>
        private void RejectClient(TcpClient client, string message)
        {
            try
            {
                client.Client.SendTimeout = RejectWriteTimeoutMs;
                using (NetworkStream stream = client.GetStream())
                {
                    WriteLine(stream, ErrorJson(message));
                }
            }
            catch (Exception)
            {
                // 对端已经走了：拒绝本身已经生效，无需再报。
            }
            finally
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 读一行（<c>\n</c> 结尾，容忍 <c>\r\n</c>）。返回 false 时：
        /// <paramref name="tooLong"/> = 超过 4KB；<paramref name="error"/> 非空 = 可回报的读错误；
        /// 两者都空 = 对端没发完整行就断开（半截数据，静默忽略）。
        /// </summary>
        private static bool TryReadLine(NetworkStream stream, out string line, out bool tooLong, out string error)
        {
            line = null;
            tooLong = false;
            error = null;

            var buffer = new byte[MaxLineBytes + 1];
            int filled = 0;

            while (true)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, filled, buffer.Length - filled);
                }
                catch (IOException)
                {
                    return false; // 超时/对端关闭
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (read <= 0) return false;
                filled += read;

                int newline = Array.IndexOf(buffer, (byte)'\n', 0, filled);
                if (newline >= 0)
                {
                    if (newline > MaxLineBytes)
                    {
                        tooLong = true;
                        return false;
                    }

                    int length = newline;
                    if (length > 0 && buffer[length - 1] == (byte)'\r') length--;
                    line = Encoding.UTF8.GetString(buffer, 0, length).Trim();
                    return true;
                }

                if (filled > MaxLineBytes)
                {
                    tooLong = true;
                    return false;
                }

                if (filled == buffer.Length)
                {
                    tooLong = true;
                    return false;
                }
            }
        }

        private static void WriteLine(NetworkStream stream, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes((json ?? "{\"ok\":false}") + "\n");
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static string ErrorJson(string message)
        {
            return "{\"ok\":false,\"error\":" + Escape(message) + "}";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
