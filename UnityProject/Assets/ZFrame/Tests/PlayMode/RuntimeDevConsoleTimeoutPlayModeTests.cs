using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using ZFrame.View;
using ZFrame.View.DevConsole;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-AUDIT-03 (B-5) — a command that timed out must not run afterwards.
    ///
    /// Before the fix the socket thread answered <c>ok:false / command timeout</c> while
    /// the command stayed in the queue and was executed by the next main-thread pump:
    /// "failed but effective". A caller that retries therefore executed the side effect
    /// twice. Chosen semantics: <b>timeout means the command is abandoned, never run</b>
    /// (the alternative — always run and report success — cannot undo a failure already
    /// reported to the caller).
    ///
    /// The case drives a standalone <see cref="RuntimeDevConsoleServer"/> with an injected
    /// short timeout, never pumps it while the client waits (that is the "main thread is
    /// busy" premise), then pumps and asserts the side effect did NOT happen.
    ///
    /// Mutation-verified: removing the expiry/abandon guard from
    /// <c>RuntimeDevConsole.Pump(server)</c> turns this case red — QualitySettings changes.
    /// </summary>
    public sealed class RuntimeDevConsoleTimeoutPlayModeTests
    {
        private const int PortBase = 17780;
        private const int PortAttempts = 40;
        private const int InjectedTimeoutMs = 60;

        [Test]
        public void TimedOutCommand_IsDiscarded_AndItsSideEffectNeverHappens()
        {
            int original = QualitySettings.GetQualityLevel();
            int target = PickDifferentLevel(original);

            RuntimeDevConsoleServer server = StartServer(out int port);
            Assert.IsNotNull(server, "precondition: a standalone console server must bind a loopback port");

            try
            {
                string response = null;
                var client = new Thread(() =>
                {
                    try
                    {
                        response = SendBlocking(port, "quality " + CommandNameFor(target));
                    }
                    catch (Exception e)
                    {
                        response = "__exception__:" + e.GetType().Name + ": " + e.Message;
                    }
                })
                {
                    IsBackground = true,
                    Name = "AUDIT03.ConsoleClient",
                };

                client.Start();

                // The main thread does NOT pump while the client waits — this is the
                // production premise (a frame longer than the command timeout). The client
                // must come back with the timeout answer, not with a command result.
                Assert.IsTrue(client.Join(InjectedTimeoutMs + 8000),
                    "the client never returned; the timeout path was not exercised");
                Assert.IsNotNull(response, "the client got no answer at all");
                StringAssert.Contains("\"ok\":false", response);
                StringAssert.Contains("timeout", response);

                // ---- the defect trigger: the command is still queued ----
                RuntimeDevConsole.Pump(server);

                Assert.AreEqual(0, server.Pending, "the abandoned command must leave the queue");

                // ---- assertion: it must NOT have been executed ----
                Assert.AreEqual(original, QualitySettings.GetQualityLevel(),
                    "the timed-out command was executed anyway: the caller was told 'failed' but the side " +
                    "effect happened (a retry would now execute it twice)");
            }
            finally
            {
                server.Stop();
                QualitySettings.SetQualityLevel(original, true);
            }
        }

        /// <summary>Binds a standalone server with the injected short timeout (production uses 10s).</summary>
        private static RuntimeDevConsoleServer StartServer(out int boundPort)
        {
            for (int i = 0; i < PortAttempts; i++)
            {
                int candidate = PortBase + i;
                var server = new RuntimeDevConsoleServer(candidate, InjectedTimeoutMs);
                if (server.TryBind(out string error))
                {
                    boundPort = candidate;
                    return server;
                }

                Debug.LogWarning("[AUDIT03] port " + candidate + " unavailable: " + error);
            }

            Assert.Fail("no free port in " + PortBase + ".." + (PortBase + PortAttempts - 1));
            boundPort = 0;
            return null;
        }

        private static int PickDifferentLevel(int current)
        {
            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                if (i != current) return i;
            }

            Assert.Fail("precondition: the project must have at least two quality levels (has " + names.Length + ")");
            return current;
        }

        /// <summary>Maps a level index onto the whitelisted quality names the command accepts.</summary>
        private static string CommandNameFor(int level)
        {
            switch (level)
            {
                case 0: return "low";
                case 1: return "medium";
                default: return "high";
            }
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

                    var buffer = new System.IO.MemoryStream();
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
    }
}
