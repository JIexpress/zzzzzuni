using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Compilation;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// POST /compile —— AssetDatabase.Refresh 并等编译结束。
    /// 该 handler 会"延迟完成"（Deferred）：本帧只发起 Refresh，编译结束后再回填响应。
    /// 编译结束紧接着就是 domain reload（会杀后台线程），所以回填后要等响应真正写完再让 Reload 继续。
    /// </summary>
    internal static class DevCompile
    {
        const double TimeoutSeconds = 300.0;

        sealed class CompileRun
        {
            public DevHttpRequest Request;
            public Stopwatch Watch;
        }

        static readonly List<object> _errors = new List<object>();
        static int _warningCount;
        static CompileRun _run;
        static double _deadline;
        static bool _subscribed;

        public static bool Handle(DevHttpRequest request)
        {
            if (_run != null)
            {
                request.Complete(DevResponse.Error("compile already in progress", 409));
                return true;
            }

            _errors.Clear();
            _warningCount = 0;
            _run = new CompileRun { Request = request, Watch = Stopwatch.StartNew() };
            request.Deferred = true;
            _deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;

            Subscribe();

            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                Finish("AssetDatabase.Refresh 失败: " + e.GetType().Name + ": " + e.Message);
                return true;
            }

            // 没有脚本改动时不会进入编译，直接返回（例如只是 import 了资源）。
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                Finish(null);
            }

            return true;
        }

        static void Subscribe()
        {
            if (_subscribed) return;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            EditorApplication.update += OnTick;
            _subscribed = true;
        }

        static void Unsubscribe()
        {
            if (!_subscribed) return;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            EditorApplication.update -= OnTick;
            _subscribed = false;
        }

        static void OnTick()
        {
            if (_run == null) { Unsubscribe(); return; }
            if (EditorApplication.timeSinceStartup > _deadline) Finish("编译超时（>" + (int)TimeoutSeconds + "s）");
        }

        static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;
            for (int i = 0; i < messages.Length; i++)
            {
                CompilerMessage message = messages[i];
                if (message.type == CompilerMessageType.Error)
                {
                    _errors.Add(new Dictionary<string, object>
                    {
                        ["file"] = message.file,
                        ["line"] = message.line,
                        ["message"] = message.message,
                    });
                }
                else if (message.type == CompilerMessageType.Warning)
                {
                    _warningCount++;
                }
            }
        }

        static void OnCompilationFinished(object context)
        {
            Finish(null);
        }

        static void Finish(string infraError)
        {
            CompileRun run = _run;
            _run = null;
            Unsubscribe();
            if (run == null) return;

            var payload = new Dictionary<string, object>
            {
                ["ok"] = infraError == null && _errors.Count == 0,
                ["errors"] = _errors,
                ["warnings"] = _warningCount,
                ["ms"] = run.Watch.ElapsedMilliseconds,
            };

            if (infraError != null) payload["error"] = infraError;

            run.Request.Complete(DevResponse.Ok(payload));
            // 编译完几乎必然跟一次 domain reload，给它 300ms 让后台线程把响应写出去。
            run.Request.WaitForWritten(300);
        }
    }
}
