#if UNITY_EDITOR || DEVELOPMENT_BUILD || UNITY_INCLUDE_TESTS
using System;
using System.Text;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// 性能 HUD（方案 §5.7 第一步）：屏幕角落一层半透明文字叠加，
    /// 显示 FPS / DrawCall / SetPass / 三角面 / graphics driver 显存 / GPU 帧时间。
    ///
    /// 三道纪律：
    /// 1. **默认关闭**。开发包内 F3 切换，或用 <c>-zfhud</c> / <c>ZBF_HUD=1</c> 请求启动；
    ///    发布包连本类都不编译（<c>#if</c> 之外没有任何入口），运行期另有
    ///    <see cref="PerformanceHudGate"/> 挡一层，命令行/环境变量在非开发包一律无效。
    /// 2. **关闭时不采样**。ProfilerRecorder 只在 <see cref="SetVisible"/>(true) 时由
    ///    <see cref="PerformanceHudSampler.Start"/> 创建，隐藏时 Stop+Dispose；
    ///    Update 在不可见时除 F3 轮询外不做任何事。
    /// 3. **刷新节流 4 Hz**（<see cref="HudRefreshThrottle"/>），且只画 GUI.Label/GUI.Box，
    ///    不创建 Canvas / Graphic / Collider、不调用 Event.current.Use()，
    ///    因此不参与 UGUI 射线检测，不会挡住 UI 点击。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PerformanceHud : MonoBehaviour
    {
        /// <summary>切换键（仅开发包有效）。</summary>
        public const KeyCode ToggleKey = KeyCode.F3;

        /// <summary>FPS 平滑窗口：约 0.5s（60 fps 下 30 帧）。</summary>
        private const int FpsWindowSamples = 30;

        private const float Padding = 8f;
        private const float Width = 290f;
        private const float Height = 118f;

        private static PerformanceHud _instance;

        private readonly PerformanceHudSampler _sampler = new PerformanceHudSampler();
        private readonly FpsCounter _fps = new FpsCounter(FpsWindowSamples);
        private readonly HudRefreshThrottle _throttle = new HudRefreshThrottle();
        private readonly StringBuilder _builder = new StringBuilder(256);

        private bool _visible;
        private string _text = string.Empty;
        private GUIStyle _labelStyle;

        /// <summary>HUD 是否正在显示。</summary>
        public static bool IsVisible => _instance != null && _instance._visible;

        /// <summary>宿主对象是否已创建（开发包里常驻，但默认不可见）。</summary>
        public static bool IsHosted => _instance != null;

        /// <summary>测试用：当前是否需要采样（不可见时必须为 false）。</summary>
        public static bool IsSampling => _instance != null && _instance._sampler.IsRunning;

        /// <summary>当前显示的文本（测试/诊断用）。</summary>
        public static string CurrentText => _instance != null ? _instance._text : string.Empty;

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 游戏启动时自举。批处理（CI / 测试）不创建宿主；非开发包在门控处直接返回。
        /// 是否可见由 <c>-zfhud</c> / <c>ZBF_HUD</c> 决定，默认不可见。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            if (Application.isBatchMode) return;
            if (!PerformanceHudGate.IsAvailable(IsDevelopmentBuild)) return;

            PerformanceHud hud = EnsureHost();
            if (hud == null) return;

            if (PerformanceHudGate.ShouldStartVisible(
                    IsDevelopmentBuild, Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable))
            {
                hud.SetVisible(true);
            }
        }

        /// <summary>
        /// 生产切换入口：**门控在内部**，非开发包调用直接返回 false，不会创建宿主。
        /// </summary>
        public static bool TrySetVisible(bool visible)
        {
            if (!PerformanceHudGate.IsAvailable(IsDevelopmentBuild)) return false;
            PerformanceHud hud = EnsureHost();
            if (hud == null) return false;
            hud.SetVisible(visible);
            return true;
        }

        private static bool IsDevelopmentBuild()
        {
            return Debug.isDebugBuild;
        }

        private static PerformanceHud EnsureHost()
        {
            if (_instance != null) return _instance;

            var host = new GameObject("~ZFrame.PerformanceHud");
            host.hideFlags = HideFlags.HideInHierarchy;
            if (Application.isPlaying) DontDestroyOnLoad(host);

            _instance = host.AddComponent<PerformanceHud>();
            return _instance;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;
        }

        private void OnDestroy()
        {
            _sampler.Stop();
            if (_instance == this) _instance = null;
        }

        private void OnApplicationQuit()
        {
            _sampler.Stop();
        }

        // ---------------------------------------------------------------- 每帧

        private void Update()
        {
            // 键轮询本身不是"采样"：只是在等一个按键，成本是一次 GetKeyDown。
            if (Input.GetKeyDown(ToggleKey))
            {
                SetVisible(!_visible);
            }

            if (!_visible) return;

            // FPS 用 0.5s 滑动窗口平滑；文本按 4 Hz 节流重建（每帧拼字符串会自己拖帧）。
            _fps.Tick(Time.unscaledDeltaTime);
            if (_throttle.TryConsume(Time.unscaledTimeAsDouble))
            {
                RefreshText();
            }
        }

        private void SetVisible(bool visible)
        {
            if (_visible == visible) return;

            _visible = visible;
            if (visible)
            {
                _fps.Reset();
                _throttle.Reset();
                _sampler.Start();
                RefreshText();
            }
            else
            {
                _sampler.Stop();
                _text = string.Empty;
            }
        }

        private void RefreshText()
        {
            _builder.Clear();
            _builder.Append("FPS  ").Append(HudTextFormat.Fps(_fps.Fps));
            _builder.Append("  (").Append(HudTextFormat.Milliseconds(_fps.AverageMs)).AppendLine(")");
            _builder.Append("Draw  ").Append(HudTextFormat.Count(_sampler.DrawCalls)).AppendLine();
            _builder.Append("SetPass  ").Append(HudTextFormat.Count(_sampler.SetPassCalls)).AppendLine();
            _builder.Append("Tris  ").Append(HudTextFormat.Count(_sampler.Triangles)).AppendLine();
            _builder.Append("VRAM  ").Append(HudTextFormat.Megabytes(_sampler.GraphicsMemoryBytes)).AppendLine();
            _builder.Append("GPU  ").Append(HudTextFormat.Milliseconds(_sampler.GpuMilliseconds));
            _text = _builder.ToString();
        }

        // ---------------------------------------------------------------- 呈现

        private void OnGUI()
        {
            if (!_visible) return;

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    richText = false,
                    alignment = TextAnchor.UpperLeft,
                };
                _labelStyle.normal.textColor = Color.white;
            }

            // 画在最上层，但仍然只是 Label/Box：没有可交互控件，不进 EventSystem 射线。
            GUI.depth = -1000;

            var rect = new Rect(Padding, Screen.height - Height - Padding, Width, Height);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, rect.height - 8f), _text, _labelStyle);
        }

        // ---------------------------------------------------------------- 测试接缝
        // 外层 #if 已把整个类限制在 Editor / 开发构建 / 测试程序集内，
        // 下面这些 internal 接缝在发布包里随类型一起消失。

        /// <summary>
        /// 测试专用：注入"是否开发包"判据。发布包（非开发构建）里连本类型都不编译，
        /// 该入口同样不存在；开发包本来就允许 HUD，不增加攻击面。
        /// </summary>
        internal static bool TrySetVisibleForTests(Func<bool> isDevelopmentBuild, bool visible)
        {
            if (!PerformanceHudGate.IsAvailable(isDevelopmentBuild)) return false;
            PerformanceHud hud = EnsureHost();
            if (hud == null) return false;
            hud.SetVisible(visible);
            return true;
        }

        /// <summary>测试收尾：销毁宿主并清空静态引用（同时停采样）。</summary>
        internal static void ShutdownForTests()
        {
            if (_instance != null)
            {
                _instance._sampler.Stop();
                UnityEngine.Object.DestroyImmediate(_instance.gameObject);
            }

            _instance = null;
        }

        /// <summary>测试用：宿主 GameObject（未创建时为 null）。</summary>
        internal static GameObject HostForTests => _instance != null ? _instance.gameObject : null;
    }
}
#endif
