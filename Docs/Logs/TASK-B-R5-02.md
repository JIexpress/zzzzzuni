# TASK-B-R5-02 阴影：CSM 级联 + 逐光阴影图（共享可见性/PCF）

状态: doing | 派发: 2026-10-25 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：R5 第二块——全场景动态阴影：方向光 CSM 4 级联（shadow map 2048/级联分割对数+均匀混合、稳定 texel snap）、深度-only 阴影 Pass 进 RG（消费 draw list，castShadow 过滤共享可见性）、PS PCF 3x3 采样；窗口程序 `--shadows` 模式可证伪对比。

**设计要点**：
1. `Native/zrender/shadows.h/.cpp`（纯 CPU 可单测）：
   - `CascadeSplits(near, far, count=4, lambda=0.75)`：对数+均匀插值分割（纯函数，单测：分割单调、首分割≥near、末≤far）
   - 每级联视锥体构建：方向光 view（texel snap 防闪烁：light view 下对齐 texel 网格）+ 正交 proj 包裹场景包围球；`CascadeMatrix(cascadeIndex)` 输出 lightVP
   - 阴影 draw list：复用 R4-02 DrawList，新增 `castShadow` 标志与 shadow view 掩码位（kMaxViews 内分配 shadowViewId）——**共享同一份可见性数据**（简报要求：不重复剔除；主视图与阴影视图从同一 VisibilitySet 过滤）
2. RG 集成（`--shadows` 模式，改造窗口程序）：
   - 每级联一个 Imported 深度图（2048²，D16 或 D32）+ depth-only DrawPass（新 PSO：无 PS 或 min PS，CullMode 按绕序约定——注意 R4-03 发现的 CCW 问题，阴影 pass 剔除与主 pass 相反说明写注释）
   - CB：每级联 lightVP+depthBias/slopeBias 常量
   - PS 侧：像素→级联选择（按视深）→投影采样→PCF 3x3→shadow factor 进光照（pbr_forward.hlsl 扩展或新 pbr_shadow.hlsl，报告说明）
3. 窗口程序 `--shadows`：继承 --lights 场景（地面+cube+方向光），方向光斜照使 cube 在地面投出可观测阴影
   - **可证伪对比**：`--shadows off`（关 factor=1）与 `--shadows on` 各跑 30 帧，第 30 帧 readback **同一地面探针点**（cube 阴影投影区内，坐标写死并注释推导）——on 模式亮度 < off 模式（阈值差 ≥10/255）且两模式均 exit 0；另报 4 次级联分割值
   - 通过行：`D3D12 SHADOWS OK (probe delta=N)`
4. 单测（新增 test_shadows.cpp）：分割纯函数、texel snap（同参数两次调用矩阵稳定）、可见性共享（castShadow=false 项不进阴影 draw list）
5. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zrender/`、`Native/shaders/`、`Native/tests/`、`Native/CMakeLists.txt`、`Native/zrhi/`（加法）、本任务日志；**不跑 Unity**；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：CSM 全件+RG 阴影 Pass+可证伪对比+单测+`D3D12 SHADOWS OK`+施工报告四节（含探针坐标推导与分割值）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 日期: 2026-10-25 ｜ 构建: `Tools/build_native.ps1` ALL GREEN（/W4 /WX 零警告）

### 1. 交付清单

| 类别 | 文件 | 内容 |
|---|---|---|
| 新增（纯 CPU 可单测） | `Native/zrender/shadows.h/.cpp` | `CascadeSplits`（对数+均匀 lambda 混合分割，shadows.h:58 / shadows.cpp:98）、`BuildCascades`（光视图 + texel snap + 正交投影，shadows.cpp:123，texel snap 在 shadows.cpp:236-247）、`ShadowViewMask/ShadowViewId`（阴影视图掩码契约，shadows.h:96-118）、`ShadowCbufferLayout`（CB3 GPU 布局镜像，static_assert 336B） |
| 加法扩展 | `Native/zrender/draw_list.h` | `RenderItem::castShadow` 标志（draw_list.h:131）——录入可见性时带级联阴影视图掩码位 |
| 加法扩展（zrhi） | `Native/zrhi/zrhi_device.h` | `ZrGraphicsPsoDesc::depthOnly`（:134，depth-only 管线：无 PS/无 RTV）与 `srvTable`（:139，根签名追加 SRV 描述符表 t0 + 静态采样器 s0 Point/Clamp）；`ZrValidateGraphicsPsoDesc` depthOnly 分支（:150）；`IZrCommandList::SetGraphicsBindlessSrv`（:418） |
| 加法扩展（D3D12 后端） | `zrhi_d3d12_device.cpp` | 根签名 srvTable 支持（:676-712）；SRV Texture2DArray 视图（:482，数组图 + R32Float 格式覆盖）；DSV 层窗口 Texture2DArray（:529，级联 c → 层 c 单层 DSV）；`zrhi_d3d12_translate.cpp:259`（depthOnly → NumRenderTargets=0）；`zrhi_d3d12_commandlist.cpp`（SetGraphicsBindlessSrv → SetGraphicsRootDescriptorTable；SetRenderTarget 支持无效 RTV = 仅深度附件） |
| 新增 shader | `Native/shaders/shadow_depth.hlsl` | 阴影 depth-only VS（b0 级联 lightVP + b1 逐绘 model）；文件头含**剔除绕序约定注释**（主 pass R4-03 CCW→CullMode::None，阴影 pass 取 Front：FrontCounterClockwise=FALSE 下剔除 CW 保留 CCW = 恰好保留面向光源表面，即"与主 pass 相反"的绕序选择） |
| 新增 shader | `Native/shaders/pbr_shadow.hlsl` | pbr_forward 扩展：级联选择按视深 `SelectCascade`（:143，与 CPU CascadeSplits 边界语义一致）、`ShadowFactorPcf` PCF 3x3（:157，9 点采样手动深度比较，SampleLevel LOD 0，:184）、b3 CBufShadows（与 ShadowCbufferLayout 逐字节一致） |
| 窗口程序 | `Native/tests/zrhi_d3d12_window.cpp` | `--shadows [on\|off]` 模式（缺省 = off→on 各 30 帧可证伪对比）：单张 2048² D32 数组图（层=级联），RG 每级联一个 depth-only `ShadowCascadePass`（:2239），主 pass 读 4 层做 PCF；探针对比 + 分割值打印 |
| 单测 | `Native/tests/test_shadows.cpp` | 6 个 TEST_CASE：分割单调/边界/端点语义/非法参数；BuildCascades 级联边界对照 + 切片中点投影 NDC/深度合法 + texel snap 同参数逐位稳定；**可见性共享**（castShadow=false 项不进任何 ShadowViewId 视图，主视图全见——同一 VisibilitySet 按位过滤非二次剔除） |
| CMake | `Native/CMakeLists.txt` | shadows.cpp → zrender_core（:70）；test_shadows.cpp → zrender_test（:129） |

### 2. 可证伪对比（亲跑证据）

```
--shadows（缺省对比模式，off 30 帧 → on 30 帧，同一探针点）:
  cascade splits (near=0.3 far=60 lambda=0.75): 4.6524 10.7195 23.2349 60.0000
  shadows phase frame 30 probe(250,389) RGB=(43,43,40)   ← off（factor 恒 1，直射地面）
  shadows phase frame 30 probe(250,389) RGB=(14,15,14)   ← on（cube 阴影内 ≈ 纯环境项）
  [SHADOWS] probe off=43 on=14 removed=S_OK errors=0
  D3D12 SHADOWS OK (probe delta=29)                        ← 退出码 0
--shadows on  → D3D12 SHADOWS OK (single phase on,  probe=14)，退出码 0
--shadows off → D3D12 SHADOWS OK (single phase off, probe=43)，退出码 0
```

**探针坐标推导**（可复核，代码注释见 zrhi_d3d12_window.cpp:1895-1907）：相机原点朝 +Z（view=identity），fovY=60°，640×480。cube 在 (0,0,3.2)（半边 0.5），地面 y=-1；方向光指向光源方向 d=(0.4,0.85,0.35)/|d|。cube 中心沿光线方向 (-d) 投到地面：s=1/dy=1.179 → 影心 (-0.470, -1, 2.788)；cube 8 角投影外包络 x∈[-1.2,0.27]、z∈[2.3,3.3]。屏幕投影 ndcX=x/(z·tan30°·4/3)、ndcY=y/(z·tan30°)：影心 → 像素 (250, 389)，深居影区内部（PCF 3x3 边缘安全）。阈值：on 亮度 < off 且差 ≥10/255（实测 43→14，delta=29；理论直射 ≈42（0.5/π·ndl·radiance+环境项经 Reinhard），阴影内 ≈14（纯环境项）——实测与手算一致）。

### 3. 调试修复记录（重要——其中一项修复了 R4-03 以来的存量缺陷）

1. **【存量缺陷修复】CPU 矩阵约定不一致**：窗口程序 `MatTranslation/MatPerspectiveFovLH/MatRotationX/Y` 原按行向量约定存储（平移在第 4 行、透视系数转置），而 shader 用 `mul(mvp, pos)` 列向量应用——自 R4-03 起所有 draw 模式实际渲染**转置垃圾几何**（cube 渲成环绕相机的巨型盒、地面不可见），历次"中心像素 ≠ 清屏色"类宽松检查未暴露。已全部改为列向量约定（修复注释在各函数处），修复后探针实测值与手算一致。连带修复：`--lights`/`--shadows` 内联行向量 `cubeModel` 字面量 → `MatTranslation`。
2. **【存量缺陷修复】--lights 动画判定阈值重校准**：渲染修复后动画光斑按真实投影绘制（变小），相邻 10 帧对差值字节实测 0/569/614/0/471（轨道相位相关）——判定从"5/5 对 ≥500 字节"改为"≥3/5 对 ≥300 字节"（同一意图、按正确渲染重校准；常量处有注释）。
3. **PS 内隐式 LOD Sample 被运行时拒绝**：`Texture2DArray.Sample`（隐式 LOD）使 `CreateGraphicsPipelineState` 返回 E_INVALIDARG（8 种根签名组合矩阵实测隔离定位）；PCF 点采样本无需梯度，改 `SampleLevel(..., 0)`（pbr_shadow.hlsl:182-184 有注释）。
4. **根签名须覆盖 shader 全部资源**：本机 D3D12 运行时对"根签名未覆盖 shader 引用的 b0..b3/t0/s0"的 PSO 创建直接 E_INVALIDARG（实测矩阵定位）——主 PSO 配置 rootCbvCount=4 + srvTable 覆盖全部资源。
5. **阴影 pass 逐绘 model 缺失**：初版 shadow pass 仅以 lightVP 变换**局部**顶点——地面（identity model）正确入图、cube（平移 3.2m）被画到原点之外导致阴影图无 cube。修复：shadow_depth.hlsl 增 b1（逐绘 shadowModel，与主 pass model 同源），PSO rootCbvCount 1→2，窗口程序增 2 个逐项 model CB（zrhi_d3d12_window.cpp:2096-2116 写入、:2256-2260 绑定）。
6. **depth-only VS 输入签名裁剪**：shadow_depth.hlsl 未消费 NORMAL/TEXCOORD 时 FXC 裁剪 VS 签名，与 v1 固定 3 元素输入布局（超集）不匹配——VS 内显式零权消费全部属性（文件内注释说明）。

### 4. 设计要点对照（简报 5 条）

1. **CascadeSplits/texel snap 可单测** ✓：纯函数 shadows.cpp:98（对数+均匀，lambda=0.75）；BuildCascades shadows.cpp:123（texel snap :236-247）；单测 test_shadows.cpp（分割单调/边界/lambda 端点/非法参数、同参数两次调用矩阵逐位相等、级联切片中点投影合法）。
2. **阴影 draw 复用 R4-02 共享可见性** ✓：`RenderItem::castShadow`（draw_list.h:131）+ `ShadowViewMask`（shadows.h）——DrawList::Add 录入掩码 bit0（主）+bit1..4（级联 0..3）；阴影 pass 项来自同一 VisibilitySet 的 `Iterate(ShadowViewId(c))` 位过滤（zrhi_d3d12_window.cpp:2146-2149 收集、:2254 消费），**无第二次剔除**；单测断言 castShadow=false 项不进任何阴影视图。
3. **RG 每级联深度图 + depth-only DrawPass** ✓：单张 2048² D32 数组图（层=级联）Imported 进 RG，每级联一个 `ShadowCascadePass`（Write DepthStencilAttachmentOptimal）；depth-only PSO（`depthOnly=true`：无 PS/无颜色目标）；主 pass 以 `Read(DepthStencilReadOnlyOptimal)` 消费（RG 自动 DEPTH_WRITE→DEPTH_READ 屏障）。
4. **PS PCF 3x3 + 按视深选级联** ✓：`SelectCascade`（pbr_shadow.hlsl:143，视深反解后与 cascadeSplits 边界比较，语义与 CPU 一致）；`ShadowFactorPcf`（:157，9 点手动比较 + depthBias/slopeBias 每级联 CB3 常量）。
5. **窗口程序 --shadows 可证伪对比** ✓：见第 2 节（off=直射 43 / on=阴影内 14，delta=29 ≥ 10；4 次级联分割值打印；两模式均 exit 0；InfoQueue 0 错误、无设备移除、RG 未声明使用 0）。

### 5. 自测证据汇总

- `build_native.ps1` ALL GREEN：configure + build（/W4 /WX 零警告）+ 测试。
- `zrender_test.exe`：**110 用例 / 703853 断言全绿**（新增 test_shadows.cpp 6 用例，断言数 703774→703853）。
- 亲跑窗口程序：`--shadows`（对比，OK 行 delta=29）、`--shadows on`、`--shadows off`（单模式均 OK exit 0）、`--draw`（FIRST DRAW OK）、`--lights`（LIGHTS OK，动画判定按第 3.2 条重校准）。
- 文件规范：新增文件均 UTF-8 无 BOM、LF、`#pragma once`（shadows.h）。

### 6. 遗留说明（供验收/后续任务）

- `zrhi` 的 `SetGraphicsBindlessSrv` 为 B-R5-02 加法扩展（接口 + D3D12 后端 + mock 三处同步）；Vulkan 后端 R7 实现时需映射为采样图描述符绑定。
- 阴影 bias（depthBias=0.05/zRange、slopeBias=0.15/zRange）按本场景调参；PCF 3x3 手动比较（非 SampleCmp）——后续升级比较采样器时可复用同一张深度图。
- 本机无 D3D12 调试层（如前任务），验证依赖 readback 断言与 InfoQueue 降级计数。

## 验收清单（主控写）

1. build_native.ps1 全绿（用例数写入结论）
2. 分割/texel snap/可见性共享单测真实断言（读源码行号）
3. 亲跑对比：`--shadows off` 与 `--shadows on` 探针 delta≥10 且 `D3D12 SHADOWS OK`；4 次分割值打印
4. RG 阴影 Pass 消费共享可见性（代码行号：阴影 draw 项来自同一 DrawList 过滤，非二次剔除）
5. PCF 3x3 实现行号；级联选择按视深（行号）
6. 旧模式回归（--draw/--lights/默认至少两项亲跑）
7. 文件规范+施工报告完整（探针推导可复核）

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ 日期: 2026-10-23 ｜ 结论: **PASS**（未跑 Unity，未改任何代码，未 git commit）

1. **构建全绿** ✓ — 亲跑 `Tools/build_native.ps1`：configure+build（/W4 /WX）+ doctest `110 用例 / 703853 断言` 0 失败（与施工报告一致）。
2. **单测真实断言** ✓ — test_shadows.cpp：分割单调/边界（:69-84）、lambda 端点=均匀/对数解析式（:86-104）、非法参数拒绝（:106-115）；texel snap 同参数两次调用 `memcmp` 逐位相等（:162-174）；可见性共享 castShadow=false 仅 bit0、各级联 Iterate 恰 2 项且玻璃面永不出现（:198-249）。
3. **对比亲跑** ✓ — `--shadows off` exit 0 probe=43；`--shadows on` exit 0 probe=14；缺省对比 `D3D12 SHADOWS OK (probe delta=29)`（29≥10）；4 次分割值 `4.6524 10.7195 23.2349 60.0000`，与手算（uniform/log λ=0.75 混合，i=1/2/3 → 4.652/10.720/23.236）一致。
4. **【历史缺陷修复独立复核】PASS，修复正确** — 独立推导：CPU 侧行主序存储（索引=row*4+col，MatTranslation 平移在第 4 列 zrhi_d3d12_window.cpp:611-618；透视系数 zf/(zf-zn)、-zn·zf/(zf-zn) 在第 2 行第 4 列 :587-588，代入 z=zn→0、z=zf→zf 验证 D3D LH z∈[0,1]）+ HLSL `row_major` 声明 + `mul(M,v)` 列向量应用（pbr_shadow.hlsl:39/95、shadow_depth.hlsl:34/58/62）——两侧约定一致；shadows.cpp BuildCascades 的 lightView/ortho 平移项同在第 4 列（shadows.cpp:187/195/259/261/263），单测 ProjectByLightVp 同索引约定。**探针手算复核成立**：影心 (-0.4706,-1,2.7882) → 像素 (249.9,389.1) 与 (250,389) 吻合；off 理论直射 0.5/π·ndl·1.0+0.06 经 Reinhard ≈41.6/255 vs 实测 43；on 纯环境 0.5·0.12 经 Reinhard ≈14.4/255 vs 实测 14——若矩阵仍转置，几何错位则两值均不可能与手算吻合。结论：修复使 CPU/HLSL 约定两侧一致，论证成立。
5. **共享可见性** ✓ — 阴影项来自同一 DrawList：`Visibility().Iterate(ShadowViewId(c))` 收集（zrhi_d3d12_window.cpp:2146-2149）→ ShadowCascadePass 直接消费（:2254-2258），全程无第二次剔除；`RenderItem::castShadow`（draw_list.h:131）+ ShadowViewMask（shadows.h:113-118）。CullMode 相反注释在两处：shadow_depth.hlsl:17-24 与窗口程序 :2676-2682（主 None :2661 / 阴影 Front :2682）。
6. **旧模式回归亲跑** ✓ — `--draw`：`D3D12 FIRST DRAW OK` exit 0（6/6 center 检查通过）；`--lights`：`D3D12 LIGHTS OK` exit 0（center 6/6、groundLit 6/6、anim 3/5≥阈值）。
7. **文件规范+报告完整** ✓ — 新增 5 文件均无 BOM（首 3 字节 "// "）、磁盘 CRLF 系 `core.autocrlf=true` 检出产物（既有文件同状态，.gitattributes `eol=lf` 保证入库 LF）；shadows.h `#pragma once`；CMakeLists.txt:70/:129 挂接；施工报告四节齐全，探针推导数值经本代理独立复算全部吻合。

**非阻塞备注**：pbr_shadow.hlsl:17 注释写 "b0 CBufPerDraw（224B）"，实际镜像结构体 240B（static_assert 240，zrhi_d3d12_window.cpp:1921）——文档注释笔误，建议后续任务顺手修正，不影响验收。
