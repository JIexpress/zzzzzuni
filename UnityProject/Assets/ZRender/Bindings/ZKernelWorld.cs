// ZKernelWorld.cs —— DataKernel World C ABI 的托管安全包装（TASK-B-R9-17）。
// 原生侧：Native/zkernel/zkernel_abi.h（句柄表 + Copy 语义 + 游标式批量迭代）；
// P/Invoke 声明集中在本目录 ZKernelNative.cs（internal），本类是唯一公开门面。
//
// 线程模型（沿用原生单线程约定）：本类全部成员只允许主线程调用，不做内部加锁；
// 游标迭代（EachBatches）期间不得并发修改本世界。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// DataKernel World 的托管句柄式包装（IDisposable；一个实例对应原生句柄表
    /// 中的一个 World 槽位，进程内最多 8 个未释放实例）。组件 ID 由调用方显式分配
    /// （0..127，每个 World 内唯一）。销毁/释放语义由原生代际与布局版本号兜底：
    /// 旧实体句柄、旧 World 句柄、布局变更后的游标一律被原生侧拒绝。
    /// </summary>
    public sealed class ZKernelWorld : IDisposable
    {
        private ulong _handle;
        private readonly Dictionary<uint, uint> _componentSizes;
        private bool _disposed;

        /// <summary>创建 World（原生句柄表登记）。表满（8 个未释放）时抛出。</summary>
        /// <exception cref="ZKernelException">原生创建失败（表满/分配失败）。</exception>
        public ZKernelWorld()
        {
            int err = ZKernelNative.ZK_WorldCreate(out _handle);
            if (err != ZKernelErrorCodes.Ok)
            {
                throw new ZKernelException(err, "ZK_WorldCreate");
            }

            _componentSizes = new Dictionary<uint, uint>(16);
        }

        /// <summary>已释放（Dispose 后句柄失效，后续调用一律抛出）。</summary>
        public bool IsDisposed
        {
            get { return _disposed; }
        }

        /// <summary>释放 World（原生销毁全部实体与 chunk 存储，槽位代际 +1）。可重复调用。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = ZKernelNative.ZK_WorldDestroy(_handle); // 句柄必有效；失败不外抛（幂等释放）
            _handle = 0;
            GC.SuppressFinalize(this);
        }

        ~ZKernelWorld()
        {
            // 兜底释放（正常路径应显式 Dispose）；仅当尚未释放时销毁原生 World。
            if (!_disposed)
            {
                _ = ZKernelNative.ZK_WorldDestroy(_handle);
                _disposed = true;
            }
        }

        // ---- 组件注册 -----------------------------------------------------------

        /// <summary>注册组件（完整参数版）：compId 显式分配（0..127），sizeBytes &gt; 0，
        /// alignBytes 为 2 的幂（列起始按其对齐；chunk 分配取全列最大对齐值）。</summary>
        /// <exception cref="ZKernelException">重复注册（ErrState）/参数非法（ErrArgs、ErrRange）。</exception>
        public void RegisterComponent(uint compId, uint sizeBytes, uint alignBytes)
        {
            ThrowIfDisposed();
            int err = ZKernelNative.ZK_RegisterComponent(_handle, compId, sizeBytes, alignBytes);
            ThrowOnError(err, "ZK_RegisterComponent");
            _componentSizes[compId] = sizeBytes;
        }

        /// <summary>
        /// 注册组件类型版：compId 显式分配（0..127）；sizeBytes = Marshal.SizeOf&lt;T&gt;()；
        /// alignBytes = size 的最大 2 的幂因子（封顶 64）。该启发式只会**高估**对齐
        /// （数学上必然是真对齐的整数倍：size 恒为真对齐的整数倍，而最大 2 的幂因子
        /// 的指数 ≥ 真对齐的指数）——列起始过度对齐只降 chunk 容量、不影响正确性；
        /// 需要精确对齐时改用 <see cref="RegisterComponent(uint,uint,uint)"/>。
        /// T 须为非托管 POD（unmanaged 约束排除引用字段）。
        /// </summary>
        /// <exception cref="ZKernelException">重复注册 / 参数非法。</exception>
        public void RegisterComponent<T>(uint compId) where T : unmanaged
        {
            uint size = checked((uint)Marshal.SizeOf<T>());
            RegisterComponent(compId, size, AlignOf(size));
        }

        // ---- 实体 ---------------------------------------------------------------

        /// <summary>按掩码生成实体（组件内存未初始化，用 SetComponent 写入）。</summary>
        /// <exception cref="ZKernelException">掩码含未注册组件（ErrRange）等。</exception>
        public ZkEntity Spawn(ZkMask mask)
        {
            ThrowIfDisposed();
            int err = ZKernelNative.ZK_Spawn(_handle, mask.Lo, mask.Hi, out var entity);
            ThrowOnError(err, "ZK_Spawn");
            return entity;
        }

        /// <summary>销毁实体（swap-back 收行 + 代际 +1，旧句柄自此失效）。
        /// 句柄失效/已销毁返回 false（预期内失败，不抛）。</summary>
        public bool Destroy(ZkEntity entity)
        {
            ThrowIfDisposed();
            int err = ZKernelNative.ZK_Destroy(_handle, entity);
            if (err == ZKernelErrorCodes.Ok)
            {
                return true;
            }

            if (err == ZKernelErrorCodes.ErrState)
            {
                return false;
            }

            throw new ZKernelException(err, "ZK_Destroy");
        }

        // ---- 组件读写（Copy 语义）-------------------------------------------------

        /// <summary>写组件（Copy 语义）：把 value 逐字节拷入实体行内该组件列。
        /// T 的封送尺寸必须与注册尺寸一致（不一致抛 ArgumentException）。</summary>
        /// <exception cref="ArgumentException">T 尺寸与注册尺寸不一致 / compId 未注册。</exception>
        /// <exception cref="ZKernelException">实体未持有该组件（ErrState）等。</exception>
        public unsafe void SetComponent<T>(ZkEntity entity, uint compId, in T value) where T : unmanaged
        {
            ThrowIfDisposed();
            int size = ValidateComponentSize<T>(compId);
            T copy = value; // 栈副本取地址：局部变量天然固定，无需 fixed（CS0213）
            int err;
            unsafe
            {
                void* p = &copy;
                err = ZKernelNative.ZK_SetComponent(_handle, entity, compId, p, (uint)size);
            }

            ThrowOnError(err, "ZK_SetComponent");
        }

        /// <summary>读组件（Copy 语义）。实体未持有该组件时抛出（容错读用 TryGetComponent）。</summary>
        /// <exception cref="ZKernelException">实体句柄失效 / 未持有该组件。</exception>
        public T GetComponent<T>(ZkEntity entity, uint compId) where T : unmanaged
        {
            if (!TryGetComponent<T>(entity, compId, out var value))
            {
                throw new ZKernelException(ZKernelErrorCodes.ErrState,
                    $"ZK_GetComponent<{typeof(T).Name}>（实体 {entity} 不持有组件 {compId}）");
            }

            return value;
        }

        /// <summary>容错读组件（Copy 语义）：未持有/句柄失效返回 false 且 value 为 default。</summary>
        /// <exception cref="ArgumentException">T 尺寸与注册尺寸不一致 / compId 未注册。</exception>
        public unsafe bool TryGetComponent<T>(ZkEntity entity, uint compId, out T value) where T : unmanaged
        {
            ThrowIfDisposed();
            int size = ValidateComponentSize<T>(compId);
            T result = default;
            uint ok;
            int err;
            unsafe
            {
                void* p = &result;
                err = ZKernelNative.ZK_GetComponent(_handle, entity, compId, p, (uint)size, out ok);
            }

            ThrowOnError(err, "ZK_GetComponent");
            value = result; // ok == 0 时原生未写入，保持 default
            return ok != 0u;
        }

        /// <summary>运行时增组件（新列零初始化）。已持有该组件时抛出（不做幂等）。</summary>
        /// <exception cref="ZKernelException">已持有（ErrState）等。</exception>
        public unsafe void AddComponent(ZkEntity entity, uint compId)
        {
            ThrowIfDisposed();
            int err = ZKernelNative.ZK_AddComponent(_handle, entity, compId, null); // 空指针 = 零初始化（合法）
            ThrowOnError(err, "ZK_AddComponent");
        }

        /// <summary>运行时增组件并写入初始数据（Copy 进新列）。已持有该组件时抛出。</summary>
        /// <exception cref="ArgumentException">T 尺寸与注册尺寸不一致 / compId 未注册。</exception>
        /// <exception cref="ZKernelException">已持有（ErrState）等。</exception>
        public unsafe void AddComponent<T>(ZkEntity entity, uint compId, in T initialData) where T : unmanaged
        {
            ThrowIfDisposed();
            int size = ValidateComponentSize<T>(compId);
            T copy = initialData;
            int err;
            unsafe
            {
                void* p = &copy;
                err = ZKernelNative.ZK_AddComponent(_handle, entity, compId, p);
            }

            ThrowOnError(err, "ZK_AddComponent");
        }

        /// <summary>运行时删组件（被删列数据丢弃）。泛型版先做 T 尺寸校验（与注册一致性）。
        /// 未持有/句柄失效时抛出。</summary>
        /// <exception cref="ArgumentException">T 尺寸与注册尺寸不一致 / compId 未注册。</exception>
        /// <exception cref="ZKernelException">未持有该组件（ErrState）等。</exception>
        public void RemoveComponent<T>(ZkEntity entity, uint compId) where T : unmanaged
        {
            ThrowIfDisposed();
            _ = ValidateComponentSize<T>(compId); // 泛型版的价值：类型层面锁定组件布局
            int err = ZKernelNative.ZK_RemoveComponent(_handle, entity, compId);
            ThrowOnError(err, "ZK_RemoveComponent");
        }

        /// <summary>运行时删组件（非泛型版，无 T 校验）。未持有/句柄失效时抛出。</summary>
        public void RemoveComponent(ZkEntity entity, uint compId)
        {
            ThrowIfDisposed();
            int err = ZKernelNative.ZK_RemoveComponent(_handle, entity, compId);
            ThrowOnError(err, "ZK_RemoveComponent");
        }

        // ---- 游标式批量迭代 -------------------------------------------------------

        /// <summary>
        /// 批量迭代（游标式，无回调跨 ABI）：遍历满足 (entityMask 含 mask 全部位) 的
        /// 全部实体，逐 chunk 调用 <paramref name="onBatch"/>——
        /// columns[j] = 查询掩码升序 compId 位序第 j 列在本 chunk 的起始指针
        /// （SoA：行 r 位于 columns[j] + r * 该组件注册字节数），rowCount = 本 chunk
        /// 行数（满批 = chunk 容量，尾批为余数）。返回累计交付总行数。
        /// </summary>
        /// <remarks>
        /// delegate 生命周期：原生侧不持有任何托管引用（游标是调用方持有的 POD，
        /// 本方法循环内同步调用 onBatch，其引用由调用栈根持有），无需 GCHandle.Alloc，
        /// 迭代中断（异常）也无原生侧清理——见 ZkEachBatchHandler 的 remarks。
        /// 回调内禁止 Spawn/Destroy/Add/Remove（原生以布局版本号防护，违者下一次
        /// 推进抛 ZKernelException）；列内容可经指针原地读写。
        /// 出参数组按查询位列数一次分配、跨 chunk 复用（迭代路径零逐 chunk 分配）。
        /// </remarks>
        public unsafe uint EachBatches(ZkMask mask, ZkEachBatchHandler onBatch)
        {
            ThrowIfDisposed();
            if (onBatch == null)
            {
                throw new ArgumentNullException(nameof(onBatch));
            }

            int err = ZKernelNative.ZK_EachBatchBegin(_handle, mask.Lo, mask.Hi, out var cursor);
            ThrowOnError(err, "ZK_EachBatchBegin");

            int colCount = mask.CountBits();
            var columns = new IntPtr[colCount];
            fixed (IntPtr* colBase = columns) // 空掩码（colCount==0）时 p 为 null，原生允许
            {
                void** colPtrs = (void**)colBase;
                while (true)
                {
                    err = ZKernelNative.ZK_EachBatchNext(
                        _handle, ref cursor, colPtrs, (uint)columns.Length,
                        out var cumulative, out var chunkRows);
                    if (err < 0)
                    {
                        ThrowOnError(err, "ZK_EachBatchNext"); // 含布局变更作废（ErrState）
                    }

                    if (err == 0)
                    {
                        return cumulative; // 耗尽：累计总行数
                    }

                    onBatch(columns, chunkRows);
                }
            }
        }

        // ---- 观测 ---------------------------------------------------------------

        /// <summary>统计快照（存活实体数 / chunk 总数 / 迁移三计数）。</summary>
        public ZkWorldStats GetStats()
        {
            ThrowIfDisposed();
            int err = ZKernelNative.ZK_WorldStats(_handle, out var stats);
            ThrowOnError(err, "ZK_WorldStats");
            return stats;
        }

        /// <summary>组件注册字节数（未注册返回 0；列行步长计算用）。</summary>
        public uint GetComponentSize(uint compId)
        {
            return _componentSizes.TryGetValue(compId, out var size) ? size : 0u;
        }

        // ---- 内部 ---------------------------------------------------------------

        /// <summary>T 封送尺寸与注册尺寸一致性校验（Marshal.SizeOf 校验，简报要求）。
        /// 返回注册字节数；不一致抛 ArgumentException。</summary>
        private int ValidateComponentSize<T>(uint compId) where T : unmanaged
        {
            if (!_componentSizes.TryGetValue(compId, out var registered))
            {
                throw new ArgumentException($"组件 {compId} 未注册（先 RegisterComponent）", nameof(compId));
            }

            int marshalSize = Marshal.SizeOf<T>();
            if (marshalSize != registered)
            {
                throw new ArgumentException(
                    $"T={typeof(T).Name} 封送尺寸 {marshalSize} 与组件 {compId} 注册尺寸 {registered} 不一致",
                    nameof(compId));
            }

            return marshalSize;
        }

        /// <summary>size 的最大 2 的幂因子（封顶 64）；size 恒 ≥ 1。</summary>
        private static uint AlignOf(uint size)
        {
            uint align = 1u;
            while (align < size && align < 64u)
            {
                uint next = align << 1;
                if (size % next != 0u)
                {
                    break;
                }

                align = next;
            }

            return align;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ZKernelWorld));
            }
        }

        private static void ThrowOnError(int err, string op)
        {
            if (err != ZKernelErrorCodes.Ok)
            {
                throw new ZKernelException(err, op);
            }
        }
    }
}
