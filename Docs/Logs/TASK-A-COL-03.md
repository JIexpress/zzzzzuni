# TASK-A-COL-03 · Colony 存档/读档（SaveSystem 接入 NpcSlot 确定性实例化）

> 简报（主控写）｜2026-11-22｜施工：<待填>｜验收：<待填>｜前置：A-COL-01（16afa34）、A-COL-02（数据管线）

## 一、任务简报（唯一需求来源）

### 背景

《声望认知与人口》§4.4 的存档方案：**种子固定 → 结果可复现**；存档只需要存"哪些 slot 已实例化"+ 已实例化的内容，读档后同一个 NPC 性格不变。A-COL-01/02 已有 GameWorld（具名/聚落/背景 slot/契约/抗议/声望/支线延迟队列）与数据驱动建局。本任务给世界加上**存档/读档**。

### 设计要点

1. **`ColonySaveData`**（纯 DTO，`Game/Domain/Colony/`）：worldSeed、day、背景层已实例化 slot 列表 + 每个档案字段（name/job/origin/baseLoyalty/grievanceBias/speechTrait）、具名 NPC 全量真实状态（loyalty/grievance/organization/factionLean/maskStrength/knownLoyalty/lastObserved/recognitionScore + 契约 stage/violations/daysInStage/cooperation/intelCarried + protest level/forms/isCovert/discovered + culture 八浮点 + MF 六权重 + image.entries/beliefs）、聚落参数（含 grievanceFloor/leanMean/leanVariance/namedNpcs）、地区（policyExecution/policyBenefit/deeds/rumorPool）、governor（production/tradeValue/militaryDeed/credibility/promises/repressionCount/lastVisit/informants/intel 列表含 boughtDay/validDays/forged）、延迟后果队列（dueDay/kind/where/text/vis）、市场（stock/basePrice/convoys）、关系边（affinity/publicAffinity/type/trust/debt）。
2. **`ColonySaveCapture` / `ColonySaveRestore`**（静态类，纯 Domain）：`Capture(GameWorld) → ColonySaveData`；`Restore(ColonySaveData) → GameWorld`。Restore 后：`EnsureProfile` 用原种子重新生成时必须与存档前逐字段一致（种子确定性兜底，档案字段仅存"已实例化标记"+覆盖值）；关系图按存档边重建；`ColonyEventPump` 游标重置。
3. **SaveSystem 桥**（`Game/Tests/Domain/Colony/` 测试侧，沿用 A-COL-02 的 Adapter 先例——Game.Domain.Colony 保持零 ZFrame.Data 依赖）：把 ColonySaveData 经 ZFrame SaveSystem 的 payload 通道做序列化往返（先读 `ZFrame/Data/` SaveSystem 源码确认 payload/序列化 API；SaveData→byte[]/json→SaveData）。
4. **测试 `ColonySaveLoadTests`**（NUnit EditMode，数据驱动建局走 A-COL-02 的 ColonyConfigAdapter 路径）：
   - `S1_StateEquivalence`：建局推进 45 天（含违背契约/镇压×2/商人抗议编排）→ Capture → Restore → 逐字段断言：具名 NPC 忠诚/怨气/契约阶段/配合度/面具、聚落四参数、地区声望输入、governor 信誉。
   - `S2_DeterministicProfile`：restore 后 `EnsureProfile(slotX)` 与存档前同一 slot 的档案逐字段一致（§4.4「读档后他性格变了」禁止发生）。
   - `S3_WorldContinues`：restore 后 `AdvanceDays(7)` → 事件流非空；组织度/怨气沿既有轨迹继续（残垣城若已过阈值 A，restore 后组织者仍存在且不重复具名化）。
   - `S4_SlotFootprint`：存档里 profiled slot 数 < 背景层总数 10%（玩家一辈子深聊不过 200 人，§4.2 表：无档案 8B/槽）；DTO 序列化后大小 < 1MB（1000 slot fixture 口径）。
   - `S5_SaveSystemRoundTrip`：经 ZFrame SaveSystem payload 通道往返后 Capture 比较等价（字段级）。

### 约束（白名单）

- `Assets/Game/Domain/Colony/**`（新增 DTO/Capture/Restore）
- `Assets/Game/Tests/Domain/Colony/**`（新增测试与 SaveSystem 桥）
- `Docs/Logs/TASK-A-COL-03.md`
- 其余（asmdef/架构校验器）如需改动，登记待主控裁决，勿自行扩大。不 commit、不推远程。

### 自测要求

编译 exit 0 → EditMode 全量 failed=0 且 total ≥ 1287+5 → arch exit 0 → 变异自证（改坏 S2 一处断言 → RED → 还原 sha256 一致 → GREEN，log 留证）。

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error（自跑）。
- V2 EditMode 全量 failed=0，total 记录确切值 ≥1292；A-COL-01 V1-V7 与 A-COL-02 D1-D4 不回归（xml 点名抽查）。
- V3 架构校验 exit 0；S1-S5 逐条 xml Passed。
- V4 变异复红（验收自选与施工不同位置，还原 sha256 一致）。
- V5 git status 改动仅限白名单。

## 二、施工报告

<施工代理填写>

## 三、验收清单

<验收代理填写>

## 四、验收结论

<验收代理填写>
