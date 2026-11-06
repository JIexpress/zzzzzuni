# TASK-B-UNITY-04 —— 集成稳定性：纹理泄漏 + 帧同步 + 矩阵/顶点单测

> 缺口清理第 1 刀。B-UNITY-03 已让 Unity 物件进 ZRender（验收 PASS with 受限），
> 但验收明确点出三个必须补的：**零单测**、**纹理重建泄漏**、**每帧 WaitIdle 串行化**。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 背景（已验收的事实）

- `-zrender` 已能画出真实游戏场景（位置逐像素吻合 `(693,421)`）
- mesh 按 InstanceID 缓存，3840 帧上传累计恒为 12
- **验收列的三个遗留**（本任务就是清它们）：
  1. **零新增单测**：矩阵转换（Unity 列主序 → D3D 行主序）与顶点解析（32B/vertex）没有任何测试，改坏了没人拦
  2. **纹理重建泄漏**：窗口尺寸变化时重建共享目标，但旧的 `Texture2D` 没 `Destroy`，每次改分辨率漏一个
  3. **每帧 `WaitIdle()`**：CPU/GPU 完全串行，接真实场景前必须换成 fence 双缓冲

## 任务（三项，按此顺序）

### 1）补单测（最重要——现在这块是裸奔）

在 `UnityProject/Assets/ZRender/Tests/` 下补，纯函数优先（不依赖 GPU）：

- **矩阵转换**：把 Unity 的 `Matrix4x4` 转成传给原生的 `float[16]` 时，主序是否正确。
  写一个可证伪的用例：给一个**非对称**矩阵（比如只有 `m03 = 7`），断言转换后目标位置的值对得上；
  若实现写错（直接 memcpy），该用例必须变红。
- **投影矩阵**：`GL.GetGPUProjectionMatrix` 的 Y 翻转与深度范围——断言近/远平面点投影后的 NDC z 落在 `[0,1]`。
- **顶点打包**：`Mesh` → 32B/vertex（pos+normal）的打包函数，断言 stride、偏移、法线归一化。
- **剔除**：`GeometryUtility.TestPlanesAABB` 的包装——完全在视锥内/外/跨边界三种情形。

要求：**每条都要"改回错误实现会变红"**（至少矩阵与顶点打包两条要有变异证据）。

### 2）修纹理重建泄漏

尺寸变化重建 `Texture2D` 时，先 `Object.Destroy` 旧的（或用 `DestroyImmediate` 视时机），
并保证 `ZR_Unity_ReleaseSharedTarget` 在释放前被调用。验证：连续改 3 次分辨率，纹理对象数不增长。

### 3）把每帧 WaitIdle 换成 fence 双缓冲

现状：`RenderFrame` 里每帧 `WaitIdle()`，CPU 等 GPU 干完才返回。
改法（任选，说明理由）：
- **双缓冲**：两套 command allocator + fence，第 N 帧等第 N-1 帧的 fence（不等当前帧）
- 或至少把「等最后一帧」改成「等 N-1 帧」，容忍一帧延迟

验收看：帧率有可观测提升（或 CPU 等待时间下降），且画面不撕裂/不花。

## 判据

- 新增单测 ≥ 4 条，且**矩阵/顶点两条有变异证据**（改错必红）
- 连续改分辨率 3 次，纹理对象数不增长（有计数证据）
- CPU 等待下降（给出改前/改后的对比数字，哪怕是粗略的帧时间）
- 回归：原生 317、EditMode 916（+新增）、`--draw` 60 帧、`-zrender` 画面仍正常
- 不改游戏内容

## 环境纪律（真实踩过）

- Unity 批处理**单实例**：查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿加 `-quit`**；僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- 原生构建走 `Tools/build_native.ps1`（仓库根 CWD），不要手动 cmake
- **截图坑**：`PrintWindow` 对该 Unity 窗口返回全黑，200% DPI 下 `GetWindowRect` 坐标不符
- 跑完查 `git status`：Editor 改写的 `URP_*.asset`/`QualitySettings.asset` 非本意就还原
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- **不动游戏内容**；不动 Governor 三件套、Unity 安装目录；`.cs/.cpp/.h` 无 BOM、LF

---

## 施工报告（施工代理写）

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 新增单测 ≥4，覆盖矩阵主序/投影深度/顶点打包/剔除
- [ ] V2 矩阵与顶点打包两条**有变异证据**（改错必红，改回必绿，附哈希）
- [ ] V3 连续改 3 次分辨率，纹理对象数不增长
- [ ] V4 CPU 等待下降（有改前/改后数字）
- [ ] V5 回归：317 / 916+新增 / --draw 60 帧 / -zrender 画面正常
- [ ] V6 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
