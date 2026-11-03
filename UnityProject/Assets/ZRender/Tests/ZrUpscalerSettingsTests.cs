// ZrUpscalerSettingsTests.cs —— 超分设置封装实证（TASK-B-R6-03）：
// ① 真 ABI 往返（zrender.dll 已入库 Assets/Plugins/ZRender，ADR-004）；
// ② "原生不可用"桩下的降级行为：dll 缺失（DllNotFoundException）/ 符号缺失
//    （EntryPointNotFoundException）/ 原生错误码（RANGE/SIZE）一律返回不可用，不抛异常。

using System;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    /// <summary>"原生不可用"桩：每个方法都抛注入的异常（模拟 dll/符号缺失）。</summary>
    internal sealed class UnavailableUpscalerApi : IZrUpscalerApi
    {
        private readonly Exception _exception;

        public UnavailableUpscalerApi(Exception exception)
        {
            _exception = exception;
        }

        public int SetPreferred(int preferred) => throw _exception;

        public int GetActive() => throw _exception;

        public int QueryCaps(out ZrUpscalerCapsNative caps) => throw _exception;
    }

    /// <summary>"原生拒绝"桩：不抛异常，按错误码语义拒绝（RANGE / SIZE）。</summary>
    internal sealed class RejectingUpscalerApi : IZrUpscalerApi
    {
        public int SetPreferred(int preferred) => -2; // ZR_UPSCALER_ERR_RANGE

        public int GetActive() => 0; // 越界值：应视为查询失败

        public int QueryCaps(out ZrUpscalerCapsNative caps)
        {
            caps = default;
            return -3; // ZR_UPSCALER_ERR_SIZE
        }
    }

    [TestFixture]
    public class ZrUpscalerSettingsTests
    {
        // ---------- 真 ABI（dll 在位） ----------

        [Test]
        public void RealApi_QueryCaps_ReportsAvailable()
        {
            var settings = new ZrUpscalerSettings();
            UpscalerCapsInfo caps = settings.QueryCaps();

            Assert.IsTrue(caps.Available, "zrender.dll 在位时能力查询应成功（其余字段才有意义）");
            Assert.GreaterOrEqual(caps.PreferredBackend, 0, "偏好后端应为 ZrUpscalerPreferred（0=Auto）");
            Assert.LessOrEqual(caps.PreferredBackend, 5);
            Assert.GreaterOrEqual(caps.ActiveBackend, 1, "生效后端应解析到 1..5");
            Assert.LessOrEqual(caps.ActiveBackend, 5);
            Assert.IsTrue(settings.IsAvailable);
        }

        [Test]
        public void RealApi_TrySetPreferred_Auto_Succeeds_AndReflectsInCaps()
        {
            var settings = new ZrUpscalerSettings();

            Assert.IsTrue(settings.TrySetPreferred(0), "preferred=Auto 应被原生接受");

            UpscalerCapsInfo caps = settings.QueryCaps();
            Assert.AreEqual(0, caps.PreferredBackend, "caps 应回读偏好 = Auto");
        }

        [Test]
        public void RealApi_TrySetPreferred_OutOfRange_ReturnsFalseWithoutThrow()
        {
            var settings = new ZrUpscalerSettings();

            Assert.IsFalse(settings.TrySetPreferred(999), "越界偏好应被原生以 RANGE 拒绝");
            Assert.IsFalse(settings.TrySetPreferred(-1));
        }

        [Test]
        public void RealApi_TryGetActiveBackend_InRange1To5()
        {
            var settings = new ZrUpscalerSettings();

            Assert.IsTrue(settings.TryGetActiveBackend(out int active));
            Assert.GreaterOrEqual(active, 1);
            Assert.LessOrEqual(active, 5);
        }

        // ---------- 桩：dll 缺失 / 符号缺失 → 优雅降级，不抛 ----------

        [Test]
        public void Stub_DllMissing_AllOperationsDegradeWithoutThrow()
        {
            var settings = new ZrUpscalerSettings(
                new UnavailableUpscalerApi(new DllNotFoundException("zrender")));

            Assert.IsFalse(settings.IsAvailable, "dll 缺失必须降级为不可用");

            UpscalerCapsInfo caps = settings.QueryCaps();
            Assert.IsFalse(caps.Available, "能力查询应返回不可用默认值而非抛异常");

            Assert.IsFalse(settings.TrySetPreferred(0), "偏好设置应返回 false 而非抛异常");
            Assert.IsFalse(settings.TryGetActiveBackend(out int active), "生效后端查询应返回 false 而非抛异常");
            Assert.AreEqual(0, active, "失败时 out 值应为 0（1..5 之外）");
        }

        [Test]
        public void Stub_SymbolMissing_AllOperationsDegradeWithoutThrow()
        {
            var settings = new ZrUpscalerSettings(
                new UnavailableUpscalerApi(new EntryPointNotFoundException("ZR_Upscaler_QueryCaps")));

            Assert.IsFalse(settings.IsAvailable, "符号缺失（旧 dll）必须降级为不可用");
            Assert.IsFalse(settings.QueryCaps().Available);
            Assert.IsFalse(settings.TrySetPreferred(4));
            Assert.IsFalse(settings.TryGetActiveBackend(out _));
        }

        // ---------- 桩：原生错误码拒绝 ----------

        [Test]
        public void Stub_NativeRangeError_TrySetPreferred_ReturnsFalseWithoutThrow()
        {
            var settings = new ZrUpscalerSettings(new RejectingUpscalerApi());

            Assert.IsFalse(settings.TrySetPreferred(0), "原生返回 -2（RANGE）时应折算为 false");
        }

        [Test]
        public void Stub_NativeSizeError_QueryCaps_ReturnsUnavailableWithoutThrow()
        {
            var settings = new ZrUpscalerSettings(new RejectingUpscalerApi());

            UpscalerCapsInfo caps = settings.QueryCaps();
            Assert.IsFalse(caps.Available, "原生返回 -3（SIZE=ABI 不符）时应折算为不可用");

            Assert.IsFalse(settings.IsAvailable);
            Assert.IsFalse(settings.TryGetActiveBackend(out _), "GetActive 返回 0（越界）应折算为 false");
        }
    }
}
