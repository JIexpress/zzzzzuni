# TASK-B-UNITY-02 —— ZRender 接入 Unity（第二步：渲染到共享纹理 + 管线接线）

> 三段拆解的第 2 段。第 1 段（B-UNITY-01）已完成，原生侧能接受外部 `ID3D12Device`。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 背景（已验收的事实，别重复摸索）

**B-UNITY-01 已完成（验收 PASS）**：
- `ZrD3D12Device::CreateFrom(ID3D12Device* external)` 可用，`Create()` 原路径不变
- `zrender.def` 导出 23 → **28**，新增 5 个 `ZR_Unity_*`（`AttachDevice` / `BeginFrame` / `EndFrame` / 调试与错误类）
- 自证：宿主自建设备 → 外部纹理清屏，回读整帧 256×256 = `(13,26,38)`，清屏前是哨兵 `(255,0,255)`

**当前状态**：`ZRenderPipeline.Render()` 仍是空实现（只 `context.Submit()`），
`ProjectSettings` 的 Windows 图形 API 是自动（实际 D3D11）。

## 架构决策（主控已定，**不要改**）

验收明确指出「直接画 Unity backbuffer」有两个硬阻断：**backbuffer 状态**（PRESENT→RENDER_TARGET 无隐式提升）
和**跨队列同步**（桥自建 DIRECT 队列，`WaitIdle` 只管自己）。所以本任务**不走那条路**。

**采用方案：ZRender 渲染到自己的共享纹理，Unity 采样显示。**

```
原生：CreateFrom(UnityDevice) → 建共享纹理(BGRA8) → RenderGraph 渲染 → 转到 SRV → fence 等完成
                              ↓ 导出 NT 共享句柄
Unity：CreateExternalTexture(handle) → CommandBuffer.Blit(该纹理 → CameraTarget) → Submit
```

状态与同步全部关在原生内部（它自己建、自己转、自己等），Unity 只做「采样一张外部纹理再贴屏」，
不需要碰 backbuffer 状态，也不需要跨队列 fence。

## 任务

### 1）原生侧：共享纹理 + 渲染 + 导出

- 在 `CreateFrom` 已包装的设备上创建一张 **BGRA8** 纹理（尺寸由调用方指定，建议 1280×720 或 640×480）
- 复用现有 `--draw` 的渲染路径（zmesh cube + `first_draw.hlsl` + RenderGraph），**渲染到这张纹理**
- 渲染完成后把纹理转到 `PIXEL_SHADER_RESOURCE`（Unity 采样要这个状态），并 fence 等待完成
- 新增/扩展 C ABI（加到 `zrender.def`）：
  ```
  ZR_Unity_CreateSharedTarget(uint32_t w, uint32_t h) -> int   // 0=ok
  ZR_Unity_GetSharedTargetHandle(uint64_t* outHandle) -> int   // NT handle (D3D12)
  ZR_Unity_RenderFrame() -> int                                // 渲染一帧到该纹理
  ZR_Unity_ReleaseSharedTarget() -> void
  ```
- 必须保证每帧渲染完纹理处于可采样状态再返回（这是 Unity 侧正确性的前提）

### 2）Unity 侧接线

- **插件层**：`[DllImport("zrender")]` 包装上述 4 个入口（沿用现有 `ZrNative`/`ZKernelNative` 的风格）
- **`ZRenderPipeline.Render()`**：拿到当前相机 → 调 `RenderFrame()` → 用 `CommandBuffer.Blit`
  把外部纹理贴到 `BuiltinRenderTextureType.CameraTarget` → `context.ExecuteCommandBuffer` + `Submit`
- **外部纹理**：`Texture2D.CreateExternalTexture(w, h, TextureFormat.BGRA32, false, false, (IntPtr)handle)`
- **门控**：`ZRenderPipeline` 启用前必须确认当前 Graphics API 是 **D3D12**；否则不要启用（回退 URP），
  并在日志里明确写原因。**不要**在 D3D11 下硬跑（会崩）

### 3）图形 API

- `ProjectSettings` 的 **Windows** 图形 API 设为 **D3D12**（可保留 D3D11 作 fallback，但 ZRender 只在 D3D12 下启用）
- **不要动 Android 的配置**（`m_BuildTargetGraphicsAPIs` 里 AndroidPlayer 的那条保持原样）

### 4）验证（本任务成立的关键）

必须产出**看得见的证据**，不能只"编译通过"：

- 构建 Windows Player（`Tools/build_player.ps1`，约 2-10 分钟）
- 运行 `ZFrame.exe`，确认：**屏幕上是 ZRender 渲染的内容**（深蓝背景 `(13,26,38)` + 旋转立方体），
  而不是原来的 URP 游戏画面
- 给出证据：推进到某一帧后回读屏幕中心像素（能读就回读；不能读就**用 PlayMode 或内置截图机制**，
  或至少在 Player.log 里打印 `RenderFrame` 的真实调用计数与纹理句柄非零）
- **如果做不到"屏幕可见"**，就退到"能证明 `RenderFrame` 被每帧调用且纹理被 Blit"，
  并在报告里如实说明差在哪一步——**不要谎报看到画面**

## 判据

- `ZFrame.exe` 启动后不崩（D3D12 初始化成功）
- `RenderFrame` 每帧被调用（有计数或证据），纹理句柄非零
- **画面确实是 ZRender 的内容**（截图 / 像素回读 / 至少日志+代码路径三方一致）
- 原生 311 用例不回归；`zrhi_d3d12_window.exe --draw` 仍 60 帧正常
- 不改 Android 配置；不改游戏内容

## 已知风险（如实预判，踩到就在报告里写）

1. `Texture2D.CreateExternalTexture` 在 D3D12 下对 NT handle 的要求（需要 `D3D12_HEAP_FLAG_SHARED` + `CreateSharedHandle`）
2. Unity 外部纹理的采样需要资源处于 SRV 状态，若原生转态不对 → 黑屏/花屏
3. 每帧 `CreateExternalTexture` 会泄漏，**纹理只应创建一次**并在尺寸变化时重建
4. Unity 可能在 D3D12 下对 backbuffer 用 `FLIP_DISCARD`，Blit 路径要走 Unity 自己的 CommandBuffer（不要手动 Present）
5. 切 D3D12 后首次构建可能触发 shader 重编，构建时间变长

## 环境纪律（真实踩过）

- Unity 批处理**单实例**：查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 构建**勿加 `-quit`**；退出僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- GUI Editor 加 `-force-d3d12` **会挂起**，别加
- 原生构建走 `Tools/build_native.ps1`（**仓库根 CWD**），**不要**手动 cmake
- 本机有 Vulkan SDK，**不要**设假 `VULKAN_SDK`
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改 Governor 三件套、Unity 安装目录；`.cs` 无 BOM、LF
- **不动游戏内容**（角色/场景/手感）

---

## 施工报告（施工代理写）

> 施工代理：DeepSeek-V4.1-Flash｜日期：2026-11-06｜**未 commit**（按铁律）
> 一句话结论：**屏幕上确实是 ZRender 渲染的内容**（深蓝 (13,26,38) 背景 + 三个旋转棋盘 cube），
> 证据链：原生自身像素回读 + Player 截图逐像素采样 + `RenderFrame` 真实计数 1560+（同一帧三方一致）。
> 启用方式：`ZFrame.exe -zrender`（默认仍是 URP，见 §2.6 的取舍说明）。

### 2.1 改了什么（文件级）

**原生（Native/）**

| 文件 | 改动 |
|---|---|
| `zrhi/d3d12/zrhi_d3d12_unity_target.h/.cpp` | **新增**。共享渲染目标模块：建 BGRA8 共享纹理、复用 `--draw` 的渲染路径（内嵌 cube 24 顶点 + `first_draw.hlsl` 逐字副本 + `DrawList` 合批 + `RgGraph`）、帧末转 SRV + fence 等完成、导出句柄/资源/统计 |
| `zrhi/d3d12/zrhi_d3d12_device.h/.cpp` | 新增 `CreateSharedImage(desc, outHandle)`（`D3D12_HEAP_FLAG_SHARED` + `CreateSharedHandle(GENERIC_ALL)`）与 `D3D12ResourceOf(ZrBuffer)` 重载（回读拷贝用） |
| `zplugin/zplugin.h/.cpp`、`zplugin/zrender.def` | C ABI 新增 **7 个导出**（28 → **35**）：`ZR_Unity_AttachDeviceFromTexture / CreateSharedTarget / GetSharedTargetHandle / GetSharedTargetResource / RenderFrame / ReleaseSharedTarget / GetTargetStats` |
| `Native/CMakeLists.txt` | 新源文件入 `zrhi`；`zrhi` 链 `zrender_core`（复用 RG/DrawList；方向 `zrhi → zrender_core`，不成环）；新增测试文件 |
| `tests/test_zrhi_unity_target.cpp` | **新增** 6 用例（空指针/无设备/尺寸校验/幂等释放/统计结构体布局/**内嵌 HLSL 与 `Native/shaders/first_draw.hlsl` 逐字节一致**） |
| `tests/zrhi_unity_bridge.cpp` | 自证程序加"共享目标"阶段（导出解析、开句柄、描述符对位、10 帧计数、中心像素 ≠ 清屏色、释放后句柄归零） |

**Unity（UnityProject/）**

| 文件 | 改动 |
|---|---|
| `Assets/ZRender/Bindings/ZrNative.cs` | 7 个 `DllImport` + `ZrUnityTargetStatsNative`（48B，布局单测锁住）+ 公开门面 `ZrUnityTargetNative` |
| `Assets/ZRender/Runtime/ZRenderPipeline.cs` | 真实现：D3D12 门控 → 接入设备 → 建共享目标（**只建一次**）→ `RenderFrame` → `Blit` 到 `CameraTarget` → `Submit`；帧计数日志每 120 帧一条 |
| `Assets/ZRender/Runtime/ZRenderBoot.cs` | **新增**。`-zrender` 启动开关（`RuntimeInitializeOnLoadMethod(BeforeSplashScreen)`），非 D3D12 明确拒绝并回退 |
| `Assets/ZRender/Runtime/Resources/ZRenderBlit.shader` | **新增**。自带 blit 着色器（内置 blit 材质在 Player 里会崩，见 §2.7） |
| `ProjectSettings/ProjectSettings.asset` | `m_BuildTargetGraphicsAPIs` 增加 `Standalone: D3D12(0x12) + D3D11(0x02)`；**AndroidPlayer 那条一字未动**（diff 可见） |

### 2.2 共享纹理怎么建的

`ZrD3D12Device::CreateSharedImage`（`zrhi_d3d12_device.cpp`）：

1. `D3D12_HEAP_PROPERTIES{ Type = DEFAULT }` + **`D3D12_HEAP_FLAG_SHARED`**（必须在创建时给；事后 `CreateSharedHandle` 会 `E_INVALIDARG`）；
2. `BGRA8_UNORM`、单 mip / 单层 / 单采样、`ALLOW_RENDER_TARGET`（`usage = ColorAttachment | TransferSrc | Sampled`）；
3. `CreateCommittedResource(..., COMMON, nullptr, ...)`；
4. `ID3D12Device::CreateSharedHandle(resource, nullptr, GENERIC_ALL, nullptr, &handle)`（D3D12 只接受 `GENERIC_ALL`）；
5. 复用 `FinalizeImageCreation`（格式能力探测 / 隐式 RTV / 句柄表 / 初始布局落表）。

尺寸 = 调用方给定（Unity 侧取 `Screen.width/height`，本次 960×540）；**同尺寸重复调用幂等返回 OK，尺寸变化才 Release 后重建**（`EnsureNativeTarget` 里显式判断，避免每帧 `CreateExternalTexture` 泄漏，判据 V4）。Player 日志里 `共享目标就绪` 只出现 **1 次**（`TempLogs/TASK-B-UNITY-02/Player_zrender.log:29`）。

### 2.3 状态与 fence 怎么处理的

一帧（`zr::unity_target::RenderFrame`）：

```
cmdList->Begin()
RgGraph: Import(color) + Import(depth)
  ├─ ZRTargetClear     : ClearColorImage(ColorAttachmentOptimal) + ClearDepthStencilView
  ├─ ZRTargetCubeDraw  : SetPSO → SetRT → SetViewport → VB/IB → 逐批 SetCB + DrawIndexedInstanced
  └─ ZRTargetReadback  : (每 30 帧) TransferSrcOptimal → 1×1 CopyTextureRegion 到 READBACK
帧末两根屏障（同一命令表内）：
  color : ColorAttachmentOptimal|TransferSrcOptimal → ShaderReadOnlyOptimal
          (src = ColorAttachmentWrite|TransferRead / dst = ShaderRead, dstStage = FragmentShader)
  depth : DepthStencilAttachmentOptimal → Undefined（下一帧 RG 起点假设，与 --draw 一致）
cmdList->End() → queue->Submit() → device->WaitIdle() → graph.FlushRetired()
（回读帧）Map READBACK → 记 centerRGBA
```

要点：
- `ShaderReadOnlyOptimal` 在 D3D12 映射为 **PIXEL|NON_PIXEL 双态**（`zrhi_d3d12_translate.cpp:58`），Unity 采样前无需再转；
- **`WaitIdle` 只排空 ZRender 自己的队列**——这正是"画 backbuffer"被否掉的原因，但本案纹理只由这条队列写，等完即"内容+状态都就绪"，Unity 只需采样，**不存在跨队列竞态**；
- 状态与同步全在原生内部，Unity 侧不碰 backbuffer 状态。

### 2.4 构建结果

| 项 | 结果 |
|---|---|
| `Tools/build_native.ps1` | **ALL GREEN**（configure + build `/W4 /WX` 零警告 + 单测 + 拷 dll 到 Unity） |
| 原生单测 | **317 用例 / 776,740 断言 全通过**（基线 311 / 776,716 → +6 用例 / +24 断言，**无回归**） |
| `zrhi_d3d12_window.exe --draw` | **60 帧 OK**，`frames=60 center checks=6 passed=6`，device removed=S_OK，InfoQueue 错误 0 |
| `zrhi_unity_bridge.exe` | **UNITY BRIDGE OK (incl. B-UNITY-02 shared target)，0 failure**（B-UNITY-01 的五项老检查仍全 PASS） |
| `Tools/build_player.ps1` | **PASS**（bundleVersion 0.1.0.32；smoke 20s 存活 + 启动链标记命中） |
| 图形 API | Player 日志 `Version: Direct3D 12 [level 12.1]`、`AMD Radeon 780M` |

### 2.5 画面证据（这条是本任务的关键）

**运行方式**：`ZFrame.exe -zrender -screen-width 960 -screen-height 540 -screen-fullscreen 0`

**(a) 屏幕截图（像素级）**：`TempLogs/TASK-B-UNITY-02/zrender_screen_b.png`

| 采样点 | 值 | 说明 |
|---|---|---|
| 背景（右上/下角/上中） | **(13,26,38)** | ZRender 的 `ClearColorImage` 色 `(0.05,0.10,0.15)`，不是 Unity 的 clear 色 |
| 画面中心（cube 上） | (54,54,54) / (172,168,160) | cube 的棋盘格：暗面=环境项、亮面=Lambert 受光 |
| 网格扫描 1092 点 | **431 点 ≠ 清屏色** | 约 39% 画面被 cube 覆盖（三个 cube 占中带） |

截图肉眼可见：深蓝背景 + **三个棋盘纹理立方体**（与 `--draw` 演示同一场景，随帧旋转），叠着游戏自己的 UGUI 主菜单（开始/继续/设置/退出）——UI 走 Unity 的 overlay 通道，与 SRP 无关，所以照常显示。
对照图 `zrender_screen.png`（同一构建、不同旋转相位）与 `urp_default.png`（**不带 `-zrender`**：纯 UGUI、无 cube、无 (13,26,38) 背景）—— 两张图证明"有 cube / 无 cube"确实由该开关控制。

**(b) 原生纹理回读（同一帧，与屏幕互证）**：`Player_zrender.log`

```
[ZRender] 共享目标就绪：960x540 BGRA8, NT handle=0x17AC, resource=0x24604AD0840，外部纹理只建一次（尺寸变化才重建）
[ZRender] RenderFrame 计数=120  ... 中心像素回读=(54,54,54) 回读次数=4
...
[ZRender] RenderFrame 计数=1560 ... 中心像素回读=(54,54,54) 回读次数=52
```
- **`RenderFrame` 真调用计数 1560+**（20 秒 ≈78fps，与 960×540 的帧率相符，不是一次性调用）；
- **`NT handle = 0x17AC` 非零**，`resource = 0x24604AD0840` 非零，且"共享目标就绪"只出现一次；
- **中心像素 (54,54,54) 与屏幕截图中心像素完全一致**（截图中心采样同样是 (54,54,54)）——两条独立路径给出同一组数字；
- 原生侧自证程序另有一路证据：`stats(frame0) center=(10,22,54)`（帧 0 暗面）与宿主 `OpenSharedHandle` 后同帧回读一致。

**(c) 未取到的证据（如实登记）**：宿主导入侧"整面 `CopyTextureRegion` 回读"在本机不可靠（只有 1/8 行序能读到正确内容，见 §2.7 坑 5），因此**没有**用它作为证据；也没有做逐帧录屏或 GPU 抓帧（RenderDoc）。

### 2.6 门控与"默认不切管线"的取舍（需主控确认）

- `ZRenderPipeline.Render()` 第一行判断 `SystemInfo.graphicsDeviceType != Direct3D12` → 打一次明确警告并直接 `Submit()`（**不在 D3D11 下硬跑**）。
- `ZRenderBoot` 同样二次判定：非 D3D12 时**拒绝启用**并回退 URP。日志样例见代码注释与 `Player.log`。
- **为什么默认不把项目管线切成 ZRender**：A 轨玩法/UI/相机像素差分用例全部基于 URP；把 `GraphicsSettings` + `QualitySettings`（三档都绑了 URP_*）改掉会让游戏只剩 cube，并让既有 PlayMode 像素用例集体失效（跨任务回归）；而"运行期跨管线热切"是设计明令禁止的。故做成**启动开关**：`-zrender` 才启用，不带开关行为零变化（`urp_default.png` 实测）。
  **若验收要求"开箱即是 ZRender"**：把 `ZRenderBoot` 的 `HasCommandLineFlag()` 改成 `return true;`（或在 ProjectSettings 把默认/质量档管线指向 `Assets/Settings/ZRenderPipeline.asset`）即可，一行改动，风险是上述 URP 用例。

### 2.7 踩了什么坑（都实测过，按代价排序）

1. **`Texture2D.CreateExternalTexture` 在 D3D12 下要的是 `ID3D12Resource*`，不是 NT 句柄。**
   传 `CreateSharedHandle` 的 NT 句柄 → 首帧 `0xc0000005`（崩在 `UnityPlayer.dll+0x98e944`，Windows 事件日志可查，Player.log 只有孤立一行 `Crash!!!`）。
   改为传资源指针后立即稳定（1560 帧无异常）。因此新增 `ZR_Unity_GetSharedTargetResource`；**NT 句柄仍按简报导出**（跨进程/跨 API 用），两条路并存。
2. **`RenderTexture.GetTemporary(...).GetNativeTexturePtr()` 在 SRP 首帧返回 0**（没真正分配）→ 改用 `new Texture2D + Apply(false, true)` 取探针纹理反查设备。
3. **内置 blit 材质在 Player 里不可靠**：`CommandBuffer.Blit(tex, CameraTarget)` 不传材质的路径同样崩（此时崩溃点已在 Blit 之后），改为**自带 `Resources/ZRenderBlit.shader` + 运行时 `new Material`**（Resources 下随包走，不依赖 Always Included Shaders）。
4. **必须 `context.SetupCameraProperties(camera)`**，否则 SRP 里 `BuiltinRenderTextureType.CameraTarget` 解析不到目标（同样的崩溃形态）。
5. **宿主导入侧的整面回读是"假象陷阱"**：同一张共享纹理，ZRender 用自己的资源对象回读完全正确，宿主 `OpenSharedHandle` 后 `CopyTextureRegion` 整面/1×1 回读却只有 1/8 行正确（其余为 0）；我花了多轮才用"先宿主清洋红 → 再让 ZRender 渲染 → 看该像素是否被覆盖"把责任定到**宿主导入侧**（ZRender 侧同一像素读到 (13,26,38)、宿主读到 (0,0,0)）。**这条不影响 Unity 采样**（SRV 采样正常，截图已证），但会误导任何"用回读验收"的人，特此记录。
6. 早期版本把 `D3D12_TEXTURE_LAYOUT_ROW_MAJOR` 当解药试过：`D3D12_FEATURE_DATA_D3D12_OPTIONS` 里没有 `RowMajorTextureSupported`（那是 cross-adapter 专用），已回退。
7. `-screen-width/height` 不传时窗口只有 333×216，`CopyFromScreen` 抓到的是桌面；改用 `PrintWindow(PW_RENDERFULLCONTENT)` + `SetProcessDPIAware`（脚本 `TempLogs/TASK-B-UNITY-02/capture_zrender.ps1`）。

### 2.8 没做成 / 边界（如实）

- **没跑 EditMode / PlayMode 用例**（不在本任务判据；但 `ProjectSettings` 的 Windows 图形 API 改了 D3D12，理论上影响编辑器与 PlayMode 的 GPU 路径——**建议验收方跑一次 `Tools/run_tests.ps1` 或至少 PlayMode 48 例**）。
- **没在 Android 验证**（配置未动；ZRender 只在 D3D12 桌面上启用）。
- **跨进程/跨适配器共享未做 keyed mutex**（当前是同一设备、同一进程；跨进程再共享需要 `IDXGIKeyedMutex` 或 fence 导出）。
- **游戏 3D 内容不参与**：ZRender 目前画的是自带的 cube 演示场景（与 `--draw` 同源），把真实场景搬进 ZRender 是后续任务；本次只证明"ZRender 的画面能上屏"。
- **未做帧率/带宽测量**（每帧一次 `WaitIdle` 会串行化 CPU/GPU，性能优化不在本任务）。
- `Tools/version.json` 与 `ProjectSettings.bundleVersion` 因多次跑 `build_player.ps1` 自增到 0.1.0.32（工具设计如此，非手改）。
- 临时捕获脚本与截图在 `TempLogs/TASK-B-UNITY-02/`（不入库，作证据留存）。

---

## 验收清单（供验收代理）

- [x] V1 `ZFrame.exe` 在 D3D12 下启动不崩，日志能证明是 D3D12
- [x] V2 `RenderFrame` 被每帧调用（有真实计数），共享纹理句柄非零
- [x] V3 **画面是 ZRender 的内容**（截图/像素/三方一致；若仅"Blit 已调用"须如实降级）
- [x] V4 外部纹理只创建一次（尺寸变化才重建），无每帧泄漏
- [x] V5 原生 311 用例不回归；`--draw` 60 帧正常
- [x] V6 Android 图形 API 配置未被动；未改游戏内容；未 commit；现场干净
- [x] V7 D3D11 环境下不会硬跑 ZRender（有明确回退与日志）

## 验收结论（验收代理写）

> 验收代理：CodeBuddy-Code（与施工代理不同实例）｜日期：2026-11-06｜**未 commit**
> 取证方式：全部**自己复跑**，不采信 §2 自述。施工的截图/日志只作对照，独立证据落在
> `TempLogs/TASK-B-UNITY-02/audit/`（`audit_capture2.ps1` / `audit_resize.ps1` / `audit_click.ps1` + 各次
> `Player_audit*.log` 与截图）。

### 一句话裁决

**PASS with 受限**：ZRender 的画面确实上了 Unity 的屏（颜色变异实验证实是"每帧真画"，不是贴旧图），
原生零回归、D3D12 门控与 Android 配置都对；但这是**演示级集成**——ZRender 管线里根本没有场景渲染，
3D 游戏本体不是"被盖住"而是"完全不画"，且默认启动仍是 URP，需 `-zrender` 开关。

### V1–V7 逐条

| 项 | 判定 | 证据（本次独立复跑） |
|---|---|---|
| V1 D3D12 启动不崩 | **PASS** | `audit/Player_audit.log:8-10` = `d3d12: loaded!` / `Version: Direct3D 12 [level 12.1]` / AMD Radeon 780M；进程存活 22s、渲到 1800 帧无崩溃、日志无 `Crash` |
| V2 RenderFrame 每帧真调用 + 句柄非零 | **PASS** | 同一日志 `RenderFrame 计数=120…1800` 严格 120 递增（约 60–82fps，18–22s 内累计 1200–1800 次）；`NT handle=0x1728 / 0x17E4 / 0x17A4 / 0x17A8`（每次进程不同、均非零）、`resource` 非零；两次独立截图窗口区 **14.4% 像素不同**（cube 旋转相位不同）= 逐帧在画。另跑 `zrhi_unity_bridge.exe` 独立进程自证：`handle=0x3B0`、10 帧全 OK、`UNITY BRIDGE OK (0 failure)` |
| V3 画面归属（核心） | **PASS** | 见下方 a/b/c 三段 |
| V4 外部纹理只建一次 | **PASS with 受限** | 全仓库 `CreateExternalTexture` 只有 1 处调用点（`ZRenderPipeline.cs:170`），且被 `EnsureNativeTarget()` 的尺寸相等提前返回挡住（`:121`）；每次运行日志 `共享目标就绪` 恰好 **1** 次（`Player_audit.log`、施工 `Player_zrender.log` 各 1）。**额外加压测试**：运行中把窗口从 960×540 改到 2374×1329，日志出现第 2 次 `共享目标就绪：2374x1329`、计数从 0 重新累加、进程不崩 → "尺寸变化才重建"确为真。**受限**：重建分支只 `_externalTexture = null`，**没有 `Object.Destroy` 旧纹理**（`:144`），每次改分辨率泄漏一个 Texture2D 包装对象（非每帧，非阻断） |
| V5 原生回归 | **PASS** | `Tools/build_native.ps1`：`317 cases / 317 passed`，`776740 / 776740` assertions（= 基线 311+6 用例、776716+24 断言，无回归）；`zrhi_d3d12_window.exe --draw`：`frames=60 center checks=6 passed=6`、`device removed = S_OK`、`InfoQueue error count = 0`、exit 0 |
| V6 现场与配置 | **PASS** | `git diff`：AndroidPlayer 那条是**上下文行**（`m_APIs: 150000000b000000` / `m_Automatic: 0` 值未变），只新增 `Standalone: 1200000002000000`(= D3D12(0x12)+D3D11(0x02))；未改游戏内容（`Assets/Game/**` 零改动，仅 ZRender 插件/管线 + ProjectSettings + Native + Tools/version.json）；未 commit（HEAD 仍是 `6892642 chore: B-UNITY-02 派发`）；TASKS/HANDOFF/WORKLOG 未出现在 status 中。注：`bundleVersion 0.1.0.25→32`、`AndroidBundleVersionCode 3→10` 是构建工具行为（`Assets/Game/Editor/PlayerBuild.cs:157` 按 version 写回），非手改 |
| V7 D3D11 不硬跑 ZRender | **PASS** | 实测 `ZFrame.exe -zrender -force-d3d11`：`Player_audit2.log:7-10` = `Forcing GfxDevice: Direct3D 11` / `Direct3D 11.0 [level 11.1]`；`:23` = `[ZRender] 收到 -zrender，但当前图形 API = Direct3D11：ZRender 只在 D3D12 下启用，已回退 URP（不改任何设置）`；截图里 `(13,26,38)` 命中 **0/20340**、无 cube、进程不崩。双层门控在代码里也在：`ZRenderBoot.cs:48`（切管线前）+ `ZRenderPipeline.cs:53`（每帧渲染前） |

### V3 的独立差异证明（三段，全部本次自己产生）

**(a) 两张对照图的差异（自己打开看 + 像素统计，不采信文字描述）**

| | `zrender_screen_b.png`（带 `-zrender`） | `urp_default.png`（不带） |
|---|---|---|
| 背景主色 | **(13,26,38) 深蓝，占 65.3%** | **(26,26,26) 近黑，占 80.8%**（另有 4.6% 纯黑） |
| 几何体 | **三个棋盘格立方体**横排，占画面约 12%（棋盘深色 `(10,22,54)` 6.0% + 灰面 `(54,54,54)` 6.0%），随帧旋转 | **一个立方体都没有**（蓝色像素只有 UI 按钮的 3.9%） |
| 蓝色占比（B−R>40） | 11.9% | 4.2% |
| 共同部分 | 标题「ZFrame 垂直切片」+ 开始/继续/设置/退出 四个 UGUI 按钮（按钮叠在 cube 之上，说明 UI 走 overlay 通道仍正常） | 同左，但按钮位置因为没有了 cube 而显得居中 |

结论：背景色完全不同（13,26,38 vs 26,26,26）、几何体有无相反 —— **不是同一张图的两种压缩，是两个不同的渲染结果**。

**(b) 颜色变异实验（做完了，含还原哈希）**

1. 基线哈希（变异前）：
   - `Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp` = `bb36588d01be748f97d44a7a598d48612eaa3fc8978a192bb22da1516a100389`
   - `zrender.dll`（Build 与 Unity 插件一致）= `475e07a8792a153854f0a1f62c19c81d6071a913db4580b49fb6e148b8e49595`
2. 基线运行（本次自己的图）：背景 `(13,26,38)` 命中 **6102/20340**，bbox `(56,104)-(1008,632)`，品红 0 命中。
3. 变异：把 `kClearColor` 由 `{0.05,0.10,0.15,1}` 改为 `{1,0,1,1}`（品红），`build_native.ps1` 重建，
   替换 `Build/Windows64/ZFrame_Data/Plugins/x86_64/zrender.dll`（`6c885481…`），**不动 Unity 资源、不重建 Player**。
4. 结果：背景变成 **品红** —— `(255,0,255)` 命中 **6114/20340**，bbox **与基线完全相同** `(56,104)-(1008,632)`；
   三个 cube 仍在画（说明变异只改了 clear，绘制路径照旧）。
   → **屏幕内容确实每帧由 ZRender 的 clear + draw 产生，不是把一张旧图贴上去**（旧图不动的话背景不可能跟着清屏色走）。
5. 还原：源码改回原值，`sha256` 回到 `bb36588…`（**与基线逐字节一致**）；重新 `build_native.ps1`（顺带跑完 V5 的 317 用例），
   新 dll = `3aebf338317dba57491e3e14f1100cb9be46d242ce22e5e7d34132df3db3cf0a`（与变异前 `475e07…` 不同，是 PE 时间戳导致的
   构建非确定性，不是常量残留）。为排除残留，做二进制常量指纹比对：
   - 变异 dll：`0.05f`×0、`0.10f`×0、`(1,0,1,1)` 序列 **×2**
   - 还原 dll：`0.05f`×1、`0.10f`×1、`(1,0,1,1)` 序列 **×1** —— 与变异前原 dll（也是 ×1/×1/×1）**指纹一致**
6. 还原后功能复核：背景回到 `(13,26,38)` 命中 **6075/20340**、品红 **0** 命中。
   变异 dll 与原始 dll 都留档在 `TempLogs/TASK-B-UNITY-02/audit/`（不入库）。

**(c) 局限**：不需要走"降级出口"——变已完成。但未做 GPU 抓帧（RenderDoc）与逐帧录屏。

### §2.7「七个坑」逐条裁决

| 坑 | 结论 |
|---|---|
| 1. `CreateExternalTexture` 在 D3D12 要 `ID3D12Resource*` 而非 NT 句柄（传句柄首帧 0xC0000005） | **可接受，但必须记账**。这是 Unity API 的硬约束，补 `ZR_Unity_GetSharedTargetResource` 是对的做法，NT 句柄仍导出（跨进程用）。风险提示：**任何后来者若按简报字面用 NT 句柄**就会崩，需在 zplugin.h 注释里保持醒目 |
| 2. `RenderTexture.GetTemporary` 首帧 ptr=0 | 工作绕过（探针 Texture2D），可接受 |
| 3. 内置 blit 材质在 Player 崩 → 自带 shader | 可接受；代价是多一个 Resources shader（已随包，截图证明生效） |
| 4. 必须 `SetupCameraProperties` | 可接受，属 SRP 常识 |
| 5. **宿主导入侧整面回读只有 1/8 行正确** | **验收必须拦下的遗留缺陷**（不阻断本任务裁决）：同一张共享纹理，ZRender 自己读对、宿主 `OpenSharedHandle` 后 `CopyTextureRegion` 只有 1/8 行正确，说明共享资源的行距/布局假设在**导入侧**不一致。当前 Unity 走 SRV 采样不受影响（截图已证），但任何"共享纹理跨进程/跨侧回读"的后续任务都会踩雷，必须先修 |
| 6. `TEXTURE_LAYOUT_ROW_MAJOR` 误判 | 已回退，无残留（源码里无该 flag） |
| 7. 窗口 333×216 / `CopyFromScreen` 抓桌面 | **验收复现了同类问题**：本次 `PrintWindow(PW_RENDERFULLCONTENT)` 对该 Unity flip-model 窗口**稳定返回全黑**，`GetWindowRect` 在 200% DPI 下与 `CopyFromScreen` 坐标不一致（986×611 vs 真实 493×306）。最终改用"整桌面抓取 + 按颜色直方图定位窗口"才拿到可信图（`audit_capture2.ps1`）。**任何后续截图验收都不能假设 PrintWindow 可用**，这条应写进环境纪律 |

### §2.8「未做项」逐条裁决

| 未做项 | 是否拦 | 说明 |
|---|---|---|
| 没跑 EditMode/PlayMode | **拦（限定范围）**：不阻断本任务裁决，但**必须在关闭任务前补一次**。Windows 图形 API 已改成 D3D12，A 轨 UI/相机像素差分用例全部走 GPU 路径，这是跨任务回归风险。本次验收**没有跑**，如实登记为未验证 |
| 没在 Android 验证 | 不拦：配置一字未动，ZRender 桌面限定 |
| 跨进程共享无 keyed mutex | 不拦（当前同进程同设备），但作为已知边界写入后续任务 |
| **游戏 3D 内容不参与（只画 --draw 的 cube）** | **不拦本任务**（简报第 1 条就要求"复用现有 `--draw` 的渲染路径"），但**必须明确写"未达成可玩集成"**，见下节 |
| 未做帧率/带宽测量 | 不拦。但代码事实：`RenderFrame()` 每帧 `device->WaitIdle()`（`zrhi_d3d12_unity_target.cpp:686`）**串行化 CPU/GPU**，帧率上限被锁在"ZRender 队列排空"上，后续接真实场景前必须换成 fence 双缓冲 |
| version/bundleVersion 自增 | 不拦，已核实是 `PlayerBuild.cs:157` 的既定行为 |

### 「是演示级集成还是可玩集成」——判定：**演示级（明确）**

**证据（代码级，已读）**：`ZRenderPipeline.Render()`（`ZRenderPipeline.cs:50-110`）整个函数只做三件事——
门控、`ZR_Unity_RenderFrame()`、`CommandBuffer.Blit(外部纹理 → CameraTarget)`。它**没有任何场景绘制调用**
（无 `context.DrawRenderers`、无 `DrawSkybox`），也就是说不带 `-zrender` 时由 URP 画的角色/敌人/场景，
开 `-zrender` 后**不是被盖住，而是根本没有人画**。屏幕 = ZRender 的 cube 演示 + Unity 的 UGUI overlay。

**相机也不跟随**：原生侧 `view` 矩阵是单位阵、投影固定 `FOV=60°`（`zrhi_d3d12_unity_target.cpp:535-537`），
完全不读 Unity 相机的 position/rotation/FOV；镜头动、角色动，画面不会跟着动。

**尝试过的经验补充**：本想点「开始」进游戏看是否被覆盖，用 `SetCursorPos + mouse_event` 在 ZRender 与 URP
两种模式下各点一次，**两种模式都停在主菜单**（输入没有真正落到 UGUI 上），因此这条**没有拿到经验证据**，
上面判定完全来自代码 + 施工自己的 §2.8 承认。**不得把"点击无效"当作"游戏被挡"的证据**。

**多相机/Scene 视图**：只处理 `cameras[0]`（`ZRenderPipeline.cs:102`），其余相机完全不画也不清屏 →
分屏/Camera 叠加在 -zrender 下必然异常。**未实测**，按代码事实登记。

### 现场洁净度

- 未 commit；HEAD 仍为派发提交 `6892642`。
- `git status` 仅 20 项，全部属于本任务范围（ZRender 插件/管线、ProjectSettings、Native、Tools/version.json、本日志）。
- 变异已还原：源码哈希与变异前逐字节一致；`Build/.../zrender.dll` 与 `UnityProject/Assets/Plugins/ZRender/zrender.dll` 均为还原后构建
  `3aebf338…`（两者一致），并已用二进制常量指纹 + 重新运行（背景回 `13,26,38`、品红 0）双重确认。
- 验收临时脚本/截图/日志全部在 `TempLogs/TASK-B-UNITY-02/audit/`（`.gitignore:45` 已忽略 `TempLogs/`），未污染产品代码。
- 无残留 `ZFrame.exe` / `Unity.exe` 进程。

### 最终裁决

**PASS with 受限**（V1–V7 全 PASS，其中 V3/V4 带明确边界），**接受为本任务的交付**，但附三条必须跟进的限制：

1. **范围确认需主控拍板**：默认启动仍是 URP，只有 `-zrender` 才是 ZRender 画面（§2.6 已主动提出）。
   若判据要求"开箱即 ZRender"，改一行即可，但会打破 A 轨 PlayMode 像素用例——建议维持现状并单独立项。
2. **"可玩集成"未达成**：3D 游戏内容不渲染、相机不跟随、仅 `cameras[0]`。后续任务（接真实场景 + 相机参数传递 + 多相机）必须先做，
   否则"ZRender 上屏"只是演示。
3. **两处技术债必须记**：① 宿主导入侧整面回读 1/8 行错误（坑 5，潜在共享布局缺陷）；② 尺寸变化时旧 `Texture2D` 未 `Destroy`
   （V4 的轻微泄漏）；③ 每帧 `WaitIdle` 的串行化（性能天花板）。

