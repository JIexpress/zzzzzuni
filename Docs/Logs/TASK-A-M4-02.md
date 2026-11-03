# TASK-A-M4-02 垂直切片收口：启动全链 + PlayMode 冒烟 + 演示文档

状态: 待验收 | 派发: 2026-10-25 | 施工: 施工代理（2026-10-25） | 验收: （待填） | commit: （待填）

**前置**：A-M4-01 完成。

## 任务简报（主控写）

**目标**：M4 收口——可玩的垂直切片：Boot 场景 GameRoot 全启动链（服务初始化→主菜单面板→加载 SCN_Game→游玩：移动/交互/敌人/拾取/暂停/设置/存读档→退出主菜单），PlayMode 自动冒烟测试，演示说明文档。方法论"垂直切片先行"的验收物。

**设计要点**：
1. **启动链**（`Game/Bootstrap/`）：
   - SCN_Boot 挂 `GameBootstrap`（MonoBehaviour）：Awake 依序 `ZFrameData.Init()`→Services 就绪检查→EventBus 日志→SceneLoader.LoadScene("SCN_Empty"→改"SCN_Game"？主菜单先）——流程：Boot→主菜单（UI 面板：开始/设置/退出三按钮，UIManager.Push）→SCN_Game
   - 主菜单面板 `UI_MainMenu`（MVP：Presenter 订阅，开始按钮→SceneLoader 带 Payload 进 SCN_Game；设置按钮→SettingsPanel Push）
   - 游玩态：玩家移动（MoveInputEvent 驱动胶囊 CharacterController 简版或 transform）、E 交互、Esc 暂停面板（继续/设置/回主菜单——暂停=GameTime.IsPaused 冻结 Logic 档，M0-04 已备）
   - 死亡/拾取计数 HUD（订阅事件刷新文本）
   - 回主菜单：OnBeforeSceneUnload 钩子触发存档（ISaveable 接 Inventory 与玩家位置）→读档入口（主菜单"继续"）
2. **PlayMode 冒烟测试**（ZFrame.PlayMode.Tests 新程序集，`-testPlatform PlayMode -runTests`）：
   - 用例1 启动链：Boot 场景加载→若干帧后 UIManager 当前面板=主菜单
   - 用例2 进游戏：模拟点击开始（直接调 Presenter 方法）→SCN_Game 加载完成事件→玩家存在
   - 用例3 交互：构造门前→Fire Interact 命令→门状态翻转断言
   - 用例4 存读档：改 Inventory→SaveToSlot→读回一致
   - 用例5 暂停/恢复语义（IsPaused 冻结断言）
   - PlayMode 测试注意：Unity 纪律同 EditMode（xml 取证）；失败截图留 TempLogs
3. **演示文档** `Docs/Demo.md`：怎么跑（编辑器 Play/构建命令）、看什么（交互/敌人/超分开关——若 R6 已完成则含 --fsr4 演示截图）、当前限制清单（诚实）
4. 自测：EditMode 全绿+PlayMode 全绿+Validate exit 0+人工冒烟一次（施工代理开编辑器不可行——用批处理 `-executeMethod` 跑一个自动化"假装人工"流程截图存 Docs/Parity/demo_boot.png）

**约束**：可动 UnityProject/Assets/Game/、Assets/UI/、Assets/ZFrame/（Tests/新增 PlayMode 程序集；Gameplay 若需微调）、Scenes/、Packages/（PlayMode 测试无需新包）、本任务日志、Docs/Demo.md、Docs/Parity/；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：启动链+主菜单/暂停/HUD+PlayMode 冒烟 5 用例+Demo.md+施工报告四节。

## 施工报告（施工代理写）

**施工代理**：施工代理｜**日期**：2026-10-25｜**未提交**（禁 git add/commit）

### 1. 实现摘要

**启动链**（`Game/Bootstrap/`，新增 3 文件 + 2 编辑器文件）

- `GameBootstrap`（MonoBehaviour，SCN_Boot 独立 GameObject）：Awake 依序
  `GameRoot.Bootstrap()`（幂等）→ `ZFrameData.Init()` → 四服务就绪检查（IGameTime/
  IResourceService/IConfigService/ISaveSystem，fail-fast）→ `InputRuntime.EnsureCreated()`
  → `UIRoot.EnsureCreated()` → 创建 UIManager（服务层 ResourceService）→ 注册
  MainMenuView/PausePanelView 的 UIBinding → 订阅 SceneLoadedEvent/PauseInputEvent/
  SceneLoader.OnBeforeSceneUnload → `UIManager.Push<MainMenuView>`（UIBinding 装配
  MainMenuPresenter）。重复实例只销毁自身 GO 并转交存活实例补推主菜单；自身
  DontDestroyOnLoad。
- 流程：主菜单"开始/继续"→ `SceneLoader.LoadSceneAsync("SCN_Game", payload)`
  （继续时带 `load=1`）→ SceneLoadedEvent(SCN_Game) → 恢复播放态、(重)注册两个
  ISaveable（背包 + 玩家位置）、生成 GameHud、payload 命中则 `LoadFromSlot(0)` 读档；
  Esc（PauseInputEvent，游戏场景内）→ OpenPause：`GameTime.IsPaused=true`（M0-04
  冻结语义，Logic 档）**加** `Time.timeScale=0`（引擎侧冻结——切片的 PlayerMotor/
  EnemyView 直接消费 `Time.deltaTime`，单靠 GameTime 冻不住它们）+ InputBlocker
  （冻结 Player 键位映射）；暂停面板"继续/设置/回主菜单"分别恢复 / 复用同一
  UIManager 推 SettingsPanelView（含超分五选项实接）→ `QuitToMenu`：先解除暂停再
  `LoadSceneAsync("SCN_Boot")`。
- 存档：`OnBeforeSceneUnload` 钩子在活动场景为 SCN_Game 时 `SaveToSlot(0)`（背包 +
  玩家位置两个 ISaveable），随后 `InteractionWorld.ResetShared()`（落实 M4-01 报告
  §5.4 的移交事项，防陈旧 Domain 实体泄漏进下一场景）；主菜单"继续"按钮按
  `GetSlotInfo(0)` 显隐。
- **关键教训（验收关注）**：SCN_Boot 上**不得**把 GameBootstrap 与 GameRoot 组件放
  同一 GameObject——AudioService 在 BeforeSceneLoad 就 `GameRoot.EnsureInstance()`
  创建了 GameRoot 实例，场景内 GameRoot.Awake 的去重逻辑会 Destroy 整个 GO，把刚
  完成启动、已订阅事件的 GameBootstrap 一起带走（首轮 PlayMode 2 用例失败的根因，
  修复前后对比见 §3）。GameRoot 实例由框架自挂载保证，场景只挂 GameBootstrap。

**UI 层**（`Game/View/UI/`，新增 5 文件；prefab 由编辑器脚本生成）

- `MainMenuView`/`MainMenuPresenter`、`PausePanelView`/`PausePanelPresenter`：
  MVP，View 只发意图事件（纯 UGUI prefab，`LegacyRuntime.ttf`），Presenter 纯 C#
  （`UIPresenter<T>`，EventScope 保护），意图由 GameBootstrap 以 delegates 注入
  （MainMenuArgs/PausePanelArgs）。暂停面板 Esc 用 SettingsPanelView 同款 legacy
  Input 轮询（Player map 已被 InputBlocker 停用），且被上层面板阻塞时（OnPause）
  停止轮询，避免与设置面板的 Esc 双重响应。
- `GameHud`：HUD 层画布左上两行 UGUI 文本——拾取计数（订阅 ItemPickedUp，
  EventBus）与守卫提示（订阅各 EnemyView 的 FSM EnemyStateChanged，Dead 显示
  "守卫已被击败"）；随游戏场景生灭。
- `GameUiSetup`（Game.View 编辑器）：生成 `Assets/UI/Resources/ui_main_menu.prefab`
  （标题+开始/继续/设置/退出）与 `ui_pause_panel.prefab`（暗幕+继续/设置/回主菜单），
  幂等（有效 prefab 不动），UISetup 同款模式。
- 面板动画刻意取 None（不用 PanelAnim.Fade）：EditMode/批处理下协程不可靠，
  换取 DemoBootFlow 与 PlayMode 的确定性。

**PlayMode 冒烟**（`ZFrame/Tests/PlayMode/`，新程序集，Tests 豁免未登记矩阵）

- 5 用例（`BootChainSmokeTests`），全部走真实链路（菜单视图意图 → Presenter →
  GameBootstrap → SceneLoader），等待一律帧循环 + realtime 超时上限（无墙钟依赖）：
  1. `BootChain_ReachesMainMenu`——加载 SCN_Boot → GameRoot.IsBootstrapped、
     ZFrameData.IsInitialized、主菜单在栈；
  2. `MenuStart_EntersGameScene`——触发"开始"→ SCN_Game 激活、Player 存在、菜单弹出；
  3. `InteractCommand_FlipsDoor`——传送玩家至门前 → `EventBus.Fire(InteractInputEvent)`
     → 门实体 IsOpen 翻转；
  4. `SaveLoad_InventoryAndPositionRoundtrip`——改背包/位置 → `SaveToSlot(1)` →
     清空/移动 → `LoadFromSlot(1)` → 断言还原（用槽位 1，不污染演示槽 0，用后删除）；
  5. `PauseFreezesLogic_AndResumeRestores`——`Fire(PauseInputEvent)` → 暂停面板在栈、
     IsPaused、timeScale=0、`time.Advance(0.016)` 后 `DeltaTime==0`（冻结语义断言）→
     面板"继续"→ 全部还原。
- `GameBootstrap` 另留 `EnsureMainMenu()` 公共入口：SCN_Boot 经任何方式（SceneLoader
  或直接 SceneManager）重新加载后主菜单必定回栈（重复实例路径与 OnSceneLoaded 双保险）。

**演示文档与截图**

- `Docs/Demo.md`：编辑器 Play 与批处理双路径、8 项体验清单（含超分切换说明与键位）、
  11 条诚实限制清单。
- `Game/Bootstrap/Editor/DemoBootFlow`（-executeMethod）：编辑态逐步重放 Awake 启动链
  （GameRoot.Bootstrap → ZFrameData.Init → 服务检查 → Input/UI → Push(MainMenuView)，
  真实 prefab + 真实 UIBinding），UGUI 画布临时切 ScreenSpaceCamera 后相机 RT 渲染
  1280×720 → `Docs/Parity/demo_boot.png` + `demo_boot_log.txt`（Play 模式下 UGUI
  Overlay 画布无法在批处理截图，故取编辑态模拟；端到端真链路由 PlayMode 覆盖）。

### 2. 变更文件清单

新增（12 文件 + 2 生成资产；.meta 由 Unity 自动生成，guid 未手改）：

| 路径 | 说明 |
|---|---|
| `Assets/Game/Bootstrap/GameBootstrap.cs` | 启动链/游戏流程/暂停/存档钩子 |
| `Assets/Game/Bootstrap/GameSaveModules.cs` | InventorySaveable + PlayerTransformSaveable（ISaveable） |
| `Assets/Game/Bootstrap/Editor/BootSceneSetup.cs` | SCN_Boot 装配（含清理旧版合并 GO） |
| `Assets/Game/Bootstrap/Editor/DemoBootFlow.cs` | 启动链模拟 + 截图 |
| `Assets/Game/View/UI/MainMenuView.cs` `MainMenuPresenter.cs` | 主菜单 MVP |
| `Assets/Game/View/UI/PausePanelView.cs` `PausePanelPresenter.cs` | 暂停面板 MVP |
| `Assets/Game/View/UI/GameHud.cs` | 拾取计数 + 守卫提示 HUD |
| `Assets/Game/View/Editor/GameUiSetup.cs` | 两个 UI prefab 生成器 |
| `Assets/UI/Resources/ui_main_menu.prefab` `ui_pause_panel.prefab` | 脚本生成资产 |
| `Assets/ZFrame/Tests/PlayMode/ZFrame.PlayMode.Tests.asmdef` | PlayMode 测试程序集（Tests 豁免） |
| `Assets/ZFrame/Tests/PlayMode/BootChainSmokeTests.cs` | 5 冒烟用例 |
| `Docs/Demo.md` `Docs/Parity/demo_boot.png` `Docs/Parity/demo_boot_log.txt` | 演示文档与截图 |

修改：

| 路径 | 改动 |
|---|---|
| `Assets/Scenes/SCN_Boot.unity` | 挂独立 GameBootstrap GameObject（由 BootSceneSetup 写入；先前误挂的 GameRoot+GameBootstrap 合并 GO 已被同一脚本清理） |
| `ProjectSettings/ProjectSettings.asset` | 仅 `runInBackground: 0→1`——Unity 批处理 PlayMode 测试的自动副产物（Test Runner 写入），非人工配置变更，如实披露 |

未触碰：`TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`ArchitectureValidator.cs`；ZFrame 既有
代码零修改（Gameplay 未动，Domain/Gameplay 纯度未破坏——校验器证据见 §3）。

### 3. 自测结果

**① EditMode（`TempLogs/m4_02_editmode_final.xml`，最终态复跑，exit 0）**

```
<test-run id="2" testcasecount="489" result="Passed" total="489" passed="489" failed="0" inconclusive="0" skipped="0" ... duration="5.77" />
```

- 基线 489 → **489 全绿，零删减、零放宽**（本任务仅新增 PlayMode 用例，EditMode 计数不变）。

**② PlayMode（`TempLogs/m4_02_playmode3.xml`，exit 0）**

```
<test-run id="2" testcasecount="5" result="Passed" total="5" passed="5" failed="0" inconclusive="0" skipped="0" ... duration="6.00" />
```

- **5/5 全绿**。前两轮（playmode.xml / playmode2.xml）为 3/5，失败与根因如实记录：
  `PauseFreezesLogic` 与 `SaveLoadRoundtrip` 失败同因——SCN_Boot 上 GameBootstrap 与
  GameRoot 同 GO，AudioService（BeforeSceneLoad）先创建的 GameRoot 使场景内
  GameRoot.Awake 判定重复并 Destroy 整个 GO，已订阅事件的 GameBootstrap 随之销毁
  （诊断日志 4 次 "full boot starts" + 4 次 "GameRoot: duplicate instance destroyed"
  且零 SceneLoaded/PauseInput 佐证）。修复：GameBootstrap 独立 GO + 自 DDoL +
  重复时转交存活实例补推主菜单（§1 教训段），修复后一轮通过。
- 失败轮取证按要求保留：`TempLogs/m4_02_playmode.xml`、`m4_02_playmode2.log`。

**③ 架构校验器（`TempLogs/arch_report.txt`，exit 0）**

```
asmdef 扫描数: 20（期望矩阵条目: 10，Tests 程序集豁免）
纯度扫描目录: Assets/ZFrame/Gameplay; Assets/Game/Domain
违规数: 0
结果: PASS
```

- 新 asmdef `ZFrame.PlayMode.Tests` 按 `.Tests` 后缀豁免（ArchitectureValidator
  校验规则 141-147 行），矩阵 10 条目未动。

**④ Demo 截图（`Game.Bootstrap.Editor.DemoBootFlow.RunFromCommandLine`，exit 0）**

- `Docs/Parity/demo_boot.png`：PNG 31,675 B / 1280×720（PNG magic 校验通过），
  内容为启动链末态主菜单（无存档 → "继续"隐藏，与 demo_boot_log.txt 记录一致）。

**⑤ 规范**：全部新增文本 UTF-8 无 BOM + LF；`.meta` 由 Unity 生成。

### 4. 已知风险与限制

1. **玩家死亡未实现**（无生命值/伤害判定）：HUD 提示行落地为"守卫被击败"（敌人
   FSM Dead 态）；简报"死亡提示"按此理解，Demo.md 限制清单第 1 条已如实标注。
2. **暂停是双通道冻结**：`GameTime.IsPaused`（框架语义，PlayMode 用例断言）+
   `Time.timeScale=0`（引擎侧，因 PlayerMotor/EnemyView 直接消费 Time.deltaTime）。
   只做前者时切片可见玩法不会被冻住——实现取舍已在 §1 说明，验收如认为应改
   PlayerMotor 走 TickQueue Logic 档，属 Gameplay 重构，未在本次范围内。
3. **`demo_boot.png` 为编辑态模拟**（批处理无法对 UGUI Overlay 画布截图）；端到端
   真链路由 PlayMode 5 用例担保。
4. **SettingsEntry 框架示例按钮仍自挂载**（HUD 右上"设置"），与切片面板并存、
   走独立 UIManager 栈——未移除以保持 M3 验收物不动（Demo.md 限制 6）。
5. **门/NPC 的视觉反馈极简**：门状态仅体现在提示文案，NPC 对话无对话框 UI
   （Domain 推进，Demo.md 限制 3/4）。
6. `GameBootstrap.Awake` 保留两条 Debug.Log（完整启动/去重路径）作为启动链追踪
   证据；ZLog 通道默认关闭，服务清单日志实际不落编辑器控制台（框架既有行为）。
7. 面板动画取 None（避开批处理协程），如需淡入淡出应给 UIAnimRunner 增加编辑态
   安全路径——框架层改动，未纳入本任务。
8. PlayMode 测试在批处理下总时长 ~6s（含真实场景加载与 SceneLoader 0.4s 进度尾窗），
   未复现简报预警的"较慢或偶发超时"；如后续 CI 复现，按纪律先重跑再下结论。



## 验收清单（主控写）

1. EditMode+PlayMode 双全绿（用例数各写入结论）
2. 启动链服务顺序与 GameRoot Bootstrap 一致（行号）；主菜单 MVP 装配（UIBinding 注册）
3. 暂停语义：IsPaused 冻结 Logic 档断言（PlayMode 用例）
4. 存读档 PlayMode 用例真实（Inventory roundtrip）
5. Demo.md 完整可执行（验收代理按文档命令复跑启动链冒烟）
6. Validate exit 0
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收代理**：独立验收代理（与施工代理不同实例）｜**日期**：2026-10-25｜**总结论：PASS（7/7）**

全部证据由验收代理本机实跑/实读取得；EditMode 与 PlayMode 串行运行（跑前 `tasklist | grep Unity.exe` 确认 0 实例、删 UnityLockfile）。

### 逐项结论

**项1 EditMode+PlayMode 双平台自跑全绿 —— PASS**
- EditMode：`TempLogs/acc_m4_02_edit.xml`（验收自跑，exit 0）：`testcasecount="489" result="Passed" passed="489" failed="0" skipped="0"`，**489/489，基线持平零删减**。
- PlayMode：`TempLogs/acc_m4_02_play.xml`（验收自跑，exit 0）：`testcasecount="5" result="Passed" passed="5" failed="0"`，**5/5**。

**项2 启动链顺序与装配 —— PASS**
- `GameBootstrap.cs`：`GameRoot.Bootstrap()`:92 → `ZFrameData.Init()`:95 → 四服务就绪检查:98-101 → `InputRuntime.EnsureCreated()`:105 → `UIRoot.EnsureCreated()`:106 → UIManager:107 → `RegisterBindings()`:108 → 订阅 SceneLoaded/PauseInput/OnBeforeSceneUnload:110-112 → `PushMainMenuAsync()`:114。
- UIBinding 注册：`GameBootstrap.cs:150-151`（MainMenuView→MainMenuPresenter、PausePanelView→PausePanelPresenter）。
- 进 SCN_Game 带 Payload：`EnterGame` 构造 `payload["load"]="1"`:224-228 + `SceneLoader.LoadSceneAsync(GameSceneName, payload)`:230；读档消费 `OnSceneLoaded` `TryPeekPayload(LoadPayloadKey)`:353-359。
- **关键修复核实成立**：① `AudioService.cs:82`（BeforeSceneLoad）+:148 `GameRoot.EnsureInstance()` 确会在场景 Awake 前创建 GameRoot；② `GameRoot.cs:61-68` 确认 Awake 去重时 `Destroy(gameObject)` 销毁整个 GO（同 GO 必连坐）；③ 修复物证：`git diff SCN_Boot.unity` 仅新增**独立** GameBootstrap GameObject（场景无 GameRoot 组件）+ `GameBootstrap.cs:67-78` 重复时只 `Destroy(gameObject)` 自身并 `EnsureMainMenu()` 转交。施工报告叙述如实。

**项3 暂停语义断言 —— PASS**
- `BootChainSmokeTests.cs:152-157`：`time.IsPaused`:153、`Time.timeScale==0`:154、`time.Advance(0.016f)`:156 后 `Assert.AreEqual(0f, time.DeltaTime, "Logic stage dt must be zero while paused (M0-04 semantics)")`:157——真实冻结语义断言，非空壳。

**项4 存读档 PlayMode 用例真实 —— PASS**
- `BootChainSmokeTests.cs:120-137`：`Inventory.Add("gold",7)`+玩家位置:120-121 → `SaveToSlot(1)`:125 → 清空+移动:127-128 → `LoadFromSlot(1)`:130 → 断言 gold==7:133、position.x==1/z==2:134-135 → `DeleteSlot(1)`:137。Inventory 与位置双 roundtrip，真实。

**项5 Demo.md 可复跑 —— PASS**
- 按 Demo.md §1.2 原样复跑截图命令 `-executeMethod Game.Bootstrap.Editor.DemoBootFlow.RunFromCommandLine`：**exit 0**，`Docs/Parity/demo_boot.png` 重新生成（31,675 B 与施工报告一致；PNG magic `89 50 4e 47` 验证通过；日志 step0 资产校验/step1 服务就绪逐步推进）。
- 抽查一致性（3 条）：限制1 HUD"守卫已被击败"=`GameHud.cs:20,111`（EnemyStateChanged 订阅）✓；限制5 退出按钮=`GameBootstrap.cs:211 Application.Quit`（编辑器不退出）✓；体验6 超分五选项=`UpscalerSettingOptions.cs:48`（自动/关/FSR 3.1/FSR 4.1/DLSS 5）✓；另限制6 SettingsEntry 仍存在（`ZFrame/View/Settings/SettingsEntry.cs`）✓。
- **受限**：验收环境模型不支持目验图片内容，PNG 以 magic/尺寸/日志/确定性重跑佐证。

**项6 架构校验器 exit 0 —— PASS**
- 验收自跑（带 -quit）：exit 0；`TempLogs/acc_m4_02_arch.log` L331-334：`asmdef 扫描数: 20（期望矩阵条目: 10，Tests 豁免）/ 纯度扫描: Gameplay+Game/Domain / 违规数: 0 / 结果: PASS`。

**项7 文件规范 + 报告四节 + runInBackground 披露 —— PASS**
- 报告四节齐全（验收结论本节补齐）；状态行待主控刷 commit。
- `git diff ProjectSettings.asset` 仅 `-runInBackground: 0 / +runInBackground: 1`（L83），与披露一致。
- 新增文件 BOM/行尾以 `od` 实证：无 BOM、纯 LF（GameBootstrap.cs/GameSaveModules.cs/UI 5 文件/BootChainSmokeTests.cs/Demo.md/asmdef）。

### 防作弊核查

- **既有代码/测试 diff 审查**：工作区被修改的既有文件仅 4 个——TASKS.md、本日志、SCN_Boot.unity、ProjectSettings.asset。**无任何既有测试或 ZFrame/Gameplay/Domain 代码被修改**（施工报告"ZFrame 既有代码零修改"属实）；EditMode 489 与基线持平互为佐证。
- **Game/Domain 纯度抽查**：`grep -rln MonoBehaviour Assets/Game/Domain/` 零命中，无新 MonoBehaviour。

### 与施工报告不符处

1. 施工报告 §2 声称"未触碰 TASKS.md"——实际 TASKS.md 有改动（B-R6-03 行上移+A-M4-02 标 doing 的看板刷新）。内容无破坏，但与"未触碰"声明不符，如实记录（不影响结论）。
2. 其余披露（关键修复、runInBackground、demo 截图性质、失败轮保留）均核实相符。

### 遗留项

- FSR4/DLSS 真机化：既有外部补验项（硬件限制），非本任务引入。
- `demo_boot.png` 图像内容未目验（验收环境非多模态，受限如上）。
- 本日志状态行的 commit 号由主控闭环时填写（验收代理禁 git commit）。
