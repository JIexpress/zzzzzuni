using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// POST /screenshot[?view=auto|camera|gameview|scene&amp;name=] —— 真出 PNG 文件。
    ///
    /// source 与实际出图路径严格一致（A-DEV-01 那条"不许黑图冒充"的同一原则）：
    /// - <c>gameview</c>：Play Mode 下用 <see cref="ScreenCapture.CaptureScreenshot"/> 抓 GameView，异步落盘后回填；
    ///   编辑模式没有 GameView 渲染输出 → 明确 503，<b>不再静默回退到 camera 却写着 gameview</b>。
    /// - <c>camera</c>：相机离屏渲染（编辑/播放都可用）。
    /// - <c>scene</c>：SceneView 相机。
    /// - <c>auto</c>：播放中先试 gameview，失败才回退 camera，并且<b>如实把回退原因写进响应</b>。
    /// 任何一种路径拿到纯色（典型黑图）都不写文件、明确报不可用。
    /// </summary>
    internal static class DevScreenshot
    {
        /// <summary>CaptureScreenshot 是"下一帧末"异步落盘，给足等待上限；超时按不可用处理。</summary>
        const double GameViewTimeoutSeconds = 8.0;

        const string RawFolder = "_raw";

        sealed class PendingCapture
        {
            public DevHttpRequest Request;
            public string Name;
            public string TempPath;
            public string FinalPath;
            public bool AllowCameraFallback;
            public double Deadline;
        }

        static readonly List<PendingCapture> _pending = new List<PendingCapture>();
        static bool _ticking;
        static long _sequence;

        public static bool Handle(DevHttpRequest request)
        {
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                request.Complete(DevResponse.Error("method not allowed: " + request.Method + " /screenshot（用 POST）", 405));
                return true;
            }

            string view = (request.GetQuery("view", "auto") ?? "auto").Trim().ToLowerInvariant();
            string name = Sanitize(request.GetQuery("name", null));

            if (view == "scene" || view == "sceneview")
            {
                CompleteWithTexture(request, CaptureSceneView, "sceneview");
                return true;
            }

            if (view == "camera" || view == "cam")
            {
                CompleteWithTexture(request, CaptureCamera, "camera");
                return true;
            }

            if (view == "game" || view == "gameview")
            {
                return StartGameView(request, name, false);
            }

            if (view == "auto" || view.Length == 0)
            {
                return StartGameView(request, name, true);
            }

            request.Complete(DevResponse.Error("未知 view '" + view + "'，只支持 auto|camera|gameview|scene"));
            return true;
        }

        // ---------------------------------------------------------------- gameview（异步）

        static bool StartGameView(DevHttpRequest request, string name, bool allowCameraFallback)
        {
            if (!Application.isPlaying)
            {
                if (allowCameraFallback)
                {
                    // auto 在编辑模式没有 gameview 可抓：直接走 camera，并把"为什么不是 gameview"写清楚。
                    string cameraError;
                    Texture2D cameraTexture = CaptureCamera(out cameraError);
                    if (cameraTexture != null)
                    {
                        try
                        {
                            WriteTexture(request, cameraTexture, name, "camera",
                                "auto 未用 gameview：当前 playing=false，GameView 只有 Play Mode 才有渲染输出");
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(cameraTexture);
                        }

                        return true;
                    }

                    request.Complete(DevResponse.Error("截图不可用: playing=false 且 camera 也失败（" + cameraError + "）", 503));
                    return true;
                }

                // 编辑模式没有 GameView 渲染输出。这里必须"说不"，不许回退到 camera 还写着 gameview。
                var payload = new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["source"] = "gameview",
                    ["error"] = "gameview 截图不可用：当前 playing=false，GameView 只有 Play Mode 才有渲染输出",
                    ["hint"] = "用 ?view=auto（播放中自动选 gameview，编辑模式走 camera）或 ?view=camera",
                };
                request.Complete(new DevResponse { Status = 503, Body = DevJson.Serialize(payload) });
                return true;
            }

            string directory = ScreenshotDirectory();
            string rawDirectory = Path.Combine(directory, RawFolder);
            string tempPath;
            string finalPath;
            try
            {
                Directory.CreateDirectory(directory);
                Directory.CreateDirectory(rawDirectory);
                _sequence++;
                tempPath = Path.Combine(rawDirectory, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + _sequence + ".png");
                finalPath = Path.Combine(directory, name + ".png");
                ScreenCapture.CaptureScreenshot(tempPath);
            }
            catch (Exception e)
            {
                request.Complete(DevResponse.Error("gameview 截图失败: " + e.GetType().Name + ": " + e.Message, 503));
                return true;
            }

            _pending.Add(new PendingCapture
            {
                Request = request,
                Name = name,
                TempPath = tempPath,
                FinalPath = finalPath,
                AllowCameraFallback = allowCameraFallback,
                Deadline = EditorApplication.timeSinceStartup + GameViewTimeoutSeconds,
            });
            request.Deferred = true;

            if (!_ticking)
            {
                EditorApplication.update += Tick;
                _ticking = true;
            }

            return true;
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            bool settledAny = false;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingCapture pending = _pending[i];

                Texture2D texture;
                long bytes;
                string readError;
                if (TryLoadCapture(pending.TempPath, out texture, out bytes, out readError))
                {
                    Color32 uniformColor;
                    if (IsUniform(texture, out uniformColor))
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                        _pending.RemoveAt(i);
                        settledAny = true;
                        FailGameView(pending, "GameView 截图落盘但为纯色 (" + uniformColor.r + "," + uniformColor.g + "," + uniformColor.b + ")，不可用",
                            pending.TempPath);
                        continue;
                    }

                    int width = texture.width;
                    int height = texture.height;
                    UnityEngine.Object.DestroyImmediate(texture);

                    string copyError = null;
                    try
                    {
                        File.Copy(pending.TempPath, pending.FinalPath, true);
                    }
                    catch (Exception e)
                    {
                        copyError = e.GetType().Name + ": " + e.Message;
                    }

                    _pending.RemoveAt(i);
                    settledAny = true;

                    if (copyError != null)
                    {
                        FailGameView(pending, "GameView 截图复制失败: " + copyError, pending.TempPath);
                        continue;
                    }

                    pending.Request.Complete(DevResponse.Ok(new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["path"] = pending.FinalPath.Replace('\\', '/'),
                        ["width"] = width,
                        ["height"] = height,
                        ["source"] = "gameview",
                        ["bytes"] = bytes,
                        ["capture"] = "ScreenCapture.CaptureScreenshot (Play Mode GameView)",
                        ["rawPath"] = pending.TempPath.Replace('\\', '/'),
                    }));
                    continue;
                }

                if (readError != null)
                {
                    _pending.RemoveAt(i);
                    settledAny = true;
                    FailGameView(pending, "GameView 截图读取失败: " + readError, pending.TempPath);
                    continue;
                }

                if (now > pending.Deadline)
                {
                    _pending.RemoveAt(i);
                    settledAny = true;
                    FailGameView(pending,
                        "GameView 截图超时：ScreenCapture.CaptureScreenshot 在 " + (int)GameViewTimeoutSeconds + "s 内没有落盘（GameView 可能不可见/没在渲染）",
                        pending.TempPath);
                }
            }

            if (settledAny && _pending.Count == 0 && _ticking)
            {
                EditorApplication.update -= Tick;
                _ticking = false;
            }
        }

        /// <summary>gameview 失败：auto 才回退 camera，且把回退原因和真实 source 一起写进响应。</summary>
        static void FailGameView(PendingCapture pending, string gameViewError, string rawPath)
        {
            if (pending.AllowCameraFallback)
            {
                string cameraError;
                Texture2D texture = CaptureCamera(out cameraError);
                if (texture != null)
                {
                    try
                    {
                        WriteTexture(pending.Request, texture, pending.Name, "camera",
                            "auto 回退：gameview 不可用（" + gameViewError + "）");
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }

                    return;
                }

                gameViewError = gameViewError + " / camera 也不可用: " + cameraError;
            }

            var payload = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["source"] = "gameview",
                ["error"] = "gameview 截图不可用：" + gameViewError,
                ["hint"] = "可改用 ?view=auto 或 ?view=camera",
            };

            if (!string.IsNullOrEmpty(rawPath)) payload["rawPath"] = rawPath.Replace('\\', '/');

            pending.Request.Complete(new DevResponse { Status = 503, Body = DevJson.Serialize(payload) });
        }

        // ---------------------------------------------------------------- camera / scene（同步）

        delegate Texture2D CaptureFunc(out string error);

        static void CompleteWithTexture(DevHttpRequest request, CaptureFunc capture, string source)
        {
            string error;
            Texture2D texture = capture(out error);
            if (texture == null)
            {
                request.Complete(DevResponse.Error(source + " 截图不可用: " + error, 503));
                return;
            }

            try
            {
                WriteTexture(request, texture, Sanitize(request.GetQuery("name", null)), source, null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static void WriteTexture(DevHttpRequest request, Texture2D texture, string name, string source, string note)
        {
            byte[] png = texture.EncodeToPNG();
            if (png == null || png.Length == 0)
            {
                request.Complete(DevResponse.Error(source + " 截图不可用: EncodeToPNG 返回空数据", 500));
                return;
            }

            string directory = ScreenshotDirectory();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name + ".png");
            File.WriteAllBytes(path, png);

            var payload = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["path"] = path.Replace('\\', '/'),
                ["width"] = texture.width,
                ["height"] = texture.height,
                ["source"] = source,
                ["bytes"] = png.Length,
            };

            if (!string.IsNullOrEmpty(note)) payload["note"] = note;
            request.Complete(DevResponse.Ok(payload));
        }

        static Texture2D CaptureSceneView(out string error)
        {
            error = null;
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                error = "没有打开的 SceneView 窗口";
                return null;
            }

            Camera camera = sceneView.camera;
            if (camera == null)
            {
                error = "SceneView 没有相机";
                return null;
            }

            return RenderCamera(camera, Mathf.Max(64, (int)sceneView.position.width), Mathf.Max(64, (int)sceneView.position.height), out error);
        }

        static Texture2D CaptureCamera(out string error)
        {
            Camera camera = Camera.main;
            if (camera == null || !camera.isActiveAndEnabled)
            {
                Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
                camera = null;
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] != null && cameras[i].isActiveAndEnabled)
                    {
                        camera = cameras[i];
                        break;
                    }
                }
            }

            if (camera == null)
            {
                error = "场景里没有启用的相机";
                return null;
            }

            return RenderCamera(camera, 1280, 720, out error);
        }

        static Texture2D RenderCamera(Camera camera, int width, int height, out string error)
        {
            error = null;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = previousTarget;

                RenderTexture.active = target;
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                Color32 uniformColor;
                if (IsUniform(texture, out uniformColor))
                {
                    error = "相机渲染结果是纯色 (" + uniformColor.r + "," + uniformColor.g + "," + uniformColor.b + ")，不可用";
                    UnityEngine.Object.DestroyImmediate(texture);
                    return null;
                }

                return texture;
            }
            catch (Exception e)
            {
                error = "相机渲染失败: " + e.GetType().Name + ": " + e.Message;
                return null;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // ---------------------------------------------------------------- 落盘读取

        /// <summary>读取 CaptureScreenshot 落盘的 PNG；文件还没写完 / 还不完整时返回 false 且 error 为 null。</summary>
        static bool TryLoadCapture(string path, out Texture2D texture, out long bytes, out string error)
        {
            texture = null;
            bytes = 0;
            error = null;

            try
            {
                if (!File.Exists(path)) return false;

                var info = new FileInfo(path);
                if (info.Length < 1024) return false;

                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47) return false;

                var loaded = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!loaded.LoadImage(data))
                {
                    UnityEngine.Object.DestroyImmediate(loaded);
                    return false;
                }

                texture = loaded;
                bytes = data.Length;
                return true;
            }
            catch (IOException)
            {
                return false; // 还在写，下一帧再试
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        static bool IsUniform(Texture2D texture, out Color32 sample)
        {
            sample = default(Color32);
            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0) return true;

            sample = pixels[0];
            int step = Mathf.Max(1, pixels.Length / 8192);
            for (int i = 0; i < pixels.Length; i += step)
            {
                Color32 pixel = pixels[i];
                if (pixel.r != sample.r || pixel.g != sample.g || pixel.b != sample.b) return false;
            }

            return true;
        }

        // ---------------------------------------------------------------- 工具

        static string ScreenshotDirectory()
        {
            string projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? ".";
            string repoRoot = Path.GetDirectoryName(projectRoot) ?? projectRoot;
            return Path.Combine(Path.Combine(Path.Combine(repoRoot, "TempLogs"), "DevServer"), "screenshots");
        }

        static string Sanitize(string requested)
        {
            string name = string.IsNullOrEmpty(requested)
                ? "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                : requested.Trim();

            var builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length && builder.Length < 64; i++)
            {
                char c = name[i];
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                builder.Append(safe ? c : '_');
            }

            return builder.Length == 0 ? "shot" : builder.ToString();
        }
    }
}
