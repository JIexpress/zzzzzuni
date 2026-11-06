// ZRenderFrameSyncTests.cs —— 帧同步（fence 双缓冲）的 EditMode 检验（TASK-B-UNITY-04）。
//
// 钉住的是 B-UNITY-03 验收点出的第三个遗留：**每帧 WaitIdle**（CPU 等 GPU 把本帧干完
// 才返回，帧间完全串行）。改法是"帧 N 只等帧 N-1 的 fence"，本帧的完成由下一帧回收。
//
// 这条用例直接量原生侧的等待耗时（SyncStats.avgWaitMs）与阻塞帧比例：
//   - WaitIdle 版：几乎每帧都会阻塞（刚提交就要等它完成），blocked ≈ frames；
//   - fence 版：等的是上一帧（隔了一整帧），绝大多数时候已完成，blocked 远小于一半。
// 结构性判据（阻塞帧比例）比"平均耗时 < X ms"稳：机器快慢只影响耗时，不影响"等的是
// 哪一帧"。耗时只作为报告数字打印，不做过紧的断言。
//
// 前提：真实设备（D3D12）。编辑器不是 D3D12 时 Skip，改由 Player 现场验证。

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Render;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZRenderFrameSyncTests
    {
        private const int FrameCount = 120;
        private const int Width = 960;
        private const int Height = 540;

        /// <summary>
        /// 两次 RenderFrame 之间的"宿主帧间隔"（毫秒）。必须留这一口气，否则测出来的是
        /// "CPU 能把 GPU 甩多远"（紧密循环里 CPU 永远比 GPU 快，两种同步模型都会每帧阻塞），
        /// 而不是真实帧循环里的等待。8ms ≈ 125fps 的帧间隔，比 GPU 干这点活（清屏 + 3 个
        /// cube）长得多——真实宿主里同样如此（中间还有 Unity 自己的一整帧）。
        /// </summary>
        private const int HostFrameGapMs = 8;


        [Test]
        public void RenderFrameLoop_WaitsOnPreviousFrameOnly()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("共享目标只在 D3D12 下可用（当前 " +
                    SystemInfo.graphicsDeviceType + "）；该路径由 Player 现场验证");
            }

            var pipeline = new ZRenderPipeline();
            try
            {
                Assert.IsTrue(pipeline.EnsureNativeTargetForSize(Width, Height),
                    "共享目标必须建成功（D3D12 下失败即真问题）");

                for (int frame = 0; frame < FrameCount; frame++)
                {
                    int result = ZrUnityTargetNative.RenderFrame();
                    Assert.AreEqual(ZrUnityTargetNative.Ok, result, $"第 {frame} 帧 RenderFrame 失败：{result}");
                    if (frame + 1 < FrameCount)
                    {
                        System.Threading.Thread.Sleep(HostFrameGapMs);
                    }
                }

                Assert.AreEqual(ZrUnityTargetNative.Ok,
                    ZrUnityTargetNative.GetSyncStats(out ZrUnitySyncStatsNative sync),
                    "SyncStats 必须可读");

                Assert.AreEqual((uint)FrameCount, sync.Frames, "统计帧数必须等于调用次数");
                Debug.Log($"[ZRenderSync] frames={sync.Frames} blocked={sync.BlockedFrames} " +
                          $"avgWait={sync.AvgWaitMs:F3}ms lastWait={sync.LastWaitMs:F3}ms " +
                          $"inFlight={sync.FramesInFlight}");

                // 结构性判据：等的是上一帧 → 绝大多数帧不该阻塞。
                // （WaitIdle 版在这里必红：它每帧都在等刚提交的那一帧。）
                Assert.Less(sync.BlockedFrames * 2u, sync.Frames,
                    $"阻塞帧必须少于一半（实际 {sync.BlockedFrames}/{sync.Frames}）——" +
                    "否则说明又退回「每帧等本帧」了");

                Assert.Less(sync.AvgWaitMs, 0.5, "平均等待不该接近整帧 GPU 时间");

                // 双缓冲 + 延迟一帧回读：中心像素必须真的读回来（证明回读没被同步改动弄丢）
                Assert.AreEqual(ZrUnityTargetNative.Ok,
                    ZrUnityTargetNative.GetTargetStats(out ZrUnityTargetStatsNative stats));
                Assert.Greater(stats.ReadbackCount, 0u, "回读必须真的发生（每 30 帧一次）");
                Assert.Greater(stats.RenderFrameCount, (uint)FrameCount - 1u);

                // 可读槽位必须在 [0, 1]
                Assert.AreEqual(ZrUnityTargetNative.Ok,
                    ZrUnityTargetNative.GetReadableTargetIndex(out uint readable));
                Assert.Less(readable, 2u, "可读槽位号必须落在双槽范围内");
            }
            finally
            {
                pipeline.DisposeForTests();
            }
        }
    }
}
