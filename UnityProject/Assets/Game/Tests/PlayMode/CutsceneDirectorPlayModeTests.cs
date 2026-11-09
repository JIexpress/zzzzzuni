// CutsceneDirectorPlayModeTests.cs —— TASK-A-CUT-01：过场门闸 + 播片 + 人群让位。
// 口径：真 VideoPlayer 解码 Resources 里的占位片源（640×360/24fps/3s），
// 真 CrowdLODPipeline 实例验证「过场期间表示层被抑制」。
// 若 batchmode 下解码器不可用，prepare 断言会直接暴露（不静默跳过）。

using System.Collections;
using Game.View.Crowd;
using Game.View.Cutscene;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Render.Bindings;

namespace Game.View.PlayMode.Tests
{
    /// <summary>过场导演：门闸语义 / 播片推进 / 人群抑制三件事的场景级证据。</summary>
    public sealed class CutsceneDirectorPlayModeTests
    {
        [UnityTest]
        public IEnumerator Begin_PlaysTheClip_AndSuppressesTheCrowd()
        {
            using (var world = new ZKernelWorld())
            {
                var cameraGo = new GameObject("~CutsceneTestCamera");
                var pipeline = new CrowdLODPipeline(world, cameraGo.transform);

                CutsceneDirector director = CutsceneDirector.EnsureInstance();
                try
                {
                    Assert.IsFalse(CutsceneDirector.IsActive, "初始状态不该是过场中");

                    director.Begin();
                    Assert.IsTrue(CutsceneDirector.IsActive, "Begin 后门闸必须打开");
                    Assert.IsTrue(pipeline.IsSuppressed, "过场期间人群表示层必须被抑制");
                    Assert.AreEqual(1, CrowdLODPipeline.LiveCount, "抑制覆盖面应覆盖所有存活管线");

                    // 幂等：重复 Begin 不应重启播放器
                    director.Begin();
                    Assert.IsTrue(CutsceneDirector.IsActive);

                    Assert.IsNotNull(director.Clip, "片源必须从 Resources 加载成功");

                    float deadline = Time.unscaledTime + 10f;
                    while (!director.PlayerPrepared && Time.unscaledTime < deadline)
                    {
                        yield return null;
                    }

                    Assert.IsTrue(director.PlayerPrepared,
                        "VideoPlayer 未在 10s 内 prepare——batchmode 解码不可用或片源损坏");

                    long frameAtStart = director.Frame;
                    yield return new WaitForSeconds(0.6f);
                    Assert.Greater(director.Frame, frameAtStart,
                        $"过场必须真的在播：帧号 {frameAtStart} → {director.Frame}");

                    director.End();
                    Assert.IsFalse(CutsceneDirector.IsActive, "End 后门闸必须关闭");
                    Assert.IsFalse(pipeline.IsSuppressed, "End 后人群必须恢复");
                }
                finally
                {
                    director.End();
                    pipeline.Dispose();
                }

                Object.Destroy(cameraGo);
            }
        }
    }
}
