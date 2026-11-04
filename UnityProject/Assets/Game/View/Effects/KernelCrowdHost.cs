// KernelCrowdHost.cs —— 千人演练场场景生命周期宿主（TASK-A-R9-13 建，A-R9-15 改为默认可见）。
// 不改任何既有场景文件 / 既有模块：照 GameAudioBindings 的
// RuntimeInitializeOnLoadMethod 自举先例，运行期建一个隐藏 DontDestroyOnLoad 宿主。
// A-R9-15：门控同时订阅 Unity 原生 SceneManager.sceneLoaded 与自定义
// SceneLoadedEvent，按「激活场景是否为 SCN_Game」自动 EnsureCrowd / DisposeCrowd，
// 实现「进 SCN_Game 即开箱可见」；AutoEnable 默认 true。
// 离开 SCN_Game（含回主菜单）即 DisposeCrowd()，释放原生 World 句柄
// （表上限 8，泄漏会耗尽槽位）。

using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体宿主：自举 + 场景门控 + 默认可见 + 显式启停。
    /// </summary>
    /// <remarks>
    /// A-R9-13 时 <see cref="AutoEnable"/> 默认 false（群体需菜单手动开），
    /// 理由是「有可能污染 A-R9-10 的 SCN_Game 相机差分」。A-R9-15 前置调研查明：
    /// A-R9-10 用 SceneManager.LoadSceneAsync 直连加载、不过 SceneLoader、不触发
    /// SceneLoadedEvent，按 A-R9-13 旧门控群体根本不会自动启用，故该顾虑从未真正
    /// 发生、也从未被量化。本任务改为按「激活场景名」门控的确定性自动挂载
    /// （覆盖全部加载路径），并把 A-R9-10 差分用例在 SetUp 显式关闭群体，
    /// 既有断言一字未动、可证伪。英雄侧玩法手感、场景布局、既有模块全部零改动。
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

        /// <summary>
        /// 是否在进入 SCN_Game 时自动启用（A-R9-15 起默认 <c>true</c> = 开箱可见）。
        /// 差分 / 像素证据用例可在 SetUp 临时置 false 并在 TearDown 复原，
        /// 把群体排除在自己的采样之外（不改任何既有断言）。
        /// </summary>
        public static bool AutoEnable { get; set; } = true;

        /// <summary>群体驱动（未启用时 null）。</summary>
        public KernelCrowdDriver Driver => _driver;

        /// <summary>批量绘制渲染器（未启用时 null）。</summary>
        public KernelCrowdRenderer Renderer => _renderer;

        /// <summary>群体是否已启用。</summary>
        public bool CrowdActive => _driver != null;

        /// <summary>
        /// 自举：仅在 Play Mode / Player 启动时触发（EditMode 测试不跑），
        /// 与既有模块零耦合。创建宿主并按当前激活场景门控一次。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            KernelCrowdHost host = EnsureInstance();
            host.ApplySceneGate(SceneManager.GetActiveScene().name);
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
            SceneManager.sceneLoaded += OnUnitySceneLoaded;
        }

        private void OnDestroy()
        {
            DisposeCrowd();
            SceneManager.sceneLoaded -= OnUnitySceneLoaded;
            _sceneToken?.Dispose();
            _sceneToken = null;
            if (_instance == this)
            {
                _instance = null;
            }
        }

        // 自定义事件总线路径（生产 boot chain：SceneLoader.FinishLoad fire）。
        private void OnSceneLoaded(SceneLoadedEvent evt)
        {
            ApplySceneGate(evt.SceneName);
        }

        // Unity 原生事件路径：覆盖所有加载方式（含测试里 SceneManager.LoadSceneAsync
        // 直连、no boot chain），是「进 SCN_Game 即开箱可见」的确定性依据。
        private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ApplySceneGate(scene.name);
        }

        /// <summary>
        /// 场景门控：激活场景为 SCN_Game 且 <see cref="AutoEnable"/> 且服务就绪
        /// → 自动 <see cref="EnsureCrowd"/>；其它场景（含回主菜单）→
        /// <see cref="DisposeCrowd"/> 释放句柄，杜绝跨场景/跨测试泄漏。
        /// </summary>
        private void ApplySceneGate(string sceneName)
        {
            if (!string.Equals(sceneName, GameSceneName, StringComparison.Ordinal))
            {
                DisposeCrowd();
                return;
            }

            if (AutoEnable && Services.TryGet(out ITickQueue ready) && ready != null)
            {
                EnsureCrowd();
            }
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
