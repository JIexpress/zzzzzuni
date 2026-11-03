using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFrame.Core;

namespace ZFrame.View.DevConsole
{
    /// <summary>
    /// 白名单命令执行器（**只在主线程调用**）。硬编码 switch，绝不反射、绝不 eval、
    /// 绝不按名字查类型。每个 handler 返回一行 JSON；任何异常都在上层折算成
    /// <c>{"ok":false,"error":...}</c>，不逃逸到游戏循环。
    /// </summary>
    internal static class RuntimeDevConsoleCommands
    {
        public static readonly string[] CommandNames =
        {
            "help", "dump", "spark", "quality", "fg", "shot",
        };

        private const string SupportedQuality = "low|medium|high";

        // H2③：`shot` 是唯一"无认证写盘"的命令，验收点名的头号危险面。
        // 两道配额：最短间隔 + 目录文件数上限，两道都是结构化错误，绝不静默丢。
        private const int ShotMinIntervalMs = 200;
        private const int ShotMaxFiles = 200;

        private static long _lastShotMs = -1000000L;

        public static string Execute(string line)
        {
            if (string.IsNullOrEmpty(line)) return Error("empty command");
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return Error("empty command");

            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();

            switch (verb)
            {
                case "help": return Help();
                case "dump": return Dump();
                case "spark": return Spark(parts);
                case "quality": return Quality(parts);
                case "fg": return FrameGeneration(parts);
                case "shot": return Shot(parts);
                default: return Error("unknown command: " + parts[0]);
            }
        }

        public static string Error(string message)
        {
            return RuntimeDevConsoleJson.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = message ?? "unknown error",
            });
        }

        private static string Ok(object payload) => RuntimeDevConsoleJson.Serialize(payload);

        private static string Ok(Dictionary<string, object> payload)
        {
            payload["ok"] = true;
            return RuntimeDevConsoleJson.Serialize(payload);
        }

        // ---------------------------------------------------------------- help

        private static string Help()
        {
            return Ok(new Dictionary<string, object>
            {
                ["commands"] = CommandNames,
                ["usage"] = new[]
                {
                    "help",
                    "dump",
                    "spark <x> <y> <z> [count]",
                    "quality <low|medium|high>",
                    "fg <on|off>",
                    "shot [name]",
                },
                ["protocol"] = "one line per request, one JSON line per response, UTF-8, max 4096 bytes",
                ["port"] = RuntimeDevConsole.Port,
            });
        }

        // ---------------------------------------------------------------- dump

        private static string Dump()
        {
            float delta = Time.unscaledDeltaTime;
            GameObject player = FindPlayer();
            Camera camera = Camera.main;

            string capability;
            bool fgOn = ProbeFrameGeneration(out capability);

            var payload = new Dictionary<string, object>
            {
                ["fps"] = delta > 0f ? 1f / delta : 0f,
                ["frameMs"] = delta * 1000f,
                ["quality"] = CurrentQualityName(),
                ["qualityLevel"] = QualitySettings.GetQualityLevel(),
                ["qualityLevels"] = QualitySettings.names,
                ["fgOn"] = fgOn,
                ["fgCapability"] = capability,
                ["scene"] = SafeSceneName(),
                ["playerPos"] = player != null ? Vector(player.transform.position) : null,
                ["cameraPos"] = camera != null ? Vector(camera.transform.position) : null,
                ["particleCount"] = ParticleRuntime.TotalParticleCount,
                ["hitSparkParticleCount"] = ParticleRuntime.HitSparkParticleCount,
                ["memoryMB"] = GC.GetTotalMemory(false) / (1024 * 1024),
                ["idlePumpUsLast"] = RuntimeDevConsole.LastIdlePumpMicros,
                ["idlePumpUsAverage"] = RuntimeDevConsole.AverageIdlePumpMicros,
                ["commandsServed"] = RuntimeDevConsole.CommandsServed,
                ["uptimeSec"] = RuntimeDevConsole.UptimeSeconds,
            };

            return Ok(payload);
        }

        private static GameObject FindPlayer()
        {
            try
            {
                return GameObject.FindGameObjectWithTag("Player");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeSceneName()
        {
            try
            {
                return SceneManager.GetActiveScene().name;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static Dictionary<string, object> Vector(Vector3 value)
        {
            return new Dictionary<string, object>
            {
                ["x"] = value.x,
                ["y"] = value.y,
                ["z"] = value.z,
            };
        }

        private static string CurrentQualityName()
        {
            string[] names = QualitySettings.names;
            int level = QualitySettings.GetQualityLevel();
            return names != null && level >= 0 && level < names.Length ? names[level] : level.ToString(CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- spark

        private static string Spark(string[] parts)
        {
            if (parts.Length < 4) return Error("usage: spark <x> <y> <z> [count]");

            if (!TryFloat(parts[1], out float x) || !TryFloat(parts[2], out float y) || !TryFloat(parts[3], out float z))
            {
                return Error("spark: x/y/z must be numbers");
            }

            int count = 12;
            if (parts.Length >= 5 && !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            {
                return Error("spark: count must be an integer");
            }

            count = Mathf.Clamp(count, 1, ParticleRuntime.MaxSparkBurst);

            if (!ParticleRuntime.Enabled)
            {
                // 不假成功：粒子运行时被关掉时，SpawnHitSpark 返回 0，这里如实报错。
                return Error("particle runtime is disabled (ParticleRuntime.SetEnabled(false))");
            }

            int emitted = ParticleRuntime.SpawnHitSpark(new Vector3(x, y, z), count);
            return Ok(new Dictionary<string, object>
            {
                ["emitted"] = emitted,
                ["requested"] = count,
                ["particleCount"] = ParticleRuntime.TotalParticleCount,
                ["hitSparkParticleCount"] = ParticleRuntime.HitSparkParticleCount,
                ["position"] = Vector(new Vector3(x, y, z)),
            });
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // ---------------------------------------------------------------- quality

        private static string Quality(string[] parts)
        {
            if (parts.Length < 2) return Error("usage: quality <" + SupportedQuality + ">");
            string requested = parts[1].Trim().ToLowerInvariant();

            string[] names = QualitySettings.names ?? new string[0];
            int index = -1;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], requested, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                // 项目档位名就叫 Low/Medium/High；名字对不上时才退到固定序号。
                switch (requested)
                {
                    case "low": index = 0; break;
                    case "medium": index = 1; break;
                    case "high": index = 2; break;
                    default: return Error("quality: unknown level '" + requested + "' (use " + SupportedQuality + ")");
                }

                if (index >= names.Length) return Error("quality: level '" + requested + "' does not exist in this project");
            }

            int previous = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(index, true);

            // 回读：不是"我调用了设置"，而是"设置真的变了"。
            int readBack = QualitySettings.GetQualityLevel();
            if (readBack != index)
            {
                return Error("quality not applied: requested " + requested + "(" + index + ") but QualitySettings.GetQualityLevel()=" + readBack);
            }

            return Ok(new Dictionary<string, object>
            {
                ["quality"] = CurrentQualityName(),
                ["qualityLevel"] = readBack,
                ["previousLevel"] = previous,
                ["requested"] = requested,
                ["verified"] = true,
            });
        }

        // ---------------------------------------------------------------- fg

        private static string FrameGeneration(string[] parts)
        {
            if (parts.Length < 2) return Error("usage: fg <on|off>");
            string requested = parts[1].Trim().ToLowerInvariant();
            if (requested != "on" && requested != "off") return Error("usage: fg <on|off>");

            bool wanted = requested == "on";

            if (!Services.TryGet<IFrameGenerationSettings>(out IFrameGenerationSettings settings) || settings == null)
            {
                // 平台/装配里没有插帧服务：明确 unsupported，绝不像征性回 ok。
                return Unsupported("frame generation service is not registered");
            }

            if (!settings.IsAvailable)
            {
                return Unsupported("frame generation unavailable: " + settings.QueryCapability() + " " + (settings.CapabilityDetail ?? string.Empty));
            }

            bool accepted;
            try
            {
                accepted = settings.TrySetEnabled(wanted);
            }
            catch (Exception e)
            {
                return Unsupported("frame generation threw " + e.GetType().Name + ": " + e.Message);
            }

            if (!accepted) return Unsupported("frame generation rejected the request (" + settings.QueryCapability() + ")");

            // 回读：stats.Enabled 是原生侧"上一帧真的在插帧"的判据。读不到就不许说成功。
            if (!settings.TryGetStats(out FrameGenerationStatsInfo stats))
            {
                return Unsupported("frame generation stats unavailable: cannot verify the switch took effect");
            }

            if (stats.Enabled != wanted)
            {
                return Error("frame generation switch did not take effect: requested " + wanted + " but stats.Enabled=" + stats.Enabled
                    + " (" + settings.QueryCapability() + ")");
            }

            return Ok(new Dictionary<string, object>
            {
                ["fgOn"] = stats.Enabled,
                ["capability"] = settings.QueryCapability().ToString(),
                ["verified"] = true,
            });
        }

        private static string Unsupported(string detail)
        {
            // 契约要求 {ok:false,error:"unsupported"}：开关没生效就是失败，不是"带个标记的成功"。
            return RuntimeDevConsoleJson.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "unsupported",
                ["detail"] = detail ?? string.Empty,
            });
        }

        private static bool ProbeFrameGeneration(out string capability)
        {
            capability = "absent";
            if (!Services.TryGet<IFrameGenerationSettings>(out IFrameGenerationSettings settings) || settings == null) return false;

            try
            {
                capability = settings.QueryCapability().ToString();
                return settings.TryGetStats(out FrameGenerationStatsInfo stats) && stats.Enabled;
            }
            catch (Exception)
            {
                capability = "error";
                return false;
            }
        }

        // ---------------------------------------------------------------- shot

        private static string Shot(string[] parts)
        {
            string requestedName = parts.Length >= 2 ? parts[1] : null;
            string name = Sanitize(requestedName);

            string directory = System.IO.Path.Combine(Application.persistentDataPath, "DevConsole", "screenshots");

            // 配额检查放在渲染之前：被限流时不做任何 GPU/磁盘工作。
            long now = NowMs;
            if (now - _lastShotMs < ShotMinIntervalMs)
            {
                return Error("shot rate limited: at least " + ShotMinIntervalMs + "ms between shots (last "
                    + (now - _lastShotMs) + "ms ago)");
            }

            int existing;
            try
            {
                existing = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.png").Length : 0;
            }
            catch (Exception e)
            {
                return Error("shot quota check failed: " + e.GetType().Name + ": " + e.Message);
            }

            if (existing >= ShotMaxFiles)
            {
                return Error("shot quota exceeded: " + existing + " files in " + directory.Replace('\\', '/')
                    + " (max " + ShotMaxFiles + "); delete old screenshots to continue");
            }

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

            if (camera == null) return Error("shot: no enabled camera in the scene");

            int width = Mathf.Clamp(Screen.width > 0 ? Screen.width : 1280, 64, 1920);
            int height = Mathf.Clamp(Screen.height > 0 ? Screen.height : 720, 64, 1920);

            RenderTexture target = null;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            Texture2D texture = null;

            try
            {
                target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);

                byte[] png = texture.EncodeToPNG();
                if (png == null || png.Length == 0) return Error("shot: EncodeToPNG returned no data");

                Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, name + ".png");
                File.WriteAllBytes(path, png);
                _lastShotMs = NowMs;

                Color32 uniform;
                bool isUniform = IsUniform(texture, out uniform);

                return Ok(new Dictionary<string, object>
                {
                    ["path"] = path.Replace('\\', '/'),
                    ["width"] = width,
                    ["height"] = height,
                    ["bytes"] = png.Length,
                    ["source"] = "camera",
                    ["camera"] = camera.name,
                    ["uniform"] = isUniform,
                });
            }
            catch (Exception e)
            {
                return Error("shot failed: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        private static long NowMs => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

        private static bool IsUniform(Texture2D texture, out Color32 sample)
        {
            sample = default(Color32);
            try
            {
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
            catch (Exception)
            {
                return false;
            }
        }

        private static string Sanitize(string requested)
        {
            string name;
            if (string.IsNullOrEmpty(requested))
            {
                name = "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            }
            else
            {
                name = requested.Trim();
            }

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
