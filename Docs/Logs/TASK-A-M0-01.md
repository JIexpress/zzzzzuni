# TASK-A-M0-01 Unity 工程创建与骨架

状态: doing | 派发: 2026-10-23 | 施工: general-purpose 施工子代理(重派) | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：在 `UnityProject/` 创建 Unity 2022.3.62 LTS 工程，建立目录与 asmdef 骨架，产出可编译的 Boot 场景。

**环境**（先读 AGENTS.md / HANDOFF.md）：
- Unity.exe: `C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe`
- 仓库根: `C:/Users/1/Desktop/zcode/GameFramework`（UnityProject 在其下）

**步骤建议**：
1. 批处理创建工程（默认内置管线模板，无 URP）：
   `"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -quit -createProject "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" -logFile create.log`（注意可能需数分钟；若 license 报错→停止并在报告注明）
2. 在 `Assets/Editor/` 写一个 `ZFrameSetup.cs`（Editor 程序集），含静态方法（可被 `-executeMethod` 调用）：
   - 创建目录骨架：`Assets/ZFrame/{Core,Data,Gameplay,View,Editor,Tests}`、`Assets/Game/{Domain,View,Bootstrap}`、`Assets/Art/{Models,Models/Raw,Materials,Textures,Animations}`、`Assets/Audio`、`Assets/Prefabs`、`Assets/Scenes`、`Assets/UI`、`Assets/Configs`、`Assets/Settings`、`Assets/Plugins`
   - 创建并保存空场景 `Assets/Scenes/SCN_Boot.unity`，加入 BuildSettings 场景列表（EditorBuildSettings.scenes）
3. 按 asmdef 矩阵（方案 §3.2）创建 asmdef（引用关系按矩阵；`ZFrame.Data.Addressables` 本任务**跳过**——Addressables 包未装）：
   - `ZFrame.Core`（无引用）、`ZFrame.Data`（引 Core）、`ZFrame.Gameplay`（引 Core）、`ZFrame.View`（引 Core/Data/Gameplay）、`ZFrame.Editor`（editor-only，引上述全部）、`Game.Domain`（引 Core/Gameplay/Data）、`Game.View`（引 Core/Data/Gameplay/ZFrame.View/Game.Domain）、`Game.Bootstrap`（引全部运行时）
   - 每个 asmdef 内放一个占位类：`namespace <同名>; public static class ModuleInfo { public const string Version = "0.0.1"; }`
4. 修改 ProjectSettings：productName=ZFrame、companyName=ZFrame（文本替换 ProjectSettings.asset 即可）
5. 验证编译：再次批处理 `-projectPath ... -quit -logFile verify.log`，退出码 0 且无编译错误
6. 清理：删除 create.log/verify.log 等临时日志（不留在仓库）

**约束**：
- 不安装任何包；不动 Packages/manifest.json（保持默认）
- 不 git commit（主控专属）；不修改治理文件
- 文件规范见 Docs/Standards/file-conventions.md（UTF-8 无 BOM、LF）

**交付物**：目录骨架、8 个 asmdef+占位类、SCN_Boot.unity、ProjectSettings 修改、本日志"施工报告"节（含变更文件清单、自测输出摘要、Unity 日志关键行）

## 施工报告（施工代理写）

> 历史：2026-10-23 首次施工因 Unity 许可证未激活阻断，本次为重派

**结果：施工完成。简报步骤 1-6 全部执行，验证编译通过（退出码 0，8 个 asmdef 程序集全部产出 DLL）。**

### ① 实现摘要

- 步骤 1 批处理 `-createProject` 成功：许可证正常（`Successfully updated license` / `Successfully resolved entitlement details`），退出码 0；内置管线模板，`Packages/manifest.json` 为 Unity 默认（仅 com.unity.modules.*），未触碰。
- 步骤 2 写入 `Assets/Editor/ZFrameSetup.cs`（编入 Assembly-CSharp-Editor，命名空间 ZFrame.EditorSetup），批处理 `-executeMethod ZFrame.EditorSetup.ZFrameSetup.Setup` 运行成功：创建 21 个骨架目录、保存空场景 `Assets/Scenes/SCN_Boot.unity`、写入 EditorBuildSettings 场景列表（唯一且首位）。
- 步骤 3 创建 8 个 asmdef + 占位类 `ModuleInfo`（namespace=asmdef 名，`Version = "0.0.1"`）；`ZFrame.Editor` 仅 includePlatforms=["Editor"]；引用矩阵按简报（Game.Bootstrap 引全部 6 个运行时程序集）；`ZFrame.Data.Addressables` 按简报跳过。
- 步骤 4 文本替换 `ProjectSettings/ProjectSettings.asset`：companyName/productName 均改为 ZFrame；验证运行后复检仍为 ZFrame。
- 步骤 5 验证编译批处理退出码 0，日志无 error；`Library/ScriptAssemblies/` 下 8 个 asmdef DLL（+pdb）与 Assembly-CSharp-Editor.dll 全部产出。
- 步骤 6 三个临时日志（系统 Temp 下 zf_m001_{create,setup,verify}.log）已删除，仓库与 UnityProject 内无 .log 残留。

### ② 变更文件清单（目录级）

- 新增 `UnityProject/`（整个工程）：
  - `Assets/`：21 个骨架目录（ZFrame/{Core,Data,Gameplay,View,Editor,Tests}、Game/{Domain,View,Bootstrap}、Art/{Models,Models/Raw,Materials,Textures,Animations}、Audio、Prefabs、Scenes、UI、Configs、Settings、Plugins）及目录 .meta；`Editor/ZFrameSetup.cs`；`Scenes/SCN_Boot.unity`；8 个 asmdef + 8 个 ModuleInfo.cs 及 .meta
  - `ProjectSettings/`（Unity 生成全套 + productName/companyName=ZFrame + EditorBuildSettings 含 SCN_Boot）
  - `Packages/manifest.json`（Unity 默认生成，未修改）
  - `Library/ Temp/ Logs/ UserSettings/`（编辑器生成产物，按规范不入库）
- 修改 `Docs/Logs/TASK-A-M0-01.md`（本报告与头部"施工"字段）
- 未动：治理文件（AGENTS/HANDOFF/TASKS/WORKLOG/DECISIONS/Standards）、Native/、Tools/

### ③ 自测结果

- 命令 1（创建工程）：`Unity.exe -batchmode -quit -createProject .../UnityProject -logFile <Temp>/zf_m001_create.log` → 退出码 **0**；日志关键行：`[Licensing::Client] Successfully updated license`、`Exiting batchmode successfully now!`、`Application will terminate with return code 0`；无上次阻断行（No valid Unity Editor license / No ULF license found 均未出现）。
- 命令 2（骨架）：`... -projectPath .../UnityProject -executeMethod ZFrame.EditorSetup.ZFrameSetup.Setup -quit` → 退出码 **0**；关键行：`[ZFrameSetup] Scaffold complete: directories, SCN_Boot, BuildSettings entry.`
- 命令 3（验证编译）：`... -projectPath .../UnityProject -quit` → 退出码 **0**；对日志 grep `error CS|CompilerError|Compilation failed|Scripts have compiler errors|Aborting batchmode|No valid Unity Editor license` 零命中；`Library/ScriptAssemblies/` 产出 ZFrame.{Core,Data,Gameplay,View,Editor}.dll + Game.{Domain,View,Bootstrap}.dll + Assembly-CSharp-Editor.dll（各带 .pdb）。
- BuildSettings 复核：`ProjectSettings/EditorBuildSettings.asset` 中 `m_Scenes[0]` 为 `path: Assets/Scenes/SCN_Boot.unity, enabled: 1`（唯一条目）。
- 文件规范抽查：8 个 .asmdef + 9 个 .cs 全部 UTF-8 无 BOM、LF、末尾换行（od/grep 逐文件核查通过）。

### ④ 已知风险/遗留

- 本机 Unity 为 china_unity 构建，批处理启动阶段存在许可证握手重试噪音（`HandshakeResponse reported an error` 后自动重连成功并 `Successfully updated license`），不影响结果；验收代理自跑验收清单第 1 条时应以**退出码 + 编译 error 关键字**为准，勿被启动期 licensing 行误判。
- `Assets/Editor/ZFrameSetup.cs` 为一次性脚手架脚本（幂等，可安全重复执行），暂保留在仓内；后续任务如需可并入 ZFrame.Editor 程序集或删除。
- `Assets/ZFrame/Tests/`、`Assets/Plugins/` 目前为空目录（仅含 .meta），按骨架要求预置，属预期。
- 批处理各次耗时数分钟属正常；验收代理复跑时建议 timeout ≥ 10 分钟。

## 验收清单（主控写）

1. 批处理打开工程退出码 0，日志无 error（验收代理自跑一遍）
2. `Assets/Scenes/SCN_Boot.unity` 存在且在 EditorBuildSettings 场景列表首位
3. 8 个 asmdef 齐全且引用矩阵正确（对照方案 §3.2）
4. 目录骨架与方案 §3.3 一致（Art 子目录齐全）
5. productName=ZFrame
6. 文件规范抽查：新建 .cs/.asmdef 无 BOM、LF
7. 任务日志"施工报告"节完整（变更清单/自测结果/风险）

## 验收结论（验收代理写）

**总判定：PASS**（2026-10-23，独立复跑全部 7 项）

1. 批处理编译：退出码 0；日志 grep `error CS|CompilerError|Aborting` 零命中；`Exiting batchmode successfully` + `return code 0`（china_unity 许可证握手噪音未误判）；`Library/ScriptAssemblies/` 8 个 asmdef DLL+pdb 齐全
2. `Assets/Scenes/SCN_Boot.unity` 存在（3566 字节）；EditorBuildSettings.scenes 唯一且首位条目 `path: Assets/Scenes/SCN_Boot.unity, enabled: 1`
3. 8 个 asmdef 齐全，引用矩阵逐文件比对方案 §3.2 与简报全部一致：ZFrame.Core 引用为空；ZFrame.Data/Gameplay 仅引 Core；ZFrame.View 引 Core/Data/Gameplay（无 Game.*）；ZFrame.Editor 引 4 个 ZFrame 运行时程序集且 includePlatforms=["Editor"]；Game.Domain 引 Core/Data/Gameplay；Game.View 引 Core/Data/Gameplay/ZFrame.View/Game.Domain；Game.Bootstrap 引全部 6 个运行时程序集
4. 目录骨架齐全：Art/{Models,Models/Raw,Materials,Textures,Animations}、Audio、Prefabs、Scenes、UI、Configs、Settings、Plugins 均存在（另 ZFrame 六子目录、Game 三子目录、Plugins/ZRender 为并行 B 轨任务产物，忽略）
5. ProjectSettings.asset 第 15-16 行：`companyName: ZFrame`、`productName: ZFrame`
6. 文件规范：ZFrameSetup.cs + 8 个 ModuleInfo.cs 共 9 个 .cs 逐文件核查，首 3 字节均非 EF BB BF（无 BOM）、CR 计数全为 0（纯 LF）
7. 施工报告完整：含重派历史行 + ①实现摘要/②变更文件清单/③自测结果/④已知风险四小节齐全

验收过程临时日志已删除；未做任何 git 操作。
