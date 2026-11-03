# TASK-B-R9-13 —— ZRender 资产画廊：4 人物 .zmesh 渲染（真实建模进自研管线）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景（资产已由主控备好）

`UnityProject/Assets/Art/Models/` 有 4 个人物 FBX（CHR_Kenney_CharacterMedium/CHR_Robot/CHR_Human/CHR_DarkElf，精度从超低到 game-ready，见 SOURCES.md）。

## 目标

1. **烘焙**：ZMeshBaker 新增静态批处理入口 `BakeGalleryToRepoData()`（参照既有 `BakeBuiltinCubeToRepoData()`）——加载 4 个 FBX 的 Mesh 烘焙为 `Native/tests/data/CHR_*.zmesh`（4 个文件，命名与 FBX 对应）；-executeMethod 跑通
2. **画廊模式**：窗口程序新增 `--assets` 模式——加载 4 个 .zmesh，横向一字排开在地面上（间距按包围盒），`--lights` 簇光管线渲染（阴影开），固定相机正面取景；既有 cube/地面场景元素保留
3. **验证**：
   - 每模型 vertex/index 数打印（与 Unity 侧 Mesh 数值对照表进报告——两侧一致性核对）
   - readback 探针：每个模型站位处像素非背景色（4 探针）
   - 截图存 `Docs/Parity/assets_gallery.png`
   - 通过行 `D3D12 ASSETS GALLERY OK (models=4)`
4. 单测：4 个 .zmesh 加载解析（doctest，真数据文件——照 cube.zmesh 先例；断言 counts>0 且包围盒合理）
5. 自测：build 全绿（272 基线只增不减）；`--assets` 与 `--zmeshcheck` 抽查；`--draw` 回归

### ⚠️ 已知风险（报告必须覆盖）

- FBX 的**轴朝向与缩放**：Unity Mesh 烘焙的是 Unity 坐标系数据（Y-up、已应用 import scale），烘焙后 zmesh 直接是 Unity 语义——但部分 FBX（尤其 Blender 转换的 DarkElf/Human）在 Unity 导入时可能带 180° 旋转或异常 scale，烘焙前需在批处理入口核对 Mesh.bounds 并按需在 Unity 侧修正导入设置后再烘（**修 FBX 导入设置属 Unity 侧，允许改 Assets/Art/Models 的 .meta 导入参数，报告单列**）
- 材质：zmesh 不带材质——画廊用统一 PBR 常量色即可（每模型一色区分）

## 约束

- 白名单：`Native/tests/`（含 data/ 新 zmesh）、`Native/zrender/`（--assets 模式）、`Native/CMakeLists.txt`、`UnityProject/Assets/ZRender/Runtime/ZMeshBaker.cs`（批处理入口）、`UnityProject/Assets/Art/Models/**`（导入设置 meta）、`Docs/Parity/assets_gallery.png`、本日志
- **Unity 批处理纪律**（烘焙与 meta 生成需要 Unity：清锁/轮询/taskkill/勿带 -quit；与并行 A-R9-10 撞锁则等 60s×10 重试）
- 不 git commit；UTF-8 无 BOM/LF；VK 不泄漏

## 施工报告（施工代理写）

### ① 交付物清单（全部在白名单内）

| 文件 | 变更 |
|---|---|
| `UnityProject/Assets/ZRender/Runtime/ZMeshBaker.cs` | 新增批处理入口 `BakeGalleryToRepoData()`（4 FBX → CHR_*.zmesh，含躺倒站立修正 + bounds 核对日志；`GalleryFbxNames`/`GalleryStandUpFix` 表） |
| `UnityProject/Assets/Art/Models/CHR_*.fbx.meta ×4` | 导入设置修改（见 ③，单列） |
| `Native/tests/data/CHR_*.zmesh ×4` | 烘焙产物入库（526,928 / 53,276 / 29,748 / 52,196 B） |
| `Native/tests/zrhi_d3d12_window.cpp` | 新增 `--assets` 模式（`RunAssetsGalleryMode`，恒离屏；节头注 + main 分发） |
| `Native/tests/test_zmesh_gallery.cpp` | 新增 doctest：4 真数据解析 + counts 汇总（5 用例） |
| `Native/CMakeLists.txt` | zrender_test 并入 test_zmesh_gallery.cpp（+1 行） |
| `Docs/Parity/assets_gallery.png` | 画廊截图（640×480，第 60 帧全帧 readback） |

### ② 烘焙入口（简报目标 1）

- `BakeGalleryToRepoData()`：`AssetDatabase.LoadAllAssetsAtPath` 逐 FBX 取**顶点最多**的 Mesh（DarkElf 单 Mesh 4 子网格经 `mesh.triangles` 串联，无需合并）→ 打印 vertexCount/indexCount/subMeshes/bounds/normals/uv0（对照表数据源）→ 烘焙 `Native/tests/data/CHR_*.zmesh` → `EditorApplication.Exit(4/4 ? 0 : 1)`。
- **幂等**（V4）：重复运行覆盖输出；第 7 次运行 `UNITY_EXIT=0`、`完成：4/4 烘焙成功`。
- Unity 批处理纪律：清锁 → 后台启动 → 轮询日志/进程；全程未带 `-quit`（executeMethod 自 Exit）。

### ③ FBX 导入设置与轴朝向/缩放核对（简报风险节，单列）

**首烘实测（globalScale=1，bake#1）**——两个风险都命中：

| 模型 | 原始 bounds size (m) | 诊断 |
|---|---|---|
| CHR_Kenney_CharacterMedium | (0.036, 0.011, 0.038) | 厘米级缩放 + 高度沿 Z（躺倒） |
| CHR_Robot | (0.015, 0.014, 0.016) | 同上 |
| CHR_Human | (0.019, 0.004, 0.019) | 同上（Blender 转换件） |
| CHR_DarkElf | (0.069, 0.017, 0.079) | 同上（Blender 转换件） |

**导入 meta 修改**（仅 meshes 段两参数，guid 未动）：

| meta | bakeAxisConversion | globalScale（→ 最终身高） |
|---|---|---|
| CHR_Kenney_CharacterMedium | 0→1 | 1→**48**（1.81m） |
| CHR_Robot | 0→1 | 1→**100**（1.59m） |
| CHR_Human | 0→1 | 1→**96**（1.80m） |
| CHR_DarkElf | 0→1 | 1→**24**（1.90m） |

**实证结论（超出简报预设的部分，如实报告）**：
1. globalScale 修复后（bake#4）身高数值到位（1.59~1.90m）但**高度仍在 Z 轴**——`bakeAxisConversion:1` 是 **no-op**：这批 FBX 容器头声明 Y-up（无可转换轴），几何却以 Z-up 存储并混入厘米缩放。**导入设置无法表达"容器声明与几何不符"的轴修正**。
2. 修正落在烘焙入口（Unity 侧、烘前）：躺倒探测（`size.z > size.y`）→ 临时实例（不改原资源，cube 先例同策）绕 X ±90° 旋转位置+法线（`GalleryStandUpFix` 逐模型方向表：Kenney +90°，Robot −90°，Human/DarkElf +90°）→ 产出 zmesh 统一 Unity 语义 Y-up 站立。
3. 方向表经**画廊截图两轮核验**：首烘 Human/DarkElf 头朝下（其网格原点在头/上身而非脚，方向猜测反了）→ 翻转两条目重烘 → 四模型全部直立朝向相机。
4. 与 A-R9-10 的边界：其 `ModelAssetSetup` 只**读** importer（probe 报告 fileScale/globalScale），不写导入设置——两侧无写冲突；本任务的 globalScale 修改对其场景布置同样有益（原始 FBX 数据厘米级，其脚本另有实例级 scale 处理）。

### ④ --assets 画廊模式（简报目标 2/3）

- **恒离屏**（`--parity` 先例：无窗口/交换链/vsync，RGBA 序截图免 swizzle）；管线 = `pbr_shadow.hlsl`（簇光源 + PBR + **CSM 阴影开**，`--lights` 同源 9 光：1 方向光即阴影来源 + 4 静态 + 4 动画环绕）+ `shadow_depth.hlsl` depth-only ×4 级联（2048²，texel snap，bias 0.05/0.15 世界域）。
- **场景**：地面 quad 20×20 @y=-1 + 既有 cube（(0,0,3.2)，--lights 同位）保留 + 4 人物 @z=9 横向一字排开——间距按包围盒宽度 + 0.8m，**中缝 3.2m** 让开 cube 视线走廊（z=9 处 cube 遮挡半宽 ≈1.41m，实测 slots=(-2.47,-4.90,2.50,5.03)，rowHalf=5.86 < visibleHalf=6.93）；脚底对齐地面（boundsMin.y 落地）；每模型统一 PBR 常量色（橙/钢蓝/肤色/深紫，简报"一色区分"口径）。
- **校验**：①每模型 counts/bounds 打印（对照表见 ⑥）②4 探针 = 包围盒中心世界点屏幕投影（推导同 kShadowProbeX 口径），每 10 帧 readback，**6/6 帧 × 4/4 探针全部非清屏色**（清屏 (8,8,12)，探针如 Kenney (33,15,5)）③第 60 帧全帧 readback → `WritePng24` → `Docs/Parity/assets_gallery.png` ④`removed=S_OK errors=0 undeclared=0`。
- **通过行**：`D3D12 ASSETS GALLERY OK (models=4)`，exit 0（证据 `TempLogs/assets_gallery_final.log`）。VK 不泄漏：仅 D3D12 路径，未触碰 VK 代码。

### ⑤ doctest（简报目标 4）

`test_zmesh_gallery.cpp` 5 用例：4 模型逐个 `zmesh_Parse`（counts>0、index%3==0、hasNormals=1、包围盒 Y-up 站立语义：高度 0.5~3.0m 且 x/z ≤ 高度×1.5）+ 4 模型 counts 汇总（verts≥1000）。**对风险有牙的实证**：首烘（躺倒+厘米级）数据在盘时恰 4 用例 10 断言红；站立修正后全绿。

### ⑥ Unity↔原生 counts 对照表（V3）

| 模型 | Unity verts | 原生 verts | Unity indices | 原生 indices | Unity bounds size（烘后） | 原生 bounds size（解析） |
|---|---|---|---|---|---|---|
| CHR_Kenney_CharacterMedium | 1029 | 1029 | 4812 | 4812 | (1.7369, 1.8070, 0.5031) | (1.7369, 1.8071, 0.5031) |
| CHR_Robot | 1784 | 1784 | 2610 | 2610 | (1.5185, 1.5947, 1.3646) | (1.5185, 1.5947, 1.3647) |
| CHR_Human | 623 | 623 | 2448 | 2448 | (1.7973, 1.7951, 0.3474) | (1.7972, 1.7952, 0.3473) |
| CHR_DarkElf | 10728 | 10728 | 45903 | 45903 | (1.6600, 1.9039, 0.4182) | (1.6600, 1.9038, 0.4181) |

Unity 侧 = bake#6/7 批处理日志（烘焙时 Mesh 值）；原生侧 = `--assets`/`--zmeshcheck` 的 `zmesh_Parse` 统计。counts 两侧完全一致；bounds 尾数 ±0.0001 为 float32 写盘舍入。Robot `uv=0`（源 FBX 无 UV，管线不消费 uv0，无影响）。

### ⑦ 自测证据（简报目标 5）

| 项 | 结果 |
|---|---|
| `build_native.ps1` | **ALL GREEN**：281 用例 / 771,162 断言（B-R9-12 基线 276 / 771,100，**+5 用例 +62 断言只增不减**；零警告 /WX） |
| `--assets` | exit 0 + `D3D12 ASSETS GALLERY OK (models=4)`；截图在盘并经目视核验（四模型直立） |
| `--zmeshcheck` ×4 | 全 OK（counts/bounds 与烘焙日志一致） |
| `--draw` 回归 | `D3D12 FIRST DRAW OK`（InfoQueue 0） |
| `--lights` 回归 | `D3D12 LIGHTS OK`（共享代码零回归） |
| 烘焙幂等 | 第 7 次运行 exit 0 |

### ⑧ 并行撞锁记录（A-R9-10）

两次 Unity 批处理因 A-R9-10 在建代码的瞬时编译错误退出（`ModelAssetSetup.cs` embeddedMaterialCount → 其后 `GameSceneSetup.cs` Object 歧义）——按纪律轮询其错误行修复后重试（未代改其文件）；一次清 stale `UnityLockfile`（确认无 Unity.exe 进程后）。

### ⑨ 受限与遗留（不阻塞）

1. `zrender.dll` 由 build_native 步骤 4/4 自动重部署（脚本既有行为），M 状态留主控按 ADR 口径处理。
2. 画廊光照沿用 `--lights` 同源口径（未为展示单独调亮），截图偏暗但四模型清晰可辨、探针全过。
3. `bakeAxisConversion:1` 对本批 FBX 为 no-op（实证记录在案），若后续资产替换为真 Y-up 容器该设置即生效；站立修正按 `size.z > size.y` 探测自动跳过已站立网格。
4. 站立方向表（`GalleryStandUpFix`）是逐模型实测值：换新 FBX 需重核（新模型先按 −90° 试烘，截图核验方向）。

## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减）；4 个 zmesh 解析 doctest 真数据过
- [ ] V2 亲跑 `--assets`：GALLERY OK 行+4 探针非背景+截图存在
- [ ] V3 Unity↔原生 counts 对照表（报告内，两侂数值一致）
- [ ] V4 FBX 导入设置改动单列（如有）；烘焙入口幂等（二次运行 exit 0）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**PASS（V1–V5 全过，5/5）**——验收代理独立亲证，未跑 Unity（按派发约束），未改任何代码。

- **V1 PASS**：亲跑 `build_native.ps1` → `ALL GREEN`，doctest **281 用例 281 过 0 红 / 771,162 断言 0 红**（vs B-R9-12 基线 276/771,100，+5 用例 +62 断言只增不减）；gallery 真数据 doctest 5 用例过，汇总打印 `total verts=14164 indices=55773 across 4 CHR meshes`（=报告⑥表合计）。
- **V2 PASS**：亲跑 `zrhi_d3d12_window.exe --assets` → exit 0 + `D3D12 ASSETS GALLERY OK (models=4)` + `[ASSETS] frames=60 checks=6 probePasses=6/6 6/6 6/6 6/6 png=1 removed=S_OK errors=0`；24 条探针 OK 行（6 帧×4 探针，如 Kenney RGB=(33,15,5)，非清屏色 (8,8,12)）；`Docs/Parity/assets_gallery.png`（640×480 PNG）验收代理 Read 亲看：**4 人物全部站立在地面、T-pose、不重叠不躺倒**，cube 居中保留、地面阴影可见；偏暗但四模型清晰可辨（与报告⑨.2 口径一致）。
- **V3 PASS**：四组 counts 两侧亲核一致——Unity 侧 `TempLogs/bake_gallery_7.log`（Kenney 1029/4812、Robot 1784/2610、Human 623/2448、DarkElf 10728/45903）= 原生 `--assets` 打印（同值，Robot uv=0 亦符合）= 报告⑥表；原生 bounds 由 --assets boundsMin/Max **独立复算**（如 Kenney (1.7369,1.8071,0.5031)、DarkElf (1.6600,1.9038,0.4181)）与报告表逐项吻合（尾数 float32 舍入口径属实）。
- **V4 PASS**：①幂等——按"不跑 Unity"约束以历史证据亲核：`bake_gallery_6.log` 与 `bake_gallery_7.log` 均 `ExitCode: 0` + `完成：4/4 烘焙成功`（连续两次复跑皆 exit 0）；②doctest 有牙——**抽样变异亲证**：以 Python 按各模型 globalScale（1/48、1/100、1/96、1/24）+ y/z 轴交换精确复现 bake#1 坏数据，亲跑 `-tc="zmesh gallery:*"` → **5 用例 4 红、62 断言恰 10 红**（`height>0.5` / `width、depth ≤ height×1.5`，构成 3+1+3+3，与报告"恰 4 用例 10 断言红"一致）；恢复原文件后 SHA256 逐一吻合备份、复跑 5/5 全绿。变异脚本与备份在仓库外 Temp（zr13_mutation），未污染仓库。
- **V5 PASS**：①文件规范字节级抽查（tr 计 CR 字节 + od 查 BOM）：`test_zmesh_gallery.cpp`、`Native/CMakeLists.txt`、`zrhi_d3d12_window.cpp`、`ZMeshBaker.cs`、本日志、4 个 CHR meta 全部 **0 CR、无 BOM**；`git ls-files --eol` index 全 i/lf、.gitattributes eol=lf 强制；CMakeLists diff 恰 +1 行；②四节齐全（简报/施工报告①–⑨/验收清单/本节）；③meta 单列评估——4 个 `.fbx.meta` 实测 `globalScale 48/100/96/24` + `bakeAxisConversion 1` 与报告③表一致，`GalleryStandUpFix` 源码表 `(+1,−1,+1,+1)` 与报告"Kenney +90°/Robot −90°/Human、DarkElf +90°"一致，躺倒探测 `size.z > size.y` 与临时实例不改原资源在 ZMeshBaker.cs:241–291 实证；bake_gallery_1.log 证实首烘坏数据（Kenney size=(0.0362,0.0105,0.0376) 厘米级+躺倒，烘焙器自身告警）；zmesh 字节数 526,928/53,276/29,748/52,196 B 与报告①一致；`--assets` 段源码 0 处 VK 引用（不泄漏）。
- 范围备注：工作区中 `Docs/Demo.md`、`ModelStageLayout*`/`StageScreenshot`/`ModelAssetSetup`、`ENV_*.meta`、`MAT_*.mat`、`SCN_Game.unity` 属并行 A-R9-10 车道文件；`zrender.dll` M 状态为 build 步骤 4/4 自动重部署（报告⑨.1 已声明）——均不属本任务改动，留主控归口。本验收未 git commit。
