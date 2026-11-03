using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// Shared off-screen pixel probe for the TASK-A-R9-05/06 pixel-evidence
    /// suites (extracted verbatim from ParticlePixelEvidencePlayModeTests so
    /// the R9-06 bindings suite measures frames the exact same way without
    /// copy-pasting the statistics engine; the R9-05 file keeps its own copy —
    /// zero risk to a green suite).
    ///
    /// Rig (no interactive desktop, nothing is eyeballed): a probe camera
    /// parked far away from everything, solid 0.02 background, rendering into
    /// a small ARGBFloat target read back with ReadPixels. The control frame
    /// is uniform, so any brighter pixel can only come from the effect under
    /// test. Place the camera with <see cref="Build(Vector3, Vector3)"/> and
    /// keep the effect inside <see cref="FarClip"/>.
    /// </summary>
    internal sealed class PixelProbeRig
    {
        /// <summary>Probe park position: far outside anything a test scene draws.</summary>
        public static readonly Vector3 FarPark = new Vector3(5000f, 5000f, 5000f);

        /// <summary>1.2 m gap: a 0.05-0.10 m spark is ~15-30 px (R9-05 calibration).</summary>
        public const float BurstDistance = 1.2f;

        public const int Width = 320;
        public const int Height = 180;
        public const float FarClip = 20f;

        private GameObject _rig;
        private GameObject _cameraGo;
        private RenderTexture _target;

        /// <summary>The probe camera (null before <see cref="Build"/>).</summary>
        public Camera Camera { get; private set; }

        /// <summary>Builds the rig looking from <paramref name="eyePosition"/> at <paramref name="lookTarget"/>.</summary>
        public void Build(Vector3 eyePosition, Vector3 lookTarget)
        {
            _rig = new GameObject("~PixelProbeRig");
            _cameraGo = new GameObject("~PixelProbeCamera");
            _cameraGo.transform.SetParent(_rig.transform, false);
            _cameraGo.transform.position = eyePosition;
            _cameraGo.transform.rotation = Quaternion.LookRotation((lookTarget - eyePosition).normalized);
            Camera = _cameraGo.AddComponent<Camera>();
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
            Camera.allowHDR = true;
            Camera.allowMSAA = false;
            Camera.nearClipPlane = 0.1f;
            Camera.farClipPlane = FarClip;
            Camera.fieldOfView = 60f;

            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.name = "~PixelProbeTarget";
            _target.Create();
            Camera.targetTexture = _target;
            Camera.enabled = true;
        }

        /// <summary>Waits one frame, then hands the rendered frame to <paramref name="sink"/>.</summary>
        public IEnumerator CaptureInto(Action<Frame> sink)
        {
            yield return null;
            sink(ReadBack());
        }

        public void Dispose()
        {
            if (Camera != null)
            {
                Camera.targetTexture = null;
                Camera = null;
            }

            if (_rig != null)
            {
                UnityEngine.Object.Destroy(_rig);
                _rig = null;
            }

            _cameraGo = null;
            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.Destroy(_target);
                _target = null;
            }
        }

        private Frame ReadBack()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _target;
            var tex = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            tex.Apply(false, false);
            float[] lumas = LumaOf(tex.GetPixels());
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(tex);
            return new Frame(lumas);
        }

        private static float[] LumaOf(Color[] pixels)
        {
            var lumas = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                lumas[i] = 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
            }

            return lumas;
        }

        /// <summary>One-line luminance stats dump for the test log.</summary>
        public static void LogFrame(string label, Frame frame)
        {
            ImageStats s = frame.Stats;
            Debug.Log($"[FXBINDINGS] {label}: meanLuma={s.MeanLuma:F5} stdLuma={s.StdLuma:F5} " +
                      $"min={s.MinLuma:F4} max={s.MaxLuma:F4} p95={s.P95Luma:F4}");
        }

        public readonly struct ImageStats
        {
            public readonly float MeanLuma;
            public readonly float StdLuma;
            public readonly float P95Luma;
            public readonly float MinLuma;
            public readonly float MaxLuma;

            public ImageStats(float mean, float std, float p95, float min, float max)
            {
                MeanLuma = mean;
                StdLuma = std;
                P95Luma = p95;
                MinLuma = min;
                MaxLuma = max;
            }
        }

        public readonly struct Frame
        {
            public readonly float[] Lumas;
            public readonly ImageStats Stats;

            public Frame(float[] lumas)
            {
                Lumas = lumas;

                int count = lumas.Length;
                double sum = 0d;
                float min = float.MaxValue;
                float max = float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    float luma = lumas[i];
                    sum += luma;
                    if (luma < min) min = luma;
                    if (luma > max) max = luma;
                }

                float mean = (float)(sum / count);
                double variance = 0d;
                for (int i = 0; i < count; i++)
                {
                    double d = lumas[i] - mean;
                    variance += d * d;
                }

                var sorted = (float[])lumas.Clone();
                Array.Sort(sorted);
                int p95 = Mathf.Clamp((int)(count * 0.95f), 0, count - 1);

                Stats = new ImageStats(mean, Mathf.Sqrt((float)(variance / count)), sorted[p95], min, max);
            }
        }

        public readonly struct FrameDiff
        {
            /// <summary>A pixel counts as "moved" when its luminance changes by more than this.</summary>
            public const float MoveEpsilon = 0.02f;

            /// <summary>Mean absolute luminance change over all pixels.</summary>
            public readonly float MeanAbs;

            /// <summary>Share of pixels whose luminance rose by more than <see cref="MoveEpsilon"/>.</summary>
            public readonly float BrighterFraction;

            /// <summary>Share of pixels whose luminance fell by more than <see cref="MoveEpsilon"/>.</summary>
            public readonly float DarkerFraction;

            /// <summary>Share of pixels that moved at all (either direction).</summary>
            public readonly float MovedFraction;

            private FrameDiff(float meanAbs, float brighter, float darker, float moved)
            {
                MeanAbs = meanAbs;
                BrighterFraction = brighter;
                DarkerFraction = darker;
                MovedFraction = moved;
            }

            public static FrameDiff Between(Frame a, Frame b)
            {
                Assert.AreEqual(a.Lumas.Length, b.Lumas.Length, "frames must have the same pixel count");
                int count = a.Lumas.Length;
                double sumAbs = 0d;
                int brighter = 0;
                int darker = 0;
                for (int i = 0; i < count; i++)
                {
                    float delta = b.Lumas[i] - a.Lumas[i];
                    sumAbs += Mathf.Abs(delta);
                    if (delta > MoveEpsilon) brighter++;
                    else if (delta < -MoveEpsilon) darker++;
                }

                return new FrameDiff((float)(sumAbs / count), brighter / (float)count, darker / (float)count,
                    (brighter + darker) / (float)count);
            }
        }
    }
}
