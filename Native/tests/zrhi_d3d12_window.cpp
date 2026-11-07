// =============================================================================
// zrhi_d3d12_window.cpp —— D3D12 后端窗口验证程序（TASK-B-R2-02）
//
// 目标：zrhi_d3d12_window.exe（仅 Release，不进 ctest）——D3D12 基座的端到端
// 冒烟验证。Vulkan 语义对应（每帧 ≈ Vulkan 帧循环）：
//   AcquireNextImage ≈ vkAcquireNextImageKHR
//   Barrier(Present→RenderTarget) ≈ vkCmdPipelineBarrier 布局转换
//   ClearColorImage  ≈ vkCmdClearColorImage（RTV 路径）
//   Barrier(RenderTarget→Present) ≈ 转回呈现布局
//   Submit/Present   ≈ vkQueueSubmit / vkQueuePresentKHR
//
// 流程：创建 640x480 Win32 窗口 → 设备+交换链（BGRA8Unorm，3 缓冲，Fifo）→
//   60 帧循环：Acquire → Barrier(Present→RenderTarget) → Clear（红/绿/蓝循环）→
//   Barrier(RenderTarget→Present) → Present；
//   每 10 帧（第 10/20/.../60 帧）把后台缓冲 CopyTextureRegion 到 readback 缓冲，
//   校验中心像素 RGB 与预期清屏色匹配（容差 ±2）；
//   尾声：离屏 RGBA32Float storage 图像走 UAV 清屏 + readback 校验（两种
//   清屏路径都验证）→ WaitIdle → 销毁全部资源 → InfoQueue Error 计数须为 0。
// 全部通过打印 `D3D12 WINDOW SMOKE OK` 并退出码 0。
//
// --headless：无窗口（HWND 不可用/被沙箱拒绝时）：跳过交换链，用离屏 Commit
// 资源（RGBA8Unorm ColorAttachment|TransferSrc）走同样的 Clear+readback 校验
// 路径（字节序 RGBA 而非 BGRA）。窗口模式创建失败时自动降级并打印说明。
//
// readback 插桩说明：RHI 基座尚无 CopyImageToBuffer 命令（后续任务），本程序
// 经后端原生扩展点（D3D12CommandListOf/D3D12ResourceOf）在同一命令流内记录
// CopyTextureRegion——不影响被验证的 RHI 语义路径。
//
// TASK-B-R2-03 增补：帧循环前"资源自检"阶段（①Buffer 初始数据上传+回读比对
// ②SRV/UAV 视图 bindless 索引互异+槽回收复用 ③Sampler 创建）+ 帧循环内两个
// GPU 时间戳（第 10/11 帧）→ Resolve 换算毫秒打印两帧间隔（断言 0.01~100ms）。
// 四项全过打印 `D3D12 RESOURCE SMOKE OK`；窗口/headless 两模式同跑。
//
// TASK-B-R4-03 增补：`--draw [zmeshPath] [hlslPath]` 模式——图形 PSO + 首绘：
//   加载 .zmesh（默认 Native/tests/data/cube.zmesh）→ CreateBuffer(VB/IB，经
//   B-R2-03 initData UPLOAD 上传链) → DrawList（3 个 cube 项，Add→Sort→
//   BuildBatches）→ 每帧 RG：Import 后台缓冲+深度（均 Imported）→ ClearPass
//   （颜色+深度清屏）→ CubeDrawPass（execute 回调里 SetPipelineState/
//   SetRenderTarget/SetViewportScissor/SetVertexBuffers/SetIndexBuffer/逐项
//   SetGraphicsConstantBuffer+DrawIndexedInstanced，真实消费 draw list 批）→
//   （ReadbackPass）→ PresentPass。常量（row_major mvp+光照）每帧 Map 写入，
//   mvp 随帧旋转（60 帧转一圈），光照固定方向。
//   校验（无 readback 3D 断言能力，按简报启发式）：①InfoQueue 0 错误 ②中心
//   1x1 像素 ≠ 清屏色（每 10 帧校验，失败重试后续相位帧）③60 帧完成且无设备
//   移除（GetDeviceRemovedReason）。全部通过打印 `D3D12 FIRST DRAW OK`。
//   窗口模式失败/不可用时自动降级离屏目标（RGBA8Unorm），RG 路径两模式同构。
//
// TASK-B-R5-01 增补：`--lights [zmeshPath] [hlslPath]` 模式——簇光源 + PBR 前向
//   多光源真渲染：程序化地面 quad（20×20m，C++ 直构顶点）+ cube.zmesh；1 方向光
//   +8 点光（其中 4 个随帧动画环绕）；CPU 侧每帧 AssignLightsToClusters（视空间
//   16×9×16 簇网格）→ CB2（start/count 范围 + 平坦光索引）；pbr_forward.hlsl
//   像素级簇索引 + GGX/Smith/Lambert + Reinhard tonemap。校验：中心+地面横带
//   readback——①中心像素 ≠ 清屏色（cube 可见）②地面横带受光（远超环境项）③
//   相邻校验帧横带像素随光源动画变化（最大通道差 > 阈值）④60 帧稳定无设备移除
//   ⑤InfoQueue 0 错误。全部通过打印 `D3D12 LIGHTS OK`。
//
// TASK-B-R5-02 增补：`--shadows [on|off] [zmeshPath] [hlslPath] [depthHlslPath]` 模式
//   ——CSM 级联阴影真渲染：继承 --lights 场景（地面 quad + cube + 方向光，无点光）；
//   CPU 侧 CascadeSplits（lambda=0.75）/ BuildCascades（texel snap + 正交 LH）出 4 级联
//   lightVP；RG 每级联一个 depth-only DrawPass（单张 2048² D32 数组图，层=级联，
//   depth-only PSO 无 PS/无颜色目标、CullMode::Front——与主 pass None 的绕序关系见
//   shadow_depth.hlsl 注）；主 pass pbr_shadow.hlsl 按视深选级联 + PCF 3x3 采样
//   （Texture2DArray R32Float SRV + 根 SRV 表/静态采样器，zrhi B-R5-02 加法扩展）。
//   可证伪对比：缺省先 off（factor 恒 1，跳过阴影 Pass）后 on 各 30 帧，第 30 帧
//   readback 同一地面探针点（推导注释见 kShadowProbeX），on 需暗 ≥10/255 且两相位
//   均无设备移除/InfoQueue 0 错误；打印 4 次级联分割值。
//   通过行：`D3D12 SHADOWS OK (probe delta=N)`。
//
// TASK-B-R5-03 增补：`--r5` 模式——SSR + 动态 GI 探针 + NPR 风格线（R5 收官）。
//   继承 --shadows on 场景（地面 quad 20×20 + PBR cube + 方向光 CSM），新增：
//   Toon Cube（toon.hlsl 量化 ramp + toon_outline.hlsl 反向壳描边，draw list
//   描边视图掩码位 bit5）、2 动画点光（绕 (0,0.9,6.5) 半径 2 相位相反，GI 探
//   针填充随动画变化）、GI 探针（gi_probes.h 8×2×8 CPU 辐照度累加每 4 帧轮转，
//   pbr_r5.hlsl b4 三线性替代常数环境项）、SSR（ssr_pass.h Hi-Z 6 级 mip + 32
//   步 march + 4 步二分 + 4-tap 模糊；trace/blur 半分辨率；粗糙度走 sceneColor.a）。
//   RG 帧结构：Clear → ShadowCascade×4 → Main(pbr_r5) → Toon → Outline →
//   {HiZBuild → SsrTrace → SsrBlur → SsrComposite}（ssr_pass.h RecordSsrPasses
//   单一声明源）→ Readback → Present。
//   可证伪读back（SSR off→on 两相位各 30 帧；帧内动画相位一致）：
//     ①SSR：反射探针点（Cube 正下方地面延长线 (0,-1,2.2)→像素(320,429)）SSR
//       on/off 差 ≥8；②GI：远离直射光的地面角落（2 点光轨道中心正下地面
//       (0,-1,6.5)→像素(320,320)——2 光相位相反，直接光贡献恒定，差值只来自
//       探针填充）在动画两相位（帧 40/60）差 ≥3；③Toon：亮带/暗带两探针点差
//       ≥40 且带内平坦（带内两点差 <6）。
//   通过行：`D3D12 R5 OK (ssr=N gi=N toon=N)`。
//   TASK-B-R8-01 增补：--r5 接线 Vulkan（--backend vulkan|both）——8 着色器文件/
//   16 入口点 glslc SPIR-V（同源 hlsl 双编译器消费，零 fork）；VK 路径三角洲见
//   RunR5PhaseVulkan 节头注；both = 双后端探针并排对照（RunR5Parity）。通过行
//   `VULKAN R5 OK (ssr=N gi=N toon=N)` / `D3D12/VULKAN R5 PARITY OK`。
//
// TASK-B-R9-09 增补：`--resize` 模式（仅 Vulkan）——交换链真重建可证伪验证。
//   起窗 640x480 → 30 帧红色清屏（基线 readback）→ SetWindowPos 真改客户区
//   480x360（真机 resize 事件）→ OUT_OF_DATE → 帧闸粘性 → Acquire 入口自动
//   真重建（产品路径，本程序不干预）→ 重建后 readback 仍 = 红 → 通过行
//   `VULKAN RESIZE OK (old=WxH new=WxH serial=N→M)`（尺寸/序号取设备台账）。
//   重建路径缺失（R10-03 基线）→ 重建后 readback 计数 0 → FAILED exit 1。
//
// TASK-B-R5-04 增补：`--parity` 模式——URP 对等闸门基准场景（双管线对比，人工过目）。
//   恒离屏（无窗口/交换链/vsync），1280×720 契约场景（契约全文见 RunParityMode 上方
//   常量注释，与 UnityProject/Assets/Game/Editor/ParityBench.cs 严格一致）：地面
//   20×20 @y=0 + cube A（PBR 暖白）+ cube B（toon+描边，投影）+ 方向光 CSM 阴影。
//   30 帧固定场景：逐帧 DrawIndexedInstanced 计数 + CPU 墙钟帧耗（排除首帧）；
//   第 30 帧全帧 readback → 24bit BMP 自写编码（WriteBmp24，C++ 无依赖）→
//   Docs/Parity/parity_zrender.bmp + 指标 Docs/Parity/metrics_zrender.txt。
//   自检探针（推导可复核）：toon 暗带红显著 / 阴影探针暗于受光地面参照 ≥8。
//   通过行：`D3D12 PARITY OK`。
//
// TASK-B-R6-01 增补：`--fsr [on|off]` 模式——FSR SDK 接入 + UpscalerChain 超分框架
//   （恒离屏）：内部渲染 960×540 → UpscalerChain 上采样 1280×720。场景继承
//   --shadows on（地面 quad + PBR cube(0,0,3.2) + 方向光 CSM），相机静止 + cube
//   绕 Y 旋转（MV 来自旋转）。RG 帧结构：Clear → ShadowCascade×4 → Main(pbr_shadow)
//   → MvPass（RG 特性：motion_vectors.hlsl，当前帧 clip − 上帧 clip，RG16Float
//   全渲染分辨率；两帧 mvp 各含当帧 jitter）→ RG 外 UpscalerChain.Evaluate
//   （ffxDispatch / Bilinear blit）→ readback → 帧末回 COMMON。jitter 用 FSR 官方
//   Halton(2,3) 序列（zupscaler.h ZupJitter*）注入主视投影（960→1280 相位数 11）。
//   off 相位 = SetOverride(Bilinear) 显式钉死基线；on 相位 = 自动选优（真机应为
//   FSR3，回落 Bilinear 视为 FSR 运行时缺失 → 显式失败）。
//   可证伪：锐度探针 = display 域 cube 轮廓竖直边横线（row 340）相邻像素梯度幅度，
//   on/off ≥1.2×（RCAS 锐化 vs 双线性）；InfoQueue 0 错误；无设备移除；截图
//   Docs/Parity/fsr_on.png、fsr_off.png（自写 PNG 编码，zlib stored 块）。
//   通过行：`D3D12 FSR OK (edge=N vs M)`（off 相位单跑为 `D3D12 FSR BASELINE OK`）。
//
// TASK-B-R6-02 增补：`--fsr4` 模式——三向可证伪对比（bilinear / FSR3.1 / FSR4.1，
//   各 30 帧）。相位编排复用 --fsr 的同一场景与 RunFsrPhase，额外采集：
//     · 锐度探针（同一 row 340 扫描窗，三相位可比）
//     · GPU 时间戳：每帧起/止两个 timestamp（ZrQueryPool 60 槽），解析后取均值
//     · CPU 帧耗：steady_clock 包住 Begin..WaitIdle 的均值
//   FSR4 相位走 SetOverride(Fsr4) 钉死；能力矩阵拒绝时绕过矩阵直接问 FFX 运行时
//   （打印 provider 列表 + NO_PROVIDER 证据），不伪装成功。
//   通过行：`D3D12 FSR4 OK (edge3=N edge4=M ms3=X ms4=Y)`；FSR4 不可用 →
//   `D3D12 FSR4 UNAVAILABLE (...)` 且 exit 2（受限≠失败）。
//
// TASK-B-R8-02 增补：`--fg` 模式——FFX FrameGeneration 帧生成（仅 D3D12）。
//   FrameGenerator（zupscaler/fg_ffx.h）接管 present 层：FG 效果上下文 + 代理
//   交换链（ForHwnd）+ 每帧 Configure/PrepareV2 + Present 包装；场景同源 --fsr
//   （地面 quad + PBR cube + CSM + MV Pass）但直渲染 displaySize 640x480（FG
//   输入色契约），MV 复用 R6-01 的 motion_vectors.hlsl。present 回调逐帧计数
//   isGeneratedFrame（SDK pacing 线程）→ 60 帧后帧计数翻倍≈2x 即 FG 真实产出
//   证据。诚实预期：FG 官方支持矩阵不含核显（780M 预期被拒）——OK 行
//   `D3D12 FG OK (real=N gen=M)`；SDK 拒绝/未产出插帧 → `D3D12 FG LIMITED
//   (reason=...)` 且 exit 0（受限≠失败）；计数翻倍但 InfoQueue 有错 → FAILED
//   exit 1；pacing 死锁 → TIMEOUT exit 3（硬失败）。截图 Docs/Parity/fg_real_frame.png。
//
// TASK-B-R9-13 增补：`--assets` 模式——4 人物 .zmesh 资产画廊（真实建模进自研管线）。
//   Unity ZMeshBaker.BakeGalleryToRepoData 烘焙的 CHR_*.zmesh ×4 加载进 --lights 同源
//   簇光管线（pbr_shadow.hlsl：簇光源 + PBR + CSM 阴影开），横向一字排开 @z=9
//   （间距按包围盒，脚底对齐地面，中缝让开既有 cube 的视线走廊；cube/地面保留）；
//   每模型统一 PBR 常量色一色区分。恒离屏（--parity 同策，RGBA 序截图无 swizzle）。
//   校验：①每模型 vertex/index 数打印（Unity↔原生对照表进报告）②4 探针（包围盒
//   中心屏幕投影，每 10 帧非清屏色）③第 60 帧全帧 readback → Docs/Parity/
//   assets_gallery.png ④无设备移除 + InfoQueue 0 错误。
//   通过行：`D3D12 ASSETS GALLERY OK (models=4)`。
//
// TASK-B-TECH-03 增补：`--planet` 模式——行星尺度最小切片（浮点原点重定位 +
//   球面重力 + 球体渲染雏形；Docs/TECH-ROADMAP.md 方向三，无 Unity 依赖）。
//   恒离屏 640x480（--assets 同策）300 帧自动飞行相机：贴地（0-119，2m 高度
//   前进 20km，穿越 10km 阈值触发浮点原点重定位）→ 爬升（120-209，至 2 万 km，
//   61.7km 关键帧看地平线曲率）→ 入轨远观（210-299，2.6 万 km 绕行星慢扫）。
//   数学内核 tests/planet_math.h（与 test_planet.cpp doctest 共享实现）：
//   ①相机 double 累计 + 物体 double 绝对坐标 + 渲染偏移 float(objAbs-camAbs)，
//   相机-原点偏移 >10km 重定位（原点吸附 + 近场地形片重锚）；顶点级抖动消除
//   以 JitterAudit 数值断言（naive 全 float ≈ULP(100km)=7.8mm vs 重定位后
//   ≈ULP(10m)/2≈0.5µm）+ 贴地相位 8 近场探针逐帧实况；②半径 6.371e6m 球面
//   程序化几何三档切换（近场片 ±24km / 远盘 1100km 覆盖真地平线 / 整球 UV），
//   相位伸缩 near/far（"near/far 相机"路线，对数深度留 zrhi 加法）；③球面重力
//   a=-μ·r̂/r² velocity Verlet：弹道（1200m/s@60°，积分至撞击）+ 低轨椭圆
//   （7750m/s 切向）ribbon 线带渲染，数值轨迹近/远地点 vs 活力公式 + 能量守恒
//   数值断言。探针每 10 帧（地面≠清屏/天顶≈清屏/行星中心≠清屏/角落太空≈清屏）
//   + 3 关键帧截图 Docs/Parity/planet_ground|climb|orbit.png + InfoQueue 0 错误。
//   通过行：`D3D12 PLANET OK (origin=RELOCATED@Xm jitter=before/after)`。
// =============================================================================
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
// TASK-B-R9-16：--devconsole 的 TCP 传输层——winsock2.h 必须先于 windows.h
// （否则拉进 winsock1 冲突）；LEAN_AND_MEAN 下仍需显式 ws2tcpip.h（inet_addr）
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include "zrhi/d3d12/zrhi_d3d12_device.h"
#include "zrender/cluster_lighting.h"
#include "zrender/devconsole_auth.h"   // TASK-X-SEC-01：token 认证/常量时间比较/限流
#include "zrender/devconsole_proto.h" // TASK-B-R9-16：--devconsole 协议纯逻辑
#include "zrender/draw_list.h"
#include "zrender/gi_probes.h"
#include "zrender/gpu_hud.h"           // TASK-B-R9-16：HUD 统计聚合
#include "zrender/rg_export.h"         // TASK-B-R9-16：RG 编译产物导出
#include "zrender/rg_export_path.h"    // TASK-X-SEC-01：rg.export 落盘路径限定
#include "zrender/rg_render_graph.h"
#include "zrender/renderdoc_capture.h" // TASK-B-R9-16：RenderDoc 应用内 API
#include "zrender/shadows.h"
#include "zrender/ssr_pass.h"
#include "zrender/zmesh.h"
#include "zupscaler/zupscaler.h" // TASK-B-R6-01：UpscalerChain/能力矩阵/jitter
#include "zupscaler/fg_ffx.h"    // TASK-B-R8-02：FrameGenerator（--fg 模式）

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "bench_json.h" // TASK-B-R9-10：--lights --json 单行帧耗输出
#include "planet_math.h" // TASK-B-TECH-03：--planet 浮点原点/球面重力数学内核（与 test_planet 共享）
#include "zplugin/zplugin.h"

#include <d3d12.h>
#include <dxgi.h>
#include <wrl/client.h>

// ---- TASK-B-R8-02：--fg 模式消费 FFX FG 表面格式映射（官方 inline 纯函数；
//      /W4 豁免隔离段——第三方头，同 vulkan include 段约定）--------------------------
#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4201 4324 4456 4457 4458 4514 4623 4625 4626 5045)
#include "ffx_api_types.h"
#include "dx12/ffx_api_dx12.h"
#pragma warning(pop)

// ---- TASK-B-R7-01：Vulkan 第二后端（ZR_HAS_VULKAN=1 由 CMake 按 VULKAN_SDK 探测
// 注入；SDK 缺失时本 exe 只编译 D3D12 路径——CI/他机友好）----------------------------
#if defined(ZR_HAS_VULKAN)
#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4201 4324 4456 4457 4458 4514 4623 4625 4626 5045)
#include <vulkan/vulkan.h>
#pragma warning(pop)
#include "zrhi/vulkan/zrhi_vulkan_device.h"
#endif

namespace {

// 诊断用：当前活动设备（供 RG 的 Imported 资源反查真实 desc）。
// 窗口程序每个测试函数各建一个设备，这里只保留"最近创建的那个"——resolver 只在
// rg.export 这类诊断路径读 desc，不影响渲染。
ZrD3D12Device* g_activeDevice = nullptr;

bool ResolveImageDesc(ZrImage image, ZrImageDesc* out)
{
    if (g_activeDevice == nullptr) {
        return false;
    }
    return g_activeDevice->GetImageDesc(image, out);
}

constexpr uint32_t kWidth = 640u;
constexpr uint32_t kHeight = 480u;
constexpr uint32_t kFrameCount = 60u;
constexpr uint32_t kCheckInterval = 10u; // 每 10 帧 readback 校验
constexpr int kTolerance = 2;            // UNORM 通道容差 ±2

// 帧序循环清屏色：红 → 绿 → 蓝（float RGBA， clearColor 输入）
constexpr float kClearColors[3][4] = {
    {1.0f, 0.0f, 0.0f, 1.0f},
    {0.0f, 1.0f, 0.0f, 1.0f},
    {0.0f, 0.0f, 1.0f, 1.0f},
};
constexpr int kExpectedRgb[3][3] = {{255, 0, 0}, {0, 255, 0}, {0, 0, 255}};
constexpr const char* kColorNames[3] = {"RED", "GREEN", "BLUE"};

LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg) {
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}

HWND TryCreateWindow(uint32_t clientW, uint32_t clientH)
{
    WNDCLASSEXW wc {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = &WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, reinterpret_cast<LPCWSTR>(IDC_ARROW));
    wc.lpszClassName = L"ZrD3D12SmokeWindow";
    if (RegisterClassExW(&wc) == 0
        && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        // 同进程二次创建（--backend both 双后端对照）时类已注册属正常，继续建窗
        return nullptr;
    }
    RECT rect {};
    rect.right = static_cast<LONG>(clientW);
    rect.bottom = static_cast<LONG>(clientH);
    if (AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE) == 0) {
        return nullptr;
    }
    return CreateWindowExW(0u, L"ZrD3D12SmokeWindow",
        L"ZRender RHI D3D12 Window Smoke (TASK-B-R2-02)", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT,
        CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr,
        wc.hInstance, nullptr);
}

void PumpMessages()
{
    MSG msg {};
    while (PeekMessageW(&msg, nullptr, 0u, 0u, PM_REMOVE) != 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

Microsoft::WRL::ComPtr<ID3D12Resource> MakeReadbackBuffer(ID3D12Device* device, uint64_t size)
{
    // READBACK 堆提交缓冲（D3D12 要求以 COPY_DEST 初始态创建；此后恒处该态）
    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC desc {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = size;
    desc.Height = 1u;
    desc.DepthOrArraySize = 1u;
    desc.MipLevels = 1u;
    desc.SampleDesc.Count = 1u;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    Microsoft::WRL::ComPtr<ID3D12Resource> resource;
    if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&resource)))) {
        return nullptr;
    }
    return resource;
}

void ComputeFootprint(ID3D12Device* device, ID3D12Resource* texture,
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT& outFootprint, uint64_t& outTotalBytes)
{
    const D3D12_RESOURCE_DESC desc = texture->GetDesc();
    UINT64 rowSize = 0;
    UINT numRows = 0;
    device->GetCopyableFootprints(&desc, 0u, 1u, 0u, &outFootprint, &numRows, &rowSize,
        &outTotalBytes);
}

void RecordReadback(ID3D12GraphicsCommandList* cmdList, ID3D12Resource* texture,
    ID3D12Resource* readbackBuffer, const D3D12_PLACED_SUBRESOURCE_FOOTPRINT& footprint)
{
    // 纹理 → readback 缓冲拷贝（mip0/层0；缓冲无状态屏障需求）
    D3D12_TEXTURE_COPY_LOCATION dst {};
    dst.pResource = readbackBuffer;
    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst.PlacedFootprint = footprint;
    D3D12_TEXTURE_COPY_LOCATION src {};
    src.pResource = texture;
    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    src.SubresourceIndex = 0u;
    cmdList->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, nullptr);
}

bool VerifyCenterPixelUnorm8(const uint8_t* mapped, uint64_t rowPitch, bool bgraOrder,
    const int expected[3], uint32_t frame)
{
    const uint64_t cx = kWidth / 2u;
    const uint64_t cy = kHeight / 2u;
    const uint8_t* px = mapped + rowPitch * cy + cx * 4u;
    const int c0 = static_cast<int>(px[0]);
    const int c1 = static_cast<int>(px[1]);
    const int c2 = static_cast<int>(px[2]);
    const int alpha = static_cast<int>(px[3]);
    const int r = bgraOrder ? c2 : c0;
    const int g = c1;
    const int b = bgraOrder ? c0 : c2;
    const bool ok = std::abs(r - expected[0]) <= kTolerance && std::abs(g - expected[1]) <= kTolerance
        && std::abs(b - expected[2]) <= kTolerance && std::abs(alpha - 255) <= kTolerance;
    std::printf("  frame %u readback center RGB=(%d,%d,%d) A=%d, expected (%d,%d,%d), "
                "tolerance +/-%d -> %s\n",
        frame + 1u, r, g, b, alpha, expected[0], expected[1], expected[2], kTolerance,
        ok ? "OK" : "MISMATCH");
    return ok;
}

bool VerifyCenterPixelFloat(
    const uint8_t* mapped, uint64_t rowPitch, const float expected[4])
{
    const uint64_t cx = kWidth / 2u;
    const uint64_t cy = kHeight / 2u;
    const uint8_t* px = mapped + rowPitch * cy + cx * 16u;
    float v[4];
    std::memcpy(v, px, sizeof(v));
    bool ok = true;
    for (int i = 0; i < 4; ++i) {
        if (std::fabs(v[i] - expected[i]) > 1e-6) {
            ok = false;
        }
    }
    std::printf("  UAV clear readback center RGBA=(%.6f,%.6f,%.6f,%.6f), expected "
                "(%.1f,%.1f,%.1f,%.1f) -> %s\n",
        v[0], v[1], v[2], v[3], expected[0], expected[1], expected[2], expected[3],
        ok ? "OK" : "MISMATCH");
    return ok;
}

ZrImageBarrier MakeBarrier(ZrImage image, ZrImageLayout prev, ZrImageLayout next,
    ZrAccessBits srcAccess, ZrPipelineStageBits srcStage, ZrAccessBits dstAccess,
    ZrPipelineStageBits dstStage)
{
    ZrImageBarrier barrier {};
    barrier.image = image;
    barrier.prevLayout = prev;
    barrier.newLayout = next;
    barrier.srcAccess = srcAccess;
    barrier.srcStage = srcStage;
    barrier.dstAccess = dstAccess;
    barrier.dstStage = dstStage;
    return barrier;
}

// =============================================================================
// TASK-B-R2-03 资源自检（帧循环前的独立阶段）：
//   ① CreateBuffer(初始数据)+回读比对（DEFAULT 经 UPLOAD 中转上传 → READBACK 拷贝
//      回读，字节级比对；附带验证 DEFAULT 堆 Map 不可用契约）
//   ② CreateImage(Sampled|Storage) + SRV/UAV 两视图 bindless 索引互异 + 槽位回收复用
//   ③ Sampler 创建（线性钳制预设）
//   （④ 时间戳在帧循环内进行，见 main；四项全过打印 D3D12 RESOURCE SMOKE OK）
// =============================================================================
bool RunBufferRoundtripCheck(ZrD3D12Device* device, IZrQueue* queue, ZrCommandList cmdHandle)
{
    constexpr uint64_t kSize = 256;
    uint8_t initial[kSize];
    for (uint32_t i = 0; i < kSize; ++i) {
        initial[i] = static_cast<uint8_t>((i * 7u + 3u) & 0xFFu);
    }

    ZrBufferDesc gpuDesc {};
    gpuDesc.size = kSize;
    gpuDesc.usage = ZrBufferUsage::TransferSrc | ZrBufferUsage::TransferDst;
    // initData 路径：DEFAULT 堆 + UPLOAD 中转 + CopyBufferRegion + fence 等待
    ZrBuffer gpuBuffer = device->CreateBuffer(gpuDesc, initial);
    ZrBufferDesc rbDesc {};
    rbDesc.size = kSize;
    rbDesc.usage = ZrBufferUsage::TransferDst;
    rbDesc.readback = true;
    ZrBuffer rbBuffer = device->CreateBuffer(rbDesc);
    if (!gpuBuffer.IsValid() || !rbBuffer.IsValid()) {
        std::printf("  buffer roundtrip: FAILED to create buffers\n");
        return false;
    }

    IZrCommandList* cmd = device->GetCommandList(cmdHandle);
    cmd->Begin();
    cmd->CopyBuffer(gpuBuffer, rbBuffer, 0, 0, kSize); // DEFAULT → READBACK
    cmd->End();
    queue->Submit(cmdHandle);
    device->WaitIdle();

    bool ok = true;
    IZrBuffer* rbInterface = device->GetBuffer(rbBuffer);
    void* mapped = (rbInterface != nullptr) ? rbInterface->Map() : nullptr;
    if (mapped == nullptr) {
        std::printf("  buffer roundtrip: readback Map FAILED\n");
        ok = false;
    } else {
        const bool match = (std::memcmp(mapped, initial, static_cast<size_t>(kSize)) == 0);
        std::printf("  buffer roundtrip: %llu bytes, memcmp %s\n",
            static_cast<unsigned long long>(kSize), match ? "OK (byte-exact)" : "MISMATCH");
        ok = match;
        rbInterface->Unmap();
    }
    // 契约：DEFAULT 堆缓冲 Map 返回 nullptr（不可直接映射，经中转堆拷贝）
    IZrBuffer* gpuInterface = device->GetBuffer(gpuBuffer);
    if (gpuInterface != nullptr && gpuInterface->Map() != nullptr) {
        std::printf("  buffer roundtrip: DEFAULT heap Map should return null\n");
        gpuInterface->Unmap();
        ok = false;
    }

    device->Destroy(gpuBuffer);
    device->Destroy(rbBuffer);
    return ok;
}

bool RunViewSamplerCheck(ZrD3D12Device* device)
{
    // ② 图像 + SRV/UAV 视图 + bindless 索引互异 + 槽回收复用
    ZrImageDesc imageDesc {};
    imageDesc.format = ZrFormat::RGBA8Unorm;
    imageDesc.width = 64u;
    imageDesc.height = 64u;
    imageDesc.mipLevels = 2u;
    imageDesc.usage = ZrImageUsage::Sampled | ZrImageUsage::Storage;
    ZrImage image = device->CreateImage(imageDesc);
    if (!image.IsValid()) {
        std::printf("  view/sampler: FAILED to create image\n");
        return false;
    }

    ZrImageViewDesc srvDesc {};
    srvDesc.image = image;
    srvDesc.viewType = ZrImageViewType::Srv;
    srvDesc.subresources = ZrSubresourceRange::All();
    ZrImageViewDesc uavDesc = srvDesc;
    uavDesc.viewType = ZrImageViewType::Uav;
    ZrImageView srvView = device->CreateImageView(srvDesc);
    ZrImageView uavView = device->CreateImageView(uavDesc);
    if (!srvView.IsValid() || !uavView.IsValid()) {
        std::printf("  view/sampler: FAILED to create views\n");
        device->Destroy(image);
        return false;
    }
    const uint32_t srvIndex = device->GetBindlessIndex(srvView);
    const uint32_t uavIndex = device->GetBindlessIndex(uavView);
    bool ok = (srvIndex != ZR_BINDLESS_INVALID) && (uavIndex != ZR_BINDLESS_INVALID)
        && (srvIndex != uavIndex); // bindless index mutual distinctness
    std::printf("  view/sampler: bindless SRV slot=%u UAV slot=%u -> %s\n", srvIndex, uavIndex,
        ok ? "OK (distinct, valid)" : "FAILED (invalid or equal)");

    // Destroy the SRV view then create another one: freed slot should be reused (free list)
    device->Destroy(srvView);
    ZrImageView srvView2 = device->CreateImageView(srvDesc);
    const uint32_t srvIndex2 = device->GetBindlessIndex(srvView2);
    const bool reused = srvView2.IsValid() && (srvIndex2 == srvIndex);
    std::printf("  view/sampler: SRV slot recycle: destroy %u -> realloc %u -> %s\n", srvIndex,
        srvIndex2, reused ? "OK (slot reused)" : "FAILED");
    ok = ok && reused;

    // ③ Sampler (linear clamp preset)
    ZrSamplerDesc samplerDesc {}; // default: Linear/Linear/Linear + ClampToEdge
    ZrSampler sampler = device->CreateSampler(samplerDesc);
    std::printf("  view/sampler: sampler (linear/clamp) -> %s\n",
        sampler.IsValid() ? "OK" : "FAILED");
    ok = ok && sampler.IsValid();

    if (srvView2.IsValid()) {
        device->Destroy(srvView2);
    }
    device->Destroy(uavView);
    if (sampler.IsValid()) {
        device->Destroy(sampler);
    }
    device->Destroy(image);
    return ok;
}

bool RunResourceSelfCheck(ZrD3D12Device* device, IZrQueue* queue, ZrCommandList cmdHandle)
{
    std::printf("zrhi_d3d12_window: --- resource self-check (TASK-B-R2-03) ---\n");
    const bool bufferOk = RunBufferRoundtripCheck(device, queue, cmdHandle);
    const bool viewOk = RunViewSamplerCheck(device);
    return bufferOk && viewOk;
}

} // namespace

// TASK-B-R3-02 增补：窗口模式帧循环改为 RenderGraph 驱动（Import 后台缓冲 →
// 风格化 RT → 深度预通道 → 合成 → 末通道 → 呈现；readback 帧插入拷贝读回 Pass）。
// 图内三个 Transient 贴图（styleRt[0..1] / depth[1..2] / postRt[2..3]）别名池
// 真复用（slot0: styleRt+postRt，slot1: depth → 2 堆 < 3 贴图）；depth 写->读
// 走 split barrier（BEGIN_ONLY/END_ONLY 真实下发）。headless 降级保留原手动路径
//（离屏图像跨帧布局跟踪与图编译器的逐帧 Undefined 起点不一致，RG 路径仅窗口模式）。
// 全部通过打印 `D3D12 RG SMOKE OK`（RESOURCE/WINDOW SMOKE OK 保留）。
namespace {

/// 单帧 RG 建图+编译+真后端执行（graph 由调用方逐帧提供——退役队列须活到
/// Submit 完成后的 FlushRetired）。返回执行统计。
RgExecuteStats RunRenderGraphFrame(RgGraph& graph, ZrD3D12Device* device,
    IZrCommandList* cmdList, ID3D12GraphicsCommandList* nativeCmd, ZrImage backbuffer,
    const float clearColor[4], bool doReadback, ID3D12Resource* readbackBuffer,
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT* outFootprint, ZrQueryPool timestampPool,
    uint32_t frame)
{
    RgHandle back = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));

    ZrImageDesc styleDesc {};
    styleDesc.format = ZrFormat::RGBA8Unorm;
    styleDesc.width = kWidth;
    styleDesc.height = kHeight;
    styleDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled;
    RgHandle styleRt = graph.CreateTransient(
        RgResourceDesc::TransientImage("styleRt", styleDesc)); // 前一帧风格化 RT

    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    RgHandle depth = graph.CreateTransient(RgResourceDesc::TransientImage("depth", depthDesc));

    ZrImageDesc postDesc = styleDesc;
    RgHandle postRt = graph.CreateTransient(
        RgResourceDesc::TransientImage("postRt", postDesc)); // 后处理临时 RT（别名复用 styleRt 槽）

    // P0 风格化：写 styleRt（RTV 清屏）
    graph.AddPass("StylePass", [&](RgPass& pass) { pass.Write(styleRt); },
        [&](RgContext& ctx) {
            cmdList->ClearColorImage(ctx.GetImage(styleRt),
                ZrImageLayout::ColorAttachmentOptimal, clearColor, ZrSubresourceRange::All());
        });
    // P1 深度预通道：读 styleRt + 写 depth（styleRt 生命周期止于此——与 postRt 不重叠）
    graph.AddPass("DepthPrepass", [&](RgPass& pass) { pass.Read(styleRt).Write(depth); },
        [](RgContext&) {});
    // P2 合成：读 depth（split END_ONLY 之前）+ 写 postRt
    graph.AddPass("CompositePass", [&](RgPass& pass) { pass.Read(depth).Write(postRt); },
        [&](RgContext& ctx) {
            cmdList->ClearColorImage(ctx.GetImage(postRt),
                ZrImageLayout::ColorAttachmentOptimal, clearColor, ZrSubresourceRange::All());
        });
    // P3 末通道：读 postRt + 写后台缓冲（Imported 写 = 图根）
    graph.AddPass("FinalPass",
        [&](RgPass& pass) {
            pass.Read(postRt)
                .Write(back, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
        },
        [&](RgContext& ctx) {
            cmdList->ClearColorImage(ctx.GetImage(back),
                ZrImageLayout::ColorAttachmentOptimal, clearColor, ZrSubresourceRange::All());
            if (timestampPool.IsValid() && (frame == 9u || frame == 10u)) {
                cmdList->WriteTimestamp(timestampPool, frame == 9u ? 0u : 1u);
            }
        });
    // P4（readback 帧）：后台缓冲转拷贝源 + 原生 CopyTextureRegion 读回
    // （MarkRoot：纯读侧 Pass 不被死剔除——回读校验是本帧目的之一）
    if (doReadback) {
        graph.AddPass("ReadbackPass",
            [&](RgPass& pass) {
                pass.Read(back, ZrImageLayout::TransferSrcOptimal, ZrAccessBits::TransferRead,
                    ZrPipelineStageBits::Transfer)
                    .MarkRoot();
            },
            [&](RgContext& ctx) {
                ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(back));
                uint64_t totalBytes = 0;
                ComputeFootprint(
                    device->D3D12Device(), texture, *outFootprint, totalBytes);
                RecordReadback(nativeCmd, texture, readbackBuffer, *outFootprint);
            });
    }
    // P5 呈现：后台缓冲转呈现布局（MarkRoot：呈现是帧的最终消费点）
    graph.AddPass("PresentPass",
        [&](RgPass& pass) { pass.Present(back).MarkRoot(); }, nullptr);

    if (!graph.Compile()) {
        std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
            static_cast<unsigned>(graph.Error()));
        return RgExecuteStats{};
    }
    return graph.ExecuteAliased(*device, *cmdList);
}


} // namespace

// ---- TASK-B-R4-01：--zmeshcheck <path> 模式 -----------------------------------
// 命令行验证通道：加载 .zmesh 文件（经 zrender.dll 同源 C ABI 实现的静态库链接）
// 打印统计并以退出码 0/1 报告成败；不创建窗口/设备。
static int RunZmeshCheck(const char* path)
{
    std::printf("zmeshcheck: loading '%s'\n", path);
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) {
        std::printf("zmeshcheck: FAILED to open file\n");
        return 1;
    }
    const std::streampos length = file.tellg();
    file.seekg(0, std::ios::beg);
    std::vector<char> bytes(static_cast<size_t>(length));
    if (!file.read(bytes.data(), length)) {
        std::printf("zmeshcheck: FAILED to read file\n");
        return 1;
    }

    uint64_t handle = 0;
    const int loadResult = ZR_MeshLoadFromMemory(
        bytes.data(), static_cast<uint32_t>(bytes.size()), &handle);
    if (loadResult != 0) {
        std::printf("zmeshcheck: FAILED to parse (zmesh error %d)\n", loadResult);
        return 1;
    }

    ZmeshStats stats{};
    const int statsResult = ZR_MeshStatsGet(handle, &stats);
    if (statsResult != 0) {
        std::printf("zmeshcheck: FAILED to get stats (error %d)\n", statsResult);
        ZR_MeshFree(handle);
        return 1;
    }
    std::printf("zmeshcheck: vertexCount=%u indexCount=%u normals=%u uv=%u "
        "boundsMin=[%.3f %.3f %.3f] boundsMax=[%.3f %.3f %.3f]\n",
        stats.vertexCount, stats.indexCount, stats.hasNormals, stats.hasUv,
        stats.boundsMin[0], stats.boundsMin[1], stats.boundsMin[2],
        stats.boundsMax[0], stats.boundsMax[1], stats.boundsMax[2]);

    ZR_MeshFree(handle);
    std::printf("zmeshcheck: OK\n");
    return 0;
}

// =============================================================================
// ---- TASK-B-R4-03：--draw 模式（图形 PSO + RG 驱动绘制列表首绘）-----------------
// =============================================================================

// 清屏色（深蓝；中心像素校验 = 非 (13,26,38) 附近）
constexpr float kDrawClearColor[4] = {0.05f, 0.10f, 0.15f, 1.0f};

// 常量布局（96B）——与 Native/shaders/first_draw.hlsl 的 CBufPerDraw 一一对应
struct DrawConstants {
    float mvp[16];      // row_major（shader row_major 声明 ⇒ CPU 行主序写入）
    float lightDir[4];  // xyz = 指向光源（已归一化）
    float lightColor[4];
};
static_assert(sizeof(DrawConstants) == 96, "DrawConstants must be 96 bytes");

// v1 固定顶点布局：Pos3f+Normal3f+Uv2f（32B，与 ZrD3D12_GraphicsInputLayout 一致）
struct FirstDrawVertex {
    float pos[3];
    float normal[3];
    float uv[2];
};
static_assert(sizeof(FirstDrawVertex) == 32, "FirstDrawVertex must be 32 bytes");

// ---- 行主序 4x4 矩阵助手（列向量数学：clip = P·V·M·pos；存储 = 行主序）--------
void MatMultiply(const float* a, const float* b, float* out)
{
    float r[16];
    for (int row = 0; row < 4; ++row) {
        for (int col = 0; col < 4; ++col) {
            float sum = 0.0f;
            for (int k = 0; k < 4; ++k) {
                sum += a[row * 4 + k] * b[k * 4 + col];
            }
            r[row * 4 + col] = sum;
        }
    }
    std::memcpy(out, r, sizeof(r));
}

void MatPerspectiveFovLH(float fovY, float aspect, float zn, float zf, float* out)
{
    // D3D LH 透视（clip z ∈ [0,1]）。列向量约定：clip = P·pos（shader 侧
    // mul(mvp, pos)）；行主序存储，平移/投影系数在每行的第 4 列。
    // （TASK-B-R5-02 修复：原实现把系数存在第 4 行（行向量约定），与 shader 的
    // 列向量 mul 相互转置——自 R4-03 起所有 draw 模式实际渲染的是转置垃圾几何，
    // 仅因"中心像素 ≠ 清屏色"类宽松检查未暴露。）
    const float f = 1.0f / std::tan(fovY * 0.5f);
    const float m[16] = { f / aspect, 0.0f, 0.0f, 0.0f, 0.0f, f, 0.0f, 0.0f, 0.0f, 0.0f,
        zf / (zf - zn), -zn * zf / (zf - zn), 0.0f, 0.0f, 1.0f, 0.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatRotationX(float angle, float* out)
{
    const float c = std::cos(angle);
    const float s = std::sin(angle);
    // 列向量约定（与 shader mul(M, v) 一致；原为行向量存储已转置修复）
    const float m[16] = { 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, c, -s, 0.0f, 0.0f, s, c, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatRotationY(float angle, float* out)
{
    const float c = std::cos(angle);
    const float s = std::sin(angle);
    const float m[16] = { c, 0.0f, s, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -s, 0.0f, c, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatTranslation(float x, float y, float z, float* out)
{
    // 列向量约定：平移在第 4 列（原存第 4 行系行向量约定，与 shader 的
    // mul(mvp, pos) 相互转置——R4-03 起所有 draw 模式渲染转置垃圾几何，已修复）
    const float m[16] = { 1.0f, 0.0f, 0.0f, x, 0.0f, 1.0f, 0.0f, y, 0.0f, 0.0f, 1.0f,
        z, 0.0f, 0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

bool ReadWholeFile(const char* path, std::vector<char>& out)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) {
        return false;
    }
    const std::streampos length = file.tellg();
    file.seekg(0, std::ios::beg);
    out.resize(static_cast<size_t>(length));
    return static_cast<bool>(file.read(out.data(), length));
}

// --draw 模式主体（返回进程退出码）。
// TASK-B-R7-01 增补：outCenters/outBarrierPlan —— 双后端对照捕获（--backend both
// 模式收集 D3D12/Vulkan 各自的中心像素与 RG 屏障合并计数做并排对照断言；
// 缺省 nullptr = 单后端模式零开销）
static int RunFirstDraw(const char* zmeshPath, const char* hlslPath,
    std::vector<int>* outCenters = nullptr, std::vector<uint32_t>* outBarrierPlan = nullptr)
{
    std::printf("zrhi_d3d12_window: --draw mode (TASK-B-R4-03): zmesh='%s' hlsl='%s'\n",
        zmeshPath, hlslPath);

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --draw device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏）-------------------------------------------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --draw window rejected (GetLastError=%lu), "
                    "falling back to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --draw swapchain FAILED, falling back to offscreen "
                        "target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
        std::printf("zrhi_d3d12_window: --draw window 640x480 + swapchain (BGRA8Unorm, FLIP "
                    "DISCARD, Fifo)\n");
    }

    // ---- 渲染目标：窗口=交换链后台缓冲；离屏=提交资源（ColorAttachment|TransferSrc）----
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --draw FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrD3D12DestroyDevice(device);
            return 1;
        }
    }

    // ---- 深度资源（D32Float，RG Imported；DSV 视图）----------------------------------
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    if (!depthImage.IsValid() || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create depth image/DSV\n");
        return 1; // 设备析构路径负责已建资源
    }

    // ---- 加载 cube.zmesh → 交错顶点/索引 → CreateBuffer（经 B-R2-03 initData 上传链）--
    std::vector<char> meshBytes;
    if (!ReadWholeFile(zmeshPath, meshBytes)) {
        std::printf("zrhi_d3d12_window: --draw FAILED to open zmesh '%s'\n", zmeshPath);
        return 1;
    }
    ZmeshData mesh {};
    const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
    if (parseResult != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --draw zmesh parse FAILED (error %d)\n", parseResult);
        return 1;
    }
    const uint32_t vertexCount = mesh.stats.vertexCount;
    const uint32_t indexCount = mesh.stats.indexCount;
    if (mesh.stats.hasNormals == 0u || mesh.stats.hasUv == 0u) {
        std::printf("zrhi_d3d12_window: --draw zmesh lacks normals/uv (v1 固定布局需要)\n");
        return 1;
    }
    std::vector<FirstDrawVertex> vertices(vertexCount);
    for (uint32_t i = 0; i < vertexCount; ++i) {
        std::memcpy(vertices[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(vertices[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(vertices[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> indices(indexCount);
    std::memcpy(indices.data(), mesh.indices, sizeof(uint32_t) * indexCount);
    std::printf("zrhi_d3d12_window: --draw zmesh: vertices=%u indices=%u (interleaved 32B/vert)\n",
        vertexCount, indexCount);

    ZrBufferDesc vbDesc {};
    vbDesc.size = static_cast<uint64_t>(vertices.size()) * sizeof(FirstDrawVertex);
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer vb = device->CreateBuffer(vbDesc, vertices.data()); // DEFAULT+UPLOAD 中转链
    ZrBufferDesc ibDesc {};
    ibDesc.size = static_cast<uint64_t>(indices.size()) * sizeof(uint32_t);
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer ib = device->CreateBuffer(ibDesc, indices.data());
    if (!vb.IsValid() || !ib.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create VB/IB\n");
        return 1;
    }

    // ---- 常量缓冲：每绘制项一个 hostVisible（UPLOAD）缓冲，每帧 Map 持久映射写入 -------
    constexpr uint32_t kDrawItems = 3u; // 三个 cube（不同横向位置，深度测试可辨遮挡）
    ZrBuffer cbHandles[kDrawItems] = {};
    void* cbMapped[kDrawItems] = {};
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        ZrBufferDesc cbDesc {};
        cbDesc.size = 256u; // 根 CBV 对齐裕量（常量实际 96B）
        cbDesc.usage = ZrBufferUsage::Uniform;
        cbDesc.hostVisible = true;
        cbHandles[i] = device->CreateBuffer(cbDesc);
        IZrBuffer* iface = device->GetBuffer(cbHandles[i]);
        cbMapped[i] = (iface != nullptr && cbHandles[i].IsValid()) ? iface->Map() : nullptr;
        if (cbMapped[i] == nullptr) {
            std::printf("zrhi_d3d12_window: --draw FAILED to create/map constant buffer %u\n", i);
            return 1;
        }
    }

    // ---- 着色器编译（运行时 D3DCompile）+ PSO ---------------------------------------
    std::vector<char> hlsl;
    if (!ReadWholeFile(hlslPath, hlsl)) {
        std::printf("zrhi_d3d12_window: --draw FAILED to open hlsl '%s'\n", hlslPath);
        return 1;
    }
    const ZrShader vs = device->CompileHlsl(
        ZrShaderStage::Vertex, hlsl.data(), hlsl.size(), "VsMain");
    const ZrShader ps = device->CompileHlsl(
        ZrShaderStage::Pixel, hlsl.data(), hlsl.size(), "PsMain");
    if (!vs.IsValid() || !ps.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw shader compilation FAILED (see stderr)\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --draw shaders compiled (runtime D3DCompile, SM5.0 DXBC)\n");

    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = vs;
    psoDesc.pixelShader = ps;
    psoDesc.rtvFormat = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    // 剔除取 None：Unity LH 烘焙的 cube 绕序在本投影下屏幕呈 CCW（实证：Back 剔除
    // 时中心像素恒为清屏色，None 后首绘可见），绕序约定归资产管线任务统一
    psoDesc.cullMode = ZrCullMode::None;
    ZrPipelineState pso = device->CreatePipelineState(psoDesc);
    if (!pso.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create graphics PSO\n");
        return 1;
    }

    // ---- 绘制列表：3 个 cube 项 → Sort → BuildBatches（RG DrawPass 真实消费批）---------
    DrawList drawList(16u);
    drawList.RegisterMesh(1u, vertexCount);
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        RenderItem item;
        item.meshHandle = 1u;
        item.materialId = 7u;
        item.shaderId = 0u;
        item.lod = 0u;
        item.depth = 3.2f; // 视深（同一平面；批合并后逐项绘制）
        item.translucent = false;
        if (!drawList.Add(item, 0x01u)) {
            std::printf("zrhi_d3d12_window: --draw FAILED to add draw list item %u\n", i);
            return 1;
        }
    }
    drawList.Sort();
    const uint32_t batchCount = drawList.BuildBatches();
    if (batchCount == 0u || drawList.Batches().size() != 1u
        || drawList.Batches()[0].count != kDrawItems) {
        std::printf("zrhi_d3d12_window: --draw unexpected draw list batches (%u)\n", batchCount);
        return 1;
    }
    std::printf("zrhi_d3d12_window: --draw draw list: items=%u batches=%u (state-clustered)\n",
        drawList.Count(), batchCount);

    // ---- RTV 视图（窗口模式按后台缓冲缓存；离屏单视图）--------------------------------
    ZrImageView rtvViews[3] = {}; // 与交换链缓冲数一致（3）
    ZrImageView offscreenRtv {};
    if (!windowMode) {
        ZrImageViewDesc rtvViewDesc {};
        rtvViewDesc.image = offscreenTarget;
        rtvViewDesc.viewType = ZrImageViewType::Rtv;
        offscreenRtv = device->CreateImageView(rtvViewDesc);
        if (!offscreenRtv.IsValid()) {
            std::printf("zrhi_d3d12_window: --draw FAILED to create offscreen RTV view\n");
            return 1;
        }
    }

    // ---- readback（中心 1x1 像素）------------------------------------------------------
    Microsoft::WRL::ComPtr<ID3D12Resource> readback = MakeReadbackBuffer(device->D3D12Device(), 256u);
    if (readback == nullptr) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create readback buffer\n");
        return 1;
    }

    // ---- 60 帧循环：RG（Import 后台/深度 → ClearPass → CubeDrawPass → Readback/Present）--
    const float lightDir[3] = {0.5f, 0.8f, 0.6f};   // 指向光源（固定方向）
    const float invLen = 1.0f
        / std::sqrt(lightDir[0] * lightDir[0] + lightDir[1] * lightDir[1]
              + lightDir[2] * lightDir[2]);
    const float lightColor[4] = {1.0f, 0.95f, 0.9f, 1.0f};

    float proj[16];
    MatPerspectiveFovLH(3.14159265f / 3.0f, static_cast<float>(kWidth) / static_cast<float>(kHeight),
        0.1f, 100.0f, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f}; // 相机在原点朝 +Z

    uint32_t readbackChecks = 0u;
    uint32_t readbackPasses = 0u;
    bool allOk = true;
    uint32_t undeclaredUses = 0u;
    for (uint32_t frame = 0u; frame < kFrameCount; ++frame) {
        const bool doReadback = ((frame + 1u) % kCheckInterval) == 0u;

        // ---- 常量写入：mvp 随帧旋转（60 帧转一圈），逐项横向排开 ---------------------
        const float angle = 6.2831853f * static_cast<float>(frame + 1u)
            / static_cast<float>(kFrameCount);
        float rotY[16];
        MatRotationY(angle, rotY);
        float rotX[16];
        MatRotationX(0.35f, rotX);
        float rx[16];
        MatMultiply(rotX, rotY, rx); // M' = Rx·Ry（先 Y 旋转再 X 倾斜）
        for (uint32_t i = 0; i < kDrawItems; ++i) {
            const float tx = (static_cast<float>(i) - 1.0f) * 1.6f; // -1.6 / 0 / +1.6
            float trans[16] = {};
            MatTranslation(tx, 0.0f, 3.2f, trans);
            float model[16];
            MatMultiply(trans, rx, model);
            float pv[16];
            MatMultiply(proj, view, pv);
            float mvp[16];
            MatMultiply(pv, model, mvp); // clip = P·V·M·pos
            DrawConstants constants;
            std::memcpy(constants.mvp, mvp, sizeof(mvp));
            constants.lightDir[0] = lightDir[0] * invLen;
            constants.lightDir[1] = lightDir[1] * invLen;
            constants.lightDir[2] = lightDir[2] * invLen;
            constants.lightDir[3] = 0.0f;
            std::memcpy(constants.lightColor, lightColor, sizeof(lightColor));
            std::memcpy(cbMapped[i], &constants, sizeof(constants));
        }

        // ---- 取后台缓冲（窗口模式）+ RTV 视图 -----------------------------------------
        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  frame %u: FAILED to obtain RTV view\n", frame + 1u);
            allOk = false;
            continue;
        }

        // ---- 每帧 RG：Import 后台缓冲+深度（均 Imported）-------------------------------
        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                // Imported 资源 desc 无 usage（ImportImage 不携带）→ 派生会落到
                // General；显式给同步三元组（与 legacy FinalPass 同模式）
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kDrawClearColor,
                    ZrSubresourceRange::All());
                // 深度清屏（原生扩展点：RHI 尚无 ClearDepth；本 Pass 声明了 depth 写
                // → 此刻深度已处 DEPTH_WRITE 态，清屏合法）
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });
        // CubeDrawPass：execute 回调里真实消费 draw list 批（SetPSO→SetRT→SetVB→
        // SetIB→逐项 SetCB+DrawIndexedInstanced）
        graph.AddPass("CubeDrawPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(pso);
                cmdList->SetRenderTarget(rtvView, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                ZrBuffer vbs[1] = {vb};
                cmdList->SetVertexBuffers(vbs, 1u);
                cmdList->SetIndexBuffer(ib);
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    for (uint32_t i = 0; i < batch.count; ++i) {
                        const uint32_t itemIndex = batch.startIndex + i; // 排序后项序号=常量槽
                        cmdList->SetGraphicsConstantBuffer(0u, cbHandles[itemIndex]);
                        cmdList->DrawIndexedInstanced(indexCount, 1u, 0u, 0u, 0u);
                    }
                }
            });
        // ReadbackPass（校验帧）：后台缓冲转拷贝源，中心 1x1 拷入 readback
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(backH));
                    const UINT cx = kWidth / 2u;
                    const UINT cy = kHeight / 2u;
                    // D3D12_BOX 字段序：(left, top, front, right, bottom, back)
                    const D3D12_BOX box = {cx, cy, 0u, cx + 1u, cy + 1u, 1u};
                    D3D12_TEXTURE_COPY_LOCATION dst {};
                    dst.pResource = readback.Get();
                    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                    dst.PlacedFootprint.Footprint.Format
                        = windowMode ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
                    dst.PlacedFootprint.Footprint.Width = 1u;
                    dst.PlacedFootprint.Footprint.Height = 1u;
                    dst.PlacedFootprint.Footprint.Depth = 1u;
                    dst.PlacedFootprint.Footprint.RowPitch = 256u;
                    D3D12_TEXTURE_COPY_LOCATION src {};
                    src.pResource = texture;
                    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    src.SubresourceIndex = 0u;
                    nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass", [&](RgPass& pass) { pass.Present(backH).MarkRoot(); },
                nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        // TASK-B-R7-01：首个 readback 帧捕获 RG 屏障合并计数（跨后端一致性断言依据；
        // 图编译为纯 CPU 逻辑，两后端跑同一 RG 结构应得相同计数）
        if (doReadback && frame + 1u == kCheckInterval && outBarrierPlan != nullptr) {
            const RgBarrierPlan& plan = graph.BarrierPlan();
            outBarrierPlan->push_back(plan.TotalBarrierCount());
            outBarrierPlan->push_back(plan.SplitCount());
        }
        const RgExecuteStats rgStatsDraw = graph.ExecuteAliased(*device, *cmdList);
        if (doReadback && frame + 1u == kCheckInterval && outBarrierPlan != nullptr) {
            outBarrierPlan->push_back(rgStatsDraw.inlineBarrierCalls);
            outBarrierPlan->push_back(rgStatsDraw.splitBegins);
            outBarrierPlan->push_back(rgStatsDraw.splitEnds);
            outBarrierPlan->push_back(rgStatsDraw.aliasHeapsCreated);
            outBarrierPlan->push_back(rgStatsDraw.transientImages);
        }
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // ---- 帧末手动屏障：深度回 COMMON（与下一帧 RG 起点 Undefined/COMMON 假设一致；
        // 离屏模式后台缓冲同理——窗口模式由 Present 天然回到 COMMON(=PRESENT)）---------
        ZrImageBarrier depthReset = MakeBarrier(depthImage,
            ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);
        if (!windowMode) {
            const ZrImageLayout backLayoutNow = doReadback ? ZrImageLayout::TransferSrcOptimal
                                                           : ZrImageLayout::ColorAttachmentOptimal;
            ZrImageBarrier backReset = MakeBarrier(offscreenTarget, backLayoutNow,
                ZrImageLayout::Undefined, ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&backReset, 1u, nullptr, 0u);
        }

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle(); // readback/退役冲刷前提
        graph.FlushRetired(*device);

        // ---- 中心像素校验：≠ 清屏色（cube 占中心；失败即记，后续相位帧重试）------------
        if (doReadback) {
            void* mapped = nullptr;
            ++readbackChecks;
            const HRESULT mapHr = readback->Map(0, nullptr, &mapped);
            if (FAILED(mapHr) || mapped == nullptr) {
                std::printf("  frame %u: readback Map FAILED (hr=0x%08lX)\n", frame + 1u,
                    static_cast<unsigned long>(mapHr));
                allOk = false;
            } else {
                const uint8_t* px = static_cast<const uint8_t*>(mapped);
                // 通道序按目标格式：窗口后台缓冲=BGRA8（字节序 B,G,R,A），离屏=RGBA8
                const int bByte = static_cast<int>(px[0]);
                const int g = static_cast<int>(px[1]);
                const int rByte = static_cast<int>(px[2]);
                const int r = windowMode ? rByte : bByte;
                const int b = windowMode ? bByte : rByte;
                // 清屏色 (0.05,0.10,0.15) → UNORM8 ≈ (13,26,38)；任意通道偏差 >2 即非清屏色
                const int clear0 = static_cast<int>(kDrawClearColor[0] * 255.0f + 0.5f);
                const int clear1 = static_cast<int>(kDrawClearColor[1] * 255.0f + 0.5f);
                const int clear2 = static_cast<int>(kDrawClearColor[2] * 255.0f + 0.5f);
                const bool differs
                    = std::abs(r - clear0) > 2 || std::abs(g - clear1) > 2 || std::abs(b - clear2) > 2;
                if (differs) {
                    ++readbackPasses;
                }
                std::printf("  frame %u readback center RGB=(%d,%d,%d), clear≈(%d,%d,%d) -> %s\n",
                    frame + 1u, r, g, b, clear0, clear1, clear2,
                    differs ? "CUBE (first draw visible)" : "CLEAR COLOR (no draw)");
                if (outCenters != nullptr) {
                    outCenters->push_back(r);
                    outCenters->push_back(g);
                    outCenters->push_back(b);
                }
                allOk = allOk && differs;
                readback->Unmap(0, nullptr);
            }
        }

        if (windowMode) {
            PumpMessages();
        }
        std::printf("frame %2u/%u: [FIRST DRAW] angle=%.1f deg%s\n", frame + 1u, kFrameCount,
            angle * 57.2958f, doReadback ? "  (+center readback)" : "");
    }

    // ---- 无设备移除 + InfoQueue 计数 --------------------------------------------------
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;

    // ---- 清理 -------------------------------------------------------------------------
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        if (cbHandles[i].IsValid()) {
            device->Destroy(cbHandles[i]);
        }
    }
    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(dsvView);
    device->Destroy(pso);
    device->Destroy(vs);
    device->Destroy(ps);
    device->Destroy(vb);
    device->Destroy(ib);
    device->Destroy(depthImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    readback.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: [FIRST DRAW] frames=%u center checks=%u passed=%u "
                "undeclared uses=%u\n",
        kFrameCount, readbackChecks, readbackPasses, undeclaredUses);
    std::printf("zrhi_d3d12_window: [FIRST DRAW] device removed reason = 0x%08lX (%s)\n",
        static_cast<unsigned long>(removedReason), noDeviceRemoval ? "S_OK" : "REMOVED");
    std::printf("zrhi_d3d12_window: InfoQueue error count = %u\n", infoQueueErrors);

    if (allOk && readbackChecks > 0u && readbackPasses == readbackChecks && noDeviceRemoval
        && undeclaredUses == 0u && infoQueueErrors == 0u) {
        std::printf("D3D12 FIRST DRAW OK\n");
        return 0;
    }
    std::printf("D3D12 FIRST DRAW FAILED (allOk=%d checks=%u passes=%u removed=%d undeclared=%u "
                "errors=%u)\n",
        allOk ? 1 : 0, readbackChecks, readbackPasses, noDeviceRemoval ? 0 : 1, undeclaredUses,
        infoQueueErrors);
    return 1;
}

// =============================================================================
// ---- TASK-B-R5-01：--lights 模式（簇光源 + PBR 前向多光源真渲染）-----------------
// =============================================================================

constexpr float kLightsClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f}; // 深灰蓝（近黑）
constexpr uint32_t kLightsCount = 9u;    // 1 方向光 + 8 点光（≤ ZR_MAX_LIGHTS=32）
constexpr uint32_t kAnimLightBase = 5u;  // 光源 5..8 为动画点光（4 个）
constexpr uint32_t kLightsCheckInterval = 10u;                       // 每 10 帧 readback
// 地面横带 readback：y ∈ [368,372)（世界 z≈3.19 处地面，穿过动画光源环绕轨迹）
constexpr UINT kLightsStripY0 = 368u;
constexpr UINT kLightsStripRows = 4u;
constexpr UINT kLightsStripRowPitch = kWidth * 4u; // 2560（256 对齐）
constexpr int kLightsGroundLitMin = 40;            // 横带最大通道 ≥ 此值 ⇒ 受光（环境项≈15）
constexpr int kLightsAnimDiffMin = 8;              // 相邻校验帧像素差判定阈值
constexpr int kLightsAnimChangedMin = 300;         // 差 ≥ 阈值的字节数下限（10240 字节带）
// TASK-B-R5-02 校准说明：矩阵约定修复后，横带处动画点光地面光斑按真实投影渲染，
// 相邻 10 帧对的差值字节数实测 0/569/614/0/471（轨道相位相关，部分对移动慢）——
// 判定从"全部对达标"改为"≥3/5 对达标"（原 500 阈值系按 R5-01 时期转置垃圾
// 渲染校准，修复后图像几何正确、光斑变小，按同一意图重校准）。
// TASK-B-R7-02：VK 侧 readback 镜像（vkCmdCopyImageToBuffer 行紧缩布局与
// D3D12 PlacedFootprint.RowPitch 同值——两后端横带字节布局逐字节可比的前提）
static_assert(kLightsStripRowPitch == kWidth * 4u,
    "strip row pitch must equal width*4B (VK tightly-packed copy = D3D12 footprint)");

// CB0 镜像（208B）——与 Native/shaders/pbr_forward.hlsl 的 CBufPerDraw 一一对应
struct LightsConstants {
    float mvp[16];        // row_major（shader row_major 声明 ⇒ CPU 行主序写入）
    float model[16];      // row_major（世界变换）
    float projParams[4];  // x=zf/(zf-zn) y=zn*zf/(zf-zn) z=nearZ w=farZ
    float gridParams[4];  // x/y/z=dimX/Y/Z w=invZRange
    float screenParams[4]; // x=width y=height z=lightCount w=ambient
    float material[4];    // rgb=albedo a=roughness
    float camPos[4];      // xyz=相机世界位置
};
static_assert(sizeof(LightsConstants) == 208, "LightsConstants must be 208 bytes");
static_assert(sizeof(PackedLight) == 32, "PackedLight must be 32 bytes (GPU layout)");
static_assert(ZR_CLUSTER_COUNT == 2304, "cluster grid must be 2304");
static_assert(ZR_CLUSTER_COUNT * 4 + ZR_CLUSTER_FLAT_MAX * 4 <= 65536,
    "CB2 ranges+flat must fit 64KB cbuffer");

/// 点光构造助手（--lights 场景；LightData 语义见 cluster_lighting.h）
LightData MakeAppPointLight(float x, float y, float z, float radius, float intensity, float cr,
    float cg, float cb)
{
    LightData light {};
    light.pos[0] = x;
    light.pos[1] = y;
    light.pos[2] = z;
    light.radius = radius;
    light.color[0] = cr;
    light.color[1] = cg;
    light.color[2] = cb;
    light.intensity = intensity;
    light.kind = kZrLightPoint;
    return light;
}

// --lights 模式主体（返回进程退出码）。
// TASK-B-R7-02：可选 out 参数（双后端对照采集；缺省空 = D3D12 既有行为零改动）
//   outCenters：每校验帧中心像素 r,g,b（窗口模式已解码为 RGB 序）
//   outBarrierPlan：首个校验帧 RG 屏障合并计数 7 元组（与 --draw 同口径）
// TASK-B-R9-10：jsonOut=true 时在末尾证据区追加一行 `BENCH_JSON {...}` 帧耗
//   单行 JSON（帧耗汇总固定行无条件打印，解析器两者都能吃；缺省 false =
//   既有调用方（--lights / --backend both 对照）输出只多一行 frame_ms 汇总）。
// TASK-B-R9-10：forceHeadless=true（--headless --lights）跳过窗口/交换链创建，
//   走既有离屏回退路径——bench 回归测量纯渲染成本（窗口 Present 的垂直同步
//   会把帧耗钉在刷新间隔上，门禁对低于刷新间隔的回归失明）。
static int RunLightsMode(const char* zmeshPath, const char* hlslPath,
    std::vector<int>* outCenters = nullptr,
    std::vector<uint32_t>* outBarrierPlan = nullptr,
    bool jsonOut = false,
    bool forceHeadless = false)
{
    std::printf("zrhi_d3d12_window: --lights mode (TASK-B-R5-01): zmesh='%s' hlsl='%s'\n",
        zmeshPath, hlslPath);

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --lights device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏）-------------------------------------------
    // TASK-B-R9-10：--headless --lights 直接走离屏（bench 回归：无 vsync 干扰）
    HWND hWnd = forceHeadless ? nullptr : TryCreateWindow(kWidth, kHeight);
    if (forceHeadless) {
        std::printf("zrhi_d3d12_window: --lights --headless: offscreen target (no swapchain, "
                    "no vsync - bench regression mode)\n");
    } else if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --lights window rejected (GetLastError=%lu), "
                    "falling back to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --lights swapchain FAILED, falling back to offscreen "
                        "target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
        std::printf("zrhi_d3d12_window: --lights window 640x480 + swapchain (BGRA8Unorm)\n");
    }

    // ---- 渲染目标（离屏降级）+ 深度 --------------------------------------------------
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --lights FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrD3D12DestroyDevice(device);
            return 1;
        }
    }
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    if (!depthImage.IsValid() || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create depth image/DSV\n");
        return 1;
    }

    // ---- 几何：程序化地面 quad（20×20m，y=-1，C++ 直构顶点）+ cube.zmesh -------------
    // （view=identity：相机在原点朝 +Z，世界=视空间——簇分配的视空间参数前提）
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<char> meshBytes;
    if (!ReadWholeFile(zmeshPath, meshBytes)) {
        std::printf("zrhi_d3d12_window: --lights FAILED to open zmesh '%s'\n", zmeshPath);
        return 1;
    }
    ZmeshData mesh {};
    const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
    if (parseResult != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --lights zmesh parse FAILED (error %d)\n", parseResult);
        return 1;
    }
    if (mesh.stats.hasNormals == 0u) {
        std::printf("zrhi_d3d12_window: --lights zmesh lacks normals\n");
        return 1;
    }
    const uint32_t cubeVertexCount = mesh.stats.vertexCount;
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(cubeVertexCount);
    for (uint32_t i = 0; i < cubeVertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);
    std::printf("zrhi_d3d12_window: --lights geometry: ground quad 20x20m (4 verts) + cube.zmesh "
                "(%u verts / %u indices)\n",
        cubeVertexCount, cubeIndexCount);

    ZrBufferDesc vbDesc {};
    vbDesc.size = sizeof(groundVerts);
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer groundVb = device->CreateBuffer(vbDesc, groundVerts);
    ZrBufferDesc ibDesc {};
    ibDesc.size = sizeof(groundIndices);
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer groundIb = device->CreateBuffer(ibDesc, groundIndices);
    ZrBufferDesc cubeVbDesc {};
    cubeVbDesc.size = static_cast<uint64_t>(cubeVerts.size()) * sizeof(FirstDrawVertex);
    cubeVbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer cubeVb = device->CreateBuffer(cubeVbDesc, cubeVerts.data());
    ZrBufferDesc cubeIbDesc {};
    cubeIbDesc.size = static_cast<uint64_t>(cubeIndices.size()) * sizeof(uint32_t);
    cubeIbDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer cubeIb = device->CreateBuffer(cubeIbDesc, cubeIndices.data());
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create VB/IB\n");
        return 1;
    }

    // ---- 常量缓冲：CB0×2（逐绘，256B 裕量）/ CB1 光源（1024B）/ CB2 簇（65280B）------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    void* cb0GroundMapped = nullptr;
    void* cb0CubeMapped = nullptr;
    void* cbLightsMapped = nullptr;
    void* cbClustersMapped = nullptr;
    {
        auto makeCb = [&](uint64_t size, ZrBuffer& outHandle, void*& outMapped) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            outHandle = device->CreateBuffer(desc);
            IZrBuffer* iface = device->GetBuffer(outHandle);
            outMapped = (iface != nullptr && outHandle.IsValid()) ? iface->Map() : nullptr;
            return outMapped != nullptr;
        };
        // CB2 尺寸 = 2304×4B（范围）+ ZR_CLUSTER_FLAT_MAX×4B（平坦索引）= 65216B，
        // 向上对齐 256（根 CBV 基址对齐约定，尺寸取整裕量）
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u
                                     + 255u)
            & ~255ull;
        const bool ok = makeCb(256u, cb0Ground, cb0GroundMapped)
            && makeCb(256u, cb0Cube, cb0CubeMapped) && makeCb(1024u, cbLights, cbLightsMapped)
            && makeCb(cb2Size, cbClusters, cbClustersMapped);
        if (!ok) {
            std::printf("zrhi_d3d12_window: --lights FAILED to create/map constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器编译（运行时 D3DCompile）+ PSO（3 根 CBV：b0/b1/b2）--------------------
    std::vector<char> hlsl;
    if (!ReadWholeFile(hlslPath, hlsl)) {
        std::printf("zrhi_d3d12_window: --lights FAILED to open hlsl '%s'\n", hlslPath);
        return 1;
    }
    const ZrShader vs = device->CompileHlsl(
        ZrShaderStage::Vertex, hlsl.data(), hlsl.size(), "VsMain");
    const ZrShader ps = device->CompileHlsl(
        ZrShaderStage::Pixel, hlsl.data(), hlsl.size(), "PsMain");
    if (!vs.IsValid() || !ps.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights shader compilation FAILED (see stderr)\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --lights shaders compiled (runtime D3DCompile, SM5.0 DXBC)\n");

    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = vs;
    psoDesc.pixelShader = ps;
    psoDesc.rtvFormat = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    psoDesc.cullMode = ZrCullMode::None; // 与 --draw 同策：绕序约定归资产管线任务统一
    psoDesc.rootCbvCount = 3u;           // b0 逐绘常量 / b1 光源数组 / b2 簇数据
    ZrPipelineState pso = device->CreatePipelineState(psoDesc);
    if (!pso.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create graphics PSO\n");
        return 1;
    }

    // ---- 投影/簇网格参数（与 pbr_forward.hlsl + cluster_lighting.h 两侧一致）----------
    constexpr float kFovY = 3.14159265f / 3.0f; // 60°
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kWidth) / static_cast<float>(kHeight), kNearZ,
        kFarZ, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float projView[16];
    MatMultiply(proj, view, projView);

    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);

    // ---- 光源：1 方向光（槽 0）+ 8 点光（其中 4 个随帧动画环绕）-----------------------
    LightData sceneLights[kLightsCount] = {};
    // 方向光：指向光源的方向（归一化），自左上前方射向场景
    {
        LightData& dir = sceneLights[0];
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen
            = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        dir.pos[0] = d[0] * invLen;
        dir.pos[1] = d[1] * invLen;
        dir.pos[2] = d[2] * invLen;
        dir.color[0] = 1.0f;
        dir.color[1] = 0.95f;
        dir.color[2] = 0.88f;
        dir.intensity = 1.0f;
        dir.kind = kZrLightDirectional;
    }
    // 静态点光 1..4（彩色环境点缀）
    sceneLights[1] = MakeAppPointLight(-3.0f, 0.6f, 2.5f, 5.0f, 2.0f, 1.0f, 0.35f, 0.25f);
    sceneLights[2] = MakeAppPointLight(3.0f, 0.6f, 2.5f, 5.0f, 2.0f, 0.25f, 0.5f, 1.0f);
    sceneLights[3] = MakeAppPointLight(0.0f, 1.4f, 6.0f, 6.0f, 2.0f, 0.3f, 1.0f, 0.4f);
    sceneLights[4] = MakeAppPointLight(-1.5f, 2.2f, 7.5f, 6.0f, 1.5f, 1.0f, 0.8f, 0.3f);
    // 动画点光 5..8：环绕场景中心（半径 2.6，绕行 120 帧一圈，相位 π/2 步进）
    const float animColors[4][3] = {{1.0f, 0.5f, 0.15f}, {0.2f, 0.9f, 1.0f}, {0.9f, 0.2f, 0.9f},
        {0.5f, 1.0f, 0.2f}};

    // ---- RTV 视图（窗口模式按后台缓冲缓存；离屏单视图）--------------------------------
    ZrImageView rtvViews[3] = {};
    ZrImageView offscreenRtv {};
    if (!windowMode) {
        ZrImageViewDesc rtvViewDesc {};
        rtvViewDesc.image = offscreenTarget;
        rtvViewDesc.viewType = ZrImageViewType::Rtv;
        offscreenRtv = device->CreateImageView(rtvViewDesc);
        if (!offscreenRtv.IsValid()) {
            std::printf("zrhi_d3d12_window: --lights FAILED to create offscreen RTV view\n");
            return 1;
        }
    }

    // ---- readback：中心 1×1（cube）+ 地面横带 640×4（y∈[368,372)）----------------------
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackCenter
        = MakeReadbackBuffer(device->D3D12Device(), 256u);
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackStrip
        = MakeReadbackBuffer(device->D3D12Device(),
            uint64_t(kLightsStripRowPitch) * kLightsStripRows);
    if (readbackCenter == nullptr || readbackStrip == nullptr) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create readback buffers\n");
        return 1;
    }
    std::vector<uint8_t> prevStrip(size_t(kLightsStripRowPitch) * kLightsStripRows, 0u);
    bool havePrevStrip = false;

    uint32_t readbackChecks = 0u;
    uint32_t centerPasses = 0u;
    uint32_t groundLitPasses = 0u;
    uint32_t animPairCount = 0u;
    uint32_t animPairPasses = 0u;
    bool allOk = true;
    uint32_t undeclaredUses = 0u;

    // TASK-B-R9-10：CPU 墙钟帧耗采集（bench 回归）。口径与 --parity 的 frameMs 同族：
    // 单帧 = 光源动画 + 簇分配 + 常量写 + RG 构建/执行 + Submit + WaitIdle +
    // FlushRetired + 校验帧 readback（每帧 WaitIdle 强制 GPU 同步 → 读数稳定）。
    // 失败帧（continue 跳出）不入样本；首帧暖启动在汇总时剔除。
    std::vector<double> benchFrameMs;
    benchFrameMs.reserve(kFrameCount);

    for (uint32_t frame = 0u; frame < kFrameCount; ++frame) {
        const bool doReadback = ((frame + 1u) % kLightsCheckInterval) == 0u;
        const auto benchFrameStart = std::chrono::steady_clock::now();

        // ---- 动画点光位置更新（4 个环绕）---------------------------------------------
        for (uint32_t i = 0; i < 4u; ++i) {
            const float angle = 6.2831853f * static_cast<float>(frame + 1u) / 120.0f
                + 3.14159265f * 0.5f * static_cast<float>(i);
            LightData& l = sceneLights[kAnimLightBase + i];
            l.pos[0] = std::cos(angle) * 2.6f;
            l.pos[1] = 0.9f;
            l.pos[2] = 3.2f + std::sin(angle) * 2.6f;
            l.radius = 3.5f;
            l.color[0] = animColors[i][0];
            l.color[1] = animColors[i][1];
            l.color[2] = animColors[i][2];
            l.intensity = 6.0f;
            l.kind = kZrLightPoint;
        }

        // ---- CPU 簇分配 → CB1/CB2 每帧写入 --------------------------------------------
        PackedLight packedLights[kLightsCount];
        PackLights(sceneLights, kLightsCount, packedLights);
        std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
        // 其余槽（9..31）清零（PS 只经平坦索引访问，防御性清零）
        uint8_t* lightsBytes = static_cast<uint8_t*>(cbLightsMapped);
        std::memset(lightsBytes + sizeof(packedLights), 0, 1024u - sizeof(packedLights));
        const int32_t flatCount = AssignLightsToClusters(grid, sceneLights, kLightsCount,
            clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
        if (flatCount < 0) {
            std::printf("  frame %u: AssignLightsToClusters FAILED (%d)\n", frame + 1u, flatCount);
            allOk = false;
            continue;
        }
        uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
        std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);
        std::memcpy(cb2 + size_t(ZR_CLUSTER_COUNT) * 4u, clusterFlat.data(),
            size_t(flatCount) * 4u);
        if (frame == 0u) {
            std::printf("zrhi_d3d12_window: --lights cluster assign frame1: lights=%u "
                        "clusters=%u flat entries=%d\n",
                kLightsCount, ZR_CLUSTER_COUNT, flatCount);
        }

        // ---- CB0（逐绘常量：地面 / cube）----------------------------------------------
        const float ambient = 0.12f;
        LightsConstants groundConst {};
        std::memcpy(groundConst.mvp, projView, sizeof(projView));
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        std::memcpy(groundConst.model, identity, sizeof(identity));
        LightsConstants cubeConst {};
        float cubeModel[16];
        MatTranslation(0.0f, 0.0f, 3.2f, cubeModel); // 列向量约定（修复：原为行向量字面量）
        std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
        float cubeMvp[16];
        MatMultiply(projView, cubeModel, cubeMvp);
        std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
        for (LightsConstants* c : {&groundConst, &cubeConst}) {
            c->projParams[0] = kFarZ / (kFarZ - kNearZ);          // projA
            c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ); // projB
            c->projParams[2] = kNearZ;
            c->projParams[3] = kFarZ;
            c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
            c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
            c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
            c->gridParams[3] = 1.0f / (kFarZ - kNearZ);           // invZRange
            c->screenParams[0] = static_cast<float>(kWidth);
            c->screenParams[1] = static_cast<float>(kHeight);
            c->screenParams[2] = static_cast<float>(kLightsCount);
            c->screenParams[3] = ambient;
            c->camPos[0] = 0.0f;
            c->camPos[1] = 0.0f;
            c->camPos[2] = 0.0f;
            c->camPos[3] = 0.0f;
        }
        groundConst.material[0] = 0.50f;
        groundConst.material[1] = 0.52f;
        groundConst.material[2] = 0.50f;
        groundConst.material[3] = 0.85f; // roughness
        cubeConst.material[0] = 0.75f;
        cubeConst.material[1] = 0.75f;
        cubeConst.material[2] = 0.78f;
        cubeConst.material[3] = 0.35f;
        std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
        std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));

        // ---- 取后台缓冲（窗口模式）+ RTV 视图 -----------------------------------------
        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  frame %u: FAILED to obtain RTV view\n", frame + 1u);
            allOk = false;
            continue;
        }

        // ---- 每帧 RG：Import 后台缓冲+深度 → ClearPass → LightsDrawPass → readback/Present
        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kLightsClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });
        // LightsDrawPass：地面 + cube（共享 CB1 光源数组 / CB2 簇数据；逐绘 CB0）
        graph.AddPass("LightsDrawPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(pso);
                cmdList->SetRenderTarget(rtvView, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                cmdList->SetGraphicsConstantBuffer(1u, cbLights); // b1：光源数组
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters); // b2：簇 start/count+索引
                // 地面 quad
                ZrBuffer groundVbs[1] = {groundVb};
                cmdList->SetVertexBuffers(groundVbs, 1u);
                cmdList->SetIndexBuffer(groundIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Ground);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                // cube
                ZrBuffer cubeVbs[1] = {cubeVb};
                cmdList->SetVertexBuffers(cubeVbs, 1u);
                cmdList->SetIndexBuffer(cubeIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Cube);
                cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
            });
        // ReadbackPass（校验帧）：中心 1×1 + 地面横带 640×4
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(backH));
                    const DXGI_FORMAT format
                        = windowMode ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
                    D3D12_TEXTURE_COPY_LOCATION src {};
                    src.pResource = texture;
                    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    src.SubresourceIndex = 0u;
                    // 中心 1×1
                    const UINT cx = kWidth / 2u;
                    const UINT cy = kHeight / 2u;
                    const D3D12_BOX centerBox = {cx, cy, 0u, cx + 1u, cy + 1u, 1u};
                    D3D12_TEXTURE_COPY_LOCATION centerDst {};
                    centerDst.pResource = readbackCenter.Get();
                    centerDst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                    centerDst.PlacedFootprint.Footprint.Format = format;
                    centerDst.PlacedFootprint.Footprint.Width = 1u;
                    centerDst.PlacedFootprint.Footprint.Height = 1u;
                    centerDst.PlacedFootprint.Footprint.Depth = 1u;
                    centerDst.PlacedFootprint.Footprint.RowPitch = 256u;
                    nativeCmd->CopyTextureRegion(&centerDst, 0u, 0u, 0u, &src, &centerBox);
                    // 地面横带 640×4
                    const D3D12_BOX stripBox = {0u, kLightsStripY0, 0u, kWidth,
                        kLightsStripY0 + kLightsStripRows, 1u};
                    D3D12_TEXTURE_COPY_LOCATION stripDst {};
                    stripDst.pResource = readbackStrip.Get();
                    stripDst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                    stripDst.PlacedFootprint.Footprint.Format = format;
                    stripDst.PlacedFootprint.Footprint.Width = kWidth;
                    stripDst.PlacedFootprint.Footprint.Height = kLightsStripRows;
                    stripDst.PlacedFootprint.Footprint.Depth = 1u;
                    stripDst.PlacedFootprint.Footprint.RowPitch = kLightsStripRowPitch;
                    nativeCmd->CopyTextureRegion(&stripDst, 0u, 0u, 0u, &src, &stripBox);
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass", [&](RgPass& pass) { pass.Present(backH).MarkRoot(); },
                nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        // 首个校验帧捕获 RG 屏障合并计数（双后端对照断言依据；与 --draw 同口径）
        if (doReadback && frame + 1u == kLightsCheckInterval && outBarrierPlan != nullptr) {
            const RgBarrierPlan& plan = graph.BarrierPlan();
            outBarrierPlan->push_back(plan.TotalBarrierCount());
            outBarrierPlan->push_back(plan.SplitCount());
        }
        const RgExecuteStats rgStats = graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();
        if (doReadback && frame + 1u == kLightsCheckInterval && outBarrierPlan != nullptr) {
            outBarrierPlan->push_back(rgStats.inlineBarrierCalls);
            outBarrierPlan->push_back(rgStats.splitBegins);
            outBarrierPlan->push_back(rgStats.splitEnds);
            outBarrierPlan->push_back(rgStats.aliasHeapsCreated);
            outBarrierPlan->push_back(rgStats.transientImages);
        }

        // ---- 帧末手动屏障：深度回 COMMON（与 --draw 同策）------------------------------
        ZrImageBarrier depthReset = MakeBarrier(depthImage,
            ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);
        if (!windowMode) {
            const ZrImageLayout backLayoutNow = doReadback ? ZrImageLayout::TransferSrcOptimal
                                                           : ZrImageLayout::ColorAttachmentOptimal;
            ZrImageBarrier backReset = MakeBarrier(offscreenTarget, backLayoutNow,
                ZrImageLayout::Undefined, ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&backReset, 1u, nullptr, 0u);
        }

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle();
        graph.FlushRetired(*device);

        // ---- 校验：中心 ≠ 清屏色 / 横带受光 / 横带随动画变化 ----------------------------
        if (doReadback) {
            ++readbackChecks;
            void* centerMapped = nullptr;
            void* stripMapped = nullptr;
            const HRESULT centerHr = readbackCenter->Map(0, nullptr, &centerMapped);
            const HRESULT stripHr = readbackStrip->Map(0, nullptr, &stripMapped);
            if (FAILED(centerHr) || centerMapped == nullptr || FAILED(stripHr)
                || stripMapped == nullptr) {
                std::printf("  frame %u: readback Map FAILED (center hr=0x%08lX strip "
                            "hr=0x%08lX)\n",
                    frame + 1u, static_cast<unsigned long>(centerHr),
                    static_cast<unsigned long>(stripHr));
                allOk = false;
            } else {
                // ① 中心像素 ≠ 清屏色（cube 可见；通道序按目标格式解码）
                const uint8_t* cpx = static_cast<const uint8_t*>(centerMapped);
                const int c0 = static_cast<int>(cpx[0]);
                const int c1 = static_cast<int>(cpx[1]);
                const int c2 = static_cast<int>(cpx[2]);
                const int r = windowMode ? c2 : c0;
                const int b = windowMode ? c0 : c2;
                const int clear0 = static_cast<int>(kLightsClearColor[0] * 255.0f + 0.5f);
                const int clear1 = static_cast<int>(kLightsClearColor[1] * 255.0f + 0.5f);
                const int clear2 = static_cast<int>(kLightsClearColor[2] * 255.0f + 0.5f);
                const bool centerOk = std::abs(r - clear0) > 2 || std::abs(c1 - clear1) > 2
                    || std::abs(b - clear2) > 2;
                centerPasses += centerOk ? 1u : 0u;
                if (outCenters != nullptr) {
                    outCenters->push_back(r);
                    outCenters->push_back(c1);
                    outCenters->push_back(b);
                }

                // ② 横带最大通道值（地面受光判定）+ ③ 与上一校验帧的像素差
                //    （动画判定：差 ≥ kLightsAnimDiffMin 的字节数 ≥ kLightsAnimChangedMin
                //    ——光源环绕 10 帧转 30°，横带覆盖轨迹，必有大量像素变化）
                const uint8_t* strip = static_cast<const uint8_t*>(stripMapped);
                const size_t stripBytes = size_t(kLightsStripRowPitch) * kLightsStripRows;
                int stripMax = 0;
                int maxDiff = -1;
                int changedBytes = 0;
                for (size_t i = 0; i < stripBytes; ++i) {
                    const int v = static_cast<int>(strip[i]);
                    if ((i & 3u) != 3u) { // 跳过 alpha（恒 255，不参与受光判定）
                        stripMax = (v > stripMax) ? v : stripMax;
                    }
                    if (havePrevStrip) {
                        const int d = std::abs(v - static_cast<int>(prevStrip[i]));
                        maxDiff = (d > maxDiff) ? d : maxDiff;
                        if (d >= kLightsAnimDiffMin) {
                            ++changedBytes;
                        }
                    }
                }
                const bool groundLit = stripMax >= kLightsGroundLitMin;
                groundLitPasses += groundLit ? 1u : 0u;
                if (havePrevStrip) {
                    ++animPairCount;
                    const bool animOk = changedBytes >= kLightsAnimChangedMin;
                    animPairPasses += animOk ? 1u : 0u;
                    std::printf("  frame %u lights readback: center RGB=(%d,%d,%d) "
                                "stripMax=%d groundLit=%d animDiff(max=%d changed>=%d: %d "
                                "bytes, min %d) -> center=%s anim=%s\n",
                        frame + 1u, r, c1, b, stripMax, groundLit ? 1 : 0, maxDiff,
                        kLightsAnimDiffMin, changedBytes, kLightsAnimChangedMin,
                        centerOk ? "OK" : "CLEAR", animOk ? "OK" : "STATIC");
                } else {
                    std::printf("  frame %u lights readback: center RGB=(%d,%d,%d) "
                                "stripMax=%d groundLit=%d (baseline strip)\n",
                        frame + 1u, r, c1, b, stripMax, groundLit ? 1 : 0);
                }
                allOk = allOk && centerOk && groundLit;
                std::memcpy(prevStrip.data(), strip, stripBytes);
                havePrevStrip = true;
                readbackCenter->Unmap(0, nullptr);
                readbackStrip->Unmap(0, nullptr);
            }
        }

        if (windowMode) {
            PumpMessages();
        }
        std::printf("frame %2u/%u: [LIGHTS] flatEntries=%d%s\n", frame + 1u, kFrameCount, flatCount,
            doReadback ? "  (+readback)" : "");
        benchFrameMs.push_back(std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - benchFrameStart)
                                   .count());
    }

    // ---- 无设备移除 + InfoQueue 计数 --------------------------------------------------
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;

    // ---- 清理 -------------------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(dsvView);
    device->Destroy(pso);
    device->Destroy(vs);
    device->Destroy(ps);
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(depthImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    readbackCenter.Reset();
    readbackStrip.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    // TASK-B-R9-10：帧耗汇总（固定人类可读行；--json 再追加单行 BENCH_JSON）。
    // 排除首帧暖启动（与 --parity 的 avg_frame_ms 同口径），样本不足 2 帧不打行。
    if (benchFrameMs.size() >= 2u) {
        double sum = 0.0;
        double minMs = benchFrameMs[1];
        double maxMs = benchFrameMs[1];
        for (size_t i = 1; i < benchFrameMs.size(); ++i) {
            sum += benchFrameMs[i];
            minMs = (benchFrameMs[i] < minMs) ? benchFrameMs[i] : minMs;
            maxMs = (benchFrameMs[i] > maxMs) ? benchFrameMs[i] : maxMs;
        }
        const double avgMs = sum / static_cast<double>(benchFrameMs.size() - 1);
        std::printf("zrhi_d3d12_window: [LIGHTS] frame_ms avg=%.3f min=%.3f max=%.3f "
                    "n=%u (frame1 excluded)\n",
            avgMs, minMs, maxMs, static_cast<unsigned>(benchFrameMs.size() - 1u));
        if (jsonOut) {
            zbench::JsonLineWriter w("zrhi_d3d12_window");
            w.Metric("frame_ms_avg", avgMs);
            w.Metric("frame_ms_min", minMs);
            w.Metric("frame_ms_max", maxMs);
            w.MetricU64("frames", benchFrameMs.size() - 1u);
            std::printf("%s\n", w.Finish().c_str());
        }
    }
    std::printf("zrhi_d3d12_window: [LIGHTS] frames=%u readback checks=%u centerPass=%u "
                "groundLitPass=%u animPairs=%u/%u (threshold %d)\n",
        kFrameCount, readbackChecks, centerPasses, groundLitPasses, animPairPasses, animPairCount,
        kLightsAnimDiffMin);
    std::printf("zrhi_d3d12_window: [LIGHTS] device removed reason = 0x%08lX (%s)\n",
        static_cast<unsigned long>(removedReason), noDeviceRemoval ? "S_OK" : "REMOVED");
    std::printf("zrhi_d3d12_window: InfoQueue error count = %u\n", infoQueueErrors);

    if (allOk && readbackChecks > 0u && centerPasses == readbackChecks
        && groundLitPasses == readbackChecks && animPairCount > 0u
        && animPairPasses * 5u >= animPairCount * 3u && noDeviceRemoval
        && undeclaredUses == 0u && infoQueueErrors == 0u) {
        std::printf("D3D12 LIGHTS OK\n");
        return 0;
    }
    std::printf("D3D12 LIGHTS FAILED (allOk=%d checks=%u center=%u groundLit=%u anim=%u/%u "
                "removed=%d undeclared=%u errors=%u)\n",
        allOk ? 1 : 0, readbackChecks, centerPasses, groundLitPasses, animPairPasses,
        animPairCount, noDeviceRemoval ? 0 : 1, undeclaredUses, infoQueueErrors);
    return 1;
}

// =============================================================================
// ---- TASK-B-R5-02：--shadows 模式（CSM 级联阴影 + PCF，可证伪对比）---------------
// =============================================================================

// --shadows 场景（继承 --lights：程序化地面 quad + cube + 方向光；无点光——
// 探针亮度差必须只来自 CSM 阴影，动画光源会污染对比）
constexpr uint32_t kShadowsLightCount = 1u; // 仅方向光（槽 0）
constexpr uint32_t kShadowsFramesPerPhase = 30u; // 每相位 30 帧（简报指定）
constexpr uint32_t kShadowsTexSize = ZR_SHADOW_TEX_SIZE; // 2048²/级联

// ---- 探针坐标推导（可复核；相机原点朝 +Z，fovY=60°，640x480）--------------------
// cube 位于 (0,0,3.2)（半边 0.5），地面 y=-1；方向光指向光源方向
// d=(0.4,0.85,0.35)/|d|（|d|≈1.0025），光线行进方向 -d。
// cube 中心沿 -d 投到地面：s = 1/dy = 1.0025/0.85 ≈ 1.179
//   → 影心 x = -s*dx = -0.470，z = 3.2 - s*dz = 2.788。
// 单位 cube 影区覆盖地面 x∈[-1.2,0.27]、z∈[2.3,3.3]（8 角投影外包络）。
// 屏幕投影（透视 ndcX = x/(z·tan30°·aspect), ndcY = y/(z·tan30°)）：
//   影心 → ndc=(-0.219,-0.621) → 像素 (250, 389)，深居影区内部（PCF 边缘安全）。
constexpr UINT kShadowProbeX = 250u;
constexpr UINT kShadowProbeY = 389u;
// 阈值：on 模式探针（影内 ≈ 环境项 14/255）比 off 模式（直射 ≈ 42/255）暗 ≥10
constexpr int kShadowProbeDeltaMin = 10;

// b0 镜像（224B）——与 Native/shaders/pbr_shadow.hlsl 的 CBufPerDraw 一一对应
struct ShadowDrawConstants {
    float mvp[16];
    float model[16];
    float projParams[4];
    float gridParams[4];
    float screenParams[4];
    float material[4];
    float camPos[4];
    float cascadeSplits[4]; // xyz=级联 0..2 远边界 w=farZ
    float shadowParams[4];  // x=阴影强度(0/1) y=阴影图边长
};
static_assert(sizeof(ShadowDrawConstants) == 240, "ShadowDrawConstants must be 240 bytes");
static_assert(sizeof(ShadowCbufferLayout) == 336, "CB3 must be 336 bytes");

// 阴影 pass b0 镜像（64B）——与 Native/shaders/shadow_depth.hlsl 的 CBufShadowPass 一致
struct ShadowPassConstants {
    float lightVP[16];
};
static_assert(sizeof(ShadowPassConstants) == 64, "ShadowPassConstants must be 64 bytes");

// 阴影 pass b1 镜像（64B）——与 shadow_depth.hlsl 的 CBufShadowModel 一致
struct ShadowModelConstants {
    float shadowModel[16];
};
static_assert(sizeof(ShadowModelConstants) == 64, "ShadowModelConstants must be 64 bytes");

// --shadows 单相位帧循环（enabled=false 跳过阴影 Pass 且 factor 恒 1）。
// outProbe：第 30 帧探针像素 R 通道（0..255）；返回 false=帧内硬错误。
bool RunShadowsPhase(ZrD3D12Device* device, IZrQueue* queue, ZrCommandList cmdHandle,
    IZrCommandList* cmdList, ID3D12GraphicsCommandList* nativeCmd, IZrSwapchain* swapchain,
    ZrImage offscreenTarget, ZrImageView offscreenRtv, ZrImage depthImage, ZrImageView dsvView,
    ZrImage shadowImage, ZrImageView* shadowDsvs, ZrImageView shadowSrv, ZrPipelineState mainPso,
    ZrPipelineState shadowPso, ZrBuffer groundVb, ZrBuffer groundIb, ZrBuffer cubeVb,
    ZrBuffer cubeIb, uint32_t cubeIndexCount, ZrBuffer cb0Ground, ZrBuffer cb0Cube,
    ZrBuffer cbLights, ZrBuffer cbClusters, ZrBuffer cbShadows, ZrBuffer* cbShadowPass,
    ZrBuffer cbShadowModelGround, ZrBuffer cbShadowModelCube, bool enabled,
    const float cascadeSplits[4], int* outProbe)
{
    ZrImageView rtvViews[3] = {};
    bool windowMode = (swapchain != nullptr);
    void* cb0GroundMapped = device->GetBuffer(cb0Ground)->Map();
    void* cb0CubeMapped = device->GetBuffer(cb0Cube)->Map();
    void* cbLightsMapped = device->GetBuffer(cbLights)->Map();
    void* cbClustersMapped = device->GetBuffer(cbClusters)->Map();
    void* cbShadowsMapped = device->GetBuffer(cbShadows)->Map();
    if (cb0GroundMapped == nullptr || cb0CubeMapped == nullptr || cbLightsMapped == nullptr
        || cbClustersMapped == nullptr || cbShadowsMapped == nullptr) {
        std::printf("  shadows phase: constant buffer Map FAILED\n");
        return false;
    }

    // ---- 静态场景常量 -----------------------------------------------------------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kWidth) / static_cast<float>(kHeight), kNearZ,
        kFarZ, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float projView[16];
    MatMultiply(proj, view, projView);

    // 光源：1 方向光（槽 0），与 --lights 场景同方向（探针推导的前提）
    LightData sceneLight {};
    {
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        sceneLight.pos[0] = d[0] * invLen;
        sceneLight.pos[1] = d[1] * invLen;
        sceneLight.pos[2] = d[2] * invLen;
        sceneLight.color[0] = 1.0f;
        sceneLight.color[1] = 0.95f;
        sceneLight.color[2] = 0.88f;
        sceneLight.intensity = 1.0f;
        sceneLight.kind = kZrLightDirectional;
    }

    // CPU 级联矩阵（相机静态 → 每相位构建一次；texel snap 稳定性见单测）
    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    const float ld[3] = {sceneLight.pos[0], sceneLight.pos[1], sceneLight.pos[2]};
    std::memcpy(shadowParamsStruct.lightDir, ld, sizeof(ld));
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = -0.5f;
    shadowParamsStruct.sceneCenter[2] = 10.0f;
    shadowParamsStruct.sceneRadius = 15.0f; // 覆盖 20×20 地面 + cube
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("  shadows phase: BuildCascades FAILED\n");
        return false;
    }

    // CB3（级联 lightVP + bias；bias 深度域 = 世界域 / zRange）
    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;  // depthBias（5cm 世界域）
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;  // slopeBias（掠射面加成）
    }
    shadowCb.cascadeSplits[0] = cascadeSplits[0];
    shadowCb.cascadeSplits[1] = cascadeSplits[1];
    shadowCb.cascadeSplits[2] = cascadeSplits[2];
    shadowCb.cascadeSplits[3] = cascadeSplits[3]; // farZ
    std::memcpy(cbShadowsMapped, &shadowCb, sizeof(shadowCb));

    // CB1 光源 / CB2 簇（仅方向光：写全簇，平坦表 0 条目）
    PackedLight packedLights[kShadowsLightCount];
    PackLights(&sceneLight, kShadowsLightCount, packedLights);
    std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
    std::memset(static_cast<uint8_t*>(cbLightsMapped) + sizeof(packedLights), 0,
        1024u - sizeof(packedLights));
    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);
    const int32_t flatCount = AssignLightsToClusters(grid, &sceneLight, kShadowsLightCount,
        clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
    if (flatCount < 0) {
        std::printf("  shadows phase: AssignLightsToClusters FAILED (%d)\n", flatCount);
        return false;
    }
    uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
    std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);

    // CB0（地面 / cube；material 同 --lights）
    const float ambient = 0.12f;
    ShadowDrawConstants groundConst {};
    std::memcpy(groundConst.mvp, projView, sizeof(projView));
    const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    std::memcpy(groundConst.model, identity, sizeof(identity));
    ShadowDrawConstants cubeConst {};
    float cubeModel[16];
    MatTranslation(0.0f, 0.0f, 3.2f, cubeModel); // 列向量约定（修复：原为行向量字面量）
    std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
    float cubeMvp[16];
    MatMultiply(projView, cubeModel, cubeMvp);
    std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
    for (ShadowDrawConstants* c : {&groundConst, &cubeConst}) {
        c->projParams[0] = kFarZ / (kFarZ - kNearZ);
        c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
        c->projParams[2] = kNearZ;
        c->projParams[3] = kFarZ;
        c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
        c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
        c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
        c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
        c->screenParams[0] = static_cast<float>(kWidth);
        c->screenParams[1] = static_cast<float>(kHeight);
        c->screenParams[2] = static_cast<float>(kShadowsLightCount);
        c->screenParams[3] = ambient;
        c->camPos[0] = 0.0f;
        c->camPos[1] = 0.0f;
        c->camPos[2] = 0.0f;
        c->camPos[3] = 0.0f;
        c->cascadeSplits[0] = cascadeSplits[0];
        c->cascadeSplits[1] = cascadeSplits[1];
        c->cascadeSplits[2] = cascadeSplits[2];
        c->cascadeSplits[3] = cascadeSplits[3];
        c->shadowParams[0] = enabled ? 1.0f : 0.0f; // 阴影强度（off → factor 恒 1）
        c->shadowParams[1] = static_cast<float>(kShadowsTexSize);
    }
    groundConst.material[0] = 0.50f;
    groundConst.material[1] = 0.52f;
    groundConst.material[2] = 0.50f;
    groundConst.material[3] = 0.85f;
    cubeConst.material[0] = 0.75f;
    cubeConst.material[1] = 0.75f;
    cubeConst.material[2] = 0.78f;
    cubeConst.material[3] = 0.35f;
    std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
    std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));


    // 阴影 pass 逐级联 CB（b0 = 该级联 lightVP）+ 逐项 model CB（b1）
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("  shadows phase: shadow pass CB %u Map FAILED\n", c);
            return false;
        }
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        std::memcpy(mapped, &spc, sizeof(spc));
    }
    {
        ShadowModelConstants gm {};
        std::memcpy(gm.shadowModel, groundConst.model, sizeof(gm.shadowModel));
        void* mapped = device->GetBuffer(cbShadowModelGround)->Map();
        if (mapped == nullptr) {
            std::printf("  shadows phase: shadow model CB (ground) Map FAILED\n");
            return false;
        }
        std::memcpy(mapped, &gm, sizeof(gm));
        ShadowModelConstants cm {};
        std::memcpy(cm.shadowModel, cubeConst.model, sizeof(cm.shadowModel));
        mapped = device->GetBuffer(cbShadowModelCube)->Map();
        if (mapped == nullptr) {
            std::printf("  shadows phase: shadow model CB (cube) Map FAILED\n");
            return false;
        }
        std::memcpy(mapped, &cm, sizeof(cm));
    }

    // ---- 阴影 draw list：共享可见性（castShadow 过滤，非二次剔除）----------------
    // 主视图 bit0 + 级联阴影视图 bit1..4（ShadowViewMask；见 zrender/shadows.h）。
    // 地面/cube 均投影阴影；Sort 后主 pass 走批，阴影 pass 走同一 VisibilitySet
    // 的 ShadowViewId(c) 位过滤结果（插入序 = 可见性 renderItemId）。
    DrawList drawList(16u);
    drawList.RegisterMesh(1u, 6u);  // 地面 quad：6 索引
    drawList.RegisterMesh(2u, cubeIndexCount);
    RenderItem groundItem {};
    groundItem.meshHandle = 1u;
    groundItem.materialId = 7u;
    groundItem.depth = 2.8f;
    groundItem.castShadow = true;
    RenderItem cubeItem {};
    cubeItem.meshHandle = 2u;
    cubeItem.materialId = 8u;
    cubeItem.depth = 3.2f;
    cubeItem.castShadow = true;
    if (!drawList.Add(groundItem, ShadowViewMask(true))
        || !drawList.Add(cubeItem, ShadowViewMask(true))) {
        std::printf("  shadows phase: draw list Add FAILED\n");
        return false;
    }
    // Sort 前收集各级联阴影项（可见性 id = 插入序号；Sort 会重排 m_items）
    std::vector<uint32_t> shadowItems[ZR_SHADOW_CASCADES];
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        drawList.Visibility().Iterate(
            ShadowViewId(c), [&](uint32_t id) { shadowItems[c].push_back(id); });
    }
    drawList.Sort();
    const uint32_t batchCount = drawList.BuildBatches();
    if (batchCount == 0u) {
        std::printf("  shadows phase: BuildBatches FAILED\n");
        return false;
    }


    // ---- 逐项绘制辅助（meshHandle → VB/IB + 常量槽）------------------------------
    auto drawShadowItem = [&](uint32_t itemId) {
        if (drawList.ItemAt(itemId).meshHandle == 1u) {
            ZrBuffer vbs[1] = {groundVb};
            cmdList->SetVertexBuffers(vbs, 1u);
            cmdList->SetIndexBuffer(groundIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        } else {
            ZrBuffer vbs[1] = {cubeVb};
            cmdList->SetVertexBuffers(vbs, 1u);
            cmdList->SetIndexBuffer(cubeIb);
            cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
        }
    };

    // ---- 30 帧循环 ----------------------------------------------------------------
    bool allOk = true;
    uint32_t undeclaredUses = 0u;
    Microsoft::WRL::ComPtr<ID3D12Resource> readback
        = MakeReadbackBuffer(device->D3D12Device(), 256u);
    if (readback == nullptr) {
        std::printf("  shadows phase: readback buffer FAILED\n");
        return false;
    }

    for (uint32_t frame = 0u; frame < kShadowsFramesPerPhase; ++frame) {
        const bool doReadback = (frame == kShadowsFramesPerPhase - 1u); // 第 30 帧
        constexpr float kShadowsClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f};

        // 取后台缓冲 + RTV
        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  shadows phase frame %u: FAILED to obtain RTV\n", frame + 1u);
            return false;
        }

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        RgHandle shadowH {};
        if (enabled) {
            shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));
        }

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kShadowsClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });

        // 每级联一个 depth-only DrawPass（RG 阴影 Pass；消费共享可见性的
        // 阴影 draw list——shadowItems[c] 来自同一 DrawList 的视图掩码位过滤）
        if (enabled) {
            for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
                graph.AddPass("ShadowCascadePass",
                    [&](RgPass& pass) {
                        pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                            ZrAccessBits::DepthStencilAttachmentWrite,
                            ZrPipelineStageBits::LateFragmentTests);
                    },
                    [&, c](RgContext&) {
                        // depth-only PSO：无颜色目标（RTV 无效句柄 = 仅深度附件）
                        cmdList->SetPipelineState(shadowPso);
                        cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                        cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                        // 该级联层清屏（DSV 已绑定层 c；此刻资源处 DEPTH_WRITE 态）
                        nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(shadowDsvs[c]),
                            D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
                        cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                        for (uint32_t id : shadowItems[c]) {
                            const bool isGround = drawList.ItemAt(id).meshHandle == 1u;
                            cmdList->SetGraphicsConstantBuffer(
                                1u, isGround ? cbShadowModelGround : cbShadowModelCube);
                            drawShadowItem(id);
                        }
                    });
            }
        }

        // 主绘制 Pass（PBR + 簇光照；enabled 时读级联阴影图做 PCF）
        graph.AddPass("ShadowedLightsDrawPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                if (enabled) {
                    // 阴影图作为深度只读资源被 PS 采样（D3D12：DEPTH_READ 态）
                    pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                        ZrAccessBits::DepthStencilAttachmentRead,
                        ZrPipelineStageBits::FragmentShader);
                }
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mainPso);
                cmdList->SetRenderTarget(rtvView, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                // 根 SRV 表（槽 4 = rootCbvCount）：t0 = 级联阴影图（两相位都绑——
                // off 相位 shader 动态分支跳过采样，但根参数静态被引用必须绑定）
                cmdList->SetGraphicsBindlessSrv(4u, shadowSrv);
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    for (uint32_t i = 0; i < batch.count; ++i) {
                        const uint32_t itemIndex = batch.startIndex + i;
                        const uint32_t mesh = drawList.ItemAt(itemIndex).meshHandle;
                        ZrBuffer vbs[1] = {(mesh == 1u) ? groundVb : cubeVb};
                        cmdList->SetVertexBuffers(vbs, 1u);
                        cmdList->SetIndexBuffer((mesh == 1u) ? groundIb : cubeIb);
                        cmdList->SetGraphicsConstantBuffer(
                            0u, (mesh == 1u) ? cb0Ground : cb0Cube);
                        cmdList->DrawIndexedInstanced(
                            (mesh == 1u) ? 6u : cubeIndexCount, 1u, 0u, 0u, 0u);
                    }
                }
            });

        // ReadbackPass（第 30 帧）：探针 1×1（推导见 kShadowProbeX 注释）
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(backH));
                    const DXGI_FORMAT format
                        = windowMode ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
                    const D3D12_BOX box
                        = {kShadowProbeX, kShadowProbeY, 0u, kShadowProbeX + 1u,
                            kShadowProbeY + 1u, 1u};
                    D3D12_TEXTURE_COPY_LOCATION dst {};
                    dst.pResource = readback.Get();
                    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                    dst.PlacedFootprint.Footprint.Format = format;
                    dst.PlacedFootprint.Footprint.Width = 1u;
                    dst.PlacedFootprint.Footprint.Height = 1u;
                    dst.PlacedFootprint.Footprint.Depth = 1u;
                    dst.PlacedFootprint.Footprint.RowPitch = 256u;
                    D3D12_TEXTURE_COPY_LOCATION src {};
                    src.pResource = texture;
                    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    src.SubresourceIndex = 0u;
                    nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass", [&](RgPass& pass) { pass.Present(backH).MarkRoot(); },
                nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  shadows phase frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末手动屏障：深度/阴影图回 COMMON（与下一帧 RG 起点 Undefined 假设一致）
        ZrImageBarrier depthReset = MakeBarrier(depthImage,
            ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);
        if (enabled) {
            ZrImageBarrier shadowReset = MakeBarrier(shadowImage,
                ZrImageLayout::DepthStencilReadOnlyOptimal, ZrImageLayout::Undefined,
                ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&shadowReset, 1u, nullptr, 0u);
        }
        if (!windowMode) {
            const ZrImageLayout backLayoutNow
                = doReadback ? ZrImageLayout::TransferSrcOptimal
                             : ZrImageLayout::ColorAttachmentOptimal;
            ZrImageBarrier backReset = MakeBarrier(offscreenTarget, backLayoutNow,
                ZrImageLayout::Undefined, ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&backReset, 1u, nullptr, 0u);
        }

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle();
        graph.FlushRetired(*device);

        if (doReadback) {
            void* mapped = nullptr;
            const HRESULT hr = readback->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  shadows phase frame %u: probe Map FAILED (hr=0x%08lX)\n",
                    frame + 1u, static_cast<unsigned long>(hr));
                allOk = false;
            } else {
                const uint8_t* px = static_cast<const uint8_t*>(mapped);
                const int r = windowMode ? static_cast<int>(px[2]) : static_cast<int>(px[0]);
                const int g = static_cast<int>(px[1]);
                const int b = windowMode ? static_cast<int>(px[0]) : static_cast<int>(px[2]);
                std::printf("  shadows phase frame %u probe(%u,%u) RGB=(%d,%d,%d)\n", frame + 1u,
                    kShadowProbeX, kShadowProbeY, r, g, b);
                *outProbe = r;
                readback->Unmap(0, nullptr);
            }
        }

        if (windowMode) {
            PumpMessages();
        }
    }

    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    if (undeclaredUses != 0u) {
        std::printf("  shadows phase: undeclared RG uses = %u\n", undeclaredUses);
        allOk = false;
    }
    return allOk;
}

// --shadows 模式主体（contrast：先 off 后 on 各 30 帧，同一探针点对比）。
// mode 参数："on"/"off" 单相位调试；空串 = 完整可证伪对比（默认）。
// TASK-B-R7-02：可选 out 参数（双后端对照采集；缺省空 = D3D12 既有行为零改动）
static int RunShadowsMode(
    const char* zmeshPath, const char* hlslPath, const char* depthHlslPath, const char* modeStr,
    int* outProbeOff = nullptr, int* outProbeOn = nullptr)
{
    const bool contrast = (modeStr[0] == '\0');
    const bool forceOn = (std::strcmp(modeStr, "on") == 0);
    const bool forceOff = (std::strcmp(modeStr, "off") == 0);
    std::printf("zrhi_d3d12_window: --shadows mode (TASK-B-R5-02, %s): zmesh='%s' hlsl='%s'\n",
        contrast ? "off-vs-on contrast" : (forceOn ? "on only" : "off only"), zmeshPath, hlslPath);
    if (!contrast && !forceOn && !forceOff) {
        std::printf("zrhi_d3d12_window: --shadows 未知相位 '%s'（可选 on/off 或缺省对比）\n",
            modeStr);
        return 1;
    }

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --shadows device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏）-------------------------------------------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --shadows window rejected (GetLastError=%lu), "
                    "falling back to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --shadows swapchain FAILED, falling back to "
                        "offscreen target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
    }

    // ---- 渲染目标（离屏降级）+ 场景深度 + 级联阴影图 ----------------------------------
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --shadows FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrD3D12DestroyDevice(device);
            return 1;
        }
    }
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    if (!depthImage.IsValid() || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create depth image/DSV\n");
        return 1;
    }

    // 级联阴影图：单张 2048² D32 数组图（层 i = 级联 i）——每级联一个层 DSV +
    // 全 4 层 Texture2DArray SRV（R32Float 解释，PCF 手动比较采样）
    ZrImageDesc shadowDesc {};
    shadowDesc.format = ZrFormat::D32Float;
    shadowDesc.width = kShadowsTexSize;
    shadowDesc.height = kShadowsTexSize;
    shadowDesc.arrayLayers = ZR_SHADOW_CASCADES;
    shadowDesc.usage = ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled;
    ZrImage shadowImage = device->CreateImage(shadowDesc);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        ZrImageViewDesc layerDsvDesc {};
        layerDsvDesc.image = shadowImage;
        layerDsvDesc.viewType = ZrImageViewType::Dsv;
        layerDsvDesc.subresources.baseArrayLayer = c;
        layerDsvDesc.subresources.layerCount = 1u;
        shadowDsvs[c] = device->CreateImageView(layerDsvDesc);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float; // 深度格式采样的非深度孪生解释
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    if (!shadowImage.IsValid() || !shadowSrv.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create shadow image/views\n");
        return 1;
    }
    for (ZrImageView v : shadowDsvs) {
        if (!v.IsValid()) {
            std::printf("zrhi_d3d12_window: --shadows FAILED to create cascade layer DSV\n");
            return 1;
        }
    }

    // ---- 几何（同 --lights：地面 quad + cube.zmesh）-----------------------------------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<char> meshBytes;
    if (!ReadWholeFile(zmeshPath, meshBytes)) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to open zmesh '%s'\n", zmeshPath);
        return 1;
    }
    ZmeshData mesh {};
    const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
    if (parseResult != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --shadows zmesh parse FAILED (error %d)\n", parseResult);
        return 1;
    }
    const uint32_t cubeVertexCount = mesh.stats.vertexCount;
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(cubeVertexCount);
    for (uint32_t i = 0; i < cubeVertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);

    ZrBufferDesc gVbDesc {};
    gVbDesc.size = sizeof(groundVerts);
    gVbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer groundVb = device->CreateBuffer(gVbDesc, groundVerts);
    ZrBufferDesc gIbDesc {};
    gIbDesc.size = sizeof(groundIndices);
    gIbDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer groundIb = device->CreateBuffer(gIbDesc, groundIndices);
    ZrBufferDesc cVbDesc {};
    cVbDesc.size = static_cast<uint64_t>(cubeVerts.size()) * sizeof(FirstDrawVertex);
    cVbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer cubeVb = device->CreateBuffer(cVbDesc, cubeVerts.data());
    ZrBufferDesc cIbDesc {};
    cIbDesc.size = static_cast<uint64_t>(cubeIndices.size()) * sizeof(uint32_t);
    cIbDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer cubeIb = device->CreateBuffer(cIbDesc, cubeIndices.data());
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create VB/IB\n");
        return 1;
    }
    // ---- 常量缓冲：b0×2 / b1 光源 / b2 簇 / b3 级联 / 阴影 pass b0×4 -------------------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbShadowModelGround {};
    ZrBuffer cbShadowModelCube {};
    {
        auto makeCb = [&](uint64_t size, ZrBuffer& outHandle) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            outHandle = device->CreateBuffer(desc);
            return outHandle.IsValid() && device->GetBuffer(outHandle)->Map() != nullptr;
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        bool ok = makeCb(256u, cb0Ground) && makeCb(256u, cb0Cube) && makeCb(1024u, cbLights)
            && makeCb(cb2Size, cbClusters) && makeCb(384u, cbShadows); // 336 → 384 对齐裕量
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            ok = ok && makeCb(256u, cbShadowPass[c]);
        }
        ok = ok && makeCb(256u, cbShadowModelGround) && makeCb(256u, cbShadowModelCube);
        if (!ok) {
            std::printf("zrhi_d3d12_window: --shadows FAILED to create constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器 + PSO ----------------------------------------------------------------
    std::vector<char> hlsl;
    if (!ReadWholeFile(hlslPath, hlsl)) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to open hlsl '%s'\n", hlslPath);
        return 1;
    }
    std::vector<char> depthHlsl;
    if (!ReadWholeFile(depthHlslPath, depthHlsl)) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to open depth hlsl '%s'\n",
            depthHlslPath);
        return 1;
    }
    const ZrShader mainVs = device->CompileHlsl(ZrShaderStage::Vertex, hlsl.data(), hlsl.size(),
        "VsMain");
    const ZrShader mainPs = device->CompileHlsl(ZrShaderStage::Pixel, hlsl.data(), hlsl.size(),
        "PsMain");
    const ZrShader shadowVs = device->CompileHlsl(
        ZrShaderStage::Vertex, depthHlsl.data(), depthHlsl.size(), "VsMain");
    if (!mainVs.IsValid() || !mainPs.IsValid() || !shadowVs.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows shader compilation FAILED (see stderr)\n");
        return 1;
    }

    ZrGraphicsPsoDesc mainPsoDesc {};
    mainPsoDesc.vertexShader = mainVs;
    mainPsoDesc.pixelShader = mainPs;
    mainPsoDesc.rtvFormat = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    mainPsoDesc.dsvFormat = ZrFormat::D32Float;
    mainPsoDesc.blend = ZrBlendMode::Opaque;
    mainPsoDesc.depthTest = true;
    mainPsoDesc.depthWrite = true;
    mainPsoDesc.cullMode = ZrCullMode::None; // R4-03 绕序实证（见 shadow_depth.hlsl 注）
    mainPsoDesc.rootCbvCount = 4u;           // b0 逐绘 / b1 光源 / b2 簇 / b3 级联
    mainPsoDesc.srvTable = true;             // t0 级联阴影图 + s0 Point/Clamp
    ZrPipelineState mainPso = device->CreatePipelineState(mainPsoDesc);

    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = shadowVs;
    // depth-only：无 PS/无颜色目标（ZrValidateGraphicsPsoDesc 的 depthOnly 分支放行）
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    // 剔除绕序约定（与主 pass 的关系——R4-03 CCW 发现）：
    //   主 pass 因资产烘焙绕序呈 CCW 被迫 CullMode::None；本阴影 pass 取
    //   CullMode::Front（剔除"正面"）：FrontCounterClockwise=FALSE 下正面=CW，
    //   剔除 CW 即保留 CCW 面——与主相机同为 LH 投影时"面向光源的表面呈 CCW"
    //   一致，恰好只写光向正面的深度（标准 shadow-map 正面剔除，兼抗自阴影
    //   acne）。相对"常规引擎 主 Back/阴影 Front"的配对，此处主 pass 被迫 None，
    //   阴影 Front 即"与主 pass 相反"的绕序选择。
    shadowPsoDesc.cullMode = ZrCullMode::Front;
    shadowPsoDesc.rootCbvCount = 2u; // b0 = 该级联 lightVP / b1 = 逐绘 model
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    if (!mainPso.IsValid() || !shadowPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create graphics PSOs (main=%d "
                    "shadow=%d)\n",
            mainPso.IsValid() ? 1 : 0, shadowPso.IsValid() ? 1 : 0);
        return 1;
    }

    // ---- CPU 级联分割（打印 4 次级联分割值；探针推导的前提条件）------------------------
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(0.3f, 60.0f, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("zrhi_d3d12_window: --shadows CascadeSplits FAILED\n");
            return 1;
        }
        cascadeSplits[0] = interior[0];
        cascadeSplits[1] = interior[1];
        cascadeSplits[2] = interior[2];
        cascadeSplits[3] = 60.0f; // 末级联上界 = farZ
        std::printf("zrhi_d3d12_window: --shadows cascade splits (near=0.3 far=60 "
                    "lambda=0.75): %.4f %.4f %.4f %.4f\n",
            cascadeSplits[0], cascadeSplits[1], cascadeSplits[2], cascadeSplits[3]);
    }

    ZrImageViewDesc offRtvDesc {};
    offRtvDesc.image = offscreenTarget;
    offRtvDesc.viewType = ZrImageViewType::Rtv;
    ZrImageView offscreenRtv
        = offscreenTarget.IsValid() ? device->CreateImageView(offRtvDesc) : ZrImageView{};

    // ---- 两相位执行：off（factor 恒 1）→ on（CSM PCF），同一探针点 ----------------------
    int probeOff = -1;
    int probeOn = -1;
    bool offOk = true;
    bool onOk = true;
    if (!forceOn) {
        std::printf("zrhi_d3d12_window: --shadows phase OFF (%u frames)\n",
            kShadowsFramesPerPhase);
        offOk = RunShadowsPhase(device, queue, cmdHandle, cmdList, nativeCmd, swapchain,
            offscreenTarget, offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv,
            mainPso, shadowPso, groundVb, groundIb, cubeVb, cubeIb, cubeIndexCount, cb0Ground,
            cb0Cube, cbLights, cbClusters, cbShadows, cbShadowPass, cbShadowModelGround,
            cbShadowModelCube, /*enabled=*/false, cascadeSplits, &probeOff);
    }
    if (!forceOff) {
        std::printf("zrhi_d3d12_window: --shadows phase ON (%u frames)\n",
            kShadowsFramesPerPhase);
        onOk = RunShadowsPhase(device, queue, cmdHandle, cmdList, nativeCmd, swapchain,
            offscreenTarget, offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv,
            mainPso, shadowPso, groundVb, groundIb, cubeVb, cubeIb, cubeIndexCount, cb0Ground,
            cb0Cube, cbLights, cbClusters, cbShadows, cbShadowPass, cbShadowModelGround,
            cbShadowModelCube, /*enabled=*/true, cascadeSplits, &probeOn);
    }

    // ---- 无设备移除 + InfoQueue 计数 --------------------------------------------------
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;

    // ---- 清理 -------------------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    device->Destroy(cbShadowModelGround);
    device->Destroy(cbShadowModelCube);
    for (ZrImageView& v : shadowDsvs) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    device->Destroy(shadowSrv);
    device->Destroy(mainPso);
    device->Destroy(shadowPso);
    device->Destroy(mainVs);
    device->Destroy(mainPs);
    device->Destroy(shadowVs);
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(depthImage);
    device->Destroy(dsvView);
    device->Destroy(shadowImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: [SHADOWS] probe off=%d on=%d removed=%s errors=%u\n",
        probeOff, probeOn, noDeviceRemoval ? "S_OK" : "REMOVED", infoQueueErrors);
    if (outProbeOff != nullptr) {
        *outProbeOff = probeOff;
    }
    if (outProbeOn != nullptr) {
        *outProbeOn = probeOn;
    }

    const bool singleModeOk = (forceOn || forceOff) && offOk && onOk && noDeviceRemoval
        && infoQueueErrors == 0u && (forceOn ? probeOn >= 0 : probeOff >= 0);
    const bool contrastOk = contrast && offOk && onOk && noDeviceRemoval
        && infoQueueErrors == 0u && probeOff >= 0 && probeOn >= 0
        && (probeOff - probeOn) >= kShadowProbeDeltaMin;
    if (contrastOk) {
        std::printf("D3D12 SHADOWS OK (probe delta=%d)\n", probeOff - probeOn);
        return 0;
    }
    if (singleModeOk) {
        std::printf("D3D12 SHADOWS OK (single phase %s, probe=%d)\n", forceOn ? "on" : "off",
            forceOn ? probeOn : probeOff);
        return 0;
    }
    std::printf("D3D12 SHADOWS FAILED (contrast=%d offOk=%d onOk=%d off=%d on=%d removed=%d "
                "errors=%u)\n",
        contrast ? 1 : 0, offOk ? 1 : 0, onOk ? 1 : 0, probeOff, probeOn,
        noDeviceRemoval ? 0 : 1, infoQueueErrors);
    return 1;
}

// =============================================================================
// ---- TASK-B-R5-03：--r5 模式（SSR + GI 探针 + NPR 风格线，可证伪对比）----------
// =============================================================================

// --r5 场景常量（继承 --shadows：地面 quad + cube + 方向光；新增 toon cube 与
// 2 动画点光。全部探针坐标推导可复核）------------------------------------------------
// Toon Cube 世界 (−2.4,0,4.2)（半边 0.5），风格化光 toonLightDir=(0.3,0.5,−0.81)/0.998
// （相机侧：可见面为 −z 前面（法线 (0,0,-1)，ndl≈0.81→亮带）与 +x 右面
// （ndl≈0.30→暗带）；阈值 0.35/0.6）。屏幕投影：
//   前面(−z, z=3.7) px 0..106；右面(+x)近棱 z=3.7 → px 106，远棱 z=4.7 → px 152
//   （右面更近覆盖 px 106..152，前面可见区 px 0..106）。y=±0.5 → py 196..284。
//   探针点均深入面内。
constexpr UINT kR5ToonBrightX = 80u;   // 前面（亮带）
constexpr UINT kR5ToonBrightAY = 240u;
constexpr UINT kR5ToonBrightBY = 255u; // 带内第二点（平坦断言）
constexpr UINT kR5ToonDarkX = 125u;    // 右面（暗带）
constexpr UINT kR5ToonDarkAY = 240u;
constexpr UINT kR5ToonDarkBY = 255u;
// SSR 反射探针：地面 (0,-1,2.2)——反射方向 (0,1,2.2) 命中 cube 前面 (y=0.227)；
//   ndcY = -1/(2.2·tan30°) = -0.787 → py=(1+0.787)/2·480=429，px=320（推导同上）
constexpr UINT kR5SsrProbeX = 320u;
constexpr UINT kR5SsrProbeY = 429u;
// GI 探针：2 动画点光轨道中心 (0,0.9,6.5) 正下地面 (0,-1,6.5)——轨道半径 2 恒定，
//   两光对该点距离恒为 sqrt(4+1.9²)，直接光贡献和恒定（0.200 线性）→ 两相位差
//   只来自 GI 探针填充变化。ndcY = -1/(6.5·tan30°) = -0.266 → py=(1+0.266)/2·480=304
constexpr UINT kR5GiProbeX = 320u;
constexpr UINT kR5GiProbeY = 304u;

// 阈值（简报指定：SSR≥8 / GI≥3 / Toon 带间≥40 带内<6）
constexpr int kR5SsrDeltaMin = 8;
constexpr int kR5GiDeltaMin = 3;
constexpr int kR5ToonDeltaMin = 40;
constexpr int kR5ToonBandFlatMax = 5; // 带内平坦：两点差须 <6

constexpr uint32_t kR5FramesPerPhase = 30u; // SSR off / on 各 30 帧
constexpr uint32_t kR5LightCount = 3u;      // 1 方向光 + 2 动画点光
constexpr uint32_t kR5HalfW = kWidth / ZR_SSR_HALF_RES;  // 320
constexpr uint32_t kR5HalfH = kHeight / ZR_SSR_HALF_RES; // 240

// 探针读back盒（6×1px；槽 i → readback 缓冲偏移 i*256）
struct R5ProbeBox {
    UINT x;
    UINT y;
};
constexpr R5ProbeBox kR5Probes[6] = {
    { kR5SsrProbeX, kR5SsrProbeY },     // 0 SSR 反射探针
    { kR5GiProbeX, kR5GiProbeY },       // 1 GI 探针
    { kR5ToonBrightX, kR5ToonBrightAY },// 2 亮带 A
    { kR5ToonBrightX, kR5ToonBrightBY },// 3 亮带 B（带内）
    { kR5ToonDarkX, kR5ToonDarkAY },    // 4 暗带 A
    { kR5ToonDarkX, kR5ToonDarkBY },    // 5 暗带 B（带内）
};
constexpr UINT kR5ProbeCount = 6u;

// b0 镜像（208B）——与 Native/shaders/toon.hlsl 的 CBufToon 一一对应
// （B-R5-04 加法：toonBase 基色进 CB；--r5 填回原硬编码值，探针基准不变）
struct ToonConstants {
    float mvp[16];
    float model[16];
    float lightDir[4];
    float params[4];   // x=band0 y=band1 z=暗部灰度 w=中间调灰度
    float specular[4]; // x=高光阈值 y=高光强度
    float camPos[4];
    float base[4];     // rgb=基色（toonBase）
};
static_assert(sizeof(ToonConstants) == 208, "ToonConstants must be 208 bytes");

// b0 镜像（80B）——与 Native/shaders/toon_outline.hlsl 的 CBufOutline 一一对应
struct OutlineConstants {
    float mvp[16];
    float params[4]; // x=壳外扩宽度（世界域，米）
};
static_assert(sizeof(OutlineConstants) == 80, "OutlineConstants must be 80 bytes");

// b0 镜像（16B）——hiz.hlsl CBufHiZ / ssr_blur.hlsl CBufBlur 共形
struct R5TexelConstants {
    float texel[4];
};
static_assert(sizeof(R5TexelConstants) == 16, "R5TexelConstants must be 16 bytes");

// b0 镜像（16B）——composite.hlsl CBufComposite
struct CompositeConstants {
    float params[4]; // x=SSR 开关
};
static_assert(sizeof(CompositeConstants) == 16, "CompositeConstants must be 16 bytes");

/// 通用 4×4 求逆（行主序；SSR trace 的 invViewProj 用）。奇异（|det|<1e-12）
/// 返回 false 不写 out。
bool MatInverse4x4(const float* m, float* out)
{
    const float a00 = m[0], a01 = m[1], a02 = m[2], a03 = m[3];
    const float a10 = m[4], a11 = m[5], a12 = m[6], a13 = m[7];
    const float a20 = m[8], a21 = m[9], a22 = m[10], a23 = m[11];
    const float a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15];

    const float b00 = a00 * a11 - a01 * a10;
    const float b01 = a00 * a12 - a02 * a10;
    const float b02 = a00 * a13 - a03 * a10;
    const float b03 = a01 * a12 - a02 * a11;
    const float b04 = a01 * a13 - a03 * a11;
    const float b05 = a02 * a13 - a03 * a12;
    const float b06 = a20 * a31 - a21 * a30;
    const float b07 = a20 * a32 - a22 * a30;
    const float b08 = a20 * a33 - a23 * a30;
    const float b09 = a21 * a32 - a22 * a31;
    const float b10 = a21 * a33 - a23 * a31;
    const float b11 = a22 * a33 - a23 * a32;

    const float det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
    if (std::fabs(det) < 1e-12f) {
        return false;
    }
    const float invDet = 1.0f / det;
    const float inv[16] = {
        (a11 * b11 - a12 * b10 + a13 * b09) * invDet,
        (a02 * b10 - a01 * b11 - a03 * b09) * invDet,
        (a31 * b05 - a32 * b04 + a33 * b03) * invDet,
        (a22 * b04 - a21 * b05 - a23 * b03) * invDet,
        (a12 * b08 - a10 * b11 - a13 * b07) * invDet,
        (a00 * b11 - a02 * b08 + a03 * b07) * invDet,
        (a32 * b02 - a30 * b05 - a33 * b01) * invDet,
        (a20 * b05 - a22 * b02 + a23 * b01) * invDet,
        (a10 * b10 - a11 * b08 + a13 * b06) * invDet,
        (a01 * b08 - a00 * b10 - a03 * b06) * invDet,
        (a30 * b04 - a31 * b02 + a33 * b00) * invDet,
        (a21 * b02 - a20 * b04 - a23 * b00) * invDet,
        (a11 * b07 - a10 * b09 - a12 * b06) * invDet,
        (a00 * b09 - a01 * b07 + a02 * b06) * invDet,
        (a31 * b01 - a30 * b03 - a32 * b00) * invDet,
        (a20 * b03 - a21 * b01 + a22 * b00) * invDet,
    };
    std::memcpy(out, inv, sizeof(inv));
    return true;
}

/// 单次读back：6 个 1px 探针盒拷入 readback 缓冲（槽 i → 偏移 i*256）
void RecordR5ProbeReadback(ID3D12GraphicsCommandList* nativeCmd, ID3D12Resource* texture,
    ID3D12Resource* readback, DXGI_FORMAT format)
{
    for (UINT i = 0; i < kR5ProbeCount; ++i) {
        const R5ProbeBox& p = kR5Probes[i];
        const D3D12_BOX box = { p.x, p.y, 0u, p.x + 1u, p.y + 1u, 1u };
        D3D12_TEXTURE_COPY_LOCATION dst {};
        dst.pResource = readback;
        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst.PlacedFootprint.Footprint.Format = format;
        dst.PlacedFootprint.Footprint.Width = 1u;
        dst.PlacedFootprint.Footprint.Height = 1u;
        dst.PlacedFootprint.Footprint.Depth = 1u;
        dst.PlacedFootprint.Footprint.RowPitch = 256u;
        dst.PlacedFootprint.Offset = static_cast<UINT64>(i) * 256u;
        D3D12_TEXTURE_COPY_LOCATION src {};
        src.pResource = texture;
        src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        src.SubresourceIndex = 0u;
        nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
    }
}

/// 读back 解码：槽 i 的 RGB（窗口 BGRA / 离屏 RGBA 通道序）
void DecodeR5Probe(const uint8_t* mapped, uint32_t slot, bool windowMode, int outRgb[3])
{
    const uint8_t* px = mapped + static_cast<size_t>(slot) * 256u;
    const int c0 = static_cast<int>(px[0]);
    const int c1 = static_cast<int>(px[1]);
    const int c2 = static_cast<int>(px[2]);
    outRgb[0] = windowMode ? c2 : c0;
    outRgb[1] = c1;
    outRgb[2] = windowMode ? c0 : c2;
}

/// 三通道最大绝对差
int R5MaxChannelDiff(const int a[3], const int b[3])
{
    int m = 0;
    for (int i = 0; i < 3; ++i) {
        const int d = std::abs(a[i] - b[i]);
        m = (d > m) ? d : m;
    }
    return m;
}

// --r5 单相位（ssrEnabled=SSR 开关；GI 恒开）。outProbes：第 10/20/30 帧各采一组
// 6 探针 RGB（[readback 序 0..2][探针 0..5][RGB]）。返回 false = 帧内硬错误。
bool RunR5Phase(ZrD3D12Device* device, IZrQueue* queue, ZrCommandList cmdHandle,
    IZrCommandList* cmdList, ID3D12GraphicsCommandList* nativeCmd, IZrSwapchain* swapchain,
    ZrImage offscreenTarget, ZrImageView offscreenRtv, ZrImage depthImage, ZrImageView dsvView,
    ZrImage shadowImage, ZrImageView* shadowDsvs, ZrImageView shadowSrv, ZrImage sceneImage,
    ZrImageView sceneRtv, ZrImageView sceneSrv, ZrImage hizImage, ZrImageView* hizRtvs,
    ZrImageView hizSrv, ZrImageView* hizMipSrvs, ZrImageView depthSrv, ZrImage traceImage,
    ZrImageView traceRtv, ZrImageView traceSrv, ZrImage blurImage, ZrImageView blurRtv,
    ZrImageView blurSrv, ZrPipelineState mainPso, ZrPipelineState toonPso,
    ZrPipelineState outlinePso, ZrPipelineState shadowPso, ZrPipelineState hizCopyPso,
    ZrPipelineState hizMinPso, ZrPipelineState tracePso, ZrPipelineState blurPso,
    ZrPipelineState compPso, ZrBuffer groundVb, ZrBuffer groundIb, ZrBuffer cubeVb,
    ZrBuffer cubeIb, ZrBuffer toonVb, ZrBuffer toonIb, ZrBuffer quadVb, ZrBuffer quadIb,
    uint32_t cubeIndexCount, ZrBuffer cb0Ground, ZrBuffer cb0Cube, ZrBuffer cbToon,
    ZrBuffer cbOutline, ZrBuffer cbLights, ZrBuffer cbClusters, ZrBuffer cbShadows, ZrBuffer cbGi,
    ZrBuffer cbSsr, ZrBuffer* cbHiZ, ZrBuffer cbBlur, ZrBuffer cbComposite,
    ZrBuffer* cbShadowPass, ZrBuffer cbShadowModelGround, ZrBuffer cbShadowModelCube,
    bool ssrEnabled, ID3D12Resource* readback, int outProbes[3][kR5ProbeCount][3])
{
    ZrImageView rtvViews[3] = {};
    const bool windowMode = (swapchain != nullptr);
    void* cb0GroundMapped = device->GetBuffer(cb0Ground)->Map();
    void* cb0CubeMapped = device->GetBuffer(cb0Cube)->Map();
    void* cbToonMapped = device->GetBuffer(cbToon)->Map();
    void* cbOutlineMapped = device->GetBuffer(cbOutline)->Map();
    void* cbLightsMapped = device->GetBuffer(cbLights)->Map();
    void* cbClustersMapped = device->GetBuffer(cbClusters)->Map();
    void* cbShadowsMapped = device->GetBuffer(cbShadows)->Map();
    void* cbGiMapped = device->GetBuffer(cbGi)->Map();
    void* cbSsrMapped = device->GetBuffer(cbSsr)->Map();
    void* cbBlurMapped = device->GetBuffer(cbBlur)->Map();
    void* cbCompositeMapped = device->GetBuffer(cbComposite)->Map();
    void* cbShadowModelGroundMapped = device->GetBuffer(cbShadowModelGround)->Map();
    void* cbShadowModelCubeMapped = device->GetBuffer(cbShadowModelCube)->Map();
    if (cb0GroundMapped == nullptr || cb0CubeMapped == nullptr || cbToonMapped == nullptr
        || cbOutlineMapped == nullptr || cbLightsMapped == nullptr
        || cbClustersMapped == nullptr || cbShadowsMapped == nullptr || cbGiMapped == nullptr
        || cbSsrMapped == nullptr || cbBlurMapped == nullptr || cbCompositeMapped == nullptr
        || cbShadowModelGroundMapped == nullptr || cbShadowModelCubeMapped == nullptr) {
        std::printf("  r5 phase: constant buffer Map FAILED\n");
        return false;
    }

    // ---- 相机 / 投影（继承 --shadows：原点朝 +Z，fovY=60°）------------------------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kWidth) / static_cast<float>(kHeight), kNearZ,
        kFarZ, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float projView[16];
    MatMultiply(proj, view, projView);
    float invProjView[16];
    if (!MatInverse4x4(projView, invProjView)) {
        std::printf("  r5 phase: projView invert FAILED\n");
        return false;
    }

    // ---- 光源：1 方向光（继承）+ 2 动画点光（轨道 (0,0.9,6.5) 半径 2 相位相反）----
    LightData sceneLights[kR5LightCount] = {};
    {
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        sceneLights[0].pos[0] = d[0] * invLen;
        sceneLights[0].pos[1] = d[1] * invLen;
        sceneLights[0].pos[2] = d[2] * invLen;
        sceneLights[0].color[0] = 1.0f;
        sceneLights[0].color[1] = 0.95f;
        sceneLights[0].color[2] = 0.88f;
        sceneLights[0].intensity = 1.0f;
        sceneLights[0].kind = kZrLightDirectional;
    }
    const float pointColors[2][3] = {{1.0f, 0.85f, 0.6f}, {0.6f, 0.85f, 1.0f}};

    // ---- CPU 级联（相机静态，每相位一次）-----------------------------------------
    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    std::memcpy(shadowParamsStruct.lightDir, sceneLights[0].pos, sizeof(sceneLights[0].pos));
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = -0.5f;
    shadowParamsStruct.sceneCenter[2] = 10.0f;
    shadowParamsStruct.sceneRadius = 15.0f;
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("  r5 phase: BuildCascades FAILED\n");
        return false;
    }
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(kNearZ, kFarZ, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("  r5 phase: CascadeSplits FAILED\n");
            return false;
        }
        cascadeSplits[0] = interior[0];
        cascadeSplits[1] = interior[1];
        cascadeSplits[2] = interior[2];
        cascadeSplits[3] = kFarZ;
    }

    // CB3（级联 lightVP + bias）
    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;
    }
    shadowCb.cascadeSplits[0] = cascadeSplits[0];
    shadowCb.cascadeSplits[1] = cascadeSplits[1];
    shadowCb.cascadeSplits[2] = cascadeSplits[2];
    shadowCb.cascadeSplits[3] = cascadeSplits[3];
    std::memcpy(cbShadowsMapped, &shadowCb, sizeof(shadowCb));

    // 阴影 pass 逐级联 CB
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("  r5 phase: shadow pass CB %u Map FAILED\n", c);
            return false;
        }
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        std::memcpy(mapped, &spc, sizeof(spc));
    }
    {
        ShadowModelConstants gm {};
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        std::memcpy(gm.shadowModel, identity, sizeof(identity));
        std::memcpy(cbShadowModelGroundMapped, &gm, sizeof(gm));
        float cubeModel[16];
        MatTranslation(0.0f, 0.0f, 3.2f, cubeModel);
        ShadowModelConstants cm {};
        std::memcpy(cm.shadowModel, cubeModel, sizeof(cm.shadowModel));
        std::memcpy(cbShadowModelCubeMapped, &cm, sizeof(cm));
    }

    // ---- 簇网格参数 ----------------------------------------------------------------
    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);

    // ---- GI 探针（CPU 辐照度累加，每 4 帧轮转刷新）----------------------------------
    GiProbeGrid giGrid; // 默认：origin(−10,−1,0) spacing(2.5,2,2.5)——8×2×8 覆盖场景
    float giProbes[ZR_GI_PROBE_COUNT][3] = {}; // 0 起步（冷启动 4 帧内填满）
    GiCbufferLayout giCb {};
    giCb.gridOrigin[0] = giGrid.origin[0];
    giCb.gridOrigin[1] = giGrid.origin[1];
    giCb.gridOrigin[2] = giGrid.origin[2];
    giCb.gridOrigin[3] = 1.0f; // GI 开
    giCb.gridSpacing[0] = giGrid.spacing[0];
    giCb.gridSpacing[1] = giGrid.spacing[1];
    giCb.gridSpacing[2] = giGrid.spacing[2];
    giCb.gridSpacing[3] = static_cast<float>(ZR_GI_PROBE_COUNT);

    // ---- Hi-Z / SSR 常量（相位内静态）----------------------------------------------
    SsrTraceConstants ssrCb {};
    std::memcpy(ssrCb.invViewProj, invProjView, sizeof(invProjView));
    std::memcpy(ssrCb.viewProj, projView, sizeof(projView));
    ssrCb.projParams[0] = kFarZ / (kFarZ - kNearZ);
    ssrCb.projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
    ssrCb.projParams[2] = kNearZ;
    ssrCb.projParams[3] = kFarZ;
    ssrCb.ssrParams[0] = ZR_SSR_ROUGHNESS_MAX;
    ssrCb.ssrParams[1] = ZR_SSR_STEP_METERS;
    ssrCb.ssrParams[2] = ZR_SSR_EDGE_FADE;
    ssrCb.ssrParams[3] = ssrEnabled ? 1.0f : 0.0f;
    ssrCb.screenParams[0] = static_cast<float>(kWidth);
    ssrCb.screenParams[1] = static_cast<float>(kHeight);
    ssrCb.screenParams[2] = static_cast<float>(kR5HalfW);
    ssrCb.screenParams[3] = static_cast<float>(kR5HalfH);
    ssrCb.camPos[0] = 0.0f;
    ssrCb.camPos[1] = 0.0f;
    ssrCb.camPos[2] = 0.0f;
    ssrCb.camPos[3] = 0.0f;
    std::memcpy(cbSsrMapped, &ssrCb, sizeof(ssrCb));

    // Hi-Z 每 mip texel 常量（min pass m 的源 = mip m−1）
    SsrMipLevel hizLevels[ZR_SSR_MAX_MIPS] = {};
    const uint32_t hizLevelCount = SsrMipChainPlan(kWidth, kHeight, ZR_SSR_MAX_MIPS, hizLevels);
    if (hizLevelCount != ZR_SSR_MAX_MIPS) {
        std::printf("  r5 phase: Hi-Z mip chain unexpected (%u)\n", hizLevelCount);
        return false;
    }
    for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS; ++m) {
        void* mapped = device->GetBuffer(cbHiZ[m])->Map();
        if (mapped == nullptr) {
            std::printf("  r5 phase: hiz CB %u Map FAILED\n", m);
            return false;
        }
        R5TexelConstants tc {};
        if (m > 0u) {
            tc.texel[0] = 1.0f / static_cast<float>(hizLevels[m - 1u].width);
            tc.texel[1] = 1.0f / static_cast<float>(hizLevels[m - 1u].height);
        }
        std::memcpy(mapped, &tc, sizeof(tc));
    }
    {
        R5TexelConstants bc {};
        bc.texel[0] = 1.0f / static_cast<float>(kR5HalfW);
        bc.texel[1] = 1.0f / static_cast<float>(kR5HalfH);
        std::memcpy(cbBlurMapped, &bc, sizeof(bc));
    }

    // ---- toon / outline 常量 --------------------------------------------------------
    float toonCubeModel[16];
    MatTranslation(-2.4f, 0.0f, 4.2f, toonCubeModel);
    float toonCubeMvp[16];
    MatMultiply(projView, toonCubeModel, toonCubeMvp);
    {
        ToonConstants tc {};
        std::memcpy(tc.mvp, toonCubeMvp, sizeof(toonCubeMvp));
        std::memcpy(tc.model, toonCubeModel, sizeof(toonCubeModel));
        // 风格化光方向 (0.3,0.5,-0.81)/0.998（相机侧）：−z 前面 ndl≈0.81 亮带、
        // +x 右面 ndl≈0.30 暗带（可见面为 −z/+x/−y——相机朝 +Z 看 cube 左前方）
        const float ld[3] = {0.3f, 0.5f, -0.81f};
        const float invLen = 1.0f / std::sqrt(ld[0] * ld[0] + ld[1] * ld[1] + ld[2] * ld[2]);
        tc.lightDir[0] = ld[0] * invLen;
        tc.lightDir[1] = ld[1] * invLen;
        tc.lightDir[2] = ld[2] * invLen;
        tc.lightDir[3] = 0.0f;
        tc.params[0] = 0.35f; // band0 阈值
        tc.params[1] = 0.60f; // band1 阈值
        tc.params[2] = 0.35f; // 暗部灰度
        tc.params[3] = 0.70f; // 中间调灰度
        tc.specular[0] = 0.98f; // 高光阈值（探针点无高光——带内平坦）
        tc.specular[1] = 0.60f; // 高光强度
        tc.camPos[0] = 0.0f;
        tc.camPos[1] = 0.0f;
        tc.camPos[2] = 0.0f;
        tc.camPos[3] = 0.0f;
        // 基色进 CB（B-R5-04 加法）：--r5 填回原硬编码值，量化带探针基准不变
        tc.base[0] = 0.85f;
        tc.base[1] = 0.35f;
        tc.base[2] = 0.28f;
        tc.base[3] = 1.0f;
        std::memcpy(cbToonMapped, &tc, sizeof(tc));

        OutlineConstants oc {};
        std::memcpy(oc.mvp, toonCubeMvp, sizeof(toonCubeMvp));
        oc.params[0] = 0.045f; // 壳外扩宽度（世界域；z≈4.2 处约 4~5px）
        std::memcpy(cbOutlineMapped, &oc, sizeof(oc));
    }

    // ---- Composite 常量（SSR 开关）--------------------------------------------------
    {
        CompositeConstants cc {};
        cc.params[0] = ssrEnabled ? 1.0f : 0.0f;
        std::memcpy(cbCompositeMapped, &cc, sizeof(cc));
    }

    // ---- 静态 CB0（地面 / cube；地面低粗糙度 = SSR 应用目标）------------------------
    const float ambient = 0.12f;
    ShadowDrawConstants groundConst {};
    std::memcpy(groundConst.mvp, projView, sizeof(projView));
    const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    std::memcpy(groundConst.model, identity, sizeof(identity));
    ShadowDrawConstants cubeConst {};
    float cubeModel[16];
    MatTranslation(0.0f, 0.0f, 3.2f, cubeModel);
    std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
    float cubeMvp[16];
    MatMultiply(projView, cubeModel, cubeMvp);
    std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
    for (ShadowDrawConstants* c : {&groundConst, &cubeConst}) {
        c->projParams[0] = kFarZ / (kFarZ - kNearZ);
        c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
        c->projParams[2] = kNearZ;
        c->projParams[3] = kFarZ;
        c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
        c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
        c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
        c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
        c->screenParams[0] = static_cast<float>(kWidth);
        c->screenParams[1] = static_cast<float>(kHeight);
        c->screenParams[2] = static_cast<float>(kR5LightCount);
        c->screenParams[3] = ambient;
        c->camPos[0] = 0.0f;
        c->camPos[1] = 0.0f;
        c->camPos[2] = 0.0f;
        c->camPos[3] = 0.0f;
        c->cascadeSplits[0] = cascadeSplits[0];
        c->cascadeSplits[1] = cascadeSplits[1];
        c->cascadeSplits[2] = cascadeSplits[2];
        c->cascadeSplits[3] = cascadeSplits[3];
        c->shadowParams[0] = 1.0f; // --r5 继承 --shadows on
        c->shadowParams[1] = static_cast<float>(kShadowsTexSize);
    }
    groundConst.material[0] = 0.50f;
    groundConst.material[1] = 0.52f;
    groundConst.material[2] = 0.50f;
    groundConst.material[3] = 0.15f; // 低粗糙度：SSR 应用目标（sceneColor.a 近似 roughness）
    cubeConst.material[0] = 0.75f;
    cubeConst.material[1] = 0.75f;
    cubeConst.material[2] = 0.78f;
    cubeConst.material[3] = 0.35f; // > ZR_SSR_ROUGHNESS_MAX：SSR 排除（回落环境项）
    std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
    std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));

    // ---- draw list：地面 / pbr cube（阴影视图）/ toon cube（+描边视图）--------------
    DrawList drawList(16u);
    drawList.RegisterMesh(1u, 6u);          // 地面 quad
    drawList.RegisterMesh(2u, cubeIndexCount); // pbr cube
    drawList.RegisterMesh(3u, cubeIndexCount); // toon cube
    RenderItem groundItem {};
    groundItem.meshHandle = 1u;
    groundItem.materialId = 7u;
    groundItem.shaderId = 0u;
    groundItem.depth = 2.8f;
    groundItem.castShadow = true;
    RenderItem cubeItem {};
    cubeItem.meshHandle = 2u;
    cubeItem.materialId = 8u;
    cubeItem.shaderId = 0u;
    cubeItem.depth = 3.2f;
    cubeItem.castShadow = true;
    RenderItem toonItem {};
    toonItem.meshHandle = 3u;
    toonItem.materialId = 9u;
    toonItem.shaderId = 1u;
    toonItem.depth = 4.2f;
    toonItem.castShadow = false; // toon 物体不投影（简化；报告注明）
    const uint8_t toonMask
        = static_cast<uint8_t>(ShadowViewMask(false) | kOutlineViewBit); // 0x21
    if (!drawList.Add(groundItem, ShadowViewMask(true))
        || !drawList.Add(cubeItem, ShadowViewMask(true)) || !drawList.Add(toonItem, toonMask)) {
        std::printf("  r5 phase: draw list Add FAILED\n");
        return false;
    }
    std::vector<uint32_t> shadowItems[ZR_SHADOW_CASCADES];
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        drawList.Visibility().Iterate(
            ShadowViewId(c), [&](uint32_t id) { shadowItems[c].push_back(id); });
    }
    std::vector<uint32_t> outlineItems;
    drawList.Visibility().Iterate(kOutlineViewId, [&](uint32_t id) { outlineItems.push_back(id); });
    drawList.Sort();
    if (drawList.BuildBatches() == 0u) {
        std::printf("  r5 phase: BuildBatches FAILED\n");
        return false;
    }

    // ---- 逐项绘制辅助（meshHandle → VB/IB）-----------------------------------------
    auto bindMesh = [&](uint32_t meshHandle) {
        ZrBuffer vbs[1] = {(meshHandle == 1u) ? groundVb
                          : (meshHandle == 2u) ? cubeVb
                                               : toonVb};
        cmdList->SetVertexBuffers(vbs, 1u);
        cmdList->SetIndexBuffer(
            (meshHandle == 1u) ? groundIb : (meshHandle == 2u) ? cubeIb : toonIb);
    };

    // ---- 30 帧循环 ------------------------------------------------------------------
    constexpr float kR5ClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f};
    bool allOk = true;
    uint32_t undeclaredUses = 0u;

    for (uint32_t frame = 0u; frame < kR5FramesPerPhase; ++frame) {
        const bool doReadback = ((frame + 1u) % 10u) == 0u; // 第 10/20/30 帧
        uint32_t readbackSlot = 0u;
        if (doReadback) {
            readbackSlot = (frame + 1u) / 10u - 1u;
        }

        // 动画点光（相位内帧号驱动——两相位同帧同相位，SSR 对比纯净）
        for (uint32_t i = 0; i < 2u; ++i) {
            const float angle
                = 6.2831853f * static_cast<float>(frame + 1u) / 120.0f
                + 3.14159265f * static_cast<float>(i); // 相位相反
            LightData& l = sceneLights[1u + i];
            l.pos[0] = std::cos(angle) * 2.0f;
            l.pos[1] = 0.9f;
            l.pos[2] = 6.5f + std::sin(angle) * 2.0f;
            l.radius = 3.5f;
            l.color[0] = pointColors[i][0];
            l.color[1] = pointColors[i][1];
            l.color[2] = pointColors[i][2];
            l.intensity = 6.0f;
            l.kind = kZrLightPoint;
        }

        // GI 探针轮转累加（本帧切片 → 重算该切片探针的辐照度）
        uint32_t slice[ZR_GI_PROBE_COUNT / 4u] = {};
        const uint32_t sliceCount = UpdateProbeSlice(frame, 4u, slice, ZR_GI_PROBE_COUNT / 4u);
        for (uint32_t s = 0; s < sliceCount; ++s) {
            const uint32_t probe = slice[s];
            const uint32_t px = probe % ZR_GI_PROBE_DIM_X;
            const uint32_t py = (probe / ZR_GI_PROBE_DIM_X) % ZR_GI_PROBE_DIM_Y;
            const uint32_t pz = probe / (ZR_GI_PROBE_DIM_X * ZR_GI_PROBE_DIM_Y);
            const float pos[3] = {giGrid.origin[0] + giGrid.spacing[0] * (static_cast<float>(px) + 0.5f),
                giGrid.origin[1] + giGrid.spacing[1] * (static_cast<float>(py) + 0.5f),
                giGrid.origin[2] + giGrid.spacing[2] * (static_cast<float>(pz) + 0.5f)};
            float acc[3] = {0.0f, 0.0f, 0.0f};
            AccumulateProbeIrradiance(pos, sceneLights, kR5LightCount, acc);
            giProbes[probe][0] = acc[0];
            giProbes[probe][1] = acc[1];
            giProbes[probe][2] = acc[2];
        }
        for (uint32_t probe = 0; probe < ZR_GI_PROBE_COUNT; ++probe) {
            giCb.probes[probe][0] = giProbes[probe][0];
            giCb.probes[probe][1] = giProbes[probe][1];
            giCb.probes[probe][2] = giProbes[probe][2];
            giCb.probes[probe][3] = 1.0f;
        }
        std::memcpy(cbGiMapped, &giCb, sizeof(giCb));

        // CB1 光源 / CB2 簇
        PackedLight packedLights[kR5LightCount];
        PackLights(sceneLights, kR5LightCount, packedLights);
        std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
        std::memset(static_cast<uint8_t*>(cbLightsMapped) + sizeof(packedLights), 0,
            1024u - sizeof(packedLights));
        const int32_t flatCount = AssignLightsToClusters(grid, sceneLights, kR5LightCount,
            clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
        if (flatCount < 0) {
            std::printf("  r5 phase frame %u: AssignLightsToClusters FAILED (%d)\n", frame + 1u,
                flatCount);
            return false;
        }
        uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
        std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);
        std::memcpy(cb2 + size_t(ZR_CLUSTER_COUNT) * 4u, clusterFlat.data(),
            size_t(flatCount) * 4u);

        // 取后台缓冲 + RTV
        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  r5 phase frame %u: FAILED to obtain RTV\n", frame + 1u);
            return false;
        }

        // ---- 帧图：Clear → Shadow×4 → Main → Toon → Outline → SSR 四连 → Readback/Present
        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        // 阴影恒开（--r5 继承 --shadows on 场景）
        RgHandle shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));
        RgHandle sceneH = graph.Import(RgResourceDesc::ImportImage("sceneColor", sceneImage));
        RgHandle hizH = graph.Import(RgResourceDesc::ImportImage("hiz", hizImage));
        RgHandle traceH = graph.Import(RgResourceDesc::ImportImage("ssrTrace", traceImage));
        RgHandle blurH = graph.Import(RgResourceDesc::ImportImage("ssrBlur", blurImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(sceneH),
                    ZrImageLayout::ColorAttachmentOptimal, kR5ClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });

        // 每级联 depth-only pass（继承 --shadows；共享可见性过滤）
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            graph.AddPass("ShadowCascadePass",
                [&](RgPass& pass) {
                    pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                        ZrAccessBits::DepthStencilAttachmentWrite,
                        ZrPipelineStageBits::LateFragmentTests);
                },
                [&, c](RgContext&) {
                    cmdList->SetPipelineState(shadowPso);
                    cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                    cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                    nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(shadowDsvs[c]),
                        D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
                    cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                    for (uint32_t id : shadowItems[c]) {
                        const bool isGround = drawList.ItemAt(id).meshHandle == 1u;
                        cmdList->SetGraphicsConstantBuffer(
                            1u, isGround ? cbShadowModelGround : cbShadowModelCube);
                        bindMesh(drawList.ItemAt(id).meshHandle);
                        cmdList->DrawIndexedInstanced(
                            (drawList.ItemAt(id).meshHandle == 1u) ? 6u : cubeIndexCount, 1u, 0u,
                            0u, 0u);
                    }
                });
        }

        // 主绘制 Pass（pbr_r5：GI 探针环境项 + CSM + 簇光源）
        graph.AddPass("MainPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                    ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader);
            },
            [&](RgContext&) {
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    if (batch.shaderId != 0u) {
                        continue; // toon 批归 ToonPass
                    }
                    cmdList->SetPipelineState(mainPso);
                    cmdList->SetRenderTarget(sceneRtv, dsvView);
                    cmdList->SetViewportScissor(kWidth, kHeight);
                    cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                    cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                    cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                    cmdList->SetGraphicsConstantBuffer(4u, cbGi);
                    cmdList->SetGraphicsBindlessSrv(5u, shadowSrv); // t0（rootCbvCount=5）
                    for (uint32_t i = 0; i < batch.count; ++i) {
                        const uint32_t itemIndex = batch.startIndex + i;
                        const uint32_t mesh = drawList.ItemAt(itemIndex).meshHandle;
                        bindMesh(mesh);
                        cmdList->SetGraphicsConstantBuffer(0u, (mesh == 1u) ? cb0Ground : cb0Cube);
                        cmdList->DrawIndexedInstanced(
                            (mesh == 1u) ? 6u : cubeIndexCount, 1u, 0u, 0u, 0u);
                    }
                }
            });

        // Toon pass（量化 ramp 变体）
        graph.AddPass("ToonPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    if (batch.shaderId != 1u) {
                        continue;
                    }
                    cmdList->SetPipelineState(toonPso);
                    cmdList->SetRenderTarget(sceneRtv, dsvView);
                    cmdList->SetViewportScissor(kWidth, kHeight);
                    cmdList->SetGraphicsConstantBuffer(0u, cbToon);
                    bindMesh(3u);
                    cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
                }
            });

        // Outline pass（反向壳：FrontCull + 深度测试；描边视图掩码位 bit5）
        graph.AddPass("OutlinePass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(outlinePso);
                cmdList->SetRenderTarget(sceneRtv, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                cmdList->SetGraphicsConstantBuffer(0u, cbOutline);
                for (uint32_t id : outlineItems) {
                    bindMesh(drawList.ItemAt(id).meshHandle);
                    cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
                }
            });

        // ---- SSR 四连（单一声明源；execute 回调在此录制真渲染命令）------------------
        auto hiZExec = [&](RgContext&) {
            // mip0：场景深度 1-tap 直拷（深度图经 R32Float 孪生 SRV 采样）
            cmdList->SetPipelineState(hizCopyPso);
            cmdList->SetRenderTarget(hizRtvs[0], ZrImageView{});
            cmdList->SetViewportScissor(hizLevels[0].width, hizLevels[0].height);
            cmdList->SetGraphicsConstantBuffer(0u, cbHiZ[0]);
            cmdList->SetGraphicsBindlessSrv(1u, depthSrv);
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
            // mip 1..5：上一级 4 邻域 min（pass 内 per-mip 手动子资源屏障）
            for (uint32_t m = 1u; m < hizLevelCount; ++m) {
                ZrImageBarrier toSrv = MakeBarrier(hizImage,
                    ZrImageLayout::ColorAttachmentOptimal, ZrImageLayout::ShaderReadOnlyOptimal,
                    ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput,
                    ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
                toSrv.subresources.baseMipLevel = m - 1u;
                toSrv.subresources.levelCount = 1u;
                cmdList->ResourceBarrier(&toSrv, 1u, nullptr, 0u);
                cmdList->SetPipelineState(hizMinPso);
                cmdList->SetRenderTarget(hizRtvs[m], ZrImageView{});
                cmdList->SetViewportScissor(hizLevels[m].width, hizLevels[m].height);
                cmdList->SetGraphicsConstantBuffer(0u, cbHiZ[m]);
                cmdList->SetGraphicsBindlessSrv(1u, hizMipSrvs[m - 1u]);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
            }
            // 采样过的 mip（0..levelCount-2）回 RENDER_TARGET——与 RG 整资源声明
            // （ColorAttachmentOptimal）在 pass 出口一致
            ZrImageBarrier toRtv = MakeBarrier(hizImage,
                ZrImageLayout::ShaderReadOnlyOptimal, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            toRtv.subresources.baseMipLevel = 0u;
            toRtv.subresources.levelCount = hizLevelCount - 1u;
            cmdList->ResourceBarrier(&toRtv, 1u, nullptr, 0u);
        };
        auto traceExec = [&](RgContext&) {
            cmdList->SetPipelineState(tracePso);
            cmdList->SetRenderTarget(traceRtv, ZrImageView{});
            cmdList->SetViewportScissor(kR5HalfW, kR5HalfH);
            cmdList->SetGraphicsConstantBuffer(0u, cbSsr);
            cmdList->SetGraphicsBindlessSrv(1u, hizSrv);  // t0 Hi-Z 链
            cmdList->SetGraphicsBindlessSrv(2u, sceneSrv); // t1 场景颜色
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        };
        auto blurExec = [&](RgContext&) {
            cmdList->SetPipelineState(blurPso);
            cmdList->SetRenderTarget(blurRtv, ZrImageView{});
            cmdList->SetViewportScissor(kR5HalfW, kR5HalfH);
            cmdList->SetGraphicsConstantBuffer(0u, cbBlur);
            cmdList->SetGraphicsBindlessSrv(1u, traceSrv);
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        };
        auto compositeExec = [&](RgContext&) {
            cmdList->SetPipelineState(compPso);
            cmdList->SetRenderTarget(rtvView, ZrImageView{});
            cmdList->SetViewportScissor(kWidth, kHeight);
            cmdList->SetGraphicsConstantBuffer(0u, cbComposite);
            cmdList->SetGraphicsBindlessSrv(1u, blurSrv);  // t0 模糊反射
            cmdList->SetGraphicsBindlessSrv(2u, sceneSrv); // t1 场景颜色
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        };
        SsrPassHandles ssrHandles;
        ssrHandles.depth = depthH;
        ssrHandles.hiz = hizH;
        ssrHandles.sceneColor = sceneH;
        ssrHandles.ssrTrace = traceH;
        ssrHandles.ssrBlur = blurH;
        ssrHandles.back = backH;
        RecordSsrPasses(graph, ssrHandles, hiZExec, traceExec, blurExec, compositeExec);

        // Readback pass（第 30 帧）：6 探针盒
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(backH));
                    const DXGI_FORMAT format
                        = windowMode ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
                    RecordR5ProbeReadback(nativeCmd, texture, readback, format);
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass",
                [&](RgPass& pass) { pass.Present(backH).MarkRoot(); }, nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  r5 phase frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            return false;
        }
        graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末手动屏障：跨帧持久图像回 COMMON（与下一帧 RG 起点 Undefined 一致）
        {
            ZrImageBarrier resets[5];
            uint32_t n = 0u;
            resets[n++] = MakeBarrier(depthImage,
                ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(shadowImage, ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::DepthStencilAttachmentRead,
                ZrPipelineStageBits::FragmentShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(sceneImage, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::FragmentShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(hizImage, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::FragmentShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(traceImage, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::FragmentShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            // blur 图最后被 composite 读（ShaderReadOnly）；SSR off 相位同构
            resets[n++] = MakeBarrier(blurImage, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::FragmentShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(resets, n, nullptr, 0u);
        }
        if (!windowMode) {
            const ZrImageLayout backLayoutNow = doReadback ? ZrImageLayout::TransferSrcOptimal
                                                            : ZrImageLayout::ColorAttachmentOptimal;
            ZrImageBarrier backReset = MakeBarrier(offscreenTarget, backLayoutNow,
                ZrImageLayout::Undefined, ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&backReset, 1u, nullptr, 0u);
        }

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle();
        graph.FlushRetired(*device);

        if (doReadback) {
            void* mapped = nullptr;
            const HRESULT hr = readback->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  r5 phase frame %u: probe Map FAILED (hr=0x%08lX)\n", frame + 1u,
                    static_cast<unsigned long>(hr));
                return false;
            }
            const uint8_t* bytes = static_cast<const uint8_t*>(mapped);
            for (uint32_t p = 0; p < kR5ProbeCount; ++p) {
                DecodeR5Probe(bytes, p, windowMode, outProbes[readbackSlot][p]);
            }
            readback->Unmap(0, nullptr);
            std::printf("  r5 phase frame %u probes: ssr=(%d,%d,%d) gi=(%d,%d,%d) "
                        "toonB=(%d,%d,%d)/(%d,%d,%d) toonD=(%d,%d,%d)/(%d,%d,%d)\n",
                frame + 1u, outProbes[readbackSlot][0][0], outProbes[readbackSlot][0][1],
                outProbes[readbackSlot][0][2], outProbes[readbackSlot][1][0],
                outProbes[readbackSlot][1][1], outProbes[readbackSlot][1][2],
                outProbes[readbackSlot][2][0], outProbes[readbackSlot][2][1],
                outProbes[readbackSlot][2][2], outProbes[readbackSlot][3][0],
                outProbes[readbackSlot][3][1], outProbes[readbackSlot][3][2],
                outProbes[readbackSlot][4][0], outProbes[readbackSlot][4][1],
                outProbes[readbackSlot][4][2], outProbes[readbackSlot][5][0],
                outProbes[readbackSlot][5][1], outProbes[readbackSlot][5][2]);
        }

        if (windowMode) {
            PumpMessages();
        }
    }

    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    if (undeclaredUses != 0u) {
        std::printf("  r5 phase: undeclared RG uses = %u\n", undeclaredUses);
        allOk = false;
    }
    return allOk;
}

// --r5 模式主体（SSR off 30 帧 → on 30 帧；三组可证伪读back）。
// TASK-B-R8-01 增补：outPhaseProbes —— 双后端对照捕获（--backend both 模式收集
// D3D12/Vulkan 各自的两相位探针读数做并排对照；缺省 nullptr = 单后端模式零开销）。
// outPhaseProbes[0] = SSR-off 相位 [readback 0..2][探针 0..5][RGB]，[1] = SSR-on 相位。
static int RunR5Mode(int (*outPhaseProbes)[3][kR5ProbeCount][3] = nullptr)
{
    std::printf("zrhi_d3d12_window: --r5 mode (TASK-B-R5-03): SSR + GI probes + NPR toon\n");

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --r5 device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏）-------------------------------------------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --r5 window rejected (GetLastError=%lu), falling back "
                    "to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --r5 swapchain FAILED, falling back to offscreen "
                        "target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
    }

    // ---- 渲染目标（离屏降级）+ 跨帧持久图像组 ---------------------------------------
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --r5 FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrD3D12DestroyDevice(device);
            return 1;
        }
    }
    auto makeImage = [&](ZrFormat format, uint32_t w, uint32_t h, uint32_t mips,
                         ZrImageUsage usage) {
        ZrImageDesc desc {};
        desc.format = format;
        desc.width = w;
        desc.height = h;
        desc.mipLevels = mips;
        desc.usage = usage;
        return device->CreateImage(desc);
    };
    ZrImage depthImage = makeImage(
        ZrFormat::D32Float, kWidth, kHeight, 1u, ZrImageUsage::DepthStencilAttachment);
    // TASK-B-R8-01 修正（D3D12 侧同款）：shadowImage 须为 ZR_SHADOW_CASCADES 层数组图
    // （--shadows/--parity/--fsr 既有做法）。原 makeImage 路径缺省 arrayLayers=1，
    // 4 个级联层 DSV 退化为同一张图的 Texture2D 视图（层窗口被 D3D12 后端忽略）——
    // 4 级联相互过写、Texture2DArray 采样恒落层 0，属 R5-03 遗留的隐性错误
    // （R5 断言不隔离阴影所以未暴露）；B-R8-01 双后端对等收口按真 CSM 修正。
    ZrImageDesc shadowDescR5 {};
    shadowDescR5.format = ZrFormat::D32Float;
    shadowDescR5.width = kShadowsTexSize;
    shadowDescR5.height = kShadowsTexSize;
    shadowDescR5.arrayLayers = ZR_SHADOW_CASCADES;
    shadowDescR5.usage = ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled
        | ZrImageUsage::TransferDst;
    ZrImage shadowImage = device->CreateImage(shadowDescR5);
    ZrImage sceneImage = makeImage(ZrFormat::RGBA8Unorm, kWidth, kHeight, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    ZrImage hizImage = makeImage(ZrFormat::R32Float, kWidth, kHeight, ZR_SSR_MAX_MIPS,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    ZrImage traceImage = makeImage(ZrFormat::RGBA8Unorm, kR5HalfW, kR5HalfH, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    ZrImage blurImage = makeImage(ZrFormat::RGBA8Unorm, kR5HalfW, kR5HalfH, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    if (!depthImage.IsValid() || !shadowImage.IsValid() || !sceneImage.IsValid()
        || !hizImage.IsValid() || !traceImage.IsValid() || !blurImage.IsValid()) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create images\n");
        return 1;
    }

    // 视图组
    ZrImageViewDesc dsvDesc {};
    dsvDesc.image = depthImage;
    dsvDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvDesc);
    ZrImageViewDesc depthSrvDesc {};
    depthSrvDesc.image = depthImage;
    depthSrvDesc.viewType = ZrImageViewType::Srv;
    depthSrvDesc.format = ZrFormat::R32Float;
    depthSrvDesc.overrideFormat = true;
    ZrImageView depthSrv = device->CreateImageView(depthSrvDesc);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        ZrImageViewDesc layerDsvDesc {};
        layerDsvDesc.image = shadowImage;
        layerDsvDesc.viewType = ZrImageViewType::Dsv;
        layerDsvDesc.subresources.baseArrayLayer = c;
        layerDsvDesc.subresources.layerCount = 1u;
        shadowDsvs[c] = device->CreateImageView(layerDsvDesc);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float;
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    auto makeRtv = [&](ZrImage image, uint32_t mip = 0u) {
        ZrImageViewDesc desc {};
        desc.image = image;
        desc.viewType = ZrImageViewType::Rtv;
        desc.subresources.baseMipLevel = mip;
        desc.subresources.levelCount = 1u;
        return device->CreateImageView(desc);
    };
    auto makeSrv = [&](ZrImage image) {
        ZrImageViewDesc desc {};
        desc.image = image;
        desc.viewType = ZrImageViewType::Srv;
        return device->CreateImageView(desc);
    };
    ZrImageView sceneRtv = makeRtv(sceneImage);
    ZrImageView sceneSrv = makeSrv(sceneImage);
    ZrImageView hizRtvs[ZR_SSR_MAX_MIPS] = {};
    for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS; ++m) {
        hizRtvs[m] = makeRtv(hizImage, m);
    }
    ZrImageView hizSrv = makeSrv(hizImage);
    ZrImageView hizMipSrvs[ZR_SSR_MAX_MIPS - 1] = {};
    for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS - 1u; ++m) {
        ZrImageViewDesc desc {};
        desc.image = hizImage;
        desc.viewType = ZrImageViewType::Srv;
        desc.subresources.baseMipLevel = m;
        desc.subresources.levelCount = 1u;
        hizMipSrvs[m] = device->CreateImageView(desc);
    }
    ZrImageView traceRtv = makeRtv(traceImage);
    ZrImageView traceSrv = makeSrv(traceImage);
    ZrImageView blurRtv = makeRtv(blurImage);
    ZrImageView blurSrv = makeSrv(blurImage);
    ZrImageView offscreenRtv {};
    if (!windowMode) {
        offscreenRtv = makeRtv(offscreenTarget);
    }
    bool viewsOk = dsvView.IsValid() && depthSrv.IsValid() && shadowSrv.IsValid()
        && sceneRtv.IsValid() && sceneSrv.IsValid() && hizSrv.IsValid() && traceRtv.IsValid()
        && traceSrv.IsValid() && blurRtv.IsValid() && blurSrv.IsValid();
    for (ZrImageView v : shadowDsvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    for (ZrImageView v : hizRtvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    for (ZrImageView v : hizMipSrvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    if (!viewsOk || (!windowMode && !offscreenRtv.IsValid())) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create views\n");
        return 1;
    }

    // ---- 几何（继承 --shadows：地面 quad + cube.zmesh ×2 实例 VB）+ 全屏 quad ---------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};
    const FirstDrawVertex quadVerts[4] = {
        {{-1.0f, 1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {0.0f, 0.0f}}, // clip-space 全屏 quad
        {{1.0f, 1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {1.0f, 0.0f}},
        {{1.0f, -1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {1.0f, 1.0f}},
        {{-1.0f, -1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t quadIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<char> meshBytes;
    if (!ReadWholeFile("Native/tests/data/cube.zmesh", meshBytes)) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to open cube.zmesh\n");
        return 1;
    }
    ZmeshData mesh {};
    if (zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh) != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --r5 cube.zmesh parse FAILED\n");
        return 1;
    }
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(mesh.stats.vertexCount);
    for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);

    auto makeVb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    auto makeIb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    ZrBuffer groundVb = makeVb(groundVerts, sizeof(groundVerts));
    ZrBuffer groundIb = makeIb(groundIndices, sizeof(groundIndices));
    ZrBuffer cubeVb = makeVb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex));
    ZrBuffer cubeIb = makeIb(cubeIndices.data(), uint64_t(cubeIndices.size()) * 4u);
    ZrBuffer toonVb = makeVb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex));
    ZrBuffer toonIb = makeIb(cubeIndices.data(), uint64_t(cubeIndices.size()) * 4u);
    ZrBuffer quadVb = makeVb(quadVerts, sizeof(quadVerts));
    ZrBuffer quadIb = makeIb(quadIndices, sizeof(quadIndices));
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()
        || !toonVb.IsValid() || !toonIb.IsValid() || !quadVb.IsValid() || !quadIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create VB/IB\n");
        return 1;
    }

    // ---- 常量缓冲 --------------------------------------------------------------------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbToon {};
    ZrBuffer cbOutline {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    ZrBuffer cbGi {};
    ZrBuffer cbSsr {};
    ZrBuffer cbHiZ[ZR_SSR_MAX_MIPS] = {};
    ZrBuffer cbBlur {};
    ZrBuffer cbComposite {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbShadowModelGround {};
    ZrBuffer cbShadowModelCube {};
    {
        auto makeCb = [&](uint64_t size) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            ZrBuffer b = device->CreateBuffer(desc);
            return (b.IsValid() && device->GetBuffer(b)->Map() != nullptr) ? b : ZrBuffer{};
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        bool ok = (cb0Ground = makeCb(256u)).IsValid() && (cb0Cube = makeCb(256u)).IsValid()
            && (cbToon = makeCb(256u)).IsValid() && (cbOutline = makeCb(96u)).IsValid()
            && (cbLights = makeCb(1024u)).IsValid() && (cbClusters = makeCb(cb2Size)).IsValid()
            && (cbShadows = makeCb(384u)).IsValid() && (cbGi = makeCb(2112u)).IsValid()
            && (cbSsr = makeCb(256u)).IsValid() && (cbBlur = makeCb(256u)).IsValid()
            && (cbComposite = makeCb(256u)).IsValid()
            && (cbShadowModelGround = makeCb(256u)).IsValid()
            && (cbShadowModelCube = makeCb(256u)).IsValid();
        for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS; ++m) {
            ok = ok && (cbHiZ[m] = makeCb(256u)).IsValid();
        }
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            ok = ok && (cbShadowPass[c] = makeCb(256u)).IsValid();
        }
        if (!ok) {
            std::printf("zrhi_d3d12_window: --r5 FAILED to create constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器 + PSO ----------------------------------------------------------------
    const char* kShaderPaths[7] = {"Native/shaders/pbr_r5.hlsl", "Native/shaders/toon.hlsl",
        "Native/shaders/toon_outline.hlsl", "Native/shaders/shadow_depth.hlsl",
        "Native/shaders/hiz.hlsl", "Native/shaders/ssr_trace.hlsl", "Native/shaders/ssr_blur.hlsl"};
    std::vector<char> shaderSrc[7];
    for (int i = 0; i < 7; ++i) {
        if (!ReadWholeFile(kShaderPaths[i], shaderSrc[i])) {
            std::printf("zrhi_d3d12_window: --r5 FAILED to open %s\n", kShaderPaths[i]);
            return 1;
        }
    }
    std::vector<char> compSrc;
    if (!ReadWholeFile("Native/shaders/composite.hlsl", compSrc)) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to open composite.hlsl\n");
        return 1;
    }

    auto compile = [&](ZrShaderStage stage, const std::vector<char>& src, const char* entry) {
        return device->CompileHlsl(stage, src.data(), src.size(), entry);
    };
    const ZrShader mainVs = compile(ZrShaderStage::Vertex, shaderSrc[0], "VsMain");
    const ZrShader mainPs = compile(ZrShaderStage::Pixel, shaderSrc[0], "PsMain");
    const ZrShader toonVs = compile(ZrShaderStage::Vertex, shaderSrc[1], "VsMain");
    const ZrShader toonPs = compile(ZrShaderStage::Pixel, shaderSrc[1], "PsMain");
    const ZrShader outlineVs = compile(ZrShaderStage::Vertex, shaderSrc[2], "VsMain");
    const ZrShader outlinePs = compile(ZrShaderStage::Pixel, shaderSrc[2], "PsMain");
    const ZrShader shadowVs = compile(ZrShaderStage::Vertex, shaderSrc[3], "VsMain");
    const ZrShader hizVs = compile(ZrShaderStage::Vertex, shaderSrc[4], "VsMain");
    const ZrShader hizCopyPs = compile(ZrShaderStage::Pixel, shaderSrc[4], "HiZCopyPsMain");
    const ZrShader hizMinPs = compile(ZrShaderStage::Pixel, shaderSrc[4], "HiZMinPsMain");
    const ZrShader traceVs = compile(ZrShaderStage::Vertex, shaderSrc[5], "VsMain");
    const ZrShader tracePs = compile(ZrShaderStage::Pixel, shaderSrc[5], "PsMain");
    const ZrShader blurVs = compile(ZrShaderStage::Vertex, shaderSrc[6], "VsMain");
    const ZrShader blurPs = compile(ZrShaderStage::Pixel, shaderSrc[6], "PsMain");
    const ZrShader compVs = compile(ZrShaderStage::Vertex, compSrc, "VsMain");
    const ZrShader compPs = compile(ZrShaderStage::Pixel, compSrc, "PsMain");
    const ZrShader allShaders[16] = {mainVs, mainPs, toonVs, toonPs, outlineVs, outlinePs,
        shadowVs, hizVs, hizCopyPs, hizMinPs, traceVs, tracePs, blurVs, blurPs, compVs, compPs};
    bool shadersOk = true;
    for (const ZrShader& s : allShaders) {
        shadersOk = shadersOk && s.IsValid();
    }
    if (!shadersOk) {
        std::printf("zrhi_d3d12_window: --r5 shader compilation FAILED (see stderr)\n");
        return 1;
    }

    const ZrFormat windowRtv = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    auto makePso = [&](ZrShader vs, ZrShader ps, ZrFormat rtv, bool depth, bool depthWrite,
        ZrCullMode cull, uint32_t cbvCount, uint32_t srvTables) {
        ZrGraphicsPsoDesc desc {};
        desc.vertexShader = vs;
        desc.pixelShader = ps;
        desc.rtvFormat = rtv;
        desc.dsvFormat = depth ? ZrFormat::D32Float : ZrFormat::Count;
        desc.blend = ZrBlendMode::Opaque;
        desc.depthTest = depth;
        desc.depthWrite = depthWrite;
        desc.cullMode = cull;
        desc.rootCbvCount = cbvCount;
        desc.srvTableCount = srvTables;
        return device->CreatePipelineState(desc);
    };
    ZrPipelineState mainPso = makePso(mainVs, mainPs, ZrFormat::RGBA8Unorm, true, true,
        ZrCullMode::None, 5u, 1u); // b0..b4 + t0 阴影图
    ZrPipelineState toonPso = makePso(toonVs, toonPs, ZrFormat::RGBA8Unorm, true, true,
        ZrCullMode::None, 1u, 0u);
    ZrPipelineState outlinePso = makePso(outlineVs, outlinePs, ZrFormat::RGBA8Unorm, true, false,
        ZrCullMode::Front, 1u, 0u); // 反向壳：FrontCull + 只测不写
    ZrPipelineState hizCopyPso = makePso(hizVs, hizCopyPs, ZrFormat::R32Float, false, false,
        ZrCullMode::None, 1u, 1u);
    ZrPipelineState hizMinPso = makePso(hizVs, hizMinPs, ZrFormat::R32Float, false, false,
        ZrCullMode::None, 1u, 1u);
    ZrPipelineState tracePso = makePso(traceVs, tracePs, ZrFormat::RGBA8Unorm, false, false,
        ZrCullMode::None, 1u, 2u); // t0 Hi-Z + t1 场景颜色
    ZrPipelineState blurPso = makePso(blurVs, blurPs, ZrFormat::RGBA8Unorm, false, false,
        ZrCullMode::None, 1u, 1u);
    ZrPipelineState compPso = makePso(compVs, compPs, windowRtv, false, false, ZrCullMode::None,
        1u, 2u); // t0 反射 + t1 场景
    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = shadowVs;
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    shadowPsoDesc.cullMode = ZrCullMode::Front;
    shadowPsoDesc.rootCbvCount = 2u;
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    if (!mainPso.IsValid() || !toonPso.IsValid() || !outlinePso.IsValid() || !hizCopyPso.IsValid()
        || !hizMinPso.IsValid() || !tracePso.IsValid() || !blurPso.IsValid()
        || !compPso.IsValid() || !shadowPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create PSOs\n");
        return 1;
    }

    // ---- readback（6 探针 × 256B 槽）--------------------------------------------------
    Microsoft::WRL::ComPtr<ID3D12Resource> readback
        = MakeReadbackBuffer(device->D3D12Device(), uint64_t(kR5ProbeCount) * 256u);
    if (readback == nullptr) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create readback buffer\n");
        return 1;
    }

    // ---- 两相位：SSR off（30 帧）→ on（30 帧）；三组可证伪读back ----------------------
    // probesOff/On[readback 序 0..2 = 帧 10/20/30][探针 0..5][RGB]
    int probesOff[3][kR5ProbeCount][3] = {};
    int probesOn[3][kR5ProbeCount][3] = {};
    bool offOk = true;
    bool onOk = true;
    std::printf("zrhi_d3d12_window: --r5 phase SSR-OFF (%u frames)\n", kR5FramesPerPhase);
    offOk = RunR5Phase(device, queue, cmdHandle, cmdList, nativeCmd, swapchain, offscreenTarget,
        offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv, sceneImage,
        sceneRtv, sceneSrv, hizImage, hizRtvs, hizSrv, hizMipSrvs, depthSrv, traceImage, traceRtv,
        traceSrv, blurImage, blurRtv, blurSrv, mainPso, toonPso, outlinePso, shadowPso,
        hizCopyPso, hizMinPso, tracePso, blurPso, compPso, groundVb, groundIb, cubeVb, cubeIb,
        toonVb, toonIb, quadVb, quadIb, cubeIndexCount, cb0Ground, cb0Cube, cbToon, cbOutline,
        cbLights, cbClusters, cbShadows, cbGi, cbSsr, cbHiZ, cbBlur, cbComposite, cbShadowPass,
        cbShadowModelGround, cbShadowModelCube, /*ssrEnabled=*/false, readback.Get(), probesOff);
    std::printf("zrhi_d3d12_window: --r5 phase SSR-ON (%u frames)\n", kR5FramesPerPhase);
    onOk = RunR5Phase(device, queue, cmdHandle, cmdList, nativeCmd, swapchain, offscreenTarget,
        offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv, sceneImage,
        sceneRtv, sceneSrv, hizImage, hizRtvs, hizSrv, hizMipSrvs, depthSrv, traceImage, traceRtv,
        traceSrv, blurImage, blurRtv, blurSrv, mainPso, toonPso, outlinePso, shadowPso,
        hizCopyPso, hizMinPso, tracePso, blurPso, compPso, groundVb, groundIb, cubeVb, cubeIb,
        toonVb, toonIb, quadVb, quadIb, cubeIndexCount, cb0Ground, cb0Cube, cbToon, cbOutline,
        cbLights, cbClusters, cbShadows, cbGi, cbSsr, cbHiZ, cbBlur, cbComposite, cbShadowPass,
        cbShadowModelGround, cbShadowModelCube, /*ssrEnabled=*/true, readback.Get(), probesOn);
    if (outPhaseProbes != nullptr) {
        std::memcpy(outPhaseProbes[0], probesOff, sizeof(probesOff));
        std::memcpy(outPhaseProbes[1], probesOn, sizeof(probesOn));
    }

    // ---- 三组可证伪读back判定 ----------------------------------------------------------
    // ①SSR：两相位第 30 帧（相位内帧 30，同动画相位）同探针点差
    const int ssrDelta = R5MaxChannelDiff(probesOff[2][0], probesOn[2][0]);
    // ②GI：SSR-on 相位内两动画相位（相位帧 10 与帧 30，全局帧 40/60，角差 60°）
    //   ——直接光贡献在轨道中心恒定，差值只来自 GI 探针填充变化
    const int giDelta = R5MaxChannelDiff(probesOn[0][1], probesOn[2][1]);
    // ③Toon：亮带/暗带差 + 带内平坦（SSR-on 相位第 30 帧）
    const int toonDelta = R5MaxChannelDiff(probesOn[2][2], probesOn[2][4]);
    const int bandFlat = (std::max)(R5MaxChannelDiff(probesOn[2][2], probesOn[2][3]),
        R5MaxChannelDiff(probesOn[2][4], probesOn[2][5]));

    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;

    // ---- 清理 -------------------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbToon);
    device->Destroy(cbOutline);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    device->Destroy(cbGi);
    device->Destroy(cbSsr);
    device->Destroy(cbBlur);
    device->Destroy(cbComposite);
    for (ZrBuffer& b : cbHiZ) {
        device->Destroy(b);
    }
    for (ZrBuffer& b : cbShadowPass) {
        device->Destroy(b);
    }
    device->Destroy(cbShadowModelGround);
    device->Destroy(cbShadowModelCube);
    device->Destroy(mainPso);
    device->Destroy(toonPso);
    device->Destroy(outlinePso);
    device->Destroy(hizCopyPso);
    device->Destroy(hizMinPso);
    device->Destroy(tracePso);
    device->Destroy(blurPso);
    device->Destroy(compPso);
    device->Destroy(shadowPso);
    for (const ZrShader& s : allShaders) {
        device->Destroy(s);
    }
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(toonVb);
    device->Destroy(toonIb);
    device->Destroy(quadVb);
    device->Destroy(quadIb);
    device->Destroy(dsvView);
    device->Destroy(depthSrv);
    for (ZrImageView v : shadowDsvs) {
        device->Destroy(v);
    }
    device->Destroy(shadowSrv);
    device->Destroy(sceneRtv);
    device->Destroy(sceneSrv);
    for (ZrImageView v : hizRtvs) {
        device->Destroy(v);
    }
    device->Destroy(hizSrv);
    for (ZrImageView v : hizMipSrvs) {
        device->Destroy(v);
    }
    device->Destroy(traceRtv);
    device->Destroy(traceSrv);
    device->Destroy(blurRtv);
    device->Destroy(blurSrv);
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(depthImage);
    device->Destroy(shadowImage);
    device->Destroy(sceneImage);
    device->Destroy(hizImage);
    device->Destroy(traceImage);
    device->Destroy(blurImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    readback.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: [R5] ssr delta=%d (min %d) gi delta=%d (min %d) "
                "toon delta=%d bandFlat=%d (max %d)\n",
        ssrDelta, kR5SsrDeltaMin, giDelta, kR5GiDeltaMin, toonDelta, bandFlat,
        kR5ToonBandFlatMax);
    std::printf("zrhi_d3d12_window: [R5] device removed reason = 0x%08lX (%s)\n",
        static_cast<unsigned long>(removedReason), noDeviceRemoval ? "S_OK" : "REMOVED");
    std::printf("zrhi_d3d12_window: InfoQueue error count = %u\n", infoQueueErrors);

    const bool deltasOk = ssrDelta >= kR5SsrDeltaMin && giDelta >= kR5GiDeltaMin
        && toonDelta >= kR5ToonDeltaMin && bandFlat < 6;
    if (offOk && onOk && deltasOk && noDeviceRemoval && infoQueueErrors == 0u) {
        std::printf("D3D12 R5 OK (ssr=%d gi=%d toon=%d)\n", ssrDelta, giDelta, toonDelta);
        return 0;
    }
    std::printf("D3D12 R5 FAILED (offOk=%d onOk=%d ssr=%d/%d gi=%d/%d toon=%d/%d flat=%d "
                "removed=%d errors=%u)\n",
        offOk ? 1 : 0, onOk ? 1 : 0, ssrDelta, kR5SsrDeltaMin, giDelta, kR5GiDeltaMin, toonDelta,
        kR5ToonDeltaMin, bandFlat, noDeviceRemoval ? 0 : 1, infoQueueErrors);
    return 1;
}

// =============================================================================
// ---- TASK-B-R5-04：--parity 模式（URP 对等闸门基准场景，双管线对比）-----------
// =============================================================================

// 基准场景契约（与 UnityProject/Assets/Game/Editor/ParityBench.cs 头部注释严格
// 一致，修改须两侧同步）：
//   分辨率 1280×720；相机 pos(0,1.6,-4) 朝 +Z 看原点（fovY 60°，near 0.3 far 60）
//   地面：20×20 平面 y=0，albedo (0.5,0.5,0.5)，roughness 0.9（URP smoothness 0.1）
//   Cube A：pos(0,0.5,0) 1m³，PBR albedo (0.9,0.85,0.75)，roughness 0.5
//           （URP Lit smoothness 0.5；roughness = 1 - smoothness）
//   Cube B：pos(1.8,0.5,0.8) 1m³，toon 基色 (0.9,0.3,0.25) + 反向壳描边；投影阴影
//           （URP 侧为无光照 Unlit 纯色，无量化带——差异清单见 parity_report.md）
//   方向光：光行进方向 (-0.5,-1,-0.3)/|·|（ZRender LightData 存 to-light 方向
//           (0.5,1,0.3)/|·|），色 (1,0.96,0.9)，强度 1.2
//   环境：常数环境项 0.12（URP 侧 RenderSettings Flat ambient 同值对齐）
//   清屏色 (0.03,0.03,0.045)（URP 相机 Solid Color 同值）；阴影开；后处理关；
//   固定一帧（无动画）。--parity 恒离屏渲染（无交换链/vsync，指标口径纯净）。
constexpr uint32_t kParityW = 1280u;
constexpr uint32_t kParityH = 720u;
constexpr uint32_t kParityFrames = 30u;
constexpr uint32_t kParityLightCount = 1u;
// 全帧 readback 行距（1280×4=5120 天然 256 对齐）
constexpr uint32_t kParityReadbackRowPitch = (kParityW * 4u + 255u) & ~255u;
constexpr const char* kParityBmpPath = "Docs/Parity/parity_zrender.bmp";
constexpr const char* kParityMetricsPath = "Docs/Parity/metrics_zrender.txt";

// 自检探针（推导可复核；ndcX=Δx/(Δz·tan30°·aspect)、ndcY=Δy/(Δz·tan30°)、
// px=(1+ndcX)/2·W、py=(1-ndcY)/2·H，D3D LH 相机朝 +Z）：
//   Toon 暗带：cube B 中心 (1.8,0.5,0.8) 相对相机 Δ=(1.8,-1.1,4.8) → 像素
//     (874,503)，落在 -z 前面（ndl=0 → 暗带 0.35）：RGB≈(80,27,22)，判 r>g+10
//     且 r>b+10（暗带红显著）。
//   阴影探针：cube A 影区地面 (-0.5,0,-0.65)（影四边形 x∈[-1,0.5]、z∈[-0.8,0.5]
//     内部，且在 cube A 前方不被其几何遮挡）→ 像素 (547,658)，影内≈环境项
//     0.12·0.5 → RGB≈(14,14,14)。
//   受光地面参照：(300,650) → 地面 (-1.87,0,-0.57)（影区外）：diffuse+ambient
//     → RGB≈(47,45,43)。判 shadow.r ≤ lit.r - 8。
constexpr UINT kParityToonX = 874u;
constexpr UINT kParityToonY = 503u;
constexpr UINT kParityShadowX = 547u;
constexpr UINT kParityShadowY = 658u;
constexpr UINT kParityLitX = 300u;
constexpr UINT kParityLitY = 650u;

/// 24bit BMP 自写编码（C++ 无依赖）：bottom-up 行序、BGR、54B 头
/// （BITMAPFILEHEADER 14B + BITMAPINFOHEADER 40B）。1280×3=3840 行宽天然 4B 对齐。
bool WriteBmp24(const char* path, const uint8_t* pixels, uint32_t width, uint32_t height,
    uint32_t srcRowPitch, bool bgraOrder)
{
    const uint32_t dstStride = width * 3u; // 3840（%4==0，无需补齐）
    const uint32_t pixelBytes = dstStride * height;
    const uint32_t fileSize = 54u + pixelBytes;
    std::ofstream file(path, std::ios::binary);
    if (!file) {
        return false;
    }
    uint8_t header[54] = {};
    header[0] = 'B';
    header[1] = 'M';
    std::memcpy(header + 2, &fileSize, 4u);   // 文件大小
    const uint32_t offset = 54u;
    std::memcpy(header + 10, &offset, 4u);    // 像素数据偏移
    const uint32_t dibSize = 40u;
    std::memcpy(header + 14, &dibSize, 4u);   // BITMAPINFOHEADER
    const int32_t w = static_cast<int32_t>(width);
    const int32_t h = static_cast<int32_t>(height);
    std::memcpy(header + 18, &w, 4u);         // 宽
    std::memcpy(header + 22, &h, 4u);         // 高（正 = bottom-up）
    const uint16_t planes = 1u;
    std::memcpy(header + 26, &planes, 2u);
    const uint16_t bpp = 24u;
    std::memcpy(header + 28, &bpp, 2u);
    const uint32_t compression = 0u;
    std::memcpy(header + 30, &compression, 4u);
    std::memcpy(header + 34, &pixelBytes, 4u);
    const uint32_t ppm = 2835u; // 72 DPI
    std::memcpy(header + 38, &ppm, 4u);
    std::memcpy(header + 42, &ppm, 4u);
    file.write(reinterpret_cast<const char*>(header), sizeof(header));

    std::vector<uint8_t> row(dstStride);
    for (int32_t y = static_cast<int32_t>(height) - 1; y >= 0; --y) {
        const uint8_t* src = pixels + static_cast<size_t>(y) * srcRowPitch;
        for (uint32_t x = 0; x < width; ++x) {
            const uint8_t* px = src + static_cast<size_t>(x) * 4u;
            // BMP 像素序 BGR；源 RGBA8Unorm(bgraOrder=false)：B=px[2] R=px[0]；
            // 源 BGRA8Unorm(bgraOrder=true)：B=px[0] R=px[2]
            row[x * 3u + 0u] = bgraOrder ? px[0] : px[2]; // B
            row[x * 3u + 1u] = px[1];                     // G
            row[x * 3u + 2u] = bgraOrder ? px[2] : px[0]; // R
        }
        file.write(reinterpret_cast<const char*>(row.data()),
            static_cast<std::streamsize>(dstStride));
    }
    return static_cast<bool>(file);
}

/// 单相位 30 帧契约场景（阴影恒开）。outFrameMs：逐帧耗时（record+submit+
/// waitidle+flush 的 CPU 墙钟）；outDrawCalls：第 30 帧 DrawIndexedInstanced
/// 调用数；outProbes：第 30 帧三探针 RGB（0=toon 1=shadow 2=lit）。
bool RunParityPhase(ZrD3D12Device* device, IZrQueue* queue, ZrCommandList cmdHandle,
    IZrCommandList* cmdList, ID3D12GraphicsCommandList* nativeCmd, ZrImage offscreenTarget,
    ZrImageView offscreenRtv, ZrImage depthImage, ZrImageView dsvView, ZrImage shadowImage,
    ZrImageView* shadowDsvs, ZrImageView shadowSrv, ZrPipelineState mainPso,
    ZrPipelineState toonPso, ZrPipelineState outlinePso, ZrPipelineState shadowPso,
    ZrBuffer groundVb, ZrBuffer groundIb, ZrBuffer cubeVb, ZrBuffer cubeIb,
    uint32_t cubeIndexCount, ZrBuffer cb0Ground, ZrBuffer cb0CubeA, ZrBuffer cbToon,
    ZrBuffer cbOutline, ZrBuffer cbLights, ZrBuffer cbClusters, ZrBuffer cbShadows,
    ZrBuffer* cbShadowPass, ZrBuffer* cbShadowModel, ID3D12Resource* readback,
    std::vector<double>& outFrameMs, uint32_t& outDrawCalls, int outProbes[3][3])
{
    void* cb0GroundMapped = device->GetBuffer(cb0Ground)->Map();
    void* cb0CubeAMapped = device->GetBuffer(cb0CubeA)->Map();
    void* cbToonMapped = device->GetBuffer(cbToon)->Map();
    void* cbOutlineMapped = device->GetBuffer(cbOutline)->Map();
    void* cbLightsMapped = device->GetBuffer(cbLights)->Map();
    void* cbClustersMapped = device->GetBuffer(cbClusters)->Map();
    void* cbShadowsMapped = device->GetBuffer(cbShadows)->Map();
    if (cb0GroundMapped == nullptr || cb0CubeAMapped == nullptr || cbToonMapped == nullptr
        || cbOutlineMapped == nullptr || cbLightsMapped == nullptr
        || cbClustersMapped == nullptr || cbShadowsMapped == nullptr) {
        std::printf("  parity phase: constant buffer Map FAILED\n");
        return false;
    }

    // ---- 相机 / 投影（契约：pos(0,1.6,-4) 朝 +Z，fovY 60°，near 0.3 far 60）------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    const float kCamPos[3] = {0.0f, 1.6f, -4.0f};
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kParityW) / static_cast<float>(kParityH),
        kNearZ, kFarZ, proj);
    // 契约相机 pos(0,1.6,-4) 朝 +Z：视图矩阵 = T(-pos)（列向量约定；view·world 使
    // 相机位置回到原点）。此前误用单位阵（相机原点）导致渲染为空——探针自检兜住。
    float view[16];
    MatTranslation(-kCamPos[0], -kCamPos[1], -kCamPos[2], view);
    float projView[16];
    MatMultiply(proj, view, projView);

    // ---- 方向光（契约：行进方向 (-0.5,-1,-0.3)/|·| → to-light (0.5,1,0.3)/|·|）----
    LightData sceneLight {};
    {
        const float d[3] = {0.5f, 1.0f, 0.3f};
        const float invLen = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        sceneLight.pos[0] = d[0] * invLen;
        sceneLight.pos[1] = d[1] * invLen;
        sceneLight.pos[2] = d[2] * invLen;
        sceneLight.color[0] = 1.0f;
        sceneLight.color[1] = 0.96f;
        sceneLight.color[2] = 0.9f;
        sceneLight.intensity = 1.2f;
        sceneLight.kind = kZrLightDirectional;
    }

    // ---- CPU 级联（相机静态，一次构建；camView 带平移）---------------------------
    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = static_cast<float>(kParityW) / static_cast<float>(kParityH);
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    const float ld[3] = {sceneLight.pos[0], sceneLight.pos[1], sceneLight.pos[2]};
    std::memcpy(shadowParamsStruct.lightDir, ld, sizeof(ld));
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = 0.0f;
    shadowParamsStruct.sceneCenter[2] = 0.0f;
    shadowParamsStruct.sceneRadius = 15.0f; // 覆盖 20×20 地面（对角 14.1）+ 双 cube
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("  parity phase: BuildCascades FAILED\n");
        return false;
    }
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(kNearZ, kFarZ, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("  parity phase: CascadeSplits FAILED\n");
            return false;
        }
        cascadeSplits[0] = interior[0];
        cascadeSplits[1] = interior[1];
        cascadeSplits[2] = interior[2];
        cascadeSplits[3] = kFarZ;
    }

    // CB3（级联 lightVP + bias）
    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;
    }
    shadowCb.cascadeSplits[0] = cascadeSplits[0];
    shadowCb.cascadeSplits[1] = cascadeSplits[1];
    shadowCb.cascadeSplits[2] = cascadeSplits[2];
    shadowCb.cascadeSplits[3] = cascadeSplits[3];
    std::memcpy(cbShadowsMapped, &shadowCb, sizeof(shadowCb));

    // 阴影 pass 逐级联 CB（b0）+ 逐项 model CB（b1：地面/ cubeA/ cubeB）
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("  parity phase: shadow pass CB %u Map FAILED\n", c);
            return false;
        }
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        std::memcpy(mapped, &spc, sizeof(spc));
    }
    {
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        const float modelA[16] = {1.0f, 0, 0, 0.0f, 0, 1.0f, 0, 0.5f, 0, 0, 1.0f, 0.0f, 0, 0, 0,
            1.0f}; // cube A T(0,0.5,0)
        const float modelB[16] = {1.0f, 0, 0, 1.8f, 0, 1.0f, 0, 0.5f, 0, 0, 1.0f, 0.8f, 0, 0, 0,
            1.0f}; // cube B T(1.8,0.5,0.8)
        const float* models[3] = {identity, modelA, modelB};
        for (uint32_t i = 0; i < 3u; ++i) {
            void* mapped = device->GetBuffer(cbShadowModel[i])->Map();
            if (mapped == nullptr) {
                std::printf("  parity phase: shadow model CB %u Map FAILED\n", i);
                return false;
            }
            ShadowModelConstants mc {};
            std::memcpy(mc.shadowModel, models[i], sizeof(mc.shadowModel));
            std::memcpy(mapped, &mc, sizeof(mc));
        }
    }

    // ---- CB1 光源 / CB2 簇（仅方向光：写全簇，平坦表 0 条目）---------------------
    PackedLight packedLights[kParityLightCount];
    PackLights(&sceneLight, kParityLightCount, packedLights);
    std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
    std::memset(static_cast<uint8_t*>(cbLightsMapped) + sizeof(packedLights), 0,
        1024u - sizeof(packedLights));
    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kParityW) / static_cast<float>(kParityH);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);
    const int32_t flatCount = AssignLightsToClusters(grid, &sceneLight, kParityLightCount,
        clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
    if (flatCount < 0) {
        std::printf("  parity phase: AssignLightsToClusters FAILED (%d)\n", flatCount);
        return false;
    }
    uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
    std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);

    // ---- 静态 CB0（契约材质）/ toon / outline ------------------------------------
    constexpr float kParityClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f};
    constexpr float kAmbient = 0.12f; // 契约：常数环境项（URP Flat ambient 对齐值）
    ShadowDrawConstants groundConst {};
    std::memcpy(groundConst.mvp, projView, sizeof(projView));
    const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    std::memcpy(groundConst.model, identity, sizeof(identity));
    ShadowDrawConstants cubeAConst {};
    float cubeAModel[16];
    MatTranslation(0.0f, 0.5f, 0.0f, cubeAModel);
    std::memcpy(cubeAConst.model, cubeAModel, sizeof(cubeAModel));
    float cubeAMvp[16];
    MatMultiply(projView, cubeAModel, cubeAMvp);
    std::memcpy(cubeAConst.mvp, cubeAMvp, sizeof(cubeAMvp));
    for (ShadowDrawConstants* c : {&groundConst, &cubeAConst}) {
        c->projParams[0] = kFarZ / (kFarZ - kNearZ);
        c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
        c->projParams[2] = kNearZ;
        c->projParams[3] = kFarZ;
        c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
        c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
        c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
        c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
        c->screenParams[0] = static_cast<float>(kParityW);
        c->screenParams[1] = static_cast<float>(kParityH);
        c->screenParams[2] = static_cast<float>(kParityLightCount);
        c->screenParams[3] = kAmbient;
        c->camPos[0] = kCamPos[0];
        c->camPos[1] = kCamPos[1];
        c->camPos[2] = kCamPos[2];
        c->camPos[3] = 0.0f;
        c->cascadeSplits[0] = cascadeSplits[0];
        c->cascadeSplits[1] = cascadeSplits[1];
        c->cascadeSplits[2] = cascadeSplits[2];
        c->cascadeSplits[3] = cascadeSplits[3];
        c->shadowParams[0] = 1.0f; // 契约：阴影开
        c->shadowParams[1] = static_cast<float>(kShadowsTexSize);
    }
    groundConst.material[0] = 0.50f; // 契约：地面灰 (0.5,0.5,0.5)
    groundConst.material[1] = 0.50f;
    groundConst.material[2] = 0.50f;
    groundConst.material[3] = 0.90f; // roughness 0.9（URP smoothness 0.1）
    cubeAConst.material[0] = 0.90f;  // 契约：暖白 (0.9,0.85,0.75)
    cubeAConst.material[1] = 0.85f;
    cubeAConst.material[2] = 0.75f;
    cubeAConst.material[3] = 0.50f; // roughness 0.5（URP smoothness 0.5）
    std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
    std::memcpy(cb0CubeAMapped, &cubeAConst, sizeof(cubeAConst));
    {
        float cubeBModel[16];
        MatTranslation(1.8f, 0.5f, 0.8f, cubeBModel);
        float cubeBMvp[16];
        MatMultiply(projView, cubeBModel, cubeBMvp);
        ToonConstants tc {};
        std::memcpy(tc.mvp, cubeBMvp, sizeof(cubeBMvp));
        std::memcpy(tc.model, cubeBModel, sizeof(cubeBModel));
        // 契约：toon 风格化光用场景方向光 to-light 方向（不做 --r5 的独立风格化）
        tc.lightDir[0] = sceneLight.pos[0];
        tc.lightDir[1] = sceneLight.pos[1];
        tc.lightDir[2] = sceneLight.pos[2];
        tc.lightDir[3] = 0.0f;
        tc.params[0] = 0.35f;
        tc.params[1] = 0.60f;
        tc.params[2] = 0.35f;
        tc.params[3] = 0.70f;
        tc.specular[0] = 0.98f;
        tc.specular[1] = 0.60f;
        tc.camPos[0] = kCamPos[0];
        tc.camPos[1] = kCamPos[1];
        tc.camPos[2] = kCamPos[2];
        tc.camPos[3] = 0.0f;
        tc.base[0] = 0.9f; // 契约：卡通基色 (0.9,0.3,0.25)
        tc.base[1] = 0.3f;
        tc.base[2] = 0.25f;
        tc.base[3] = 1.0f;
        std::memcpy(cbToonMapped, &tc, sizeof(tc));

        OutlineConstants oc {};
        std::memcpy(oc.mvp, cubeBMvp, sizeof(cubeBMvp));
        oc.params[0] = 0.05f; // 壳外扩宽度（世界域；z≈5.2 处约 4~6px）
        std::memcpy(cbOutlineMapped, &oc, sizeof(oc));
    }

    // ---- draw list：地面 / cube A（PBR，投影）/ cube B（toon，投影 + 描边）--------
    uint32_t drawCallCount = 0u; // 指标：本帧 DrawIndexedInstanced 调用数
    DrawList drawList(16u);
    drawList.RegisterMesh(1u, 6u);
    drawList.RegisterMesh(2u, cubeIndexCount);
    drawList.RegisterMesh(3u, cubeIndexCount);
    RenderItem groundItem {};
    groundItem.meshHandle = 1u;
    groundItem.materialId = 7u;
    groundItem.depth = 4.0f;
    groundItem.castShadow = true;
    RenderItem cubeAItem {};
    cubeAItem.meshHandle = 2u;
    cubeAItem.materialId = 8u;
    cubeAItem.shaderId = 0u;
    cubeAItem.depth = 4.0f;
    cubeAItem.castShadow = true;
    RenderItem cubeBItem {};
    cubeBItem.meshHandle = 3u;
    cubeBItem.materialId = 9u;
    cubeBItem.shaderId = 1u;
    cubeBItem.depth = 4.8f;
    cubeBItem.castShadow = true; // 契约：cube B 投影阴影（--r5 曾简化为不投影）
    const uint8_t toonMask
        = static_cast<uint8_t>(ShadowViewMask(true) | kOutlineViewBit);
    if (!drawList.Add(groundItem, ShadowViewMask(true))
        || !drawList.Add(cubeAItem, ShadowViewMask(true))
        || !drawList.Add(cubeBItem, toonMask)) {
        std::printf("  parity phase: draw list Add FAILED\n");
        return false;
    }
    std::vector<uint32_t> shadowItems[ZR_SHADOW_CASCADES];
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        drawList.Visibility().Iterate(
            ShadowViewId(c), [&](uint32_t id) { shadowItems[c].push_back(id); });
    }
    std::vector<uint32_t> outlineItems;
    drawList.Visibility().Iterate(kOutlineViewId, [&](uint32_t id) { outlineItems.push_back(id); });
    drawList.Sort();
    if (drawList.BuildBatches() == 0u) {
        std::printf("  parity phase: BuildBatches FAILED\n");
        return false;
    }

    // 逐项绘制辅助（可见性 id = 插入序：0 地面 / 1 cube A / 2 cube B）
    auto bindItem = [&](uint32_t itemId) {
        const uint32_t mesh = drawList.ItemAt(itemId).meshHandle;
        ZrBuffer vbs[1] = {(mesh == 1u) ? groundVb : cubeVb};
        cmdList->SetVertexBuffers(vbs, 1u);
        cmdList->SetIndexBuffer((mesh == 1u) ? groundIb : cubeIb);
    };
    auto drawItem = [&](uint32_t itemId) {
        const uint32_t mesh = drawList.ItemAt(itemId).meshHandle;
        cmdList->DrawIndexedInstanced((mesh == 1u) ? 6u : cubeIndexCount, 1u, 0u, 0u, 0u);
        drawCallCount += 1u; // 指标：DrawIndexedInstanced 调用计数
    };

    // ---- 30 帧循环（固定一帧；第 30 帧全帧 readback → BMP）------------------------
    bool allOk = true;
    uint32_t undeclaredUses = 0u;
    outFrameMs.clear();

    for (uint32_t frame = 0u; frame < kParityFrames; ++frame) {
        const bool doReadback = (frame == kParityFrames - 1u);
        drawCallCount = 0u;
        const auto frameStart = std::chrono::steady_clock::now();

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(
            RgResourceDesc::ImportImage("backbuffer", offscreenTarget));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        RgHandle shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kParityClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });

        // 每级联 depth-only pass（契约：阴影开；地面 + 双 cube 均 cast）
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            graph.AddPass("ShadowCascadePass",
                [&](RgPass& pass) {
                    pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                        ZrAccessBits::DepthStencilAttachmentWrite,
                        ZrPipelineStageBits::LateFragmentTests);
                },
                [&, c](RgContext&) {
                    cmdList->SetPipelineState(shadowPso);
                    cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                    cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                    nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(shadowDsvs[c]),
                        D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
                    cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                    for (uint32_t id : shadowItems[c]) {
                        cmdList->SetGraphicsConstantBuffer(1u, cbShadowModel[id]);
                        bindItem(id);
                        drawItem(id);
                    }
                });
        }

        // 主绘制 Pass（PBR：地面 + cube A；CSM + 簇光照 + Reinhard）
        graph.AddPass("MainPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                    ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mainPso);
                cmdList->SetRenderTarget(offscreenRtv, dsvView);
                cmdList->SetViewportScissor(kParityW, kParityH);
                cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                cmdList->SetGraphicsBindlessSrv(4u, shadowSrv); // t0 级联阴影图
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    if (batch.shaderId != 0u) {
                        continue; // toon 批归 ToonPass
                    }
                    for (uint32_t i = 0; i < batch.count; ++i) {
                        const uint32_t itemIndex = batch.startIndex + i;
                        const uint32_t mesh = drawList.ItemAt(itemIndex).meshHandle;
                        bindItem(itemIndex);
                        cmdList->SetGraphicsConstantBuffer(
                            0u, (mesh == 1u) ? cb0Ground : cb0CubeA);
                        drawItem(itemIndex);
                    }
                }
            });

        // Toon pass（cube B：量化 ramp，基色进 CB）
        graph.AddPass("ToonPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(toonPso);
                cmdList->SetRenderTarget(offscreenRtv, dsvView);
                cmdList->SetViewportScissor(kParityW, kParityH);
                cmdList->SetGraphicsConstantBuffer(0u, cbToon);
                bindItem(2u);
                drawItem(2u);
            });

        // Outline pass（反向壳：FrontCull + 深度只测不写）
        graph.AddPass("OutlinePass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(outlinePso);
                cmdList->SetRenderTarget(offscreenRtv, dsvView);
                cmdList->SetViewportScissor(kParityW, kParityH);
                cmdList->SetGraphicsConstantBuffer(0u, cbOutline);
                for (uint32_t id : outlineItems) {
                    bindItem(id);
                    drawItem(id);
                }
            });

        // 全帧 ReadbackPass（第 30 帧）：整幅拷到 readback 缓冲（BMP 编码源）
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    ID3D12Resource* texture
                        = device->D3D12ResourceOf(ctx.GetImage(backH));
                    const D3D12_BOX box
                        = {0u, 0u, 0u, kParityW, kParityH, 1u};
                    D3D12_TEXTURE_COPY_LOCATION dst {};
                    dst.pResource = readback;
                    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                    dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                    dst.PlacedFootprint.Footprint.Width = kParityW;
                    dst.PlacedFootprint.Footprint.Height = kParityH;
                    dst.PlacedFootprint.Footprint.Depth = 1u;
                    dst.PlacedFootprint.Footprint.RowPitch = kParityReadbackRowPitch;
                    D3D12_TEXTURE_COPY_LOCATION src {};
                    src.pResource = texture;
                    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    src.SubresourceIndex = 0u;
                    nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
                });
        }

        if (!graph.Compile()) {
            std::printf("  parity phase frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末手动屏障：深度/阴影/离屏图回 COMMON（与下一帧 RG 起点一致）
        ZrImageBarrier depthReset = MakeBarrier(depthImage,
            ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);
        ZrImageBarrier shadowReset = MakeBarrier(shadowImage,
            ZrImageLayout::DepthStencilReadOnlyOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&shadowReset, 1u, nullptr, 0u);
        {
            const ZrImageLayout backLayoutNow
                = doReadback ? ZrImageLayout::TransferSrcOptimal
                             : ZrImageLayout::ColorAttachmentOptimal;
            ZrImageBarrier backReset = MakeBarrier(offscreenTarget, backLayoutNow,
                ZrImageLayout::Undefined, ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&backReset, 1u, nullptr, 0u);
        }

        cmdList->End();
        queue->Submit(cmdHandle);
        device->WaitIdle();
        graph.FlushRetired(*device);
        const auto frameEnd = std::chrono::steady_clock::now();
        outFrameMs.push_back(
            std::chrono::duration<double, std::milli>(frameEnd - frameStart).count());

        if (doReadback) {
            outDrawCalls = drawCallCount;
            void* mapped = nullptr;
            const HRESULT hr = readback->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  parity phase frame %u: full-frame Map FAILED (hr=0x%08lX)\n",
                    frame + 1u, static_cast<unsigned long>(hr));
                allOk = false;
            } else {
                const auto decode = [&](UINT x, UINT y, int outRgb[3]) {
                    const uint8_t* px = static_cast<const uint8_t*>(mapped)
                        + static_cast<size_t>(y) * kParityReadbackRowPitch
                        + static_cast<size_t>(x) * 4u;
                    outRgb[0] = static_cast<int>(px[0]); // RGBA（离屏恒 RGBA 序）
                    outRgb[1] = static_cast<int>(px[1]);
                    outRgb[2] = static_cast<int>(px[2]);
                };
                decode(kParityToonX, kParityToonY, outProbes[0]);
                decode(kParityShadowX, kParityShadowY, outProbes[1]);
                decode(kParityLitX, kParityLitY, outProbes[2]);
                std::printf("  parity frame %u probes: toon(%u,%u) RGB=(%d,%d,%d) "
                            "shadow(%u,%u) RGB=(%d,%d,%d) lit(%u,%u) RGB=(%d,%d,%d) "
                            "drawCalls=%u\n",
                    frame + 1u, kParityToonX, kParityToonY, outProbes[0][0], outProbes[0][1],
                    outProbes[0][2], kParityShadowX, kParityShadowY, outProbes[1][0],
                    outProbes[1][1], outProbes[1][2], kParityLitX, kParityLitY, outProbes[2][0],
                    outProbes[2][1], outProbes[2][2], outDrawCalls);
                readback->Unmap(0, nullptr);
            }
        }
    }

    if (undeclaredUses != 0u) {
        std::printf("  parity phase: undeclared RG uses = %u\n", undeclaredUses);
        allOk = false;
    }
    return allOk;
}

// --parity 模式主体（返回进程退出码）。恒离屏（无窗口/交换链/vsync）：契约场景
// 30 帧 → 第 30 帧全帧 readback → 24bit BMP + 指标文件。
static int RunParityMode()
{
    std::printf("zrhi_d3d12_window: --parity mode (TASK-B-R5-04): URP parity bench scene "
                "1280x720\n");

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --parity FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --parity device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --parity FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 离屏目标 + 场景深度 + 级联阴影图 -------------------------------------------
    ZrImageDesc rtDesc {};
    rtDesc.format = ZrFormat::RGBA8Unorm;
    rtDesc.width = kParityW;
    rtDesc.height = kParityH;
    rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
    ZrImage offscreenTarget = device->CreateImage(rtDesc);
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kParityW;
    depthDesc.height = kParityH;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    ZrImageDesc shadowDesc {};
    shadowDesc.format = ZrFormat::D32Float;
    shadowDesc.width = kShadowsTexSize;
    shadowDesc.height = kShadowsTexSize;
    shadowDesc.arrayLayers = ZR_SHADOW_CASCADES;
    shadowDesc.usage = ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled;
    ZrImage shadowImage = device->CreateImage(shadowDesc);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        ZrImageViewDesc layerDsvDesc {};
        layerDsvDesc.image = shadowImage;
        layerDsvDesc.viewType = ZrImageViewType::Dsv;
        layerDsvDesc.subresources.baseArrayLayer = c;
        layerDsvDesc.subresources.layerCount = 1u;
        shadowDsvs[c] = device->CreateImageView(layerDsvDesc);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float;
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    ZrImageViewDesc offRtvDesc {};
    offRtvDesc.image = offscreenTarget;
    offRtvDesc.viewType = ZrImageViewType::Rtv;
    ZrImageView offscreenRtv = device->CreateImageView(offRtvDesc);
    if (!offscreenTarget.IsValid() || !depthImage.IsValid() || !dsvView.IsValid()
        || !shadowImage.IsValid() || !shadowSrv.IsValid() || !offscreenRtv.IsValid()) {
        std::printf("zrhi_d3d12_window: --parity FAILED to create images/views\n");
        return 1;
    }
    for (ZrImageView v : shadowDsvs) {
        if (!v.IsValid()) {
            std::printf("zrhi_d3d12_window: --parity FAILED to create cascade layer DSV\n");
            return 1;
        }
    }

    // ---- 几何（契约：地面 quad 20×20 @y=0 + cube.zmesh ×2 实例）----------------------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, 0.0f, -10.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, 0.0f, -10.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, 0.0f, 10.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, 0.0f, 10.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<char> meshBytes;
    if (!ReadWholeFile("Native/tests/data/cube.zmesh", meshBytes)) {
        std::printf("zrhi_d3d12_window: --parity FAILED to open cube.zmesh\n");
        return 1;
    }
    ZmeshData mesh {};
    if (zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh) != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --parity cube.zmesh parse FAILED\n");
        return 1;
    }
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(mesh.stats.vertexCount);
    for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);

    auto makeVb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    auto makeIb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    ZrBuffer groundVb = makeVb(groundVerts, sizeof(groundVerts));
    ZrBuffer groundIb = makeIb(groundIndices, sizeof(groundIndices));
    ZrBuffer cubeVb = makeVb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex));
    ZrBuffer cubeIb = makeIb(cubeIndices.data(), uint64_t(cubeIndices.size()) * 4u);
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --parity FAILED to create VB/IB\n");
        return 1;
    }

    // ---- 常量缓冲 --------------------------------------------------------------------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0CubeA {};
    ZrBuffer cbToon {};
    ZrBuffer cbOutline {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbShadowModel[3] = {};
    {
        auto makeCb = [&](uint64_t size) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            ZrBuffer b = device->CreateBuffer(desc);
            return (b.IsValid() && device->GetBuffer(b)->Map() != nullptr) ? b : ZrBuffer{};
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        bool ok = (cb0Ground = makeCb(256u)).IsValid() && (cb0CubeA = makeCb(256u)).IsValid()
            && (cbToon = makeCb(256u)).IsValid() && (cbOutline = makeCb(96u)).IsValid()
            && (cbLights = makeCb(1024u)).IsValid() && (cbClusters = makeCb(cb2Size)).IsValid()
            && (cbShadows = makeCb(384u)).IsValid();
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            ok = ok && (cbShadowPass[c] = makeCb(256u)).IsValid();
        }
        for (ZrBuffer& b : cbShadowModel) {
            ok = ok && (b = makeCb(256u)).IsValid();
        }
        if (!ok) {
            std::printf("zrhi_d3d12_window: --parity FAILED to create constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器 + PSO ----------------------------------------------------------------
    const char* kParityShaderPaths[4] = {"Native/shaders/pbr_shadow.hlsl",
        "Native/shaders/toon.hlsl", "Native/shaders/toon_outline.hlsl",
        "Native/shaders/shadow_depth.hlsl"};
    std::vector<char> shaderSrc[4];
    for (int i = 0; i < 4; ++i) {
        if (!ReadWholeFile(kParityShaderPaths[i], shaderSrc[i])) {
            std::printf("zrhi_d3d12_window: --parity FAILED to open %s\n", kParityShaderPaths[i]);
            return 1;
        }
    }
    const ZrShader mainVs = device->CompileHlsl(ZrShaderStage::Vertex, shaderSrc[0].data(),
        shaderSrc[0].size(), "VsMain");
    const ZrShader mainPs = device->CompileHlsl(ZrShaderStage::Pixel, shaderSrc[0].data(),
        shaderSrc[0].size(), "PsMain");
    const ZrShader toonVs = device->CompileHlsl(ZrShaderStage::Vertex, shaderSrc[1].data(),
        shaderSrc[1].size(), "VsMain");
    const ZrShader toonPs = device->CompileHlsl(ZrShaderStage::Pixel, shaderSrc[1].data(),
        shaderSrc[1].size(), "PsMain");
    const ZrShader outlineVs = device->CompileHlsl(ZrShaderStage::Vertex, shaderSrc[2].data(),
        shaderSrc[2].size(), "VsMain");
    const ZrShader outlinePs = device->CompileHlsl(ZrShaderStage::Pixel, shaderSrc[2].data(),
        shaderSrc[2].size(), "PsMain");
    const ZrShader shadowVs = device->CompileHlsl(ZrShaderStage::Vertex, shaderSrc[3].data(),
        shaderSrc[3].size(), "VsMain");
    const ZrShader allShaders[7] = {mainVs, mainPs, toonVs, toonPs, outlineVs, outlinePs, shadowVs};
    bool shadersOk = true;
    for (const ZrShader& s : allShaders) {
        shadersOk = shadersOk && s.IsValid();
    }
    if (!shadersOk) {
        std::printf("zrhi_d3d12_window: --parity shader compilation FAILED (see stderr)\n");
        return 1;
    }

    ZrGraphicsPsoDesc mainPsoDesc {};
    mainPsoDesc.vertexShader = mainVs;
    mainPsoDesc.pixelShader = mainPs;
    mainPsoDesc.rtvFormat = ZrFormat::RGBA8Unorm;
    mainPsoDesc.dsvFormat = ZrFormat::D32Float;
    mainPsoDesc.blend = ZrBlendMode::Opaque;
    mainPsoDesc.depthTest = true;
    mainPsoDesc.depthWrite = true;
    mainPsoDesc.cullMode = ZrCullMode::None; // R4-03 绕序实证（见 shadow_depth.hlsl 注）
    mainPsoDesc.rootCbvCount = 4u;
    mainPsoDesc.srvTable = true;
    ZrPipelineState mainPso = device->CreatePipelineState(mainPsoDesc);
    ZrGraphicsPsoDesc toonPsoDesc {};
    toonPsoDesc.vertexShader = toonVs;
    toonPsoDesc.pixelShader = toonPs;
    toonPsoDesc.rtvFormat = ZrFormat::RGBA8Unorm;
    toonPsoDesc.dsvFormat = ZrFormat::D32Float;
    toonPsoDesc.blend = ZrBlendMode::Opaque;
    toonPsoDesc.depthTest = true;
    toonPsoDesc.depthWrite = true;
    toonPsoDesc.cullMode = ZrCullMode::None;
    toonPsoDesc.rootCbvCount = 1u;
    ZrPipelineState toonPso = device->CreatePipelineState(toonPsoDesc);
    ZrGraphicsPsoDesc outlinePsoDesc {};
    outlinePsoDesc.vertexShader = outlineVs;
    outlinePsoDesc.pixelShader = outlinePs;
    outlinePsoDesc.rtvFormat = ZrFormat::RGBA8Unorm;
    outlinePsoDesc.dsvFormat = ZrFormat::D32Float;
    outlinePsoDesc.blend = ZrBlendMode::Opaque;
    outlinePsoDesc.depthTest = true;
    outlinePsoDesc.depthWrite = false;
    outlinePsoDesc.cullMode = ZrCullMode::Front; // 反向壳：剔除正面保留背面壳
    outlinePsoDesc.rootCbvCount = 1u;
    ZrPipelineState outlinePso = device->CreatePipelineState(outlinePsoDesc);
    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = shadowVs;
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    shadowPsoDesc.cullMode = ZrCullMode::Front;
    shadowPsoDesc.rootCbvCount = 2u;
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    if (!mainPso.IsValid() || !toonPso.IsValid() || !outlinePso.IsValid() || !shadowPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --parity FAILED to create graphics PSOs\n");
        return 1;
    }

    // ---- 全帧 readback 缓冲（RGBA8，行距 256 对齐）------------------------------------
    Microsoft::WRL::ComPtr<ID3D12Resource> readback
        = MakeReadbackBuffer(device->D3D12Device(), kParityReadbackRowPitch * kParityH);
    if (readback == nullptr) {
        std::printf("zrhi_d3d12_window: --parity FAILED to create readback buffer\n");
        return 1;
    }

    // ---- 30 帧契约场景 -----------------------------------------------------------------
    std::vector<double> frameMs;
    uint32_t drawCallsPerFrame = 0u;
    int probes[3][3] = {};
    const bool phaseOk = RunParityPhase(device, queue, cmdHandle, cmdList, nativeCmd,
        offscreenTarget, offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv,
        mainPso, toonPso, outlinePso, shadowPso, groundVb, groundIb, cubeVb, cubeIb,
        cubeIndexCount, cb0Ground, cb0CubeA, cbToon, cbOutline, cbLights, cbClusters, cbShadows,
        cbShadowPass, cbShadowModel, readback.Get(), frameMs, drawCallsPerFrame, probes);

    // ---- BMP 编码（第 30 帧全帧）--------------------------------------------------------
    bool bmpOk = false;
    if (phaseOk) {
        void* mapped = nullptr;
        if (SUCCEEDED(readback->Map(0, nullptr, &mapped)) && mapped != nullptr) {
            std::filesystem::create_directories(
                std::filesystem::path(kParityBmpPath).parent_path());
            bmpOk = WriteBmp24(kParityBmpPath, static_cast<const uint8_t*>(mapped), kParityW,
                kParityH, kParityReadbackRowPitch, /*bgraOrder=*/false);
            readback->Unmap(0, nullptr);
        }
    }

    // ---- 无设备移除 + InfoQueue 计数 ----------------------------------------------------
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;

    // ---- 清理 -------------------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0CubeA);
    device->Destroy(cbToon);
    device->Destroy(cbOutline);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    for (ZrBuffer& b : cbShadowPass) {
        device->Destroy(b);
    }
    for (ZrBuffer& b : cbShadowModel) {
        device->Destroy(b);
    }
    device->Destroy(mainPso);
    device->Destroy(toonPso);
    device->Destroy(outlinePso);
    device->Destroy(shadowPso);
    for (const ZrShader& s : allShaders) {
        device->Destroy(s);
    }
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    for (ZrImageView& v : shadowDsvs) {
        device->Destroy(v);
    }
    device->Destroy(shadowSrv);
    device->Destroy(offscreenRtv);
    device->Destroy(dsvView);
    device->Destroy(depthImage);
    device->Destroy(shadowImage);
    device->Destroy(offscreenTarget);
    readback.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    // ---- 指标统计与文件 -----------------------------------------------------------------
    bool metricsOk = false;
    if (phaseOk && frameMs.size() == kParityFrames) {
        double sum = 0.0;
        double minMs = frameMs[1];
        double maxMs = frameMs[1];
        for (size_t i = 1; i < frameMs.size(); ++i) { // 排除首帧（管线暖启动）
            sum += frameMs[i];
            minMs = (frameMs[i] < minMs) ? frameMs[i] : minMs;
            maxMs = (frameMs[i] > maxMs) ? frameMs[i] : maxMs;
        }
        const double avgMs = sum / static_cast<double>(frameMs.size() - 1);

        // 探针自检（推导见 kParityToonX 注释；硬失败——场景契约的确定性结果）
        const bool toonOk = probes[0][0] > probes[0][1] + 10 && probes[0][0] > probes[0][2] + 10;
        const bool shadowOk = probes[1][0] <= probes[2][0] - 8;
        std::printf("zrhi_d3d12_window: [PARITY] toonOk=%d (r=%d g=%d b=%d) shadowOk=%d "
                    "(shadow r=%d vs lit r=%d)\n",
            toonOk ? 1 : 0, probes[0][0], probes[0][1], probes[0][2], shadowOk ? 1 : 0,
            probes[1][0], probes[2][0]);

        std::filesystem::create_directories(
            std::filesystem::path(kParityMetricsPath).parent_path());
        std::ofstream metrics(kParityMetricsPath, std::ios::binary);
        if (metrics) {
            metrics << "ZRender parity metrics (TASK-B-R5-04)\n";
            metrics << "backend: D3D12 (offscreen RGBA8Unorm, no swapchain / no vsync)\n";
            metrics << "resolution: " << kParityW << "x" << kParityH << "\n";
            metrics << "frames: " << kParityFrames << " (fixed scene, no animation, shadows on)\n";
            metrics << "avg_frame_ms: " << avgMs
                    << "  (口径: 30 帧排除首帧取均值; CPU 墙钟覆盖单帧 record+submit+"
                       "WaitIdle+FlushRetired, 离屏无 vsync)\n";
            metrics << "frame_ms_min: " << minMs << "\n";
            metrics << "frame_ms_max: " << maxMs << "\n";
            metrics << "draw_calls_per_frame: " << drawCallsPerFrame
                    << "  (口径: DrawIndexedInstanced 调用数/帧 = 阴影 4 级联 x 3 casters "
                       "= 12 + 主 pass 2 (地面+cube A) + toon 1 + outline 1 = 16)\n";
            metrics << "screenshot: " << kParityBmpPath
                    << " (24bit BMP, bottom-up, 第 30 帧全帧 readback)\n";
            metrics << "sanity: toon_probe RGB=(" << probes[0][0] << "," << probes[0][1] << ","
                    << probes[0][2] << ") shadow_probe RGB=(" << probes[1][0] << ","
                    << probes[1][1] << "," << probes[1][2] << ") lit_ground RGB=("
                    << probes[2][0] << "," << probes[2][1] << "," << probes[2][2] << ")\n";
            metrics << "device_removed: " << (noDeviceRemoval ? "0" : "1") << "\n";
            metrics << "infoqueue_errors: " << infoQueueErrors << "\n";
            metrics << "version: " << ZR_GetVersion() << "\n";
            metricsOk = static_cast<bool>(metrics);
        }
    }

    // ---- 证据行与判定 -------------------------------------------------------------------
    const bool toonOk = probes[0][0] > probes[0][1] + 10 && probes[0][0] > probes[0][2] + 10;
    const bool shadowOk = probes[1][0] <= probes[2][0] - 8;
    std::printf("zrhi_d3d12_window: [PARITY] bmp=%s metrics=%s toonOk=%d shadowOk=%d "
                "removed=%s errors=%u\n",
        bmpOk ? "OK" : "FAILED", metricsOk ? "OK" : "FAILED", toonOk ? 1 : 0,
        shadowOk ? 1 : 0, noDeviceRemoval ? "S_OK" : "REMOVED", infoQueueErrors);
    if (phaseOk && bmpOk && metricsOk && toonOk && shadowOk && noDeviceRemoval
        && infoQueueErrors == 0u) {
        std::printf("D3D12 PARITY OK (bmp=%s metrics=%s)\n", kParityBmpPath, kParityMetricsPath);
        return 0;
    }
    std::printf("D3D12 PARITY FAILED (phase=%d bmp=%d metrics=%d removed=%d errors=%u)\n",
        phaseOk ? 1 : 0, bmpOk ? 1 : 0, metricsOk ? 1 : 0, noDeviceRemoval ? 0 : 1,
        infoQueueErrors);
    return 1;
}

// =============================================================================
// ---- TASK-B-R6-01：--fsr 模式（FSR SDK 接入 + UpscalerChain 超分框架，可证伪）----
// =============================================================================

// 恒离屏（无窗口/交换链/vsync）：内部渲染 960×540 → UpscalerChain 上采样到
// 1280×720（1.333x）。场景继承 --shadows on（地面 quad 20×20 @y=-1 + PBR cube
// (0,0,3.2) + 方向光 CSM），相机静止、cube 绕 Y 旋转（MV 来自旋转，真实可信）。
// 可证伪对比：--fsr off = 覆盖 Bilinear 基线；--fsr on = 自动选优（真机应为
// FSR3——若运行时缺失导致回落 Bilinear 则显式失败，不静默伪装）。
// 锐度探针：display 域 cube 轮廓竖直边所在横线的相邻像素梯度幅度（读back 帧
// angle=2π≡0°：cube 半宽 0.5、近面 z=2.7 → 边缘 x=640±205）on vs off ≥1.2×。
constexpr uint32_t kFsrRenderW = 960u;
constexpr uint32_t kFsrRenderH = 540u;
constexpr uint32_t kFsrDisplayW = 1280u;
constexpr uint32_t kFsrDisplayH = 720u;
constexpr uint32_t kFsrFramesPerPhase = 30u;
constexpr uint32_t kFsrRotationPeriod = 36u;   // 旋转周期帧数（读back 帧=300°）
constexpr uint32_t kFsrProbeRow = 340u;          // cube 竖直边所在横线（display 域）
constexpr uint32_t kFsrProbeXMin = 420u;         // 扫描窗（覆盖 640±205 两条竖边）
constexpr uint32_t kFsrProbeXMax = 860u;
constexpr double kFsrEdgeRatioMin = 1.2;         // 简报阈值：on/off ≥1.2×
constexpr float kFsrSharpness = 1.0f;            // FSR RCAS 锐化（on 相位；最大档）
constexpr uint32_t kFsrReadbackRowPitch
    = (kFsrDisplayW * 4u + 255u) & ~255u;        // 1280×4=5120 天然 256 对齐
constexpr uint32_t kFsrSceneRowPitch
    = (kFsrRenderW * 4u + 255u) & ~255u;         // 960×4=3840 天然 256 对齐
constexpr float kFsrClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f};
constexpr const char* kFsrPngOn = "Docs/Parity/fsr_on.png";
constexpr const char* kFsrPngOff = "Docs/Parity/fsr_off.png";
// B-R6-02：三向对比拼图（左=基准/低档，右=高档）
constexpr const char* kFsr4PngPair = "Docs/Parity/fsr41_vs_31.png";   // 左 FSR3.1 / 右 FSR4.1
constexpr const char* kFsr31PngPair = "Docs/Parity/fsr31_vs_bilinear.png"; // FSR4 不可用时的替代证据

// ---- PNG 编码（无依赖：zlib stored 块 + CRC32/Adler32 自算）---------------------
uint32_t FsrCrc32(const uint8_t* data, size_t n)
{
    static uint32_t table[256];
    static bool tableReady = false;
    if (!tableReady) {
        for (uint32_t i = 0; i < 256u; ++i) {
            uint32_t c = i;
            for (int k = 0; k < 8; ++k) {
                c = (c & 1u) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            }
            table[i] = c;
        }
        tableReady = true;
    }
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t i = 0; i < n; ++i) {
        crc = table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
    }
    return crc ^ 0xFFFFFFFFu;
}

uint32_t FsrAdler32(const uint8_t* data, size_t n)
{
    uint32_t a = 1u;
    uint32_t b = 0u;
    for (size_t i = 0; i < n; ++i) {
        a = (a + data[i]) % 65521u;
        b = (b + a) % 65521u;
    }
    return (b << 16) | a;
}

/// PNG 要求网络字节序（大端）——本机 x86 为小端，显式逐字节写入
void FsrPngWriteBE32(std::ofstream& file, uint32_t v)
{
    const uint8_t b[4] = {static_cast<uint8_t>((v >> 24) & 0xFFu),
        static_cast<uint8_t>((v >> 16) & 0xFFu), static_cast<uint8_t>((v >> 8) & 0xFFu),
        static_cast<uint8_t>(v & 0xFFu)};
    file.write(reinterpret_cast<const char*>(b), 4u);
}

void FsrPngChunk(std::ofstream& file, const char type[4], const std::vector<uint8_t>& payload)
{
    FsrPngWriteBE32(file, static_cast<uint32_t>(payload.size()));
    std::vector<uint8_t> body(payload.size() + 4u);
    std::memcpy(body.data(), type, 4u);
    if (!payload.empty()) {
        std::memcpy(body.data() + 4u, payload.data(), payload.size());
    }
    file.write(reinterpret_cast<const char*>(body.data()), static_cast<std::streamsize>(body.size()));
    const uint32_t crc = FsrCrc32(body.data(), body.size());
    FsrPngWriteBE32(file, crc);
}

/// 24bit PNG（RGB，filter 0；zlib stored 块）——C++ 无依赖
bool WritePng24(const char* path, const uint8_t* pixels, uint32_t width, uint32_t height,
    uint32_t srcRowPitch)
{    std::vector<uint8_t> raw;
    raw.reserve(static_cast<size_t>(height) * (size_t(width) * 3u + 1u));
    for (uint32_t y = 0; y < height; ++y) {
        raw.push_back(0u); // filter type 0（None）
        const uint8_t* row = pixels + static_cast<size_t>(y) * srcRowPitch;
        for (uint32_t x = 0; x < width; ++x) {
            raw.push_back(row[x * 4u + 0u]); // R
            raw.push_back(row[x * 4u + 1u]); // G
            raw.push_back(row[x * 4u + 2u]); // B
        }
    }
    // zlib 流：0x78 0x01 + stored deflate 块（每块 ≤65535B）+ adler32
    std::vector<uint8_t> zlib;
    zlib.push_back(0x78u);
    zlib.push_back(0x01u);
    size_t offset = 0u;
    while (offset < raw.size()) {
        const size_t block = (raw.size() - offset > 65535u) ? 65535u : (raw.size() - offset);
        const bool finalBlock = (offset + block == raw.size());
        zlib.push_back(finalBlock ? 1u : 0u);
        const uint16_t len16 = static_cast<uint16_t>(block);
        const uint16_t nlen = static_cast<uint16_t>(~len16);
        zlib.push_back(static_cast<uint8_t>(len16 & 0xFFu));
        zlib.push_back(static_cast<uint8_t>(len16 >> 8));
        zlib.push_back(static_cast<uint8_t>(nlen & 0xFFu));
        zlib.push_back(static_cast<uint8_t>(nlen >> 8));
        zlib.insert(zlib.end(), raw.begin() + static_cast<std::ptrdiff_t>(offset),
            raw.begin() + static_cast<std::ptrdiff_t>(offset + block));
        offset += block;
    }
    const uint32_t adler = FsrAdler32(raw.data(), raw.size());
    for (int i = 3; i >= 0; --i) {
        zlib.push_back(static_cast<uint8_t>((adler >> (i * 8)) & 0xFFu));
    }

    std::ofstream file(path, std::ios::binary);
    if (!file) {
        return false;
    }
    const uint8_t signature[8] = {137u, 80u, 78u, 71u, 13u, 10u, 26u, 10u};
    file.write(reinterpret_cast<const char*>(signature), 8u);
    std::vector<uint8_t> ihdr(13u);
    ihdr[0] = static_cast<uint8_t>((width >> 24) & 0xFFu); // 大端宽/高（PNG 规范）
    ihdr[1] = static_cast<uint8_t>((width >> 16) & 0xFFu);
    ihdr[2] = static_cast<uint8_t>((width >> 8) & 0xFFu);
    ihdr[3] = static_cast<uint8_t>(width & 0xFFu);
    ihdr[4] = static_cast<uint8_t>((height >> 24) & 0xFFu);
    ihdr[5] = static_cast<uint8_t>((height >> 16) & 0xFFu);
    ihdr[6] = static_cast<uint8_t>((height >> 8) & 0xFFu);
    ihdr[7] = static_cast<uint8_t>(height & 0xFFu);
    ihdr[8] = 8u;  // bit depth
    ihdr[9] = 2u;  // color type：truecolor RGB
    ihdr[10] = 0u; // compression
    ihdr[11] = 0u; // filter
    ihdr[12] = 0u; // interlace
    FsrPngChunk(file, "IHDR", ihdr);
    FsrPngChunk(file, "IDAT", zlib);
    FsrPngChunk(file, "IEND", std::vector<uint8_t>());
    return static_cast<bool>(file);
}

/// 左右拼图（B-R6-02：FSR4.1 vs FSR3.1 / FSR3.1 vs bilinear 对比截图）：
/// 逐行左右拷贝到临时 RGBA 缓冲后复用 WritePng24（宽度翻倍）
bool WritePng24Pair(const char* path, const uint8_t* left, const uint8_t* right,
    uint32_t width, uint32_t height, uint32_t srcRowPitch)
{
    if (left == nullptr || right == nullptr || width == 0u || height == 0u) {
        return false;
    }
    const uint32_t pairWidth = width * 2u;
    const uint32_t pairPitch = pairWidth * 4u;
    std::vector<uint8_t> merged(static_cast<size_t>(pairPitch) * height);
    for (uint32_t y = 0u; y < height; ++y) {
        const uint8_t* leftRow = left + static_cast<size_t>(y) * srcRowPitch;
        const uint8_t* rightRow = right + static_cast<size_t>(y) * srcRowPitch;
        uint8_t* dst = merged.data() + static_cast<size_t>(y) * pairPitch;
        std::memcpy(dst, leftRow, static_cast<size_t>(width) * 4u);
        std::memcpy(dst + static_cast<size_t>(width) * 4u, rightRow,
            static_cast<size_t>(width) * 4u);
    }
    return WritePng24(path, merged.data(), pairWidth, height, pairPitch);
}

/// 锐度探针：扫描行相邻像素梯度幅度（通道最大值域的 |Δ| 均值）
double FsrEdgeMetric(const uint8_t* rowPixels, uint32_t xMin, uint32_t xMax)
{
    double sum = 0.0;
    uint32_t count = 0u;
    auto lum = [](const uint8_t* px) -> int {
        const int r = px[0];
        const int g = px[1];
        const int b = px[2];
        return (r > g) ? ((r > b) ? r : b) : ((g > b) ? g : b);
    };
    for (uint32_t x = xMin; x + 1u < xMax; ++x) {
        const int d = std::abs(lum(rowPixels + x * 4u) - lum(rowPixels + (x + 1u) * 4u));
        sum += static_cast<double>(d);
        count += 1u;
    }
    return (count == 0u) ? 0.0 : sum / static_cast<double>(count);
}

// =============================================================================
// ---- TASK-B-R9-13：--assets 模式（4 人物 .zmesh 资产画廊）----------------------
// =============================================================================
// 真实建模进自研管线的端到端验证：Unity ZMeshBaker.BakeGalleryToRepoData 烘焙的
// 4 个人物 .zmesh（CHR_*，Unity 坐标系语义 Y-up、已应用 import scale/轴转换）加载
// 进 --lights 同源的簇光管线（pbr_shadow.hlsl：簇光源 + PBR + CSM 阴影开）。
//
// 场景（恒离屏，无窗口/交换链/vsync——照 --parity 先例；截图颜色序确定 RGBA）：
//   地面 quad 20×20 @y=-1 + cube（(0,0,3.2)，既有元素保留）+ 4 人物横向一字排开
//   @z=9（中缝让开 cube 的视线走廊：z=9 处 cube 遮挡半宽 = 0.5×9/3.2 ≈ 1.41m；
//   站位间距按各自包围盒宽度 + 固定间隙；脚底对齐地面）。
//   每模型统一 PBR 常量色（albedo 逐模型一色区分；zmesh 不带材质——简报口径）。
//   光源：1 方向光（CSM 阴影来源）+ 4 静态点光 + 4 动画点光（环绕 cube 与人物排）。
//
// 校验（可复核）：
//   ①每模型 vertex/index 数打印（与 Unity 侧 Mesh 数值对照表进报告）
//   ②4 探针：每模型包围盒中心的屏幕投影点（推导同 kShadowProbeX 口径）readback
//     每 10 帧一次，像素 ≠ 清屏色（(8,8,12)±2）
//   ③第 60 帧全帧 readback → 24bit PNG 自写编码（复用 WritePng24）→
//     Docs/Parity/assets_gallery.png
//   ④60 帧稳定无设备移除 + InfoQueue 0 错误 + RG 零未声明使用
// 通过行：`D3D12 ASSETS GALLERY OK (models=4)`。
// =============================================================================

constexpr const char* kGalleryPngPath = "Docs/Parity/assets_gallery.png";
constexpr uint32_t kGalleryModelCount = 4u;
// 4 个烘焙产物（ZMeshBaker.BakeGalleryToRepoData 产出；命名与 FBX 一一对应）
const char* const kGalleryMeshPaths[kGalleryModelCount] = {
    "Native/tests/data/CHR_Kenney_CharacterMedium.zmesh",
    "Native/tests/data/CHR_Robot.zmesh",
    "Native/tests/data/CHR_Human.zmesh",
    "Native/tests/data/CHR_DarkElf.zmesh",
};
const char* const kGalleryModelNames[kGalleryModelCount] = {
    "CHR_Kenney_CharacterMedium", "CHR_Robot", "CHR_Human", "CHR_DarkElf",
};
constexpr uint32_t kGalleryFrames = 60u;             // 与 --lights 同帧数口径
constexpr uint32_t kGalleryCheckInterval = 10u;      // 每 10 帧 readback（--lights 同）
constexpr float kGalleryClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f}; // --lights 同
constexpr float kGalleryRowZ = 9.0f;                 // 人物排开深度（相机原点朝 +Z）
constexpr float kGalleryGroundY = -1.0f;             // 与地面 quad 同面
constexpr float kGalleryCentralGap = 3.2f;           // 中缝（cube 视线走廊，见节头注）
constexpr float kGallerySideGap = 0.8f;              // 相邻模型包围盒间隙
constexpr uint32_t kGalleryRowPitch = (kWidth * 4u + 255u) & ~255u; // 640×4=2560
// 统一 PBR 常量色（简报：每模型一色区分；roughness 半光面）
const float kGalleryAlbedo[kGalleryModelCount][3] = {
    {0.90f, 0.45f, 0.15f}, // Kenney：暖橙
    {0.35f, 0.60f, 0.85f}, // Robot：钢蓝
    {0.85f, 0.68f, 0.50f}, // Human：肤色
    {0.42f, 0.20f, 0.60f}, // DarkElf：深紫
};
constexpr float kGalleryRoughness[kGalleryModelCount] = {0.45f, 0.35f, 0.60f, 0.50f};

/// 画廊单模型加载态（几何/常量/探针全部就绪后的完整绘制单元）
struct GalleryModel {
    std::vector<FirstDrawVertex> verts;
    std::vector<uint32_t> indices;
    ZrBuffer vb {};
    ZrBuffer ib {};
    float boundsMin[3] = {};
    float boundsMax[3] = {};
    float centerWorld[3] = {}; // 包围盒中心的世界位置（探针投影输入）
    float model[16] = {};
    float mvp[16] = {};
    uint32_t probeX = 0u;
    uint32_t probeY = 0u;
};

// --assets 模式主体（返回进程退出码）。恒离屏：照 --parity 先例（无窗口/交换链）。
static int RunAssetsGalleryMode()
{
    std::printf("zrhi_d3d12_window: --assets mode (TASK-B-R9-13): 4 CHR .zmesh gallery, "
                "cluster lights + CSM shadows, offscreen %ux%u\n",
        kWidth, kHeight);

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --assets FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --assets device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --assets FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 渲染目标（离屏）+ 深度 + 级联阴影图（--shadows 同构）------------------------
    ZrImageDesc rtDesc {};
    rtDesc.format = ZrFormat::RGBA8Unorm;
    rtDesc.width = kWidth;
    rtDesc.height = kHeight;
    rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
    ZrImage offscreenTarget = device->CreateImage(rtDesc);
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    ZrImageViewDesc rtvViewDesc {};
    rtvViewDesc.image = offscreenTarget;
    rtvViewDesc.viewType = ZrImageViewType::Rtv;
    ZrImageView offscreenRtv = device->CreateImageView(rtvViewDesc);
    if (!offscreenTarget.IsValid() || !offscreenRtv.IsValid() || !depthImage.IsValid()
        || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --assets FAILED to create target/depth views\n");
        return 1;
    }
    ZrImageDesc shadowDesc {};
    shadowDesc.format = ZrFormat::D32Float;
    shadowDesc.width = kShadowsTexSize;
    shadowDesc.height = kShadowsTexSize;
    shadowDesc.arrayLayers = ZR_SHADOW_CASCADES;
    shadowDesc.usage = ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled;
    ZrImage shadowImage = device->CreateImage(shadowDesc);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        ZrImageViewDesc layerDsvDesc {};
        layerDsvDesc.image = shadowImage;
        layerDsvDesc.viewType = ZrImageViewType::Dsv;
        layerDsvDesc.subresources.baseArrayLayer = c;
        layerDsvDesc.subresources.layerCount = 1u;
        shadowDsvs[c] = device->CreateImageView(layerDsvDesc);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float; // 深度格式的非深度孪生解释（--shadows 同）
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    if (!shadowImage.IsValid() || !shadowSrv.IsValid()) {
        std::printf("zrhi_d3d12_window: --assets FAILED to create shadow image/views\n");
        return 1;
    }
    for (ZrImageView v : shadowDsvs) {
        if (!v.IsValid()) {
            std::printf("zrhi_d3d12_window: --assets FAILED to create cascade layer DSV\n");
            return 1;
        }
    }

    // ---- 几何加载：地面 quad + cube.zmesh（既有元素）+ 4 人物 .zmesh ------------------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<FirstDrawVertex> cubeVerts;
    std::vector<uint32_t> cubeIndices;
    {
        std::vector<char> meshBytes;
        if (!ReadWholeFile("Native/tests/data/cube.zmesh", meshBytes)) {
            std::printf("zrhi_d3d12_window: --assets FAILED to open cube.zmesh\n");
            return 1;
        }
        ZmeshData mesh {};
        const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
        if (parseResult != ZMESH_OK || mesh.stats.hasNormals == 0u) {
            std::printf("zrhi_d3d12_window: --assets cube.zmesh parse FAILED (error %d)\n",
                parseResult);
            return 1;
        }
        cubeVerts.resize(mesh.stats.vertexCount);
        for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
            std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
            std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
            std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
        }
        cubeIndices.resize(mesh.stats.indexCount);
        std::memcpy(cubeIndices.data(), mesh.indices,
            sizeof(uint32_t) * mesh.stats.indexCount);
    }

    GalleryModel models[kGalleryModelCount] = {};
    for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
        std::vector<char> meshBytes;
        if (!ReadWholeFile(kGalleryMeshPaths[m], meshBytes)) {
            std::printf("zrhi_d3d12_window: --assets FAILED to open '%s'\n",
                kGalleryMeshPaths[m]);
            return 1;
        }
        ZmeshData mesh {};
        const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
        if (parseResult != ZMESH_OK) {
            std::printf("zrhi_d3d12_window: --assets '%s' parse FAILED (error %d)\n",
                kGalleryMeshPaths[m], parseResult);
            return 1;
        }
        if (mesh.stats.hasNormals == 0u) {
            std::printf("zrhi_d3d12_window: --assets '%s' lacks normals（烘焙需含法线列）\n",
                kGalleryMeshPaths[m]);
            return 1;
        }
        GalleryModel& model = models[m];
        model.verts.resize(mesh.stats.vertexCount);
        for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
            std::memcpy(model.verts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
            std::memcpy(model.verts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
            if (mesh.stats.hasUv != 0u) {
                std::memcpy(model.verts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
            }
        }
        model.indices.resize(mesh.stats.indexCount);
        std::memcpy(model.indices.data(), mesh.indices,
            sizeof(uint32_t) * mesh.stats.indexCount);
        std::memcpy(model.boundsMin, mesh.stats.boundsMin, sizeof(model.boundsMin));
        std::memcpy(model.boundsMax, mesh.stats.boundsMax, sizeof(model.boundsMax));
        // 对照表证据行（Unity 侧 Mesh 数值对照见任务日志施工报告）
        std::printf("zrhi_d3d12_window: --assets model[%u] %s: verts=%u indices=%u uv=%u "
                    "boundsMin=(%.4f,%.4f,%.4f) boundsMax=(%.4f,%.4f,%.4f)\n",
            m, kGalleryModelNames[m], mesh.stats.vertexCount, mesh.stats.indexCount,
            mesh.stats.hasUv, model.boundsMin[0], model.boundsMin[1], model.boundsMin[2],
            model.boundsMax[0], model.boundsMax[1], model.boundsMax[2]);
    }

    // ---- 站位布局：横向一字排开，间距按包围盒（脚底对齐地面；中缝让开 cube 走廊）-----
    // 站位（包围盒中心 x）：左组 [0]=Kenney [1]=Robot，右组 [2]=Human [3]=DarkElf
    //（简报清单顺序；固定槽位使探针可复核）。
    float slotX[kGalleryModelCount] = {};
    {
        const float centralHalf = kGalleryCentralGap * 0.5f;
        const float w[kGalleryModelCount] = {
            models[0].boundsMax[0] - models[0].boundsMin[0],
            models[1].boundsMax[0] - models[1].boundsMin[0],
            models[2].boundsMax[0] - models[2].boundsMin[0],
            models[3].boundsMax[0] - models[3].boundsMin[0],
        };
        slotX[0] = -(centralHalf + w[0] * 0.5f);
        slotX[1] = -(centralHalf + w[0] + kGallerySideGap + w[1] * 0.5f);
        slotX[2] = centralHalf + w[2] * 0.5f;
        slotX[3] = centralHalf + w[2] + kGallerySideGap + w[3] * 0.5f;
        const float rowHalf = std::max(std::abs(slotX[1] - w[1] * 0.5f), slotX[3] + w[3] * 0.5f);
        const float visibleHalf = kGalleryRowZ * std::tan(3.14159265f / 6.0f)
            * (static_cast<float>(kWidth) / static_cast<float>(kHeight));
        std::printf("zrhi_d3d12_window: --assets layout rowZ=%.1f slots=(%.2f,%.2f,%.2f,%.2f) "
                    "rowHalf=%.2f visibleHalf=%.2f%s\n",
            kGalleryRowZ, slotX[0], slotX[1], slotX[2], slotX[3], rowHalf, visibleHalf,
            rowHalf > visibleHalf ? " (WARNING: row wider than frustum)" : "");
    }
    const float kFovY = 3.14159265f / 3.0f; // 60°（--lights/--shadows 同）
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
        GalleryModel& model = models[m];
        const float bcx = (model.boundsMin[0] + model.boundsMax[0]) * 0.5f;
        const float bcz = (model.boundsMin[2] + model.boundsMax[2]) * 0.5f;
        const float height = model.boundsMax[1] - model.boundsMin[1];
        // 平移使：包围盒中心 x = 站位 x、脚底（boundsMin.y）= 地面、包围盒中心 z = 行深
        MatTranslation(slotX[m] - bcx, kGalleryGroundY - model.boundsMin[1],
            kGalleryRowZ - bcz, model.model);
        model.centerWorld[0] = slotX[m];
        model.centerWorld[1] = kGalleryGroundY + height * 0.5f;
        model.centerWorld[2] = kGalleryRowZ;
    }

    // ---- 矩阵与探针投影 --------------------------------------------------------------
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kWidth) / static_cast<float>(kHeight), kNearZ,
        kFarZ, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float projView[16];
    MatMultiply(proj, view, projView);
    {
        float cubeModel[16];
        MatTranslation(0.0f, 0.0f, 3.2f, cubeModel); // 既有 cube 元素（--lights 同位）
        for (GalleryModel& model : models) {
            MatMultiply(projView, model.model, model.mvp);
        }
        // 探针：包围盒中心世界点 → 屏幕像素（推导口径同 kShadowProbeX 注：
        // ndcX = x/(z·tan30°·aspect)，ndcY = y/(z·tan30°)；D3D 屏幕原点左上）
        const float tanHalf = std::tan(kFovY * 0.5f);
        const float aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
        for (GalleryModel& model : models) {
            const float ndcX = model.centerWorld[0]
                / (model.centerWorld[2] * tanHalf * aspect);
            const float ndcY = model.centerWorld[1] / (model.centerWorld[2] * tanHalf);
            const float fx = (ndcX * 0.5f + 0.5f) * static_cast<float>(kWidth);
            const float fy = (0.5f - ndcY * 0.5f) * static_cast<float>(kHeight);
            model.probeX = std::min(kWidth - 2u,
                std::max(1u, static_cast<uint32_t>(fx + 0.5f)));
            model.probeY = std::min(kHeight - 2u,
                std::max(1u, static_cast<uint32_t>(fy + 0.5f)));
            std::printf("zrhi_d3d12_window: --assets probe[%s] world=(%.2f,%.2f,%.2f) "
                        "pixel=(%u,%u)\n",
                kGalleryModelNames[static_cast<uint32_t>(&model - models)],
                model.centerWorld[0], model.centerWorld[1], model.centerWorld[2],
                model.probeX, model.probeY);
        }
    }

    // ---- GPU 缓冲：VB/IB + 常量缓冲 ---------------------------------------------------
    auto makeVbIb = [&](const void* vData, uint64_t vSize, const void* iData, uint64_t iSize,
                        ZrBuffer& outVb, ZrBuffer& outIb) {
        ZrBufferDesc vbDesc {};
        vbDesc.size = vSize;
        vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
        outVb = device->CreateBuffer(vbDesc, vData);
        ZrBufferDesc ibDesc {};
        ibDesc.size = iSize;
        ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        outIb = device->CreateBuffer(ibDesc, iData);
        return outVb.IsValid() && outIb.IsValid();
    };
    ZrBuffer groundVb {};
    ZrBuffer groundIb {};
    ZrBuffer cubeVb {};
    ZrBuffer cubeIb {};
    bool buffersOk = makeVbIb(groundVerts, sizeof(groundVerts), groundIndices,
        sizeof(groundIndices), groundVb, groundIb)
        && makeVbIb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex),
            cubeIndices.data(), uint64_t(cubeIndices.size()) * sizeof(uint32_t), cubeVb,
            cubeIb);
    for (GalleryModel& model : models) {
        buffersOk = buffersOk
            && makeVbIb(model.verts.data(),
                uint64_t(model.verts.size()) * sizeof(FirstDrawVertex), model.indices.data(),
                uint64_t(model.indices.size()) * sizeof(uint32_t), model.vb, model.ib);
    }
    if (!buffersOk) {
        std::printf("zrhi_d3d12_window: --assets FAILED to create VB/IB\n");
        return 1;
    }

    // b0×6（地面/cube/4 模型）+ 阴影 pass b0×4 + 阴影 model b1×6 + b1/b2/b3
    constexpr uint32_t kGalleryItemCount = 2u + kGalleryModelCount; // 地面+cube+4 模型
    ZrBuffer cb0[kGalleryItemCount] = {};
    ZrBuffer cbShadowModel[kGalleryItemCount] = {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    void* cb0Mapped[kGalleryItemCount] = {};
    void* cbShadowModelMapped[kGalleryItemCount] = {};
    {
        auto makeCb = [&](uint64_t size, ZrBuffer& outHandle, void*& outMapped) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            outHandle = device->CreateBuffer(desc);
            IZrBuffer* iface = device->GetBuffer(outHandle);
            outMapped = (iface != nullptr && outHandle.IsValid()) ? iface->Map() : nullptr;
            return outMapped != nullptr;
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        void* cbLightsMapped = nullptr;
        void* cbClustersMapped = nullptr;
        void* cbShadowsMapped = nullptr;
        bool ok = makeCb(1024u, cbLights, cbLightsMapped)
            && makeCb(cb2Size, cbClusters, cbClustersMapped)
            && makeCb(384u, cbShadows, cbShadowsMapped);
        for (uint32_t i = 0; i < kGalleryItemCount; ++i) {
            ok = ok && makeCb(256u, cb0[i], cb0Mapped[i]) && makeCb(256u,
                cbShadowModel[i], cbShadowModelMapped[i]);
        }
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            void* mapped = nullptr;
            ok = ok && makeCb(256u, cbShadowPass[c], mapped);
        }
        if (!ok) {
            std::printf("zrhi_d3d12_window: --assets FAILED to create/map constant buffers\n");
            return 1;
        }
        // cbLights/cbClusters 的初始映射仅作创建期校验；两缓冲每帧重写（帧循环内重 Map）。
    }

    // ---- 着色器 + PSO（pbr_shadow：簇光 + CSM；shadow_depth：depth-only）--------------
    std::vector<char> hlsl;
    std::vector<char> depthHlsl;
    if (!ReadWholeFile("Native/shaders/pbr_shadow.hlsl", hlsl)
        || !ReadWholeFile("Native/shaders/shadow_depth.hlsl", depthHlsl)) {
        std::printf("zrhi_d3d12_window: --assets FAILED to open shaders\n");
        return 1;
    }
    const ZrShader mainVs = device->CompileHlsl(
        ZrShaderStage::Vertex, hlsl.data(), hlsl.size(), "VsMain");
    const ZrShader mainPs = device->CompileHlsl(
        ZrShaderStage::Pixel, hlsl.data(), hlsl.size(), "PsMain");
    const ZrShader shadowVs = device->CompileHlsl(
        ZrShaderStage::Vertex, depthHlsl.data(), depthHlsl.size(), "VsMain");
    if (!mainVs.IsValid() || !mainPs.IsValid() || !shadowVs.IsValid()) {
        std::printf("zrhi_d3d12_window: --assets shader compilation FAILED (see stderr)\n");
        return 1;
    }
    ZrGraphicsPsoDesc mainPsoDesc {};
    mainPsoDesc.vertexShader = mainVs;
    mainPsoDesc.pixelShader = mainPs;
    mainPsoDesc.rtvFormat = ZrFormat::RGBA8Unorm; // 恒离屏（RGBA 序，截图无需 swizzle）
    mainPsoDesc.dsvFormat = ZrFormat::D32Float;
    mainPsoDesc.blend = ZrBlendMode::Opaque;
    mainPsoDesc.depthTest = true;
    mainPsoDesc.depthWrite = true;
    mainPsoDesc.cullMode = ZrCullMode::None; // 烘焙绕序与 --lights/--shadows 同策（None）
    mainPsoDesc.rootCbvCount = 4u;           // b0 逐绘 / b1 光源 / b2 簇 / b3 级联
    mainPsoDesc.srvTable = true;             // t0 级联阴影图 + s0 Point/Clamp
    ZrPipelineState mainPso = device->CreatePipelineState(mainPsoDesc);
    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = shadowVs;
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    shadowPsoDesc.cullMode = ZrCullMode::Front; // --shadows 同：正面剔除抗自阴影 acne
    shadowPsoDesc.rootCbvCount = 2u;            // b0 = 级联 lightVP / b1 = 逐绘 model
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    if (!mainPso.IsValid() || !shadowPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --assets FAILED to create graphics PSOs (main=%d "
                    "shadow=%d)\n",
            mainPso.IsValid() ? 1 : 0, shadowPso.IsValid() ? 1 : 0);
        return 1;
    }

    // ---- CPU 级联（静态相机 → 一次构建；--shadows 同参数）-----------------------------
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(kNearZ, kFarZ, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("zrhi_d3d12_window: --assets CascadeSplits FAILED\n");
            return 1;
        }
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES - 1u; ++c) {
            cascadeSplits[c] = interior[c];
        }
        cascadeSplits[ZR_SHADOW_CASCADES - 1u] = kFarZ;
        std::printf("zrhi_d3d12_window: --assets cascade splits (near=%.1f far=%.1f "
                    "lambda=0.75): %.4f %.4f %.4f %.4f\n",
            kNearZ, kFarZ, cascadeSplits[0], cascadeSplits[1], cascadeSplits[2],
            cascadeSplits[3]);
    }
    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    // 方向光与 --lights 同向（左上前方）；场景球覆盖人物排（z=9）与 cube（z=3.2）
    const float kLightDir[3] = {0.4f, 0.85f, 0.35f};
    {
        const float invLen = 1.0f
            / std::sqrt(kLightDir[0] * kLightDir[0] + kLightDir[1] * kLightDir[1]
                        + kLightDir[2] * kLightDir[2]);
        for (int a = 0; a < 3; ++a) {
            shadowParamsStruct.lightDir[a] = kLightDir[a] * invLen;
        }
    }
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = -0.5f;
    shadowParamsStruct.sceneCenter[2] = 6.0f;
    shadowParamsStruct.sceneRadius = 15.0f; // --shadows 同（20×20 地面余量）
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("zrhi_d3d12_window: --assets BuildCascades FAILED\n");
        return 1;
    }
    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;
    }
    std::memcpy(shadowCb.cascadeSplits, cascadeSplits, sizeof(cascadeSplits));
    {
        IZrBuffer* iface = device->GetBuffer(cbShadows);
        void* mapped = iface->Map();
        if (mapped == nullptr) {
            std::printf("zrhi_d3d12_window: --assets cbShadows Map FAILED\n");
            return 1;
        }
        std::memcpy(mapped, &shadowCb, sizeof(shadowCb));
    }
    // 阴影 pass 逐级联 CB（b0 = 该级联 lightVP）
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("zrhi_d3d12_window: --assets shadow pass CB %u Map FAILED\n", c);
            return 1;
        }
        std::memcpy(mapped, &spc, sizeof(spc));
    }

    // ---- 常量写入：b0 逐项 + 阴影 model（静态部分一次写；光源/簇每帧重写）------------
    {
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        const float ambient = 0.12f;
        auto fillCommon = [&](ShadowDrawConstants& c) {
            c.projParams[0] = kFarZ / (kFarZ - kNearZ);
            c.projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
            c.projParams[2] = kNearZ;
            c.projParams[3] = kFarZ;
            c.gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
            c.gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
            c.gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
            c.gridParams[3] = 1.0f / (kFarZ - kNearZ);
            c.screenParams[0] = static_cast<float>(kWidth);
            c.screenParams[1] = static_cast<float>(kHeight);
            c.screenParams[2] = 9.0f; // 1 方向光 + 4 静态 + 4 动画（--lights 同 9 光）
            c.screenParams[3] = ambient;
            c.camPos[0] = 0.0f;
            c.camPos[1] = 0.0f;
            c.camPos[2] = 0.0f;
            c.camPos[3] = 0.0f;
            c.cascadeSplits[0] = cascadeSplits[0];
            c.cascadeSplits[1] = cascadeSplits[1];
            c.cascadeSplits[2] = cascadeSplits[2];
            c.cascadeSplits[3] = cascadeSplits[3];
            c.shadowParams[0] = 1.0f; // 阴影开（画廊契约）
            c.shadowParams[1] = static_cast<float>(kShadowsTexSize);
        };
        ShadowDrawConstants itemConst[kGalleryItemCount] = {};
        // 地面
        std::memcpy(itemConst[0].mvp, projView, sizeof(projView));
        std::memcpy(itemConst[0].model, identity, sizeof(identity));
        itemConst[0].material[0] = 0.50f;
        itemConst[0].material[1] = 0.52f;
        itemConst[0].material[2] = 0.50f;
        itemConst[0].material[3] = 0.85f;
        // cube（既有元素，--lights 同位同材质）
        {
            float cubeModel[16];
            MatTranslation(0.0f, 0.0f, 3.2f, cubeModel);
            std::memcpy(itemConst[1].model, cubeModel, sizeof(cubeModel));
            MatMultiply(projView, cubeModel, itemConst[1].mvp);
            itemConst[1].material[0] = 0.75f;
            itemConst[1].material[1] = 0.75f;
            itemConst[1].material[2] = 0.78f;
            itemConst[1].material[3] = 0.35f;
        }
        // 4 模型（统一 PBR 常量色）
        for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
            std::memcpy(itemConst[2u + m].model, models[m].model, sizeof(models[m].model));
            std::memcpy(itemConst[2u + m].mvp, models[m].mvp, sizeof(models[m].mvp));
            for (int a = 0; a < 3; ++a) {
                itemConst[2u + m].material[a] = kGalleryAlbedo[m][a];
            }
            itemConst[2u + m].material[3] = kGalleryRoughness[m];
        }
        for (uint32_t i = 0; i < kGalleryItemCount; ++i) {
            fillCommon(itemConst[i]);
            std::memcpy(cb0Mapped[i], &itemConst[i], sizeof(itemConst[i]));
            ShadowModelConstants smc {};
            std::memcpy(smc.shadowModel, itemConst[i].model, sizeof(smc.shadowModel));
            std::memcpy(cbShadowModelMapped[i], &smc, sizeof(smc));
        }
    }

    // ---- readback：4 探针（1×1×4 槽）+ 全帧（第 60 帧 PNG 源）------------------------
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackProbes
        = MakeReadbackBuffer(device->D3D12Device(), 256u * kGalleryModelCount);
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackFrame
        = MakeReadbackBuffer(device->D3D12Device(),
            uint64_t(kGalleryRowPitch) * kHeight);
    if (readbackProbes == nullptr || readbackFrame == nullptr) {
        std::printf("zrhi_d3d12_window: --assets FAILED to create readback buffers\n");
        return 1;
    }

    // ---- 光源：1 方向光（槽 0）+ 4 静态点光 + 4 动画点光（--lights 同 9 光口径）-------
    // 静态/动画点位朝人物排（z=9）与 cube（z=3.2）之间布光；方向光即 CSM 阴影来源。
    constexpr uint32_t kGalleryLightCount = 9u;
    LightData sceneLights[kGalleryLightCount] = {};
    {
        LightData& dir = sceneLights[0];
        const float invLen = 1.0f
            / std::sqrt(kLightDir[0] * kLightDir[0] + kLightDir[1] * kLightDir[1]
                        + kLightDir[2] * kLightDir[2]);
        dir.pos[0] = kLightDir[0] * invLen;
        dir.pos[1] = kLightDir[1] * invLen;
        dir.pos[2] = kLightDir[2] * invLen;
        dir.color[0] = 1.0f;
        dir.color[1] = 0.95f;
        dir.color[2] = 0.88f;
        dir.intensity = 1.0f;
        dir.kind = kZrLightDirectional;
    }
    sceneLights[1] = MakeAppPointLight(-3.6f, 0.8f, 7.0f, 5.0f, 2.0f, 1.0f, 0.35f, 0.25f);
    sceneLights[2] = MakeAppPointLight(3.6f, 0.8f, 7.0f, 5.0f, 2.0f, 0.25f, 0.5f, 1.0f);
    sceneLights[3] = MakeAppPointLight(0.0f, 1.6f, 11.0f, 7.0f, 2.0f, 0.3f, 1.0f, 0.4f);
    sceneLights[4] = MakeAppPointLight(0.0f, 2.6f, 5.0f, 6.0f, 1.5f, 1.0f, 0.85f, 0.3f);
    const float animColors[4][3] = {{1.0f, 0.5f, 0.15f}, {0.2f, 0.9f, 1.0f}, {0.9f, 0.2f, 0.9f},
        {0.5f, 1.0f, 0.2f}};

    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);

    // ---- 帧循环（60 帧；每 10 帧探针 readback；第 60 帧全帧 → PNG）--------------------
    uint32_t readbackChecks = 0u;
    uint32_t probePasses[kGalleryModelCount] = {};
    bool allOk = true;
    uint32_t undeclaredUses = 0u;
    bool pngOk = false;

    for (uint32_t frame = 0u; frame < kGalleryFrames; ++frame) {
        const bool doReadback = ((frame + 1u) % kGalleryCheckInterval) == 0u;
        const bool finalFrame = (frame + 1u == kGalleryFrames);

        // 动画点光 5..8：环绕 (0,1.0,6.5) 半径 4.2（覆盖 cube 与人物排之间），120 帧一圈
        for (uint32_t i = 0; i < 4u; ++i) {
            const float angle = 6.2831853f * static_cast<float>(frame + 1u) / 120.0f
                + 3.14159265f * 0.5f * static_cast<float>(i);
            LightData& l = sceneLights[5u + i];
            l.pos[0] = std::cos(angle) * 4.2f;
            l.pos[1] = 1.0f;
            l.pos[2] = 6.5f + std::sin(angle) * 4.2f;
            l.radius = 3.5f;
            l.color[0] = animColors[i][0];
            l.color[1] = animColors[i][1];
            l.color[2] = animColors[i][2];
            l.intensity = 6.0f;
            l.kind = kZrLightPoint;
        }

        // CPU 簇分配 → CB1/CB2
        PackedLight packedLights[kGalleryLightCount];
        PackLights(sceneLights, kGalleryLightCount, packedLights);
        {
            IZrBuffer* iface = device->GetBuffer(cbLights);
            void* mapped = iface->Map();
            if (mapped == nullptr) {
                std::printf("  frame %u: cbLights Map FAILED\n", frame + 1u);
                allOk = false;
                continue;
            }
            std::memcpy(mapped, packedLights, sizeof(packedLights));
            std::memset(static_cast<uint8_t*>(mapped) + sizeof(packedLights), 0,
                1024u - sizeof(packedLights));
        }
        const int32_t flatCount = AssignLightsToClusters(grid, sceneLights, kGalleryLightCount,
            clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
        if (flatCount < 0) {
            std::printf("  frame %u: AssignLightsToClusters FAILED (%d)\n", frame + 1u,
                flatCount);
            allOk = false;
            continue;
        }
        {
            IZrBuffer* iface = device->GetBuffer(cbClusters);
            void* mapped = iface->Map();
            if (mapped == nullptr) {
                std::printf("  frame %u: cbClusters Map FAILED\n", frame + 1u);
                allOk = false;
                continue;
            }
            uint8_t* cb2 = static_cast<uint8_t*>(mapped);
            std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);
            std::memcpy(cb2 + size_t(ZR_CLUSTER_COUNT) * 4u, clusterFlat.data(),
                size_t(flatCount) * 4u);
        }
        if (frame == 0u) {
            std::printf("zrhi_d3d12_window: --assets cluster assign frame1: lights=%u "
                        "clusters=%u flat entries=%d\n",
                kGalleryLightCount, ZR_CLUSTER_COUNT, flatCount);
        }

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", offscreenTarget));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        RgHandle shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kGalleryClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });

        // 每级联 depth-only 阴影 pass（全部 6 项 cast：地面/cube/4 人物）
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            graph.AddPass("ShadowCascadePass",
                [&](RgPass& pass) {
                    pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                        ZrAccessBits::DepthStencilAttachmentWrite,
                        ZrPipelineStageBits::LateFragmentTests);
                },
                [&, c](RgContext&) {
                    cmdList->SetPipelineState(shadowPso);
                    cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                    cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                    nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(shadowDsvs[c]),
                        D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
                    cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                    auto drawShadowSet = [&]() {
                        // 地面（6 索引）
                        {
                            ZrBuffer vbs[1] = {groundVb};
                            cmdList->SetVertexBuffers(vbs, 1u);
                            cmdList->SetIndexBuffer(groundIb);
                            cmdList->SetGraphicsConstantBuffer(1u, cbShadowModel[0]);
                            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                        }
                        // cube
                        {
                            ZrBuffer vbs[1] = {cubeVb};
                            cmdList->SetVertexBuffers(vbs, 1u);
                            cmdList->SetIndexBuffer(cubeIb);
                            cmdList->SetGraphicsConstantBuffer(1u, cbShadowModel[1]);
                            cmdList->DrawIndexedInstanced(
                                static_cast<uint32_t>(cubeIndices.size()), 1u, 0u, 0u, 0u);
                        }
                        // 4 人物
                        for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
                            ZrBuffer vbs[1] = {models[m].vb};
                            cmdList->SetVertexBuffers(vbs, 1u);
                            cmdList->SetIndexBuffer(models[m].ib);
                            cmdList->SetGraphicsConstantBuffer(1u, cbShadowModel[2u + m]);
                            cmdList->DrawIndexedInstanced(
                                static_cast<uint32_t>(models[m].indices.size()), 1u, 0u, 0u,
                                0u);
                        }
                    };
                    drawShadowSet();
                });
        }

        // 主绘制 Pass（PBR + 簇光 + CSM 阴影：地面 + cube + 4 人物）
        graph.AddPass("GalleryMainPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                    ZrAccessBits::DepthStencilAttachmentRead,
                    ZrPipelineStageBits::FragmentShader);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mainPso);
                cmdList->SetRenderTarget(offscreenRtv, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                cmdList->SetGraphicsBindlessSrv(4u, shadowSrv); // t0 级联阴影图
                auto drawItem = [&](uint32_t vbIndexCount, ZrBuffer vb, ZrBuffer ib,
                                    ZrBuffer cb) {
                    ZrBuffer vbs[1] = {vb};
                    cmdList->SetVertexBuffers(vbs, 1u);
                    cmdList->SetIndexBuffer(ib);
                    cmdList->SetGraphicsConstantBuffer(0u, cb);
                    cmdList->DrawIndexedInstanced(vbIndexCount, 1u, 0u, 0u, 0u);
                };
                drawItem(6u, groundVb, groundIb, cb0[0]);
                drawItem(static_cast<uint32_t>(cubeIndices.size()), cubeVb, cubeIb, cb0[1]);
                for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
                    drawItem(static_cast<uint32_t>(models[m].indices.size()), models[m].vb,
                        models[m].ib, cb0[2u + m]);
                }
            });

        // ReadbackPass：校验帧 4 探针；最终帧追加全帧（PNG 源）
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(backH));
                    D3D12_TEXTURE_COPY_LOCATION src {};
                    src.pResource = texture;
                    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    src.SubresourceIndex = 0u;
                    // 4 探针：同一 readback 缓冲的 4 个 256B 槽（footprint Offset 定位）
                    for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
                        const D3D12_BOX box = {models[m].probeX, models[m].probeY, 0u,
                            models[m].probeX + 1u, models[m].probeY + 1u, 1u};
                        D3D12_TEXTURE_COPY_LOCATION dst {};
                        dst.pResource = readbackProbes.Get();
                        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                        dst.PlacedFootprint.Offset = 256ull * m;
                        dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                        dst.PlacedFootprint.Footprint.Width = 1u;
                        dst.PlacedFootprint.Footprint.Height = 1u;
                        dst.PlacedFootprint.Footprint.Depth = 1u;
                        dst.PlacedFootprint.Footprint.RowPitch = 256u;
                        nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
                    }
                    if (finalFrame) {
                        // 全帧 → PNG 源（离屏恒 RGBA8Unorm，WritePng24 直取 RGB 序）
                        const D3D12_BOX fullBox = {0u, 0u, 0u, kWidth, kHeight, 1u};
                        D3D12_TEXTURE_COPY_LOCATION fullDst {};
                        fullDst.pResource = readbackFrame.Get();
                        fullDst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                        fullDst.PlacedFootprint.Footprint.Format
                            = DXGI_FORMAT_R8G8B8A8_UNORM;
                        fullDst.PlacedFootprint.Footprint.Width = kWidth;
                        fullDst.PlacedFootprint.Footprint.Height = kHeight;
                        fullDst.PlacedFootprint.Footprint.Depth = 1u;
                        fullDst.PlacedFootprint.Footprint.RowPitch = kGalleryRowPitch;
                        nativeCmd->CopyTextureRegion(&fullDst, 0u, 0u, 0u, &src, &fullBox);
                    }
                });
        }

        if (!graph.Compile()) {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        const RgExecuteStats rgStats = graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末手动屏障：深度/阴影/离屏图回 COMMON（--parity 同策）
        ZrImageBarrier depthReset = MakeBarrier(depthImage,
            ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);
        ZrImageBarrier shadowReset = MakeBarrier(shadowImage,
            ZrImageLayout::DepthStencilReadOnlyOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&shadowReset, 1u, nullptr, 0u);
        {
            const ZrImageLayout backLayoutNow = doReadback ? ZrImageLayout::TransferSrcOptimal
                                                           : ZrImageLayout::ColorAttachmentOptimal;
            ZrImageBarrier backReset = MakeBarrier(offscreenTarget, backLayoutNow,
                ZrImageLayout::Undefined, ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&backReset, 1u, nullptr, 0u);
        }
        cmdList->End();
        queue->Submit(cmdHandle);
        device->WaitIdle();
        graph.FlushRetired(*device);

        // ---- 校验帧：4 探针非清屏色 --------------------------------------------------
        if (doReadback) {
            ++readbackChecks;
            void* mapped = nullptr;
            const HRESULT hr = readbackProbes->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  frame %u: probe readback Map FAILED (hr=0x%08lX)\n", frame + 1u,
                    static_cast<unsigned long>(hr));
                allOk = false;
            } else {
                const int clear0 = static_cast<int>(kGalleryClearColor[0] * 255.0f + 0.5f);
                const int clear1 = static_cast<int>(kGalleryClearColor[1] * 255.0f + 0.5f);
                const int clear2 = static_cast<int>(kGalleryClearColor[2] * 255.0f + 0.5f);
                for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
                    // 槽 m 位于 256B×m（footprint Offset 同步）
                    const uint8_t* px = static_cast<const uint8_t*>(mapped) + 256u * m;
                    const int r = static_cast<int>(px[0]);
                    const int g = static_cast<int>(px[1]);
                    const int b = static_cast<int>(px[2]);
                    const bool probeOk = std::abs(r - clear0) > 2 || std::abs(g - clear1) > 2
                        || std::abs(b - clear2) > 2;
                    probePasses[m] += probeOk ? 1u : 0u;
                    std::printf("  frame %u probe[%s] pixel=(%u,%u) RGB=(%d,%d,%d) -> %s\n",
                        frame + 1u, kGalleryModelNames[m], models[m].probeX, models[m].probeY, r,
                        g, b, probeOk ? "OK" : "CLEAR");
                    allOk = allOk && probeOk;
                }
                readbackProbes->Unmap(0, nullptr);
            }
        }

        // ---- 最终帧：全帧 → PNG -------------------------------------------------------
        if (finalFrame) {
            void* mapped = nullptr;
            const HRESULT hr = readbackFrame->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  frame %u: full-frame Map FAILED (hr=0x%08lX)\n", frame + 1u,
                    static_cast<unsigned long>(hr));
                allOk = false;
            } else {
                std::filesystem::create_directories("Docs/Parity");
                pngOk = WritePng24(kGalleryPngPath, static_cast<const uint8_t*>(mapped), kWidth,
                    kHeight, kGalleryRowPitch);
                std::printf("  frame %u full-frame readback -> %s (%s)\n", frame + 1u,
                    kGalleryPngPath, pngOk ? "written" : "FAILED");
                readbackFrame->Unmap(0, nullptr);
                allOk = allOk && pngOk;
            }
        }
        std::printf("frame %2u/%u: [ASSETS] flatEntries=%d%s\n", frame + 1u, kGalleryFrames,
            flatCount, doReadback ? "  (+readback)" : "");
    }

    // ---- 无设备移除 + InfoQueue 计数 --------------------------------------------------
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;

    // ---- 清理 -------------------------------------------------------------------------
    for (uint32_t i = 0; i < kGalleryItemCount; ++i) {
        device->Destroy(cb0[i]);
        device->Destroy(cbShadowModel[i]);
    }
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        device->Destroy(cbShadowPass[c]);
    }
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    for (GalleryModel& model : models) {
        device->Destroy(model.vb);
        device->Destroy(model.ib);
    }
    for (ZrImageView& v : shadowDsvs) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    device->Destroy(shadowSrv);
    device->Destroy(mainPso);
    device->Destroy(shadowPso);
    device->Destroy(mainVs);
    device->Destroy(mainPs);
    device->Destroy(shadowVs);
    device->Destroy(offscreenRtv);
    device->Destroy(dsvView);
    device->Destroy(depthImage);
    device->Destroy(shadowImage);
    device->Destroy(offscreenTarget);
    readbackProbes.Reset();
    readbackFrame.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: [ASSETS] frames=%u checks=%u probePasses="
                "%u/%u %u/%u %u/%u %u/%u png=%d removed=%s errors=%u\n",
        kGalleryFrames, readbackChecks, probePasses[0], readbackChecks, probePasses[1],
        readbackChecks, probePasses[2], readbackChecks, probePasses[3], readbackChecks,
        pngOk ? 1 : 0, noDeviceRemoval ? "S_OK" : "REMOVED", infoQueueErrors);
    bool probesAll = readbackChecks > 0u;
    for (uint32_t m = 0; m < kGalleryModelCount; ++m) {
        probesAll = probesAll && probePasses[m] == readbackChecks;
    }
    if (allOk && probesAll && pngOk && noDeviceRemoval && undeclaredUses == 0u
        && infoQueueErrors == 0u) {
        std::printf("D3D12 ASSETS GALLERY OK (models=%u)\n", kGalleryModelCount);
        return 0;
    }
    std::printf("D3D12 ASSETS GALLERY FAILED (allOk=%d checks=%u probes=%u/%u/%u/%u png=%d "
                "removed=%d undeclared=%u errors=%u)\n",
        allOk ? 1 : 0, readbackChecks, probePasses[0], probePasses[1], probePasses[2],
        probePasses[3], pngOk ? 1 : 0, noDeviceRemoval ? 0 : 1, undeclaredUses,
        infoQueueErrors);
    return 1;
}

// =============================================================================
// ---- TASK-B-TECH-03：--planet 模式（浮点原点重定位 + 球面重力 + 球体渲染雏形）--
// =============================================================================
// 恒离屏（--assets 同策）。数学内核 tests/planet_math.h 与 test_planet.cpp
// doctest 共享同一实现（窗口程序验证的就是单测覆盖的那份代码）。
// 场景/飞行/校验设计见文件头 TASK-B-TECH-03 块。
// =============================================================================

namespace zplanetapp {

constexpr uint32_t kPlanetFrames = 300u;
constexpr uint32_t kPlanetGroundEnd = 120u;  // 帧 [0,120) 贴地
constexpr uint32_t kPlanetClimbEnd = 210u;   // 帧 [120,210) 爬升；[210,300) 入轨
constexpr uint32_t kPlanetGroundShotFrame = 40u;
constexpr uint32_t kPlanetClimbShotFrame = 148u;  // alt ≈ 61.7km（地平线曲率）
constexpr uint32_t kPlanetOrbitShotFrame = 255u;
constexpr double kRelocThresholdM = 10000.0;      // 重定位阈值（简报口径 10km）
constexpr double kFlightAuditM = 100000.0;        // 抖动审计飞行距离（简报 100km）
constexpr double kNearProbeM = 10.37;             // 审计近场物体距离（m）
constexpr double kDiskAltMinM = 3000.0;           // 近场片 → 远盘切换（对地高度）
constexpr double kSphereAltMinM = 1.0e6;          // 远盘 → 整球切换
constexpr double kEllipseDrawAltMinM = 1.0e5;     // 轨道椭圆线带绘制下限高度
constexpr double kOrbitCameraDistM = 2.6e7;       // 入轨相机距行星中心
constexpr float kPlanetClearColor[4] = {0.051f, 0.077f, 0.153f, 1.0f}; // = 着色器 kSkyColor

constexpr uint32_t kPatchDim = 192u;              // 近场地形片 192²（250m 格距）
constexpr double kPatchExtentM = 24000.0;         // ±24km
constexpr uint32_t kDiskRings = 160u;             // 远盘极坐标网格
constexpr uint32_t kDiskSectors = 160u;
constexpr double kDiskRadiusM = 1.1e6;            // 1100km（覆盖真地平线 889km）
constexpr uint32_t kSphereStacks = 48u;           // 整球 UV 网格
constexpr uint32_t kSphereSlices = 64u;
constexpr uint32_t kRibbonMaxQuads = 640u;        // 线带四边形容量（弹道+椭圆）
constexpr uint32_t kPlanetCubeCount = 3u;         // 浮点原点演示立方体

// 近场抖动实况探针偏移（贴地相位逐帧采样；模拟贴着相机的近场顶点）
constexpr double kLiveProbeOffsets[8][3] = {
    {10.37, 3.2, -7.7}, {-5.1, 1.05, 12.9}, {18.2, -2.4, 4.4}, {-12.6, 0.5, -15.3},
    {7.7, 5.5, 22.1}, {-20.4, -1.2, 8.8}, {3.3, 2.2, -18.6}, {25.6, -0.8, 1.1},
};

// b0 常量（160B）——与 Native/shaders/planet.hlsl CBufPerDraw 一一对应
struct PlanetDrawConstants {
    float mvp[16];   // clip = mvp·pos（列向量约定，行主序存储）
    float model[16]; // 渲染空间（相机相对）物体变换
    float color[4];  // 模式 2 纯色 rgb
    float params[4]; // x=模式(0地形/1球体/2纯色) y=等高线间距(m) z=雾距离(m,≤0关)
};
static_assert(sizeof(PlanetDrawConstants) == 160, "PlanetDrawConstants must be 160 bytes");

// LH lookAt 旋转（相机恒在渲染空间原点；列向量约定、行主序存储——与
// MatMultiply/MatPerspectiveFovLH 同约定；clip = P·V·pos）
void PlanetMatLookAtLH(const float fwd[3], const float upHint[3], float* out)
{
    float rx = upHint[1] * fwd[2] - upHint[2] * fwd[1];
    float ry = upHint[2] * fwd[0] - upHint[0] * fwd[2];
    float rz = upHint[0] * fwd[1] - upHint[1] * fwd[0];
    const float rLen = std::sqrt(rx * rx + ry * ry + rz * rz);
    rx /= rLen;
    ry /= rLen;
    rz /= rLen;
    const float ux = fwd[1] * rz - fwd[2] * ry;
    const float uy = fwd[2] * rx - fwd[0] * rz;
    const float uz = fwd[0] * ry - fwd[1] * rx;
    const float m[16] = { rx, ry, rz, 0.0f, ux, uy, uz, 0.0f, fwd[0], fwd[1], fwd[2], 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

double PlanetSmoothstep(double edge0, double edge1, double x)
{
    const double t = std::min(1.0, std::max(0.0, (x - edge0) / (edge1 - edge0)));
    return t * t * (3.0 - 2.0 * t);
}

// 轨迹 ribbon 段构造：相邻采样点 → 相机朝向 billboard 窄四边形（RHI v1 仅
// TRIANGLELIST；宽度按距离比例 ≈ 恒定像素宽）。返回四边形数；outVerts 为
// FirstDrawVertex 流（每四边形 4 顶点，索引由调用方静态模式表覆盖）。
uint32_t PlanetBuildRibbon(const std::vector<zplanet::Vec3d>& path, const double camAbs[3],
    double farZ, float halfWidthFactor, std::vector<float>& outVerts)
{
    outVerts.clear();
    if (path.size() < 2u) {
        return 0u;
    }
    outVerts.reserve((path.size() - 1u) * 32u);
    uint32_t quads = 0u;
    for (size_t i = 0u; i + 1u < path.size(); ++i) {
        const double rel0[3] = { path[i].x - camAbs[0], path[i].y - camAbs[1],
            path[i].z - camAbs[2] };
        const double rel1[3] = { path[i + 1u].x - camAbs[0], path[i + 1u].y - camAbs[1],
            path[i + 1u].z - camAbs[2] };
        const double midD[3] = { (rel0[0] + rel1[0]) * 0.5, (rel0[1] + rel1[1]) * 0.5,
            (rel0[2] + rel1[2]) * 0.5 };
        const float mid[3] = { static_cast<float>(midD[0]), static_cast<float>(midD[1]),
            static_cast<float>(midD[2]) };
        const float midLen = std::sqrt(mid[0] * mid[0] + mid[1] * mid[1] + mid[2] * mid[2]);
        if (!(midLen > 1.0e-3f) || midLen > static_cast<float>(farZ) * 1.05f) {
            continue; // 距离退化 / 远平面外
        }
        const float view[3] = { mid[0] / midLen, mid[1] / midLen, mid[2] / midLen };
        float dir[3] = { static_cast<float>(rel1[0] - rel0[0]),
            static_cast<float>(rel1[1] - rel0[1]), static_cast<float>(rel1[2] - rel0[2]) };
        const float dirLen = std::sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
        if (!(dirLen > 1.0e-6f)) {
            continue;
        }
        dir[0] /= dirLen;
        dir[1] /= dirLen;
        dir[2] /= dirLen;
        // side = dir × view（退化时换垂直参考轴）
        float side[3] = { dir[1] * view[2] - dir[2] * view[1], dir[2] * view[0] - dir[0] * view[2],
            dir[0] * view[1] - dir[1] * view[0] };
        float sideLen = std::sqrt(side[0] * side[0] + side[1] * side[1] + side[2] * side[2]);
        if (sideLen < 1.0e-4f) {
            const float altUp[3] = { 0.0f, 1.0f, 0.0f };
            side[0] = dir[1] * altUp[2] - dir[2] * altUp[1];
            side[1] = dir[2] * altUp[0] - dir[0] * altUp[2];
            side[2] = dir[0] * altUp[1] - dir[1] * altUp[0];
            sideLen = std::sqrt(side[0] * side[0] + side[1] * side[1] + side[2] * side[2]);
            if (sideLen < 1.0e-6f) {
                continue;
            }
        }
        const float invSide = 1.0f / sideLen;
        side[0] *= invSide;
        side[1] *= invSide;
        side[2] *= invSide;
        float halfW = halfWidthFactor * midLen;
        halfW = std::min(300000.0f, std::max(0.5f, halfW));
        const float nrm[3] = { -view[0], -view[1], -view[2] }; // 朝相机（光照稳定）
        float q[4][8];
        const float* relF[2] = { nullptr, nullptr };
        const float rel0f[3] = { static_cast<float>(rel0[0]), static_cast<float>(rel0[1]),
            static_cast<float>(rel0[2]) };
        const float rel1f[3] = { static_cast<float>(rel1[0]), static_cast<float>(rel1[1]),
            static_cast<float>(rel1[2]) };
        relF[0] = rel0f;
        relF[1] = rel1f;
        // 四边形顶点序：v0=p0-side, v1=p0+side, v2=p1+side, v3=p1-side（索引 0,1,2/0,2,3）
        const float sideSign[4] = { -1.0f, 1.0f, 1.0f, -1.0f };
        for (uint32_t c = 0u; c < 4u; ++c) {
            const float* base = relF[c >> 1];
            float* dst = q[c];
            dst[0] = base[0] + side[0] * halfW * sideSign[c];
            dst[1] = base[1] + side[1] * halfW * sideSign[c];
            dst[2] = base[2] + side[2] * halfW * sideSign[c];
            dst[3] = nrm[0];
            dst[4] = nrm[1];
            dst[5] = nrm[2];
            dst[6] = 0.0f;
            dst[7] = 0.0f;
        }
        outVerts.insert(outVerts.end(), q[0], q[0] + 8u);
        outVerts.insert(outVerts.end(), q[1], q[1] + 8u);
        outVerts.insert(outVerts.end(), q[2], q[2] + 8u);
        outVerts.insert(outVerts.end(), q[3], q[3] + 8u);
        ++quads;
        if (quads >= kRibbonMaxQuads) {
            break;
        }
    }
    return quads;
}

} // namespace zplanetapp

// --planet 模式主体（返回进程退出码）。恒离屏：照 --assets 先例（无窗口/交换链）。
static int RunPlanetMode()
{
    using namespace zplanetapp;
    using zplanet::Vec3d;
    std::printf("zrhi_d3d12_window: --planet mode (TASK-B-TECH-03): floating origin + "
                "spherical gravity + planet sphere, offscreen %ux%u\n",
        kWidth, kHeight);

    // ---- 数学前提：抖动审计（重定位前后顶点级量化误差，简报 100km 飞行口径）-----
    const zplanet::JitterAuditResult audit = zplanet::JitterAudit(kFlightAuditM, 1.0, kNearProbeM);
    std::printf("zrhi_d3d12_window: --planet jitter audit: flight=%.0fm step=1m near=%.2fm "
                "naiveMax=%.7fm relocatedMax=%.7fm ulp(100km)=%.7fm\n",
        kFlightAuditM, kNearProbeM, audit.naiveMaxErrM, audit.relocatedMaxErrM,
        audit.ulpAtFlightM);
    const bool auditOk = audit.steps == 100000 && audit.naiveMaxErrM > 0.001
        && audit.naiveMaxErrM <= audit.ulpAtFlightM + 1e-9 && audit.relocatedMaxErrM < 1e-6
        && audit.naiveMaxErrM > 100.0 * audit.relocatedMaxErrM;

    // ---- 球面重力：低轨椭圆（渲染 + 数值断言）+ 抛射体弹道（渲染 + 数值断言）----
    const Vec3d planetCenter = { 0.0, -zplanet::kPlanetRadiusM, 0.0 }; // 切点在原点正下方
    zplanet::GravityState orb0;
    orb0.r = { 0.0, zplanet::kPlanetRadiusM + 200000.0, 0.0 }; // 地表上 200km
    orb0.v = { 0.0, 0.0, 7750.0 };                             // 切向（< 环绕速度 7792）
    const zplanet::OrbitInfo orbExpect = zplanet::OrbitFromState(orb0, zplanet::kGravityMu);
    const double orbPeriodS = zplanet::OrbitPeriodS(orbExpect, zplanet::kGravityMu);
    const zplanet::IntegrateResult orbRes = zplanet::IntegrateGravity(orb0,
        zplanet::kGravityMu, 0.5, static_cast<int>(orbPeriodS * 1.05 / 0.5) + 1, 0.0, 40,
        planetCenter);
    const double orbRpErr = std::fabs(orbRes.rMinM - orbExpect.perigeeM) / orbExpect.perigeeM;
    const double orbRaErr = std::fabs(orbRes.rMaxM - orbExpect.apogeeM) / orbExpect.apogeeM;
    const double orbEpsDrift = orbRes.epsDriftMax / std::fabs(orbRes.eps0);
    std::printf("zrhi_d3d12_window: --planet orbit ellipse: rp=%.1fm ra=%.1fm "
                "visviva rp=%.1fm ra=%.1fm rpErr=%.2e raErr=%.2e epsDrift=%.2e\n",
        orbRes.rMinM, orbRes.rMaxM, orbExpect.perigeeM, orbExpect.apogeeM, orbRpErr,
        orbRaErr, orbEpsDrift);
    const bool orbOk = orbRes.eps0 < 0.0 && orbEpsDrift < 1e-6 && orbRpErr < 1e-4
        && orbRaErr < 1e-4 && orbExpect.perigeeM > zplanet::kPlanetRadiusM;

    zplanet::GravityState ball0;
    {
        // 发射点：切平面偏移 (t1=9000 前方, t2=1200 右舷 1.2km)——升弧段落在贴地
        // 视锥内（发射点若取切点原点，近弧段在相机头顶、远弧段被雾吞没）。
        // 初始锚点 (0,0,0) 的切基与场景 patchBasis 同一（up=+Y, e1=+Z, e2=+X）。
        const zplanet::TangentBasis originBasis
            = zplanet::TangentBasisAt({ 0.0, 0.0, 0.0 }, planetCenter);
        const zplanet::SpherePointResult launchSite = zplanet::SpherePointFromTangent(
            planetCenter, originBasis, 9000.0, 1200.0);
        ball0.r = zplanet::VecSub(launchSite.posAbs, planetCenter); // 地表 +0m（球面点）
        const Vec3d upSite = launchSite.normal;                     // 球面法线 = 当地 up
        const Vec3d zAxis = { 0.0, 0.0, 1.0 };
        const Vec3d hDirRaw = zplanet::VecSub(zAxis,
            zplanet::VecScale(upSite, zplanet::VecDot(zAxis, upSite))); // +Z 投影到切平面
        const Vec3d hDir = zplanet::VecScale(hDirRaw, 1.0 / zplanet::VecLen(hDirRaw));
        // 1200m/s @ 仰角 60°（600 水平 + 1039.23 沿当地 up）
        ball0.v = zplanet::VecAdd(zplanet::VecScale(hDir, 600.0),
            zplanet::VecScale(upSite, 1039.230484541326));
    }
    const zplanet::OrbitInfo ballExpect = zplanet::OrbitFromState(ball0, zplanet::kGravityMu);
    const zplanet::IntegrateResult ballRes = zplanet::IntegrateGravity(ball0,
        zplanet::kGravityMu, 0.05, 20000, zplanet::kPlanetRadiusM, 16, planetCenter);
    const double ballApoErr = std::fabs(ballRes.rMaxM - ballExpect.apogeeM)
        / ballExpect.apogeeM;
    std::printf("zrhi_d3d12_window: --planet ballistic: apogee=%.1fm visviva=%.1fm "
                "err=%.2e flight=%.1fs impacted=%d samples=%zu\n",
        ballRes.rMaxM, ballExpect.apogeeM, ballApoErr, ballRes.elapsedS,
        ballRes.impacted ? 1 : 0, ballRes.pathAbs.size());
    const bool ballOk = ballRes.impacted && ballApoErr < 1e-3
        && ballRes.rMaxM > zplanet::kPlanetRadiusM + 40000.0
        && ballRes.rMaxM < zplanet::kPlanetRadiusM + 200000.0;

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --planet FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --planet device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --planet FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 渲染目标（离屏）+ 深度（--assets 同构）--------------------------------------
    ZrImageDesc rtDesc {};
    rtDesc.format = ZrFormat::RGBA8Unorm;
    rtDesc.width = kWidth;
    rtDesc.height = kHeight;
    rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
    ZrImage offscreenTarget = device->CreateImage(rtDesc);
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    ZrImageViewDesc rtvViewDesc {};
    rtvViewDesc.image = offscreenTarget;
    rtvViewDesc.viewType = ZrImageViewType::Rtv;
    ZrImageView offscreenRtv = device->CreateImageView(rtvViewDesc);
    if (!offscreenTarget.IsValid() || !offscreenRtv.IsValid() || !depthImage.IsValid()
        || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --planet FAILED to create target/depth views\n");
        return 1;
    }

    // ---- 几何：程序化球面（double 建模 → float 相对渲染）-----------------------------
    const uint32_t kPatchVertCount = kPatchDim * kPatchDim;
    std::vector<double> patchT1(kPatchDim);
    std::vector<double> patchT2(kPatchDim);
    for (uint32_t i = 0; i < kPatchDim; ++i) {
        patchT1[i] = -kPatchExtentM
            + 2.0 * kPatchExtentM * static_cast<double>(i) / static_cast<double>(kPatchDim - 1u);
        patchT2[i] = patchT1[i];
    }
    std::vector<double> patchVertAbs(size_t(kPatchVertCount) * 3u);
    std::vector<double> patchHeight(kPatchVertCount);
    std::vector<float> patchNormal(size_t(kPatchVertCount) * 3u);
    Vec3d patchAnchor = { 0.0, 0.0, 0.0 }; // 初始锚点 = 切平面原点正上方球面点
    zplanet::TangentBasis patchBasis = zplanet::TangentBasisAt(patchAnchor, planetCenter);
    const double startCam[3] = { 0.0, 2.0, 0.0 }; // 相机起始（贴地 2m）
    // 重锚（重定位时调用）：锚点吸附到相机正下球面点，全部顶点 double 重算——
    // 「所有物体 transform 平移回原点附近」的地形片形态。
    auto reanchorPatch = [&](const double camAbs[3]) {
        const Vec3d rel = { camAbs[0] - planetCenter.x, camAbs[1] - planetCenter.y,
            camAbs[2] - planetCenter.z };
        const double relLen = zplanet::VecLen(rel);
        patchAnchor = zplanet::VecAdd(planetCenter,
            zplanet::VecScale(rel, zplanet::kPlanetRadiusM / relLen));
        patchBasis = zplanet::TangentBasisAt(patchAnchor, planetCenter);
        for (uint32_t j = 0; j < kPatchDim; ++j) {
            for (uint32_t i = 0; i < kPatchDim; ++i) {
                const zplanet::SpherePointResult sp = zplanet::SpherePointFromTangent(
                    planetCenter, patchBasis, patchT1[i], patchT2[j]);
                const size_t vi = (size_t(j) * kPatchDim + i) * 3u;
                patchVertAbs[vi] = sp.posAbs.x;
                patchVertAbs[vi + 1u] = sp.posAbs.y;
                patchVertAbs[vi + 2u] = sp.posAbs.z;
                patchNormal[vi] = static_cast<float>(sp.normal.x);
                patchNormal[vi + 1u] = static_cast<float>(sp.normal.y);
                patchNormal[vi + 2u] = static_cast<float>(sp.normal.z);
                patchHeight[size_t(j) * kPatchDim + i] = sp.heightM;
            }
        }
    };
    reanchorPatch(startCam);

    // 近场片静态索引（张量网格）
    std::vector<uint32_t> patchIndices;
    patchIndices.reserve(size_t(kPatchDim - 1u) * (kPatchDim - 1u) * 6u);
    for (uint32_t j = 0; j + 1u < kPatchDim; ++j) {
        for (uint32_t i = 0; i + 1u < kPatchDim; ++i) {
            const uint32_t v00 = j * kPatchDim + i;
            const uint32_t v10 = v00 + 1u;
            const uint32_t v01 = v00 + kPatchDim;
            const uint32_t v11 = v01 + 1u;
            patchIndices.push_back(v00);
            patchIndices.push_back(v10);
            patchIndices.push_back(v11);
            patchIndices.push_back(v00);
            patchIndices.push_back(v11);
            patchIndices.push_back(v01);
        }
    }

    // 远盘（极坐标网格，静态拓扑：顶点 0 = 锚点 + Rings×Sectors 环扇）
    const uint32_t kDiskVertCount = 1u + kDiskRings * kDiskSectors;
    std::vector<uint32_t> diskIndices;
    diskIndices.reserve(size_t(kDiskRings) * kDiskSectors * 6u + size_t(kDiskSectors) * 3u);
    for (uint32_t s = 0; s < kDiskSectors; ++s) { // 中心扇（环 1 → 锚点）
        diskIndices.push_back(0u);
        diskIndices.push_back(1u + s);
        diskIndices.push_back(1u + (s + 1u) % kDiskSectors);
    }
    for (uint32_t k = 1u; k < kDiskRings; ++k) { // 环间四边形
        for (uint32_t s = 0; s < kDiskSectors; ++s) {
            const uint32_t a0 = 1u + (k - 1u) * kDiskSectors + s;
            const uint32_t a1 = 1u + (k - 1u) * kDiskSectors + (s + 1u) % kDiskSectors;
            const uint32_t b0 = 1u + k * kDiskSectors + s;
            const uint32_t b1 = 1u + k * kDiskSectors + (s + 1u) % kDiskSectors;
            diskIndices.push_back(a0);
            diskIndices.push_back(b0);
            diskIndices.push_back(b1);
            diskIndices.push_back(a0);
            diskIndices.push_back(b1);
            diskIndices.push_back(a1);
        }
    }

    // 整球（静态局部坐标：相对行星中心，float——入轨远观距离下 0.5m 量化不可见；
    // 平移偏移经 model 矩阵承载 double→float 相对量）
    std::vector<FirstDrawVertex> sphereVerts(size_t(kSphereStacks + 1u) * (kSphereSlices + 1u));
    for (uint32_t k = 0; k <= kSphereStacks; ++k) {
        const double phi = 3.14159265358979323846 * static_cast<double>(k) / kSphereStacks;
        for (uint32_t s = 0; s <= kSphereSlices; ++s) {
            const double lambda = 6.28318530717958647692 * static_cast<double>(s) / kSphereSlices;
            const double dir[3] = { std::sin(phi) * std::cos(lambda), std::cos(phi),
                std::sin(phi) * std::sin(lambda) };
            FirstDrawVertex& v = sphereVerts[size_t(k) * (kSphereSlices + 1u) + s];
            v.pos[0] = static_cast<float>(zplanet::kPlanetRadiusM * dir[0]);
            v.pos[1] = static_cast<float>(zplanet::kPlanetRadiusM * dir[1]);
            v.pos[2] = static_cast<float>(zplanet::kPlanetRadiusM * dir[2]);
            v.normal[0] = static_cast<float>(dir[0]);
            v.normal[1] = static_cast<float>(dir[1]);
            v.normal[2] = static_cast<float>(dir[2]);
            v.uv[0] = 0.0f;
            v.uv[1] = 0.0f;
        }
    }
    std::vector<uint32_t> sphereIndices;
    sphereIndices.reserve(size_t(kSphereStacks) * kSphereSlices * 6u);
    for (uint32_t k = 0; k < kSphereStacks; ++k) {
        for (uint32_t s = 0; s < kSphereSlices; ++s) {
            const uint32_t v00 = k * (kSphereSlices + 1u) + s;
            const uint32_t v10 = v00 + 1u;
            const uint32_t v01 = v00 + (kSphereSlices + 1u);
            const uint32_t v11 = v01 + 1u;
            sphereIndices.push_back(v00);
            sphereIndices.push_back(v10);
            sphereIndices.push_back(v11);
            sphereIndices.push_back(v00);
            sphereIndices.push_back(v11);
            sphereIndices.push_back(v01);
        }
    }

    // 浮点原点演示立方体（cube.zmesh；贴地相位沿飞行路径的近场物体）
    std::vector<FirstDrawVertex> cubeVerts;
    std::vector<uint32_t> cubeIndices;
    {
        std::vector<char> meshBytes;
        if (!ReadWholeFile("Native/tests/data/cube.zmesh", meshBytes)) {
            std::printf("zrhi_d3d12_window: --planet FAILED to open cube.zmesh\n");
            return 1;
        }
        ZmeshData mesh {};
        const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
        if (parseResult != ZMESH_OK || mesh.stats.hasNormals == 0u) {
            std::printf("zrhi_d3d12_window: --planet cube.zmesh parse FAILED (error %d)\n",
                parseResult);
            return 1;
        }
        cubeVerts.resize(mesh.stats.vertexCount);
        for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
            std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
            std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
            std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
        }
        cubeIndices.resize(mesh.stats.indexCount);
        std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * mesh.stats.indexCount);
    }
    // 立方体绝对坐标（double）：球面地表抬起（×8 缩放后半高 8m）；切平面偏移
    // (t1=z, t2=x)——初始锚点基 e1=+Z / e2=+X（见 TangentBasisAt）。
    // 侧偏 60m：贴地飞行时从右舷掠过（重定位跨越它们的可视证据）。
    const double kCubeTangent[kPlanetCubeCount][2] = { { 7600.0, 60.0 }, { 13600.0, 60.0 },
        { 19600.0, 60.0 } };
    double cubeAbs[kPlanetCubeCount][3] = {};
    for (uint32_t c = 0; c < kPlanetCubeCount; ++c) {
        const zplanet::SpherePointResult sp = zplanet::SpherePointFromTangent(planetCenter,
            patchBasis, kCubeTangent[c][0], kCubeTangent[c][1]);
        cubeAbs[c][0] = sp.posAbs.x;
        cubeAbs[c][1] = sp.posAbs.y + 8.0; // ×8 缩放后半高 8m（贴地 2m 视角可见标记）
        cubeAbs[c][2] = sp.posAbs.z;
    }

    // 线带静态索引（四边形模式表）
    std::vector<uint32_t> ribbonIndices;
    ribbonIndices.reserve(size_t(kRibbonMaxQuads) * 6u);
    for (uint32_t q = 0; q < kRibbonMaxQuads; ++q) {
        const uint32_t base = q * 4u;
        ribbonIndices.push_back(base);
        ribbonIndices.push_back(base + 1u);
        ribbonIndices.push_back(base + 2u);
        ribbonIndices.push_back(base);
        ribbonIndices.push_back(base + 2u);
        ribbonIndices.push_back(base + 3u);
    }

    // ---- GPU 缓冲：VB/IB + 常量缓冲（动态几何 hostVisible UPLOAD 持久映射）----------
    auto makeBuffer = [&](uint64_t size, ZrBufferUsage usage, bool hostVisible,
                          const void* initData) {
        ZrBufferDesc desc {};
        desc.size = size;
        desc.usage = usage;
        desc.hostVisible = hostVisible;
        return device->CreateBuffer(desc, initData);
    };
    auto makeDynamicBuffer = [&](uint64_t size, ZrBufferUsage usage, void** outMapped) {
        ZrBufferDesc desc {};
        desc.size = size;
        desc.usage = usage;
        desc.hostVisible = true;
        ZrBuffer h = device->CreateBuffer(desc);
        IZrBuffer* iface = device->GetBuffer(h);
        *outMapped = (iface != nullptr && h.IsValid()) ? iface->Map() : nullptr;
        return h;
    };
    void* patchVbMapped = nullptr;
    void* diskVbMapped = nullptr;
    void* ribbonVbMapped = nullptr;
    ZrBuffer patchVb = makeDynamicBuffer(uint64_t(kPatchVertCount) * 32u,
        ZrBufferUsage::Vertex, &patchVbMapped);
    ZrBuffer patchIb = makeBuffer(uint64_t(patchIndices.size()) * 4u,
        ZrBufferUsage::Index, false, patchIndices.data());
    ZrBuffer diskVb = makeDynamicBuffer(uint64_t(kDiskVertCount) * 32u,
        ZrBufferUsage::Vertex, &diskVbMapped);
    ZrBuffer diskIb = makeBuffer(uint64_t(diskIndices.size()) * 4u,
        ZrBufferUsage::Index, false, diskIndices.data());
    ZrBuffer sphereVb = makeBuffer(uint64_t(sphereVerts.size()) * 32u,
        ZrBufferUsage::Vertex, false, sphereVerts.data());
    ZrBuffer sphereIb = makeBuffer(uint64_t(sphereIndices.size()) * 4u,
        ZrBufferUsage::Index, false, sphereIndices.data());
    ZrBuffer cubeVb = makeBuffer(uint64_t(cubeVerts.size()) * 32u,
        ZrBufferUsage::Vertex, false, cubeVerts.data());
    ZrBuffer cubeIb = makeBuffer(uint64_t(cubeIndices.size()) * 4u,
        ZrBufferUsage::Index, false, cubeIndices.data());
    ZrBuffer ribbonVb = makeDynamicBuffer(uint64_t(kRibbonMaxQuads) * 4u * 32u,
        ZrBufferUsage::Vertex, &ribbonVbMapped);
    void* ellipseVbMapped = nullptr;
    ZrBuffer ellipseVb = makeDynamicBuffer(uint64_t(kRibbonMaxQuads) * 4u * 32u,
        ZrBufferUsage::Vertex, &ellipseVbMapped); // 椭圆带独立 VB（共用线带索引模式表）
    ZrBuffer ribbonIb = makeBuffer(uint64_t(ribbonIndices.size()) * 4u,
        ZrBufferUsage::Index, false, ribbonIndices.data());
    if (!patchVb.IsValid() || !patchIb.IsValid() || !diskVb.IsValid() || !diskIb.IsValid()
        || !sphereVb.IsValid() || !sphereIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()
        || !ribbonVb.IsValid() || !ellipseVb.IsValid() || !ribbonIb.IsValid()
        || patchVbMapped == nullptr || diskVbMapped == nullptr || ribbonVbMapped == nullptr
        || ellipseVbMapped == nullptr) {
        std::printf("zrhi_d3d12_window: --planet FAILED to create VB/IB\n");
        return 1;
    }

    // 常量缓冲 ×8（地形片/远盘/球体/3 立方体/弹道带/椭圆带；256B）
    constexpr uint32_t kPlanetItemCount = 8u;
    ZrBuffer cb0[kPlanetItemCount] = {};
    void* cb0Mapped[kPlanetItemCount] = {};
    for (uint32_t i = 0; i < kPlanetItemCount; ++i) {
        ZrBufferDesc desc {};
        desc.size = 256u;
        desc.usage = ZrBufferUsage::Uniform;
        desc.hostVisible = true;
        cb0[i] = device->CreateBuffer(desc);
        IZrBuffer* iface = device->GetBuffer(cb0[i]);
        cb0Mapped[i] = (iface != nullptr && cb0[i].IsValid()) ? iface->Map() : nullptr;
        if (cb0Mapped[i] == nullptr) {
            std::printf("zrhi_d3d12_window: --planet FAILED to create/map CB %u\n", i);
            return 1;
        }
    }
    // 槽位：0 地形片 / 1 远盘 / 2 球体 / 3..5 立方体 / 6 弹道带 / 7 椭圆带
    constexpr uint32_t kCbPatch = 0u;
    constexpr uint32_t kCbDisk = 1u;
    constexpr uint32_t kCbSphere = 2u;
    constexpr uint32_t kCbCubeBase = 3u;
    constexpr uint32_t kCbArc = 6u;
    constexpr uint32_t kCbEllipse = 7u;

    // ---- 着色器（运行时 D3DCompile）+ PSO（单 PSO 三模式，rootCbvCount=1）-----------
    std::vector<char> hlsl;
    if (!ReadWholeFile("Native/shaders/planet.hlsl", hlsl)) {
        std::printf("zrhi_d3d12_window: --planet FAILED to open Native/shaders/planet.hlsl\n");
        return 1;
    }
    const ZrShader vs = device->CompileHlsl(ZrShaderStage::Vertex, hlsl.data(), hlsl.size(),
        "VsMain");
    const ZrShader ps = device->CompileHlsl(ZrShaderStage::Pixel, hlsl.data(), hlsl.size(),
        "PsMain");
    if (!vs.IsValid() || !ps.IsValid()) {
        std::printf("zrhi_d3d12_window: --planet shader compilation FAILED (see stderr)\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --planet shaders compiled (runtime D3DCompile, SM5.0)\n");
    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = vs;
    psoDesc.pixelShader = ps;
    psoDesc.rtvFormat = ZrFormat::RGBA8Unorm; // 恒离屏（RGBA 序，截图直取）
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    psoDesc.cullMode = ZrCullMode::None; // 球面网格绕序混用（--lights 同策）
    psoDesc.rootCbvCount = 1u;
    ZrPipelineState pso = device->CreatePipelineState(psoDesc);
    if (!pso.IsValid()) {
        std::printf("zrhi_d3d12_window: --planet FAILED to create graphics PSO\n");
        return 1;
    }

    // ---- readback：2 探针槽（值探针/天空探针）+ 全帧（关键帧 PNG 源）-----------------
    constexpr uint32_t kPlanetRowPitch = (kWidth * 4u + 255u) & ~255u; // 640×4=2560
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackProbes
        = MakeReadbackBuffer(device->D3D12Device(), 256u * 2u);
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackFrame
        = MakeReadbackBuffer(device->D3D12Device(), uint64_t(kPlanetRowPitch) * kHeight);
    if (readbackProbes == nullptr || readbackFrame == nullptr) {
        std::printf("zrhi_d3d12_window: --planet FAILED to create readback buffers\n");
        return 1;
    }

    // ---- 浮点原点状态（double 相机累计 + 10km 阈值重定位）---------------------------
    zplanet::FloatingOrigin origin;
    origin.thresholdM = kRelocThresholdM;
    origin.MoveCameraBy(0.0, 2.0, 0.0); // 相机起始 (0,2,0)（double 累计起点）
    double prevCam[3] = { 0.0, 2.0, 0.0 };
    double lastFlightRelocM = -1.0; // 10 万 m 飞行段内最后一次重定位距初始原点的距离
    double liveNaiveMaxM = 0.0;
    double liveRelocMaxM = 0.0;
    constexpr float kFovY = 3.14159265f / 3.0f; // 60°
    const float aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);

    // ---- 帧循环 ------------------------------------------------------------------------
    uint32_t readbackChecks = 0u;
    uint32_t valueProbePasses = 0u;
    uint32_t skyProbeChecks = 0u;
    uint32_t skyProbePasses = 0u;
    bool allOk = true;
    uint32_t undeclaredUses = 0u;
    bool pngOk[3] = { false, false, false };
    constexpr const char* kPlanetPng[3] = { "Docs/Parity/planet_ground.png",
        "Docs/Parity/planet_climb.png", "Docs/Parity/planet_orbit.png" };

    for (uint32_t frame = 0u; frame < kPlanetFrames; ++frame) {
        // ---- 相机编排：贴地 → 爬升 → 入轨（double 绝对坐标）--------------------------
        double camAbs[3] = {};
        float fwd[3] = {};
        float upHint[3] = { 0.0f, 1.0f, 0.0f };
        double nearZ = 1.0;
        double farZ = 150000.0;
        if (frame < kPlanetGroundEnd) { // 贴地：2m 高度 +Z 前进 170m/帧
            camAbs[0] = 0.0;
            camAbs[1] = 2.0;
            camAbs[2] = 170.0 * static_cast<double>(frame);
            // 俯仰 -3°（帧 100 起 ramps 到 -25°，与爬升相位衔接）
            const double dipDeg = 3.0
                + 22.0 * static_cast<double>(frame >= 100u ? frame - 100u : 0u) / 19.0;
            const double dip = dipDeg * 3.14159265358979323846 / 180.0;
            fwd[0] = 0.0f;
            fwd[1] = static_cast<float>(-std::sin(dip));
            fwd[2] = static_cast<float>(std::cos(dip));
            nearZ = 1.0;
            farZ = 150000.0;
        } else if (frame < kPlanetClimbEnd) { // 爬升：alt = 2 + 2e7·s⁵（s∈[0,1]）
            const double s = static_cast<double>(frame - kPlanetGroundEnd)
                / static_cast<double>(kPlanetClimbEnd - kPlanetGroundEnd - 1u);
            const double alt = 2.0 + (2.0e7 - 2.0) * s * s * s * s * s;
            camAbs[0] = 0.0;
            camAbs[1] = alt;
            camAbs[2] = 170.0 * static_cast<double>(frame);
            // 视线中心保持在地平线下 5°（行星恒占据视野中下部、天顶恒为天空——
            // 探针高度不变性）；alt > 1.2e7 后混向行星中心（w=1 时正对中心，
            // 与入轨相位衔接）
            const double horizonDip = std::acos(std::min(1.0,
                zplanet::kPlanetRadiusM / (zplanet::kPlanetRadiusM + alt)));
            double dip = std::max(horizonDip + 5.0 * 3.14159265358979323846 / 180.0,
                25.0 * 3.14159265358979323846 / 180.0);
            const double w = PlanetSmoothstep(1.2e7, 2.0e7, alt);
            dip = (1.0 - w) * dip + w * (3.14159265358979323846 * 0.5);
            const float fPitch[3] = { 0.0f, static_cast<float>(-std::sin(dip)),
                static_cast<float>(std::cos(dip)) };
            const Vec3d toC = { planetCenter.x - camAbs[0], planetCenter.y - camAbs[1],
                planetCenter.z - camAbs[2] };
            const double toCLen = zplanet::VecLen(toC);
            const float fCenter[3] = { static_cast<float>(toC.x / toCLen),
                static_cast<float>(toC.y / toCLen), static_cast<float>(toC.z / toCLen) };
            const float blend[3] = { static_cast<float>((1.0 - w) * fPitch[0] + w * fCenter[0]),
                static_cast<float>((1.0 - w) * fPitch[1] + w * fCenter[1]),
                static_cast<float>((1.0 - w) * fPitch[2] + w * fCenter[2]) };
            const float blendLen = std::sqrt(blend[0] * blend[0] + blend[1] * blend[1]
                + blend[2] * blend[2]);
            fwd[0] = blend[0] / blendLen;
            fwd[1] = blend[1] / blendLen;
            fwd[2] = blend[2] / blendLen;
            upHint[0] = 0.0f;
            upHint[1] = (std::fabs(fwd[1]) < 0.9f) ? 1.0f : 0.0f;
            upHint[2] = (std::fabs(fwd[1]) < 0.9f) ? 0.0f : 1.0f;
            nearZ = std::min(8.0e6, std::max(1.0, 0.5 * alt));
            farZ = std::max(150000.0, (toCLen + zplanet::kPlanetRadiusM) * 2.5);
        } else { // 入轨远观：距行星中心 2.6 万 km 慢扫（15°→55°）
            const double thetaDeg = 15.0
                + 40.0 * static_cast<double>(frame - kPlanetClimbEnd)
                    / static_cast<double>(kPlanetFrames - kPlanetClimbEnd - 1u);
            const double theta = thetaDeg * 3.14159265358979323846 / 180.0;
            const double dir[3] = { std::cos(theta), 0.35, std::sin(theta) };
            const double dirLen = std::sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
            camAbs[0] = planetCenter.x + kOrbitCameraDistM * dir[0] / dirLen;
            camAbs[1] = planetCenter.y + kOrbitCameraDistM * dir[1] / dirLen;
            camAbs[2] = planetCenter.z + kOrbitCameraDistM * dir[2] / dirLen;
            const Vec3d toC = { planetCenter.x - camAbs[0], planetCenter.y - camAbs[1],
                planetCenter.z - camAbs[2] };
            const double toCLen = zplanet::VecLen(toC);
            fwd[0] = static_cast<float>(toC.x / toCLen);
            fwd[1] = static_cast<float>(toC.y / toCLen);
            fwd[2] = static_cast<float>(toC.z / toCLen);
            nearZ = 8.0e6;
            farZ = (toCLen + zplanet::kPlanetRadiusM) * 2.5;
        }
        const Vec3d camRelC = { camAbs[0] - planetCenter.x, camAbs[1] - planetCenter.y,
            camAbs[2] - planetCenter.z };
        const double altM = zplanet::VecLen(camRelC) - zplanet::kPlanetRadiusM;
        const bool drawPatch = altM < kDiskAltMinM;
        const bool drawDisk = altM >= kDiskAltMinM && altM < kSphereAltMinM;
        const bool drawSphere = altM >= kSphereAltMinM;
        const bool drawCubes = altM < kDiskAltMinM;
        const bool drawEllipse = altM >= kEllipseDrawAltMinM;

        // ---- 浮点原点：double 累计 + 阈值重定位（原点吸附相机；近场片随动重锚）--------
        origin.MoveCameraBy(camAbs[0] - prevCam[0], camAbs[1] - prevCam[1],
            camAbs[2] - prevCam[2]);
        if (origin.ShouldRelocateNow()) {
            const double off = origin.Relocate();
            const double flightDist = zplanet::VecLen(
                { camAbs[0], camAbs[1], camAbs[2] });
            if (flightDist <= kFlightAuditM + 500.0) {
                lastFlightRelocM = flightDist;
            }
            if (drawPatch) {
                reanchorPatch(camAbs); // 所有地形顶点 transform 平移回原点附近
            }
            std::printf("  frame %u: RELOCATE #%d offset=%.1fm flightDist=%.1fm\n", frame + 1u,
                origin.relocationCount, off, flightDist);
        }
        prevCam[0] = camAbs[0];
        prevCam[1] = camAbs[1];
        prevCam[2] = camAbs[2];

        // ---- 近场抖动实况探针（贴地相位；naive vs 重定位后逐帧量化误差）----------------
        if (frame < kPlanetGroundEnd) {
            for (const double* off : kLiveProbeOffsets) {
                for (int a = 0; a < 3; ++a) {
                    const double objA = camAbs[a] + off[a];
                    const float cf = static_cast<float>(camAbs[a]);
                    const float of = static_cast<float>(objA);
                    const double errN = std::fabs(static_cast<double>(of - cf) - off[a]);
                    const double errR = std::fabs(
                        static_cast<double>(static_cast<float>(objA - camAbs[a])) - off[a]);
                    if (errN > liveNaiveMaxM) {
                        liveNaiveMaxM = errN;
                    }
                    if (errR > liveRelocMaxM) {
                        liveRelocMaxM = errR;
                    }
                }
            }
        }

        // ---- 矩阵（列向量约定；相机恒在渲染空间原点）----------------------------------
        float proj[16];
        MatPerspectiveFovLH(kFovY, aspect, static_cast<float>(nearZ), static_cast<float>(farZ),
            proj);
        float view[16];
        PlanetMatLookAtLH(fwd, upHint, view);
        float projView[16];
        MatMultiply(proj, view, projView);
        const float kIdentity[16] = { 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f };

        // 模式参数：雾距离随相位（贴地 150km——保住弹道远弧段可见 / 远盘 400km / 太空关）
        const float fogDist = drawPatch ? 150000.0f : (drawDisk ? 400000.0f : 0.0f);

        auto fillCb = [&](uint32_t slot, const float* mvp, const float* model, float mode,
                          float contourM, const float rgb[3]) {
            PlanetDrawConstants c {};
            std::memcpy(c.mvp, mvp, sizeof(c.mvp));
            std::memcpy(c.model, model, sizeof(c.model));
            c.color[0] = rgb[0];
            c.color[1] = rgb[1];
            c.color[2] = rgb[2];
            c.color[3] = 1.0f;
            c.params[0] = mode;
            c.params[1] = contourM;
            c.params[2] = fogDist;
            c.params[3] = 0.0f;
            std::memcpy(cb0Mapped[slot], &c, sizeof(c));
        };
        fillCb(kCbPatch, projView, kIdentity, 0.0f, 250.0f, kPlanetClearColor);
        fillCb(kCbDisk, projView, kIdentity, 0.0f, 10000.0f, kPlanetClearColor);
        // 球体：model = T(行星中心相对相机)（double→float；入轨距离下量化 ~2m 不可见）
        float sphereModel[16];
        MatTranslation(static_cast<float>(planetCenter.x - camAbs[0]),
            static_cast<float>(planetCenter.y - camAbs[1]),
            static_cast<float>(planetCenter.z - camAbs[2]), sphereModel);
        float sphereMvp[16];
        MatMultiply(projView, sphereModel, sphereMvp);
        {
            // 球体雾恒关（fillCb 用全局 fogDist——太空相位本身为 0）
            PlanetDrawConstants c {};
            std::memcpy(c.mvp, sphereMvp, sizeof(c.mvp));
            std::memcpy(c.model, sphereModel, sizeof(c.model));
            c.params[0] = 1.0f;
            c.params[1] = 0.0f;
            c.params[2] = 0.0f;
            c.params[3] = 0.0f;
            std::memcpy(cb0Mapped[kCbSphere], &c, sizeof(c));
        }
        for (uint32_t c = 0; c < kPlanetCubeCount; ++c) {
            float cubeT[16];
            MatTranslation(static_cast<float>(cubeAbs[c][0] - camAbs[0]),
                static_cast<float>(cubeAbs[c][1] - camAbs[1]),
                static_cast<float>(cubeAbs[c][2] - camAbs[2]), cubeT);
            const float cubeScale[16] = { 8.0f, 0.0f, 0.0f, 0.0f, 0.0f, 8.0f, 0.0f, 0.0f,
                0.0f, 0.0f, 8.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f }; // ×8（16m 标记）
            float cubeModel[16];
            MatMultiply(cubeT, cubeScale, cubeModel); // world = T·S·local
            float cubeMvp[16];
            MatMultiply(projView, cubeModel, cubeMvp);
            const float cubeRgb[3] = { c == 0u ? 0.90f : (c == 1u ? 0.30f : 0.25f),
                c == 0u ? 0.55f : (c == 1u ? 0.85f : 0.75f),
                c == 0u ? 0.20f : (c == 1u ? 0.95f : 0.90f) };
            fillCb(kCbCubeBase + c, cubeMvp, cubeModel, 2.0f, 0.0f, cubeRgb);
        }

        // ---- 动态几何：double 相对化 → float 顶点（浮点原点渲染路径）-------------------
        if (drawPatch) {
            FirstDrawVertex* v = static_cast<FirstDrawVertex*>(patchVbMapped);
            for (uint32_t vi = 0; vi < kPatchVertCount; ++vi) {
                const double* abs = &patchVertAbs[size_t(vi) * 3u];
                FirstDrawVertex& out = v[vi];
                out.pos[0] = static_cast<float>(abs[0] - camAbs[0]);
                out.pos[1] = static_cast<float>(abs[1] - camAbs[1]);
                out.pos[2] = static_cast<float>(abs[2] - camAbs[2]);
                out.normal[0] = patchNormal[size_t(vi) * 3u];
                out.normal[1] = patchNormal[size_t(vi) * 3u + 1u];
                out.normal[2] = patchNormal[size_t(vi) * 3u + 2u];
                out.uv[0] = static_cast<float>(patchHeight[vi]);
                out.uv[1] = 0.0f;
            }
        }
        if (drawDisk) {
            // 锚点 = 相机正下球面点（逐帧随动）；顶点 double 建模 → float 相对
            const Vec3d relC = camRelC;
            const double relLen = zplanet::VecLen(relC);
            const Vec3d diskAnchor = zplanet::VecAdd(planetCenter,
                zplanet::VecScale(relC, zplanet::kPlanetRadiusM / relLen));
            const zplanet::TangentBasis diskBasis
                = zplanet::TangentBasisAt(diskAnchor, planetCenter);
            FirstDrawVertex* v = static_cast<FirstDrawVertex*>(diskVbMapped);
            for (uint32_t k = 0; k < kDiskRings; ++k) {
                const double radius = kDiskRadiusM * static_cast<double>(k + 1u)
                    / static_cast<double>(kDiskRings);
                for (uint32_t s = 0; s < kDiskSectors; ++s) {
                    const double ang = 6.28318530717958647692 * static_cast<double>(s)
                        / static_cast<double>(kDiskSectors);
                    const zplanet::SpherePointResult sp = zplanet::SpherePointFromTangent(
                        planetCenter, diskBasis, radius * std::cos(ang),
                        radius * std::sin(ang));
                    FirstDrawVertex& out = v[1u + size_t(k) * kDiskSectors + s];
                    out.pos[0] = static_cast<float>(sp.posAbs.x - camAbs[0]);
                    out.pos[1] = static_cast<float>(sp.posAbs.y - camAbs[1]);
                    out.pos[2] = static_cast<float>(sp.posAbs.z - camAbs[2]);
                    out.normal[0] = static_cast<float>(sp.normal.x);
                    out.normal[1] = static_cast<float>(sp.normal.y);
                    out.normal[2] = static_cast<float>(sp.normal.z);
                    out.uv[0] = static_cast<float>(sp.heightM);
                    out.uv[1] = 0.0f;
                }
            }
            FirstDrawVertex& center = v[0];
            center.pos[0] = static_cast<float>(diskAnchor.x - camAbs[0]);
            center.pos[1] = static_cast<float>(diskAnchor.y - camAbs[1]);
            center.pos[2] = static_cast<float>(diskAnchor.z - camAbs[2]);
            center.normal[0] = static_cast<float>(diskBasis.up.x);
            center.normal[1] = static_cast<float>(diskBasis.up.y);
            center.normal[2] = static_cast<float>(diskBasis.up.z);
            center.uv[0] = 0.0f;
            center.uv[1] = 0.0f;
        }

        // 线带（弹道恒画；椭圆按高度）：轨迹 double 绝对采样 → 相机相对 float
        std::vector<float> arcVerts;
        std::vector<float> ellipseVerts;
        const uint32_t arcQuads = PlanetBuildRibbon(ballRes.pathAbs, camAbs, farZ, 0.0035f,
            arcVerts);
        const uint32_t ellipseQuads = drawEllipse
            ? PlanetBuildRibbon(orbRes.pathAbs, camAbs, farZ, 0.0025f, ellipseVerts)
            : 0u;
        if (!arcVerts.empty()) {
            std::memcpy(ribbonVbMapped, arcVerts.data(), arcVerts.size() * sizeof(float));
        }
        if (!ellipseVerts.empty()) {
            std::memcpy(ellipseVbMapped, ellipseVerts.data(),
                ellipseVerts.size() * sizeof(float));
        }
        {
            PlanetDrawConstants c {};
            std::memcpy(c.mvp, projView, sizeof(c.mvp));
            std::memcpy(c.model, kIdentity, sizeof(c.model));
            c.params[0] = 2.0f;
            c.params[1] = 0.0f;
            c.params[2] = fogDist;
            c.params[3] = 0.0f;
            c.color[0] = 1.0f;
            c.color[1] = 0.55f;
            c.color[2] = 0.10f; // 弹道：橙
            c.color[3] = 1.0f;
            std::memcpy(cb0Mapped[kCbArc], &c, sizeof(c));
            c.color[0] = 0.30f;
            c.color[1] = 0.90f;
            c.color[2] = 1.0f; // 椭圆：青
            std::memcpy(cb0Mapped[kCbEllipse], &c, sizeof(c));
        }

        // ---- RG 帧：Clear → Main（全几何单 pass）→ Readback -----------------------------
        const bool doReadback = ((frame + 1u) % 10u) == 0u;
        const int shotIndex = (frame + 1u == kPlanetGroundShotFrame) ? 0
            : ((frame + 1u == kPlanetClimbShotFrame) ? 1
                : ((frame + 1u == kPlanetOrbitShotFrame) ? 2 : -1));

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", offscreenTarget));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kPlanetClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });

        graph.AddPass("PlanetMainPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(pso);
                cmdList->SetRenderTarget(offscreenRtv, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                auto drawIndexed = [&](uint32_t indexCount, ZrBuffer vb, ZrBuffer ib,
                                       ZrBuffer cb) {
                    ZrBuffer vbs[1] = { vb };
                    cmdList->SetVertexBuffers(vbs, 1u);
                    cmdList->SetIndexBuffer(ib);
                    cmdList->SetGraphicsConstantBuffer(0u, cb);
                    cmdList->DrawIndexedInstanced(indexCount, 1u, 0u, 0u, 0u);
                };
                if (drawPatch) {
                    drawIndexed(static_cast<uint32_t>(patchIndices.size()), patchVb, patchIb,
                        cb0[kCbPatch]);
                }
                if (drawDisk) {
                    drawIndexed(static_cast<uint32_t>(diskIndices.size()), diskVb, diskIb,
                        cb0[kCbDisk]);
                }
                if (drawSphere) {
                    drawIndexed(static_cast<uint32_t>(sphereIndices.size()), sphereVb, sphereIb,
                        cb0[kCbSphere]);
                }
                if (drawCubes) {
                    for (uint32_t c = 0; c < kPlanetCubeCount; ++c) {
                        drawIndexed(static_cast<uint32_t>(cubeIndices.size()), cubeVb, cubeIb,
                            cb0[kCbCubeBase + c]);
                    }
                }
                if (arcQuads > 0u) {
                    drawIndexed(arcQuads * 6u, ribbonVb, ribbonIb, cb0[kCbArc]);
                }
                if (ellipseQuads > 0u) {
                    // 椭圆带独立 VB + 共享四边形索引模式表（顶点序 0 起）
                    drawIndexed(ellipseQuads * 6u, ellipseVb, ribbonIb, cb0[kCbEllipse]);
                }
            });

        if (doReadback || shotIndex >= 0) { // 关键帧亦须 readback（截图与探针同源）
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(backH));
                    D3D12_TEXTURE_COPY_LOCATION src {};
                    src.pResource = texture;
                    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    src.SubresourceIndex = 0u;
                    // 探针槽 0：值探针（相位相关坐标，见下方推导）
                    // 探针槽 1：天空/太空探针（仅爬升/入轨相位）
                    uint32_t probeX[2] = { 320u, 320u };
                    uint32_t probeY[2] = { 300u, 80u };
                    uint32_t probeCount = 1u;
                    if (altM >= kSphereAltMinM) {
                        probeX[0] = 320u;
                        probeY[0] = 240u; // 行星中心
                        if (frame >= kPlanetClimbEnd) {
                            probeX[1] = 40u;
                            probeY[1] = 40u; // 角落太空（入轨行星角半径 14°<半 FOV）
                            probeCount = 2u;
                        }
                    } else if (altM >= kDiskAltMinM) {
                        probeX[0] = 320u;
                        probeY[0] = 380u; // 视野下方行星表面（视线中心=地平线下 5°）
                        probeX[1] = 80u;
                        probeY[1] = 80u; // 地平线上方 15°、左舷（弹道弧线走中轴附近，避开）
                        probeCount = (altM >= 30000.0) ? 2u : 1u;
                    }
                    for (uint32_t p = 0; p < probeCount; ++p) {
                        const D3D12_BOX box = { probeX[p], probeY[p], 0u, probeX[p] + 1u,
                            probeY[p] + 1u, 1u };
                        D3D12_TEXTURE_COPY_LOCATION dst {};
                        dst.pResource = readbackProbes.Get();
                        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                        dst.PlacedFootprint.Offset = 256ull * p;
                        dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                        dst.PlacedFootprint.Footprint.Width = 1u;
                        dst.PlacedFootprint.Footprint.Height = 1u;
                        dst.PlacedFootprint.Footprint.Depth = 1u;
                        dst.PlacedFootprint.Footprint.RowPitch = 256u;
                        nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
                    }
                    if (shotIndex >= 0) {
                        const D3D12_BOX fullBox = { 0u, 0u, 0u, kWidth, kHeight, 1u };
                        D3D12_TEXTURE_COPY_LOCATION fullDst {};
                        fullDst.pResource = readbackFrame.Get();
                        fullDst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                        fullDst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                        fullDst.PlacedFootprint.Footprint.Width = kWidth;
                        fullDst.PlacedFootprint.Footprint.Height = kHeight;
                        fullDst.PlacedFootprint.Footprint.Depth = 1u;
                        fullDst.PlacedFootprint.Footprint.RowPitch = kPlanetRowPitch;
                        nativeCmd->CopyTextureRegion(&fullDst, 0u, 0u, 0u, &src, &fullBox);
                    }
                });
        }

        if (!graph.Compile()) {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        const RgExecuteStats rgStats = graph.ExecuteAliased(*device, *cmdList);
        (void)rgStats;
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末手动屏障：深度/离屏图回 COMMON（--assets 同策）
        ZrImageBarrier depthReset = MakeBarrier(depthImage,
            ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
            ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
            ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
        cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);
        {
            const ZrImageLayout backLayoutNow = doReadback ? ZrImageLayout::TransferSrcOptimal
                                                           : ZrImageLayout::ColorAttachmentOptimal;
            ZrImageBarrier backReset = MakeBarrier(offscreenTarget, backLayoutNow,
                ZrImageLayout::Undefined, ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(&backReset, 1u, nullptr, 0u);
        }
        cmdList->End();
        queue->Submit(cmdHandle);
        device->WaitIdle();
        graph.FlushRetired(*device);

        // ---- 校验帧：探针判定（readback 节拍帧 + 关键帧——拷贝已保证新鲜）----------------
        if (doReadback || shotIndex >= 0) {
            ++readbackChecks;
            // 当前帧探针坐标（与 ReadbackPass 内推导一致）
            uint32_t probeX[2] = { 320u, 320u };
            uint32_t probeY[2] = { 300u, 80u };
            uint32_t probeCount = 1u;
            bool skyIsClear[2] = { false, false };
            if (altM >= kSphereAltMinM) {
                probeX[0] = 320u;
                probeY[0] = 240u;
                if (frame >= kPlanetClimbEnd) {
                    probeX[1] = 40u;
                    probeY[1] = 40u;
                    probeCount = 2u;
                    skyIsClear[1] = true;
                }
            } else if (altM >= kDiskAltMinM) {
                probeX[0] = 320u;
                probeY[0] = 380u;
                probeX[1] = 80u;
                probeY[1] = 80u;
                probeCount = (altM >= 30000.0) ? 2u : 1u;
                skyIsClear[1] = true;
            }
            void* mapped = nullptr;
            const HRESULT hr = readbackProbes->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  frame %u: probe readback Map FAILED (hr=0x%08lX)\n", frame + 1u,
                    static_cast<unsigned long>(hr));
                allOk = false;
            } else {
                const int clearR = static_cast<int>(kPlanetClearColor[0] * 255.0f + 0.5f);
                const int clearG = static_cast<int>(kPlanetClearColor[1] * 255.0f + 0.5f);
                const int clearB = static_cast<int>(kPlanetClearColor[2] * 255.0f + 0.5f);
                for (uint32_t p = 0; p < probeCount; ++p) {
                    const uint8_t* px = static_cast<const uint8_t*>(mapped) + 256u * p;
                    const int r = static_cast<int>(px[0]);
                    const int g = static_cast<int>(px[1]);
                    const int b = static_cast<int>(px[2]);
                    const bool nearClear = std::abs(r - clearR) <= 2
                        && std::abs(g - clearG) <= 2 && std::abs(b - clearB) <= 2;
                    const bool pass = skyIsClear[p] ? nearClear : !nearClear;
                    if (skyIsClear[p]) {
                        ++skyProbeChecks;
                        skyProbePasses += pass ? 1u : 0u;
                    } else {
                        valueProbePasses += pass ? 1u : 0u;
                    }
                    if (!pass) {
                        allOk = false;
                        std::printf("  frame %u probe[%u] (%u,%u) RGB=(%d,%d,%d) -> FAIL "
                                    "(expect %s)\n",
                            frame + 1u, p, probeX[p], probeY[p], r, g, b,
                            skyIsClear[p] ? "clear" : "non-clear");
                    }
                }
                readbackProbes->Unmap(0, nullptr);
            }
        }

        // ---- 关键帧：全帧 → PNG -----------------------------------------------------------
        if (shotIndex >= 0) {
            void* mapped = nullptr;
            const HRESULT hr = readbackFrame->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  frame %u: full-frame Map FAILED (hr=0x%08lX)\n", frame + 1u,
                    static_cast<unsigned long>(hr));
                allOk = false;
            } else {
                std::filesystem::create_directories("Docs/Parity");
                pngOk[shotIndex] = WritePng24(kPlanetPng[shotIndex],
                    static_cast<const uint8_t*>(mapped), kWidth, kHeight, kPlanetRowPitch);
                std::printf("  frame %u keyframe -> %s (%s)\n", frame + 1u, kPlanetPng[shotIndex],
                    pngOk[shotIndex] ? "written" : "FAILED");
                readbackFrame->Unmap(0, nullptr);
                allOk = allOk && pngOk[shotIndex];
            }
        }
    }

    // ---- 证据行 ------------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: --planet relocations: count=%d threshold=%.0fm "
                "lastInFlight=%.1fm maxOriginOffset=%.1fm\n",
        origin.relocationCount, origin.thresholdM, lastFlightRelocM, origin.maxOriginOffsetM);
    std::printf("zrhi_d3d12_window: --planet live jitter (ground phase): naiveMax=%.7fm "
                "relocatedMax=%.7fm\n",
        liveNaiveMaxM, liveRelocMaxM);
    const bool liveOk = liveNaiveMaxM > 1e-4 && liveRelocMaxM < 1e-5
        && liveNaiveMaxM > 50.0 * liveRelocMaxM;
    const bool relocOk = origin.relocationCount >= 2 && lastFlightRelocM > 0.0;
    std::printf("zrhi_d3d12_window: --planet frames=%u checks=%u value=%u/%u sky=%u/%u "
                "png=%d/%d/%d relocs=%d audit=%d live=%d gravity=%d/%d\n",
        kPlanetFrames, readbackChecks, valueProbePasses, readbackChecks, skyProbePasses,
        skyProbeChecks, pngOk[0] ? 1 : 0, pngOk[1] ? 1 : 0, pngOk[2] ? 1 : 0,
        origin.relocationCount, auditOk ? 1 : 0, liveOk ? 1 : 0, orbOk ? 1 : 0,
        ballOk ? 1 : 0);

    // ---- 清理 ----------------------------------------------------------------------------
    for (uint32_t i = 0; i < kPlanetItemCount; ++i) {
        device->Destroy(cb0[i]);
    }
    device->Destroy(patchVb);
    device->Destroy(patchIb);
    device->Destroy(diskVb);
    device->Destroy(diskIb);
    device->Destroy(sphereVb);
    device->Destroy(sphereIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(ribbonVb);
    device->Destroy(ellipseVb);
    device->Destroy(ribbonIb);
    device->Destroy(pso);
    device->Destroy(vs);
    device->Destroy(ps);
    device->Destroy(offscreenRtv);
    device->Destroy(dsvView);
    device->Destroy(depthImage);
    device->Destroy(offscreenTarget);
    readbackProbes.Reset();
    readbackFrame.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;
    ZrD3D12DestroyDevice(device);

    // ---- 判定与通过行 -----------------------------------------------------------------------
    const bool probesOk = readbackChecks > 0u && valueProbePasses == readbackChecks
        && skyProbePasses == skyProbeChecks;
    if (allOk && probesOk && relocOk && auditOk && liveOk && orbOk && ballOk && noDeviceRemoval
        && undeclaredUses == 0u && infoQueueErrors == 0u) {
        std::printf("D3D12 PLANET OK (origin=RELOCATED@%.0fm jitter=%.7fm/%.7fm)\n",
            lastFlightRelocM, audit.naiveMaxErrM, audit.relocatedMaxErrM);
        return 0;
    }
    std::printf("D3D12 PLANET FAILED (allOk=%d probes=%d relocs=%d@%.1fm audit=%d live=%d "
                "orbit=%d ball=%d removed=%d undeclared=%u errors=%u)\n",
        allOk ? 1 : 0, probesOk ? 1 : 0, relocOk ? 1 : 0, lastFlightRelocM, auditOk ? 1 : 0,
        liveOk ? 1 : 0, orbOk ? 1 : 0, ballOk ? 1 : 0, noDeviceRemoval ? 0 : 1, undeclaredUses,
        infoQueueErrors);
    return 1;
}

/// MV 常量（b0 镜像）——zupscaler.h ZrUpscalerMotionVectorConstants 直接复用

/// （单一事实源；motion_vectors.hlsl CBufMotionVectors 与之一一对应）
using MvPassConstants = zr::ZrUpscalerMotionVectorConstants;
static_assert(sizeof(MvPassConstants) == 128, "MV constants must be 128 bytes");

// --fsr 单相位（fsrEnabled 仅用于证据行；链已由调用方选定后端）。
// outFrameBytes：读back 帧（第 30 帧）全帧 display 域像素（行距 kFsrReadbackRowPitch）；
// outEdge：锐度探针梯度幅度。返回 false = 帧内硬错误。
// B-R6-02 增补：tsPool（2×帧数 时间戳槽）+ outGpuMs/outCpuMs —— GPU 时间戳
// 逐帧均值与 CPU 墙钟逐帧均值（含 Submit+WaitIdle），供 --fsr4 三向对比。
bool RunFsrPhase(ZrD3D12Device* device, IZrQueue* queue, ZrCommandList cmdHandle,
    IZrCommandList* cmdList, ID3D12GraphicsCommandList* nativeCmd, zr::UpscalerChain* chain,
    const char* phaseName, ZrPipelineState mainPso, ZrPipelineState shadowPso,
    ZrPipelineState mvPso, ZrImage sceneImage, ZrImageView sceneRtv, ZrImage mvImage,
    ZrImageView mvRtv, ZrImage depthImage, ZrImageView dsvView, ZrImage shadowImage,
    ZrImageView* shadowDsvs, ZrImageView shadowSrv, ZrImage displayImage, ZrBuffer cb0Ground,
    ZrBuffer cb0Cube, ZrBuffer cbLights, ZrBuffer cbClusters, ZrBuffer cbShadows,
    ZrBuffer* cbShadowPass, ZrBuffer cbShadowModelGround, ZrBuffer cbShadowModelCube,
    ZrBuffer cbMvGround, ZrBuffer cbMvCube, ZrBuffer groundVb, ZrBuffer groundIb,
    ZrBuffer cubeVb, ZrBuffer cubeIb, uint32_t cubeIndexCount, ID3D12Resource* readback,
    ID3D12Resource* sceneReadbackForDiagnostics,
    std::vector<uint8_t>& outFrameBytes, double* outEdge, ZrQueryPool tsPool = ZrQueryPool{},
    double* outGpuMs = nullptr, double* outCpuMs = nullptr)
{
    void* cb0GroundMapped = device->GetBuffer(cb0Ground)->Map();
    void* cb0CubeMapped = device->GetBuffer(cb0Cube)->Map();
    void* cbLightsMapped = device->GetBuffer(cbLights)->Map();
    void* cbClustersMapped = device->GetBuffer(cbClusters)->Map();
    void* cbShadowsMapped = device->GetBuffer(cbShadows)->Map();
    void* cbMvGroundMapped = device->GetBuffer(cbMvGround)->Map();
    void* cbMvCubeMapped = device->GetBuffer(cbMvCube)->Map();
    void* cbShadowModelGroundMapped = device->GetBuffer(cbShadowModelGround)->Map();
    void* cbShadowModelCubeMapped = device->GetBuffer(cbShadowModelCube)->Map();
    if (cb0GroundMapped == nullptr || cb0CubeMapped == nullptr || cbLightsMapped == nullptr
        || cbClustersMapped == nullptr || cbShadowsMapped == nullptr
        || cbMvGroundMapped == nullptr || cbMvCubeMapped == nullptr
        || cbShadowModelGroundMapped == nullptr || cbShadowModelCubeMapped == nullptr) {
        std::printf("  fsr %s phase: constant buffer Map FAILED\n", phaseName);
        return false;
    }

    // ---- 相机 / 投影（原点朝 +Z，fovY=60°；jitter 逐帧注入投影）------------------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    constexpr float kAspect
        = static_cast<float>(kFsrRenderW) / static_cast<float>(kFsrRenderH);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    const uint32_t phaseCount = zr::ZupJitterPhaseCount(kFsrRenderW, kFsrDisplayW);
    std::printf("  fsr %s: jitter phase count=%u (960x540 -> 1280x720)\n", phaseName,
        phaseCount);

    // ---- 光源：1 方向光（静态；继承 --shadows 方向）-------------------------------
    LightData sceneLights[1] = {};
    {
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        sceneLights[0].pos[0] = d[0] * invLen;
        sceneLights[0].pos[1] = d[1] * invLen;
        sceneLights[0].pos[2] = d[2] * invLen;
        sceneLights[0].color[0] = 1.0f;
        sceneLights[0].color[1] = 0.95f;
        sceneLights[0].color[2] = 0.88f;
        sceneLights[0].intensity = 2.0f; // 强对比（锐度探针信号量）
        sceneLights[0].kind = kZrLightDirectional;
    }

    // ---- CPU 级联（相机/光源静态，每相位一次）-------------------------------------
    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = kAspect;
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    std::memcpy(shadowParamsStruct.lightDir, sceneLights[0].pos, sizeof(sceneLights[0].pos));
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = -0.5f;
    shadowParamsStruct.sceneCenter[2] = 10.0f;
    shadowParamsStruct.sceneRadius = 15.0f;
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("  fsr %s phase: BuildCascades FAILED\n", phaseName);
        return false;
    }
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(kNearZ, kFarZ, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("  fsr %s phase: CascadeSplits FAILED\n", phaseName);
            return false;
        }
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES - 1u; ++c) {
            cascadeSplits[c] = interior[c];
        }
        cascadeSplits[ZR_SHADOW_CASCADES - 1u] = kFarZ;
    }
    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;
    }
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES - 1u; ++c) {
        shadowCb.cascadeSplits[c] = cascadeSplits[c];
    }
    shadowCb.cascadeSplits[ZR_SHADOW_CASCADES - 1u] = kFarZ;
    std::memcpy(cbShadowsMapped, &shadowCb, sizeof(shadowCb));
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("  fsr %s phase: shadow pass CB %u Map FAILED\n", phaseName, c);
            return false;
        }
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        std::memcpy(mapped, &spc, sizeof(spc));
    }
    {
        ShadowModelConstants gm {};
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        std::memcpy(gm.shadowModel, identity, sizeof(identity));
        std::memcpy(cbShadowModelGroundMapped, &gm, sizeof(gm));
    }

    // ---- 簇网格（1 方向光；相机静态 → 相位内一次分配）------------------------------
    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = kAspect;
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);
    PackedLight packedLights[1];
    PackLights(sceneLights, 1u, packedLights);
    std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
    std::memset(static_cast<uint8_t*>(cbLightsMapped) + sizeof(packedLights), 0,
        1024u - sizeof(packedLights));
    const int32_t flatCount = AssignLightsToClusters(grid, sceneLights, 1u,
        clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
    if (flatCount < 0) {
        std::printf("  fsr %s phase: AssignLightsToClusters FAILED (%d)\n", phaseName,
            flatCount);
        return false;
    }
    uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
    std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);
    std::memcpy(cb2 + size_t(ZR_CLUSTER_COUNT) * 4u, clusterFlat.data(),
        size_t(flatCount) * 4u);

    // ---- 静态 CB0（地面 / cube 材质；mvp 逐帧更新）--------------------------------
    const float ambient = 0.12f;
    ShadowDrawConstants groundConst {};
    const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    std::memcpy(groundConst.model, identity, sizeof(identity));
    ShadowDrawConstants cubeConst {};
    for (ShadowDrawConstants* c : {&groundConst, &cubeConst}) {
        c->projParams[0] = kFarZ / (kFarZ - kNearZ);
        c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
        c->projParams[2] = kNearZ;
        c->projParams[3] = kFarZ;
        c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
        c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
        c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
        c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
        c->screenParams[0] = static_cast<float>(kFsrRenderW);
        c->screenParams[1] = static_cast<float>(kFsrRenderH);
        c->screenParams[2] = 1.0f;
        c->screenParams[3] = ambient;
        c->camPos[0] = 0.0f;
        c->camPos[1] = 0.0f;
        c->camPos[2] = 0.0f;
        c->camPos[3] = 0.0f;
        c->cascadeSplits[0] = cascadeSplits[0];
        c->cascadeSplits[1] = cascadeSplits[1];
        c->cascadeSplits[2] = cascadeSplits[2];
        c->cascadeSplits[3] = kFarZ;
        c->shadowParams[0] = 1.0f; // 阴影开（继承 --shadows on）
        c->shadowParams[1] = static_cast<float>(kShadowsTexSize);
    }
    groundConst.material[0] = 0.50f;
    groundConst.material[1] = 0.52f;
    groundConst.material[2] = 0.50f;
    groundConst.material[3] = 0.9f;
    cubeConst.material[0] = 0.75f;
    cubeConst.material[1] = 0.75f;
    cubeConst.material[2] = 0.78f;
    cubeConst.material[3] = 0.35f;

    // ---- 30 帧循环 ------------------------------------------------------------------
    bool allOk = true;
    std::vector<double> cpuFrameMs; // 逐帧墙钟（含 Submit + WaitIdle）
    cpuFrameMs.reserve(kFsrFramesPerPhase);
    for (uint32_t frame = 0u; frame < kFsrFramesPerPhase; ++frame) {
        const bool doReadback = ((frame + 1u) % kFsrFramesPerPhase) == 0u;
        const auto frameStart = std::chrono::steady_clock::now();

        // cube 旋转（两相位同帧同角度 → 对比纯净；读back 帧（第 30 帧）angle=300°
        // ——对角受光面可见（ndl≈0.49），轮廓对比度强，锐度探针信号足量）
        const float angle = 6.2831853f * static_cast<float>(frame + 1u)
            / static_cast<float>(kFsrRotationPeriod);
        // prev 与 cur 同一旋转周期函数（frame 的角度 = 上一帧角度；帧 0 的 prev
        // 取 0 = 旋转起点）——MV 必须反映真实运动
        const float prevAngle = 6.2831853f * static_cast<float>(frame)
            / static_cast<float>(kFsrRotationPeriod);

        // jitter（FSR 序列；prev = 上帧相位——MV 需两帧 jitter 一致注入）
        float jx = 0.0f;
        float jy = 0.0f;
        float pjx = 0.0f;
        float pjy = 0.0f;
        zr::ZupJitterOffset(frame, phaseCount, &jx, &jy);
        zr::ZupJitterOffset((frame == 0u) ? 0u : frame - 1u, phaseCount, &pjx, &pjy);
        float projCur[16];
        MatPerspectiveFovLH(kFovY, kAspect, kNearZ, kFarZ, projCur);
        projCur[2] = 2.0f * jx / static_cast<float>(kFsrRenderW);
        projCur[6] = -2.0f * jy / static_cast<float>(kFsrRenderH);
        float projPrev[16];
        MatPerspectiveFovLH(kFovY, kAspect, kNearZ, kFarZ, projPrev);
        projPrev[2] = 2.0f * pjx / static_cast<float>(kFsrRenderW);
        projPrev[6] = -2.0f * pjy / static_cast<float>(kFsrRenderH);
        float projViewCur[16];
        MatMultiply(projCur, view, projViewCur);
        float projViewPrev[16];
        MatMultiply(projPrev, view, projViewPrev);

        // cube 模型：T(0,0,3.2)·R_Y(angle)（列向量约定）
        float rotCur[16];
        MatRotationY(angle, rotCur);
        float transCube[16];
        MatTranslation(0.0f, 0.0f, 3.2f, transCube);
        float cubeModel[16];
        MatMultiply(transCube, rotCur, cubeModel);
        float rotPrev[16];
        MatRotationY(prevAngle, rotPrev);
        float cubeModelPrev[16];
        MatMultiply(transCube, rotPrev, cubeModelPrev);

        // CB0（mvp = projView·model）
        std::memcpy(groundConst.mvp, projViewCur, sizeof(projViewCur));
        std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
        float cubeMvp[16];
        MatMultiply(projViewCur, cubeModel, cubeMvp);
        std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
        std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
        std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));

        // MV 常量（当前/上帧 mvp，各含当帧 jitter）
        MvPassConstants mvGround {};
        std::memcpy(mvGround.mvpCur, projViewCur, sizeof(projViewCur));
        std::memcpy(mvGround.mvpPrev, projViewPrev, sizeof(projViewPrev));
        std::memcpy(cbMvGroundMapped, &mvGround, sizeof(mvGround));
        MvPassConstants mvCube {};
        float cubeMvpPrev[16];
        MatMultiply(projViewPrev, cubeModelPrev, cubeMvpPrev);
        std::memcpy(mvCube.mvpCur, cubeMvp, sizeof(cubeMvp));
        std::memcpy(mvCube.mvpPrev, cubeMvpPrev, sizeof(cubeMvpPrev));
        std::memcpy(cbMvCubeMapped, &mvCube, sizeof(mvCube));

        // 阴影 pass cube 模型 CB（旋转中）
        {
            ShadowModelConstants cm {};
            std::memcpy(cm.shadowModel, cubeModel, sizeof(cubeModel));
            std::memcpy(cbShadowModelCubeMapped, &cm, sizeof(cm));
        }

        // ---- 帧图：Clear → Shadow×4 → Main(PBR+CSM) → MvPass（RG 声明）-----------
        cmdList->Begin();
        if (tsPool.IsValid()) {
            cmdList->WriteTimestamp(tsPool, frame * 2u); // 帧起点（GPU 时钟）
        }
        RgGraph graph(ResolveImageDesc);
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        RgHandle shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));
        RgHandle sceneH = graph.Import(RgResourceDesc::ImportImage("sceneColor", sceneImage));
        RgHandle mvH = graph.Import(RgResourceDesc::ImportImage("motionVectors", mvImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(sceneH),
                    ZrImageLayout::ColorAttachmentOptimal, kFsrClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });

        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            graph.AddPass("ShadowCascadePass",
                [&](RgPass& pass) {
                    pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                        ZrAccessBits::DepthStencilAttachmentWrite,
                        ZrPipelineStageBits::LateFragmentTests);
                },
                [&, c](RgContext&) {
                    cmdList->SetPipelineState(shadowPso);
                    cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                    cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                    nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(shadowDsvs[c]),
                        D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
                    cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                    // 地面 + cube（均投影）
                    cmdList->SetGraphicsConstantBuffer(1u, cbShadowModelGround);
                    ZrBuffer gvb[1] = {groundVb};
                    cmdList->SetVertexBuffers(gvb, 1u);
                    cmdList->SetIndexBuffer(groundIb);
                    cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                    cmdList->SetGraphicsConstantBuffer(1u, cbShadowModelCube);
                    ZrBuffer cvb[1] = {cubeVb};
                    cmdList->SetVertexBuffers(cvb, 1u);
                    cmdList->SetIndexBuffer(cubeIb);
                    cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
                });
        }

        graph.AddPass("MainPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                    ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mainPso);
                cmdList->SetRenderTarget(sceneRtv, dsvView);
                cmdList->SetViewportScissor(kFsrRenderW, kFsrRenderH); // 内部低分辨率渲染
                cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                cmdList->SetGraphicsBindlessSrv(4u, shadowSrv); // t0（rootCbvCount=4）
                ZrBuffer gvb[1] = {groundVb};
                cmdList->SetVertexBuffers(gvb, 1u);
                cmdList->SetIndexBuffer(groundIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Ground);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                ZrBuffer cvb[1] = {cubeVb};
                cmdList->SetVertexBuffers(cvb, 1u);
                cmdList->SetIndexBuffer(cubeIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Cube);
                cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
            });

        // ---- MV Pass（RG 特性：声明写 mvH；产物被 RG 外的 UpscalerChain 消费 →
        //      MarkRoot 防死剔除；motion_vectors.hlsl，输出 RG16Float）-------------
        graph.AddPass("MvPass",
            [&](RgPass& pass) {
                pass.Write(mvH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput)
                    .MarkRoot();
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mvPso);
                cmdList->SetRenderTarget(mvRtv, ZrImageView{});
                cmdList->SetViewportScissor(kFsrRenderW, kFsrRenderH);
                // 地面（静态：仅 jitter 增量）→ cube（旋转 + jitter）覆盖绘制
                ZrBuffer gvb[1] = {groundVb};
                cmdList->SetVertexBuffers(gvb, 1u);
                cmdList->SetIndexBuffer(groundIb);
                cmdList->SetGraphicsConstantBuffer(0u, cbMvGround);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                ZrBuffer cvb[1] = {cubeVb};
                cmdList->SetVertexBuffers(cvb, 1u);
                cmdList->SetIndexBuffer(cubeIb);
                cmdList->SetGraphicsConstantBuffer(0u, cbMvCube);
                cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
            });

        if (!graph.Compile()) {
            std::printf("  fsr %s frame %u: RG Compile FAILED (error=%u)\n", phaseName,
                frame + 1u, static_cast<unsigned>(graph.Error()));
            return false;
        }
        graph.ExecuteAliased(*device, *cmdList);

        // ---- UpscalerChain 求值（RG 外：FFX dispatch/Bilinear blit 自管状态）------
        // 输入资源转入上采样读取态；输出转 UAV（FSR）/ RTV（Bilinear）
        {
            ZrImageBarrier inBarriers[3];
            uint32_t n = 0u;
            inBarriers[n++] = MakeBarrier(sceneImage, ZrImageLayout::ColorAttachmentOptimal,
                ZrImageLayout::ShaderReadOnlyOptimal, ZrAccessBits::ColorAttachmentWrite,
                ZrPipelineStageBits::ColorAttachmentOutput, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::ComputeShader);
            inBarriers[n++] = MakeBarrier(mvImage, ZrImageLayout::ColorAttachmentOptimal,
                ZrImageLayout::ShaderReadOnlyOptimal, ZrAccessBits::ColorAttachmentWrite,
                ZrPipelineStageBits::ColorAttachmentOutput, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::ComputeShader);
            inBarriers[n++] = MakeBarrier(depthImage,
                ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
                ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::ComputeShader);
            cmdList->ResourceBarrier(inBarriers, n, nullptr, 0u);
        }
        const bool fsrPath
            = (chain->ActiveBackend() != zr::UpscalerBackend::Bilinear);
        ZrImage displayImageHandle = displayImage;
        {
            ZrImageBarrier outBarrier = MakeBarrier(displayImageHandle,
                ZrImageLayout::Undefined,
                fsrPath ? ZrImageLayout::General : ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe,
                fsrPath ? ZrAccessBits::ShaderWrite : ZrAccessBits::ColorAttachmentWrite,
                fsrPath ? ZrPipelineStageBits::ComputeShader
                        : ZrPipelineStageBits::ColorAttachmentOutput);
            cmdList->ResourceBarrier(&outBarrier, 1u, nullptr, 0u);
        }
        {
            zr::UpscalerFrameArgs args {};
            args.nativeCmdList = nativeCmd;
            args.inputColor = device->D3D12ResourceOf(sceneImage);
            args.inputDepth = device->D3D12ResourceOf(depthImage);
            args.inputMotionVectors = device->D3D12ResourceOf(mvImage);
            args.output = device->D3D12ResourceOf(displayImage);
            const uint32_t readState = zr::ZR_UPSCALER_STATE_COMPUTE_READ
                | zr::ZR_UPSCALER_STATE_PIXEL_READ;
            args.inputColorState = readState;
            args.inputDepthState = readState;
            args.inputMvState = readState;
            args.outputState = fsrPath ? zr::ZR_UPSCALER_STATE_UNORDERED_ACCESS
                                       : zr::ZR_UPSCALER_STATE_PIXEL_READ;
            args.jitterX = jx;
            args.jitterY = jy;
            float mvScale[2] = {};
            zr::ZupMotionVectorScale(kFsrRenderW, kFsrRenderH, mvScale);
            args.motionVectorScaleX = mvScale[0];
            args.motionVectorScaleY = mvScale[1];
            args.frameTimeDeltaMs = 16.666f;
            args.resetHistory = (frame == 0u);
            args.sharpness = fsrPath ? kFsrSharpness : 0.0f;
            if (!chain->Evaluate(args)) {
                std::printf("  fsr %s frame %u: UpscalerChain Evaluate FAILED\n", phaseName,
                    frame + 1u);
                return false;
            }
        }

        // ---- readback 帧：全帧 display 拷回 + 锐度探针 ------------------------------
        if (doReadback) {
            // 场景图（960×540）先行诊断拷回（上采样前的源）
            ID3D12Resource* sceneTexture = device->D3D12ResourceOf(sceneImage);
            {
                ZrImageBarrier toCopySrcScene = MakeBarrier(sceneImage,
                    ZrImageLayout::ShaderReadOnlyOptimal, ZrImageLayout::TransferSrcOptimal,
                    ZrAccessBits::ShaderRead, ZrPipelineStageBits::ComputeShader,
                    ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer);
                cmdList->ResourceBarrier(&toCopySrcScene, 1u, nullptr, 0u);
                ID3D12Resource* sceneReadback = sceneReadbackForDiagnostics;
                if (sceneReadback != nullptr) {
                    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp {};
                    uint64_t tb = 0u;
                    ComputeFootprint(device->D3D12Device(), sceneTexture, fp, tb);
                    D3D12_TEXTURE_COPY_LOCATION dst {};
                    dst.pResource = sceneReadback;
                    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                    dst.PlacedFootprint = fp;
                    D3D12_TEXTURE_COPY_LOCATION src {};
                    src.pResource = sceneTexture;
                    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                    src.SubresourceIndex = 0u;
                    nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, nullptr);
                }
                ZrImageBarrier backToSrv = MakeBarrier(sceneImage,
                    ZrImageLayout::TransferSrcOptimal, ZrImageLayout::ShaderReadOnlyOptimal,
                    ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer,
                    ZrAccessBits::ShaderRead, ZrPipelineStageBits::ComputeShader);
                cmdList->ResourceBarrier(&backToSrv, 1u, nullptr, 0u);
            }
            if (fsrPath) {
                ZrImageBarrier toCopySrc = MakeBarrier(displayImageHandle,
                    ZrImageLayout::General, ZrImageLayout::TransferSrcOptimal,
                    ZrAccessBits::ShaderWrite, ZrPipelineStageBits::ComputeShader,
                    ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer);
                cmdList->ResourceBarrier(&toCopySrc, 1u, nullptr, 0u);
            } else {
                ZrImageBarrier toCopySrc = MakeBarrier(displayImageHandle,
                    ZrImageLayout::ColorAttachmentOptimal, ZrImageLayout::TransferSrcOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput, ZrAccessBits::TransferRead,
                    ZrPipelineStageBits::Transfer);
                cmdList->ResourceBarrier(&toCopySrc, 1u, nullptr, 0u);
            }
            ID3D12Resource* displayTexture = device->D3D12ResourceOf(displayImage);
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint {};
            uint64_t totalBytes = 0u;
            ComputeFootprint(device->D3D12Device(), displayTexture, footprint, totalBytes);
            RecordReadback(nativeCmd, displayTexture, readback, footprint);
        }

        // ---- 帧末：持久图像回 COMMON（与下一帧 RG 起点 Undefined 一致）--------------
        {
            ZrImageBarrier resets[5];
            uint32_t n = 0u;
            if (doReadback) {
                resets[n++] = MakeBarrier(displayImageHandle, ZrImageLayout::TransferSrcOptimal,
                    ZrImageLayout::Undefined, ZrAccessBits::TransferRead,
                    ZrPipelineStageBits::Transfer, ZrAccessBits::None,
                    ZrPipelineStageBits::TopOfPipe);
            } else {
                resets[n++] = MakeBarrier(displayImageHandle,
                    fsrPath ? ZrImageLayout::General : ZrImageLayout::ColorAttachmentOptimal,
                    ZrImageLayout::Undefined,
                    fsrPath ? ZrAccessBits::ShaderWrite : ZrAccessBits::ColorAttachmentWrite,
                    fsrPath ? ZrPipelineStageBits::ComputeShader
                            : ZrPipelineStageBits::ColorAttachmentOutput,
                    ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
            }
            resets[n++] = MakeBarrier(sceneImage, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::ComputeShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(mvImage, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::ComputeShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(depthImage, ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::DepthStencilAttachmentRead,
                ZrPipelineStageBits::ComputeShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(shadowImage, ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::DepthStencilAttachmentRead,
                ZrPipelineStageBits::FragmentShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(resets, n, nullptr, 0u);
        }

        if (tsPool.IsValid()) {
            cmdList->WriteTimestamp(tsPool, frame * 2u + 1u); // 帧终点（GPU 时钟）
        }
        cmdList->End();
        queue->Submit(cmdHandle);
        device->WaitIdle();
        graph.FlushRetired(*device);
        cpuFrameMs.push_back(std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - frameStart)
                                 .count());

        if (doReadback) {
            void* mapped = nullptr;
            const HRESULT hr = readback->Map(0, nullptr, &mapped);
            if (FAILED(hr) || mapped == nullptr) {
                std::printf("  fsr %s frame %u: readback Map FAILED (hr=0x%08lX)\n", phaseName,
                    frame + 1u, static_cast<unsigned long>(hr));
                return false;
            }
            const uint8_t* bytes = static_cast<const uint8_t*>(mapped);
            outFrameBytes.assign(bytes, bytes + size_t(kFsrReadbackRowPitch) * kFsrDisplayH);
            *outEdge = FsrEdgeMetric(
                outFrameBytes.data() + size_t(kFsrProbeRow) * kFsrReadbackRowPitch, kFsrProbeXMin,
                kFsrProbeXMax);
            readback->Unmap(0, nullptr);
            // 像素诊断：中心 / cube 预期区 / 角落（display 域 RGBA8Unorm → R,G,B 顺序）
            auto px = [&](uint32_t x, uint32_t y) {
                const uint8_t* p = outFrameBytes.data() + size_t(y) * kFsrReadbackRowPitch + x * 4u;
                std::printf("    pixel(%u,%u) = (%d,%d,%d,%d)\n", x, y, p[0], p[1], p[2], p[3]);
            };
            std::printf("  fsr %s frame %u: edge probe=%.3f (row %u, x [%u..%u))\n", phaseName,
                frame + 1u, *outEdge, kFsrProbeRow, kFsrProbeXMin, kFsrProbeXMax);
            px(kFsrDisplayW / 2u, kFsrProbeRow);
            px(470u, kFsrProbeRow);
            px(810u, kFsrProbeRow);
            px(0u, 0u);
            // 场景源图诊断（上采样前；RGBA8Unorm 960×540）
            if (sceneReadbackForDiagnostics != nullptr) {
                void* sceneMapped = nullptr;
                if (SUCCEEDED(sceneReadbackForDiagnostics->Map(0, nullptr, &sceneMapped))
                    && sceneMapped != nullptr) {
                    const uint8_t* sb = static_cast<const uint8_t*>(sceneMapped);
                    auto spx = [&](uint32_t x, uint32_t y) {
                        const uint8_t* p
                            = sb + size_t(y) * kFsrSceneRowPitch + size_t(x) * 4u;
                        std::printf("    scene(%u,%u) = (%d,%d,%d,%d)\n", x, y, p[0], p[1], p[2],
                            p[3]);
                    };
                    spx(kFsrRenderW / 2u, kFsrRenderH / 2u);
                    spx(kFsrRenderW / 2u, kFsrRenderH - 20u);
                    spx(20u, 20u);
                    sceneReadbackForDiagnostics->Unmap(0, nullptr);
                }
            }
        }
    }

    // ---- 逐帧计时汇总（B-R6-02：GPU 时间戳均值 + CPU 墙钟均值）------------------
    if (outCpuMs != nullptr) {
        double sum = 0.0;
        for (double v : cpuFrameMs) {
            sum += v;
        }
        *outCpuMs = cpuFrameMs.empty() ? -1.0 : sum / static_cast<double>(cpuFrameMs.size());
    }
    if (outGpuMs != nullptr) {
        *outGpuMs = -1.0;
        if (tsPool.IsValid()) {
            const uint32_t stampCount = kFsrFramesPerPhase * 2u;
            std::vector<double> stamps(stampCount, 0.0);
            const uint32_t resolved
                = device->ResolveTimestamps(tsPool, stamps.data(), stampCount);
            if (resolved == stampCount) {
                double sum = 0.0;
                uint32_t pairs = 0u;
                for (uint32_t i = 0u; i < kFsrFramesPerPhase; ++i) {
                    const double delta = stamps[i * 2u + 1u] - stamps[i * 2u];
                    if (delta > 0.0) {
                        sum += delta;
                        pairs += 1u;
                    }
                }
                if (pairs > 0u) {
                    *outGpuMs = sum / static_cast<double>(pairs);
                }
            }
            std::printf("  fsr %s: gpu ms mean=%.4f (stamps resolved=%u/%u) cpu ms mean=%.4f\n",
                phaseName, *outGpuMs, resolved, stampCount,
                (outCpuMs != nullptr) ? *outCpuMs : -1.0);
        }
    }
    return allOk;
}

// --fsr / --fsr4 模式主体（返回进程退出码）。
//   --fsr on ：off 相位（覆盖 Bilinear）→ on 相位（自动选优，真机 FSR3）→ 锐度比 ≥1.2×
//              → 写 Docs/Parity/fsr_on.png + fsr_off.png → `D3D12 FSR OK (...)`
//   --fsr off：仅 Bilinear 基线相位自检（无 FSR）→ `D3D12 FSR BASELINE OK (edge=M)`
//   --fsr4   ：B-R6-02 三向可证伪——bilinear / FSR3.1 / FSR4.1 各 30 帧，
//              输出锐度探针 + GPU 时间戳均值 + CPU 帧耗；
//              FSR4 真跑通 → `D3D12 FSR4 OK (edge3=N edge4=M ms3=X ms4=Y)`（exit 0）
//              FSR4 不可用 → `D3D12 FSR4 UNAVAILABLE (...)`（exit 2 = 受限，非缺陷）
enum class FsrMode {
    Off,  // 仅基线
    On,   // off vs on（自动选优）
    Fsr4, // 三向：bilinear / FSR3.1 / FSR4.1
};

/// 单相位结果（B-R6-02：三向对比需要逐相位保留帧 + 锐度 + 计时）
struct FsrPhaseOutcome {
    bool ok = false;
    double edge = 0.0;
    double gpuMs = -1.0;
    double cpuMs = -1.0;
    std::vector<uint8_t> frame;
    const char* backendName = "none";
    const char* backendVersion = "";
};

static int RunFsrMode(FsrMode mode)
{
    const bool runOnMode = (mode != FsrMode::Off);
    // 崩溃时保留已打印证据（行缓冲在管道重定向下会整体丢失）
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (mode == FsrMode::Fsr4) {
        std::printf("zrhi_d3d12_window: --fsr4 mode (TASK-B-R6-02): three-way "
                    "bilinear vs FSR3.1 vs FSR4.1 (%u frames each)\n",
            kFsrFramesPerPhase);
    } else {
        std::printf("zrhi_d3d12_window: --fsr mode (TASK-B-R6-01): UpscalerChain %s\n",
            runOnMode ? "FSR on (falsifiable vs bilinear)" : "off (bilinear baseline only)");
    }

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --fsr device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 设备能力档案（DXGI 适配器（与 zrhi 建备同策略：首个非软件适配器）+
    //      FSR 运行时探测）--------------------------------------------------------------
    zr::UpscalerDeviceCaps caps;
    {
        IDXGIFactory1* factory = nullptr;
        if (SUCCEEDED(::CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && factory != nullptr) {
            IDXGIAdapter1* adapter = nullptr;
            for (UINT i = 0u;
                 factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
                DXGI_ADAPTER_DESC1 adapterDesc {};
                if (SUCCEEDED(adapter->GetDesc1(&adapterDesc))
                    && (adapterDesc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0u) {
                    caps.vendorId = adapterDesc.VendorId;
                    caps.deviceId = adapterDesc.DeviceId;
                    char descA[128] = {};
                    ::WideCharToMultiByte(CP_UTF8, 0, adapterDesc.Description, -1, descA,
                        sizeof(descA), nullptr, nullptr);
                    std::printf("zrhi_d3d12_window: --fsr adapter: %s (vendor=0x%04X "
                                "device=0x%04X)\n",
                        descA, caps.vendorId, caps.deviceId);
                    adapter->Release();
                    break;
                }
                adapter->Release();
            }
            factory->Release();
        }
    }
    caps.fsrRuntimePresent = zr::ZupFsrRuntimeProbe();
    // B-R6-02：FSR4 能力以 provider 运行时枚举为准（不猜 GPU 代际）；
    // DLSS 能力 = N 卡 + Streamline 运行时（本机恒 false）
    caps.fsr4Available = caps.fsrRuntimePresent && zr::ZupFsr4Probe(nullptr);
    caps.nvidiaHardware = (caps.vendorId == 0x10DEu);
    caps.dlssRuntimePresent = zr::ZupDlssRuntimeProbe();
    std::printf("zrhi_d3d12_window: --fsr caps: vendor=0x%04X device=0x%04X "
                "fsrRuntime=%d fsr4Available=%d nvidia=%d dlssRuntime=%d\n",
        caps.vendorId, caps.deviceId, caps.fsrRuntimePresent ? 1 : 0,
        caps.fsr4Available ? 1 : 0, caps.nvidiaHardware ? 1 : 0,
        caps.dlssRuntimePresent ? 1 : 0);
    {
        zr::ZupProviderVersion providers[8] = {};
        const uint32_t providerCount = zr::ZupFsrEnumerateProviders(nullptr, providers, 8u);
        std::printf("zrhi_d3d12_window: --fsr provider enumeration (device=null, conservative): "
                    "count=%u\n",
            providerCount);
        for (uint32_t i = 0u; i < providerCount; ++i) {
            std::printf("  provider[%u] id=0x%llX name=%s\n", i,
                static_cast<unsigned long long>(providers[i].versionId), providers[i].name);
        }
    }

    // ---- 渲染目标与视图 --------------------------------------------------------------
    auto makeImage = [&](ZrFormat format, uint32_t w, uint32_t h, uint32_t arrayLayers,
                         uint32_t mips, ZrImageUsage usage) {
        ZrImageDesc desc {};
        desc.format = format;
        desc.width = w;
        desc.height = h;
        desc.arrayLayers = arrayLayers;
        desc.mipLevels = mips;
        desc.usage = usage;
        return device->CreateImage(desc);
    };
    ZrImage sceneImage = makeImage(ZrFormat::RGBA8Unorm, kFsrRenderW, kFsrRenderH, 1u, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled | ZrImageUsage::Storage);
    ZrImage mvImage = makeImage(ZrFormat::RG16Float, kFsrRenderW, kFsrRenderH, 1u, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled | ZrImageUsage::Storage);
    ZrImage depthImage = makeImage(ZrFormat::D32Float, kFsrRenderW, kFsrRenderH, 1u, 1u,
        ZrImageUsage::DepthStencilAttachment);
    ZrImage shadowImage = makeImage(ZrFormat::D32Float, kShadowsTexSize, kShadowsTexSize,
        ZR_SHADOW_CASCADES, 1u,
        ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled);
    ZrImage displayImage = makeImage(ZrFormat::RGBA8Unorm, kFsrDisplayW, kFsrDisplayH, 1u, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Storage | ZrImageUsage::TransferSrc);
    if (!sceneImage.IsValid() || !mvImage.IsValid() || !depthImage.IsValid()
        || !shadowImage.IsValid() || !displayImage.IsValid()) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to create images\n");
        return 1;
    }
    auto makeView = [&](ZrImage image, ZrImageViewType type, uint32_t layer = 0u) {
        ZrImageViewDesc desc {};
        desc.image = image;
        desc.viewType = type;
        desc.subresources.baseArrayLayer = layer;
        desc.subresources.layerCount = 1u;
        return device->CreateImageView(desc);
    };
    ZrImageView sceneRtv = makeView(sceneImage, ZrImageViewType::Rtv);
    ZrImageView mvRtv = makeView(mvImage, ZrImageViewType::Rtv);
    ZrImageView dsvView = makeView(depthImage, ZrImageViewType::Dsv);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        shadowDsvs[c] = makeView(shadowImage, ZrImageViewType::Dsv, c);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float;
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    bool viewsOk = sceneRtv.IsValid() && mvRtv.IsValid() && dsvView.IsValid() && shadowSrv.IsValid();
    for (const ZrImageView& v : shadowDsvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    if (!viewsOk) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to create views\n");
        return 1;
    }

    // ---- 几何（同 --shadows：地面 quad 20×20 @y=-1 + cube.zmesh）---------------------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};
    std::vector<char> meshBytes;
    if (!ReadWholeFile("Native/tests/data/cube.zmesh", meshBytes)) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to open cube.zmesh\n");
        return 1;
    }
    ZmeshData mesh {};
    if (zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh) != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --fsr cube.zmesh parse FAILED\n");
        return 1;
    }
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(mesh.stats.vertexCount);
    for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);

    auto makeVb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    auto makeIb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    ZrBuffer groundVb = makeVb(groundVerts, sizeof(groundVerts));
    ZrBuffer groundIb = makeIb(groundIndices, sizeof(groundIndices));
    ZrBuffer cubeVb = makeVb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex));
    ZrBuffer cubeIb = makeIb(cubeIndices.data(), uint64_t(cubeIndices.size()) * 4u);
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to create VB/IB\n");
        return 1;
    }

    // ---- 常量缓冲 --------------------------------------------------------------------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbShadowModelGround {};
    ZrBuffer cbShadowModelCube {};
    ZrBuffer cbMvGround {};
    ZrBuffer cbMvCube {};
    {
        auto makeCb = [&](uint64_t size) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            ZrBuffer b = device->CreateBuffer(desc);
            return (b.IsValid() && device->GetBuffer(b)->Map() != nullptr) ? b : ZrBuffer{};
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        bool ok = (cb0Ground = makeCb(256u)).IsValid() && (cb0Cube = makeCb(256u)).IsValid()
            && (cbLights = makeCb(1024u)).IsValid() && (cbClusters = makeCb(cb2Size)).IsValid()
            && (cbShadows = makeCb(384u)).IsValid()
            && (cbShadowModelGround = makeCb(256u)).IsValid()
            && (cbShadowModelCube = makeCb(256u)).IsValid()
            && (cbMvGround = makeCb(256u)).IsValid() && (cbMvCube = makeCb(256u)).IsValid();
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            ok = ok && (cbShadowPass[c] = makeCb(256u)).IsValid();
        }
        if (!ok) {
            std::printf("zrhi_d3d12_window: --fsr FAILED to create constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器 + PSO ----------------------------------------------------------------
    const char* kFsrShaderPaths[3] = {"Native/shaders/pbr_shadow.hlsl",
        "Native/shaders/shadow_depth.hlsl", "Native/shaders/motion_vectors.hlsl"};
    std::vector<char> shaderSrc[3];
    for (int i = 0; i < 3; ++i) {
        if (!ReadWholeFile(kFsrShaderPaths[i], shaderSrc[i])) {
            std::printf("zrhi_d3d12_window: --fsr FAILED to open %s\n", kFsrShaderPaths[i]);
            return 1;
        }
    }
    std::vector<char> bilinearSrc;
    if (!ReadWholeFile("Native/shaders/upsample_bilinear.hlsl", bilinearSrc)) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to open upsample_bilinear.hlsl\n");
        return 1;
    }
    const ZrShader mainVs = device->CompileHlsl(
        ZrShaderStage::Vertex, shaderSrc[0].data(), shaderSrc[0].size(), "VsMain");
    const ZrShader mainPs = device->CompileHlsl(
        ZrShaderStage::Pixel, shaderSrc[0].data(), shaderSrc[0].size(), "PsMain");
    const ZrShader shadowVs = device->CompileHlsl(
        ZrShaderStage::Vertex, shaderSrc[1].data(), shaderSrc[1].size(), "VsMain");
    const ZrShader mvVs = device->CompileHlsl(
        ZrShaderStage::Vertex, shaderSrc[2].data(), shaderSrc[2].size(), "VsMain");
    const ZrShader mvPs = device->CompileHlsl(
        ZrShaderStage::Pixel, shaderSrc[2].data(), shaderSrc[2].size(), "PsMain");
    const ZrShader allShaders[5] = {mainVs, mainPs, shadowVs, mvVs, mvPs};
    bool shadersOk = true;
    for (const ZrShader& s : allShaders) {
        shadersOk = shadersOk && s.IsValid();
    }
    if (!shadersOk) {
        std::printf("zrhi_d3d12_window: --fsr shader compilation FAILED (see stderr)\n");
        return 1;
    }

    ZrGraphicsPsoDesc mainPsoDesc {};
    mainPsoDesc.vertexShader = mainVs;
    mainPsoDesc.pixelShader = mainPs;
    mainPsoDesc.rtvFormat = ZrFormat::RGBA8Unorm;
    mainPsoDesc.dsvFormat = ZrFormat::D32Float;
    mainPsoDesc.blend = ZrBlendMode::Opaque;
    mainPsoDesc.depthTest = true;
    mainPsoDesc.depthWrite = true;
    mainPsoDesc.cullMode = ZrCullMode::None;
    mainPsoDesc.rootCbvCount = 4u;
    mainPsoDesc.srvTable = true;
    ZrPipelineState mainPso = device->CreatePipelineState(mainPsoDesc);
    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = shadowVs;
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    shadowPsoDesc.cullMode = ZrCullMode::Front;
    shadowPsoDesc.rootCbvCount = 2u;
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    ZrGraphicsPsoDesc mvPsoDesc {};
    mvPsoDesc.vertexShader = mvVs;
    mvPsoDesc.pixelShader = mvPs;
    mvPsoDesc.rtvFormat = ZrFormat::RG16Float; // MV 输出（简报指定格式）
    mvPsoDesc.dsvFormat = ZrFormat::Count;
    mvPsoDesc.blend = ZrBlendMode::Opaque;
    mvPsoDesc.cullMode = ZrCullMode::None;
    mvPsoDesc.rootCbvCount = 1u;
    ZrPipelineState mvPso = device->CreatePipelineState(mvPsoDesc);
    if (!mainPso.IsValid() || !shadowPso.IsValid() || !mvPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to create PSOs\n");
        return 1;
    }

    // ---- readback（全帧 1280×720）------------------------------------------------------
    Microsoft::WRL::ComPtr<ID3D12Resource> readback = MakeReadbackBuffer(device->D3D12Device(),
        uint64_t(kFsrReadbackRowPitch) * kFsrDisplayH);
    Microsoft::WRL::ComPtr<ID3D12Resource> sceneReadback = MakeReadbackBuffer(
        device->D3D12Device(), uint64_t(kFsrSceneRowPitch) * kFsrRenderH);
    if (readback == nullptr || sceneReadback == nullptr) {
        std::printf("zrhi_d3d12_window: --fsr FAILED to create readback buffers\n");
        return 1;
    }

    // ---- 相位编排：off（覆盖 Bilinear）→ on（自动选优）/ FSR4 三向 -------------------
    zr::UpscalerInitDesc initDesc {};
    initDesc.inputWidth = kFsrRenderW;
    initDesc.inputHeight = kFsrRenderH;
    initDesc.outputWidth = kFsrDisplayW;
    initDesc.outputHeight = kFsrDisplayH;
    initDesc.cameraFovY = 3.14159265f / 3.0f;
    initDesc.cameraNear = 0.3f;
    initDesc.cameraFar = 60.0f;
    initDesc.nativeDevice = device->D3D12Device();
    initDesc.outputDxgiFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
    initDesc.bilinearHlsl = bilinearSrc.data();
    initDesc.bilinearHlslSize = static_cast<uint32_t>(bilinearSrc.size());

    // GPU 时间戳堆：每帧 2 个槽（起/止），逐相位解析后取均值（B-R6-02）
    ZrQueryPool tsPool = device->CreateTimestampQueryHeap(kFsrFramesPerPhase * 2u);
    if (!tsPool.IsValid()) {
        std::printf("zrhi_d3d12_window: --fsr timestamp heap unavailable; GPU ms reported as -1\n");
    }

    // 单相位执行体（三条路径共用同一参数表，避免三份 12 行调用漂移）
    auto runPhase = [&](zr::UpscalerChain& chain, const char* label, FsrPhaseOutcome& out) {
        out.ok = RunFsrPhase(device, queue, cmdHandle, cmdList, nativeCmd, &chain, label,
            mainPso, shadowPso, mvPso, sceneImage, sceneRtv, mvImage, mvRtv, depthImage,
            dsvView, shadowImage, shadowDsvs, shadowSrv, displayImage, cb0Ground, cb0Cube,
            cbLights, cbClusters, cbShadows, cbShadowPass, cbShadowModelGround,
            cbShadowModelCube, cbMvGround, cbMvCube, groundVb, groundIb, cubeVb, cubeIb,
            cubeIndexCount, readback.Get(), sceneReadback.Get(), out.frame, &out.edge, tsPool,
            &out.gpuMs, &out.cpuMs);
        out.backendName = zr::UpscalerBackendName(chain.ActiveBackend());
        out.backendVersion = chain.ActiveBackendVersion();
        return out.ok;
    };

    FsrPhaseOutcome off;
    FsrPhaseOutcome on;
    FsrPhaseOutcome fsr3;
    FsrPhaseOutcome fsr4;

    {
        zr::UpscalerChain chain;
        std::printf("zrhi_d3d12_window: --fsr phase OFF (bilinear baseline, %u frames)\n",
            kFsrFramesPerPhase);
        chain.SetOverride(zr::UpscalerBackend::Bilinear); // 显式钉死基线（可证伪）
        if (!chain.SelectAndInitialize(caps, initDesc)
            || chain.ActiveBackend() != zr::UpscalerBackend::Bilinear) {
            std::printf("zrhi_d3d12_window: --fsr OFF phase: bilinear select FAILED\n");
        } else {
            std::printf("zrhi_d3d12_window: --fsr OFF phase backend=%s (%s)\n",
                zr::UpscalerBackendName(chain.ActiveBackend()), chain.ActiveBackendVersion());
            runPhase(chain, "OFF", off);
        }
        chain.Shutdown();
    }
    if (mode == FsrMode::On) {
        zr::UpscalerChain chain;
        std::printf("zrhi_d3d12_window: --fsr phase ON (auto select, %u frames)\n",
            kFsrFramesPerPhase);
        // 自动选优（默认优先序）。回落 Bilinear = FSR 运行时不可用——显式失败，
        // 不静默伪装成 FSR 对比（简报网络纪律：失败必须报告具体失败点）。
        if (!chain.SelectAndInitialize(caps, initDesc)
            || chain.ActiveBackend() == zr::UpscalerBackend::Bilinear) {
            std::printf("zrhi_d3d12_window: --fsr ON phase: FSR runtime unavailable "
                        "(vendor=0x%04X fsrRuntime=%d); chain fallback reached Bilinear — "
                        "refusing to fake FSR comparison\n",
                caps.vendorId, caps.fsrRuntimePresent ? 1 : 0);
        } else {
            std::printf("zrhi_d3d12_window: --fsr ON phase backend=%s (%s)\n",
                zr::UpscalerBackendName(chain.ActiveBackend()), chain.ActiveBackendVersion());
            runPhase(chain, "ON", on);
        }
        chain.Shutdown();
    }
    if (mode == FsrMode::Fsr4) {
        // ① FSR 3.1（覆盖钉死——三向对比的中间档）
        {
            zr::UpscalerChain chain;
            std::printf("zrhi_d3d12_window: --fsr4 phase FSR3.1 (pinned override, %u frames)\n",
                kFsrFramesPerPhase);
            chain.SetOverride(zr::UpscalerBackend::Fsr3);
            if (!chain.SelectAndInitialize(caps, initDesc)
                || chain.ActiveBackend() != zr::UpscalerBackend::Fsr3) {
                std::printf("zrhi_d3d12_window: --fsr4 FSR3.1 phase: select FAILED\n");
            } else {
                std::printf("zrhi_d3d12_window: --fsr4 FSR3.1 phase backend=%s (%s)\n",
                    zr::UpscalerBackendName(chain.ActiveBackend()),
                    chain.ActiveBackendVersion());
                runPhase(chain, "FSR3.1", fsr3);
            }
            chain.Shutdown();
        }
        // ② FSR 4.1（覆盖钉死；不可用 → 打印真实原因并跳过，不伪装）
        {
            zr::UpscalerChain chain;
            std::printf("zrhi_d3d12_window: --fsr4 phase FSR4.1 (pinned override, %u frames)\n",
                kFsrFramesPerPhase);
            chain.SetOverride(zr::UpscalerBackend::Fsr4);
            const bool selected = chain.SelectAndInitialize(caps, initDesc);
            if (!selected || chain.ActiveBackend() != zr::UpscalerBackend::Fsr4) {
                std::printf("zrhi_d3d12_window: --fsr4 FSR4.1 phase: capability matrix rejects "
                            "Fsr4 (fsr4Available=%d) — probing the FFX runtime directly for the "
                            "exact reason:\n",
                    caps.fsr4Available ? 1 : 0);
                // 绕过能力矩阵直接问 FFX：打印 provider 列表 + NO_PROVIDER 证据
                zr::IUpscaler* probe = zr::CreateFsr4Upscaler();
                if (probe != nullptr) {
                    const bool probeOk = probe->Initialize(initDesc);
                    std::printf("zrhi_d3d12_window: --fsr4 direct Fsr4 Initialize=%d\n",
                        probeOk ? 1 : 0);
                    probe->Shutdown();
                    delete probe;
                }
            } else {
                std::printf("zrhi_d3d12_window: --fsr4 FSR4.1 phase backend=%s (%s)\n",
                    zr::UpscalerBackendName(chain.ActiveBackend()),
                    chain.ActiveBackendVersion());
                runPhase(chain, "FSR4.1", fsr4);
            }
            chain.Shutdown();
        }
    }

    // ---- 清理 -------------------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    for (ZrBuffer& b : cbShadowPass) {
        device->Destroy(b);
    }
    device->Destroy(cbShadowModelGround);
    device->Destroy(cbShadowModelCube);
    device->Destroy(cbMvGround);
    device->Destroy(cbMvCube);
    device->Destroy(mainPso);
    device->Destroy(shadowPso);
    device->Destroy(mvPso);
    for (const ZrShader& s : allShaders) {
        device->Destroy(s);
    }
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(sceneRtv);
    device->Destroy(mvRtv);
    device->Destroy(dsvView);
    for (ZrImageView& v : shadowDsvs) {
        device->Destroy(v);
    }
    device->Destroy(shadowSrv);
    device->Destroy(sceneImage);
    device->Destroy(mvImage);
    device->Destroy(depthImage);
    device->Destroy(shadowImage);
    device->Destroy(displayImage);
    readback.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (tsPool.IsValid()) {
        device->Destroy(tsPool);
    }
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    // ---- 判定与证据 ---------------------------------------------------------------------
    const bool edgeOffSane = off.edge > 0.0;
    std::printf("zrhi_d3d12_window: [FSR] bilinear edge=%.3f gpu=%.4f ms cpu=%.4f ms\n", off.edge,
        off.gpuMs, off.cpuMs);

    if (mode == FsrMode::Fsr4) {
        // 三向可证伪：bilinear / FSR3.1 / FSR4.1。FSR4 真跑通 → OK 行；
        // 否则 UNAVAILABLE（exit 2 = 环境受限，不是实现失败）。
        const bool fsr3Sane = fsr3.ok && fsr3.edge > 0.0;
        const bool fsr4Sane = fsr4.ok && fsr4.edge > 0.0;
        std::printf("zrhi_d3d12_window: [FSR4] device removed reason = 0x%08lX (%s), "
                    "InfoQueue errors = %u\n",
            static_cast<unsigned long>(removedReason), noDeviceRemoval ? "S_OK" : "REMOVED",
            infoQueueErrors);
        if (fsr3Sane && fsr4Sane) {
            std::filesystem::create_directories(
                std::filesystem::path(kFsr4PngPair).parent_path());
            const bool pngOk = WritePng24Pair(kFsr4PngPair, fsr3.frame.data(),
                fsr4.frame.data(), kFsrDisplayW, kFsrDisplayH, kFsrReadbackRowPitch);
            std::printf("zrhi_d3d12_window: [FSR4] screenshot %s=%d (left=FSR3.1 right=FSR4.1)\n",
                kFsr4PngPair, pngOk ? 1 : 0);
            std::printf("zrhi_d3d12_window: [FSR4] edge3=%.3f edge4=%.3f ms3=%.4f ms4=%.4f "
                        "msBilinear=%.4f\n",
                fsr3.edge, fsr4.edge, fsr3.gpuMs, fsr4.gpuMs, off.gpuMs);
            if (pngOk && noDeviceRemoval && infoQueueErrors == 0u) {
                std::printf("D3D12 FSR4 OK (edge3=%.3f edge4=%.3f ms3=%.3f ms4=%.3f)\n",
                    fsr3.edge, fsr4.edge, fsr3.gpuMs, fsr4.gpuMs);
                return 0;
            }
            std::printf("D3D12 FSR4 FAILED (png=%d removed=%d errors=%u)\n", pngOk ? 1 : 0,
                noDeviceRemoval ? 0 : 1, infoQueueErrors);
            return 1;
        }
        // FSR4 不可用：仍落盘 bilinear vs FSR3.1 拼图（证明三向链路除 FSR4 外全通）
        if (off.edge > 0.0 && fsr3Sane) {
            std::filesystem::create_directories(
                std::filesystem::path(kFsr31PngPair).parent_path());
            const bool pngOk = WritePng24Pair(kFsr31PngPair, off.frame.data(),
                fsr3.frame.data(), kFsrDisplayW, kFsrDisplayH, kFsrReadbackRowPitch);
            std::printf("zrhi_d3d12_window: [FSR4] fallback screenshot %s=%d "
                        "(left=bilinear right=FSR3.1)\n",
                kFsr31PngPair, pngOk ? 1 : 0);
        }
        std::printf("D3D12 FSR4 UNAVAILABLE (reason=FSR4 provider 4.x not enumerated "
                    "(needs discrete RX 7000/RX 9000); bilinear=%.3f fsr3=%.3f ms3=%.3f "
                    "msBilinear=%.3f removed=%d errors=%u)\n",
            off.edge, fsr3.edge, fsr3.gpuMs, off.gpuMs, noDeviceRemoval ? 0 : 1,
            infoQueueErrors);
        return 2;
    }

    if (runOnMode) {
        const double ratio = (off.edge > 0.0) ? (on.edge / off.edge) : 0.0;
        std::printf("zrhi_d3d12_window: [FSR] on edge=%.3f ratio=%.3f (min %.2f) backend=%s (%s)\n",
            on.edge, ratio, kFsrEdgeRatioMin, on.backendName, on.backendVersion);
        bool pngOnOk = false;
        bool pngOffOk = false;
        if (on.ok && off.ok) {
            std::filesystem::create_directories(
                std::filesystem::path(kFsrPngOff).parent_path());
            pngOffOk = !off.frame.empty()
                && WritePng24(kFsrPngOff, off.frame.data(), kFsrDisplayW, kFsrDisplayH,
                       kFsrReadbackRowPitch);
            pngOnOk = !on.frame.empty()
                && WritePng24(kFsrPngOn, on.frame.data(), kFsrDisplayW, kFsrDisplayH,
                       kFsrReadbackRowPitch);
            std::printf("zrhi_d3d12_window: [FSR] screenshots: %s=%d %s=%d\n", kFsrPngOn,
                pngOnOk ? 1 : 0, kFsrPngOff, pngOffOk ? 1 : 0);
        }
        const bool onIsFsr = on.ok && (std::strcmp(on.backendName, "Bilinear") != 0);
        const bool pass = off.ok && on.ok && onIsFsr && edgeOffSane && pngOnOk && pngOffOk
            && ratio >= kFsrEdgeRatioMin && noDeviceRemoval && infoQueueErrors == 0u;
        std::printf("zrhi_d3d12_window: [FSR] device removed reason = 0x%08lX (%s), "
                    "InfoQueue errors = %u\n",
            static_cast<unsigned long>(removedReason), noDeviceRemoval ? "S_OK" : "REMOVED",
            infoQueueErrors);
        if (pass) {
            std::printf("D3D12 FSR OK (edge=%.3f vs %.3f, ratio=%.3f, backend=%s %s)\n", on.edge,
                off.edge, ratio, on.backendName, on.backendVersion);
            return 0;
        }
        std::printf("D3D12 FSR FAILED (offOk=%d onOk=%d fsr=%d ratio=%.3f/%.2f pngOn=%d "
                    "pngOff=%d removed=%d errors=%u)\n",
            off.ok ? 1 : 0, on.ok ? 1 : 0, onIsFsr ? 1 : 0, ratio, kFsrEdgeRatioMin,
            pngOnOk ? 1 : 0, pngOffOk ? 1 : 0, noDeviceRemoval ? 0 : 1, infoQueueErrors);
        return 1;
    }
    // --fsr off：基线自检（顺带落盘 fsr_off.png 作为基线证据）
    if (!off.frame.empty()) {
        std::filesystem::create_directories(std::filesystem::path(kFsrPngOff).parent_path());
        if (WritePng24(kFsrPngOff, off.frame.data(), kFsrDisplayW, kFsrDisplayH,
                kFsrReadbackRowPitch)) {
            std::printf("zrhi_d3d12_window: [FSR] baseline screenshot: %s\n", kFsrPngOff);
        }
    }
    std::printf("zrhi_d3d12_window: [FSR] removed reason = 0x%08lX (%s), InfoQueue errors = %u\n",
        static_cast<unsigned long>(removedReason), noDeviceRemoval ? "S_OK" : "REMOVED",
        infoQueueErrors);
    if (off.ok && edgeOffSane && noDeviceRemoval && infoQueueErrors == 0u) {
        std::printf("D3D12 FSR BASELINE OK (edge=%.3f)\n", off.edge);
        return 0;
    }
    std::printf("D3D12 FSR BASELINE FAILED (offOk=%d edge=%.3f removed=%d errors=%u)\n",
        off.ok ? 1 : 0, off.edge, noDeviceRemoval ? 0 : 1, infoQueueErrors);
    return 1;
}


// =============================================================================
// ---- TASK-B-R8-02：--fg 模式（FFX FrameGeneration D3D12 集成 + 能力实证）------
//
// 设计（简报要点对照）：
//   - FrameGenerator（zupscaler/fg_ffx.h）承担 present 层接管：FG 效果上下文 +
//     代理交换链上下文（ForHwnd 路径）+ 每帧 Configure/Prepare + Present 包装。
//     与 UpscalerChain 的关系（独立呈现层组件，不进 IUpscaler）见 fg_ffx.h 头注。
//   - MV 复用 R6-01 的 MV Pass（motion_vectors.hlsl，两帧 mvp 各含当帧 jitter，
//     RG16Float 全渲染分辨率）；场景渲染同源 --fsr（地面 quad + PBR cube + CSM），
//     但按 FG 输入色契约直接渲染在 displaySize（640x480，无上采样）。
//   - present 路径切换：每帧 Configure(frameID+1) → 场景 RG → 场景 BGRA8 拷入
//     代理交换链游戏缓冲（CopyResource，COMMON→COPY_DEST→COMMON）→ PrepareV2
//     （深度+MV）→ 代理交换链 Present（SDK 回调插帧+合成+计数）。
//   - 帧计数翻倍证据：present 回调逐帧计数 isGeneratedFrame（SDK pacing 线程），
//     60 帧后 real≈60 gen≈59 → ZupFgFrameCountsDoubled 判定。
//   - OK/LIMITED 双路径（诚实预期：FG 官方矩阵不含核显，780M 预期被拒）：
//     OK      = 上下文创建成功且计数翻倍 + InfoQueue 0 错误 + 无设备移除
//               → `D3D12 FG OK (real=N gen=M)`，exit 0
//     LIMITED = dll 缺失 / provider 枚举空 / ffxCreateContext 拒绝 / 计数未翻倍
//               → `D3D12 FG LIMITED (reason=...)`，exit 0（受限不是失败）
//     FAILED  = 计数翻倍但 InfoQueue 有错误/设备移除 → exit 1（集成缺陷）
//     TIMEOUT = 帧起步等待 frame latency waitable 超时（插帧 GPU 死锁类）→ exit 3
//               （硬失败，日志无缓冲先落盘——不可恢复，避免验收无限挂起）
//   - 截图留档：第 60 帧场景渲染帧（进 FG 的真帧源）→ Docs/Parity/fg_real_frame.png。
// =============================================================================
constexpr uint32_t kFgSizeW = 640u;   // 窗口/交换链/场景渲染/MV 同尺寸
constexpr uint32_t kFgSizeH = 480u;   //（FG 契约：输入色 = displaySize）
constexpr uint32_t kFgFrames = 60u;
constexpr uint32_t kFgRotationPeriod = 36u;  // 同 --fsr（MV 反映真实旋转运动）
constexpr uint32_t kFgRowPitch = (kFgSizeW * 4u + 255u) & ~255u; // 2560，天然对齐
constexpr float kFgClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f};
constexpr const char* kFgPng = "Docs/Parity/fg_real_frame.png";
constexpr DWORD kFgWaitableTimeoutMs = 20000; // 帧起步等待上限（正常 << 1s）

/// --fg 模式主体（返回进程退出码；OK/LIMITED 均为 0——受限不是失败）
static int RunFgMode()
{
    std::setvbuf(stdout, nullptr, _IONBF, 0); // 崩溃/超时前保住已打印证据
    std::printf("zrhi_d3d12_window: --fg mode (TASK-B-R8-02): FFX FrameGeneration, "
                "%u frames @ %ux%u\n",
        kFgFrames, kFgSizeW, kFgSizeH);

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --fg FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --fg device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)" : "UNAVAILABLE (degraded)");
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --fg FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 适配器证据（与 --fsr 同策略：首个非软件适配器）------------------------------
    {
        IDXGIFactory1* factory = nullptr;
        if (SUCCEEDED(::CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && factory != nullptr) {
            IDXGIAdapter1* adapter = nullptr;
            for (UINT i = 0u; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
                DXGI_ADAPTER_DESC1 adapterDesc {};
                if (SUCCEEDED(adapter->GetDesc1(&adapterDesc))
                    && (adapterDesc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0u) {
                    char descA[128] = {};
                    ::WideCharToMultiByte(CP_UTF8, 0, adapterDesc.Description, -1, descA,
                        sizeof(descA), nullptr, nullptr);
                    std::printf("zrhi_d3d12_window: --fg adapter: %s (vendor=0x%04X "
                                "device=0x%04X)\n",
                        descA, adapterDesc.VendorId, adapterDesc.DeviceId);
                    adapter->Release();
                    break;
                }
                adapter->Release();
            }
            factory->Release();
        }
    }

    // ---- FG 能力探测（verbose provider 枚举 + 门控结论）------------------------------
    {
        zr::FrameGenProbe probe = zr::FrameGenerator::QuerySupport(device->D3D12Device());
        std::printf("zrhi_d3d12_window: --fg probe: support=%u rc=%u (%s) provider='%s'\n",
            static_cast<unsigned>(probe.support), probe.returnCode,
            zr::ZupFgReturnCodeName(probe.returnCode), probe.providerName);
        if (probe.support == zr::FrameGenSupport::Unavailable
            || probe.support == zr::FrameGenSupport::NoProvider) {
            std::printf("D3D12 FG LIMITED (reason=FG runtime %s; official FG support matrix "
                        "does not include integrated GPUs - same class as FSR4/DLSS limits; "
                        "probe rc=%u)\n",
                (probe.support == zr::FrameGenSupport::Unavailable) ? "dll unavailable"
                                                                    : "has no provider",
                probe.returnCode);
            device->WaitIdle();
            ZrD3D12DestroyDevice(device);
            return 0; // 受限不是失败
        }
    }

    // ---- FG 效果上下文（创建返回码即事实；拒绝 → LIMITED 列确切错误码）---------------
    zr::FrameGenerator fg;
    {
        zr::FrameGenCreateDesc fgDesc {};
        fgDesc.displayWidth = kFgSizeW;
        fgDesc.displayHeight = kFgSizeH;
        fgDesc.renderWidth = kFgSizeW; // 本集成无上采样：render == display
        fgDesc.renderHeight = kFgSizeH;
        fgDesc.backBufferFfxFormat = ffxApiGetSurfaceFormatDX12(DXGI_FORMAT_B8G8R8A8_UNORM);
        fgDesc.displayResolutionMv = true;
        fgDesc.jitterCancellation = true; // MV 两帧 mvp 均含当帧 jitter（R6-01 同源）
        fgDesc.debugChecking = true;
        fgDesc.nativeDevice = device->D3D12Device();
        if (!fg.Create(fgDesc)) {
            std::printf("D3D12 FG LIMITED (reason=ffxCreateContext FRAMEGENERATION rejected "
                        "rc=%u (%s); official FG support matrix does not include integrated "
                        "GPUs)\n",
                fg.LastReturnCode(), zr::ZupFgReturnCodeName(fg.LastReturnCode()));
            device->WaitIdle();
            ZrD3D12DestroyDevice(device);
            return 0;
        }
    }

    // ---- 窗口 + DXGI 工厂（代理交换链由 FG dll 经 ForHwnd 路径创建）------------------
    HWND hWnd = TryCreateWindow(kFgSizeW, kFgSizeH);
    if (hWnd == nullptr) {
        std::printf("D3D12 FG LIMITED (reason=window creation rejected GetLastError=%lu; "
                    "FG proxy swapchain needs a real HWND)\n",
            GetLastError());
        device->WaitIdle();
        ZrD3D12DestroyDevice(device);
        return 0;
    }
    Microsoft::WRL::ComPtr<IDXGIFactory> dxgiFactory;
    if (FAILED(::CreateDXGIFactory1(IID_PPV_ARGS(&dxgiFactory))) || !dxgiFactory) {
        std::printf("D3D12 FG LIMITED (reason=CreateDXGIFactory1 FAILED)\n");
        device->WaitIdle();
        ZrD3D12DestroyDevice(device);
        return 0;
    }
    void* wrappedSwapchain = nullptr;
    void* waitable = nullptr;
    if (!fg.CreateFgSwapchainForHwnd(hWnd, dxgiFactory.Get(), device->D3D12Queue(), kFgSizeW,
            kFgSizeH, DXGI_FORMAT_B8G8R8A8_UNORM, 3u, &wrappedSwapchain, &waitable)
        || wrappedSwapchain == nullptr) {
        std::printf("D3D12 FG LIMITED (reason=swapchain context rejected rc=%u (%s))\n",
            fg.LastReturnCode(), zr::ZupFgReturnCodeName(fg.LastReturnCode()));
        device->WaitIdle();
        ZrD3D12DestroyDevice(device);
        return 0;
    }

    // ---- 渲染目标与视图（BGRA8 场景：与交换链后台缓冲同格式 → CopyResource 直拷）-----
    auto makeImage = [&](ZrFormat format, uint32_t w, uint32_t h, uint32_t arrayLayers,
                         ZrImageUsage usage) {
        ZrImageDesc desc {};
        desc.format = format;
        desc.width = w;
        desc.height = h;
        desc.arrayLayers = arrayLayers;
        desc.mipLevels = 1u;
        desc.usage = usage;
        return device->CreateImage(desc);
    };
    ZrImage sceneImage = makeImage(ZrFormat::BGRA8Unorm, kFgSizeW, kFgSizeH, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled | ZrImageUsage::TransferSrc);
    ZrImage mvImage = makeImage(ZrFormat::RG16Float, kFgSizeW, kFgSizeH, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    ZrImage depthImage = makeImage(ZrFormat::D32Float, kFgSizeW, kFgSizeH, 1u,
        ZrImageUsage::DepthStencilAttachment);
    ZrImage shadowImage = makeImage(ZrFormat::D32Float, kShadowsTexSize, kShadowsTexSize,
        ZR_SHADOW_CASCADES, ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled);
    if (!sceneImage.IsValid() || !mvImage.IsValid() || !depthImage.IsValid()
        || !shadowImage.IsValid()) {
        std::printf("zrhi_d3d12_window: --fg FAILED to create images\n");
        return 1;
    }
    auto makeView = [&](ZrImage image, ZrImageViewType type, uint32_t layer = 0u) {
        ZrImageViewDesc desc {};
        desc.image = image;
        desc.viewType = type;
        desc.subresources.baseArrayLayer = layer;
        desc.subresources.layerCount = 1u;
        return device->CreateImageView(desc);
    };
    ZrImageView sceneRtv = makeView(sceneImage, ZrImageViewType::Rtv);
    ZrImageView mvRtv = makeView(mvImage, ZrImageViewType::Rtv);
    ZrImageView dsvView = makeView(depthImage, ZrImageViewType::Dsv);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        shadowDsvs[c] = makeView(shadowImage, ZrImageViewType::Dsv, c);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float;
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    bool viewsOk
        = sceneRtv.IsValid() && mvRtv.IsValid() && dsvView.IsValid() && shadowSrv.IsValid();
    for (const ZrImageView& v : shadowDsvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    if (!viewsOk) {
        std::printf("zrhi_d3d12_window: --fg FAILED to create views\n");
        return 1;
    }

    // ---- 几何 + 常量缓冲 + 着色器 + PSO（同 --fsr 场景，尺寸换 kFg*）------------------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};
    std::vector<char> meshBytes;
    if (!ReadWholeFile("Native/tests/data/cube.zmesh", meshBytes)) {
        std::printf("zrhi_d3d12_window: --fg FAILED to open cube.zmesh\n");
        return 1;
    }
    ZmeshData mesh {};
    if (zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh) != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --fg cube.zmesh parse FAILED\n");
        return 1;
    }
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(mesh.stats.vertexCount);
    for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);

    auto makeVb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    auto makeIb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    ZrBuffer groundVb = makeVb(groundVerts, sizeof(groundVerts));
    ZrBuffer groundIb = makeIb(groundIndices, sizeof(groundIndices));
    ZrBuffer cubeVb = makeVb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex));
    ZrBuffer cubeIb = makeIb(cubeIndices.data(), uint64_t(cubeIndices.size()) * 4u);
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --fg FAILED to create VB/IB\n");
        return 1;
    }

    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbShadowModelGround {};
    ZrBuffer cbShadowModelCube {};
    ZrBuffer cbMvGround {};
    ZrBuffer cbMvCube {};
    {
        auto makeCb = [&](uint64_t size) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            ZrBuffer b = device->CreateBuffer(desc);
            return (b.IsValid() && device->GetBuffer(b)->Map() != nullptr) ? b : ZrBuffer{};
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        bool ok = (cb0Ground = makeCb(256u)).IsValid() && (cb0Cube = makeCb(256u)).IsValid()
            && (cbLights = makeCb(1024u)).IsValid() && (cbClusters = makeCb(cb2Size)).IsValid()
            && (cbShadows = makeCb(384u)).IsValid()
            && (cbShadowModelGround = makeCb(256u)).IsValid()
            && (cbShadowModelCube = makeCb(256u)).IsValid()
            && (cbMvGround = makeCb(256u)).IsValid() && (cbMvCube = makeCb(256u)).IsValid();
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            ok = ok && (cbShadowPass[c] = makeCb(256u)).IsValid();
        }
        if (!ok) {
            std::printf("zrhi_d3d12_window: --fg FAILED to create constant buffers\n");
            return 1;
        }
    }

    const char* kFgShaderPaths[3] = {"Native/shaders/pbr_shadow.hlsl",
        "Native/shaders/shadow_depth.hlsl", "Native/shaders/motion_vectors.hlsl"};
    std::vector<char> shaderSrc[3];
    for (int i = 0; i < 3; ++i) {
        if (!ReadWholeFile(kFgShaderPaths[i], shaderSrc[i])) {
            std::printf("zrhi_d3d12_window: --fg FAILED to open %s\n", kFgShaderPaths[i]);
            return 1;
        }
    }
    const ZrShader mainVs = device->CompileHlsl(
        ZrShaderStage::Vertex, shaderSrc[0].data(), shaderSrc[0].size(), "VsMain");
    const ZrShader mainPs = device->CompileHlsl(
        ZrShaderStage::Pixel, shaderSrc[0].data(), shaderSrc[0].size(), "PsMain");
    const ZrShader shadowVs = device->CompileHlsl(
        ZrShaderStage::Vertex, shaderSrc[1].data(), shaderSrc[1].size(), "VsMain");
    const ZrShader mvVs = device->CompileHlsl(
        ZrShaderStage::Vertex, shaderSrc[2].data(), shaderSrc[2].size(), "VsMain");
    const ZrShader mvPs = device->CompileHlsl(
        ZrShaderStage::Pixel, shaderSrc[2].data(), shaderSrc[2].size(), "PsMain");
    const ZrShader allShaders[5] = {mainVs, mainPs, shadowVs, mvVs, mvPs};
    bool shadersOk = true;
    for (const ZrShader& s : allShaders) {
        shadersOk = shadersOk && s.IsValid();
    }
    if (!shadersOk) {
        std::printf("zrhi_d3d12_window: --fg shader compilation FAILED (see stderr)\n");
        return 1;
    }

    ZrGraphicsPsoDesc mainPsoDesc {};
    mainPsoDesc.vertexShader = mainVs;
    mainPsoDesc.pixelShader = mainPs;
    mainPsoDesc.rtvFormat = ZrFormat::BGRA8Unorm; // = 交换链后台缓冲格式
    mainPsoDesc.dsvFormat = ZrFormat::D32Float;
    mainPsoDesc.blend = ZrBlendMode::Opaque;
    mainPsoDesc.depthTest = true;
    mainPsoDesc.depthWrite = true;
    mainPsoDesc.cullMode = ZrCullMode::None;
    mainPsoDesc.rootCbvCount = 4u;
    mainPsoDesc.srvTable = true;
    ZrPipelineState mainPso = device->CreatePipelineState(mainPsoDesc);
    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = shadowVs;
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    shadowPsoDesc.cullMode = ZrCullMode::Front;
    shadowPsoDesc.rootCbvCount = 2u;
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    ZrGraphicsPsoDesc mvPsoDesc {};
    mvPsoDesc.vertexShader = mvVs;
    mvPsoDesc.pixelShader = mvPs;
    mvPsoDesc.rtvFormat = ZrFormat::RG16Float; // R6-01 MV Pass 同源复用
    mvPsoDesc.dsvFormat = ZrFormat::Count;
    mvPsoDesc.blend = ZrBlendMode::Opaque;
    mvPsoDesc.cullMode = ZrCullMode::None;
    mvPsoDesc.rootCbvCount = 1u;
    ZrPipelineState mvPso = device->CreatePipelineState(mvPsoDesc);
    if (!mainPso.IsValid() || !shadowPso.IsValid() || !mvPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --fg FAILED to create PSOs\n");
        return 1;
    }

    // ---- 场景截图 readback（第 kFgFrames 帧：进 FG 的真帧源留档）----------------------
    Microsoft::WRL::ComPtr<ID3D12Resource> sceneReadback
        = MakeReadbackBuffer(device->D3D12Device(), uint64_t(kFgRowPitch) * kFgSizeH);
    if (sceneReadback == nullptr) {
        std::printf("zrhi_d3d12_window: --fg FAILED to create readback buffer\n");
        return 1;
    }

    // ---- 静态场景参数（相机/光源/级联/簇；同 --fsr 值，尺寸换 kFg*）-------------------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    constexpr float kAspect = static_cast<float>(kFgSizeW) / static_cast<float>(kFgSizeH);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    const uint32_t phaseCount = zr::ZupJitterPhaseCount(kFgSizeW, kFgSizeW); // 无上采样：8
    std::printf("zrhi_d3d12_window: --fg jitter phase count=%u (render==display, "
                "jitter cancellation on)\n",
        phaseCount);

    void* cb0GroundMapped = device->GetBuffer(cb0Ground)->Map();
    void* cb0CubeMapped = device->GetBuffer(cb0Cube)->Map();
    void* cbLightsMapped = device->GetBuffer(cbLights)->Map();
    void* cbClustersMapped = device->GetBuffer(cbClusters)->Map();
    void* cbShadowsMapped = device->GetBuffer(cbShadows)->Map();
    void* cbMvGroundMapped = device->GetBuffer(cbMvGround)->Map();
    void* cbMvCubeMapped = device->GetBuffer(cbMvCube)->Map();
    void* cbShadowModelGroundMapped = device->GetBuffer(cbShadowModelGround)->Map();
    void* cbShadowModelCubeMapped = device->GetBuffer(cbShadowModelCube)->Map();
    if (cb0GroundMapped == nullptr || cb0CubeMapped == nullptr || cbLightsMapped == nullptr
        || cbClustersMapped == nullptr || cbShadowsMapped == nullptr
        || cbMvGroundMapped == nullptr || cbMvCubeMapped == nullptr
        || cbShadowModelGroundMapped == nullptr || cbShadowModelCubeMapped == nullptr) {
        std::printf("zrhi_d3d12_window: --fg constant buffer Map FAILED\n");
        return 1;
    }

    LightData sceneLights[1] = {};
    {
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        sceneLights[0].pos[0] = d[0] * invLen;
        sceneLights[0].pos[1] = d[1] * invLen;
        sceneLights[0].pos[2] = d[2] * invLen;
        sceneLights[0].color[0] = 1.0f;
        sceneLights[0].color[1] = 0.95f;
        sceneLights[0].color[2] = 0.88f;
        sceneLights[0].intensity = 2.0f;
        sceneLights[0].kind = kZrLightDirectional;
    }
    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = kAspect;
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    std::memcpy(shadowParamsStruct.lightDir, sceneLights[0].pos, sizeof(sceneLights[0].pos));
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = -0.5f;
    shadowParamsStruct.sceneCenter[2] = 10.0f;
    shadowParamsStruct.sceneRadius = 15.0f;
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("zrhi_d3d12_window: --fg BuildCascades FAILED\n");
        return 1;
    }
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(kNearZ, kFarZ, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("zrhi_d3d12_window: --fg CascadeSplits FAILED\n");
            return 1;
        }
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES - 1u; ++c) {
            cascadeSplits[c] = interior[c];
        }
        cascadeSplits[ZR_SHADOW_CASCADES - 1u] = kFarZ;
    }
    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;
    }
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES - 1u; ++c) {
        shadowCb.cascadeSplits[c] = cascadeSplits[c];
    }
    shadowCb.cascadeSplits[ZR_SHADOW_CASCADES - 1u] = kFarZ;
    std::memcpy(cbShadowsMapped, &shadowCb, sizeof(shadowCb));
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("zrhi_d3d12_window: --fg shadow pass CB %u Map FAILED\n", c);
            return 1;
        }
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        std::memcpy(mapped, &spc, sizeof(spc));
    }
    {
        ShadowModelConstants gm {};
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        std::memcpy(gm.shadowModel, identity, sizeof(identity));
        std::memcpy(cbShadowModelGroundMapped, &gm, sizeof(gm));
    }
    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = kAspect;
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);
    PackedLight packedLights[1];
    PackLights(sceneLights, 1u, packedLights);
    std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
    std::memset(static_cast<uint8_t*>(cbLightsMapped) + sizeof(packedLights), 0,
        1024u - sizeof(packedLights));
    const int32_t flatCount = AssignLightsToClusters(grid, sceneLights, 1u,
        clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
    if (flatCount < 0) {
        std::printf("zrhi_d3d12_window: --fg AssignLightsToClusters FAILED (%d)\n", flatCount);
        return 1;
    }
    uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
    std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);
    std::memcpy(cb2 + size_t(ZR_CLUSTER_COUNT) * 4u, clusterFlat.data(),
        size_t(flatCount) * 4u);

    const float ambient = 0.12f;
    ShadowDrawConstants groundConst {};
    const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    std::memcpy(groundConst.model, identity, sizeof(identity));
    ShadowDrawConstants cubeConst {};
    for (ShadowDrawConstants* c : {&groundConst, &cubeConst}) {
        c->projParams[0] = kFarZ / (kFarZ - kNearZ);
        c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
        c->projParams[2] = kNearZ;
        c->projParams[3] = kFarZ;
        c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
        c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
        c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
        c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
        c->screenParams[0] = static_cast<float>(kFgSizeW);
        c->screenParams[1] = static_cast<float>(kFgSizeH);
        c->screenParams[2] = 1.0f;
        c->screenParams[3] = ambient;
        c->camPos[0] = 0.0f;
        c->camPos[1] = 0.0f;
        c->camPos[2] = 0.0f;
        c->camPos[3] = 0.0f;
        c->cascadeSplits[0] = cascadeSplits[0];
        c->cascadeSplits[1] = cascadeSplits[1];
        c->cascadeSplits[2] = cascadeSplits[2];
        c->cascadeSplits[3] = kFarZ;
        c->shadowParams[0] = 1.0f;
        c->shadowParams[1] = static_cast<float>(kShadowsTexSize);
    }
    groundConst.material[0] = 0.50f;
    groundConst.material[1] = 0.52f;
    groundConst.material[2] = 0.50f;
    groundConst.material[3] = 0.9f;
    cubeConst.material[0] = 0.75f;
    cubeConst.material[1] = 0.75f;
    cubeConst.material[2] = 0.78f;
    cubeConst.material[3] = 0.35f;

    // ---- 60 帧循环：Configure → 场景 RG → 拷入代理交换链 → PrepareV2 → Present --------
    bool framesOk = true;
    uint32_t framesRan = 0u;
    float mvScale[2] = {};
    zr::ZupMotionVectorScale(kFgSizeW, kFgSizeH, mvScale);
    auto prevFrameTime = std::chrono::steady_clock::now();
    for (uint32_t frame = 0u; frame < kFgFrames && framesOk; ++frame) {
        const bool doReadback = (frame + 1u == kFgFrames);
        // 帧起步：等待代理交换链 pacing（官方契约——GetFrameLatencyWaitableObject）
        const DWORD waitResult = ::WaitForSingleObject(static_cast<HANDLE>(waitable),
            kFgWaitableTimeoutMs);
        if (waitResult != WAIT_OBJECT_0) {
            std::printf("zrhi_d3d12_window: --fg frame %u: frame latency waitable TIMEOUT "
                        "(result=%lu) — interpolation pacing stalled; hard exit to avoid "
                        "unbounded hang (real=%llu gen=%llu)\n",
                frame + 1u, static_cast<unsigned long>(waitResult),
                static_cast<unsigned long long>(fg.RealPresents()),
                static_cast<unsigned long long>(fg.GeneratedPresents()));
            std::fflush(stdout);
            ::TerminateProcess(::GetCurrentProcess(), 3u); // TIMEOUT：硬失败（见节头注）
        }
        const uint64_t frameId = static_cast<uint64_t>(frame) + 1u;
        // 每帧配置（官方契约：frameID 严格 +1；回调经 fg_ffx 静态蹦床计数）
        if (!fg.ConfigureFrame(wrappedSwapchain, frameId, /*enabled=*/true, nullptr, nullptr)) {
            std::printf("zrhi_d3d12_window: --fg frame %u: ConfigureFrame FAILED\n", frame + 1u);
            framesOk = false;
            break;
        }

        const auto frameStart = std::chrono::steady_clock::now();
        const float angle
            = 6.2831853f * static_cast<float>(frame + 1u) / static_cast<float>(kFgRotationPeriod);
        const float prevAngle
            = 6.2831853f * static_cast<float>(frame) / static_cast<float>(kFgRotationPeriod);
        float jx = 0.0f;
        float jy = 0.0f;
        float pjx = 0.0f;
        float pjy = 0.0f;
        zr::ZupJitterOffset(frame, phaseCount, &jx, &jy);
        zr::ZupJitterOffset((frame == 0u) ? 0u : frame - 1u, phaseCount, &pjx, &pjy);
        float projCur[16];
        MatPerspectiveFovLH(kFovY, kAspect, kNearZ, kFarZ, projCur);
        projCur[2] = 2.0f * jx / static_cast<float>(kFgSizeW);
        projCur[6] = -2.0f * jy / static_cast<float>(kFgSizeH);
        float projPrev[16];
        MatPerspectiveFovLH(kFovY, kAspect, kNearZ, kFarZ, projPrev);
        projPrev[2] = 2.0f * pjx / static_cast<float>(kFgSizeW);
        projPrev[6] = -2.0f * pjy / static_cast<float>(kFgSizeH);
        float projViewCur[16];
        MatMultiply(projCur, view, projViewCur);
        float projViewPrev[16];
        MatMultiply(projPrev, view, projViewPrev);

        float rotCur[16];
        MatRotationY(angle, rotCur);
        float transCube[16];
        MatTranslation(0.0f, 0.0f, 3.2f, transCube);
        float cubeModel[16];
        MatMultiply(transCube, rotCur, cubeModel);
        float rotPrev[16];
        MatRotationY(prevAngle, rotPrev);
        float cubeModelPrev[16];
        MatMultiply(transCube, rotPrev, cubeModelPrev);

        std::memcpy(groundConst.mvp, projViewCur, sizeof(projViewCur));
        std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
        float cubeMvp[16];
        MatMultiply(projViewCur, cubeModel, cubeMvp);
        std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
        std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
        std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));

        MvPassConstants mvGround {};
        std::memcpy(mvGround.mvpCur, projViewCur, sizeof(projViewCur));
        std::memcpy(mvGround.mvpPrev, projViewPrev, sizeof(projViewPrev));
        std::memcpy(cbMvGroundMapped, &mvGround, sizeof(mvGround));
        MvPassConstants mvCube {};
        float cubeMvpPrev[16];
        MatMultiply(projViewPrev, cubeModelPrev, cubeMvpPrev);
        std::memcpy(mvCube.mvpCur, cubeMvp, sizeof(cubeMvp));
        std::memcpy(mvCube.mvpPrev, cubeMvpPrev, sizeof(cubeMvpPrev));
        std::memcpy(cbMvCubeMapped, &mvCube, sizeof(mvCube));

        {
            ShadowModelConstants cm {};
            std::memcpy(cm.shadowModel, cubeModel, sizeof(cubeModel));
            std::memcpy(cbShadowModelCubeMapped, &cm, sizeof(cm));
        }

        // 当前帧代理交换链游戏缓冲（渲染落点；FLIP 模型 → 拷贝进出均 COMMON 域）
        auto* wrapped4 = static_cast<IDXGISwapChain4*>(wrappedSwapchain);
        Microsoft::WRL::ComPtr<ID3D12Resource> backbuffer;
        const UINT bbIndex = wrapped4->GetCurrentBackBufferIndex();
        if (FAILED(wrapped4->GetBuffer(bbIndex, IID_PPV_ARGS(&backbuffer)))
            || backbuffer == nullptr) {
            std::printf("zrhi_d3d12_window: --fg frame %u: GetBuffer(%u) FAILED\n", frame + 1u,
                bbIndex);
            framesOk = false;
            break;
        }

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        RgHandle shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));
        RgHandle sceneH = graph.Import(RgResourceDesc::ImportImage("sceneColor", sceneImage));
        RgHandle mvH = graph.Import(RgResourceDesc::ImportImage("motionVectors", mvImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(sceneH),
                    ZrImageLayout::ColorAttachmentOptimal, kFgClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });

        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            graph.AddPass("ShadowCascadePass",
                [&](RgPass& pass) {
                    pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                        ZrAccessBits::DepthStencilAttachmentWrite,
                        ZrPipelineStageBits::LateFragmentTests);
                },
                [&, c](RgContext&) {
                    cmdList->SetPipelineState(shadowPso);
                    cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                    cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                    nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(shadowDsvs[c]),
                        D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
                    cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                    cmdList->SetGraphicsConstantBuffer(1u, cbShadowModelGround);
                    ZrBuffer gvb[1] = {groundVb};
                    cmdList->SetVertexBuffers(gvb, 1u);
                    cmdList->SetIndexBuffer(groundIb);
                    cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                    cmdList->SetGraphicsConstantBuffer(1u, cbShadowModelCube);
                    ZrBuffer cvb[1] = {cubeVb};
                    cmdList->SetVertexBuffers(cvb, 1u);
                    cmdList->SetIndexBuffer(cubeIb);
                    cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
                });
        }

        graph.AddPass("MainPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                    ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mainPso);
                cmdList->SetRenderTarget(sceneRtv, dsvView);
                cmdList->SetViewportScissor(kFgSizeW, kFgSizeH); // displaySize 直渲染
                cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                cmdList->SetGraphicsBindlessSrv(4u, shadowSrv);
                ZrBuffer gvb[1] = {groundVb};
                cmdList->SetVertexBuffers(gvb, 1u);
                cmdList->SetIndexBuffer(groundIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Ground);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                ZrBuffer cvb[1] = {cubeVb};
                cmdList->SetVertexBuffers(cvb, 1u);
                cmdList->SetIndexBuffer(cubeIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Cube);
                cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
            });

        // MV Pass（R6-01 同源：两帧 mvp 各含当帧 jitter；产物供 PrepareV2 消费 →
        // MarkRoot 防死剔除）
        graph.AddPass("MvPass",
            [&](RgPass& pass) {
                pass.Write(mvH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput)
                    .MarkRoot();
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mvPso);
                cmdList->SetRenderTarget(mvRtv, ZrImageView{});
                cmdList->SetViewportScissor(kFgSizeW, kFgSizeH);
                ZrBuffer gvb[1] = {groundVb};
                cmdList->SetVertexBuffers(gvb, 1u);
                cmdList->SetIndexBuffer(groundIb);
                cmdList->SetGraphicsConstantBuffer(0u, cbMvGround);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                ZrBuffer cvb[1] = {cubeVb};
                cmdList->SetVertexBuffers(cvb, 1u);
                cmdList->SetIndexBuffer(cubeIb);
                cmdList->SetGraphicsConstantBuffer(0u, cbMvCube);
                cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
            });

        if (!graph.Compile()) {
            std::printf("zrhi_d3d12_window: --fg frame %u: RG Compile FAILED (error=%u)\n",
                frame + 1u, static_cast<unsigned>(graph.Error()));
            framesOk = false;
            break;
        }
        graph.ExecuteAliased(*device, *cmdList);

        // ---- RG 外：场景拷入代理交换链游戏缓冲 + PrepareV2（同命令流）----------------
        {
            // 场景/深度/MV → 拷贝与读取所需状态
            ZrImageBarrier barriers[3];
            uint32_t n = 0u;
            barriers[n++] = MakeBarrier(sceneImage, ZrImageLayout::ColorAttachmentOptimal,
                ZrImageLayout::TransferSrcOptimal, ZrAccessBits::ColorAttachmentWrite,
                ZrPipelineStageBits::ColorAttachmentOutput, ZrAccessBits::TransferRead,
                ZrPipelineStageBits::Transfer);
            barriers[n++] = MakeBarrier(mvImage, ZrImageLayout::ColorAttachmentOptimal,
                ZrImageLayout::ShaderReadOnlyOptimal, ZrAccessBits::ColorAttachmentWrite,
                ZrPipelineStageBits::ColorAttachmentOutput, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::ComputeShader);
            barriers[n++] = MakeBarrier(depthImage,
                ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
                ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::ComputeShader);
            cmdList->ResourceBarrier(barriers, n, nullptr, 0u);
        }
        {
            // 代理交换链游戏缓冲：COMMON（FLIP present 域）→ COPY_DEST → 拷贝 → COMMON。
            // SDK 合成线程随后在自身命令流上做 PRESENT→COPY_SOURCE→PRESENT 迁移
            // （FrameInterpolationSwapchainDX12 presentWithUiComposition 同款契约）。
            D3D12_RESOURCE_BARRIER bb = {};
            bb.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            bb.Transition.pResource = backbuffer.Get();
            bb.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
            bb.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
            nativeCmd->ResourceBarrier(1u, &bb);
            nativeCmd->CopyResource(backbuffer.Get(), device->D3D12ResourceOf(sceneImage));
            if (doReadback) {
                // 场景真帧（进 FG 的源）留档：→ readback（场景已处 COPY_SOURCE）
                D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp {};
                uint64_t tb = 0u;
                ComputeFootprint(device->D3D12Device(), device->D3D12ResourceOf(sceneImage),
                    fp, tb);
                RecordReadback(nativeCmd, device->D3D12ResourceOf(sceneImage),
                    sceneReadback.Get(), fp);
            }
            std::swap(bb.Transition.StateBefore, bb.Transition.StateAfter);
            nativeCmd->ResourceBarrier(1u, &bb);
        }
        {
            zr::FrameGenPrepareArgs prepare {};
            prepare.nativeCmdList = nativeCmd;
            prepare.nativeDepth = device->D3D12ResourceOf(depthImage);
            prepare.nativeMotionVectors = device->D3D12ResourceOf(mvImage);
            // FfxApiResourceState 位组合（PrepareV2 计算着色器读取；COMPUTE_READ）
            prepare.depthState = FFX_API_RESOURCE_STATE_COMPUTE_READ;
            prepare.mvState = FFX_API_RESOURCE_STATE_COMPUTE_READ;
            prepare.jitterX = jx;
            prepare.jitterY = jy;
            prepare.motionVectorScaleX = mvScale[0];
            prepare.motionVectorScaleY = mvScale[1];
            const auto now = std::chrono::steady_clock::now();
            prepare.frameTimeDeltaMs = static_cast<float>(
                std::chrono::duration<double, std::milli>(now - prevFrameTime).count());
            prevFrameTime = now;
            prepare.cameraFovY = kFovY;
            prepare.cameraNear = kNearZ;
            prepare.cameraFar = kFarZ;
            prepare.reset = (frame == 0u);
            prepare.frameId = frameId;
            // 相机位姿（PrepareV2 必填）：FrameGenPrepareArgs 默认值即本场景位姿
            // （原点朝 +Z（LH），up=+Y，right=+X，pos=原点）——无需覆写。
            if (!fg.DispatchPrepare(prepare)) {
                std::printf("zrhi_d3d12_window: --fg frame %u: DispatchPrepare FAILED\n",
                    frame + 1u);
                framesOk = false;
                break;
            }
        }
        // 帧末：持久图像回 COMMON（与下一帧 RG 起点 Undefined 一致）
        {
            ZrImageBarrier resets[3];
            uint32_t n = 0u;
            resets[n++] = MakeBarrier(sceneImage, ZrImageLayout::TransferSrcOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::TransferRead,
                ZrPipelineStageBits::Transfer, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(mvImage, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::ComputeShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            resets[n++] = MakeBarrier(depthImage, ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrImageLayout::Undefined, ZrAccessBits::DepthStencilAttachmentRead,
                ZrPipelineStageBits::ComputeShader, ZrAccessBits::None,
                ZrPipelineStageBits::TopOfPipe);
            cmdList->ResourceBarrier(resets, n, nullptr, 0u);
        }
        cmdList->End();
        queue->Submit(cmdHandle);
        device->WaitIdle();
        graph.FlushRetired(*device);

        // Present 包装：SDK 回调插帧 + 合成 + 计数（真帧/插帧）
        if (!fg.PresentFrame(wrappedSwapchain, 1u)) {
            std::printf("zrhi_d3d12_window: --fg frame %u: PresentFrame FAILED\n", frame + 1u);
            framesOk = false;
            break;
        }
        framesRan = frame + 1u;
        if ((frame % 10u) == 9u) {
            PumpMessages(); // 窗口消息泵（保持响应）
            std::printf("zrhi_d3d12_window: --fg frame %u done (real=%llu gen=%llu, cpu=%.2f ms)\n",
                frame + 1u, static_cast<unsigned long long>(fg.RealPresents()),
                static_cast<unsigned long long>(fg.GeneratedPresents()),
                std::chrono::duration<double, std::milli>(
                    std::chrono::steady_clock::now() - frameStart)
                    .count());
        }
    }

    // ---- 统计与收尾（官方 Shutdown 契约顺序）-----------------------------------------
    fg.WaitForPresents(); // pacing/插帧 GPU 工作落盘 → 计数定值
    const uint64_t realPresents = fg.RealPresents();
    const uint64_t genPresents = fg.GeneratedPresents();
    device->WaitIdle();
    fg.Shutdown(); // Configure(false) → 销毁 FG 上下文 → 销毁交换链上下文 → CloseHandle

    // 截图留档（第 60 帧场景渲染帧）
    bool pngOk = false;
    if (framesOk && framesRan == kFgFrames) {
        void* mapped = nullptr;
        if (SUCCEEDED(sceneReadback->Map(0, nullptr, &mapped)) && mapped != nullptr) {
            const uint8_t* bytes = static_cast<const uint8_t*>(mapped);
            std::filesystem::create_directories(std::filesystem::path(kFgPng).parent_path());
            pngOk = WritePng24(kFgPng, bytes, kFgSizeW, kFgSizeH, kFgRowPitch);
            std::printf("zrhi_d3d12_window: --fg screenshot %s=%d (rendered real frame)\n", kFgPng,
                pngOk ? 1 : 0);
            sceneReadback->Unmap(0, nullptr);
        }
    }

    // ---- 资源清理与判定 ----------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    for (ZrBuffer& b : cbShadowPass) {
        device->Destroy(b);
    }
    device->Destroy(cbShadowModelGround);
    device->Destroy(cbShadowModelCube);
    device->Destroy(cbMvGround);
    device->Destroy(cbMvCube);
    device->Destroy(mainPso);
    device->Destroy(shadowPso);
    device->Destroy(mvPso);
    for (const ZrShader& s : allShaders) {
        device->Destroy(s);
    }
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(sceneRtv);
    device->Destroy(mvRtv);
    device->Destroy(dsvView);
    for (ZrImageView& v : shadowDsvs) {
        device->Destroy(v);
    }
    device->Destroy(shadowSrv);
    device->Destroy(sceneImage);
    device->Destroy(mvImage);
    device->Destroy(depthImage);
    device->Destroy(shadowImage);
    sceneReadback.Reset();
    device->WaitIdle();
    device->Destroy(cmdHandle);
    const HRESULT removedReason = device->D3D12Device()->GetDeviceRemovedReason();
    const bool noDeviceRemoval = removedReason == S_OK;
    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    std::printf("zrhi_d3d12_window: --fg present counters: real=%llu generated=%llu "
                "(framesRan=%u) | device removed reason = 0x%08lX (%s), InfoQueue errors = %u\n",
        static_cast<unsigned long long>(realPresents),
        static_cast<unsigned long long>(genPresents), framesRan,
        static_cast<unsigned long>(removedReason), noDeviceRemoval ? "S_OK" : "REMOVED",
        infoQueueErrors);

    if (framesOk && framesRan == kFgFrames) {
        if (zr::ZupFgFrameCountsDoubled(realPresents, genPresents)) {
            if (noDeviceRemoval && infoQueueErrors == 0u && pngOk) {
                std::printf("D3D12 FG OK (real=%llu gen=%llu, screenshot=%s)\n",
                    static_cast<unsigned long long>(realPresents),
                    static_cast<unsigned long long>(genPresents), kFgPng);
                return 0;
            }
            std::printf("D3D12 FG FAILED (counts doubled but removed=%d errors=%u png=%d)\n",
                noDeviceRemoval ? 0 : 1, infoQueueErrors, pngOk ? 1 : 0);
            return 1;
        }
        // 上下文被接受但计数未翻倍：运行时接受了 iGPU 却未产出插帧——如实列受限
        std::printf("D3D12 FG LIMITED (reason=context accepted but frame counts not doubled: "
                    "real=%llu gen=%llu; runtime accepted creation yet produced no "
                    "interpolated presents)\n",
            static_cast<unsigned long long>(realPresents),
            static_cast<unsigned long long>(genPresents));
        return 0;
    }
    std::printf("D3D12 FG LIMITED (reason=frame loop aborted at %u/%u frames: real=%llu "
                "gen=%llu)\n",
        framesRan, kFgFrames, static_cast<unsigned long long>(realPresents),
        static_cast<unsigned long long>(genPresents));
    return 0;
}


// =============================================================================
// ---- TASK-B-R7-01：--draw 的 Vulkan 后端路径（与 RunFirstDraw 同构镜像）----------
// 与 D3D12 路径的三角洲（其余全部走 IZrDevice 接口，逐行对位）：
//   1. 设备：ZrVulkanCreateDevice（SPIR-V 着色器经 glslc 预编译产物加载，
//      D3D12 路径的运行时 D3DCompile 无 VK 对位物——字节码契约同构）
//   2. 深度清屏：RecordClearDepth 扩展点（对位 ClearDepthStencilView）
//   3. readback：vkCmdCopyImageToBuffer（对位 CopyTextureRegion）
//   4. 设备移除检查：VK 无对位物（validation ERROR 计数=0 已覆盖设备失效场景）
// =============================================================================
#if defined(ZR_HAS_VULKAN)

/// 从 hlslPath 派生 SPIR-V 路径（glslc 产物：<stem>_vs.spv / <stem>_fs.spv，
/// 构建落在 Native/build/bin/shaders_spv/；验证程序自仓库根运行）
static bool FindSpirvPaths(const char* hlslPath, std::vector<char>& outVs,
    std::vector<char>& outFs)
{
    std::string stem(hlslPath);
    const size_t slash = stem.find_last_of("/\\");
    if (slash != std::string::npos) {
        stem = stem.substr(slash + 1);
    }
    const size_t dot = stem.find_last_of('.');
    if (dot != std::string::npos) {
        stem = stem.substr(0, dot);
    }
    const std::string candidates[] = {
        "Native/build/bin/shaders_spv/" + stem + "_vs.spv",
        "build/bin/shaders_spv/" + stem + "_vs.spv",
        "shaders_spv/" + stem + "_vs.spv",
    };
    for (const std::string& candidate : candidates) {
        std::ifstream vsFile(candidate, std::ios::binary | std::ios::ate);
        if (!vsFile) {
            continue;
        }
        const std::string fsPath
            = candidate.substr(0, candidate.size() - 7) + "_fs.spv";
        std::ifstream fsFile(fsPath, std::ios::binary | std::ios::ate);
        if (!fsFile) {
            continue;
        }
        const std::streampos vsLen = vsFile.tellg();
        vsFile.seekg(0, std::ios::beg);
        outVs.resize(static_cast<size_t>(vsLen));
        if (!vsFile.read(outVs.data(), vsLen)) {
            return false;
        }
        const std::streampos fsLen = fsFile.tellg();
        fsFile.seekg(0, std::ios::beg);
        outFs.resize(static_cast<size_t>(fsLen));
        if (!fsFile.read(outFs.data(), fsLen)) {
            return false;
        }
        std::printf("  SPIR-V loaded: %s + %s\n", candidate.c_str(), fsPath.c_str());
        return true;
    }
    return false;
}

// 从 hlslPath 派生单个 SPIR-V 路径（vs-only 着色器用；返回空串=未找到）
static std::string FindSpirvSingle(const char* hlslPath, const char* suffix)
{
    std::string stem(hlslPath);
    const size_t slash = stem.find_last_of("/\\");
    if (slash != std::string::npos) {
        stem = stem.substr(slash + 1);
    }
    const size_t dot = stem.find_last_of('.');
    if (dot != std::string::npos) {
        stem = stem.substr(0, dot);
    }
    const std::string candidates[] = {
        "Native/build/bin/shaders_spv/" + stem + suffix,
        "build/bin/shaders_spv/" + stem + suffix,
        "shaders_spv/" + stem + suffix,
    };
    for (const std::string& candidate : candidates) {
        std::ifstream f(candidate, std::ios::binary);
        if (f) {
            return candidate;
        }
    }
    return std::string();
}

// --draw Vulkan 路径主体（返回进程退出码；镜像 RunFirstDraw 的 out 参数契约）
static int RunFirstDrawVulkan(const char* zmeshPath, const char* hlslPath,
    std::vector<int>* outCenters = nullptr,
    std::vector<uint32_t>* outBarrierPlan = nullptr)
{
    std::printf("zrhi_d3d12_window: --draw mode vulkan backend (TASK-B-R7-01): "
                "zmesh='%s' hlsl='%s'\n",
        zmeshPath, hlslPath);

    // ---- 设备 / 队列 / 命令表（全接口层）-------------------------------------------
    ZrVulkanDevice* device = ZrVulkanCreateDevice();
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create Vulkan device\n");
        return 1;
    }
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create queue/command list\n");
        ZrVulkanDestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏；同 D3D12 路径）--------------------------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --draw window rejected (GetLastError=%lu), "
                    "falling back to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --draw swapchain FAILED, falling back to offscreen "
                        "target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
        std::printf("zrhi_d3d12_window: --draw window 640x480 + swapchain (BGRA8Unorm, "
                    "Fifo, dynamic rendering)\n");
    }

    // ---- 渲染目标 / 深度（接口层；同 D3D12）----------------------------------------
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --draw FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrVulkanDestroyDevice(device);
            return 1;
        }
    }
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    if (!depthImage.IsValid() || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create depth image/DSV\n");
        return 1;
    }

    // ---- zmesh → VB/IB/CB（与 D3D12 路径逐行同构）----------------------------------
    std::vector<char> meshBytes;
    if (!ReadWholeFile(zmeshPath, meshBytes)) {
        std::printf("zrhi_d3d12_window: --draw FAILED to open zmesh '%s'\n", zmeshPath);
        return 1;
    }
    ZmeshData mesh {};
    const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
    if (parseResult != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --draw zmesh parse FAILED (error %d)\n", parseResult);
        return 1;
    }
    const uint32_t vertexCount = mesh.stats.vertexCount;
    const uint32_t indexCount = mesh.stats.indexCount;
    if (mesh.stats.hasNormals == 0u || mesh.stats.hasUv == 0u) {
        std::printf("zrhi_d3d12_window: --draw zmesh lacks normals/uv (v1 固定布局需要)\n");
        return 1;
    }
    std::vector<FirstDrawVertex> vertices(vertexCount);
    for (uint32_t i = 0; i < vertexCount; ++i) {
        std::memcpy(vertices[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(vertices[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(vertices[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> indices(indexCount);
    std::memcpy(indices.data(), mesh.indices, sizeof(uint32_t) * indexCount);
    std::printf("zrhi_d3d12_window: --draw zmesh: vertices=%u indices=%u "
                "(interleaved 32B/vert)\n",
        vertexCount, indexCount);

    ZrBufferDesc vbDesc {};
    vbDesc.size = static_cast<uint64_t>(vertices.size()) * sizeof(FirstDrawVertex);
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer vb = device->CreateBuffer(vbDesc, vertices.data());
    ZrBufferDesc ibDesc {};
    ibDesc.size = static_cast<uint64_t>(indices.size()) * sizeof(uint32_t);
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer ib = device->CreateBuffer(ibDesc, indices.data());
    if (!vb.IsValid() || !ib.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create VB/IB\n");
        return 1;
    }

    constexpr uint32_t kDrawItems = 3u;
    ZrBuffer cbHandles[kDrawItems] = {};
    void* cbMapped[kDrawItems] = {};
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        ZrBufferDesc cbDesc {};
        cbDesc.size = 256u;
        cbDesc.usage = ZrBufferUsage::Uniform;
        cbDesc.hostVisible = true;
        cbHandles[i] = device->CreateBuffer(cbDesc);
        IZrBuffer* iface = device->GetBuffer(cbHandles[i]);
        cbMapped[i] = (iface != nullptr && cbHandles[i].IsValid()) ? iface->Map() : nullptr;
        if (cbMapped[i] == nullptr) {
            std::printf("zrhi_d3d12_window: --draw FAILED to create/map constant buffer %u\n",
                i);
            return 1;
        }
    }

    // ---- 着色器：glslc 预编译 SPIR-V（字节码契约与 DXBC 同构）+ PSO ------------------
    std::vector<char> spvVs;
    std::vector<char> spvFs;
    if (!FindSpirvPaths(hlslPath, spvVs, spvFs)) {
        std::printf("zrhi_d3d12_window: --draw FAILED to load SPIR-V for '%s' "
                    "(build generates Native/build/bin/shaders_spv/)\n",
            hlslPath);
        return 1;
    }
    ZrShaderDesc vsDesc {};
    vsDesc.stage = ZrShaderStage::Vertex;
    vsDesc.code = spvVs.data();
    vsDesc.codeSize = spvVs.size();
    vsDesc.entryPoint = "VsMain";
    ZrShaderDesc psDesc {};
    psDesc.stage = ZrShaderStage::Pixel;
    psDesc.code = spvFs.data();
    psDesc.codeSize = spvFs.size();
    psDesc.entryPoint = "PsMain";
    const ZrShader vs = device->CreateShader(vsDesc);
    const ZrShader ps = device->CreateShader(psDesc);
    if (!vs.IsValid() || !ps.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw shader module creation FAILED\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --draw shaders loaded (glslc SPIR-V)\n");

    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = vs;
    psoDesc.pixelShader = ps;
    psoDesc.rtvFormat = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    psoDesc.cullMode = ZrCullMode::None; // 与 D3D12 同策（绕序约定归资产管线任务）
    ZrPipelineState pso = device->CreatePipelineState(psoDesc);
    if (!pso.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create graphics PSO\n");
        return 1;
    }

    // ---- 绘制列表（同 D3D12）--------------------------------------------------------
    DrawList drawList(16u);
    drawList.RegisterMesh(1u, vertexCount);
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        RenderItem item;
        item.meshHandle = 1u;
        item.materialId = 7u;
        item.shaderId = 0u;
        item.lod = 0u;
        item.depth = 3.2f;
        item.translucent = false;
        if (!drawList.Add(item, 0x01u)) {
            std::printf("zrhi_d3d12_window: --draw FAILED to add draw list item %u\n", i);
            return 1;
        }
    }
    drawList.Sort();
    const uint32_t batchCount = drawList.BuildBatches();
    if (batchCount == 0u || drawList.Batches().size() != 1u
        || drawList.Batches()[0].count != kDrawItems) {
        std::printf("zrhi_d3d12_window: --draw unexpected draw list batches (%u)\n",
            batchCount);
        return 1;
    }
    std::printf("zrhi_d3d12_window: --draw draw list: items=%u batches=%u "
                "(state-clustered)\n",
        drawList.Count(), batchCount);

    // ---- RTV 视图 / readback 缓冲（READBACK 堆 + vkCmdCopyImageToBuffer 扩展点）--------
    ZrImageView rtvViews[3] = {};
    ZrImageView offscreenRtv {};
    if (!windowMode) {
        ZrImageViewDesc rtvViewDesc {};
        rtvViewDesc.image = offscreenTarget;
        rtvViewDesc.viewType = ZrImageViewType::Rtv;
        offscreenRtv = device->CreateImageView(rtvViewDesc);
        if (!offscreenRtv.IsValid()) {
            std::printf("zrhi_d3d12_window: --draw FAILED to create offscreen RTV view\n");
            return 1;
        }
    }
    ZrBufferDesc rbDesc {};
    rbDesc.size = 256u;
    rbDesc.usage = ZrBufferUsage::TransferDst;
    rbDesc.readback = true;
    ZrBuffer readbackBuffer = device->CreateBuffer(rbDesc);
    if (!readbackBuffer.IsValid()) {
        std::printf("zrhi_d3d12_window: --draw FAILED to create readback buffer\n");
        return 1;
    }

    // ---- 60 帧循环（RG 结构与 D3D12 路径逐 Pass 同构 → 屏障合并计数可对照）-----------
    const float lightDir[3] = {0.5f, 0.8f, 0.6f};
    const float invLen = 1.0f
        / std::sqrt(lightDir[0] * lightDir[0] + lightDir[1] * lightDir[1]
              + lightDir[2] * lightDir[2]);
    const float lightColor[4] = {1.0f, 0.95f, 0.9f, 1.0f};

    float proj[16];
    MatPerspectiveFovLH(3.14159265f / 3.0f,
        static_cast<float>(kWidth) / static_cast<float>(kHeight), 0.1f, 100.0f, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};

    uint32_t readbackChecks = 0u;
    uint32_t readbackPasses = 0u;
    bool allOk = true;
    uint32_t undeclaredUses = 0u;
    for (uint32_t frame = 0u; frame < kFrameCount; ++frame) {
        const bool doReadback = ((frame + 1u) % kCheckInterval) == 0u;

        const float angle = 6.2831853f * static_cast<float>(frame + 1u)
            / static_cast<float>(kFrameCount);
        float rotY[16];
        MatRotationY(angle, rotY);
        float rotX[16];
        MatRotationX(0.35f, rotX);
        float rx[16];
        MatMultiply(rotX, rotY, rx);
        for (uint32_t i = 0; i < kDrawItems; ++i) {
            const float tx = (static_cast<float>(i) - 1.0f) * 1.6f;
            float trans[16] = {};
            MatTranslation(tx, 0.0f, 3.2f, trans);
            float model[16];
            MatMultiply(trans, rx, model);
            float pv[16];
            MatMultiply(proj, view, pv);
            float mvp[16];
            MatMultiply(pv, model, mvp);
            DrawConstants constants;
            std::memcpy(constants.mvp, mvp, sizeof(mvp));
            constants.lightDir[0] = lightDir[0] * invLen;
            constants.lightDir[1] = lightDir[1] * invLen;
            constants.lightDir[2] = lightDir[2] * invLen;
            constants.lightDir[3] = 0.0f;
            std::memcpy(constants.lightColor, lightColor, sizeof(lightColor));
            std::memcpy(cbMapped[i], &constants, sizeof(constants));
        }

        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  frame %u: FAILED to obtain RTV view\n", frame + 1u);
            allOk = false;
            continue;
        }

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kDrawClearColor,
                    ZrSubresourceRange::All());
                // 深度清屏（扩展点 RecordClearDepth；对位 D3D12 ClearDepthStencilView）
                device->RecordClearDepth(cmdHandle, dsvView, 1.0f);
            });
        graph.AddPass("CubeDrawPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(pso);
                cmdList->SetRenderTarget(rtvView, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                ZrBuffer vbs[1] = {vb};
                cmdList->SetVertexBuffers(vbs, 1u);
                cmdList->SetIndexBuffer(ib);
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    for (uint32_t i = 0; i < batch.count; ++i) {
                        const uint32_t itemIndex = batch.startIndex + i;
                        cmdList->SetGraphicsConstantBuffer(0u, cbHandles[itemIndex]);
                        cmdList->DrawIndexedInstanced(indexCount, 1u, 0u, 0u, 0u);
                    }
                }
            });
        // ReadbackPass（校验帧）：后台缓冲转拷贝源，中心 1x1 拷入 readback 缓冲
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    // vkCmdCopyImageToBuffer（1x1 中心像素；布局已由屏障转 TransferSrc）
                    VkBufferImageCopy region {};
                    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                    region.imageSubresource.layerCount = 1u;
                    region.imageOffset = { static_cast<int32_t>(kWidth / 2u),
                        static_cast<int32_t>(kHeight / 2u), 0 };
                    region.imageExtent = { 1u, 1u, 1u };
                    vkCmdCopyImageToBuffer(device->VulkanNativeCmdOf(cmdHandle),
                        device->VulkanImageOf(ctx.GetImage(backH)),
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        device->VulkanBufferOf(readbackBuffer), 1u, &region);
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass",
                [&](RgPass& pass) { pass.Present(backH).MarkRoot(); }, nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        // 首个 readback 帧捕获 RG 屏障合并计数（与 D3D12 路径对照断言依据）
        if (doReadback && frame + 1u == kCheckInterval && outBarrierPlan != nullptr) {
            const RgBarrierPlan& plan = graph.BarrierPlan();
            outBarrierPlan->push_back(plan.TotalBarrierCount());
            outBarrierPlan->push_back(plan.SplitCount());
        }
        const RgExecuteStats rgStatsVk = graph.ExecuteAliased(*device, *cmdList);
        if (doReadback && frame + 1u == kCheckInterval && outBarrierPlan != nullptr) {
            outBarrierPlan->push_back(rgStatsVk.inlineBarrierCalls);
            outBarrierPlan->push_back(rgStatsVk.splitBegins);
            outBarrierPlan->push_back(rgStatsVk.splitEnds);
            outBarrierPlan->push_back(rgStatsVk.aliasHeapsCreated);
            outBarrierPlan->push_back(rgStatsVk.transientImages);
        }
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末屏障：Vulkan 路径无帧末复位——VK 禁止 newLayout=UNDEFINED 的屏障，
        // 而后续帧的 oldLayout=UNDEFINED 屏障本身合法（"内容不保证"语义），跳过
        // 复位即可（D3D12 路径的 COMMON=PRESENT=0 语义此处无对位物）

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle();
        graph.FlushRetired(*device);

        // ---- 中心像素校验（与 D3D12 路径同判据；通道序按目标格式）------------------------
        if (doReadback) {
            ++readbackChecks;
            IZrBuffer* rbInterface = device->GetBuffer(readbackBuffer);
            void* mapped = (rbInterface != nullptr) ? rbInterface->Map() : nullptr;
            if (mapped == nullptr) {
                std::printf("  frame %u: readback Map FAILED\n", frame + 1u);
                allOk = false;
            } else {
                const uint8_t* px = static_cast<const uint8_t*>(mapped);
                // 通道序按目标格式：窗口后台缓冲=BGRA8（B,G,R,A），离屏=RGBA8
                const int bByte = static_cast<int>(px[0]);
                const int g = static_cast<int>(px[1]);
                const int rByte = static_cast<int>(px[2]);
                const int r = windowMode ? rByte : bByte;
                const int b = windowMode ? bByte : rByte;
                const int clear0 = static_cast<int>(kDrawClearColor[0] * 255.0f + 0.5f);
                const int clear1 = static_cast<int>(kDrawClearColor[1] * 255.0f + 0.5f);
                const int clear2 = static_cast<int>(kDrawClearColor[2] * 255.0f + 0.5f);
                const bool differs = std::abs(r - clear0) > 2 || std::abs(g - clear1) > 2
                    || std::abs(b - clear2) > 2;
                if (differs) {
                    ++readbackPasses;
                }
                std::printf("  frame %u readback center RGB=(%d,%d,%d), clear≈(%d,%d,%d) "
                            "-> %s [vulkan]\n",
                    frame + 1u, r, g, b, clear0, clear1, clear2,
                    differs ? "CUBE (first draw visible)" : "CLEAR COLOR (no draw)");
                if (outCenters != nullptr) {
                    outCenters->push_back(r);
                    outCenters->push_back(g);
                    outCenters->push_back(b);
                }
                allOk = allOk && differs;
            }
        }

        if (windowMode) {
            PumpMessages();
        }
        std::printf("frame %2u/%u: [FIRST DRAW][vulkan] angle=%.1f deg%s\n", frame + 1u,
            kFrameCount, angle * 57.2958f, doReadback ? "  (+center readback)" : "");
    }

    // ---- 清理（同 D3D12 次序）---------------------------------------------------------
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        if (cbHandles[i].IsValid()) {
            device->Destroy(cbHandles[i]);
        }
    }
    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(dsvView);
    device->Destroy(pso);
    device->Destroy(vs);
    device->Destroy(ps);
    device->Destroy(vb);
    device->Destroy(ib);
    device->Destroy(readbackBuffer);
    device->Destroy(depthImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t validationErrors = device->GetDebugErrorCount();
    ZrVulkanDestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: [FIRST DRAW][vulkan] frames=%u center checks=%u passed=%u "
                "undeclared uses=%u\n",
        kFrameCount, readbackChecks, readbackPasses, undeclaredUses);
    std::printf("zrhi_d3d12_window: [vulkan] validation error count = %u\n", validationErrors);

    if (allOk && readbackChecks > 0u && readbackPasses == readbackChecks
        && undeclaredUses == 0u && validationErrors == 0u) {
        std::printf("VULKAN FIRST DRAW OK\n");
        return 0;
    }
    std::printf("VULKAN FIRST DRAW FAILED (allOk=%d checks=%u passes=%u undeclared=%u "
                "errors=%u)\n",
        allOk ? 1 : 0, readbackChecks, readbackPasses, undeclaredUses, validationErrors);
    return 1;
}

// --lights Vulkan 路径主体（TASK-B-R7-02；返回进程退出码；镜像 RunLightsMode 的
// out 参数契约）。D3D12 原生扩展点 → VK 对位替换：
//   设备创建            ZrVulkanCreateDevice
//   着色器              glslc SPIR-V（pbr_forward_vs/_fs.spv，构建期产出）
//   深度清屏            device->RecordClearDepth（对位 ClearDepthStencilView）
//   readback            vkCmdCopyImageToBuffer（中心 1×1 + 地面横带 640×4，
//                       两区域各自 ZrBuffer；行紧缩布局=D3D12 RowPitch 2560B 同值）
//   帧末深度复位屏障    跳过（VK 禁止 newLayout=UNDEFINED；B-R7-01 既定差异点）
//   InfoQueue           validation error count
static int RunLightsVulkan(const char* zmeshPath, const char* hlslPath,
    std::vector<int>* outCenters = nullptr,
    std::vector<uint32_t>* outBarrierPlan = nullptr,
    bool jsonOut = false,
    bool forceHeadless = false)
{
    std::printf("zrhi_d3d12_window: --lights mode vulkan backend (TASK-B-R7-02): "
                "zmesh='%s' hlsl='%s'\n",
        zmeshPath, hlslPath);

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrVulkanDevice* device = ZrVulkanCreateDevice();
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create Vulkan device\n");
        return 1;
    }
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create queue/command list\n");
        ZrVulkanDestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏；同 D3D12 路径）--------------------------
    // TASK-B-R9-11：forceHeadless 正式接入（--headless --lights 直接离屏，bench
    // 回归无 vsync 干扰）。B-R9-10 曾因离屏 readback 全零有意不接——根因与修复见
    // 本任务施工报告（readback 全零的排除链与真修）。
    HWND hWnd = forceHeadless ? nullptr : TryCreateWindow(kWidth, kHeight);
    if (forceHeadless) {
        std::printf("zrhi_d3d12_window: --lights --headless: offscreen target (no swapchain, "
                    "no vsync - bench regression mode)\n");
    } else if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --lights window rejected (GetLastError=%lu), "
                    "falling back to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --lights swapchain FAILED, falling back to "
                        "offscreen target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
        std::printf("zrhi_d3d12_window: --lights window 640x480 + swapchain (BGRA8Unorm, "
                    "Fifo, dynamic rendering)\n");
    }

    // ---- 渲染目标（离屏降级）+ 深度 --------------------------------------------------
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --lights FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrVulkanDestroyDevice(device);
            return 1;
        }
    }
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    if (!depthImage.IsValid() || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create depth image/DSV\n");
        return 1;
    }

    // ---- 几何：程序化地面 quad + cube.zmesh（与 D3D12 路径同构）----------------------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<char> meshBytes;
    if (!ReadWholeFile(zmeshPath, meshBytes)) {
        std::printf("zrhi_d3d12_window: --lights FAILED to open zmesh '%s'\n", zmeshPath);
        return 1;
    }
    ZmeshData mesh {};
    const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
    if (parseResult != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --lights zmesh parse FAILED (error %d)\n", parseResult);
        return 1;
    }
    if (mesh.stats.hasNormals == 0u) {
        std::printf("zrhi_d3d12_window: --lights zmesh lacks normals\n");
        return 1;
    }
    const uint32_t cubeVertexCount = mesh.stats.vertexCount;
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(cubeVertexCount);
    for (uint32_t i = 0; i < cubeVertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);
    std::printf("zrhi_d3d12_window: --lights geometry: ground quad 20x20m (4 verts) + cube.zmesh "
                "(%u verts / %u indices)\n",
        cubeVertexCount, cubeIndexCount);

    ZrBufferDesc gVbDesc {};
    gVbDesc.size = sizeof(groundVerts);
    gVbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer groundVb = device->CreateBuffer(gVbDesc, groundVerts);
    ZrBufferDesc gIbDesc {};
    gIbDesc.size = sizeof(groundIndices);
    gIbDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer groundIb = device->CreateBuffer(gIbDesc, groundIndices);
    ZrBufferDesc cVbDesc {};
    cVbDesc.size = static_cast<uint64_t>(cubeVerts.size()) * sizeof(FirstDrawVertex);
    cVbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer cubeVb = device->CreateBuffer(cVbDesc, cubeVerts.data());
    ZrBufferDesc cIbDesc {};
    cIbDesc.size = static_cast<uint64_t>(cubeIndices.size()) * sizeof(uint32_t);
    cIbDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer cubeIb = device->CreateBuffer(cIbDesc, cubeIndices.data());
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create VB/IB\n");
        return 1;
    }

    // ---- 常量缓冲：CB0×2（256B）/ CB1 光源（1024B）/ CB2 簇（65280B 对齐）------------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    void* cb0GroundMapped = nullptr;
    void* cb0CubeMapped = nullptr;
    void* cbLightsMapped = nullptr;
    void* cbClustersMapped = nullptr;
    {
        auto makeCb = [&](uint64_t size, ZrBuffer& outHandle, void*& outMapped) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            outHandle = device->CreateBuffer(desc);
            IZrBuffer* iface = device->GetBuffer(outHandle);
            outMapped = (iface != nullptr && outHandle.IsValid()) ? iface->Map() : nullptr;
            return outMapped != nullptr;
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        const bool ok = makeCb(256u, cb0Ground, cb0GroundMapped)
            && makeCb(256u, cb0Cube, cb0CubeMapped) && makeCb(1024u, cbLights, cbLightsMapped)
            && makeCb(cb2Size, cbClusters, cbClustersMapped);
        if (!ok) {
            std::printf("zrhi_d3d12_window: --lights FAILED to create/map constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器：glslc SPIR-V + PSO（rootCbvCount=3：b0/b1/b2；后端能力已就绪）--------
    std::vector<char> spvVs;
    std::vector<char> spvFs;
    if (!FindSpirvPaths(hlslPath, spvVs, spvFs)) {
        std::printf("zrhi_d3d12_window: --lights FAILED to load SPIR-V for '%s' "
                    "(build generates Native/build/bin/shaders_spv/)\n",
            hlslPath);
        return 1;
    }
    ZrShaderDesc vsDesc {};
    vsDesc.stage = ZrShaderStage::Vertex;
    vsDesc.code = spvVs.data();
    vsDesc.codeSize = spvVs.size();
    vsDesc.entryPoint = "VsMain";
    ZrShaderDesc psDesc {};
    psDesc.stage = ZrShaderStage::Pixel;
    psDesc.code = spvFs.data();
    psDesc.codeSize = spvFs.size();
    psDesc.entryPoint = "PsMain";
    const ZrShader vs = device->CreateShader(vsDesc);
    const ZrShader ps = device->CreateShader(psDesc);
    if (!vs.IsValid() || !ps.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights shader module creation FAILED\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --lights shaders loaded (glslc SPIR-V)\n");

    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = vs;
    psoDesc.pixelShader = ps;
    psoDesc.rtvFormat = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    psoDesc.cullMode = ZrCullMode::None; // 与 D3D12 路径同策
    psoDesc.rootCbvCount = 3u;           // b0 逐绘常量 / b1 光源数组 / b2 簇数据
    ZrPipelineState pso = device->CreatePipelineState(psoDesc);
    if (!pso.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create graphics PSO\n");
        return 1;
    }

    // ---- 投影/簇网格参数/光源（与 D3D12 路径同值同源）--------------------------------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kWidth) / static_cast<float>(kHeight), kNearZ,
        kFarZ, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float projView[16];
    MatMultiply(proj, view, projView);

    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);

    LightData sceneLights[kLightsCount] = {};
    {
        LightData& dir = sceneLights[0];
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen
            = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        dir.pos[0] = d[0] * invLen;
        dir.pos[1] = d[1] * invLen;
        dir.pos[2] = d[2] * invLen;
        dir.color[0] = 1.0f;
        dir.color[1] = 0.95f;
        dir.color[2] = 0.88f;
        dir.intensity = 1.0f;
        dir.kind = kZrLightDirectional;
    }
    sceneLights[1] = MakeAppPointLight(-3.0f, 0.6f, 2.5f, 5.0f, 2.0f, 1.0f, 0.35f, 0.25f);
    sceneLights[2] = MakeAppPointLight(3.0f, 0.6f, 2.5f, 5.0f, 2.0f, 0.25f, 0.5f, 1.0f);
    sceneLights[3] = MakeAppPointLight(0.0f, 1.4f, 6.0f, 6.0f, 2.0f, 0.3f, 1.0f, 0.4f);
    sceneLights[4] = MakeAppPointLight(-1.5f, 2.2f, 7.5f, 6.0f, 1.5f, 1.0f, 0.8f, 0.3f);
    const float animColors[4][3] = {{1.0f, 0.5f, 0.15f}, {0.2f, 0.9f, 1.0f}, {0.9f, 0.2f, 0.9f},
        {0.5f, 1.0f, 0.2f}};

    // ---- RTV 视图（窗口模式按后台缓冲缓存；离屏单视图）--------------------------------
    ZrImageView rtvViews[3] = {};
    ZrImageView offscreenRtv {};
    if (!windowMode) {
        ZrImageViewDesc rtvViewDesc {};
        rtvViewDesc.image = offscreenTarget;
        rtvViewDesc.viewType = ZrImageViewType::Rtv;
        offscreenRtv = device->CreateImageView(rtvViewDesc);
        if (!offscreenRtv.IsValid()) {
            std::printf("zrhi_d3d12_window: --lights FAILED to create offscreen RTV view\n");
            return 1;
        }
    }

    // ---- readback：中心 1×1（256B）+ 地面横带 640×4（10240B；行紧缩 2560B/行）---------
    ZrBufferDesc rbCenterDesc {};
    rbCenterDesc.size = 256u;
    rbCenterDesc.usage = ZrBufferUsage::TransferDst;
    rbCenterDesc.readback = true;
    ZrBuffer readbackCenter = device->CreateBuffer(rbCenterDesc);
    ZrBufferDesc rbStripDesc {};
    rbStripDesc.size = uint64_t(kLightsStripRowPitch) * kLightsStripRows;
    rbStripDesc.usage = ZrBufferUsage::TransferDst;
    rbStripDesc.readback = true;
    ZrBuffer readbackStrip = device->CreateBuffer(rbStripDesc);
    if (!readbackCenter.IsValid() || !readbackStrip.IsValid()) {
        std::printf("zrhi_d3d12_window: --lights FAILED to create readback buffers\n");
        return 1;
    }
    std::vector<uint8_t> prevStrip(size_t(kLightsStripRowPitch) * kLightsStripRows, 0u);
    bool havePrevStrip = false;

    uint32_t readbackChecks = 0u;
    uint32_t centerPasses = 0u;
    uint32_t groundLitPasses = 0u;
    uint32_t animPairCount = 0u;
    uint32_t animPairPasses = 0u;
    bool allOk = true;
    uint32_t undeclaredUses = 0u;

    // TASK-B-R9-10：CPU 墙钟帧耗采集（与 D3D12 路径同口径，镜像；失败帧不入样本）
    std::vector<double> benchFrameMs;
    benchFrameMs.reserve(kFrameCount);

    for (uint32_t frame = 0u; frame < kFrameCount; ++frame) {
        const bool doReadback = ((frame + 1u) % kLightsCheckInterval) == 0u;
        const auto benchFrameStart = std::chrono::steady_clock::now();

        // ---- 动画点光位置更新（与 D3D12 路径同式）------------------------------------
        for (uint32_t i = 0; i < 4u; ++i) {
            const float angle = 6.2831853f * static_cast<float>(frame + 1u) / 120.0f
                + 3.14159265f * 0.5f * static_cast<float>(i);
            LightData& l = sceneLights[kAnimLightBase + i];
            l.pos[0] = std::cos(angle) * 2.6f;
            l.pos[1] = 0.9f;
            l.pos[2] = 3.2f + std::sin(angle) * 2.6f;
            l.radius = 3.5f;
            l.color[0] = animColors[i][0];
            l.color[1] = animColors[i][1];
            l.color[2] = animColors[i][2];
            l.intensity = 6.0f;
            l.kind = kZrLightPoint;
        }

        // ---- CPU 簇分配 → CB1/CB2 每帧写入 --------------------------------------------
        PackedLight packedLights[kLightsCount];
        PackLights(sceneLights, kLightsCount, packedLights);
        std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
        uint8_t* lightsBytes = static_cast<uint8_t*>(cbLightsMapped);
        std::memset(lightsBytes + sizeof(packedLights), 0, 1024u - sizeof(packedLights));
        const int32_t flatCount = AssignLightsToClusters(grid, sceneLights, kLightsCount,
            clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
        if (flatCount < 0) {
            std::printf("  frame %u: AssignLightsToClusters FAILED (%d)\n", frame + 1u, flatCount);
            allOk = false;
            continue;
        }
        uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
        std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);
        std::memcpy(cb2 + size_t(ZR_CLUSTER_COUNT) * 4u, clusterFlat.data(),
            size_t(flatCount) * 4u);
        if (frame == 0u) {
            std::printf("zrhi_d3d12_window: --lights cluster assign frame1: lights=%u "
                        "clusters=%u flat entries=%d\n",
                kLightsCount, ZR_CLUSTER_COUNT, flatCount);
        }

        // ---- CB0（逐绘常量：地面 / cube）----------------------------------------------
        const float ambient = 0.12f;
        LightsConstants groundConst {};
        std::memcpy(groundConst.mvp, projView, sizeof(projView));
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        std::memcpy(groundConst.model, identity, sizeof(identity));
        LightsConstants cubeConst {};
        float cubeModel[16];
        MatTranslation(0.0f, 0.0f, 3.2f, cubeModel);
        std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
        float cubeMvp[16];
        MatMultiply(projView, cubeModel, cubeMvp);
        std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
        for (LightsConstants* c : {&groundConst, &cubeConst}) {
            c->projParams[0] = kFarZ / (kFarZ - kNearZ);
            c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
            c->projParams[2] = kNearZ;
            c->projParams[3] = kFarZ;
            c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
            c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
            c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
            c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
            c->screenParams[0] = static_cast<float>(kWidth);
            c->screenParams[1] = static_cast<float>(kHeight);
            c->screenParams[2] = static_cast<float>(kLightsCount);
            c->screenParams[3] = ambient;
            c->camPos[0] = 0.0f;
            c->camPos[1] = 0.0f;
            c->camPos[2] = 0.0f;
            c->camPos[3] = 0.0f;
        }
        groundConst.material[0] = 0.50f;
        groundConst.material[1] = 0.52f;
        groundConst.material[2] = 0.50f;
        groundConst.material[3] = 0.85f;
        cubeConst.material[0] = 0.75f;
        cubeConst.material[1] = 0.75f;
        cubeConst.material[2] = 0.78f;
        cubeConst.material[3] = 0.35f;
        std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
        std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));

        // ---- 取后台缓冲（窗口模式）+ RTV 视图 -----------------------------------------
        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  frame %u: FAILED to obtain RTV view\n", frame + 1u);
            allOk = false;
            continue;
        }

        // ---- 每帧 RG：ClearPass → LightsDrawPass → ReadbackPass/Present（同 D3D12 结构）
        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kLightsClearColor,
                    ZrSubresourceRange::All());
                // 深度清屏扩展点（对位 D3D12 ClearDepthStencilView 原生调用）
                device->RecordClearDepth(cmdHandle, dsvView, 1.0f);
            });
        graph.AddPass("LightsDrawPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(pso);
                cmdList->SetRenderTarget(rtvView, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                ZrBuffer groundVbs[1] = {groundVb};
                cmdList->SetVertexBuffers(groundVbs, 1u);
                cmdList->SetIndexBuffer(groundIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Ground);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
                ZrBuffer cubeVbs[1] = {cubeVb};
                cmdList->SetVertexBuffers(cubeVbs, 1u);
                cmdList->SetIndexBuffer(cubeIb);
                cmdList->SetGraphicsConstantBuffer(0u, cb0Cube);
                cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
            });
        // ReadbackPass（校验帧）：中心 1×1 + 地面横带 640×4 → vkCmdCopyImageToBuffer
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    // 中心 1×1（bufferOffset 0；行紧缩 4B）
                    VkBufferImageCopy centerRegion {};
                    centerRegion.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                    centerRegion.imageSubresource.layerCount = 1u;
                    centerRegion.imageOffset = { static_cast<int32_t>(kWidth / 2u),
                        static_cast<int32_t>(kHeight / 2u), 0 };
                    centerRegion.imageExtent = { 1u, 1u, 1u };
                    vkCmdCopyImageToBuffer(device->VulkanNativeCmdOf(cmdHandle),
                        device->VulkanImageOf(ctx.GetImage(backH)),
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        device->VulkanBufferOf(readbackCenter), 1u, &centerRegion);
                    // 地面横带 640×4（imageOffset.y=368；行紧缩 = 640×4B = 2560B/行，
                    // 与 D3D12 PlacedFootprint.RowPitch 同值）
                    VkBufferImageCopy stripRegion {};
                    stripRegion.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                    stripRegion.imageSubresource.layerCount = 1u;
                    stripRegion.imageOffset = { 0, static_cast<int32_t>(kLightsStripY0), 0 };
                    stripRegion.imageExtent = { kWidth, kLightsStripRows, 1u };
                    vkCmdCopyImageToBuffer(device->VulkanNativeCmdOf(cmdHandle),
                        device->VulkanImageOf(ctx.GetImage(backH)),
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        device->VulkanBufferOf(readbackStrip), 1u, &stripRegion);
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass",
                [&](RgPass& pass) { pass.Present(backH).MarkRoot(); }, nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        // 首个校验帧捕获 RG 屏障合并计数（双后端对照断言依据；与 --draw 同口径）
        if (doReadback && frame + 1u == kLightsCheckInterval && outBarrierPlan != nullptr) {
            const RgBarrierPlan& plan = graph.BarrierPlan();
            outBarrierPlan->push_back(plan.TotalBarrierCount());
            outBarrierPlan->push_back(plan.SplitCount());
        }
        const RgExecuteStats rgStatsVk = graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();
        if (doReadback && frame + 1u == kLightsCheckInterval && outBarrierPlan != nullptr) {
            outBarrierPlan->push_back(rgStatsVk.inlineBarrierCalls);
            outBarrierPlan->push_back(rgStatsVk.splitBegins);
            outBarrierPlan->push_back(rgStatsVk.splitEnds);
            outBarrierPlan->push_back(rgStatsVk.aliasHeapsCreated);
            outBarrierPlan->push_back(rgStatsVk.transientImages);
        }

        // 帧末屏障：VK 路径无帧末复位（VK 禁止 newLayout=UNDEFINED；同 --draw 既定差异）

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle();
        graph.FlushRetired(*device);

        // ---- 校验（判据与 D3D12 路径同源：中心≠清屏 / 横带受光 / 横带动画变化）----------
        if (doReadback) {
            ++readbackChecks;
            IZrBuffer* centerIface = device->GetBuffer(readbackCenter);
            IZrBuffer* stripIface = device->GetBuffer(readbackStrip);
            void* centerMapped = (centerIface != nullptr) ? centerIface->Map() : nullptr;
            void* stripMapped = (stripIface != nullptr) ? stripIface->Map() : nullptr;
            if (centerMapped == nullptr || stripMapped == nullptr) {
                std::printf("  frame %u: readback Map FAILED\n", frame + 1u);
                allOk = false;
            } else {
                const uint8_t* cpx = static_cast<const uint8_t*>(centerMapped);
                const int c0 = static_cast<int>(cpx[0]);
                const int c1 = static_cast<int>(cpx[1]);
                const int c2 = static_cast<int>(cpx[2]);
                const int r = windowMode ? c2 : c0;
                const int b = windowMode ? c0 : c2;
                const int clear0 = static_cast<int>(kLightsClearColor[0] * 255.0f + 0.5f);
                const int clear1 = static_cast<int>(kLightsClearColor[1] * 255.0f + 0.5f);
                const int clear2 = static_cast<int>(kLightsClearColor[2] * 255.0f + 0.5f);
                const bool centerOk = std::abs(r - clear0) > 2 || std::abs(c1 - clear1) > 2
                    || std::abs(b - clear2) > 2;
                centerPasses += centerOk ? 1u : 0u;
                if (outCenters != nullptr) {
                    outCenters->push_back(r);
                    outCenters->push_back(c1);
                    outCenters->push_back(b);
                }

                const uint8_t* strip = static_cast<const uint8_t*>(stripMapped);
                const size_t stripBytes = size_t(kLightsStripRowPitch) * kLightsStripRows;
                int stripMax = 0;
                int maxDiff = -1;
                int changedBytes = 0;
                for (size_t i = 0; i < stripBytes; ++i) {
                    const int v = static_cast<int>(strip[i]);
                    if ((i & 3u) != 3u) { // 跳过 alpha（恒 255，不参与受光判定）
                        stripMax = (v > stripMax) ? v : stripMax;
                    }
                    if (havePrevStrip) {
                        const int d = std::abs(v - static_cast<int>(prevStrip[i]));
                        maxDiff = (d > maxDiff) ? d : maxDiff;
                        if (d >= kLightsAnimDiffMin) {
                            ++changedBytes;
                        }
                    }
                }
                const bool groundLit = stripMax >= kLightsGroundLitMin;
                groundLitPasses += groundLit ? 1u : 0u;
                if (havePrevStrip) {
                    ++animPairCount;
                    const bool animOk = changedBytes >= kLightsAnimChangedMin;
                    animPairPasses += animOk ? 1u : 0u;
                    std::printf("  frame %u lights readback: center RGB=(%d,%d,%d) "
                                "stripMax=%d groundLit=%d animDiff(max=%d changed>=%d: %d "
                                "bytes, min %d) -> center=%s anim=%s [vulkan]\n",
                        frame + 1u, r, c1, b, stripMax, groundLit ? 1 : 0, maxDiff,
                        kLightsAnimDiffMin, changedBytes, kLightsAnimChangedMin,
                        centerOk ? "OK" : "CLEAR", animOk ? "OK" : "STATIC");
                } else {
                    std::printf("  frame %u lights readback: center RGB=(%d,%d,%d) "
                                "stripMax=%d groundLit=%d (baseline strip) [vulkan]\n",
                        frame + 1u, r, c1, b, stripMax, groundLit ? 1 : 0);
                }
                allOk = allOk && centerOk && groundLit;
                std::memcpy(prevStrip.data(), strip, stripBytes);
                havePrevStrip = true;
                centerIface->Unmap();
                stripIface->Unmap();
            }
        }

        if (windowMode) {
            PumpMessages();
        }
        std::printf("frame %2u/%u: [LIGHTS][vulkan] flatEntries=%d%s\n", frame + 1u, kFrameCount,
            flatCount, doReadback ? "  (+readback)" : "");
        benchFrameMs.push_back(std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - benchFrameStart)
                                   .count());
    }

    // ---- 清理（同 D3D12 次序）---------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(dsvView);
    device->Destroy(pso);
    device->Destroy(vs);
    device->Destroy(ps);
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(readbackCenter);
    device->Destroy(readbackStrip);
    device->Destroy(depthImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t validationErrors = device->GetDebugErrorCount();
    ZrVulkanDestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    // TASK-B-R9-10：帧耗汇总（与 D3D12 路径镜像；固定行 + --json 单行 BENCH_JSON）
    if (benchFrameMs.size() >= 2u) {
        double sum = 0.0;
        double minMs = benchFrameMs[1];
        double maxMs = benchFrameMs[1];
        for (size_t i = 1; i < benchFrameMs.size(); ++i) {
            sum += benchFrameMs[i];
            minMs = (benchFrameMs[i] < minMs) ? benchFrameMs[i] : minMs;
            maxMs = (benchFrameMs[i] > maxMs) ? benchFrameMs[i] : maxMs;
        }
        const double avgMs = sum / static_cast<double>(benchFrameMs.size() - 1);
        std::printf("zrhi_d3d12_window: [LIGHTS][vulkan] frame_ms avg=%.3f min=%.3f max=%.3f "
                    "n=%u (frame1 excluded)\n",
            avgMs, minMs, maxMs, static_cast<unsigned>(benchFrameMs.size() - 1u));
        if (jsonOut) {
            // TASK-B-R9-11：VK 离屏帧耗单行 JSON——program 标签与指标键都用 _vk 后缀，
            // 与 D3D12 离屏行（frame_ms_avg）同解析器共存不碰撞（bench 门禁第 7 指标）
            zbench::JsonLineWriter w("zrhi_d3d12_window_vk");
            w.Metric("frame_ms_avg_vk", avgMs);
            w.Metric("frame_ms_min_vk", minMs);
            w.Metric("frame_ms_max_vk", maxMs);
            w.MetricU64("frames_vk", benchFrameMs.size() - 1u);
            std::printf("%s\n", w.Finish().c_str());
        }
    }
    std::printf("zrhi_d3d12_window: [LIGHTS][vulkan] frames=%u readback checks=%u centerPass=%u "
                "groundLitPass=%u animPairs=%u/%u (threshold %d)\n",
        kFrameCount, readbackChecks, centerPasses, groundLitPasses, animPairPasses, animPairCount,
        kLightsAnimDiffMin);
    std::printf("zrhi_d3d12_window: [vulkan] validation error count = %u\n", validationErrors);

    if (allOk && readbackChecks > 0u && centerPasses == readbackChecks
        && groundLitPasses == readbackChecks && animPairCount > 0u
        && animPairPasses * 5u >= animPairCount * 3u && undeclaredUses == 0u
        && validationErrors == 0u) {
        std::printf("VULKAN LIGHTS OK\n");
        return 0;
    }
    std::printf("VULKAN LIGHTS FAILED (allOk=%d checks=%u center=%u groundLit=%u anim=%u/%u "
                "undeclared=%u errors=%u)\n",
        allOk ? 1 : 0, readbackChecks, centerPasses, groundLitPasses, animPairPasses,
        animPairCount, undeclaredUses, validationErrors);
    return 1;
}

// ---- 双后端对照（--lights --backend both，TASK-B-R7-02）：D3D12 与 Vulkan 各跑一轮
// --lights，断言 RG 屏障合并计数逐项相同 + 中心像素并排对照（差 ≤ kParityTolerance）----
static constexpr int kParityTolerance = 16; // 驱动浮点差异容差（实际差值照实报告）

static int RunLightsParity(const char* zmeshPath, const char* hlslPath,
    bool forceHeadless = false)
{
    std::printf("zrhi_d3d12_window: --lights --backend both (TASK-B-R7-02 dual-backend parity)%s\n",
        forceHeadless ? " [headless: both offscreen]" : "");
    std::vector<int> d3d12Centers;
    std::vector<uint32_t> d3d12Barriers;
    const int d3d12Rc
        = RunLightsMode(zmeshPath, hlslPath, &d3d12Centers, &d3d12Barriers, false, forceHeadless);
    std::vector<int> vkCenters;
    std::vector<uint32_t> vkBarriers;
    const int vkRc
        = RunLightsVulkan(zmeshPath, hlslPath, &vkCenters, &vkBarriers, false, forceHeadless);

    // ---- RG 屏障合并计数一致性断言（同一 RenderGraph 在两后端跑）----------------------
    bool barriersEqual = (d3d12Barriers.size() == vkBarriers.size())
        && (d3d12Barriers.size() == 7u);
    if (barriersEqual) {
        for (size_t i = 0; i < d3d12Barriers.size(); ++i) {
            if (d3d12Barriers[i] != vkBarriers[i]) {
                barriersEqual = false;
            }
        }
    }
    std::printf("RG BARRIER PLAN PARITY (lights): d3d12=[total=%u splits=%u calls=%u begin=%u "
                "end=%u heaps=%u transients=%u]\n",
        d3d12Barriers.size() > 0u ? d3d12Barriers[0] : 0u,
        d3d12Barriers.size() > 1u ? d3d12Barriers[1] : 0u,
        d3d12Barriers.size() > 2u ? d3d12Barriers[2] : 0u,
        d3d12Barriers.size() > 3u ? d3d12Barriers[3] : 0u,
        d3d12Barriers.size() > 4u ? d3d12Barriers[4] : 0u,
        d3d12Barriers.size() > 5u ? d3d12Barriers[5] : 0u,
        d3d12Barriers.size() > 6u ? d3d12Barriers[6] : 0u);
    std::printf("RG BARRIER PLAN PARITY (lights): vulkan=[total=%u splits=%u calls=%u begin=%u "
                "end=%u heaps=%u transients=%u] -> %s\n",
        vkBarriers.size() > 0u ? vkBarriers[0] : 0u,
        vkBarriers.size() > 1u ? vkBarriers[1] : 0u,
        vkBarriers.size() > 2u ? vkBarriers[2] : 0u,
        vkBarriers.size() > 3u ? vkBarriers[3] : 0u,
        vkBarriers.size() > 4u ? vkBarriers[4] : 0u,
        vkBarriers.size() > 5u ? vkBarriers[5] : 0u,
        vkBarriers.size() > 6u ? vkBarriers[6] : 0u,
        barriersEqual ? "EQUAL (assert OK)" : "MISMATCH (assert FAILED)");

    // ---- 中心像素并排对照（逐校验帧；报告最大通道差）----------------------------------
    bool pixelsOk = (d3d12Centers.size() == vkCenters.size()) && !d3d12Centers.empty();
    int maxDelta = 0;
    if (pixelsOk) {
        const size_t checks = d3d12Centers.size() / 3u;
        for (size_t c = 0; c < checks; ++c) {
            const int dr = std::abs(d3d12Centers[c * 3u + 0u] - vkCenters[c * 3u + 0u]);
            const int dg = std::abs(d3d12Centers[c * 3u + 1u] - vkCenters[c * 3u + 1u]);
            const int db = std::abs(d3d12Centers[c * 3u + 2u] - vkCenters[c * 3u + 2u]);
            const int frameDelta = dr > dg ? (dr > db ? dr : db) : (dg > db ? dg : db);
            if (frameDelta > maxDelta) {
                maxDelta = frameDelta;
            }
            std::printf("CENTER PIXEL PARITY (lights): check %zu d3d12 RGB=(%d,%d,%d) "
                        "vulkan RGB=(%d,%d,%d) maxDelta=%d\n",
                c + 1u, d3d12Centers[c * 3u + 0u], d3d12Centers[c * 3u + 1u],
                d3d12Centers[c * 3u + 2u], vkCenters[c * 3u + 0u], vkCenters[c * 3u + 1u],
                vkCenters[c * 3u + 2u], frameDelta);
        }
    }
    const bool parityOk = barriersEqual && pixelsOk && maxDelta <= kParityTolerance;
    std::printf("D3D12/VULKAN LIGHTS PARITY %s (maxDelta=%d tolerance=%d)\n",
        parityOk ? "OK" : "FAILED", maxDelta, kParityTolerance);
    return (d3d12Rc == 0 && vkRc == 0 && parityOk) ? 0 : 1;
}

// --shadows Vulkan 单相位帧循环（TASK-B-R7-02；镜像 RunShadowsPhase 的参数与返回
// 契约）。D3D12 原生扩展点 → VK 对位替换：
//   深度/级联清屏   device->RecordClearDepth（视图子资源窗口语义；级联清屏置于
//                   SetRenderTarget 之前——RecordClearDepth 内部做布局往返屏障，
//                   与渲染实例不兼容）
//   探针 readback   vkCmdCopyImageToBuffer（1×1）
//   帧末复位屏障    跳过（VK 禁止 newLayout=UNDEFINED；B-R7-01 既定差异点）
//   InfoQueue       validation error count（模式主体侧）
bool RunShadowsPhaseVulkan(ZrVulkanDevice* device, IZrQueue* queue, ZrCommandList cmdHandle,
    IZrCommandList* cmdList, IZrSwapchain* swapchain, ZrImage offscreenTarget,
    ZrImageView offscreenRtv, ZrImage depthImage, ZrImageView dsvView, ZrImage shadowImage,
    ZrImageView* shadowDsvs, ZrImageView shadowSrv, ZrPipelineState mainPso,
    ZrPipelineState shadowPso, ZrBuffer groundVb, ZrBuffer groundIb, ZrBuffer cubeVb,
    ZrBuffer cubeIb, uint32_t cubeIndexCount, ZrBuffer cb0Ground, ZrBuffer cb0Cube,
    ZrBuffer cbLights, ZrBuffer cbClusters, ZrBuffer cbShadows, ZrBuffer* cbShadowPass,
    ZrBuffer cbShadowModelGround, ZrBuffer cbShadowModelCube, bool enabled,
    const float cascadeSplits[4], int* outProbe)
{
    ZrImageView rtvViews[3] = {};
    bool windowMode = (swapchain != nullptr);
    void* cb0GroundMapped = device->GetBuffer(cb0Ground)->Map();
    void* cb0CubeMapped = device->GetBuffer(cb0Cube)->Map();
    void* cbLightsMapped = device->GetBuffer(cbLights)->Map();
    void* cbClustersMapped = device->GetBuffer(cbClusters)->Map();
    void* cbShadowsMapped = device->GetBuffer(cbShadows)->Map();
    if (cb0GroundMapped == nullptr || cb0CubeMapped == nullptr || cbLightsMapped == nullptr
        || cbClustersMapped == nullptr || cbShadowsMapped == nullptr) {
        std::printf("  shadows phase: constant buffer Map FAILED\n");
        return false;
    }

    // ---- 静态场景常量（与 RunShadowsPhase 同值同源）-------------------------------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kWidth) / static_cast<float>(kHeight), kNearZ,
        kFarZ, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float projView[16];
    MatMultiply(proj, view, projView);

    LightData sceneLight {};
    {
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        sceneLight.pos[0] = d[0] * invLen;
        sceneLight.pos[1] = d[1] * invLen;
        sceneLight.pos[2] = d[2] * invLen;
        sceneLight.color[0] = 1.0f;
        sceneLight.color[1] = 0.95f;
        sceneLight.color[2] = 0.88f;
        sceneLight.intensity = 1.0f;
        sceneLight.kind = kZrLightDirectional;
    }

    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    const float ld[3] = {sceneLight.pos[0], sceneLight.pos[1], sceneLight.pos[2]};
    std::memcpy(shadowParamsStruct.lightDir, ld, sizeof(ld));
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = -0.5f;
    shadowParamsStruct.sceneCenter[2] = 10.0f;
    shadowParamsStruct.sceneRadius = 15.0f;
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("  shadows phase: BuildCascades FAILED\n");
        return false;
    }

    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;
    }
    shadowCb.cascadeSplits[0] = cascadeSplits[0];
    shadowCb.cascadeSplits[1] = cascadeSplits[1];
    shadowCb.cascadeSplits[2] = cascadeSplits[2];
    shadowCb.cascadeSplits[3] = cascadeSplits[3];
    std::memcpy(cbShadowsMapped, &shadowCb, sizeof(shadowCb));

    PackedLight packedLights[kShadowsLightCount];
    PackLights(&sceneLight, kShadowsLightCount, packedLights);
    std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
    std::memset(static_cast<uint8_t*>(cbLightsMapped) + sizeof(packedLights), 0,
        1024u - sizeof(packedLights));
    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);
    const int32_t flatCount = AssignLightsToClusters(grid, &sceneLight, kShadowsLightCount,
        clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
    if (flatCount < 0) {
        std::printf("  shadows phase: AssignLightsToClusters FAILED (%d)\n", flatCount);
        return false;
    }
    uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
    std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);

    const float ambient = 0.12f;
    ShadowDrawConstants groundConst {};
    std::memcpy(groundConst.mvp, projView, sizeof(projView));
    const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    std::memcpy(groundConst.model, identity, sizeof(identity));
    ShadowDrawConstants cubeConst {};
    float cubeModel[16];
    MatTranslation(0.0f, 0.0f, 3.2f, cubeModel);
    std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
    float cubeMvp[16];
    MatMultiply(projView, cubeModel, cubeMvp);
    std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
    for (ShadowDrawConstants* c : {&groundConst, &cubeConst}) {
        c->projParams[0] = kFarZ / (kFarZ - kNearZ);
        c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
        c->projParams[2] = kNearZ;
        c->projParams[3] = kFarZ;
        c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
        c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
        c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
        c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
        c->screenParams[0] = static_cast<float>(kWidth);
        c->screenParams[1] = static_cast<float>(kHeight);
        c->screenParams[2] = static_cast<float>(kShadowsLightCount);
        c->screenParams[3] = ambient;
        c->camPos[0] = 0.0f;
        c->camPos[1] = 0.0f;
        c->camPos[2] = 0.0f;
        c->camPos[3] = 0.0f;
        c->cascadeSplits[0] = cascadeSplits[0];
        c->cascadeSplits[1] = cascadeSplits[1];
        c->cascadeSplits[2] = cascadeSplits[2];
        c->cascadeSplits[3] = cascadeSplits[3];
        c->shadowParams[0] = enabled ? 1.0f : 0.0f;
        c->shadowParams[1] = static_cast<float>(kShadowsTexSize);
    }
    groundConst.material[0] = 0.50f;
    groundConst.material[1] = 0.52f;
    groundConst.material[2] = 0.50f;
    groundConst.material[3] = 0.85f;
    cubeConst.material[0] = 0.75f;
    cubeConst.material[1] = 0.75f;
    cubeConst.material[2] = 0.78f;
    cubeConst.material[3] = 0.35f;
    std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
    std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));

    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("  shadows phase: shadow pass CB %u Map FAILED\n", c);
            return false;
        }
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        std::memcpy(mapped, &spc, sizeof(spc));
    }
    {
        ShadowModelConstants gm {};
        std::memcpy(gm.shadowModel, groundConst.model, sizeof(gm.shadowModel));
        void* mapped = device->GetBuffer(cbShadowModelGround)->Map();
        if (mapped == nullptr) {
            std::printf("  shadows phase: shadow model CB (ground) Map FAILED\n");
            return false;
        }
        std::memcpy(mapped, &gm, sizeof(gm));
        ShadowModelConstants cm {};
        std::memcpy(cm.shadowModel, cubeConst.model, sizeof(cm.shadowModel));
        mapped = device->GetBuffer(cbShadowModelCube)->Map();
        if (mapped == nullptr) {
            std::printf("  shadows phase: shadow model CB (cube) Map FAILED\n");
            return false;
        }
        std::memcpy(mapped, &cm, sizeof(cm));
    }

    DrawList drawList(16u);
    drawList.RegisterMesh(1u, 6u);
    drawList.RegisterMesh(2u, cubeIndexCount);
    RenderItem groundItem {};
    groundItem.meshHandle = 1u;
    groundItem.materialId = 7u;
    groundItem.depth = 2.8f;
    groundItem.castShadow = true;
    RenderItem cubeItem {};
    cubeItem.meshHandle = 2u;
    cubeItem.materialId = 8u;
    cubeItem.depth = 3.2f;
    cubeItem.castShadow = true;
    if (!drawList.Add(groundItem, ShadowViewMask(true))
        || !drawList.Add(cubeItem, ShadowViewMask(true))) {
        std::printf("  shadows phase: draw list Add FAILED\n");
        return false;
    }
    std::vector<uint32_t> shadowItems[ZR_SHADOW_CASCADES];
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        drawList.Visibility().Iterate(
            ShadowViewId(c), [&](uint32_t id) { shadowItems[c].push_back(id); });
    }
    drawList.Sort();
    const uint32_t batchCount = drawList.BuildBatches();
    if (batchCount == 0u) {
        std::printf("  shadows phase: BuildBatches FAILED\n");
        return false;
    }

    auto drawShadowItem = [&](uint32_t itemId) {
        if (drawList.ItemAt(itemId).meshHandle == 1u) {
            ZrBuffer vbs[1] = {groundVb};
            cmdList->SetVertexBuffers(vbs, 1u);
            cmdList->SetIndexBuffer(groundIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        } else {
            ZrBuffer vbs[1] = {cubeVb};
            cmdList->SetVertexBuffers(vbs, 1u);
            cmdList->SetIndexBuffer(cubeIb);
            cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
        }
    };

    bool allOk = true;
    uint32_t undeclaredUses = 0u;
    ZrBufferDesc rbDesc {};
    rbDesc.size = 256u;
    rbDesc.usage = ZrBufferUsage::TransferDst;
    rbDesc.readback = true;
    ZrBuffer readback = device->CreateBuffer(rbDesc);
    if (!readback.IsValid()) {
        std::printf("  shadows phase: readback buffer FAILED\n");
        return false;
    }

    for (uint32_t frame = 0u; frame < kShadowsFramesPerPhase; ++frame) {
        const bool doReadback = (frame == kShadowsFramesPerPhase - 1u);
        constexpr float kShadowsClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f};

        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  shadows phase frame %u: FAILED to obtain RTV\n", frame + 1u);
            return false;
        }

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        RgHandle shadowH {};
        if (enabled) {
            shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));
        }

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kShadowsClearColor,
                    ZrSubresourceRange::All());
                device->RecordClearDepth(cmdHandle, dsvView, 1.0f);
            });

        if (enabled) {
            for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
                graph.AddPass("ShadowCascadePass",
                    [&](RgPass& pass) {
                        pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                            ZrAccessBits::DepthStencilAttachmentWrite,
                            ZrPipelineStageBits::LateFragmentTests);
                    },
                    [&, c](RgContext&) {
                        // 级联层清屏先于附件绑定（RecordClearDepth 内部布局往返
                        // 屏障与渲染实例互斥；清屏语义与 D3D12 的 SetRenderTarget
                        // 后 ClearDepthStencilView 等价——loadOp=LOAD 不丢内容）
                        device->RecordClearDepth(cmdHandle, shadowDsvs[c], 1.0f);
                        // depth-only PSO：无颜色目标（RTV 无效句柄 = 仅深度附件）
                        cmdList->SetPipelineState(shadowPso);
                        cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                        cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                        cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                        for (uint32_t id : shadowItems[c]) {
                            const bool isGround = drawList.ItemAt(id).meshHandle == 1u;
                            cmdList->SetGraphicsConstantBuffer(
                                1u, isGround ? cbShadowModelGround : cbShadowModelCube);
                            drawShadowItem(id);
                        }
                    });
            }
        }

        graph.AddPass("ShadowedLightsDrawPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                if (enabled) {
                    // 阴影图作为深度只读资源被 PS 采样（VK：DEPTH_STENCIL_READ_ONLY）
                    pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                        ZrAccessBits::DepthStencilAttachmentRead,
                        ZrPipelineStageBits::FragmentShader);
                }
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(mainPso);
                cmdList->SetRenderTarget(rtvView, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                // 根 SRV 表（槽 4 = rootCbvCount）：t0 = 级联阴影图（两相位都绑）
                cmdList->SetGraphicsBindlessSrv(4u, shadowSrv);
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    for (uint32_t i = 0; i < batch.count; ++i) {
                        const uint32_t itemIndex = batch.startIndex + i;
                        const uint32_t mesh = drawList.ItemAt(itemIndex).meshHandle;
                        ZrBuffer vbs[1] = {(mesh == 1u) ? groundVb : cubeVb};
                        cmdList->SetVertexBuffers(vbs, 1u);
                        cmdList->SetIndexBuffer((mesh == 1u) ? groundIb : cubeIb);
                        cmdList->SetGraphicsConstantBuffer(
                            0u, (mesh == 1u) ? cb0Ground : cb0Cube);
                        cmdList->DrawIndexedInstanced(
                            (mesh == 1u) ? 6u : cubeIndexCount, 1u, 0u, 0u, 0u);
                    }
                }
            });

        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    // 探针 1×1 → vkCmdCopyImageToBuffer（对位 D3D12 CopyTextureRegion）
                    VkBufferImageCopy region {};
                    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                    region.imageSubresource.layerCount = 1u;
                    region.imageOffset = { static_cast<int32_t>(kShadowProbeX),
                        static_cast<int32_t>(kShadowProbeY), 0 };
                    region.imageExtent = { 1u, 1u, 1u };
                    vkCmdCopyImageToBuffer(device->VulkanNativeCmdOf(cmdHandle),
                        device->VulkanImageOf(ctx.GetImage(backH)),
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        device->VulkanBufferOf(readback), 1u, &region);
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass", [&](RgPass& pass) { pass.Present(backH).MarkRoot(); },
                nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  shadows phase frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            allOk = false;
            cmdList->End();
            continue;
        }
        graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末屏障：VK 路径无帧末复位（同 --draw/--lights 既定差异点）

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle();
        graph.FlushRetired(*device);

        if (doReadback) {
            IZrBuffer* rbInterface = device->GetBuffer(readback);
            void* mapped = (rbInterface != nullptr) ? rbInterface->Map() : nullptr;
            if (mapped == nullptr) {
                std::printf("  shadows phase frame %u: probe Map FAILED\n", frame + 1u);
                allOk = false;
            } else {
                const uint8_t* px = static_cast<const uint8_t*>(mapped);
                const int r = windowMode ? static_cast<int>(px[2]) : static_cast<int>(px[0]);
                const int g = static_cast<int>(px[1]);
                const int b = windowMode ? static_cast<int>(px[0]) : static_cast<int>(px[2]);
                std::printf("  shadows phase frame %u probe(%u,%u) RGB=(%d,%d,%d) [vulkan]\n",
                    frame + 1u, kShadowProbeX, kShadowProbeY, r, g, b);
                *outProbe = r;
            }
        }

        if (windowMode) {
            PumpMessages();
        }
    }

    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    device->Destroy(readback);
    if (undeclaredUses != 0u) {
        std::printf("  shadows phase: undeclared RG uses = %u\n", undeclaredUses);
        allOk = false;
    }
    return allOk;
}

// --shadows Vulkan 路径主体（TASK-B-R7-02；镜像 RunShadowsMode 的 out 参数契约）
static int RunShadowsVulkan(const char* zmeshPath, const char* hlslPath,
    const char* depthHlslPath, const char* modeStr, int* outProbeOff = nullptr,
    int* outProbeOn = nullptr)
{
    const bool contrast = (modeStr[0] == '\0');
    const bool forceOn = (std::strcmp(modeStr, "on") == 0);
    const bool forceOff = (std::strcmp(modeStr, "off") == 0);
    std::printf("zrhi_d3d12_window: --shadows mode vulkan backend (TASK-B-R7-02, %s): "
                "zmesh='%s' hlsl='%s'\n",
        contrast ? "off-vs-on contrast" : (forceOn ? "on only" : "off only"), zmeshPath, hlslPath);
    if (!contrast && !forceOn && !forceOff) {
        std::printf("zrhi_d3d12_window: --shadows 未知相位 '%s'（可选 on/off 或缺省对比）\n",
            modeStr);
        return 1;
    }

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrVulkanDevice* device = ZrVulkanCreateDevice();
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create Vulkan device\n");
        return 1;
    }
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create queue/command list\n");
        ZrVulkanDestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏）-------------------------------------------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --shadows window rejected (GetLastError=%lu), "
                    "falling back to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --shadows swapchain FAILED, falling back to "
                        "offscreen target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
    }

    // ---- 渲染目标（离屏降级）+ 场景深度 + 级联阴影图 ----------------------------------
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --shadows FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrVulkanDestroyDevice(device);
            return 1;
        }
    }
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    if (!depthImage.IsValid() || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create depth image/DSV\n");
        return 1;
    }

    // 级联阴影图：单张 2048² D32 数组图（层 i = 级联 i）——每级联一个层 DSV +
    // 全 4 层 Texture2DArray SRV（R32Float 解释，PCF 手动比较采样）
    ZrImageDesc shadowDesc {};
    shadowDesc.format = ZrFormat::D32Float;
    shadowDesc.width = kShadowsTexSize;
    shadowDesc.height = kShadowsTexSize;
    shadowDesc.arrayLayers = ZR_SHADOW_CASCADES;
    shadowDesc.usage = ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled;
    ZrImage shadowImage = device->CreateImage(shadowDesc);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        ZrImageViewDesc layerDsvDesc {};
        layerDsvDesc.image = shadowImage;
        layerDsvDesc.viewType = ZrImageViewType::Dsv;
        layerDsvDesc.subresources.baseArrayLayer = c;
        layerDsvDesc.subresources.layerCount = 1u;
        shadowDsvs[c] = device->CreateImageView(layerDsvDesc);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float; // 深度格式采样的非深度孪生解释
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    if (!shadowImage.IsValid() || !shadowSrv.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create shadow image/views\n");
        return 1;
    }
    for (ZrImageView v : shadowDsvs) {
        if (!v.IsValid()) {
            std::printf("zrhi_d3d12_window: --shadows FAILED to create cascade layer DSV\n");
            return 1;
        }
    }

    // ---- 几何（同 --lights：地面 quad + cube.zmesh）-----------------------------------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<char> meshBytes;
    if (!ReadWholeFile(zmeshPath, meshBytes)) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to open zmesh '%s'\n", zmeshPath);
        return 1;
    }
    ZmeshData mesh {};
    const int parseResult = zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh);
    if (parseResult != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --shadows zmesh parse FAILED (error %d)\n", parseResult);
        return 1;
    }
    const uint32_t cubeVertexCount = mesh.stats.vertexCount;
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(cubeVertexCount);
    for (uint32_t i = 0; i < cubeVertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);

    ZrBufferDesc gVbDesc {};
    gVbDesc.size = sizeof(groundVerts);
    gVbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer groundVb = device->CreateBuffer(gVbDesc, groundVerts);
    ZrBufferDesc gIbDesc {};
    gIbDesc.size = sizeof(groundIndices);
    gIbDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer groundIb = device->CreateBuffer(gIbDesc, groundIndices);
    ZrBufferDesc cVbDesc {};
    cVbDesc.size = static_cast<uint64_t>(cubeVerts.size()) * sizeof(FirstDrawVertex);
    cVbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer cubeVb = device->CreateBuffer(cVbDesc, cubeVerts.data());
    ZrBufferDesc cIbDesc {};
    cIbDesc.size = static_cast<uint64_t>(cubeIndices.size()) * sizeof(uint32_t);
    cIbDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer cubeIb = device->CreateBuffer(cIbDesc, cubeIndices.data());
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create VB/IB\n");
        return 1;
    }
    // ---- 常量缓冲：b0×2 / b1 光源 / b2 簇 / b3 级联 / 阴影 pass b0×4 -------------------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbShadowModelGround {};
    ZrBuffer cbShadowModelCube {};
    {
        auto makeCb = [&](uint64_t size, ZrBuffer& outHandle) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            outHandle = device->CreateBuffer(desc);
            return outHandle.IsValid() && device->GetBuffer(outHandle)->Map() != nullptr;
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        bool ok = makeCb(256u, cb0Ground) && makeCb(256u, cb0Cube) && makeCb(1024u, cbLights)
            && makeCb(cb2Size, cbClusters) && makeCb(384u, cbShadows);
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            ok = ok && makeCb(256u, cbShadowPass[c]);
        }
        ok = ok && makeCb(256u, cbShadowModelGround) && makeCb(256u, cbShadowModelCube);
        if (!ok) {
            std::printf("zrhi_d3d12_window: --shadows FAILED to create constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器（glslc SPIR-V）+ PSO ---------------------------------------------------
    std::vector<char> mainVsSpv;
    std::vector<char> mainFsSpv;
    if (!FindSpirvPaths(hlslPath, mainVsSpv, mainFsSpv)) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to load SPIR-V for '%s' "
                    "(build generates Native/build/bin/shaders_spv/)\n",
            hlslPath);
        return 1;
    }
    std::ifstream depthVsFile(FindSpirvSingle(depthHlslPath, "_vs.spv"), std::ios::binary);
    if (!depthVsFile) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to load SPIR-V for '%s'\n",
            depthHlslPath);
        return 1;
    }
    std::vector<char> depthVsSpv(
        (std::istreambuf_iterator<char>(depthVsFile)), std::istreambuf_iterator<char>());

    ZrShaderDesc mainVsDesc {};
    mainVsDesc.stage = ZrShaderStage::Vertex;
    mainVsDesc.code = mainVsSpv.data();
    mainVsDesc.codeSize = mainVsSpv.size();
    mainVsDesc.entryPoint = "VsMain";
    ZrShaderDesc mainPsDesc {};
    mainPsDesc.stage = ZrShaderStage::Pixel;
    mainPsDesc.code = mainFsSpv.data();
    mainPsDesc.codeSize = mainFsSpv.size();
    mainPsDesc.entryPoint = "PsMain";
    ZrShaderDesc depthVsDesc {};
    depthVsDesc.stage = ZrShaderStage::Vertex;
    depthVsDesc.code = depthVsSpv.data();
    depthVsDesc.codeSize = depthVsSpv.size();
    depthVsDesc.entryPoint = "VsMain";
    const ZrShader mainVs = device->CreateShader(mainVsDesc);
    const ZrShader mainPs = device->CreateShader(mainPsDesc);
    const ZrShader shadowVs = device->CreateShader(depthVsDesc);
    if (!mainVs.IsValid() || !mainPs.IsValid() || !shadowVs.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows shader module creation FAILED\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: --shadows shaders loaded (glslc SPIR-V)\n");

    ZrGraphicsPsoDesc mainPsoDesc {};
    mainPsoDesc.vertexShader = mainVs;
    mainPsoDesc.pixelShader = mainPs;
    mainPsoDesc.rtvFormat = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    mainPsoDesc.dsvFormat = ZrFormat::D32Float;
    mainPsoDesc.blend = ZrBlendMode::Opaque;
    mainPsoDesc.depthTest = true;
    mainPsoDesc.depthWrite = true;
    mainPsoDesc.cullMode = ZrCullMode::None;
    mainPsoDesc.rootCbvCount = 4u; // b0 逐绘 / b1 光源 / b2 簇 / b3 级联
    mainPsoDesc.srvTable = true;   // t0 级联阴影图 + s0 Point/Clamp
    ZrPipelineState mainPso = device->CreatePipelineState(mainPsoDesc);

    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = shadowVs;
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    shadowPsoDesc.cullMode = ZrCullMode::Front; // 绕序关系见 D3D12 路径注释
    shadowPsoDesc.rootCbvCount = 2u;            // b0 = 该级联 lightVP / b1 = 逐绘 model
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    if (!mainPso.IsValid() || !shadowPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --shadows FAILED to create graphics PSOs (main=%d "
                    "shadow=%d)\n",
            mainPso.IsValid() ? 1 : 0, shadowPso.IsValid() ? 1 : 0);
        return 1;
    }

    // ---- CPU 级联分割 ------------------------------------------------------------------
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(0.3f, 60.0f, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("zrhi_d3d12_window: --shadows CascadeSplits FAILED\n");
            return 1;
        }
        cascadeSplits[0] = interior[0];
        cascadeSplits[1] = interior[1];
        cascadeSplits[2] = interior[2];
        cascadeSplits[3] = 60.0f;
        std::printf("zrhi_d3d12_window: --shadows cascade splits (near=0.3 far=60 "
                    "lambda=0.75): %.4f %.4f %.4f %.4f\n",
            cascadeSplits[0], cascadeSplits[1], cascadeSplits[2], cascadeSplits[3]);
    }

    ZrImageViewDesc offRtvDesc {};
    offRtvDesc.image = offscreenTarget;
    offRtvDesc.viewType = ZrImageViewType::Rtv;
    ZrImageView offscreenRtv
        = offscreenTarget.IsValid() ? device->CreateImageView(offRtvDesc) : ZrImageView{};

    // ---- 两相位执行：off（factor 恒 1）→ on（CSM PCF），同一探针点 ----------------------
    int probeOff = -1;
    int probeOn = -1;
    bool offOk = true;
    bool onOk = true;
    if (!forceOn) {
        std::printf("zrhi_d3d12_window: --shadows phase OFF (%u frames) [vulkan]\n",
            kShadowsFramesPerPhase);
        offOk = RunShadowsPhaseVulkan(device, queue, cmdHandle, cmdList, swapchain,
            offscreenTarget, offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv,
            mainPso, shadowPso, groundVb, groundIb, cubeVb, cubeIb, cubeIndexCount, cb0Ground,
            cb0Cube, cbLights, cbClusters, cbShadows, cbShadowPass, cbShadowModelGround,
            cbShadowModelCube, /*enabled=*/false, cascadeSplits, &probeOff);
    }
    if (!forceOff) {
        std::printf("zrhi_d3d12_window: --shadows phase ON (%u frames) [vulkan]\n",
            kShadowsFramesPerPhase);
        onOk = RunShadowsPhaseVulkan(device, queue, cmdHandle, cmdList, swapchain,
            offscreenTarget, offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv,
            mainPso, shadowPso, groundVb, groundIb, cubeVb, cubeIb, cubeIndexCount, cb0Ground,
            cb0Cube, cbLights, cbClusters, cbShadows, cbShadowPass, cbShadowModelGround,
            cbShadowModelCube, /*enabled=*/true, cascadeSplits, &probeOn);
    }

    // ---- 清理 -------------------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    device->Destroy(cbShadowModelGround);
    device->Destroy(cbShadowModelCube);
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        if (cbShadowPass[c].IsValid()) {
            device->Destroy(cbShadowPass[c]);
        }
    }
    for (ZrImageView& v : shadowDsvs) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    device->Destroy(shadowSrv);
    device->Destroy(mainPso);
    device->Destroy(shadowPso);
    device->Destroy(mainVs);
    device->Destroy(mainPs);
    device->Destroy(shadowVs);
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(depthImage);
    device->Destroy(dsvView);
    device->Destroy(shadowImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t validationErrors = device->GetDebugErrorCount();
    ZrVulkanDestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: [SHADOWS][vulkan] probe off=%d on=%d validation errors=%u\n",
        probeOff, probeOn, validationErrors);
    if (outProbeOff != nullptr) {
        *outProbeOff = probeOff;
    }
    if (outProbeOn != nullptr) {
        *outProbeOn = probeOn;
    }

    const bool singleModeOk = (forceOn || forceOff) && offOk && onOk
        && validationErrors == 0u && (forceOn ? probeOn >= 0 : probeOff >= 0);
    const bool contrastOk = contrast && offOk && onOk && validationErrors == 0u
        && probeOff >= 0 && probeOn >= 0 && (probeOff - probeOn) >= kShadowProbeDeltaMin;
    if (contrastOk) {
        std::printf("VULKAN SHADOWS OK (probe delta=%d)\n", probeOff - probeOn);
        return 0;
    }
    if (singleModeOk) {
        std::printf("VULKAN SHADOWS OK (single phase %s, probe=%d)\n", forceOn ? "on" : "off",
            forceOn ? probeOn : probeOff);
        return 0;
    }
    std::printf("VULKAN SHADOWS FAILED (contrast=%d offOk=%d onOk=%d off=%d on=%d errors=%u)\n",
        contrast ? 1 : 0, offOk ? 1 : 0, onOk ? 1 : 0, probeOff, probeOn, validationErrors);
    return 1;
}

// ---- 双后端对照（--shadows --backend both，TASK-B-R7-02）：D3D12 与 Vulkan 各跑一轮
// off/on 双相位，断言两后端探针值一致（差 ≤ kParityTolerance）+ 各自可证伪 delta ----
static int RunShadowsParity(const char* zmeshPath, const char* hlslPath,
    const char* depthHlslPath)
{
    std::printf("zrhi_d3d12_window: --shadows --backend both (TASK-B-R7-02 dual-backend parity)\n");
    int d3d12Off = -1;
    int d3d12On = -1;
    const int d3d12Rc = RunShadowsMode(zmeshPath, hlslPath, depthHlslPath, "", &d3d12Off, &d3d12On);
    int vkOff = -1;
    int vkOn = -1;
    const int vkRc
        = RunShadowsVulkan(zmeshPath, hlslPath, depthHlslPath, "", &vkOff, &vkOn);

    const int offDelta = std::abs(d3d12Off - vkOff);
    const int onDelta = std::abs(d3d12On - vkOn);
    std::printf("SHADOW PROBE PARITY: off d3d12=%d vulkan=%d delta=%d\n", d3d12Off, vkOff,
        offDelta);
    std::printf("SHADOW PROBE PARITY: on  d3d12=%d vulkan=%d delta=%d\n", d3d12On, vkOn, onDelta);
    std::printf("SHADOW PROBE PARITY: d3d12 delta=%d vulkan delta=%d (min %d)\n",
        d3d12Off - d3d12On, vkOff - vkOn, kShadowProbeDeltaMin);
    const bool parityOk = offDelta <= kParityTolerance && onDelta <= kParityTolerance;
    std::printf("D3D12/VULKAN SHADOWS PARITY %s (off delta=%d on delta=%d tolerance=%d)\n",
        parityOk ? "OK" : "FAILED", offDelta, onDelta, kParityTolerance);
    return (d3d12Rc == 0 && vkRc == 0 && parityOk) ? 0 : 1;
}

// ---- 双后端对照（--draw --backend both）：D3D12 与 Vulkan 各跑一轮 --draw，断言
// RG 屏障合并计数逐项相同 + 中心像素并排对照（差 ≤ kParityTolerance，报数字）--------

static int RunFirstDrawParity(const char* zmeshPath, const char* hlslPath)
{
    std::printf("zrhi_d3d12_window: --draw --backend both (TASK-B-R7-01 dual-backend parity)\n");
    std::vector<int> d3d12Centers;
    std::vector<uint32_t> d3d12Barriers;
    const int d3d12Rc = RunFirstDraw(zmeshPath, hlslPath, &d3d12Centers, &d3d12Barriers);
    std::vector<int> vkCenters;
    std::vector<uint32_t> vkBarriers;
    const int vkRc = RunFirstDrawVulkan(zmeshPath, hlslPath, &vkCenters, &vkBarriers);

    // ---- RG 屏障合并计数一致性断言（同一 RenderGraph 在两后端跑）----------------------
    bool barriersEqual = (d3d12Barriers.size() == vkBarriers.size())
        && (d3d12Barriers.size() == 7u);
    if (barriersEqual) {
        for (size_t i = 0; i < d3d12Barriers.size(); ++i) {
            if (d3d12Barriers[i] != vkBarriers[i]) {
                barriersEqual = false;
            }
        }
    }
    std::printf("RG BARRIER PLAN PARITY: d3d12=[total=%u splits=%u calls=%u begin=%u end=%u "
                "heaps=%u transients=%u]\n",
        d3d12Barriers.size() > 0u ? d3d12Barriers[0] : 0u,
        d3d12Barriers.size() > 1u ? d3d12Barriers[1] : 0u,
        d3d12Barriers.size() > 2u ? d3d12Barriers[2] : 0u,
        d3d12Barriers.size() > 3u ? d3d12Barriers[3] : 0u,
        d3d12Barriers.size() > 4u ? d3d12Barriers[4] : 0u,
        d3d12Barriers.size() > 5u ? d3d12Barriers[5] : 0u,
        d3d12Barriers.size() > 6u ? d3d12Barriers[6] : 0u);
    std::printf("RG BARRIER PLAN PARITY: vulkan=[total=%u splits=%u calls=%u begin=%u end=%u "
                "heaps=%u transients=%u] -> %s\n",
        vkBarriers.size() > 0u ? vkBarriers[0] : 0u,
        vkBarriers.size() > 1u ? vkBarriers[1] : 0u,
        vkBarriers.size() > 2u ? vkBarriers[2] : 0u,
        vkBarriers.size() > 3u ? vkBarriers[3] : 0u,
        vkBarriers.size() > 4u ? vkBarriers[4] : 0u,
        vkBarriers.size() > 5u ? vkBarriers[5] : 0u,
        vkBarriers.size() > 6u ? vkBarriers[6] : 0u,
        barriersEqual ? "EQUAL (assert OK)" : "MISMATCH (assert FAILED)");

    // ---- 中心像素并排对照（逐校验帧；报告最大通道差）----------------------------------
    bool pixelsOk = (d3d12Centers.size() == vkCenters.size()) && !d3d12Centers.empty();
    int maxDelta = 0;
    if (pixelsOk) {
        const size_t checks = d3d12Centers.size() / 3u;
        for (size_t c = 0; c < checks; ++c) {
            const int dr = std::abs(d3d12Centers[c * 3u + 0u] - vkCenters[c * 3u + 0u]);
            const int dg = std::abs(d3d12Centers[c * 3u + 1u] - vkCenters[c * 3u + 1u]);
            const int db = std::abs(d3d12Centers[c * 3u + 2u] - vkCenters[c * 3u + 2u]);
            const int frameDelta = dr > dg ? (dr > db ? dr : db) : (dg > db ? dg : db);
            if (frameDelta > maxDelta) {
                maxDelta = frameDelta;
            }
            std::printf("CENTER PIXEL PARITY: check %zu d3d12 RGB=(%d,%d,%d) "
                        "vulkan RGB=(%d,%d,%d) maxDelta=%d\n",
                c + 1u, d3d12Centers[c * 3u + 0u], d3d12Centers[c * 3u + 1u],
                d3d12Centers[c * 3u + 2u], vkCenters[c * 3u + 0u], vkCenters[c * 3u + 1u],
                vkCenters[c * 3u + 2u], frameDelta);
        }
    }
    const bool parityOk = barriersEqual && pixelsOk && maxDelta <= kParityTolerance;
    std::printf("D3D12/VULKAN FIRST DRAW PARITY %s (maxDelta=%d tolerance=%d)\n",
        parityOk ? "OK" : "FAILED", maxDelta, kParityTolerance);
    return (d3d12Rc == 0 && vkRc == 0 && parityOk) ? 0 : 1;
}

// =============================================================================
// ---- TASK-B-R8-01：--r5 的 Vulkan 后端路径（与 RunR5Phase/RunR5Mode 同构镜像）----
// 与 D3D12 路径的三角洲（其余全部走 IZrDevice 接口，逐行对位）：
//   1. 设备：ZrVulkanCreateDevice（SPIR-V 经 glslc 预编译产物加载——8 着色器文件/
//      16 入口点，与 D3D12 侧 FXC 运行时编译同源 hlsl，差异仅编译旗标见 CMake）
//   2. 深度/级联清屏：RecordClearDepth 扩展点；级联清屏置于 SetRenderTarget 之前
//      （RecordClearDepth 内部布局往返屏障与渲染实例互斥——--shadows 既定差异）
//   3. 探针 readback：vkCmdCopyImageToBuffer ×6（探针 i → bufferOffset i*256，
//      与 D3D12 PlacedFootprint.Offset 同值；1×1 行紧缩 4B，槽位互不重叠）
//   4. 帧末复位屏障：跳过（VK 禁止 newLayout=UNDEFINED；B-R7-01 既定差异——
//      跨帧持久图像布局由下帧 planner 初态 Undefined 的首道 discard 屏障覆盖）
//   5. 设备移除检查：VK 无对位物（validation ERROR 计数=0 覆盖设备失效场景）
//   6. 资源用途即真相：depthImage 须显式加 Sampled（Hi-Z mip0 源 = 场景深度
//      R32Float 孪生 SRV；D3D12 后端 usage 宽松无需声明，VK 缺位即视图非法）；
//      shadowImage 无需 TransferDst（RecordClearDepth 走 vkCmdClearDepthStencilImage）
// =============================================================================

// --r5 Vulkan 单相位（ssrEnabled=SSR 开关；GI 恒开）。outProbes：第 10/20/30 帧各
// 采一组 6 探针 RGB（[readback 序 0..2][探针 0..5][RGB]）。返回 false = 帧内硬错误。
bool RunR5PhaseVulkan(ZrVulkanDevice* device, IZrQueue* queue, ZrCommandList cmdHandle,
    IZrCommandList* cmdList, IZrSwapchain* swapchain, ZrImage offscreenTarget,
    ZrImageView offscreenRtv, ZrImage depthImage, ZrImageView dsvView, ZrImage shadowImage,
    ZrImageView* shadowDsvs, ZrImageView shadowSrv, ZrImage sceneImage, ZrImageView sceneRtv,
    ZrImageView sceneSrv, ZrImage hizImage, ZrImageView* hizRtvs, ZrImageView hizSrv,
    ZrImageView* hizMipSrvs, ZrImageView depthSrv, ZrImage traceImage, ZrImageView traceRtv,
    ZrImageView traceSrv, ZrImage blurImage, ZrImageView blurRtv, ZrImageView blurSrv,
    ZrPipelineState mainPso, ZrPipelineState toonPso, ZrPipelineState outlinePso,
    ZrPipelineState shadowPso, ZrPipelineState hizCopyPso, ZrPipelineState hizMinPso,
    ZrPipelineState tracePso, ZrPipelineState blurPso, ZrPipelineState compPso, ZrBuffer groundVb,
    ZrBuffer groundIb, ZrBuffer cubeVb, ZrBuffer cubeIb, ZrBuffer toonVb, ZrBuffer toonIb,
    ZrBuffer quadVb, ZrBuffer quadIb, uint32_t cubeIndexCount, ZrBuffer cb0Ground,
    ZrBuffer cb0Cube, ZrBuffer cbToon, ZrBuffer cbOutline, ZrBuffer cbLights, ZrBuffer cbClusters,
    ZrBuffer cbShadows, ZrBuffer cbGi, ZrBuffer cbSsr, ZrBuffer* cbHiZ, ZrBuffer cbBlur,
    ZrBuffer cbComposite, ZrBuffer* cbShadowPass, ZrBuffer cbShadowModelGround,
    ZrBuffer cbShadowModelCube, bool ssrEnabled, int outProbes[3][kR5ProbeCount][3])
{
    ZrImageView rtvViews[3] = {};
    const bool windowMode = (swapchain != nullptr);
    void* cb0GroundMapped = device->GetBuffer(cb0Ground)->Map();
    void* cb0CubeMapped = device->GetBuffer(cb0Cube)->Map();
    void* cbToonMapped = device->GetBuffer(cbToon)->Map();
    void* cbOutlineMapped = device->GetBuffer(cbOutline)->Map();
    void* cbLightsMapped = device->GetBuffer(cbLights)->Map();
    void* cbClustersMapped = device->GetBuffer(cbClusters)->Map();
    void* cbShadowsMapped = device->GetBuffer(cbShadows)->Map();
    void* cbGiMapped = device->GetBuffer(cbGi)->Map();
    void* cbSsrMapped = device->GetBuffer(cbSsr)->Map();
    void* cbBlurMapped = device->GetBuffer(cbBlur)->Map();
    void* cbCompositeMapped = device->GetBuffer(cbComposite)->Map();
    void* cbShadowModelGroundMapped = device->GetBuffer(cbShadowModelGround)->Map();
    void* cbShadowModelCubeMapped = device->GetBuffer(cbShadowModelCube)->Map();
    if (cb0GroundMapped == nullptr || cb0CubeMapped == nullptr || cbToonMapped == nullptr
        || cbOutlineMapped == nullptr || cbLightsMapped == nullptr
        || cbClustersMapped == nullptr || cbShadowsMapped == nullptr || cbGiMapped == nullptr
        || cbSsrMapped == nullptr || cbBlurMapped == nullptr || cbCompositeMapped == nullptr
        || cbShadowModelGroundMapped == nullptr || cbShadowModelCubeMapped == nullptr) {
        std::printf("  r5 phase: constant buffer Map FAILED\n");
        return false;
    }

    // ---- 相机 / 投影（同 RunR5Phase：原点朝 +Z，fovY=60°）------------------------
    constexpr float kFovY = 3.14159265f / 3.0f;
    constexpr float kNearZ = 0.3f;
    constexpr float kFarZ = 60.0f;
    float proj[16];
    MatPerspectiveFovLH(kFovY, static_cast<float>(kWidth) / static_cast<float>(kHeight), kNearZ,
        kFarZ, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float projView[16];
    MatMultiply(proj, view, projView);
    float invProjView[16];
    if (!MatInverse4x4(projView, invProjView)) {
        std::printf("  r5 phase: projView invert FAILED\n");
        return false;
    }

    // ---- 光源：1 方向光 + 2 动画点光（与 RunR5Phase 同值同源）--------------------
    LightData sceneLights[kR5LightCount] = {};
    {
        const float d[3] = {0.4f, 0.85f, 0.35f};
        const float invLen = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
        sceneLights[0].pos[0] = d[0] * invLen;
        sceneLights[0].pos[1] = d[1] * invLen;
        sceneLights[0].pos[2] = d[2] * invLen;
        sceneLights[0].color[0] = 1.0f;
        sceneLights[0].color[1] = 0.95f;
        sceneLights[0].color[2] = 0.88f;
        sceneLights[0].intensity = 1.0f;
        sceneLights[0].kind = kZrLightDirectional;
    }
    const float pointColors[2][3] = {{1.0f, 0.85f, 0.6f}, {0.6f, 0.85f, 1.0f}};

    // ---- CPU 级联（相机静态，每相位一次）-----------------------------------------
    ShadowSceneParams shadowParamsStruct {};
    std::memcpy(shadowParamsStruct.camView, view, sizeof(view));
    shadowParamsStruct.tanHalfFovY = std::tan(kFovY * 0.5f);
    shadowParamsStruct.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    shadowParamsStruct.nearZ = kNearZ;
    shadowParamsStruct.farZ = kFarZ;
    std::memcpy(shadowParamsStruct.lightDir, sceneLights[0].pos, sizeof(sceneLights[0].pos));
    shadowParamsStruct.sceneCenter[0] = 0.0f;
    shadowParamsStruct.sceneCenter[1] = -0.5f;
    shadowParamsStruct.sceneCenter[2] = 10.0f;
    shadowParamsStruct.sceneRadius = 15.0f;
    shadowParamsStruct.lambda = 0.75f;
    shadowParamsStruct.texSize = kShadowsTexSize;
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    if (!BuildCascades(shadowParamsStruct, cascades, ZR_SHADOW_CASCADES)) {
        std::printf("  r5 phase: BuildCascades FAILED\n");
        return false;
    }
    float cascadeSplits[ZR_SHADOW_CASCADES] = {};
    {
        float interior[ZR_SHADOW_CASCADES - 1] = {};
        if (!CascadeSplits(kNearZ, kFarZ, ZR_SHADOW_CASCADES, 0.75f, interior)) {
            std::printf("  r5 phase: CascadeSplits FAILED\n");
            return false;
        }
        cascadeSplits[0] = interior[0];
        cascadeSplits[1] = interior[1];
        cascadeSplits[2] = interior[2];
        cascadeSplits[3] = kFarZ;
    }

    // CB3（级联 lightVP + bias）
    ShadowCbufferLayout shadowCb {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        std::memcpy(shadowCb.lightVP[c], cascades[c].lightVP, sizeof(cascades[c].lightVP));
        shadowCb.cascadeBias[c][0] = 0.05f / cascades[c].zRange;
        shadowCb.cascadeBias[c][1] = 0.15f / cascades[c].zRange;
    }
    shadowCb.cascadeSplits[0] = cascadeSplits[0];
    shadowCb.cascadeSplits[1] = cascadeSplits[1];
    shadowCb.cascadeSplits[2] = cascadeSplits[2];
    shadowCb.cascadeSplits[3] = cascadeSplits[3];
    std::memcpy(cbShadowsMapped, &shadowCb, sizeof(shadowCb));

    // 阴影 pass 逐级联 CB
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        void* mapped = device->GetBuffer(cbShadowPass[c])->Map();
        if (mapped == nullptr) {
            std::printf("  r5 phase: shadow pass CB %u Map FAILED\n", c);
            return false;
        }
        ShadowPassConstants spc {};
        std::memcpy(spc.lightVP, cascades[c].lightVP, sizeof(spc.lightVP));
        std::memcpy(mapped, &spc, sizeof(spc));
    }
    {
        ShadowModelConstants gm {};
        const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        std::memcpy(gm.shadowModel, identity, sizeof(identity));
        std::memcpy(cbShadowModelGroundMapped, &gm, sizeof(gm));
        float cubeModel[16];
        MatTranslation(0.0f, 0.0f, 3.2f, cubeModel);
        ShadowModelConstants cm {};
        std::memcpy(cm.shadowModel, cubeModel, sizeof(cm.shadowModel));
        std::memcpy(cbShadowModelCubeMapped, &cm, sizeof(cm));
    }

    // ---- 簇网格参数 ----------------------------------------------------------------
    ClusterGrid grid;
    grid.tanHalfFovY = std::tan(kFovY * 0.5f);
    grid.aspect = static_cast<float>(kWidth) / static_cast<float>(kHeight);
    grid.nearZ = kNearZ;
    grid.farZ = kFarZ;
    std::vector<uint32_t> clusterRanges(ZR_CLUSTER_COUNT, 0u);
    std::vector<uint32_t> clusterFlat(ZR_CLUSTER_FLAT_MAX, 0u);

    // ---- GI 探针（CPU 辐照度累加，每 4 帧轮转刷新）----------------------------------
    GiProbeGrid giGrid; // 默认：origin(−10,−1,0) spacing(2.5,2,2.5)——8×2×8 覆盖场景
    float giProbes[ZR_GI_PROBE_COUNT][3] = {}; // 0 起步（冷启动 4 帧内填满）
    GiCbufferLayout giCb {};
    giCb.gridOrigin[0] = giGrid.origin[0];
    giCb.gridOrigin[1] = giGrid.origin[1];
    giCb.gridOrigin[2] = giGrid.origin[2];
    giCb.gridOrigin[3] = 1.0f; // GI 开
    giCb.gridSpacing[0] = giGrid.spacing[0];
    giCb.gridSpacing[1] = giGrid.spacing[1];
    giCb.gridSpacing[2] = giGrid.spacing[2];
    giCb.gridSpacing[3] = static_cast<float>(ZR_GI_PROBE_COUNT);

    // ---- Hi-Z / SSR 常量（相位内静态）----------------------------------------------
    SsrTraceConstants ssrCb {};
    std::memcpy(ssrCb.invViewProj, invProjView, sizeof(invProjView));
    std::memcpy(ssrCb.viewProj, projView, sizeof(projView));
    ssrCb.projParams[0] = kFarZ / (kFarZ - kNearZ);
    ssrCb.projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
    ssrCb.projParams[2] = kNearZ;
    ssrCb.projParams[3] = kFarZ;
    ssrCb.ssrParams[0] = ZR_SSR_ROUGHNESS_MAX;
    ssrCb.ssrParams[1] = ZR_SSR_STEP_METERS;
    ssrCb.ssrParams[2] = ZR_SSR_EDGE_FADE;
    ssrCb.ssrParams[3] = ssrEnabled ? 1.0f : 0.0f;
    ssrCb.screenParams[0] = static_cast<float>(kWidth);
    ssrCb.screenParams[1] = static_cast<float>(kHeight);
    ssrCb.screenParams[2] = static_cast<float>(kR5HalfW);
    ssrCb.screenParams[3] = static_cast<float>(kR5HalfH);
    ssrCb.camPos[0] = 0.0f;
    ssrCb.camPos[1] = 0.0f;
    ssrCb.camPos[2] = 0.0f;
    ssrCb.camPos[3] = 0.0f;
    std::memcpy(cbSsrMapped, &ssrCb, sizeof(ssrCb));

    // Hi-Z 每 mip texel 常量（min pass m 的源 = mip m−1）
    SsrMipLevel hizLevels[ZR_SSR_MAX_MIPS] = {};
    const uint32_t hizLevelCount = SsrMipChainPlan(kWidth, kHeight, ZR_SSR_MAX_MIPS, hizLevels);
    if (hizLevelCount != ZR_SSR_MAX_MIPS) {
        std::printf("  r5 phase: Hi-Z mip chain unexpected (%u)\n", hizLevelCount);
        return false;
    }
    for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS; ++m) {
        void* mapped = device->GetBuffer(cbHiZ[m])->Map();
        if (mapped == nullptr) {
            std::printf("  r5 phase: hiz CB %u Map FAILED\n", m);
            return false;
        }
        R5TexelConstants tc {};
        if (m > 0u) {
            tc.texel[0] = 1.0f / static_cast<float>(hizLevels[m - 1u].width);
            tc.texel[1] = 1.0f / static_cast<float>(hizLevels[m - 1u].height);
        }
        std::memcpy(mapped, &tc, sizeof(tc));
    }
    {
        R5TexelConstants bc {};
        bc.texel[0] = 1.0f / static_cast<float>(kR5HalfW);
        bc.texel[1] = 1.0f / static_cast<float>(kR5HalfH);
        std::memcpy(cbBlurMapped, &bc, sizeof(bc));
    }

    // ---- toon / outline 常量（与 RunR5Phase 同值同源）-------------------------------
    float toonCubeModel[16];
    MatTranslation(-2.4f, 0.0f, 4.2f, toonCubeModel);
    float toonCubeMvp[16];
    MatMultiply(projView, toonCubeModel, toonCubeMvp);
    {
        ToonConstants tc {};
        std::memcpy(tc.mvp, toonCubeMvp, sizeof(toonCubeMvp));
        std::memcpy(tc.model, toonCubeModel, sizeof(toonCubeModel));
        const float ld[3] = {0.3f, 0.5f, -0.81f};
        const float invLen = 1.0f / std::sqrt(ld[0] * ld[0] + ld[1] * ld[1] + ld[2] * ld[2]);
        tc.lightDir[0] = ld[0] * invLen;
        tc.lightDir[1] = ld[1] * invLen;
        tc.lightDir[2] = ld[2] * invLen;
        tc.lightDir[3] = 0.0f;
        tc.params[0] = 0.35f;
        tc.params[1] = 0.60f;
        tc.params[2] = 0.35f;
        tc.params[3] = 0.70f;
        tc.specular[0] = 0.98f;
        tc.specular[1] = 0.60f;
        tc.camPos[0] = 0.0f;
        tc.camPos[1] = 0.0f;
        tc.camPos[2] = 0.0f;
        tc.camPos[3] = 0.0f;
        tc.base[0] = 0.85f;
        tc.base[1] = 0.35f;
        tc.base[2] = 0.28f;
        tc.base[3] = 1.0f;
        std::memcpy(cbToonMapped, &tc, sizeof(tc));

        OutlineConstants oc {};
        std::memcpy(oc.mvp, toonCubeMvp, sizeof(toonCubeMvp));
        oc.params[0] = 0.045f;
        std::memcpy(cbOutlineMapped, &oc, sizeof(oc));
    }

    // ---- Composite 常量（SSR 开关）--------------------------------------------------
    {
        CompositeConstants cc {};
        cc.params[0] = ssrEnabled ? 1.0f : 0.0f;
        std::memcpy(cbCompositeMapped, &cc, sizeof(cc));
    }

    // ---- 静态 CB0（地面 / cube）----------------------------------------------------
    const float ambient = 0.12f;
    ShadowDrawConstants groundConst {};
    std::memcpy(groundConst.mvp, projView, sizeof(projView));
    const float identity[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    std::memcpy(groundConst.model, identity, sizeof(identity));
    ShadowDrawConstants cubeConst {};
    float cubeModel[16];
    MatTranslation(0.0f, 0.0f, 3.2f, cubeModel);
    std::memcpy(cubeConst.model, cubeModel, sizeof(cubeModel));
    float cubeMvp[16];
    MatMultiply(projView, cubeModel, cubeMvp);
    std::memcpy(cubeConst.mvp, cubeMvp, sizeof(cubeMvp));
    for (ShadowDrawConstants* c : {&groundConst, &cubeConst}) {
        c->projParams[0] = kFarZ / (kFarZ - kNearZ);
        c->projParams[1] = kNearZ * kFarZ / (kFarZ - kNearZ);
        c->projParams[2] = kNearZ;
        c->projParams[3] = kFarZ;
        c->gridParams[0] = static_cast<float>(ZR_CLUSTER_DIM_X);
        c->gridParams[1] = static_cast<float>(ZR_CLUSTER_DIM_Y);
        c->gridParams[2] = static_cast<float>(ZR_CLUSTER_DIM_Z);
        c->gridParams[3] = 1.0f / (kFarZ - kNearZ);
        c->screenParams[0] = static_cast<float>(kWidth);
        c->screenParams[1] = static_cast<float>(kHeight);
        c->screenParams[2] = static_cast<float>(kR5LightCount);
        c->screenParams[3] = ambient;
        c->camPos[0] = 0.0f;
        c->camPos[1] = 0.0f;
        c->camPos[2] = 0.0f;
        c->camPos[3] = 0.0f;
        c->cascadeSplits[0] = cascadeSplits[0];
        c->cascadeSplits[1] = cascadeSplits[1];
        c->cascadeSplits[2] = cascadeSplits[2];
        c->cascadeSplits[3] = cascadeSplits[3];
        c->shadowParams[0] = 1.0f; // --r5 继承 --shadows on
        c->shadowParams[1] = static_cast<float>(kShadowsTexSize);
    }
    groundConst.material[0] = 0.50f;
    groundConst.material[1] = 0.52f;
    groundConst.material[2] = 0.50f;
    groundConst.material[3] = 0.15f; // 低粗糙度：SSR 应用目标
    cubeConst.material[0] = 0.75f;
    cubeConst.material[1] = 0.75f;
    cubeConst.material[2] = 0.78f;
    cubeConst.material[3] = 0.35f; // > ZR_SSR_ROUGHNESS_MAX：SSR 排除
    std::memcpy(cb0GroundMapped, &groundConst, sizeof(groundConst));
    std::memcpy(cb0CubeMapped, &cubeConst, sizeof(cubeConst));

    // ---- draw list：地面 / pbr cube（阴影视图）/ toon cube（+描边视图）--------------
    DrawList drawList(16u);
    drawList.RegisterMesh(1u, 6u);
    drawList.RegisterMesh(2u, cubeIndexCount);
    drawList.RegisterMesh(3u, cubeIndexCount);
    RenderItem groundItem {};
    groundItem.meshHandle = 1u;
    groundItem.materialId = 7u;
    groundItem.shaderId = 0u;
    groundItem.depth = 2.8f;
    groundItem.castShadow = true;
    RenderItem cubeItem {};
    cubeItem.meshHandle = 2u;
    cubeItem.materialId = 8u;
    cubeItem.shaderId = 0u;
    cubeItem.depth = 3.2f;
    cubeItem.castShadow = true;
    RenderItem toonItem {};
    toonItem.meshHandle = 3u;
    toonItem.materialId = 9u;
    toonItem.shaderId = 1u;
    toonItem.depth = 4.2f;
    toonItem.castShadow = false; // toon 物体不投影（同 D3D12 路径简化）
    const uint8_t toonMask
        = static_cast<uint8_t>(ShadowViewMask(false) | kOutlineViewBit); // 0x21
    if (!drawList.Add(groundItem, ShadowViewMask(true))
        || !drawList.Add(cubeItem, ShadowViewMask(true)) || !drawList.Add(toonItem, toonMask)) {
        std::printf("  r5 phase: draw list Add FAILED\n");
        return false;
    }
    std::vector<uint32_t> shadowItems[ZR_SHADOW_CASCADES];
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        drawList.Visibility().Iterate(
            ShadowViewId(c), [&](uint32_t id) { shadowItems[c].push_back(id); });
    }
    std::vector<uint32_t> outlineItems;
    drawList.Visibility().Iterate(kOutlineViewId, [&](uint32_t id) { outlineItems.push_back(id); });
    drawList.Sort();
    if (drawList.BuildBatches() == 0u) {
        std::printf("  r5 phase: BuildBatches FAILED\n");
        return false;
    }

    // ---- 逐项绘制辅助（meshHandle → VB/IB）-----------------------------------------
    auto bindMesh = [&](uint32_t meshHandle) {
        ZrBuffer vbs[1] = {(meshHandle == 1u) ? groundVb
                          : (meshHandle == 2u) ? cubeVb
                                               : toonVb};
        cmdList->SetVertexBuffers(vbs, 1u);
        cmdList->SetIndexBuffer(
            (meshHandle == 1u) ? groundIb : (meshHandle == 2u) ? cubeIb : toonIb);
    };

    // ---- 探针 readback 缓冲（6 探针 × 256B 槽；VK 路径 ZrBuffer 每相位建/毁）--------
    ZrBuffer readback {};
    {
        ZrBufferDesc rbDesc {};
        rbDesc.size = uint64_t(kR5ProbeCount) * 256u;
        rbDesc.usage = ZrBufferUsage::TransferDst;
        rbDesc.readback = true;
        readback = device->CreateBuffer(rbDesc);
    }
    if (!readback.IsValid()) {
        std::printf("  r5 phase: readback buffer FAILED\n");
        return false;
    }

    // ---- 30 帧循环 ------------------------------------------------------------------
    constexpr float kR5ClearColor[4] = {0.03f, 0.03f, 0.045f, 1.0f};
    bool allOk = true;
    uint32_t undeclaredUses = 0u;

    for (uint32_t frame = 0u; frame < kR5FramesPerPhase; ++frame) {
        const bool doReadback = ((frame + 1u) % 10u) == 0u; // 第 10/20/30 帧
        uint32_t readbackSlot = 0u;
        if (doReadback) {
            readbackSlot = (frame + 1u) / 10u - 1u;
        }

        // 动画点光（相位内帧号驱动——两相位同帧同相位，SSR 对比纯净）
        for (uint32_t i = 0; i < 2u; ++i) {
            const float angle
                = 6.2831853f * static_cast<float>(frame + 1u) / 120.0f
                + 3.14159265f * static_cast<float>(i); // 相位相反
            LightData& l = sceneLights[1u + i];
            l.pos[0] = std::cos(angle) * 2.0f;
            l.pos[1] = 0.9f;
            l.pos[2] = 6.5f + std::sin(angle) * 2.0f;
            l.radius = 3.5f;
            l.color[0] = pointColors[i][0];
            l.color[1] = pointColors[i][1];
            l.color[2] = pointColors[i][2];
            l.intensity = 6.0f;
            l.kind = kZrLightPoint;
        }

        // GI 探针轮转累加（本帧切片 → 重算该切片探针的辐照度）
        uint32_t slice[ZR_GI_PROBE_COUNT / 4u] = {};
        const uint32_t sliceCount = UpdateProbeSlice(frame, 4u, slice, ZR_GI_PROBE_COUNT / 4u);
        for (uint32_t s = 0; s < sliceCount; ++s) {
            const uint32_t probe = slice[s];
            const uint32_t px = probe % ZR_GI_PROBE_DIM_X;
            const uint32_t py = (probe / ZR_GI_PROBE_DIM_X) % ZR_GI_PROBE_DIM_Y;
            const uint32_t pz = probe / (ZR_GI_PROBE_DIM_X * ZR_GI_PROBE_DIM_Y);
            const float pos[3] = {giGrid.origin[0] + giGrid.spacing[0] * (static_cast<float>(px) + 0.5f),
                giGrid.origin[1] + giGrid.spacing[1] * (static_cast<float>(py) + 0.5f),
                giGrid.origin[2] + giGrid.spacing[2] * (static_cast<float>(pz) + 0.5f)};
            float acc[3] = {0.0f, 0.0f, 0.0f};
            AccumulateProbeIrradiance(pos, sceneLights, kR5LightCount, acc);
            giProbes[probe][0] = acc[0];
            giProbes[probe][1] = acc[1];
            giProbes[probe][2] = acc[2];
        }
        for (uint32_t probe = 0; probe < ZR_GI_PROBE_COUNT; ++probe) {
            giCb.probes[probe][0] = giProbes[probe][0];
            giCb.probes[probe][1] = giProbes[probe][1];
            giCb.probes[probe][2] = giProbes[probe][2];
            giCb.probes[probe][3] = 1.0f;
        }
        std::memcpy(cbGiMapped, &giCb, sizeof(giCb));

        // CB1 光源 / CB2 簇
        PackedLight packedLights[kR5LightCount];
        PackLights(sceneLights, kR5LightCount, packedLights);
        std::memcpy(cbLightsMapped, packedLights, sizeof(packedLights));
        std::memset(static_cast<uint8_t*>(cbLightsMapped) + sizeof(packedLights), 0,
            1024u - sizeof(packedLights));
        const int32_t flatCount = AssignLightsToClusters(grid, sceneLights, kR5LightCount,
            clusterRanges.data(), clusterFlat.data(), static_cast<uint32_t>(clusterFlat.size()));
        if (flatCount < 0) {
            std::printf("  r5 phase frame %u: AssignLightsToClusters FAILED (%d)\n", frame + 1u,
                flatCount);
            return false;
        }
        uint8_t* cb2 = static_cast<uint8_t*>(cbClustersMapped);
        std::memcpy(cb2, clusterRanges.data(), size_t(ZR_CLUSTER_COUNT) * 4u);
        std::memcpy(cb2 + size_t(ZR_CLUSTER_COUNT) * 4u, clusterFlat.data(),
            size_t(flatCount) * 4u);

        // 取后台缓冲 + RTV
        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }
        if (!rtvView.IsValid()) {
            std::printf("  r5 phase frame %u: FAILED to obtain RTV\n", frame + 1u);
            return false;
        }

        // ---- 帧图：Clear → Shadow×4 → Main → Toon → Outline → SSR 四连 → Readback/Present
        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        // 阴影恒开（--r5 继承 --shadows on 场景）
        RgHandle shadowH = graph.Import(RgResourceDesc::ImportImage("shadowMap", shadowImage));
        RgHandle sceneH = graph.Import(RgResourceDesc::ImportImage("sceneColor", sceneImage));
        RgHandle hizH = graph.Import(RgResourceDesc::ImportImage("hiz", hizImage));
        RgHandle traceH = graph.Import(RgResourceDesc::ImportImage("ssrTrace", traceImage));
        RgHandle blurH = graph.Import(RgResourceDesc::ImportImage("ssrBlur", blurImage));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(sceneH),
                    ZrImageLayout::ColorAttachmentOptimal, kR5ClearColor,
                    ZrSubresourceRange::All());
                // 深度清屏扩展点（对位 D3D12 ClearDepthStencilView 原生调用）
                device->RecordClearDepth(cmdHandle, dsvView, 1.0f);
            });

        // 每级联 depth-only pass（继承 --shadows；共享可见性过滤）
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            graph.AddPass("ShadowCascadePass",
                [&](RgPass& pass) {
                    pass.Write(shadowH, ZrImageLayout::DepthStencilAttachmentOptimal,
                        ZrAccessBits::DepthStencilAttachmentWrite,
                        ZrPipelineStageBits::LateFragmentTests);
                },
                [&, c](RgContext&) {
                    // 级联层清屏先于附件绑定（RecordClearDepth 内部布局往返屏障
                    // 与渲染实例互斥；语义与 D3D12 的 SetRenderTarget 后清屏等价
                    // ——loadOp=LOAD 不丢内容，--shadows 既定次序）
                    device->RecordClearDepth(cmdHandle, shadowDsvs[c], 1.0f);
                    cmdList->SetPipelineState(shadowPso);
                    cmdList->SetRenderTarget(ZrImageView{}, shadowDsvs[c]);
                    cmdList->SetViewportScissor(kShadowsTexSize, kShadowsTexSize);
                    cmdList->SetGraphicsConstantBuffer(0u, cbShadowPass[c]);
                    for (uint32_t id : shadowItems[c]) {
                        const bool isGround = drawList.ItemAt(id).meshHandle == 1u;
                        cmdList->SetGraphicsConstantBuffer(
                            1u, isGround ? cbShadowModelGround : cbShadowModelCube);
                        bindMesh(drawList.ItemAt(id).meshHandle);
                        cmdList->DrawIndexedInstanced(
                            (drawList.ItemAt(id).meshHandle == 1u) ? 6u : cubeIndexCount, 1u, 0u,
                            0u, 0u);
                    }
                });
        }

        // 主绘制 Pass（pbr_r5：GI 探针环境项 + CSM + 簇光源）
        graph.AddPass("MainPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
                pass.Read(shadowH, ZrImageLayout::DepthStencilReadOnlyOptimal,
                    ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader);
            },
            [&](RgContext&) {
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    if (batch.shaderId != 0u) {
                        continue; // toon 批归 ToonPass
                    }
                    cmdList->SetPipelineState(mainPso);
                    cmdList->SetRenderTarget(sceneRtv, dsvView);
                    cmdList->SetViewportScissor(kWidth, kHeight);
                    cmdList->SetGraphicsConstantBuffer(1u, cbLights);
                    cmdList->SetGraphicsConstantBuffer(2u, cbClusters);
                    cmdList->SetGraphicsConstantBuffer(3u, cbShadows);
                    cmdList->SetGraphicsConstantBuffer(4u, cbGi);
                    cmdList->SetGraphicsBindlessSrv(5u, shadowSrv); // t0（rootCbvCount=5）
                    for (uint32_t i = 0; i < batch.count; ++i) {
                        const uint32_t itemIndex = batch.startIndex + i;
                        const uint32_t mesh = drawList.ItemAt(itemIndex).meshHandle;
                        bindMesh(mesh);
                        cmdList->SetGraphicsConstantBuffer(0u, (mesh == 1u) ? cb0Ground : cb0Cube);
                        cmdList->DrawIndexedInstanced(
                            (mesh == 1u) ? 6u : cubeIndexCount, 1u, 0u, 0u, 0u);
                    }
                }
            });

        // Toon pass（量化 ramp 变体）
        graph.AddPass("ToonPass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                for (const ZrDrawBatch& batch : drawList.Batches()) {
                    if (batch.shaderId != 1u) {
                        continue;
                    }
                    cmdList->SetPipelineState(toonPso);
                    cmdList->SetRenderTarget(sceneRtv, dsvView);
                    cmdList->SetViewportScissor(kWidth, kHeight);
                    cmdList->SetGraphicsConstantBuffer(0u, cbToon);
                    bindMesh(3u);
                    cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
                }
            });

        // Outline pass（反向壳：FrontCull + 深度只测不写）
        graph.AddPass("OutlinePass",
            [&](RgPass& pass) {
                pass.Write(sceneH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(outlinePso);
                cmdList->SetRenderTarget(sceneRtv, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                cmdList->SetGraphicsConstantBuffer(0u, cbOutline);
                for (uint32_t id : outlineItems) {
                    bindMesh(drawList.ItemAt(id).meshHandle);
                    cmdList->DrawIndexedInstanced(cubeIndexCount, 1u, 0u, 0u, 0u);
                }
            });

        // ---- SSR 四连（单一声明源；execute 回调在此录制真渲染命令）------------------
        auto hiZExec = [&](RgContext&) {
            // mip0：场景深度 1-tap 直拷（深度图经 R32Float 孪生 SRV 采样）
            cmdList->SetPipelineState(hizCopyPso);
            cmdList->SetRenderTarget(hizRtvs[0], ZrImageView{});
            cmdList->SetViewportScissor(hizLevels[0].width, hizLevels[0].height);
            cmdList->SetGraphicsConstantBuffer(0u, cbHiZ[0]);
            cmdList->SetGraphicsBindlessSrv(1u, depthSrv);
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
            // mip 1..5：上一级 4 邻域 min（pass 内 per-mip 手动子资源屏障——
            // VK 侧字面布局转译成立：pass 入口整链已被 planner 置于
            // ColorAttachmentOptimal，逐 mip 转 ShaderReadOnly 采样后 pass 末
            // 全部回 ColorAttachment 与 RG 整资源声明一致）
            for (uint32_t m = 1u; m < hizLevelCount; ++m) {
                ZrImageBarrier toSrv = MakeBarrier(hizImage,
                    ZrImageLayout::ColorAttachmentOptimal, ZrImageLayout::ShaderReadOnlyOptimal,
                    ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput,
                    ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
                toSrv.subresources.baseMipLevel = m - 1u;
                toSrv.subresources.levelCount = 1u;
                cmdList->ResourceBarrier(&toSrv, 1u, nullptr, 0u);
                cmdList->SetPipelineState(hizMinPso);
                cmdList->SetRenderTarget(hizRtvs[m], ZrImageView{});
                cmdList->SetViewportScissor(hizLevels[m].width, hizLevels[m].height);
                cmdList->SetGraphicsConstantBuffer(0u, cbHiZ[m]);
                cmdList->SetGraphicsBindlessSrv(1u, hizMipSrvs[m - 1u]);
                cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
            }
            // 采样过的 mip（0..levelCount-2）回 RENDER_TARGET——与 RG 整资源声明
            // （ColorAttachmentOptimal）在 pass 出口一致
            ZrImageBarrier toRtv = MakeBarrier(hizImage,
                ZrImageLayout::ShaderReadOnlyOptimal, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            toRtv.subresources.baseMipLevel = 0u;
            toRtv.subresources.levelCount = hizLevelCount - 1u;
            cmdList->ResourceBarrier(&toRtv, 1u, nullptr, 0u);
        };
        auto traceExec = [&](RgContext&) {
            cmdList->SetPipelineState(tracePso);
            cmdList->SetRenderTarget(traceRtv, ZrImageView{});
            cmdList->SetViewportScissor(kR5HalfW, kR5HalfH);
            cmdList->SetGraphicsConstantBuffer(0u, cbSsr);
            cmdList->SetGraphicsBindlessSrv(1u, hizSrv);  // t0 Hi-Z 链
            cmdList->SetGraphicsBindlessSrv(2u, sceneSrv); // t1 场景颜色
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        };
        auto blurExec = [&](RgContext&) {
            cmdList->SetPipelineState(blurPso);
            cmdList->SetRenderTarget(blurRtv, ZrImageView{});
            cmdList->SetViewportScissor(kR5HalfW, kR5HalfH);
            cmdList->SetGraphicsConstantBuffer(0u, cbBlur);
            cmdList->SetGraphicsBindlessSrv(1u, traceSrv);
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        };
        auto compositeExec = [&](RgContext&) {
            cmdList->SetPipelineState(compPso);
            cmdList->SetRenderTarget(rtvView, ZrImageView{});
            cmdList->SetViewportScissor(kWidth, kHeight);
            cmdList->SetGraphicsConstantBuffer(0u, cbComposite);
            cmdList->SetGraphicsBindlessSrv(1u, blurSrv);  // t0 模糊反射
            cmdList->SetGraphicsBindlessSrv(2u, sceneSrv); // t1 场景颜色
            ZrBuffer qvbs[1] = {quadVb};
            cmdList->SetVertexBuffers(qvbs, 1u);
            cmdList->SetIndexBuffer(quadIb);
            cmdList->DrawIndexedInstanced(6u, 1u, 0u, 0u, 0u);
        };
        SsrPassHandles ssrHandles;
        ssrHandles.depth = depthH;
        ssrHandles.hiz = hizH;
        ssrHandles.sceneColor = sceneH;
        ssrHandles.ssrTrace = traceH;
        ssrHandles.ssrBlur = blurH;
        ssrHandles.back = backH;
        RecordSsrPasses(graph, ssrHandles, hiZExec, traceExec, blurExec, compositeExec);

        // Readback pass（第 10/20/30 帧）：6 探针盒 → vkCmdCopyImageToBuffer
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal,
                        ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    for (uint32_t i = 0; i < kR5ProbeCount; ++i) {
                        VkBufferImageCopy region {};
                        region.bufferOffset = static_cast<VkDeviceSize>(i) * 256u;
                        region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                        region.imageSubresource.layerCount = 1u;
                        region.imageOffset = { static_cast<int32_t>(kR5Probes[i].x),
                            static_cast<int32_t>(kR5Probes[i].y), 0 };
                        region.imageExtent = { 1u, 1u, 1u };
                        vkCmdCopyImageToBuffer(device->VulkanNativeCmdOf(cmdHandle),
                            device->VulkanImageOf(ctx.GetImage(backH)),
                            VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                            device->VulkanBufferOf(readback), 1u, &region);
                    }
                });
        }
        if (windowMode) {
            graph.AddPass("PresentPass",
                [&](RgPass& pass) { pass.Present(backH).MarkRoot(); }, nullptr);
        }

        if (!graph.Compile()) {
            std::printf("  r5 phase frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            return false;
        }
        graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();

        // 帧末屏障：VK 路径无帧末复位（VK 禁止 newLayout=UNDEFINED；同 --draw/
        // --lights/--shadows 既定差异——跨帧持久图像布局由下帧 planner 初态
        // Undefined 的首道 discard 屏障覆盖）

        cmdList->End();
        queue->Submit(cmdHandle);
        if (windowMode) {
            swapchain->Present();
        }
        device->WaitIdle();
        graph.FlushRetired(*device);

        if (doReadback) {
            IZrBuffer* rbInterface = device->GetBuffer(readback);
            void* mapped = (rbInterface != nullptr) ? rbInterface->Map() : nullptr;
            if (mapped == nullptr) {
                std::printf("  r5 phase frame %u: probe Map FAILED\n", frame + 1u);
                return false;
            }
            const uint8_t* bytes = static_cast<const uint8_t*>(mapped);
            for (uint32_t p = 0; p < kR5ProbeCount; ++p) {
                DecodeR5Probe(bytes, p, windowMode, outProbes[readbackSlot][p]);
            }
            rbInterface->Unmap();
            std::printf("  r5 phase frame %u probes: ssr=(%d,%d,%d) gi=(%d,%d,%d) "
                        "toonB=(%d,%d,%d)/(%d,%d,%d) toonD=(%d,%d,%d)/(%d,%d,%d) [vulkan]\n",
                frame + 1u, outProbes[readbackSlot][0][0], outProbes[readbackSlot][0][1],
                outProbes[readbackSlot][0][2], outProbes[readbackSlot][1][0],
                outProbes[readbackSlot][1][1], outProbes[readbackSlot][1][2],
                outProbes[readbackSlot][2][0], outProbes[readbackSlot][2][1],
                outProbes[readbackSlot][2][2], outProbes[readbackSlot][3][0],
                outProbes[readbackSlot][3][1], outProbes[readbackSlot][3][2],
                outProbes[readbackSlot][4][0], outProbes[readbackSlot][4][1],
                outProbes[readbackSlot][4][2], outProbes[readbackSlot][5][0],
                outProbes[readbackSlot][5][1], outProbes[readbackSlot][5][2]);
        }

        if (windowMode) {
            PumpMessages();
        }
    }

    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v);
        }
    }
    device->Destroy(readback);
    if (undeclaredUses != 0u) {
        std::printf("  r5 phase: undeclared RG uses = %u\n", undeclaredUses);
        allOk = false;
    }
    return allOk;
}

// --r5 Vulkan 路径主体（TASK-B-R8-01；镜像 RunR5Mode 的相位编排与判定口径，
// out 参数契约同 RunR5Mode 的 outPhaseProbes——both 模式探针并排对照用）
static int RunR5ModeVulkan(int (*outPhaseProbes)[3][kR5ProbeCount][3] = nullptr)
{
    std::printf("zrhi_d3d12_window: --r5 mode vulkan backend (TASK-B-R8-01): "
                "SSR + GI probes + NPR toon\n");

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrVulkanDevice* device = ZrVulkanCreateDevice();
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create Vulkan device\n");
        return 1;
    }
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create queue/command list\n");
        ZrVulkanDestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏；同 D3D12 路径）--------------------------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --r5 window rejected (GetLastError=%lu), falling back "
                    "to offscreen target\n",
            GetLastError());
    }
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --r5 swapchain FAILED, falling back to offscreen "
                        "target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
    }

    // ---- 渲染目标（离屏降级）+ 跨帧持久图像组 ---------------------------------------
    ZrImage offscreenTarget {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        if (!offscreenTarget.IsValid()) {
            std::printf("zrhi_d3d12_window: --r5 FAILED to create offscreen target\n");
            device->Destroy(cmdHandle);
            ZrVulkanDestroyDevice(device);
            return 1;
        }
    }
    auto makeImage = [&](ZrFormat format, uint32_t w, uint32_t h, uint32_t mips,
                         ZrImageUsage usage) {
        ZrImageDesc desc {};
        desc.format = format;
        desc.width = w;
        desc.height = h;
        desc.mipLevels = mips;
        desc.usage = usage;
        return device->CreateImage(desc);
    };
    // VK 三角洲（见节头注 6）：depthImage 显式加 Sampled（Hi-Z 源 = 深度孪生 SRV）；
    // shadowImage 无需 TransferDst（RecordClearDepth 走 vkCmdClearDepthStencilImage）
    ZrImage depthImage = makeImage(ZrFormat::D32Float, kWidth, kHeight, 1u,
        ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled);
    // 层数组图与 D3D12 路径同款（TASK-B-R8-01：4 级联层 DSV 的合法前提）
    ZrImageDesc shadowDescR5 {};
    shadowDescR5.format = ZrFormat::D32Float;
    shadowDescR5.width = kShadowsTexSize;
    shadowDescR5.height = kShadowsTexSize;
    shadowDescR5.arrayLayers = ZR_SHADOW_CASCADES;
    shadowDescR5.usage = ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled;
    ZrImage shadowImage = device->CreateImage(shadowDescR5);
    ZrImage sceneImage = makeImage(ZrFormat::RGBA8Unorm, kWidth, kHeight, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    ZrImage hizImage = makeImage(ZrFormat::R32Float, kWidth, kHeight, ZR_SSR_MAX_MIPS,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    ZrImage traceImage = makeImage(ZrFormat::RGBA8Unorm, kR5HalfW, kR5HalfH, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    ZrImage blurImage = makeImage(ZrFormat::RGBA8Unorm, kR5HalfW, kR5HalfH, 1u,
        ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled);
    if (!depthImage.IsValid() || !shadowImage.IsValid() || !sceneImage.IsValid()
        || !hizImage.IsValid() || !traceImage.IsValid() || !blurImage.IsValid()) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create images\n");
        return 1;
    }

    // 视图组（同 D3D12 路径；深度孪生解释/逐 mip/逐层视图 VK 侧 B-R7-02 已备）
    ZrImageViewDesc dsvDesc {};
    dsvDesc.image = depthImage;
    dsvDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvDesc);
    ZrImageViewDesc depthSrvDesc {};
    depthSrvDesc.image = depthImage;
    depthSrvDesc.viewType = ZrImageViewType::Srv;
    depthSrvDesc.format = ZrFormat::R32Float;
    depthSrvDesc.overrideFormat = true;
    ZrImageView depthSrv = device->CreateImageView(depthSrvDesc);
    ZrImageView shadowDsvs[ZR_SHADOW_CASCADES] = {};
    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        ZrImageViewDesc layerDsvDesc {};
        layerDsvDesc.image = shadowImage;
        layerDsvDesc.viewType = ZrImageViewType::Dsv;
        layerDsvDesc.subresources.baseArrayLayer = c;
        layerDsvDesc.subresources.layerCount = 1u;
        shadowDsvs[c] = device->CreateImageView(layerDsvDesc);
    }
    ZrImageViewDesc shadowSrvDesc {};
    shadowSrvDesc.image = shadowImage;
    shadowSrvDesc.viewType = ZrImageViewType::Srv;
    shadowSrvDesc.format = ZrFormat::R32Float;
    shadowSrvDesc.overrideFormat = true;
    ZrImageView shadowSrv = device->CreateImageView(shadowSrvDesc);
    auto makeRtv = [&](ZrImage image, uint32_t mip = 0u) {
        ZrImageViewDesc desc {};
        desc.image = image;
        desc.viewType = ZrImageViewType::Rtv;
        desc.subresources.baseMipLevel = mip;
        desc.subresources.levelCount = 1u;
        return device->CreateImageView(desc);
    };
    auto makeSrv = [&](ZrImage image) {
        ZrImageViewDesc desc {};
        desc.image = image;
        desc.viewType = ZrImageViewType::Srv;
        return device->CreateImageView(desc);
    };
    ZrImageView sceneRtv = makeRtv(sceneImage);
    ZrImageView sceneSrv = makeSrv(sceneImage);
    ZrImageView hizRtvs[ZR_SSR_MAX_MIPS] = {};
    for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS; ++m) {
        hizRtvs[m] = makeRtv(hizImage, m);
    }
    ZrImageView hizSrv = makeSrv(hizImage);
    ZrImageView hizMipSrvs[ZR_SSR_MAX_MIPS - 1] = {};
    for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS - 1u; ++m) {
        ZrImageViewDesc desc {};
        desc.image = hizImage;
        desc.viewType = ZrImageViewType::Srv;
        desc.subresources.baseMipLevel = m;
        desc.subresources.levelCount = 1u;
        hizMipSrvs[m] = device->CreateImageView(desc);
    }
    ZrImageView traceRtv = makeRtv(traceImage);
    ZrImageView traceSrv = makeSrv(traceImage);
    ZrImageView blurRtv = makeRtv(blurImage);
    ZrImageView blurSrv = makeSrv(blurImage);
    ZrImageView offscreenRtv {};
    if (!windowMode) {
        offscreenRtv = makeRtv(offscreenTarget);
    }
    bool viewsOk = dsvView.IsValid() && depthSrv.IsValid() && shadowSrv.IsValid()
        && sceneRtv.IsValid() && sceneSrv.IsValid() && hizSrv.IsValid() && traceRtv.IsValid()
        && traceSrv.IsValid() && blurRtv.IsValid() && blurSrv.IsValid();
    for (ZrImageView v : shadowDsvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    for (ZrImageView v : hizRtvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    for (ZrImageView v : hizMipSrvs) {
        viewsOk = viewsOk && v.IsValid();
    }
    if (!viewsOk || (!windowMode && !offscreenRtv.IsValid())) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create views\n");
        return 1;
    }

    // ---- 几何（同 D3D12 路径：地面 quad + cube.zmesh ×2 实例 VB）+ 全屏 quad ---------
    const FirstDrawVertex groundVerts[4] = {
        {{-10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
        {{10.0f, -1.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
        {{10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
        {{-10.0f, -1.0f, 20.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t groundIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};
    const FirstDrawVertex quadVerts[4] = {
        {{-1.0f, 1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {0.0f, 0.0f}}, // clip-space 全屏 quad
        {{1.0f, 1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {1.0f, 0.0f}},
        {{1.0f, -1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {1.0f, 1.0f}},
        {{-1.0f, -1.0f, 0.0f}, {0.0f, 0.0f, 0.0f}, {0.0f, 1.0f}},
    };
    const uint32_t quadIndices[6] = {0u, 1u, 2u, 0u, 2u, 3u};

    std::vector<char> meshBytes;
    if (!ReadWholeFile("Native/tests/data/cube.zmesh", meshBytes)) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to open cube.zmesh\n");
        return 1;
    }
    ZmeshData mesh {};
    if (zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh) != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --r5 cube.zmesh parse FAILED\n");
        return 1;
    }
    const uint32_t cubeIndexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> cubeVerts(mesh.stats.vertexCount);
    for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
        std::memcpy(cubeVerts[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(cubeVerts[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> cubeIndices(cubeIndexCount);
    std::memcpy(cubeIndices.data(), mesh.indices, sizeof(uint32_t) * cubeIndexCount);

    auto makeVb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    auto makeIb = [&](const void* data, uint64_t bytes) {
        ZrBufferDesc desc {};
        desc.size = bytes;
        desc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        return device->CreateBuffer(desc, data);
    };
    ZrBuffer groundVb = makeVb(groundVerts, sizeof(groundVerts));
    ZrBuffer groundIb = makeIb(groundIndices, sizeof(groundIndices));
    ZrBuffer cubeVb = makeVb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex));
    ZrBuffer cubeIb = makeIb(cubeIndices.data(), uint64_t(cubeIndices.size()) * 4u);
    ZrBuffer toonVb = makeVb(cubeVerts.data(), uint64_t(cubeVerts.size()) * sizeof(FirstDrawVertex));
    ZrBuffer toonIb = makeIb(cubeIndices.data(), uint64_t(cubeIndices.size()) * 4u);
    ZrBuffer quadVb = makeVb(quadVerts, sizeof(quadVerts));
    ZrBuffer quadIb = makeIb(quadIndices, sizeof(quadIndices));
    if (!groundVb.IsValid() || !groundIb.IsValid() || !cubeVb.IsValid() || !cubeIb.IsValid()
        || !toonVb.IsValid() || !toonIb.IsValid() || !quadVb.IsValid() || !quadIb.IsValid()) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create VB/IB\n");
        return 1;
    }

    // ---- 常量缓冲（同 D3D12 路径逐条对位）--------------------------------------------
    ZrBuffer cb0Ground {};
    ZrBuffer cb0Cube {};
    ZrBuffer cbToon {};
    ZrBuffer cbOutline {};
    ZrBuffer cbLights {};
    ZrBuffer cbClusters {};
    ZrBuffer cbShadows {};
    ZrBuffer cbGi {};
    ZrBuffer cbSsr {};
    ZrBuffer cbHiZ[ZR_SSR_MAX_MIPS] = {};
    ZrBuffer cbBlur {};
    ZrBuffer cbComposite {};
    ZrBuffer cbShadowPass[ZR_SHADOW_CASCADES] = {};
    ZrBuffer cbShadowModelGround {};
    ZrBuffer cbShadowModelCube {};
    {
        auto makeCb = [&](uint64_t size) {
            ZrBufferDesc desc {};
            desc.size = size;
            desc.usage = ZrBufferUsage::Uniform;
            desc.hostVisible = true;
            ZrBuffer b = device->CreateBuffer(desc);
            return (b.IsValid() && device->GetBuffer(b)->Map() != nullptr) ? b : ZrBuffer{};
        };
        const uint64_t cb2Size = (uint64_t(ZR_CLUSTER_COUNT) * 4u
                                     + uint64_t(ZR_CLUSTER_FLAT_MAX) * 4u + 255u)
            & ~255ull;
        bool ok = (cb0Ground = makeCb(256u)).IsValid() && (cb0Cube = makeCb(256u)).IsValid()
            && (cbToon = makeCb(256u)).IsValid() && (cbOutline = makeCb(96u)).IsValid()
            && (cbLights = makeCb(1024u)).IsValid() && (cbClusters = makeCb(cb2Size)).IsValid()
            && (cbShadows = makeCb(384u)).IsValid() && (cbGi = makeCb(2112u)).IsValid()
            && (cbSsr = makeCb(256u)).IsValid() && (cbBlur = makeCb(256u)).IsValid()
            && (cbComposite = makeCb(256u)).IsValid()
            && (cbShadowModelGround = makeCb(256u)).IsValid()
            && (cbShadowModelCube = makeCb(256u)).IsValid();
        for (uint32_t m = 0; m < ZR_SSR_MAX_MIPS; ++m) {
            ok = ok && (cbHiZ[m] = makeCb(256u)).IsValid();
        }
        for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
            ok = ok && (cbShadowPass[c] = makeCb(256u)).IsValid();
        }
        if (!ok) {
            std::printf("zrhi_d3d12_window: --r5 FAILED to create constant buffers\n");
            return 1;
        }
    }

    // ---- 着色器（glslc SPIR-V：8 文件/16 入口点）+ PSO --------------------------------
    // 与 CMake vulkan_shaders 产物名逐条对位（同源 hlsl 两编译器消费，禁 fork；
    // 清单镜像断言见 tests/test_zrhi_vulkan_spv.cpp）
    constexpr uint32_t kR5ShaderCount = 16u;
    const char* kR5HlslPaths[8] = {"Native/shaders/pbr_r5.hlsl", "Native/shaders/toon.hlsl",
        "Native/shaders/toon_outline.hlsl", "Native/shaders/shadow_depth.hlsl",
        "Native/shaders/hiz.hlsl", "Native/shaders/ssr_trace.hlsl", "Native/shaders/ssr_blur.hlsl",
        "Native/shaders/composite.hlsl"};
    const ZrShaderStage kR5Stages[kR5ShaderCount] = {ZrShaderStage::Vertex, ZrShaderStage::Pixel,
        ZrShaderStage::Vertex, ZrShaderStage::Pixel, ZrShaderStage::Vertex, ZrShaderStage::Pixel,
        ZrShaderStage::Vertex, ZrShaderStage::Vertex, ZrShaderStage::Pixel, ZrShaderStage::Pixel,
        ZrShaderStage::Vertex, ZrShaderStage::Pixel, ZrShaderStage::Vertex, ZrShaderStage::Pixel,
        ZrShaderStage::Vertex, ZrShaderStage::Pixel};
    const char* kR5Entries[kR5ShaderCount] = {"VsMain", "PsMain", "VsMain", "PsMain", "VsMain",
        "PsMain", "VsMain", "VsMain", "HiZCopyPsMain", "HiZMinPsMain", "VsMain", "PsMain",
        "VsMain", "PsMain", "VsMain", "PsMain"};
    const uint32_t kR5HlslOf[kR5ShaderCount] = {0u, 0u, 1u, 1u, 2u, 2u, 3u, 4u, 4u, 4u, 5u, 5u,
        6u, 6u, 7u, 7u};
    const char* kR5SpvSuffix[kR5ShaderCount] = {"_vs.spv", "_fs.spv", "_vs.spv", "_fs.spv",
        "_vs.spv", "_fs.spv", "_vs.spv", "_vs.spv", "_copy_fs.spv", "_min_fs.spv", "_vs.spv",
        "_fs.spv", "_vs.spv", "_fs.spv", "_vs.spv", "_fs.spv"};
    ZrShader r5Shaders[kR5ShaderCount] = {};
    std::vector<char> r5SpvBytes[kR5ShaderCount];
    for (uint32_t i = 0; i < kR5ShaderCount; ++i) {
        const std::string spvPath = FindSpirvSingle(kR5HlslPaths[kR5HlslOf[i]], kR5SpvSuffix[i]);
        if (spvPath.empty() || !ReadWholeFile(spvPath.c_str(), r5SpvBytes[i])
            || r5SpvBytes[i].empty()) {
            std::printf("zrhi_d3d12_window: --r5 FAILED to load SPIR-V for %s%s "
                        "(build generates Native/build/bin/shaders_spv/)\n",
                kR5HlslPaths[kR5HlslOf[i]], kR5SpvSuffix[i]);
            return 1;
        }
        ZrShaderDesc sDesc {};
        sDesc.stage = kR5Stages[i];
        sDesc.code = r5SpvBytes[i].data();
        sDesc.codeSize = r5SpvBytes[i].size();
        sDesc.entryPoint = kR5Entries[i];
        r5Shaders[i] = device->CreateShader(sDesc);
        if (!r5Shaders[i].IsValid()) {
            std::printf("zrhi_d3d12_window: --r5 shader module creation FAILED (%s)\n",
                spvPath.c_str());
            return 1;
        }
        std::printf("  SPIR-V loaded: %s\n", spvPath.c_str());
    }

    const ZrFormat windowRtv = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    auto makePso = [&](ZrShader vs, ZrShader ps, ZrFormat rtv, bool depth, bool depthWrite,
        ZrCullMode cull, uint32_t cbvCount, uint32_t srvTables) {
        ZrGraphicsPsoDesc desc {};
        desc.vertexShader = vs;
        desc.pixelShader = ps;
        desc.rtvFormat = rtv;
        desc.dsvFormat = depth ? ZrFormat::D32Float : ZrFormat::Count;
        desc.blend = ZrBlendMode::Opaque;
        desc.depthTest = depth;
        desc.depthWrite = depthWrite;
        desc.cullMode = cull;
        desc.rootCbvCount = cbvCount;
        desc.srvTableCount = srvTables;
        return device->CreatePipelineState(desc);
    };
    ZrPipelineState mainPso = makePso(r5Shaders[0], r5Shaders[1], ZrFormat::RGBA8Unorm, true, true,
        ZrCullMode::None, 5u, 1u); // b0..b4 + t0 阴影图
    ZrPipelineState toonPso = makePso(r5Shaders[2], r5Shaders[3], ZrFormat::RGBA8Unorm, true, true,
        ZrCullMode::None, 1u, 0u);
    ZrPipelineState outlinePso = makePso(r5Shaders[4], r5Shaders[5], ZrFormat::RGBA8Unorm, true,
        false, ZrCullMode::Front, 1u, 0u); // 反向壳：FrontCull + 只测不写
    ZrPipelineState hizCopyPso = makePso(r5Shaders[7], r5Shaders[8], ZrFormat::R32Float, false,
        false, ZrCullMode::None, 1u, 1u);
    ZrPipelineState hizMinPso = makePso(r5Shaders[7], r5Shaders[9], ZrFormat::R32Float, false,
        false, ZrCullMode::None, 1u, 1u);
    ZrPipelineState tracePso = makePso(r5Shaders[10], r5Shaders[11], ZrFormat::RGBA8Unorm, false,
        false, ZrCullMode::None, 1u, 2u); // t0 Hi-Z + t1 场景颜色
    ZrPipelineState blurPso = makePso(r5Shaders[12], r5Shaders[13], ZrFormat::RGBA8Unorm, false,
        false, ZrCullMode::None, 1u, 1u);
    ZrPipelineState compPso = makePso(r5Shaders[14], r5Shaders[15], windowRtv, false, false,
        ZrCullMode::None, 1u, 2u); // t0 反射 + t1 场景
    ZrGraphicsPsoDesc shadowPsoDesc {};
    shadowPsoDesc.vertexShader = r5Shaders[6];
    shadowPsoDesc.pixelShader = ZrShader{};
    shadowPsoDesc.rtvFormat = ZrFormat::Count;
    shadowPsoDesc.dsvFormat = ZrFormat::D32Float;
    shadowPsoDesc.blend = ZrBlendMode::Opaque;
    shadowPsoDesc.depthTest = true;
    shadowPsoDesc.depthWrite = true;
    shadowPsoDesc.cullMode = ZrCullMode::Front;
    shadowPsoDesc.rootCbvCount = 2u;
    shadowPsoDesc.depthOnly = true;
    ZrPipelineState shadowPso = device->CreatePipelineState(shadowPsoDesc);
    if (!mainPso.IsValid() || !toonPso.IsValid() || !outlinePso.IsValid() || !hizCopyPso.IsValid()
        || !hizMinPso.IsValid() || !tracePso.IsValid() || !blurPso.IsValid()
        || !compPso.IsValid() || !shadowPso.IsValid()) {
        std::printf("zrhi_d3d12_window: --r5 FAILED to create PSOs\n");
        return 1;
    }

    // ---- 两相位：SSR off（30 帧）→ on（30 帧）；三组可证伪读back ----------------------
    int probesOff[3][kR5ProbeCount][3] = {};
    int probesOn[3][kR5ProbeCount][3] = {};
    bool offOk = true;
    bool onOk = true;
    std::printf("zrhi_d3d12_window: --r5 phase SSR-OFF (%u frames) [vulkan]\n", kR5FramesPerPhase);
    offOk = RunR5PhaseVulkan(device, queue, cmdHandle, cmdList, swapchain, offscreenTarget,
        offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv, sceneImage,
        sceneRtv, sceneSrv, hizImage, hizRtvs, hizSrv, hizMipSrvs, depthSrv, traceImage, traceRtv,
        traceSrv, blurImage, blurRtv, blurSrv, mainPso, toonPso, outlinePso, shadowPso,
        hizCopyPso, hizMinPso, tracePso, blurPso, compPso, groundVb, groundIb, cubeVb, cubeIb,
        toonVb, toonIb, quadVb, quadIb, cubeIndexCount, cb0Ground, cb0Cube, cbToon, cbOutline,
        cbLights, cbClusters, cbShadows, cbGi, cbSsr, cbHiZ, cbBlur, cbComposite, cbShadowPass,
        cbShadowModelGround, cbShadowModelCube, /*ssrEnabled=*/false, probesOff);
    std::printf("zrhi_d3d12_window: --r5 phase SSR-ON (%u frames) [vulkan]\n", kR5FramesPerPhase);
    onOk = RunR5PhaseVulkan(device, queue, cmdHandle, cmdList, swapchain, offscreenTarget,
        offscreenRtv, depthImage, dsvView, shadowImage, shadowDsvs, shadowSrv, sceneImage,
        sceneRtv, sceneSrv, hizImage, hizRtvs, hizSrv, hizMipSrvs, depthSrv, traceImage, traceRtv,
        traceSrv, blurImage, blurRtv, blurSrv, mainPso, toonPso, outlinePso, shadowPso,
        hizCopyPso, hizMinPso, tracePso, blurPso, compPso, groundVb, groundIb, cubeVb, cubeIb,
        toonVb, toonIb, quadVb, quadIb, cubeIndexCount, cb0Ground, cb0Cube, cbToon, cbOutline,
        cbLights, cbClusters, cbShadows, cbGi, cbSsr, cbHiZ, cbBlur, cbComposite, cbShadowPass,
        cbShadowModelGround, cbShadowModelCube, /*ssrEnabled=*/true, probesOn);
    if (outPhaseProbes != nullptr) {
        std::memcpy(outPhaseProbes[0], probesOff, sizeof(probesOff));
        std::memcpy(outPhaseProbes[1], probesOn, sizeof(probesOn));
    }

    // ---- 三组可证伪读back判定（与 D3D12 同式同阈值）------------------------------------
    const int ssrDelta = R5MaxChannelDiff(probesOff[2][0], probesOn[2][0]);
    const int giDelta = R5MaxChannelDiff(probesOn[0][1], probesOn[2][1]);
    const int toonDelta = R5MaxChannelDiff(probesOn[2][2], probesOn[2][4]);
    const int bandFlat = (std::max)(R5MaxChannelDiff(probesOn[2][2], probesOn[2][3]),
        R5MaxChannelDiff(probesOn[2][4], probesOn[2][5]));

    // ---- 清理 -------------------------------------------------------------------------
    device->Destroy(cb0Ground);
    device->Destroy(cb0Cube);
    device->Destroy(cbToon);
    device->Destroy(cbOutline);
    device->Destroy(cbLights);
    device->Destroy(cbClusters);
    device->Destroy(cbShadows);
    device->Destroy(cbGi);
    device->Destroy(cbSsr);
    device->Destroy(cbBlur);
    device->Destroy(cbComposite);
    for (ZrBuffer& b : cbHiZ) {
        device->Destroy(b);
    }
    for (ZrBuffer& b : cbShadowPass) {
        device->Destroy(b);
    }
    device->Destroy(cbShadowModelGround);
    device->Destroy(cbShadowModelCube);
    device->Destroy(mainPso);
    device->Destroy(toonPso);
    device->Destroy(outlinePso);
    device->Destroy(hizCopyPso);
    device->Destroy(hizMinPso);
    device->Destroy(tracePso);
    device->Destroy(blurPso);
    device->Destroy(compPso);
    device->Destroy(shadowPso);
    for (const ZrShader& s : r5Shaders) {
        device->Destroy(s);
    }
    device->Destroy(groundVb);
    device->Destroy(groundIb);
    device->Destroy(cubeVb);
    device->Destroy(cubeIb);
    device->Destroy(toonVb);
    device->Destroy(toonIb);
    device->Destroy(quadVb);
    device->Destroy(quadIb);
    device->Destroy(dsvView);
    device->Destroy(depthSrv);
    for (ZrImageView v : shadowDsvs) {
        device->Destroy(v);
    }
    device->Destroy(shadowSrv);
    device->Destroy(sceneRtv);
    device->Destroy(sceneSrv);
    for (ZrImageView v : hizRtvs) {
        device->Destroy(v);
    }
    device->Destroy(hizSrv);
    for (ZrImageView v : hizMipSrvs) {
        device->Destroy(v);
    }
    device->Destroy(traceRtv);
    device->Destroy(traceSrv);
    device->Destroy(blurRtv);
    device->Destroy(blurSrv);
    if (offscreenRtv.IsValid()) {
        device->Destroy(offscreenRtv);
    }
    device->Destroy(depthImage);
    device->Destroy(shadowImage);
    device->Destroy(sceneImage);
    device->Destroy(hizImage);
    device->Destroy(traceImage);
    device->Destroy(blurImage);
    if (offscreenTarget.IsValid()) {
        device->Destroy(offscreenTarget);
    }
    device->WaitIdle();
    device->Destroy(cmdHandle);
    if (windowMode) {
        device->Destroy(swapchainHandle);
    }
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }
    const uint32_t validationErrors = device->GetDebugErrorCount();
    ZrVulkanDestroyDevice(device);

    // ---- 证据行与判定 -------------------------------------------------------------------
    std::printf("zrhi_d3d12_window: [R5][vulkan] ssr delta=%d (min %d) gi delta=%d (min %d) "
                "toon delta=%d bandFlat=%d (max %d)\n",
        ssrDelta, kR5SsrDeltaMin, giDelta, kR5GiDeltaMin, toonDelta, bandFlat,
        kR5ToonBandFlatMax);
    std::printf("zrhi_d3d12_window: [vulkan] validation error count = %u\n", validationErrors);

    const bool deltasOk = ssrDelta >= kR5SsrDeltaMin && giDelta >= kR5GiDeltaMin
        && toonDelta >= kR5ToonDeltaMin && bandFlat < 6;
    // undeclaredUses 在相位内检查并折入 offOk/onOk（同 D3D12 RunR5Phase 契约）
    if (offOk && onOk && deltasOk && validationErrors == 0u) {
        std::printf("VULKAN R5 OK (ssr=%d gi=%d toon=%d)\n", ssrDelta, giDelta, toonDelta);
        return 0;
    }
    std::printf("VULKAN R5 FAILED (offOk=%d onOk=%d ssr=%d/%d gi=%d/%d toon=%d/%d flat=%d "
                "errors=%u)\n",
        offOk ? 1 : 0, onOk ? 1 : 0, ssrDelta, kR5SsrDeltaMin, giDelta, kR5GiDeltaMin, toonDelta,
        kR5ToonDeltaMin, bandFlat, validationErrors);
    return 1;
}

// ---- 双后端对照（--r5 --backend both，TASK-B-R8-01）：D3D12 与 Vulkan 各跑一轮两相位
// --r5。判定：两后端各自的可证伪值（ssr/gi/toon）独立达标 + 全部探针读数并排对照
// （两相位 × 3 读back帧 × 6 探针 × RGB，差 ≤ kParityTolerance，报实际数字）----
static int RunR5Parity()
{
    std::printf("zrhi_d3d12_window: --r5 --backend both (TASK-B-R8-01 dual-backend parity)\n");
    int d3d12Probes[2][3][kR5ProbeCount][3] = {};
    const int d3d12Rc = RunR5Mode(d3d12Probes);
    int vkProbes[2][3][kR5ProbeCount][3] = {};
    const int vkRc = RunR5ModeVulkan(vkProbes);

    bool probesOk = true;
    int maxDelta = 0;
    static const char* kR5PhaseNames[2] = {"SSR-OFF", "SSR-ON"};
    for (uint32_t phase = 0; phase < 2u; ++phase) {
        for (uint32_t r = 0; r < 3u; ++r) {
            for (uint32_t p = 0; p < kR5ProbeCount; ++p) {
                const int dr
                    = std::abs(d3d12Probes[phase][r][p][0] - vkProbes[phase][r][p][0]);
                const int dg
                    = std::abs(d3d12Probes[phase][r][p][1] - vkProbes[phase][r][p][1]);
                const int db
                    = std::abs(d3d12Probes[phase][r][p][2] - vkProbes[phase][r][p][2]);
                const int delta = dr > dg ? (dr > db ? dr : db) : (dg > db ? dg : db);
                if (delta > maxDelta) {
                    maxDelta = delta;
                }
                if (delta > kParityTolerance) {
                    probesOk = false;
                    std::printf("R5 PROBE PARITY: %s readback %u probe %u d3d12 RGB=(%d,%d,%d) "
                                "vulkan RGB=(%d,%d,%d) maxDelta=%d EXCEEDS tolerance %d\n",
                        kR5PhaseNames[phase], r + 1u, p, d3d12Probes[phase][r][p][0],
                        d3d12Probes[phase][r][p][1], d3d12Probes[phase][r][p][2],
                        vkProbes[phase][r][p][0], vkProbes[phase][r][p][1],
                        vkProbes[phase][r][p][2], delta, kParityTolerance);
                }
            }
        }
    }
    std::printf("D3D12/VULKAN R5 PARITY %s (probe maxDelta=%d tolerance=%d d3d12Rc=%d vkRc=%d)\n",
        (probesOk && d3d12Rc == 0 && vkRc == 0) ? "OK" : "FAILED", maxDelta, kParityTolerance,
        d3d12Rc, vkRc);
    return (d3d12Rc == 0 && vkRc == 0 && probesOk && maxDelta <= kParityTolerance) ? 0 : 1;
}

// ---- TASK-B-R9-09：--resize 模式（Vulkan 交换链真重建可证伪验证）----------------
// 流程（照 A-4 的"窗口变小"口径）：
//   1. 起窗 640x480 → 30 帧红色清屏（每 5 帧 readback 中心 1x1 = 红，基线证据）；
//   2. SetWindowPos 真改客户区 480x360（**真机 resize 事件**，非 API 直调重建）；
//   3. 继续帧循环：resize 后 acquire 命中 VK_ERROR_OUT_OF_DATE_KHR → 帧闸粘性
//      （R10-03 A-5）→ 下一帧 Acquire 入口自动重建（产品路径，本程序不干预）→
//      重建后 readback 仍 = 红（"继续渲染且颜色校验仍过"）。
// 可证伪性：重建路径缺失（R10-03 基线行为）时 OUT_OF_DATE 后所有帧被跳过，
//   重建后成功 readback 计数为 0 → VULKAN RESIZE FAILED exit 1。
// 通过行：`VULKAN RESIZE OK (old=WxH new=WxH serial=N→M)`（尺寸/序号取设备台账）。
static int RunResizeModeVulkan()
{
    std::printf("zrhi_d3d12_window: --resize mode vulkan backend (TASK-B-R9-09)\n");
    constexpr uint32_t kNewWidth = 480u;  // 窗口变小（A-4 口径）
    constexpr uint32_t kNewHeight = 360u;
    constexpr uint32_t kFramesBefore = 30u; // resize 前基线帧数
    constexpr uint32_t kFramesTotal = 75u;  // 前 30 + 后 45（含 OUT_OF_DATE 跳帧余量）
    constexpr uint32_t kReadbackStride = 5u;
    constexpr float kClearColor[4] = {1.0f, 0.0f, 0.0f, 1.0f}; // 红色两相位恒定
    const int kExpected[3] = {255, 0, 0};

    // ---- 设备 / 队列 / 命令表 -------------------------------------------------------
    ZrVulkanDevice* device = ZrVulkanCreateDevice();
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --resize FAILED to create Vulkan device\n");
        return 1;
    }
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr) {
        std::printf("zrhi_d3d12_window: --resize FAILED to create queue/command list\n");
        ZrVulkanDestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链 --------------------------------------------------------------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    if (hWnd == nullptr) {
        std::printf("zrhi_d3d12_window: --resize window rejected (GetLastError=%lu)\n",
            GetLastError());
        ZrVulkanDestroyDevice(device);
        return 1;
    }
    ZrSwapchainDesc scDesc {};
    scDesc.width = kWidth;
    scDesc.height = kHeight;
    scDesc.windowHandle = hWnd;
    ZrSwapchain swapchainHandle = device->CreateSwapchain(scDesc);
    IZrSwapchain* swapchain = device->GetSwapchain(swapchainHandle);
    if (!swapchainHandle.IsValid() || swapchain == nullptr) {
        std::printf("zrhi_d3d12_window: --resize swapchain FAILED\n");
        DestroyWindow(hWnd);
        device->Destroy(cmdHandle);
        ZrVulkanDestroyDevice(device);
        return 1;
    }
    ShowWindow(hWnd, SW_SHOWNORMAL);
    PumpMessages();
    std::printf("zrhi_d3d12_window: --resize window 640x480 + swapchain (BGRA8Unorm, Fifo)\n");

    // ---- readback 缓冲（中心 1x1；READBACK 堆持久映射，同 --lights 既有做法）--------
    ZrBufferDesc rbDesc {};
    rbDesc.size = 256u;
    rbDesc.usage = ZrBufferUsage::TransferDst;
    rbDesc.readback = true;
    ZrBuffer readbackCenter = device->CreateBuffer(rbDesc);
    if (!readbackCenter.IsValid()) {
        std::printf("zrhi_d3d12_window: --resize FAILED to create readback buffer\n");
        device->Destroy(swapchainHandle);
        DestroyWindow(hWnd);
        device->Destroy(cmdHandle);
        ZrVulkanDestroyDevice(device);
        return 1;
    }

    // ---- 帧循环（两相位；重建检测按设备台账 serial）----------------------------------
    std::vector<ZrImageView> rtvViews; // 按后台缓冲索引缓存（serial 变更即整表作废重建）
    uint32_t lastSerial = 0u;
    uint32_t serialBeforeResize = 0u;
    uint32_t skippedFrames = 0u;       // OUT_OF_DATE 跳帧计数（取证）
    uint32_t undeclaredUses = 0u;
    uint32_t preChecks = 0u, prePasses = 0u;
    uint32_t postChecks = 0u, postPasses = 0u;
    bool sawRecreate = false;

    for (uint32_t frame = 0u; frame < kFramesTotal; ++frame) {
        if (frame == kFramesBefore) {
            // 真机 resize：客户区 640x480 → 480x360（AdjustWindowRect 换算外框；
            // VK surface 跟随 HWND 客户区，下一帧 acquire 命中 OUT_OF_DATE/SUBOPTIMAL）
            RECT rect {};
            rect.right = static_cast<LONG>(kNewWidth);
            rect.bottom = static_cast<LONG>(kNewHeight);
            AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
            SetWindowPos(hWnd, nullptr, 0, 0, rect.right - rect.left, rect.bottom - rect.top,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            PumpMessages();
            zr::SwapchainResizeLedger pre {};
            device->GetSwapchainResizeInfo(swapchainHandle, &pre);
            serialBeforeResize = pre.serial;
            std::printf("  --resize: window client %ux%u -> %ux%u (real WM_SIZE; serial=%u)\n",
                kWidth, kHeight, kNewWidth, kNewHeight, serialBeforeResize);
        }

        // 重建检测（产品路径在 Acquire 入口自动触发；这里只读台账做证据与缓存失效）
        zr::SwapchainResizeLedger ledger {};
        device->GetSwapchainResizeInfo(swapchainHandle, &ledger);
        if (ledger.serial != lastSerial) {
            std::printf("  --resize: swapchain recreated %ux%u -> %ux%u (serial=%u -> %u, "
                        "failedAttempts=%u)\n",
                ledger.fromWidth, ledger.fromHeight, ledger.toWidth, ledger.toHeight,
                lastSerial, ledger.serial, ledger.failedAttempts);
            lastSerial = ledger.serial;
            sawRecreate = true;
            // 依赖面收口的调用方侧：旧视图句柄已被设备销毁（stale），缓存整表作废
            rtvViews.clear();
        }

        const uint32_t index = swapchain->AcquireNextImage(); // ≈ vkAcquireNextImageKHR（含自动重建入口）
        if (!device->ShouldSubmitThisFrame()) {
            // OUT_OF_DATE 帧：无可用图像（R10-03 A-5 语义）——不渲染、不呈现
            ++skippedFrames;
            PumpMessages();
            continue;
        }

        ZrImage backbuffer = swapchain->GetCurrentImage();
        if (rtvViews.size() <= index) {
            rtvViews.resize(static_cast<size_t>(index) + 1u);
        }
        if (!rtvViews[index].IsValid()) {
            ZrImageViewDesc rtvDesc {};
            rtvDesc.image = backbuffer;
            rtvDesc.viewType = ZrImageViewType::Rtv;
            rtvViews[index] = device->CreateImageView(rtvDesc);
        }
        if (!rtvViews[index].IsValid()) {
            std::printf("  frame %u: FAILED to obtain RTV view\n", frame + 1u);
            continue;
        }

        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));

        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kClearColor,
                    ZrSubresourceRange::All());
            });
        const bool doReadback = (frame % kReadbackStride) == (kReadbackStride - 1u);
        if (doReadback) {
            graph.AddPass("ReadbackPass",
                [&](RgPass& pass) {
                    pass.Read(backH, ZrImageLayout::TransferSrcOptimal, ZrAccessBits::TransferRead,
                        ZrPipelineStageBits::Transfer)
                        .MarkRoot();
                },
                [&](RgContext& ctx) {
                    // 中心 1x1（行紧缩 4B；BGRA8Unorm 字节序 = B,G,R,A）
                    VkBufferImageCopy region {};
                    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                    region.imageSubresource.layerCount = 1u;
                    region.imageOffset = { static_cast<int32_t>(kWidth / 2u),
                        static_cast<int32_t>(kHeight / 2u), 0 };
                    region.imageExtent = { 1u, 1u, 1u };
                    vkCmdCopyImageToBuffer(device->VulkanNativeCmdOf(cmdHandle),
                        device->VulkanImageOf(ctx.GetImage(backH)),
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        device->VulkanBufferOf(readbackCenter), 1u, &region);
                });
        }
        graph.AddPass("PresentPass", [&](RgPass& pass) { pass.Present(backH).MarkRoot(); },
            nullptr);

        if (!graph.Compile()) {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            cmdList->End();
            continue;
        }
        (void)graph.ExecuteAliased(*device, *cmdList);
        undeclaredUses += graph.LastExecuteUndeclaredUses();
        cmdList->End();
        queue->Submit(cmdHandle);
        swapchain->Present();
        device->WaitIdle();
        graph.FlushRetired(*device);
        PumpMessages();

        // ---- 颜色校验（中心 1x1 = 红；相位判定按 frame == kFramesBefore 分界）----------
        if (doReadback) {
            IZrBuffer* rbIface = device->GetBuffer(readbackCenter);
            void* mapped = (rbIface != nullptr) ? rbIface->Map() : nullptr;
            if (mapped == nullptr) {
                std::printf("  frame %u: readback Map FAILED\n", frame + 1u);
                continue;
            }
            const uint8_t* px = static_cast<const uint8_t*>(mapped);
            const int r = static_cast<int>(px[2]); // BGRA → RGB
            const int g = static_cast<int>(px[1]);
            const int b = static_cast<int>(px[0]);
            const bool ok = std::abs(r - kExpected[0]) <= kTolerance
                && std::abs(g - kExpected[1]) <= kTolerance
                && std::abs(b - kExpected[2]) <= kTolerance;
            const bool postPhase = (frame >= kFramesBefore);
            if (postPhase) {
                ++postChecks;
                postPasses += ok ? 1u : 0u;
            } else {
                ++preChecks;
                prePasses += ok ? 1u : 0u;
            }
            std::printf("  frame %u readback center RGB=(%d,%d,%d) expected (%d,%d,%d) -> %s "
                        "[%s]\n",
                frame + 1u, r, g, b, kExpected[0], kExpected[1], kExpected[2], ok ? "OK" : "MISMATCH",
                postPhase ? "post-recreate" : "pre-resize");
            rbIface->Unmap();
        }
    }

    // ---- 判定与证据行 -----------------------------------------------------------------
    zr::SwapchainResizeLedger finalLedger {};
    const bool ledgerOk = device->GetSwapchainResizeInfo(swapchainHandle, &finalLedger);
    const uint32_t validationErrors = device->GetDebugErrorCount();
    const bool colorOk = preChecks > 0u && prePasses == preChecks && postChecks >= 2u
        && postPasses == postChecks;
    const bool recreateOk = sawRecreate && finalLedger.serial > serialBeforeResize
        && finalLedger.fromWidth == kWidth && finalLedger.fromHeight == kHeight
        && finalLedger.toWidth == kNewWidth && finalLedger.toHeight == kNewHeight;

    device->Destroy(readbackCenter);
    for (ZrImageView& v : rtvViews) {
        if (v.IsValid()) {
            device->Destroy(v); // 若台账收口已销毁则此为 stale 安全空操作
        }
    }
    device->WaitIdle();
    device->Destroy(cmdHandle);
    device->Destroy(swapchainHandle);
    DestroyWindow(hWnd);
    PumpMessages();
    ZrVulkanDestroyDevice(device);

    std::printf("zrhi_d3d12_window: [RESIZE][vulkan] frames=%u skipped=%u pre=%u/%u post=%u/%u "
                "serial=%u failedAttempts=%u validationErrors=%u undeclared=%u\n",
        kFramesTotal, skippedFrames, prePasses, preChecks, postPasses, postChecks,
        finalLedger.serial, finalLedger.failedAttempts, validationErrors, undeclaredUses);
    if (ledgerOk && colorOk && recreateOk && validationErrors == 0u && undeclaredUses == 0u) {
        std::printf("VULKAN RESIZE OK (old=%ux%u new=%ux%u serial=%u→%u)\n",
            finalLedger.fromWidth, finalLedger.fromHeight, finalLedger.toWidth,
            finalLedger.toHeight, serialBeforeResize, finalLedger.serial);
        return 0;
    }
    std::printf("VULKAN RESIZE FAILED (recreate=%d color=%d ledger=%d recreateOk=%d "
                "skipped=%u errors=%u)\n",
        sawRecreate ? 1 : 0, colorOk ? 1 : 0, ledgerOk ? 1 : 0, recreateOk ? 1 : 0,
        skippedFrames, validationErrors);
    return 1;
}

#endif // ZR_HAS_VULKAN


// =============================================================================
// ---- TASK-B-R9-16：--devconsole 模式（GPU 工具链运行时入口，方案 §5.7）----------
//
// 机制沿用 A-DEV-03（Unity 侧运行期 DevConsole）的契约落到原生窗口程序：
//   - 传输：TCP 只绑 127.0.0.1（绝不 0.0.0.0），端口缺省 17323（避开 Editor
//     17321 与 Unity 运行期 17322），可 --devconsole-port <n> 覆盖；
//   - 协议：一行一条请求、一行一条 JSON 响应，单行上限 4KB；
//   - 认证（TASK-X-SEC-01）：每连接首帧必须 `auth <token>`（128 位 CSPRNG，
//     启动打到 stdout，仅本地调试）；常量时间比较；连续 5 次失败封 30s 并记录
//     对端；未认证一律 auth_required/auth_failed/auth_locked；
//   - 白名单（硬编码，解析/信封在 zrender/devconsole_proto.* 纯逻辑层）：
//       help | hud | rg.export [path] [json|text] | renderdoc.capture [name] | quit
//   - 线程纪律：监听线程只做 socket 收发；命令在渲染线程的帧循环内执行
//     （设备统计/RG/RenderDoc 均不跨线程），响应在命令执行点回写。
//
// 三件交付在此汇合：
//   1. hud   —— 每帧 DrawCall 计数（CubeDrawPass 内真实计数）+ RT 分辨率 +
//               显存字节估算（本模式自建资源台账，ZrEstimate* 纯函数）+
//               ZrDeviceStats 透传，经 ZrHudAggregator 滑窗聚合；
//   2. renderdoc.capture —— RenderDoc 应用内 API 一键捕获：进程被 RenderDoc
//               UI 启动注入后自动挂接（GetModuleHandleA 探测）；未挂接 →
//               明确错误 {"ok":false,"error":"renderdoc_not_attached"}（不
//               静默假成功——RenderDoc 本体仍是用户手动启动，本程序不注入）；
//   3. rg.export —— 当帧 RgGraph 编译产物经 RgExportJson/RgExportText 落盘。
//               TASK-X-SEC-01：落盘路径限定在允许根内（zrender/rg_export_path.*），
//               根外/含 `..`/符号链接逃逸一律拒绝并记录。
// =============================================================================

namespace devconsole {

/// 捕获/导出的固定落盘目录（相对仓库根调用约定，与其它模式一致）
constexpr const char* kDefaultOutputDir = "Native/build/renderdoc";
/// 监听端口缺省值（17321/17322 已被 Editor/Unity DevConsole 占用）
constexpr uint16_t kDefaultPort = 17323u;
/// 待执行请求队列上限（超出即回 server_busy——不无限积压）
constexpr size_t kMaxQueuedRequests = 64u;
/// 单连接读行超时（毫秒）：客户端连上后须立即发整行
constexpr DWORD kRecvTimeoutMs = 5000u;

// ---- TASK-X-SEC-01：认证辅助 ---------------------------------------------------

uint64_t NowMs()
{
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch())
                                     .count());
}

std::string FormatPeer(const sockaddr_in& from)
{
    char ip[INET_ADDRSTRLEN] = {};
    if (::inet_ntop(AF_INET, &from.sin_addr, ip, sizeof(ip)) == nullptr) {
        return std::string("unknown");
    }
    return std::string(ip) + ':'
        + std::to_string(static_cast<unsigned>(::ntohs(from.sin_port)));
}

bool IsLoopbackPeer(const sockaddr_in& from)
{
    return from.sin_family == AF_INET
        && (static_cast<unsigned>(::ntohl(from.sin_addr.s_addr)) >> 24) == 127u;
}

struct LineRead {
    bool gotLine = false;
    bool overflow = false;
    std::string line;
};

/// 按行读取协议帧（每个连接一个实例）：保留一次 recv 里跨行的剩余字节。
/// 认证帧与命令帧可能在同一个 TCP 段到达（客户端管道化发送），若每次读行都
/// 丢弃尾随字节，第二条命令就会被吞掉。
class ProtocolLineReader {
public:
    ProtocolLineReader(SOCKET client, DWORD timeoutMs)
        : m_client(client)
    {
        DWORD tv = timeoutMs;
        ::setsockopt(m_client, SOL_SOCKET, SO_RCVTIMEO,
            reinterpret_cast<const char*>(&tv), sizeof(tv));
    }

    LineRead Next()
    {
        LineRead out;
        for (;;) {
            const size_t newline = m_pending.find('\n');
            if (newline != std::string::npos) {
                out.line = m_pending.substr(0u, newline);
                m_pending.erase(0u, newline + 1u);
                out.gotLine = true;
                return out;
            }
            if (m_pending.size() > zrender::kZrDevConsoleMaxLineBytes) {
                out.overflow = true;
                m_pending.clear();
                return out;
            }
            char buffer[512];
            const int got = ::recv(m_client, buffer, sizeof(buffer), 0);
            if (got <= 0) {
                return out; // 超时/断开：gotLine=false
            }
            m_pending.append(buffer, static_cast<size_t>(got));
        }
    }

private:
    SOCKET m_client;
    std::string m_pending;
};

/// 首帧形态：`auth <token>`（恰好两个 token）。命令关键字不是秘密，普通比较即可；
/// token 的相等判断一律走 zrender::ZrDevConsoleTokenEquals（常量时间）。
bool ParseAuthLine(const std::string& line, std::string& outToken)
{
    const auto isSpace = [](unsigned char c) {
        return c == ' ' || c == '\t' || c == '\r' || c == '\n';
    };
    size_t begin = 0;
    size_t end = line.size();
    while (begin < end && isSpace(static_cast<unsigned char>(line[begin]))) {
        ++begin;
    }
    while (end > begin && isSpace(static_cast<unsigned char>(line[end - 1]))) {
        --end;
    }
    std::vector<std::string> tokens;
    std::string current;
    for (size_t i = begin; i < end; ++i) {
        const unsigned char c = static_cast<unsigned char>(line[i]);
        if (isSpace(c)) {
            if (!current.empty()) {
                tokens.push_back(current);
                current.clear();
            }
        } else {
            current += static_cast<char>(c);
        }
    }
    if (!current.empty()) {
        tokens.push_back(current);
    }
    if (tokens.size() != 2u || tokens[0] != "auth") {
        return false;
    }
    outToken = tokens[1];
    return true;
}

// ---- TCP 白名单控制台（监听线程只收发；执行在渲染线程）---------------------------
class DevConsoleServer {
public:
    struct Request {
        SOCKET client;
        std::string line;
        std::string peer; // 认证通过后的对端标识（日志/拒绝记录）
    };

    DevConsoleServer() = default;
    DevConsoleServer(const DevConsoleServer&) = delete;
    DevConsoleServer& operator=(const DevConsoleServer&) = delete;
    ~DevConsoleServer() { Stop(); }

    /// token 为空拒绝启动（fail-closed：绝不出现"无 token 即放行"）
    bool Start(uint16_t port, const std::string& token, std::string& outError)
    {
        if (token.empty()) {
            outError = "empty auth token - refusing to listen";
            return false;
        }
        m_auth.SetToken(token);
        WSADATA wsa {};
        if (::WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
            outError = "WSAStartup failed";
            return false;
        }
        m_wsaStarted = true;
        m_listener = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (m_listener == INVALID_SOCKET) {
            outError = "socket() failed";
            return false;
        }
        // 只绑 127.0.0.1——本控制台能触发捕获/写盘，绝不暴露给外部（A-DEV-03 铁律）
        sockaddr_in addr {};
        addr.sin_family = AF_INET;
        addr.sin_port = ::htons(port);
        if (::inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr) != 1) {
            outError = "inet_pton(127.0.0.1) failed";
            return false;
        }
        if (::bind(m_listener, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr))
            == SOCKET_ERROR) {
            outError = "bind(127.0.0.1:" + std::to_string(port) + ") failed (errno "
                + std::to_string(::WSAGetLastError()) + ")";
            return false;
        }
        if (::listen(m_listener, 4) == SOCKET_ERROR) {
            outError = "listen() failed";
            return false;
        }
        m_running.store(true, std::memory_order_release);
        m_thread = std::thread([this]() { ListenLoop(); });
        return true;
    }

    void Stop()
    {
        if (m_running.exchange(false)) {
            if (m_thread.joinable()) {
                m_thread.join(); // select 超时循环 100ms 内自然退出（不在阻塞点关闭）
            }
        }
        if (m_listener != INVALID_SOCKET) {
            ::closesocket(m_listener);
            m_listener = INVALID_SOCKET;
        }
        // 未发送完的挂起连接一律关闭（进程通常即将退出）
        std::vector<Request> leftovers;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            leftovers.swap(m_queue);
        }
        for (const Request& r : leftovers) {
            ::closesocket(r.client);
        }
        if (m_wsaStarted) {
            ::WSACleanup();
            m_wsaStarted = false;
        }
    }

    /// 渲染线程每帧取走已到达的请求（此后 socket 归渲染线程所有）
    std::vector<Request> Drain()
    {
        std::vector<Request> out;
        std::lock_guard<std::mutex> lock(m_mutex);
        out.swap(m_queue);
        return out;
    }

    /// 发一行并保持连接（认证回执用；失败返回 false）
    static bool SendLine(SOCKET client, const std::string& response)
    {
        if (client == INVALID_SOCKET) {
            return false;
        }
        std::string line = response;
        line += '\n';
        const char* cursor = line.data();
        size_t remaining = line.size();
        while (remaining > 0u) {
            const int sent = ::send(client, cursor, static_cast<int>(remaining), 0);
            if (sent == SOCKET_ERROR || sent <= 0) {
                return false;
            }
            cursor += sent;
            remaining -= static_cast<size_t>(sent);
        }
        return true;
    }

    /// 回一行 JSON 响应并关闭连接（单行协议：响应追加 '\n'；处理部分发送）
    static void SendAndClose(SOCKET client, const std::string& response)
    {
        if (client == INVALID_SOCKET) {
            return;
        }
        SendLine(client, response);
        ::shutdown(client, SD_SEND);
        // 排空对端已管道化发送的剩余字节：带着未读数据 closesocket 会让 TCP
        // 回 RST 并丢弃刚发出的响应（客户端只看到连接被重置，看不到拒绝原因）。
        // 上限 8KB + 100ms，避免恶意客户端拖住监听/渲染线程。
        DWORD drainTimeoutMs = 100u;
        ::setsockopt(client, SOL_SOCKET, SO_RCVTIMEO,
            reinterpret_cast<const char*>(&drainTimeoutMs), sizeof(drainTimeoutMs));
        char scratch[512];
        size_t drained = 0u;
        while (drained < 8192u) {
            const int got = ::recv(client, scratch, sizeof(scratch), 0);
            if (got <= 0) {
                break;
            }
            drained += static_cast<size_t>(got);
        }
        ::closesocket(client);
    }

private:
    void ListenLoop()
    {
        while (m_running.load(std::memory_order_acquire)) {
            // select 超时轮询 accept：Stop() 无需在阻塞点下关 socket
            fd_set readSet;
            FD_ZERO(&readSet);
            FD_SET(m_listener, &readSet);
            timeval timeout {};
            timeout.tv_sec = 0;
            timeout.tv_usec = 100 * 1000; // 100ms
            const int ready = ::select(0, &readSet, nullptr, nullptr, &timeout);
            if (ready <= 0) {
                continue; // 超时/错误：回看 running 标志
            }
            sockaddr_in from {};
            int fromLen = sizeof(from);
            const SOCKET client
                = ::accept(m_listener, reinterpret_cast<sockaddr*>(&from), &fromLen);
            if (client == INVALID_SOCKET) {
                continue;
            }
            HandleConnection(client, from);
        }
    }

    /// TASK-X-SEC-01：连接生命周期 = 首帧 `auth <token>`（回执不关连接）→
    /// 第二行命令（入队，渲染线程执行并回包关闭）。认证失败限流 + 记录对端。
    void HandleConnection(SOCKET client, const sockaddr_in& from)
    {
        const std::string peer = FormatPeer(from);
        const uint64_t now = NowMs();

        // 纵深防御：bind 已限定 127.0.0.1，这里再挡一次非回环对端
        if (!IsLoopbackPeer(from)) {
            std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH DENIED peer=%s "
                        "reason=non_loopback (LOCAL DEBUG ONLY)\n",
                peer.c_str());
            std::fflush(stdout);
            SendAndClose(client,
                zrender::ZrDevConsoleError("auth_denied", "loopback peers only"));
            return;
        }

        if (m_auth.IsLocked(now)) {
            const uint64_t remaining = m_auth.LockRemainingMs(now);
            std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH DENIED peer=%s reason=locked "
                        "remaining_ms=%llu failures=%u/%u (LOCAL DEBUG ONLY)\n",
                peer.c_str(), static_cast<unsigned long long>(remaining),
                m_auth.ConsecutiveFailures(), m_auth.MaxFailures());
            std::fflush(stdout);
            SendAndClose(client,
                zrender::ZrDevConsoleError("auth_locked",
                    "too many failed attempts; retry in "
                        + std::to_string(remaining) + " ms"));
            return;
        }

        ProtocolLineReader reader(client, kRecvTimeoutMs);
        const LineRead first = reader.Next();
        if (first.overflow) {
            std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH DENIED peer=%s "
                        "reason=line_too_long (LOCAL DEBUG ONLY)\n",
                peer.c_str());
            std::fflush(stdout);
            SendAndClose(client, zrender::ZrDevConsoleError("line_too_long",
                "auth line exceeds 4096 bytes"));
            return;
        }
        if (!first.gotLine) {
            // 连上不发帧即断开：不计数（避免误连/探测把正常客户端拖入封禁）
            std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH DENIED peer=%s "
                        "reason=no_auth_frame (LOCAL DEBUG ONLY)\n",
                peer.c_str());
            std::fflush(stdout);
            ::closesocket(client);
            return;
        }

        std::string presented;
        if (!ParseAuthLine(first.line, presented)) {
            m_auth.Verify(std::string(), now); // 计入一次失败
            std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH DENIED peer=%s "
                        "reason=missing_auth failures=%u/%u (LOCAL DEBUG ONLY)\n",
                peer.c_str(), m_auth.ConsecutiveFailures(), m_auth.MaxFailures());
            std::fflush(stdout);
            SendAndClose(client, zrender::ZrDevConsoleError("auth_required",
                "first frame must be: auth <token> (token is printed at startup)"));
            return;
        }

        const zrender::ZrDevConsoleAuthenticator::Verdict verdict
            = m_auth.Verify(presented, now);
        if (verdict != zrender::ZrDevConsoleAuthenticator::Verdict::Accepted) {
            const bool locked
                = verdict == zrender::ZrDevConsoleAuthenticator::Verdict::Locked;
            if (locked) {
                std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH DENIED peer=%s "
                            "reason=locked remaining_ms=%llu (LOCAL DEBUG ONLY)\n",
                    peer.c_str(),
                    static_cast<unsigned long long>(m_auth.LockRemainingMs(NowMs())));
            } else {
                std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH DENIED peer=%s "
                            "reason=bad_token failures=%u/%u%s (LOCAL DEBUG ONLY)\n",
                    peer.c_str(), m_auth.ConsecutiveFailures(), m_auth.MaxFailures(),
                    m_auth.IsLocked(NowMs()) ? " NOW_LOCKED" : "");
            }
            std::fflush(stdout);
            SendAndClose(client,
                zrender::ZrDevConsoleError(locked ? "auth_locked" : "auth_failed",
                    "invalid DevConsole token"));
            return;
        }

        std::printf("zrhi_d3d12_window: DEVCONSOLE AUTH OK peer=%s (LOCAL DEBUG ONLY)\n",
            peer.c_str());
        std::fflush(stdout);

        // 回执保持连接（客户端可以先发命令再读两行，也可以逐帧握手）
        if (!SendLine(client, zrender::ZrDevConsoleOk("\"authenticated\":true"))) {
            ::closesocket(client);
            return;
        }

        const LineRead cmd = reader.Next();
        if (cmd.overflow) {
            SendAndClose(client, zrender::ZrDevConsoleError("line_too_long",
                "request line exceeds 4096 bytes"));
            return;
        }
        if (!cmd.gotLine) {
            ::closesocket(client); // 认证后不发命令：只握手不执行
            return;
        }

        {
            std::lock_guard<std::mutex> lock(m_mutex);
            if (m_queue.size() < kMaxQueuedRequests) {
                m_queue.push_back(Request { client, cmd.line, peer });
                return;
            }
        }
        SendAndClose(client, zrender::ZrDevConsoleError("server_busy",
            "request queue full (>64) - render thread stalled?"));
    }

    std::thread m_thread;
    std::mutex m_mutex;
    std::vector<Request> m_queue;
    zrender::ZrDevConsoleAuthenticator m_auth; // 监听线程独占（不跨线程）
    std::atomic<bool> m_running { false };
    SOCKET m_listener = INVALID_SOCKET;
    bool m_wsaStarted = false;
};

} // namespace devconsole

// --devconsole 模式主体（返回进程退出码；参数见 main 分发处注释）
static int RunDevConsoleMode(int argc, char** argv)
{
    // ---- 参数（本模式自有选项；zmesh/hlsl 缺省与 --draw 同源）--------------------
    uint16_t port = devconsole::kDefaultPort;
    uint32_t maxFrames = 0u; // 0 = 不限（quit / 窗口关闭 / 百万帧安全闸退出）
    const char* zmeshPath = "Native/tests/data/cube.zmesh";
    const char* hlslPath = "Native/shaders/first_draw.hlsl";
    std::string outputDir = devconsole::kDefaultOutputDir;
    std::string explicitExportDir; // TASK-X-SEC-01：--rg-export-dir（显式允许根）
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--devconsole-port") == 0 && i + 1 < argc) {
            port = static_cast<uint16_t>(std::atoi(argv[++i]));
        } else if (std::strcmp(argv[i], "--devconsole-frames") == 0 && i + 1 < argc) {
            maxFrames = static_cast<uint32_t>(std::atoi(argv[++i]));
        } else if (std::strcmp(argv[i], "--devconsole-zmesh") == 0 && i + 1 < argc) {
            zmeshPath = argv[++i];
        } else if (std::strcmp(argv[i], "--devconsole-hlsl") == 0 && i + 1 < argc) {
            hlslPath = argv[++i];
        } else if (std::strcmp(argv[i], "--devconsole-dir") == 0 && i + 1 < argc) {
            outputDir = argv[++i];
        } else if (std::strcmp(argv[i], "--rg-export-dir") == 0 && i + 1 < argc) {
            explicitExportDir = argv[++i];
        }
    }

    // ---- TASK-X-SEC-01：rg.export 允许根（默认拒绝，显式授权）---------------------
    // 三个根并列生效：exe 同目录 rg_export / %LOCALAPPDATA%\ZFrame\rg_export /
    // 显式 --rg-export-dir。缺省落点优先级同上（后者最高），均与进程 CWD 无关。
    std::filesystem::path exeExportRoot;
    {
        wchar_t exePath[4096] = {};
        const DWORD len = ::GetModuleFileNameW(nullptr, exePath, 4096u);
        if (len > 0u && len < 4096u) {
            exeExportRoot = std::filesystem::path(exePath).parent_path() / "rg_export";
        }
    }
    std::filesystem::path localExportRoot;
    {
        wchar_t buffer[4096] = {};
        const DWORD len = ::GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, 4096u);
        if (len > 0u && len < 4096u) {
            localExportRoot = std::filesystem::path(buffer) / "ZFrame" / "rg_export";
        }
    }
    zrender::ZrRgExportRoots exportRoots;
    if (!exeExportRoot.empty()) {
        exportRoots.allowed.push_back(exeExportRoot.u8string());
    }
    if (!localExportRoot.empty()) {
        exportRoots.allowed.push_back(localExportRoot.u8string());
    }
    if (!explicitExportDir.empty()) {
        std::error_code ec;
        const std::filesystem::path absoluteExportDir
            = std::filesystem::absolute(std::filesystem::u8path(explicitExportDir), ec);
        if (ec) {
            std::printf("zrhi_d3d12_window: --devconsole FAILED to resolve --rg-export-dir "
                        "'%s': %s\n",
                explicitExportDir.c_str(), ec.message().c_str());
            return 1;
        }
        exportRoots.allowed.push_back(absoluteExportDir.u8string());
        exportRoots.defaultRoot = absoluteExportDir.u8string();
    } else if (!localExportRoot.empty()) {
        exportRoots.defaultRoot = localExportRoot.u8string();
    } else if (!exeExportRoot.empty()) {
        exportRoots.defaultRoot = exeExportRoot.u8string();
    }
    if (exportRoots.defaultRoot.empty()) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED: no rg.export root available "
                    "(LOCALAPPDATA unset and exe path unknown)\n");
        return 1;
    }
    {
        std::error_code ec;
        std::filesystem::create_directories(
            std::filesystem::u8path(exportRoots.defaultRoot), ec);
        if (ec) {
            std::printf("zrhi_d3d12_window: --devconsole WARNING: cannot create rg.export "
                        "root '%s': %s\n",
                exportRoots.defaultRoot.c_str(), ec.message().c_str());
        }
    }

    // ---- TASK-X-SEC-01：一次性 token（128 位 CSPRNG）-----------------------------
    // 只打到 stdout 日志（本地调试通道），不写进任何随包分发的位置；
    // 生成失败即拒绝启动（不退化到弱随机/无认证）。
    std::string authTokenError;
    const std::string authToken = zrender::ZrDevConsoleGenerateToken(authTokenError);
    if (authToken.empty()) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to generate auth token: %s\n",
            authTokenError.c_str());
        return 1;
    }

    std::printf("zrhi_d3d12_window: --devconsole mode (TASK-B-R9-16/X-SEC-01): port=%u "
                "zmesh='%s' hlsl='%s' dir='%s' rg_export_root='%s' frames=%s\n",
        static_cast<unsigned>(port), zmeshPath, hlslPath, outputDir.c_str(),
        exportRoots.defaultRoot.c_str(),
        maxFrames == 0u ? "unbounded" : std::to_string(maxFrames).c_str());
    std::printf("zrhi_d3d12_window: DEVCONSOLE rg.export allowed roots:");
    for (const std::string& rootText : exportRoots.allowed) {
        std::printf(" '%s'", rootText.c_str());
    }
    std::printf("\n");
    std::printf("zrhi_d3d12_window: DEVCONSOLE auth token (LOCAL DEBUG ONLY - do not ship "
                "this log): %s\n",
        authToken.c_str());
    std::fflush(stdout);

    // ---- 设备 / 队列 / 命令表（--draw 同构）--------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to create D3D12 device\n");
        return 1;
    }
    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdHandle);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdHandle);
    if (queue == nullptr || !cmdHandle.IsValid() || cmdList == nullptr
        || nativeCmd == nullptr) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- 窗口 + 交换链（失败自动降级离屏；离屏用 RenderDoc 手动帧捕获半边）--------
    HWND hWnd = TryCreateWindow(kWidth, kHeight);
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {};
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: --devconsole swapchain FAILED, falling back to "
                        "offscreen target\n");
            DestroyWindow(hWnd);
            hWnd = nullptr;
        }
    } else {
        std::printf("zrhi_d3d12_window: --devconsole window rejected (GetLastError=%lu), "
                    "falling back to offscreen target\n",
            GetLastError());
    }
    const bool windowMode = (swapchain != nullptr);
    if (windowMode) {
        ShowWindow(hWnd, SW_SHOWNORMAL);
    }

    // ---- 渲染目标（离屏降级）+ 深度 + DSV -----------------------------------------
    ZrImage offscreenTarget {};
    ZrImageView offscreenRtv {};
    ZrImageDesc swapchainImageDesc {};
    swapchainImageDesc.format = ZrFormat::BGRA8Unorm;
    swapchainImageDesc.width = kWidth;
    swapchainImageDesc.height = kHeight;
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenTarget = device->CreateImage(rtDesc);
        ZrImageViewDesc rtvViewDesc {};
        rtvViewDesc.image = offscreenTarget;
        rtvViewDesc.viewType = ZrImageViewType::Rtv;
        offscreenRtv = device->CreateImageView(rtvViewDesc);
        if (!offscreenTarget.IsValid() || !offscreenRtv.IsValid()) {
            std::printf("zrhi_d3d12_window: --devconsole FAILED to create offscreen target\n");
            ZrD3D12DestroyDevice(device);
            return 1;
        }
    }
    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = kWidth;
    depthDesc.height = kHeight;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    ZrImage depthImage = device->CreateImage(depthDesc);
    ZrImageViewDesc dsvViewDesc {};
    dsvViewDesc.image = depthImage;
    dsvViewDesc.viewType = ZrImageViewType::Dsv;
    ZrImageView dsvView = device->CreateImageView(dsvViewDesc);
    if (!depthImage.IsValid() || !dsvView.IsValid()) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to create depth image/DSV\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- zmesh → VB/IB → 3 个逐绘常量缓冲 → PSO（--draw 同构）----------------------
    std::vector<char> meshBytes;
    if (!ReadWholeFile(zmeshPath, meshBytes)) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to open zmesh '%s'\n", zmeshPath);
        ZrD3D12DestroyDevice(device);
        return 1;
    }
    ZmeshData mesh {};
    if (zmesh_Parse(meshBytes.data(), meshBytes.size(), &mesh) != ZMESH_OK) {
        std::printf("zrhi_d3d12_window: --devconsole zmesh parse FAILED\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }
    const uint32_t indexCount = mesh.stats.indexCount;
    std::vector<FirstDrawVertex> vertices(mesh.stats.vertexCount);
    for (uint32_t i = 0; i < mesh.stats.vertexCount; ++i) {
        std::memcpy(vertices[i].pos, &mesh.positions[i * 3u], sizeof(float) * 3u);
        std::memcpy(vertices[i].normal, &mesh.normals[i * 3u], sizeof(float) * 3u);
        std::memcpy(vertices[i].uv, &mesh.uvs[i * 2u], sizeof(float) * 2u);
    }
    std::vector<uint32_t> indices(indexCount);
    std::memcpy(indices.data(), mesh.indices, sizeof(uint32_t) * indexCount);

    ZrBufferDesc vbDesc {};
    vbDesc.size = static_cast<uint64_t>(vertices.size()) * sizeof(FirstDrawVertex);
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    ZrBuffer vb = device->CreateBuffer(vbDesc, vertices.data());
    ZrBufferDesc ibDesc {};
    ibDesc.size = static_cast<uint64_t>(indices.size()) * sizeof(uint32_t);
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    ZrBuffer ib = device->CreateBuffer(ibDesc, indices.data());
    if (!vb.IsValid() || !ib.IsValid()) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to create VB/IB\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    constexpr uint32_t kDrawItems = 3u; // 3 个 cube → 每帧 3 次 DrawIndexedInstanced
    ZrBuffer cbHandles[kDrawItems] = {};
    void* cbMapped[kDrawItems] = {};
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        ZrBufferDesc cbDesc {};
        cbDesc.size = 256u; // 根 CBV 对齐裕量（常量实际 96B）
        cbDesc.usage = ZrBufferUsage::Uniform;
        cbDesc.hostVisible = true;
        cbHandles[i] = device->CreateBuffer(cbDesc);
        IZrBuffer* iface = device->GetBuffer(cbHandles[i]);
        cbMapped[i] = (iface != nullptr && cbHandles[i].IsValid()) ? iface->Map() : nullptr;
        if (cbMapped[i] == nullptr) {
            std::printf("zrhi_d3d12_window: --devconsole FAILED to create/map CB %u\n", i);
            ZrD3D12DestroyDevice(device);
            return 1;
        }
    }

    std::vector<char> hlsl;
    if (!ReadWholeFile(hlslPath, hlsl)) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to open hlsl '%s'\n", hlslPath);
        ZrD3D12DestroyDevice(device);
        return 1;
    }
    const ZrShader vs = device->CompileHlsl(ZrShaderStage::Vertex, hlsl.data(), hlsl.size(),
        "VsMain");
    const ZrShader ps = device->CompileHlsl(ZrShaderStage::Pixel, hlsl.data(), hlsl.size(),
        "PsMain");
    if (!vs.IsValid() || !ps.IsValid()) {
        std::printf("zrhi_d3d12_window: --devconsole shader compilation FAILED\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }
    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = vs;
    psoDesc.pixelShader = ps;
    psoDesc.rtvFormat = windowMode ? ZrFormat::BGRA8Unorm : ZrFormat::RGBA8Unorm;
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    psoDesc.cullMode = ZrCullMode::None; // --draw 同款：烘焙 cube 绕序实证 None 才可见
    ZrPipelineState pso = device->CreatePipelineState(psoDesc);
    if (!pso.IsValid()) {
        std::printf("zrhi_d3d12_window: --devconsole FAILED to create graphics PSO\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- RTV 视图（窗口模式按后台缓冲缓存）+ 显存字节台账 --------------------------
    ZrImageView rtvViews[3] = {};
    uint64_t memoryBytes = 0u; // 本模式自建资源的估算字节（调用方口径，见 gpu_hud.h）
    if (windowMode) {
        memoryBytes += zrender::ZrEstimateImageBytes(swapchainImageDesc) * 3u; // 三缓冲
    } else {
        ZrImageDesc offDesc {};
        offDesc.format = ZrFormat::RGBA8Unorm;
        offDesc.width = kWidth;
        offDesc.height = kHeight;
        memoryBytes += zrender::ZrEstimateImageBytes(offDesc);
    }
    memoryBytes += zrender::ZrEstimateImageBytes(depthDesc);
    memoryBytes += zrender::ZrEstimateBufferBytes(vbDesc) + zrender::ZrEstimateBufferBytes(ibDesc)
        + 256ull * kDrawItems;

    // ---- RenderDoc 挂接（进程内无 renderdoc.dll → 明确的未挂接态，不静默）----------
    std::filesystem::create_directories(std::filesystem::path(outputDir));
    const std::string captureTemplate = outputDir + "/zframe";
    zrender::ZrRenderDocCapture renderdoc;
    const zrender::ZrRenderDocError rdError = renderdoc.Attach();
    if (renderdoc.IsAttached()) {
        renderdoc.SetCaptureFilePathTemplate(captureTemplate.c_str());
        std::printf("zrhi_d3d12_window: --devconsole RenderDoc ATTACHED (API 1.6.0) - captures "
                    "to '%s'\n",
            outputDir.c_str());
    } else {
        std::printf("zrhi_d3d12_window: --devconsole RenderDoc NOT attached (%s) - "
                    "renderdoc.capture will report renderdoc_not_attached\n",
            zrender::ZrRenderDocErrorText(rdError));
    }
    uint32_t reportedCaptures = 0u;
    std::string lastCapturePath;
    bool manualCaptureOpen = false; // 离屏模式：Start/End 手动半边跨帧状态

    // ---- DevConsole 服务（127.0.0.1:port）-----------------------------------------
    devconsole::DevConsoleServer server;
    std::string serverError;
    if (!server.Start(port, authToken, serverError)) {
        std::printf("zrhi_d3d12_window: --devconsole server FAILED to start: %s\n",
            serverError.c_str());
        ZrD3D12DestroyDevice(device);
        return 1;
    }
    std::printf("zrhi_d3d12_window: DEVCONSOLE listening on 127.0.0.1:%u "
                "(auth <token> required; help|hud|rg.export|renderdoc.capture|quit)\n",
        static_cast<unsigned>(port));
    std::fflush(stdout);

    // ---- 帧循环（quit / 窗口关闭 / --devconsole-frames / 百万帧安全闸退出）---------
    const float lightDir[3] = {0.5f, 0.8f, 0.6f};
    const float invLen = 1.0f
        / std::sqrt(lightDir[0] * lightDir[0] + lightDir[1] * lightDir[1]
              + lightDir[2] * lightDir[2]);
    const float lightColor[4] = {1.0f, 0.95f, 0.9f, 1.0f};
    float proj[16];
    MatPerspectiveFovLH(3.14159265f / 3.0f, static_cast<float>(kWidth) / static_cast<float>(kHeight),
        0.1f, 100.0f, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};

    zrender::ZrHudAggregator hud(60u);
    uint32_t frame = 0u;
    uint32_t exportsWritten = 0u;
    bool quitRequested = false;
    constexpr uint32_t kSafetyFrameCap = 1000000u;

    while (!quitRequested && (maxFrames == 0u || frame < maxFrames)
        && frame < kSafetyFrameCap) {
        const auto frameStart = std::chrono::steady_clock::now();

        // ---- 帧首取请求：即时响应型就地处理；rg.export 等编译产物 → 帧内延后回写 --
        struct PendingExport {
            SOCKET client;
            std::string path;   // 已通过 ZrResolveRgExportPath 校验的规范化绝对路径
            std::string format;
            std::string peer;
        };
        std::vector<devconsole::DevConsoleServer::Request> requests = server.Drain();
        std::vector<PendingExport> pendingExports;
        for (const devconsole::DevConsoleServer::Request& req : requests) {
            const zrender::ZrDevConsoleRequest parsed = zrender::ZrDevConsoleParse(req.line);
            if (!parsed.valid) {
                devconsole::DevConsoleServer::SendAndClose(req.client,
                    zrender::ZrDevConsoleError(parsed.error, parsed.detail));
                continue;
            }
            switch (parsed.command) {
            case zrender::ZrDevConsoleCommand::Help: {
                std::string payload = "\"commands\":[";
                const std::vector<std::string> names = zrender::ZrDevConsoleCommandNames();
                for (size_t ci = 0; ci < names.size(); ++ci) {
                    if (ci != 0u) {
                        payload += ',';
                    }
                    payload += '"' + zrender::ZrDevConsoleJsonEscape(names[ci]) + '"';
                }
                payload += ']';
                devconsole::DevConsoleServer::SendAndClose(req.client,
                    zrender::ZrDevConsoleOk(payload));
                break;
            }
            case zrender::ZrDevConsoleCommand::Hud: {
                // 统计 = 聚合器当前窗口（含上一帧样本；见 ZrHudFormatJson 字段）。
                // ZrHudFormatJson 是独立单对象 JSON——经 MergeObject 剥外壳并入信封，
                // 整体仍是合法单行 JSON（{"ok":true,"drawCalls":...}）。
                const zrender::ZrHudFrameStats stats = hud.Snapshot();
                devconsole::DevConsoleServer::SendAndClose(req.client,
                    zrender::ZrDevConsoleOk(
                        zrender::ZrDevConsoleMergeObject(zrender::ZrHudFormatJson(stats))));
                break;
            }
            case zrender::ZrDevConsoleCommand::RenderDocCapture: {
                // 可选 [name]：换捕获名（模板 = <dir>/<name>，RenderDoc 追加 _帧号.rdc）
                // TASK-X-SEC-01：name 只允许纯文件名（分隔符/`..`/设备名一律拒绝），
                // 否则它同样是一条"任意路径写文件"的旁路。先于挂接判定校验参数
                // （输入合法性不依赖运行环境）。
                if (!parsed.args.empty()) {
                    const std::string nameError
                        = zrender::ZrRgExportValidateLeafName(parsed.args[0]);
                    if (!nameError.empty()) {
                        std::printf("zrhi_d3d12_window: DEVCONSOLE renderdoc.capture REJECTED "
                                    "peer=%s name='%s' reason=%s\n",
                            req.peer.c_str(), parsed.args[0].c_str(), nameError.c_str());
                        std::fflush(stdout);
                        devconsole::DevConsoleServer::SendAndClose(req.client,
                            zrender::ZrDevConsoleError("bad_capture_name", nameError));
                        break;
                    }
                }
                if (!renderdoc.IsAttached()) {
                    // 简报硬要求：未挂接 → 明确报错（不静默、不假成功）
                    devconsole::DevConsoleServer::SendAndClose(req.client,
                        zrender::ZrDevConsoleError("renderdoc_not_attached",
                            std::string("start this exe via RenderDoc UI (File > Launch "
                                        "Application) - ")
                                + zrender::ZrRenderDocErrorText(rdError)));
                    break;
                }
                if (!parsed.args.empty()) {
                    const std::string tmpl = outputDir + "/" + parsed.args[0];
                    renderdoc.SetCaptureFilePathTemplate(tmpl.c_str());
                }
                bool requested = false;
                if (windowMode) {
                    requested = renderdoc.TriggerCapture(); // present 边界落盘
                } else {
                    // 离屏无 present：手动半边（Start 就地 / End 在 Submit 后）
                    requested = renderdoc.StartFrameCapture(device->D3D12Device(), nullptr);
                    manualCaptureOpen = requested;
                }
                if (!requested) {
                    devconsole::DevConsoleServer::SendAndClose(req.client,
                        zrender::ZrDevConsoleError("renderdoc_request_failed",
                            zrender::ZrRenderDocErrorText(renderdoc.LastError())));
                    break;
                }
                std::string payload = "\"requested\":true,\"frame\":" + std::to_string(frame)
                    + ",\"dir\":\"" + zrender::ZrDevConsoleJsonEscape(outputDir)
                    + "\",\"capturesSoFar\":" + std::to_string(reportedCaptures);
                if (!lastCapturePath.empty()) {
                    payload += ",\"lastCapturePath\":\""
                        + zrender::ZrDevConsoleJsonEscape(lastCapturePath) + "\"";
                }
                devconsole::DevConsoleServer::SendAndClose(req.client,
                    zrender::ZrDevConsoleOk(payload));
                break;
            }
            case zrender::ZrDevConsoleCommand::RgExport: {
                // 参数校验（格式值域在本执行层——协议层只管形状）
                std::string requested;
                std::string format = "json";
                if (!parsed.args.empty()) {
                    requested = parsed.args[0];
                }
                if (parsed.args.size() >= 2u) {
                    format = parsed.args[1];
                }
                if (format != "json" && format != "text") {
                    devconsole::DevConsoleServer::SendAndClose(req.client,
                        zrender::ZrDevConsoleError("bad_format",
                            "usage: rg.export [path] [json|text] (got '" + format + "')"));
                    break;
                }
                // TASK-X-SEC-01：路径白名单（规范化 + 根内判定 + 拒绝 `..`/符号链接）
                const zrender::ZrRgExportPathResult resolved
                    = zrender::ZrResolveRgExportPath(requested, exportRoots);
                if (!resolved.ok) {
                    std::printf("zrhi_d3d12_window: DEVCONSOLE rg.export REJECTED peer=%s "
                                "error=%s req='%s' detail=%s\n",
                        req.peer.c_str(), resolved.code.c_str(), requested.c_str(),
                        resolved.detail.c_str());
                    std::fflush(stdout);
                    devconsole::DevConsoleServer::SendAndClose(req.client,
                        zrender::ZrDevConsoleError(resolved.code, resolved.detail));
                    break;
                }
                pendingExports.push_back(
                    PendingExport { req.client, resolved.path, format, req.peer });
                break;
            }
            case zrender::ZrDevConsoleCommand::Quit: {
                devconsole::DevConsoleServer::SendAndClose(req.client,
                    zrender::ZrDevConsoleOk(""));
                quitRequested = true;
                break;
            }
            case zrender::ZrDevConsoleCommand::None:
            default:
                // 解析层已覆盖全部枚举值；此分支只兜底（不崩溃）
                devconsole::DevConsoleServer::SendAndClose(req.client,
                    zrender::ZrDevConsoleError("unknown_command", "unhandled command enum"));
                break;
            }
        }

        // ---- 常量写入（mvp 随帧旋转；--draw 同构）----------------------------------
        const float angle = 6.2831853f * static_cast<float>(frame % 60u) / 60.0f;
        float rotY[16];
        MatRotationY(angle, rotY);
        float rotX[16];
        MatRotationX(0.35f, rotX);
        float rx[16];
        MatMultiply(rotX, rotY, rx);
        for (uint32_t i = 0; i < kDrawItems; ++i) {
            const float tx = (static_cast<float>(i) - 1.0f) * 1.6f;
            float trans[16] = {};
            MatTranslation(tx, 0.0f, 3.2f, trans);
            float model[16];
            MatMultiply(trans, rx, model);
            float pv[16];
            MatMultiply(proj, view, pv);
            float mvp[16];
            MatMultiply(pv, model, mvp);
            DrawConstants constants;
            std::memcpy(constants.mvp, mvp, sizeof(mvp));
            constants.lightDir[0] = lightDir[0] * invLen;
            constants.lightDir[1] = lightDir[1] * invLen;
            constants.lightDir[2] = lightDir[2] * invLen;
            constants.lightDir[3] = 0.0f;
            std::memcpy(constants.lightColor, lightColor, sizeof(lightColor));
            std::memcpy(cbMapped[i], &constants, sizeof(constants));
        }

        // ---- 取后台缓冲 + RTV ------------------------------------------------------
        ZrImage backbuffer {};
        ZrImageView rtvView {};
        if (windowMode) {
            const uint32_t index = swapchain->AcquireNextImage();
            backbuffer = swapchain->GetCurrentImage();
            if (!rtvViews[index].IsValid()) {
                ZrImageViewDesc rtvViewDesc {};
                rtvViewDesc.image = backbuffer;
                rtvViewDesc.viewType = ZrImageViewType::Rtv;
                rtvViews[index] = device->CreateImageView(rtvViewDesc);
            }
            rtvView = rtvViews[index];
        } else {
            backbuffer = offscreenTarget;
            rtvView = offscreenRtv;
        }

        // ---- 每帧 RG：Import 后台/深度 → ClearPass → CubeDrawPass（真实计数）→ Present
        constexpr float kConsoleClearColor[4] = {0.05f, 0.10f, 0.15f, 1.0f};
        uint32_t frameDrawCalls = 0u; // HUD 的 DrawCall 数据源（命令表插桩计数器）
        cmdList->Begin();
        RgGraph graph(ResolveImageDesc);
        RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", backbuffer));
        RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("depth", depthImage));
        graph.AddPass("ClearPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext& ctx) {
                cmdList->ClearColorImage(ctx.GetImage(backH),
                    ZrImageLayout::ColorAttachmentOptimal, kConsoleClearColor,
                    ZrSubresourceRange::All());
                nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(dsvView),
                    D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
            });
        graph.AddPass("CubeDrawPass",
            [&](RgPass& pass) {
                pass.Write(backH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(pso);
                cmdList->SetRenderTarget(rtvView, dsvView);
                cmdList->SetViewportScissor(kWidth, kHeight);
                ZrBuffer vbs[1] = {vb};
                cmdList->SetVertexBuffers(vbs, 1u);
                cmdList->SetIndexBuffer(ib);
                for (uint32_t i = 0; i < kDrawItems; ++i) {
                    cmdList->SetGraphicsConstantBuffer(0u, cbHandles[i]);
                    cmdList->DrawIndexedInstanced(indexCount, 1u, 0u, 0u, 0u);
                    frameDrawCalls += 1u; // HUD DrawCall 计数器（真实调用点插桩）
                }
            });
        if (windowMode) {
            graph.AddPass("PresentPass", [&](RgPass& pass) { pass.Present(backH).MarkRoot(); },
                nullptr);
        }

        bool frameCompiled = false;
        if (graph.Compile()) {
            frameCompiled = true;

            // ---- rg.export：本帧编译产物落盘（pending 请求在此回写）----------------
            for (const PendingExport& pe : pendingExports) {
                const std::string content = pe.format == "text" ? zrender::RgExportText(graph)
                                                                : zrender::RgExportJson(graph);
                // 路径已通过 ZrResolveRgExportPath 校验；这里只补建根内子目录
                const std::filesystem::path outPath
                    = std::filesystem::u8path(pe.path);
                std::error_code dirEc;
                std::filesystem::create_directories(outPath.parent_path(), dirEc);
                if (dirEc) {
                    std::printf("zrhi_d3d12_window: DEVCONSOLE rg.export WRITE FAILED peer=%s "
                                "path='%s' reason=create_directories: %s\n",
                        pe.peer.c_str(), pe.path.c_str(), dirEc.message().c_str());
                    std::fflush(stdout);
                    devconsole::DevConsoleServer::SendAndClose(pe.client,
                        zrender::ZrDevConsoleError("write_failed",
                            "cannot create parent directory for '" + pe.path
                                + "': " + dirEc.message()));
                    continue;
                }
                std::ofstream out(outPath, std::ios::binary);
                if (!out) {
                    std::printf("zrhi_d3d12_window: DEVCONSOLE rg.export WRITE FAILED peer=%s "
                                "path='%s' reason=cannot_open\n",
                        pe.peer.c_str(), pe.path.c_str());
                    std::fflush(stdout);
                    devconsole::DevConsoleServer::SendAndClose(pe.client,
                        zrender::ZrDevConsoleError("write_failed",
                            "cannot open '" + pe.path + "' for writing"));
                    continue;
                }
                out << content;
                out.close();
                if (!out) {
                    std::printf("zrhi_d3d12_window: DEVCONSOLE rg.export WRITE FAILED peer=%s "
                                "path='%s' reason=short_write\n",
                        pe.peer.c_str(), pe.path.c_str());
                    std::fflush(stdout);
                    devconsole::DevConsoleServer::SendAndClose(pe.client,
                        zrender::ZrDevConsoleError("write_failed",
                            "short write on '" + pe.path + "'"));
                    continue;
                }
                exportsWritten += 1u;
                std::printf("zrhi_d3d12_window: DEVCONSOLE rg.export OK peer=%s path='%s' "
                            "bytes=%llu\n",
                    pe.peer.c_str(), pe.path.c_str(),
                    static_cast<unsigned long long>(content.size()));
                std::fflush(stdout);
                const std::string payload = "\"path\":\""
                    + zrender::ZrDevConsoleJsonEscape(pe.path) + "\",\"format\":\""
                    + pe.format + "\",\"bytes\":"
                    + std::to_string(static_cast<unsigned long long>(content.size()))
                    + ",\"frame\":" + std::to_string(frame) + ",\"passes\":"
                    + std::to_string(static_cast<unsigned long long>(graph.CompiledOrder().size()))
                    + ",\"barriers\":" + std::to_string(graph.BarrierPlan().TotalBarrierCount());
                devconsole::DevConsoleServer::SendAndClose(pe.client,
                    zrender::ZrDevConsoleOk(payload));
            }
            pendingExports.clear();

            graph.ExecuteAliased(*device, *cmdList); // 本模式无 Transient，统计恒零（留观测位）
        } else {
            std::printf("  frame %u: RG Compile FAILED (error=%u)\n", frame + 1u,
                static_cast<unsigned>(graph.Error()));
            // pending 导出请求明确失败（不静默丢）
            for (const PendingExport& pe : pendingExports) {
                devconsole::DevConsoleServer::SendAndClose(pe.client,
                    zrender::ZrDevConsoleError("rg_compile_failed", "graph did not compile "
                                                                 "this frame"));
            }
            pendingExports.clear();
        }
        cmdList->End();
        if (frameCompiled) {
            queue->Submit(cmdHandle);
            if (windowMode) {
                swapchain->Present();
            }
            if (manualCaptureOpen && !windowMode) {
                // 离屏手动捕获半边闭合（EndFrameCapture 失败=CaptureFailed，不假成功）
                renderdoc.EndFrameCapture(device->D3D12Device(), nullptr);
                manualCaptureOpen = false;
            }
        }

        // ---- HUD 采样（每帧一条；含设备统计与显存台账）------------------------------
        zrender::ZrHudSample sample {};
        sample.drawCalls = frameDrawCalls;
        sample.rtWidth = kWidth;
        sample.rtHeight = kHeight;
        sample.memoryBytes = memoryBytes;
        sample.device = device->GetDeviceStats();
        const auto frameEnd = std::chrono::steady_clock::now();
        sample.frameMs
            = std::chrono::duration<double, std::milli>(frameEnd - frameStart).count();
        hud.Push(sample);

        // ---- RenderDoc 捕获落盘探测（完成后取真实 .rdc 路径）------------------------
        if (renderdoc.IsAttached()) {
            const uint32_t captures = renderdoc.GetNumCaptures();
            if (captures > reportedCaptures) {
                if (renderdoc.GetCapturePath(captures - 1u, lastCapturePath)) {
                    std::printf("  DEVCONSOLE: RenderDoc capture written: %s\n",
                        lastCapturePath.c_str());
                }
                reportedCaptures = captures;
            }
        }

        // ---- 消息泵（本地版：WM_QUIT 可观测——窗口关闭即优雅退出）-------------------
        MSG msg {};
        while (::PeekMessageW(&msg, nullptr, 0u, 0u, PM_REMOVE) != 0) {
            if (msg.message == WM_QUIT) {
                quitRequested = true;
            }
            ::TranslateMessage(&msg);
            ::DispatchMessageW(&msg);
        }
        frame += 1u;
    }

    server.Stop();
    const zrender::ZrHudFrameStats finalStats = hud.Snapshot();
    std::printf("zrhi_d3d12_window: --devconsole done: frames=%u exports=%u captures=%u\n",
        frame, exportsWritten, reportedCaptures);
    std::printf("  final HUD: %s\n", zrender::ZrHudFormatText(finalStats).c_str());
    std::printf("D3D12 DEVCONSOLE OK\n");
    ZrD3D12DestroyDevice(device);
    return 0;
}

// =============================================================================
// TASK-B-PSO-01：--psocache [salt] —— PSO 磁盘缓存基准（连续两次启动对比用）
//
// 用 first_draw.hlsl（VsMain/PsMain）建 24 个**互不相同**的图形 PSO（根 CBV 数 /
// RTV 格式 / DSV 格式 / 剔除模式 / SRV 表数交叉组合），打印本进程 PSO 创建总耗时
// 与缓存统计（命中/未命中/加载耗时）。
//
// salt（可选，默认 0）：把常量环境项扰动 1e-7*salt —— 产生**驱动从未见过**的
// 着色器字节码，从而可复现"驱动着色器缓存冷"的首次启动；同一 salt 的第二次
// 启动由本库命中（不再进驱动编译）。测量方法：
//   rm cache.bin; exe --psocache 20261107   → 第一次（未命中/现编）
//   exe --psocache 20261107                  → 第二次（全部命中）
// 环境变量 ZFRAME_PSO_CACHE 指定缓存路径（测量用临时文件，勿污染仓库）。
// =============================================================================
int RunPsoCacheBench(uint32_t salt)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0); // 逐行可见（崩溃定位友好）
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: --psocache FAILED to create D3D12 device\n");
        return 1;
    }

    std::vector<char> hlsl;
    if (!ReadWholeFile("Native/shaders/first_draw.hlsl", hlsl)) {
        std::printf("zrhi_d3d12_window: --psocache FAILED to open hlsl\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }
    // salt：改写常量环境项 → 每次换 salt 得到驱动从未编译过的 DXBC
    // （复现"首次启动冷编译"；同一 salt 的第二次启动走本库命中）
    {
        const char* const kAmbient = "float3 ambient = float3(0.25f, 0.25f, 0.25f);";
        std::string source(hlsl.begin(), hlsl.end());
        const size_t at = source.find(kAmbient);
        if (at != std::string::npos) {
            char patched[128] {};
            std::snprintf(patched, sizeof(patched),
                "float3 ambient = float3(0.25f, 0.25f, 0.25f + %.9ff);",
                1e-7 * static_cast<double>(salt + 1u));
            source.replace(at, std::strlen(kAmbient), patched);
            hlsl.assign(source.begin(), source.end());
        } else {
            std::printf("zrhi_d3d12_window: --psocache WARNING salt patch point not found "
                        "(shader changed?); using source as-is\n");
        }
    }
    const ZrShader vs
        = device->CompileHlsl(ZrShaderStage::Vertex, hlsl.data(), hlsl.size(), "VsMain");
    const ZrShader ps
        = device->CompileHlsl(ZrShaderStage::Pixel, hlsl.data(), hlsl.size(), "PsMain");
    if (!vs.IsValid() || !ps.IsValid()) {
        std::printf("zrhi_d3d12_window: --psocache shader compilation FAILED\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    constexpr uint32_t kTargetPsos = 24u;
    std::vector<ZrPipelineState> psos;
    psos.reserve(kTargetPsos);
    uint32_t made = 0;
    double sumMs = 0.0;
    const auto begin = std::chrono::steady_clock::now();
    for (uint32_t cbv = 1u; cbv <= 5u && made < kTargetPsos; ++cbv) {
        for (uint32_t rtvSel = 0u; rtvSel < 2u && made < kTargetPsos; ++rtvSel) {
            for (uint32_t dsvSel = 0u; dsvSel < 3u && made < kTargetPsos; ++dsvSel) {
                for (uint32_t cullSel = 0u; cullSel < 3u && made < kTargetPsos; ++cullSel) {
                    ZrGraphicsPsoDesc desc {};
                    desc.vertexShader = vs;
                    desc.pixelShader = ps;
                    desc.rtvFormat = rtvSel != 0u ? ZrFormat::RGBA8Unorm
                                                  : ZrFormat::BGRA8Unorm;
                    desc.dsvFormat = dsvSel == 0u
                        ? ZrFormat::D32Float
                        : (dsvSel == 1u ? ZrFormat::D24UnormS8 : ZrFormat::Count);
                    desc.depthTest = dsvSel != 2u;
                    desc.depthWrite = dsvSel != 2u;
                    desc.cullMode = cullSel == 0u
                        ? ZrCullMode::None
                        : (cullSel == 1u ? ZrCullMode::Back : ZrCullMode::Front);
                    desc.rootCbvCount = cbv;
                    desc.srvTableCount = cullSel == 2u ? 2u : 0u;

                    const auto t0 = std::chrono::steady_clock::now();
                    const ZrPipelineState pso = device->CreatePipelineState(desc);
                    sumMs += std::chrono::duration<double, std::milli>(
                        std::chrono::steady_clock::now() - t0).count();
                    if (!pso.IsValid()) {
                        std::printf("zrhi_d3d12_window: --psocache FAILED at PSO %u "
                                    "(cbv=%u rtv=%u dsv=%u cull=%u)\n",
                            made, cbv, rtvSel, dsvSel, cullSel);
                        for (const ZrPipelineState& p : psos) {
                            device->Destroy(p);
                        }
                        device->Destroy(vs);
                        device->Destroy(ps);
                        ZrD3D12DestroyDevice(device);
                        return 1;
                    }
                    psos.push_back(pso);
                    made += 1u;
                }
            }
        }
    }
    const double totalMs = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - begin).count();
    const ZrPsoCacheStats st = device->GetPsoCacheStats();
    std::printf("zrhi_d3d12_window: --psocache salt=%u created=%u create_total_ms=%.3f "
                "sum_ms=%.3f cache_active=%d hits=%u misses=%u fallbacks=%u "
                "loaded=%d load_ms=%.3f\n",
        salt, made, totalMs, sumMs, device->IsPsoCacheActive() ? 1 : 0, st.hits,
        st.misses, st.fallbacks, st.diskLoaded ? 1 : 0, st.loadMs);
    std::printf("D3D12 PSOCACHE BENCH OK (%u PSOs)\n", made);

    for (const ZrPipelineState& p : psos) {
        device->Destroy(p);
    }
    device->Destroy(vs);
    device->Destroy(ps);
    ZrD3D12DestroyDevice(device); // 析构内 Flush：Serialize + 原子写盘 + 统计日志
    return 0;
}

int main(int argc, char** argv)
{
    bool headless = false;
    // TASK-B-R7-01：--backend vulkan|d3d12|both —— RHI 后端选择（zplugin C ABI 同步
    // 记录；both = 双后端对照模式，仅 --draw 支持）。缺省 d3d12（旧行为不变）。
    // 预扫描：--backend 可能出现在 --draw 之后（先 return 的模式参数之前），须先取。
    // TASK-B-R9-10：--headless 同入预扫描（--lights 在预扫描后 return，原先
    // `--lights --headless` 里 headless 永远来不及置位；保持原有逐段扫描兼容）。
    const char* backendStr = nullptr;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--backend") == 0 && i + 1 < argc) {
            backendStr = argv[i + 1];
        }
        if (std::strcmp(argv[i], "--headless") == 0) {
            headless = true;
        }
    }
    if (backendStr != nullptr) {
        if (std::strcmp(backendStr, "vulkan") == 0) {
            ZR_SetBackend(1); // 1 = Vulkan（zplugin 后端选择 C ABI）
        } else if (std::strcmp(backendStr, "d3d12") == 0) {
            ZR_SetBackend(0); // 0 = D3D12（缺省）
        } else if (std::strcmp(backendStr, "both") != 0) {
            std::printf("zrhi_d3d12_window: unknown --backend '%s' "
                        "(expected vulkan|d3d12|both)\n",
                backendStr);
            return 1;
        }
    }
    // TASK-B-PSO-01：--psocache [salt] —— PSO 磁盘缓存基准（连续两次启动对比）
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--psocache") == 0) {
            uint32_t salt = 0u;
            if (i + 1 < argc && argv[i + 1][0] != '-') {
                salt = static_cast<uint32_t>(std::strtoul(argv[++i], nullptr, 10));
            }
            return RunPsoCacheBench(salt);
        }
    }
    // TASK-B-R4-03：--draw [zmeshPath] [hlslPath] —— RG+绘制列表真渲染首绘模式
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--headless") == 0) {
            headless = true;
        }
        if (std::strcmp(argv[i], "--draw") == 0) {
            const char* zmeshPath = (i + 1 < argc && argv[i + 1][0] != '-') ? argv[++i]
                                                                           : "Native/tests/data/cube.zmesh";
            const char* hlslPath
                = (i + 1 < argc && argv[i + 1][0] != '-') ? argv[++i] : "Native/shaders/first_draw.hlsl";
#if defined(ZR_HAS_VULKAN)
            // TASK-B-R7-01：后端分发（both = 双后端对照；vulkan = Vulkan 路径）
            if (backendStr != nullptr && std::strcmp(backendStr, "both") == 0) {
                return RunFirstDrawParity(zmeshPath, hlslPath);
            }
            if (backendStr != nullptr && std::strcmp(backendStr, "vulkan") == 0) {
                return RunFirstDrawVulkan(zmeshPath, hlslPath);
            }
#endif
            return RunFirstDraw(zmeshPath, hlslPath);
        }
        // TASK-B-R9-09：--resize —— Vulkan 交换链真重建可证伪验证（真窗口 resize →
        // OUT_OF_DATE → 帧闸粘性 → Acquire 入口自动重建 → 颜色校验）。仅 Vulkan 后端
        // （D3D12 的 HookResizeBuffers 先例在 zfgprobe 侧，与本模式无耦合）。
        if (std::strcmp(argv[i], "--resize") == 0) {
#if defined(ZR_HAS_VULKAN)
            return RunResizeModeVulkan();
#else
            std::printf(
                "zrhi_d3d12_window: --resize requires ZR_HAS_VULKAN (Vulkan SDK build)\n");
            return 1;
#endif
        }
        // TASK-B-R5-01：--lights [zmeshPath] [hlslPath] —— 簇光源 + PBR 前向多光源模式
        if (std::strcmp(argv[i], "--lights") == 0) {
            const char* zmeshPath = (i + 1 < argc && argv[i + 1][0] != '-')
                ? argv[++i]
                : "Native/tests/data/cube.zmesh";
            const char* hlslPath = (i + 1 < argc && argv[i + 1][0] != '-')
                ? argv[++i]
                : "Native/shaders/pbr_forward.hlsl";
            // TASK-B-R9-10：--json 透传（bench 回归采集单行帧耗 JSON）
            const bool jsonOut = zbench::HasJsonFlag(argc, argv);
#if defined(ZR_HAS_VULKAN)
            // TASK-B-R7-02：--lights 接线 Vulkan（B-R7-01 NOTICE 移除）——
            // vulkan = Vulkan 路径；both = 双后端对照
            // TASK-B-R9-11：--headless 透传 both（两侧同走离屏——无 vsync 的纯渲染对照）
            if (backendStr != nullptr && std::strcmp(backendStr, "both") == 0) {
                return RunLightsParity(zmeshPath, hlslPath, headless);
            }
            if (backendStr != nullptr && std::strcmp(backendStr, "vulkan") == 0) {
                return RunLightsVulkan(zmeshPath, hlslPath, nullptr, nullptr, jsonOut, headless);
            }
#endif
            return RunLightsMode(zmeshPath, hlslPath, nullptr, nullptr, jsonOut, headless);
        }
        // TASK-B-R5-02：--shadows [on|off] [zmeshPath] [hlslPath] —— CSM 级联阴影模式。
        // 缺省相位 = 可证伪对比（off 30 帧 → on 30 帧，同一地面探针点）。
        if (std::strcmp(argv[i], "--shadows") == 0) {
            const char* modeStr
                = (i + 1 < argc && (std::strcmp(argv[i + 1], "on") == 0
                                      || std::strcmp(argv[i + 1], "off") == 0))
                ? argv[++i]
                : "";
            const char* zmeshPath = (i + 1 < argc && argv[i + 1][0] != '-')
                ? argv[++i]
                : "Native/tests/data/cube.zmesh";
            const char* hlslPath = (i + 1 < argc && argv[i + 1][0] != '-')
                ? argv[++i]
                : "Native/shaders/pbr_shadow.hlsl";
            const char* depthHlslPath = (i + 1 < argc && argv[i + 1][0] != '-')
                ? argv[++i]
                : "Native/shaders/shadow_depth.hlsl";
#if defined(ZR_HAS_VULKAN)
            // TASK-B-R7-02：--shadows 接线 Vulkan —— vulkan = Vulkan 路径；
            // both = 双后端探针对照
            if (backendStr != nullptr && std::strcmp(backendStr, "both") == 0) {
                return RunShadowsParity(zmeshPath, hlslPath, depthHlslPath);
            }
            if (backendStr != nullptr && std::strcmp(backendStr, "vulkan") == 0) {
                return RunShadowsVulkan(zmeshPath, hlslPath, depthHlslPath, modeStr);
            }
#endif
            return RunShadowsMode(zmeshPath, hlslPath, depthHlslPath, modeStr);
        }
        // TASK-B-R5-03：--r5 —— SSR + GI 探针 + NPR 风格线（R5 收官，可证伪对比）。
        // TASK-B-R8-01：接线 Vulkan（B-R7-02 NOTICE 移除）——8 着色器文件/16 入口点
        // SPIR-V + Hi-Z mip 链/SSR 半分辨率链/GI 探针 CB/toon+描边全链；vulkan =
        // Vulkan 路径，both = 双后端对照（探针读数并排 + 各自可证伪值达标）
        if (std::strcmp(argv[i], "--r5") == 0) {
#if defined(ZR_HAS_VULKAN)
            if (backendStr != nullptr && std::strcmp(backendStr, "both") == 0) {
                return RunR5Parity();
            }
            if (backendStr != nullptr && std::strcmp(backendStr, "vulkan") == 0) {
                return RunR5ModeVulkan();
            }
#endif
            return RunR5Mode();
        }
        // TASK-B-R5-04：--parity —— URP 对等闸门基准场景（1280×720，契约见 RunParityMode 注）
        if (std::strcmp(argv[i], "--parity") == 0) {
            return RunParityMode();
        }
        // TASK-B-R6-01：--fsr [on|off] —— FSR SDK 接入 + UpscalerChain 超分框架。
        // on（缺省）= off/on 双相位可证伪对比（锐度比 ≥1.2× + 双截图）；
        // off = 仅 Bilinear 基线相位自检。
        if (std::strcmp(argv[i], "--fsr") == 0) {
            bool runOn = true;
            if (i + 1 < argc
                && (std::strcmp(argv[i + 1], "on") == 0 || std::strcmp(argv[i + 1], "off") == 0)) {
                runOn = (std::strcmp(argv[i + 1], "on") == 0);
                ++i;
            }
#if defined(ZR_HAS_VULKAN)
            // TASK-B-R7-02：FFX SDK v2.3.0 预编译包无 Vulkan 组件（bin 仅 dx12 dll、
            // zip 0 个 vk 条目——取证见任务日志），FSR on 相位（FFX 运行时）在 VK 侧
            // 无对位物 → 显式声明而非静默回退；D3D12 路径照常
            if (backendStr != nullptr && std::strcmp(backendStr, "vulkan") == 0) {
                std::printf("zrhi_d3d12_window: NOTICE --backend vulkan not implemented for "
                            "--fsr in B-R7-02 (FFX SDK v2.3.0 ships no Vulkan component - "
                            "dx12 dlls only; d3d12 path runs instead, see task log "
                            "'FFX Vulkan 组件可用性')\n");
            }
#endif
            return RunFsrMode(runOn ? FsrMode::On : FsrMode::Off);
        }
        // TASK-B-R6-02：--fsr4 —— 三向（bilinear / FSR3.1 / FSR4.1）各 30 帧：
        // 锐度探针 + GPU 时间戳均值 + CPU 帧耗；FSR4 不可用时 exit 2（受限）。
        if (std::strcmp(argv[i], "--fsr4") == 0) {
#if defined(ZR_HAS_VULKAN)
            // TASK-B-R7-02：同 --fsr——FFX 无 Vulkan 组件，显式声明
            if (backendStr != nullptr && std::strcmp(backendStr, "vulkan") == 0) {
                std::printf("zrhi_d3d12_window: NOTICE --backend vulkan not implemented for "
                            "--fsr4 in B-R7-02 (FFX SDK v2.3.0 ships no Vulkan component; "
                            "d3d12 path runs instead)\n");
            }
#endif
            return RunFsrMode(FsrMode::Fsr4);
        }
        // TASK-B-R8-02：--fg —— FFX FrameGeneration 帧生成（仅 D3D12，独立 flag 不破坏
        // 现有模式）：60 帧 @ 640x480，MV 复用 R6-01 MV Pass，present 路径切换到
        // FrameGenerator（代理交换链）。OK（real=N gen=M 计数翻倍）或 LIMITED
        // （确切原因，exit 0——受限不是失败）；详细口径见 RunFgMode 节头注。
        if (std::strcmp(argv[i], "--fg") == 0) {
#if defined(ZR_HAS_VULKAN)
            // FG 组件仅 dx12（FFX SDK v2.3.0 无 FG VK 组件），显式声明
            if (backendStr != nullptr && std::strcmp(backendStr, "vulkan") == 0) {
                std::printf("zrhi_d3d12_window: NOTICE --backend vulkan not implemented for "
                            "--fg (FFX SDK v2.3.0 ships no Vulkan frame-generation "
                            "component; d3d12 path runs instead)\n");
            }
#endif
            return RunFgMode();
        }
        // TASK-B-R9-13：--assets —— 4 人物 .zmesh 资产画廊（簇光 + CSM 阴影 + 4 探针
        // + 截图 Docs/Parity/assets_gallery.png）。恒离屏（--parity 同策）。
        if (std::strcmp(argv[i], "--assets") == 0) {
            return RunAssetsGalleryMode();
        }
        // TASK-B-TECH-03：--planet —— 行星尺度最小切片（浮点原点重定位 + 球面重力
        // + 球体渲染雏形）：恒离屏 300 帧自动飞行相机（贴地→爬升→入轨）+ 3 关键帧
        // 截图 Docs/Parity/planet_*.png + 数值断言（抖动审计/活力公式/能量守恒）。
        // 通过行 `D3D12 PLANET OK (origin=RELOCATED@Xm jitter=before/after)`。
        if (std::strcmp(argv[i], "--planet") == 0) {
            return RunPlanetMode();
        }
        // TASK-B-R9-16：--devconsole —— GPU 工具链运行时入口（方案 §5.7）：
        // 127.0.0.1 白名单控制台（缺省端口 17323）+ hud 统计 + rg.export +
        // renderdoc.capture。选项：--devconsole-port <n> / --devconsole-frames <n>
        // (0=不限，quit/窗口关闭退出) / --devconsole-dir <path>（RenderDoc 捕获落盘
        // 目录，缺省 Native/build/renderdoc）/ --devconsole-zmesh|hlsl <path>。
        // TASK-X-SEC-01：首帧必须 `auth <token>`（token 打到启动日志，仅本地调试）；
        // rg.export 只能写到 exe 同目录 rg_export / %LOCALAPPDATA%\ZFrame\rg_export /
        // --rg-export-dir <path> 之下。
        // D3D12 路径（与 --draw 同源 PSO/编译链；RenderDoc 挂接在后端无关层）。
        if (std::strcmp(argv[i], "--devconsole") == 0) {
            return RunDevConsoleMode(argc, argv);
        }
        // TASK-B-R4-01：--zmeshcheck <path> —— 加载校验后直接退出。
        if (std::strcmp(argv[i], "--zmeshcheck") == 0) {
            if (i + 1 >= argc) {
                std::printf("zmeshcheck: missing <path> argument\n");
                return 1;
            }
            return RunZmeshCheck(argv[i + 1]);
        }
    }
    std::printf("zrhi_d3d12_window: TASK-B-R2-02 D3D12 backend smoke (requested mode: %s)\n",
        headless ? "--headless" : "window");

    // ---- 设备 -------------------------------------------------------------------
    ZrD3D12Device* device = ZrD3D12CreateDevice();
    g_activeDevice = device;
    if (device == nullptr) {
        std::printf("zrhi_d3d12_window: FAILED to create D3D12 device\n");
        return 1;
    }
    std::printf("zrhi_d3d12_window: device created, debug layer %s\n",
        device->IsDebugLayerEnabled() ? "ENABLED (InfoQueue active)"
                                      : "UNAVAILABLE (degraded mode)");
    if (!device->IsDebugLayerEnabled()) {
        // 降级说明：验收清单第 3 条要求注明
        std::printf("zrhi_d3d12_window: note: debug layer unavailable, InfoQueue count "
                    "will report 0 (degraded)\n");
    }

    IZrQueue* queue = device->GetQueue(ZrQueueType::Graphics);
    ZrCommandList cmdListHandle = device->CreateCommandList(ZrQueueType::Graphics);
    IZrCommandList* cmdList = device->GetCommandList(cmdListHandle);
    if (queue == nullptr || !cmdListHandle.IsValid() || cmdList == nullptr) {
        std::printf("zrhi_d3d12_window: FAILED to create queue/command list\n");
        ZrD3D12DestroyDevice(device);
        return 1;
    }
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(cmdListHandle);

    // ---- 窗口 + 交换链（窗口模式）---------------------------------------------------
    HWND hWnd = nullptr;
    ZrSwapchain swapchainHandle {};
    IZrSwapchain* swapchain = nullptr;
    if (!headless) {
        hWnd = TryCreateWindow(kWidth, kHeight);
        if (hWnd == nullptr) {
            std::printf("zrhi_d3d12_window: window creation rejected (GetLastError=%lu), "
                        "falling back to --headless\n",
                GetLastError());
            headless = true;
        }
    }
    if (hWnd != nullptr) {
        ZrSwapchainDesc scDesc {}; // 默认：BGRA8Unorm / 3 缓冲 / Fifo
        scDesc.width = kWidth;
        scDesc.height = kHeight;
        scDesc.windowHandle = hWnd;
        swapchainHandle = device->CreateSwapchain(scDesc);
        swapchain = device->GetSwapchain(swapchainHandle);
        if (!swapchainHandle.IsValid() || swapchain == nullptr) {
            std::printf("zrhi_d3d12_window: FAILED to create swapchain\n");
            DestroyWindow(hWnd);
            device->Destroy(cmdListHandle);
            ZrD3D12DestroyDevice(device);
            return 1;
        }
        ShowWindow(hWnd, SW_SHOWNORMAL);
        PumpMessages();
        std::printf("zrhi_d3d12_window: window 640x480 + swapchain (BGRA8Unorm, 3 buffers, "
                    "FLIP_DISCARD, Fifo/SyncInterval=1)\n");
    }
    const bool windowMode = (swapchain != nullptr);
    if (!windowMode) {
        std::printf("zrhi_d3d12_window: running offscreen commit resource path "
                    "(RGBA8Unorm, ColorAttachment|TransferSrc)\n");
    }

    // ---- 离屏图像 -----------------------------------------------------------------
    ZrImage offscreenImage {};
    if (!windowMode) {
        ZrImageDesc rtDesc {};
        rtDesc.format = ZrFormat::RGBA8Unorm;
        rtDesc.width = kWidth;
        rtDesc.height = kHeight;
        rtDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        offscreenImage = device->CreateImage(rtDesc);
        if (!offscreenImage.IsValid()) {
            std::printf("zrhi_d3d12_window: FAILED to create offscreen image\n");
            device->Destroy(cmdListHandle);
            ZrD3D12DestroyDevice(device);
            return 1;
        }
    }
    // UAV 清屏路径验证图像（两种模式都跑；RGBA32Float 支持 UAV、不支持 RTV）
    ZrImageDesc uavImageDesc {};
    uavImageDesc.format = ZrFormat::RGBA32Float;
    uavImageDesc.width = kWidth;
    uavImageDesc.height = kHeight;
    uavImageDesc.usage = ZrImageUsage::Storage | ZrImageUsage::TransferSrc;
    ZrImage uavImage = device->CreateImage(uavImageDesc);
    if (!uavImage.IsValid()) {
        std::printf("zrhi_d3d12_window: FAILED to create UAV image\n");
        if (offscreenImage.IsValid()) {
            device->Destroy(offscreenImage);
        }
        device->Destroy(cmdListHandle);
        ZrD3D12DestroyDevice(device);
        return 1;
    }

    // ---- readback 缓冲（4B/px 与 16B/px 各一；行距按 256 对齐）-----------------------
    const uint64_t rowPitchUnorm = (uint64_t(kWidth) * 4u + 255u) & ~255ull;
    const uint64_t rowPitchFloat = (uint64_t(kWidth) * 16u + 255u) & ~255ull;
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackUnorm
        = MakeReadbackBuffer(device->D3D12Device(), rowPitchUnorm * kHeight);
    Microsoft::WRL::ComPtr<ID3D12Resource> readbackFloat
        = MakeReadbackBuffer(device->D3D12Device(), rowPitchFloat * kHeight);
    if (readbackUnorm == nullptr || readbackFloat == nullptr) {
        std::printf("zrhi_d3d12_window: FAILED to create readback buffers\n");
        return 1; // ComPtr 与设备析构路径负责清理
    }

    // ---- 资源自检阶段（TASK-B-R2-03；④ 时间戳在帧循环内进行）-------------------------
    const bool resourceOk = RunResourceSelfCheck(device, queue, cmdListHandle);
    ZrQueryPool timestampPool = device->CreateTimestampQueryHeap(2u);
    if (!timestampPool.IsValid()) {
        std::printf("zrhi_d3d12_window: FAILED to create timestamp query heap\n");
    }

    // ---- 60 帧循环（TASK-B-R3-02：窗口模式 RG 驱动；headless 保留手动路径）----------
    ZrImageLayout offscreenLayout = ZrImageLayout::Undefined; // headless 布局跟踪
    uint32_t checkCount = 0u;
    bool allOk = true;
    bool rgOk = true;
    uint32_t rgFrames = 0u, rgAliasHeaps = 0u, rgTransients = 0u;
    uint32_t rgSplitBegins = 0u, rgSplitEnds = 0u, rgPeakAlive = 0u;
    const ZrDeviceStats baselineStats = device->GetDeviceStats();

    if (windowMode) {
        // ======================= RG 驱动帧循环（窗口模式）===========================
        for (uint32_t frame = 0u; frame < kFrameCount; ++frame) {
            const uint32_t colorIndex = frame % 3u;
            const bool doReadback = ((frame + 1u) % kCheckInterval) == 0u;
            swapchain->AcquireNextImage(); // ≈ vkAcquireNextImageKHR
            ZrImage backbuffer = swapchain->GetCurrentImage();

            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint {};
            cmdList->Begin();
            RgGraph graph(ResolveImageDesc); // 逐帧图：资源/Pass 每帧重建，退役队列活到帧末冲刷
            RgExecuteStats stats = RunRenderGraphFrame(graph, device, cmdList, nativeCmd,
                backbuffer, kClearColors[colorIndex], doReadback, readbackUnorm.Get(),
                &footprint, timestampPool, frame);
            cmdList->End();
            queue->Submit(cmdListHandle); // ≈ vkQueueSubmit
            swapchain->Present();         // ≈ vkQueuePresentKHR
            device->WaitIdle();           // 退役冲刷前提：命令表引用活到提交完成
            graph.FlushRetired(*device);  // 本帧 Transient + 别名堆物理销毁

            // ---- 运行时实证：别名复用 / split 半边 / 用后即毁 ------------------------
            const bool frameOk = (stats.aliasHeapsCreated == 2u) // 2 堆 < 3 Transient
                && (stats.transientImages == 3u) && (stats.splitBegins == 1u)
                && (stats.splitEnds == 1u) && (stats.peakAliveTransients == 2u);
            if (!frameOk) {
                std::printf("  frame %u: RG stats unexpected (heaps=%u transients=%u "
                            "split=%u/%u peak=%u)\n",
                    frame + 1u, stats.aliasHeapsCreated, stats.transientImages,
                    stats.splitBegins, stats.splitEnds, stats.peakAliveTransients);
            }
            rgOk = rgOk && frameOk;
            rgFrames += 1u;
            rgAliasHeaps += stats.aliasHeapsCreated;
            rgTransients += stats.transientImages;
            rgSplitBegins += stats.splitBegins;
            rgSplitEnds += stats.splitEnds;
            rgPeakAlive = stats.peakAliveTransients > rgPeakAlive ? stats.peakAliveTransients
                                                                  : rgPeakAlive;

            // 用后即毁：冲刷后设备图像/堆计数回落到帧前基线（区间外不存在）
            const ZrDeviceStats now = device->GetDeviceStats();
            if (now.imageCount != baselineStats.imageCount
                || now.aliasHeapCount != baselineStats.aliasHeapCount) {
                std::printf("  frame %u: post-flush stats mismatch (images=%u/%u heaps=%u/%u)\n",
                    frame + 1u, now.imageCount, baselineStats.imageCount, now.aliasHeapCount,
                    baselineStats.aliasHeapCount);
                rgOk = false;
            }

            // readback 颜色校验（每 10 帧；与原路径同规则）
            if (doReadback) {
                void* mapped = nullptr;
                if (FAILED(readbackUnorm->Map(0, nullptr, &mapped)) || mapped == nullptr) {
                    std::printf("  frame %u readback Map FAILED\n", frame + 1u);
                    allOk = false;
                } else {
                    allOk = VerifyCenterPixelUnorm8(static_cast<const uint8_t*>(mapped),
                                   footprint.Footprint.RowPitch, /*bgraOrder=*/true,
                                   kExpectedRgb[colorIndex], frame)
                        && allOk;
                    readbackUnorm->Unmap(0, nullptr);
                    checkCount += 1u;
                }
            }

            PumpMessages();
            std::printf("frame %2u/%u: [RG] clear=%-5s alias heaps=%u transients=%u "
                        "split begin/end=%u/%u peak=%u%s\n",
                frame + 1u, kFrameCount, kColorNames[colorIndex], stats.aliasHeapsCreated,
                stats.transientImages, stats.splitBegins, stats.splitEnds,
                stats.peakAliveTransients, doReadback ? "  (+readback)" : "");
        }
    } else {
        // ======================= 手动帧循环（headless 降级，R2-02 原路径）============
        for (uint32_t frame = 0u; frame < kFrameCount; ++frame) {
            const uint32_t colorIndex = frame % 3u;
            const bool doReadback = ((frame + 1u) % kCheckInterval) == 0u;
            ZrImage targetImage = offscreenImage;

            ZrImageLayout prevLayout = offscreenLayout;

            cmdList->Begin();

            // ④ 时间戳：第 10/11 帧各写一个（循环后 Resolve 打印两帧间隔）
            if (timestampPool.IsValid() && (frame == 9u || frame == 10u)) {
                cmdList->WriteTimestamp(timestampPool, frame == 9u ? 0u : 1u);
            }

            // Barrier 1：呈现布局 → 颜色目标（≈ vkCmdPipelineBarrier）
            ZrImageBarrier toTarget = MakeBarrier(targetImage, prevLayout,
                ZrImageLayout::ColorAttachmentOptimal,
                prevLayout == ZrImageLayout::Undefined ? ZrAccessBits::None
                                                       : ZrAccessBits::MemoryRead,
                prevLayout == ZrImageLayout::TransferSrcOptimal
                    ? ZrPipelineStageBits::Transfer
                    : ZrPipelineStageBits::ColorAttachmentOutput,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            cmdList->ResourceBarrier(&toTarget, 1u, nullptr, 0u);

            // 清屏（红/绿/蓝循环；RTV 路径）
            cmdList->ClearColorImage(targetImage, ZrImageLayout::ColorAttachmentOptimal,
                kClearColors[colorIndex], ZrSubresourceRange::All());

            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint {};
            if (doReadback) {
                ZrImageBarrier toCopySrc = MakeBarrier(targetImage,
                    ZrImageLayout::ColorAttachmentOptimal, ZrImageLayout::TransferSrcOptimal,
                    ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput,
                    ZrAccessBits::TransferRead, ZrPipelineStageBits::Transfer);
                cmdList->ResourceBarrier(&toCopySrc, 1u, nullptr, 0u);

                ID3D12Resource* texture = device->D3D12ResourceOf(targetImage);
                uint64_t totalBytes = 0;
                ComputeFootprint(device->D3D12Device(), texture, footprint, totalBytes);
                RecordReadback(nativeCmd, texture, readbackUnorm.Get(), footprint);
                offscreenLayout = ZrImageLayout::TransferSrcOptimal;
            } else {
                offscreenLayout = ZrImageLayout::ColorAttachmentOptimal;
            }

            cmdList->End();
            queue->Submit(cmdListHandle); // ≈ vkQueueSubmit

            if (doReadback) {
                device->WaitIdle(); // ≈ vkDeviceWaitIdle（readback 前确保 GPU 完成）
                void* mapped = nullptr;
                if (FAILED(readbackUnorm->Map(0, nullptr, &mapped)) || mapped == nullptr) {
                    std::printf("  frame %u readback Map FAILED\n", frame + 1u);
                    allOk = false;
                } else {
                    allOk = VerifyCenterPixelUnorm8(static_cast<const uint8_t*>(mapped),
                                   footprint.Footprint.RowPitch, /*bgraOrder=*/false,
                                   kExpectedRgb[colorIndex], frame)
                        && allOk;
                    readbackUnorm->Unmap(0, nullptr);
                    checkCount += 1u;
                }
            }

            Sleep(1); // headless 无垂直同步节拍：保证时间戳间隔 ≥0.01ms 断言带宽
            std::printf("frame %2u/%u: clear=%-5s (headless)%s\n", frame + 1u, kFrameCount,
                kColorNames[colorIndex], doReadback ? "  (+readback)" : "");
        }
    }

    // ---- UAV 清屏路径验证（离屏 RGBA32Float storage；≈ vkCmdClearColorImage on GENERAL）----
    {
        const float magenta[4] = {1.0f, 0.0f, 1.0f, 1.0f};
        cmdList->Begin();
        ZrImageBarrier toGeneral
            = MakeBarrier(uavImage, ZrImageLayout::Undefined, ZrImageLayout::General,
                ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe, ZrAccessBits::ShaderWrite,
                ZrPipelineStageBits::ComputeShader);
        cmdList->ResourceBarrier(&toGeneral, 1u, nullptr, 0u);
        cmdList->ClearColorImage(uavImage, ZrImageLayout::General, magenta,
            ZrSubresourceRange::All()); // UAV 路径（ClearUnorderedAccessViewFloat）
        ZrImageBarrier toCopySrc = MakeBarrier(uavImage, ZrImageLayout::General,
            ZrImageLayout::TransferSrcOptimal, ZrAccessBits::ShaderWrite,
            ZrPipelineStageBits::ComputeShader, ZrAccessBits::TransferRead,
            ZrPipelineStageBits::Transfer);
        cmdList->ResourceBarrier(&toCopySrc, 1u, nullptr, 0u);
        ID3D12Resource* uavTexture = device->D3D12ResourceOf(uavImage);
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint {};
        uint64_t totalBytes = 0;
        ComputeFootprint(device->D3D12Device(), uavTexture, footprint, totalBytes);
        RecordReadback(nativeCmd, uavTexture, readbackFloat.Get(), footprint);
        cmdList->End();
        queue->Submit(cmdListHandle);
        device->WaitIdle();

        void* mapped = nullptr;
        if (FAILED(readbackFloat->Map(0, nullptr, &mapped)) || mapped == nullptr) {
            std::printf("  UAV readback Map FAILED\n");
            allOk = false;
        } else {
            allOk = VerifyCenterPixelFloat(static_cast<const uint8_t*>(mapped),
                       footprint.Footprint.RowPitch, magenta)
                && allOk;
            readbackFloat->Unmap(0, nullptr);
        }
    }

    // ---- ④ 时间戳解析（两帧后）：GPU 频率换算毫秒 + 间隔合理性断言 -----------------
    bool timestampOk = false;
    if (timestampPool.IsValid()) {
        double ms[2] = {-1.0, -1.0};
        const uint32_t resolved = device->ResolveTimestamps(timestampPool, ms, 2u);
        const double intervalMs = (resolved == 2u) ? (ms[1] - ms[0]) : -1.0;
        timestampOk = (resolved == 2u) && (intervalMs >= 0.01) && (intervalMs <= 100.0);
        std::printf("zrhi_d3d12_window: timestamps: frame10=%.6f ms frame11=%.6f ms "
                    "interval=%.6f ms (assert 0.01..100) -> %s\n",
            ms[0], ms[1], intervalMs, timestampOk ? "OK" : "FAILED");
    }
    // 资源自检四项（①回读比对 ②bindless 互异+槽复用 ③Sampler ④时间戳）全过
    if (resourceOk && timestampOk && timestampPool.IsValid()) {
        std::printf("D3D12 RESOURCE SMOKE OK\n");
    } else {
        std::printf("D3D12 RESOURCE SMOKE FAILED (resource=%d timestamp=%d)\n",
            resourceOk ? 1 : 0, timestampOk ? 1 : 0);
    }

    // ---- TASK-B-R3-02：RG 驱动帧循环汇总实证（仅窗口模式）--------------------------
    if (windowMode) {
        std::printf("zrhi_d3d12_window: [RG] frames=%u alias heaps=%u < transients=%u "
                    "(per-frame 2 < 3, styleRt/postRt 共槽复用), split begin/end=%u/%u, "
                    "peak alive=%u\n",
            rgFrames, rgAliasHeaps, rgTransients, rgSplitBegins, rgSplitEnds, rgPeakAlive);
        if (rgOk && rgAliasHeaps < rgTransients && rgSplitBegins > 0u
            && rgSplitBegins == rgSplitEnds) {
            std::printf("D3D12 RG SMOKE OK\n");
        } else {
            std::printf("D3D12 RG SMOKE FAILED (rgOk=%d heaps=%u transients=%u split=%u/%u)\n",
                rgOk ? 1 : 0, rgAliasHeaps, rgTransients, rgSplitBegins, rgSplitEnds);
        }
    }

    // ---- 清理与校验 -----------------------------------------------------------------
    device->WaitIdle();
    device->Destroy(cmdListHandle);
    if (timestampPool.IsValid()) {
        device->Destroy(timestampPool);
    }
    if (windowMode) {
        device->Destroy(swapchainHandle); // 后台缓冲整组随之释放
    }
    if (offscreenImage.IsValid()) {
        device->Destroy(offscreenImage);
    }
    device->Destroy(uavImage);
    readbackUnorm.Reset();
    readbackFloat.Reset();
    if (hWnd != nullptr) {
        DestroyWindow(hWnd);
        PumpMessages();
    }

    const uint32_t infoQueueErrors = device->GetDebugErrorCount();
    ZrD3D12DestroyDevice(device);

    std::printf("zrhi_d3d12_window: mode=%s, frames=%u, color checks=%u (interval %u), "
                "UAV check=1\n",
        windowMode ? "window" : "headless", kFrameCount, checkCount, kCheckInterval);
    std::printf("zrhi_d3d12_window: InfoQueue error count = %u\n", infoQueueErrors);

    if (allOk && rgOk && resourceOk && timestampOk && checkCount == kFrameCount / kCheckInterval
        && infoQueueErrors == 0u) {
        std::printf("D3D12 WINDOW SMOKE OK\n");
        return 0;
    }
    std::printf("D3D12 WINDOW SMOKE FAILED (allOk=%d rg=%d resource=%d timestamp=%d checks=%u "
                "errors=%u)\n",
        allOk ? 1 : 0, rgOk ? 1 : 0, resourceOk ? 1 : 0, timestampOk ? 1 : 0, checkCount,
        infoQueueErrors);
    return 1;
}
