#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Text;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Runtime debug panel v1 (IMGUI). Compiles only for Editor and
    /// Development builds. F1 toggles visibility.
    ///
    /// Bootstrapping: [RuntimeInitializeOnLoadMethod] self-creates a hidden
    /// DontDestroyOnLoad host — GameRoot (Core) never references this class,
    /// so no Core→View reverse dependency exists. Lives in ZFrame.View which
    /// already references Core.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DebugPanelRoot : MonoBehaviour
    {
        private const int FpsWindow = 120;
        private const int LogCapacity = 256;

        private static DebugPanelRoot instance;

        private readonly FpsCounter fps = new FpsCounter(FpsWindow);
        private readonly ZLogRingBuffer logs = new ZLogRingBuffer(LogCapacity);
        private readonly StringBuilder sb = new StringBuilder(512);

        private bool visible;
        private int tab;
        private LogLevel minLevel = LogLevel.Info;
        private Vector2 logScroll;
        private Vector2 poolScroll;
        private Vector2 serviceScroll;

        /// <summary>Self-bootstrap: creates the panel host after the first scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            if (instance != null)
            {
                return;
            }

            var host = new GameObject("~DebugPanelRoot");
            instance = host.AddComponent<DebugPanelRoot>();
            DontDestroyOnLoad(host);
        }

        private void Awake()
        {
            instance = this;
            logs.TimestampProvider = () => Time.realtimeSinceStartupAsDouble;
            ZLog.OnLog += logs.Enqueue;
        }

        private void OnDestroy()
        {
            ZLog.OnLog -= logs.Enqueue;
            if (instance == this)
            {
                instance = null;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                visible = !visible;
            }

            fps.Tick(Time.unscaledDeltaTime);
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            const float width = 460f;
            const float height = 360f;
            var rect = new Rect(16f, 16f, width, height);
            GUI.Box(rect, "ZFrame Debug (F1)");
            rect = new Rect(rect.x + 8f, rect.y + 24f, width - 16f, height - 32f);
            GUILayout.BeginArea(rect);

            tab = GUILayout.Toolbar(tab, new[] { "概览", "日志", "服务", "池" });
            switch (tab)
            {
                case 0: DrawOverview(); break;
                case 1: DrawLogs(); break;
                case 2: DrawServices(); break;
                case 3: DrawPools(); break;
            }

            GUILayout.EndArea();
        }

        private void DrawOverview()
        {
            GUILayout.Label($"FPS: {fps.Fps:F1}");
            GUILayout.Label($"平均帧: {fps.AverageMs:F2} ms");
            GUILayout.Label($"最差帧(窗口{FpsWindow}): {fps.WorstMs:F2} ms");
            GUILayout.Label($"日志缓冲: {logs.Count}/{logs.Capacity}");
        }

        private void DrawLogs()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("级别过滤:", GUILayout.Width(60f));
            if (GUILayout.Toggle(minLevel == LogLevel.Info, "Info", "Button"))
            {
                minLevel = LogLevel.Info;
            }

            if (GUILayout.Toggle(minLevel == LogLevel.Warn, "Warn", "Button"))
            {
                minLevel = LogLevel.Warn;
            }

            if (GUILayout.Toggle(minLevel == LogLevel.Error, "Error", "Button"))
            {
                minLevel = LogLevel.Error;
            }

            if (GUILayout.Button("清空"))
            {
                logs.Clear();
            }

            GUILayout.EndHorizontal();

            logScroll = GUILayout.BeginScrollView(logScroll);
            sb.Clear();
            foreach (var entry in logs.Entries(minLevel))
            {
                sb.Append($"[{entry.Time,8:F2}][{entry.Level}][{entry.Channel}] {entry.Message}\n");
            }

            GUILayout.Label(sb.ToString());
            GUILayout.EndScrollView();
        }

        private void DrawServices()
        {
            serviceScroll = GUILayout.BeginScrollView(serviceScroll);
            var any = false;
            foreach (var name in Services.GetAllRegistered())
            {
                any = true;
                GUILayout.Label(name);
            }

            if (!any)
            {
                GUILayout.Label("(无已注册服务)");
            }

            GUILayout.EndScrollView();
        }

        private void DrawPools()
        {
            poolScroll = GUILayout.BeginScrollView(poolScroll);
            var any = false;
            foreach (var stat in PoolRegistry.GetAllStats())
            {
                any = true;
                GUILayout.Label(
                    $"{stat.PoolName}: Active={stat.Stats.ActiveCount} Pooled={stat.Stats.PooledCount} " +
                    $"Peak={stat.Stats.PeakActive} Instantiate={stat.Stats.InstantiateCount}");
            }

            if (!any)
            {
                GUILayout.Label("(无已注册对象池)");
            }

            GUILayout.EndScrollView();
        }
    }
}
#endif
