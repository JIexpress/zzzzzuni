# TASK-A-R9-07 —— 粒子事件源补全：门/对话/FSM 状态三类接线

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

**前置**：A-R9-06 完成（GameplayFxBindings 框架已就绪——本任务是其"4 类未接"遗留的补全）。

## 任务简报（主控写）

**目标**：把 A-R9-06 事件源盘点表中未接的 3 类补全（第四类"对话"带语义判断，见下），让粒子系统覆盖全部玩法事件；沿用既有模式（EventScope 订阅/同帧合并/IGameplaySparkEmitter 接缝），**零新概念**。

**设计要点**：
1. **门**：`DoorStateChanged`（Domain/Interaction/DoorEntity.cs:43，载荷 id+IsOpen——**无位置字段**）：DoorEntity 需最小扩展加 Position（同 ItemPickedUp 先例，报告注明）；火花语义=门开/关各一小簇（位置=门位置）
2. **对话**：`DialogAdvanced`（有 dialogId/lineIndex/line/isFinished）——**语义判断**：对话本身不该每行冒火花；接 `isFinished==true`（对话结束）一次小簇（或干脆不接，**若你判断对话结束冒火花体验违和，可以决定不接并写清理由——简报授权你做这个产品判断**，报告说明）
3. **FSM 状态**：`EnemyStateChanged`（M4-01 敌人状态事件，查载荷有无位置）：进入 Dead 时火花（死亡爆点）——敌人死亡是最自然的第三源；其余状态转换不冒（免刷屏），理由写报告
4. 全部走既有 GameplayFxBindings（扩展它，不建新类）；同帧合并/Enabled 协同/Domain 零粒子引用三红线不变
5. 单测（EditMode 追加）：三事件源各自映射与合并；门事件位置断言；死亡触发断言
6. PlayMode 像素证据：门开关→像素变化（沿 PixelProbeRig 口径）；可证伪
7. 自测：EditMode+PlayMode 全绿（基线 736/15 只增不减）+ Validate exit 0（Unity 纪律）

**约束**：白名单 Assets/Game/**、Assets/ZFrame/View/**（若需）、Tests/**、本日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：三类接线（或含放弃判断的如实说明）+单测+像素证据+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 2026-10-31 ｜ 路径 `C:\Users\1\Desktop\zcode\GameFramework`

### ① 交付概述

A-R9-06 盘点表遗留 3 类事件源全部接线，**零新概念**——仍是同一条链：`EventBus` 订阅（一个 `EventScope`）→ 位置映射（`ToWorld` 同一 2D→3D 约定）→ 同帧同位置合并 → 注入的 `IGameplaySparkEmitter`。绑定源从 R9-06 的 2 源扩到 **5 源**：攻击、拾取（既有）+ **门开合、敌人死亡、对话完结**（本任务）。Domain 零粒子引用红线不变；同帧合并、Enabled 协同两条红线复用未动。

### ② 改动清单（全部在白名单 Assets/Game/**、Assets/ZFrame/Tests/**、本日志内）

**A. Domain 事件载荷最小扩展**（同 `ItemPickedUp`/`PickupEntity` R9-06 先例：新增**可选**参数，旧构造保留并置 `Vec2.Zero`，全仓既有调用点零改动——EditMode 基线全绿即证）：

| 文件 | 行号 | 内容 |
|---|---|---|
| `UnityProject/Assets/Game/Domain/Interaction/DoorStateChanged.cs` | :20、:31-35 | `Position` 字段 + 3 参构造（2 参构造保留） |
| `UnityProject/Assets/Game/Domain/Interaction/DoorEntity.cs` | :38、:48、:60、:100 | 可选 `position` 参数 + 只读 `Position` 属性 + `Toggle()` 把位置装进事件 |
| `UnityProject/Assets/Game/Domain/Enemy/EnemyStateChanged.cs` | :28、:41-45 | `Position` 字段 + 4 参构造（3 参构造保留） |
| `UnityProject/Assets/Game/Domain/Enemy/EnemyFsm.cs` | :187 | `OnStateChanged` 把当前 `Position` 装进事件（死亡爆点=倒下处） |
| `UnityProject/Assets/Game/Domain/Interaction/DialogAdvanced.cs` | :27、:42-46 | `Position` 字段 + 5 参构造（4 参构造保留） |
| `UnityProject/Assets/Game/Domain/Interaction/DialogEntity.cs` | :36、:58、:72、:113 | 同款（可选 `position` + `Position` 属性 + `Advance()` 带位置） |

**B. View 供给侧**：`UnityProject/Assets/Game/View/Interaction/InteractableProxy.cs:77-97`——平面位置统一取 `transform.position.x/z`（与既有 Pickup 完全同一映射），Door 与 Dialog 构造时传入；Pickup 分支语义不变。

**C. 绑定扩展**：`UnityProject/Assets/Game/View/Effects/GameplayFxBindings.cs`
- `:184-186` 三条新 `Subscribe`（DoorStateChanged / EnemyStateChanged / DialogAdvanced），同一 `try` 块半开 scope 防护不变；
- `:243-248` 门 handler：每次 Toggle 一小簇，开/关同款，位置=门位置；
- `:250-263` 死亡过滤：`evt.Current != EnemyStateId.Dead` 提前 return；
- `:265-280` 对话完结过滤：`!evt.IsFinished` 提前 return；
- `:25-44` 类文档更新（五源契约 + **过滤语义**：被源规则丢弃的事件不计入计数器，`EventsReceived == SparksRequested + SparksMerged` 不变式保持）。

**D. 测试**：
- EditMode `UnityProject/Assets/Game/Tests/View/GameplayFxBindingsTests.cs`：**11 条新增**——`DoorToggle_SparksAtTheDoorPosition_BothDirections`(:275)、`DoorOpenAndCloseSameFrame_AtTheSameSpot_MergeToOneSpark`(:297)、`DoorEventWithoutPosition_SparksAtThePlaneOrigin`(:313)、`DeathTransition_SparksAtTheEnemyPosition`(:324)、`NonDeathTransitions_NeverSpark`(:336，五条合法非死亡转换全不冒)、`DialogFinish_SparksOnce_AtTheDialogPosition`(:354)、`DialogMidLines_NeverSpark`(:368)、`FiveSourcesSameFrameSameSpot_MergeIntoOneSpark`(:378，events=5/sparks=1/merged=4)、`RealDoorToggle_SparksThroughTheRealEventPath`(:396)、`RealEnemyFsmKill_SparksDeathBurstAtTheFallSpot`(:413，真 EnemyFsm.Kill 链)、`RealDialogCompletion_SparksExactlyOnceAtTheEnd`(:426，真 DialogEntity.Advance 链)；另把 2 条既有生命周期用例（订阅恰一次/Shutdown 全退订）扩为五源断言。
- PlayMode `UnityProject/Assets/ZFrame/Tests/PlayMode/GameplayFxPixelEvidencePlayModeTests.cs:183`：`DoorToggleEvents_BrightenTheFrame_AtTheDoorPosition_BothDirections`——真 `DoorEntity` 持探针点位，`Toggle()` 开/关各一次（跨帧），门控与 faded 断言齐全。

### ③ 三源语义与产品判断

1. **门**：开/关各一小簇（"门真的动了"），位置=门位置。已知边界：同帧开+关（同位置）被同帧合并吸收为 1 簇——这是合并语义（frame+position，与 R9-06"10 连拾取=1 簇"完全同款）的自然结果，已用专门用例钉死；真实交互两次 Toggle 必跨帧，不受影响。
2. **死亡**：仅 `Current == Dead` 触发；其余转换（含 FSM 启动时的初始 Idle 公告 Previous==Current）一律不冒。理由：巡逻循环 Idle/Patrol/Chase 来回切换每秒可达数次，冒火花=屏幕频闪；死亡是唯一玩家必感的"世界状态改变"，值得也只需要一簇，位置=倒下处（`EnemyFsm.Position`）。
3. **对话（简报授权的产品判断）——决定：接，但仅 `IsFinished==true` 一次小簇**：
   - 简报真正担心的违和点是"每行冒火花"打断叙事节奏——这个予以排除（中段行零触发，用例证明）；而**对话完结**与门落位、敌人倒下同属"世界状态到达终态"的一次性时刻，与本切片火花词汇（命中/拾取/状态变化）连贯，且单次触发天然无刷屏。
   - 成本：与门同款可选参数扩展（约 6 行），无新概念；不接的话反而要在报告里留下"4 类事件 3 接 1 放"的覆盖缺口。
   - **反方理由如实记录**（若验收/主控持此观点可否决本判断）：① 对话完结的反馈已存在（末行文本 + prompt 消失），火花边际增益弱；② 若将来对话 UI 覆盖 NPC，火花被遮挡浪费；③ "火花=物理接触/获取"的词汇纯度论。**撤销路径**：删 `OnDialogAdvanced` handler + `:186` 一条 Subscribe + 2 条 EditMode 用例即可，Domain 侧 `Position` 字段可保留为无害元数据。
4. **如实记账（非本简报范围）**：行为树脑（`EnemyDriveMode.BehaviorTree`）死亡走黑板旗标（`EnemyBehaviorTree.Kill()`，A-R8-03），**不发** `EnemyStateChanged`，故 BT 模式死亡当前不冒火花；补齐需 BT 发状态事件或新事件类型，超出"零新概念"授权。切片演示路径（`EnemyView`→`EnemyFsm`）已覆盖。

### ④ 自测与证据

- **EditMode 747/747 全绿**（基线 736 + 11 新增；`TempLogs/runtests_editmode.xml` 03:05 本轮，Unity exit 0）。首跑 746 中 1 红：`DoorToggle_BothDirections` 未推进帧号，开+关被同帧合并吸收为 1——属**测试预期错误**而非产品缺陷（恰是合并语义的旁证）；改为跨帧断言后全绿，并补 `DoorOpenAndCloseSameFrame` 用例把"同帧开+关=1 簇"显式钉住。
- **PlayMode 16/16 全绿**（基线 15 + 1 新增；`TempLogs/runtests_playmode.xml` 03:06 本轮，Unity exit 0）。
- **像素证据**（PixelProbeRig 口径：320×180 ARGBFloat 实读回、control 均匀 0.0200、门限 brighter≥0.004 / meanAbs≥0.002 沿 R9-05 标定；`TempLogs/runtests_playmode.log` [FXBINDINGS] 行）：
  - 门**开**：brighter=**0.0180**（门限 4.5×）、meanAbs=0.01496；events=1/sparks=1/merged=0/LastSpawnEmitted=12；
  - 门**关**：brighter=**0.0082**（门限 2.05×）、meanAbs=0.00741；events=2/sparks=2/merged=0（两次 Toggle 跨帧互不合并）；
  - 0.55 s 后 HitSparkParticleCount=0（火花寿终）；
  - 全程走真路径 `new DoorEntity(position) → Toggle() → EventBus → Bindings → SpawnHitSpark`，与 `InteractableProxy` 驱动同链，可证伪（删接线/丢位置字段即红）；
  - 同轮 R9-06 既有用例全绿未回归：attack brighter=0.0131、Enabled=false moved=0.0000、10 连拾取 events=10/sparks=1/merged=9。
- **三校验器 exit 0**：arch（`TempLogs/arch_report.txt` 03:08:22）、config（03:08:59）、naming（03:09:28），全部晚于最晚源文件改动。
- **Domain 零粒子引用（V2 供验收 grep）**：`grep -ri particle Assets/Game/Domain` = **0 命中**；`grep -rinE "vfx|emitter|monobehaviour|gameobject"` = **0 命中**；Domain 新增引用仅 `ZFrame.Gameplay` 的 `Vec2`（架构矩阵既有合法引用）。

### ⑤ 遗留 / 备注

1. **工作区并行改动**：git 工作区含并行任务 B-R9-09 的文件（`Native/zrhi/vulkan/zrhi_vulkan_device.cpp/.h` 修改、`Native/zrhi/vulkan/zrhi_vulkan_recreate.h` 新增）——本任务未触碰（白名单外零改动），主控收口时注意分属两任务、分开提交。
2. 火花位置精度与 R9-06 同级：死亡位=`EnemyFsm.Position`（状态机当前位置），非命中判定点；`SpawnHitSpark` 攻击位取 `PlayerPosition` 的既有近似（R9-06 记账项）未动。
3. BT 脑死亡不冒火花（见 ③.4），后续如需补齐建议给 `EnemyBehaviorTree` 发 `EnemyStateChanged`（复用本绑定，零新增消费方）。
4. run_tests.ps1 的 `-Segments` 从 Git Bash 传多段时逗号/空格列表均只绑定首段——本轮 arch/config/naming 分三次调用跑齐（三次均 exit 0），不影响结论，仅记录调用口径。

## 验收清单（主控写）

- [x] V1 EditMode 全绿（数字写入结论）；三源映射与合并用例真实（行号）
- [x] V2 门/敌人事件位置字段最小扩展评估（兼容性）；Domain 零粒子引用（grep）
- [x] V3 对话源的产品判断（接/不接）与理由评审
- [x] V4 PlayMode 像素证据亲跑（门开关变化）；Validate exit 0（自跑）
- [x] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ 2026-10-31 ｜ 全部证据为本代理亲跑亲核，未复用施工方任何结论 ｜ 判定：**PASS（5/5，无受限项）**

- **V1 PASS**——亲跑 `Tools/run_tests.ps1 -Segments editmode`：exit 0，本轮 `TempLogs/runtests_editmode.xml`（03:31）total=**747** passed=747 failed=0 skipped=0；11 条新用例名全部在本轮 XML 中逐一确认（DoorToggle 双向:275 / 同帧合并:297 / 无位置回退:313 / 死亡:324 / 五条非死亡:336 / 对话完结:354 / 中段行:368 / 五源合并:378 / 真门链:396 / 真死亡链:413 / 真对话链:426，与报告行号一致且断言非恒真——含位置坐标、计数器三元组与"拒绝再触发不加簇"反证）。亲跑 PlayMode：exit 0，`runtests_playmode.xml`（03:36）total=**16** passed=16，含门像素用例。
- **V2 PASS**——`GameplayFxBindings.cs`：:184-186 三条 Subscribe 齐；:243-248 门开/关同款各一簇（无状态过滤）；:257-260 仅 `Current == EnemyStateId.Dead` 通过（其余提前 return）；:274-277 仅 `IsFinished` 通过；:25-44 五源契约+过滤语义文档在。Domain 最小扩展评估=**成立**：三个事件各加 1 个 readonly Position + 1 个重载构造，旧构造全保留（DoorStateChanged 2参:23 / EnemyStateChanged 3参:31 / DialogAdvanced 4参:30，默认 `Vec2.Zero`）；grep 证实生产构造点仅三实体各一处（DoorEntity:100 / EnemyFsm:187 / DialogEntity:113）、旧构造仅兼容性测试引用（:317），全仓既有调用点零破坏（747 全绿即编译证明）；替代方案（View 端 id→位置注册表）需新概念，违背"零新概念"，故扩展既最小又必要，且与 R9-06 ItemPickedUp 先例同款。
- **V3 PASS**——独立判断：**同意"接，仅 IsFinished==true 一次小簇"**。我的理由（独立形成，非复述施工）：三选项（每行/仅完结/不接）中"仅完结"是唯一同时满足零新概念、R9-06 盘点遗留闭环无缺口、触发有界三条件的；"不接"是为一个可逆的美学怀疑支付永久覆盖缺口，不划算；词汇上本切片火花语义已是"世界状态变化的位置反馈"（攻击落点/门开合/死亡倒下），对话完结属同族，"物理接触纯度论"与已接受的门/攻击先例自相矛盾；且单发性质在实体层有保证（`DialogEntity.Advance` 在 IsFinished 后拒绝再进、`Restart()` 不发事件——代码亲核），不单靠 View 过滤承担。反方三条中"反馈已存在"最有力但属不同反馈通道（冗余是游戏感常态），UI 遮挡窗口极小（火花高 1m/寿命 0.22-0.45s）；施工报告如实列出反方并给出廉价撤销路径（删 handler+1 Subscribe+2 用例），把美学争议正确做成可逆决策——记录诚实，判断成立。
- **V4 PASS**——亲跑 grep：`grep -ri particle Assets/Game/Domain` = **0 命中**；`grep -rinE "vfx|emitter|monobehaviour|gameobject"` = **0 命中**；Domain 新增引用仅 `ZFrame.Gameplay` 的 `Vec2`（架构矩阵既有合法项）。三校验器本代理自跑全部 exit 0：arch（`arch_report.txt` 03:44:30，明示纯度扫描 `Assets/Game/Domain` 违规 0）、config（03:45:04）、naming（03:45:38），均晚于最晚源文件改动。**BT 记账核实属实**：`EnemyBehaviorTree.Kill()`（:214-218）仅置 `_isDead`+黑板旗标、不发任何事件，且 `EnemyController` 按 mode 二选一构造（BT 模式 `_fsm` 为 null）——BT 模式死亡确实不冒火花，报告 ③.4 为如实记账而非遮掩；"后续给 BT 发 EnemyStateChanged 复用本绑定"的建议可行。
- **V5 PASS**——文件规范抽查：10 个触碰的 .cs 文件 `tr -cd '\r'` 计数全为 0（纯 LF）、首字节无 BOM（首查 `grep $'\r'` 报数系 MSYS 空模式全匹配假阳性，经 od/tr 字节级复核推翻）；日志无 BOM。施工报告四节齐全（简报/施工报告①-⑤/验收清单/本节）；"测试未推帧被合并吸收"首跑 1 红的修正记录在 ④（属测试预期错误而非产品缺陷），且与代码对得上——:275 跨帧版（`_frame++`）+ :297 同帧合并版显式钉死，修正路径清晰。

**验收备注（不阻塞）**：① 工作区并行改动 B-R9-09 的 Native/* 与 `zrender.dll` M 属他任务（`git diff --stat` 证实本任务触碰面仅 Assets/Game/** + PlayMode 测试 + 本日志），主控收口分开提交；② 像素数值每轮有正常方差（门开 brighter 本轮 0.0172 vs 施工轮 0.0180），均高于门限 0.004（4.3×/4.5×），门关 0.0081/0.0082 均 2.0×，用例判定稳定；③ 本轮亲跑数字：EditMode 747/747 + PlayMode 16/16 + 三校验器 exit 0，基线 736+11 / 15+1 与报告口径一致。**未 git commit**（收口归主控）。
