# TASK-X-ARCH-01 —— 架构校验器矩阵补登记：ZFrame.Render.Probes（B-PROBE-02 债）

- **轨道**：X / HYG（微任务）
- **状态**：doing
- **派发**：主控，2026-11-14
- **背景**：B-PROBE-02（72126a0）新增 `ZFrame.Render.Probes.asmdef`（Editor-only 探针，autoReferenced=false，引用 Bindings+URP Runtime）未登记 ArchitectureValidator 期望矩阵 → EditMode 2 例红（ArchitectureValidator）。B-PROBE-02 验收未跑 arch 段——**主控验收清单口径债**，X-ARCH-01 修补并把"探针类任务验收必跑 arch 段"写入流程。

## 任务简报（主控写）

1. **矩阵补条目**（`UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs`）：`["ZFrame.Render.Probes"] = ...`——AllowedReferences 以**实际 asmdef 文件**为准读取（读 UnityProject/Assets/ZRender/Tools/ZFrame.Render.Probes.asmdef 的 references 列表逐个填，格式对照既有条目如 ZFrame.Data.Addressables 的包引用写法）；注释写明依据（B-PROBE-02 探针程序集，Editor-only，不接运行时流程）
2. **单测**：ArchitectureValidatorTests 的"矩阵条目数=实际 asmdef 数"校验随条目自动满足；如测试有写死计数，同步改并在报告说明
3. **验证**：run_tests.ps1 -Segments arch（exit 0）+ -Segments editmode（≥1182，failed=0——B-PLANET-06 的 WIP 原生改动不影响 C# 段）；naming 段顺跑
4. **白名单**：ArchitectureValidator.cs、ArchitectureValidatorTests.cs（如需）、本日志；其余不碰；不碰 Native/（B-PLANET-06 在途）

## 完成判据

- arch 段 PASS（矩阵条目数一致）；EditMode failed=0；naming PASS
- 验收：本任务为单行级微修，验收由 B-PLANET-06 的验收代理捎带执行（主控显式决定，记录在案）：核对本条目存在+arch exit 0+EditMode failed=0，结论并入 B-PLANET-06 验收报告，X-ARCH-01 由主控依该报告闭环

## 施工记录（2026-11-14，施工子代理）

- **改动文件**：仅 `UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs`（+10 行，UTF-8 无 BOM / LF）。`ArchitectureValidatorTests.cs` **无需改**——`ExpectedMatrix_MatchesActualAsmdefCount` 为动态统计（实际 asmdef 数 − Tests 豁免数），无写死计数，新增条目后自动满足。
- **矩阵条目**（插在 `ZFrame.Render` 条目之后）：

  ```csharp
  ["ZFrame.Render.Probes"] = new AsmdefRule
  {
      AllowedReferences = new[] { "ZFrame.Render.Bindings", "Unity.RenderPipelines.Universal.Runtime" },
      EditorOnly = true,
  },
  ```

  - AllowedReferences 逐项对齐 `UnityProject/Assets/ZRender/Tools/ZFrame.Render.Probes.asmdef` 实际 references（共 2 项：`ZFrame.Render.Bindings`、`Unity.RenderPipelines.Universal.Runtime`）；包引用写法沿用既有先例（同 `Unity.Addressables` / `Unity.InputSystem` 的程序集名直写）。
  - `EditorOnly = true` 与该 asmdef `includePlatforms: ["Editor"]` 一致；注释载明依据：B-PROBE-02（72126a0）探针程序集，Editor-only 诊断用途，不接运行时流程，`autoReferenced=false` 不对外扩散。
- **验证**（`run_tests.ps1`，Unity 单实例纪律，全程未杀进程；三条机器静默检查均 VERDICT: BUSY（Taskmgr/ZCode 占 CPU≥20%），耗时数字不做跨跑比较）：
  - arch：**PASS，exit=0**（`TempLogs/arch_report.txt`：asmdef 扫描数 27，期望矩阵条目 13，违规 0）
  - editmode：**PASS，total=1182 passed=1182 failed=0 skipped=0**（满足 ≥1182 & failed=0；B-PLANET-06 原生 WIP 不影响 C# 段）
  - naming：**PASS，exit=0**
- **捎带核对要点**（供 B-PLANET-06 验收代理）：① ArchitectureValidator.cs 含 `["ZFrame.Render.Probes"]` 条目且 AllowedReferences 与 asmdef 一致；② `-Segments arch` exit 0；③ `-Segments editmode` failed=0（total=1182）。证据日志：`TempLogs/runtests_arch.log`、`TempLogs/runtests_editmode.xml`、`TempLogs/runtests_naming.log`。
- **白名单遵守**：未 commit、未改看板/HANDOFF/WORKLOG、未碰 Native/（`git status` 中 Native/* 与 zrender.dll 改动属 B-PLANET-06 在途工作，非本任务产物）。
