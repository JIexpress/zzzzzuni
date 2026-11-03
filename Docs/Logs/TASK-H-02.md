# TASK-H-02 —— B 轨原生防御性修复（收口后独立审查发现）

> 施工代理按本简报实现并自测，写「施工报告」到本文档第二节。**禁止 git commit**。
> 验收由另一个代理独立执行（第三节清单）。

## 任务简报（主控撰写）

### 背景

项目已于 FINAL-01 阶段收口（原生 161 用例 / 704,980 断言全绿）。阶段收口后的独立审查
（2026-10-26）发现若干**当前测试与演示路径照不到**的防御性缺陷：容量无上限校验、失败被
静默吞掉、资源未释放。本任务修 B 轨（原生 C++）部分。

### 缺陷清单（每条已在代码中定位，勿扩大范围）

**D1 — 簇网格容量无上限校验（越界写 static 数组）**

- 位置：`Native/zrender/cluster_lighting.cpp:150-160`（`counts/starts/cursors` 三个
  `static uint32_t [ZR_CLUSTER_COUNT]`），`Native/zrender/cluster_lighting.h:84-92`
- 现状：`ClusterGrid::dimX/dimY/dimZ` 是 public 可写成员（默认 16/9/16 → 2304），
  `ClusterCount()` 直接返回三者乘积且**无上限校验**；`clusterTotal` 用于遍历写入上述
  三个 2304 项数组。调用方把 dimX 设为 32 就会越界写。
- 现状补充：全仓库当前无自定义维度用法（只有默认值），故**不是现存 bug，是 API 契约缺口**。
- 期望：在写数组之前校验 `ClusterCount() <= ZR_CLUSTER_COUNT`，超限按既有返回码机制
  **明确报错返回**（不要 assert-only，Release 会失效）；若现有 Assign 返回值不足以表达
  「容量超限」，可复用已有的错误码/返回 false，**不要**改公共 ABI 语义。
- 验收要能证明：构造 `dimX=32`（乘积 4608）调用分配函数，得到失败返回且**不写**数组；
  默认维度行为完全不变。

**D2 — FSR Resize 未校验输出上限（写越分配的输出资源）**

- 位置：`Native/zupscaler/fsr_upscaler.cpp:344` 附近（`Resize`）
- 现状：创建时按 `maxUpscaleSize` 分配输出资源，但 `Resize` 只校验输入上限，未校验
  「目标输出尺寸 ≤ 创建时上限」，超限即 dispatch 写到未分配内存。
- 期望：保存创建时的上限并在 `Resize` 校验，超限返回失败（不 dispatch）。
  注意本机 780M 无 FSR4 provider，**不要**试图真机跑 FSR4，用 null/受限路径的单测覆盖即可。

**D3 — D3D12 Present 的 HRESULT 被整条丢弃（设备丢失/被遮挡静默）**

- 位置：`Native/zrhi/d3d12/zrhi_d3d12_device.cpp:1413` 附近（`Present`）
- 现状：`Present(...)` 返回值未检查，`DXGI_ERROR_DEVICE_REMOVED` / `DXGI_ERROR_WAS_STILL_DRAWING`
  等被静默吞掉，上层无法得知设备已失效。
- 期望：检查 HRESULT，失败时记录可观测状态（设备内部状态位或统计字段，供上层查询），
  **不要**在库内直接 printf/abort。若已有错误上报通道（如 device stats），复用之。

**D4 — Bilinear 超分资源泄漏**

- 位置：`Native/zupscaler/bilinear_upscaler.cpp:288` 附近，及 `Initialize` 的中途失败分支
  （约 `:189`、`:204`）
- 现状：持久映射未 `Unmap` 就 Release；`Initialize` 中途失败时已创建的
  rootSignature/PSO 未释放，再次 `Initialize` 直接覆盖指针 → 泄漏。
- 期望：所有提前返回路径释放已建资源；持久映射在析构/重建前解除映射。幂等：
  失败后再次 `Initialize` 不泄漏、不 UB。

### 约束

- 只改上述 4 处及其直接依赖。**不改公共 ABI 的既有语义**（可加字段/加校验，不可改签名含义）。
- 每处修复**必须配 doctest 用例**（`Native/tests/`），且用例要在修复前失败。
  容量/尺寸类用纯逻辑路径即可，不需要真 GPU。
- 保持 `/W4 /WX` 零警告。C++17，不改编译器选项。

### 环境纪律（**必读，否则会卡很久**）

1. **跑构建前先清重复代理变量**（否则 MSBuild 崩）：
   `unset http_proxy https_proxy all_proxy`
   环境里同时有 `HTTP_PROXY`/`http_proxy`，MSBuild 的
   `ProcessStartInfo.EnvironmentVariables` 用大小写敏感字典会抛
   「已添加项。字典中的关键字:"HTTP_PROXY"…」。
2. **cmake 不在 PATH**，用：
   `"/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
3. **构建命令**（在 `Native/` 下，且已 `unset`）：
   `"$CM" --build build --config Release --parallel`
4. **PowerShell 工具无法执行外部 exe**（报"无法在管道中间运行文档"），`Start-Process` 被安全钩子拦。
   **一律用 Bash 工具**调用 cmake 和测试 exe。
5. **跑测试**：`cd Native/build/bin/Release && ./zrender_test.exe`（末尾会打印
   `test cases: N | N passed | 0 failed` 与断言数）。基线：**161 用例 / 704,980 断言**。
   你新增用例后数字应上升，**不得有 failed**。
6. **safe-delete 钩子拦批量删除**：清理旧文件用 `mv` 归档，不要 `rm -rf`。
7. **Unity 不可用**（UPM/Bee 环境问题），本任务不需要 Unity。

### 交付物

1. 4 处修复 + doctest 用例
2. 本文档第二节「施工报告」：文件:行号、改法、自测命令与**实际输出数字**
   （用例数/断言数必须写实测值，不许写"全绿"了事）
3. 不要改 TASKS.md / HANDOFF.md / WORKLOG.md（主控负责）

---

## 施工报告（施工代理填写）

### 交付概览

- 4 处缺陷全部修复，每处配 doctest 用例（`Native/tests/`），并完成「修复前失败」举证（见 §四）。
- 构建：`/W4 /WX` **零警告**（未改任何编译选项，C++17）；未改公共 ABI 既有语义（仅新增校验/字段/纯函数）。
- 全量单测实测：`test cases: 166 | 166 passed | 0 failed | 0 skipped`，
  `assertions: 705052 | 705052 passed | 0 failed`（基线 161 用例 / 704,980 断言 → 新增 5 用例 / 72 断言）。
- 未 `git commit`；未改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md`。

改动文件（11 源 + 3 测试）：

| 文件 | 缺陷 |
|---|---|
| `Native/zrender/cluster_lighting.cpp` / `.h` | D1 |
| `Native/zupscaler/zupscaler.cpp` / `.h`、`fsr_upscaler.cpp` | D2 |
| `Native/zrhi/zrhi_device.h`、`Native/zrhi/d3d12/zrhi_d3d12_translate.h` / `.cpp`、`zrhi_d3d12_device.h` / `.cpp` | D3 |
| `Native/zupscaler/bilinear_upscaler.cpp` | D4 |
| `Native/tests/test_cluster_lighting.cpp`、`test_upscaler.cpp`、`test_zrhi_d3d12_translate.cpp` | 用例 |

### 一、D1 — 簇网格容量无上限校验（越界写 static 数组）

**位置**：`Native/zrender/cluster_lighting.cpp:147-159`（`AssignLightsToClusters` 内，写
`counts/starts/cursors` 之前）；`Native/zrender/cluster_lighting.h:92`（`ClusterCount` 契约注释）、
`:130-141`（返回码文档）

**改法**：
- 在取 `clusterTotal` 之前先校验：任一分量为 0 → 返回 `-3`（避免除零/空簇表）；
  用 **64 位乘积** `dimX×dimY×dimZ` 与 `ZR_CLUSTER_COUNT` 比较，超限 → 返回 `-3`。
  用 64 位是因为 32 位乘积在大维度下会回绕（如 `65536³ → 0`）而蒙混过关。
- `clusterTotal` 改由该 64 位乘积转换而来，故后续三个 static 数组的写入不可能越界。
- 复用既有返回码机制（原有 `-1` 参数错误 / `-2` 平坦表容量不足），新增 `-3`
  = 维度为 0 或网格超容量；失败时**不写** `outRanges`/`outFlat`（用例以哨兵值举证）。
- 默认 16×9×16 路径行为完全不变（用例首段回归护栏）。结构与签名未改。

**用例**：`cluster: 簇网格超容量拒绝且不写输出数组（TASK-H-02 D1）`（`tests/test_cluster_lighting.cpp:496`）
覆盖：默认维度仍正常 → `dimX=32`（4608>2304）拒绝且输出数组保持 `0xC0FFEE` 哨兵 →
`dimY=45`（6480）→ `65536³`（32 位回绕）→ `dimZ=0`。

### 二、D2 — FSR `Resize` 未校验输出上限（写越分配的输出资源）

**位置**：`Native/zupscaler/fsr_upscaler.cpp:337-362`（`Resize`）、`:239-242`（Initialize 记录上限）、
`:437-439`（新增 `m_maxOutputWidth/m_maxOutputHeight`）；`Native/zupscaler/zupscaler.h:190-201`
（新增纯函数声明）、`zupscaler.cpp:15-35`（实现）

**改法**：
- `Initialize` 保存创建时的 `maxUpscaleSize`（= `desc.outputWidth/Height`，即输出资源分配上限）。
- `Resize` 改走新增纯逻辑函数 `ZupResizeWithinLimits(...)`：非 0 → 输入 ≤ maxInput →
  **输出 ≤ maxOutput** → 输出 ≥ 输入（超分后端不接受下采样）。任一不满足即打印
  `input→output, max input→max output` 明细并返回 false，不 dispatch、不改内部尺寸状态。
- 校验逻辑抽成无副作用纯函数（头文件公共声明，纯新增），使该契约可在无 GPU 环境下单测；
  FSR 后端只是它的调用方，不存在两处各写一遍的分叉。
- 未尝试真机跑 FSR4（本机 780M 无 4.x provider，见 TASK-B-R6-02），按简报走受限路径。

**用例**：
- `ZupResizeWithinLimits: 目标输出超出创建时上限被拒（TASK-H-02 D2）`（`tests/test_upscaler.cpp:887`）：
  上限内合法（含等于上限的边界）/ 1920×1080、3840×2160、单轴超限均拒 / 输入超限 / 下采样 / 零维度。
- `CreateFsrUpscaler: 未初始化时不接受任何 Resize（TASK-H-02 D2）`（`:912`）。

### 三、D3 — D3D12 `Present` 的 HRESULT 被整条丢弃

**位置**：`Native/zrhi/d3d12/zrhi_d3d12_device.cpp:1472-1476`（`ZrD3D12Swapchain::Present`）、
`:1394-1398`（`ZrD3D12Device::NotePresentResult`）、`:381-388`（`GetDeviceStats` 上报）；
`Native/zrhi/d3d12/zrhi_d3d12_translate.h:57-70`（新增 `ZrD3D12PresentStatus`）、
`zrhi_d3d12_translate.cpp:98-113`（`Note` 实现）；`Native/zrhi/d3d12/zrhi_d3d12_device.h:318`、`:379`；
`Native/zrhi/zrhi_device.h:213-219`（`ZrDeviceStats` 尾部新增 3 字段）

**改法**：
- `Present` 的返回值不再丢弃：`const HRESULT hr = m_swapchain->Present(...)` →
  `m_owner->NotePresentResult(hr)`。库内不 printf、不 abort、不改控制流
  （帧槽 fence 信号语义保持原样：它跟踪 GPU 工作完成，不是「呈现被接受」）。
- `ZrD3D12PresentStatus::Note(long hr)`（纯逻辑，可单测）：`hr >= 0`（含 `DXGI_STATUS_OCCLUDED`，
  被遮挡/最小化属正常态）不计失败；`FAILED(hr)` 累计 `failureCount`、记录
  `lastFailureHresult`；`DEVICE_REMOVED`/`DEVICE_RESET`/`DRIVER_INTERNAL_ERROR` 额外置**粘滞**
  `deviceRemoved`（后续成功不清除）。
- 复用既有上报通道 `IZrDevice::GetDeviceStats`：`ZrDeviceStats` 尾部新增
  `presentFailureCount` / `lastPresentHresult` / `deviceRemoved`（纯追加字段，默认值 0/false，
  既有聚合初始化与 Vulkan 后端不受影响）。上层据此判断是否重建设备。

**用例**：`ZrD3D12PresentStatus: Present 失败累计且设备移除粘滞（TASK-H-02 D3）`
（`tests/test_zrhi_d3d12_translate.cpp:212`）：S_OK/OCCLUDED 不计失败、WAS_STILL_DRAWING 计数、
DEVICE_REMOVED 置粘滞位、其后成功不清历史、DEVICE_RESET/DRIVER_INTERNAL_ERROR 同样置位。

### 四、D4 — Bilinear 超分资源泄漏

**位置**：`Native/zupscaler/bilinear_upscaler.cpp:51-57`（Initialize 入口幂等复位）、
`:80/92/143/154/180/198/203/214/223`（各提前返回路径回滚）、`:293-305`（Shutdown 先 Unmap 再 Release）

**改法**：
1. `Initialize` 入口改为**无条件** `Shutdown()`（原来只在 `m_initialized` 为真时清理）。
   上一次 Initialize 中途失败时 `m_initialized` 仍是 false，旧代码跳过清理，随后给
   `m_rootSignature/m_pso/...` 赋值会直接覆盖指针 → 泄漏。
2. 所有提前返回路径（VS/PS 编译、根签名序列化、CreateRootSignature、CreateGraphicsPipelineState、
   CB 创建、CB Map、SRV/RTV 堆）统一 `Shutdown()` 回滚，结构上保证「返回 false ⇒ 不持有任何资源」。
3. `Shutdown` 中持久映射的常量缓冲改为**先 `Unmap(0, nullptr)` 再 `Release`**
   （映射未解除的资源在 Release 后仍被运行时持有，属确定性泄漏；原实现先把
   `m_cbMapped` 置空再 Release，等于永久放弃解映射）。

**用例**：`BilinearUpscaler: 中途失败不留资源 / 重复 Initialize 不叠加（TASK-H-02 D4）`
（`tests/test_upscaler.cpp:829`）。用 **WARP 软件设备**（`D3D12CreateDevice` + `EnumWarpAdapter`，
不需要真实 GPU/窗口），以**设备引用计数**（`AddRef`+`Release` 抵消读取）作为泄漏探针：
1. 注入 PSO 创建失败（VS 故意不输出 `SV_Position`，WARP 稳定返回 `E_INVALIDARG`，
   失败点正好在根签名之后）→ 连续 4 次失败初始化后引用计数必须回到基线；
2. 失败后再成功初始化 → Shutdown → 回到基线；重复 Shutdown 幂等；
3. Initialize→Shutdown 三轮循环，每轮回到基线（含 CB 解映射）。
WARP 不可用时用例打印 MESSAGE 并降级（本机可用，实测执行了全部断言）。

### 五、自测命令与实际输出

```bash
unset http_proxy https_proxy all_proxy
CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
cd Native && "$CM" --build build --config Release --parallel
# → 全部目标构建成功，0 warning（/WX 下任何警告即构建失败）
cd build/bin/Release && ./zrender_test.exe
```

修复后（最终态）实测末尾：

```
[doctest] test cases:    166 |    166 passed | 0 failed | 0 skipped
[doctest] assertions: 705052 | 705052 passed | 0 failed |
[doctest] Status: SUCCESS!
```

修复前基线（本任务开工时实跑，确认环境与基线一致）：`161 | 161 passed | 0 failed` /
`assertions: 704980 | 704980 passed | 0 failed`。

**修复前失败举证**（用例确为真断言，非事后凑数）：

| 缺陷 | 举证方式 | 实测输出（修复前） |
|---|---|---|
| D1 | 先加用例、后改实现；`./zrender_test.exe -tc="*TASK-H-02 D1*"` | `FATAL ERROR: test case CRASHED: SIGSEGV`（4608 簇写入 2304 项 static 数组直接段错误），`1 \| 0 passed \| 1 failed` |
| D2 | 加用例后临时屏蔽实现里的输出上限判断（变异验证），跑 `-tc="*TASK-H-02 D2*"` | 4 条 `CHECK_FALSE(ZupResizeWithinLimits(...1920x1080 / 3840x2160 / 1280x2160 / 3840x720...))` 全 ERROR |
| D3 | 加用例后临时把 `ZrD3D12PresentStatus::Note` 改为空实现（等价于丢弃 HRESULT） | 10 条断言 ERROR（failureCount/lastFailureHresult/deviceRemoved），`2 failed` 用例 |
| D4 | 先加用例、后改实现；`-tc="*TASK-H-02 D4*"` | 4×`CreateGraphicsPipelineState FAILED (hr=0x80070057)`；`CHECK( 6 == 1 )`（4 次失败初始化后引用计数未回基线）、`CHECK( 2 == 1 )`（成功初始化→Shutdown 后仍高 1），`7 failed` 断言 |

变异验证后已从备份（`TempLogs/archive_pre_audit/`）恢复源文件并重新构建，恢复后全量 166/705052 全绿。

### 六、未能验证项及原因

1. **D3 端到端（真实交换链上的 Present 失败）**：需要在窗口 + 真实/软件交换链上制造
   `DXGI_ERROR_DEVICE_REMOVED`（TDR）或被遮挡路径，单测环境无法构造，也未运行
   `zrhi_d3d12_window.exe`（需前台窗口）。已用纯逻辑用例覆盖分类/累计/粘滞语义，并由
   代码审查覆盖调用链 `Present → NotePresentResult → GetDeviceStats`。
2. **D4 的 Unmap 直接证据**：D3D12 不回报「资源是否仍被映射」，无法直接读状态；改用设备
   引用计数间接举证——修复前「成功初始化 → Shutdown」后计数居高 1（映射未解除的 CB 未被销毁），
   修复后回基线。其余失败分支同理（修复前失败 4 次后计数 +1，修复后回基线）。
3. **FSR / FSR4 真机路径**：本机 780M/RDNA3 无 4.x provider（TASK-B-R6-02 已知结论），
   按简报不真机跑 FSR4；`Resize` 的新契约以纯逻辑函数 + 未初始化路径覆盖，
   `Evaluate`/dispatch 未改动，未做真机帧率/画质验证。
4. **D1 的超限用例在修复前表现为段错误**（进程级失败而非断言失败）：这是缺陷本身的后果，
   不以「崩溃」作为修复后判据——修复后该用例断言全部正常通过。


---

## 验收清单（主控撰写，验收代理执行）

- [ ] V1 D1：`dimX=32`（4608 > 2304）调用分配得到失败返回且未越界写数组；有 doctest 举证
- [ ] V2 D2：`Resize` 目标输出超上限被拒并返回失败，无 dispatch
- [ ] V3 D3：`Present` 失败 HRESULT 不再静默（可观测状态位/统计可查）
- [ ] V4 D4：`Initialize` 各失败分支无资源泄漏；持久映射有对应 Unmap；重复 Initialize 安全
- [ ] V5 新增 doctest 为真断言，且在修复前会失败
- [ ] V6 **独立实跑**：`unset http_proxy https_proxy all_proxy` 后构建，`/W4 /WX` 零警告；
      `zrender_test.exe` 用例数与断言数 ≥ 基线且 `0 failed`（把实测数字抄进结论）
- [ ] V7 改动范围与简报一致，未触碰公共 ABI 既有语义

## 验收结论（验收代理填写）

（待填）
