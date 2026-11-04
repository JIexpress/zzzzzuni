// KernelCrowdMenu.cs —— 千人演练场人工体验入口（TASK-A-R9-13）。
// Editor-only（#if UNITY_EDITOR，同 GameSceneSetup 先例）；不落盘任何资产，
// 只在 Play Mode 里开关群体，供 Docs/Demo.md 体验项与人工目测使用。

#if UNITY_EDITOR
using Game.View.Effects;
using UnityEditor;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>Play Mode 下手工开启/关闭千人演练场的菜单。</summary>
    public static class KernelCrowdMenu
    {
        /// <summary>开启群体（并允许之后每次进入 SCN_Game 自动启用）。</summary>
        [MenuItem("ZFrame/千人演练场/开启（Play Mode）")]
        private static void Enable()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[A-R9-13] 请先进入 Play Mode 再开启千人演练场。");
                return;
            }

            KernelCrowdHost.AutoEnable = true;
            KernelCrowdHost.EnsureInstance().EnsureCrowd();
            KernelCrowdRenderer renderer = KernelCrowdHost.Instance?.Renderer;
            Debug.Log("[A-R9-13] 千人演练场已开启：entities=" +
                (KernelCrowdHost.Instance?.Driver?.EntityCount ?? 0) +
                ", shader=" + (renderer != null ? renderer.ResolvedShaderName : "none") +
                ", submitCalls=" + (renderer?.LastSubmitCalls ?? 0));
        }

        /// <summary>关闭群体并释放原生 World 句柄。</summary>
        [MenuItem("ZFrame/千人演练场/关闭")]
        private static void Disable()
        {
            KernelCrowdHost.AutoEnable = false;
            KernelCrowdHost.Instance?.DisposeCrowd();
            Debug.Log("[A-R9-13] 千人演练场已关闭（World 已 Dispose）。");
        }
    }
}
#endif
