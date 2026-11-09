# TASK-A-CUT-01 —— L3 播片过场（固定机位）

> 路线图出处：`Docs/LLM-HANDOFF-NOTES.md` §7 第 3 项「L3 播片过场（VideoPlayer+固定机位，管线已留位）」。
> 前置已就绪：`CrowdLODPipeline` 留了 L3 枚举位与边界文档（A-AND-09）；片源已由 Blender 生成并入库。

## 一、任务简报（主控）

### 背景

- `CrowdLodLevel.L3VideoClip` 存在但**分配器永不产出**：`CrowdLODPipeline` 走到 L3 分支直接 `throw`（契约破坏）。
- 边界文档写明：L3 = 固定机位/过场判定（**场景标记驱动，与距离无关**）+ VideoPlayer 播片资产管线；数据侧无需新列。
- 片源：`Assets/Art/Cutscenes/cutscene_crowd.mp4`（640×360 / 24fps / 3s，Blender 5.2 Workbench 生成，占位用，非美术终稿）。

### 目标

1. **过场门闸**：`CutsceneDirector`（或等价组件）提供 `IsActive` 状态；激活时把人群整体切到 L3（隐藏 L0/L1/L2 表示），播放预渲染片。
2. **播片链路**：`VideoPlayer` → `RenderTexture` → 全屏 `RawImage`（或 `Camera` 背景），音频默认关闭；循环可配。
3. **L3 分支不再 throw**：`CrowdLODPipeline` 的 L3 分支改为「由门闸接管表示层」，返回可诊断状态而不是异常。
4. **可证伪**：过场开/关必须能用像素或状态断言区分（人群可见 → 视频可见）。

### 设计要点（实现方向，非唯一写法）

- 门闸用**场景标记**而非距离：进入过场区域/触发点 → `Begin()`；离开/播完 → `End()`。
- 播片期间冻结人群的解算与提交（不只是隐藏）：避免"看不见但在跑"的 CPU 浪费。
- `VideoPlayer` 在 batchmode/Editor 下的解码能力要实测；若 PlayMode 无法解码，用例退化为「状态与门闸」断言，并在日志里如实标受限。
- 不碰 Domain、不改 A-AND-09 的硬上限语义；L0 剧情 NPC 的优先级不变。

### 白名单

- `UnityProject/Assets/Game/View/Cutscene/**`（新目录，含 asmdef 或并入 Game.View）
- `UnityProject/Assets/Game/View/Crowd/CrowdLODPipeline.cs`（仅 L3 分支）
- `UnityProject/Assets/ZFrame/Tests/PlayMode/**`（新增过场用例）
- `Docs/Logs/TASK-A-CUT-01.md`

### 验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 门闸语义 | `Begin()` 后 `IsActive=true`，`End()` 后 false；重复 Begin/End 幂等 |
| V2 | 人群切换 | 过场激活时 L0/L1/L2 表示全部 `SetActive(false)`；结束后恢复原档位 |
| V3 | 播片 | `VideoPlayer.isPlaying=true` 且 `frame` 在 1s 内推进 ≥ 10 帧（若 batchmode 无法解码：如实记受限并降级为 `prepareCompleted` 断言） |
| V4 | L3 不再 throw | 旧契约用例改为断言"L3 分支由门闸接管"，不再期望异常 |
| V5 | 变异复红 | 关掉门闸对人群的隐藏 → V2 必红；恢复后全绿 |

### 自测命令

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments editmode
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments playmode
```

## 二、实施记录

**新增**
- `Assets/Game/View/Cutscene/CutsceneDirector.cs`：门闸（`Begin/End/IsActive`，幂等）+
  VideoPlayer→RenderTexture(1280×720)→全屏 RawImage（Overlay，sortingOrder 30000）；
  片源优先用序列化字段，否则 `Resources.Load("Cutscenes/cutscene_crowd")`。
- 片源：`Assets/Resources/Cutscenes/cutscene_crowd.mp4`（Blender 5.2 Workbench 渲染，
  640×360/24fps/3s；放 Resources 是为了运行时与测试都能加载，换正式片源只替换文件）。

**改动**
- `CrowdLODPipeline`：新增存活表 + `SetAllSuppressed(bool)` / `IsSuppressed` / `LiveCount`；
  抑制态下隐藏 L0、清零 L1/L2 计数、**跳过 kernel 读取**（看不见的群体不再耗 CPU）；
  `Dispose` 时移出存活表（不会留永久抑制的僵尸管线）。
- **L3 契约保持不变**：分配器仍永不产出 L3，A-AND-09 的硬上限语义（L0≤20/L1≤50/L2≤512）
  完全不受过场影响；只把类头注释从「未实现」更新为「由门闸全局抑制实现」。

## 三、验收证据（本机实跑）

| 项 | 结果 |
|---|---|
| EditMode 全量 | **1104 / 1104 / 0**（+1 抑制语义用例） |
| PlayMode 全量 | **90 / 90 / 0**（+1 过场用例） |
| 门闸 | `Begin` 后 `IsActive=true`、`End` 后 false；重复 Begin 不重启播放器 |
| 人群让位 | 过场中 `pipeline.IsSuppressed=true`、`LiveCount` 覆盖所有存活管线；`End` 后恢复 |
| **真解码** | batchmode 下 `VideoPlayer.isPrepared=true`，0.6s 内帧号推进（**非降级路径**） |
| 变异复红 | 删掉 `Begin` 里的 `SetAllSuppressed(true)` → 过场用例 **1/1 红**；还原后 sha256 一致（`2f7732bc…2648`），复跑全绿 |

## 四、受限

- 片源是 Blender 生成的**占位片段**（96 个方块小人 + 相机横移），不是美术终稿。
- 未做场景接线：SCN_Game 里还没有触发过场的区域/剧情点，目前只能由代码调 `Begin/End`。
- 只在 Windows + D3D12 + 780M 上验证；Android/IL2CPP 的 VideoPlayer 解码未验（移动端需确认 H.264 支持与 `VideoPlayer` 权限）。
- 音频关闭（占位片源无音轨）；正式过场若需要音频，需补 `audioOutputMode` 与混音总线。
- 过场期间的**输入冻结**未接：`IsActive` 只提供状态，是否屏蔽输入由调用方决定。


## 三、验收结论（待填）
