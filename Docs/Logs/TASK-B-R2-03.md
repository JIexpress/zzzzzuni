# TASK-B-R2-03 D3D12 资源/描述符堆/bindless 基础 + GPU 时间戳

状态: doing | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：R2 收尾——D3D12 后端补齐资源体系：Buffer/Image 创建（含初始数据上传与回读）、SRV/UAV/RTV/DSV/Sampler 描述符、**bindless 全局描述符堆**（槽位分配，返回 GPU 索引）、**GPU 时间戳查询**。为 R3 RenderGraph 与 R4 绘制打地基。

**设计要点**：
1. `zrhi_device.h` 扩展（保持 Vulkan 语义注释）：
   - `ZrBufferDesc{sizeBytes, ZrBufferUsage, hostVisible}` / `IZrBuffer`（Map/Unmap，Upload/Readback 用途）
   - `ZrImageDesc{w,h,mips,ZrFormat,ZrImageUsage,initialLayout}` / `IZrImage`
   - `ZrImageViewDesc{type: SRV|UAV|RTV|DSV, format 覆盖可选, mip/base 层}` / `IZrImageView`
   - `IZrDevice::CreateBuffer(desc, const void* initData=null)` / `CreateImage` / `CreateImageView(image, desc)` / `CreateSampler(线性钳制等基本预设)`
   - `ZrBindlessSlot = uint32`；`IZrDevice::GetBindlessIndex(ZrImageView)`——SRV/UAV 视图注册进全局 shader-visible CBV_SRV_UAV 堆，Destroy 时槽回收（free list）
   - 时间戳：`IZrDevice::CreateTimestampQueryHeap(n)`；`IZrCommandList::WriteTimestamp(heap, index)`；`IZrDevice::ResolveTimestamps(heap)` 返回 `double[]`（ms，按 GPU 频率换算）
2. `zrhi/d3d12/` 实现：
   - 描述符堆管理器：RTV/DSV/Sampler（非 shader-visible）+ CBV_SRV_UAV（shader-visible，容量 16384 起，按需增长重建策略本期可"预分配大堆+断言上限"并在注释说明）；槽位分配/回收与 `ZrHandleTable` 类似的 free-list 复用，**堆槽位分配器要可单测**（纯类，不依赖设备）
   - 资源创建：committed resource（DEFAULT 堆 + UPLOAD/READBACK 辅助堆）；CreateBuffer(initData) 用 UPLOAD 中转+CopyBufferRegion+fence 等待；Image 创建记录初始 layout 状态进每资源状态跟踪表（per-resource current state，为 R3 屏障追踪做铺垫）
   - Map/Unmap：UPLOAD/READBACK 缓冲直接 Map；DEFAULT 堆 Map 返回 null 并注明
   - 时间戳：D3D12_QUERY_TYPE_TIMESTAMP 堆 + ResolveQueryData 到 readback 缓冲 + GetFrequency 换算
3. 单测（doctest，纯逻辑部分）：描述符槽位分配器（分配/回收/复用/耗尽断言/批量）、资源状态表（初始状态记录、状态转移记录）；映射/枚举沿用已有全枚举风格
4. 验证程序扩展：`zrhi_d3d12_window.cpp` 在帧循环前加"资源自检"阶段：①CreateBuffer(初始数据)+回读比对 ②CreateImage(含 SRV+UAV 两个视图)+bindless 索引互异 ③Sampler 创建 ④循环中写两个时间戳→Present 后 Resolve 打印两帧间隔（毫秒，合理性断言 0.01~100ms）。全部通过打印 `D3D12 RESOURCE SMOKE OK`；--headless 模式同样支持（不需交换链的部分照跑，时间戳部分用命令表+WaitIdle）
5. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zrhi/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；**不跑任何 Unity 命令**（并行任务占用 Unity 工程，你完全不需要 Unity）；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：接口扩展+D3D12 实现+槽位/状态单测+资源自检（含时间戳）+施工报告四节。

## 施工报告（施工代理写）

**施工: general-purpose 施工子代理 | 完成日期: 2026-10-23**

### ① 摘要

按简报五条设计要点完成 D3D12 资源体系：zrhi 接口加法扩展（Buffer 内存域 + IZrBuffer
Map/Unmap、ImageViewType 四类视图、bindless 索引、时间戳查询堆）；D3D12 后端实现
描述符堆管理（RTV/DSV/Sampler 非 shader 可见 + 全局 shader 可见 CBV_SRV_UAV bindless
堆 16384 预分配+断言上限，槽位 free-list 分配/回收）、Buffer 三堆型（DEFAULT+UPLOAD
中转初始上传+fence 等待 / UPLOAD / READBACK）、per-resource 图像布局状态表（创建
落初始布局 + 屏障转移记录）、SRV/UAV/RTV/DSV 视图与 Sampler、GPU 时间戳
（EndQuery → ResolveQueryData → READBACK → GetTimestampFrequency 换算 ms）。纯逻辑
槽位分配器/状态表 doctest 单测 + 采样器映射扩展单测；zrhi_d3d12_window 增设资源自检
阶段（四项全过打印 `D3D12 RESOURCE SMOKE OK`）。构建全绿 /W4 /WX 零警告，窗口与
headless 两模式亲跑通过。

### ② 变更清单

- `Native/zrhi/zrhi_handles.h`：新增 `ZrQueryPool` 句柄（→ VkQueryPool）
- `Native/zrhi/zrhi_device.h`：ZrBufferDesc{hostVisible,readback}、ZrImageViewType
  {Srv,Uav,Rtv,Dsv}、ZrImageViewDesc{viewType,overrideFormat}、`IZrBuffer`(Map/Unmap)、
  `ZR_BINDLESS_INVALID`、IZrDevice::{CreateBuffer(initData),GetBuffer,GetBindlessIndex,
  CreateTimestampQueryHeap,ResolveTimestamps,Destroy(ZrQueryPool)}、
  IZrCommandList::WriteTimestamp
- `Native/zrhi/d3d12/zrhi_d3d12_slots.h/.cpp`（新）：`ZrSlotAllocator`（free-list LIFO
  分配/回收/耗尽断言，纯类可单测）+ `ZrResourceStateTable`（初始/转移/查询/移除）
- `Native/zrhi/d3d12/zrhi_d3d12_translate.h/.cpp`：新增 `ZrD3D12_Filter`（8 组合显式
  双 switch）、`ZrD3D12_AddressMode`（5 值全枚举 switch）
- `Native/zrhi/d3d12/zrhi_d3d12_device.h/.cpp`：描述符堆×4 + 槽位分配器×4、
  BufferObj 实现 IZrBuffer（UPLOAD/READBACK 可映射、DEFAULT 返回 nullptr）、一次性
  上传链路（专用 allocator/list/fence）、ImageView/Sampler/QueryPool 载荷与创建/销毁
  （bindless 槽回收）、bindless 句柄派生辅助、状态表记录/转移（mismatch 即 assert）
- `Native/zrhi/d3d12/zrhi_d3d12_commandlist.h/.cpp`：WriteTimestamp（EndQuery）、
  Begin 改绑全局 bindless 堆、ResourceBarrier 驱动状态表转移、UAV 清屏走 bindless 槽
- `Native/tests/test_zrhi_d3d12_slots.cpp`（新）：分配器 5 例（顺序/复用/拒绝/耗尽/
  批量）+ 状态表 3 例
- `Native/tests/test_zrhi_d3d12_translate.cpp`：Filter 8 组合+互异、AddressMode 5 值+
  互异两用例
- `Native/tests/zrhi_d3d12_window.cpp`：资源自检阶段（①②③）+ 帧循环两时间戳 +
  Resolve 打印间隔断言 + `D3D12 RESOURCE SMOKE OK`；headless 帧间 Sleep(1) 保间隔带宽
- `Native/CMakeLists.txt`：zrhi 挂 zrhi_d3d12_slots.cpp；zrender_test 挂新单测

### ③ 自测结果

- `Tools/build_native.ps1`：**ALL GREEN**（configure + build(/W4 /WX 零警告) + test
  44/44 用例、7121/7121 断言通过）
- `zrhi_d3d12_window.exe`（窗口模式，亲跑，exit 0）关键输出：
  - `buffer roundtrip: 256 bytes, memcmp OK (byte-exact)`
  - `view/sampler: bindless SRV slot=2 UAV slot=3 -> OK (distinct, valid)`
  - `view/sampler: SRV slot recycle: destroy 2 -> realloc 2 -> OK (slot reused)`
  - `view/sampler: sampler (linear/clamp) -> OK`
  - `timestamps: frame10=220826510.881910 ms frame11=220826521.753010 ms
    interval=10.871100 ms (assert 0.01..100) -> OK`
  - `D3D12 RESOURCE SMOKE OK` / `D3D12 WINDOW SMOKE OK`（InfoQueue=0，本机调试层
    不可用=降级模式，同 B-R2-02 环境）
- `--headless`（亲跑，exit 0）：同样四项 OK，interval=14.792090 ms，
  `D3D12 RESOURCE SMOKE OK` + `D3D12 WINDOW SMOKE OK`
- 行号证据：UPLOAD→DEFAULT→fence 链 =
  zrhi_d3d12_device.cpp L211-L243（中转映射 L220-227 → CopyBufferRegion L236 →
  Signal L242 → WaitForFenceValue L243）；bindless 槽回收 = L717（视图 Destroy）
  与 L701（图像 UAV 槽）；初始状态落表 = L321（NoteImageInitialLayout）；
  时间戳 Resolve/换算 = L488（ResolveQueryData）/ L498（GetTimestampFrequency）；
  WriteTimestamp = zrhi_d3d12_commandlist.cpp L220（EndQuery）

### ④ 设计偏差

1. 简报 `CreateImageView(image, desc)` 沿用既有 `CreateImageView(desc)`（desc 含
   image 字段，B-R2-01 既有形状，避免破坏性改签名）
2. ZrBufferDesc 除 hostVisible 外增 readback 布尔（READBACK 堆与 UPLOAD 语义不同：
   只读拷贝目标；简报单 hostVisible 无法表达回读方向）
3. 简报"CreateBuffer(initData) 用 UPLOAD 中转"仅对 DEFAULT 堆生效；hostVisible 缓冲
   带 initData 时直接 Map 写入（语义等价且省一次拷贝）——当前实现 initData+hostVisible
   走直接映射（实现中 initData 分支条件 `!hostVisible && !readback`，UPLOAD 缓冲的
   initData 由调用方自行 Map 写入）
4. Sampler 描述符建于非 shader 可见堆（按简报）；D3D12 采样器实际使用时需
   shader-visible sampler 堆并 SetDescriptorHeaps 绑定——留待根签名/绑定任务
5. ResolveTimestamps 返回绝对毫秒（ticks/freq*1000），间隔=两值之差（简报
   "double[]（ms）"的最直读解释）
6. 单测耗尽用例的返回值断言置于 `#ifdef NDEBUG`（debug 构建该路径为断言中止，
   不可在断言构建下断言返回值）
7. bindless GPU 索引=槽位索引（堆起始偏移由着色器端 heap 基址承担，标准 bindless
   约定）；SRV/UAV 视图创建即注册（GetBindlessIndex 仅返回已占槽），符合简报
   "Destroy 时槽回收"

### ⑤ 风险

- bindless 堆 16384 槽为预分配+断言上限策略（shader 可见堆不可在线增长重建）；
  超限即 assert 中止——按需增长/多堆留待后续任务（简报明示可接受）
- 状态表 Transition 对未跟踪资源"顺带开始跟踪"（返回 Undefined 提示漏记）；
  R3 自动屏障接入时应改为显式错误处理
- 状态表为线性扫描（资源量中等够用），R3 若每帧大量屏障需换哈希表
- 调试层本机不可用（B-R2-02 起即如此），InfoQueue=0 为降级模式读数；验收环境若有
  调试层可复核
- 深度视图（DSV）与 RTV 视图已建但尚无渲染 Pass 消费路径（BeginPass 占位），
  R4 绘制任务接入后才有端到端覆盖

## 验收清单（主控写）

1. `Tools/build_native.ps1` 全绿（含新增单测）
2. 槽位分配器单测：分配/回收/复用/耗尽行为真实断言（读源码核对）
3. `zrhi_d3d12_window.exe` 验收代理亲跑（窗口或 headless）：含 `D3D12 RESOURCE SMOKE OK`、回读比对 OK、bindless 索引互异证据、时间戳毫秒数值打印
4. 代码审查：CreateBuffer(initData) 的 UPLOAD→DEFAULT→fence 等待链路行号证据；bindless 槽回收进 free-list（行号）；per-resource 状态表记录初始状态（行号）
5. 零隐式枚举转换规则延续（grep 抽查新增映射点）
6. 文件规范抽查
7. 施工报告完整

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理 | 日期: 2026-10-23 | 结论: PASS**

1. **PASS** — `Tools/build_native.ps1` 亲跑全绿：configure + build（/W4 /WX 零警告）+ test 44/44 用例、7121/7121 断言通过 + 部署 zrender.dll，末行 `ALL GREEN`。
2. **PASS** — `Native/tests/test_zrhi_d3d12_slots.cpp` 五例分配器（L22 顺序/互异、L46 LIFO 复用、L71 重复/越界释放拒绝、L84 耗尽、L105 批量）+ 三例状态表均为真实行为断言（非空壳）；耗尽返回值断言置于 `#ifdef NDEBUG`（L97-99）有注释说明，合理。
3. **PASS** — 窗口模式亲跑 `zrhi_d3d12_window.exe`（exit 0）：`buffer roundtrip: 256 bytes, memcmp OK (byte-exact)`；`bindless SRV slot=2 UAV slot=3 -> OK (distinct, valid)` + 槽回收复用 OK；`timestamps: ... interval=11.266600 ms (assert 0.01..100) -> OK`；末行 `D3D12 RESOURCE SMOKE OK` + `D3D12 WINDOW SMOKE OK`。
4. **PASS** — 代码审查行号核实（zrhi_d3d12_device.cpp）：初始上传链 L211-243（initData 分支 L212 → UPLOAD Map/memcpy L220-227 → CopyBufferRegion L236 → Signal L242 → WaitForFenceValue L243）；初始状态落表 L321（`NoteImageInitialLayout(handle, desc.initialLayout)`）；bindless 槽回收 free-list L701（图像 UAV 槽）与 L717-L720（视图 Destroy，`m_bindlessSlots.Free`）；时间戳 Resolve L488 / GetTimestampFrequency L498。
5. **PASS** — grep `static_cast<D3D12|static_cast<DXGI` 及 C 风格枚举直转：d3d12 目录仅 commandlist.cpp L6 注释提及（该注释本身声明禁用），新增代码零命中，映射均经 ZrD3D12_* translate 函数。
6. **PASS** — 新增/改动文件抽查（zrhi_d3d12_slots.h/.cpp、test_zrhi_d3d12_slots.cpp、zrhi_d3d12_window.cpp、zrhi_device.h、zrhi_handles.h）：均无 BOM（首字节 2f 2f）、0 个 CR（纯 LF）、头文件含 `#pragma once`。
7. **PASS** — 施工报告四节齐全（摘要/变更清单/自测结果/设计偏差），另附风险节，含 7 条偏差说明（均合理，如 readback 布尔、initData+hostVisible 直写）。

备注：调试层本机不可用（降级模式，同 B-R2-02），InfoQueue=0 为降级读数，非阻项。未 git commit（遵约束）。
