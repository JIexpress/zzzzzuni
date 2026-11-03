# TASK-B-R4-02 共享可见性 + 64 位 sort key + 绘制列表编译（radix 排序/状态聚簇合批）

状态: doing | 派发: 2026-10-24 | 施工: general-purpose 施工子代理（2026-10-24） | 验收: general-purpose 验收子代理（2026-10-23） | commit: （待填）

## 任务简报（主控写）

**目标**：R4 核心——"剔除、排序、批处理一体化"的 CPU 侧：多视图**共享一份可见性数据**（视图掩码过滤，杜绝重复剔除）；绘制项编码 **64 位 sort key**（状态聚簇成为排序天然产物）；**LSD 基数排序**（零分配、O(n)）；按状态聚簇合并成批（indirect draw 参数包 + drawID 表）。真 GPU 绘制（PSO/着色器）是下一任务，本任务产物全量单测。

**设计要点**：
1. `Native/zrender/draw_list.h/.cpp`（新）：
   - **可见性共享**：`VisibilitySet`：`AddItem(renderItemId, viewMask)`；`Iterate(viewIndex, fn)` 只遍历含该视图位的项（内部可建按视图的桶索引或位扫描——实现自定，报告说明选型）；常量 `kMaxViews=8`（viewMask uint8）
   - `RenderItem{meshHandle, materialId, shaderId, lod, float depth, bool translucent}`（depth=视深，供排序）
   - `DrawList::Add(item, viewMask)`：可见性集+待排序数组
   - **64 位 key 编码**（doc 注释位布局）：
     - 不透明：`[63]=0 | [62:39] 深度前→后 24bit(量化) | [38:31] shader 8b | [30:15] 材质 16b | [14:8] mesh/lod 7b | [7:0] 序号`
     - 半透明：`[63]=1 | 深度后→前（同字段反序量化）| 其后同上`
   - **排序**：LSD radix sort（8 轮×8bit，桶计数数组复用，**零堆分配**——预分配容量，Add 超容量报错或增长策略二选一报告说明）；正确性单测与 std::sort 参照对拍（随机 10 万项）
   - **合批**：排序后扫描相邻项 shader+material 相同且同 mesh 连续→合并 `Batch{startIndex,count,shaderId,materialId,meshHandle,drawIdBase}`；输出 `ZrIndirectDrawArgs` 数组（VertexIndex/InstanceCount/StartVertex/StartInstance——本任务填充数值即可）+ drawID 表（batch 内每个实例一个 drawID）
2. `Native/zkernel/` 不动；本任务纯 CPU 逻辑，不碰 zrhi/d3d12
3. doctest（`tests/test_draw_list.cpp`）：
   - 可见性：多视图掩码过滤（3 视图交叠场景，各视图遍历集合精确断言）
   - key：手工构造已知项断言编码值/相对序
   - 排序：不透明按深度前→后、同深度按 shader 聚簇；半透明反序；与 std::sort 对拍 10 万随机项一致；零分配（排序函数无 new/malloc——代码审查+可断言容量不增长）
   - 合批：N 个同状态项→1 批；状态切换处断批；drawArgs 数值正确；drawID 连续
4. CMake 挂接；/W4 /WX 零警告；基准（可选加分）：`zkernel_bench` 同级加 10 万项 Add+Sort+Batch 毫秒数入报告

**约束**：只动 `Native/zrender/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；**不跑 Unity**；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：DrawList 三层+单测全绿+（可选）基准+施工报告（含位布局说明、排序对拍结果、批合并示例数字）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | 日期: 2026-10-24 | 构建: Release /W4 /WX 零警告全绿

### 1. 交付物

| 文件 | 内容 |
|---|---|
| `Native/zrender/draw_list.h`（新） | VisibilitySet / RenderItem / ZrIndirectDrawArgs / ZrDrawBatch / DrawList（位布局 doc 注释在文件头） |
| `Native/zrender/draw_list.cpp`（新） | key 编码 / LSD radix 排序（零分配）/ 状态聚簇合批 + drawArgs + drawID 表 |
| `Native/tests/test_draw_list.cpp`（新） | doctest 12 用例（可见性/key/排序对拍/零分配/合批/容量） |
| `Native/tests/zrender_bench.cpp`（新） | 10 万项 Add+Sort+Batch 基准（QPC，预热 2 轮 + 10 轮均值） |
| `Native/CMakeLists.txt` | draw_list.cpp 并入 zrender_core；test_draw_list.cpp 并入 zrender_test；新增 zrender_bench（zkernel_bench 同策：不进 ctest，仅 Release 默认构建） |

zkernel/zrhi/d3d12 零改动；未跑任何 Unity 命令；未 git commit。

### 2. 关键设计与选型说明

**64 位 sort key 位布局**（`draw_list.h` 文件头 + `draw_list.cpp` EncodeSortKey，`draw_list.cpp` L23-L27 有 static_assert 钉死布局）：

```
[63]    透明标志：0=不透明 / 1=半透明（不透明整体排前）
[62:39] 深度 24bit：FloatToSortableBits（IEEE-754 flip：负数取反/正数置高位）
        右移 8 位取高 24bit，任意 float 的无符号序=数值序
        不透明=原值（近→小，排序后前→后，early-z 友好）
        半透明=0xFFFFFF-原值（远→小，排序后后→前）
[38:31] shaderId 低 8bit（状态聚簇第一位）
[30:15] materialId 低 16bit（状态聚簇第二位）
[14:8]  mesh 低 5bit <<2 | lod 低 2bit（截断，仅作聚簇提示）
[7:0]   序号（Add 顺序低 8bit，同量化深度平局保持插入序）
```

**可见性选型**：`Iterate` 用位扫描（线性扫全表 + 掩码位测试）而非按视图桶索引——每帧每视图一次 O(n) 顺序扫描对缓存友好，无需维护/重建桶结构；桶索引仅在"视图多且反复迭代"场景占优。kMaxViews=8，viewMask uint8（bit i=视图 i）。

**容量策略**（二选一取"超容量报错"）：构造时一次性预分配项/key/排序乒乓缓冲/批/drawID 全部缓冲，`Add` 超容量返回 false——换取 Sort/BuildBatches 路径可证明零堆分配（定容不增长）。

**可见性录入双路径**（施工中发现并修复的性能缺陷）：`VisibilitySet::AddItem` 带 O(k) 线性查重合并（适合外部零散录入）；`DrawList::Add` 走新增的 `AppendItem`（O(1) 追加，id=插入序号唯一）。修复前 DrawList::Add 逐项线性查重 → 10 万项 Add 2374ms（O(n²)）；修复后 1.2~1.9ms（约 1800 倍）。

**drawArgs 填充**：`vertexIndex`=每实例顶点数（经 `RegisterMesh(meshHandle, vertexCount)` 登记，未登记为 0；真实网格顶点数衔接归下一任务/资产管线）；`instanceCount`=批内项数；`startVertex` 本任务恒 0（顶点池编排归下一任务）；`startInstance`=该批 drawIdBase。

### 3. 自测结果

- `Tools/build_native.ps1` 全绿（configure + build /W4 /WX 零警告 + doctest + dll 部署）：**84 用例 / 307712 断言，0 失败**（原基线 70 用例 / 7322 断言，新增 14 个 draw_list 用例；断言量大来自 10 万项逐项 REQUIRE）。
- **对拍**：10 万随机项（shader/material/mesh 32bit 全域、深度 0.5 网格平局、透明各半）radix 结果与 `std::sort` 参照**逐项一致**；且排序后每项重编码（抹序号字节）与 key 一致。
- 双向深度序：不透明近→远 / 半透明远→近 / 不透明整体在前，均有精确断言；量化平局（1.0f 与 0x3F800001 位模式同 24bit 桶）保持插入序有断言。
- 零分配证据：排序前后 `Capacity()`/`Items()`/`Keys()` 指针不变（断言）；`Sort()` 代码路径仅触碰定容成员数组与栈上 256 项计数数组（每轮 memset 复用），无 new/malloc/临时容器（行号：draw_list.cpp Sort 全函数，乒乓缓冲 m_keysTmp/m_itemsTmp 构造时 resize）。
- 合批断言：6 项（3+1+2 三种状态）→ 3 批且各批字段/序精确；深度交错使同状态分段 → 断 3 批；drawArgs 五数值全对；drawID 跨批全局连续 0..N-1；超容量 Add 拒绝且状态不变；Reset 后复用零分配。

### 4. 基准（10 万项，Release /O2，QPC 均值 10 轮，两轮运行）

| 场景 | Add | Sort | Batch | 总计 | 批数 | 省掉绘制调用 |
|---|---|---|---|---|---|---|
| A 全域随机（深度[0,1000]） | 1.24~1.31ms | 3.75~5.93ms | 1.50~1.52ms | 6.6~8.7ms | 100000 | 0（0%） |
| B 小状态域 4 shader/8 材质/4 网格（深度[0,10]） | 1.35~1.88ms | 3.83~4.09ms | 0.38~0.44ms | 5.9~6.1ms | **5120** | **94880（94.9%）** |

量化观察：深度是 key 首位字段，状态聚簇只在同量化深度桶内发生——**每深度桶项数必须显著大于状态域才有可观合批率**（A 场景全精度深度下合批率趋零是设计使然，非缺陷；真实帧可按场景调深度量化粒度）。LSD radix 10 万项约 4~6ms（8 轮 O(n)）。

### 5. 施工中发现并修复的缺陷

1. **key 移位常量标反**：初版把 shader 编进 [22:15]、材质编进 [38:23]，与简报位布局不符——被"已知项编码值逐字段核对"用例当场抓出（doctest 数值比对），按简报修正为 material@15 / shader@31（自低位推导并有 static_assert 钉死）。教训：手算期望值用例对位布局类代码是必要防线。
2. **可见性录入 O(n²)**：见 §2 双路径说明，基准从 2374ms 降到 1.2ms。

### 6. 遗留与建议

- `ZrIndirectDrawArgs.startVertex` 恒 0、`vertexIndex` 依赖 RegisterMesh 登记——真实值应来自下一任务 GPU 提交时的顶点池编排/资产管线，届时可去掉登记 API。
- 分视图剔除（frustum/occlusion 产出 viewMask）与 per-view 批编译是天然下一步；本任务 VisibilitySet 已按 8 视图共享一份数据留好接口。

## 验收清单（主控写）

1. build_native.ps1 全绿（用例数写入结论）
2. 可见性三视图交叠过滤精确断言用例（读源码核对）
3. key 位布局与简报一致（头文件 doc+代码移位行号）；不透明/半透明双向深度序断言存在
4. radix 与 std::sort 对拍 10 万项一致用例；排序路径零堆分配（行号审查）
5. 合批断言：同状态合 1 批/状态切换断批/drawArgs 数值/drawID 连续
6. （若做基准）10 万项 Add+Sort+Batch 毫秒数已录报告
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理（独立执行，与施工不同代理，未修改任何代码，未跑 Unity，未 git commit）| 日期: 2026-10-23

**结论: PASS**（7/7 项通过）

1. **构建全绿 PASS**——验收代理独立运行 `Tools/build_native.ps1`：configure+build（/W4 /WX 无警告输出）+ doctest **84 用例 / 307712 断言 / 0 失败**，Status: SUCCESS，zrender.dll 部署完成；用例数与施工报告一致（基线 70 + 新增 14）。
2. **可见性三视图交叠 PASS**——`Native/tests/test_draw_list.cpp` L29-51 用例"三视图交叠掩码过滤"：collect(0)=={0,1}、collect(1)=={0,2}、collect(2)=={0,2,3} 逐视图集合精确断言（vector 相等），另 L53-72 掩码 OR 合并/零掩码拒绝/越界视图防御、L74-86 DrawList 可见性转发。
3. **key 位布局 PASS**——`draw_list.h` L13-23 doc 与简报逐字段一致；`draw_list.cpp` L35-38 移位常量（material@15/shader@31/depth@39/透明@63，自低位推导）+ L39 static_assert 钉死；编码 L57-64 与布局对应；已知值逐字段核对用例 L92-117；双向深度序断言存在 L119-143（不透明近<远 / 半透明远<近 / 不透明整体在前）。
4. **对拍+零分配 PASS**——对拍用例 L223-258：kN=100000 随机项（shader/material/mesh 全域、0.5 深度网格平局、透明各半）radix 结果与 std::sort 参照逐项 REQUIRE 相等 + 排序后重编码核对；零分配行号审查：`Sort()`（draw_list.cpp L118-173）仅触碰构造时定容的 m_keys/m_keysTmp/m_items/m_itemsTmp（L75-78）与栈上 counts[256]（L130，每轮 memset 复用），无 new/malloc/临时容器；跳轮奇偶由 L168-171 memcpy 兜底（正确处理全零字节轮不交换的奇偶破坏）；指针/容量不变断言 L260-281。
5. **合批断言 PASS**——L287-340：6 项（S1/MeshA×3、S1/MeshB、S2/MeshA×2）→ 3 批，批字段（startIndex/count/shaderId/materialId/meshHandle/drawIdBase）与 drawArgs 数值（vertexIndex 8/16、instanceCount、startVertex=0、startInstance）全精确；L342-363 深度交错同状态分段断 3 批；L365-393 drawID 跨批全局连续 0..N-1 且 startInstance==drawIdBase 闭环。
6. **基准已录 PASS**——报告 §4 含两场景 Add/Sort/Batch 毫秒与合批节省率；验收代理独立复跑 `zrender_bench.exe`：B 场景 batches=5120、drawCallsSaved=94880（94.9%）与报告完全一致，A 场景 add=1.37/sort=5.94/batch=1.62ms 在报告区间的机器噪声范围内。
7. **规范+报告 PASS**——4 个新文件（draw_list.h/.cpp、test_draw_list.cpp、zrender_bench.cpp）核验：UTF-8 无 BOM（首 3 字节均为 "// "）、0 个 CR 字节（纯 LF）、draw_list.h L32 #pragma once；CMakeLists.txt L67（zrender_core）/L121（zrender_test）/L152-154（zrender_bench 同 zkernel_bench 策）挂接正确；日志四节齐全（本节即第 4 节）；zkernel/zrhi 零改动（构建目标列表无新增改动源）。

备注：施工自修 2 缺陷（key 移位反、可见性 O(n²)）已在现源码中确认修复（L35-36 布局与 L68-75 AppendItem O(1) 路径），修复过程记录于报告 §2/§5，不影响验收。
