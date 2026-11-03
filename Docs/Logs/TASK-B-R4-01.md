# TASK-B-R4-01 资产烘焙管线：Mesh → SoA .zmesh（C# 烘焙 + 原生加载验证）

状态: done | 派发: 2026-10-24 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理(代码复核) | commit: a422666

## 任务简报（主控写）

**目标**：R4 第一步——"建模接入"的数据通道：C# 编辑器烘焙 Mesh 为 SoA 二进制 `.zmesh`（位置/法线/uv0 列式存储+索引），原生层新增 C ABI 加载与校验，端到端验证：Unity 内置 Cube 烘焙→原生加载→统计吻合。这是后续"原生层直接消费烘焙数据做 bindless 批量绘制"的地基。

**设计要点**：
1. `.zmesh` 格式（小端，版本 1）：
   ```
   头: magic 'ZMSH'(4B) | version u32=1 | vertexCount u32 | indexCount u32 | flags u32
   体: positions float3×N 连续 | normals float3×N（flags bit0 有无） | uv0 float2×N（bit1） | indices u32×M
   ```
   头结构体在 C# 与 C++ 两侧各定义一份并注释对齐（POD，显式 LayoutKind/固定字段，无 padding）
2. C# 侧（`Assets/ZRender/Editor/ZMeshBaker.cs`，ZFrame.Render 程序集内 #if UNITY_EDITOR 或 Editor 目录——注意校验器矩阵，取现有模式）：
   - `Bake(Mesh mesh, string path)`：读取 vertices/normals/uv/triangles → 二进制写入
   - 菜单 `ZFrame/ZMesh/Bake Selection`（选中 Mesh/MeshFilter 物体）+ 静态方法供测试调用
   - `ZMeshReader.Read(path)`（测试用读回校验）
3. 原生侧：
   - `Native/zrender/zmesh.h/.cpp`：`ZmeshData` 解析（从内存 buffer，校验 magic/version/尺寸一致性，返回结构{counts+各列指针/跨度}）+ `ZmeshStats{vertexCount,indexCount,hasNormals,hasUv,boundsMin/Max}`
   - `Native/zplugin/` 新导出（C ABI，沿用 def 文件）：`ZR_MeshLoadFromMemory(const void*, uint32, ZrMeshStatsHnd*)` 句柄式（句柄进 zkernel 句柄表或简单静态表+代际）→ `ZR_MeshStatsGet(hnd, ZmeshStats*)`、`ZR_MeshFree(hnd)`
4. 验证三路：
   - doctest：合成小 buffer（3 顶点 3 索引）roundtrip 解析+错误路（坏 magic/尺寸不一致拒绝）
   - EditMode 测试（ZFrame.Render.Tests 追加）：`var cube = new Mesh()` 用 `Unity Built-in Cube`（可 `GameObject.CreatePrimitive` 取 mesh 或手写 24 顶点立方体）烘焙到 TempLogs/temp.zmesh → ZMeshReader 读回断言 counts/首顶点/首索引；再经 `ZrNative` 新 P/Invoke 调 `ZR_MeshLoadFromMemory`（读文件字节）→ `ZR_MeshStatsGet` 断言 vertexCount==24、indexCount==36（以实际内置 Cube 为准，断言值先打印确认再固化）→ Free
   - 窗口程序 `--zmeshcheck <path>` 模式：加载并打印统计退出（供命令行验证）
5. CMake 挂接；/W4 /WX 零警告

**约束**：可动 `UnityProject/Assets/ZRender/`（含 Editor/Tests）、`Native/zrender/`、`Native/zplugin/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；**Unity 批处理撞锁等 60s 重试最多 10 次**（并行代理也在跑 Unity 测试）；原生构建自测 build_native.ps1 全绿；不 git commit；UTF-8 无 BOM/LF/#pragma once；架构校验器保持零违规。

**交付物**：格式+双侧解析+C ABI+三路验证+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理｜**完成**: 2026-10-24｜**结果**: 全部交付，三路验证全绿

### 1. 交付清单

| 文件 | 内容 |
|---|---|
| `UnityProject/Assets/ZRender/Runtime/ZMesh.cs` | .zmesh v1 格式常量/头定义（`ZmeshHeader` 20B POD，`Marshal.SizeOf` 断言）+ `ZMeshReader.Read`（magic/version/flags/体尺寸校验，错误抛 `InvalidDataException`） |
| `UnityProject/Assets/ZRender/Runtime/ZMeshBaker.cs` | `Bake(Mesh,string)` 静态方法 + 菜单 `ZFrame/ZMesh/Bake Selection` + 批处理入口 `BakeBuiltinCubeToRepoData`（`#if UNITY_EDITOR` 包裹置于 Runtime 程序集，不新增 asmdef，架构校验器零违规） |
| `UnityProject/Assets/ZRender/Bindings/ZrMesh.cs` | 托管门卫：`LoadFromMemory`（pin+P/Invoke，失败抛 `MeshLoadException`）/`GetStats`/`Free` |
| `UnityProject/Assets/ZRender/Bindings/ZrNative.cs` | 追加三导出 P/Invoke 声明 + `ZmeshStatsNative`（40B 定宽） |
| `UnityProject/Assets/ZRender/Tests/ZMeshTests.cs` | EditMode 测试 3 用例（见下） |
| `Native/zrender/zmesh.h/.cpp` | `ZmeshHeader`（static_assert 20B）/`ZmeshStats`（static_assert 40B）/`ZmeshData`（零拷贝列指针）+ `zmesh_Parse`（magic/version/flags/尺寸/索引范围五重校验+包围盒统计）+ `ZMESH_ERR_*` 六错误码 |
| `Native/zplugin/zplugin.cpp/.h` | 句柄表（64 槽静态表+代际，句柄=(代际<<32)|槽位）：`ZR_MeshLoadFromMemory`（先验证后拷贝入库重解析）/`ZR_MeshStatsGet`/`ZR_MeshFree`（代际推进防旧句柄误用） |
| `Native/zplugin/zrender.def` | 追加三导出（注意：def 内不放中文行内注释——链接器按 GBK 解析，教训已注释在文件头） |
| `Native/tests/test_zmesh.cpp` | doctest 10 用例：结构体大小断言、合成 3 顶点 3 索引 roundtrip、坏 magic/version/flags/截断/尺寸不一致/索引越界拒绝、C ABI 全链+坏数据+重复释放+野句柄 |
| `Native/tests/zrhi_d3d12_window.cpp` | `--zmeshcheck <path>` 模式（加载→打印统计→退出码） |
| `Native/CMakeLists.txt` | zmesh.cpp 并入 zrender_core、test_zmesh.cpp 并入 zrender_test |
| `Native/tests/data/cube.zmesh` | 入库示例资产（932B），来源：Unity 2022.3.62 内置 Cube（`GameObject.CreatePrimitive(PrimitiveType.Cube)` 实例化后烘焙） |

### 2. 头结构两侧对齐

- C++：`static_assert(sizeof(ZmeshHeader)==20)`、`static_assert(sizeof(ZmeshStats)==40)`（zmesh.h）
- C#：`Marshal.SizeOf(typeof(ZmeshHeader))==20` 运行时断言（ZMeshBaker.Bake 与 ZMeshReader.Read 双侧）
- magic：C++ `char[4]="ZMSH"` ↔ C# 小端 u32 `0x48534D5A`，字节序等值

### 3. Cube 实际值（实测固化）

Unity 内置 Cube：**vertexCount=24、indexCount=36**、normals=24、uv=24、包围盒 [-0.5,0.5]^3（EditMode 测试日志 `TempLogs/zmesh_edittest.log` 打印确认，断言值据此固化）。

### 4. 三路验证输出（自测亲跑）

1. **doctest**（build_native.ps1 /W4 /WX 零警告）：`test cases: 70 | 70 passed`，`assertions: 7322 | 7322 passed`（含 zmesh 10 新用例）
2. **EditMode**：results.xml `total=91 passed=91 failed=0`；`ZMeshTests` 3 用例全 Passed（BakeThenReaderReadback_MatchesMesh / NativeLoadFromMemory_StatsMatchCube（24/36/法线/uv/包围盒断言+重复释放拒绝）/ NativeLoadFromMemory_BadMagicRejected）
3. **--zmeshcheck 亲跑**（`Native/build/bin/Release/zrhi_d3d12_window.exe --zmeshcheck Native/tests/data/cube.zmesh`，exit 0）：
   ```
   zmeshcheck: vertexCount=24 indexCount=36 normals=1 uv=1 boundsMin=[-0.500 -0.500 -0.500] boundsMax=[0.500 0.500 0.500]
   zmeshcheck: OK
   ```
4. 附加：dumpbin /EXPORTS 核对 zrender.dll 6 符号齐（含三新导出）；架构校验器 ValidateFromCommandLine exit 0（违规数 0）；全部新文件 UTF-8 无 BOM/LF 抽查通过

### 5. 遗留/说明

- 句柄表非线程安全（编辑器/单线程加载期使用，头文件已注明；多线程消费留后续任务）
- 未 git commit（按纪律留给主控）

## 验收清单（主控写）

1. build_native.ps1 全绿（合成 roundtrip+错误路用例数）；EditMode 全绿（烘焙→读回→原生加载全链用例）
2. 窗口程序 `--zmeshcheck` 验收代理亲跑（用一个临时烘焙的 .zmesh 或施工代理入库的示例文件——若入库示例资产放 `Native/tests/data/` 并注明来源 Unity 内置 Cube）
3. C ABI 新符号 def 文件齐（dumpbin 或导出表核对三符号）
4. 头结构对齐：C# 与 C++ 两侧头定义一致（字节大小断言两侧都有或验收比对）
5. 错误路：坏 magic/尺寸不一致均拒绝（单测断言）
6. 文件规范抽查+架构校验器 exit 0
7. 施工报告完整（含 Cube 实际顶点/索引数）

## 验收结论（验收代理写）

**总判定: PASS**

机器项（未亲跑，引用）: 主控中立取证（build 70 用例/EditMode 91/91/zmeshcheck exit0 输出24顶点36索引/dumpbin 六导出）。

代码复核四项证据（验收子代理, 2026-10-23, 只读）:
1. 头对齐: C++ `Native/zrender/zmesh.h:31` `static_assert(sizeof(ZmeshHeader)==20)`（另 :42 `sizeof(ZmeshStats)==40`）; C# `UnityProject/Assets/ZRender/Runtime/ZMesh.cs:76-79` `Marshal.SizeOf(typeof(ZmeshHeader))!=HeaderSize(20)` 断言、`ZMeshBaker.cs:59` Bake 内同断言。两侧齐。
2. 错误路真实断言: `Native/tests/test_zmesh.cpp:107` 坏 magic `CHECK(...==ZMESH_ERR_BAD_MAGIC)`（TEST_CASE :102）; 尺寸不一致 :148/:155 `==ZMESH_ERR_BAD_SIZE`（TEST_CASE :141, 多1字节/少1字节双向）; 另坏 version :118、坏 flags :129、截断 :136-138、索引越界 :166。均 doctest CHECK 实断言非注释。
3. 文件规范抽查 4 新文件: zmesh.h/zmesh.cpp、ZMeshBaker.cs、ZMeshTests.cs — 首 3 字节均 `2f 2f 20`（无 BOM）、file 检测无 CRLF（纯 LF）、zmesh.h:18 `#pragma once`（.cpp/.cs 不适用）。通过。
4. 施工报告四节完整: §1 交付清单、§2 头结构两侧对齐、§3 Cube 实测 vertexCount=24/indexCount=36（TempLogs/zmesh_edittest.log 固化）、§4 三路验证输出（doctest 70/70+7322 断言、EditMode 91/91、--zmeshcheck exit0 输出引用、dumpbin 六导出）。齐。
