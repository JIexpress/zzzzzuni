# TASK-A-M2-02 AudioService 音频服务（四总线/交叉淡化/纯 C# 驱动）

状态: doing | 派发: 2026-10-24 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：A 轨 M2 第二件——音频服务：Master/BGM/SFX/UI 四总线（AudioMixer）、音量 dB 映射、BGM 交叉淡化、SFX 池化播放、**纯 C# 核心**（可 EditMode 测），由 GameLoopDriver 驱动（方案 §6.6）。

**设计要点**：
1. 资产（入库）：`Assets/Audio/MIX_AudioMixer.asset`（Setup 脚本生成：Master→{BGM,SFX,UI} 四 Group，暴露 Volume 参数）；测试音频可省（EditMode 不真放声，运行时冒烟留 M4）
2. `ZFrame/View/Audio/`（View 程序集；AudioMixer 引用需 UnityEngine.Audio——View 允许）：
   - **纯 C# 核**（无 UnityEngine，可测）：`AudioMath`（音量 0..1→dB 曲线：-80 静音/0 满，对数映射+0 以下 clamp，纯函数）；`CrossfadeState`（两轨权重：Start(target, duration)→Tick(dt) 返回 (a,b) 权重，等功率交叉 cos 曲线）；`SfxLimiter`（同帧同音去重：同 id 同帧只放一次；每帧上限计数）——三件零 UnityEngine 依赖
   - `AudioService`（MonoBehaviour 适配）：注册进 `Services`（IAudioService 接口：PlayBgm(id, fade=0.5)/StopBgm(fade)/PlaySfx(id, worldPos=null)/SetBusVolume(bus, v)/GetBusVolume）；内部 AudioSource 池（BGM 2 轨交叉、SFX 12 池经 PoolRegistry 或自建简单池——报告说明）、AudioMixer.SetFloat 实时生效；Tick 由 GameLoopDriver 驱动（View 档注册 ITickable——若 GameLoopDriver 无法引用 View，则 AudioService 自 AddComponent Update？**不行，违背统一驱动**——方案：AudioService 实 现 ITickable（ITickable 在 Core），经 Services.Get<IGameLoopTickQueue>() 注册——若 Core 无该服务，本任务在 Core 加 `ITickQueue` 接口+GameRoot 暴露（Core 已有 TickQueue 实例，加接口注册即可，小改 Core 允许，报告注明）
   - 资源键：bgm_/sfx_ 前缀约定；缺失资源 LogWarn 不抛
3. 单测：AudioMath 边界（0/1/负值 clamp）与单调性；CrossfadeState 周期正确/中点等权/完成态；SfxLimiter 去重与上限；IAudioService 注册后 Services 可 Get（EditMode 构造 AudioService GameObject，不真播）
4. 自测：EditMode 全绿 + ValidateFromCommandLine exit 0（Unity 纪律）

**约束**：可动 `UnityProject/Assets/ZFrame/View/`、`UnityProject/Assets/ZFrame/Core/`（仅加 ITickQueue 注册小改）、`UnityProject/Assets/Audio/`、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：四总线服务+纯 C# 三件+Mixer 资产+单测+施工报告四节（含驱动链说明）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | 完成时间: 2026-10-24 | 未 git commit

### 1. 交付清单

| 文件 | 说明 |
|---|---|
| `Assets/ZFrame/View/Audio/AudioMath.cs` | 纯 C# ①：0..1→dB 对数映射（-80 静音/0 满，两端 clamp，单调），`LinearToDb`/`DbToLinear` 互逆 |
| `Assets/ZFrame/View/Audio/CrossfadeState.cs` | 纯 C# ②：双轨等功率交叉淡化 `Start(target, duration)`→`Tick(dt)` 返回 (a,b) 权重，cos/sin 曲线（中点等权 ≈0.707，a²+b²≡1） |
| `Assets/ZFrame/View/Audio/SfxLimiter.cs` | 纯 C# ③：`BeginFrame()`+`TryPlay(id)` 同帧同 id 去重 + 每帧预算上限（默认 4） |
| `Assets/ZFrame/View/Audio/AudioBus.cs` | 四总线枚举 Master/BGM/SFX/UI |
| `Assets/ZFrame/View/Audio/IAudioService.cs` | 服务接口：PlayBgm(id, fade=0.5)/StopBgm(fade)/PlaySfx(id, worldPos=null)/SetBusVolume/GetBusVolume |
| `Assets/ZFrame/View/Audio/AudioService.cs` | MonoBehaviour 适配：IAudioService + ITickable；BGM 双轨交叉、SFX 12 池、AudioMixer.SetFloat 实时生效 |
| `Assets/ZFrame/View/Editor/AudioMixerSetup.cs` | Mixer 资产生成器（幂等，批处理入口 `SetupFromCommandLine`） |
| `Assets/Audio/MIX_AudioMixer.asset` | 生成产物（已入库）：Master→{BGM,SFX,UI} 四 Group + 4 个暴露 Volume 参数 |
| `Assets/ZFrame/Core/GameLoop/ITickQueue.cs` | Core 小改①：新增接口（Register/Unregister/Count） |
| `Assets/ZFrame/Core/GameLoop/TickQueue.cs` | Core 小改②：TickQueue 实现 ITickQueue（L37，仅加接口声明，逻辑零改动） |
| `Assets/ZFrame/Core/GameRoot.cs` | Core 小改③：Bootstrap 注册 `Services.Register<ITickQueue>(queue)`（L128） |
| `Assets/ZFrame/Tests/Audio/ZFrame.View.Tests.asmdef` | 测试程序集（引用 Core+View，Editor-only，Tests 命名豁免架构矩阵） |
| `Assets/ZFrame/Tests/Audio/AudioMathTests.cs` (9) / `CrossfadeStateTests.cs` (10) / `SfxLimiterTests.cs` (6) / `AudioServiceTests.cs` (9) | 新增 34 用例 |

### 2. 纯 C# 三件：零 UnityEngine 证据

三件只有 doc 注释里出现 "UnityEngine" 字样，无任何 using/类型引用（grep 证据）：
- `AudioMath.cs`：全文件仅 `using System;`（L3），`System.Math.Log10/Pow` 实现映射
- `CrossfadeState.cs`：无 using，`System.Math.Sin/Cos`（L99-100）实现等功率曲线
- `SfxLimiter.cs`：仅 `using System.Collections.Generic;`（L2），`HashSet<string>` 去重

关键行号：AudioMath L17/L20（MinDb=-80/MaxDb=0）、L27（LinearToDb clamp+对数）；CrossfadeState L48（Start）、L72（Tick→权重）、L91（CurrentWeights）；SfxLimiter L20（MaxPerFrame）、L28（下限 1 clamp）。

### 3. 驱动链说明（统一驱动，非 Update 直驱）

```
GameLoopDriver.Update()                          (Core/GameLoop/GameLoopDriver.cs L39)
  └─ _queue.TickAll(TickStage.View, dt)          (L62 —— GameLoopDriver 唯一的引擎 Update)
       └─ TickQueue 快照迭代 → AudioService.Tick(dt)   (View/Audio/AudioService.cs L313)
            ├─ _limiter.BeginFrame()             (L315  —— 每帧 SFX 预算重置)
            └─ _crossfade.Tick(dt) → (a,b) 权重 → 双 BGM AudioSource.volume (L317-320)
```

注册链：`AudioService.Initialize()`（L139-156）→ `GameRoot.EnsureInstance()` 保证循环服务存在（EditMode 下 Awake 不跑时走 `GameRoot.Instance.Initialize()` 文档化接缝）→ `Services.Get<ITickQueue>().Register(this, TickStage.View)`（L155，接口在 Core L128 注册，AudioService 只依赖 ITickQueue 不依赖具体类）→ 同时 `Services.Register<IAudioService>(this)`（L154）。OnDestroy→`Shutdown()`（公共确定性接缝，仿 GameRoot.Initialize/Shutdown 先例；EditMode 测试直接调 Shutdown 驱动同一路径）。AudioService 全程未实现 Update/LateUpdate。

### 4. 关键设计决策

- **SFX 池选择自建简单池**（报告注明，简报允许二选一）：12 个 AudioSource 一次性创建于 Init，`AcquireSfxSource()` 优先取空闲源、全忙时按游标轮转"抢最老"（L371-389）。PoolRegistry/ComponentPool 面向"prefab 实例的 Spawn/Despawn 语义"，与"常驻 AudioSource 借还"不匹配，且需要 steal 策略——自建 20 行循环更短更清晰。
- **StopBgm 复用交叉淡化**：淡化目标是"无 clip 的另一轨"，无需额外"淡出到零"状态；Tick 中权值归零且非目标的轨道会被 Stop（L323-331），避免积累哑声源。
- **暴露参数 GUID 取 `group.GetGUIDForVolume()`**（组自身的 m_Volume，`PreallocateGUIDs()` 分配）——与编辑器里手动勾选 Volume 暴露时的 GUID 同源；资产 YAML 里 4 个参数 GUID 与各 Group 的 m_Volume 一一对应（可读 asset 文本核对）。
- **资源加载走 IResourceService**（key 前缀 bgm_/sfx_），provider 缺资产抛异常 → `TryLoadClip` catch 后 ZLog.Warn 返回 false，不向上抛（简报要求）；IResourceService 未注册时同样 Warn 不抛。
- **AudioMixer 为可选依赖**：资产按简报固定在 `Assets/Audio/`（非 Resources 目录），运行时 `Resources.Load` 找不到则 Info 提示并降级（总线音量缓存，`SetMixer(mixer)` 由场景接线/M4 调用时经 AudioMath 一次性回放）；EditMode 测试均以无 mixer 路径验证。

### 5. MIX_AudioMixer.asset 生成与幂等

`AudioMixerSetup`（View/Editor，#if UNITY_EDITOR，仿 SceneProjectSetup）：Unity 无公开 AudioMixer 授权 API，经反射调用内部 `UnityEditor.Audio.AudioMixerController/GroupController/SnapshotController/EffectController`（对照 UnityCsReference 2022.3 bindings）。序列化顺序关键：先 `CreateAsset` 主资产→每个 Group/Effect/Snapshot 先 `AddObjectToAsset` 再互连引用（否则引用落成 fileID:0 悬空——首轮踩坑并已加固 `Validate`：三子 Group 存在 + 4 个 GetFloat 可读 + Group 子资产存在）。幂等：已验证重跑 exit 0 且资产 mtime 不变（重跑走 "existing+Validate 通过→跳过" 分支）。

### 6. 自测证据（最终轮，2026-10-24 23:50）

- **EditMode 全绿**：`-runTests -testPlatform EditMode` → **203/203 passed, 0 failed**（results.xml：`TempLogs/audio_editmode_results.xml`；新增 34 用例：AudioMathTests 9 / CrossfadeStateTests 10 / SfxLimiterTests 6 / AudioServiceTests 9）。重点用例：dB 两端 clamp+单调性+互逆；交叉淡化中点等权（cos45°≈0.7071）+ 等功率 a²+b²≡1 + 周期/完成态/零时长 snap；同帧同 id 去重 + 帧预算 + BeginFrame 重置；ITickQueue 与 TickQueue 同实例（AreSame）；AudioService 注册进 Services + View 档；**TickQueue.TickAll(View) 驱动交叉淡化改变 BGM 轨音量**（真驱动链证据）；SetBusVolume/GetBusVolume 往返；缺资源 Play 不抛。
  - 注：本轮 results.xml 写完后测试进程退出挂起（Unity 测试运行器已知怪癖，非用例失败），按 HANDOFF 纪律轮询 xml 取证后 taskkill；exit code 以 results.xml 为准。
- **ValidateFromCommandLine exit 0**：`TempLogs/arch_report.txt`（@ 23:50:19）→ 违规数 0，PASS（asmdef 16 个，Tests 豁免；新增测试程序集不触碰期望矩阵）。
- **Mixer 生成 exit 0** + 重跑幂等 exit 0（资产 mtime 不变，未重建）。
- 跑测纪律：单实例检查 + 清 UnityLockfile + results.xml 为准；全程未 -quit 跑测试。

### 7. 偏差与注意事项（供主控/验收）

1. **IAudioService/AudioBus 放在 ZFrame.View**（简报未指定程序集）：接口含 `Vector3?` 参数，放 View 最自然；Gameplay（只引 Core）暂不可见，M4 接线时若 Gameplay 需要可在 Core 加无依赖契约层——届时小改。
2. **PlayBgm/PlaySfx 返回 void**（按简报签名）；成败通过 ZLog.Warn 反馈。
3. **AudioService.Shutdown() 是新增公共方法**：OnDestroy 在 EditMode DestroyImmediate 下不可靠（实测未回调），故提取确定性接缝，与 GameRoot.Initialize/Shutdown 同构；OnDestroy 在 play mode 调它。
4. **运行时 mixer 自动发现**仅尝试 `Resources.Load("MIX_AudioMixer")`（当前不在 Resources 下，预期降级）；场景接线（建议 M4/Bootstrap）调 `SetMixer` 即可，AudioMixerSetup 的 `Validate` 已保证资产可被 GetFloat 驱动。
5. 测试音频资产未建（简报允许，EditMode 不真放声）；运行时冒烟留 M4。

## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 三件纯 C# 零 UnityEngine（行号证据）；AudioMath 曲线/交叉淡中点等权/SfxLimiter 去重用例核对
3. ITickable 驱动链：GameLoopDriver Tick → AudioService（代码行号，非 Update 直驱）
4. AudioMixer 四 Group 与暴露参数核对（读 asset 文本或 Setup 脚本）
5. ValidateFromCommandLine exit 0（自跑）
6. 文件规范抽查+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 | 时间: 2026-10-24 | 结论: **PASS（6/6）**

| # | 项 | 结论 | 证据（一行） |
|---|---|---|---|
| 1 | 自跑 EditMode 测试 | PASS | 独立重跑（清锁/单实例/不带 -quit）：`TempLogs/acceptance_m2_02_editmode.xml` total=203 passed=203 failed=0（进程写完 xml 后挂起，按纪律轮询取证后 taskkill，与施工报告所述怪癖一致） |
| 2 | 三件纯 C# 零 UnityEngine + 用例核对 | PASS | grep 行号证据：AudioMath.cs 仅 L1 `using System;`；CrossfadeState.cs 零 using；SfxLimiter.cs 仅 L1 `using System.Collections.Generic;`——三件除 doc 注释外无任何 UnityEngine 字样。用例核对：AudioMath 边界（`LinearToDb_NegativeClampsToSilence`/`AboveOneClampsToFull`）+单调（`LinearToDb_IsMonotonicOverUnitRange` 100 点采样）；Crossfade 中点等权（`Midpoint_WeightsAreEqual_TargetA/B` 断言 cos45°≈0.7071）+等功率（`EqualPower_SumOfSquaresIsOneThroughout`）；SfxLimiter 去重（`SameIdSameFrame_PlayedOnce`）+预算+`BeginFrame_ResetsDedupAndBudget` |
| 3 | 驱动链审查 | PASS | GameLoopDriver.Update（Core/GameLoop/GameLoopDriver.cs L39）→ `_queue.TickAll(TickStage.View, dt)`（L62）→ AudioService 经 `Services.Get<ITickQueue>().Register(this, TickStage.View)`（View/Audio/AudioService.cs L155）进入快照迭代 → `AudioService.Tick(dt)`（L313，`_limiter.BeginFrame()` L315、权重写回双 BGM 轨 L317-319）；AudioService.cs 全文件无 Update/LateUpdate/FixedUpdate（grep 仅注释提及）；TickQueue 实现 ITickQueue（Core/GameLoop/TickQueue.cs L37），GameRoot.Bootstrap 注册 `Services.Register<ITickQueue>(queue)`（Core/GameRoot.cs L128） |
| 4 | AudioMixer 资产核对 | PASS | 读 `Assets/Audio/MIX_AudioMixer.asset` YAML：四 Group（Master L166 挂 3 个 m_Children → BGM L37/SFX L147/UI L56）；m_ExposedParameters（L84-91）4 个，名称 MasterVolume/BGMVolume/SFXVolume/UIVolume 与 AudioService.GetExposedParamName（L298-301）一致，且各参数 GUID 与对应 Group 的 m_Volume GUID 一一相同（Master ae88…/BGM 0043…/SFX 1e01…/UI a930…） |
| 5 | ValidateFromCommandLine exit 0 | PASS | 独立自跑 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`：`TempLogs/arch_report.txt` @23:55:16 违规数 0 结果 PASS；log L304 证实到达 `EditorApplication.Exit(ok?0:1)` 前最后一行（报告 PASS→ok=true→Exit(0)），其后进程挂起为已知怪癖按纪律 taskkill；Exit 代码语义经 ArchitectureValidator.cs L292-298 核实 |
| 6 | 文件规范 + 施工报告完整 | PASS | 抽查 14 个新增/改动文件（三件纯 C#、AudioService、AudioBus、IAudioService、ITickQueue、TickQueue、GameRoot、AudioMixerSetup、asmdef、4 测试文件、MIX_AudioMixer.asset）均 UTF-8 无 BOM；施工报告四节齐全（简报/施工报告/验收清单/本结论），含 Shutdown 接缝说明（§3+§7.3）与 mixer 接线降级说明（§4 末条+§7.4）；未发现 git commit |

**验收备注**：施工报告§7 五条偏差均核实属实且在简报授权范围内（IAudioService 放 View 合理、自建 SFX 简单池有说明、Shutdown 为文档化确定性接缝、mixer 非必选依赖降级路径清晰、测试音频省略符合简报）。验收过程未修改任何代码，未 git commit。
