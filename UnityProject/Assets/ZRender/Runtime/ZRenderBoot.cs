// ZRenderBoot.cs —— 用命令行开关把当前运行的管线切到 ZRender（TASK-B-UNITY-02）。
//
// 为什么是"显式开关"而不是直接改项目默认管线：
//   A 轨的玩法/UI/相机像素差分用例全部基于 URP；把 GraphicsSettings 的默认管线改成
//   ZRender 会让整个游戏只剩一张 cube，并让既有 PlayMode 像素用例集体失效（跨任务
//   回归）。本任务要证明的是"ZRender 能真出画面"，所以做成启动参数：
//
//     ZFrame.exe -zrender      → 启用 ZRender 管线（屏幕上是 ZRender 的渲染结果）
//     ZFrame.exe               → 保持项目原配置（URP），行为零变化
//
// 门控（硬要求）：只有 D3D12 才启用。其它图形 API 明确回退 URP 并打日志——绝不在
// D3D11 下硬跑原生路径（会崩）。
//
// 切换时机：RuntimeInitializeOnLoadMethod(BeforeSplashScreen)，即第一帧渲染之前，
// 因此不构成"运行期跨管线热切换"（RenderPipelineSelector 禁止的是运行中热切）。

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZFrame.Render
{
    /// <summary>启动期管线开关（命令行 <c>-zrender</c>）。</summary>
    public static class ZRenderBoot
    {
        /// <summary>启用 ZRender 管线的命令行开关。</summary>
        public const string CommandLineFlag = "-zrender";

        private static ZRenderPipelineAsset _runtimeAsset;

        /// <summary>本次进程是否已把管线切到 ZRender（诊断/日志用）。</summary>
        public static bool IsActive { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void OnBeforeSplashScreen()
        {
            TryEnableFromCommandLine();
        }

        /// <summary>命令行带了 <c>-zrender</c> 就切换管线；否则什么都不做（保持 URP）。</summary>
        public static void TryEnableFromCommandLine()
        {
            if (IsActive || !HasCommandLineFlag())
            {
                return;
            }

            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Debug.LogWarning(
                    $"[ZRender] 收到 {CommandLineFlag}，但当前图形 API = {SystemInfo.graphicsDeviceType}：" +
                    "ZRender 只在 D3D12 下启用，已回退 URP（不改任何设置）。");
                return;
            }

            try
            {
                // 运行期新建资产实例：Player 里没有编辑器资产引用，CreateInstance 最稳。
                _runtimeAsset = ScriptableObject.CreateInstance<ZRenderPipelineAsset>();
                _runtimeAsset.name = "ZRenderPipeline (runtime)";

                // QualitySettings 的 per-level 绑定优先级高于 GraphicsSettings 的默认管线，
                // 两处都设才能真正切过去（项目当前三个档位都绑了 URP_*）。
                QualitySettings.renderPipeline = _runtimeAsset;
                GraphicsSettings.defaultRenderPipeline = _runtimeAsset;
                IsActive = true;

                Debug.Log($"[ZRender] 已启用自研管线（graphics API = {SystemInfo.graphicsDeviceType}，" +
                          "屏幕内容来自 ZRender 的共享纹理；原生未启用时会退回空场景）。");
            }
            catch (Exception error)
            {
                Debug.LogError($"[ZRender] 启用失败，保持 URP：{error.GetType().Name}: {error.Message}");
                IsActive = false;
            }
        }

        private static bool HasCommandLineFlag()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; ++i)
            {
                if (string.Equals(args[i], CommandLineFlag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
