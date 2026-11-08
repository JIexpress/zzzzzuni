// CrowdArrayBaseline.cs —— 群体行为的 C# 自管数组参照实现（TASK-A-AND-08）。
// A/B 性能对比的「数组路径」基线：与 KernelCrowdDriver 完全相同的行为语义
// （积分 + X 回绕 + 生命衰减 + 死亡/重生 + 实例矩阵重建），但数据宿主是纯托管
// 数组（SoA float[]/byte[]），零内核、零 ABI。产品代码零调用——它只被
// A/B 用例（EditMode / PlayMode）实例化，作为「如果没有 DataKernel，
// A-R9-13 之前的写法」的可测量参照物。
// 矩阵公式直接复用 KernelCrowdDriver.BuildMatrix（internal，同程序集），
// 保证两条路径的矩阵成本逐位同口径，A/B 差值 = 纯数据访问层差异。

using UnityEngine;

namespace Game.View.Effects
{
    /// <summary>
    /// C# 自管数组路径的千人群体参照实现（A/B 基线；产品侧零调用）。
    /// </summary>
    public sealed class CrowdArrayBaseline
    {
        private readonly int _count;
        private readonly float[] _x;
        private readonly float[] _y;
        private readonly float[] _z;
        private readonly float[] _vx;
        private readonly float[] _vy;
        private readonly float[] _vz;
        private readonly float[] _health;
        private readonly byte[] _alive;
        private readonly Matrix4x4[] _matrices;

        /// <summary>构造并写入与 KernelCrowdDriver.Initialize 相同的初始状态。</summary>
        public CrowdArrayBaseline(int entityCount = KernelCrowdIds.DefaultEntityCount)
        {
            if (entityCount <= 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(entityCount), "实体数必须 > 0");
            }

            _count = entityCount;
            _x = new float[entityCount];
            _y = new float[entityCount];
            _z = new float[entityCount];
            _vx = new float[entityCount];
            _vy = new float[entityCount];
            _vz = new float[entityCount];
            _health = new float[entityCount];
            _alive = new byte[entityCount];
            _matrices = new Matrix4x4[entityCount];

            for (int i = 0; i < _count; ++i)
            {
                _x[i] = i;
                _y[i] = 0.25f;
                _z[i] = 0f;
                _vx[i] = 0.5f + (i % 3) * 0.1f;
                _vy[i] = 0f;
                _vz[i] = 0f;
                _health[i] = KernelCrowdIds.InitialHealth((uint)i);
                _alive[i] = 1;
                _matrices[i] = KernelCrowdDriver.BuildMatrix(_x[i], _y[i], (uint)i, _count);
            }
        }

        /// <summary>实体数。</summary>
        public int EntityCount => _count;

        /// <summary>实例矩阵数组（与 KernelCrowdDriver.Matrices 同口径）。</summary>
        public Matrix4x4[] Matrices => _matrices;

        /// <summary>当前存活实体数。</summary>
        public int AliveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _count; ++i)
                {
                    if (_alive[i] != 0)
                    {
                        ++n;
                    }
                }

                return n;
            }
        }

        /// <summary>
        /// 每帧行为更新：与 <see cref="KernelCrowdDriver.Tick"/> 语义逐分支一致
        /// （存活：积分 + 回绕 + 衰减/死亡；死亡：回复/重生 + 矩阵重建）。
        /// </summary>
        public void Tick(float dt)
        {
            float decay = KernelCrowdIds.DecayPerSecond * dt;
            float heal = KernelCrowdIds.RespawnHealPerSecond * dt;
            float respawnHealth = -KernelCrowdIds.RespawnDelaySeconds * KernelCrowdIds.RespawnHealPerSecond;

            for (int i = 0; i < _count; ++i)
            {
                if (_alive[i] != 0)
                {
                    float nx = _x[i] + _vx[i] * dt;
                    if (nx >= _count)
                    {
                        nx -= _count;
                    }

                    _x[i] = nx;
                    _y[i] += _vy[i] * dt;
                    _z[i] += _vz[i] * dt;

                    float h = _health[i] - decay;
                    if (h <= 0f)
                    {
                        _health[i] = respawnHealth;
                        _alive[i] = 0;
                    }
                    else
                    {
                        _health[i] = h;
                    }
                }
                else
                {
                    float h = _health[i] + heal;
                    if (h >= 0f)
                    {
                        _health[i] = KernelCrowdIds.InitialHealth((uint)i);
                        _alive[i] = 1;
                    }
                    else
                    {
                        _health[i] = h;
                    }
                }

                if (_alive[i] != 0)
                {
                    _matrices[i] = KernelCrowdDriver.BuildMatrix(_x[i], _y[i], (uint)i, _count);
                }
                else
                {
                    _matrices[i] = Matrix4x4.zero;
                }
            }
        }

        /// <summary>Position X 全量求和（与 KernelCrowdDriver.PullSum 同口径的遍历参照）。</summary>
        public double SumX()
        {
            double sum = 0.0;
            for (int i = 0; i < _count; ++i)
            {
                sum += _x[i];
            }

            return sum;
        }
    }
}
