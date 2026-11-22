# HANDOFF —— 项目当前状态快照

> 唯一权威状态源。接管者先读本文件，再读 `Docs/交接档案.md`（路线图+环境坑+技术债），再读 TASKS.md。
> ⚡ **给下一个 LLM 主控**：先读 `Docs/LLM-HANDOFF-NOTES.md`——132 个任务沉淀的全部操作手册（派发模板/变异验证法/故障处置手册/技术坑备忘/工具调用技巧/额度管理），照做即可满血接力。
> 历史交接记录已归档 git 历史（`git show G-113:HANDOFF.md`），WORKLOG.md 有全部任务索引。

**快照时间**: 2026-11-22（**A-COL-06 NPC 自主行为入账**）｜**刷新者**: 主控（ZCode 会话）｜**仓库**: `C://Users//1//Desktop//zcode//GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni（已同步）

## ⚡ 最新（2026-11-22 第六批：A-COL-06 NPC 自己找事）

- **用户原始需求「NPC 不是按预设跑，而是自己找事」落地（25aaadb）**：`ColonyActionScorer`（公开可测评分公式：人格/怨气/组织度/地位→行为分）+ `ColonyTaskBoard`（轻量自建）+ `ColonyDailyBehavior`（挂 GameWorld.DailyTick 既有循环）。五行为：Work/Protest/Recruit/Gossip/Rest，**泄压阀防全员抗议**（T2 Work 占比 43.2%∈[40,90] 区间断言）。T4 涌现加速对照：开启自主行为 +0.621 vs 关闭 0；T5 性能红线 30 天×100 人 0.019s（<50ms）。
- **方案裁决（施工选择+验收核对）**：方案 B 轻量自建——Game/AI TaskBoard 系小队战斗语义（Vec2/秒级过期/认领互斥），复用需泛化改造 Enemy 消费方（禁止）；方案 A 需 Colony 加 Game.Domain 引用，arch 矩阵钉死其只引 ZFrame.Core（必违规）。
- **验收**：6 次 Unity 启动独立亲证；变异第三位置（ProtestVent 泄压阀 0.4→0）→ RED 时 Work 占比 16.57% 崩穿下限——**泄压阀机制被独立证实**；施工第一处变异哑弹（RecruitOrgGain→0 不可区分）如实登记重做。全程零 taskkill 用户编辑器（两次锁残留均为施工批处理自身，命令行核实）。
- **主控认可四项**：Raw 折叠显示契约（Truth/Referenced 仍全量）、T4 下限断言、T2 裕度 3.2pp、中途两红已修（边去重/UGUI 顶点溢出）。
- 家底：原生 381/823,640、EditMode **1306**、PlayMode **119**、arch 28/14 条目/0 违规；看板 **182 done / 0 todo / 1 held**。
- **A/COL 序列候选**：报告面板滚动+异常项跳转；三档报告选择交互（花钱买 Raw）；月度报告 View 截图探针入门禁；A-AI-05（held）NPC 自主模式 G 键与 Colony 自主行为合流评估。

**快照时间**: 2026-11-22（**A-COL-05 垂直切片入账：打开游戏看见世界在转**）｜**刷新者**: 主控（ZCode 会话）｜**仓库**: `C://Users//1//Desktop//zcode//GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni（已同步至 4b9626c+本次）

## ⚡ 最新（2026-11-22 第五批：A-COL-05 框架与游戏本体正式会师）

- **垂直切片闭环（a1e67f7）**：`ColonyWorldHost`（View 层，RuntimeInitializeOnLoadMethod+sceneLoaded 门控挂 SCN_Game，**零场景/零 Bootstrap 改动**；GameLoopDriver TickQueue Logic 段注册；1 秒=1 游戏日可配；月结链 ApplyMonthlyPolicy(默认 Aggressive)→Compose→EventBus.Fire）→ `ReportPresenter`（纯 C# P 层）→ `ReportPanelView`（UIManager pure-view 通道代码构建 UGUI，无 prefab；Tab 热键；Toggle 查原始记录=玩家主动检查玩法）。生产侧 `ColonyConfigAdapter`（A-COL-02 裁决：View 承担 ConfigService→DTO 投影）。
- **测试家底**：EditMode **1301/1301/0**（1297+P1-P4）+ PlayMode **两轮 119/119/0 同形**（115+C1-C4，取证隔离：宿主销毁+面板离栈断言）+ arch exit 0（Game.View.asmdef +Domain.Colony 引用、校验器矩阵同步——主控批准，View→Domain 依赖方向正确）。DebugFastForward 加速钩子（正式构建零副作用）。
- **验收**：独立亲证全部数字+C2 月份断言变异复红（先落仓外备份 sha256，零滞留）；Docs/Parity PNG 5 项为 PlayMode 噪声 git checkout 丢弃。
- **已知不代表性（登记）**：表加载走编辑器通道（真机 build fail-soft，真机路径后续）；报告面板无滚动（Raw ~80 行超屏，UI 迭代项）。
- **下一步候选**：①Raw 面板滚动+异常项点击跳转 ②月度报告接「三档选择」交互（玩家选 Summary 还是花钱买 Raw——指挥代理 §1.2）③TaskBoard/UtilityAI 接组织度涌现（NPC 自主行为消费 Game/AI）④月度报告的 View 截图探针入门禁。
- 家底：原生 381/823,640、EditMode **1301**、PlayMode **119**、arch 28/14 条目/0 违规；看板 **181 done / 0 todo / 1 held**。

**快照时间**: 2026-11-22（**A-COL-04 月度报告+AI 瞒报入账**）｜**刷新者**: 主控（ZCode 会话）｜**仓库**: `C://Users//1//Desktop//zcode//GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni

## ⚡ 最新（2026-11-22 第四批：A-COL-04 月度报告三档 + AI 执政官瞒报）

- **指挥代理规格落地（3910c68）**：`MonthlyReport`（Summary/Standard/Raw 三档 + 四类异常项标出；**视野纪律=Summary/Standard 只吃 Public+亲历，Covert 只进 Raw**——报告「可对不上」的机制来源）+ `AiSteward`（AiPersona 四原型；激进 AI 月度「过度优化」产量+0.02/怨气+0.08 加速涌现链，Public《增产令》报喜、Covert【暗账】留痕；Verbosity<0.3 报告缩水；自我豁免过滤）。EditMode **1297/1297/0**（1292+M1-M5）；验收双变异复红（施工 M3/验收 M4 不同位置）。
- **主控流程事故与修正（如实登记）**：① A-COL-03 commit（dfeb0f3）时主控 add 命令只取 untracked，漏了施工交付的 9 个已跟踪文件修改（internal 通道）→ 补账 **369b3cd**（验收代理 git show --stat 逐一核实与施工清单一致）；② A-COL-04 验收代理在变异 RED 阶段被 600s 超时切断，变异短暂滞留工作区 → 主控核查 sha256 发现后提醒代理无损还原（回 ef7aab0b…与施工还原值逐字节一致）。**教训**：主控 add 必须覆盖 untracked+modified 两类；验收代理变异必须先落 sha256 备份再动手。
- 家底：原生 381/823,640、EditMode **1297**、PlayMode 115、arch 28/14 条目/0 违规；看板 **180 done / 0 todo / 1 held**。
- **A/COL 序列剩余**：A-COL-05 TaskBoard/UtilityAI 接组织度涌现（Game/AI 资产消费）；后续月度报告接 UI（UIFramework MVP）。

**快照时间**: 2026-11-22（**A-COL-03 存档入账**）｜**刷新者**: 主控（ZCode 会话）｜**仓库**: `C://Users//1//Desktop//zcode//GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni

## ⚡ 最新（2026-11-22 第三批：A-COL-03 存档/读档闭环）

- **世界可以存档了**：`ColonySaveData` DTO + `ColonySaveCapture/Restore`（纯 Domain）+ SaveSystem payload 桥（测试侧，Newtonsoft 对齐 ZFrame.Data.Tests 先例）。**规格 §4.4 落地**：背景层目录不入档、读档后 `EnsureProfile` 种子重放逐位一致（「读档后他性格变了」被 S2 断言钉死）；关系边按档重建、组织者按聚落 namedNpcs 反推注册地。EditMode **1292/1292/0**（1287+S1-S5）；验收双变异复红（施工 S2/验收 S3 不同位置）。
- **主控追认两处**：测试 asmdef precompiledReferences + Newtonsoft.Json.dll；DTO 增配 Policies/Quests(+QuestDelayed)/Dialogue 三节——Restore 是唯一输入，内容库不可种子重导，否则读档世界支线全空。
- **登记**：世界 Rng 流不入档（确定性契约只钉 NpcSlot，读档后随机事件序列不逐位复现——S3 只断言延续性）；TrySpawnOrganizer 重复同 id 涌现为 A-COL-01 既有行为，S3 按身份集合断言。
- **下一步**：A-COL-04 月度报告（ReportDetail Summary/Standard/Raw 三档+异常项+AI 瞒报分歧——指挥代理规格）；A-COL-05 TaskBoard/UtilityAI 接涌现。
- 家底：原生 381/823,640、EditMode **1292**、PlayMode 115、arch 28/14 条目/0 违规；看板 **179 done / 0 todo / 1 held**。

**快照时间**: 2026-11-22（**A-COL-02 数据管线入账**）｜**刷新者**: 主控（ZCode 会话）｜**仓库**: `C://Users//1//Desktop//zcode//GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni

## ⚡ 最新（2026-11-22 第二批：A-COL-02 真实数据进 ConfigService）

- **GLM 内容五数据源正式进引擎配置管线（b16429e）**：npcs(100)/places(120)/policies(40)/quests(12)/dialogue(300) 五张 CSV → A-CONF-02 表类零手写流程（生成 10 个 Row/Table 类）→ 真菜单链路导入 5 个 CFG_*Table.asset（errors=0、就地更新不换 GUID）。ColonyDataLoader 从 ConfigService 建局（origin→文化映射 §21 注释来源）。EditMode **1287/1287/0**（家底 1283+D1-D4）；验收 3452 cells 双侧 0 mismatch + 双变异复红。
- **架构裁决（沿用）**：Game.Domain.Colony 仍只引 ZFrame.Core；ConfigService→DTO 投影放测试侧 ColonyConfigAdapter，生产接线时宿主程序集承担——Domain 保持零配置资产依赖。
- **主控数据 bug 记录**：md→CSV 转换抓错正则分组导致 quests 选项列一度是字母占位符，已修复并同步（OptionA/B/C 12 行完整文本，Q01-Q12 Id 契约）。
- **下一步**：A-COL-03 存档/读档（简报已写：ColonySaveData DTO+Capture/Restore+SaveSystem payload 桥+S1-S5 确定性断言）；随后 A-COL-04 月度报告（Summary/Raw 瞒报分歧）、A-COL-05 TaskBoard/UtilityAI 接涌现。
- 家底：原生 381/823,640、EditMode **1287**、PlayMode 115、arch 28 asmdef/14 条目/0 违规；看板 **178 done / 0 todo / 1 held**。

**快照时间**: 2026-11-22（**游戏本体首接线：A-COL-01 闭环**）｜**刷新者**: 主控（ZCode 会话）｜**仓库**: `C://Users//1//Desktop//zcode//GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni

## ⚡ 最新（2026-11-22：A-COL-01 殖民地模拟核心迁入 Game.Domain）

- **游戏本体有了第一批真实 Domain 代码（16afa34）**：ColonyCore 模拟核心（源码在 `C:\Users\1\WorkBuddy\杂项\ColonyCore\`+`NpcSystem\`，net8 控制台 29 项自测，设计规格 14 份在 `杂项\设计\`）迁入 `Game/Domain/Colony/`——21 文件全部 C# 9 适配（块状命名空间/显式 using/零 Console），**`Game.Domain.Colony.asmdef` 仅引用 ZFrame.Core，noEngineReferences:true**（架构校验器矩阵加至 14 条目，纯度扫描 0 违规）。
- **垂直闭环 V1-V7 全真实断言**（`Game/Tests/Domain/Colony/ColonyVerticalLoopTests.cs`，内存 fixture 固定种子 20261122）：政策颁布→文化加权怨气→挖坑 30 天爆雷 / 商人涨价 ×1.36 包装「供应紧张」→ 观察动作取得间接证据 → 信念契约 30 天沉默 61 天离开带走情报 → 镇压×2 → 组织者从参数里涌现 → **PlayerFeed(25)<Truth(83) 信息不对称** → `ColonyEventPump` 把事件经 ZFrame EventBus `Fire` 发布。
- **验收 PASS（独立亲证）**：EditMode **1283/1283/0**（家底 1276+7 新增）；施工与验收各自做了**不同位置**的变异复红（施工改 V2 钳制值，验收改 V4 Secret 断言），还原 sha256 逐字节一致。
- **关键技术坑（登记）**：`Rng` 必须 class 不能 struct——按值传参会冻结随机序列（市场漂移/每帧判定永远同结果）；`NpcDirectory.Origins` 被 `Settlement.TrySpawnOrganizer` 依赖不可删；Protest 事件按设计由编排层写入（引擎代码零 Protest 写入路径，规格如此——弱者不广播抗议）。
- **已知不代表性（登记）**：V3/V7 依赖固定种子确定性（种子改动需复验）；`RelationshipGraph._fakeIntimateCount`/`RegionState._nextId` 为静态跨用例状态；GLM 内容数据（100 卡/120 地点/40 政策/12 支线/300 台词）尚未走 ConfigSystem CSV 管线（内存 fixture 代替，下一任务）。
- **下一步（A/COL 序列）**：① GLM 五数据源转 CSV 走 ConfigService（替掉内存 fixture）② SaveSystem 接「哪些 NpcSlot 已实例化」③ Game/AI 的 TaskBoard/UtilityAI 接抗议路由与组织度涌现 ④ 月度报告（指挥代理 §8：Summary+异常项+Raw 瞒报分歧）。
- 家底：原生 381/823,640、EditMode **1283**、PlayMode 115、arch 14 条目/0 违规；看板 **177 done / 0 todo / 1 held**。

## ⚡ 最新（2026-11-15 接管轮 IV：视锥接线 + 帧耗门禁）

- **B-VIS-02 从「库函数」变成「真在用」（630c803）**：`CullToViewMask` 接进 `ZRenderSceneBridge.Submit` 的逐物件循环，改两阶段（整批算掩码 → 只对可见条目取 mesh/矩阵/绘制），128 一条分块不截断。
  - **接线时挖出的真问题**：球版掩码（r=|extents|）是 AABB 判定的**超集**，直接替换会在视锥边缘多画 → 补 `Bounds` 重载按 Σ|n_i|·e_i 复算，与 `TestPlanesAABB` 同式。
  - 等价性 660 例（随机位姿×包围盒 / 远平面外 / 退化半径）分歧 **0**；相切 220 组里 10 组落在 1 ULP 膜内（三种写法对比确认是舍入，非判据差异）。512 条合成条目的五个计数**逐位相同**（禁用/层/视锥/无 mesh/drawn）。EditMode **1276**。
- **A-PERF-02 基线变门禁（15602f2）**：`Tools/bench_player.ps1`——5 轮 cost 口径、丢首轮、每指标取中位数，比 `Tools/Probes/baseline/player-frame-baseline.json` 的包络；`-Gate` 超包络 RED exit 1，取不到数据 exit 2（「无法证明没回归」按回归算）；`-SelfTest` 52 条。
  - 基线：GPU **3.1413** / 主 **3.5599** / 渲染 **0.7255** / SetPass 28 / Batches 84 / Tris 106738。
  - 验收亲自变异（baseline GPU 改小 30%）→ RED exit 1 → 还原 exit 0；把 `Build/Windows64` 改名 → MISS exit 2；`bench_regression.ps1` 的 34 条自测仍过、零改动。
- **已知缺口（登记，未做）**：`ZrVisibilityFrame.Validate` 里 `M_v ≤ N` 因自校口径**恒真**（真正有牙的是桥内断言）；`CullToViewMask` 每帧仍按条目逐个调用（未做「整帧一次校验」短路）。两处都要等有实测压力再动。
- **产物规矩**：`out/*.log` 与 `out/frame-baseline-*.json` 入 `.gitignore`（逐轮原始档只做本地证据），基线快照以 `Tools/Probes/baseline/` 为准；6MB PNG 不进库（大文件门禁）。
- 家底：原生 381/823,640、EditMode **1276**、PlayMode 115、arch 27/0；看板 **176 done / 0 todo / 1 held**。

## ⚡ 最新（2026-11-15 接管轮 III：第一版帧耗基线落地）

- **07 清单第 1 条已做（c7365b5）**：Player 探针 `FrameBaselineProbe`（只在 `-framebaseline` / `-framebaseline-vsync0` 时启用，其余零开销零日志），SCN_Game 1080p 采样 120 warmup + 600 帧，落 `Tools/Probes/out/frame-baseline-*.json`。
- **基线数字（本机 780M / URP 14.0.11 / Unity 2022.3.62f3c1 / Linear / Medium / 1080p）**：
  - **asShipped**：帧间隔 p50 **11.1105ms**——这是 90Hz + vSync 的**钳制值，不是成本**；
  - **cost（vSync 关）**：GPU p50 **3.12ms**、主线程 **3.61ms**、渲染线程 **0.75ms**（验收 5 轮中位数），帧间隔 ≈3.6ms；
  - 渲染统计：**SetPass 28 / Batches 84 / Triangles 106,782 / Vertices 97,051**（Medium、MSAA 2、renderScale 1，未降档）。
- **回归判据（验收修正）**：单次三连跑的 1.31% 包络**不能**当门限；有效门限 = **≥5 轮跨会话中位数 ± 实测包络（GPU≈10% / 主线程≈12% / 渲染线程≈20%）**，差值超包络才算回归。
- **已知不代表性**：无玩家输入、静态出生点、iGPU 受后台占用与冷启 PSO 影响（首轮偏低 +7.4% 反向未定位）；测量期不要并行采集 GPU 计数器（会自扰动）。
- **git**：HEAD `691799c`，已推 origin（含此前 10 个提交）。

## ⚡ 最新（2026-11-15 接管轮 II，五条 backlog 清零）

- **本轮并行两线五件，全部施工/验收分代理，todo 清零（174 done / 0 todo / 1 held）**。
- **原生线（ac79c50）**
  - **X-DIAG-02**：`-s` 全量 SIGSEGV 根因不在被测代码——doctest 把 `unique_ptr<char[]>` 判为可流化，成功断言打印时 `strlen(nullptr)`。改比 `.get()`（断言一条不少）。`-s` 全量 **381 用例 / 823,640 断言 / exit 0**；变异复现崩溃后逐字节还原。
  - **B-PLANET-13**：裙边口径**裁决保留** `2%×格距`（near 99.28m / far 416.98m）。关键量：near 侧 T 型落差 256.6m → 裕度 **0.387×**，但 1080p 下折合 **0.182px**（亚像素且背后是地形）；换设计口径（6254.7m）收益仅 0.182px→0，不采纳。登记：裕度下限 0.386868×、触发条件**档界 <147.17km**（当前 5.5× 余量）或加 mid 档时必须重算。
- **Unity 线（0d2d62e）**
  - **B-VIS-02**：`CullToViewMask` 真视锥剔除（128 位，复用 `ZkMask` 位序）+ `ZrVisibilityFrame` 录入/消费账本；相切可见、r=0 点测、NaN/∞/退化矩阵 fail-closed。首次变异 19/19 全绿（用例没抓住）→ 补「六面逐一越界」后才红，留痕。
  - **A-CONF-02**：表类零手写（行类+表类同批生成 + 反射注册，删手写 `TableByCsvName`）。**中途被验收判 FAIL**：菜单链路 `TableAssetPathFor` 产出带 `..` 的非规范路径 → 既 Load 不到、`CreateAsset` 还会清空同名资产，而用例显式传规范化路径造成假绿。返工：路径归一 + 存在则就地更新（不换 GUID）+ 用例改走**真菜单**；复验 PASS。
  - **X-DIAG-03**：审计 14 个写出点 + 3 个编码器，同源缺陷仅 planet3 两条（已修）；重生成后条纹列 638/638→0、绿通道 h/v **79.6→0.05**。写出端仍缺 pitch 守卫（需重编 dll），**如实登记为未做**。
- **测试家底**：原生 **381 / 823,640**；EditMode **1268**；PlayMode **115**；arch 27/0；naming 0 违规。
- **git 现场**：HEAD `0d2f900`，工作区干净（仅 `Tools/Probes/out/` 未跟踪证据）；**领先 origin/main 9 提交**（待用户确认后推送）。
- **剩余只有 1 条 held**：A-AI-05（NPC 自主模式 G 键 + 预约态，简报已写未动工，用户此前决定后置）。
- **补验待办（需另一台机/设备）**：fsr4 对端阳性 + 双机帧耗；Android 冒烟；Windows IL2CPP 补装。

## ⚡ 最新（2026-11-15，接管轮：B-PLANET-15 闭环）

- **接管方式**：读 U 盘三份文档（07 清单 / 08 需求对照 / ZRender 改进计划）+ 上一会话本地工作记忆（`C://Users//1//.zcode//cli//memories//projects//zcode-6da466a520fe1fb1//memory//zframe-zrender-project.md`，末条 11-14 P2 收官）+ HANDOFF + git 现场。上一会话留下 **B-PLANET-15 未提交半成品**，本轮接手完成。
- **B-PLANET-15 done（6423b82）**：多相机选型 b——管线仍只渲 `cameras[0]`，其余相机**首帧一行日志显式跳过**（不再静默）；探针加**内容锚**（limb 四角清屏/盘心地形/圆盘占比/RT 中心与原生 1×1 回读 sRGB 互证），杜绝 `nonBlack>0.2` 恒真。
- **验收抓到的真问题（本轮最有价值的一条）**：探针 fixture **独跑 5/5 绿、全量 5 红**——根因不是相机排序，是**前序用例 `LoadSceneAsync("SCN_Game")` 不卸载**，探针帧被遗留场景占据（`renderer=54 可读=6` vs 隔离 `0`）。修法：fixture 显式 `CreateScene("~ProbeEmpty")` + 销毁其余场景根对象 + 关掉非探针相机 + **断言 `Renderer` 计数为 0，环境不脏才继续**。修后全量 **115/115 两轮同形**。
- **测试家底**：EditMode **1235**、PlayMode **115**、原生 380/823,621、arch 27/0、naming 0 违规。
- **看板校准**：B-PLANET-02 / B-PROBE-01 重复且误标 todo（实为 done f5e6aa1 / 74ec9bd），已去重并改正；统计改为行扫描口径 **done 169 / todo 5 / held 1**。
- **git 现场**：HEAD `36545bc`（含 chore 版本号同步），工作区干净；**领先 origin/main 6 提交**（按 AGENTS 铁律，推远程待用户确认）。
- **剩余 5 条 todo**：A-CONF-02（表类薄壳）、B-VIS-02（真视锥）、X-DIAG-02（`test_zfg_env -s` 基线 SIGSEGV）、B-PLANET-13（裙边深度口径裁决）、X-DIAG-03（Parity 条纹，根因已实证待收尾）。
- **补验待办（需另一台机/设备）**：fsr4 对端阳性 + 双机帧耗；Android 设备冒烟（APK 就绪）；Windows IL2CPP 模块补装。

## ⚡ 最新（2026-11-14，双机分工 + 材料入仓 + P0 派发）

- **双机分工（用户 2026-11-14 拍板）**：本机（780M/RDNA3）=**引擎改进机**；另一台机（RX 9060 XT/RDNA4，`D:\Dev\ZFrame` 只读）=**游戏开发机**——第一版 EVA 修复循环用 URP（清单 `Docs/Planning/07-第一版清单-2026-11-13.md`）。本机本轮=按 U 盘《ZRender-改进计划-2026-11-13》补齐全部未完成项。
- **交接材料入仓（G-158）**：`Docs/Planning/`（07 清单/08 需求对照/改进计划/约法三章）；探针家底移 `Tools/Probes/`（env-probe.js 仓库自适应两机可跑，out/ 存两机证据）。**约法三章为硬规矩**：真跑/真输入/缺就报缺/可复核；**全仓写死能力表面一律视为伪造候选**（对端 demo FSR 选项写死被真机戳穿的教训，用户明确要求）。
- **08 对照结论（对端 AI 实测，基线 cdf4eb2=本机 HEAD，无漂移）**：框架要求基本全实现；ZRender 实为"旁路渲染器"（C ABI 只收 float 矩阵+rgba）；未做到=XeSS/FSR3.5/多视图共享可见性（B-VIS-01 裁决：共享可见性已实现）；半=Config 代码生成（已闭环 A-CONF-01）/替代 Unity/满血 bindless/3 周期实测（已闭环 B-KERNEL-01：实测 4~5/5，目标 3 未达如实记）/跨平台。**改进计划口径：不把 ZRender 做成完整管线，只做行星 chunk 专用通道（P2，第二章前，4–8 周）。**
- **P0 已闭环（B-FSR-01，ae03f62，验收 PASS 11P+1MISSING裁决）**：upscaler_caps.cpp 枚举适配器后建真设备传 `ZupFsr4Probe`。**实测修正——780M(iGPU)+仓内 SDK v2.3.0 即使真设备也无 4.x provider（null=真设备=2 条：3.1.5/2.3.4）**：「FSR4 本机不可用」是硬件真相（B-R6-02 结论成立），假阴性差异只在支持硬件出现（对端 RX 9060 XT null=2 vs 真=4 含 4.1.2）。修复价值=支持硬件上消除假阴性，本机负对照证无误报。350 用例/778,737 断言全绿（+1/+14）；失实注释 4 文件改事实陈述；fg_ffx.cpp/.h 同型遗留移交 B-FSR-02；**阳性对照移交 B-PROBE-01（两机探针，对端补验）**。
- **本机 env-probe 首跑**：HEAD cdf4eb2 / 556 C# / 6 场景 / 许可证在位；**本机 Build/Windows64 播放器缺 FFX DLL**（对端是手工补的 3 FFX+1 AGS）→ 引擎机玩家包当前不能跑 FSR（属实记录）；原生测试 exe 目录 FFX DLL 齐备，真机验证走 `zrender_test.exe`。
- **本轮进度（2026-11-14 凌晨）**：B-VIS-01 done（132d183）｜B-FSR-01 P0 done（ae03f62）｜B-FSR-02 P1 done（ce4bdf4）｜A-CONF-01 done（d15faad）｜**B-PLANET-01 done（80d82bc，FAIL一轮→修订→复验 PASS）**：P2 行星 ABI 设计稿定稿（`Docs/Planning/P2-行星ABI设计.md`，四项签名可抄、B-PLANET-02..12 已登看板）｜B-KERNEL-01 done（fcbcb95，验收 PASS K1-K7 变异亲做）——08 对照 #7 从「未验证」改判「已验证」：**实测 a1 4~5 / a2 端到端 5 / a3 含 idiv 17 cycles/op，设计目标约 3 未达（如实记录）**；BMI2 表述 6 文件纠正为 POPCNT/TZCNT｜**B-PLANET-02 done（f5e6aa1，验收 PASS P1-P7）**：行星 ABI 头落地——§14 汇编入 zplugin.h（接口冻结）、ApiVersion=3 六链同步、ZrPlanetNative 门面+门控 ≥3、def 零 diff｜**X-NAMES-01 done（42e981f，验收 PASS）**：naming 段 0 违规（A-CONT-01 的 6 处红灯清零）｜**B-PLANET-03 done（11a477f，验收 PASS Z1-Z6）**：行星数学生产化——Native/zplanet/ 在库、15 条 doctest 逐位回归、§② 重力留 tests（shim）｜**B-PLANET-05 done（3d3ac1c，验收 PASS C1-C7）**：SetCameraOrigin/GetCameraOrigin 实现、def 46→48、行星量级 round-trip 逐位 doctest｜**B-PLANET-06 done（63fa2f4，验收 PASS D1-D10）**：SetWorld+SampleHeight、def 48→50、**12,864 chunk 顶点采样 0 失配**（玩法贴地=渲染网格逐位）、并发 38,400 次零撕裂｜**B-PLANET-04 done（8b03db6，验收 PASS E1-E10）**：**P2 最大件落地**——chunk 通道六导出 50→56、RenderFrame 集成同深度缓冲、--planet3 真渲染实证（30 chunk 剔除 26/30.6 万像素非清屏色/截图入库）、相机原点迁移零 doctest 破坏、账目恒等式+变异复红、单 chunk 近档 7.4ms 实测补 MISSING；G1 uv 语义设计稿勘误、G4 改判 v1 全档核查（开销项转 07）｜**B-PROBE-01 done（74ec9bd，验收 PASS F1-F7）**：fsr4-probe 两机可跑（便携模式 exe+4DLL）、本机负对照三方逐位互证、FG 族双设备对照首次有数、**阳性对照待对端 RX 9060 XT 补验**（步骤在 Tools/Probes/README.md）｜**B-PLANET-07 done（b73c0c5，验收 PASS J1-J8）**：Unity 托管桥——chunk 流式管理器（分帧预算+近档硬帽）、**WorldOrigin 首次真接线**（ToCameraRelativeView 唯一边界）、账目 A1-A12、EditMode 1211/1211｜**B-PLANET-08 done（dce7e49，验收 PASS M1-M7）**：接缝专项——VB 缝宽 MISSING 清零（240 对三层口径，实测 ≈1×ULP）、三方案定版 A 维持裙边、neighborMask 三面锁定、**G4-R1 原生门：远档 seam 固定税 920ms→0.019ms**（B-PLANET-07 测得的 ~150µs/块税已消失）｜**B-PLANET-09 done（35fb6ee，验收 PASS N1-N7）**：planet-budget 探针——**1080p 判据 MET（fps 654，余量 22×，口径=30-chunk 测试场景）**、接缝零泄漏、**D9 MISSING 清零**（贴地偏差近档 max 197.8m→"玩法碰撞必须贴纯函数"设计输入）、附带实证修复 X-DIAG-03 根因（PNG 编码缺陷）｜**B-PLANET-10 done（1329dd6，验收 PASS P1-P7）**：abi-precision 探针——四点×双口径、正确路径 0.145~0.341 ULP 全达标、naive 对照 38.85×~1.68e5×（双原点设计价值量化版）｜**B-PLANET-11 done（9598c90，验收 PASS Q1-Q7）**：编辑器内集成取证——三档截图（near/mid 曲率/far limb）、三变体行走（50km 23 次重定位 camAbs 逐位守恒）、跳变三层全绿、PlayMode 114/114；**新发现：ZRenderPipeline 多相机缺口（只渲 cameras[0]）→B-PLANET-15 登记**；像素证据 fixture 隔离纪律确立｜**B-PLANET-12 done（da54772，验收 PASS T1-T7）**：IL2CPP+Android——C# 高度纯函数移植 **804 点 float 位模式逐位相等 0 位差**、Android APK 38.76MB 构建成功、P/Invoke 生成码实锤懒解析、PlanetRuntimeProbe 双模式进包（设备在位零改动可验）；A 段 Windows IL2CPP 构建 MISSING（Tuanjie 仅 Mono 变体，链路就绪补装后一条命令补验）｜**X-HAPTICS-01 done（1161c40，捎带验收 PASS H1）**：Windows Player 编译债 CS0103 清偿（Mono Player 全链 build+smoke PASS）。**→ P2 主线 B-PLANET-02..12 全部 done（收官快照见下节）**。backlog 新增：X-DIAG-02（基线 SIGSEGV）、B-PLANET-13 裙边口径、B-PLANET-14 排名阈值、B-PLANET-15 多相机；X-ARCH-01/02、X-HAPTICS-01 微修闭环。**远程已同步（用户 2026-11-14 授权；e992a96 已推，其后增量重试中）**。

## 🏁 P2 主线收官快照（2026-11-14，B-PLANET-02..12 全 done，11 任务 11 commit 全独立验收）

| 环节 | commit | 一句话 |
|---|---|---|
| 设计稿 | 80d82bc | 四项 ABI 签名可抄+精度预算十环节（FAIL 一轮→修订→复验 PASS） |
| ABI 头冻结 | f5e6aa1 | ApiVersion=3、§14↔头文件零签名偏离、门控 ≥3 |
| 数学生产化 | 11a477f | Native/zplanet/（§①③④，15 条 doctest 逐位回归） |
| 相机原点 | 3d3ac1c | SetCameraOrigin/Get（行星量级 round-trip 逐位） |
| 高度采样 | 63fa2f4 | 12,864 chunk 顶点采样 0 失配（玩法贴地=渲染网格逐位） |
| chunk 通道 | 8b03db6 | 六导出 50→56+RenderFrame 集成+--planet3 真渲染+账目恒等式 |
| Unity 托管桥 | b73c0c5 | 流式管理器（分帧预算）+WorldOrigin 首次真接线（唯一边界铁律） |
| 接缝专项 | dce7e49 | VB 缝宽 MISSING 清零（≈1×ULP）、定版裙边、G4-R1 门远档税 920ms→0.019ms |
| 1080p 探针 | 35fb6ee | 帧耗 MET 余量 22×（口径限定 30-chunk）+D9 清零（碰撞必须贴纯函数） |
| 精度探针 | 1329dd6 | 四点双口径 0.145~0.341 ULP 全达标、naive 对照 38.85×~1.68e5× |
| 编辑器集成 | 9598c90 | 三档截图+三变体行走（23 次重定位逐位守恒）+跳变三层全绿 |
| IL2CPP/Android | da54772 | C# 移植 804 点逐位+APK 构建+P/Invoke 生成码实锤；A 段/设备 MISSING 如实 |

**测试家底（收官口径）**：原生 380/823,621 全绿；EditMode 1223；PlayMode 114；arch 27/0；naming 0 违规。

**补验/待办**：①对端机 fsr4 阳性+双机帧耗（Tools/Probes/README.md 步骤）②Android 设备冒烟（APK 就绪，logcat `[PlanetProbe]`）③Windows IL2CPP 模块补装后 `build_player.ps1 -Backend Il2cpp` ④Backlog：B-PLANET-13/14/15（S 级）、X-DIAG-02/03、B-VIS-02、A-CONF-02。
- **git 现场**：main 领先 origin/main **51+ 提交**未推（推远程需用户确认——AGENTS.md 铁律）。

## ⚡ 最新-2026-11-09（A-AI-04 闭环——NPC 会自己走去找事做）

- **看板真实口径**：**144 done / 0 doing / 0 todo / 1 held**（held = A-AI-05，NPC 自主模式+预约态，简报已写未动工，正式开发前暂缓后置；A-AI-01/A-AND-09/A-AI-02/A-AND-10 早已闭环，**不要再派**）。
- **A-AI-05 暂缓（held，本次收尾登记）**：NPC 自主模式（G 键开关）+ 预约态（到达才占）。简报已写、**零半成品代码**（`NpcAutonomyMode.cs` 不存在、`AffordanceRegistry.cs` 无预约痕迹）。用户决定进入正式游戏开发，此功能后置，恢复时按简报 §五验收清单直接派施工。
- **测试真家底（实测）**：原生 **349 用例 / 778,723 断言全绿**（`zrender_test.exe`）；EditMode **1140** + PlayMode **110**（Linear 下实跑）；合计 **1599 用例**。架构校验 **26 asmdef / 0 违规**。
- **A-ANIM-02 动画扩展第一刀（d938565）**：跳跃从「只触发动画」变成真垂直位移——纯函数弧线（v0=√2gh、T=2v0/g、峰值 0.7500m、落地差 0.0000）、`Grounded` 由真值驱动、控制器补 Idle→Jump 与 Jump→Run（6 过渡）、空中二次请求被拒。两处变异复红后 sha256 还原。
- **A-ANIM-03 脚步 IK（0d65dd3）**：平地零干预（权重<0.05）、25° 斜板权重>0.3 且脚贴合地面法线；中性踝高在平地学习，避免「平地也拽脚」；变异关掉 IK → 用例必红后 sha256 还原。
- **A-AI-04 可供性接场景（89f3497）**：`SceneAffordances` 按真实锚点（桶/树/NPC）注册 3 条可供性、**零可见物**；`AffordanceSeeker` 走过去并占用（默认不挂、10s 超时）。修掉「自占用导致目标抖动」的 bug。验收 PASS 7/7。**受限**：Claim 发生在选中时而非到达时。
- **A-AI-03 可供性扫描（ccb5897）**：`Affordance`/`AffordanceRegistry`（容量/64 预算/最近可用）+ `AffordanceAction` 接入效用打分（`needWeight×clamp01(1-d/max)×available`）。既有 AI 四文件零改动。验收 PASS 7/7。
- **已知偶发**：物理边界用例 `ConnectedChain_...` 曾出现 0.0500005335 vs 0.05 的边缘红，复跑即过（验收已记录，未修）。
- **A-WORLD-01 双精度世界（fc08132）**：`WorldPosition`（double）+ `WorldOriginMath` 纯函数 + `WorldOrigin`（4000m 注册制重定位）。100km 处 float ULP **7.8mm** vs double 守恒 **4.4e-11m**；验收 PASS 7/7。**未挂载到任何场景**（零影响）。
- **A-ASM-03 建造模式（2dba3c7）**：SCN_Game 里按 **B** 进建造：Q/E 切 5 种零件、鼠标吸附连接（≤1.2m）、右键删除、**K 存 / L 读**蓝图（`slot1`，走 AssemblyGraphSync+SnapshotCodec+Rebuilder）。默认待机零可见物。验收 PASS 6/6，PlayMode 98。
- **A-LOOP-02 战斗闭环（4394a2d）**：敌人 2 点血 + 受击闪白，J 键扇形攻击（2.5m/±60°/0.35s），击杀计入 HUD；活敌才扣火堆耐久。验收 PASS 6/6。
- **技术验证报告**：`Docs/TECH-VALIDATION.md`（AI 自主 NPC / 装配 / 行星尺度三条线的现状-差距-路径-验证方式，含建议任务顺序）。
- **A-LOOP-01 最小可玩循环（ddfb03c）**：守住火堆 90s——纯逻辑状态机（Idle/Running/Won/Lost）、波次曲线（人数递增/间隔 6s×0.85ⁿ 下限 3s）、火堆耐久、IMGUI HUD、F 开始/R 重开；**待机态不建任何可见物**（像素用例零影响）。EditMode 1111/PlayMode 91。
- **A-CONT-01 内容管线（42f5a26）**：6 张程序化可平铺贴图 + 7 个环境材质挂 `_BaseMap`（底色归一白，否则二次压暗）+ SCN_Game 地面换成草地材质；内容守卫 2 例（贴图槽/导入设置/底色）。画面细节量 **+56%**。
- **A-CUT-01 L3 过场（accffb5）**：`CutsceneDirector` 门闸 + VideoPlayer→RT→全屏 RawImage；过场期间人群全局抑制（L0 隐藏、L1/L2 不提交、kernel 不读），**分配器仍不产出 L3**（A-AND-09 硬上限语义不变）。batchmode 下真解码、帧号推进；变异复红。
- **A-R9-19 色彩管线迁移（82daf86）**：Gamma→Linear + Medium/High 补 Tonemapping(Neutral) + 两档后处理重新分层（High/Medium 差 1.48x）+ ZRender 桥改 UNORM 视图。Linear 下 **1102+88 全绿**，SCN_Game 实拍均值 +8%、无裁切。
- **git 现场**：HEAD `57ecb98`（A-ART-01），工作区有 3 处未提交改动（TASKS.md 登记 A-AI-05、`Docs/Logs/TASK-A-AI-05.md` 收尾、`Docs/接入指南-给下一个Agent.md` 新增），**领先 origin/main 50 个提交未推**（A-AND-10 触屏包、A-AI-02、A-AND-09、A-AI-01、G-136~G-140 治理与交接手册）。
- **文档漂移已修（G-141）**：README 的 103 任务/916/48/311/25 asmdef、TASKS 行内统计 114、A-R9-18 空 commit、ROADMAP 方向四 doing/todo 全部对齐实测值。
- **盘点新发现（已处置，见下「渲染体检」）**：色彩空间 Gamma → **已迁 Linear**；HDR 无 Tonemapping → **已补**；`Art/Textures` **0 张位图**、动画 0、预制体 5（内容短板仍在）。

## 三十秒速览

| 项 | 值 |
|---|---|
| 任务看板 | **144 done / 0 doing / 0 todo / 1 held**（TASKS.md，held=A-AI-05 暂缓） |
| 测试家底 | 原生 **349 / 778,723 断言** + EditMode **1102** + PlayMode **88** ≈ **1539 用例**；三校验器 exit 0 |
| 代码规模 | C# 530 文件 / 86,755 行；C++ 175 文件 / 77,106 行（生产 27k + 测试 34k） |
| 可交付物 | `Build/Windows64`（81.8MB Mono 包）、`Build/Installer/ZFrame-Setup-0.1.0.32.exe`（24MB）、`Build/Android/ZFrame.apk`（IL2CPP，minSdk 22，Vulkan→GLES3） |
| 管线 | Unity 2022.3.62f3c1 + **URP 14.0.11**（活跃）；ZRender 自研管线为可切换的第二管线（D3D12 门控） |
| 最新里程碑 | 三技术切片全 PASS；AI 深水区（记忆/态度/协作/人群 LOD）收官；Android 触屏打磨完成 |

## 一句话现状

**功能侧无未闭环任务（1 项 held 暂缓）**：双轨（玩法框架 + 原生渲染内核）全部里程碑收口，Windows/Android 都能出包。**下一棒不是补欠账，是选方向**——用户已决定进入正式游戏开发；技术侧最值钱的三件事在「下一步优先级」，内容侧最大短板是零贴图零动画。

## 渲染体检（2026-11-09 主控只读扫描，未改任何渲染资产）

| 项 | 实测 | 判据 |
|---|---|---|
| 活跃管线 | `GraphicsSettings.m_CustomRenderPipeline` = `URP_Medium.asset`；包内只有 URP 14.0.11 | ZRender 需手动切换 |
| 三档映射 | QualitySettings 三档 → Low/Medium/High 三个 URP asset；MSAA 1/2/4，阴影距离 20/40/60，软阴影仅 High；三档 `m_UseSRPBatcher:1` | 映射正确 |
| 材质/Shader | 10 个 `.mat` **全部**引用 URP/Lit（guid `933532a4…`）；自定义 shader 仅 `ZRenderBlit.shader` | 无粉红风险 |
| 后处理链 | Low 无 override（无 HDR，按设计）；Medium = Tonemapping(Neutral) + Bloom 0.6；High = Tonemapping + Bloom 2.5 + ColorAdjustments(exposure 0.5/sat 14) | 三档分层，差 1.48x |
| 色彩空间 | `m_ActiveColorSpace: 1` = **Linear**（2026-11-09 A-R9-19 迁移） | 正确 |
| 深度/不透明纹理 | `m_RequireDepthTexture:0` / `m_RequireOpaqueTexture:0`；RendererFeatures 空 | 做 SSAO/软粒子前必须开 |
| 内容资产 | 贴图 **6**（程序化环境套件，256²）、动画 **0**、预制体 5、模型 25 FBX、音频 7 | 环境已有材质；角色仍是纯色 |

**渲染缺陷处置结果（2026-11-09 A-R9-19 已全部落地，Linear 下 1102+88 全绿）**：

| # | 缺陷 | 处置 | 证据 |
|---|---|---|---|
| 1 | Gamma 色彩空间 + URP（HDR/Bloom 在线性空间外混合） | 切 **Linear** | SCN_Game 均值 116.5→125.9（+8%）、pct>32=0.89%、无裁切；全量 1102+88 |
| 2 | HDR 开但无 Tonemapping（高光直接裁剪） | Medium/High 补 **Tonemapping(Neutral)** | 同上 |
| 3 | 三档后处理不连贯 | 重新分层：Low 无后处理 / Medium Tonemap+Bloom 0.6 / High +exposure 0.5+sat 14 | High/Medium on-off 差 **1.48x**（门限 1.35） |

代价与边界（如实）：
- **P4 灯光未逐场景重标定**：`m_LightsUseLinearIntensity` 随迁移翻 1，画面整体略亮——这是"接受新基准"，不是"调完"。某场景偏亮/偏暗就单独调该场景 `Light.intensity`。
- **ZRender 桥**在 Linear 下改用 UNORM 视图（`CreateExternalTexture(linear:true)`），dxgi 报错消失；代价是这条**非活跃**管线（活跃的是 URP）画面与原生直出存在亮度差。**2026-11-14 B-PROBE-02 实测（72126a0）：桥输出较 URP 直出亮 12.5%（luma 61.22→68.88）——"略暗"形容词被推翻**；差值是复合差（原生 Lambert+环境 0.45/sRGB 基色当线性 vs URP PBR+环境 0.12），背景逐字节相同（p50=0）；换视图前须先拆着色/颜色语义项（P1-b 前置）。数字+差值图：`Tools/Probes/out/linear-bridge-*.json`（探针 `ZRender>Probes>Linear Bridge Compare`，两次独立跑逐位可复现）。可消除该代价的 typeless 方案实测触发设备移除，留作 P1-b。
- 迁移工具 `Assets/Game/Editor/ColorPipelineSetup.cs` 保留为幂等重放入口，带 `ZF_COLOR_MIGRATION_OK=1` 闸门。

⚠ **踩坑记录（咬了两次）**：切色彩空间后的**第一次运行不可信**——首张截图全黑、首轮粒子像素用例假红（0.0003 vs 门限 0.0040），复跑即恢复。任何色彩空间/管线切换的证据都必须复跑一次再判定。

## 接管者动作序列

1. `git log --oneline -5` + `git status`（有 3 处未提交改动：TASKS.md 登记 A-AI-05、A-AI-05 日志收尾、接入指南新增）＋确认 `git rev-list --count origin/main..HEAD`（当前 50）
2. 读 `Docs/LLM-HANDOFF-NOTES.md`（操作手册）+ `Docs/交接档案.md`（环境坑/技术债）
3. 先跑一次基线再动手：`./Native/build/bin/Release/zrender_test.exe --success=false`（应 349/778,723）；Unity 五段用 `Tools/run_tests.ps1 -Segments editmode,playmode,arch,config,naming`
4. 从「下一步优先级」选一项 → 写简报 → 派施工 → 派**不同**验收 → commit → 刷新三件套

## 下一步优先级（按价值/成本排序，均无需新硬件）

| # | 事项 | 现状 | 代价 |
|---|---|---|---|
| 1 | 渲染三缺陷（已知会连锁，需按 P1→P5 做） | 试验已证伪并回滚，方案齐 | 大，含原生桥与美术标定 |
| 2 | 动画扩展续（脚步 IK → 程序化层） | 跳跃已闭环；IK 只有 look-at | 中，PlayMode 可自证 |
| 3 | SRP Batcher 与 DrawCall 可测化（A/B 测量，替代 Frame Debugger 目测） | 只有开关，无数字 | 中，PlayMode 用例可自证 |
| 4 | 内容管线起步（贴图/材质/动画） | 0 贴图 0 动画 | 大，决定「像不像游戏」 |
| 5 | 游戏本体（星球探索 + 装配生存） | 用户构思中，勿抢跑 | 大 |
| 6 | Windows IL2CPP 正式包 | 当前 Mono | 小，一条命令 + 验证 |
| 7 | 推 50 个提交 / token 轮换 | 待用户确认 | 极小 |

## 环境纪律速查（每条真实踩过，详见交接档案 §3）

- **Unity 批处理**：单实例（查进程+删 `UnityProject/Temp/UnityLockfile`）｜僵死（轮询 xml，5 分钟有果即 `taskkill //PID //F`）｜跑测试**勿带 -quit**｜证据以 xml 为准
- **子代理派发**：并发/额度限制 → 退避 5-10 分钟重试；停滞 10 分钟 → 查现场续派或轻量代理复核收尾（ADR-006）
- **代理变量重复**（HTTP_PROXY+http_proxy）崩 MSBuild——构建前按交接档案 §3.2 清理
- **Tuanjie Hub**：无头 CLI 不认（Android 子模块已用外部工具链绕行，勿重试）；unity.cn 页面是 SPA（shell 抓不到直链）
- **网络**：github raw 可能重置→REST API；含 % 中文勿用 python % 格式化（用 Edit 工具）
- **Android 工具链（已装配勿重装）**：`C:/Users/1/Android/`——JDK11/cmdline-tools 19.0/platform-tools/build-tools 34.0.0/NDK r23b；EditorPrefs 键名实测：`AndroidSdkRoot`/`AndroidNdkRootR23B`/`Jdk11Path`（硬校验 JDK major==11）
- **zrender.dll**：构建产物入库（ADR-004），提交前 `git checkout --` 丢弃非任务 M 噪声

## 待办备忘

- **50 个提交未推**（推远程前必须用户确认，AGENTS.md 铁律）；CI token 轮换仍成立（用户给的令牌与仓内凭据逐字节相同，非新令牌）
- `Tools/android_support_installer.exe` 456MB 已在 .gitignore（未入库），可删本地副本
- 仓库 515MB：FSR 两个 DLL 占 65MB、`Docs/Parity` PNG 约 20MB——ADR-004 允许，但 clone 成本高
- `TestPanelView` 仍随包（技术债 §5）；`run_tests.ps1` BOM 可顺手清
- CI 只跑 L1（native+static），Unity 层注释在 ci.yml 文末——**云端绿 ≠ Unity 绿**，别把 CI 当全量证据

## 最近任务日志

- 触屏打磨 A-AND-10（PlayMode 85 / EditMode 1099）、AI 队友 A-AI-02（1082）、人群 LOD A-AND-09、NPC 记忆 A-AI-01
- 全部历史：WORKLOG.md（132 任务索引）

## 下一个接管者

一切以磁盘为准：AGENTS.md → HANDOFF（本文件）→ TASKS.md → 交接档案 → LLM-HANDOFF-NOTES。写简报→派施工→派验收→闭环刷新。祝顺利。
