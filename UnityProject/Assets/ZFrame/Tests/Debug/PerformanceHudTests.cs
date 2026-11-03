using System;
using NUnit.Framework;
using UnityEngine;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// 性能 HUD 的纯逻辑与门控（TASK-A-GPU-01）。
    ///
    /// 这组用例的重点不是"HUD 能画出来"，而是三件容易假成功的事：
    /// 1) 门控的牙：发布包 + <c>-zfhud</c> + <c>ZBF_HUD=1</c> 必须仍是拒绝——
    ///    把 <c>isDevelopmentBuild</c> 判断删掉，<see cref="PerformanceHudGateTests.ReleaseBuild_RefusesEveryEnableRequest"/> 立刻变红；
    /// 2) 懒采样：隐藏时 recorder 数量必须回到基线（不是"采样了但不画"）；
    /// 3) 节流落在 2-4 Hz，且 0 值格式化不会吐 NaN。
    /// </summary>
    public sealed class PerformanceHudGateTests
    {
        [Test]
        public void DevelopmentBuild_IsDefaultOff()
        {
            Assert.IsTrue(PerformanceHudGate.IsAvailable(() => true));
            Assert.IsFalse(
                PerformanceHudGate.ShouldStartVisible(() => true, new string[0], _ => null),
                "HUD must be off by default even in a development build");
            Assert.IsFalse(
                PerformanceHudGate.ShouldStartVisible(() => true, null, _ => null),
                "null command line must behave like no arguments");
        }

        [Test]
        public void DevelopmentBuild_StartsFromCliOrEnvironment()
        {
            Assert.IsTrue(
                PerformanceHudGate.ShouldStartVisible(() => true, new[] { "-zfhud" }, _ => null),
                "CLI -zfhud must request the HUD");
            Assert.IsTrue(
                PerformanceHudGate.ShouldStartVisible(() => true, new[] { "-ZFHUD" }, _ => null),
                "CLI flag must be case-insensitive");

            Assert.IsTrue(
                PerformanceHudGate.ShouldStartVisible(() => true, new string[0], name =>
                    name == PerformanceHudGate.EnvEnable ? "1" : null));
            Assert.IsTrue(
                PerformanceHudGate.ShouldStartVisible(() => true, new string[0], name =>
                    name == PerformanceHudGate.EnvEnable ? " TRUE " : null));
            Assert.IsTrue(
                PerformanceHudGate.ShouldStartVisible(() => true, new string[0], name =>
                    name == PerformanceHudGate.EnvEnable ? "on" : null));

            Assert.IsFalse(
                PerformanceHudGate.ShouldStartVisible(() => true, new string[0], name =>
                    name == PerformanceHudGate.EnvEnable ? "0" : null),
                "ZBF_HUD=0 must not enable the HUD");
        }

        /// <summary>
        /// **门控的牙**：发布包（isDebugBuild=false）即使带了所有开启参数也必须拒绝。
        /// 变异：删掉 <see cref="PerformanceHudGate.ShouldStartVisible"/> 里的 IsAvailable 判断，
        /// 本用例两个断言都会红。
        /// </summary>
        [Test]
        public void ReleaseBuild_RefusesEveryEnableRequest()
        {
            Func<string, string> envOn = name => name == PerformanceHudGate.EnvEnable ? "1" : null;

            Assert.IsFalse(
                PerformanceHudGate.ShouldStartVisible(() => false, new[] { "-zfhud" }, envOn),
                "release build + CLI + env must still be refused: no performance backdoor in shipping builds");
            Assert.IsFalse(
                PerformanceHudGate.ShouldStartVisible(() => false, new[] { "-zfhud" }, _ => null));
            Assert.IsFalse(
                PerformanceHudGate.ShouldStartVisible(() => false, new string[0], envOn));
            Assert.IsFalse(PerformanceHudGate.IsAvailable(() => false), "F3 toggle path must be closed in release");
            Assert.IsFalse(PerformanceHudGate.IsAvailable(null), "null probe must fail closed");
        }

        [Test]
        public void EnvironmentValue_Parsing()
        {
            Assert.IsTrue(PerformanceHudGate.IsTruthy("1"));
            Assert.IsTrue(PerformanceHudGate.IsTruthy("yes"));
            Assert.IsFalse(PerformanceHudGate.IsTruthy(""));
            Assert.IsFalse(PerformanceHudGate.IsTruthy(null));
            Assert.IsFalse(PerformanceHudGate.IsTruthy("0"));
            Assert.IsFalse(PerformanceHudGate.IsTruthy("false"));
            Assert.IsFalse(PerformanceHudGate.IsTruthy("random"));
        }
    }

    public sealed class HudTextFormatTests
    {
        [Test]
        public void Count_CompactsThousandsAndMillions()
        {
            Assert.AreEqual("0", HudTextFormat.Count(0));
            Assert.AreEqual("999", HudTextFormat.Count(999));
            Assert.AreEqual("1.0k", HudTextFormat.Count(1000));
            Assert.AreEqual("1.2k", HudTextFormat.Count(1234));
            Assert.AreEqual("999.9k", HudTextFormat.Count(999900));
            Assert.AreEqual("1.0M", HudTextFormat.Count(999999), "999,999 must roll over to 1.0M, not print 1000.0k");
            Assert.AreEqual("1.0M", HudTextFormat.Count(1000000));
            Assert.AreEqual("1.2M", HudTextFormat.Count(1234567));
            Assert.AreEqual("0", HudTextFormat.Count(-5), "negative counts must clamp, never render -5");
        }

        [Test]
        public void Megabytes_ConvertsFromBytes_AndNeverPrintsNaN()
        {
            Assert.AreEqual("0.0 MB", HudTextFormat.Megabytes(0));
            Assert.AreEqual("1.0 MB", HudTextFormat.Megabytes(1048576));
            Assert.AreEqual("1.5 MB", HudTextFormat.Megabytes(1572864));
            Assert.AreEqual("0.0 MB", HudTextFormat.Megabytes(-1));

            string zero = HudTextFormat.Megabytes(0);
            StringAssert.DoesNotContain("NaN", zero);
            StringAssert.DoesNotContain("Infinity", zero);
        }

        [Test]
        public void Milliseconds_AndFps_HandleZeroAndUnavailable()
        {
            Assert.AreEqual("0.00 ms", HudTextFormat.Milliseconds(0.0));
            Assert.AreEqual("8.21 ms", HudTextFormat.Milliseconds(8.214));

            Assert.AreEqual(HudTextFormat.Unavailable, HudTextFormat.Milliseconds(-1.0));
            Assert.AreEqual(HudTextFormat.Unavailable, HudTextFormat.Milliseconds(double.NaN));
            Assert.AreEqual(HudTextFormat.Unavailable, HudTextFormat.Milliseconds(double.PositiveInfinity));

            Assert.AreEqual("0.0", HudTextFormat.Fps(0.0));
            Assert.AreEqual("59.9", HudTextFormat.Fps(59.94));
            Assert.AreEqual(HudTextFormat.Unavailable, HudTextFormat.Fps(double.NaN));
        }
    }

    public sealed class HudRefreshThrottleTests
    {
        [Test]
        public void Refresh_IsThrottledToFourHertz()
        {
            var throttle = new HudRefreshThrottle();

            Assert.IsTrue(throttle.TryConsume(0.0), "first refresh must always happen");
            Assert.IsFalse(throttle.TryConsume(0.10));
            Assert.IsFalse(throttle.TryConsume(0.24));
            Assert.IsTrue(throttle.TryConsume(0.25), "4 Hz: second refresh at 250 ms");
            Assert.IsFalse(throttle.TryConsume(0.30));
            Assert.IsTrue(throttle.TryConsume(0.50));
        }

        [Test]
        public void Interval_IsKeptWithinTwoToFourHertz()
        {
            var throttle = new HudRefreshThrottle();
            Assert.AreEqual(0.25, throttle.IntervalSeconds, 1e-6);
            Assert.GreaterOrEqual(1.0 / throttle.IntervalSeconds, 2.0, "must not refresh slower than 2 Hz");
            Assert.LessOrEqual(1.0 / throttle.IntervalSeconds, 4.0 + 1e-9, "must not refresh faster than 4 Hz");

            Assert.DoesNotThrow(() => new HudRefreshThrottle(0.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HudRefreshThrottle(0.1f),
                "faster than 4 Hz would distort the measurement");
            Assert.Throws<ArgumentOutOfRangeException>(() => new HudRefreshThrottle(0.75f),
                "slower than 2 Hz is outside the promised range");
            Assert.Throws<ArgumentOutOfRangeException>(() => new HudRefreshThrottle(float.NaN));
        }

        [Test]
        public void Reset_AndClockRollback_DoNotWedgeTheThrottle()
        {
            var throttle = new HudRefreshThrottle();
            Assert.IsTrue(throttle.TryConsume(10.0));
            Assert.IsFalse(throttle.TryConsume(10.1));

            throttle.Reset();
            Assert.IsTrue(throttle.TryConsume(10.1), "reset must allow an immediate refresh");

            // 时钟回退（编辑器重启计时 / 平台异常）不能把刷新永久卡死。
            Assert.IsTrue(throttle.TryConsume(0.0));
        }
    }

    public sealed class PerformanceHudTests
    {
        [SetUp]
        public void SetUp()
        {
            PerformanceHud.ShutdownForTests();
        }

        [TearDown]
        public void TearDown()
        {
            PerformanceHud.ShutdownForTests();
        }

        /// <summary>
        /// 懒启动的两个方向都要证：开 → recorder 真的创建；关 → 真的释放。
        /// </summary>
        [Test]
        public void Hidden_DoesNotSample_Visible_StartsAndStops()
        {
            int baseline = PerformanceHudSampler.LiveSamplerCount;

            Assert.IsFalse(PerformanceHud.IsHosted, "no host before the gate allows it");
            Assert.IsFalse(PerformanceHud.IsSampling);

            Assert.IsTrue(PerformanceHud.TrySetVisibleForTests(() => true, true));
            Assert.IsTrue(PerformanceHud.IsVisible);
            Assert.IsTrue(PerformanceHud.IsSampling, "showing the HUD must start the recorders");
            Assert.AreEqual(baseline + 1, PerformanceHudSampler.LiveSamplerCount);
            Assert.IsNotEmpty(PerformanceHud.CurrentText, "first refresh must produce text");

            Assert.IsTrue(PerformanceHud.TrySetVisibleForTests(() => true, false));
            Assert.IsFalse(PerformanceHud.IsVisible);
            Assert.IsFalse(PerformanceHud.IsSampling, "hiding the HUD must stop sampling");
            Assert.AreEqual(baseline, PerformanceHudSampler.LiveSamplerCount, "recorders must be disposed while hidden");
            Assert.IsEmpty(PerformanceHud.CurrentText);
        }

        /// <summary>发布包分支不仅不显示，连宿主都不创建（ProfilerRecorder 永远不会出现）。</summary>
        [Test]
        public void ReleaseBuild_RefusesToShow_AndCreatesNoHost()
        {
            Assert.IsFalse(PerformanceHud.TrySetVisibleForTests(() => false, true),
                "non-development build must refuse the HUD");
            Assert.IsFalse(PerformanceHud.IsHosted);
            Assert.IsFalse(PerformanceHud.IsVisible);
            Assert.IsFalse(PerformanceHud.IsSampling);
        }

        [Test]
        public void ToggleKey_IsF3()
        {
            Assert.AreEqual(KeyCode.F3, PerformanceHud.ToggleKey);
        }

        /// <summary>
        /// 不挡 UI 点击的机检侧写：宿主上没有 Canvas / Collider / 子物件，
        /// 意味着没有 GraphicRaycaster 可挂载，也没有物理射线可命中。
        /// </summary>
        [Test]
        public void HudHost_HasNoRaycastSurfaces()
        {
            Assert.IsTrue(PerformanceHud.TrySetVisibleForTests(() => true, true));

            GameObject host = PerformanceHud.HostForTests;
            Assert.IsNotNull(host);
            Assert.IsNull(host.GetComponent<Canvas>(), "HUD must not own a Canvas (no GraphicRaycaster host)");
            Assert.IsNull(host.GetComponent<Collider>(), "HUD must not own a Collider (no physics raycast blocker)");
            Assert.AreEqual(0, host.transform.childCount, "HUD must stay a single empty GameObject");
            Assert.AreEqual(HideFlags.HideInHierarchy, host.hideFlags);
        }
    }
}
