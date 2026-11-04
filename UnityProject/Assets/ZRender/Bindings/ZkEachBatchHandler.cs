// ZkEachBatchHandler.cs —— EachBatches 批回调委托（TASK-B-R9-17）。

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// 批量迭代单批回调（对应原生 EachBatches 的一次 chunk 批）。
    /// <paramref name="columns"/>[j] = 第 j 列（查询掩码升序 compId 位序）在本 chunk
    /// 的起始指针（SoA：行 r 位于 <c>columns[j] + r * 组件字节数</c>）；
    /// <paramref name="rowCount"/> = 本 chunk 可读行数（满批 = chunk 容量，尾批为余数）。
    /// 数组由包装层一次分配、跨 chunk 复用（迭代路径零逐 chunk 分配）。
    /// </summary>
    /// <remarks>
    /// delegate 生命周期（简报要点）：原生侧**不持有**任何托管引用——游标迭代
    /// 无回调跨 ABI，本委托只在 C# 包装循环内被同步调用；调用期间它由调用栈
    /// （实参引用）根持有，不会被 GC 回收，因此包装层无需 GCHandle.Alloc。
    /// 约束：回调内禁止 Spawn/Destroy/AddComponent/RemoveComponent（原生 Next
    /// 以布局版本号防护，违者下一次推进抛 <see cref="ZKernelException"/>）；
    /// 列内容可经原生指针原地读写。
    /// </remarks>
    public delegate void ZkEachBatchHandler(System.IntPtr[] columns, uint rowCount);
}
