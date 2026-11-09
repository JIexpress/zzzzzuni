# TASK-A-LOOP-01 —— 最小可玩循环：守住火堆 90 秒

> 目标：用现有系统拼出**第一个有输赢的闭环**，回答「这到底是不是游戏」。
> 原则：不新增美术、不新增系统，只做**编排**——消费已有的敌人 FSM、玩家控制器、HUD、事件总线、过场门闸。

## 一、玩法（一句话）

**玩家守住场地中央的火堆，撑过 90 秒即胜利；火堆耐久归零则失败。**

## 二、为什么是「守」

- 它只需要**一个动词**（靠近→挡住/击杀），不需要背包、对话、任务链；
- 它能复用已有能力：敌人 FSM（追击）、玩家移动/交互、HUD、`EventBus`、失败后重开（SceneLoader）；
- 它天然有**压力曲线**：波次间隔递减、敌人数量递增；
- 它能在 3 分钟内被完整验证（赢一次、输一次）。

## 三、设计要点

### 状态机（纯逻辑，EditMode 可断言）

`SurvivalLoopState`（引擎无关）：
- 字段：`ElapsedSeconds`、`RemainingSeconds`、`FireHp`、`MaxFireHp`、`WaveIndex`、`Phase{Idle,Running,Won,Lost}`。
- `Tick(dt)`：推进计时；到 90s → `Won`；`FireHp <= 0` → `Lost`；结束后不再推进（幂等）。
- `ApplyFireDamage(amount)`：仅 `Running` 生效；夹到 0。
- `NextWaveDelay()` / `WaveSize()`：纯函数，波次越往后间隔越短、数量越多（给数值表，不写死魔法数）。
- `Reset()`：回到初始态（重开用）。

### 场景编排

- `SurvivalLoop`（MonoBehaviour）：持有 state，`Update` 调 `Tick(Time.deltaTime)`；波次到点用已有敌人预制/生成入口刷怪，敌人目标点 = 火堆。
- **火堆**：场景里的一个对象 + `FirePillar`（耐久、受击、归零触发 `Lost`）；受击来源 = 敌人抵达（距离判定，不做复杂战斗）。
- **HUD**：倒计时 + 火堆耐久条 + 波次提示（复用 `GameHud` 的文本/条，不新增 UI 框架）。
- **结束**：`Won` / `Lost` 面板（复用设置面板的 Panel 栈），按钮「重开」走 `SceneLoader` 重载场景。

### 不做

- 不做背包/建造/技能树；不做存档（重开即重置）；不改敌人 AI 决策；不新增美术资源。

## 四、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 计时胜利 | `Tick` 累计到 90s → `Won`，`RemainingSeconds=0`；继续 Tick 不变 |
| V2 | 耐久失败 | `ApplyFireDamage` 累加至 0 → `Lost`；`Won` 后受伤不再改变 Phase |
| V3 | 幂等 | `Won`/`Lost` 后 `Tick`、`ApplyFireDamage` 都不改变状态与计时 |
| V4 | 波次曲线 | `WaveSize` 单调不减、`NextWaveDelay` 单调不增；第 1 波间隔 ≥ 第 5 波 |
| V5 | 场景闭环 | PlayMode：火堆受伤 → HUD 条下降；耐久归零 → 失败面板出现；重开 → 状态复位 |
| V6 | 变异复红 | 把 `Phase != Running` 的守卫去掉 → V3 必红；恢复后全绿 |

## 五、白名单

- `UnityProject/Assets/Game/Domain/Survival/**`（纯逻辑 state）
- `UnityProject/Assets/Game/View/Survival/**`（MonoBehaviour 编排 + HUD 绑定）
- `UnityProject/Assets/Game/Tests/**`（EditMode + PlayMode）
- `UnityProject/Assets/Scenes/SCN_Game.unity`（火堆对象 + 组件挂载）
- `Docs/Logs/TASK-A-LOOP-01.md`

## 六、实施记录

**新增**
- `Game/Domain/Survival/SurvivalLoopState.cs`：纯逻辑状态机（Idle/Running/Won/Lost）、
  90s 计时、耐久、波次消费、波次曲线纯函数（人数 1/1/1/2/2/2…，间隔 6s×0.85ⁿ，下限 3s）。
- `Game/View/Survival/SurvivalLoop.cs`：表现层编排——SCN_Game 自动挂载但**默认待机**，
  按 F 开始；建火堆（发光橙）、按波刷红方块敌人、敌人抵达 2.2m 内扣 8 点耐久并消失；
  HUD 用 IMGUI（刻意不走 Canvas：不会被相机 RenderTexture 捕获，**不污染既有像素用例**）；
  胜利/失败提示 + R 重开。
- 测试：`SurvivalLoopStateTests`（5 例纯逻辑）、`SurvivalLoopPlayModeTests`（场景闭环 1 例）。

**设计取舍**
- 待机态不建任何可见物——这样后处理/开门火花/相机差分的像素用例在 SCN_Game 里跑时画面零影响。
- 敌人不复用现有 Enemy FSM，先用最简"直线逼近 + 抵达扣血"，把闭环跑通；接 AI 是下一步。

## 七、验收结论

| # | 断言 | 结果 |
|---|---|---|
| V1 | 计时胜利（89s Running → 91s Won，时间夹在 90） | PASS |
| V2 | 耐久归零判负；失败后伤害被拒 | PASS |
| V3 | 结束后 Tick/伤害/刷波全部幂等；Reset 复位 | PASS |
| V4 | 波次人数单调不减、间隔单调不增且 ≥3s | PASS |
| V5 | PlayMode：待机无火堆无敌人 → StartLoop → 模拟 95s 判胜 → 重开满耐久 → 满额伤害判负 → 销毁解除静态引用 | PASS |
| V6 | 变异复红（去掉 `ApplyFireDamage` 的 Phase 守卫 → 2 例红；按原样还原后 sha256 一致） | PASS |

**全量**：EditMode **1111/1111**、PlayMode **91/91**。

**受限**
- 敌人是"直线逼近"的最简实现，没有寻路/攻击动作/受击反馈；玩家目前**不能击杀敌人**，
  只能靠站位挡（这一版只验证"守"的闭环，战斗留给下一刀）。
- HUD 是 IMGUI 占位；没有音效；没有难度档位；没有存档（重开即重置）。
- 未在 Android 真机验证。


## 七、验收结论（待填）
