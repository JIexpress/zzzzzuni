# TASK-A-ANIM-03 —— 动画扩展（二）：脚步 IK

> 路线图出处：`Docs/LLM-HANDOFF-NOTES.md` §7「动画扩展（Jump 接线/脚步 IK/程序化层）」第二刀。
> 上游：A-ANIM-01（脑→动画）、A-ANIM-02（跳跃真位移）。

## 一、任务简报

**目标**：让 NPC 的脚在斜坡/台阶上贴合地面，平地时**完全不干预**动画姿态。

**设计**：
- 权重纯函数 `KenneyLocomotionPlan.FootIkWeightFor(slopeDeg, groundDelta, maxSlopeDeg, maxLift)`：
  取「坡度比」与「落差比」的较大者，0 平地、1 到限，超限夹 1，非法上限返回 0。
- **中性踝高学习**：人形骨架的踝骨本身离地一段（随 rig 变化），若直接拿「地面高度」当目标，平地也会把脚往下拽。
  因此在坡度 ≤5° 时不落地学习 `neutral = 踝高 - 地面高`，之后 `targetY = 地面高 + neutral`，平地落差恒为 0。
- 每只脚独立射线：从踝骨 +0.6m 向下 1.8m；命中后按权重 `SetIKPosition` / `SetIKRotation`（旋转对齐地面法线）。
- 空中（跳跃弧线中）整体释放；`FootIkEnabled` 作为可证伪开关。

**白名单**：`KenneyLocomotionPlan.cs`、`NpcAnimatorDriver.cs`、`KenneyLocomotionPlanTests.cs`、`NpcAnimatorPlayModeTests.cs`、任务日志、TASKS.md。

## 二、实施记录

- 结构改造：`OnAnimatorIK` 拆成 `ApplyLookAt()` + `ApplyFootIk()`，保证脚部解算看到的是最终身体姿态。
- 诊断属性：`LastFootIkWeightLeft/Right`、`LastFootGroundDeltaLeft/Right`、`FootIkHitCount`、`FootIkEnabled`。
- 参数默认：最大坡度 32°、最大落差 0.22m、平坡判定 5°、射线 +0.6/−1.2m。

## 三、验收证据（本机实跑）

| 项 | 结果 |
|---|---|
| EditMode 全量 | **1103 / 1103 / 0**（+1 纯函数用例） |
| PlayMode 全量 | **89 / 89 / 0**（+1 场景用例） |
| 平地 | 权重 **< 0.05**（不拽脚），脚下射线命中 > 0 |
| 25° 斜板 | 权重 **> 0.3**、落差 > 0.02m（脚贴合并对齐法线） |
| 撤掉斜板 | 权重回落到 **< 0.05** |

**变异复红**：把 `ApplyFootIk` 的入口条件改成恒真（等价于关闭脚部 IK）→
`FootIk_StaysOffOnFlatGround_AndEngagesOnSlope` **1/1 红**（斜板阶段权重不升）；
还原后 `sha256sum -c` 校验一致（`164b0b45…977f`），复跑全绿。

## 四、受限

- 只在 SCN_Anim 的平地 + 运行时创建的 25° 斜板上验证；未覆盖台阶（离散落差）、楼梯、移动中的连续坡面。
- 未做腿部拉伸限制（极端高差下可能出现轻微拉伸）；当前由 0.22m 的落差上限间接兜住。
- 脚部旋转按地面法线对齐，未按坡向做脚尖朝向修正（视觉上可接受，未做像素级验证）。
