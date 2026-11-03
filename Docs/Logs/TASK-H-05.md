# TASK-H-05 —— 修 H-03 / H-04 验收发现项（返工）

> 施工代理按本简报返工，写「施工报告」到第二节。**禁止 git commit**。
> 返工后由**新的验收代理**复核（不可能与本次施工同代理）。

## 任务简报（主控撰写）

### 背景

H-02/H-01 施工后，H-03（原生+工具链）判 **FAIL 1 项**、H-04（C#）判 **PASS with 受限** 并列出若干契约与弱断言问题。
本任务只修验收指出的项，**不新开方向、不扩大范围**。

### 必修（验收 FAIL / 契约不一致）

**F1 — Vulkan 描述符分配失败仍然静默（H-03 FIX-1，阻塞项）**

- 位置：`Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp:422`（`++m_descriptorAllocFailures`）、
  `Native/zrhi/vulkan/zrhi_vulkan_commandlist.h:130`（字段声明）
- 问题：该计数器全仓库**只有声明与自增，没有任何读取方**（无 getter、无 stats 字段、无日志）。
  而 `Native/build/zrhi.vcxproj:136` 定义了 `NDEBUG`，`assert` 在 Release 被编译掉——池耗尽时行为
  与修复前**完全等价**，代码注释却自称 "make the failure observable"，属把推测写成结论。
- 期望：真正可观测。推荐复用 H-02 刚建立的模式（`ZrD3D12PresentStatus` → `IZrDevice::GetDeviceStats`
  尾部追加字段）：把 Vulkan 命令表的分配失败计数**累加到设备级 `ZrDeviceStats`**，由 `GetDeviceStats`
  读出。D3D12 侧同字段填 0（或以既有语义填值）。库内不 `printf`、不 `abort`。
- **同时**加 ABI 护栏：`ZrDeviceStats` 现有"尾部追加"做法没有 `static_assert` 保护，请补
  `static_assert(sizeof(ZrDeviceStats) == 期望值, ...)` 或等价护栏，避免下次追加时静默改变布局。
  注意 H-02 已追加过 3 个字段，护栏值以**当前实际大小**为准（先测量再写死）。
- 验收要能证明：`GetDeviceStats` 能读出该计数；有 doctest 覆盖。

**F2 — 契约注释与新行为不符（H-04 中-1/中-2）**

- `UnityProject/Assets/ZFrame/Data/Save/SaveSystem.cs:20-24`：类注释仍写
  「load returns null … next load falls back to .bak1」，实际已改为**同一次调用内**降级。
- `UnityProject/Assets/ZFrame/Data/Save/ISaveSystem.cs:28-33`、`:36`：仍写
  「Returns null … after a corrupt-save quarantine」「Null when the slot has no main save file」，
  实际主档缺失/损坏都会降级到备份。
- 期望：把三处注释改成与当前实现一致（同调用降级、任一候选可读即返回、全部候选不可用才 null）。
  **只改注释，不改逻辑**。

**F3 — `RebindService` 的 Reset 路径仍裸调 `Save()`（H-04 低-7，与 D1 同类）**

- 位置：`UnityProject/Assets/ZFrame/View/Input/RebindService.cs:320`、`:349`、`:361`
  （`ResetBinding` / `ResetAll` 等）
- 问题：写盘失败会抛进按钮回调（H-01 只保护了 `CompleteCaptured` / `StartCapture`）。
- 期望：与 D1 同策——捕获后 `ZLog.Error("Input", ...)`，不打穿到 UI；若这些方法有返回值可表达
  失败则用之，否则至少不抛。**行为不要改变成功的路径**。

### 应修（验收观察项，成本低）

**F4 — Vulkan 池容量与 maxSets 不自洽（H-03 观察 1）**

- `rootCbvCount=5` 时 512 个 set 需 2560 个 `UNIFORM_BUFFER`，当前只声明 2048（最多支撑 409 set）。
- 期望：使 `maxSets × 每 set 最大用量` ≤ 各 type 容量。建议把 uniform 提到 2560（或等价调整），
  并在注释里写明「容量 = maxSets × 每 set 上界」的推导，避免下次再错。

**F5 — Vulkan bindless 槽未检查 `ZR_SLOT_INVALID`（H-03 观察 2）**

- 位置：`Native/zrhi/vulkan/zrhi_vulkan_device.cpp:780` 附近
- 期望：与 D3D12 同策加检查与失败返回（`WriteBindlessDescriptor` 有边界兜底，不会越界，但失败静默）。

**F6 — 两处注释/断言收紧（H-03 观察 3、4）**

- `Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp:79` 注释仍写 `maxSets=128`，实际 512 → 改对。
- D1 的 doctest 只断言 `rc < 0`，收紧为 `== -3`（新增返回码），提高判别性。

### 记录不修（本任务范围外，主控已记入看板备注）

- H-04 低-4/低-5 的弱断言（`SettingsModelTests.cs:298/:354`、`RebindServiceTests.cs:377-379`）
- H-04 低-8 UIManager 回滚未调 `OnDestroy`（当前无实际泄漏）
- H-04 低-9 静默加载旧备份（有事件+日志）
- H-04 低-10 `TryReadSlotInfo` 未捕 `UnauthorizedAccessException`（沿用旧实现）

### 环境纪律（**必读**）

1. 构建前必须 `unset http_proxy https_proxy all_proxy`（重复大小写代理变量让 MSBuild 抛「字典重复键」）
2. cmake 全路径：`CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
3. 构建：`cd Native && "$CM" --build build --config Release --parallel`
4. 测试：`cd Native/build/bin/Release && ./zrender_test.exe`。**当前基线：166 用例 / 705,052 断言 / 0 failed**
5. **PowerShell 工具无法执行外部 exe**，一律用 Bash
6. 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
7. **Unity 不可用**（UPM IPC / Bee cache-folder）。C# 改动**无法编译**，以静态审查 + 括号配平
   （`TempLogs/brace_check.py`，用 `C:/Users/1/.workbuddy/binaries/python/versions/3.13.12/python3`）
   + Grep 核对 API 签名为准，并在报告中如实标注未编译。

### 交付物

1. F1–F6 修改 + 对应测试（F1 必须有 doctest；F2/F3 若有 C# 测试价值则补，否则说明原因）
2. 本文档第二节「施工报告」：文件:行号、改法、**实测构建/测试数字**（用例数与断言数写实际值）、未验证项
3. 不改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md`

---

## 施工报告（施工代理填写）

> 施工代理：本机（Windows 11 / MSVC 2022 BuildTools / AMD Radeon 780M，Vulkan 1.4）。
> 全部构建/测试均在 `unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY` 之后执行。
> **未 git commit**；未改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md`；未触碰「记录不修」4 项。
> **C# 未编译**（Unity 本机不可用），只做静态审查，见 §5。

### 1. F1 —— Vulkan 描述符分配失败真正可观测（阻塞项）

**设计：单一写入点 → 单一读出点，删掉那个没人读的私有计数器。**

| 文件:行号 | 改法 |
|---|---|
| `Native/zrhi/zrhi_device.h:227` | `ZrDeviceStats` **尾部追加** `uint32_t descriptorSetAllocFailureCount`（既有 6 字段偏移不变；库无 C ABI 导出该结构，见 §1.4） |
| `Native/zrhi/vulkan/zrhi_vulkan_device.h:281-288` | 新增公开内部扩展点 `VkDescriptorSet AllocateRootDescriptorSet(VkDescriptorPool, VkDescriptorSetLayout)`——命令表 `RebindDescriptorSet` 的**唯一**分配点 |
| `Native/zrhi/vulkan/zrhi_vulkan_device.h:334-336` | 私有成员 `uint32_t m_descriptorSetAllocFailures`（写点唯一） |
| `Native/zrhi/vulkan/zrhi_vulkan_device.cpp:695-711` | 实现：`vkAllocateDescriptorSets` 失败 → `m_descriptorSetAllocFailures += 1` + 返回 `VK_NULL_HANDLE`（不 printf、不 abort，与 H-02 Present 失败同策） |
| `Native/zrhi/vulkan/zrhi_vulkan_device.cpp:688-691` | `GetDeviceStats()` 填 `stats.descriptorSetAllocFailureCount = m_descriptorSetAllocFailures`（读点唯一） |
| `Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp:417-430` | `RebindDescriptorSet` 改调 `m_owner->AllocateRootDescriptorSet(...)`；失败即 `assert`（Debug）+ 带原因注释的 `return`（Release 的唯一行为：跳过重绑） |
| `Native/zrhi/vulkan/zrhi_vulkan_commandlist.h:126-131` | **删除** `m_descriptorAllocFailures` 字段，原位留注释说明"计数已上移到设备级，保留命令表私有计数器就是保留假的可观测性" |
| `Native/zrhi/d3d12/zrhi_d3d12_device.cpp:388-392` | D3D12 侧同字段**显式填 0** + 注释说明本后端无"池耗尽后静默跳过重绑"这一形态（D3D12 槽耗尽在 `Create*` 处即返回空句柄），故 0 是"无来源"而非"漏填" |
| `Native/zrhi/zrhi_device.h:230-247` | ABI 布局护栏：`static_assert(sizeof(ZrDeviceStats) == 28)` + 逐字段 `offsetof` 7 条（0/4/8/12/16/20/24），尾字段断言写明"必须是尾部追加" |

**1.1 观测链路（谁写 / 谁读 / 怎么证明）**

```
池耗尽
  → ZrVulkanCommandList::RebindDescriptorSet  (zrhi_vulkan_commandlist.cpp:423)
      m_owner->AllocateRootDescriptorSet(m_descriptorPool, obj->setLayout)
  → ZrVulkanDevice::AllocateRootDescriptorSet  (zrhi_vulkan_device.cpp:695)
      vkAllocateDescriptorSets != VK_SUCCESS
      → m_descriptorSetAllocFailures += 1        ← 唯一写点
      → return VK_NULL_HANDLE
  → 命令表跳过本次重绑（assert 仅 Debug；Release 不改变控制流）
```
读：
```
上层（窗口程序/引擎）IZrDevice* rhi
  → rhi->GetDeviceStats()                     (zrhi_vulkan_device.cpp:682)
      stats.descriptorSetAllocFailureCount = m_descriptorSetAllocFailures  ← 唯一读点
```
> 与 H-02 的 `ZrD3D12PresentStatus → GetDeviceStats` 同构：状态累加在设备内部，
> 经同一个 `IZrDevice::GetDeviceStats()` 出口上报，库内不打印、不中止。

**1.2 计数器的写入/读出点已用 grep 全仓核对**（`grep -rn "m_descriptorAllocFailures|descriptorSetAllocFailureCount|AllocateRootDescriptorSet|m_descriptorSetAllocFailures" Native`）：
旧字段 0 命中（已删除）；新字段命中 = 声明 1 处 + 写 1 处（`:708`）+ 读 1 处（`:691`）+ D3D12 填 0 1 处 + 护栏/测试若干。**不存在只增没人读的计数器。**

**1.3 新增 doctest（`Native/tests/test_zrhi_device_stats.cpp`，2 用例 / 25 断言）**

- 用例 1「布局护栏」：`sizeof==28`、7 条 `offsetof`、默认值 0 —— 与头文件 `static_assert` 同源，但让"护栏值 == 实际布局"出现在测试输出里。
- 用例 2「Vulkan 描述符集分配失败经 `GetDeviceStats` 可观测」：**真实**创建 `ZrVulkanDevice`（日志实证 `device created: AMD Radeon 780M Graphics (api 1.4)`），自建 `VkDescriptorSetLayout`(1×UNIFORM_BUFFER) + `VkDescriptorPool(maxSets=1)`，
  1) 第 1 次分配成功且计数仍 0；
  2) 第 2 次真实 `VK_ERROR_OUT_OF_POOL_MEMORY` → 返回 `VK_NULL_HANDLE`，`IZrDevice::GetDeviceStats().descriptorSetAllocFailureCount == 1`（**这就是 F1 的读链实证**），且 `aliasHeapCount/imageCount/bufferCount` 未被污染；
  3) 第 3 次再失败 → 计数 2（累计而非覆盖）；
  4) VK 后端不虚报 D3D12 专属的 `presentFailureCount`/`deviceRemoved`。
- **诚实边界**：该用例驱动的是设备级分配函数（= 命令表的唯一分配点），不是"PSO + 真实帧"的端到端路径——后者需要有效 SPIR-V 才能建 PSO，属测试环境限制。`RebindDescriptorSet → AllocateRootDescriptorSet` 这 1 行胶水只有代码审查证据（`zrhi_vulkan_commandlist.cpp:423`）。作为补强，另跑了真实双后端渲染（见 §4），池容量改动 + 分配路径重构后 `maxDelta=0` / `validation errors=0`，说明真实帧路径仍走通。

**1.4 为什么"尾部追加"安全（不是推测，已核）**
- 全仓 `ZrDeviceStats` 的**构造点**只有 3 处——`zrhi_d3d12_device.cpp:380`、`zrhi_vulkan_device.cpp:684`、`tests/rg_mock_device.h:281`，**均为默认构造 + 逐字段赋值**，无位置聚合初始化（`ZrDeviceStats{...}` 全仓 0 命中）→ 追加字段不破坏任何调用点；其余使用点（`test_rg_execute.cpp:86/175/176`、`zrhi_d3d12_window.cpp:9496/9538`）都是 `GetDeviceStats()` 的只读消费。
- `ZrDeviceStats` 未被 `zplugin/zrender.def` 或任何 C ABI 导出（`grep -rn "DeviceStats" zplugin zrhi/zrhi.h zrhi/zrhi.cpp` 无命中）→ 不构成 C ABI 变更。
- 护栏值不是猜的：用编译器诊断实测两次——
  改动前 `error C2079: "g_zrSizeProbe" 使用未定义的 struct "ZrSizeProbe<24>"` → **sizeof=24**；
  追加 F1 字段后 `... "ZrSizeProbe2<28>"` → **sizeof=28**，随后写死 `static_assert(... == 28)`。
  （探针为临时语句，已从 `tests/test_zrhi_types.cpp` 还原，`git status` 中该文件无改动。）

### 2. F2 —— C# 契约注释与实现对齐（**只改注释，零逻辑改动**）

| 文件:行号 | 改法 |
|---|---|
| `UnityProject/Assets/ZFrame/Data/Save/SaveSystem.cs:20-28` | 删掉"load returns null，下次 load 才回落 .bak1"的旧描述；改为：失败候选不作为结果返回，**同一次调用**继续走 main→.bak1→.bak2，取第一个能读出/迁移/恢复的候选，三者皆不可用才 null；恢复**不依赖调用方重试** |
| `UnityProject/Assets/ZFrame/Data/Save/ISaveSystem.cs:28-38` | `LoadFromSlot`：删掉"after a corrupt-save quarantine 就返回 null"；写明同调用降级 + 缺失跳过 / 损坏则 quarantine + 事件后跳过 + 全部候选不可用才 null |
| `UnityProject/Assets/ZFrame/Data/Save/ISaveSystem.cs:39-44` | `GetSlotInfo`：删掉"Null when the slot has no main save file"；改为与 `LoadFromSlot` 同候选序（main→bak1→bak2，无 quarantine 副作用），仅当无候选文件存在或全部不可读才 null |

对照实现取证（未改逻辑）：`SaveSystem.cs:216-250` 固定三候选 `foreach`（缺失 `continue`、失败注释"try the next generation"、三者皆空 `return null`）；`SaveSystem.cs:253-283` 的 `GetSlotInfo` 同序走只读 `TryReadSlotInfo`；`SaveSystem.cs:345-357` 读失败记 Error + 事件并返回 null（不 quarantine），`:364-377` 解析/版本失败走 `Quarantine`。

### 3. F3 —— `RebindService` Reset 路径不再裸调 `Save()`

| 文件:行号 | 改法 |
|---|---|
| `UnityProject/Assets/ZFrame/View/Input/RebindService.cs:384-405` | 新增私有 `SaveAfterReset(string scope)`：`try { Save(); } catch (Exception e) { ZLog.Error("Input", ...) }` —— 与 D1（`CompleteCaptured`）同策，成功路径**原样**跑同一个 `Save()` |
| `UnityProject/Assets/ZFrame/View/Input/RebindService.cs:320` | `ResetBinding(actionKey)`：`Save()` → `SaveAfterReset($"'{actionKey}'")` |
| `UnityProject/Assets/ZFrame/View/Input/RebindService.cs:349` | `ResetBinding(actionKey, bindingIndex)`：同上（带 binding 序号便于定位） |
| `UnityProject/Assets/ZFrame/View/Input/RebindService.cs:361` | `ResetAll()`：`SaveAfterReset("the whole asset (恢复默认)")` |

- 这三个入口签名都是 `void`（`IBindingSettings` 契约，不改公共 ABI），无返回值可表达失败 → 采取"至少不抛 + Error 日志"；内存侧重置**不回滚**（与 D1 的既有取舍一致：玩家要的是恢复默认，磁盘旧文件保持原样，下一次成功保存会写全量快照自愈）。
- 复核：`grep -n "Save();" RebindService.cs` 只剩 2 处（`:399` 在 `SaveAfterReset` 的 try 内、`:433` 在 `CompleteCaptured` 的 try 内）→ 无裸调用残留。
- 新增 EditMode 用例 `UnityProject/Assets/ZFrame/Tests/Input/RebindServiceTests.cs:428`（`ResetPaths_WhenPersistFails_DoNotThrow_AndResetStaysInMemory`）：复用既有 `LockBindingsFile()`（`:323`，未改）制造真实写盘失败，逐条驱动**三个调用点** `ResetBinding(key,idx)` / `ResetBinding(key)` / `ResetAll()` 断言不抛 + 内存已重置 + 磁盘旧档未被截断。修复前这三条 `Assert.DoesNotThrow` 必红（IO 异常直接冒泡）。**未编译、未运行**（见 §5）。
- 未触碰「记录不修」：`RebindServiceTests.cs:377-379` 的弱断言原样保留。

### 4. F4 / F5 / F6

**F4 — 池容量与 maxSets 自洽**（`Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp:79-105`）
- `sizes[0].descriptorCount` 2048 → **2560**（= 512 × `rootCbvCount`≤5）；SAMPLED_IMAGE/SAMPLER 各 1024（= 512 × `srvTableCount`≤2，原本已自洽）。
- 注释写明推导：`maxSets × 每 set 上界 = 各 type 容量`，并记录旧值 2048 在 `rootCbvCount=5` 时只能开 409 个集合（把"容量不足"伪装成"分配失败"）。
- 实测兜底：`--draw/--shadows --backend both` 双后端仍 `maxDelta=0`、`validation errors=0`（§4 末）。

**F5 — Vulkan bindless 槽检查**（`Native/zrhi/vulkan/zrhi_vulkan_device.cpp:801-812`）
- `m_bindlessSlots.Allocate()` 后立即判 `ZR_SLOT_INVALID`：`assert`（Debug）+ `delete obj` + 返回空 `ZrImageView`，与 D3D12 侧 7 处同策；不再"视图创建成功却落在无效槽"。既有 `vkCreateImageView` 失败回收槽位的路径（`:813-819`）不变。

**F6 — 注释/断言收紧**
- `zrhi_vulkan_commandlist.cpp:79` 的 `maxSets=128`（过期）→ 随 F4 一并改为 512 并附容量推导。
- `Native/tests/test_cluster_lighting.cpp:519`：`CHECK(rc < 0)` → `CHECK(rc == -3)`；同类三条（`:533` dimY=45、`:546` 65536³ 回绕、`:557` dimZ=0）一并收紧为 `== -3`（实现 `zrender/cluster_lighting.cpp:152`（零维度）与 `:157`（超容量）对两者都 `return -3`）。`:470-483` 那组的 `< 0` 属 `-1`（超 32 灯/空指针/容量 0）语义，**未动**。

### 5. 实测构建与测试数字（亲眼看，非引用）

| 项 | 命令 | 实测输出 |
|---|---|---|
| 全量清理重建 | `cmake --build build --config Release --clean-first --parallel`（`TempLogs/h05_build.log`） | `EXIT=0`；`grep -ci warning` = **0**（`/W4 /WX` 零警告）；无 error |
| 全量单测 | `Native/build/bin/Release/zrender_test.exe`（`TempLogs/h05_test.log`） | `test cases: 168 \| 168 passed \| 0 failed \| 0 skipped`；`assertions: 705077 \| 705077 passed \| 0 failed` |
| 新用例隔离跑 | `zrender_test.exe -tc="*TASK-H-05*"` | `2 \| 2 passed \| 0 failed \| 166 skipped`；`assertions: 25 \| 25 passed` |
| 双后端绘制 | `zrhi_d3d12_window.exe --draw --backend both`（`TempLogs/h05_draw.log`） | 6 次 `CENTER PIXEL PARITY ... maxDelta=0`；`[vulkan] validation error count = 0`；`FIRST DRAW PARITY OK` |
| 双后端阴影 | `zrhi_d3d12_window.exe --shadows --backend both`（`TempLogs/h05_shadows.log`） | `SHADOW PROBE PARITY off delta=0 / on delta=0`；`validation errors=0`；`D3D12/VULKAN SHADOWS PARITY OK` |

与基线对比：**166 → 168 用例（+2）**、**705,052 → 705,077 断言（+25）**、`0 failed`。增量与新增用例的 `CHECK` 数逐一对得上（用例 1 = 11 条，用例 2 = 14 条，共 25），可证明两个新用例**确实执行到**（不是被跳过）。

### 6. 改动文件清单（仅这些）

原生（6 改 1 新）：
`Native/zrhi/zrhi_device.h`、`Native/zrhi/d3d12/zrhi_d3d12_device.cpp`、
`Native/zrhi/vulkan/zrhi_vulkan_device.h`、`Native/zrhi/vulkan/zrhi_vulkan_device.cpp`、
`Native/zrhi/vulkan/zrhi_vulkan_commandlist.h`、`Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp`、
`Native/tests/test_cluster_lighting.cpp`、`Native/CMakeLists.txt`（注册新测试 + 测试目标 `ZR_HAS_VULKAN=1`）、
**新增** `Native/tests/test_zrhi_device_stats.cpp`。

C#（4 改）：`UnityProject/Assets/ZFrame/Data/Save/SaveSystem.cs`、`ISaveSystem.cs`、
`UnityProject/Assets/ZFrame/View/Input/RebindService.cs`、`UnityProject/Assets/ZFrame/Tests/Input/RebindServiceTests.cs`。

`git status` 中其余改动（`Native/zrender/*`、`Native/zupscaler/*`、`Native/zrhi/d3d12/zrhi_d3d12_translate.*`、
`Tools/*.ps1`、`TASKS.md`、`UnityProject/.../GameHud.cs`、`SettingsModel.cs`、`UIManager.cs`、
`*Tests.cs`、新增 `Game/Tests/View/*` 等）**均为 H-01/H-02 遗留的未提交改动，本任务未触碰**
（`TASKS.md` 在我开工前即已 `M`）。未新增/修改任何 `.meta`、未产生入库构建产物。

### 7. 未验证项（如实）

1. **C# 全部未编译、未运行**（Unity 本机不可用：UPM IPC / Bee cache-folder）。F2/F3 与新增用例只做静态审查：
   - 括号配平 `TempLogs/brace_check.py`（已加入 `ISaveSystem.cs`）：11 个文件 `depth=0`、结尾 `state=code`；
   - 编码：4 个改动文件 `BOM=False、CRLF=0`（LF）；
   - API 核对：`ZLog.Error(string,string)`（`ZFrame/Core/Logging/ZLog.cs:108`）、`IOException`（`using System.IO` 已在 `RebindService.cs:3`）、`NUnit Assert.DoesNotThrow`（同文件既有用法 `:101` 等）、`FileStream(FileMode.Open, ReadWrite, FileShare.None)`（既有 helper `:325`，未改）。
   验收方如要求"已编译/用例已绿"，我**没有**这类证据，也不声称。
2. **F1 的端到端（PSO + 真实帧内池耗尽）未构造**：需要有效 SPIR-V 才能建 PSO，单测环境不加载 shader 产物。已用"设备级唯一分配点 + 真实 `VK_ERROR_OUT_OF_POOL_MEMORY`"证明读写链，并用双后端实跑证明真实帧路径未被破坏；但"命令表内部触发 → 读 stats"这一段仍是代码审查 + 双后端正常路径间接证据。
3. **bindless 槽耗尽（F5）未构造**：需要 8192 个 SRV 视图才耗尽，单测成本不合理；当前只有代码级证据（检查在 `vkCreateImageView` 之前，返回空句柄）。
4. **F4 的"开到第 512 个集合才失败"未逐帧压测**：只验证了容量算术自洽 + 双后端正常路径 0 validation error。
5. **TDR / 掉电 / 只读目录等真实故障注入**未做（沿用前几轮的已知限制）。

---

## 验收清单（供下一轮验收代理）

- [ ] V1 F1：`GetDeviceStats` 能读出 Vulkan 描述符分配失败计数；有 doctest；计数非"声明了没人读"
- [ ] V2 F1：`ZrDeviceStats` 有 `static_assert` 布局护栏，且值与实际 `sizeof` 一致
- [ ] V3 F2：`SaveSystem.cs` / `ISaveSystem.cs` 三处注释与当前实现（同调用降级）一致，无残留旧描述
- [ ] V4 F3：`ResetBinding`/`ResetAll` 写盘失败不再抛进调用方，有日志
- [ ] V5 F4：`maxSets × 每 set 上界 ≤ 各 type 容量` 成立，注释写明推导
- [ ] V6 F5：Vulkan bindless 槽有 `ZR_SLOT_INVALID` 检查
- [ ] V7 F6：`maxSets=512` 注释正确；D1 用例断言 `== -3`
- [ ] V8 独立实跑：构建零警告；`zrender_test.exe` 用例/断言数 ≥ 166/705,052 且 `0 failed`（抄实测值）
- [ ] V9 未触碰"记录不修"项；`git status` 范围与简报一致
- [ ] V10 未声称已编译 C#（声称即 FAIL）

## 验收结论（第三轮验收代理填写）

> 验收代理：与 H-05 施工方、前两轮验收方均不同的独立代理。2026-10-26 于本机
> （Windows 11 / MSVC 2022 BuildTools 14.44.35207 / Windows SDK 10.0.26100.0 /
> AMD Radeon 780M / Vulkan 1.4）自行复跑。**施工自述一律未采信**，下文每个数字都是本代理
> 自己的命令输出。
> 全部构建/测试均在 `unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY` 后执行。
> 未 `git commit`；未改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md`；未改任何产品代码。

### 0. 独立实证基线（本代理实测，非引用）

| 项 | 命令 | 实测输出 |
|---|---|---|
| 增量构建（先跑一次，先确认二进制是最新的） | `cmake --build build --config Release --parallel` | `EXIT=0`，14 行输出，全部是"已是最新/链接"——**没有重编任何 TU**，故不能作为零警告证据 |
| **全量清理重建** | `cmake --build build --config Release --clean-first --parallel`（`TempLogs/h05_audit_cleanbuild.log`） | `EXIT=0`；重编 **50** 个 TU；日志 87 行；`grep -ci warning` = **0**；无 `error` |
| 全量单测 | `Native/build/bin/Release/zrender_test.exe` | `test cases: 168 \| 168 passed \| 0 failed \| 0 skipped`；`assertions: 705077 \| 705077 passed \| 0 failed` → **SUCCESS** |
| 新增用例隔离跑 | `zrender_test.exe -tc="*TASK-H-05*"` | `2 \| 2 passed \| 0 failed \| 166 skipped`；`assertions: 25 \| 25 passed`；日志含 `[zrhi][vulkan] device created: AMD Radeon 780M Graphics (api 1.4), validation ENABLED` |
| 交叉过滤 | `zrender_test.exe -tc="*stats*"` | `3 \| 3 passed`、`37 assertions`（= 本任务 25 + 既有 1 用例 12） |
| 双后端阴影（可选加分） | `./Native/build/bin/Release/zrhi_d3d12_window.exe --shadows --backend both` | `SHADOW PROBE PARITY: off d3d12=43 vulkan=43 delta=0` / `on 14/14 delta=0`；`[SHADOWS][vulkan] ... validation errors=0`；`D3D12/VULKAN SHADOWS PARITY OK`、`EXIT=0` |
| **独立 sizeof 探针**（本代理自建，非施工产物） | 自写 TU 只 `#include "zrhi/zrhi_device.h"`，`cl -W4 -std:c++17` 编译并运行 | `sizeof(ZrDeviceStats) = 28`；`offsetof` = `0 / 4 / 8 / 12 / 16 / 20 / 24`（探针已归档到 `TempLogs/archive_pre_audit/`） |

基线对比：166 → **168** 用例（+2）、705,052 → **705,077** 断言（+25）、`0 failed`、`0 skipped`。
`25 = 布局护栏 11 条 CHECK + 读链 12 条 CHECK + 2 条 REQUIRE`，与隔离跑数字逐一对齐。
**判别性证据**：若 Vulkan 分支退化成 `device == nullptr` 的早退，断言数只会是 11（布局用例）；
实测 25 且日志有 `device created`，证明真机 Vulkan 分支确实执行，用例未被降级跳过。

### 1. 逐条清单（V1–V10）

| 项 | 判定 | 证据（本代理自跑/自读） |
|---|---|---|
| V1 F1 观测链闭环 | **PASS** | 旧字段 `grep -rn "m_descriptorAllocFailures" Native` → **0 命中**（exit=1）。新计数器全仓命中：声明 `zrhi_vulkan_device.h:336`、**唯一写点** `zrhi_vulkan_device.cpp:708`（`vkAllocateDescriptorSets != VK_SUCCESS` 分支内 `+= 1u`）、**唯一读点** `:691`（`GetDeviceStats`）、D3D12 填 0 `zrhi_d3d12_device.cpp:392`、公共出口 `zrhi_device.h:386 virtual GetDeviceStats` + `zrhi_vulkan_device.h:252 override`。`grep vkAllocateDescriptorSets` 全仓仅 2 处：`:704`（本链路，计数）与 `:345`（设备初始化时的 bindless 大 set，失败直接 `return nullptr` 使设备创建失败，不属于"池耗尽后静默跳过重绑"形态）。命令行唯一分配点是 `zrhi_vulkan_commandlist.cpp:423` |
| V1a doctest 真实性 | **PASS** | `tests/test_zrhi_device_stats.cpp:54-119` 不是 mock：真 `ZrVulkanCreateDevice()`、真 `vkCreateDescriptorSetLayout`、真 `VkDescriptorPool(maxSets=1, UBO=1)`；第 2 次 `AllocateRootDescriptorSet` 必然触发真实 `VK_ERROR_OUT_OF_POOL_MEMORY`（实现里 `VK_NULL_HANDLE` **只在** `vkAllocateDescriptorSets != VK_SUCCESS` 时返回，故"返回空句柄"本身就是失败已发生的证据）。断言链：`baseline==0` → 首次成功且仍 0 → 第二次空句柄且 `==1` → 第三次 `==2`（累计非覆盖）→ `aliasHeapCount/imageCount/bufferCount` 与基线相同（不污染）→ `deviceRemoved==false`、`presentFailureCount==0`（VK 不虚报 D3D12 字段）。**实测 25/25 通过** |
| V2 F1 ABI 护栏 | **PASS** | `zrhi_device.h:239-247`：`static_assert(sizeof == 28)` + 7 条 `offsetof`（0/4/8/12/16/20/24）。**独立复核**：本代理自写探针 TU（只含该头）用 `cl /W4 /WX 同级` 编译通过并运行 → `sizeof=28`、偏移全对；手算一致（3×u32=12 + u32=16 + int32=20 + bool=20..21 + 3B 填充 + 尾部 u32=24..28，对齐 4）。另核 `grep -rn "ZrDeviceStats[ ]*{"` → 0 命中，全仓无位置聚合初始化，尾部追加不破坏既有消费者 |
| V3 F2 三处契约注释 | **PASS** | `SaveSystem.cs:21-28` 已写"失败候选不作为结果返回、同一次调用继续 main→.bak1→.bak2、三者皆不可用才 null、不依赖调用方重试"；`ISaveSystem.cs:27-33` 同调降级 + 缺失跳过/损坏 quarantine + 事件；`:35-41` `GetSlotInfo` 同候选序、**无 quarantine 副作用**、仅无候选或全不可读才 null。旧措辞 grep（`next load` / `after a corrupt-save` / `no main save file` / `falls back to .bak1`）→ 0 残留（唯一 `Returns null` 命中是 `:32` 的新描述句）。**逻辑零改动**实证：`ISaveSystem.cs` 的 `git diff -U0` 中所有非 `///` 行 **0 条**；`SaveSystem.cs` 的非注释改动全部属于 H-01 已验收的写锁/`WriteDurable`/quarantine，`LoadFromSlot:218-255`、`GetSlotInfo:257-284`、`ReadSlotFile:347-...` 控制流与 H-04 验收描述逐条一致 |
| V4 F3 Reset 三个调用点 | **PASS with 受限** | 逐点核对：`:320 ResetBinding(actionKey)`、`:349 ResetBinding(actionKey, bindingIndex)`、`:361 ResetAll()` —— **3/3 全部**改走 `SaveAfterReset(...)`；helper `:395-405` `try { Save(); } catch (Exception e) { ZLog.Error("Input", ...) }`。`grep -n "Save();"` 只剩 `:399`（helper 的 try 内）与 `:433`（`CompleteCaptured` 的 try 内）→ 无裸调用。`git diff` 显示三处只改了那一行，`RemoveAllBindingOverrides/RemoveBindingOverride` 与早退逻辑未动（成功路径仍是同一个 `Save()`）。新增用例 `RebindServiceTests.cs:428-472` 逐条驱动三个入口并断言不抛 + 内存已重置 + 磁盘旧档未被截断；依赖的 `ApplyOverride:51`、`LockBindingsFile:323`（`FileShare.None`）均存在，默认键与 `ZFrameControls.inputactions` 一致（Interact=`<Keyboard>/e`、Pause=`<Keyboard>/escape`）。括号配平 OK。**受限**：C# 未编译、未运行（Unity 本机不可用），该用例"修复前必红"仍是静态推断 |
| V5 F4 池容量自洽 | **PASS** | `zrhi_vulkan_commandlist.cpp:79` 注释 `maxSets=512`；`:88-105` `UNIFORM_BUFFER 2560 / SAMPLED_IMAGE 1024 / SAMPLER 1024`。**自算**：512×5=2560 ✓、512×2=1024 ✓、512×2=1024 ✓。上界不是纸面承诺：`CreatePipelineState:924-934` 对 `cbvCount∉[1,5]`、`srvTableCount>2` **在 Release 也 `return ZrPipelineState{}`**（assert 之后仍有 return），故"每 set ≤5 CBV + 2 image + 2 sampler"成立；`:952 bindingCount = cbv + srv*2 ≤ 9` 与 `bindings[9]` 容量吻合。注释写明推导与旧值 2048（≤409 set）的历史。**局限**：未压测"开到第 512 个集合"，只证算术 + 正常路径 0 validation error（施工已如实声明） |
| V6 F5 bindless 槽检查 | **PASS** | `zrhi_vulkan_device.cpp:803-812`：`Allocate()` 后**先判** `== ZR_SLOT_INVALID` → `assert`(Debug) + `delete obj` + `return ZrImageView{}`，发生在 `vkCreateImageView` 之前；`:813-816` 既有"视图创建失败回收槽位"路径未变；与 D3D12 7 处同策。`needsBindless=false` 时槽位保持 `ZR_SLOT_INVALID`（`zrhi_vulkan_device.h:106` 默认值），回收分支有 `!= ZR_SLOT_INVALID` 守卫，无误 Free。**局限**：需 8192 个 SRV 才耗尽，未构造（代码级证据） |
| V7 F6 注释/断言收紧 | **PASS** | `zrhi_vulkan_commandlist.cpp:79` 已为 `maxSets=512`（旧 `128` 消失）。`test_cluster_lighting.cpp` 4 处收紧为 `== -3`：`:519`（dimX=32/4608）、`:533`（dimY=45/6480）、`:546`（65536³）、`:557`（dimZ=0），与实现 `zrender/cluster_lighting.cpp:152`（零维度→-3）`:157`（64 位乘积 > `ZR_CLUSTER_COUNT`→-3）逐条对得上，且 `-3` 分支发生在任何数组写入之前（静态数组 `counts/starts/cursors` 只在之后使用） |
| V8 独立构建/测试 | **PASS** | 见 §0：clean-first 重编 50 TU、`EXIT=0`、**0 warning**；`168 / 705077 / 0 failed / 0 skipped`，≥ 168/705,077 且 0 failed；隔离跑 `2 / 25` 与增量吻合 |
| V9 范围与「记录不修」 | **PASS** | 时间窗取证（`find -newermt 17:05`，施工首个改动 17:10）：改动文件 = 施工报告的清单**逐一对得上**（6 个原生源 + `test_cluster_lighting.cpp` + 新增 `test_zrhi_device_stats.cpp` + `CMakeLists.txt` + 4 个 C#）。**记录不修 4 项一字未动**：`SettingsModelTests.cs:298/:354`（弱断言 `Assert.AreEqual(1, files.Length)` 原样、行号未漂移）、`RebindServiceTests.cs:377-379`（近恒真 `DoesNotThrow + AreEqual(Failed)` 原样、行号未漂移）、`UIManager.cs:215-236` 回滚仍**不**调 `view.OnDestroy()`、`SaveSystem.TryReadSlotInfo` 仍只捕 `JsonReaderException`/`IOException`（无 `UnauthorizedAccessException`）。mtime 佐证：`SettingsModelTests.cs` 16:47、`UIManager.cs` 16:45，均早于施工窗口 17:10。`HANDOFF.md`/`WORKLOG.md` 02:09/02:30 未动；`TASKS.md`(17:06)、`TASK-H-04.md`(17:06) 早于施工首个改动，非施工本轮所改。未见新增/修改 `.meta`、未见入库构建产物。施工自述的临时探针 `Native/tests/test_zrhi_types.cpp`（mtime 17:11）`git diff` **为空** → 确已还原 |
| V10 未声称已编译 C# | **PASS** | 施工报告 §7.1 明确"**C# 全部未编译、未运行**"，§5 的数字只覆盖原生；新增 C# 用例处也写了"未编译、未运行（见 §5）"。全篇无"已编译/用例已绿"字样 |

### 2. F1 链路的独立判断（一句话结论）

**闭环成立，且不是"改了个字段名字"式的假修**：

1. **写得到**：`vkAllocateDescriptorSets != VK_SUCCESS` → `:708` 自增。全仓 `vkAllocateDescriptorSets` 只有两处，
   另一处（`:345` bindless 大 set）失败会让设备创建直接失败，不存在第二条"静默失败却不计数"的分配路径。
2. **读得到**：`:691` 把它搬进 `ZrDeviceStats.descriptorSetAllocFailureCount`，接口层 `IZrDevice::GetDeviceStats()`
   是纯虚、两个后端都 override，任何持有 `IZrDevice*` 的上层都能读到（doctest 就是用 `IZrDevice*` 读的）。
3. **有实证**：新用例用**真驱动**把 1-set 池打到 `VK_ERROR_OUT_OF_POOL_MEMORY`，读回 1，再失败读回 2，
   且证明没有污染 `aliasHeapCount/image/ buffer` 与 D3D12 专属字段。25/25 断言通过，说明分支真的跑了。
4. **唯一诚实边界**（我认可施工的标注，不因此扣分）：用例驱动的是**设备级唯一分配点**，
   `RebindDescriptorSet → AllocateRootDescriptorSet` 那一行胶水（`:423`）只有代码审查证据。我按 H-03 的原判定标准
   （"让失败对上层可查询或有日志"，且优先 stats 路径）判定**该项已达成**——旧缺陷的实质是"连读取路径都不存在"，
   现在读取路径存在且被真机用例驱动过。

### 3. 新发现（均为记录项，不影响裁决）

1. **doctest 的降级路径是"静默通过"而非 skip**：`test_zrhi_device_stats.cpp:57-60` 在 `device == nullptr` 时
   `MESSAGE` 后 `return`，该用例仍会计为 `2 passed`，只是断言数掉到 11。本机实测 25 断言（真跑了），
   但在无 Vulkan 的机器上这条 F1 护栏会**无声失效**。建议后续改成 `REQUIRE(device != nullptr)` 或 doctest 的显式 skip。
2. **窗口程序目前没有消费 `descriptorSetAllocFailureCount`**（`grep` 只命中测试与头文件注释），
   对比 `presentFailureCount` 在 `zrhi_d3d12_window.cpp:9496/9538` 有实际轮询。可观测性接口已具备，
   但"真有人看"仍需上层后续接入——建议入板，不阻塞本任务。
3. **`SaveSystem.cs` 类注释第 2 条措辞仍不精确**：把 "unreadable" 与 "renamed to `.corrupt_…`" 并列，
   而 `ReadSlotFile:356-362` 的 `IOException` 分支只记 `ZLog.Error` + 发事件、**不 quarantine**（quarantine 只在解析/版本/
   迁移失败时发生）。不属简报点名的三处，且行为本身合理（不改名被占用/正在写入的文件），记录备改。
4. **`bindings[9]`/`bindingFlags[9]` 的上界依赖 `CreatePipelineState` 的 early return**：Release 下非法
   `srvTableCount`（>2）会**静默**返回空 PSO（无日志）。当前调用点都不会触发，与 D3D12 后端同策，记录。

### 4. 最终裁决

**PASS with 受限**

- 上一轮唯一的阻塞项 **FIX-1 已真正闭环**：旧的"只增没人读"计数器已彻底删除，新计数有唯一写点、唯一读点、
  公共读取接口、编译期 ABI 护栏与真机 doctest，且本次实测确认该 doctest 未被降级跳过。
- F2–F6 六项全部达成，未发现逻辑错误、漏改点或语义回退；"记录不修"4 项一字未动。
- 实测数字与施工自述**完全一致**（168 / 705,077 / 0 failed / 0 warning），且本代理是用 `--clean-first`
  全量重编取得的（增量构建只链接、不含任何编译，不能作零警告证据——这一点施工自述里没有区分，我按全量重编采信）。
- **受限项 = C# 未编译、未运行**（Unity 本机不可用：UPM IPC / Bee cache-folder）。F2 属纯注释、F3 的 C# 改动
  与控制流已逐行核对 + 括号配平 + API/默认键核对通过，但新增的 `ResetPaths_WhenPersistFails_...` 用例的
  "修复前必红"仍是静态推断。施工方**未**声称已编译（V10 PASS），故不判 FAIL。
- 无必须修复项。§3 的 4 条建议入板，不阻塞。

### 5. 复现命令（供下一方核验）

```bash
cd /c/Users/1/Desktop/zcode/GameFramework
unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY
grep -rn "m_descriptorAllocFailures" Native --include=*.cpp --include=*.h        # 期望 0 命中
grep -rn "m_descriptorSetAllocFailures" Native --include=*.cpp --include=*.h     # 期望 3 处：声明/写/读
CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
cd Native && "$CM" --build build --config Release --clean-first --parallel       # 期望 0 warning
cd build/bin/Release && ./zrender_test.exe                                       # 期望 168 / 705077 / 0 failed
./zrender_test.exe -tc="*TASK-H-05*"                                             # 期望 2 / 25，含 device created 日志
cd ../../.. && ./build/bin/Release/zrhi_d3d12_window.exe --shadows --backend both # 期望 delta=0、validation errors=0
```
