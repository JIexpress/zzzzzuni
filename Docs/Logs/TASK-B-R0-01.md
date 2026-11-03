# TASK-B-R0-01 CMake 原生构建基座 + zrender.dll

状态: doing | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：搭起 `Native/` 的 CMake 工程骨架，用 MSVC x64 构建出 `zrender.dll`（含 C ABI 版本函数）与独立测试程序，建立 doctest 单测基线与一键构建脚本。

**环境**（先读 AGENTS.md / HANDOFF.md）：
- CMake 4.4.3 全局可用；MSVC v143 在 `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools`（vcvars64.bat 位于 `VC/Auxiliary/Build/`）
- 仓库根: `C:/Users/1/Desktop/zcode/GameFramework`

**步骤建议**：
1. 目录骨架（每模块一个静态库目标 + 一个公共头；本任务只放最小占位实现，后续任务填充）：
   ```
   Native/
   ├─ CMakeLists.txt          # 顶层：选项/警告/输出目录（build/bin、build/lib）
   ├─ zkernel/zkernel.h + zkernel.cpp     # 占位：ZK_GetVersion()
   ├─ zrhi/zrhi.h + zrhi.cpp
   ├─ zrender/zrender.h + zrender.cpp
   ├─ zupscaler/zupscaler.h + zupscaler.cpp
   ├─ zplugin/zplugin.h + zplugin.cpp     # dll 对外入口：ZR_GetVersion() 聚合各模块版本
   ├─ tests/test_smoke.cpp                # doctest：版本函数+基础断言
   ├─ ThirdParty/doctest/doctest.h        # 单头文件版（见步骤2）
   └─ cmake/  (可选公共函数)
   ```
2. doctest 单头获取：从 GitHub 官方仓库 `doctest/doctest`（https）下载 `doctest/doctest.h` 放入 `Native/ThirdParty/doctest/`，同目录留 `SOURCE.md` 注明 URL/版本/日期。若网络失败：改用 CMake FetchContent，或报告注明并暂时自写最小断言宏（不可静默跳过测试）
3. CMake 要求：`cmake_minimum_required(3.24)`；`ZRENDER_VERSION 0.1.0`；MSVC `/W4 /utf-8 /MP`，Release `/O2`；目标：`zrender.dll`（SHARED，链接五模块静态库）、`zrender_test.exe`（doctest）
4. 构建脚本 `Tools/build_native.ps1`：configure（-G "Visual Studio 17 2022" -A x64）→ build（Release）→ 跑测试 → 若 `UnityProject/Assets/Plugins/` 存在则拷贝 dll 到 `UnityProject/Assets/Plugins/ZRender/`（本任务并行中 Unity 工程可能未建好：不存在则跳过拷贝并在输出注明，不算失败）
5. C ABI 设计（zplugin.h）：
   ```cpp
   extern "C" {
       uint32_t ZR_ApiVersion(void);      // 返回 1
       const char* ZR_GetVersion(void);   // "0.1.0"
       const char* ZR_GetBuildInfo(void); // 编译器/后端/日期
   }
   ```
6. 自测：`powershell -File Tools/build_native.ps1` 成功；`Native/build/bin/Release/zrender_test.exe` 全绿

**约束**：
- 不引入 doctest 之外的任何第三方；不 git commit；不修改治理文件
- C++ 规范见 Docs/Standards/file-conventions.md §3（UTF-8 无 BOM、LF、#pragma once）
- 本任务不涉及 Unity 侧代码（后续任务接入）

**交付物**：上述骨架、可运行的 build_native.ps1、全绿单测、本日志"施工报告"节（含变更清单、构建输出摘要、风险）

## 施工报告（施工代理写）

### ① 实现摘要

- `Native/CMakeLists.txt`（顶层单文件）：`cmake_minimum_required(3.24)`、`project(ZRender VERSION 0.1.0)`；MSVC 统一 `/W4 /utf-8 /MP /WX`（用 /WX 把"零警告"变成硬约束，任何警告直接构建失败）、Release 显式 `/O2`；输出目录 `build/bin`（exe/dll）与 `build/lib`（静态库/导入库）；版本宏 `ZR_VERSION_STRING="0.1.0"` 由 CMake 注入，全模块单一事实来源。
- 五模块静态库：`zkernel` / `zrhi` / `zrender_core`（源码在 `zrender/`，目标名避开与 dll 同名冲突）/ `zupscaler` / `zplugin`。每模块一个公共头 + 最小占位实现（版本函数），公共 include 根为 `Native/`，跨模块引用形如 `#include "zkernel/zkernel.h"`。
- `zrender.dll`（SHARED）：链接五模块静态库；导出不用 dllexport，改由 `zplugin/zrender.def` 声明 3 个 C 符号，链接器据此从静态库抽取实现（同一份头文件声明可直接被静态库消费者复用）；`dll_main.cpp` 为锚点翻译单元（不定义 DllMain，由 CRT 提供默认入口，避免引入 windows.h）。
- C ABI（`zplugin/zplugin.h`，简报规定三函数）：`ZR_ApiVersion()->1`、`ZR_GetVersion()->"0.1.0"`、`ZR_GetBuildInfo()` 聚合编译器/后端占位/五模块版本/`__DATE__ __TIME__`。
- 测试 `tests/test_smoke.cpp`（doctest，单头、`DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN`）：5 个 TEST_CASE / 16 断言，含验收要求的 `ZR_GetVersion()=="0.1.0"` 与 `ZR_ApiVersion()==1`。
- `Tools/build_native.ps1`：四步（configure `-G "Visual Studio 17 2022" -A x64` → build Release → 跑测试 → 条件拷贝 dll 到 `UnityProject/Assets/Plugins/ZRender/`，Plugins 不存在则跳过并注明）。含 cmake 探测回退逻辑（见风险 1）。纯 ASCII 无 BOM + CRLF（PS 5.1 无 BOM 脚本须 ASCII）。

### ② 变更文件清单（全部新增，共 17 个）

```
Native/CMakeLists.txt
Native/zkernel/zkernel.h            Native/zkernel/zkernel.cpp
Native/zrhi/zrhi.h                  Native/zrhi/zrhi.cpp
Native/zrender/zrender.h            Native/zrender/zrender.cpp
Native/zupscaler/zupscaler.h        Native/zupscaler/zupscaler.cpp
Native/zplugin/zplugin.h            Native/zplugin/zplugin.cpp
Native/zplugin/dll_main.cpp         Native/zplugin/zrender.def
Native/tests/test_smoke.cpp
Native/ThirdParty/doctest/doctest.h Native/ThirdParty/doctest/SOURCE.md
Tools/build_native.ps1
```
另有本日志（施工报告节 + 头部"施工:"字段）。构建产物全部落在 `Native/build/`（.gitignore 已排除）。

### ③ 自测结果

命令：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1`（先 `rm -rf Native/build` 全新构建），退出码 0。关键输出：

```
cmake: C:\Program Files (x86)\...\BuildTools\...\bin\cmake.exe (cmake version 3.31.6-msvc6)
-- Selecting Windows SDK version 10.0.26100.0 ... The CXX compiler identification is MSVC 19.44.35229.0
-- Configuring done / Generating done
zkernel.lib / zrhi.lib / zrender_core.lib / zupscaler.lib / zplugin.lib -> Native\build\lib\Release\
zrender.dll / zrender_test.exe -> Native\build\bin\Release\    （MSBuild 17.14）
[doctest] test cases:  5 |  5 passed | 0 failed | 0 skipped
[doctest] assertions: 16 | 16 passed | 0 failed |
[doctest] Status: SUCCESS!
skipped copy: UnityProject/Assets/Plugins does not exist yet (Unity side not ready - not an error)
ALL GREEN: configure + build + test succeeded
```
- 零警告证明：构建日志中编译器/链接器 warning 数为 0（全文 warning/error 关键字仅命中脚本自身横幅与 cmake 探测回退提示）；且 `/WX` 开启，出现任何警告构建即失败。
- 导出验证：`dumpbin /EXPORTS zrender.dll` → 恰好 3 个名字导出：`ZR_ApiVersion` / `ZR_GetBuildInfo` / `ZR_GetVersion`。
- `ctest -C Release`：100% tests passed (1/1)。

### ④ doctest 来源

- 版本 2.5.3（官方最新 release tag `v2.5.3`，2026-10-23 获取），仓库 `https://github.com/doctest/doctest`，文件 `doctest/doctest.h`，362,930 字节。
- 下载方式（降级路径）：`https://github.com/doctest/doctest/raw/...` 与 `https://raw.githubusercontent.com/doctest/doctest/v2.5.3/doctest/doctest.h` 在本机网络均被重置（Connection reset，非代理可控）；改用 GitHub 官方 REST API（https，域 `api.github.com`）：`GET /repos/doctest/doctest/contents/doctest/doctest.h?ref=v2.5.3`，base64 解码落盘。
- 完整性：解码结果按 `blob <size>\0<content>` 计算 git blob sha1 = `0b36ccafaed7f4802cdf1b1c322977489b1dd658`，与 API 返回 sha 逐字节一致（即与官方仓库对象完全相同）；SHA256 `cfd518a3ef90f67e1f3ba514df23fb3627437de1a2feeba78cf5062a40021421`。详见 `Native/ThirdParty/doctest/SOURCE.md`。

### ⑤ 已知风险

1. **cmake 探测回退**：本会话沙箱拒绝 PATH 上的 MSYS2 cmake（`C:\msys64\mingw64\bin\cmake.exe`）写入构建树（Permission denied）。脚本对候选 cmake（PATH 优先 → VS BuildTools 自带 3.31.6）做真实写探针后择优使用，两环境均可运行；代价是本机实际用的是 3.31.6 而非 AGENTS.md 所述全局 4.4.3（`cmake_minimum_required(3.24)` 对两者均满足，无功能差异）。日志首行会打印一条被拒候选的报错，属预期噪音。
2. **zrender.def 注释语法**：MSVC def 文件只支持 `;` 注释，`//` 会触发 LNK4017 警告（已被 /W4 流程发现并修正），后续编辑 def 请勿用 C++ 注释。
3. dll 目前仅 3 个占位符号、无真实功能；`backend=placeholder`。Unity 侧 P/Invoke 绑定由后续任务接入（当前 `UnityProject/` 尚不存在，脚本拷贝步骤按简报跳过）。
4. `ZR_GetBuildInfo` 使用函数内静态缓冲：首次调用拼接（C++11 magic static 保证只执行一次），之后只读返回、无数据竞争；若未来诊断信息变长需同步调整 512 字节缓冲上限。
5. doctest.h 为上游原样文件（自带版权头，未改动）；上游若发新版本，按 SOURCE.md 流程更新并复核 blob sha。

## 验收清单（主控写）

1. 验收代理独立执行 `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1`：configure+build+test 全部成功
2. `zrender_test.exe` 全部用例通过；断言包含 ZR_GetVersion()=="0.1.0" 与 ZR_ApiVersion()==1
3. 结构核对：五模块 + ThirdParty/doctest（SOURCE.md 注明来源 URL）+ tests 存在
4. dll 导出检查：dumpbin/exports 或等价方式确认三个 C 符号导出（dumpbin 在 BuildTools 里，或用 powershell 读取 PE 导出表）
5. 警告等级 /W4 且构建零 warning（日志证明）
6. 文件规范抽查：.h/.cpp 无 BOM、LF、#pragma once
7. 任务日志"施工报告"节完整

## 验收结论（验收代理写）

**总判定：PASS**（验收代理：general-purpose 验收子代理，2026-10-23；全部 7 项独立自跑，先 `rm -rf Native/build` 全新构建，不采信施工报告结论）

| # | 项 | 结果 | 证据（命令/输出关键行） |
|---|---|---|---|
| 1 | build_native.ps1 全流程 | PASS | 清空 `Native/build` 后独立执行 `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1`，退出码 0；configure（VS 17 2022 x64, MSVC 19.44.35229.0, SDK 10.0.26100.0）→ build（MSBuild 17.14，五 .lib + zrender.dll + zrender_test.exe）→ test → 拷贝步骤按简报跳过并注明；末行 `ALL GREEN: configure + build + test succeeded`。日志首行 cmake 探测报错为简报预告的候选回退噪音（实际采用 VS BuildTools 自带 cmake 3.31.6，满足 cmake_minimum_required(3.24)） |
| 2 | 单测全过 + 断言核对 | PASS | 独立运行 `Native/build/bin/Release/zrender_test.exe`：`test cases: 5 | 5 passed | 0 failed`、`assertions: 16 | 16 passed`、`Status: SUCCESS!`，退出码 0；读 `tests/test_smoke.cpp` 核实：L20 `CHECK(ZR_ApiVersion() == 1u)`、L27 `CHECK(std::strcmp(version, "0.1.0") == 0)`，两断言确在 |
| 3 | 结构核对 | PASS | `Native/` 下 `zkernel/zrhi/zrender/zupscaler/zplugin` 五模块各有 .h+.cpp；`ThirdParty/doctest/` 含 doctest.h + SOURCE.md（注明 https://github.com/doctest/doctest 、v2.5.3、blob sha1、下载日期 2026-10-23）；`tests/test_smoke.cpp` 存在 |
| 4 | dll 导出验证 | PASS | `dumpbin.exe /EXPORTS zrender.dll`（MSVC 14.44.35207 Hostx64/x64）：`number of functions: 3 / number of names: 3`，恰好 `ZR_ApiVersion`、`ZR_GetBuildInfo`、`ZR_GetVersion`，无多余符号 |
| 5 | 零警告 | PASS | 全新构建日志 grep `warning|警告` 零命中（仅命中项为 cmake 探测回退与"not an error"跳过提示）；`CMakeLists.txt` L27 `add_compile_options(/W4 /utf-8 /MP /WX)`、L29 Release `/O2`；/WX 下构建成功即零警告的硬证明 |
| 6 | 文件规范抽查 | PASS | 抽查 zplugin.h / zkernel.h / test_smoke.cpp / zrender.cpp / zupscaler.h（另覆盖全部 5 个模块头）：`head -c 3` 均为 `2f 2f 20`（非 EF BB BF，无 BOM）；`od -tx1` 全文字节扫描 0x0D 计数均为 0（纯 LF）；5 个 .h 全部含 `#pragma once` |
| 7 | 施工报告完整 | PASS | 本日志"施工报告"节 ①实现摘要 ②变更文件清单 ③自测结果 ④doctest 来源 ⑤已知风险 五小节齐全 |

保留意见（不影响判定）：本机实际 cmake 为 VS BuildTools 3.31.6 而非 AGENTS.md 所述全局 4.4.3，系沙箱拒绝 MSYS2 cmake 后脚本按预告的回退逻辑选择，两版本均满足工程要求；`zrender.def` 注释语法风险施工方已记录（仅 `;` 合法）。

