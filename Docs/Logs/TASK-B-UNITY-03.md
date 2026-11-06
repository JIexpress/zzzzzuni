# TASK-B-UNITY-03 —— 可玩级第一刀：DrawRenderers（把 Unity 的物件搬进 ZRender）

> 三段之后的第 4 步。目标：**能玩**（看得见场景、角色会动、位置正确），不追求画质精度。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 背景（已验收的事实）

- B-UNITY-01：原生接受 Unity 的 `ID3D12Device`（`CreateFrom`）✅
- B-UNITY-02：渲染到共享纹理 → Unity `Blit` 贴屏，**画面已出现在 Unity 窗口**（颜色变异证伪过）✅
- **但 `ZRenderPipeline.Render()` 只做「门控 + `RenderFrame()` + `Blit`」**，
  `RenderFrame` 内部（`zrhi_d3d12_unity_target.cpp:510`）画的是**硬编码的三个旋转 cube + 固定 FOV 相机**，
  完全不读 Unity 场景 → 开 `-zrender` 后游戏物件**一个都不画**

## 目标（"能玩"的判定标准）

打开 `-zrender` 后，屏幕上应该看到**真实的游戏场景**：
地面、角色、敌人、可交互物件都在**正确的位置**，玩家移动时**相机跟着动**，物件**朝向正确**。

不要求：PBR 精度、阴影、后处理、材质贴图正确、超分。**这些都不是本任务。**

## 任务

### 1）原生侧：让帧内容由调用方决定

新增导出（加进 `Native/zplugin/zrender.def` 与头）：

```
ZR_Unity_SetCamera(const float* view16, const float* proj16) -> int
ZR_Unity_CreateMesh(const float* verts, uint32_t vertCount, uint32_t strideFloats,
                    const uint32_t* indices, uint32_t indexCount, uint64_t* outHandle) -> int
ZR_Unity_DestroyMesh(uint64_t handle) -> void
ZR_Unity_DrawMesh(uint64_t handle, const float* world16, const float* rgba) -> int
ZR_Unity_BeginScene() -> int      // 清空本帧 draw list
ZR_Unity_EndScene() -> int
```

改造 `unity_target::RenderFrame`：
- **有** `SetCamera` + `DrawMesh` 的内容 → 用传入的 view/proj 和物件画
- **没有** → 保持原样画那三个 cube（**不能把现有验证弄坏**）
- 顶点布局与现有 `first_draw.hlsl` 输入一致（position + normal，32B/vertex）

### 2）Unity 侧：收集 + 剔除 + 提交

在 `ZRenderPipeline` 里（或新开一个 `ZRenderSceneBridge`）：

- **相机**：`camera.worldToCameraMatrix` / `GL.GetGPUProjectionMatrix(camera.projectionMatrix, false)`
  → 转成 float[16] 传给 `SetCamera`（注意行/列主序，D3D 用行主序，Unity 矩阵转置关系要核对清楚）
- **物件收集**：启动时缓存场景内所有 `MeshRenderer`（别每帧 `FindObjectsOfType`）；
  每帧用 `GeometryUtility.TestPlanesAABB` + `renderer.isVisible` 做可见性判断
- **mesh 上传**：按 `mesh.GetInstanceID()` 缓存句柄，**每个 mesh 只上传一次**；
  提前转换成 32B/vertex（pos+normal）的紧凑数组，别每帧分配
- **每帧**：`BeginScene` → 对每个可见 renderer `DrawMesh(handle, localToWorld, color)` → `EndScene`
- **颜色**：取 `sharedMaterial.color`（有 `_BaseColor` 用 `_BaseColor`），拿不到就用白色

### 3）性能底线（"能玩"的必要条件）

- **不许每帧上传顶点**（mesh 缓存）
- **不许每帧 new 数组**（复用缓冲）
- 目标：千人场景之外，常规场景（≤200 物件）帧率不低于 URP 的一半即可

## 判据

- 开 `-zrender` 后**屏幕上能看到游戏场景**：地面 + 角色 + 敌人（位置/朝向正确）
- 玩家移动时相机跟随，画面内容随之变化
- 关掉 `-zrender` 仍走 URP，画面正常（不破坏原路径）
- `zrhi_d3d12_window.exe --draw` 仍 60 帧正常（原生演示不受影响）
- 原生用例不回归（当前 317）
- **给出画面证据**（截图对比：URP 画面 vs ZRender 画面，同一位置）

## 已知风险（踩到就如实写）

1. **矩阵主序**：Unity 是列主序，D3D 的 `XMMATRIX` 是行主序，直接 memcpy 会得到转置画面（物体位置全错）
2. **`GL.GetGPUProjectionMatrix`**：D3D 的 Y 翻转与深度范围（[0,1] vs [-1,1]）必须处理，否则上下颠倒或全黑
3. 顶点格式：Unity Mesh 的 normal 可能与 `first_draw.hlsl` 期望的插值方式不同 → 光照发黑
4. `Renderer.isVisible` 在 SRP 下语义可能不同，必要时改用 CullingResults
5. 视锥剔除用 `GeometryUtility` 是 CPU 近似，边界物件可能闪

## 环境纪律（真实踩过）

- Unity 批处理**单实例**：查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 构建**勿加 `-quit`**；僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- 原生构建走 `Tools/build_native.ps1`（仓库根 CWD），不要手动 cmake
- 截图：`PrintWindow` 对该 Unity 窗口**稳定返回全黑**，`GetWindowRect` 在 200% DPI 下坐标不符 →
  用「整桌面抓取 + 颜色直方图定位」
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- **不动游戏内容**（角色/场景/手感/关卡）——用户明确说这块交给另一个窗口
- 不动 Governor 三件套、Unity 安装目录；`.cs/.cpp/.h` 无 BOM、LF

---

## 施工报告（施工代理写）

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 开 `-zrender` 能看到**游戏场景**（地面/角色/敌人），位置与朝向正确
- [ ] V2 玩家移动时相机跟随、画面变化
- [ ] V3 关 `-zrender` 走 URP 正常（原路径未破）
- [ ] V4 `--draw` 仍 60 帧；原生用例不回归
- [ ] V5 mesh 只上传一次（有缓存证据，不是每帧传顶点）
- [ ] V6 同一位置的 URP / ZRender 两张截图对比
- [ ] V7 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
