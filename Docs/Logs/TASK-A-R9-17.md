# TASK-A-R9-17 —— URP 变体剥离的真实收益（变体数 + 构建时长对照）

> 补 A-R9-16 的核心受限项。当时 `stripped=14` 只是**规则命中数**，不是减少的变体数；
> **真实构建变体数 / 构建时长对照从未做过**。如果剥离其实没省时间，那 21 条规则 + 36 条用例就是白做。
> 本任务只做测量，不改剥离器规则。**禁止 git commit**。

## 要回答的问题

在同一台机器、同一命令、同一平台同一档位下：
- 剥离**关**（默认）→ 构建变体数 = ? 构建耗时 = ?
- 剥离**开** → 构建变体数 = ? 构建耗时 = ?
- 差值是多少？剥离器是否真的删除了变体（`IPreprocessShaders` 是否真被调用）？

## 步骤（照 A-R9-16 R7 走，不要自创）

### 1）开变体日志（唯一需要改的资产，事后精确还原）
`UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset:33` `m_ShaderVariantLogLevel: 0` → `1`。
- **只改这一行**，改前记下原值
- 若 `1` 时日志仍无变体行，可试 `2`（更详细），报告里说明用了哪一档
- 这 3 个 `URP_*.asset:113` 也有同名字段，**先只改 GlobalSettings**，不够再逐个试并记录

### 2）环境准备
- `Tools/check_machine_quiet.ps1` 确认 QUIET
- PowerShell 侧清 8 个代理变量；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- Unity 单实例：查 `Unity.exe` 进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑完 Unity 可能僵死：轮询 results，5 分钟无退出则 `taskkill //PID //F`

### 3）关态构建（基线）
- 确认剥离器关：EditorPrefs `ZFrame.UrpVariantStripper.Enabled` 默认 false（**先确认，别假设**）
- 跑 `Tools/build_player.ps1`（Windows x64，比 Android 快得多）
- 记录：脚本总耗时、Unity 日志里的变体汇总行（`total variants` / 每个 shader 的 `variants: N`）
- 变体行在哪：构建日志或 `Editor.log`，把**实际命令与日志路径**写进报告

### 4）开态构建（对照）
- `Unity.exe -batchmode -executeMethod ZFrame.Editor.UrpVariantStripper.SetEnabledFromCommandLine -urpStripEnabled true`（注意：**不要加 `-quit`**）
- **同一命令、同一机器、同一平台、同一档位**再跑一次 `build_player.ps1`
- 记录同样两个量

### 5）还原
- `git checkout -- UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset`（**只这个单文件**）
- EditorPrefs 恢复 false
- **不要整仓 checkout**；`zrender.dll` 时间戳噪声按纪律**不要**丢弃
- 报告里贴 `git status` 证明只还原了该还原的

### 6）误伤检查（若构建出包）
- 装包（或跑 exe）看：粉红材质 / Bloom / 阴影 / 色调映射是否异常

## 判据

- 给出**两组数字**（变体数、构建耗时）+ **差值**，且注明测量方式（哪个日志、哪一行）
- 明确回答：`IPreprocessShaders` **有没有真被调用**、有没有真删掉变体
- 如果测不出差异（例如 batchmode 根本不打变体日志、或 Windows 变体本来就少），**如实说测不出** + 说明卡在哪一步、下次换 Android 要注意什么
- 数字必须来自实际日志，**不许估算、不许编**

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- **不改剥离器规则**（本任务只测量）；不改游戏内容
- 资产改动**精确到单文件**还原，不整仓 checkout
- 说「变快了」必须附「怎么测的」，两个数字必须同源同法

## 环境纪律（真实踩过）

- 构建前 `unset http_proxy https_proxy all_proxy`（Bash）；PowerShell 清 8 个变量
- **PATH 上的 cmake 是 MSYS2 版无写权限** → 原生构建走 `Tools/build_native.ps1`
- Unity 批处理**单实例**；**勿加 `-quit`**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- PS 5.1 对原生 exe 加 `2>&1` 会把退出码变 1（假失败）
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

---

## 施工报告（施工代理写）

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 两组数字同源同法（同一个日志、同一行口径）
- [ ] V2 明确回答 `IPreprocessShaders` 是否被调用、是否真删变体
- [ ] V3 资产精确还原（`git status` 证明无多余改动）
- [ ] V4 若测不出，如实说明卡点，无编造
- [ ] V5 未改剥离器规则；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
