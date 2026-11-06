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

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 `ZFrame.exe` 在 D3D12 下启动不崩，日志能证明是 D3D12
- [ ] V2 `RenderFrame` 被每帧调用（有真实计数），共享纹理句柄非零
- [ ] V3 **画面是 ZRender 的内容**（截图/像素/三方一致；若仅"Blit 已调用"须如实降级）
- [ ] V4 外部纹理只创建一次（尺寸变化才重建），无每帧泄漏
- [ ] V5 原生 311 用例不回归；`--draw` 60 帧正常
- [ ] V6 Android 图形 API 配置未被动；未改游戏内容；未 commit；现场干净
- [ ] V7 D3D11 环境下不会硬跑 ZRender（有明确回退与日志）

## 验收结论（验收代理写）

（待填）
