// ZRenderPipeline.cs —— 自研管线入口，真渲染后续任务接入。
// v0：仅对每相机提交空的 ScriptableRenderContext（占位 Render），确保 SRP 壳可用。

using UnityEngine;
using UnityEngine.Rendering;

namespace ZFrame.Render
{
    /// <summary>自研渲染管线实例；由 <see cref="ZRenderPipelineAsset"/> 创建。</summary>
    public sealed class ZRenderPipeline : RenderPipeline
    {
        public ZRenderPipeline()
        {
            // 管线创建即触发原生库加载校验（失败快速抛出，带清晰信息）。
            _ = ZrNativeBridge.PluginInfo;
        }

        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            // 自研管线入口，真渲染后续任务接入。
            foreach (Camera camera in cameras)
            {
                context.Submit();
            }
        }
    }
}
