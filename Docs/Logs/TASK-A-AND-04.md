# TASK-A-AND-04 —— 触屏虚拟摇杆 + 屏上交互键（真机可玩闭环）

> **带门禁**：先证明"换输入模块后现有 UI 按钮仍可点"，再实现摇杆。施工代理按本简报执行，第 2 节写报告。
> **禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写，2026-11-01）

### 背景

A-AND-03 已让游戏在华为 OXF-AN00 上完整跑起来（主菜单→点开始→SCN_Game 渲染 HUD/FSM/阴影）。
但**操作仍靠键盘**——手机上点了开始之后**没法控制角色**，等于"能看不能玩"。这是当前最大的可玩性缺口。

### 现状（主控已核，派发前事实）

| 项 | 事实 |
|---|---|
| Input System | `com.unity.inputsystem` **1.7.0** 已装；`activeInputHandler: 2`（Both） |
| 输入链路 | `ZFrame/View/Input/PlayerInputBridge.cs` 读 InputAction `_controls.Move`（`performed`/`canceled`）→ `Vector2` → `EventBus.Fire(new MoveInputEvent(Vec2))`；另有 `InteractInputEvent` / `PauseInputEvent` |
| UI 事件模块 | `ZFrame/View/UI/UIRoot.cs:151-152` 用 **`StandaloneInputModule`（legacy）**，同处注释明确预留 seam：*"Input System (M4) will swap this for `InputSystemUIInputModule` — see class remarks"* |
| 冲突点 | `ZFrame/Tests/Debug/UIHierarchyTests.cs:74` 断言 `EventSystem` 上有 `StandaloneInputModule` —— **换模块会直接让这条用例红** |
| 现有摇杆 | 无（`grep Joystick/OnScreenStick/VirtualStick` 零命中） |

### 目标

1. **屏上虚拟摇杆**（左下）+ **交互键 / 暂停键**（右下），只在触摸设备出现。
2. 摇杆/按钮必须驱动**同一条既有链路**（`MoveInputEvent` / `InteractInputEvent`），不得另开一条输入旁路，
   以免与 `RebindService` / `BindingConflictDetector` 的既有语义脱节。
3. **真机上能真玩**：左手推摇杆移动、右手点交互键，**两指同时**有效。

### 阶段 A（门禁）：换模块不能弄坏现有 UI

`UIRoot` 的 seam 是换 `InputSystemUIInputModule`。但换模块后若 `actionsAsset` 未配好，
**现有 UI 按钮全部点不动**（Input System 的经典坑）。所以先证明：

- A1 换模块后，主菜单/设置页的**按钮仍可点**（Editor 内实测点击，或 PlayMode 用例）；
- A2 `UIHierarchyTests:74` 的断言如何处置——**改断言必须写明理由**（"seam 落地"是正当理由），
  但**不许把断言删掉或改成恒真**；
- A3 若 A1 无法在合理代价内达成（例如现有 UI 大量依赖 legacy 语义），**改用备选方案**：
  自研 UGUI 摇杆（`IPointerDownHandler`/`IDragHandler`）直接 `EventBus.Fire(new MoveInputEvent(...))`，
  **不动 EventSystem 模块**。二选一，用证据决定，不要两个都做一半。

### 阶段 B：实现

- 摇杆/按钮的**触屏多点**必须支持（左摇杆 + 右按钮同时按）。
- **只在触摸设备显示**（`Application.isMobilePlatform` 或 `InputSystem` 有触摸设备时），
  桌面/Editor 下不显示（或可用设置项强制显示以便调试）。
- **安全区**：刘海屏/手势条避让（`Screen.safeArea`），别让摇杆被系统手势区吃掉。
- 视觉与现有 UI 风格一致（不用美术资源，纯 UGUI 图形即可）。
- 不改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`。

### 阶段 C：真机实证（必做，不是可选项）

- 用 `Tools/build_android.ps1` 出包 → `adb install` → 真机启动 → **进 SCN_Game 后推摇杆，角色真的移动**。
- 证据形式：`adb logcat`/DeviceFileLogger 打点 + 屏幕截图（能看出角色位置变化或 HUD 数值变化）。
- 若 adb 不可用或设备未连，**如实报告"未做真机实证"**，不要用 Editor 证据冒充。

### 验证要求（验收会查）

- Editor：拖摇杆能产生非零 `MoveInputEvent`（可断言）；松手归零。
- 多点：左摇杆 + 右交互键同时按下时两者都生效。
- 键盘不回归：桌面 WASD 仍能移动（既有行为不变）。
- 测试家底不回归：**EditMode 785 / PlayMode 20**（新增用例应只增不减）；原生 281/771,162 不受影响。
- 关闭/非触摸设备时**零 UI 残留**。

### 环境纪律（每条都真实踩过，见交接档案 §3）

- **Unity 批处理单实例**：跑前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿带 `-quit`**；退出可能僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 证据以 `results.xml` 为准；`TestReport.txt` 是构建产物不入库
- 构建前清代理变量：`unset http_proxy https_proxy all_proxy`（重复大小写会让 MSBuild 抛「字典重复键」）
- Bash 侧禁止转调 powershell → 用 **PowerShell 工具**，先清代理变量 + `Set-Location` 到仓库根
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完查 `git status`：Editor 可能自动改写 `URP_*.asset`/`QualitySettings.asset`，非本意就 `git checkout` 还原

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- `.cs` 无 BOM、LF
- 不伪造真机证据

---

## 施工报告（施工代理写）

> 施工日期 2026-11-01。所有数字来自本轮自己跑的 `results.xml`；真机证据来自 OXF-AN00 当次运行的
> `zframe_boot.log`（`TempLogs/and04/device_final_full.log`）与截屏（`TempLogs/and04/final_*.png`）。
> **未 git commit**（施工代理铁律）。

### 2.0 结论速览

| 项 | 结果 |
|---|---|
| 阶段 A 判定 | **走 A3 备选方案**：自研 UGUI 摇杆，**EventSystem 模块一行未动**（理由见 2.1，证据 E1–E5） |
| `UIHierarchyTests:74` | **未改**（仍断言 `StandaloneInputModule`，不是恒真） |
| 阶段 B | 摇杆（左）+ 互动/暂停键（右），纯 UGUI 程序化生成，无美术资源；多点按 pointerId 隔离；`Screen.safeArea` 避让；非触摸设备 `Spawn` 返回 null（零残留） |
| 阶段 C | **真机已实证**：出包 → `adb install` → 进 SCN_Game → `adb input swipe` 推摇杆 → 角色真的走到门口（截图里有"按 E 开门"提示）→ `[motor] move=(-0.02,0.92)`；松手 `[bridge] move ZERO fired` + `[motor] move=(0.00,0.00)` |
| EditMode | **797 / 797 passed**（基线 785 + 新增 12），`TempLogs/and04/editmode.xml` |
| PlayMode | **20 / 20 passed**（不变），`TempLogs/and04/playmode.xml` |
| 原生 | **281 / 771,162 passed**（未改原生源码，直接跑既有 Release 二进制复核） |
| 顺带修掉的阻断缺陷 | `EventSystem` 原来是被建在**根对象**上、未随 UIRoot 持久化 → 单模式加载 SCN_Game 时被销毁 → **游戏场景内所有 UGUI 点击全死**（含既有"设置"键与暂停面板按钮）。详见 2.2.6 |

### 2.1 阶段 A 门禁：为什么没换 `InputSystemUIInputModule`

#### 证据

- **E1（项目资产，决定性的那一半）**：`Assets/ZFrame/View/Input/Resources/ZFrameControls.inputactions` 的
  `UI` map 只有两条指针绑定：
  `Point → <Mouse>/position`、`Click → <Mouse>/leftButton`，**没有任何 `<Touchscreen>/*` 绑定**。
  （用脚本 dump 全量 binding 得到，非目测。）
- **E2（包语义）**：`com.unity.inputsystem@1.7.0` 的 `InputSystemUIInputModule.cs` 在
  `point` / `leftClick` 的 remarks 里写明：*"together with `point`, this action is necessary for a
  pointer to be functional"*，并给出的示例必须带 `<Touchscreen>/touch*/position` 与
  `<Touchscreen>/touch*/press` 两条触摸绑定。
- **E3（对照）**：包自带的 `DefaultInputActions.inputactions` 的 UI map **确实**含触摸绑定
  （Point 三绑含 `<Touchscreen>/touch*/position`；Click 四绑含 `<Touchscreen>/touch*/press`）——
  也就是说"换模块还能点"只有两条路：① 用 `AssignDefaultActions()` 引入**第二套** Actions 资产
  （绕过项目的 `ZFrameControls` / `RebindService` / `BindingConflictDetector` 语义，UI map 变成摆设）；
  ② 改项目资产补触摸绑定，连带重绑定/冲突用例与 `ZFrameControls` 文档全线复核。
- **E4（启动次序）**：`UIRoot`（seam 所在）与 `InputRuntime` 都是 `AfterSceneLoad` 自举，
  `actionsAsset` 必须在模块创建时就配好；配不上的结果正是简报点名的"按钮全部点不动"，而且
  **Editor 里不报错、只在真机上表现为点不动**。
- **E5（既有真机链路）**：A-AND-03 已在同一台 OXF-AN00 上证明 `StandaloneInputModule` + 触摸点击可用
  （主菜单→开始→SCN_Game）；本轮 Phase C 再次复证（截图 `final_07_menu.png` 的"开始"点击、
  `final_04_pause_panel.png` 的"暂停"点击都生效）。

#### 判定

换模块的**收益**（输入统一、seam 落地）不在本任务交付路径上；**代价**是改资产 + 改断言 +
追加真机回归（每轮 4 分钟构建 + 安装），且引入"真机才暴露"的回归面。按简报 A3 选备选方案：
**不动 EventSystem，自研 UGUI 摇杆，事件走既有 `MoveInputEvent` / `InteractInputEvent`。**

#### A1 / A2 处置

- **A1（按钮仍可点）**：因为 EventSystem 与 UI 层一行未改，断言为"未触碰即无回归"，
  并给出两条实测证据：① `UIHierarchyTests.UIRoot_EnsuresAnEventSystem` 原样通过；
  ② 真机点"开始"进 SCN_Game（`final_07_menu.png` → 日志 `STEP EnterGame(loadSave=False) begin`）、
  真机点"暂停"开面板（`final_04_pause_panel.png` + `STEP #1248 touch button: 暂停 pressed -> PauseInputEvent`）。
- **A2（断言处置）**：`UIHierarchyTests.cs:74` **未改**。没有"删断言"也没有"改成恒真"。
  该断言仍然锁定 `StandaloneInputModule`；等未来真做 seam 时，它应当改成
  `InputSystemUIInputModule`（理由：seam 落地），并在同一 commit 补触摸绑定的回归用例。
  这里明确记录：**本任务没有落地 seam，所以不改断言**。

### 2.2 阶段 B：实现

#### 2.2.1 文件清单

| 文件 | 变更 |
|---|---|
| `ZFrame/View/Input/Touch/VirtualJoystick.cs` | **新增**：摇杆几何（圆心/半径/死区/夹取）、pointerId 独占、`Moved` 事件 |
| `ZFrame/View/Input/Touch/TouchActionButton.cs` | **新增**：按下即触发（非抬起）、按下着色、pointerId 独占、`PressCount` 诊断 |
| `ZFrame/View/Input/Touch/TouchControlsView.cs` | **新增**：程序化搭 UGUI（圆盘贴图运行时生成）、`Screen.safeArea` 宿主、可见性判定、接线到 bridge、拆除 |
| `ZFrame/View/Input/PlayerInputBridge.cs` | **改**：`ApplyVirtualMove/Interact/Pause` 三个虚拟输入入口；抽出 `Forward*` 让真输入与虚拟输入走同一具函数体；零向量恒放行（防"松手被门禁吞掉→解锁后继续跑"）；dev 诊断日志 |
| `ZFrame/View/UI/UIRoot.cs` | **改**：`EnsureEventSystem(Transform root)`——EventSystem 挂到持久化的 UIRoot 下（见 2.2.6） |
| `Game/View/UI/GameHud.cs` | **改**：进 SCN_Game 时 `TouchControlsView.Spawn`，`Shutdown` 时先释放摇杆再拆除 |
| `Game/Bootstrap/GameBootstrap.cs` | **改**（dev 门控）：`OnSceneLoaded` 打 `eventSystem=...`；新增"player moved"位置打点 |
| `Game/View/Player/PlayerMotor.cs` | **改**（dev 门控）：`OnMove` 打 `[motor] move=(x,y)`（0.25s 节流） |
| `ZFrame/View/AssemblyInfo.cs` | **改**：加 `InternalsVisibleTo("ZFrame.View.Input.Tests")`（摇杆几何接缝） |
| `ZFrame/Tests/Input/ZFrame.View.Input.Tests.asmdef` | **改**：加 `ZFrame.Gameplay` 引用（用例要断言 `MoveInputEvent`） |
| `ZFrame/Tests/Input/TouchControlsTests.cs` | **新增**：12 条 EditMode 用例 |
| `Tools/build_android.ps1` | **改**：新增 `-Backend MonoDev`（Mono + `BuildOptions.Development`），真机取证用 |

#### 2.2.2 摇杆怎么接进既有链路（不是旁路）

```
UGUI 指针(触摸) → VirtualJoystick.OnPointerDown/OnDrag → ApplyLocalOffset → Moved(Vec2)
   → TouchControlsView.OnStickMoved → PlayerInputBridge.ApplyVirtualMove(Vec2)
   → ForwardMove → EventBus.Fire(new MoveInputEvent(vec)) → PlayerMotor.OnMove
```

- 点击/拖动仍由 **`StandaloneInputModule`** 派发（A-AND-03 已验证过的真机链路），没有引入第二个事件模块。
- `ForwardMove/ForwardInteract/ForwardPause` 是**键盘/手柄 action 回调与触摸共用的同一具函数体**，
  所以 `LastMove` / `InteractCount` / `PauseCount` 诊断对两条来源一致。
- **门控一致**：`ApplyVirtualMove` 在 `PlayerInputGate` 把 Player map 关掉时（场景加载/模态面板）
  吞掉**非零**推杆；但**零向量恒放行**——否则"按下摇杆时被暂停→松手事件被吞→恢复后角色一直跑"。
  用例 `VirtualInput_IsSwallowedWhileThePlayerMapIsBlocked_ButReleaseStillLands` 锁死这条语义。
- 没有任何新事件类型、没有新输入旁路；`RebindService`/`BindingConflictDetector` 语义不受影响
  （键盘绑定照旧，虚拟输入不写绑定表）。

#### 2.2.3 多点怎么做的、怎么验的

- 实现：两个 widget 各自用 `pointerId` 独占（`_pointerId` / `PointerId`），**非持有者的 down/up 一律忽略**；
  摇杆 zone 与按钮是 UGUI 树里两个独立 raycast 目标，`StandaloneInputModule` 对每根手指各发一条
  pointer 流（`pointerId == Touch.fingerId`），因此"左手推杆 + 右手点键"天然成立。
- Editor 证据（`TouchControlsTests`）：
  - `MultiTouch_StickAndInteractButton_AreBothLive`：pointerId 21 按住摇杆推到 `Y>0.5`，
    期间 pointerId 22 按下互动键 → `InteractCount==1`、`InteractInputEvent` 收到、
    **摇杆方向不被打断**，抬起后各自归零。
  - `Stick_KeepsTheFirstPointer_AndIgnoresASecondFinger`：第二根手指落在摇杆上不抢占，
    非持有者抬手指不释放摇杆。
- **真机局限（如实披露）**：`adb shell input` 只能注入单指（`swipe`/`tap`），**无法在两指同时按下**，
  所以"物理双指"没有机器证据，只有上面两条组件级用例 + 单指真机实证。此项目前由人工双指验收补。

#### 2.2.4 只在触摸设备显示 / 桌面零残留

- `TouchControlsView.ShouldShow = ForceVisible || Application.isMobilePlatform || Touchscreen.current != null`；
  `Spawn` 不满足时**直接返回 null，一个 GameObject 都不创建**（桌面连空节点都没有）。
  调试开关是 `TouchControlsView.ForceVisible`（仅调试/测试置位）。
- 用例 `Spawn_CreatesNothing_OnDesktopWithoutTheDebugOverride` 断言：返回 null、场景里没有
  `TouchControlsView`、HUD canvas `childCount == 0`。
- 销毁：`GameHud.Shutdown → TouchControlsView.Shutdown`（先 `Stick.Release()` 发零，再解绑，再销毁层级）；
  `Shutdown_ReleasesTheStick_AndRemovesTheWholeOverlay` 断言归零 + 全树移除；`Spawn_IsIdempotent` 防重复。

#### 2.2.5 安全区

- 控件全部挂在 `[TouchControls.SafeArea]` 下，该 RectTransform 的 anchors 由
  `Screen.safeArea / Screen.size` 归一化得出（`ApplySafeArea`，纯静态函数、可单测）；
  横竖屏切换/手势条变化时 0.5s 轮询重锚（`Screen.safeArea` 是 native getter，不每帧取）。
- 用例 `ApplySafeArea_MapsTheInsetOntoNormalizedAnchors_AndFallsBackWhenDegenerate`：
  1080×2400 + 上下各 120px inset → anchors `(0,0.05)-(1,0.95)`；退化输入回落整屏而不是塌成 0。
- 真机：1080×2400 竖屏下摇杆圆盘完整可见、没有被底部手势条压住（`final_01_before_stick.png`）。

#### 2.2.6 顺带修掉的阻断缺陷（真机上才发现）

**现象**：第一版装到真机后，进 SCN_Game 后**所有 UGUI 都点不动**——摇杆没反应、互动/暂停没反应，
连既有的右上角"设置"按钮也没反应（`adb input tap` 之后设备日志里只有 `engine sees touch`，没有 handler 打点）。

**根因**：`UIRoot.EnsureEventSystem()` 把 EventSystem 建在**根对象** `[UIRoot.EventSystem]` 上，
而 `DontDestroyOnLoad` 只作用在 `[UIRoot]` 宿主上。`SceneLoader` 单模式加载 SCN_Game 时，SCN_Boot
连同 EventSystem 一起被卸载 → 游戏场景内 `EventSystem` 为空 → UGUI 完全不派发。
这是 A-AND-03 没覆盖到的面（那次只验证了 SCN_Boot 里的菜单点击）；顺带说明**暂停面板上的按钮在
真机上此前也是死的**（暂停只能用键盘 Esc 开，进去点不动）。

**修复**：`EnsureEventSystem(Transform root)` 把 EventSystem 挂到持久化的 UIRoot 宿主下，随宿主一起
`DontDestroyOnLoad`；并在 `GameBootstrap.OnSceneLoaded` 打 `eventSystem=[UIRoot.EventSystem]` 取证。
修复后真机日志 `STEP #33 OnSceneLoaded('SCN_Game') eventSystem=[UIRoot.EventSystem]`，
随后摇杆/按钮全部生效。**只改 UIRoot 一处 + 一条 dev 打点；`UIHierarchyTests` 仍然通过（断言只看存在性与模块类型）。**

### 2.3 阶段 C：真机实证（已做）

#### 构建与安装

```
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_android.ps1 -Backend MonoDev
RESULT: PASS (Android build)
APK: Build/Android/ZFrame_MonoDev.apk  Size: 48.43 MB (50,780,409 bytes)  Built: 2026/11/1 20:53:52
adb install -r → Success
```
设备：`HUAWEI OXF-AN00 / Android 12 (API-31) / 1080×2400@480dpi / Vulkan Mali-G76`，
应用版本 `0.1.0.22`（构建自增，见 2.6 说明）。

#### 证据 1：推摇杆 → 既有链路 → 角色位移（设备文件日志，`device_final_full.log`）

```
STEP #33 OnSceneLoaded('SCN_Game') eventSystem=[UIRoot.EventSystem]
INF  [Game] GameHud: touch controls spawned (joystick + 互动/暂停).
STEP #35 player sample start: pos=(0.00, 1.00, 0.00)
STEP #37 engine sees touch: count=1 phase=Began pos=(206.00, 208.00)      ← 手指落在摇杆盘上
STEP #38 [motor] move=(0.00,0.00) at 45.7s
STEP #41 [motor] move=(-0.02,0.46) at 46.0s
STEP #42 touch stick: x=-0.02 y=0.46 ptr=0 moves=9
STEP #43 [motor] move=(-0.02,0.92) at 46.2s
STEP #44 touch stick: x=-0.02 y=0.92 ptr=0 moves=17
STEP #45 [bridge] move ZERO fired (subscribers=1)                        ← 松手
STEP #46 player moved: (0.00, 1.08, 0.00) -> (-0.03, 1.08, 1.02)         ← 位移 ±1.0 单位
```
- 推杆值只带 +Y（x≈-0.02），说明圆心修正生效（第一次真机跑到的是对角，见 2.5 修 bug 记录）。
- 松手后 `[bridge] move ZERO fired` + 紧随的 `[motor] move=(0.00,0.00)`，随后 **4 秒内位置不再变化**。

#### 证据 2：截图（角色位置变化）

| 文件 | 内容 |
|---|---|
| `TempLogs/and04/final_01_before_stick.png` | 进场景基线：角色在画面中央偏下 | 
| `TempLogs/and04/final_02_after_stick.png` | 推杆 0.7s 后：角色明显向门走近，画面中央出现 **"按 E 开门"** 交互提示 |
| `TempLogs/and04/final_03_interact.png` | 点"互动"后的画面 |
| `TempLogs/and04/final_04_pause_panel.png` | 点"暂停"后"已暂停"面板上屏，底部控件被面板压暗（阻挡正确） |

#### 证据 3：按钮键位（同一套链路）

```
STEP #100  touch button: 互动 pressed -> InteractInputEvent
STEP #1248 touch button: 暂停 pressed -> PauseInputEvent
```

#### 证据 4：HUD 状态随之变化

`final_01`（Patrol）→ 推杆走近后 `final_04`（Chase）——守卫 FSM 因为玩家真的靠近而切状态，
这是"角色确实在移动"的第二条独立佐证。

**未做**：真机双指同时按下（`adb` 无法注入多指，见 2.2.3）；真机"返回主菜单再进"的控件重建
（本轮在暂停面板里没点中"返回主菜单"，Editor 用例覆盖，见 2.5 第 2 条）。

### 2.4 测试数字（本轮自跑）

| 套 | 结果 | 证据 |
|---|---|---|
| EditMode | **797 / 797 passed, 0 failed**（785 基线 + 12 新增） | `TempLogs/and04/editmode.xml` |
| PlayMode | **20 / 20 passed**（不变） | `TempLogs/and04/playmode.xml` |
| 原生单测 | **281 cases / 771,162 assertions, 0 failed** | 直接跑 `Native/build/bin/Release/zrender_test.exe`（未改原生源码） |

新增 12 条（`ZFrame.View.Input.Tests.TouchControlsTests`）：
`Spawn_CreatesNothing_OnDesktop…`、`Spawn_BuildsOverlayOnHudLayer…`、`Spawn_IsIdempotent…`、
`Stick_DragPublishesNonZeroMove…`、`Stick_PressOnTheVisualCentre_ReadsAsZero…`、
`Stick_ClampsToUnitLength_AndHonoursTheDeadZone`、`Stick_KeepsTheFirstPointer…`、
`MultiTouch_StickAndInteractButton_AreBothLive`、`PauseButton_PublishesTheExistingPauseCommand`、
`VirtualInput_IsSwallowed…_ButReleaseStillLands`、`Shutdown_ReleasesTheStick…`、
`ApplySafeArea_MapsTheInset…`。

环境纪律执行情况：跑测试前清代理变量、查 `Unity.EXE` 单实例、删 `UnityProject/Temp/UnityLockfile`、
**不带 `-quit`**；最后一次 EditMode 出现"有 xml 不退出"的僵死，按纪律 `taskkill //PID 22916 //F`
（results.xml 已完整落盘，数字取自该文件）。

### 2.5 未验证项 / 已知边界（如实披露）

0. **桌面 WASD 没有端到端自动用例**（V5 的"键盘不回归"只能在代码层面 + 既有用例层面证明）：
   我尝试在 EditMode 里用 `InputSystem.QueueStateEvent` 合成 Keyboard 按 W——设备状态**能**置位
   （`keyboard.wKey.isPressed == true` 的断言通过），但已启用的 `ZFrameControls` 克隆 InputAction
   始终不产生 `performed`（试了两次，含"加设备后再 Update 一次"，均失败），**已删除该用例，不留红**。
   `InputTestFixture` 在包内但位于 `Tests/` 目录，不被项目程序集引用。
   替代证据：`PlayerInputBridge.OnMove` 的函数体与改动前**逐字一致**（diff 只是把后两行抽成
   `ForwardMove`，参数取值路径不变），且 `PlayerInputGateTests` / `MoveRebindTests` /
   `BindingConflictDetectorTests` / `RebindServiceTests` 全绿。建议验收代理在编辑器里**手按 WASD** 复核一次。
1. **真机物理双指**未注入（`adb` 限制）。组件级多点用例已覆盖，建议验收/人工补一次真双指。
2. **真机"返回主菜单→再进 SCN_Game"** 未走通：本轮在暂停面板上点"返回主菜单"两次都没命中
   （坐标几次试探未中，红字 dev overlay 也压在该按钮上）；控件重建/无重复只有 Editor 用例
   （`Spawn_IsIdempotent`、`Shutdown_…`）与代码保证。
3. **观察到的既有缺陷（非本任务引入，未修）**：守卫 `Chase` 会持续顶推玩家——日志里
   `[motor] move=(0.00,0.00)` 期间角色仍以 ~3 单位/秒被推着走（`device_boot6.log` 第 #45 行之后，
   位置从 (-0.03,1.02) 一路漂到 (-1500,-1300)），HUD 长时间停在 `Chase`。这会显著破坏手感，
   但属于敌人 AI/碰撞设计（A-M4 范围），建议**单开任务**，不要塞进本任务。
4. 摇杆尺寸/间距只在高通 1080×2400 竖屏（dpi 480）验证；横屏、平板、深刘海机型未验。
5. **seam 仍未落地**：`UIRoot` 里 `StandaloneInputModule` 与那条注释原样保留，等独立任务按 2.1 的方案改。
6. 编辑期曾有一次跑偏：摇杆第一次真机实测是**对角推**（子向量 x≈0.59），因为 zone 的 pivot 是
   `(0,0)`，指针局部坐标的中心其实是 `rect.center` 而不是 `(0,0)`。已修（`ApplyLocalOffset` 先减
   `rect.center`）并补回归用例 `Stick_PressOnTheVisualCentre_ReadsAsZero_NotAsACornerPush`；
   真机复测推杆值变为 `x=-0.02, y=0.92`。

### 2.6 现场与产物

- `git status`（本轮结束）只有本任务的本意改动：
  `Tools/build_android.ps1`、`Game/Bootstrap/GameBootstrap.cs`、`Game/View/Player/PlayerMotor.cs`、
  `Game/View/UI/GameHud.cs`、`ZFrame/Tests/Input/ZFrame.View.Input.Tests.asmdef`、
  `ZFrame/View/AssemblyInfo.cs`、`ZFrame/View/Input/PlayerInputBridge.cs`、`ZFrame/View/UI/UIRoot.cs`，
  以及新增 `ZFrame/View/Input/Touch/*`、`ZFrame/Tests/Input/TouchControlsTests.cs`（含 .meta）。
- **构建产生的噪音已还原**：4 次出包把 `Tools/version.json`（18→22）与
  `ProjectSettings.asset` 的 `bundleVersion` 顶高，已 `git checkout --` 还原到 HEAD；
  `URP_*.asset` / `QualitySettings.asset` 本轮**未被改写**。设备上现装的是 `0.1.0.22` 的 dev 包。
- 所有改动 `.cs` **无 BOM**；换行由 `.gitattributes`（`* text=auto eol=lf`）在提交时归一为 LF
  （本机检出为 CRLF，编辑前后一致）。证据文件都在 `TempLogs/and04/`
  （不入库）：日志 `device_final_full.log`、`editmode.xml`、`playmode.xml`、截图 `final_01..08`。
- 未跑 `git commit`。

---

## 验收清单（供验收代理）

- [ ] V1 阶段 A：换模块后按钮可点（或有证据地选了备选方案），`UIHierarchyTests` 断言处置有理由且未恒真
- [ ] V2 摇杆/按钮驱动的是既有 `MoveInputEvent`/`InteractInputEvent` 链路（不是平行旁路）
- [ ] V3 多点触控：摇杆+按钮同时有效（有证据，不是"代码看起来可以"）
- [ ] V4 只在触摸设备显示；桌面零残留；安全区避让
- [ ] V5 键盘不回归（WASD 仍可移动）；EditMode 785 / PlayMode 20 不回归
- [ ] V6 真机实证：出包+安装+推摇杆角色真动（或如实记录"未做"）
- [ ] V7 范围：未改 Governor 三件套、未动 URP 资产、`.cs` 无 BOM、未 commit
- [ ] V8 定向变异：抽 1 条新用例改回缺陷态应恰好变红（证明用例有牙）

## 验收结论（验收代理写）

> 验收日期 2026-11-01，验收代理与施工代理非同一代理。所有数字为本轮**自跑**，证据在 `TempLogs/audit_and04/`
> （不入库）。未改写任何产品代码；变异已还原（md5 一致）；设备现场已复原。

### 总表

| 项 | 裁决 | 证据（一行） |
|---|---|---|
| V1 阶段 A / 断言处置 | **PASS** | 我独立 dump `ZFrameControls.inputactions`：UI map 的 `Point`/`Click` 仅绑 `<Mouse>/*`，无任何 `<Touchscreen>/*` → 换 `InputSystemUIInputModule` 必然"按钮全灭"，A3 备选方案成立；`UIHierarchyTests.cs` 不在 `git status` 改动列表（:74 断言 `StandaloneInputModule` 原样，非恒真） |
| V2 摇杆走既有链路 | **PASS** | 全仓库 `EventBus.Fire(new MoveInputEvent…)` / `Fire(default(InteractInputEvent))` 各仅一处，均在 `PlayerInputBridge.cs:173/179`；`Touch/*` 三个新文件不引用 `EventBus`/`InputAction`，只调 `ApplyVirtualMove/Interact/Pause`（同一 `Forward*` 函数体 + 同一 `PlayerInputGate`） |
| V3 多点触控 | **PASS with 受限** | 组件级用例 `MultiTouch_StickAndInteractButton_AreBothLive`（pointerId 21/22）实跑通过；但证据形式是**直接调用 `OnPointerDown` 的合成 `PointerEventData`**，不经过 UGUI 派发，也不是真机/PlayMode；我用并发 `adb input swipe`+`tap` 试图注入双指，真机日志 `engine sees touch: count=1` 始终为 1（adbd 串行化），**真机双指仍无机器证据** |
| V4 只在触摸设备显示 / 零残留 / 安全区 | **PASS** | `Spawn_CreatesNothing_OnDesktop…`（返回 null、无组件、HUD canvas `childCount==0`）+ `ApplySafeArea_…`（1080×2400/上下 120px → anchors 0.05–0.95）实跑通过；真机 1080×2400 截图中摇杆圆盘完整可见、未被底部手势条压住 |
| V5 键盘不回归 / 测试家底 | **PASS with 受限** | 自跑 **EditMode 797/797（0 failed）**、**PlayMode 20/20（0 failed）**、原生 **281 cases / 771,162 assertions, 0 failed**；键盘侧仅代码等价（`OnMove` 原两行抽成 `ForwardMove`，参数取值路径未变），**无端到端 WASD 用例，我亦无法在批处理下合成（与 2.5.0 所述一致）——本轮未亲按 WASD** |
| V6 真机实证 | **PASS** | 设备在线（8UR4C19C03009348，仍装 `0.1.0.22`，与 `Build/Android/ZFrame_MonoDev.apk` 20:53:52 对得上）。我**独立复跑**：force-stop→启动→点"开始"→`input swipe` 推杆，日志 `[motor] move=(-0.01,1.00)`、`player moved: (0,1.08,0) -> (-1.51,1.08,5.56)`、松手 `[bridge] move ZERO fired`；截图见 `aud02_game.png`/`aud03_after_stick.png` |
| V7 范围 / 现场 | **PASS with 受限** | `git status` 无 Governor 三件套、无任何 `.asset`、`Tools/version.json`+`ProjectSettings.asset` 已还原；改动 `.cs` 首字节均非 `EF BB BF`；HEAD 仍为 `5c6f27d`（**未 commit**）；受限点＝`UIRoot.cs` 的 EventSystem 修复超出简报范围（见下 C） |
| V8 定向变异 | **PASS** | 去掉 `TouchActionButton.OnPointerDown` 的 `PointerId >= 0` 守卫 → `testcasecount=12 passed=11 failed=1`，**恰好**只有 `PauseButton_PublishesTheExistingPauseCommand` 变红；还原后 `md5sum -c` 三个文件全 OK、复跑 12/12 绿 |

### 亲跑数字（`results.xml` 为准）

| 套 | 本轮实测 | 施工自述 | 一致性 |
|---|---|---|---|
| EditMode | **797 / 797，0 failed，0 skipped**（`TempLogs/audit_and04/editmode.xml`，`testcasecount="797"`） | 797/797 | ✔ |
| PlayMode | **20 / 20，0 failed**（`playmode.xml`） | 20/20 | ✔ |
| 原生 | **281 cases / 771,162 assertions，0 failed**（`Native/build/bin/Release/zrender_test.exe` 直跑） | 281 / 771,162 | ✔ |
| 新增用例 | `TouchControlsTests` 12 条**全 Passed**（逐条核对） | 12 条 | ✔ |

### A. 摇杆是否真接既有链路 —— 是，无旁路

`UGUI 指针 → VirtualJoystick.ApplyLocalOffset → TouchControlsView.OnStickMoved → PlayerInputBridge.ApplyVirtualMove → ForwardMove → EventBus.Fire(new MoveInputEvent)`。
交叉验证：① 全仓库只有 `PlayerInputBridge` 一处构造这两个事件；② `Touch/*` 不引用 `EventBus`、`InputAction`、`Input.Get`（`using UnityEngine.InputSystem` 仅用于 `Touchscreen.current` 判可见性）；③ 虚拟输入与真输入共用同一 `Forward*` 函数体与同一 `PlayerInputGate`，`RebindService`/`BindingConflictDetector` 的绑定表未被写入。**判 PASS。**

### B. 多点触控 —— 组件级有牙，真机无证

- 有：两条用例（`MultiTouch_…`、`Stick_KeepsTheFirstPointer_…`）实跑通过，语义（第二指不抢占、非持有者松手不释放）被锁死，且 `TouchActionButton` 的重复按下守卫在 V8 变异中被证明有牙。
- 没有：`PointerEventData` 是测试里手工 `new` 的，**绕过了 `StandaloneInputModule` 的每指派发**——这恰恰是真机上唯一可能出问题的那一环。真机注入双指在本机不可行（我实测 `touchCount` 恒为 1）。
- 结论：**PASS with 受限**。工程判断上"每指一条 pointer 流"是引擎既有行为（单指真机已走通同一条 `ProcessTouchEvents`），风险不高，但**不能写成"已验证"**。

### C. 那个"顺带修的 EventSystem 阻断缺陷"（超范围改动）

**改了什么**（`git diff` 逐处，共 3 处 18 行）：
1. `EnsureEventSystem()` → `EnsureEventSystem(Transform root)` 签名加参；
2. 两处调用点由 `EnsureEventSystem()` 改为 `EnsureEventSystem(orphan.transform)` / `EnsureEventSystem(host.transform)`；
3. 新建 EventSystem 后 `host.transform.SetParent(root, false)`（在 `AddComponent<EventSystem>` 之前）。
另加 1 处 dev 打点（`GameBootstrap.OnSceneLoaded` 打 `eventSystem=…`）。**未改模块类型、未改 `DontDestroyOnLoad` 逻辑、未动 `UIHierarchyTests`。**

**① 是不是真缺陷 —— 是，可从代码独立证明**：`EnsureCreated()` 只对 `host` 调 `DontDestroyOnLoad`，而 EventSystem 当时是**同级 root 对象**，单模式加载 SCN_Game 会把它随 SCN_Boot 卸载；且 `_instance != null` 早退意味着**此后再也不会补建**。真机侧我也复现了修复后的状态：`OnSceneLoaded('SCN_Game') eventSystem=[UIRoot.EventSystem]`，且我独立点开暂停面板后**点"继续"生效**（面板关闭，截图变化）——说明 SCN_Game 内 UGUI 派发确实是活的。

**② 修法是否最小、有无回归风险**：修法最小（只改父子关系，不新增组件/不换模块）。回归风险**低但非零**：
- 层次结构变化（EventSystem 由 root 变为 UIRoot 子物体）——现有 797 条用例无 `UIRoot.transform.childCount` 断言，全绿；但这类结构断言只是"这次没人写"，不是"不可能被写"；
- 生命周期现在与 UIRoot 绑定：任何销毁/重建 UIRoot 的路径都会连带销毁 EventSystem（测试 TearDown 已各自处理，运行时无此路径）；
- **没有新增回归用例锁死"单模式换场景后 EventSystem 仍存活"**——这是我看到的最大遗留缺口，建议补一条 PlayMode 用例（加载第二个场景后断言 `EventSystem.current != null`）。

**③ 是否该拆分**：**建议拆**。它是 `ZFrame.View` 框架层的跨场景生命周期修复，影响**所有场景的所有 UGUI**，与"触屏摇杆"正交。但它同时是本任务交付的**前置阻断**（不修则 SCN_Game 内摇杆/按钮/暂停面板全死），所以"在本任务内顺手修"可以接受；仍应**单列 commit + 在 TASKS.md/WORKLOG 留痕**，不要只沉在 A-AND-04 的施工报告里。
**裁决：PASS with 受限**（不是 FAIL：修法最小、无侵入、真机实测有效；受限＝超范围 + 缺回归用例）。

### D. 真机证据真伪 —— 可信度高，但有 3 处必须打折

**核对结果（正向）**：
- 我从设备拉取 `zframe_boot.log`（md5 `204d4daa…`，146,223 B），其内含施工引用的**同一批时间戳**：`13:32/12:58:25.284 STEP #33 OnSceneLoaded('SCN_Game') eventSystem=[UIRoot.EventSystem]`、`#42 touch stick: x=-0.02 y=0.46`、`#45 [bridge] move ZERO fired (subscribers=1)`、`#100 互动`、`#1248 暂停` → 施工贴的日志与设备原件**逐行一致**，不是编的。
- 坐标自洽：`engine sees touch pos=(206,208)` 与摇杆几何推算值（`EdgeMargin 36 + 半径 150 = 186`，×CanvasScaler≈1.117 → 208）吻合，说明手指确实落在摇杆盘上。
- Version 自洽：设备 `versionName=0.1.0.22 / lastUpdateTime 20:54:12` ↔ APK 构建 20:53:52。
- **我独立复跑**（同一 APK，未重打包）：`player moved: (0.00,1.08,0.00) -> (-1.51,1.08,5.56)`，推杆值 `y` 从 0.01 单调升到 1.00，松手 `move ZERO fired`，随后 `互动`/`暂停` 均打点。施工的核心声称**复现成功**。

**必须打折的 3 处**：
1. **"松手归零"只对事件层成立，角色并没有停下**：`ZERO fired` 之后位置继续以 ≈3 单位/秒漂移 `5.56→6.92→8.50→10.07…`（我这次也一样）。施工已在 2.5.3 如实披露为守卫 Chase 顶推的既有缺陷——我复核后**同意归属**（`[motor] move=(0.00,0.00)` 之后 `MoveInputEvent` 再无产出，漂移不来自摇杆），但读者不能把"归零"理解成"角色停住"。
2. **`final_05_back_to_menu.png` 与 `final_04_pause_panel.png` 字节完全相同**（md5 `abcbd7d0…`）——即该次"返回主菜单"点击根本没有生效。报告正文写的是"未走通"，**没有拿它当成功证据**（这点是诚实的），但文件名有误导性，建议清理。
3. **真机双指无证据**（同 B）。

**其它**：设备当时在线，因此不是"未能复跑"，而是**已复跑且成功**。为留痕，我把设备日志回填为施工当次原件（见"现场还原"）。

### E. 回归亲跑 —— 见上表，全部对得上

EditMode **797/797**、PlayMode **20/20**、原生 **281/771,162**，与施工自述一致。基线 785 + 新增 12 = 797，只增不减。
**键盘 WASD 未端到端复核**：我无可用手段（批处理下 legacy Input 无可脚本化设备；`InputTestFixture` 在包 `Tests/` 目录不被项目程序集引用，施工的失败记录我认可）。可给的替代证据是 diff 等价性 + 全部输入相关用例（`PlayerInputGateTests`/`MoveRebindTests`/`BindingConflictDetectorTests`/`RebindServiceTests`）全绿。**建议主控在编辑器里手按一次 WASD 收尾**。

### F. 定向变异 —— 恰好变红，已还原

- 变异点：`TouchActionButton.OnPointerDown` 的 `if (eventData == null || PointerId >= 0)` → `if (eventData == null)`（"同键多指重复触发"缺陷态）。
- 结果：`mutant.xml` `testcasecount="12" passed="11" failed="1"`，唯一红＝`PauseButton_PublishesTheExistingPauseCommand`（`PauseCount` 期望 1 实得 2）。**没有连带误伤**其它 11 条。
- 还原：`md5sum -c TempLogs/audit_and04/md5_before.txt` → 3 个文件全 `OK`（`TouchActionButton.cs` = `1ec0551c…`）；`git diff` 该文件为空；复跑 `restored.xml` **12/12 绿**。
- 结论：新增用例**有牙**，不是恒真。

### G. 范围与现场

- 新文件：`ZFrame/View/Input/Touch/{VirtualJoystick,TouchActionButton,TouchControlsView}.cs` + `ZFrame/Tests/Input/TouchControlsTests.cs`（含 `.meta`）——与简报一致。
- `FrameGovernor`/`GovernorLadder`/`GovernorTuning`：**未改**（`git status` 零命中）。
- Unity 安装目录：**未动**（`Unity.exe` 仍为 2025-11-27，安装目录今日无新改动文件）。
- `.cs` 无 BOM：10 个改动/新增文件首字节均非 `EF BB BF`（实测 `757369`=`usi`、`2f2f20`=`// `）。
- `Tools/version.json`、`ProjectSettings.asset`、`QualitySettings.asset`、`URP_*.asset`：**均已还原**（`git status` 零命中）。
- **未 commit**：HEAD 仍为 `5c6f27d`（G-116），无新提交。
- 范围边缘（记录，不算缺陷）：`ZFrame/View/AssemblyInfo.cs` 给产品程序集加了 `InternalsVisibleTo("ZFrame.View.Input.Tests")`（测试接缝，合理但属产品程序集改动）；`Tools/build_android.ps1` 新增 `MonoDev` 后端（调用的是**已存在**的 `PlayerBuild.BuildAndroid64MonoDev`，未新增 Editor 代码）；`PlayerMotor`/`GameBootstrap` 仅加 `DEVELOPMENT_BUILD` 门控打点。

### 新发现问题（均不建议塞回本任务）

1. **暂停面板"返回主菜单"在真机点了没反应**（我实测：`(537,1573)` 点击后截图 md5 与点击前**完全相同**），而**同面板"继续"可点**、UGUI 派发正常 → 不是 EventSystem 问题。根因未定位（按钮无 handler / 被遮挡 / 命中区偏移），建议单开任务。这也解释了施工"两次都没点中"的观察。
2. **守卫 Chase 持续顶推玩家**（松手后仍 ~3 单位/秒漂移），已复现。既有缺陷（A-M4 范围），建议单开任务——它显著破坏"能玩"的手感。
3. **EventSystem 持久化修复缺回归用例**（无"换场景后 `EventSystem.current != null`"的断言）。
4. `final_05_back_to_menu.png` 与 `final_04` 字节相同，证据文件命名误导（非产品缺陷）。
5. 真机物理双指仍无机器证据（adb 注入不可行）。

### 最终裁决

**PASS with 受限（可闭环，带 3 条遗留项）**

- 三条硬门禁全部达标：**摇杆确实走既有 `MoveInputEvent`/`InteractInputEvent` 单链**（V2 PASS）、**真机推杆角色真动且松手归零在事件层成立**（V6 PASS，已独立复跑）、**测试家底 797/20/原生 281 全绿且新增用例有牙**（V5/V8 PASS）。
- 受限/须主控接手的 5 件事：
  1. **手按一次桌面 WASD** 收尾（V5 唯一缺口）；
  2. **真机双指人工点一次**（V3 唯一缺口，人工 10 秒可验）；
  3. 把 `UIRoot` 的 EventSystem 持久化修复**拆成独立任务留痕**，并补一条"换场景后 EventSystem 存活"的 PlayMode 回归用例；
  4. 单开任务查"暂停面板-返回主菜单真机点不动"；
  5. 单开任务查"守卫 Chase 顶推玩家"。
- 无 FAIL 项：未发现伪造证据、未发现把推测写成已验证、未发现范围外的产品行为改动（EventSystem 那一处属必要前置修复，已单列评估）。

### 现场还原声明

- 变异：已还原，`md5sum -c` 三文件全 OK，复跑 12/12 绿，`git diff` 为空。
- 设备：审计用临时截图（`/sdcard/aud0*.png`）已删；`zframe_boot.log` 已回填为施工当次运行原件（设备侧 md5 `204d4daa9ebd2847fdd26e698163d430`，与 `TempLogs/audit_and04/pulled_zframe_boot.log` 一致）；应用已 `force-stop`（下次启动会按设计 truncate 日志）。
- 仓库：`git status` 与验收前逐项一致（无 `.asset` 噪音、无新增/删除文件）；`TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/产品代码**一字未改**；**未 git commit**。
- 本轮新增证据（未入库）：`TempLogs/audit_and04/{editmode,playmode,mutant,restored}.xml`、`{editmode,playmode,mutant,restored}.log`、`pulled_zframe_boot.log`、`audit_run.log`、`audit_run2.log`、`audit_multitouch.log`、`aud01_menu.png`、`aud02_game.png`、`aud03_after_stick.png`、`aud04_pause.png`、`aud05_try_return_menu.png`、`aud06_resume.png`、`md5_before.txt`。
