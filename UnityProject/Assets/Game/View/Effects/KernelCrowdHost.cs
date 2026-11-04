// KernelCrowdHost.cs —— 千人演练场场景生命周期宿主（TASK-A-R9-13）。
// 不改任何既有场景文件 / 既有模块：照 GameAudioBindings 的
// RuntimeInitializeOnLoadMethod 自举先例，运行期建一个隐藏 DontDestroyOnLoad 宿主。
// 场景门控：离开 SCN_Game（SceneLoadedEvent 指向别的场景）即 DisposeCrowd()，
// 释放原生 World 句柄（表上限 8，泄漏会耗尽槽位）。

using System;
using UnityEngine;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体宿主：自举 + 场景门控 + 显式启停。
    /// </summary>
    /// <remarks>
    /// <see cref="AutoEnable"/> 默认 <c>false</c>：群体默认不自动出现在场景里，
    /// 以免干扰既有 PlayMode 像素证据用例（A-R9-10 的 SCN_Game 相机差分）。
    /// 人工体验走 Editor 菜单「ZFrame/千人演练场/开启（Play Mode）」，
    /// PlayMode 测试显式调用 <see cref="EnsureCrowd"/>。这样英雄侧玩法手感、
    /// 场景布局、既有模块全部零改动。
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class KernelCrowdHost : MonoBehaviour
    {
        /// <summary>演练场只在 SCN_Game 场景启用。</summary>
        public const string GameSceneName = "SCN_Game";

        private const string HostName = "~KernelCrowdHost";

        private static KernelCrowdHost _instance;

        private IDisposable _sceneToken;
        private KernelCrowdDriver _driver;
        private KernelCrowdRenderer _renderer;

        /// <summary>共享宿主（自动自举后非 null）。</summary>
        public static KernelCrowdHost Instance => _instance;

        /// <summary>是否在进入 SCN_Game 时自动启用（默认 false，见类 remarks）。</summary>
        public static bool AutoEnable { get; set; }

        /// <summary>群体驱动（未启用时 null）。</summary>
        public KernelCrowdDriver Driver => _driver;

        /// <summary>批量绘制渲染器（未启用时 null）。</summary>
        public KernelCrowdRenderer Renderer => _renderer;

        /// <summary>群体是否已启用。</summary>
        public bool CrowdActive => _driver != null;

        /// <summary>
        /// 自举：仅在 Play Mode / Player 启动时触发（EditMode 测试不跑），
        /// 与既有模块零耦合。创建宿主但不启用群体。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            EnsureInstance();
        }

        /// <summary>返回共享宿主，缺失时创建（幂等；测试可直接调用）。</summary>
        public static KernelCrowdHost EnsureInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var host = new GameObject(HostName);
            KernelCrowdHost component = host.AddComponent<KernelCrowdHost>();
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            _instance = component;
            return component;
        }

        private void Awake()
        {
            _sceneToken = EventBus.Subscribe<SceneLoadedEvent>(OnSceneLoaded);
        }

        private void OnDestroy()
        {
            DisposeCrowd();
            _sceneToken?.Dispose();
            _sceneToken = null;
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnSceneLoaded(SceneLoadedEvent evt)
        {
            if (string.Equals(evt.SceneName, GameSceneName, StringComparison.Ordinal))
            {
                if (AutoEnable)
                {
                    EnsureCrowd();
                }

                return;
            }

            // 离开 SCN_Game（含回主菜单）→ 场景卸载语义：释放句柄，不泄漏。
            DisposeCrowd();
        }

        /// <summary>创建单 World + 驱动 + 渲染器并注册到 ITickQueue（幂等）。</summary>
        public void EnsureCrowd()
        {
            if (_driver != null)
            {
                return;
            }

            ITickQueue queue = Services.Get<ITickQueue>();
            _driver = new KernelCrowdDriver();
            _driver.Initialize(queue);
            _renderer = new KernelCrowdRenderer();
            _renderer.Initialize(queue, _driver.Matrices, _driver.EntityCount);
        }

        /// <summary>注销 tick、释放渲染资源与原生 World（幂等）。</summary>
        public void DisposeCrowd()
        {
            _renderer?.Dispose();
            _renderer = null;
            _driver?.Dispose();
            _driver = null;
        }
    }
}
