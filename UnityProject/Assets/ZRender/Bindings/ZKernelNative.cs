// ZKernelNative.cs —— zrender.dll 内 DataKernel（ZK_*）C ABI 的纯 P/Invoke 声明
// （TASK-B-R9-17）。唯一 DllImport 集中地；安全包装见 ZKernelWorld.cs。
// 原生侧声明：Native/zkernel/zkernel_abi.h（zrender.dll 导出表内 12 个 ZK_* 符号）。
//
// 约定（与 ZrNative.cs 相同）：本类 internal，跨程序集消费一律经
// ZKernelWorld（安全包装）；异常语义 = 裸 P/Invoke（dll 缺失抛
// DllNotFoundException、符号缺失抛 EntryPointNotFoundException）。

using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>DataKernel ZK_* 导出函数的 P/Invoke 声明（程序集内部使用）。</summary>
    internal static class ZKernelNative
    {
        // ---- World 生命周期 -----------------------------------------------------

        [DllImport("zrender")]
        internal static extern int ZK_WorldCreate(out ulong world);

        [DllImport("zrender")]
        internal static extern int ZK_WorldDestroy(ulong world);

        // ---- 组件注册 ------------------------------------------------------------

        [DllImport("zrender")]
        internal static extern int ZK_RegisterComponent(ulong world, uint compId, uint sizeBytes, uint alignBytes);

        // ---- 实体 ----------------------------------------------------------------

        [DllImport("zrender")]
        internal static extern int ZK_Spawn(ulong world, ulong maskLo, ulong maskHi, out ZkEntity entity);

        [DllImport("zrender")]
        internal static extern int ZK_Destroy(ulong world, ZkEntity entity);

        // ---- 组件读写（Copy 语义：指针不出 ABI）------------------------------------

        [DllImport("zrender")]
        internal static extern unsafe int ZK_SetComponent(ulong world, ZkEntity entity, uint compId, void* data, uint sizeBytes);

        [DllImport("zrender")]
        internal static extern unsafe int ZK_GetComponent(ulong world, ZkEntity entity, uint compId, void* outData, uint sizeBytes, out uint ok);

        [DllImport("zrender")]
        internal static extern unsafe int ZK_AddComponent(ulong world, ZkEntity entity, uint compId, void* initData);

        [DllImport("zrender")]
        internal static extern int ZK_RemoveComponent(ulong world, ZkEntity entity, uint compId);

        // ---- 游标式批量迭代（无回调跨 ABI）----------------------------------------
        // colPtrs 由调用方分配（定长出参数组）；原生每次调用填入一个 chunk 的各列
        // 起始指针（列序 = 查询掩码升序 compId 位序）。

        [DllImport("zrender")]
        internal static extern int ZK_EachBatchBegin(ulong world, ulong maskLo, ulong maskHi, out ZkEachCursor cursor);

        [DllImport("zrender")]
        internal static extern unsafe int ZK_EachBatchNext(ulong world, ref ZkEachCursor cursor, void** colPtrs, uint maxCols, out uint rowCount, out uint chunkRowCount);

        // ---- 观测 ----------------------------------------------------------------

        [DllImport("zrender")]
        internal static extern int ZK_WorldStats(ulong world, out ZkWorldStats stats);
    }
}
