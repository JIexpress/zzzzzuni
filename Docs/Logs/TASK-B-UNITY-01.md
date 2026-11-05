# TASK-B-UNITY-01 —— ZRender 接入 Unity（第一步：原生侧接受外部 D3D12 设备）

> 三段拆解的第 1 段。**本任务只做原生侧**，不碰 Unity。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 背景（主控已实测，别重复摸索）

**ZRender 内核是完全可用的**，实测证据（`zrhi_d3d12_window.exe --draw`，仓库根运行）：

```
--draw device created
--draw window 640x480 + swapchain (BGRA8Unorm, FLIP DISCARD, Fifo)
--draw zmesh: vertices=24 indices=36 (interleaved 32B/vert)
--draw shaders compiled (runtime D3DCompile, SM5.0 DXBC)
--draw draw list: items=3 batches=1 (state-clustered)
frame 10 readback center RGB=(27,60,141), clear≈(13,26,38) -> CUBE (first draw visible)
frame 20 readback center RGB=(151,146,141), clear≈(13,26,38) -> CUBE (first draw visible)
```

**问题在于它接不进 Unity**，三个硬事实：

| 事实 | 位置 | 后果 |
|---|---|---|
| `ZrD3D12CreateDevice()` **无参数**，自己 `D3D12CreateDevice` | `Native/zrhi/d3d12/zrhi_d3d12_device.cpp:51` | 无法用 Unity 的 `ID3D12Device` |
| `zrender.def` 只导出 23 个符号（`ZK_*` 12 + `ZR_*` 11），**无任何渲染入口** | `Native/zplugin/zrender.def` | Unity 调不到 device/pass/draw |
| `ZRenderPipeline.Render()` 是空的（注释写"v0 占位，真渲染后续任务接入"） | `Assets/ZRender/Runtime/ZRenderPipeline.cs` | 切到 ZRender 就是黑屏 |

另外：`ProjectSettings.asset` 里 Windows 的图形 API 是**自动**（实际选 D3D11），
要走 ZRender 得让 Windows 用 D3D12。这一项归第 3 段处理，**本任务不要改 ProjectSettings**。

## 本任务目标（严格限定）

让原生侧能**接受外部传入的 `ID3D12Device`**，并**对一张外部传入的纹理清屏**。
不做 Unity 插件、不改 PlayerSettings、不碰 C# 代码。

### 要做的三件事

**1. `ZrD3D12Device` 支持外部设备**

- 新增 `static ZrD3D12Device* CreateFrom(ID3D12Device* external)`（或等价入口），
  内部跳过 `D3D12CreateDevice` 与 debug layer 启用，直接包装外部设备。
- `Create()` 保持原行为（自己建设备），**`zrhi_d3d12_window.exe` 的 60 帧演示不得受影响**。
- 注意：设备内部若有"必须由自己创建"的假设（如队列/描述符堆的初始化路径），要一并梳理；
  若某些部分无法用外部设备完成，**在报告里如实列出**，不要硬编。

**2. 新增最小 C ABI（加到 `zrender.def` 与对应头）**

至少这三个（命名可微调，但要 C 兼容、无 C++ 类型泄漏）：

```
ZR_Unity_AttachDevice(void* d3d12Device)                  -> int  // 0=ok
ZR_Unity_BeginFrame(void* backbufferTexture, uint32_t w, uint32_t h) -> int
ZR_Unity_EndFrame()                                       -> int
```

语义最小化：`AttachDevice` 包装设备；`BeginFrame` 为传入纹理建 RTV；
`EndFrame` 执行 `ClearRenderTargetView`（颜色可固定，或加参数）。**先求通，不求全。**

**3. 一个可脱离 Unity 的原生验证**

这是本任务能不能算完成的关键——**必须能自己证明桥是通的**，不能只靠"编译通过"。

做法建议（任选其一，说明选择的理由）：
- 在被测进程里**自己创建** `ID3D12Device`（模拟 Unity 的角色），`CreateFrom` 包装它，
  再建一张 offscreen 纹理，走 `AttachDevice → BeginFrame → EndFrame`，**回读像素**验证颜色正确；
- 或复用现有 `zrhi_d3d12_window` 的设备创建路径做同样的事。

回读证据要求：给出实际 RGB 值（形如 `RGB=(x,y,z)`），并说明该值来自 `ReadPixels`/`Readback` 的真实现。

### 判据

- `Create()` 行为不变，`zrhi_d3d12_window.exe --draw` 仍能跑满 60 帧且帧 10/20 回读仍判 CUBE；
- `CreateFrom(外部设备)` 可用，且有**回读像素**证明清屏真的作用在那张外部纹理上；
- 新增导出现在 `zrender.def` 里，且 `zrender_test.exe` 的导出自检（若有）不红；
- 原生测试 301 用例不回归（**允许新增用例**，不允许减少）；
- **没有**改动 Unity 侧任何文件、`ProjectSettings.asset`、`ZRenderPipeline.cs`。

### 环境纪律（真实踩过）

- 原生构建：`Tools/build_native.ps1`（**必须从仓库根跑**，脚本依赖 CWD 找 SPIR-V）
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- **PATH 上的 `cmake` 是 MSYS2 版本，对某些目录无写权限**，`build_native.ps1` 会探测并回退 VS cmake；
  不要绕过脚本手动 cmake，否则会踩 `pkgRedirects` 权限错误
- 本机有 Vulkan SDK，**不要**设假 `VULKAN_SDK`（那会少编 32 个 Vulkan 用例）
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- **不碰 Unity 侧**（本任务纯原生）；不动 Governor 三件套、Unity 安装目录
- `.cpp/.h` 无 BOM、LF

---

## 施工报告（施工代理写）

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 `Create()` 原路径不变：`zrhi_d3d12_window.exe --draw` 跑满 60 帧，帧 10/20 回读判 CUBE
- [ ] V2 `CreateFrom(外部设备)` 存在且真的用了传入设备（不是偷偷自己建）
- [ ] V3 **回读像素证据**：清屏颜色正确作用在**外部纹理**上（给出实际 RGB）
- [ ] V4 `zrender.def` 含新导出，符号可从 dll 导出表查到
- [ ] V5 原生 301 用例不回归（允许新增）
- [ ] V6 未改 Unity 侧 / ProjectSettings；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
