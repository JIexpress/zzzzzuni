# TASK-A-LOOP-02 —— 战斗闭环：玩家能打死敌人

> A-LOOP-01 的「守」目前只能靠站位挡，玩家没有反击手段——这不是玩法，是计时器。
> 本任务补上最小战斗：**敌人有血、玩家能打、击杀计数进 HUD**。

## 一、现状（施工前必读）

- `Game/View/Survival/SurvivalLoop.cs`：A-LOOP-01 的编排层。敌人生成后是**裸 GameObject + Primitive**，
  没有组件、没有血量；`MoveAttackers` 里抵达火堆就直接扣耐久并销毁。
- `Game/Domain/Survival/SurvivalLoopState.cs`：纯逻辑状态机（无 Kills 字段）。
- 输入：`activeInputHandler: 2`（Both），`SurvivalLoop` 已用 `Input.GetKeyDown(F/R)` 读旧输入。
- **不要**新增 Input System action（会牵动重绑定矩阵与既有用例）；攻击键用 `Input.GetKeyDown(KeyCode.J)`。

## 二、目标

1. 敌人有血量（默认 2 点），被打死时**不扣火堆耐久**、计入击杀。
2. 玩家攻击：按 **J**，对**身前 2.5m、±60° 扇形**内的敌人造成 1 点伤害；有冷却（0.35s）。
3. HUD 显示击杀数；胜利/失败结算里也带上击杀数。
4. 保持 A-LOOP-01 的既有契约：待机不建可见物、结束幂等、像素用例零影响。

## 三、设计要点

- 新增 `Game/View/Survival/SurvivalAttacker.cs`（MonoBehaviour）：
  - `MaxHp`、`Hp`、`IsDead`、`TakeDamage(int)`（返回是否致死）、受击闪白（材质 `_BaseColor` 短暂变亮）；
  - 死亡：标记 `IsDead`，由 `SurvivalLoop` 统一销毁（不要在组件里自己 Destroy，便于测试断言）。
- `SurvivalLoopState` 增加：
  - `int Kills { get; }`
  - `bool RegisterKill()`（仅 Running 生效，返回是否计入）
  - `Reset()` 清零；
  - **注意**：`RegisterKill` 不能改变 Phase、不能推进时间。
- `SurvivalLoop`：
  - 刷怪时给每个敌人挂 `SurvivalAttacker`；
  - `Update` 里处理攻击输入与冷却，扇形判定用 `Vector3.Dot` + 距离；
  - 命中致死 → `_state.RegisterKill()` + 销毁；
  - 敌人抵达火堆：**只有活着**才扣耐久（`IsDead` 的不扣）；
  - `OnGUI` 增加击杀数。
- 纯函数尽量下沉到 `SurvivalLoopState`（扇形判定可留在 View，但阈值写成常量并加注释）。

## 四、白名单

- `UnityProject/Assets/Game/Domain/Survival/SurvivalLoopState.cs`
- `UnityProject/Assets/Game/View/Survival/SurvivalLoop.cs`
- `UnityProject/Assets/Game/View/Survival/SurvivalAttacker.cs`（新增）
- `UnityProject/Assets/Game/Tests/Domain/SurvivalLoopStateTests.cs`
- `UnityProject/Assets/Game/Tests/PlayMode/SurvivalLoopPlayModeTests.cs`
- `Docs/Logs/TASK-A-LOOP-02.md`、`TASKS.md`（仅本任务行）

## 五、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 击杀计数 | `Start` 后 `RegisterKill()` 使 `Kills+1`；Idle/Won/Lost 阶段返回 false 且不计数 |
| V2 | 重置 | `Reset()` 后 `Kills=0` |
| V3 | 敌人血量 | `TakeDamage(1)` 两次 → `IsDead=true`；单次不致死时仍可被伤害 |
| V4 | 实战 | PlayMode：生成一波 → 攻击命中 2 次 → 敌人消失、`Kills=1`、火堆耐久不变 |
| V5 | 不回归 | 未击杀的敌人抵达火堆仍扣耐久；待机仍不建可见物 |
| V6 | 变异复红 | 把 `RegisterKill` 的阶段守卫去掉 → V1 必红；恢复后全绿 |

## 六、施工报告

### 摘要

最小战斗闭环已贯通：敌人有 2 点血、玩家按 **J** 用身前 2.5m/±60° 扇形每击 1 点打死它们；
致死走 `RegisterKill()` 记账并从敌人列表移除（`Destroy` 由 `SurvivalLoop` 统一执行，组件本身不自杀）；
抵达火堆**只有活着的敌人**才扣 8 点耐久；HUD 与胜负结算均显示击杀数。
A-LOOP-01 的三条契约保持不变（待机不建可见物、结束幂等、像素用例零影响）。

### 改动清单

| 文件 | 改动 |
|---|---|
| `Game/View/Survival/SurvivalAttacker.cs`（新增） | `MaxHp`/`Hp`/`IsDead`/`TakeDamage(int)`（返回"这一下是否致死"）/`Configure(int)`；受击 `_BaseColor` 提亮 0.1s 后还原（`MaterialPropertyBlock`，不复制材质）；**不自己 Destroy** |
| `Game/Domain/Survival/SurvivalLoopState.cs` | 新增 `int Kills { get; private set; }`、`bool RegisterKill()`（仅 Running 生效，不改 Phase、不推进时间、不碰耐久）、`Reset()` 清零 `Kills` |
| `Game/View/Survival/SurvivalLoop.cs` | 刷怪抽成 `SpawnAttacker(Vector3)` 并挂 `SurvivalAttacker`（材质之后挂，组件在 `Awake` 里记住还原色）；新增 `HandleAttackInput()`（`Input.GetKeyDown(KeyCode.J)` + 0.35s 冷却）与 `Attack()`（纯几何判定，无冷却）；`MoveAttackers` 抵达火堆前判活；HUD 加击杀数；`StartLoop` 重置冷却 |
| `Game/Tests/Domain/SurvivalLoopStateTests.cs` | +2 用例：`RegisterKill_CountsOnlyWhileRunning_AndTouchesNothingElse`（Idle/Won/Lost 返回 false 且不计数，Running 计数且 Phase/时间/耐久不变）、`Reset_ClearsKills` |
| `Game/Tests/PlayMode/SurvivalLoopPlayModeTests.cs` | +2 用例：`Attacker_TakesTwoHitsToDie_AndNeverDestroysItself`（V3）、`Attack_KillsEnemy_CountsKill_AndFireSurvivesWhileLivingEnemyStillBites`（V4+V5，含"活敌人抵达仍扣耐久"） |

设计取舍（供验收核对）：
- **冷却只在输入路径上**（`HandleAttackInput` 的 `Time.time < _nextAttackAt` 门），`Attack()` 是纯判定。
  这样 PlayMode 可以在同一帧连打两下做到确定性断言；按键路径与逻辑路径判定的是同一段几何代码。
- 攻击原点：`GameObject.Find("Player")`（SCN_Game 的胶囊），找不到退到 `Camera.main`，再没有则本次攻击空转。
- 数值常量全部 `public const`（`AttackRange=2.5`、`AttackHalfAngleCos=0.5`、`AttackCooldownSeconds=0.35`、`AttackDamage=1`），
  `SurvivalAttacker.DefaultMaxHp=2`；扇形用水平面 `Vector3.Dot`，贴脸（≤1e-4m）直接判命中以防除零方向抖动。
- 新增 `SpawnAttackerForTests(Vector3)` 仅供测试放置敌人（与刷怪共用 `SpawnAttacker`，同一条构造路径）。

### 自测数字（真跑，证据 = `TempLogs/runtests_*.xml`）

| 段 | 命令 | total | passed | failed | skipped | 基线 |
|---|---|---|---|---|---|---|
| EditMode | `Tools/run_tests.ps1 -Segments editmode` | 1113 | 1113 | 0 | 0 | 1111（+2） |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | 93 | 93 | 0 | 0 | 91（+2） |

两端均为 `Unity exit: 0`，`results.xml written by this run`（非陈旧证据）。
新用例在 xml 中逐条 `result="Passed"`：`RegisterKill_CountsOnlyWhileRunning_AndTouchesNothingElse`、`Reset_ClearsKills`、
`Attacker_TakesTwoHitsToDie_AndNeverDestroysItself`、`Attack_KillsEnemy_CountsKill_AndFireSurvivesWhileLivingEnemyStillBites`。

**V6 变异复红**：临时删掉 `RegisterKill` 的 `Phase != Running` 守卫 → EditMode `total=1113 passed=1112 failed=1`，
唯一红为 `SurvivalLoopStateTests.RegisterKill_CountsOnlyWhileRunning_AndTouchesNothingElse`；恢复后 EditMode 回到 1113/1113/0。

### 风险与未验证

- **未验证：J 键真人手感**。批处理无法注入按键，`Input.GetKeyDown(KeyCode.J)` → `Attack()` 这一段没有自动化证据（只覆盖了 `Attack()` 本身）。
- **未验证：闪白视觉**。只保证不复制材质、不抛异常；`MaterialPropertyBlock` 对 URP Lit `_BaseColor` 的实际视觉差未经截图确认。
- **玩法风险（已知设计选择）**：播放器朝向不做旋转控制（`PlayerMotor` 不转），所以"身前"恒为 `Player.forward`（场景里 +Z）。
  背向来的敌人打不到——这是简报"身前扇形"的直接后果，若要能转身攻击需要另开任务（属输入/角色控制范围）。
- 攻击冷却 0.35s 只在输入路径生效，故无单测覆盖其节流；`Attack()` 可被脚本连续调用。
- 环境：本机 shell 同时带 `HTTP_PROXY`/`http_proxy` 两套大小写变体，PS 5.1 构造环境块会抛重复键错误
  （HANDOFF §3.2 的已知坑）。本轮做法：跑测前在同一 PowerShell 进程内 `Remove-Item Env:http_proxy,Env:https_proxy` 再调脚本，未改 `run_tests.ps1`。
- PlayMode 像素证据用例每次会重写 `Docs/Parity/crowd_r9_15_on.png` 与 `utility_personality_goap_chain.png`（几字节差异），
  已按 HANDOFF"丢弃非任务 M 噪声"约定 `git checkout --` 还原，未提交。
- `TASKS.md` 里没有 A-LOOP-02 行（简报只建了本日志），未代主控新增/改动看板；也未 git commit。


## 七、验收结论（验收代理独立复跑，2026-11-09）

**总判定：PASS（V1–V6 全过）**。两项受限不影响验收项，见 §7.4。

### 7.1 独立复跑数字（数字全部取自当次重写的 `TempLogs/runtests_*.xml`，不采信施工表）

| 轮次 | 命令 | total | passed | failed | skipped | Unity exit | xml 时间窗（UTC） |
|---|---|---|---|---|---|---|---|
| EditMode（恢复后终态） | `Tools/run_tests.ps1 -Segments editmode` | 1113 | 1113 | 0 | 0 | 0 | 09:56:05–09:56:17 |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | 93 | 93 | 0 | 0 | 0 | 09:52:47–09:53:53 |
| EditMode（变异体） | 同上（仅改 `SurvivalLoopState.cs`） | 1113 | 1112 | 1 | 0 | 2 | 09:54:53–09:55:04 |

与施工自测（1113/93）一致；基线为 A-LOOP-01 的 1111/91（`git log` 1ac4cf3），增量为 +2/+2，与报告相符。
环境：按 HANDOFF 环境纪律，在同一 PowerShell 进程内先 `Remove-Item Env:http_proxy,Env:https_proxy` 再调脚本（规避 §3.2 重复代理键），未改 `run_tests.ps1`；
Unity 单实例、跑测试无 `-quit`，两次均显示 `results.xml written by this run`（非陈旧证据）。

### 7.2 逐项判定

| # | 判定 | 证据（用例 + 数字 + 代码行） |
|---|---|---|
| V1 | **PASS** | `Game.Domain.Tests.SurvivalLoopStateTests.RegisterKill_CountsOnlyWhileRunning_AndTouchesNothingElse`（EditMode xml id=2567，result=Passed）：Idle 返回 false、Kills=0；Running 连记 2 次 Kills=2，且 Phase 仍 Running、`ElapsedSeconds` 不变、FireHp=100；Won/Lost 均返回 false、Kills=0。实现 `SurvivalLoopState.cs:167-176`（守卫 169-172），`Kills` 声明 :85。 |
| V2 | **PASS** | `SurvivalLoopStateTests.Reset_ClearsKills`（id=2568，Passed）：Kills 2→0、Phase→Idle。实现 `SurvivalLoopState.cs:203-212`，清零在 :209。 |
| V3 | **PASS** | `Game.View.PlayMode.Tests.SurvivalLoopPlayModeTests.Attacker_TakesTwoHitsToDie_AndNeverDestroysItself`（PlayMode xml id=1017，Passed）：MaxHp=Hp=2；第一击 false、Hp=1、IsDead=false、宿主未销毁；第二击 true、IsDead、Hp 夹 0；对尸体再打返回 false。实现 `SurvivalAttacker.cs:66-85`，`IsDead` :37。 |
| V4 | **PASS** | `...Attack_KillsEnemy_CountsKill_AndFireSurvivesWhileLivingEnemyStillBites`（id=1018，Passed）：+Z 2.4m 生成，`Attack()` 返回 1、Hp=1、Kills 不变；再 `Attack()` 返回 1、`AttackerCount` 1→0、Kills+1、`FireHp` 不变、Phase 仍 Running；下一帧 `enemy==null`。实现 `SurvivalLoop.cs:160-226`（距离门 :205、扇形 :211、记账 :219、统一销毁 :221）。 |
| V5 | **PASS** | 同用例后半：2.1m 活敌两帧后 `FireHp` 下降（活敌扣耐久正例）；`Loop_StaysIdleUntilStarted_ThenRunsWinsAndRestarts`（id=1016，Passed）断言 Idle 时 `Fire==null`、`AttackerCount==0`。实现 `SurvivalLoop.cs:385-397`（活敌门 388-392）、待机分支 :115-127；`EnsureFire` 只由 `StartLoop` 调用 :242，`SpawnWave` 只在 Running 分支 :131-134。 |
| V6 | **PASS** | 变异复红全过程见 §7.3：删守卫 → 1113/1112/1，唯一红为 V1 用例；逐字节恢复（sha256 一致）→ 1113/1113/0。 |

### 7.3 变异复红记录（本代理亲手执行）

1. 变异：整段删除 `SurvivalLoopState.cs:169-172` 的 `if (Phase != SurvivalPhase.Running) return false;`（变异前 sha256 `1eb7420fda4a6ae1fb506779ab88487bbe22e190a948baf842087bb7a7a409d3`）。
2. 复红：EditMode `total=1113 passed=1112 failed=1 skipped=0`，Unity exit=2；**唯一红**为 `SurvivalLoopStateTests.RegisterKill_CountsOnlyWhileRunning_AndTouchesNothingElse`（id=2567，09:54:53Z）。xml 副本：`TempLogs/acc_loop02_editmode_mutant.xml`。
3. 恢复：用备份 `TempLogs/acc_loop02_state_backup.cs` 逐字节回写，`sha256sum` 前后同为 `1eb7420f…a409d3`（完整值见 `TempLogs/acc_loop02_sha_before.txt`）。
4. 恢复后：EditMode `1113/1113/0`，exit=0，全绿（09:56:05Z）。副本：`TempLogs/acc_loop02_editmode_restored.xml`。

### 7.4 施工自曝缺口的裁决

1. **“背后来敌打不到” → 设计边界，不计本任务 FAIL；登记为 P2 玩法风险。**
   理由：简报 §二.2 明确攻击范围是“身前 2.5m、±60°”，施工按 `Player.forward` 实现（`SurvivalLoop.cs:176-183`）；`PlayerMotor` 自述 “no rotation”（`PlayerMotor.cs:10`，全文无任何 rotation 写入），白名单也不含角色控制/输入层。
   所以“身前”恒等于世界 +Z 是本任务约束的直接后果，属边界内已知取舍，V1-V6 不因此判红。
   但敌人 360° 生成（`SpawnWave` 用 `Random.Range(0, 2π)`，`SurvivalLoop.cs:314-316`），真人只能打 +Z 半球来敌，闭环可玩性偏弱；建议另开“转向/瞄准”任务（P2），不应在本任务内偷偷扩权。
2. **“J 键真人链路未验证” → 不能自动化补证，如实列受限，不计 FAIL。**
   `UnityEngine.Input.GetKeyDown` 读 OS 级键盘状态，批处理无注入手段；项目虽为 Both(2)，代码走旧 `Input` API，Input System 的测试夹具也喂不到它。
   可补的只是攻击接口本身：`Attack()` 已被 V4 用例直接调用覆盖（几何判定 + 致死 + 记账 + 销毁），而按键→`HandleAttackInput` 的胶水仅 `SurvivalLoop.cs:137,144-153` 共 8 行，只能静态核对；0.35s 冷却门同样无自动化证据。

### 7.5 回归面专项

- **待机不建可见物：PASS**。PlayMode id=1016 断言 Idle 时 `Fire==null`、`AttackerCount==0`；代码侧 `EnsureFire` 只在 `StartLoop` 里调用（:242），刷怪只在 Running 分支（:131-134），待机只有 IMGUI 文本，无 Renderer。
- **只有活敌扣耐久：PASS（正例动态、负例静态）**。活敌抵达扣耐久由 id=1018 后半动态覆盖；死敌不扣由 `SurvivalLoop.cs:388-392` 的 `!health.IsDead` 门保证，且致死当帧已从列表移除（:220-221），死敌在列表中属防御性冗余分支，未单独构造动态负例——列入 §7.6 受限。
- **像素用例：PASS**。PlayMode 93 条中 15 条 `*PixelEvidence*` 全部 `result="Passed"`：GameplayFx 4、KernelCrowd 2、ModelStage 2、Particle 4、PostFx 1、VolumeCrossfade 2；另有 UtilityNpc 3 条 Passed；全段 failed=0。
  跑测会重写 `Docs/Parity/crowd_r9_15_off.png`、`crowd_r9_15_on.png`、`utility_personality_goap_chain.png`，已 `git checkout --` 还原，未留噪声。
- **A-LOOP-01 原契约**：五条 Domain 用例 + 场景闭环用例全过，结束幂等（胜利后 Tick/伤害/刷波被忽略）未回归。

### 7.6 受限与交付状态

- 受限：① J 键 → `HandleAttackInput` 的真实按键链路（批处理无法注入）；② 0.35s 冷却节流无单测；③ 受击闪白只保证 API/材质不被复制，视觉差未经截图确认；④ “死敌在列表仍不扣耐久”的负例分支仅静态核对。
- 未 git commit；未改白名单外文件（本日志第七节除外）。工作区回到施工完成态：4 个修改文件 + 新增 `SurvivalAttacker.cs/.meta` + 本日志（`Docs/TECH-VALIDATION.md` 为施工前既有未跟踪文件，未触碰）。
- 终态源码 sha256 与验收开始时一致：`SurvivalLoopState.cs 1eb7420f…`、`SurvivalLoop.cs 9536494c…`、`SurvivalAttacker.cs 01335af6…`。
- 变异/复跑证据副本在 `TempLogs/acc_loop02_*`（该目录已在 .gitignore）。
