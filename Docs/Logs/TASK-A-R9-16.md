# TASK-A-R9-16 —— URP 变体收敛（12288 变体成本中心）

状态: doing | 派发: 2026-11-04 | 施工: builder-r9-16（general-purpose） | 验收: verifier-r9-16（须与施工不同代理） | commit: （待填）

## 任务简报（主控写）

### 背景（离线体检已核实的硬事实）

- A-AND-06 的 IL2CPP Android 正式包构建耗时 **29 分 16 秒**，其中 **URP 着色器变体 12288 个 ≈ 10 分钟** + IL2CPP C++ 编译 1418 个 `.o` + Gradle。这是目前最明确的构建成本中心。
- `ProjectSettings/GraphicsSettings.asset`：4 个 `*Stripping` 全 **0**、`m_PreloadedShaders: []`、`m_ShaderVariantLogLevel: 0`、`m_AlwaysIncludedShaders` 8 项。
- `Assets/Settings/Pipeline/URP_Medium.asset`：`m_UseSRPBatcher: 1`、`m_ShaderVariantLogLevel: 0`、`m_RequireDepthTexture: 0`、`m_RequireOpaqueTexture: 0`、`m_MSAA: 2`、`m_RenderScale: 1`。
- 激活管线判定（锁版本）：**Unity 2022.3.62f3c1 + URP 14.0.11**，Windows x64（780M 核显）/ Android IL2CPP arm64。内置管线 / HDRP 结论不适用。

### 目标（按此实现，细节可斟酌但报告说明）

1. **可复核的变体计数口径**：先调研 Unity 2022.3 在 `-batchmode` 下可用的统计 API（`ShaderUtil` / `IPreprocessShaders` 回调计数 / 构建日志解析），落一个 Editor 侧工具（菜单项或 `Tools/` 脚本），输出「当前 URP shader 的关键字组合与变体数」。**若 batchmode 拿不到真实变体数，如实标受限**，并给出「下次 Windows / Android 构建时如何测量」的可执行步骤，不许编数字。
2. **剥离器**：实现 `UnityEditor.Build.IPreprocessShaders`（Editor 程序集），按**可配置规则**剥离本工程明确用不到的 keyword 组合（候选：`DEBUG_DISPLAY`、`_WRITE_RENDERING_LAYERS`、`_ADDITIONAL_LIGHTS_VERTEX`、`_MAIN_LIGHT_SHADOWS_CASCADE` 与画质档不匹配的组合等——**施工须先核对实际 shader 与档位矩阵，不许照抄这份候选表**）。
   - **默认关闭 / 白名单保护**：必须有一个显式开关（EditorPrefs 或配置文件）才启用剥离，避免误伤导致粉红材质；报告写清「误伤的判据」与回滚方式。
   - **不许把 URP 必需变体剥掉**：至少覆盖当前三档（URP_Low/Medium/High）与 Volume 效果所需的组合。
3. **纯函数规则 + EditMode 测试**：把「该 keyword 组合是否剥离」抽成可测纯函数，测试用例覆盖命中 / 不命中 / 边界（空关键字、大小写、平台分支），**变异有牙**。
4. **改前改后对照口径**：给出「规则命中数 / 预计减少的变体数」的可复核测量方法，附改前改后的同一命令输出。**不承诺帧率、不编构建时长**；若无法实测，明确写「未做完整构建对照」及原因。
5. **零资产污染**：不改 `URP_*.asset`、`QualitySettings.asset`、`GraphicsSettings.asset`、游戏内容、材质、场景；不改原生 C++；不改 `TASKS/HANDOFF/WORKLOG/README/DECISIONS`。
6. **回归**：EditMode ≥ **880**、PlayMode ≥ **48**、native **311** 不降、三校验器 exit 0；不得引入新的编译警告（/W4 /WX 是原生口径，C# 侧零 error CS）。

### 允许的降级出口（如实即可，不算 FAIL）

若 Unity 2022.3 的批处理环境确实无法统计真实变体数、或 `IPreprocessShaders` 在本工程 URP 包版本下行为不可验证，则交付「规则 + 纯函数 + 单测 + 静态体检报告 + 可执行测量步骤」，并在报告中把「真实构建变体数 / 构建时长对照」明确列为**未验证**，不得写成已收敛。

### 约束

- 白名单：`UnityProject/Assets/ZFrame/Editor/**`、`UnityProject/Assets/ZRender/Editor/**`（若合适）、`UnityProject/Assets/ZFrame/Tests/**`、`Tools/**`（仅当加统计命令）、`Docs/Logs/TASK-A-R9-16.md` 的「## 施工报告」节、`Docs/Standards/**`（仅当写规范）。
- 禁止：`Native/**`、`Assets/Game/**`、`Assets/Audio/**`、`Assets/Settings/Pipeline/*.asset`、`ProjectSettings/**`、`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md`；不 `git add` / 不 `git commit`；UTF-8 无 BOM + LF（写完自查，Windows 脚本别写成 CRLF）；`.meta` 由 Unity 生成，不手改。
- 不要 `git checkout --` 丢弃 `UnityProject/Assets/Plugins/ZRender/zrender.dll`；不要整仓 checkout。
- 唯一可写日志节是「施工报告」；**不要写「验收结论」**，不要勾选验收清单。

### 环境纪律（真实踩过，详见 `Docs/交接档案.md` §3）

- 跑测试前清 8 个代理变量（大小写成对），否则 MSBuild / Unity 启动失败。
- Unity 批处理单实例：跑前查进程 + 删 `UnityLockfile`；跑测试**勿带 `-quit`**；xml 出现但 5 分钟不退 → `taskkill //PID <pid> //F`；证据以本次新写 results.xml 为准。
- Bash 侧禁止转调 powershell → 跑 .ps1 用 PowerShell 工具；本机 GUI Editor 加 `-force-d3d12` 会挂起。
- 清理用 `mv` 归档，不要 `rm -rf`；跑完查 `git status`。

### 铁律

施工与验收必须不同代理；一任务一 commit；日志四节齐全；验收代理须亲自做「修前必红」定向变异；「说变快了必须附改前改后的测量方式」；结论必须锁 Unity 大版本 + 渲染管线 + 目标机型。证据不齐写受限，绝不把「部分完成」说成「已完成」。

## 施工报告（施工代理写）

（待填写）

## 验收清单（主控写）

- [ ] V1 变体计数工具可运行，给出实测数字与命令；拿不到则如实标受限并给可执行测量步骤
- [ ] V2 剥离器默认关闭 / 白名单保护，规则有判据（不是照抄候选表），误伤可回滚
- [ ] V3 纯函数规则 EditMode 用例真实（行号），变异有牙
- [ ] V4 改前改后同一命令输出对照；未做完整构建对照须明确写「未验证」
- [ ] V5 零资产污染（`git status` 证明）、回归数字、报告四节 + 验收亲自复做变异

## 验收结论（验收代理写）

（待填写）
