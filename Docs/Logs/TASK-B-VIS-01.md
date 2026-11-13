# TASK-B-VIS-01 —— 多视图共享可见性核实（B-R4-02 交付范围审计裁决）

- **轨道**：B / VIS（08 需求对照 §二#4「⚠️半·未找到共享可见性/剔除结果的结构」复核）
- **状态**：done（审计裁决任务，零代码改动）
- **派发**：主控，2026-11-14；**审计执行**：只读审计子代理（与施工无关）；**裁决**：主控
- **背景**：两份材料矛盾——外部审计称"未找到"，看板 B-R4-02（2e2c667）标 done。本任务只读核实，不盲信任何一边。

## 任务简报（主控写）

核实四点：①B-R4-02 的"共享可见性"共享什么；②可见性结构现状（file:line）；③主视图/CSM 级联/附加视图是否共享同一份可见性数据（有无二次剔除）；④裁决 A/B/C + 若有缺口给最小补全方向与可证伪验收判据。

## 审计报告（只读审计子代理，2026-11-14，基线 HEAD=47edde7，Native 与 cdf4eb2 一致）

**1. B-R4-02 交付的"共享可见性"共享什么**：是**多个视图共享同一份可见性数据**（zrender 内部），非 kernel↔renderer 共享。简报明确"zkernel/ 不动；纯 CPU 逻辑"（TASK-B-R4-02.md:19），交付 `VisibilitySet`：kMaxViews=8、viewMask 位过滤、`Iterate(viewIndex,fn)`（:11、:63）；遗留项自认"分视图剔除（frustum/occlusion 产出 viewMask）是天然下一步"（:96）。

**2. 可见性结构**：`Native/zrender/draw_list.h:38-115` `VisibilitySet{ vector<Entry{renderItemId,viewMask}> }`，kMaxViews=8（:42），AddItem OR 合并（:51-62）、AppendItem O(1)（:68）、ViewMaskOf（:81）、Iterate 位扫描（:95-110）；由 `DrawList::m_visibility` 持有（:226），复用 Reset（draw_list.cpp:109）。掩码由调用方 Add 时给定：`ShadowViewMask`（shadows.h:113-118，bit0=主视图+bit1..4=级联）、`kOutlineViewBit`（draw_list.h:132-145）。**全 Native 无 frustum/occlusion 剔除**（grep 视锥仅命中光照簇 AABB 与 CSM 切片注释）。

**3. 一帧内主视图/CSM 级联/附加视图是否共享**：共享，无二次剔除。真实渲染路径 `Native/tests/zrhi_d3d12_window.cpp`：单一 `DrawList`，每项仅 Add 一次带组合掩码（:2423-2424）；4 级联阴影列表 = 同一 VisibilitySet 的 `Iterate(ShadowViewId(c))`（:2428-2433）由各 `ShadowCascadePass` 消费（:2521-2544）；主 Pass 消费同一 DrawList 的 `Batches()`（:2578 起）；描边视图 bit5 同理（:3622-3634）。测试佐证：test_shadows.cpp:225-226 断言单条目 ViewMaskOf==0x1F（1 条可见性记录服务 5 视图）、玻璃项在所有级联 Iterate 均不出现（:232-236）；test_draw_list.cpp:30-47 三视图交叠精确断言。

**4. 裁决**：A（需求已满足）。一次 Add（0x1F）→ 5 视图各自 Iterate 过滤消费 → 主 Pass 批 + 4 级联 Pass + 描边 Pass，全库无第二个剔除调用点。

**5. 加固建议（判 A 仍建议）**：`DrawList::Add/VisibilitySet::AddItem` 加帧内录入计数断言——N 项 × (1 主+4 级联+1 描边) 录入次数 == N（而非 6N）、消费 == 6N；将来实现真 frustum cull 时做成 `CullToViewMask(frustum)` 纯函数一次性产出掩码写入同一 VisibilitySet，杜绝每视图重复剔除。改动面仅 zrender（draw_list.h/shadows.h + 新 cull 纯函数 + 测试），工作量 S~M。

## 裁决与验收结论（主控）

- **裁决 = A，需求已满足**；外部审计「未找到共享可见性结构」**不成立**——其检索大概率只盯"剔除/frustum"字样而漏了 viewMask 语义。`Docs/Planning/08-原始需求对照-2026-11-13.md` §二#4 相应改判：**已实现（含注记）**。
- **诚实注记**：当前掩码产生是标志位（castShadow/outline），**真 per-view frustum culling 尚不存在**；"共享可见性"成立且无重复剔除成本，但若未来把需求读作"已去重的真实逐视锥剔除"，缺口真实存在。→ 登记 **B-VIS-02**（todo，不挡路）：CullToViewMask 纯函数 + 录入/消费计数断言（变异可证伪）。
- 验收 = PASS（审计证据链与裁决一致；行号抽查可信——draw_list.h/测试断言均为既有绿测试资产）。
