# TASK-B-R3-02 RenderGraph 接真 D3D12：真资源/split 屏障落地/别名池真复用

状态: done | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：把 R3-01 的 RenderGraph 从 Mock 接到真 D3D12 后端：Transient 资源真实创建（别名槽用 placed resource 共享堆实现**真内存复用**）、屏障真实下发（含 **split barrier begin/end 落地**）、窗口验证程序帧循环改用 RG 驱动。

**设计要点**：
1. `zrhi_device.h` 扩展（保持加法式）：`ZrAliasedImageDesc{ZrImageDesc, heap}` 路径——`IZrDevice::CreateAliasHeap(sizeBytes)` 返回 `ZrAliasHeap`；`CreateImageInHeap(desc, heap, offset)`（D3D12: CreatePlacedResource）；`IZrDevice::GetDeviceStats()`（heap 数/资源数，供验证）
2. `zrhi/d3d12/` 实现：别名堆=普通 D3D12 heap（DEFAULT）；placed image 需资源尺寸对齐（用 `GetResourcePlacementInfo`/对齐要求计算，offset 传 0 且每槽一堆的简化模型——**每别名槽一个堆**，槽内串行复用天然无重叠）；Destroy 顺序与统计
3. `Native/zrender/rg_d3d12_executor.h/.cpp`（或并入 rg_render_graph）：`RgExecuteD3D12(RgGraph&, IZrDevice&, IZrCommandList&, RgExternalResources&)`：
   - 编译产物驱动：按别名计划建堆→按生命周期区间在首次使用前 CreatePlacedResource、末次使用后 Destroy（区间外不存在）；Imported 资源来自 RgExternalResources（句柄→真 IZrImage 映射）
   - 屏障下发：pass 前批量提交该组屏障；**split barrier**：write pass 结束立即 `ResourceBarrier(D3D12_RESOURCE_BARRIER_FLAG_BEGIN_ONLY)`，读 pass 前 `END_ONLY`（D3D12 split barrier 原生语义——这正是 Zr 语义到 D3D12 的映射点，translate 函数补 flag 映射）
   - RgContext 句柄→真资源解析表（执行期构建）
4. 窗口验证程序改造 `zrhi_d3d12_window.cpp`（或新 `zrhi_d3d12_rg_window.cpp` 目标，二选一报告说明）：
   - 帧循环改为：建图（Import 后台缓冲→ClearPass 写→PresentPass）+Compile+ExecuteD3D12；60 帧红绿蓝；保留 readback 颜色校验（每 10 帧）
   - **别名实证**：图中再造两个生命周期不重叠的 Transient 贴图（如"前一帧风格化 RT"与"后处理临时 RT"），打印 `alias heaps=X < transients=Y` 证明真复用；用后即毁（区间外不存在——可用 stats 断言资源数）
   - **split 实证**：加一个 DSV 深度写 pass + 后续只读 pass，开启 split 计划，打印 begin/end 屏障提交日志（或计数）
   - 通过行：`D3D12 RG SMOKE OK`（保留原 RESOURCE/WINDOW SMOKE OK）
5. 既有 mock 单测不动（回归）；新增 2-3 个纯逻辑单测（如 split 计划→begin/end 提交序列在 mock 命令表断言——把执行器写成设备无关：接收 IZrDevice/IZrCommandList 接口，mock 与 D3D12 都能跑执行器）
6. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zrender/`、`Native/zrhi/`（仅加法扩展接口与 D3D12 实现）、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；**不跑任何 Unity 命令**；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：执行器+别名堆+split 落地+RG 窗口程序+新单测+施工报告（含别名复用与 split 的运行时数字）。

## 施工报告（施工代理写）

**施工代理**: general-purpose 施工子代理 | **日期**: 2026-10-23

### 1. 交付内容（按设计要点逐条）

1. **zrhi 接口加法扩展**（`Native/zrhi/zrhi_device.h`）：`ZrDeviceStats`（135-140 行）、
   `IZrDevice::CreateAliasHeap/GetImagePlacementSize/CreateImageInHeap/GetDeviceStats`（257-279 行）、
   `Destroy(ZrAliasHeap)`（282 行）、`IZrCommandList::ResourceBarrierSplit`（303-309 行）。
   `Native/zrhi/zrhi_handles.h` 增 `ZrAliasHeap` 句柄。全部为加法，旧接口零破坏。
2. **D3D12 实现**（`Native/zrhi/d3d12/zrhi_d3d12_device.cpp`）：
   - `CreateAliasHeap`（313 行）：`CreateHeap` DEFAULT 堆，尺寸 64KB 向上对齐（320-321 行，
     `D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT`）
   - `GetImagePlacementSize`（339 行）：`GetResourceAllocationInfo`（345 行）——
     GetResourcePlacementInfo 语义等价物，返回真实落位尺寸与对齐要求
   - `CreateImageInHeap`（349 行）：`CreatePlacedResource`（367 行），offset 由调用方保证
     （每别名槽一堆 + offset=0 简化模型，槽内串行复用天然无重叠；模型注释见
     zrhi_d3d12_device.h 108-117 行）
   - `GetDeviceStats`（375 行）：堆数/图像数/缓冲数存活计数（"用后即毁"断言数据源）
3. **设备无关执行器**（并入 `Native/zrender/rg_render_graph.cpp` 的 `ExecuteAliased`，708-887 行；
   未新建 rg_d3d12_executor.*——执行器只依赖 IZrDevice/IZrCommandList，D3D12 与 Mock 均可驱动，
   并入图类避免循环依赖，特此报告说明）：
   - 每别名槽建一堆（尺寸=槽内最大 GetImagePlacementSize，718-739 行）→ 首次使用前
     `CreateImageInHeap` 物化（763-789 行）→ 末次使用后退役进延迟销毁队列（868-885 行）；
     `FlushRetired`（890-904 行）在 GPU 同步点后物理销毁（下一次 ExecuteAliased 入口自动冲刷）
   - 屏障批次占位句柄替换为当帧真资源（808-840 行；PlanBarriers 平行记录句柄表
     `perPassImageBatchResources/perPassBufferBatchResources`，rg_render_graph.cpp 664-673 行）
   - split 真实下发：写 Pass 结束 BEGIN_ONLY（851-866 行）、读 Pass 前 END_ONLY（791-806 行），
     经 `ResourceBarrierSplit` 接口
4. **split flag 映射**（translate 唯一转译点）：`ZrD3D12_SplitBarrierFlag`
   （`Native/zrhi/d3d12/zrhi_d3d12_translate.cpp` 79-82 行）；
   BEGIN_ONLY/END_ONLY 真实使用在 `zrhi_d3d12_commandlist.cpp` 74-81 行
   （`ResourceBarrierSplit` → `EmitImageBarriers(..., flag)`）
5. **窗口程序 RG 化**（改造 `Native/tests/zrhi_d3d12_window.cpp`，未新建目标）：
   - 窗口模式帧循环 = 逐帧建图（Import 后台缓冲 → StylePass 写 styleRt → DepthPrepass
     读 styleRt+写 depth → CompositePass 读 depth+写 postRt → FinalPass 写后台缓冲 →
     [每 10 帧 ReadbackPass] → PresentPass）+ Compile + ExecuteAliased（364-443 行）
   - 三个 Transient 贴图生命周期：styleRt[0..1] / depth[1..2] / postRt[2..3] →
     slot0=styleRt+postRt、slot1=depth → **2 堆 < 3 贴图真复用**
   - depth 写→读走 split（BEGIN_ONLY/END_ONLY 真实下发，每帧 1/1）
   - 60 帧红绿蓝 + 每 10 帧 readback 中心像素校验（±2 容差）全部保留且通过
   - 每帧断言 stats（heaps==2/transients==3/split 1/1/peak==2）+ 冲刷后设备计数回落基线
     （"区间外不存在"实证，622-630 行）；汇总行 `[RG] frames=60 alias heaps=120 < transients=180,
     split begin/end=60/60, peak alive=2`；通过行 **`D3D12 RG SMOKE OK`**（保留
     RESOURCE/WINDOW SMOKE OK；rgOk 并入 WINDOW 总门）
6. **新单测**（`Native/tests/test_rg_execute.cpp`，3 用例，Mock 后端跑执行器）：
   - 别名真复用：同槽同堆 offset=0、heaps(1)<transients(2)、峰值存活 1<2、冲刷后计数归零
   - split 半边：先 BEGIN_ONLY 后 END_ONLY、内容一致、句柄已替换为当帧真资源
   - 重叠生命周期：异槽异堆（2 堆==2 贴图）、内联屏障句柄全部有效解析
7. **CMake**（`Native/CMakeLists.txt`）：`zrhi_d3d12_window` 增链 `zrender_core`（149 行）；
   /W4 /WX 零警告通过

### 2. 运行时数字（亲跑证据）

- 单测：`Tools/build_native.ps1` ALL GREEN；doctest **60 用例 / 7278 断言全过**
  （既有 57 回归 + 新增 3），`zrender_test.exe` 退出码 0
- 窗口程序（真窗口，非 headless）：`D3D12 RG SMOKE OK` + `D3D12 RESOURCE SMOKE OK` +
  `D3D12 WINDOW SMOKE OK`，退出码 0
  - **别名复用**：60 帧累计 alias heaps=120 < transients=180（每帧 2<3，styleRt/postRt 共槽），
    peak alive=2 < 3（区间外不存在）；每帧冲刷后 imageCount/aliasHeapCount 回落帧前基线
  - **split**：60 帧累计 BEGIN_ONLY=60 / END_ONLY=60（每帧 depth 写→读 1/1）
  - 颜色校验：6/6 readback 全 OK（红/绿/蓝 ±2）；InfoQueue error count=0
    （本机 debug layer 不可用=降级模式，计数恒 0 已在程序内注明）
- headless 回归（手动路径）：WINDOW/RESOURCE SMOKE OK 不受影响

### 3. 施工中发现并修复的问题

1. **PlanBarriers 未记录屏障句柄平行表**：编译期占位句柄（Transient 时为无效值）无法被
   执行器替换 → 补 `perPassImageBatchResources/perPassBufferBatchResources` 填充
   （rg_render_graph.cpp 664-673 行），执行器据此替换（818-831 行）
2. **ReadbackPass/PresentPass 被死剔除**：两者只"读"后台缓冲（Present 归入 readers），非根、
   不可达 → readback 拷贝 0 区域全零。修复：两 Pass `MarkRoot()`（窗口程序 420-431/435 行）
3. 单测图连通性修正：纯读 Pass 链会被剔除，测试图补 Imported 写维持链存活

### 4. 规范符合性

- 只动 `Native/zrender/`、`Native/zrhi/`（接口加法 + D3D12 实现新增）、`Native/tests/`、
  `Native/CMakeLists.txt`、本任务日志；未跑任何 Unity 命令；未 git commit
- /W4 /WX 零警告；UTF-8 无 BOM / LF / #pragma once；zrhi 改动全为加法（57 旧用例回归全过即证）

## 验收清单（主控写）

1. `Tools/build_native.ps1` 全绿（回归+新增单测，用例数写入结论）
2. 亲跑窗口程序：`D3D12 RG SMOKE OK`、颜色校验仍全过、`alias heaps < transients` 数字证据、split begin/end 提交证据
3. split 的 D3D12 flag 映射在 translate 函数（行号）；BEGIN_ONLY/END_ONLY 真实使用（行号）
4. placed resource 创建经 GetResourcePlacementInfo 或等价对齐计算（行号证据）；每别名槽一堆模型注释清晰
5. 执行器设备无关：mock 命令表可跑执行器（新单测证据）
6. zrhi 改动为加法（旧接口零破坏——既有 57 用例回归全过即证）
7. 文件规范+施工报告完整（含运行时数字）

## 验收结论（验收代理写）

**PASS**（验收代理: general-purpose 验收子代理，2026-10-23，独立亲跑+代码审查）

1. **构建全绿**：`Tools/build_native.ps1` ALL GREEN，doctest 60 用例/7278 断言全过（57 回归+3 新增），/W4 /WX 零警告
2. **窗口程序亲跑通过**：真窗口模式（非 headless），`D3D12 RG SMOKE OK`+`RESOURCE SMOKE OK`+`WINDOW SMOKE OK`，退出码 0；汇总行 `frames=60 alias heaps=120 < transients=180 (per-frame 2 < 3), split begin/end=60/60, peak alive=2`；颜色校验 6/6 readback OK（红/绿/蓝 ±2 容差）；InfoQueue error count=0
3. **split flag 映射**：`ZrD3D12_SplitBarrierFlag`（zrhi_d3d12_translate.cpp:79-82，BEGIN_ONLY/END_ONLY 三元映射，唯一转译点）；真实使用在 zrhi_d3d12_commandlist.cpp:74-81（`ResourceBarrierSplit` → `EmitImageBarriers(..., flag)`）
4. **placed resource 对齐**：`GetImagePlacementSize` 用 `GetResourceAllocationInfo`（zrhi_d3d12_device.cpp:345，GetResourcePlacementInfo 语义等价物）；堆尺寸 64KB 对齐（320-321 行，`D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT`）；`CreatePlacedResource`（367 行）；每别名槽一堆模型注释清晰（zrhi_d3d12_device.h:108-117 及 device.cpp 300-312 语义对照块）
5. **执行器设备无关**：`Native/tests/test_rg_execute.cpp` 3 个 TEST_CASE（46/94/139 行）在 Mock 后端（rg_mock_device.h）跑 ExecuteAliased：别名同槽复用 heaps(1)<transients(2)、split 先 BEGIN_ONLY(true) 后 END_ONLY(false) 顺序与内容断言（120-121 行）、重叠生命周期异槽异堆
6. **zrhi 加法式**：git diff 核实 zrhi 全部 7 个文件 0 删除行（zrhi_device.h +42/-0 等）；既有 57 用例回归全过（见第 1 项）双证据
7. **规范+报告完整**：改动仅限允许路径（Native/zrender、zrhi、tests、CMakeLists、本日志）；抽查 6 个改动文件 UTF-8 无 BOM、LF、#pragma once；施工报告四要素齐全含运行时数字、3 缺陷修复记录与"未新建 rg_d3d12_executor.*"二选一说明

备注：git 工作区另有 TASK-A-M0-05 相关 .cs 改动，属 A 轨任务，不在本任务验收范围。
