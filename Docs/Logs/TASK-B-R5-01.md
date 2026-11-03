# TASK-B-R5-01 簇光源系统 + PBR 前向光照（多光源真渲染）

状态: done | 派发: 2026-10-24 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理（PASS 7/7） | commit: 170ad0b

## 任务简报（主控写）

**目标**：R5 第一块——实时光照：CPU 侧簇（Cluster）光源分配（视空间 3D 簇网格，光源按簇裁剪）+ PS 侧 PBR（GGX 高光+Lambert 漫反射，方向光+点光源数组）；窗口程序 `--lights` 模式渲染地面+立方体+动画点光源阵列。

**设计要点**：
1. `Native/zrender/cluster_lighting.h/.cpp`（纯 CPU，可单测）：
   - `LightData{pos[3], radius, color[3], intensity, kind}`（kind: 0=directional 1=point；方向光 pos=方向）打包为 float4×2（GPU 布局，static_assert 32B）
   - `ClusterGrid{dimX=16, dimY=9, dimZ=16, nearZ=0.3, farZ=60}`；`Assign(viewProj 近似/或视空间参数, lights)` → 每簇 `start/count`（uint32×1 打包 count<<16|start）→ 输出平坦数组（2304 簇）
   - 簇-光源相交：点光=球与簇 AABB 相交（视空间）；方向光写全簇
   - 单测：单光源命中簇集合精确断言（构造已知几何）；两光源 start/count 不重叠分配；AABB 相交边界用例；簇覆盖屏幕完整（每像素有归属）
2. `zrhi` 不新增类型（用既有 CB+PSO）；常量布局：
   - CB0：mvp/invProjParams/相机参数 + 簇网格参数 + 光源数
   - CB1：光源数组 float4×2×[32]（超 32 报错）
   - CB2：簇 start/count 数组（2304×4B=9KB，OK）
3. `Native/shaders/pbr_forward.hlsl`：像素级簇索引（屏幕→XY 簇 + 线性深度→Z 簇）→ 读 range → 循环点光（GGX NDF+Smith 可见性+Lambert，PBR 参数常量简化：albedo/roughness 每物体常量）+方向光；HDR 输出前 Reinhard 简易 tonemap（注明后续 Volume 替代）
4. 窗口程序 `--lights` 模式：程序化生成地面 quad VB（C++ 直接构造顶点，20×20m）+ cube.zmesh；1 方向光+8 点光（其中 4 个随帧动画环绕）；中心/四角 readback：随光源动画像素值变化（两帧差>阈值）、地面像素受光（非环境色）；通过行 `D3D12 LIGHTS OK`；InfoQueue 0 错误；60 帧稳定
5. mock/纯逻辑单测（CMake）：簇分配（见 1）；CB 打包大小 static_assert
6. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zrender/`、`Native/shaders/`、`Native/tests/`、`Native/CMakeLists.txt`、`Native/zrhi/`（若需 CB 大小断言等小改，加法）、本任务日志；**不跑 Unity**（并行代理在验收 A 轨）；不 git commit；UTF-8 无 BOM/LF/#pragma once；hlsl 常量缓冲对齐规范（§4）。

**交付物**：簇系统+PBR 着色器+`--lights`+单测+`D3D12 LIGHTS OK`+施工报告四节。

## 施工报告（施工代理写）

施工：general-purpose 施工子代理｜日期：2026-10-24｜自测环境：本机 780M（D3D12 调试层不可用，InfoQueue 如实降级）

### 一、交付物与实现概览（按简报设计要点 1-6）

| # | 要点 | 落地 |
|---|---|---|
| 1 | CPU 簇分配（纯 CPU 可单测） | 新增 `Native/zrender/cluster_lighting.h/.cpp`：`LightData`（创作结构）/`PackedLight`（GPU 布局 float4×2=32B，`static_assert` 锁定 h:64）；kind 编码进 `posRadius.w` 符号（<0=方向光哨兵，`PackLights` cpp:10）；`ClusterGrid`（dim 16×9×16 / nearZ 0.3 / farZ 60，h:31-40 常量）；`ClusterAabb`（视空间子视锥体段 AABB，cpp:34 起）+`SphereAabbIntersect`（最近点距离平方，相切=相交，cpp:79）；`AssignLightsToClusters`（两遍前缀和，方向光写全簇且排在段首，输出确定序，cpp:137）→ 每簇 `PackStartCount`（count<<16\|start，cpp:231）+ 平坦光索引表 |
| 2 | zrhi 不新增类型、常量布局 CB0/CB1/CB2 | zrhi 仅加法小改：`ZrGraphicsPsoDesc` 增 `rootCbvCount`（默认 1，zrhi_device.h:129）——v1 根签名泛化为 1..4 根 CBV（zrhi_d3d12_device.cpp:630-671），`--draw` 默认单槽零行为变化；`--lights` 用 3 槽 b0/b1/b2 |
| 3 | pbr_forward.hlsl | 新增 `Native/shaders/pbr_forward.hlsl`：像素级簇索引（"CLUSTER-INDEX" 段 hlsl:172-184）→ 读 range → 循环点光（hlsl:196）；GGX NDF（Walter 2007 Eq.33，hlsl:132-135）+ Smith 可见性（Schlick-GGX k=rough/2，Karis 2013，hlsl:136-141）+ Lambert（albedo/π，hlsl:154）+ Fresnel-Schlick（hlsl:146）；方向光槽 0 + 点光数组；Reinhard 简易 tonemap（hlsl:220-222，已注明 TODO 由 Volume 后处理替代）；PBR 参数常量简化（albedo/roughness 逐物体 CB0） |
| 4 | 窗口程序 --lights | `zrhi_d3d12_window.cpp` 新增 `RunLightsMode`（:1184，main 分发 `--lights [zmeshPath] [hlslPath]`）：程序化地面 quad 20×20m（C++ 直构 4 顶点，:1270）+ cube.zmesh；1 方向光 + 8 点光（光源 5..8 随帧环绕 120 帧/圈、相位 π/2 步进，强度 6）；每帧 CPU `AssignLightsToClusters`（:1500）→ CB2；readback 校验 + `D3D12 LIGHTS OK` |
| 5 | mock 单测 | 新增 `Native/tests/test_cluster_lighting.cpp`：13 例（详见第二节）；CB 布局 static_assert（h:64-68、窗口侧 `LightsConstants` 208B assert window:1160） |
| 6 | CMake 挂接 /W4 /WX | `Native/CMakeLists.txt`：cluster_lighting.cpp → zrender_core；test_cluster_lighting.cpp → zrender_test。全量构建零警告 |

**常量布局（两侧镜像，均有 static_assert）**：
- CB0 `CBufPerDraw` 208B（hlsl:54-62 ↔ window `LightsConstants` :1160）：mvp/model（row_major 64B×2）+ projParams（A=zf/(zf-zn), B=zn·zf/(zf-zn), nearZ, farZ）+ gridParams（dimX/Y/Z, invZRange）+ screenParams（width,height,lightCount,ambient）+ material（albedo.rgb, roughness）+ camPos
- CB1 `CBufLights` 1024B（hlsl:71 ↔ `PackedLight`×32）：光源数组，超 32 由 `AssignLightsToClusters` 返回 -1 报错（单测覆盖）
- CB2 `CBufClusters` 65216B ≤ 64KB（hlsl:74-83）：簇范围表 + 平坦光索引表（**唯一实现偏差，见第四节**）

### 二、自测结果

**build_native.ps1 全绿**：configure + build（/W4 /WX 零警告）+ 单测 + dll 部署，`ALL GREEN`。

**单测：102 例全过 / 703,774 断言全过**（既有 89 例回归零失败 + 新增 13 例）：

| 新增用例 | 覆盖（简报单测清单） |
|---|---|
| lightdata ×2 | PackedLight 32B static_assert、kind 编码往返（点光 w=radius / 方向光 w=-1）、超上限截断与空指针防御 |
| cluster 像素→簇索引 | 边界像素归属（(0,0)→簇(0,0,·)、(639,479)→簇(15,8,·)）、像素入口=UV 入口一致 |
| cluster 覆盖完整 | 640×480 步进 5 全像素 × 6 个视深采样（含近平面/远平面/越界 clamp）均有合法归属簇 |
| PackStartCount | 打包往返（含 0/65535 边界） |
| 球-AABB 相交边界 | 面相切（d²==r² 相交）/角相切/内部/外侧，±ε 精确判定 |
| 单点光精确断言 | 光源置于簇 (8,4,2) 几何中心+小半径 → 命中集合与**测试内独立重写的几何判定**（ExpectedHitClusters，不调用被测实现，双重互证）逐簇精确一致，恰 1 簇即目标簇 |
| 两点光不重叠 ×2 | 每簇段 [start,start+count) 互不重叠且恰铺满平坦表；内容与独立实现精确一致；大半径交叠场景必有 count=2 公共簇 |
| 方向光全簇 | 2304 簇各 1 项、start 前缀连续（=簇号）、平坦表确定 |
| 方向光+点光混合 | 方向光排在每簇段首、点光次序确定、段不重叠 |
| 越界零命中 | 相机后方（z<0）/ 远平面外（z=70>farZ）全簇 count=0 |
| 参数与预算报错 | 超 32 光源 / 空指针 / 容量 0 均返回负错误码（简报"超限报错"） |

### 三、簇索引两侧一致性对照（验收清单第 4 条）

| 步骤 | CPU（`Native/zrender/cluster_lighting.cpp`） | PS（`Native/shaders/pbr_forward.hlsl`） |
|---|---|---|
| X 瓦片 | `cx = floor(u*dimX)`，clamp [0,dimX-1]（cpp:100-110） | `cx = min(uint(floor(u * ZR_CLUSTER_DIM_X)), uint(ZR_CLUSTER_DIM_X-1))`（hlsl:177） |
| Y 瓦片 | `cy = floor(v*dimY)`，clamp（cpp:101-111） | `cy = min(uint(floor(vPix * ZR_CLUSTER_DIM_Y)), ...)`（hlsl:178） |
| Z 切片 | `cz = floor((viewZ-nearZ)*invZRange*dimZ)`，clamp（cpp:102-112） | `czF = floor((viewZ - projParams.z) * gridParams.w * ZR_CLUSTER_DIM_Z)` + max/clamp（hlsl:182-183） |
| 索引 | `(cz*dimY + cy)*dimX + cx`（cpp:114-115） | `(cz * ZR_CLUSTER_DIM_Y + cy) * ZR_CLUSTER_DIM_X + cx`（hlsl:184） |
| 网格参数 | 常量 `ZR_CLUSTER_DIM_X/Y/Z = 16/9/16`（h:32-34） | `#define` 同值（hlsl:40-42，注释指向 h）；运行时 gridParams 同值随 CB0 下发 |
| 视深 | 视空间 z（== 世界 z，--lights 相机 identity view） | `viewZ = projB/(projA-ndcZ)`（hlsl:180，projA/projB 与 CPU `MatPerspectiveFovLH` 同式） |
| 像素采样 | `ClusterIndexFromPixel` 中心采样 (+0.5)（cpp:127-134） | SV_Position.xy 即像素中心（hlsl:174-175） |

PS 侧对照段带 "CLUSTER-INDEX" 标注（hlsl:172），h 侧文档注释（h:104-113）注明"修改须两侧同步"。单测第 3/4 例锁定公式行为。

### 四、实现偏差与说明

1. **CB2 两表按 uint4 分装（对简报布局的字节级兼容实现）**：初版按简报字面写 `uint ranges[2304] + uint indices[14000]`，FXC 出码被 D3D11 字节码校验拒绝（"Index Dimension 5804 specified, max allowed is 4096"——动态索引的标量数组每元素占一整槽，2304+3500>4096）。改为 `uint4 clusterRanges[576] + uint4 clusterLightIndices[3500]`（hlsl:74-83）：**字节布局与"2304 个连续 uint32 + 平坦连续 uint32"完全相同**（uint4 元素即 4 个连续 uint32），CPU 侧 CB2 写入代码不变；PS 内按 `c>>2`/`c&3` 解包（hlsl:186-196）。总尺寸 9216+56000=65216B ≤ 64KB cbuffer 预算（h:67-68 static_assert）。576+3500=4076 ≤ 4096 槽。
2. **地面受光/动画判定的读数口径**：readback 像素含 alpha 通道（恒 255），受光判定只统计 RGB 字节（window:1747-1750）；动画变化判定 = 相邻校验帧"通道差 ≥8 的字节数 ≥500"（window:1147-1148、:1768）——比单点最大差更稳健（光斑渐变边缘大量像素越阈）。
3. **dsvFormat 深度 / cull None**：与 --draw 同策（绕序约定归资产管线任务）。
4. `UnityProject/Assets/Plugins/ZRender/zrender.dll` 的更新是 build_native.ps1 第 4 步自动部署的产物（脚本既有行为），非手工改动。
5. 遵守约束：未跑任何 Unity 命令；未 git commit；只动 `Native/zrender/`、`Native/shaders/`、`Native/tests/`、`Native/CMakeLists.txt`、`Native/zrhi/`（加法）、本任务日志。

### 五、亲跑证据（--lights，2026-10-24）

```
zrhi_d3d12_window: --lights device created, debug layer UNAVAILABLE (degraded)
zrhi_d3d12_window: --lights geometry: ground quad 20x20m (4 verts) + cube.zmesh (24 verts / 36 indices)
zrhi_d3d12_window: --lights shaders compiled (runtime D3DCompile, SM5.0 DXBC)
zrhi_d3d12_window: --lights cluster assign frame1: lights=9 clusters=2304 flat entries=4683
  frame 10 lights readback: center RGB=(46,49,42) stripMax=55 groundLit=1 (baseline strip)
  frame 20 lights readback: center RGB=(49,42,49) stripMax=42 groundLit=1 animDiff(max=15 changed>=8: 934 bytes, min 500) -> center=OK anim=OK
  frame 30 lights readback: center RGB=(50,40,50) stripMax=40 groundLit=1 animDiff(max=17 changed>=8: 620 bytes, min 500) -> center=OK anim=OK
  frame 40 lights readback: center RGB=(49,42,50) stripMax=55 groundLit=1 animDiff(max=15 changed>=8: 628 bytes, min 500) -> center=OK anim=OK
  frame 50 lights readback: center RGB=(43,48,51) stripMax=42 groundLit=1 animDiff(max=17 changed>=8: 1248 bytes, min 500) -> center=OK anim=OK
  frame 60 lights readback: center RGB=(41,49,52) stripMax=40 groundLit=1 animDiff(max=19 changed>=8: 620 bytes, min 500) -> center=OK anim=OK
zrhi_d3d12_window: [LIGHTS] frames=60 readback checks=6 centerPass=6 groundLitPass=6 animPairs=5/5 (threshold 8)
zrhi_d3d12_window: [LIGHTS] device removed reason = 0x00000000 (S_OK)
zrhi_d3d12_window: InfoQueue error count = 0
D3D12 LIGHTS OK
```

证据解读：①中心像素 ≠ 清屏色 (8,8,11)（cube 可见，6/6）；②地面横带 stripMax 40~55 远超环境项（≈15，albedo×ambient tonemap），受光成立（6/6）；③动画光源 10 帧转 30° 横扫地面横带，逐对校验帧变化像素 620~1248 字节 ≥500、最大差 15~19 ≥8（5/5）；④60 帧稳定、无设备移除、InfoQueue 0 错误（调试层不可用如实降级）。

### 六、旧模式回归（验收清单第 6 条）

```
--draw：        [FIRST DRAW] frames=60 center checks=6 passed=6 undeclared uses=0
                device removed 0x00000000 (S_OK)，InfoQueue error count = 0 → D3D12 FIRST DRAW OK
默认（无参数）： D3D12 RESOURCE SMOKE OK / D3D12 RG SMOKE OK / InfoQueue 0 → D3D12 WINDOW SMOKE OK
--zmeshcheck：  cube.zmesh vertexCount=24 indexCount=36 → zmeshcheck: OK
```

### 七、遗留与后续建议

- Reinhard tonemap 为简易全局版，待 Volume 后处理（曝光/自适应/ACES）替代（hlsl:220 已注明）。
- 簇分配为 CPU O(光源×簇) 两遍前缀和（2304×9 实测毫秒级）；后续可迁 GPU compute 或加分块粗筛。
- 根签名 4 槽上限为 v1 泛化余量；bindless 资源表接入后光源/簇数据可迁结构化缓冲消除 32 光源上限。

## 验收清单（主控写）

1. build_native.ps1 全绿（用例数写入结论）
2. 簇分配单测逐项核对（已知几何精确断言/不重叠/边界）
3. 亲跑 `--lights`：`D3D12 LIGHTS OK`、光源动画像素变化证据、InfoQueue 计数
4. 代码审查：簇索引计算（PS 里屏幕→簇）与 CPU 网格参数一致（两侧对照行号）；LightData 32B 对齐 static_assert
5. PBR：GGX 公式实现与注释引用一致（NDF/Vis 行号）
6. 旧模式回归（--draw/RESOURCE/RG/ZMESHCHECK 全过）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

验收：general-purpose 验收子代理｜日期：2026-10-23｜结论：**PASS**

1. **build_native.ps1 全绿**：configure + build（/W4 /WX 零警告）+ 单测 + dll 部署输出 `ALL GREEN`；doctest **102 用例全过 / 703,774 断言全过**（既有 89 + 新增 13）。
2. **簇分配单测逐项核对**（`Native/tests/test_cluster_lighting.cpp` 13 例）：单点光置于簇 (8,4,2) 中心+小半径，命中集合与测试内独立重写的几何判定（ExpectedHitClusters，不调用被测实现）精确互证（:290-319）；两点光段互不重叠且恰铺满平坦表（:321-372）、大半径交叠必有公共簇 count=2（:374-402）；球-AABB 面相切（d²==r²）/角相切/内部/外侧 ±ε 边界（:265-284）；超 32 光源/空指针/容量 0 均走负错误码（:466-488）；像素边界归属、全屏×视深覆盖、PackStartCount 往返齐备。
3. **亲跑 --lights**（Native/build/bin/Release/zrhi_d3d12_window.exe）：exit 0、`D3D12 LIGHTS OK`、60 帧、readback 6/6（centerPass=6≠清屏色、groundLit=6/6 stripMax 40~55 远超环境≈15）、动画证据行 5/5 对（changed 620~1248 字节 ≥500、maxDiff 15~19 ≥8，即光源动画像素变化证据）、`InfoQueue error count = 0`（调试层不可用如实降级）。
4. **簇索引两侧一致性**：X/Y=floor(u·dim) clamp、Z=floor((viewZ−nearZ)·invZRange·dimZ) clamp、索引 (cz·dimY+cy)·dimX+cx——CPU `cluster_lighting.cpp:103-105/118-125` ↔ PS `pbr_forward.hlsl:177-184`（"CLUSTER-INDEX" 段）逐式一致；视深反解 z=B/(A−ndcZ)（hlsl:179-181 ↔ window:1531-1532，与 MatPerspectiveFovLH window:566-573 的 A=zf/(zf−zn)、B=zn·zf/(zf−zn) 同式）；网格参数 16/9/16、nearZ 0.3/farZ 60 两侧同值（h:32-38 ↔ hlsl:45-52 ↔ window:1396-1409,1535-1538）；PackedLight/CB 布局 static_assert 两侧（h:64-68 ↔ window:1160-1164 ↔ test:157-159）。
5. **PBR 公式**：GGX NDF D=a²/(π((n·h)²(a²−1)+1)²) 与注释引用 Walter 2007 Eq.33 相符（hlsl:132-135）；Smith-Schlick-GGX 分离式 V=G(v)·G(l)/(4(n·v)(n·l))（hlsl:136-141，公式形式正确）；Lambert albedo/π（hlsl:146）；Fresnel-Schlick（hlsl:142-143）。备注：k=roughness/2 为简化取值，Karis 2013 原文解析光建议 k=(roughness+1)²/8——公式形式与引用一致、取值属简报允许的"常量简化"，不构成缺陷。
6. **旧模式回归亲跑**：`--draw` → `D3D12 FIRST DRAW OK`（60 帧 center 6/6、undeclared=0、InfoQueue 0、exit 0）；默认（无参数）→ `D3D12 RESOURCE SMOKE OK / RG SMOKE OK / WINDOW SMOKE OK`（exit 0）；`--zmeshcheck Native/tests/data/cube.zmesh` → `zmeshcheck: OK`（24 顶点/36 索引）。备注：验收首轮将两个窗口进程并行运行曾令默认模式 timestamp 断言失败（interval 429ms>100ms 上限），串行复跑 11.2ms 全过——判定为验收操作负载干扰，非代码回归。
7. **文件规范+报告完整性**：新增/改动文件均 UTF-8 无 BOM、LF（CR 字节数 0）、`#pragma once`（h:24）、git 索引 `i/lf w/lf`；CMake 挂接 `Native/CMakeLists.txt:69,126`；施工报告四节齐全。**CB2 uint4 分装偏差评估：等价且合理**——uint4[576]+uint4[3500] 的字节布局=2304+14000 个连续 uint32（window:1507-1510 仍按连续 uint32 写入，CPU 侧不变），576+3500=4076 ≤ FXC 动态索引 4096 槽上限，65216B ≤ 64KB cbuffer 预算（h:67-68 static_assert），偏差动机（FXC "Index Dimension 5804>4096" 拒码）与字节等价性已在报告第四节如实说明。zrhi 改动为加法（rootCbvCount 默认 1，h:129），`--draw` 回归通过证明零行为变化。
