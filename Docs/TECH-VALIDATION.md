# ZFrame / ZRender 技术可行性验证报告

> 范围：只回答「技术上能不能做、还差多少、怎么证明」，不做产品设计，不写代码。
> 证据截至 2026-11-09，HEAD `1ac4cf3`；本文「已证」均指仓库内可查的测试/日志/截图，查不到的写「未验证」或「未找到证据」；`Docs/Logs/TASK-A-LOOP-02.md` 为在途未验收内容，只作现状提示。

## 0. 版本锁与基线

| 项 | 锁定值 | 证据 |
|---|---|---|
| 引擎 | Unity **2022.3.62f3c1** | `UnityProject/ProjectSettings/ProjectVersion.txt` |
| 管线 | **URP 14.0.11** 为活跃管线；ZRender 为手动切换的第二管线 | `UnityProject/Packages/manifest.json`；`HANDOFF.md:31-42` |
| 平台 | Windows 11 + **Radeon 780M 核显**；D3D12 主 / Vulkan 备 | `README.md:103`；`Docs/Logs/TASK-B-TECH-04.md:192` |
| 色彩 | Linear（2026-11-09 迁移） | `HANDOFF.md:46` |
| 测试 | 原生 **349 用例 / 778,723 断言**；EditMode **1111**；PlayMode **91**；合计 **1551** | `HANDOFF.md:11-12` |
| 看板 | **138 done / 0 doing / 0 todo** | `TASKS.md`（138 行 done） |
| 架构 | 26 asmdef / 0 违规；`Game.Domain` 零 UnityEngine 引用 | `HANDOFF.md:12`；`Docs/Logs/TASK-A-TECH-01.md:114` |

## 1. 结论速览

| 用户诉求 | 技术结论 | 最大风险 | 优先级 |
|---|---|---|---|
| AI 自主 NPC / 队友「自己找事」 | **可行**：效用 AI + 记忆/态度 + 任务板/GOAP 已闭环；缺「世界可供性」与导航 | 动作目录编译期固定，容易被看成「高级预设」 | P0 |
| 玩家装配机器/载具/建筑 | **可行**：连接点 + 关节 + 装配图 + 快照已闭环；缺玩法化与控制 | 大质量比、20+ 件的 PhysX 稳定性未验证 | P0 |
| 飞出大气层/重力变化/贴图↔建模 | **数学与离屏渲染已证**；Unity 侧为 0；**不能直接进游戏** | 双精度世界与 Unity Transform/PhysX 的接缝；1080p 未验证 | P1 |
| 最小可玩循环 | 仅有 90 秒计时的「守火堆」；战斗闭环在途未验收 | 是测试夹具，不是 loop | 暂缓，先做集成夹具 |

---

## 2. 方向一：AI 操作 NPC / AI 队友（自主找事）

### 2.1 现状（已有什么）

> 路径根：`UnityProject/Assets/Game/Domain/AI/`（GOAP 除外，见行内）；类名可在对应文件内检索。

| 能力 | 实现与证据 |
|---|---|
| 效用评分/排序 | `Utility/UtilityBrain.cs:151` `Tick`；`:224` 个性权重；阈值 0.05 → Idle（`:42`） |
| 动作目录 | `Utility/NpcUtilityAgent.cs:305-312`：Rest / Pickup / Follow / Patrol / Repair / Flee / Counterattack（固定 7 个） |
| 世界状态 | `Utility/NPCWorldState.cs:24-66`：疲劳、玩家距离、掉落距离、库存、工具箱/设备距离、恐惧/愤怒/信任、威胁距离 |
| 记忆/态度 | `Memory/NpcMemory.cs`、`Memory/NpcAttitudeMath.cs:72` `Evaluate`；`Utility/NpcUtilityAgent.cs:485` 聚合、`:533-535` 注入 Fear/Anger/Trust |
| GOAP | `UnityProject/Assets/ZFrame/Gameplay/AI/GoapPlanner.cs:28` `DefaultMaxDepth=3`、`:54` `Plan`；`GoapAction.cs:19` |
| 队友协作 | `Coordination/TaskBoard.cs:49-203`（发布/认领/完成/过期）；`Coordination/SquadCoordinator.cs:169-268`（编队槽/协防分派）；`Coordination/TaskBoardGoapBridge.cs`（认领+执行两步链） |
| 表现 | `UnityProject/Assets/Game/View/Npc/NpcUtilityView.cs:210` 直接把 `Agent.Position` 写进 `transform.position`；`SCN_Utility` 5 个 NPC |
| 测试 | 112 条 EditMode（Utility/GOAP/Memory/TaskBoard/Squad）+ 4 条 PlayMode（NPC 场景分化/态度） |

### 2.2 差距（离「进游戏」还缺什么）

1. **动作目录封闭**：7 个动作在构造时 `_brain.Add(...)` 写死；没有「对象→可做的事」查询，NPC 只能在预设集合里排序。
2. **没有导航**：全仓 `NavMeshAgent/NavMeshSurface` **0 命中**；移动是 XZ 平面直接插值（`NpcUtilityView.cs:210`），无避障、无垂直、无动态障碍。
3. **感知是手工注入**：掉落/工具箱/设备/威胁都是 `Transform` 字段传进来，不是世界扫描；新物件不会被「发现」。
4. **规划太浅**：GOAP 两步链吃满 `maxDepth=2`，剩余一步余量；无失败重规划、无多目标调度。
5. **无决策预算**：每 NPC 每帧 `Tick`；5 个可跑，20 个（L0 上限）未验证。
6. **记忆/态度不参与发现**：只调权重，不产生新目标。
7. **场景未接线**：`SCN_Utility` 独立演示，`SCN_Game` 里没有 AI 队友。

### 2.3 实现路径

| 方案 | 做法 | 人日量级 | 风险 | 原生改动 | 结论 |
|---|---|---|---|---|---|
| **A. 可供性注册表**（推荐） | 世界对象注册 `Affordance{verb,target,pos,baseUtility,tags,capacity}`；每 0.25s 扫描邻近项，生成候选动作/GOAP 目标，仍由 `UtilityBrain` 排序 | 6–9 | 低 | 无 | **先做** |
| B. NavMesh/局部 A* + GOAP 升级 | 加 `com.unity.ai.navigation`（当前 manifest **未安装**）；GOAP 深度 5、失败重规划、多目标 | 12–18 | 中：球面地形/移动装配体上 NavMesh 不成立 | 无 | 只补「多步任务」 |
| C. 数据驱动大规模 AI | DataKernel + HTN/行为树批量调度，L2/L3 人群也走同一决策 | 25+ | 高：Domain 纯度与 kernel 只读约束 | 可能要 | 不做 |

**推荐 A**：先让 NPC 能「看见」场景里的新物件并自己排优先级；B 只作为 A 的规划深度补丁，不引入 NavMesh 作为全局方案。

### 2.4 可验证性

| 层 | 证法 |
|---|---|
| EditMode | 加/删 affordance 后 `LastRanking` 与 `LastSelected` 变化；同状态同排序（确定性）；冷却边界；变异「关掉扫描」→ 用例必红 |
| PlayMode | 同起点 3 NPC 行为分化（已有 `UtilityNpcScenePlayModeTests` 范式）；运行中投放新物件 → 选择切换；决策预算断言（每帧决策数 ≤N） |
| 像素 | 不需要；`SelectionHistory`/HUD 文本足够 |

---

## 3. 方向二：玩家装配机器 / 载具 / 建筑

### 3.1 现状（已有什么）

> 路径根：`UnityProject/Assets/Game/View/Assembly/`（测试在 `UnityProject/Assets/Game/Tests/View/` 与 `.../ZFrame/Tests/PlayMode/`）。

| 能力 | 实现与证据 |
|---|---|
| 零件 | 7 类：Cube/Thruster/Wheel/Beam/FuelTank/Separator/FuelLine（`AssemblyPartKind.cs:11-33`） |
| 连接点 | `AttachmentPoint.cs`：每个点只允许一个 `AssemblyJoint`；局部 +Z 为外法线 |
| 吸附 | `AssemblyBuilder.cs:80` `TryFindSnap`（半径内最近空闲点）；`Connect` 五重拒绝链；对接缝 **1.5cm**（`:129`） |
| 关节 | `AssemblyJoint.cs:69` `Configure`（ConfigurableJoint 锁 6 自由度）；`:128` `Break` |
| 复合体 | 关节图 BFS，上限 256（`AssemblyBuilder.cs:229`） |
| 动力/资源 | 推进器 6N 恒推力（`AssemblyThruster.cs:20,75`）；燃料质量消耗（`AssemblyFuelTank.cs`）；分离器 |
| 拓扑/存档 | `AssemblyGraph.cs:118` `AddJoint`、`:203` `Validate`；`AssemblySnapshotCodec.cs:213` `Save` |
| 场景/测试 | `SCN_Assembly` 4 零件；29 EditMode + 14 PlayMode（按 `[Test]` 方法计）；10 秒稳定性（锚点误差 <0.05m、无 NaN） |

### 3.2 差距

1. **只在独立场景**：没有从 `SCN_Game` 进入的建造模式，也没有建造 UI（只有鼠标拖拽 `AssemblyDragController.cs:112`）。
2. **没有驾驶**：无控制舱、无轮驱/电机、无力矩/SAS/油门；推进器只有恒 6N。
3. **没有结构/气动/浮力/热/损坏**；轮子是球碰撞体近似（`Docs/Logs/TASK-A-TECH-02.md:117`）。
4. **大质量比未验证**：10 秒稳定只覆盖 3 体链；20+ 件、1:100 质量比是 PhysX 已知软肋。
5. **存档未接流程**：codec 明确不走 `ISaveSystem`，蓝图/世界状态没有落盘入口。
6. **无浮点原点**：装配体没有原点重定位，远距离精度会退化；与方向三冲突。

### 3.3 实现路径

| 方案 | 做法 | 人日量级 | 风险 | 原生改动 | 结论 |
|---|---|---|---|---|---|
| **A. 玩法化最小载具**（推荐） | 控制舱 + 轮驱/力矩 + 建造 UI（旋转/对称/撤销）+ 蓝图存档 + 进主场景 | 8–12 | 低 | 无 | **先做** |
| B. 结构/气动进阶 | auto-strut、质量比钳制、应力断裂、简化气动/浮力 | 15–25 | 中：780M 上 PhysX 预算未知 | 无 | A 之后按需 |
| C. 自研刚体/约束求解器 | DataKernel 上写固定步长求解器，绕开 PhysX | 30+ | 高：等于自研物理 | 是 | 不做 |

**注意**：KSP 是「节点吸附 + 资源 + 轨道」，旷野之息是「物理胶水 + 少量物体」；不要把两者混成一个需求。当前仓库的「点对点吸附」更接近 KSP，旷野之息式抓取/浮力/风场**未找到证据**。

### 3.4 可验证性

| 层 | 证法 |
|---|---|
| EditMode | 图校验/非法连接/快照 round-trip/版本迁移；质量与惯量计算 |
| PlayMode | 10s 稳定性（复用 ⑤ 范式）；推力位移/横向漂移；拆装往返；质量比阶梯（1:1/1:10/1:100）；蓝图保存→重载→同一拓扑 |
| 像素 | 装配前后同机位差异；吸附高亮开关差异（已有颜色断言） |

---

## 4. 方向三：飞出大气层 / 重力变化 / 贴图↔建模无缝

### 4.1 先澄清「贴图↔建模」的三件事

| 说法 | 实际技术 | 仓库现状 |
|---|---|---|
| 近景地面「贴图」 | 法线/视差/位移贴图，必要时曲面细分；不是真几何 | **未找到证据**（无 terrain shader/材质） |
| 远景地面「贴图」 | billboard / impostor，带 alpha 过渡 | 人群 L2 有 billboard（`UnityProject/Assets/Game/View/Crowd/CrowdLODPipeline.cs:14-15`），**地形未用** |
| 从太空看行星是「贴图」，落地变「建模」 | 行星盘/天空盒 → 真地形 chunk，跨尺度交叉淡化 | **未实现** |

三者的方案、代价、验收完全不同；「无缝」必须分开验收，不能一句「贴图换建模」概括。

### 4.2 现状（已有什么）

| 能力 | 实现与证据 |
|---|---|
| 浮点原点数学 | `Native/tests/planet_math.h:91` `FloatingOrigin`；`:181` `JitterAudit`；double 绝对坐标、渲染偏移 `float(obj-cam)` |
| 球面重力 | `:214` `GravityAccel`；`:282` `IntegrateGravity`（velocity Verlet）；轨道能量漂移 3.57e-9 |
| 球面几何/地形 | `:340` 切平面基；`:471` 4 octave fBm 高程；`:517` cube-sphere；`:561` chunk 顶点；`:572` LOD 两档迟滞 |
| 验证 | `Native/tests/test_planet.cpp` 15 条 doctest；窗口 `--planet`/`--planet2`：6144 chunk、2 LOD、seams=0、**640×480**、7.9–8.8ms |
| 大气 | `Native/shaders/planet2.hlsl:84-107`：高程色带 + 距离雾 + limb 增亮；**解析近似，非散射** |
| 截图 | `Docs/Parity/planet2_ground.png`、`planet2_midair.png`、`planet2_far.png`（验收亲验） |

### 4.3 差距

1. **不在生产库**：全部在 `Native/tests/`；`Native/zplugin/zplugin.h` 的 Unity ABI 只有相机矩阵、网格、`DrawMesh`（float），**无 chunk/双精度/重力接口**。
2. **Unity 侧为 0**：无浮点原点、无行星代码、无球面重力；`Physics.gravity` 全局 `-9.81`；`PlayerMotor` 用 `CharacterController`（`UnityProject/Assets/Game/View/Player/PlayerMotor.cs:15,74`），不支持任意方向重力。
3. **没有物理**：无地形碰撞、无 chunk 流式加载、无生物群系/贴图；6 面×32² 均匀 chunk、两档 LOD（16/64）、无自适应细分；裙边是「遮蔽非消除」；档界有弹跳（`Docs/Logs/TASK-B-TECH-04.md:186-195`）。
4. **性能口径有限**：640×480 离屏 7.9–8.8ms；1080p、编辑器内、IL2CPP、Android **全部未验证**。
5. **双精度接缝**：Unity Transform 是 float；原点重定位必须同时移动刚体、粒子、音频、导航；PhysX 只在局部原点附近可信，重定位时移动刚体/关节与接触约束的长期稳定性未验证。

### 4.4 实现路径

| 方案 | 做法 | 人日量级 | 风险 | 原生改动 | 结论 |
|---|---|---|---|---|---|
| **A. Unity 局部球面地形片**（推荐） | 8×8km 局部 patch（cube-sphere 单面内）；double 世界坐标 + 相机相对 float；4km 阈值原点重定位；球面重力逐刚体施加；2 LOD + 裙边 + MeshCollider | 8–12 | 中：PhysX/Transform 接缝 | 无 | **先做** |
| B. Unity + ZRender 混合 | 原生渲染行星（planet2 能力产品化），Unity 合成/物理；共享纹理 | 20–30 | 高：ZRender 非活跃管线；行星未接共享纹理；无物理 | 是（C ABI） | A 成立后按需 |
| C. 全原生世界 | 双精度世界 + 自研物理 + 流式地形 + 大气散射 | 60+ | 极高：等于自研引擎 | 是 | 本阶段不做 |

**A 的关键实现顺序**：① `WorldPosition`（double）与 `WorldOrigin` 单例 → ② 相机相对渲染 + 4km 重定位 → ③ 球面重力与自定义角色马达 → ④ cube-sphere 单面 patch 生成 + MeshCollider → ⑤ 两档 LOD + 裙边 → ⑥ 远景 impostor/行星盘交叉淡化。

### 4.5 可验证性

| 层 | 证法 |
|---|---|
| 原生 doctest | 现有 15 条（抖动审计/轨道守恒/共享边逐位一致/LOD 迟滞）；新增 C ABI round-trip |
| EditMode | 重定位前后「相对位置不变」（逐位/容差）；极点/赤道重力方向；chunk 共享边 memcmp；LOD 迟滞边界 |
| PlayMode | 跨 chunk 行走 1km 无可见跳变（截图差异阈值）；径向重力跳跃落回；1080p 帧预算 |
| 像素 | 贴地/中空/轨道三图（原生已有先例）；Unity 侧同机位前后对比；**1080p 与 IL2CPP 未验证** |

> **P2 设计稿（2026-11-14，TASK-B-PLANET-01）**：`Docs/Planning/P2-行星ABI设计.md`——四项行星 ABI 签名（double 原点/chunk 句柄/LOD 邻居/高度采样）、内存与线程协议、精度链路误差预算、B-PLANET-02..12 拆解与验收判据落点。

---

## 5. 方向四：最小可玩循环（现状与定位）

| 项 | 现状 | 证据 |
|---|---|---|
| 规则 | 90s、火堆 100HP、波次 6s×0.85ⁿ 下限 3s | `UnityProject/Assets/Game/Domain/Survival/SurvivalLoopState.cs:15-50` |
| 表现 | IMGUI HUD + Primitive 敌人；待机不建可见物 | `UnityProject/Assets/Game/View/Survival/SurvivalLoop.cs:20,284` |
| 测试 | 5 EditMode + 1 PlayMode | `UnityProject/Assets/Game/Tests/Domain/SurvivalLoopStateTests.cs`、`.../Tests/PlayMode/SurvivalLoopPlayModeTests.cs` |
| 在途 | 战斗闭环（敌人血量/玩家 J 攻击）**未验收** | `Docs/Logs/TASK-A-LOOP-02.md`（未跟踪） |

**差距**：没有玩家生命/死亡、没有采集/建造/战斗动词、没有 progression、没有存档、没有与 AI/装配/行星接线；敌人是裸 Primitive。它是**技术夹具**，不是可玩的 loop。

| 方案 | 做法 | 人日 | 结论 |
|---|---|---|---|
| **A. 保留为集成夹具**（推荐） | 只加「装配体/NPC 队友进场」冒烟，证明三条线能同场景 | 2–3 | 先做 |
| B. 垂直切片 1.0 | 采集→建造→防守→撤离，接 UI/存档/内容 | 15–25 | 世界/装配成立后 |
| C. 另起一个新 loop | 换题材重做 | 30+ | 不做 |

---

## 6. 主流二游框架能借鉴什么（只讲结构）

参考课件：`GameDev160/01_PPT课件/` 共 **40 份**（M01–M36 + PA1，含 M28A/B、M31A/B、M32A/B 变体）；其中 `M28A_米哈游案例研读.pptx`（16 页：Unity 魔改全景/渲染管线/多端画质分级/资源与热更/版本工程）、`M28B_鹰角案例与引擎路线决策.pptx`（15 页：魔改 vs 自研四维决策）、`M32A/B`（标杆拆解）、`M13`（资源与场景）、`M19`（网络多人）、`M27`（渲染管线魔改）与本题最相关。

| 模块 | 二游常见结构 | 可借鉴的「结构」 | 本项目决策 |
|---|---|---|---|
| 登录/账号 | 账号服、渠道 SDK、游客绑定、token 续期 | 启动链分层、会话状态机 | **不做账号服**；现有 Boot→Menu→Game 已够 |
| 抽卡/养成 | 卡池、保底、材料、突破、体力 | 配置表驱动、数值与表现分离 | **不做经济/概率/付费** |
| 关卡/活动 | 章节、日常、活动日历、首通奖励 | 关卡定义 + 状态机 + 奖励表 | 只借「关卡定义」，不做运营日历 |
| 角色展示 | Live2D/3D 看板、语音、好感度 | 角色 Definition、展示场景隔离 | 复用 `ModelStageLayout`；**不引入 Live2D** |
| 资源热更 | AssetBundle/CDN/版本 diff/灰度 | 资源分组、按需加载、Provider 抽象 | 已有 `AddressablesProvider`（A-R8-02，本地）；**不做 CDN/热更** |
| 服务端权威 | 战斗结算校验、反作弊、跨端同步 | 无（单机不需要） | **不做**；全仓仅 DevServer/DevConsole 本地开发通道，未找到账号/支付/匹配证据 |
| 战斗/队伍 | 队伍编成、角色技能、AI 队友 | 单位 Definition、队伍槽位、任务板 | **可借**；AI 队友已落地（A-AI-02） |

**明确不该做**（会把技术验证工具做成半成品手游）：
1. 账号/登录/渠道 SDK/支付——需要服务端、合规、长期运营，且与 1551 条单机测试的确定性前提冲突。
2. 抽卡/保底/体力/货币——经济系统是产品与运营问题，不是技术验证；没有服务端就没有权威账本。
3. Live2D/语音/好感度剧情——中间件授权 + 美术产线，投入产出比最低。
4. 服务端权威/反作弊/跨端同步——单机项目，无后端；引入即改变整个架构。
5. 活动日历/日常/赛季/热更 CDN——live-ops 能力，当前没有运营对象。
6. 开放世界任务系统/对话树——内容密集型，先把「世界/装配/AI」三条技术线跑通。

**可以借的只有结构**：分层（Core/Data/Gameplay/View 已有）、配置表（SO+CSV）、单位 Definition（`NPCPersonalityDef`/`AssemblyPartKind` 已是雏形）、资源 Provider 抽象、存档迁移、UI MVP/面板栈、可观测性（DevConsole/HUD/测试）。

---

## 7. 建议顺序（按「最快让作品成立」）

> 人日为单人估算（含测试，不含美术）；标注「并行」的任务文件域不重叠，可同时开工。

| # | 任务（建议编号） | 内容 | 前置 | 人日 | 并行性 |
|---|---|---|---|---|---|
| 1 | **A-ASM-03 装配玩法化** | 控制舱/轮驱/力矩 + 建造 UI + 蓝图存档 + 从 SCN_Game 进入 | A-TECH-05、SaveSystem、UIFramework | 8–12 | 与 2 并行 |
| 2 | **A-AI-03 可供性扫描** | `Affordance` 注册/扫描 + 决策预算 + 队友接主场景 | A-TECH-01/04、A-AI-02 | 8–12 | 与 1 并行 |
| 3 | **A-WORLD-01 双精度世界** | `WorldPosition` + `WorldOrigin` + 4km 重定位 + 相机相对渲染 | 无（参考 B-TECH-03 数学） | 6–9 | 与 1/2 可并行（先定接口） |
| 4 | **A-WORLD-02 可行走地形片** | 8×8km cube-sphere patch + 2 LOD + 裙边 + MeshCollider + 球面重力马达 | 3 | 10–15 | 串行（关键路径） |
| 5 | **A-LOOP-03 集成切片** | 落地→建造→NPC 队友防守→起飞；内容 pass | 1–4 | 10–15 | 最后 |

**排序理由**：1、2 是把已有能力「变成可玩」的最短路径（装配与 AI 都只差最后一层玩法化）；3、4 是差异化但周期长，且 4 是 5 的硬前置；5 才叫「作品」；A-LOOP-02（战斗）在途，不要重复派。**关键路径**：3 → 4 → 5；1、2 与 3 可同时开工，但 1 的蓝图坐标、2 的感知查询要预留 `WorldPosition` 接口，避免 3 落地后返工。

---

## 8. 未验证清单（不要写成已完成）

| 项 | 状态 |
|---|---|
| 行星场景 **1080p** 帧耗 | 未验证（只有 640×480 离屏 7.9–8.8ms） |
| Unity 侧浮点原点/球面地形/球面重力 | 未实现（grep 无 `OriginShift`/planet/自定义重力） |
| 浮点原点与 PhysX 刚体/关节长期稳定性 | 未验证 |
| 地形「贴图↔建模」无缝过渡 | 未实现（无地形 impostor/位移贴图/交叉淡化） |
| 20+ NPC 决策预算 | 未验证（现 5 NPC） |
| 20+ 零件、大质量比关节稳定性 | 未验证（现 3 体链 10s） |
| ZRender 作为活跃管线跑完整游戏场景 | 未验证（仅立方体/共享纹理演示） |
| Android / IL2CPP 上的行星或装配 | 未验证 |
| FSR 4.1 / DLSS 5 真机 | 硬件受限（780M 核显，`README.md:109`） |
| 账号/支付/匹配/热更 | 未找到证据（不做） |
