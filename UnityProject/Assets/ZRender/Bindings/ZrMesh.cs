// ZrMesh.cs —— .zmesh 原生加载的托管门卫（TASK-B-R4-01）。
// 封装 ZR_MeshLoadFromMemory / ZR_MeshStatsGet / ZR_MeshFree 三导出的 P/Invoke，
// 供 ZFrame.Render 及测试程序集使用（ZrNative 为程序集内部声明，不外泄）。

using System;
using System.Runtime.InteropServices;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Bindings
{
    /// <summary>经 zrender.dll 原生解析器加载 .zmesh 得到的网格统计。</summary>
    public readonly struct ZrMeshStats
    {
        public readonly uint VertexCount;
        public readonly uint IndexCount;
        public readonly bool HasNormals;
        public readonly bool HasUv;
        public readonly float[] BoundsMin;
        public readonly float[] BoundsMax;

        internal ZrMeshStats(ZrNative.ZmeshStatsNative native)
        {
            VertexCount = native.VertexCount;
            IndexCount = native.IndexCount;
            HasNormals = native.HasNormals != 0u;
            HasUv = native.HasUv != 0u;
            BoundsMin = native.BoundsMin;
            BoundsMax = native.BoundsMax;
        }
    }

    /// <summary>句柄式原生 .zmesh 加载器；句柄由原生句柄表（代际）管理。</summary>
    public static class ZrMesh
    {
        /// <summary>加载失败（解析校验拒绝或参数错误）时抛出。</summary>
        public class MeshLoadException : Exception
        {
            internal MeshLoadException(int errorCode)
                : base($"ZR_MeshLoadFromMemory 拒绝（zmesh 错误码 {errorCode}）")
            {
            }
        }

        /// <summary>从内存 buffer 原生加载 .zmesh，返回有效句柄；失败抛异常。</summary>
        public static ulong LoadFromMemory(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new MeshLoadException(-1);
            }

            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                ulong handle;
                int result = ZrNative.ZR_MeshLoadFromMemory(
                    pin.AddrOfPinnedObject(), checked((uint)data.Length), out handle);
                if (result != 0)
                {
                    throw new MeshLoadException(result);
                }
                return handle;
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>取句柄网格统计；句柄无效抛 <see cref="ArgumentException"/>。</summary>
        public static ZrMeshStats GetStats(ulong handle)
        {
            int result = ZrNative.ZR_MeshStatsGet(handle, out ZrNative.ZmeshStatsNative stats);
            if (result != 0)
            {
                throw new ArgumentException($"无效网格句柄 0x{handle:X16}", nameof(handle));
            }
            return new ZrMeshStats(stats);
        }

        /// <summary>释放句柄网格；句柄无效抛 <see cref="ArgumentException"/>。</summary>
        public static void Free(ulong handle)
        {
            int result = ZrNative.ZR_MeshFree(handle);
            if (result != 0)
            {
                throw new ArgumentException($"无效网格句柄 0x{handle:X16}", nameof(handle));
            }
        }
    }
}
