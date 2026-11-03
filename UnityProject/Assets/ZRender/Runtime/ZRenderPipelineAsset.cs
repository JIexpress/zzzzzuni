// ZRenderPipelineAsset.cs —— 自研 SRP 管线资产：在 Graphics Settings 中选用后，
// Unity 用其创建 ZRenderPipeline 实例。

using UnityEngine;
using UnityEngine.Rendering;

namespace ZFrame.Render
{
    /// <summary>自研 SRP 管线资产（ZRender），真渲染能力由后续任务逐步接入。</summary>
    public sealed class ZRenderPipelineAsset : RenderPipelineAsset
    {
        protected override RenderPipeline CreatePipeline()
        {
            return new ZRenderPipeline();
        }
    }
}
