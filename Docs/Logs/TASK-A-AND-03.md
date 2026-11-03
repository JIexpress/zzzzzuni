# TASK-A-AND-03 —— Android 黑屏启动挂起：文件日志诊断 + 真因修复

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 现场症状（已取证，勿重复采集）

- APK 安装启动成功（OXF-AN00 / HarmonyOS，PID 存活），但屏幕**均匀深灰**=无相机的 SCN_Boot 清屏色——**启动链卡死在菜单场景加载之前**
- 进程 16.6% CPU 持续空转（非死锁，有线程在忙）；无 ANR 无崩溃
- logcat **零 Unity 引擎输出**（华为 ROM 抑制第三方日志，ProjectMenu AP logs 已开仍无效）→ 当前盲飞

### 任务两阶段

**阶段一：设备文件日志（诊断仪器，长期资产）**
1. `ZFrame/View/Diagnostics/DeviceFileLogger.cs`（或并入 ZLog）：Development 构建（#if DEVELOPMENT_BUILD||UNITY_EDITOR）下，ZLog 全量输出同时追加写 `Application.persistentDataPath/zframe_boot.log`（含时间戳/级别/频道/消息；启动链每步前后打点——GameBootstrap 已有 "full boot starts"，把**每个服务初始化的前后与场景加载的每步**都打上步进标记）
2. GameBootstrap 每步 try/catch：任何初始化异常→写日志+屏幕显示错误文本（UGUI 一行字，别再黑屏盲等）+继续或中止（语义自定，报告说明）
3. 重出 APK（Mono 快速包即可诊断）→ `adb install -r` → 启动 30s → `adb shell run-as com.ZFrame.ZFrame cat files/..../zframe_boot.log`（或 `adb pull` persistentDataPath）→ **读到卡在哪一步**

**阶段二：真因修复**
- 根据日志定位挂点（候选：SceneLoader 异步在 Android 的行为、某服务 init 抛异常被吞、Shader.Find 自禁用逻辑、AudioMixer 资产在 Android 的加载），修复→重出包→**验证屏幕出现主菜单**（screencap `Docs/Parity/android_menu.png`）→ logcat/文件日志显示启动链走完
- 循环直到修好（允许 2-3 轮构建迭代，每轮报告记录）

### 约束

- 白名单：`Assets/ZFrame/View/**`、`Assets/ZFrame/Core/**`（ZLog 挂载点/Logger 扩展）、`Assets/Game/Bootstrap/**`、`Assets/Game/Editor/PlayerBuild.cs`（如需构建变体参数）、`Build/Android/`、`Docs/Parity/android_*.png`、本日志
- 文件日志仅 Development 构建（#if 包裹），发布零开销；**不得**在日志里写敏感信息
- Unity 批处理纪律；构建超时 45 分钟；不 git commit；UTF-8 无 BOM/LF
- 基线：native 281/771,162 不回归（本轮不动原生）；EditMode/PlayMode 全绿（只增）

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 | 2026-11-01 | 设备 OXF-AN00 (HarmonyOS/Android 12, Mali-G76, Vulkan) | 构建轮次 5（其中修复迭代 3）

### 一、结论（TL;DR）

**根因不是挂起，是"什么都没画"**——两层叠加缺陷：

1. **SCN_Boot 没有相机**：URP Player 在场景零相机时**零渲染零呈现**（连 ScreenSpaceOverlay 的 UGUI 也不画），屏幕永远停留在启动期的旧缓冲（均匀深灰）。文件日志证明启动链其实 **3.5 秒内全部跑完、主菜单已入栈**——16.6% CPU 是 vsync 空转 + GfxDevice 线程，不是死循环。"启动链卡死在菜单场景加载之前"的现场判断被证伪。
2. **UIRoot 层画布被 Unity 静默转成 WorldSpace**：`BuildLayersIfMissing` 先 `AddComponent<Canvas>`（默认 Overlay）再 `SetParent` 挂到 UIRoot host——**Unity 对"非根 transform 的 Overlay 画布"在重挂父级时静默转换为 WorldSpace**（设备实测 `mode=WorldSpace, size=100x100, pos=(0,0,0)`）。于是即便有了相机，菜单也画在世界原点一个 100x100 的画布上：不渲染、不命中。该缺陷自 M3-01 起就存在；编辑器/PC 侧从未暴露是因为菜单验证全走程序化路径（`ReplayPrimaryForTests`），从无像素断言。

**修复（两处，均在白名单内）**：
- `Game/Bootstrap/BootCamera.cs`（新）：运行时自建展示锚点相机（SolidColor 清屏 0.106 灰、depth=-100、幂等；有场景相机时不建；随 SCN_Boot 实例销毁）。Awake 主/重复分支 + OnSceneLoaded(boot) 三处调用。
- `ZFrame/View/UI/UIRoot.cs`：`BuildLayersIfMissing` 在 SetParent **之后**显式 `canvas.renderMode = ScreenSpaceOverlay`（一行修复+注释；层级结构不变，所有测试的父子断言不受影响）。

**修复后实测（同设备）**：主菜单上屏（标题+开始/设置/退出，"继续"正确隐藏——无存档）→ 点"开始" → SceneLoader 全链走完（STEP #34 `SCN_Game loaded`）→ 游戏场景完整渲染（HUD/FSM 敌人/拾取物/阴影/天空盒）。证据：`Docs/Parity/android_menu.png`、`Docs/Parity/android_game.png`、`TempLogs/zframe_boot_final.log`（51 行全量留档）。

### 二、阶段一：诊断仪器（长期资产）

| 文件 | 内容 |
|---|---|
| `ZFrame/View/Diagnostics/DeviceFileLogger.cs`（新） | 全量镜像到 `persistentDataPath/zframe_boot.log`：挂 `ZLog.OnLog`（**过滤前** firehose，通道白名单关闭也全收）+ `Application.logMessageReceived`（**引擎全部输出**——实测绕开华为 logcat 抑制）+ `AppDomain.UnhandledException`。`SubsystemRegistration`（最早托管钩子）尝试挂载 + **每次写入懒重试**（一轮教训：过早钩子里 persistentDataPath 尚不可用会静默失能）；persistentDataPath 失败自动回退 temporaryCachePath；`LastError` 暴露给屏上显示；递归防护 + IO 失败自解除 + 每次进程启动截断（一文件=一次运行）。**`#if DEVELOPMENT_BUILD || UNITY_EDITOR`** 包裹全部实现，发布构建为空方法桩（JIT 折叠，零 IO 零分配）。 |
| `Game/Bootstrap/BootErrorOverlay.cs`（新） | 运行时自建 UGUI 顶屏文本：`Show()`（异常上屏，BOOT ERROR #N 前缀）+ `Info()`（dev 状态行）。LegacyRuntime.ttf（Arial 兜底）。空载成本：首次 Show/Info 才建宿主，干净启动零对象。 |
| `Game/Bootstrap/GameBootstrap.cs` | `RunStep()` 包装每个启动步（异常→文件+屏上+**继续下一步**——语义选择：单步失败不掩盖后续步，各步独立报错，见简报"语义自定"授权）；`PushMainMenuAsync` 全程打点（slot 检查/push 完成/异常）；EnterGame/OnSceneLoaded 打点；dev 专用触摸探针（`Update` 节流 1s，证明"触摸是否到达引擎"）；dev 专用屏上状态行 + 菜单实时状态 dump（active/canvas/alpha/rect）。 |
| `ZFrame/View/Scene/SceneLoader.cs` | BeginLoad 前后、before-unload 后、op 获得、0.9 闸门、allowSceneActivation、FinishLoad 六处打点 + 0.2 步进 raw progress 标记（仅转移时刻，非逐帧）。 |
| `Game/Editor/PlayerBuild.cs` | 新增 `BuildAndroid64MonoDev`（Mono + `BuildOptions.Development` → `ZFrame_MonoDev.apk`）：让 `DEVELOPMENT_BUILD` 宏在诊断包生效；正式 IL2CPP 包不带 Development，日志代码不编译。 |

**取数通道修正**：本机 ProjectSettings 为 External 写权限 → `persistentDataPath = /storage/emulated/0/Android/data/com.ZFrame.ZFrame/files`（简报给的 `/data/data/...` 内部沙箱不对；`run-as` 只看到内部）。读取用 `adb shell cat /sdcard/Android/data/com.ZFrame.ZFrame/files/zframe_boot.log`。

### 三、每轮构建记录（5 轮 Unity 构建；修复迭代 3 轮 ≤ 授权 3）

| 轮 | 时间 | 包 | 变更 | 设备表现 / 判定 |
|---|---|---|---|---|
| 1 | 17:13 | MonoDev 50,769,252 B (v0.1.0.15) | 仪器全量（logger+打点+overlay+MonoDev 入口） | 灰屏。`files/` 无日志——**读错路径**（内部沙箱），险些误判"托管未运行" |
| 2 | 17:25 | MonoDev 50,769,932 B | logger 自愈（懒重试+双路径+LastError） | 灰屏；在外部路径找到日志：**启动链 3.5s 走完、菜单入栈** → 推翻"挂起"假设，锁定"渲染/呈现"层 |
| 3 | 17:44 | MonoDev 50,771,086 B | **修复 A：BootCamera** + 屏上状态行 | 屏幕开始出帧（状态行可见=UGUI 渲染链路通）但菜单仍不可见 → 排除"UGUI 全局坏"，聚焦"菜单自身" |
| 4 | 17:54 | MonoDev 50,771,758 B | 屏上菜单实时 dump | **决定性证据**：`canvas=[Layer:Panel] mode=WorldSpace size=(100,100) pos=(0,0,0)` → 定位 UIRoot 重挂父转换缺陷 |
| 5 | 18:03 | MonoDev 50,771,774 B (v0.1.0.18) | **修复 B：UIRoot Overlay 重申** | **主菜单上屏**；点开始 → SCN_Game 加载完成（STEP #34）、游戏完整渲染 |

（1-2 轮为"仪器包"本体的编译期修正与取证通道修正；3-5 轮即授权的三轮修复迭代。）

### 四、真因与修复细节

**真因 1（呈现缺失）**：`SCN_Boot.unity` 无相机（grep Camera=0）。URP 的 ScreenSpaceOverlay UI 挂在相机呈现管线之后——零相机时 URP 无渲染工作、无 Present，overlay UI 一并不画。编辑器 Game 视图对"无相机"容忍度不同 + 所有菜单验证走程序化点击，故 M3/M4/A-AND-02 均未暴露。
**修法**：运行时 `BootCamera.Ensure()`——展示锚点（culling 无内容、SolidColor 清屏），不是玩法相机；SCN_Game 有自己的 MainCamera，boot 相机随场景卸载自动销毁；`回主菜单` 重载 SCN_Boot 时由 OnSceneLoaded 再补建。

**真因 2（画布降级）**：`UIRoot.BuildLayersIfMissing` 对新建 layerHost `AddComponent<Canvas>` 后 `SetParent(transform, false)`。Unity 在重挂父级时把非根 Overlay 画布转为 WorldSpace（Unity 行为，非项目代码设置——全仓 grep `renderMode` 仅我的仪器代码）。设备 dump 实证。八层画布全部中招（HUD/暂停面板/设置页同样受影响——本修复对整机 UGUI 都是修复）。
**修法**：SetParent 与组件装配之后显式 `canvas.renderMode = ScreenSpaceOverlay`。保留原层级（UIRoot host 的子物体），不破坏既有测试的 `GetLayerCanvas(...).transform == panel.transform.parent` 断言。

**附带发现（不阻塞，已如实记录）**：
- `[Settings] [超分] 原生库不可用（DllNotFoundException）`——预期：zrender.dll 不进 Android 包，超分"仅存储不生效"降级路径工作正常。
- AudioService `MIX_AudioMixer not found via Resources`（既有行为，音量缓存待场景接线）。
- 右上角"设置"悬浮钮为 DebugPanel（dev 包常规）。
- ProjectSettings 现为 Mono/ARMv7 + bundleVersion 0.1.0.18（MonoDev 构建变体副作用，与 A-AND-02 MonoVerify 先例一致；下次 IL2CPP 正式包构建自动恢复 IL2CPP/ARM64）。

### 五、自测

- **EditMode 785/785 全绿 0 failed**（TempLogs/runtests_editmode.xml；Unity 僵尸 taskkill 由 run_tests 纪律处理，results.xml 为准）。基线只增不减。
- **PlayMode 20/20 全绿**——三次运行证据链，如实记录：
  1. 本轮代码首次跑：19/20，`DoorToggleEvents_BrightenTheFrame_AtTheDoorPosition_BothDirections` 像素证据 0.0004 < 门限 0.0040（一次性）；
  2. **基线对照**（`git stash -u` 后同机同法）：20/20 全绿；
  3. **本轮代码复跑**（同树不变）：20/20 全绿。
  → 判定为**环境瞬时抖动**（新程序集编译后首个 Play 会话的 GPU/着色器管线预热），非本轮改动引入的回归；两次同码运行一红一绿排除确定性缺陷。建议验收代理亲跑一次 PlayMode 复核。
- **三校验器 exit 0**：arch（TempLogs/arch_report.txt）/ config（config_report.txt）/ naming（name_report.txt）全 PASS。
- **native 零改动**：`git diff Native/` = 空（0 行）。
- 发布零开销论证：DeviceFileLogger/打点/overlay 调用点全部为空方法桩或 `#if` 包裹（`#else` 分支 stub，V3 可核 DeviceFileLogger.cs 行号）；正式 IL2CPP 包不带 `DEVELOPMENT_BUILD`，日志代码不编译。
- 文件规范：新/改文件 UTF-8 无 BOM / LF（od+file 实测）；一类型一文件；公共 API XML 注释；`.meta` 由 Unity 自动生成、随资产一并入库。
- 未 git commit（铁律）；改动全部在白名单（`Assets/ZFrame/View/**`、`Assets/Game/Bootstrap/**`、`Assets/Game/Editor/PlayerBuild.cs`、`Build/Android/`、`Docs/Parity/android_*.png`、本日志）＋ `Tools/version.json`/`ProjectSettings.asset`（PlayerBuild 脚本写入的唯一授权路径，与 A-AND-02 先例一致）。
- Unity 批处理纪律全程遵守：单实例/删锁/Git Bash 启动/无 -quit/超时兜底（本轮未触发超时）。

### 门控收尾（V2-F1，2026-11-01，收尾施工子代理）

按验收结论 V2-F1 遗留项完成最小化门控（零行为外溢，改动仅 2 文件）：

1. **`Game/Bootstrap/GameBootstrap.cs:99-106`**：原 :95-100 的 `if (!DeviceFileLogger.IsArmed) BootErrorOverlay.Show("device file log NOT armed...")` 整体包进 `#if DEVELOPMENT_BUILD || UNITY_EDITOR` / `#endif`（与 `DeviceFileLogger.cs:50` 同一口径），并留注释说明门控原因。release 下该块整体编译剔除；其余 Show 调用点（:221 RunStep 失败 / :307、:321 PushMainMenuAsync 失败）仅在真实异常时触发，维持验收 V2 的"不算常显"判定，本轮未动。
2. **`ZFrame/View/Diagnostics/DeviceFileLogger.cs:329-336`**：release 桩 `IsArmed => false`（:336，#else 分支 :310-351）补 XML 注释说明语义安全性——对 `IsArmed == false` 有反应的唯一调用点（GameBootstrap 的 "NOT armed" overlay）已被同一 `#if` 门控编译剔除，故桩恒 false 在 release 中不可达任何用户可见错误；**行为零改动**（原验收点名的"语义别扭"由注释澄清，不改桩值）。
3. **回归**：`Tools/run_tests.ps1 -Segments editmode` → **785/785/0 全绿**（TempLogs/runtests_editmode.xml，Unity exit 0，run_tests exit 0）。基线 785 → 785，"只增不减"满足；零新增用例为本修法的设计属性（见下条）。
4. **变异自证（静态论证，按简报授权）**：grep 全仓 `BootErrorOverlay` 仅命中 GameBootstrap.cs / BootErrorOverlay.cs / DeviceFileLogger.cs（doc 注释）——**无任何 BootErrorOverlay 相关测试用例**，故走"静态论证+行号"分支：①门控态（现行）：release 构建（无 DEVELOPMENT_BUILD、无 UNITY_EDITOR）下 :99-106 编译剔除，干净启动不可能出 BOOT ERROR；②变异态（去掉 :99/:106 的 #if/#endif）：release 下 `DeviceFileLogger.IsArmed` 绑定 :336 桩 `=> false` → `!IsArmed` 恒真 → :102 Show 无条件执行 = 验收记录的原 V2-F1 缺陷复现；③EditMode 无法红证的结构原因：编辑器内 UNITY_EDITOR 恒定义，门控前后编译产物逐位相同——该门控的行为差异**只在 release player 构建体现**，测试工具链原理上不可见，这正是它逃过 785 绿与验收五项的原因。
5. 文件规范复核：两文件 UTF-8 无 BOM（`od` 首字节 `75 73 69`）+ LF（CRLF 计数 0）；预处理指令平衡（GameBootstrap 4 对 #if/#endif；DeviceFileLogger #if:50/#else:310/#endif:351）；未 git commit（铁律）。

## 验收清单（主控写）

- [x] V1 亲跑：安装诊断包→启动→拉取 zframe_boot.log（内容含步进标记与挂点）
- [x] V2 真因修复后：**屏幕出现主菜单**（screencap 亲看）+ 启动链日志走完
- [x] V3 文件日志仅 Development 构建生效（#if 行号）；发布路径零开销论证
- [x] V4 EditMode/PlayMode 全绿（数字写入结论）；native 零改动（git diff 核对）
- [x] V5 文件规范+施工报告四节完整（含根因与每轮构建记录）

## 验收结论（验收代理写）

验收: general-purpose 验收子代理 | 2026-11-01 | 设备 OXF-AN00 (8UR4C19C03009348) | 与施工不同代理，全程未改代码

**总体判定：PASS（附 1 项门控 FAIL 遗留，见 V2-F1——不阻塞本任务"黑屏修复"目标的达成，但正式 IL2CPP 包重建前必须门控）**

### 逐项证据（均为验收代理亲跑/亲读，未复用施工结论）

- **V1 PASS**：`adb devices` 在位（OXF-AN00）→ `adb install -r Build/Android/ZFrame_MonoDev.apk`（50,771,774 B / 18:03 = 第 5 轮产物，与设备 lastUpdateTime 18:03:37 一致）→ monkey 启动 → 32s 后拉取日志成功：STEP #01-#21 全链步进 + **#20 "PushMainMenuAsync COMPLETE — main menu on stack"（+3.433s）**；#16 BootCamera.Ensure done 证明修复路径在设备执行。取数通道：外部路径 `/sdcard/Android/data/com.ZFrame.ZFrame/files/zframe_boot.log`（External 写权限）；简报给的 `run-as cat` 实测内部沙箱无文件（`cat: files/zframe_boot.log: No such file or directory`）——施工报告的通道修正如实成立。
- **V2 PASS（渲染）+ 1 项 FAIL 遗留（门控）**：screencap 亲看（`TempLogs/acc_and03_menu.png`）主菜单上屏（标题+开始/设置/退出，"继续"正确隐藏 hasSave=False；红字 dump `mode=ScreenSpaceOverlay size=(1080,2400)`、`uiCanvases=8`、右下 Development Build 水印）；点开始后（`TempLogs/acc_and03_game.png`）游戏场景完整渲染（HUD 拾取计数/守卫状态 Patrol、天空盒、玩家/敌人/阴影/粒子），日志 STEP #22 触摸→#23 EnterGame→#24-#32 SceneLoader 六处打点→#33-#34 `SCN_Game loaded`。**V2-F1（FAIL 遗留）**：`GameBootstrap.cs:95-100` 的 `BootErrorOverlay.Show("device file log NOT armed...")` **不在 #if 内**，而 release 桩 `DeviceFileLogger.cs:327` 的 `IsArmed => false` 恒真 → **任何非 Development 正式包启动必现永久红字 "BOOT ERROR #1"**（发布路径零开销论证在 report 五节声称"overlay 调用点全部为空方法桩或 #if 包裹"，对 95-100 这一处不成立；现行 ZFrame.apk 构建于改动前 15:47 故未受累，**下次正式包构建前必须将 95-100 行纳入 `#if DEVELOPMENT_BUILD || UNITY_EDITOR`**）。dev 专用的菜单状态 dump 与触摸探针本身门控正确（`GameBootstrap.cs:152-160`、`:163-194`、`:236-255` 三处 #if 均实）；RunStep/PushMainMenuAsync 的 Show（:215/:301/:315）仅在真实异常时触发且为简报明文要求，不算常显。
- **V3 PASS**：`BootCamera.cs` 幂等双闸 L42-45（Instance 短路）+ L47-50（FindObjectOfType<Camera> 短路，SCN_Game 的 MainCamera 在位时不建）+ L52-56（SolidColor/depth=-100）；三挂点全在：`GameBootstrap.cs:78`（Awake 重复分支）、`:147`（Awake 主分支，RunStep 包裹）、`:474`（OnSceneLoaded SCN_Boot 分支）。`UIRoot.cs` SetParent(:115)→组件装配(:117-126)→**L136 `canvas.renderMode = RenderMode.ScreenSpaceOverlay` 重申**，位于 `foreach (UIConsts.AllLayers)` 循环内（`UIConsts.cs:59` LayerCount=8）→ **八层画布（Background/HUD/Panel/Popup/Toast/Loading/Debug/Top）全覆盖**；git diff 证实 UIRoot 仅追加注释+该行，层级结构与既有测试断言零扰动。文件日志门控：`DeviceFileLogger.cs:50` `#if DEVELOPMENT_BUILD || UNITY_EDITOR`，`:310-342` #else 全量空桩（Step/Exception/IsArmed/FilePath/LastError/StepCount/Shutdown）。
- **V4 PASS**：验收亲跑 `Tools/run_tests.ps1` 六段 **ALL PASS exit 0**（`TempLogs/acc_and03_runtests.log` + `TestReport.txt`）：native **281/771,162/0**（ctest 100%）+ EditMode **785/785/0**（runtests_editmode.xml，Unity exit 0）+ PlayMode **20/20/0**（runtests_playmode.xml）+ arch/config/naming 三校验器 exit 0。PlayMode 抖动复核：施工三次证据链（19/20→stash 基线 20/20→同树复跑 20/20）+ **验收独立第 4 次 20/20** → 同码三连绿，"环境瞬时抖动"判定成立。**native 零改动：`git diff Native/` = 0 行**。澄清：本任务**零新增用例**——785/20 即 A-AND-02 交接基线（TASK-A-AND-02.md 验收行 total=785）；简报约束仅"全绿（只增）"，满足；主控提示词"785+新增/20+新增"按"含新增后的总数"解读吻合，特此记明。
- **V5 PASS**：日志四节齐（简报/施工报告/验收清单/本节）；施工报告含根因双缺陷、5 轮构建表（17:13→18:03，版本 0.1.0.15→0.1.0.18 与 Tools/version.json build 13→18 一致）、PlayMode 19/20 像素受限的三轮处置如实记录。文件规范亲测：新/改 9 文件全部 UTF-8 无 BOM / LF（`file`+`od`+`git ls-files --eol`=w/lf 三重验证）；.meta 齐（BootCamera/BootErrorOverlay/DeviceFileLogger + Diagnostics.meta）；改动全部落在白名单+报告声明的 version.json/ProjectSettings（Mono/ARMv7 副作用已披露，与 A-AND-02 MonoVerify 先例一致）。

### 验收附带记录（不阻塞）

1. **V2-F1（唯一 FAIL 项，要求后续门控）**：GameBootstrap.cs:95-100 需 `#if DEVELOPMENT_BUILD || UNITY_EDITOR` 包裹（或 DeviceFileLogger 加 dev-only 的 `IsDevLogExpected` 语义），否则下次正式包启动画面常显红字。建议并入下一个 Android 任务或返工小刀。
2. 报告机制描述笔误：四节称"先 `AddComponent<Canvas>`（默认 Overlay）再 `SetParent`"，HEAD/当前代码实为 **SetParent(:115) 在前、AddComponent(:117) 在后**——Unity"非根画布静默转 WorldSpace"的 quirk 本身由设备前后 dump（WorldSpace 100x100 → Overlay 1080x2400）决定性证明，修复有效性不受此笔误影响。
3. 设备日志附带项与报告一致：超分 DllNotFoundException 降级（预期，zrender.dll 不进 Android 包）、AudioMixer 资源提示（既有）。

**未 git commit（铁律遵守）；验收过程仅改本日志文件。**
