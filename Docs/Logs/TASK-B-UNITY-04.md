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

### ⚠️ 本任务曾被 429 中断（主控说明，2026-11-07 03:40）

**经过**：2026-11-06 14:05 派单 → 子代理已启动并落盘改动（15:28–15:47）→ 14:10 左右触发
配额 429（`after subagent was created`），返回体丢失、**施工报告未写**。配额于 11-07 03:34 重置。

**中断前已落盘的内容**（主控核对 `git status` 后确认，17 项）：

| 文件 | 性质 |
|---|---|
| `Assets/ZRender/Runtime/ZrSceneMath.cs`（新） | 矩阵转换/顶点打包纯函数（任务 1 的落点） |
| `Assets/ZRender/Tests/ZrSceneMathTests.cs`（新） | 矩阵主序 / 投影深度 / 顶点打包 / 剔除 单测 |
| `Assets/ZRender/Tests/ZRenderPipelineTextureLifecycleTests.cs`（新） | 纹理重建泄漏（任务 2） |
| `Assets/ZRender/Tests/ZRenderFrameSyncTests.cs`（新） | fence 双缓冲（任务 3） |
| `ZRenderPipeline.cs` / `ZRenderSceneBridge.cs` / `ZrNative.cs` | 接线与绑定 |
| `Native/zplugin/*`、`zrhi/d3d12/zrhi_d3d12_unity_target.*` | 原生侧配套 |
| `Assets/Plugins/ZRender/zrender.dll` | 重建产物 |

**主控接管后的验证**（非施工自述，可复跑）：

```
[1] native   PASS  317 cases / 776,740 assertions / 0 failed；ctest 100%
[2] editmode PASS  total=925 passed=925 failed=0   (916 基线 + 9 新增)
[3] playmode PASS  total=48  passed=48  failed=0
[4-6] arch/config/naming 全 PASS
```

**一次抖动已排除**：首轮 PlayMode 有 1 例失败——
`DoorToggleEvents_BrightenTheFrame_AtTheDoorPosition_BothDirections`
（`0.000312` vs 阈值 `0.004000`，"门事件没在屏幕上产生火花"）。
重跑 **48/48 通过**，判定为像素测试抖动；该用例属**游戏内容侧粒子事件**，与本任务改动无关
（本任务未触碰 URP 路径与粒子系统）。

**待办**：施工报告正文未写（子代理被中断），上述接管说明替代之；判据结论以**独立验收**为准。


---

## 验收清单（供验收代理）

- [x] V1 新增单测 ≥4，覆盖矩阵主序/投影深度/顶点打包/剔除
- [x] V2 矩阵与顶点打包两条**有变异证据**（改错必红，改回必绿，附哈希）
- [x] V3 连续改 3 次分辨率，纹理对象数不增长
- [x] V4 CPU 等待下降（有改前/改后数字）
- [x] V5 回归：317 / 916+新增 / --draw 60 帧 / -zrender 画面正常
- [x] V6 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

> 验收代理 TASK-B-UNITY-04（独立取证，2026-11-07）。施工被 429 中断、其报告与变异验证缺失，
> 本结论**全部来自本次重新执行的命令与产物**，不采信任何自述。证据目录：`TempLogs/audit_bu04_*`
> （native build / editmode 全量与过滤 / m1 / m2 / m3_sync / green / green2 / playmode / draw / buildplayer /
> `TASK-B-UNITY-04/Player_audit_zrender.log`+`audit_zrender.png`）。

### 逐条裁决

| 项 | 结论 | 一行证据 |
|---|---|---|
| V1 | **PASS** | 9 条新用例（7 矩阵/投影/打包/剔除 + 1 纹理 + 1 帧同步），`audit_bu04_editmode.xml` 925/925，三夹具各 7/1/1 全绿 |
| V2 | **PASS** | 矩阵直拷 → 3 条红（`But was: 0.0f` 等）；打包 stride 7 → 1 条红（`Expected: 0.75 ... But was: -4.0`）；还原后 `sha256=7767565…` 与原件逐字节相同，重跑 7/7 绿 |
| V3 | **PASS（受限）** | 3 次改尺寸后 `Resources.FindObjectsOfTypeAll<Texture2D>()` 计数恒为 2，`创建 8 / 销毁 6`（日志原文）；受限项见下 |
| V4 | **PASS（受限）** | 真双缓冲：`Signal`+帧首等 N-1+双槽+可读槽位；播放器 720 帧 `阻塞 0/720，平均等待 0.002ms，在途 1`；旧语义变异体 `120/120，0.801ms` 且测试变红 |
| V5 | **PASS** | native 317/317（776,740 断言）、EditMode 925/925、PlayMode 48/48、`--draw` 60 帧中心像素判 CUBE、`-zrender` 截图与 B-UNITY-03 逐像素平均差 0.49/255 |
| V6 | **PASS** | `git diff -- Assets/Game` 为空，TASKS/HANDOFF/WORKLOG 未动，HEAD 仍 `c3bd9e2`，现场恰好 17 项任务改动 |

**最终裁决：PASS（含两处受限，见下；无阻断项）**

### V1 —— 9 条新用例到底断言了什么

`ZrSceneMathTests`（7 条，纯 CPU，D3D12 无关）：

1. `ToNativeRowMajor_NonSymmetricMatrix_LandsOnTransposedIndex`：`m03=7` 的单位阵 → `dst[3]==7`、
   `dst[12]==0`、对角 4 项为 1。**memcpy 实现必红**（7 会落 12）。这是唯一能区分两种主序的构造。
2. `ToNativeRowMajor_FullMatrix_MatchesRowMajorLayout`：`m[r,c]=r*10+c` 满阵 → 16 项逐元素等于
   `dst[r*4+c]`，列主序的字节流（0,10,20,30,…）没有任何一项能过关。
3. `ToNativeRowMajor_Translation_LandsAtIndexes3_7_11`：平移 `(1.5,-2.5,3.5)` → 落在 3/7/11，
   12/13/14 为 0（"行主序存储 + 列向量"的判据）。
4. `NormalizeProjection_NearAtZeroFarAtOneAndYUp`：输入取真实 `GL.GetGPUProjectionMatrix`，断言
   ① `m11>0`；② 视空间 `z=-near` 的 NDC z ≈ 0（±2e-3）、`z=-far` ≈ 1；③ near<far（挡遮挡反转）；
   ④ 视空间 +Y 的点 NDC y>0、+X 的点 NDC x>0（Y 翻转被消掉、X 不被翻）。
5. `PackVertices_Uses32ByteStride_AndNormalizesNormals`：常量 `strideFloats=8/strideBytes=32/pos=0/
   normal=3/uv=6`；两个顶点**逐 float 校验**：位置 0..2、法线 3..5（`(0,2,0)`→`(0,1,0)`）、uv 6..7；
   零法线走 +Y 兜底；第二个顶点基址必须是 8（stride 真的跨 8 格）。
6. `PackVertices_TooSmallDestination_ReturnsZero`：2 顶点给 15 float → 返回 0（不许越界写）。
7. `IsVisible_InsideOutsideAndStraddling`：真实 Camera + `CalculateFrustumPlanes`；完全在内 true、
   相机后方 false、远侧方 false、**跨近平面 true**（保守近似不许误剔除），并断言与
   `GeometryUtility.TestPlanesAABB` 结果逐字一致（包装层不改语义）。

`ZRenderPipelineTextureLifecycleTests`（1 条，D3D12 门控）：建 320×180 → 引擎内同名纹理数 == 2 且与
自述存活数一致 → 依次改 400×225 / 512×288 / 640×360，每次重建后**引擎内对象数必须仍为 2** →
`DestroyedExternalTextureCount == 3×2` → Dispose 后归 0。

`ZRenderFrameSyncTests`（1 条，D3D12 门控）：120 帧、帧间 sleep 8ms；`frames==120`、
`blocked×2 < frames`（等的是上一帧→绝大多数不阻塞）、`avgWait<0.5ms`、回读确实发生（`ReadbackCount>0`）、
可读槽位 ∈ [0,1]。

**覆盖判定**：矩阵主序（3 条，含非对称可证伪构造）、投影深度范围（near→0/far→1 + Y 方向）、
顶点打包 stride/偏移（常量 + 逐 float + 跨顶点基址）、剔除三态（在内/在外/跨界 + 与原生判据一致）
——**四项都真有牙，不是"跑通即通过"**。
缺口（不影响裁决）：没有用例断言"桥真的调用 ZrSceneMath"（靠删除桥内副本 + `git diff` 阅读确认）；
帧同步用例只观测帧首 fence 等待，未观测"提交后是否又 WaitIdle"（见 V4 受限）。

### V2 —— 两条变异（我自己做的，不是施工留下的）

改前绿：`audit_bu04_editmode.xml`（全量）里该夹具 7/7；改后红如下；还原后 `audit_bu04_green.xml` 7/7。

**M1 矩阵：`ToNativeRowMajor` 改成直接拷字节序（等价 memcpy）**
`sha256(ZrSceneMath.cs)=c02c7fe1…` → 3 红 4 绿（投影/打包/剔除不受影响，符合预期）：
- `ToNativeRowMajor_FullMatrix_MatchesRowMajorLayout`：`destination[1] 必须是 m[0,1]
  Expected: 1  But was: 10.0f`
- `ToNativeRowMajor_NonSymmetricMatrix_LandsOnTransposedIndex`：`m03 必须落在行主序的第 3 位
  （memcpy 会落在 12） Expected: 7.0f  But was: 0.0f`
- `ToNativeRowMajor_Translation_LandsAtIndexes3_7_11`：`Expected: 1.5d +/- 9.99e-06  But was: 0.0d`

**M2 顶点打包：`at += VertexStrideFloats` 改成 `at += 7`（stride 少 1 个 float）**
`sha256=7a0d0267…` → 1 红 6 绿：
- `PackVertices_Uses32ByteStride_AndNormalizesNormals`：`Expected: 0.75d +/- 9.99e-07  But was: -4.0d`
  （顶点 0 的 uv.y 槽被顶点 1 的 position.x 覆盖）

**还原与哈希**：`cp` 回备份 → `sha256=776756503daf267274a5f7f1511b7e7ca78c6a2784af94aece6f08a22d392ae2`
（与审计开始时逐字节相同，`cmp` 输出 IDENTICAL，文件内 `MUTANT` 计数 0），重跑 7/7 绿。

### V3 —— 纹理对象计数（真跑，不是读代码）

计数方式：**引擎侧** `Resources.FindObjectsOfTypeAll<Texture2D>()` 里 `name=="ZRenderSharedTarget"`
的个数（不是管线自己的计数器；计数器只作交叉校验）。同一进程内日志原文：

```
320x180  外部纹理存活=2（创建 2 / 销毁 0）
400x225  外部纹理存活=2（创建 4 / 销毁 2）
512x288  外部纹理存活=2（创建 6 / 销毁 4）
640x360  外部纹理存活=2（创建 8 / 销毁 6）
```

→ 连续 3 次改分辨率：对象数 **2 → 2 → 2 → 2**（旧实现应为 2→4→6→8），销毁数正好 3 批×2，
Dispose 后归 0；同一数字在两次独立运行（全量 925 与过滤 3 条）里都复现。
**受限**：走的是简报允许的"模拟尺寸变化"接缝（`EnsureNativeTargetForSize`），未做真实窗口拖拽；
另外播放态用的是延迟 `Destroy`（非 `DestroyImmediate`），理论上"重建当帧"存在一帧窗口 Unity 纹理仍引用
已释放的原生资源——本次播放器 720 帧未观察异常，但建议主控后续在播放态做一次真实改分辨率复验。

### V4 —— 是否真改成 fence 双缓冲（不是挪位置）

判断依据（三条互相独立）：
1. **代码结构**：`Native/.../zrhi_d3d12_unity_target.cpp` 提交后只 `Signal(syncFence, ++syncValue)`，
   **删掉了 `device->WaitIdle()`**；帧首 `WaitForSync(syncValue)` 等的是**上一帧**的信号值（`syncValue`
   此刻尚未自增）；颜色/深度/RTV/DSV 全部 `[2]` 双槽，`readableSlot = 帧N-1 的槽`，宿主采样的正是
   刚被 fence 确认完成的那一份。旧调用点没有"换位置"式保留。
2. **现场数字（播放器 720 帧）**：`阻塞帧=0/720，平均等待=0.002ms，最近=0.002ms，在途=1`，帧率≈90；
   回归测试 120 帧同形：`blocked=0 avgWait=0.002ms inFlight=1`。
3. **旧语义变异体 M3**（帧首不等 N-1 + 提交后 `WaitIdle`，等待同样计入统计）：
   `frames=120 blocked=120 avgWait=0.801ms inFlight=0`，同一用例**变红**：
   `阻塞帧必须少于一半（实际 120/120）——否则说明又退回「每帧等本帧」了 / Expected: less than 120 /
   But was: 240`（`ZRenderFrameSyncTests.cs:74`）。还原 cpp（`sha256=3914ef87…`）后重建、复跑 3/3 绿。

**改前/改后（每帧 CPU 等待）**：旧 WaitIdle 语义 **0.801ms/帧**（120/120 帧阻塞）→ 现实现
**0.002ms/帧**（0/120 阻塞），约 400×；播放器侧同样是 0.002ms/720 帧。整段 120 帧墙钟时间两者相近
（1.71s vs 1.83s），因为测试里 GPU 活只有清屏 + 3 个 cube、且每帧还 sleep 8ms——所以**不拿墙钟当证据，
拿的是"等哪一帧"的结构判据 + 每帧等待数字**。
**受限**：该用例只统计帧首 fence 等待，若有人"先等 N-1、再在提交后又 WaitIdle"，本用例不会变红
（M3 之所以红是因为我把等待计入了同一统计）。建议补一条 `FramesInFlight >= 1` 的断言——它在
WaitIdle 版必为 0，能堵住这个回归口。

### V5 —— 回归（本次实跑）

| 项 | 结果 |
|---|---|
| 原生单测 | **317/317 passed**，776,740 断言 0 失败（`audit_bu04_native_build.log`） |
| EditMode | **925/925**，failed 0 / skipped 0（916 基线 + 9 新增；`audit_bu04_editmode.xml`） |
| PlayMode | **48/48**，failed 0（首跑即全绿，接管说明里的门事件抖动本次未复现） |
| `--draw` | 60/60 帧，exit 0，第 10/20/…/60 帧回读判 `CUBE (first draw visible)` |
| `-zrender` | 播放器 720 帧：`场景 绘制=15 帧数=480 mesh 12 上传累计=12`、`外部纹理存活=2`、90fps；截图与 B-UNITY-03 的 `zrender.png` 同尺寸(986×611)、**平均像素差 0.49/255、仅 0.547% 像素差>24**（差异集中在右侧 UI/动画相位），中心像素同为 `(13,26,38)` 清屏色；无撕裂、无上下颠倒、无花屏 |

### V6 —— 现场

- `git diff -- UnityProject/Assets/Game` 为空；`TASKS.md/HANDOFF.md/WORKLOG.md/README.md` 无改动。
- 未 commit：HEAD 仍是 `c3bd9e2`。
- `git status` 恰为 17 项任务改动（9 改 + 8 新增含 .meta），与接管说明一致；写入本节后多出第 18 项
  （`Docs/Logs/TASK-B-UNITY-04.md` 本身，即本次验收结论）。
- 本次验收自身产生的副作用已清除并归档到 `TempLogs/archive_pre_audit/bu04/`：
  2 个 PlayMode 自动重写的 `Docs/Parity/crowd_r9_15_*.png`（已 `git checkout` 回 HEAD）、
  构建自增的 `Tools/version.json` 与 `ProjectSettings.asset`（已还原）、`zrender.dll`（用审计前备份按字节恢复，
  `sha256=6b173a5d…`；重编译会改 PE 时间戳，故不采用重编译产物）。
- 代码文件编码未破：`ZrSceneMath.cs`/`zrhi_d3d12_unity_target.cpp` 均 no-BOM、CRLF=0。

### 给主控的两条环境/加固提示

1. **`Tools/build_native.ps1` 在本机直接跑会失败**：PATH 上的 `cmake` 是 MSYS2 版（`C:/msys64/...`），
   `-E make_directory` 探测报 `Error creating directory ... Permission denied`，脚本的 stderr 在
   `$ErrorActionPreference='Stop'` 下直接终止，**不会回退到 VS 自带 cmake**。本次做法是在同一
   PowerShell 会话里把 VS cmake 目录前置到 `PATH` 后执行（`BuildTools\...\CMake\bin`），构建即成功；
   建议脚本修掉"探测失败即抛"的行为（改为 try/catch 继续下一个候选）。
2. suggest 同上 V4 受限：给 `ZRenderFrameSyncTests` 补 `FramesInFlight >= 1` 与"一次真实窗口改分辨率"的
   播放态用例，可把 V3/V4 的两处受限也收掉。

