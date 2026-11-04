// KernelCrowdRenderer.cs —— 千人演练场批量绘制（TASK-A-R9-13）。
// 不为 1000 实体各建 GameObject：共享一份 Matrix4x4[]，走
// Graphics.DrawMeshInstanced 单次（或按 1023 上限分批）提交。
// URP 14 兼容：运行期 new Material(Shader.Find("Universal Render Pipeline/..."))
// 零资产（沿用 ParticleRuntime / ModelAssetSetup 的既有做法），不开自研 SRP pass。

using System;
using System.Diagnostics;
using UnityEngine;
using ZFrame.Core;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体批量绘制（<see cref="ITickable"/>，注册 View 档，故暂停时仍逐帧提交、
    /// 群体静止可见；积分归 <see cref="KernelCrowdDriver"/> 的 Logic 档）。
    /// </summary>
    /// <remarks>
    /// 判据：DrawCall = ceil(实例数 / 1023)，不随实体数线性增长。1000 实体 = 1 次提交。
    /// <see cref="LastSubmitCalls"/> 是 Graphics.DrawMeshInstanced 的**真实调用次数**
    /// （不是估算），可在 PlayMode 里同一次运行内做 100 / 1000 实体 A/B 对照。
    /// 真实 GPU DrawCall 在 -batchmode 无渲染负载下取不到可信值，见施工报告受限项。
    /// </remarks>
    public sealed class KernelCrowdRenderer : ITickable, IDisposable
    {
        private static readonly string[] ShaderCandidates =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Unlit",
            "Sprites/Default",
        };

        private readonly Matrix4x4[] _batch;
        private Matrix4x4[] _matrices;
        private ITickQueue _queue;
        private Mesh _mesh;
        private Material _material;
        private int _count;
        private bool _selfDisabled;
        private bool _disposed;

        /// <summary>构造渲染器（尚未建材质 / 注册 tick）。</summary>
        public KernelCrowdRenderer()
        {
            _batch = new Matrix4x4[KernelCrowdIds.MaxInstancesPerDraw];
        }

        /// <summary>最近一次提交中 Graphics.DrawMeshInstanced 的真实调用次数。</summary>
        public int LastSubmitCalls { get; private set; }

        /// <summary>最近一次提交的实例数。</summary>
        public int LastSubmittedInstances { get; private set; }

        /// <summary>最近一次「渲染提交」耗时（微秒）。</summary>
        public double LastRenderSubmitUs { get; private set; }

        /// <summary>解析到的 shader 名（未解析到为空串）。</summary>
        public string ResolvedShaderName { get; private set; } = string.Empty;

        /// <summary>材质/着色器可用（false = 全部候选失败，自禁用）。</summary>
        public bool IsShaderAvailable => !_selfDisabled && _material != null;

        /// <summary>全部 shader 候选失败后进入惰性状态（如实受限，不编数字）。</summary>
        public bool SelfDisabled => _selfDisabled;

        /// <summary>单次绘制实例数上限。</summary>
        public int MaxInstancesPerDraw => KernelCrowdIds.MaxInstancesPerDraw;

        /// <summary>建运行期材质与 Mesh，注册到 <paramref name="queue"/> 的 View 档。</summary>
        public void Initialize(ITickQueue queue, Matrix4x4[] matrices, int count)
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (matrices == null)
            {
                throw new ArgumentNullException(nameof(matrices));
            }

            _queue = queue;
            _matrices = matrices;
            _count = Math.Min(count, matrices.Length);

            foreach (string name in ShaderCandidates)
            {
                Shader shader = Shader.Find(name);
                if (shader == null || !shader.isSupported)
                {
                    continue;
                }

                _material = new Material(shader)
                {
                    name = "~KernelCrowdMaterial",
                    enableInstancing = true,
                };
                ResolvedShaderName = name;
                break;
            }

            _mesh = BuildUnitCube();
            if (_material == null)
            {
                _selfDisabled = true;
                UnityEngine.Debug.LogWarning("[A-R9-13] 未解析到可用 URP shader，批量绘制自禁用（如实受限）。");
            }

            _queue.Register(this, TickStage.View);
        }

        /// <summary>每帧（View 档）提交全部实例。</summary>
        public void Tick(float dt)
        {
            Submit(_count);
        }

        /// <summary>
        /// 真实提交前 <paramref name="count"/> 个实例（按 1023 上限分批），
        /// 返回 Graphics.DrawMeshInstanced 的实际调用次数；用于 A/B 实测。
        /// </summary>
        public int Submit(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            int n = Math.Min(count, _matrices != null ? _matrices.Length : 0);
            long t0 = Stopwatch.GetTimestamp();
            int submissions = 0;

            if (!_disposed && !_selfDisabled && _material != null && _mesh != null && n > 0)
            {
                int offset = 0;
                while (offset < n)
                {
                    int batch = Math.Min(MaxInstancesPerDraw, n - offset);
                    Array.Copy(_matrices, offset, _batch, 0, batch);
                    Graphics.DrawMeshInstanced(_mesh, 0, _material, _batch, batch, null,
                        UnityEngine.Rendering.ShadowCastingMode.Off, false);
                    offset += batch;
                    ++submissions;
                }
            }

            LastSubmitCalls = submissions;
            LastSubmittedInstances = n;
            LastRenderSubmitUs = ElapsedUs(t0);
            return submissions;
        }

        /// <summary>注销 tick 并释放运行期材质/Mesh（幂等）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue?.Unregister(this);
            _queue = null;
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
                _material = null;
            }

            if (_mesh != null)
            {
                UnityEngine.Object.Destroy(_mesh);
                _mesh = null;
            }
        }

        private static Mesh BuildUnitCube()
        {
            var mesh = new Mesh { name = "~KernelCrowdCube" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4,
                3, 7, 6, 3, 6, 2,
                0, 4, 7, 0, 7, 3,
                1, 2, 6, 1, 6, 5,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static double ElapsedUs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000000.0 / Stopwatch.Frequency;
        }
    }
}
