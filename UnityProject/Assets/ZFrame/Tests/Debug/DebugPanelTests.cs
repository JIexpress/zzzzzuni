using NUnit.Framework;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    public sealed class FpsCounterTests
    {
        [Test]
        public void Fps_AveragedOverWindow()
        {
            var counter = new FpsCounter(4);
            for (int i = 0; i < 10; i++)
            {
                counter.Tick(1.0 / 60.0); // 60 fps frames
            }

            Assert.AreEqual(4, counter.SampleCount);
            Assert.AreEqual(60.0, counter.Fps, 0.01);
            Assert.AreEqual(1000.0 / 60.0, counter.AverageMs, 0.01);
        }

        [Test]
        public void WorstMs_TracksLongestFrame()
        {
            var counter = new FpsCounter(8);
            counter.Tick(1.0 / 60.0);
            counter.Tick(0.25); // 250 ms hitch
            counter.Tick(1.0 / 60.0);

            Assert.AreEqual(250.0, counter.WorstMs, 0.001);
        }

        [Test]
        public void Window_Slides_OldSamplesDropOut()
        {
            var counter = new FpsCounter(2);
            counter.Tick(0.10);
            counter.Tick(0.10);
            counter.Tick(0.10);

            // Window holds only the last two 100 ms frames -> 10 fps.
            Assert.AreEqual(2, counter.SampleCount);
            Assert.AreEqual(10.0, counter.Fps, 0.01);
        }

        [Test]
        public void EmptyWindow_ReportsZero()
        {
            var counter = new FpsCounter(16);

            Assert.AreEqual(0, counter.Fps);
            Assert.AreEqual(0, counter.AverageMs);
            Assert.AreEqual(0, counter.WorstMs);
        }
    }

    public sealed class ZLogRingBufferTests
    {
        [Test]
        public void Enqueue_RollsOverOldest_WhenFull()
        {
            var buffer = new ZLogRingBuffer(3);
            buffer.Enqueue(1, LogLevel.Info, "c", "one");
            buffer.Enqueue(2, LogLevel.Info, "c", "two");
            buffer.Enqueue(3, LogLevel.Info, "c", "three");
            buffer.Enqueue(4, LogLevel.Info, "c", "four"); // overwrites "one"

            Assert.AreEqual(3, buffer.Count);
            var messages = new System.Collections.Generic.List<string>();
            foreach (var entry in buffer.Entries())
            {
                messages.Add(entry.Message);
            }

            CollectionAssert.AreEqual(new[] { "two", "three", "four" }, messages);
            Assert.AreEqual("four", buffer.Latest(0)?.Message);
            Assert.AreEqual("two", buffer.Latest(2)?.Message);
            Assert.IsNull(buffer.Latest(3));
        }

        [Test]
        public void Entries_FilterByMinimumLevel()
        {
            var buffer = new ZLogRingBuffer(8);
            buffer.Enqueue(1, LogLevel.Info, "c", "info");
            buffer.Enqueue(2, LogLevel.Warn, "c", "warn");
            buffer.Enqueue(3, LogLevel.Error, "c", "error");
            buffer.Enqueue(4, LogLevel.Info, "c", "info2");

            Assert.AreEqual(2, System.Linq.Enumerable.Count(buffer.Entries(LogLevel.Warn)));
            Assert.AreEqual(1, System.Linq.Enumerable.Count(buffer.Entries(LogLevel.Error)));
        }

        [Test]
        public void Clear_EmptiesBuffer()
        {
            var buffer = new ZLogRingBuffer(4);
            buffer.Enqueue(LogLevel.Info, "c", "x");
            buffer.Clear();

            Assert.AreEqual(0, buffer.Count);
        }
    }

    public sealed class ZLogMountPointTests
    {
        [TearDown]
        public void TearDown()
        {
            ZLog.Reset();
        }

        [Test]
        public void OnLog_FiresWithLevelChannelMessage()
        {
            var seen = new System.Collections.Generic.List<(LogLevel, string, string)>();
            void Handler(LogLevel level, string channel, string message) => seen.Add((level, channel, message));

            ZLog.OnLog += Handler;
            ZLog.EnableChannel("test");
            ZLog.Info("test", "hello");

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual(LogLevel.Info, seen[0].Item1);
            Assert.AreEqual("test", seen[0].Item2);
            Assert.AreEqual("hello", seen[0].Item3);
        }

        [Test]
        public void OnLog_FiresBeforeFiltering_CapturesFilteredMessages()
        {
            var fired = 0;
            void Handler(LogLevel level, string channel, string message) => fired++;

            ZLog.OnLog += Handler;
            // Channel not enabled -> filtered from sink, but mount point still fires.
            ZLog.Info("disabled-channel", "hidden");

            Assert.AreEqual(1, fired);
        }
    }
}
