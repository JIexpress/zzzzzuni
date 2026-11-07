# TASK-A-R9-18 —— 打开 URP 自带后处理变体剥离（`m_StripUnusedPostProcessingVariants`）

> A-R9-17 的结论：自研 21 条规则 0 命中（结构性冗余），但顺带发现 URP 自带的
> `m_StripUnusedPostProcessingVariants` 一直是 `0`（关）。本任务测这个开关有没有真收益。
> **禁止 git commit**。

## 基线（A-R9-17 已实测，无需重跑关态）

| 项 | 值 |
|---|---|
| 编译变体数 | **7830** |
| 编译Σ | 210.00 s |
| Unity 构建 | 284.9 s |
| 档位 | StandaloneWindows64 / Release / d3d11 / 冷 ShaderCache |
| Unity / URP | 2022.3.62f3c1 / 14.0.12 |

## 要改的（只一处）

`UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset:28`
```
m_StripUnusedPostProcessingVariants: 0  →  1
```
**只改这一行**，别动 `m_StripUnusedVariants`（已是 1）和 `m_StripUnusedLODCrossFadeVariants`（已是 1）。

## 步骤

1. **开变体日志**（数变体必需）：同文件 `:33` `m_ShaderVariantLogLevel: 0 → 1`，事后还原
2. 改 `m_StripUnusedPostProcessingVariants: 1`
3. QUIET 检查 + 清代理 + 单实例（查 Unity.exe + 删 Temp/UnityLockfile）
4. **冷 ShaderCache**：`mv` 掉 `Library/ShaderCache` + `ShaderCache.db`（否则会走增量，样本作废——A-R9-17 踩过）
5. **构建一次**：`Tools/build_player.ps1 -TimeoutSec 1800`（同一命令同档位）
6. **记四个量**（与基线同法）：
   - 编译变体数 = 所有 `compiled N variants` 的 N 求和
   - 编译Σ = 上述行的秒数求和
   - Unity 构建 = 日志 `##utp` 的 `duration`
   - 墙钟 = Stopwatch
7. **误伤检查**（本任务关键，剥后处理变体可能真删掉要用的）
   - 跑构建出的 exe，看：Bloom 是否还在 / 色调映射 / 阴影 / 有没有粉红材质 / 黑屏
   - 用现有截图对比基线（`Docs/Parity/` 下有历史图，或自己跑 `-zrender` 抓一张）
8. **还原**：`git checkout -- UniversalRenderPipelineGlobalSettings.asset`（单文件），
   `Library/ShaderCache` 不必还原（可再生）；贴 `git status`

## 判据

- 给出**变体数 / 编译Σ / Unity 构建 / 墙钟**四个量 + 与基线的差值
- 明确回答：打开后**变体少了多少**？是否值得？
- **误伤结论**：画面有没有变化（附证据：截图对比或像素差）
- 如果变体没降或降得很少，如实说（可能这个项目后处理变体本来就少）
- 数字来自日志，不估算

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改剥离器规则；不改游戏内容
- 资产改动**精确到单文件**还原，不整仓 checkout；`zrender.dll` 噪声不丢弃
- 说「变快了」必须附「怎么测的」，两个数字同源同法

## 环境纪律（每条真实踩过）

- 构建前 Bash `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**；PowerShell 清 8 个代理变量
- Unity 批处理**单实例**；**勿加 `-quit`**；僵死则 `taskkill //PID //F`
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- PS 5.1 对原生 exe 加 `2>&1` 会把退出码变 1（假失败）
- **每次测量前清 ShaderCache**，否则样本作废
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

---

## 施工报告（施工代理写）

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 四个量与基线同源同法
- [ ] V2 变体数降了多少，是否值得
- [ ] V3 误伤检查有画面证据（不是「看起来正常」）
- [ ] V4 资产精确还原
- [ ] V5 未改规则；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
