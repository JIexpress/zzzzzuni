using System;
using System.Collections.Generic;
using NUnit.Framework;
using ZFrame.Core;

namespace ZFrame.Core.Tests
{
    /// <summary>EditMode tests for <see cref="ZLog"/> channel filtering and the Error → EventBus bridge.</summary>
    public class ZLogTests
    {
        private Action<LogLevel, string, string, string> _previousSink;
        private readonly List<(LogLevel Level, string Channel, string Message)> _emitted =
            new List<(LogLevel, string, string)>();

        [SetUp]
        public void SetUp()
        {
            ZLog.Reset();
            _emitted.Clear();
            _previousSink = ZLog.SetSink((level, channel, msg, line) => _emitted.Add((level, channel, msg)));
        }

        [TearDown]
        public void TearDown()
        {
            ZLog.SetSink(_previousSink);
            ZLog.Reset();
            EventBus.FlushQueued();
        }

        [Test]
        public void EnabledChannel_Emits()
        {
            ZLog.EnableChannel("Combat");
            ZLog.Info("Combat", "hit for 5");

            Assert.AreEqual(1, _emitted.Count, "enabled channel must emit");
            Assert.AreEqual(LogLevel.Info, _emitted[0].Level);
            Assert.AreEqual("Combat", _emitted[0].Channel);
            Assert.AreEqual("hit for 5", _emitted[0].Message);
        }

        [Test]
        public void DisabledChannel_IsFiltered()
        {
            ZLog.EnableChannel("Combat");
            ZLog.Info("AI", "thinking");

            Assert.AreEqual(0, _emitted.Count, "channel not in the whitelist must not emit");
            Assert.IsFalse(ZLog.WouldLog("AI", LogLevel.Info));
            Assert.IsTrue(ZLog.WouldLog("Combat", LogLevel.Info));
        }

        [Test]
        public void MinimumLevel_FiltersLowSeverity()
        {
            ZLog.EnableChannel("Net");
            ZLog.MinimumLevel = LogLevel.Warn;

            ZLog.Info("Net", "ping");
            ZLog.Warn("Net", "high latency");
            ZLog.Error("Net", "disconnected");

            Assert.AreEqual(2, _emitted.Count, "Info below MinimumLevel is dropped");
            Assert.AreEqual(LogLevel.Warn, _emitted[0].Level);
            Assert.AreEqual(LogLevel.Error, _emitted[1].Level);
        }

        [Test]
        public void DisableChannel_StopsOutput()
        {
            ZLog.EnableChannel("UI");
            ZLog.Info("UI", "panel opened");
            ZLog.DisableChannel("UI");
            ZLog.Info("UI", "panel closed");

            Assert.AreEqual(1, _emitted.Count, "output stops after DisableChannel");
        }

        [Test]
        public void Error_FiresLogErrorEvent_OnEventBus_EvenWhenFiltered()
        {
            int errors = 0;
            string seenChannel = null;
            string seenMessage = null;
            using (EventBus.Subscribe<LogErrorEvent>(e =>
            {
                errors++;
                seenChannel = e.Channel;
                seenMessage = e.Message;
            }))
            {
                // Channel NOT enabled: console output is filtered...
                ZLog.Error("Save", "disk full");
            }

            Assert.AreEqual(0, _emitted.Count, "filtered channel must not reach the sink");
            Assert.AreEqual(1, errors, "Error must fire LogErrorEvent regardless of channel filter");
            Assert.AreEqual("Save", seenChannel);
            Assert.AreEqual("disk full", seenMessage);
        }

        [Test]
        public void Info_And_Warn_DoNotFireLogErrorEvent()
        {
            int errors = 0;
            ZLog.EnableChannel("Any");
            using (EventBus.Subscribe<LogErrorEvent>(_ => errors++))
            {
                ZLog.Info("Any", "fine");
                ZLog.Warn("Any", "meh");
            }

            Assert.AreEqual(0, errors, "only Error level fires LogErrorEvent");
        }
    }
}
