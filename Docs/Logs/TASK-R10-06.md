# TASK-R10-06 —— 修复审计 P2 C# 批（A-9 相机切换 requiresDepthOption 永不还原）

- **轨道**：A / R10
- **状态**：doing
- **派发**：主控，2026-10-29（依 R10-02 审计 A-9，验收复核成立、置信度高）
- **基线**：EditMode **715 用例** / PlayMode 7，只增不减

## 任务简报（主控写）

### 缺陷（A-9，已审计确认）

`ZrFrameGenerationFeature.cs:172-183` + `ZrFrameGenerationQualityOverrides.BindCamera`：FG 开启时活动相机切换（过场→玩家、相机销毁重建、多相机叠加）直接覆盖 `_camera` 而不还原旧相机的 `requiresDepthOption` → 旧相机永久停在 On（URP 每帧多渲一遍深度）；`RestoreQualityOverrides` 只还原最后绑定的相机，关 FG 也救不回来，需重启进程。

### 修复要求

1. `BindCamera` 换目标前**先还原旧目标**（记录旧相机引用→Restore 它→再绑新相机并记原值）；相机被销毁（Unity fake-null）时安全跳过还原并清引用
2. `Restore` 路径支持**栈/表式多相机**（最小实现：维护"已改写相机→原值"字典，Restore 全量还原；语义 XML doc 写清）
3. FG 关闭路径复用同一 Restore（消灭"关掉 FG 也救不回来"）
4. 多相机叠加场景语义：同时绑多个相机时每个都记录原值，互不覆盖——实现自定但需测试覆盖

### 测试要求（修前必红、修后必绿）

- EditMode：两个桩相机切换→旧相机 requiresDepthOption 还原（修前红）；三相机叠加→各自还原；相机销毁（fake-null）→不抛且清引用；FG 关闭→全量还原
- 既有 B-R9-08 单相机用例零改动通过

### 约束

- 只动 `UnityProject/Assets/ZRender/Runtime/`（相关文件）、`UnityProject/Assets/Game/Tests/`（或既有测试集）、本任务日志
- 基线只增不减；UTF-8 无 BOM/LF；架构校验器 exit 0；不 git commit
- 自测：EditMode 全绿+Validate exit 0（Unity 批处理纪律照 HANDOFF）

## 施工报告（施工代理写）

- **施工**：施工代理，2026-10-30
- **改动文件（3 个，全部在允许范围内）**：
  1. `UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationQualityOverrides.cs`（改）
  2. `UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs`（改，仅注释/XML doc；调用语义随 ① 变化）
  3. `UnityProject/Assets/ZRender/Tests/ZrFrameGenerationCameraSwitchTests.cs`（新增，4 例）
  - `git status --short` 只有上列文件（+ Unity 自动生成的 `.meta`）。既有 `ZrFrameGenerationSettingsTests.cs` /
    `AssemblySeamVisibilityTests.cs` **零改动**（`git diff --stat` 为空，见 §5）。

### 1. 四条修复的落点

**① 换目标前先还原旧目标（+ 相机销毁安全跳过）**

- `ZrFrameGenerationQualityOverrides.BindCamera`，`ZrFrameGenerationQualityOverrides.cs:182-203`：
  ```csharp
  if (!ReferenceEquals(camera, _camera))
  {
      RestoreCamera(_camera);   // 先把上一台还原成它自己的枚举原值，再换绑
      _camera = camera;
  }
  ...
  if (!_cameraOriginals.TryGetValue(camera, out CameraOverrideOption recorded))
  {
      recorded = camera.RequiresDepthOption;   // 同一目标重复绑定时沿用第一次记下的原值
      _cameraOriginals.Add(camera, recorded);
  }
  camera.RequiresDepthOption = CameraOverrideOption.On;
  ```
- **fake-null 安全**：还原写入走 `IZrFrameGenerationCameraDepthTarget`，真实现
  `UrpFrameGenerationCameraDepthTarget.RequiresDepthOption`（`ZrFrameGenerationQualityOverrides.cs:93-105`）
  的 getter/setter 都被 `_additional != null` 守卫（Unity 的 `==` 重载对已销毁对象返回 true=空），
  所以目标被销毁时写入**退化为空操作**，不抛 `MissingReferenceException`；条目照样从表里移除
  （`RestoreCamera`，`:269-284`）。上层 `OnCameraRendered` 的 `camera == null` 早退保证不会把死相机绑进表。
  - 诚实边界：**没有**额外引入 `IsAlive` 接口 —— 该守卫已由真实现的 null 检查提供，加接口只会在测试里
    多一层"模拟 fake-null"，对生产零收益。用例 3 用真 `Camera` + `DestroyImmediate` 钉住这条路径。

**② Restore 支持表式多相机（全量还原）**

- 表：`_cameraOriginals`（`目标 → 枚举原值`），`ZrFrameGenerationQualityOverrides.cs:137-138`；
  语义写进类级 XML doc `:109-121`（两条不变量：换目标即还原、Restore 全表 + 每条目标只记一次原值）。
- `Restore()`，`:224-251`：先取键快照 `:238-239`（`RestoreCamera` 会边还原边出表，不能就地枚举），
  逐条 `RestoreCamera`；末尾 `_camera = null`。不再存在"只还原最后绑定的那一台"。
- `Apply()`，`:206-217`：再开时对**表里每一台**重新置 On（幂等）。
- `RestoreCamera`，`:269-284`：只写"确实记过原值"的目标；写回后移除条目，返回是否真的还原过。

**③ FG 关闭路径复用同一个 Restore**

- `ZrFrameGenerationRuntime.RestoreQualityOverrides()`，`ZrFrameGenerationFeature.cs:376-379`：
  仍然只是 `s_preparedCamera = null` + `s_quality.Restore()` —— 没有第二套还原实现，
  变的只是 `Restore()` 现在覆盖全表（②），"关掉 FG 也救不回来、必须重启"随之消失。

**④ 多相机叠加语义**

- 语义（写进 `BindCamera` / 类级 XML doc）：**每个被改写过的目标各占一条原值、互不覆盖；
  换目标时旧目标当场还原并出表；关插帧时 Restore 全表**。因此"叠加相机"不会出现
  "后绑定的相机把先绑定的原值踩掉"或"先绑的永远留在 On"。
- 生产侧入口不变（`ZrFrameGenerationFeature.cs:175-184`），只补了 A-9 的注释。

### 2. "修前红 / 修后绿"的两次实跑证据

全部经 `Tools/run_tests.ps1`（不裸调 Unity），跑前 `unset` 大小写代理 + 确认无残留 Unity.exe。

| 轮次 | 代码状态 | 结果 | 证据 |
|---|---|---|---|
| 修前 | 只有新用例、产品代码未动 | **FAIL** `total=719 passed=715 failed=4`，既有 715 全绿 | `TempLogs/r10_06_red_editmode.xml` |
| 修后 | 四条修复落地 | **PASS** `total=719 passed=719 failed=0` | `TempLogs/r10_06_green_editmode.xml` |
| 变异复做（V1） | 临时注掉 `RestoreCamera(_camera);` | **FAIL** `total=719 passed=716 failed=3` | `TempLogs/r10_06_mutation_red.xml` |

修前 4 条红的具体断言消息（解析自 results.xml）：

```
SwitchingToAnotherCamera_RestoresThePreviousCamerasDepthOption
  | 换相机后旧相机必须回到枚举原值（A-9：修前它永久停在 On，URP 每帧多渲一遍深度）
ThreeCamerasInARow_EachKeepsItsOwnOriginalValue_AndCloseRestoresThemAll
  | A 的原值是 Off：换到 B 之后必须回到 Off
DestroyedCamera_IsSkippedWithoutThrowing_AndDoesNotStallTheNextSwitch
  | 死相机不挡住还原链：换到 C 后 B 必须回到自己的原值（修前红：停在 On）
RebindingTheSameTarget_DoesNotRecordTheOverwrittenValueAsTheOriginal
  | 重复绑定同一目标时原值只能记一次（第二次看到的是自己写上去的 On，不能当原值）
```

变异复做红的是 3 条（`RebindingTheSameTarget_...` 与"换目标即还原"正交，只由 `RestoreCamera`+去重记录
把握，故不受该变异影响）—— 说明新用例真的挂在修复点上，不是摆设。

### 3. 多相机语义说明（写清"为什么这样做"）

- **换目标 = 立刻还原旧目标**。理由：A-9 的成本是"旧相机永久停在 On，URP 每帧多渲一遍深度"，
  只有当场还原才能把成本限制在"当前正在渲染、确实要给 FG 供深度的那一台"。
- **关插帧 = 全表还原**。表里剩的只可能是"还原被跳过/失败"的条目（例如相机已销毁），
  一并处理，保证不残留、不需要重启。
- **同一目标重复绑定不重记原值**：生产路径每次换相机都会 `new UrpFrameGenerationCameraDepthTarget(...)`
  （引用不同 → 走"换目标"分支，先还原再记，记到的仍是真实原值）；直接对同一个目标对象重复 `BindCamera`
  时才走"沿用首次原值"分支 —— 后者由用例 4 钉住（旧实现会把 `On` 记成原值）。
- **已知边界（叠加相机逐帧交替）**：base + overlay 这种"每帧两次换目标"的用法，会让被换出的那台
  立即回到自己的原值。若该相机原值是显式 `Off`，它下一帧不产深度、FG 不为它提交；原值为
  `UsePipelineSettings`（叠加相机的默认档）时不受影响 —— 因为插帧期间管线副本的
  `supportsCameraDepthTexture` 恒为 true（`BindPipeline` 的覆盖），URP 照样产深度。
  若真机需要"叠加相机都覆盖"的窗口语义，表结构已就位（把"换目标即还原"改成"同帧窗口内都保持 On、
  关插帧再全量还原"即可，改动只在 `BindCamera` 一个分支），本任务按简报的字面要求取前者。

### 4. 测试与产出

- 新增 `UnityProject/Assets/ZRender/Tests/ZrFrameGenerationCameraSwitchTests.cs`（4 例），
  **全部走生产入口**（`ZrFrameGenerationRuntime.OnCameraRendered` / `SetSubmissionEnabled`），
  用真 `Camera` + `UniversalAdditionalCameraData` 断言字段值（不是标志位）；用例 4 直接对
  `ZrFrameGenerationQualityOverrides` 做单元断言。`TearDown` 里"先关（走一遍还原）→ 清目标 → 销毁相机"。
- 用例数：EditMode **715 → 719（+4，零删）**；PlayMode 7 → 7。
- 收尾六段（最终代码，完整跑）：native 251 用例 / 711,498 断言 / 0 失败；**EditMode 719/719**；
  **PlayMode 7/7**；arch / config / naming **exit 0** → `ALL PASS (6 passed, 0 skipped, 0 failed)`，
  报告 `TestReport.txt`。`native` 段 stderr 有一条 `cmake_probe_tmp: Permission denied`（构建脚本自身的
  cmake 探测子步骤，build + doctest 均 exit 0，不影响判定）。
- 文件规范：三个改动文件均 UTF-8 无 BOM / LF / 末尾单换行 / 无制表符（脚本核对）。
- **不 git commit**（未提交，工作区留 M + ?? 供验收核对）。

### 5. 诚实边界与不确定项

1. **EditMode 下没有真 URP 帧循环**：用例断言的是"字段被正确改写/还原"，不是"URP 真的少渲一遍深度"。
   后者需要真机 Profiler（本任务无 PlayMode 侧要求，未新增真机证据）。
2. **叠加相机的"逐帧交替换目标"只在 EditMode 里按顺序调 `OnCameraRendered` 模拟**，没有真 URP 叠加
   渲染的帧序证据（见 §3 末段的边界说明）。
3. **fake-null 的"安全跳过"是间接实现的**（真实现的 `_additional != null` 守卫 + 条目出表），
   没有独立布尔表示"我跳过了它"；用例只能断言"不抛 + 后续还原链正常 + 不残留"。
4. **`Restore()` 报的 `restored` 语义**：目标已销毁时 `RestoreCamera` 返回 true（条目被清掉、确实"处理过"），
   所以关插帧仍会打一条"已还原"日志 —— 日志措辞未改（避免动 `ZrFrameGenerationFeature` 的日志面）。
5. 基线数字以本任务实跑为准：修前 `total=719 passed=715`（= 715 基线 + 4 新例全红），说明简报给的
   715 基线在本机成立。
6. **工作区里还有一条非本任务改动**：`UnityProject/Assets/Plugins/ZRender/zrender.dll` 被收尾六段的
   `native` 段（`build_native.ps1` 的部署步骤）重写（同尺寸、字节不同）。native 源码本任务一行未动，
   这是强制跑全量测试的必然产物；未做 `git checkout` 回退（避免动构建产物状态），验收时按"构建产物"看待。

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；修前必红复做一次（临时回退还原逻辑→新用例红→恢复）
- [ ] V2 多相机字典/还原路径审查（行号）；fake-null 安全
- [ ] V3 FG 关闭全量还原用例；既有单相机用例零改动通过（git diff 核对）
- [ ] V4 Validate exit 0（自跑）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**结论: PASS with 受限（6/6）** —— A-9 的行为缺陷（换相机后旧相机永久 On、关 FG 救不回来）在代码与实跑上均消除；
受限点只涉及"取证口径"（叠加相机真机帧序未验 + 表式"多条目同时存在"在生产路径结构上不可达），不影响判定。

- **验收代理**：独立实跑（2026-10-30 10:26–10:35），未复用施工给的三份 XML 数字；全部经 `Tools/run_tests.ps1`，
  每次跑前 `unset` 大小写代理变量（`http_proxy/https_proxy/all_proxy/HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/no_proxy/NO_PROXY`），
  跑前确认无残留 `Unity.exe`；未裸调 Unity；未 `rm`；未 commit。

### V1 EditMode 全绿 + 修前必红复做 —— 达成

- 绿（最终代码，实跑 4 次）：`total=719 passed=719 failed=0`（5.477s / 5.151s / 5.467s / 4.011s），
  `ALL PASS`，`ACCEPT_SCRIPT_EXIT=0`。证据：`TempLogs/accept_r1006_editmode_green.xml`、
  `TempLogs/accept_r1006_editmode_green_full.xml`（六段跑内）。
- **修前必红（本代理亲自复做）**：把 `ZrFrameGenerationQualityOverrides.cs:186` 的 `RestoreCamera(_camera);`
  临时注释 → EditMode **FAIL `total=719 passed=716 failed=3`**（Unity exit=2）：
  - `SwitchingToAnotherCamera_RestoresThePreviousCamerasDepthOption` ——
    `Expected: UsePipelineSettings / But was: On`（正是 A-9 的现场）
  - `ThreeCamerasInARow_EachKeepsItsOwnOriginalValue_AndCloseRestoresThemAll`
  - `DestroyedCamera_IsSkippedWithoutThrowing_AndDoesNotStallTheNextSwitch`
  证据：`TempLogs/accept_r1006_mutation_red.xml`。
- 复原：从跑前备份 `TempLogs/accept_r1006_fixed_quality.cs` 回写，`sha256 = ae4a4a34…21d1a5` 与备份一致，
  `grep -r ACCEPT-MUTATION` 无残留，`git diff --stat` 与变异前快照一致；复原后再跑 EditMode **719/719 绿**。
- 附加变异（本代理自加，验证"关时还原"不是空转）：把 `Restore()` 的相机表分支临时短路 →
  **5 红**：新用例 2/3/4 + **既有** `Disable_RestoresMsaaSampleCountAndCameraDepthOption_ToTheValuesCapturedBeforeEnable`、
  `Restore_KeepsTheDepthOptionEnum_ThatTheBoolProjectionWouldLose`；同样已复原（哈希同上）。
  证据：`TempLogs/accept_r1006_mutation2_close_red.xml`。

### V2 多相机字典/还原路径审查 + fake-null 安全 —— 达成

- 表与活动指针：`ZrFrameGenerationQualityOverrides.cs:136`（`_camera`）/ `:137-138`（`_cameraOriginals`）。
- `BindCamera` `:182-203`：`:184` 引用比较 → `:186` **先还原旧目标** → `:187` 换绑；`:190-193` null 早退；
  `:195-199` 同一目标只记一次原值（`TryGetValue` 去重，避免把自写的 On 当原值）；`:202` 置 On。
- `Restore` `:224-251`：`:228-233` 管线还原；`:238-239` **键快照**（`RestoreCamera` 边还原边出表，不能就地枚举）；
  `:240-246` 逐条还原；`:249` `_camera = null`。
- `RestoreCamera` `:269-284`：`:271-274` null 早退；`:276-279` 未记过的目标不写；`:281` 先出表、`:282` 再写回 —— 幂等且不残留。
- `Apply` `:206-217`：再开时对表内每台重新置 On（幂等）。
- fake-null：真实现 `UrpFrameGenerationCameraDepthTarget` 的 getter `:96-98` / setter `:101-104` 都用
  `_additional != null` 守卫（Unity 对已销毁对象重载 `==` 返回 true=空）→ 写退化为空操作，不抛 `MissingReferenceException`；
  入口侧 `ZrFrameGenerationFeature.cs:163` 的 `camera == null` 早退拦掉死相机入表。
- 审查发现（不影响判定）：生产路径上 `BindCamera` 必先还原旧目标，故 `_cameraOriginals` **可达规模恒为 ≤1 条**，
  `Restore` 的"全表"分支实际只处理 1 条 —— 语义正确，但"多相机字典"在当前语义下是结构性冗余。

### V3 FG 关闭全量还原用例；既有单相机用例零改动 —— 达成

- FG 关闭路径复用同一 `Restore`：`ZrFrameGenerationFeature.cs:376-386`（`s_preparedCamera = null` + `s_quality.Restore()`，
  无第二套实现）。
- 关后逐台断言：`ZrFrameGenerationCameraSwitchTests.cs:98-104`（关 FG 后 A=Off、B=On、C=UsePipelineSettings 分别断言，
  不是只看最后一台）；`:134-136`（fake-null 场景关 FG 不抛且第三台回原值）。
- 既有单相机用例零改动：`git diff --stat -- .../Tests/ZrFrameGenerationSettingsTests.cs .../AssemblySeamVisibilityTests.cs`
  **为空**；`git status` 的 Tests 目录只有新增的 `ZrFrameGenerationCameraSwitchTests.cs`(+`.meta`)；
  且既有 `Disable_Restores…` / `Restore_KeepsTheDepthOptionEnum…` 在变异二下变红 → 基线用例仍在守关路径。

### V4 多相机语义 / Restore 幂等 / 销毁二次保护 —— 达成（含口径限制）

- **互不覆盖**：用例 2（`:79-105`）A=Off / B=On / C=UsePipelineSettings，`:90` 断言 B 绑定后 A 回到 **Off**（不是 On），
  `:94-96` 断言 A/B 原值不被 C 覆盖 → 各记各的原值、互不踩。
- **同时多绑**：结构上不可达（`BindCamera` 换绑即还原旧的），因此"同时叠多台"不被实现；符合简报第 1 条"换目标前先还原旧目标"的字面要求。
- **幂等**：`Restore()` 二次调用 —— `:235` 表已空则跳过，`:228-233` 管线只重写回同一批原值，无计数器/无栈弹出，不会"越界/重复扣"；
  观感副作用仅是返回值仍为 true（`ZrFrameGenerationFeature.cs:379-385` 会再打一条"还原"日志，值无语义错误）。
  （此项为**静态审查**结论，未新增专门用例 —— 本代理无权加测试。）
- **销毁时的二次保护**：还原写回前先出表（`:281`），再经 `:101-104` 的 null 守卫退化为空操作 —— 保护在 **Restore 侧**而不只在绑定时；
  用例 3 已真实走这条写路径（`DestroyedCamera…:120-126`，`DestroyImmediate` 后换绑，`DoesNotThrow` 通过）。

### V5 "关 FG 也救不回来"是否消除 —— 已消除

- 用例 2 在 `SetSubmissionEnabled(false)` 之后对 **三台**逐一断言（`:101-104`）：A=Off、B=On、C=UsePipelineSettings；
  用例 3 关 FG 后断言第三台回 `UsePipelineSettings`（`:136`）。三台都在关后被检查，不是只看最后一台。
- 反向证明：变异二（关时还原失效）下用例 2/3 立刻变红（见 V1 附加变异），说明这些关后断言真的挂在还原路径上。
- 口径说明：关 FG 瞬间表内实际只剩"当前活动相机"1 条（前几台在换绑时已被还原），所以"每台都回原值"由
  **"换绑即还原" + "关时还原当前台"** 两段合成；"表内多条目一次性全量还原"在可达路径上无法被触发。

### V6 六段不回归 —— 达成

- `Tools/run_tests.ps1`（全六段）连跑两次，均 `ALL PASS (6 passed, 0 skipped, 0 failed)`，`ACCEPT_SCRIPT_EXIT=0`：
  - native **cases=251 / asserts=711,498 / failed=0**（build_native exit 0）
  - EditMode **total=719 passed=719 failed=0**（= 715 基线 + 4 新例）
  - PlayMode **total=7 passed=7 failed=0**
  - arch / config / naming 三校验器 **exit=0**
  证据：`TestReport.txt`、`TempLogs/accept_r1006_report_full.txt`、`TempLogs/accept_r1006_playmode_green.xml`、
  `TempLogs/accept_r1006_native.log`。
- 文件规范：三个 `.cs` 首字节 `2f 2f 20`（无 `EF BB BF`）、无 tab、无行尾空格；`git diff` 的新增行无 CR
  （`.gitattributes: * text=auto eol=lf`，入库为 LF；工作区被 `core.autocrlf=true` 检出为 CRLF，属环境行为）。
- 未 commit：`git log -1` = `6157fbd … R10-05 验收闭环`，R10-06 无新提交；工作区仅 2 个 M（源码）+ 1 个 M（本日志）
  + 1 个 M（`zrender.dll`，native 段部署产物）+ 2 个 `??`（新用例 + `.meta`）。

### 施工报告核对（有无夸大）

- 数字全部可复现：修前 `719/715`、修后 `719/719`、变异 `719/716/3`、native 251/711,498、PlayMode 7 —— 与本代理实跑一致。
- 唯一表述放大：把"表式多相机 / Restore 全表"说成通用多相机能力，而可达路径上表恒 ≤1 条（见 V2 审查发现）；
  属描述层面放大，不构成行为缺陷。
- 遗留：`TempLogs/r10_06_quality_fixed.cs.bak`、`r10_06_*.xml` 为施工自留证据，本代理未依赖。

### 剩余风险

1. **叠加相机逐帧交替（施工 §3 已披露）**：base + overlay 每帧两次换目标 → 被换出的那台立刻回原值；原值为 `Off` 的叠加相机
   在自己那一帧之外不产深度，可能让 FG 拿到上一台的全局 `_CameraDepthTexture`。EditMode 只断言字段值，真机 URP 帧序未验。
2. **"全量还原 >1 条"不可达**：见 V5 口径说明 —— 若将来出现"同帧保持多台 On"的需求，需改 `BindCamera` 分支，现有用例不会覆盖。
3. **Restore 重复调用会重复打日志**（返回值语义 = "有管线记录即 true"）；值正确，仅日志噪声。
4. `zrender.dll` 的 M 是强制跑 native 段的部署产物（native 源码零改动），非本任务代码改动。
