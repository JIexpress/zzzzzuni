// =============================================================================
// streamline_link_check.cpp —— Streamline 头文件 + 导入库链接自检（TASK-B-R6-02）
//
// 目的：在**没有 NVIDIA 硬件**的构建机上证明"官方头 + 导入库"能编译链接。
// 做法：取 SL_API 函数地址（强制产生导入符号引用），但**不运行**——本机没有
// sl.interposer.dll，运行会因缺失依赖失败，属预期。验收口径=链接产物生成。
//
// 说明：真正的 DLSS 后端（Native/zupscaler/dlss_upscaler.cpp）走 LoadLibrary
// 动态取符号，不静态导入 sl.interposer.dll——zrender.dll 被 Unity 加载，
// 静态导入缺失 dll 会让宿主进程加载失败。
// =============================================================================
#include "sl.h"
#include "sl_dlss.h"

namespace {

// 取地址即引用导入符号（链接期必须被导入库解析）；volatile 防被优化掉
PFun_slInit* volatile g_slInit = &slInit;
PFun_slShutdown* volatile g_slShutdown = &slShutdown;
PFun_slEvaluateFeature* volatile g_slEvaluateFeature = &slEvaluateFeature;

// 头文件 ABI 自检：结构体可实例化、枚举可引用（纯编译期）
constexpr sl::Feature kFeature = sl::kFeatureDLSS;
constexpr uint64_t kSdkVersion = sl::kSDKVersion;

} // namespace

int main()
{
    // 不调用任何 SL_API（运行需要 sl.interposer.dll，本机无）
    const bool linked = g_slInit != nullptr && g_slShutdown != nullptr
        && g_slEvaluateFeature != nullptr;
    const bool headers = (kFeature == sl::kFeatureDLSS) && (kSdkVersion != 0u);
    return (linked && headers) ? 0 : 1;
}
