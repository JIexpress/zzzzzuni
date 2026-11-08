# TASK-A-AND-10 —— 触屏打磨包：浮动摇杆+多点触控+触觉反馈+参数化

状态: doing | 派发: 2026-11-09 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景

A-AND-04 的固定摇杆已真机可用。打磨=现代手游触控体验：浮动原点、多点同时、触觉反馈、参数化、视觉反馈。**打磨不重写**——A-AND-04 产物（触屏控件/生成去重）语义保持。

### 设计要点

1. **浮动原点摇杆**：按下半屏任意处→摇杆在该处生成（底座+手柄）；松开消失；固定摇杆模式保留为设置开关（默认浮动）
2. **参数化**：死区半径/最大拖拽半径/灵敏度→**设置页"操作"页签新增三条**（Slider 0.5~2.0 倍率——注意 SettingsRegistryTests 行数护栏：新增行会改行数断言，护栏更新属本任务合法范围并报告单列）
3. **多点触控**：左半屏摇杆+右半屏交互键**同时**工作（Input System enhancedTouch 或多 pointer——A-AND-04 若单 pointer 则升级，报告说明）；攻击/交互键支持按住连发（可选）
4. **触觉反馈**：交互键按下→`Handheld.Vibration`/Android haptics 轻震（设置页可关——游戏性页新增开关或复用既有，报告选型）
5. **视觉反馈程序化**（零资产）：摇杆底座/手柄 Image 半透明+按下增亮、交互键按下缩放动画（简易 tween）
6. 可证伪 PlayMode 用例：①浮动原点：不同位置按下→摇杆锚点跟随（锚点坐标断言）②死区：微动不产生位移输入（断言）③多点：模拟左摇杆+右键同按→移动与交互同时生效 ④参数化：灵敏度 2.0 时同位移→输入向量翻倍 ⑤触觉开关关闭→无震动调用（mock/计数断言）
7. 自测：EditMode+PlayMode 全绿（基线 811/21 只增不减，以当前 HEAD 为准）+ Validate exit 0

### 约束

- 白名单：`Assets/Game/View/**`（触屏控件）、`Assets/ZFrame/View/**`（若设置项扩展）、Tests/**、本日志
- 不改 A-AND-04 既有语义（控件生成去重/EventSystem 修复保持）；Unity 批处理纪律（清锁/轮询/taskkill/勿带 -quit）；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0
- 真机验证：若设备在位（adb devices）→装包+screencap 留证；不在位列遗留（主控安排）

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ **日期**: 2026-11-09 ｜ **仓库**: `C:\Users\1\Desktop\zcode\GameFramework`（HEAD `963a73f` 起施工）

### 1. 交付清单

新文件（6 个 .cs，.meta 由 Unity 批处理导入生成）：

| 文件 | 职责 |
|---|---|
| `UnityProject/Assets/ZFrame/View/Settings/TouchControlSettingOptions.cs` | 五条触屏设置行的单一数据源（钳制 0.5~2.0 / 出厂默认 / `Changed` 通知 / ResetToDefaults 测试接缝） |
| `UnityProject/Assets/ZFrame/View/Input/Touch/TouchHaptics.cs` | 触觉闸门：`Enabled` + `Trigger()`（关闭=严格零调用零计数）+ `Pulse` 可替换接缝；默认脉冲 Android 26+ `VibrationEffect.createOneShot(20ms)` 轻震、旧系统回退 `Handheld.Vibrate()`、桌面/编辑器 no-op |
| `UnityProject/Assets/ZFrame/View/Input/Touch/FloatingStickZone.cs` | 下半屏透明命中区（anchor (0,0)-(1,0.5)，α=0 Image 供 raycast），指针流转发给 View；固定模式整体 Deactivate |
| `UnityProject/Assets/ZFrame/Tests/Input/TouchFloatingControlsTests.cs` | EditMode 11 条（浮动默认/锚点/二次手指/拖动链路/固定切换/死区倍率/灵敏度倍率/拖拽半径行） |
| `UnityProject/Assets/ZFrame/Tests/Debug/TouchControlSettingOptionsTests.cs` | EditMode 6 条（钳制/默认/Changed/触觉闸门/mock 计数契约/settings.json 往返重放） |
| `UnityProject/Assets/ZFrame/Tests/PlayMode/TouchFloatingControlsPlayModeTests.cs` | 真实屏幕空间下的五条可证伪用例（见 §4） |

改动文件（7 个，全部白名单内；**打磨不重写**——A-AND-04 控件/去重/EventSystem 语义零改动）：

| 文件 | 改动 |
|---|---|
| `ZFrame/View/Input/Touch/VirtualJoystick.cs` | 增量加参：`SetTuning(deadZone×, sensitivity×)`（:86）、`HoldChanged` 事件（:67）、`BeginPointer` 内部接缝（:114）；`ApplyLocalOffset` 死区阈值=`DefaultDeadZone×倍率`（:209）、输出=线性重映射×灵敏度再夹单位长（:216-222）。**倍率=1 时与 A-AND-04 逐位一致**（既有 11 条触屏测试零改动全绿） |
| `ZFrame/View/Input/Touch/TouchControlsView.cs` | 浮动原点：`BuildFloatZone`（:600，先于按钮建——同层后建者赢 raycast，互动/暂停永远优先）+ `FloatPress/FloatDrag/FloatRelease` 公开接缝（:263/:333/:364）+ `FloatPressAtLocal` 内部接缝（:314，EditMode 无可靠屏幕空间，同 A-AND-04 的 ApplyLocalOffset 纪律）+ `PlaceStickBaseAt`（:379，夹取在安全区内）+ 松开消失（`OnStickHoldChanged` :415 → `HideStickBase`）；参数化：`ApplyTouchSettings`（:475，拖拽半径倍率缩放底座 rect——视觉与拖拽上限同一圆）+ 模式切换 `ApplyStickMode`（:493）；视觉反馈：底座/手柄 α 0.16/0.72 → 按住 0.34/0.92（`ApplyStickColors` :455）；触觉接线：`OnInteractPressed` → `TouchHaptics.Trigger()`（:768，**互动键触发；暂停键开菜单有意不震**，一行注释说明）；`Changed` 订阅随 Build/Teardown 对称（:596/:559） |
| `ZFrame/View/Input/Touch/TouchActionButton.cs` | 按下缩放动画：0.9 倍指数 tween（`PressedScale` :29 / `TweenSpeed` :32 / `StartScaleTween` :104）；EditMode/失活时直接落位保证测试确定性；A-AND-04 色彩反馈保持 |
| `ZFrame/View/Settings/SettingsRegistry.cs` | 操作页尾部（说明行之前）插 4 行：死区半径/拖拽半径/灵敏度三条 Slider（0.5~2.0，默认 1.0）+ 固定摇杆 Toggle（默认 false=浮动）（:286-316）；游戏性页追加"震动反馈" Toggle（**该页唯一 LIVE 行**，apply→`TouchHaptics.Enabled`）（:369）；页头 XML doc 同步 |
| 护栏更新×3 文件 | 见 §3（单列） |

### 2. 设计要点 7 条逐条对照

1. **浮动原点**：下半屏（安全区内）任意处按下 → `PlaceStickBaseAt` 把底座（300px 圆 + knob）锚到按压点（夹取在安全区内，:379-399）；松开 → `HoldChanged(false)` → 底座隐藏（:415-425）；固定模式保留为 `controls.touch.fixedStick` Toggle（默认 false=浮动），固定模式底座常显+自身为命中目标（A-AND-04 原样）。**底座在浮动模式下 raycastTarget=false 纯视觉**，命中统一走 FloatZone——单一按压路径，无"按在底座上/底座外"两套行为分叉。
2. **参数化**：三条 Slider（死区半径/最大拖拽半径/灵敏度，0.5~2.0 倍率）进操作页；死区/灵敏度经 `VirtualJoystick.SetTuning`，拖拽半径经底座 rect 缩放（视觉=可拖范围）。SettingsModel 常规持久化（settings.json），开机应用点/面板即时应用共用同一条 apply 链（往返重放测试验证）。
3. **多点触控**：**A-AND-04 已是多 pointer 架构**（StandaloneInputModule 每指一条 pointer 流 + 控件 pointerId 独占），按简报"若单 pointer 则升级"——**无需升级，保持**。新增证据：浮动区按住 + 互动键同按双生效（EditMode `FloatPressAtLocal_SecondFingerIsIgnored` / PlayMode 用例③）；浮动区对第二根手指直接忽略（`Stick.IsHeld` 短路），按钮永远赢 raycast（兄弟序约束有测试断言）。按住连发未做——简报标注"可选"，简报未点名攻击键（本切片仅互动/暂停），不扩语义。
4. **触觉反馈**：互动键按下 → `TouchHaptics.Trigger()`；开关选型：**游戏性页新增"震动反馈"行（简报给出的第一选项）**，LIVE apply 写 `TouchHaptics.Enabled`。物理脉冲选型：Android 26+ `VibrationEffect.createOneShot(20ms)` 轻震（`Handheld.Vibrate` 固定 ~500ms 对按钮手感太重），API<26 回退 `Handheld.Vibrate()`，桌面/编辑器安全 no-op；振动异常吞掉降级为 no-op（触觉绝不拖垮输入链）。
5. **视觉反馈（零资产）**：底座/手柄半透明 + 按住增亮（α 0.16→0.34 / 0.72→0.92，`ApplyStickColors`）；互动/暂停按住缩放 0.9 指数 tween（`TouchActionButton`，无协程库）。
6. **五条可证伪 PlayMode 用例**（`ZFrame/Tests/PlayMode/TouchFloatingControlsPlayModeTests.cs`，真实屏幕空间 + 真实画布缩放）：①锚点跟随 :93（**非循环断言**：锚点位移 == 屏幕位移/画布缩放，缩放系数独立取自 HUD Canvas，容差 0.5px；并断言松开消失）；②死区 :132（微动 0.3×死区 → 严格零 + 宏观 0.85×半径 → >0.3，死区/半径均运行时实测而非硬编码）；③多点 :162（浮动摇杆推住 + 互动键同按 → `InteractInputEvent`/bridge 增量各一条、摇杆方向不被打断）；④灵敏度 :197（同屏幕位移，灵敏度 2.0 → 输出精确 2×基线且 ≤1，经 `ApplySensitivity(2)` 走 Changed 通知的真实调参链）；⑤触觉 :231（关闭后 mock 计数不变 + 命令链照常、重开后恢复）。
7. **自测**：EditMode **1099/1099** 全绿（基线 1082 → +17，只增不减）+ PlayMode **85/85** 全绿（基线 80 → +5）+ 三校验器 exit 0（证据 §5）。

### 3. 设置页与行数护栏更新（单列）

- **操作页**：8 行 → **12 行**（键位 6 行不动，尾部插入触屏 4 行，说明行/恢复默认仍居末尾——`MoveRebindTests` 的"说明行位于倒数第二"断言因此零改动通过）。
- **游戏性页**：3 行 → **4 行**（追加震动反馈，唯一 LIVE 行——原"全页 Apply==null"断言按行拆分保留前三行语义）。
- **护栏更新清单（本任务合法范围，4 个文件 6 处断言）**：
  - `ZFrame/Tests/Debug/SettingsRegistryTests.cs` `ControlsPage_IsTheRealBindingList`（:192 计数 8→12 + :200 类型分布 + :232-261 触屏四行逐项断言；`GameplayPage_*` :274/:282-296）；
  - `ZFrame/Tests/Input/SettingsBindingRowTests.cs` `ControlsPage_IsTheRealBindingList_WithAMapWideReset`（:239-254 类型数组 + :256-264 说明行/恢复默认下标 6/7→10/11）；
  - `ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs`（简报点名护栏之外**施工中发现**的第三处行数护栏，如实单列）：`SelectPage_RebuildsRows_PerRegistry`（:144 操作页 8→12、:147 游戏性页 3→4）与 `RevertButton_RestoresSnapshot_AndStaysOpen`（:222 撤销后行数 3→4）——两处均为纯行数断言，无语义弱化。
- 持久化格式零改动：新行走 settings.json 常规 Toggle/Slider 路径（`TouchControlSettingOptionsTests.SettingsRows_PersistAndReplayThroughTheOrdinaryPath` 往返验证 Save→Load 重放）。

### 4. A-AND-04 语义保持核查（验收 V4 对应）

- `git diff` 核对：`ZFrame/Tests/Input/TouchControlsTests.cs` 与 `ZFrame/Tests/PlayMode/TouchControlsSceneReentryPlayModeTests.cs` **零改动**（空 diff）；A-AND-04 全部 11 条 EditMode 触屏测试未改一字全绿。
- 生成去重保持：`Spawn` 的"活着实例"判据（TASK-A-AUDIT-02 A-3）原样；场景重入回归用例全绿。
- EventSystem 修复保持：无输入模块交换，控件仍骑 StandaloneInputModule+legacy touch 路径。
- `ApplyLocalOffset` 倍率=1 时逐位等价（InverseLerp(deadZone,1,mag)×1，偏移夹取/方向/单位长夹取不变）——新增调优测试同时反向验证复原一致性。
- `VirtualJoystick.Configure` 公开签名未变；新增 API 全部为加法（`SetTuning`/`HoldChanged`/`BeginPointer`(internal)）。

### 5. 自测证据（本轮 HEAD `963a73f` + 本任务改动）

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode | **total=1099 passed=1099 failed=0 skipped=0** | `TempLogs/runtests_editmode.xml`（本轮写入，8.69s xml） |
| PlayMode | **total=85 passed=85 failed=0 skipped=0** | `TempLogs/runtests_playmode.xml`（本轮写入，54.8s xml） |
| 架构校验器 arch | **exit 0** | `TempLogs/arch_report.txt` |
| config 校验器 | ALL PASS exit 0 | `TestReport.txt`（config 段） |
| naming 校验器 | ALL PASS exit 0 | `TestReport.txt`（naming 段） |
| 文件规范 | UTF-8 无 BOM / LF | `od -c` 抽查（首字节 `usi`="using"，无 BOM）+ `git ls-files --eol` 全部 `w/lf` |
| Unity 批处理纪律 | 遵守 | 经 `Tools/run_tests.ps1`（单实例检查/清锁/-runTests 无 -quit/xml 指纹证据/taskkill 轮询内建）；无并行代理撞锁发生（每次启动前 tasklist 确认无 Unity.exe） |
| adb 真机 | **设备不在位** | `adb devices` → 空列表；真机装包+screencap **列为遗留**（主控安排，简报约束原话"不在位列遗留"） |

### 6. 施工过程中的修正（如实记录）

1. 首轮编译：`TouchControlsView.cs` 缺 `using UnityEngine.EventSystems;`（Float* 接缝新增所致）→ 补。
2. 二轮编译：`PointerEventData.pressEventCamera` 为只读属性 → 重构：UGUI 生产路径走内部重载**转发原始事件对象**（相机信息不丢），合成事件只带 pointerId+position（UIRoot 画布为 ScreenSpaceOverlay，camera=null 换算精确，代码注释说明约束）。
3. 三轮编译：两个新测试文件补 `using UnityEngine.UI;`。
4. 四轮：3 条护栏失败（§3 的 SettingsPanelSmokeTests 两处 + 本任务持久化测试的"applied=3"断言错误——Save 写出的是**全部**有值行而非仅触屏三行，断言改为 valueRows 对齐）→ 全部修正。
5. 五轮 PlayMode：用例③ `InputRuntime.Bridge.InteractCount` 是**跨用例持久单例累计值**，绝对值断言受前序用例污染（Expected 1 was 2）→ 改增量断言（bridgeBefore+1）。
6. 终态：EditMode/PlayMode/三校验器全绿（§5）。

### 7. 遗留与备注

- **真机验证遗留**：无 Android 设备在位（adb 空列表）。全部交互面（浮动按压/多点/触觉）均有 PlayMode 证据，但 Handheld 振动手感、真机 safe-area/异形屏表现需装机复核——建议主控安排装包（`build_android.ps1`）+ screencap 留证。
- **白名单外观察**：工作区存在 `Docs/Parity/crowd_r9_15_on.png`、`Docs/Parity/utility_personality_goap_chain.png` 两个二进制 M 噪声（非本任务产生，未触碰）；提交前请主控按 HANDOFF 纪律 `git checkout --` 丢弃或另行处置。
- 触觉触发面=互动键（暂停键有意不震）；若验收要求暂停键也震，属一行改动。
- 按住连发（简报"可选"）未实现，理由见 §2 条 3。

## 验收清单（主控写）

- [x] V1 亲跑 EditMode+PlayMode 全绿（数字写入结论）；五条可证伪用例真实（行号）
- [x] V2 浮动原点/死区/多点/参数化/触觉五实现审查（行号）
- [x] V3 设置页新增项与行数护栏更新核对（报告单列）
- [x] V4 Validate exit 0（自跑）；A-AND-04 语义保持（git diff 既有触屏测试核对）
- [x] V5 文件规范+施工报告四节完整（含真机验证状态如实）

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ **日期**: 2026-11-09 ｜ **裁决: PASS**（V1-V5 全过，零 FAIL 项；与施工代理非同一会话，全部证据亲跑/亲查）

| 项 | 裁决 | 证据（一行） |
|---|---|---|
| V1 双轨亲跑 | PASS | 经 `Tools/run_tests.ps1`（单实例检查/清锁/-runTests 无 -quit/xml 指纹/taskkill 全部内建）亲跑：EditMode **total=1099 passed=1099 failed=0 skipped=0**（Unity exit 0，`TempLogs/runtests_editmode.xml` 本轮指纹 8.99s）+ PlayMode **total=85 passed=85 failed=0 skipped=0**（54.3s，`TempLogs/runtests_playmode.xml`）；两 xml `result="Failed"` 计数均为 0；5 条新 PlayMode 用例逐名核对在案：`Case1_FloatPress_AnchorFollowsThePressPoint_AndTheBaseDisappearsOnRelease`/`Case2_DeadZone_MicroDragsProduceZeroInput_MacroDragsProduceMovement`/`Case3_MultiTouch_FloatStickAndInteractButtonAreBothLive`/`Case4_SensitivityDoubling_SameDragDisplacementDoublesTheVector`/`Case5_HapticsToggleOff_ProducesZeroPulseCalls_CommandsStillFire`；增量对账 1082+17=1099（TouchFloatingControlsTests 11 + TouchControlSettingOptionsTests 6）、80+5=85，只增不减 |
| V2 浮动原点+死区 | PASS | 按下生成：`FloatingStickZone.cs` :37-45 OnPointerDown→Owner.FloatPress；`TouchControlsView.cs` BuildFloatZone :600-617（anchor (0,0)-(1,0.5) 下半屏 :604）、FloatPress :263-292 → PlaceStickBaseAt :379-399（夹取安全区内）；松开消失：OnStickHoldChanged(false)→HideStickBase :415-431/:444-453（enabled=false 无 raycast）；固定模式开关：ApplyStickMode :493-529（FloatZone.SetActive :497-500，底座 raycastTarget 随模式翻转 :510）+ 测试 `FixedStickSwitch_RestoresTheAAnd04Presentation`（TouchFloatingControlsTests.cs:228-251）；死区用例真实：PlayMode Case2 :132-157（微动 0.3×死区→严格零 :146-147，死区/半径运行时实测非硬编码 :136-139）+ EditMode `Tuning_DeadZoneMultiplier_ScalesTheThreshold` :256-283（倍率 2 下 20px 读零 :271、复原一致 :281） |
| V3 多点+灵敏度 | PASS | 多点同按：PlayMode Case3 :162-192——浮动区 pointer 201 推住（:168-170 断言 Y>0.3）+ 互动键独立 pointerId 202 同按 :178 → 事件增量 +1、bridge 增量 +1（:182-183，跨用例累计值用增量断言防污染 :174）、摇杆方向不被打断 :184；灵敏度 2×：PlayMode Case4 :197-226——同屏幕位移 :202/:218、经 `ApplySensitivity(2)`→Changed 真实调参链 :214、**精确翻倍断言** `Assert.AreEqual(baseline*2f, doubled, 0.02f)` :222 且夹单位长 :223（基线取未饱和区间 :210）；EditMode 几何步 `Tuning_SensitivityMultiplier_DoublesOutput_AndCapsAtUnit` :286-306（:302 精确翻倍、:304 满推仍夹 1） |
| V4 触觉开关+实现 | PASS | 开关用例：PlayMode Case5 :231-258——mock 经 `TouchHaptics.Pulse` 接缝 :52，关闭（`ApplyHaptics(false)` :242 落闸门 :243）后两次按下 `_pulseCount`/`PulseCount` 均保持 1 不变（:247-248 零调用零计数）且交互命令照常 :249、重开恢复 :256；EditMode `TouchHaptics_TriggerCountsOnlyWhenEnabled` :121-139（关闭零调用不计数 :132-133）；Android 实现：`TouchHaptics.cs` :37-38 `SDK_INT>=26` 检查 → :40-45 `VibrationEffect.createOneShot(20ms)`，API<26 回退 `vibrate(20L)` :49，异常吞掉降级 no-op :53-58，桌面/编辑器 no-op :60；闸门 `Trigger()` :80-89（!Enabled 直接 return，生产写入单路径 `ApplyHaptics`） |
| V5 护栏+校验器+规范 | PASS | 护栏 diff 亲核（4 文件）：SettingsRegistryTests.cs 操作页 8→12（:192）+触屏四行逐项（:234-262）+游戏性页 4 行/震动 LIVE（:274-296）；SettingsBindingRowTests.cs 类型数组 + 说明/恢复默认下标 6/7→10/11；SettingsPanelSmokeTests.cs :144（12 行）/:147（4 行）/:222（撤销后 4 行）——全仓 grep 所有 RowCount/SettingCount 断言无遗漏（controls=12 两处、gameplay=4 两处一致）；三校验器自跑 **arch/config/naming 全部 exit 0**（同轮 run_tests.ps1 SUMMARY 5 段 ALL PASS，报告 TempLogs/arch_report.txt 等）；文件规范：13 个新/改 .cs 首三字节非 BOM（`75 73 69`="usi"）+ 真实 CR 计数全 0（纯 LF）；四节齐全（本节即第四节）；A-AND-04 语义保持：`git diff` 对 `Tests/Input/TouchControlsTests.cs`+`Tests/PlayMode/TouchControlsSceneReentryPlayModeTests.cs` 为**空 diff**，且 SceneReentry 两条回归用例在本轮 PlayMode xml 中在案全绿；真机：验收代理亲跑 `adb devices` → 空列表，与施工报告一致，**真机装包+screencap 列遗留**（简报约束"不在位列遗留"，主控安排） |

**验收备注**（不阻塞 PASS）：① 触觉触发面=互动键、暂停键有意不震（代码注释 + 施工报告 §7 如实，属设计取舍非缺陷）；② 按住连发为简报"可选"项未实现（报告已声明理由）；③ `Docs/Parity/` 两个二进制 M 噪声非本任务产物（施工报告 §7 已标注），提交前请主控按 HANDOFF 纪律处置。未 git commit（验收代理纪律）。
