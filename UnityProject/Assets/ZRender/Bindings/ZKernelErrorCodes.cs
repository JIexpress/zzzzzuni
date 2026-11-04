// ZKernelErrorCodes.cs —— DataKernel ZK_* C ABI 错误码常量（TASK-B-R9-17）。
// 与 Native/zkernel/zkernel_abi.h 的 ZK_ABI_ERR_* 一一对应（负值，0 = 成功，
// 沿用 R10-03 风格）。

namespace ZFrame.Render.Bindings
{
    /// <summary>ZK_* C ABI 错误码（0 = 成功，负值 = 失败；与原生宏同值）。</summary>
    public static class ZKernelErrorCodes
    {
        /// <summary>成功。</summary>
        public const int Ok = 0;

        /// <summary>必填出参/数据指针为空（ZK_ABI_ERR_NULL）。</summary>
        public const int ErrNull = -1;

        /// <summary>World 句柄无效：未创建/已销毁/代际失配（ZK_ABI_ERR_HANDLE）。</summary>
        public const int ErrHandle = -2;

        /// <summary>compId 越界（0..127）或组件未注册（ZK_ABI_ERR_RANGE）。</summary>
        public const int ErrRange = -3;

        /// <summary>参数非法：size 不匹配/对齐非 2 的幂/游标损坏（ZK_ABI_ERR_ARGS）。</summary>
        public const int ErrArgs = -4;

        /// <summary>状态拒绝：重复注册/实体缺组件/句柄失效/游标作废（ZK_ABI_ERR_STATE）。</summary>
        public const int ErrState = -5;

        /// <summary>World 句柄表已满（8 个，ZK_ABI_ERR_FULL）。</summary>
        public const int ErrFull = -6;

        /// <summary>原生分配失败（异常不跨 ABI，折算为错误码；ZK_ABI_ERR_NOMEM）。</summary>
        public const int ErrNoMem = -7;
    }
}
