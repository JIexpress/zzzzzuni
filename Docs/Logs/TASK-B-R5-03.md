# TASK-B-R5-03 SSR + 动态 GI 探针（辐射缓存）+ NPR 风格线（R5 收官）

状态: doing | 派发: 2026-10-25 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：R5 最后一块——三个渲染特性（均为方案定的"近似/简化版"，诚实标注与全量版的差距）：①SSR 屏幕空间反射（Hi-Z 加速射线步进+半分辨率）②动态 GI 探针辐射缓存（CPU 逐帧更新的探针网格+PS 三线性插值注入间接光/环境填充）③NPR 卡通渲染变体（量化 ramp+反向壳描边）。完成后 R5 里程碑收口，下一任务即对等闸门。

**设计要点（按简化版实现，报告如实标注局限）**：
1. **SSR**（`ssr_pass`，RG 特性）：
   - 全屏深度→Hi-Z mip 链（RG 内生成，最多 6 级）；射线步进：视空间反射方向，半分辨率 UV 空间 march（固定步数 32+二分细化 4 步）
   - 简化边界（报告写明）：无时间降噪（仅 4tap 空间模糊）、粗糙度低于阈值不采样回落环境项、屏幕边缘 fade
   - 应用目标：地面（低粗糙度材质项）
2. **GI 探针**（`gi_probes`，CPU+PS）：
   - 探针网格 8×2×8（世界空间包围场景），CPU 每帧（或隔 4 帧轮转）为每探针累加：**直接光贡献（方向光 N·L+点光距离衰减，N 用球面平均=常数 1）**→ 探针辐照度 RGB（无遮蔽版，即"辐射缓存简化"）；SH/球谐不做，存 RGB 三元组
   - CB 上传探针数组（512×float4×2）+网格参数；PS：世界位置三线性插值 8 探针 → 作为**间接光/环境填充项**（替代现有常数环境项——pbr/pbr_shadow 材质 PS 环境项改探针采样，报告注明）
   - 单测：探针索引/三线性权重（已知位置断言 8 邻居与权重和=1）；CPU 辐照度累加（单光源已知几何断言）
3. **NPR**（`toon.hlsl` + 描边）：
   - 材质变体：PS 量化 ramp（N·L 3 阶 hard band + 高光阈值二值）——第二 Cube 用 toon 材质
   - 描边：反向壳（顶点沿法线外扩，第二 DrawPass FrontCull 纯色）——复用 draw list 第二次遍历（outline 视图掩码位）
4. 窗口程序 `--r5` 模式（继承 --shadows on 场景）：地面（SSR 反射 Cube 可见）+ PBR Cube + Toon Cube（描边）+ 2 动画点光（GI 探针填充随动画变化）
   - 可证伪读back：①SSR：反射探针点（Cube 正下方地面延长线）SSR on/off 差 ≥8 ②GI：远离直射光的地面角落在光动画两相位差 ≥3 ③Toon：Cube 亮带/暗带两探针点差 ≥40 且带内平坦（带内两点差 <6）
   - 通过行：`D3D12 R5 OK (ssr=N gi=N toon=N)`
5. 单测：级联/探针纯逻辑如上；RG 特性声明（SSR Hi-Z 资源依赖）mock 断言；全部 falsifiable 读back 数值入报告
6. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zrender/`、`Native/shaders/`、`Native/tests/`、`Native/CMakeLists.txt`、`Native/zrhi/`（加法）、本任务日志；**不跑 Unity**；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：三特性（简化版）+`--r5`+可证伪读back+单测+施工报告四节（含每项与全量版差距的诚实清单）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | **完成日期**: 2026-10-25 | **自测**: build_native.ps1 全绿（128 用例 / 704,547 断言，0 失败）；`--r5` 两轮复跑 `D3D12 R5 OK (ssr=134 gi=23 toon=141)`；旧模式回归 4 项全过（见 §4）。

### 一、交付概览

| 交付物 | 文件 | 说明 |
|---|---|---|
| GI 探针纯逻辑 | `Native/zrender/gi_probes.h/.cpp` | 8×2×8 探针网格、三线性 8 邻域权重（和恒 1）、CPU 辐照度累加（方向光常数/点光 window²/(d²+1)，与簇光源衰减式两侧一致）、每 4 帧轮转切片；`GiCbufferLayout` 2080B static_assert 锁定 |
| SSR 纯逻辑 | `Native/zrender/ssr_pass.h/.cpp` | Hi-Z mip 链计划（640×480→6 级末级 20×15）、32 步/4 步二分/4-tap 模糊常量、`SsrTraceConstants` 192B 镜像、`RecordSsrPasses`（RG 声明单一来源：HiZBuild→SsrTrace→SsrBlur→SsrComposite） |
| outline 视图掩码位 | `Native/zrender/draw_list.h` | `kOutlineViewId=5`（bit5=0x20）+ `OutlineViewMask`，与级联阴影位 1..4 正交 |
| zrhi 加法 | `Native/zrhi/zrhi_device.h` + `d3d12/zrhi_d3d12_device.cpp` | ①`rootCbvCount` 上限 4→5（b4=GI 探针数组）；②`ZrGraphicsPsoDesc::srvTableCount`（0/1/2 张 SRV 描述符表 + 静态采样器 s0/s1——SSR trace/合成需同时采样两张纹理）。纯加法，旧字段 `srvTable` 语义不变 |
| shaders | `Native/shaders/`：`pbr_r5.hlsl`、`toon.hlsl`、`toon_outline.hlsl`、`hiz.hlsl`、`ssr_trace.hlsl`、`ssr_blur.hlsl`、`composite.hlsl` | pbr_r5=pbr_shadow+b4 GI 探针环境项（替代常数 ambient，可开关回落）+alpha 输出粗糙度；toon=3 阶 hard band+高光二值；outline=反向壳（VS 法线外扩）；hiz=mip0 拷贝+4 邻域 min；trace=Hi-Z 加速 march；blur=4-tap；composite=场景+反射 |
| 窗口程序 `--r5` | `Native/tests/zrhi_d3d12_window.cpp` | 继承 --shadows on 场景（地面+PBR cube+方向光 CSM）+Toon Cube+2 动画点光；RG 帧：Clear→ShadowCascade×4→Main→Toon→Outline→SSR 四连（经 RecordSsrPasses）→Readback→Present |
| 单测 | `Native/tests/test_gi_probes.cpp`（10 用例）、`test_ssr_pass.cpp`（7 用例）、`test_draw_list.cpp` 增补 outline 掩码 1 用例 | 覆盖简报要点 5 全部清单 |
| CMake | `Native/CMakeLists.txt` | gi_probes/ssr_pass 并入 zrender_core；两个测试文件挂接 |

### 二、简化边界清单（诚实标注：与全量版的差距）

**SSR（全量版 = G-buffer 法线/粗糙度 + 时间降噪 + 波长粗糙 mip 链）**：
1. 无时间降噪/无历史复用——仅 4-tap 空间模糊（对角 ±半 texel），半分辨率下闪烁未抑制；
2. 反射法线取**地平面常数 (0,1,0)**——非 G-buffer 法线；应用目标=地面时精确，其他朝向表面反射方向失真；
3. 粗糙度阈值经 **sceneColor.a 通道近似**（pbr_r5 PS 把 material.a 写进 alpha）——全量版应读 G-buffer roughness；>阈值（0.3）回落 GI 环境项（不采样 SSR）；
4. Hi-Z 存 min 深度 + 步距线性升 mip（每 6 步 +1 级）——非逐样本自适应层级；二分比较用 mip0；
5. 静态采样器统一 **Point/Clamp**（zrhi v1 静态采样器无滤波配置）——反射颜色/场景颜色无双线性，半分辨率+4-tap 模糊部分掩盖；
6. 反射只采一跳（无 ray 反弹），屏幕外内容缺失（边缘 fade 0.08 UV 宽渐隐）。

**GI 探针（全量版 = SH9/SH7 辐射缓存 + 遮蔽/弹射 + 时间平滑）**：
1. 探针辐照度 = **直接光球面平均（N·L 取常数 1），无遮蔽**——探针在阴影内也收全光；
2. **RGB 三元组而非 SH**——方向信息完全丢失，插值结果是"环境填充"而非方向 GI（PS 侧无 per-轴辐射）；
3. 每 4 帧轮转刷新（每探针至多滞后 4 帧），**无时间平滑/无混合**——轮转边界帧可见轻微跳变；
4. 网格 8×2×8 覆盖世界 x∈[−10,10] y∈[−1,3] z∈[0,20]——网格外 clamp 到边缘探针（外推）；
5. GPU 侧三线性 8 tap 在 PS 内联（无探针体积纹理/无插值体积 pass）。

**NPR（全量版 = 材质可配 ramp 图/描边宽度自适应/屏幕空间边缘检测）**：
1. 量化 ramp 为**硬编码 3 阶**（阈值/灰度来自 CB 而非 ramp 纹理）；高光为单一阈值二值（无平滑项）；
2. 风格化光方向 `toonLightDir` **独立于物理方向光**（CB 显式给出）——NPR 解耦选择，非物理一致；
3. toon 物体**不参与 GI 探针/簇光源/SSR 采样**（alpha=1 哨兵把 toon 排除在反射外）；
4. 反向壳描边：顶点法线外扩 0.045m + FrontCull + 深度只测不写——**硬边模型角点法线不连续处壳会裂**（cube.zmesh 面法线复制，轮廓处缝隙像素级不可辨）；描边宽度为世界域常数，非屏幕空间自适应；
5. toon cube 不投影 CSM 阴影（视图掩码未含级联位——简化）。

### 三、探针推导（可复核）

- **相机**：原点朝 +Z，fovY=60°，640×480；像素映射 `px=(ndcX+1)/2·640`、`py=(1−ndcY)/2·480`（与 R5-01 横带/R5-02 阴影探针同约定，本任务两轮实测复核）。
- **SSR 探针 (320,429)**：地面点 (0,−1,2.2)。反射方向 r=(0,1,2.2)（对地平面法线），命中 PBR cube 前面（z=2.7 面，y=0.227∈[−0.5,0.5]）。ndcY=−1/(2.2·tan30°)=−0.787→py=429。实测 off=(109,112,109)（直射地面）vs on=(236,243,243)（含 cube 反射），Δ=134 ≥ 8 ✓。两相位读back均取相位内第 30 帧——动画点光相位相同，对比纯净。
- **GI 探针 (320,304)**：2 动画点光轨道中心 (0,0.9,6.5) 正下地面 (0,−1,6.5)。轨道半径 2.0 → 两光到该点距离恒为 √(2²+1.9²)=2.755（直接光贡献和恒 0.200 线性）——**两相位差值只来自探针填充变化**。ndcY=−0.266→py=304。相位帧 10（33°）vs 30（93°，角差 60°）：实测 (113,112,111)→(127,131,134)，Δ=23 ≥ 3 ✓。
- **Toon 探针**：cube (−2.4,0,4.2)，toonLightDir=(0.3,0.5,−0.81)/0.998（相机侧）。可见面：−z 前面（ndl≈0.81→亮带 1.0）、+x 右面（ndl≈0.30→暗带 0.35，阈值 0.35/0.6）。屏幕：前面可见区 px 0..106、右面 px 106..152（右面更近覆盖）、y=±0.5→py 196..284。亮 (80,240)/(80,255)=(217,89,71)；暗 (125,240)/(125,255)=(76,31,25)；带间 Δ=141 ≥ 40 ✓，带内平坦 Δ=0 < 6 ✓。
- **GI 探针网格**：origin(−10,−1,0)、spacing(2.5,2,2.5)；探针位于单元中心。地面 y=−1 在网格 y 下边界→三线性 y 轴 clamp 到层 0（权重 1）。

### 四、调试修复记录（本任务内发现并修复）

1. **doctest "Expression Too Complex"**（编译错）：`CHECK(a==x && b==y)` 复合表达式超限→拆分为独立 CHECK（test_ssr_pass.cpp）。
2. **探针中心单测断言写错**：内部探针 frac=0 时偏移角**索引**指向邻居（权重为 0）而非全部 clamp 同索引——改为"目标探针权重和=1"断言（首版误写 `indices[c]==target` 全 8 项）。
3. **toonLightDir z 符号反**：首版 (0.3,0.5,+0.81) 照向 cube 背面——可见面（−z 前面/+x 右面）全落暗带，带间差 0。改为 (0.3,0.5,−0.81)（相机侧）后亮带 0.81/暗带 0.30，Δ=141。教训：cube 朝相机面是 **−z 面**（相机在原点朝 +Z）。
4. **GI 探针 py 算错**：首版写 320（实际 (0,−1,6.5) 投影 py=304）——320 处地面在轨道中心外侧，直接光两相位不等会污染对比；修正后差值纯净（且两相位直接光恒等性由轨道几何保证）。

### 五、自测证据

| 项 | 结果 |
|---|---|
| `Tools/build_native.ps1` | ALL GREEN——/W4 /WX 零警告；**128 用例 / 704,547 断言** 0 失败（较 R5-02 的 110/703,853 增量 **+18 用例 / +694 断言**：test_gi_probes 10 + test_ssr_pass 7 + test_draw_list 增补 outline 掩码 1） |
| `--r5` 第一轮 | `D3D12 R5 OK (ssr=134 gi=23 toon=141)`，bandFlat=0，removed=S_OK，InfoQueue=0 |
| `--r5` 第二轮（复跑） | `D3D12 R5 OK (ssr=134 gi=23 toon=141)`——数值逐位可复现 |
| 回归 `--shadows`（对比） | `D3D12 SHADOWS OK (probe delta=29)`（43→14） |
| 回归 `--lights` | `D3D12 LIGHTS OK`（center 6/6 groundLit 6/6 anim 3/5） |
| 回归 `--draw` | `D3D12 FIRST DRAW OK` |
| 回归默认（RG smoke） | `D3D12 WINDOW SMOKE OK` + `RESOURCE SMOKE OK` + `RG SMOKE OK`（别名堆 2<3、split 1/1） |

**未跑任何 Unity 命令**（按简报约束）；未 git commit；动文件清单 = §1 表格范围（Native/zrender、Native/shaders、Native/tests、Native/CMakeLists.txt、Native/zrhi 加法、本日志）。

## 验收清单（主控写）

1. build_native.ps1 全绿（用例数写入结论）
2. 亲跑 `--r5`：`D3D12 R5 OK` 三项数值证据；SSR/GI 开关对比复跑
3. 简化边界诚实清单核对（报告 §，SSR 无时间降噪/GI 无遮蔽无 SH/描边壳法局限——与实现一致不夸大）
4. 探针三线性权重和=1、CPU 辐照度已知几何断言（行号）
5. Toon 量化带内平坦/带间差异断言（读back 逻辑行号）
6. 旧模式回归（--shadows on/--lights 至少两项）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 | **日期**: 2026-10-23 | **结论: PASS**（7/7 项通过，未跑 Unity，未 git commit）

| # | 清单项 | 结论 | 证据（一行） |
|---|---|---|---|
| 1 | build_native.ps1 全绿 | PASS | 亲跑 ALL GREEN（configure+build /W4 /WX+test），doctest `128 passed / 0 failed`、断言 `704547 passed / 0 failed`——与报告 §五 数字一致 |
| 2 | 亲跑 `--r5` 三项达标 | PASS | 两轮复跑均 `D3D12 R5 OK (ssr=134 gi=23 toon=141)`，bandFlat=0，device removed=S_OK，InfoQueue=0；阈值 8/3/40、带内<6 全达标（SSR off/on 与 GI 两相位对比由模式内建双相位构成，读back 明细：off(109,112,109)→on(236,243,243)、gi 帧10(113,112,111)→帧30(127,131,134)） |
| 3 | 探针/Toon 断言与读back 行号 | PASS | 权重和=1：`test_gi_probes.cpp:63`（另 85/140）、已知几何辐照度：方向光常数 `test_gi_probes.cpp:150-173`、点光 window²/(d²+1) 手算断言 `:175-206`（dist2=2.435 自证 `:205`）；Toon 读back 判定：带间 `zrhi_d3d12_window.cpp:4229`、带内平坦 `:4230-4231`、阈值常量 `:2862-2865`、通过行 `:4329-4332` |
| 4 | SSR Hi-Z/步进/RG 单一来源 | PASS | Hi-Z mip 计划 `ssr_pass.cpp:11-32`（640×480→6 级末级 20×15，单测 `test_ssr_pass.cpp:26-41`）；32 步 march `ssr_trace.hlsl:114-144` + 4 步二分 `:128-137`（常量 `ssr_pass.h:33-34`）；`RecordSsrPasses` 定义 `ssr_pass.cpp:50`，被窗口程序消费 `zrhi_d3d12_window.cpp:3682`（RG 声明单一来源成立：单测 mock 断言编译/屏障/执行序 `test_ssr_pass.cpp:105-213`） |
| 5 | 简化边界清单与实现一致 | PASS | 逐条核对属实：SSR 常数法线(0,1,0) `ssr_trace.hlsl:104`、无时间降噪仅 4-tap（`SsrBlurTapOffsets ssr_pass.cpp:34-45`，无历史缓冲代码）、粗糙度走 sceneColor.a（`pbr_r5.hlsl:314` 写 alpha、`ssr_trace.hlsl:96-99` 阈值回落）、Point/Clamp 静态采样器 `ssr_trace.hlsl:39-40`；GI 无遮蔽/无 SH（`gi_probes.cpp:88-113` 仅直接光 RGB 累加，无 SH 项）、8×2×8 clamp 外推（`gi_probes.h:28-31`）；NPR 硬编码 3 阶 `toon.hlsl:63-71`、toonLightDir 独立 CB `toon.hlsl:23`、alpha=1 哨兵 `toon.hlsl:78`、反向壳外扩 0.045m `zrhi_d3d12_window.cpp:3259`+`toon_outline.hlsl:42`、toon 不投影 `zrhi_d3d12_window.cpp:3340`（castShadow=false，掩码无级联位）——无夸大 |
| 6 | 旧模式回归亲跑 | PASS | `--shadows on` → `D3D12 SHADOWS OK (single phase on, probe=14)`（removed=S_OK errors=0）；`--lights` → `D3D12 LIGHTS OK`（center 6/6 groundLit 6/6 anim 3/5） |
| 7 | 文件规范+报告完整 | PASS | 抽查 17 个触碰文件：无 BOM（首字节 "// "）、LF 换行、4 个头文件均 `#pragma once`；.gitattributes `eol=lf` 与磁盘一致；施工报告四节齐全（简报/施工报告含 §一~§五 探针推导可复核/验收清单/本节）；git status 改动范围与简报约束吻合（仅 Native/zrender、Native/shaders、Native/tests、Native/CMakeLists.txt、Native/zrhi 加法、本日志，另 zrender.dll 构建产物为 build 脚本部署动作） |

**验收备注（不阻塞 PASS 的两处观察）**：
1. `Native/shaders/toon.hlsl:15` 头注释写 "CBufToon（176B…）"，实际布局 192B（mvp 64 + model 64 + 4×float4=192，窗口程序 `ToonConstants` static_assert 192 一致）——纯注释陈旧，功能无影响，建议下任务顺手修正；
2. `--r5` 依赖相对路径 `Native/tests/data/cube.zmesh`（`zrhi_d3d12_window.cpp:3998`），必须从仓库根运行，从 bin 目录运行会报 FAILED to open——使用约束，非缺陷。
