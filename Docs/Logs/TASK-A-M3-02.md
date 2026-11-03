# TASK-A-M3-02 SettingsSystem 设置系统 + 选项界面五页签（数据驱动生成）

状态: doing | 派发: 2026-10-25 | 施工: general-purpose 施工子代理 (2026-10-25) | 验收: general-purpose 验收子代理(代码复核) | commit: （待填）

## 任务简报（主控写）

**目标**：M3 核心交付——游戏内选项界面：SettingDefinition 数据驱动自动生成 UI 行（加选项不改布局代码）、五页签（视频/音频/操作/游戏性/无障碍）、即时应用+撤销+恢复默认、`settings.json` 独立持久化（设备级，不入存档槽）。

**设计要点**：
1. `ZFrame/View/Settings/`：
   - `SettingDefinition`（纯类）：id/显示名/类型（Toggle/Slider/Enum/Action）/默认值/当前值/Min/Max/枚举选项表/apply 回调（Action<object>）/需重启标志
   - `SettingsPage`（纯类）：页签 id+定义列表；`SettingsRegistry`：注册五页签全部定义（静态工厂 `BuildDefaults()`）——**加选项=加一行定义**（验收重点）
   - `SettingsModel`（纯 C# 可测）：当前值快照/修改标记（dirty）/RevertToSnapshot/ResetAll/Get<T>/Set<T>（Set 即时调 apply）；持久化：JSON（Newtonsoft）读写 `Application.persistentDataPath/settings.json`（**根目录可注入**，同 SaveSystem 先例）；版本号+未知项忽略
   - UI 行生成：`SettingsRowFactory`——按 Definition 类型实例化行控件（Toggle=Toggle+Label、Slider=Slider+Label+值文本、Enum=Dropdown、Action=Button），挂到 VerticalLayoutGroup 的页签容器；**布局零手摆**（验收重点）
   - `SettingsPanelView : IUIView` + `SettingsPanelPresenter`（注册进 UIBinding）：页签按钮条+行容器；"应用并关闭/撤销更改/恢复默认"三按钮（撤销=RevertToSnapshot；即时应用型选项 Set 即生效，"应用"只负责关面板+写盘——语义在 XML doc 写清）
2. 五页签定义（本期实接）：
   - **视频**：全屏模式(Enum)/垂直同步(Toggle)/帧率上限(Enum 30/60/120/无限制)/画质档位(Enum Low/Medium/High→PipelineManager.SetTier)/渲染比例与超分（Enum 占位：关/FSR3/FSR4/DLSS——**存值不生效，R6 接 UpscalerChain，XML doc 注明**）/阴影距离(Slider 关/20/40/60——写档位枚举)
   - **音频**：主/BGM/SFX/UI 四音量 Slider→AudioService.SetBusVolume
   - **操作**：只读当前键位列表（InputSystem M3-03 接，本期占位 Action 行"即将支持"）
   - **游戏性**：字幕开关/字幕大小(Enum)/教程提示频率(Enum)——存值+XML doc 注明消费方后续接入
   - **无障碍**：色盲模式(Enum 关/红/绿/蓝——存值+注明 R6 后处理接入)/UI 缩放(Slider 0.8-1.2→CanvasScaler 动态系数，实接)/屏幕震动强度(Slider 存值)
3. 入口：HUD 占位或 Boot 场景加"设置"按钮（示例面板模式）；Esc 关面板（InputBlocker 协同）
4. 单测（追加）：Model 快照/Revert/ResetAll/Set 即时 apply 调用序、JSON roundtrip（含未知项容忍）、Registry 五页签定义数与类型分布断言、RowFactory 类型→控件映射（EditMode 真 UGUI 构造）
5. 自测：EditMode 全绿 + ValidateFromCommandLine exit 0 + 批处理冒烟（构造 SettingsPanelView prefab 无异常——Setup 脚本）

**约束**：可动 `UnityProject/Assets/ZFrame/View/`、`UnityProject/Assets/UI/`、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0；测试注入根目录不写真实用户目录。

**交付物**：框架全件+五页签+单测全绿+施工报告四节。

## 施工报告（施工代理写）

### 一、交付物清单

**框架全件（`UnityProject/Assets/ZFrame/View/Settings/`，均 UTF-8 无 BOM/LF、XML doc 齐全）**：

| 文件 | 内容 |
|---|---|
| `SettingType.cs` | 枚举 Toggle/Slider/Enum/Action |
| `SettingDefinition.cs` | 纯类：id/显示名/类型/默认值/CurrentValue 镜像/Min/Max/枚举选项表/apply 回调(Action<object>)/需重启标志 + 四个静态工厂（Toggle/Slider/EnumSetting/Action，带参数校验） |
| `SettingsPage.cs` | 页签纯类：id + 显示名 + 只读定义列表 |
| `SettingsRegistry.cs` | 注册表（全库 id 唯一强制）+ 静态工厂 `BuildDefaults()` 五页签；实接 apply 见下 |
| `SettingsModel.cs` | 纯 C# 可测：Get<T>/Set<T>（Set 即时 apply）/TakeSnapshot/RevertToSnapshot/ResetAll/IsDirty；Newtonsoft JSON 持久化 `settings.json`（version 字段 + 平铺 id→值；根目录可注入，同 SaveSystem A-M1-03 先例；未知 id/错型值/新版本/损坏文件全部容忍） |
| `SettingsRowFactory.cs` | 类型→控件映射（Toggle=Toggle+Label、Slider=Slider+Label+值文本、Enum=Dropdown、Action=Button）；页容器=VerticalLayoutGroup+ContentSizeFitter，行=HorizontalLayoutGroup+LayoutElement——**布局零手摆** |
| `SettingsPanelArgs.cs` | Push 参数：Model + Close 委托（Presenter 纯 C# 不摸 UIManager，由开启方注入"如何关我"） |
| `SettingsPanelView.cs` | `UIViewBase` 子类；帧（标题/页签条宿主/行宿主/三按钮）来自 prefab，页签按钮与全部行在每次 OnInit 按 Registry 现生成；Esc=Update 轮询 `Input.GetKeyDown(KeyCode.Escape)`（`SettingsPanelView.cs:190`）→`RequestClose()` |
| `SettingsPanelPresenter.cs` | 纯 C#；attach 时 `TakeSnapshot`+`InputBlocker.Acquire()`（`SettingsPanelPresenter.cs:57`，detach 释放，Esc 协同）；三按钮语义见下 |
| `SettingsEntry.cs` | HUD 入口：RuntimeInitializeOnLoad 自挂载（PipelineManager/UIRoot 同款），HUD 层"设置"按钮，共享 Model（每次打开前 Load）+ 防重复打开 |

**Editor 装配**：`ZFrame/View/Editor/SettingsUiSetup.cs`（菜单 ZFrame/Setup/Generate UI_SettingsPanel prefab；批处理 `-executeMethod ZFrame.View.SettingsUiSetup.SetupFromCommandLine`；同 UISetup 的"生成→落盘→重载→Validate"纪律）。

**资产**：`UnityProject/Assets/UI/Resources/ui_settings_panel.prefab`（key `ui_settings_panel`；Dim blocker+CanvasGroup+Title+TabBar 宿主+RowHost+三按钮，六个序列化引用全部代码接线）。

**UIBinding**：`UIBinding.cs:67` 注册 `SettingsPanelView → SettingsPanelPresenter`（进 `EnsureDefaults`，幂等）。

**测试（`ZFrame/Tests/Debug/`，52 个新用例）**：`SettingsModelTests` 19（快照/Revert/ResetAll/Set 即时 apply 调用序、JSON roundtrip、未知项/错型/损坏/新版本容忍、注入根目录）、`SettingsRegistryTests` 14（五页签 id/顺序/类型分布断言、占位定义 apply=null 断言、id 唯一）、`SettingsRowFactoryTests` 9（真 UGUI 类型→控件映射 + LayoutRebuilder 强刷后纯布局组堆叠断言=零手摆实证）、`SettingsPanelSmokeTests` 10（真 prefab→真 UIManager→真 MVP 全链路，含"加一行定义→多一行"实证与三按钮/Esc 语义）。另 `ZFrame.Debug.Tests.asmdef` 的 `precompiledReferences` 增加 `Newtonsoft.Json.dll`（同 ZFrame.Data.Tests 先例）。

### 二、五页签实接/占位明细（验收清单 #4 对照，行号均在 `SettingsRegistry.cs`）

| 页 | 项 | 状态 | 证据 |
|---|---|---|---|
| 视频 | 全屏模式/垂直同步/帧率上限 | 实接（play mode guard） | :106-146 |
| 视频 | 画质档位 Low/Medium/High | **实接 `PipelineManager.SetTier`** | `:151` |
| 视频 | 渲染比例与超分（关/FSR3/FSR4/DLSS） | **占位存值**，XML doc 注明 B-R6 接 UpscalerChain | `:152-155`（doc 块） |
| 视频 | 阴影距离 关/20/40/60（档位枚举） | 实接 `QualitySettings.shadowDistance` | :157-162 |
| 音频 | 主/BGM/SFX/UI 四 Slider | **实接 `IAudioService.SetBusVolume`**（Services.TryGet 防御，服务未启动则跳过） | `:194` |
| 操作 | 键位重绑定 Action 行"即将支持" | 占位，点击 ZLog 反馈，XML doc 注明 A-M3-03 接 | :206-210 |
| 游戏性 | 字幕开关/字幕大小/教程提示频率 | 存值，apply=null，XML doc 注明消费方（字幕/教程系统后续切片） | :225-229 |
| 无障碍 | 色盲模式 关/红/绿/蓝 | 占位存值，XML doc 注明 R6 后处理接入 | :241-242 |
| 无障碍 | UI 缩放 Slider 0.8-1.2 | **实接**：按系数缩放 UIRoot 八层 CanvasScaler.referenceResolution（`CanvasScaler` 无直接乘系数，用 referenceResolution/scale 语义等价） | :270-276 |
| 无障碍 | 屏幕震动强度 Slider | 存值，XML doc 注明 M4 相机震动消费 | :247 |

**三按钮语义（`SettingsPanelPresenter.cs` 类 doc + 实现同文件）**：选项控件一改即 `Model.Set` 即时 apply（引擎状态立即生效）；**应用并关闭**=只负责 `Save()` 写盘+关面板（值早已生效，"应用"≠再 apply）；**撤销更改**=RevertToSnapshot（重放快照全部 apply，实时回滚）+刷新行，面板不关；**恢复默认**=ResetAll+刷新，面板不关，写盘仍需"应用并关闭"；**Esc=取消**：Revert+关，绝不写盘。

### 三、自测证据（Unity 批处理纪律：清锁/单实例确认/轮询 xml/taskkill）

- **EditMode 全绿**：`TempLogs/m3_02_editmode.xml` → `testcasecount="313" result="Passed" total="313" passed="313" failed="0" skipped="0"`（A-M3-01 基线 261 + 本任务 52 = 313；新增分布：Model 19 / Registry 14 / RowFactory 9 / PanelSmoke 10）。
- **ValidateFromCommandLine**：`ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` **exit 0**（PowerShell Start-Process -Wait 实测），`TempLogs/arch_report.txt` @11:16:50：asmdef 扫描 17、**违规数 0、结果 PASS**。
- **Setup 冒烟**：首轮 `SetupFromCommandLine` 生成 `ui_settings_panel.prefab` 并通过重载后 Validate（log `m3_02_setup.log:364` "[SettingsUiSetup] created ..."）；复跑幂等 **exit 0** 零异常（`m3_02_setup2.log`，no-change 路径静默返回——与 UISetup 先例一致）。
- 迭代中修复：3 处编译错（IList.ToArray 缺 using、Toggle 应为 `SetIsOnWithoutNotify`、Registry 缺 `UnityEngine.UI` using）、测试程序集 Newtonsoft 引用、SelectPage 编辑模式须 DestroyImmediate（同 UIManager DestroyObject 纪律）、2 处测试期望修正（Revert 全量重放/Load 计数口径）。
- 纪律记录：跑前确认无 Unity.exe+清 UnityLockfile；期间并行代理（B-R5-04）的 `ParityBench.cs` 曾短暂编译报错阻塞全项目（其自行修复，我方未动该文件）；其一次挂死残留进程（PID 15540，稳定 52MB 不退）按纪律 taskkill 后重跑；临时 ps1/log/xml 均在 TempLogs 留痕。

### 四、备注（给验收代理）

- 注入根目录：全部测试用 `Path.GetTempPath()/zframe_settings_*/` 临时根（TearDown 删除），真实 `Application.persistentDataPath` 零写入；运行时默认根仅 SettingsEntry/运行路径可达。
- 数据驱动实证口径：`SettingsPanelSmokeTests.SelectPage_RebuildsRows_PerRegistry` 尾段——BuildDefaults 视频页 6 定义 + 1 行新定义 = 7 行，布局代码零改动；`SettingsRowFactoryTests.Rows_PositionPurelyThroughLayoutGroups` 为零手摆的布局断言。
- `SettingsModel` 的 `CurrentValue` 是定义上的显示镜像，权威状态在 Model 的字典（`ApplyValue` 单一写路径）；验收时以 Model 为准。
- 输入走 legacy Input（项目 activeInputHandler=0，UIRoot StandaloneInputModule 同款）；InputSystem 迁移是 M4 既定缝隙。


## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 数据驱动实证：Registry 加一行定义即生成新行（读 RowFactory/Registry 行号+用例）；五页签定义分布断言用例
3. Model 语义：快照/Revert/ResetAll/Set 即时 apply 用例；JSON roundtrip+未知项容忍用例
4. 视频页实接核对（SetTier 调用行号）、音频页实接（SetBusVolume 行号）、渲染比例/超分占位 XML doc 注明
5. 注入根目录确认（无真实 settings.json 写入）
6. ValidateFromCommandLine exit 0（自跑）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**总判定: PASS**（验收代理仅做纯源码复核，未跑 Unity/构建；机器项引用前验收代理独立取证：**EditMode 313/313 独立复跑 @TempLogs/m3_02_acc_editmode.xml + 架构校验 违规数 0 / PASS @arch_report.txt（主控已核实）**；SetTier/SetBusVolume 实接行号已由前验收代理抽查通过）。

四项代码复核证据（本次独立核对）：

1. **注入根目录（清单 #5）— 确认无真实 persistentDataPath 写入**：`SettingsModelTests.cs:37`（`_root = Path.Combine(Path.GetTempPath(), "zframe_settings_tests_" + Guid...)`）、`SettingsPanelSmokeTests.cs:39`（`zframe_settings_panel_tests_` 临时根），两处 TearDown 递归删除（`SettingsModelTests.cs:41-48`、`SettingsPanelSmokeTests.cs:44-54`）；且有专门守卫用例 `SettingsModelTests.cs:173-178` `FilePath_LivesUnderInjectedRoot_NeverRealUserDirectory`（:175 StartsWith(TempPath)、:176 DoesNotContain(persistentDataPath)）。全部测试源码 grep `persistentDataPath` 仅命中上述断言，零运行时写入。
2. **JSON roundtrip + 未知项容忍 — 真实断言**：`SettingsModelTests.cs:198-213` `Load_Roundtrip_RestoresValues_AndAppliesAtBoot`（Save 后新 Model Load，:208 applied==3，:210-212 三值逐一回读）；`SettingsModelTests.cs:216-232` `Load_UnknownIds_AreToleratedAndSkipped`（:222-225 手工注入 `legacy.removedOption`/`future.newThing`，:229 applied==3 证明未知项被跳过、:231 未知 id 不入 Registry）；另有损坏文件 :235-243、错型只跳该项 :267-284、新版本忽略 :253-264，断言全部实质。
3. **数据驱动 Smoke（加一行定义→行数+1）— 真实存在**：`SettingsPanelSmokeTests.cs:106-143` `SelectPage_RebuildsRows_PerRegistry`——五页签行数逐页断言（:111 audio=4 / :114 controls=1 / :117 gameplay=3 / :120 accessibility=3 / :123 video=6），尾段 :127 Pop 后 :128-136 构造"BuildDefaults 视频页 6 定义 + `video.extra` 一行 Toggle"扩展 Registry 重开面板，:142 断言 `RowCount==7`（零布局代码改动）；零手摆布局断言另见 `SettingsRowFactoryTests.Rows_PositionPurelyThroughLayoutGroups`。
4. **三按钮/Esc 语义 — 实现与 XML doc 一致**：`SettingsPanelPresenter.cs:10-24` 类 doc 明示"应用并关闭=Save+关（值已即时生效）/撤销=RevertToSnapshot+刷新面板不关/恢复默认=ResetAll 面板不关/Esc=取消 Revert+关绝不写盘"；实现逐条对应——即时应用 :73-91（OnSettingChanged→Model.Set）、应用 :93-98（:96 Save+Close）、撤销 :100-104（Revert+RefreshRows，不关）、恢复默认 :106-110、Esc :112-117（:115 RevertToSnapshot，**无 Save**）；快照在 attach :53，InputBlocker Acquire :57 / OnDetach 释放 :68。Esc 轮询 `SettingsPanelView.cs:187-194`（:190 `Input.GetKeyDown(KeyCode.Escape)` → :192 `RequestClose()`，RequestClose 定义 :173）。测试侧三语义均有对应用例：`SettingsPanelSmokeTests.cs:161-175`（应用=写盘+关，:229-... Esc 用例 :212-231 断言 :229 `File.Exists==false` 绝不写盘）、:178-193（撤销面板保持打开 :191）、:196-207（恢复默认面板不关 :206）。

补充：抽查 `SettingsRegistry.cs:151`（`PipelineManager.SetTier((QualityTier)(int)value)`）、:194（`audio.SetBusVolume(bus, (float)value)`，Services.TryGet 防御 :192-195）、超分占位 XML doc :153-156 注明 B-R6 接 UpscalerChain、apply:null :159——与施工报告明细表一致。
