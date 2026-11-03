using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// 监听 + 收发全部在后台线程完成；<b>这里没有一行 Unity API</b>。
    /// 请求解析后入队，主线程（EditorApplication.update）泵出执行后回填 <see cref="DevHttpRequest.Complete"/>。
    /// 只绑 127.0.0.1，监听线程 IsBackground=true，绝不阻止 Editor 退出。
    /// </summary>
    internal sealed class DevServerCore
    {
        /// <summary>主线程可能被长任务占住（导入/编译），给足等待上限；超时返回 504 而不是挂死。</summary>
        const int RequestTimeoutMs = 300000;

        /// <summary>
        /// 并发连接上限（TASK-A-AUDIT-02 A-2，与 B 层 <c>RuntimeDevConsoleServer</c> 同口径）：
        /// 超了**当场拒绝**，不排队、不再占用一个 ThreadPool 线程等 300s。
        /// </summary>
        public const int MaxConcurrentClients = 8;

        /// <summary>待执行请求队列上限（同 B 层口径）：每条最多 1MB body，无上限时内存线性增长。</summary>
        public const int MaxQueuedRequests = 64;

        /// <summary>拒绝一条连接时写一行结构化错误的时间预算；对端不读也不能把我们挂住。</summary>
        const int RejectWriteTimeoutMs = 200;

        /// <summary>拒绝后把残留请求字节读干的预算（避免 close 带未读数据触发 RST 丢正文）。</summary>
        const int RejectDrainTimeoutMs = 50;

        readonly int _port;
        readonly ConcurrentQueue<DevHttpRequest> _queue = new ConcurrentQueue<DevHttpRequest>();

        /// <summary>后台线程只往这里塞文本，由主线程泵出后统一 Debug.Log（后台不碰 Unity API）。</summary>
        readonly ConcurrentQueue<string> _pendingLogs = new ConcurrentQueue<string>();

        int _activeClients;

        TcpListener _listener;
        Thread _acceptThread;
        volatile bool _running;

        public DevServerCore(int port)
        {
            _port = port;
        }

        public int Port => _port;
        public int Pending => _queue.Count;

        /// <summary>当前被接住的客户端数（H2 上限的判据，测试/诊断用）。</summary>
        public int ActiveClients => Volatile.Read(ref _activeClients);

        /// <summary>取一条待打印的后台日志（主线程泵调用）。</summary>
        public bool TryDequeueLog(out string message) => _pendingLogs.TryDequeue(out message);

        public bool TryStart(out string error)
        {
            error = null;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                // 先开端口复用，避免上一次 domain reload / 快速重启撞 TIME_WAIT。
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(16);

                _running = true;
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "ZFrame.DevServer.Accept:" + _port,
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
            catch { /* 关闭期的 SocketException 无需处理 */ }
            _listener = null;

            DevHttpRequest leftover;
            while (_queue.TryDequeue(out leftover))
            {
                leftover.Complete(DevResponse.Error("dev server stopped", 503));
            }

            Thread thread = _acceptThread;
            _acceptThread = null;
            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(500); }
                catch { }
            }
        }

        public bool TryDequeue(out DevHttpRequest request) => _queue.TryDequeue(out request);

        void AcceptLoop()
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
                catch (Exception) { if (!_running) return; continue; }

                TcpClient captured = client;

                // A-2①：并发上限。超了立刻回一条结构化错误并关闭——排队会让本机任意进程
                // 用 N 条连接占满 ThreadPool，把 Editor 自己的导入/TestRunner 工作饿死。
                int active = Interlocked.Increment(ref _activeClients);
                if (active > MaxConcurrentClients)
                {
                    Interlocked.Decrement(ref _activeClients);
                    Log("connection rejected: concurrent limit " + MaxConcurrentClients + " reached");
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
                    Log("client dispatch failed: " + e.GetType().Name);
                    try { captured.Close(); }
                    catch { }
                }
            }
        }

        void ServeClient(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 15000;
                    stream.WriteTimeout = 15000;

                    DevHttpRequest request;
                    string error;
                    if (!DevHttpIo.TryReadRequest(stream, out request, out error))
                    {
                        DevHttpIo.WriteResponse(stream, DevResponse.Error(error, 400));
                        return;
                    }

                    // A-1：来源闸门。Host 必须是回环、Origin 必须是本机（或无 Origin 的脚本客户端）。
                    // 被拒的请求一个 handler 都不进，因此 /menu 永远不会被执行。
                    string corsOrigin;
                    string denial = DevGuard.Authorize(request, out corsOrigin);
                    if (denial != null)
                    {
                        Log("request rejected: " + denial + " [" + request.Method + " " + request.Path + "]");
                        DevHttpIo.WriteResponse(stream, DevResponse.Error(denial, 403));
                        return;
                    }

                    // 免主线程快路径（/health）：只读快照，后台线程直接答复，主线程忙时也不排队。
                    DevResponse fastResponse;
                    if (DevRouter.TryFastPath(request, out fastResponse))
                    {
                        DevHttpIo.WriteResponse(stream, fastResponse, corsOrigin);
                        return;
                    }

                    // A-2②：队列上限。超了立刻回结构化错误，不入队、不阻塞等待。
                    if (_queue.Count >= MaxQueuedRequests)
                    {
                        Log("request rejected: pending queue full (" + MaxQueuedRequests + ")");
                        DevHttpIo.WriteResponse(stream,
                            DevResponse.Error("too many pending requests (max " + MaxQueuedRequests + "); retry later", 503),
                            corsOrigin);
                        return;
                    }

                    _queue.Enqueue(request);

                    if (!request.WaitForResponse(RequestTimeoutMs))
                    {
                        DevHttpIo.WriteResponse(stream,
                            DevResponse.Error("handler timeout after " + RequestTimeoutMs + "ms (main thread busy?)", 504),
                            corsOrigin);
                        request.MarkWritten();
                        return;
                    }

                    DevHttpIo.WriteResponse(stream, request.Response, corsOrigin);
                    request.MarkWritten();
                }
            }
            catch (Exception)
            {
                // 客户端提前断开 / 超时 / 服务停止，一律静默：这是本地开发工具，不能往 Console 里刷噪音。
            }
            finally
            {
                Interlocked.Decrement(ref _activeClients);
                try { client.Close(); }
                catch { }
            }
        }

        /// <summary>拒绝一条连接：写一行结构化 503 + 立刻关闭；不占队列、不等主线程。</summary>
        static void RejectClient(TcpClient client, string message)
        {
            try
            {
                Socket socket = client.Client;
                socket.SendTimeout = RejectWriteTimeoutMs;

                // 不用 using：NetworkStream 释放会**立刻**关掉 socket，而下面的排空必须在关之前。
                NetworkStream stream = client.GetStream();
                DevHttpIo.WriteResponse(stream, DevResponse.Error(message, 503));

                // 被拒的连接里通常还躺着对端刚发来的请求字节。带着未读数据直接 close，
                // Windows 会回 RST，客户端可能只收到响应头、正文被丢掉（实测 curl 只剩头）。
                // 先半关，再把残留读干，最后 close —— 正文才能确定性送达。
                try
                {
                    socket.Shutdown(SocketShutdown.Send);
                    socket.ReceiveTimeout = RejectDrainTimeoutMs;
                    var scratch = new byte[512];
                    while (socket.Receive(scratch) > 0)
                    {
                        // 丢弃
                    }
                }
                catch (Exception)
                {
                    // 对端已经走了 / 排空超时：拒绝已经生效。
                }
            }
            catch (Exception)
            {
                // 对端已经走了：拒绝本身已经生效。
            }
            finally
            {
                try { client.Close(); }
                catch { }
            }
        }

        /// <summary>后台线程记一行日志：只入队，由主线程泵出后 Debug.Log（后台线程零 Unity API）。</summary>
        void Log(string message)
        {
            if (_pendingLogs.Count > 32) return; // 坏客户端刷屏时自保
            _pendingLogs.Enqueue(message);
        }
    }
}
