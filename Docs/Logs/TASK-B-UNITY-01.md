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

> 施工代理：TASK-B-UNITY-01 施工（纯原生，未改任何 Unity 侧文件；**未 commit**）。
> 构建配置 Release / MSVC 19.44 / x64，`Tools/build_native.ps1` 从仓库根执行。

### 2.1 改了什么（文件清单）

| 文件 | 性质 | 内容 |
|---|---|---|
| `Native/zrhi/d3d12/zrhi_d3d12_device.h` | 改 | 新增 `ZrD3D12Device::CreateFrom(ID3D12Device*)`、私有 `InitializeRuntime(bool)`、`OwnsDevice()`、自由函数 `ZrD3D12CreateDeviceFrom()`；`m_ownsDevice` 成员 |
| `Native/zrhi/d3d12/zrhi_d3d12_device.cpp` | 改 | 把 `Create()` 尾段（InfoQueue/队列/fence/帧槽环/上传链/描述符堆/队列包装）原样抽成 `InitializeRuntime()`，`Create()` 与 `CreateFrom()` 共用；`CreateSwapchain` 增加 `m_factory == nullptr` 守卫 |
| `Native/zrhi/d3d12/zrhi_d3d12_unity_bridge.h/.cpp` | **新增** | 桥本体：`AttachDevice/BeginFrame/EndFrame/DetachDevice/QueryAttachedDevice`（不含 D3D12 头的头文件 + 实现） |
| `Native/zplugin/zplugin.cpp` | 改 | 五个 `ZR_Unity_*` 薄转发（不 include 任何 D3D12 头） |
| `Native/zplugin/zplugin.h` | 改 | 新 ABI 声明 + 错误码 `ZR_UNITY_ERR_*` + 清屏色常量 `ZR_UNITY_CLEAR_*` + 5 个函数指针 typedef |
| `Native/zplugin/zrender.def` | 改 | 新增 5 个导出（ASCII 注释，未在 EXPORTS 节内放中文） |
| `Native/CMakeLists.txt` | 改 | zrhi 源列表加 `zrhi_d3d12_unity_bridge.cpp`；新增自证 exe 目标 `zrhi_unity_bridge` |
| `Native/tests/zrhi_unity_bridge.cpp` | **新增** | 自证程序（LoadLibrary 取导出 → 自建设备/纹理 → 哨兵色 → 走桥清屏 → 回读） |

无一行改动涉及 `UnityProject/`、`ProjectSettings.asset`、`ZRenderPipeline.cs`、Governor 三件套。
新增 `.cpp/.h` 均 UTF-8 无 BOM + LF（已脚本核验：BOM=False，CRLF=0）。

### 2.2 `CreateFrom` 怎么实现

```
Create()      = 开调试层 → CreateDXGIFactory2 → 枚举非软件适配器
                → D3D12CreateDevice(12.0→11.1→11.0) → InitializeRuntime(true)
CreateFrom(e) = e 为空则返回 nullptr
                → m_device = e（ComPtr 赋值即 AddRef，宿主引用不受影响）
                → 反查 DXGI 工厂：QI IDXGIDevice → GetAdapter → GetParent(IDXGIFactory4)
                → InitializeRuntime(false)
```

- **`Create()` 行为不变**：尾段是整段平移进 `InitializeRuntime()` 的（`d->` 改 `this->`，`return nullptr` 改 `return false`），顺序与原实现逐行一致；`Create()` 里仅多了一次 `if (!InitializeRuntime(debugLayer)) return nullptr;` 的包装，成功/失败语义等价。
- **调试层不启用**：`CreateFrom` 里没有 `D3D12GetDebugInterface/EnableDebugLayer`（这两件事必须在 `D3D12CreateDevice` 之前，事后无法补救）。仅当宿主设备暴露 `ID3D12InfoQueue`（= 宿主已开调试层）时才挂接口，且**不改写**宿主的 break 策略——那是进程级调试策略，属于宿主。
- **工厂不用"新建 + 枚举"**：外部设备可能来自任意适配器，新建工厂再枚举有可能选到不一致的适配器；改为从设备反查其真实适配器与父工厂。

### 2.3 内部"必须自己创建"的假设 —— 梳理与结论

| 假设 | 结论 | 说明 |
|---|---|---|
| 设备本体必须自建 | **已解除** | `CreateFrom` 直接包外部设备；指针恒等已证（§2.5） |
| DEBUG layer 由本库在建设备前开启 | **做不到** | 创建期开关，事后无法为既有设备打开；宿主没开则 `GetDebugErrorCount()` 恒 0 |
| 适配器 / feature level / DRED / 驱动参数 | **做不到** | 全部由宿主决定，包装层无法影响 |
| DXGI 工厂 / 交换链 | **可做，但走反查** | `m_factory` 由设备反查；反查失败（设备不来自 DXGI 适配器）时 `CreateSwapchain` 返回无效句柄并 assert，其余功能不受影响。**外部设备 + 交换链这条路径本次未验证** |
| 命令队列 | **可做** | 在宿主设备上新建自己的 DIRECT 队列，合法；本桥与宿主队列**无跨队列同步**（见 §2.7 限制 ②） |
| 描述符堆 / 帧槽环 / fence / 上传链 | **可做** | 全部从宿主设备分配；代价见 §2.7 限制 ① |
| 库内资源句柄表（图像/缓冲） | **未参与** | 桥直接持有外部 `ID3D12Resource*`，不注册进 `ZrD3D12Device` 句柄表，故 `~ZrD3D12Device` 的"表必须为空"断言成立 |

### 2.4 最小 C ABI（实际导出）

```
ZR_Unity_AttachDevice(void* d3d12Device) -> int
ZR_Unity_BeginFrame(void* backBufferTexture, uint32_t w, uint32_t h) -> int
ZR_Unity_EndFrame(void) -> int
ZR_Unity_DetachDevice(void) -> int                       // 追加：宿主销毁设备前解包
ZR_Unity_DebugGetAttachedDevice(void** outDevice) -> int // 追加：自证/诊断
```

错误码：`0` ok / `-1` 空指针 / `-2` 未 Attach / `-3` 设备包装失败 / `-4` 纹理非法 / `-5` 帧状态错误。
清屏色固定 `ZR_UNITY_CLEAR = (13,26,38,255)`（与 `--draw` 演示背景同值，便于直接比对）。

`BeginFrame` 为纹理建 RTV（`pDesc=nullptr` → 按资源格式 mip0/全层）；`EndFrame` 记录
`ClearRenderTargetView` → `Close` → `ExecuteCommandLists`（本桥队列）→ `WaitIdle` 排空。
**不插屏障**：目标须处于 COMMON（D3D12 首次以 RTV 使用时的隐式提升；命令表结束隐式衰减回 COMMON）。

### 2.5 回读像素证据（本任务成立的关键）

`Native/build/bin/Release/zrhi_unity_bridge.exe`（全新进程，不依赖 Unity）：

1. `LoadLibrary(zrender.dll)` + `GetProcAddress` 取 5 个符号 —— 取不到即失败（导出表硬前提）。
2. 自建 `ID3D12Device`（`CreateDXGIFactory2` → 非软件适配器 → `D3D12CreateDevice`，与 Unity 同路径）。
3. 自建 256×256 `RGBA8Unorm + ALLOW_RENDER_TARGET` 纹理（COMMON）。
4. 用**宿主自己的队列**清成哨兵色 (255,0,255) 并回读 → 清屏前基线。
5. `AttachDevice` → `DebugGetAttachedDevice` → `BeginFrame` → `EndFrame`。
6. 再用**宿主自己的队列** `CopyTextureRegion → READBACK 缓冲 → Map` 回读整帧。

实测输出（`TempLogs/TASK-B-UNITY-01/unity_bridge_run1.log`）：

```
host adapter: AMD Radeon 780M Graphics (LUID 00000000:0000C832)
host device (Unity role): 000001F0D675C540
before(BEGIN ) pixel(128,128) RGB=(255,0,255) A=255      <- 哨兵色，与期望色不同
wrapped device: 000001F0D675C540                          <- 与 host device 同一指针
after (BRIDGE) pixel(0,0)     RGB=(13,26,38) A=255
after (BRIDGE) pixel(128,128) RGB=(13,26,38) A=255
after (BRIDGE) pixel(255,255) RGB=(13,26,38) A=255
UNITY BRIDGE OK (0 failure(s))
```

- **回读到的 RGB = (13,26,38)**，来自 `ID3D12Resource::Map`（READBACK 堆、真实
  `CopyTextureRegion`），**不是**估算/推算；并且对整帧 256×256 = 65536 个像素逐点扫描，
  全部等于 (13,26,38,255)（任何一个不符即打印首个失配坐标并失败）。
- **对比背景色**：期望色 `ZR_UNITY_CLEAR` 就是 `--draw` 演示背景 `clear≈(13,26,38)`；
  回读值与背景色**逐通道完全相等**（无 ±1 误差），且清屏前该处是 (255,0,255) 哨兵色
  → 说明像素是被这次 `ClearRenderTargetView` 改写的，不是"本来就是背景色"。
- **为什么能证明用的是传入设备**（两条独立证据）：
  ① `ZR_Unity_DebugGetAttachedDevice` 返回 `000001F0D675C540` == 进程内自建设备指针
  `000001F0D675C540`（COM 同一性规则下，这就是同一个设备对象）；
  ② D3D12 资源不可跨设备使用——被清屏的纹理是本进程用该设备创建的，若内部另建了设备，
  `CreateRenderTargetView` / 清屏不可能作用到这张纹理上，回读也就不会是 (13,26,38)。
- 附带断言：`AttachDevice(nullptr)=-1`、未 Attach 时 `EndFrame=-2`、`BeginFrame(nullptr)=-1`、
  重复 `BeginFrame=-5`、`Detach` 后 `BeginFrame=-2`、**Detach 后宿主仍能用自己的队列清屏并回读**
  （宿主设备未被桥销毁）。共 15 项 PASS、0 项 FAIL。

### 2.6 回归证据

**`--draw` 60 帧（`TempLogs/TASK-B-UNITY-01/draw_after.log`）**：退出码 0

```
frame 10 readback center RGB=(27,60,141), clear≈(13,26,38) -> CUBE (first draw visible)
frame 20 readback center RGB=(151,146,141), clear≈(13,26,38) -> CUBE (first draw visible)
frame 30/40/50/60 同样判 CUBE
[FIRST DRAW] frames=60 center checks=6 passed=6 undeclared uses=0
device removed reason = 0x00000000 (S_OK); InfoQueue error count = 0
D3D12 FIRST DRAW OK
```

与改动前基线**逐字段相同**（帧 10 = (27,60,141)、帧 20 = (151,146,141)，60/60 帧）。

**原生单测**：改动前 **311 用例 / 776716 断言**，改动后 **311 用例 / 776716 断言**，
`Status: SUCCESS!`（本任务未新增 doctest 用例——自证走独立 exe，因为它必须在运行期
从 dll 导出表取符号，而链接静态库证明不了导出表）。注：简报写的"301 用例"是旧数字，
仓库当前基线本就是 311，本条按"不减少"判定通过。

**导出表**（`dumpbin /exports`）：改前 23 个（`ZK_*` 12 + `ZR_*` 11），改后 **28 个**，
新增 5 个 `ZR_Unity_*` 全部在表内，序号 21–25。

### 2.7 没做成 / 已知限制（如实列出，未硬编）

1. **描述符堆与命令链路的固定开销**：包装时无条件在宿主设备上分配 1×16384 项
   SHADER_VISIBLE CBV_SRV_UAV 堆 + 3×128 项（RTV/DSV/Sampler）堆 + 8 个命令分配器
   + 上传链路。对 Unity 宿主是可测的显存/对象开销（约 0.6MB 描述符），且即便只用
   `BeginFrame/EndFrame` 也会付这份钱。**未做懒加载**。
2. **跨队列同步未处理**：桥用自己的 DIRECT 队列提交清屏，`EndFrame` 只等自己这条队列。
   若宿主在同一纹理上另有自己的队列工作，正确性依赖宿主自行 fence 同步。真接 Unity 时
   应改为使用宿主的图形队列（或显式跨队列 fence）。
3. **不做资源状态跟踪**：`BeginFrame` 假定目标处于 COMMON。Unity 后台缓冲常处于
   `RENDER_TARGET`/`PRESENT` 等显式状态，此时本桥不插屏障——属"最小实现"边界，
   第 3 段必须补状态记录或由托管侧先转回 COMMON。
4. **多重采样目标不支持**（`SampleDesc.Count != 1` 直接返回 -4）。
5. **调试层不可用**：本机未安装 Graphics Tools，`D3D12GetDebugInterface` 不可用
   （`--draw` 同样打印 `debug layer UNAVAILABLE (degraded)`，与改动前一致）。因此清屏
   路径**没有**经过 D3D12 验证层检查；正确性由回读像素证明，不是由验证层 0 错误证明。
6. **外部设备 + 交换链未验证**：`CreateFrom` 会反查 DXGI 工厂，`CreateSwapchain` 理论上
   可用，但本次未跑（不在本任务判据内）。
7. **进程内单实例**：桥是全局单例，同时只包一个设备；重复 `AttachDevice` 会替换旧包装。
   宿主持有多个 `ID3D12Device` 的场景不支持。

### 2.8 现场

- 未 `git commit`；未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`。
- `git status` 只剩 6 个 native 改动 + 3 个 native 新增文件；**`UnityProject/` 无 diff**
  （构建脚本第 4 步会把 `zrender.dll` 拷进 `UnityProject/Assets/Plugins/ZRender/`，
  这是脚本的既有行为，已用 `git checkout --` 还原该二进制，保持 Unity 侧零改动）。
- 构建日志/回读日志/导出表保存在 `TempLogs/TASK-B-UNITY-01/`（`TempLogs/` 已被
  `.gitignore` 忽略），未在仓库内留临时文件。

### 2.9 复现命令

```bash
# 构建（必须从仓库根；Bash 侧不要转调 powershell）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1
# 自证（回读像素）
Native/build/bin/Release/zrhi_unity_bridge.exe
# 回归
Native/build/bin/Release/zrhi_d3d12_window.exe --draw
Native/build/bin/Release/zrender_test.exe
dumpbin //nologo //exports Native/build/bin/Release/zrender.dll | findstr ZR_Unity
```

---

## 验收清单（供验收代理）

- [ ] V1 `Create()` 原路径不变：`zrhi_d3d12_window.exe --draw` 跑满 60 帧，帧 10/20 回读判 CUBE
- [ ] V2 `CreateFrom(外部设备)` 存在且真的用了传入设备（不是偷偷自己建）
- [ ] V3 **回读像素证据**：清屏颜色正确作用在**外部纹理**上（给出实际 RGB）
- [ ] V4 `zrender.def` 含新导出，符号可从 dll 导出表查到
- [ ] V5 原生 301 用例不回归（允许新增）
- [ ] V6 未改 Unity 侧 / ProjectSettings；未 commit；现场干净

## 验收结论（验收代理写）

> 验收代理独立复跑（与施工非同一代理），**不采信第 2 节自述**。构建：`Tools/build_native.ps1`
> 从仓库根复跑（Release / MSVC 19.44 / x64，`ALL GREEN`）。全部证据日志在
> `TempLogs/TASK-B-UNITY-01/`（`audit_*.log`、`mutation/`、`audit_imports_restored.txt`）。
> 清屏/回归均在**施工源码还原后的最终二进制**上重跑（见 V3 的变异还原证据）。

### 逐条结论

| 项 | 结论 | 证据（一行） |
|---|---|---|
| V1 | **PASS** | `audit_draw_restored.log`：`frames=60 center checks=6 passed=6`，帧 10 `RGB=(27,60,141)`、帧 20 `RGB=(151,146,141)`，均判 CUBE，退出码 0。 |
| V2 | **PASS** | `audit_bridge_restored.log`：清屏前中心 `RGB=(255,0,255)` 哨兵（非背景色）→ 清屏后 (0,0)/(128,128)/(255,255) 全为 `RGB=(13,26,38) A=255`，整帧 256×256 逐像素扫描一致，`UNITY BRIDGE OK (0 failure(s))`。 |
| V3 | **PASS with 受限** | 最终 `zrender.dll` 的导入表中**没有** `D3D12CreateDevice`（d3d12.dll 只导入 `D3D12SerializeRootSignature`，`grep -c` = 0）；源码中唯一调用在 `zrhi_d3d12_device.cpp:103` 的 `Create()` 内，`CreateFrom()`（:122–147）只做 `m_device = external`。**施工的“指针恒等”证据本机不可采信**（见下），已用二进制导入表 + 变异阳性对照替代。 |
| V4 | **PASS** | `dumpbin /exports`：28 个（12 `ZK_*` + 16 `ZR_*`）；5 个 `ZR_Unity_*` 齐全（序号 21–25）；以 `git show HEAD:Native/zplugin/zrender.def` 的 23 个旧符号为基线，缺失集合为空、新增恰为 5 个。 |
| V5 | **PASS** | `audit_zrender_test_restored.log`：`test cases: 311 \| 311 passed`，`assertions: 776716 \| 776716 passed`，`Status: SUCCESS!`，退出码 0（不降）。 |
| V6 | **PASS** | `git status --porcelain` 仅 7 个 native 改动 + 3 个 native 新增 + 本任务日志；`grep UnityProject` = 0 行；`HEAD` 仍为 `18d6706 chore: B-UNITY-01 派发`（未 commit）；`TASKS.md`/`HANDOFF.md`/`WORKLOG.md` 均未改；被构建脚本覆盖的 `UnityProject/Assets/Plugins/ZRender/zrender.dll` 已还原为原哈希 `7209707596b59e37…`。 |
| V7 | **PASS with 受限** | 两条限制严重度已判定：**③ 硬阻断 T2**，**② 阻断 T2 的真实合成**（详见下）。 |

### V3 独立取证说明（含一条对施工证据的纠正）

施工 §2.5 的证据 ① 是“`ZR_Unity_DebugGetAttachedDevice` 返回指针 == 宿主自建设备指针”。
本机实测该证据**恒真、无鉴别力**：独立探针 `mutation/device_identity.cpp` 在同一进程连续调用
`D3D12CreateDevice` 三次，`d1 == d2 == d3`（本机 D3D12 运行时对同进程返回同一设备对象）。
因此“指针相等”不能证明“没有偷偷自建设备”。

验收代理改用两条真正独立的证据：

1. **二进制导入表（决定性）**：最终 `zrender.dll` 从 `d3d12.dll` 只导入
   `D3D12SerializeRootSignature`，**完全没有** `D3D12CreateDevice` 导入
   （`audit_imports_restored.txt`）。这是因为 `Create()` 未被任何导出入口引用、被链接器
   裁掉；若 `CreateFrom` 路径调用了 `D3D12CreateDevice`，该导入必然保留。
2. **变异阳性对照（证明上述判据可证伪）**：向 `CreateFrom` 临时注入一次
   `D3D12CreateDevice`（AUDIT MUTATION），重建后 DLL 立刻出现该导入（序号 101），
   IAT 计数探针在 `AttachDevice` 期间计到 **1 次**调用并判 FAIL
   （`mutation/iat_probe_mutant.log`：`AttachDevice rc=0, D3D12CreateDevice calls during Attach=1`）。
   还原源码重建后该导入消失、计数为 0。变异已还原：源码 SHA-256 回到
   `90d891f3a3fe27476360d8e637dab55611c76ea491dbb651b8f7145a06c5c862`（见
   `mutation/source_before.sha256`，`sha256sum -c` = OK），文件中无 `AUDIT` 残留。
3. 行为侧佐证：被清屏的纹理由宿主设备创建、由宿主队列回读；清屏前 (255,0,255)、
   清屏后 (13,26,38)，证明清屏确实作用在这张外部纹理上。

### V7 两条限制的严重度（是否阻断 T2，明确回答）

**③ `BeginFrame` 假定纹理处于 COMMON、不插屏障 —— 硬阻断 T2，必须先改。**

- Unity D3D12 的 flip-model 后台缓冲在帧边界通常是 `PRESENT`；`ClearRenderTargetView`
  要求 `RENDER_TARGET`（或 COMMON 的隐式提升）。`PRESENT → RENDER_TARGET` 的隐式提升
  **不成立**，当前实现直接清屏属于非法状态使用：有调试层会报 state mismatch；
  本机未装 Graphics Tools（`--draw` 也打印 `debug layer UNAVAILABLE`），所以会**静默**
  地清不掉、花屏、甚至设备移除。若传入时是 `RENDER_TARGET`（渲染中），状态虽兼容，
  但桥在自己的队列上执行，仍与 Unity 队列存在并发/状态归还问题。
- 结论：**T2 不能原样把 Unity backbuffer 传进来**；T2 必须先做
  `PRESENT/RENDER_TARGET → RENDER_TARGET`（清完再转回 `PRESENT`）的状态转换，且转换必须
  发生在与 Unity 相同的队列语义下。这是 T2 的前置必做项（工作量不大，但不可省）。
- **证据性质**：以上 PRESENT/RENDER_TARGET 的行为判定依据是 D3D12 资源状态规则 + 桥的代码
  事实（无屏障、假定 COMMON）；本任务没有交换链用例，**未做该场景的像素实测**（施工 §2.7 ⑥
  也声明外部设备 + 交换链未验证）。这不是“已验证”，是必须由 T2 实测确认的前置风险。

**② 桥用自己的 DIRECT 队列、跨队列同步未处理 —— 阻断 T2 的正确每帧合成，不能绕过。**

- `AttachDevice` 在宿主设备上新建自己的 DIRECT 队列（`zrhi_d3d12_device.cpp:166–171`），
  `EndFrame` 只 `WaitIdle` 自己这条队列（`zrhi_d3d12_unity_bridge.cpp:145`），
  与 Unity 的图形队列之间**没有任何 fence**。若 Unity 队列上仍有对该纹理的未完成工作，
  或 Unity 随后 Present/继续渲染，会出现读写竞态：花屏、清屏被覆盖、状态错乱，严重时
  device removed。`EndFrame` 的 `WaitIdle` 只保证“桥的活干完了”，**不保证 Unity 的活干完了**。
- 结论：**阻断 T2 的“正确合成”**；只做一次性清屏演示也需要外部串行化（确保 Unity 队列空闲），
  不建议作为 T2 方案。T2 应直接改用 Unity 的 graphics queue/native command buffer
  （`IUnityGraphicsCommandBuffer`）或显式跨队列 fence，而不是复用桥自建队列。
- **证据性质**：队列独立与 `WaitIdle` 只等自身队列是**代码事实**（已读
  `zrhi_d3d12_device.cpp:166–171`、`zrhi_d3d12_unity_bridge.cpp:145`）；跨队列竞态本身
  **未做并发实测**（本任务只有单线程单纹理用例）。

其余 5 条：① 约 0.6MB 描述符固定开销，对 Unity 可接受（建议 T2 再做懒加载）；
④ MSAA 后台缓冲不适用于本桥，Unity 默认 backbuffer 为 1x，非阻断；⑤ 无调试层会放大
③/② 的静默失败风险，T2 联调建议装 Graphics Tools；⑥ 外部设备+交换链未验证，但 T2 由
Unity 持有交换链，本条不阻断；⑦ 单设备单例与 Unity 单 `ID3D12Device` 场景一致，非阻断。

### 最终裁决

**TASK-B-UNITY-01：PASS**（任务范围内的原生桥、外部纹理清屏、导出表、回归与现场均达标；
V3 的证据质量已修正，V7 的限制已如实披露）。

**但不能据此直接开 T2 接线**：V7-③ 是 T2 的硬阻断项（必须先补状态转换），V7-② 阻断 T2 的
真实每帧合成（必须改用 Unity 队列或显式跨队列 fence）。T2 的任务简报应把这两项列为前置
验收条件，否则“接上就花屏/黑屏”是必然结果，而不是偶发问题。

### 验收现场

- 未 `git commit`；未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`；`UnityProject/` 零 diff。
- 施工源码无残留变异（SHA-256 与变异前一致），最终二进制由还原后的源码重建。
- 验收新增证据仅在 `TempLogs/TASK-B-UNITY-01/`（已被 `.gitignore` 忽略）；未做删除式清理。

