# TASK-B-R9-11 —— VK 离屏 readback 全零：复现→根因→修复

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理（2026-10-31） | 验收: general-purpose 验收子代理（2026-10-31，PASS 5/5） | commit: （待填）

## 任务简报（主控写）

**目标**：B-R9-10 首次组合出"VK 离屏 lights"并发现 readback 全零（copy 结果不可见，非渲染黑）。当时 VK 有意不接 forceHeadless 以保交付。本任务：**临时实验分支复现→根因→真修→把离屏 VK lights 正式接入**（补全 B-R9-10 的帧耗门禁盲区）。

**背景事实（先读）**：Docs/Logs/TASK-B-R9-10.md §⑤-1 与验收遗留——`--backend vulkan --headless --lights` readback center/strip 全 00（含 alpha）、groundLit=0/anim=0/5；窗口路径正常；zrhi_d3d12_window.cpp ~L8614 注释；D3D12 离屏 readback 正常（对照）。

**设计要点（假设-验证式，报告记录每步）**：
1. **复现**：临时给 VK lights 接 forceHeadless（实验分支，最后保留为正式能力）→ 确认全零复现
2. **根因候选**（按概率排序逐个排除）：
   - 离屏路径 VK 用的渲染目标与 readback copy 的源不一致（D3D12 离屏走代理 RT，VK 离屏是否真渲染到被 copy 的 image？）
   - **image layout 不对**：copy src 要求 VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL——渲染后 image 停在 COLOR_ATTACHMENT_OPTIMAL 而未转布局（D3D12 无此概念，最可能）
   - copy 命令提交到的 queue/fence 与渲染不同步（读早了）
3. **修复后验收**：`--backend vulkan --headless --lights` 全绿（groundLit/anim 达标）+ forceHeadless 正式接入（B-R9-10 的 bench 门禁补上 VK 离屏帧耗指标——**门禁指标从 6 扩 7**，更新 bench_baseline.json 注意容差）+ 窗口路径回归
4. 单测：布局转换/离屏 readback 纯逻辑部分（如可测）
5. 自测：build 全绿（基线 270/717,652 只增不减）；`--backend vulkan --headless --lights` 亲跑全绿；`--lights both --headless` 两侧对照

**约束**：只动 `Native/zrhi/vulkan/`、`Native/tests/`、`Native/CMakeLists.txt`、`Native/tools/`（若需）、`Tools/bench_baseline.json`（新指标）、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF。

**交付物**：根因报告（含每步排除证据）+修复+离屏 VK lights 正式化+门禁扩展+施工报告四节。

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 日期: 2026-10-31 ｜ 状态: 施工完成待验收

### ① 结论一句话

真根因**不在**简报三个候选的任何一个字面（目标一致、layout 合法、同步原语完备），而在同一"同步"家族的更上游：
**R10-03 引入的 VK 帧闸（FrameGate）在离屏路径上永久关闭，所有 `vkQueueSubmit` 被静默跳过**——离屏没有
AcquireNextImage，`BeginFrame` 永不被调，`m_acquired` 恒 false，`CanSubmit()` 恒 false。修复 =
帧闸引入"交换链挂接"状态（detached 设备不受 acquire 闸约束）。修复后 `--backend vulkan --headless --lights`
全绿（groundLit 6/6、anim 3/5、validation errors=0），离屏 VK lights 正式接入 forceHeadless，
bench 门禁 6 扩 7（`frame_ms_avg_vk` 入 `bench_baseline.json`）。

### ② 复现与排除链（假设-验证式，每步有证据）

**第 0 步 · 复现**：VK lights 接入 forceHeadless（临时实验分支，后按简报保留为正式能力）后亲跑
`--backend vulkan --headless --lights`：6 次校验 readback center RGB=(0,0,0)、stripMax=0（含 alpha 全
00）、groundLit=0、anim=0/5、**validation error count = 0**、`VULKAN LIGHTS FAILED` exit 1——与 B-R9-10
记录逐项一致，复现成立。辅助事实：frame_ms avg=3.36ms（若真在渲染，窗口路径同内容 10.4-11.4ms 是
vsync 钉住的，离屏 3.4ms 属"CPU 记录+空等"剖面）。

**候选 A · 渲染目标不一致（拷贝源 ≠ 被渲染图）——排除**：
- 代码层：清屏（`ctx.GetImage(backH)`）、渲染（`offscreenRtv` 即 offscreenTarget 的视图）、RG 屏障解析
  （`resolveImage` → `desc.importedImage`）、拷贝（`ctx.GetImage(backH)`）四方全部经同一 `ZrImage` 句柄
  → 同一 `VulkanImageOf`/`ResolveImage` 查表 → 同一 VkImage；与 D3D12 离屏（工作正常）同构。
- 实证层：修复后 `--lights --backend both --headless` 的 CENTER PIXEL PARITY **maxDelta=0**（离屏双后端
  逐像素一致）、barrier plan EQUAL——若 VK 离屏渲染落在别处，对照不可能逐位相等。

**候选 B · image layout 未转 TRANSFER_SRC——排除**：
- 本机验证层 ENABLED（`VK_LAYER_KHRONOS_validation`，ERROR 级计数即到即计）：拷贝源 layout 非法
  （VUID-vkCmdCopyImageToBuffer 要求 TRANSFER_SRC_OPTIMAL|GENERAL）、屏障 oldLayout 与实际不符、
  image usage 缺 TRANSFER_SRC 位——任一发生都会计为 validation error。实测复现时 **errors=0**。
- RG 层：`PlanBarriers` 在 ReadbackPass 前产出 COLOR_ATTACHMENT→TRANSFER_SRC 屏障（在例），
  `EmitBarriers` 1:1 转译为 vkCmdPipelineBarrier——链路在，且被验证层确认合法。
- 决定性反证：**纯缓冲填充探针**（`vkCmdFillBuffer` 0xAA，不涉及任何 image/layout）同样不可见——
  layout 概念无法解释"连 buffer 填充都读不回"。

**候选 C · copy 提交与渲染不同步（读早了）——字面排除，同族定位真根因**：
- 字面形态排除：`queue->Submit` 后 `device->WaitIdle()`=vkDeviceWaitIdle（全队列全工作）；readback 缓冲
  HOST_VISIBLE|HOST_COHERENT 创建期持久映射，无需 invalidate；与窗口路径（工作正常）逐字相同。
- **真根因定位（三步探针，全部亲跑）**：
  1. **0xAA 填充探针**（拷贝前 fill readback 缓冲）：读回仍 (0,0,0) 而非 0xAA——拷贝确实执行（覆盖了
     填充）或根本没执行（新分配内存恰为零），二者待分辨；
  2. **FILL_ONLY 探针**（只填充、不拷贝）：读回仍 (0,0,0)——**本帧记录的任何命令都到不了主机可见内存**
     ⇒ 命令缓冲从未被 GPU 执行 ⇒ 提交被跳过；
  3. clear-only 探针（跳过渲染）同零，与"从未执行"一致，交叉印证。
- 溯源：`SubmitCommandList`（zrhi_vulkan_device.cpp:2001 起）在 `vkQueueSubmit` 前有
  `if (!m_frameGate.CanSubmit()) return;`——R10-03 A-5/A-6 防信号量死锁的闸；`CanSubmit()=m_acquired`
  只被 `BeginFrame`（唯一调用点=交换链 `AcquireNextImage`）置真。**离屏无交换链 ⇒ 闸永久关闭 ⇒ 每帧
  vkQueueSubmit 被静默跳过**；跳过时的诊断 printf 条件是"存在挂起信号量"——离屏恒无，故**零信号、
  零 validation 错误、零告警**（B-R9-10 观察到的"不可见"三特征全部由此解释）。窗口路径有 acquire，
  闸每帧开启，一切正常——这正是"窗口好、离屏全零"的判别根。修复后复跑复现命令，缺陷不可复现。

**附注**：三候选排除不是白做——A/B 的排除证据（同句柄链、验证层 0 错误）是"把嫌疑收敛到提交环节"的
必要步骤；fill 探针是决定性分叉点。

### ③ 修复与正式化（全部落盘，未 commit）

| 文件 | 变更 | 内容 |
|---|---|---|
| `Native/zrhi/vulkan/zrhi_vulkan_frame_gate.h` | 修改 | FrameGate 增 `m_swapchainAttached`（默认 **true**=挂接，保守沿用 R10-03 语义与既有 doctest 钉）+ `SetSwapchainAttached`/`SwapchainAttached`；`CanSubmit()` 改为 `!attached \|\| m_acquired`——detached（离屏）恒可提交（无信号量依赖可卡），attached 路径 R10-03 A-5/A-6 语义逐位不变 |
| `Native/zrhi/vulkan/zrhi_vulkan_device.cpp` | 修改 | ① `Create()` 构造后即 detached（设备生而无交换链）；② `CreateSwapchain` 成功返回前 attached=true；③ `Destroy(ZrSwapchain)` 表空后回 detached。`Recreate()` 内部重建路径不经过这两口，挂接态贯穿重建不受影响 |
| `Native/tests/test_zrhi_vulkan_result.cpp` | 修改 | +2 TEST_CASE（合计 +73 断言：60 来自"60 帧循环不丢帧"钉 + 13 来自两用例的语义断言）："离屏设备提交不受 acquire 闸约束"（60 帧无 BeginFrame 循环不丢帧 + 离屏 OnSubmit 记账 + 离屏不可 Present）、"挂接状态切换"（建链即受闸/首帧 acquire 前不可提交/全销毁即离屏/重挂接后 OUT_OF_DATE 仍拦截——R10-03 回归钉） |
| `Native/tests/zrhi_d3d12_window.cpp` | 修改 | ① `RunLightsVulkan` 接 `forceHeadless`（B-R9-10 的"有意不接"注释替换为指向本报告）；② `RunLightsParity` 同参透传，`--lights --backend both --headless` 两侧同走离屏（简报自测项）；③ main 分发透传 headless；④ VK `--json` 行改为 program=`zrhi_d3d12_window_vk` + 键 `frame_ms_avg_vk/_min_vk/_max_vk/frames_vk`（与 D3D12 行同一解析器不碰撞 frame_ms_avg） |
| `Tools/bench_regression.ps1` | 修改 | 门禁 6 扩 7：MetricDefs 追加 `frame_ms_avg_vk`（kind=time，类默认 +30%）；新伪程序条目 `zrhi_d3d12_window_vk`（args `--backend vulkan --headless --lights --json`）+ exe 名映射（同一二进制）+ 人类可读行 fallback 的 `_vk` 键映射；SelfTest +5 断言（VK 标签过滤/键可读/无跨后端碰撞/7 行全产出/第 7 指标 MISS 行） |
| `Tools/bench_baseline.json` | 修改 | **只增不改**：追加 `"frame_ms_avg_vk": 2.0438`，既有 6 指标值与字段字节不动（B-R9-10 遗留的"安静窗重跑 -UpdateBaseline"仍留给主控，本任务不越权重写） |

**容差依据（简报要求，按 B-R9-10 同法实测）**：`frame_ms_avg_vk` 取 time 类默认 **+30%**。依据：5 次独立
运行 1.9549/2.0301/2.0438/2.1046/2.2818 ms（中位 2.0438，max/min=1.17×）——离散度与 D3D12 离屏
（±13% 实测）同剖面且更紧（同为离屏逐帧 WaitIdle 强同步、无 vsync）；采集侧每程序 3 次取中位吸收单轮
离群（B-R9-10 ②-3 机制沿用）。修复后多次实测 +3.7%/+1.3%/−1.9%/+3.8%，远在带内。

### ④ 自测证据（全部亲跑，2026-10-31）

1. **build_native 全绿**：doctest **272 用例 / 717,725 断言 / 0 failed**（任务基线 270/717,652 → **+2 用例
   /+73 断言**，全部来自 frame_gate 新语义用例；270 基线只增不减）；ctest "100% tests passed"。
   `run_tests -Segments native` → PASS exit 0。
2. **缺陷不可复现**：`--backend vulkan --headless --lights` 修复后 groundLit **6/6**、anim **3/5**（判定线
   ≥60% 恰过）、center 6/6 OK、validation errors=0、`VULKAN LIGHTS OK` exit 0；多次复跑稳定。
3. **lights/headless 矩阵亲跑（2 后端 × 2 形态 + both）**：
   - d3d12×headless `--headless --lights` → D3D12 LIGHTS OK（frame_ms avg=2.045）；
   - vk×headless `--backend vulkan --headless --lights` → VULKAN LIGHTS OK（avg=2.098，errors=0）；
   - d3d12×window `--lights` → OK；vk×window `--backend vulkan --lights` → OK（errors=0）；
   - both×headless `--lights --backend both --headless` → D3D12 OK + VK OK + **RG BARRIER PLAN PARITY
     EQUAL（total=5）** + **CENTER PIXEL PARITY maxDelta=0**（离屏双后端逐像素一致——本任务新增形态）；
   - both×window → OK + EQUAL（total=6）+ maxDelta=0。
4. **窗口路径回归**：`--draw` → D3D12 FIRST DRAW OK；`--backend vulkan --draw` → VULKAN FIRST DRAW OK
   （errors=0）；`--lights` 两后端窗口见上——B-R7-02 验证过的窗口路径零破坏。
5. **bench 门禁 6 扩 7 全链**：
   - `bench_regression.ps1 -SelfTest` → **33/33 ok** exit 0（28 基线 → +5）；
   - 两后端 `--json` 行亲跑：VK 行 `BENCH_JSON {"program":"zrhi_d3d12_window_vk","metrics":{...frame_ms_avg_vk:2.1564...}}`，D3D12 行同格式——同解析器共存、键不碰撞；
   - 7 指标对比表亲跑（-Gate -SkipQuietCheck，安静护栏被并行代理负载顶住时按 B-R9-10 验收同一口径逃生）：
     `frame_ms_avg_vk` 多轮 **+3.8%/+1.3%/−1.9% OK**；
   - **注入回归（V3 式）**：基线 frame_ms_avg_vk→1.0 → gate 表 **RED +107.1%**、FAIL 行点名该指标、
     **真实 exit 1**（`-File` 直调取 $LASTEXITCODE）→ 字节级还原（cp 备份回写）→ 该指标 **OK −1.9%** 且
     不再出现在 RED 列表；还原后残留 RED 仅 sort_ms_a/b（WorkBuddy+并行 ZCode 代理带载 +40~85%，
     B-R9-10 验收 V3 已记录的同款环境性假红，与本任务指标无关）；
   - `run_tests -Segments bench`（report-only）→ 安静护栏真实生效：机器忙 → bench 行 SKIP → 套件
     ALL PASS exit 0（7 段语义零改动）。
6. **三校验器**：arch / config / naming → 全 PASS exit 0（run_tests 实跑）。
7. **文件规范**：动过的 .cpp/.h/.json 经 `git ls-files --eol` 核验全部 **i/lf w/lf + 无 BOM**
   （bench_regression.ps1 为 .ps1 规范例外 CRLF；无新增文件）。

### ⑤ 遗留与风险（如实）

1. **gate 模式 7/7 全绿需要真正安静窗口**：本机当前有并行任务代理常驻（WorkBuddy ~30% + ZCode ~20%×2），
   sort_ms_a/b 被带载 +40~85%（CPU 型指标最先感知）；frame 类两指标（D3D12+VK）在同条件下稳定 OK。
   判定链正确性已由注入回归+还原证明；**7/7 全绿留待安静窗口由验收/主控复核**（B-R9-10 验收 V3 同款口径）。
2. **基线 provenance 未变**：`machine_quiet` 仍为 B-R9-10 的 SKIPPED 标记——本任务只追加第 7 指标，
   未重写既有 6 值；"安静窗重跑 -UpdateBaseline"仍是主控待办（HANDOFF 已记）。
3. **VK `--draw` 离屏路径**：与本修复同根（同闸），修复后理论上同样可用，但无 CLI 可达（交互会话建窗
   必成功），未实测——与任务前状态一致（B-R7-01 起从未有离屏 --draw 证据），仅记账非缺陷。
4. **仓库内并行 WIP**（A-R9-09 的 EnemyBehaviorTree/GameplayFxBindings/任务日志改动）：本代理未触碰；
   `zrender.dll` 为本任务构建产物变更（tracked 二进制，按既有口径提交前 `git checkout --` 丢弃）。
5. **未做**：Unity 五段（editmode/playmode）按任务纪律未跑——本任务零 C#/Unity 改动；arch/config/naming
   三校验器已亲跑 PASS。
6. **诊断探针已全部移除**（0xAA 填充/FILL_ONLY/clear-only/clear-copy 四件临时诊断代码），施工后代码无
   残留（diff 复核）。



## 验收清单（主控写）

- [x] V1 build 全绿（用例数只增不减，数字写入结论）
- [x] V2 亲跑 `--backend vulkan --headless --lights`：全绿（groundLit/anim 达标）——缺陷不可复现
- [x] V3 根因报告评审：排除链是否完整、最终根因与修复的因果是否成立（独立判断）
- [x] V4 bench 基线 7 指标亲跑（VK 离屏帧耗新指标在列且数字合理）
- [x] V5 窗口路径回归（--lights/--draw 亲跑）+文件规范+施工报告四节完整

## 验收结论（验收代理写）

**PASS（5/5）** ｜ 验收: general-purpose 验收子代理 ｜ 日期: 2026-10-31 ｜ 全部亲跑；未跑 Unity（本任务零 C#/Unity 改动）；未 commit。

- **V1 PASS**：`build_native.ps1` 亲跑 ALL GREEN——doctest **272 用例 / 717,725 断言 / 0 failed**（基线 270/717,652 → +2 用例/+73 断言，恰为 frame_gate 两新用例的 7+60 与 6 条语义断言之和）；ctest 亲跑 "100% tests passed, 0 tests failed out of 1"。
- **V2 PASS**：`--backend vulkan --headless --lights` 亲跑 **VULKAN LIGHTS OK** exit 0——center 6/6、groundLit 6/6、anim 3/5（判定线 ≥60% 恰过）、validation error count=0、frame_ms avg=2.070，缺陷不可复现；`--lights --backend both --headless` 亲跑——RG BARRIER PLAN PARITY **EQUAL（total=5）** + CENTER PIXEL PARITY **maxDelta=0**×6（离屏双后端逐像素一致，新对照能力成立）。
- **V3 PASS**：根因链独立评审成立。①**FILL_ONLY 探针决定性逻辑成立**：`vkCmdFillBuffer` 不涉及任何 image/layout，readback 缓冲 HOST_VISIBLE|HOST_COHERENT 创建期持久映射（无需 invalidate），若 GPU 真执行必读回 0xAA——读回 0 ⇒ 本帧命令从未被 GPU 执行 ⇒ 提交被跳过，layout 因素（候选 B）在逻辑上不可能解释"buffer 填充也读不回"；②**代码亲读印证根因**：`SubmitCommandList` 在 `vkQueueSubmit` 前有 `if (!m_frameGate.CanSubmit()) return;`（zrhi_vulkan_device.cpp:2011），`m_acquired` 唯一置真点 = `BeginFrame` ← `OnFrameAcquire` ← `AcquireNextImage`（唯一调用链），离屏无交换链 ⇒ 恒 false ⇒ 静默跳过；跳过诊断 printf 条件为"存在挂起信号量"，离屏恒无 ⇒ 零信号/零 validation 错误/零告警，与 B-R9-10 观察三特征逐项吻合；③**SetSwapchainAttached 语义与 R10-03 逐位不变成立**：默认 `m_swapchainAttached=true`，`CanSubmit()=!attached||m_acquired` 在 attached 路径严格归约为旧式 `m_acquired`；R10-03 的 8 个既有 doctest **零改动**（diff 仅 1 行注释 + 纯新增 2 用例）；设备三口挂接亲读（Create 构造后 detached / CreateSwapchain 成功前 attached / Destroy 表空回 detached），`RecreateSwapchain`→`ZrVulkanSwapchain::Recreate()` 不经过两口、挂接态贯穿重建；detached 恒可提交且无信号量依赖可卡（`m_pendingAcquire` 仅由 AcquireNextImage 登记）——无 R10-03 A-5/A-6 死锁向量；诊断探针 grep 全仓零残留。
- **V4 PASS**：门禁亲跑两轮（`-Gate -SkipQuietCheck`）——**7 指标全在列**，`frame_ms_avg_vk` baseline=2.0438 → current **+2.1% / +13.9% 均 OK**（离屏逐帧 WaitIdle、无 vsync 的 ~2ms 剖面，数字合理）；`--json` 行亲验：VK 行 `program=zrhi_d3d12_window_vk` + `frame_ms_avg_vk` 等 `_vk` 键（实测 1.9886）、D3D12 行同解析器 `frame_ms_avg`——标签与键双隔离不碰撞；**既有 6 值字节不动亲证**（od 对照 HEAD：仅 `1.5505` 后补逗号 + 追加第 7 键，数值字节全同）；SelfTest 亲跑 **33/33 ok** exit 0（28→+5）；FAIL 路径活体生效：环境带载使 sort_ms_a/b 假红 → BENCH_VERDICT FAIL → **真实 exit 1** 亲取（`-File` 直调）→ 判定链正确性成立；sort 假红为 B-R9-10 验收 V3 已记载的同款并行带载环境性 RED，与本任务指标无关。
- **V5 PASS**：矩阵 **8/8 亲跑全绿**——d3d12×headless（D3D12 LIGHTS OK）/ vk×headless（VULKAN LIGHTS OK）/ d3d12×window `--lights`（OK）/ vk×window `--lights`（OK，errors=0）/ both×headless（EQUAL+maxDelta=0）/ both×window（EQUAL+maxDelta=0）+ `--draw` 双后端（D3D12 FIRST DRAW OK / **VULKAN FIRST DRAW OK** errors=0）；文件规范亲核：7 个动过文件 `git ls-files --eol` 全 **i/lf w/lf**（bench_regression.ps1 为 .ps1 规范例外 CRLF）、头 3 字节逐一验**无 BOM**、无新增文件；日志四节齐全（简报/施工报告/验收清单/验收结论）。
- **遗留确认（不阻塞 PASS）**：①7/7 全绿需真正安静窗——本验收两轮 sort_ms_a/b（及一轮 frame_ms_avg）带载假红与施工报告 §⑤-1 记载一致，frame 类指标含本任务 `frame_ms_avg_vk` 两轮均 OK；②基线 provenance 未动（`machine_quiet=SKIPPED` 保留，安静窗 `-UpdateBaseline` 仍为主控待办）；③VK `--draw` 离屏路径无 CLI 可达未实测（与本任务前状态一致，仅记账）；④仓库内并行 WIP（A-R9-09 改动与 zrender.dll 构建产物）本验收未触碰。
