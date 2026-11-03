// =============================================================================
// zfg_dxgi_exports.h —— dxgi 代理的导出签名表（TASK-R10-03 A-1）
//
// 缺陷背景（R10-02 A-1）：`zfg_dxgi_proxy.cpp:11` 曾断言"本表 20 个导出均 ≤ 4 个
// 参数，x64 全走 rcx/rdx/r8/r9"，但 DXGID3D10CreateDevice 是 7 参、CreateLayeredDevice
// 6 参、GetLayeredDeviceSize 7 参。通用转发只声明 4 个形参 → 第 5–7 参取转发函数
// 自己栈帧里的未初始化值 → 真实实现把 ID3D10Device* 写到野地址。
//
// 本表把"导出名 → 真实参数个数 → 声明形参个数（= 转发覆盖）"变成**可断言的单一
// 事实来源**，并由 Native/tests/test_zfg_dxgi_exports.cpp 表驱动校验：
//   · 声明形参个数必须 ≥ 真实参数个数（截断即红）
//   · 真实参数个数必须 ≤ kForwardSlots（转发窗口容量，超了必须扩窗口）
//   · zfg_dxgi.def 里的每个 dxgi 导出必须在表里（漏配即红）
//
// 两条纪律：
//   1. 有文档/公开原型的导出按**真实签名**声明（CreateDXGIFactory* 是热路径）；
//   2. 未文档化、个数不可证的导出（DXGID3D10* / Compat* / PIX*）一律按
//      **kForwardSlots 个整型槽位**声明并原样透传 —— 宁可多收几个槽位（被调方
//      按自己的原型忽略多余的），也绝不截断。多传参数的 x64 安全性：调用方清栈，
//      被调方只读自己声明的寄存器/栈槽。
// =============================================================================
#pragma once

#include <cstddef>
#include <cstdint>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

namespace zfg {

/// 转发窗口：一次最多透传的参数个数（= 4 个寄存器 + 4 个栈槽，Win64 上足够覆盖
/// 本表全部导出；新增 >8 参的导出时用例会红，必须扩这个常量并同步扩展 ForwardArgs）
constexpr size_t kForwardSlots = 8u;

/// 导出表的证据等级（写进施工报告，别把"猜的"写成"文档的"）
enum class ExportEvidence : uint8_t {
    MsDoc = 0,      // MS Learn / Windows SDK 文档给出的原型
    PublicProto = 1, // 未文档化导出，采用公开流传的原型（Wine/RE 资料），个数存疑
    Unknown = 2,    // 个数不可证 → 只保证"8 槽全转发，不截断"
};

/// 一条导出：名字 / 真实参数个数（自证口径见 evidence）/ 声明形参个数（= 转发覆盖）
struct DxgiExportSpec {
    const char* name;
    uint8_t paramCount;    // 真实参数个数（依据 evidence）
    uint8_t declaredArity; // 本代理导出函数声明的形参个数（必须 ≥ paramCount）
    ExportEvidence evidence;
};

/// 顺序即 g_slots[] 下标（代理按本表顺序建表、按名字解析系统实现）
constexpr DxgiExportSpec kDxgiExports[] = {
    {"ApplyCompatResolutionQuirking", 4u, 8u, ExportEvidence::Unknown},
    {"CompatString", 2u, 8u, ExportEvidence::Unknown},
    {"CompatValue", 2u, 8u, ExportEvidence::Unknown},
    {"CreateDXGIFactory", 2u, 2u, ExportEvidence::MsDoc},
    {"CreateDXGIFactory1", 2u, 2u, ExportEvidence::MsDoc},
    {"CreateDXGIFactory2", 3u, 3u, ExportEvidence::MsDoc},
    // R10-02 A-1 的三条截断点（曾一律只声明 4 个形参）
    {"DXGID3D10CreateDevice", 7u, 8u, ExportEvidence::PublicProto},
    {"DXGID3D10CreateLayeredDevice", 6u, 8u, ExportEvidence::PublicProto},
    {"DXGID3D10GetLayeredDeviceSize", 7u, 8u, ExportEvidence::PublicProto},
    {"DXGID3D10RegisterLayers", 2u, 8u, ExportEvidence::PublicProto},
    {"DXGIDeclareAdapterRemovalSupport", 0u, 0u, ExportEvidence::MsDoc},
    {"DXGIDisableVBlankVirtualization", 0u, 0u, ExportEvidence::MsDoc},
    {"DXGIDumpJournal", 1u, 8u, ExportEvidence::MsDoc},
    {"DXGIGetDebugInterface1", 3u, 3u, ExportEvidence::MsDoc},
    {"DXGIReportAdapterConfiguration", 1u, 8u, ExportEvidence::Unknown},
    {"PIXBeginCapture", 2u, 8u, ExportEvidence::MsDoc},
    {"PIXEndCapture", 1u, 8u, ExportEvidence::MsDoc},
    {"PIXGetCaptureState", 0u, 8u, ExportEvidence::MsDoc},
    {"SetAppCompatStringPointer", 2u, 8u, ExportEvidence::Unknown},
    {"UpdateHMDEmulationStatus", 1u, 8u, ExportEvidence::Unknown},
};
constexpr size_t kDxgiExportCount = sizeof(kDxgiExports) / sizeof(kDxgiExports[0]);

/// 表项下标（g_slots[] 下标；改名/重排表时必须同步）
enum DxgiExportIndex : size_t {
    kIdxApplyCompatResolutionQuirking = 0u,
    kIdxCompatString = 1u,
    kIdxCompatValue = 2u,
    kIdxCreateDXGIFactory = 3u,
    kIdxCreateDXGIFactory1 = 4u,
    kIdxCreateDXGIFactory2 = 5u,
    kIdxDXGID3D10CreateDevice = 6u,
    kIdxDXGID3D10CreateLayeredDevice = 7u,
    kIdxDXGID3D10GetLayeredDeviceSize = 8u,
    kIdxDXGID3D10RegisterLayers = 9u,
    kIdxDXGIDeclareAdapterRemovalSupport = 10u,
    kIdxDXGIDisableVBlankVirtualization = 11u,
    kIdxDXGIDumpJournal = 12u,
    kIdxDXGIGetDebugInterface1 = 13u,
    kIdxDXGIReportAdapterConfiguration = 14u,
    kIdxPIXBeginCapture = 15u,
    kIdxPIXEndCapture = 16u,
    kIdxPIXGetCaptureState = 17u,
    kIdxSetAppCompatStringPointer = 18u,
    kIdxUpdateHMDEmulationStatus = 19u,
};

} // namespace zfg

// =============================================================================
// 导出声明（定义在 zfg_dxgi_proxy.cpp；extern "C" 名字与 .def 一致）
//
// 关键纪律：这里的形参个数就是转发覆盖 —— 声明少了 = 截断。未文档化的导出按
// kForwardSlots 槽位声明，未用到的槽位由调用方按 0 传（见 .cpp 的 ForwardArgs）。
// =============================================================================
extern "C" {

// 8 槽窗口型（个数不可证或 >4）：a..h 依次对应 Win64 的 rcx/rdx/r8/r9 + 4 个栈槽
uintptr_t WINAPI ApplyCompatResolutionQuirking(uintptr_t a, uintptr_t b, uintptr_t c,
    uintptr_t d, uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h);
uintptr_t WINAPI CompatString(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d, uintptr_t e,
    uintptr_t f, uintptr_t g, uintptr_t h);
uintptr_t WINAPI CompatValue(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d, uintptr_t e,
    uintptr_t f, uintptr_t g, uintptr_t h);

// 文档签名（热路径，按真实原型声明；参数个数 = 2/2/3）
HRESULT WINAPI CreateDXGIFactory(REFIID riid, void** ppFactory);
HRESULT WINAPI CreateDXGIFactory1(REFIID riid, void** ppFactory);
HRESULT WINAPI CreateDXGIFactory2(UINT Flags, REFIID riid, void** ppFactory);

// D3D10 互操作（未文档化；公开原型 7/6/7/2 参）—— R10-02 A-1 的截断点
uintptr_t WINAPI DXGID3D10CreateDevice(uintptr_t hModule, uintptr_t pFactory, uintptr_t pAdapter,
    uintptr_t Flags, uintptr_t pExtensions, uintptr_t NumExtensions, uintptr_t ppDevice,
    uintptr_t pad0);
uintptr_t WINAPI DXGID3D10CreateLayeredDevice(uintptr_t hModule, uintptr_t pFactory,
    uintptr_t pAdapter, uintptr_t Flags, uintptr_t pExtensions, uintptr_t NumExtensions,
    uintptr_t pad0, uintptr_t pad1);
uintptr_t WINAPI DXGID3D10GetLayeredDeviceSize(uintptr_t hModule, uintptr_t pFactory,
    uintptr_t pAdapter, uintptr_t Flags, uintptr_t pExtensions, uintptr_t NumExtensions,
    uintptr_t pLayeredDeviceSize, uintptr_t pad0);
uintptr_t WINAPI DXGID3D10RegisterLayers(uintptr_t pLayers, uintptr_t NumLayers, uintptr_t pad0,
    uintptr_t pad1, uintptr_t pad2, uintptr_t pad3, uintptr_t pad4, uintptr_t pad5);

// 文档签名（0 参）
HRESULT WINAPI DXGIDeclareAdapterRemovalSupport();
HRESULT WINAPI DXGIDisableVBlankVirtualization();

// 其余按 8 槽窗口
void WINAPI DXGIDumpJournal(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d, uintptr_t e,
    uintptr_t f, uintptr_t g, uintptr_t h);
HRESULT WINAPI DXGIGetDebugInterface1(UINT Flags, REFIID riid, void** ppDebug);
uintptr_t WINAPI DXGIReportAdapterConfiguration(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h);
uintptr_t WINAPI PIXBeginCapture(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d, uintptr_t e,
    uintptr_t f, uintptr_t g, uintptr_t h);
uintptr_t WINAPI PIXEndCapture(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d, uintptr_t e,
    uintptr_t f, uintptr_t g, uintptr_t h);
uintptr_t WINAPI PIXGetCaptureState(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h);
uintptr_t WINAPI SetAppCompatStringPointer(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h);
uintptr_t WINAPI UpdateHMDEmulationStatus(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h);

} // extern "C"
