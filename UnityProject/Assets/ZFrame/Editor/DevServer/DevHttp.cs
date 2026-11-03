using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// 一个已解析的 HTTP/1.1 请求。请求由后台线程解析后入队，主线程泵出执行，
    /// 执行结果通过 <see cref="Complete"/> 回填，后台线程写回 socket。
    /// </summary>
    internal sealed class DevHttpRequest
    {
        public string Method = "GET";
        public string Path = "/";
        public readonly Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = string.Empty;

        /// <summary>true 表示 handler 不打算在本帧内完成（例如 /compile 要等编译结束）。</summary>
        public bool Deferred;

        readonly ManualResetEventSlim _waiter = new ManualResetEventSlim(false);
        readonly ManualResetEventSlim _written = new ManualResetEventSlim(false);
        DevResponse _response;

        public DevResponse Response => _response;
        public bool Completed => _waiter.IsSet;

        public void Complete(DevResponse response)
        {
            _response = response ?? DevResponse.Error("empty response");
            _waiter.Set();
        }

        public bool WaitForResponse(int milliseconds) => _waiter.Wait(milliseconds);

        /// <summary>后台线程写完响应后置位。主线程在 domain reload 前用它确认响应已落地。</summary>
        public void MarkWritten() => _written.Set();

        public bool WaitForWritten(int milliseconds) => _written.Wait(milliseconds);

        public string GetQuery(string key, string fallback = null)
        {
            return Query.TryGetValue(key, out string value) ? value : fallback;
        }
    }

    internal sealed class DevResponse
    {
        public int Status = 200;
        public string Body = "{}";

        public static DevResponse Ok(object payload) => new DevResponse { Status = 200, Body = DevJson.Serialize(payload) };

        public static DevResponse Error(string message, int status = 400)
        {
            return new DevResponse
            {
                Status = status,
                Body = DevJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = message ?? "unknown error",
                }),
            };
        }

        public static string ReasonOf(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "OK";
            }
        }
    }

    /// <summary>手写的最小 HTTP/1.1 解析/序列化（只认请求行 + 头 + Content-Length 体，响应一律 Connection: close）。</summary>
    internal static class DevHttpIo
    {
        const int MaxHeaderBytes = 16 * 1024;
        const int MaxBodyBytes = 1024 * 1024;

        public static bool TryReadRequest(NetworkStream stream, out DevHttpRequest request, out string error)
        {
            request = null;
            error = null;

            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    error = "connection closed before request headers";
                    return false;
                }

                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxHeaderBytes)
                {
                    error = "request headers exceed " + MaxHeaderBytes + " bytes";
                    return false;
                }

                headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            }

            byte[] raw = buffer.GetBuffer();
            int total = (int)buffer.Length;
            string headerText = Encoding.UTF8.GetString(raw, 0, headerEnd);

            var lines = headerText.Replace("\r\n", "\n").Split('\n');
            var request2 = new DevHttpRequest();
            bool requestLineSeen = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!requestLineSeen)
                {
                    if (line.Length == 0) continue; // 容忍请求行前的空行
                    if (!TryParseRequestLine(line, request2, out error)) return false;
                    requestLineSeen = true;
                    continue;
                }

                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                request2.Headers[name] = value;
            }

            if (!requestLineSeen)
            {
                error = "no request line";
                return false;
            }

            int contentLength = 0;
            if (request2.Headers.TryGetValue("Content-Length", out string lengthText))
            {
                if (!int.TryParse(lengthText, out contentLength) || contentLength < 0)
                {
                    error = "invalid Content-Length: " + lengthText;
                    return false;
                }
                if (contentLength > MaxBodyBytes)
                {
                    error = "request body exceeds " + MaxBodyBytes + " bytes";
                    return false;
                }
            }

            if (contentLength > 0)
            {
                var bodyBytes = new byte[contentLength];
                int alreadyRead = Math.Min(total - headerEnd, contentLength);
                Array.Copy(raw, headerEnd, bodyBytes, 0, alreadyRead);
                int got = alreadyRead;
                while (got < contentLength)
                {
                    int read = stream.Read(bodyBytes, got, contentLength - got);
                    if (read <= 0)
                    {
                        error = "connection closed while reading body";
                        return false;
                    }
                    got += read;
                }

                request2.Body = Encoding.UTF8.GetString(bodyBytes);
            }

            request = request2;
            return true;
        }

        static bool TryParseRequestLine(string line, DevHttpRequest request, out string error)
        {
            error = null;
            string[] parts = line.Split(' ');
            if (parts.Length < 2)
            {
                error = "malformed request line: " + line;
                return false;
            }

            request.Method = parts[0].ToUpperInvariant();

            string target = parts[1];
            int questionMark = target.IndexOf('?');
            if (questionMark >= 0)
            {
                request.Path = target.Substring(0, questionMark);
                ParseQuery(target.Substring(questionMark + 1), request.Query);
            }
            else
            {
                request.Path = target;
            }

            if (request.Path.Length == 0) request.Path = "/";
            if (request.Path.Length > 1 && request.Path.EndsWith("/", StringComparison.Ordinal))
            {
                request.Path = request.Path.TrimEnd('/');
                if (request.Path.Length == 0) request.Path = "/";
            }

            return true;
        }

        static void ParseQuery(string raw, Dictionary<string, string> into)
        {
            if (string.IsNullOrEmpty(raw)) return;
            string[] pairs = raw.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                if (pair.Length == 0) continue;
                int equals = pair.IndexOf('=');
                if (equals < 0)
                {
                    into[Uri.UnescapeDataString(pair)] = string.Empty;
                }
                else
                {
                    string key = Uri.UnescapeDataString(pair.Substring(0, equals).Replace("+", " "));
                    string value = Uri.UnescapeDataString(pair.Substring(equals + 1).Replace("+", " "));
                    into[key] = value;
                }
            }
        }

        /// <summary>返回正文起始下标（已越过空行）；找不到返回 -1。</summary>
        static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i + 4;
                }
            }

            for (int i = 0; i + 1 < length; i++)
            {
                if (buffer[i] == '\n' && buffer[i + 1] == '\n')
                {
                    return i + 2;
                }
            }

            return -1;
        }

        /// <summary>
        /// 写回一个响应。<paramref name="corsOrigin"/> 非空时**只**回这一个具体 origin
        /// （由 <see cref="DevGuard"/> 判定为回环后填入）；绝不回通配 <c>*</c>，
        /// 否则任意第三方页面都能用 fetch 读走 /console 的正文（TASK-A-AUDIT-02 A-1）。
        /// 被拒的请求（403）不回任何 ACAO 头。
        /// </summary>
        public static void WriteResponse(NetworkStream stream, DevResponse response, string corsOrigin = null)
        {
            if (response == null) response = DevResponse.Error("handler produced no response", 500);

            byte[] body = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(DevResponse.ReasonOf(response.Status)).Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            if (!string.IsNullOrEmpty(corsOrigin))
            {
                head.Append("Access-Control-Allow-Origin: ").Append(corsOrigin).Append("\r\n");
                head.Append("Vary: Origin\r\n");
            }

            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
