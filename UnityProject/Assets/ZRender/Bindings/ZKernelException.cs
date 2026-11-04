// ZKernelException.cs —— ZK_* C ABI 错误码的托管异常映射（TASK-B-R9-17）。

using System;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// DataKernel C ABI 调用被拒绝（返回负错误码）时抛出。
    /// <see cref="Code"/> 为原始错误码（见 <see cref="ZKernelErrorCodes"/>）。
    /// </summary>
    public class ZKernelException : Exception
    {
        /// <summary>原始 ZK_* 错误码（负值；见 ZKernelErrorCodes）。</summary>
        public int Code { get; }

        /// <summary>用出错的原生入口名 + 错误码构造。</summary>
        public ZKernelException(int code, string op)
            : base($"{op} 失败（ZK ABI 错误码 {code}：{Describe(code)}）")
        {
            Code = code;
        }

        private static string Describe(int code)
        {
            switch (code)
            {
                case ZKernelErrorCodes.ErrNull: return "ERR_NULL 必填指针为空";
                case ZKernelErrorCodes.ErrHandle: return "ERR_HANDLE World 句柄无效";
                case ZKernelErrorCodes.ErrRange: return "ERR_RANGE compId 越界或未注册";
                case ZKernelErrorCodes.ErrArgs: return "ERR_ARGS 参数非法（size/对齐/游标损坏）";
                case ZKernelErrorCodes.ErrState: return "ERR_STATE 状态拒绝（句柄失效/缺组件/游标作废）";
                case ZKernelErrorCodes.ErrFull: return "ERR_FULL World 句柄表已满";
                case ZKernelErrorCodes.ErrNoMem: return "ERR_NOMEM 原生分配失败";
                default: return "未知错误码";
            }
        }
    }
}
