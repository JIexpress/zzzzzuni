# 项目全貌与接入指南 —— 给下一个接入游戏开发 skill 的 Agent

> 定位：这不是代码审查报告，也不是复述 Unity 原生功能，而是让**下一个接入游戏开发 skill 的 Agent 快速接手**的说明书。
> 生成方式：2026-11-09 只读体检（未改任何代码/配置/构建产物）+ 收尾动作（登记 `TASK-A-AI-05` 为 held、补覆盖清单与调用示例、标注漂移点处置）。所有结论标注 **【已确认】/【待确认】/【推测】**，证据给文件:行号或目录。
> 本文件是**快照**，接手前仍以 `HANDOFF.md`（唯一权威状态源）的最新状态为准——本项目文档漂移是常态，见 §9.3。

---

## 0. 三十秒结论

- 项目是 **Unity 2022.3.62f3c1 上的"双轨"游戏框架**：Track A = `ZFrame`（玩法框架，纯 C# 分层），Track B = `ZRender`（自研 C++ 渲染内核，D3D12/Vulkan 双后端）。
- **它已经是一个"接近收口"的项目**：`TASKS.md` 看板 **144 done / 0 todo / 1 held**（2026-11-09 收尾后），三大里程碑全部 PASS，Windows/Android 都能出包，`Build/` 下已有 exe/安装器/APK。
- **下一棒不是补欠账，是选方向**。技术债在 `HANDOFF.md`「下一步优先级」+ `Docs/TECH-VALIDATION.md` 已列清楚；内容侧最大短板是"贴图 0 张、动画 0"。
- **你最容易踩的坑**：这是一套"主控/施工/验收"三角色治理工程，有严格的铁律（一任务一 commit、施工验收必须不同 agent、一切状态落盘）。**不要**跳过 `AGENTS.md` 直接开写。

---

## 1. 项目目标与背景

| 项 | 内容 | 证据 |
|---|---|---|
| 是什么 | Unity 游戏**框架**（ZFrame，玩法层可复用）+ 自研 **C++ 渲染内核**（ZRender，可独立跑、已能向 Unity 出画面） | `README.md:1` |
| 目标 | 用户立项"做游戏"，但真实诉求是**技术储备 + 引擎能力验证**，而非马上出一个完整游戏 | `Docs/框架设计方案.md:0`、`TECH-ROADMAP.md:3` |
| 当前完成度 | 功能侧无未闭环任务；游戏本体（星球探索+装配生存）**未开始**，处于"用户构思中" | `HANDOFF.md:43` |
| 完成度口径 | 原生 349 用例/778,723 断言 + EditMode 1140 + PlayMode 110 ≈ **1599 用例**；26 asmdef / 0 违规 | `HANDOFF.md:12`（实测值） |

**关键认知【已确认】**：用户提的很多"刁钻需求"（如"自研数据内核替代 ECS"、"手写 RenderGraph"、"Vulkan 语义 RHI 多后端"、"FSR4.1 主路径 + DLSS5 骨架"、"NPC 不是按预设跑而是自己找事"、"坎巴拉式装配"、"飞出大气层行星尺度"）**本质上是一个引擎级/中重度技术验证项目**。文档里反复出现的词是"对等闸门""变异验证""证据要硬"——这套工程的自律程度远高于普通 demo。

---

## 2. 引擎/框架全貌：三层改了什么

> 注意：这是**"魔改 Unity"的一种特定形态**——不是 fork Unity 源码，而是**在 Unity 之上自研渲染内核并桥接**，玩法层则用 asmdef 强隔离做"引擎无关的分层框架"。

### 2.1 项目层（玩法框架 Track A，纯 C#）【已确认】
- **分层 + asmdef 矩阵隔离**：`Core / Data / Gameplay / View / Editor`，26 个 asmdef，`ArchitectureValidator` 机器校验"谁不许引用谁"，`Game.Domain` 零 UnityEngine 引用。证据：`UnityProject/Assets/` 下 26 个 `*.asmdef`，`ZFrame/`、`Game/`、`ZRender/` 三大目录。
- **核心系统**：EventBus（强类型零装箱）、Services（服务定位器）、ObjectPool、GameLoopDriver、ResourceService（IAssetProvider 可切 Resources/Addressables）、ConfigSystem（SO+CSV 双轨）、SaveSystem（原子写/备份/迁移）、UIFramework（MVP+面板栈）、SettingsSystem（五页签数据驱动）、SceneLoader、AudioService（四总线）、FSM、BehaviorTree、CommandSystem（撤销/重做）。
- 详见 `Docs/项目全貌.md:32-153`（这是已存在的事实清单，比本文件细）。

### 2.2 引擎层（渲染内核 Track B，C++）【已确认】
- `Native/` 下六个 C++ 模块，CMake 构建出 `zrender.dll`：`zkernel`（数据内核）、`zrhi`（RHI，Vulkan 语义 + D3D12/Vulkan 双后端）、`zrender`（RenderGraph + 特性）、`zupscaler`（FSR/DLSS）、`zplugin`（Unity 原生插件 C ABI）、`tests`（doctest 349 用例）。
- **管线层次**：`Unity SRP 壳（剔除收集）→ P/Invoke → zplugin → RenderGraph 编排 → zrhi → GPU`。证据：`README.md:40`。
- **活跃管线是 URP 14.0.11**，ZRender 是可切换的第二管线（D3D12 门控，运行期不跨管线热切）。证据：`HANDOFF.md:38`、`UnityProject/Packages/manifest.json:5`。

### 2.3 编辑器层【已确认】
- `ZFrame/Editor/` + `Game/Editor/`：架构校验器、ConfigWorkbench、UI 校验器、命名检查、URP 变体剥离、DevServer（常驻本地服务，编译/测试/截图秒级反馈）、PlayerBuild（含 Android 签名校验）。
- 关键编辑器入口：`UnityProject/Assets/Game/Editor/PlayerBuild.cs`（`ConfigureAndroidSigning`，签名失败 exit 1，绝不静默退回 debug）。

---

## 3. 核心特性与调用方式（最小示例）

> 只列"能接手的 Agent 最需要知道怎么调用"的。完整清单见 `Docs/项目全貌.md`。

### Track A（玩法框架）
| 特性 | 入口/关键类 | 怎么用 |
|---|---|---|
| 事件总线 | `EventBus` | `EventBus.Publish<T>(evt)` / `Subscribe<T>(handler)`，强类型无装箱 |
| 服务定位 | `Services` | `Services.Register<T>(impl)` / `Services.Get<T>()` |
| 对象池 | `ObjectPool`/`ComponentPool` | 实现 `IPoolable`，`PoolResetter` 统一重置 |
| 资源 | `ResourceService` + `IAssetProvider` | `UseAddressables` 开关切 Provider，默认 off |
| 配置 | `ConfigService` + `ConfigTable` | SO 承载，CSV↔SO 双向（`CsvImporter`/`CsvExporter`） |
| 存档 | `SaveSystem` | 三槽位，原子写 + 滚动备份 + `IMigration` 迁移 |
| UI | `UIManager` + MVP | 面板栈，`SettingDefinition` 注册即生成设置行 |
| 场景 | `SceneLoader` | 异步加载 + `ScenePayload` 传参 + `InputBlocker` |
| 状态机/行为树 | `Fsm` / `BehaviorTreeRunner` | 敌人可在 FSM 与 BT 间切换（`EnemyDriveMode`） |
| 撤销/重做 | `CommandManager` | 双栈，`PickupCommand` 真实回滚 |
| 帧率治理 | `FrameGovernor` | 迟滞状态机 + 动态分辨率 + 画质阶梯。**实际路径** `ZFrame/View/Pipeline/FrameGovernor.cs` |

> **每个特性一个"照抄就能跑"的示例文件**（这些是项目内已存在的真实调用点，读它们比读类名有用）：
> - 事件总线 → `ZFrame/Tests/` 下 `EventBusTests.cs`（Publish/Subscribe 完整写法）
> - 服务定位器 → `ZFrame/Tests/` 下 `ServicesTests.cs`
> - 对象池 → `ZFrame/Tests/` 下 `ObjectPoolTests.cs`
> - 资源/Addressables → `ZFrame/Tests/Addressables/`、`ZFrame/Tests/AddressablesData/`
> - 配置 → `ZFrame/Tests/Data/` 下 Config 相关测试
> - 存档 → `ZFrame/Tests/Data/` 下 Save 相关测试
> - UI/MVP → `ZFrame/Tests/PlayMode/` 下 UI 面板栈用例
> - 撤销/重做 → `ZFrame/Tests/Domain/` 下 `CommandManagerTests.cs`（`CommandManager.cs` 同目录）
> - 帧率治理 → `ZFrame/Tests/Pipeline/` 下 `FrameGovernorTests.cs` / `FrameGovernorHostTests.cs`
> - 行为树/GOAP/效用AI → `ZFrame/Tests/` 与 `Game/Tests/Domain/` 下 AI 相关用例

**最小可运行代码片段（事件总线，来自既有测试范式）**：
```csharp
// 订阅
EventBus.Subscribe<PlayerDamagedEvent>(OnPlayerDamaged);
// 发布（强类型，无装箱）
EventBus.Publish(new PlayerDamagedEvent { amount = 10 });
// 退订（对称）
EventBus.Unsubscribe<PlayerDamagedEvent>(OnPlayerDamaged);
```

### Track B（渲染内核）
| 特性 | 入口 | 最小示例 |
|---|---|---|
| 原生窗口演示 | `zrhi_d3d12_window.exe` | `"$W" --draw / --lights / --shadows / --r5 / --fsr on / --parity / --backend both`（`README.md:70-79`） |
| 数据内核 | `zkernel` + C# `ZKernelNative` | 128 位掩码 `ZkMask`，SoA+chunk，`Each`/`EachParallel`，C ABI 12 个 `ZK_*` 导出 |
| 超分 | `zupscaler` | FSR 3.1（真机 path）/ FSR 4.1（代码路径，本机核显无 provider）/ DLSS5（Streamline 骨架） |
| Unity 桥接 | `zplugin.h` | `ZR_Unity_SetCamera(view16,proj16)` → `ZR_Unity_DrawMesh(handle, world16, rgba)`。**注意：ABI 全部是 float**，无 double（§9.1） |

> **C# 侧调 zplugin 的完整调用链**（P/Invoke 桥接，参考 `ZRender/Bindings/` 与 `ZRender/Runtime/`）：
> ```csharp
> // 1. C# 侧声明 P/Invoke（Bindings 层，纯声明无 Unity 渲染依赖）
> [DllImport("zrender")]
> static extern int ZR_Unity_SetCamera(float[] view16, float[] proj16);
> [DllImport("zrender")]
> static extern int ZR_Unity_DrawMesh(ulong handle, float[] world16, float[] rgba);
>
> // 2. Runtime 层调用链：SetCamera → CreateMesh → DrawMesh × N → RenderFrame
> //    见 Native/zplugin/zplugin.h:336 的注释（本帧无 DrawMesh 会回落画硬编码 cube）
> ```
> 实际桥接实现见 `ZRender/Bindings/`（P/Invoke 声明）与 `ZRender/Runtime/`（RenderPipelineAsset 实现，不依赖 URP）。

---

## 4. 目录结构与关键入口

```
GameFramework/
├─ AGENTS.md              ★ 任何 Agent 第一个读（铁律/命令）
├─ HANDOFF.md             ★ 唯一权威状态快照（当前进度/坑/下一步）
├─ TASKS.md               ★ 任务看板（一行一任务）
├─ WORKLOG.md             ★ 任务索引（132+ 任务一句话摘要）
├─ DECISIONS.md           ★ ADR 决策记录（已定不再重议）
├─ README.md              技术说明 + 五步跑起来
├─ Docs/
│  ├─ 框架设计方案.md      总体方案 v2（含附录A实现差异清单）
│  ├─ 项目全貌.md          事实清单（代码现状，非承诺）
│  ├─ 需求达成对照.md      立项需求 vs 交付 逐条对照
│  ├─ TECH-VALIDATION.md  三技术方向可行性验证（含差距/路径/验证方式）
│  ├─ TECH-ROADMAP.md     技术前瞻路线图（三大新方向+AI深水区）
│  ├─ LLM-HANDOFF-NOTES.md  ★ 132任务沉淀的操作手册（派发模板/故障手册/坑备忘）
│  ├─ 交接档案.md          ★ 环境坑/技术债/接手步骤
│  ├─ Demo.md / 玩家指南.md / 需求达成对照.md
│  ├─ Logs/TASK-*.md      单任务日志（简报/施工/验收四节）
│  └─ Standards/file-conventions.md  文件规范
├─ UnityProject/          Unity 工程根（Assets/Packages/ProjectSettings）
│  └─ Assets/{ZFrame,ZRender,Game,Scenes,Art,Audio,...}
├─ Native/                C++ 渲染内核（zkernel/zrhi/zrender/zupscaler/zplugin/tests）
├─ Tools/                 8 个构建/测试脚本
└─ Build/                 Windows64/ Installer/ Android/（产物，不入库）
```

**关键场景**：`SCN_Boot`（启动链）→ 主菜单 → `SCN_Game`（垂直切片，WASD 移动/E 互动/敌人 FSM/Esc 暂停/五页签设置）。另有 `SCN_Anim`/`SCN_Assembly`/`SCN_Utility`/`SCN_Empty`。

---

## 5. 运行 / 构建 / 调试流程

环境【已确认】：Windows x64 + Unity 2022.3.62f3c1 + VS2022 BuildTools(MSVC v143) + CMake ≥3.2 + Git Bash。Unity 批处理一律走 **Git Bash**。

```bash
# ① 打开工程：Unity Hub → UnityProject/（首次导入慢）

# ② 构建原生内核 + 部署 dll（自动跑 doctest + 拷贝 zrender.dll）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1

# ③ 一键全量测试（八段，任一失败非零退出）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1
#    单段：... -File Tools/run_tests.ps1 -Segments editmode
#    出包：PLAYER_BUILD=1 ；bench 门禁：BENCH_GATE=1

# ④ 玩垂直切片：打开 Assets/Scenes/SCN_Boot.unity → Play

# ⑤ 渲染窗口演示（仓库根跑）
W=Native/build/bin/Release/zrhi_d3d12_window.exe
"$W" --draw --backend both    # D3D12 vs Vulkan 逐像素对照 maxDelta=0
```

**平台限制【已确认】**：
- 活跃管线 URP 14；ZRender 仅 D3D12 门控，D3D11 明确回退 URP。
- Android 已 IL2CPP 出包（minSdk 22，Vulkan→GLES3），但需仓外 keystore（`C:/Users/1/Android/keys/`）。
- Metal 后端仅接口桩（无 Apple 硬件）；FSR 4.1 / DLSS5 真机需独显（RX7000/9000、RTX50）。

---

## 6. 项目约定与限制（违反会返工）

1. **三角色铁律**【已确认】：主控不写业务代码；施工与验收**必须是不同 agent**；一任务一 commit（`TASK-XXX: 摘要`）；禁止 force push、改历史 commit。证据：`AGENTS.md:22-28`。
2. **文件规范**【已确认】：UTF-8 **无 BOM**、LF 换行（`.ps1`/`.bat` 例外 CRLF）；不手改 `.meta`；不动 `Library/ Temp/ obj/ Native/build/`。证据：`Docs/Standards/file-conventions.md`、`AGENTS.md:55-57`。
3. **序列化/生命周期**【已确认，都有踩坑记录】：EditMode 下 `Awake` 不触发、`DestroyImmediate` 不回调 `OnDestroy` → 组件用显式 `Initialize()`/`Shutdown()` 接缝；`Canvas.SetParent` 会被静默转 WorldSpace。证据：`LLM-HANDOFF-NOTES.md:42-50`。
4. **停下来问人**【已确认】：验收 3 轮 FAIL；装 >2GB 软件；花钱/账号；改已定决策；对外发网络请求（开源依赖 https 除外）。证据：`AGENTS.md:60-61`。
5. **证据纪律**【已确认】：说"变快了"必须附改前改后测量；"改回旧值即红"的**变异验证**是杀手锏；像素证据给数字门限。证据：`LLM-HANDOFF-NOTES.md:14-17`。

---

## 7. 历史需求映射（关键需求 → 实现位置 → 状态）

> 完整逐条见 `Docs/需求达成对照.md`。这里只列"刁钻"的。

| 用户需求（原话提炼） | 实现位置 | 状态 | 证据 |
|---|---|---|---|
| "不用通用 ECS，自写数据内核（128位掩码/约3周期定位/SoA）" | `Native/zkernel/` + C# `ZKernelNative` | ✅ 吞吐 406M 行/s；**定位周期实测 4~5 cycles/op 非设计目标 3（如实，2026-11-14 @Zen4：端到端取数口径 5、含行内除法随机取址 17；POPCNT/TZCNT 直查实证）** | `需求达成对照.md:二-10`、`Docs/Logs/TASK-B-KERNEL-01.md` |
| "手写 Render Graph（屏障合并/别名复用/split barrier）" | `Native/zrender/` `rg_render_graph` | ✅ 别名真复用、屏障合并、split 落地 | `框架设计方案.md:附录A.3` |
| "渲染管线原生 C++ 重写 + 多后端 GPU" | `Native/zrhi/` D3D12+Vulkan | ✅ 双后端逐像素 maxDelta=0 | `README.md:104` |
| "FSR4.1 主 + FSR3.1 回退 + DLSS5" | `Native/zupscaler/` | 🟡 FSR3.1 真机✅；FSR4.1/DLSS5 代码+单测✅真机受限（无独显） | `README.md:109` |
| "NPC 不是按预设跑，是自己找事做" | `Game/Domain/AI/` Utility+GOAP+`Affordance/` | ✅ 效用AI+个性权重+GOAP+可供性扫描已闭环 | `HANDOFF.md:9-16` |
| "坎巴拉式装配（零件吸附连接）" | `Game/View/Assembly/` + `Game/Domain/` | ✅ 建造模式进 SCN_Game（B 键），K 存/L 读蓝图 | `HANDOFF.md:19` |
| "飞出大气层/重力变化/贴图↔建模无缝" | `Native/tests/planet_math.h` | 🟡 **数学+离屏渲染已证，Unity 侧为 0**，不能直接进游戏 | `TECH-VALIDATION.md:3` |
| "千人同屏（L0-L3 分档）" | `Game/View/Crowd/CrowdLODPipeline.cs` | ✅ 四档 LOD；L3 播片过场 | `TECH-ROADMAP.md:50-55` |
| "Android 真机 + 触屏" | `Game/View/...` 触屏 + `build_android.ps1` | ✅ IL2CPP 真机可玩 | `需求达成对照.md:三-1,2` |
| "命令模式撤销/重做/回放" | `ZFrame/Gameplay/Commands/CommandManager.cs` | ✅ 双栈+拾取真实回滚 | `需求达成对照.md:一-17` |

---

## 8. AI 构建过程中的关键决策与建议（采纳/放弃）

| 决策 | 采纳? | 证据/原因 |
|---|---|---|
| 双轨架构（A玩法+B渲染） | ✅ 采纳 | `DECISIONS.md:ADR-001` |
| "有推荐就按你推荐的来"→ 技术选型 | ✅ 采纳 | `ADR-002`（UI=UGUI+TMP、零依赖、SO+CSV、明文存档等） |
| 三角色治理 + 一切落盘 | ✅ 采纳（用户强要求） | `ADR-003` |
| 构建产物 dll 入库 | ✅ 采纳 | `ADR-004`（clone 即可跑 Unity 侧） |
| 用自写数据内核替代通用 ECS | ✅ 采纳 | `框架设计方案.md:397`（明确"未采纳 ECS 通用包，按你要求自写"） |
| 帧生成列为 R6 后可选 | ✅ 采纳 | `ADR-002`、`框架设计方案.md:397` |
| XeSS 默认放弃 | ✅ 放弃（按方案预设分支） | `需求达成对照.md:二-7`（Intel 插件停更） |
| Metal 后端 | ✅ 只留桩（不承诺） | `框架设计方案.md:403`（无 Apple 硬件） |
| 验收时 Unity 批处理僵死 → 允许主控中立取证+独立代码复核 | ✅ 变通采纳 | `ADR-006` |

**"建议被放弃"的典型**【已确认】：XeSS（官方插件停更，性价比低）、通用 ECS 包（用户点名自写）、Metal 全量（无硬件）、遮挡剔除 Hi-Z occlusion（viewMask 接口已留但未做，`框架设计方案.md:附录A.3`）。

---

## 8.5 本次体检覆盖清单（交底：读了什么、没读什么）

> 本文件是**快照**，不是全量审计。为避免下一个 Agent 误以为"没写到的区域=没东西"，这里如实交底覆盖范围。

**① 逐字读过的核心文档（结论据此得出）**
`README.md` / `AGENTS.md` / `DECISIONS.md` / `HANDOFF.md` / `TASKS.md` /
`Docs/框架设计方案.md` / `Docs/项目全貌.md` / `Docs/需求达成对照.md` /
`Docs/TECH-VALIDATION.md` / `Docs/TECH-ROADMAP.md` / `Docs/LLM-HANDOFF-NOTES.md` /
`Docs/交接档案.md` / `Docs/Logs/TASK-A-AI-05.md`

**② 只扫描了目录/文件名、未逐文件读的**
- `Docs/Logs/`（100+ 个 `TASK-*.md`，仅 glob 列名；单个任务细节需按需读对应日志）
- `UnityProject/Assets/ZFrame/**`、`Game/**`、`ZRender/**`（仅 `ls` 目录树 + 关键类 `grep` 定位，未逐文件读）
- `Native/**`（仅 `ls` 模块名 + `zplugin.h` 关键 ABI `grep`，未逐文件读）

**③ 完全未覆盖的**
- 工作区其余项目：`GameDev160/ gd-course/ style-course/ embedded-ui/ poetry-hall/ 植物图鉴/ zr-novk-build/`（与本项目无源码依赖，未排查）
- 隐藏目录：`.mimosa/ .zcode/ .workbuddy-ai/ .agents/`（未排查，可能含其他 agent 的会话/工具链产物）

**④ 关键边界结论（跨文档一致，可信度高）**
- `zplugin.h` 的 Unity ABI 全 float（`SetCamera(view16,proj16)` / `DrawMesh(handle, world16)`）——双精度世界/行星 Unity 侧为 0，此结论经 `grep` 直接验证。
- 活跃管线 = URP 14.0.11（`GraphicsSettings` 指向 `URP_Medium.asset`），经离线体检脚本 7/7 PASS 复核。

> **结论**：本文件的"框架全貌"结论可信；但任何**具体到某个类的实现细节**，都应以实际 `grep`/读源码为准，本文件只保证到"目录层 + 关键类定位层"。

---

## 9. 已知坑与风险

### 9.1 硬编码 / 平台分支 / 未完成项【已确认，跨文档一致】
- **ZRender 只画演示内容**：无 `DrawRenderers`/`DrawSkybox`，游戏 3D 内容不画；每帧 `WaitIdle`（CPU/GPU 串行化）。证据：`Docs/项目全貌.md:8.1`。
- **双精度世界 Unity 侧为 0**：`zplugin.h` ABI 全 float（`SetCamera(view16,proj16)`、`DrawMesh(handle, world16)`），行星/浮点原点只存在于 `Native/tests/`。证据：`Native/zplugin/zplugin.h:364-375` + `TECH-VALIDATION.md:4.3`。
- **ZRender 是非活跃管线**：活跃是 URP，切到 ZRender 需手动。`GraphicsSettings.m_CustomRenderPipeline = URP_Medium.asset`。证据：`HANDOFF.md:49`。

### 9.2 环境坑（每一条都真实踩过，接手前必读 `交接档案.md:§3`）
- **代理变量大小写重复**（`HTTP_PROXY`+`http_proxy`）会让 MSBuild 崩 → 构建前清理。
- **Unity 批处理**：单实例 + 跑测试**勿带 `-quit`** + xml 出现即算完成 + 5 分钟不退 `taskkill`。
- **性能证据只在空载时可信**：`CompatTelRunner`/`MsMpEng` 吃 20-40% CPU，先跑 `check_machine_quiet.ps1`。
- **色彩空间/管线切换后第一次运行不可信**（首帧全黑、粒子假红），证据必须复跑一次。

### 9.3 文档漂移（本体检实际发现）【已确认】

> **这些漂移点本体检"只发现、未修复"**（本文件声明未改任何代码/配置）。下表标注每个漂移点的**处置建议**，接手 Agent 应优先修掉，否则读源文档会被旧数字误导。

| 漂移点 | 现状（错误值 → 真实值） | 处置建议 |
|---|---|---|
| `HANDOFF.md` 领先提交数 | 写"26" → 实际 **50**（`git rev-list --count origin/main..HEAD`） | **待修**：更新 HANDOFF「git 现场」行 |
| `HANDOFF.md` 看板口径 | 写"133 done" → 实际 **144 done / 1 held**（A-AI-05 登记 held） | **待修**：更新 HANDOFF 速览 |
| `TECH-VALIDATION.md` 的 `AffordanceRegistry` 路径 | 写 `ZFrame/Gameplay/AI/` → 实际 `Game/Domain/AI/Affordance/` | **待修**：改路径 |
| `Docs/项目全貌.md` 测试数 | 原生 317（11-06）→ HANDOFF 349（11-09） | **待修**：刷新数字或标注生成日期 |
| `TASK-A-AI-05.md` | 未跟踪、看板未登记 | **已修**（本收尾：登记进 TASKS.md 为 held，日志补施工/验收节为"暂缓"） |

> **结论【已确认】**：任何一份文档的数字/路径都不要盲信，接手第一步先 `git log` + `git status` + 核对 `HANDOFF.md` 快照时间。上述"待修"项属于源文档的欠账，本指南不替源文档背锅——已修项在本指南 §0 和 TASKS.md 生效，待修项仍以源文档为准。

---

## 10. 后续 Agent 如何接入

### 10.1 必读文件（按序，前三个是铁律级）
1. `AGENTS.md` —— 治理铁律 + 环境命令
2. `HANDOFF.md` —— 唯一权威状态快照（先看快照时间）
3. `TASKS.md` —— 任务看板（确认 doing/todo 是否为空）
4. `Docs/交接档案.md` —— 环境坑 + 接手步骤
5. `Docs/LLM-HANDOFF-NOTES.md` —— 派发模板 + 故障手册 + 坑备忘（**派子代理前必读**）
6. 按任务读 `Docs/Logs/TASK-*.md` 单任务日志

### 10.2 如何调用"游戏开发 skill"
- 本项目已有 `.agents/skills/gamedev-course/` 知识库（`references/k03-advanced-systems.md` GOAP/效用AI、`k04-engine-internals.md` 大世界/浮点/LOD/SRP）。证据：`TECH-ROADMAP.md:4`。
- **渲染相关**：本会话加载的是 `unity-shader-support`（Unity 渲染管线与 Shader 技能包），它附带离线体检脚本 `check_shader_assets.py`（`pipeline`/`assets`/`variants` 三子命令，零依赖）。接手 Agent 若要做渲染改动，**先跑一次离线体检拿基线**再动手。
- **流程纪律**：不要一上来就写代码。先 `git status` + 读 HANDOFF → 确认无 doing 冲突任务 → 从 `HANDOFF.md`「下一步优先级」领方向 → 写简报 → 派施工（不同 agent）→ 派验收（再不同 agent）→ 主控 commit + 刷新三件套（TASKS/WORKLOG/HANDOFF）。

### 10.3 推荐任务模板（第一个任务怎么下）
**从 `HANDOFF.md`「下一步优先级」选一项**（当前列了：渲染三缺陷收口 / 动画扩展续 / SRP Batcher 可测化 / 内容管线起步 / Windows IL2CPP 正式包 / 推提交）。推荐第一个任务选**低风险、可自证**的，例如：

> "把 `run_tests.ps1` 八段跑一遍拿基线（`-Segments editmode,playmode,arch,config,naming`），确认 349+1140+110 用例全绿，把 `TestReport.txt` 的数字写回 HANDOFF 并修正已发现的文档漂移（领先提交数、测试数、Affordance 路径）。"

这是一个"审计+校准"任务，不碰业务代码，风险最低，能立刻验证你理解了治理流程。

### 10.4 验证方式
- 全量：`Tools/run_tests.ps1`（八段，exit code + `TestReport.txt`）。
- 原生：`Native/build/bin/Release/zrender_test.exe`（349 用例）。
- 渲染：`zrhi_d3d12_window.exe --draw --backend both`（maxDelta=0）。
- 玩法：Unity 打开 `SCN_Boot` 按 Play，对照 `Docs/Demo.md` 体验清单。
- 架构：`ArchitectureValidator`（26 asmdef / 0 违规）。

### 10.5 不要碰的区域【已确认】
- `Library/ Temp/ obj/ Native/build/` —— 构建产物，不入库，删了会破坏本地状态。
- `.meta` 文件 —— Unity 自动生成，手改会导致引用断裂。
- `zrender.dll` —— 受版本控制的构建产物（ADR-004），提交前 `git checkout --` 丢弃非任务 M 噪音。
- `DECISIONS.md` 已定决策 —— 要改需用户明确批准。
- **不要 push 远程** —— 当前领先 origin 50 个提交未推，推前必须用户确认（`AGENTS.md` 铁律）。
- 游戏本体（星球探索+装配生存）—— 用户"构思中，勿抢跑"。

---

## 11. 新 Agent 上手清单（照做即可）

- [ ] 1. `cd GameFramework && git log --oneline -5 && git status`（确认干净，注意可能有未跟踪的在途任务日志）
- [ ] 2. 读 `AGENTS.md` → `HANDOFF.md`（先看快照时间是否新鲜）→ `TASKS.md`（确认 doing/todo）
- [ ] 3. 读 `Docs/交接档案.md` §3（环境坑，省半天）
- [ ] 4. 跑一次基线：`Native/build/bin/Release/zrender_test.exe`（349 用例）+ `Tools/run_tests.ps1 -Segments editmode`（1140）
- [ ] 5. 从 `HANDOFF.md`「下一步优先级」选一项，写简报 `Docs/Logs/TASK-<新ID>.md`
- [ ] 6. 派施工 agent（读 LLM-HANDOFF-NOTES §5 派发模板）→ 派**不同**验收 agent → 主控 commit + 刷新三件套
- [ ] 7. 记住一条：**"这个项目的问题从来不是做不出来，而是做出来了但证据不硬"**（`交接档案.md:177`）

---

*本指南由只读体检生成，未修改任何代码/配置/构建产物。*
