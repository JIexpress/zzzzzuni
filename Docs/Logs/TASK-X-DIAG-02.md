# TASK-X-DIAG-02 —— `test_zfg_env -s` 全量 SIGSEGV 根因定位与修复

- **轨道**：X / DIAG（基线即有，B-PLANET-03 发现）
- **状态**：doing（施工完成，待验收）
- **派发**：主控，2026-11-15
- **施工**：general-purpose 施工子代理，2026-11-15

---

## 一、任务简报（主控写）

### 1. 使命

`Native/tests/test_zfg_env.cpp:117` 附近，`zrender_test.exe -s`（doctest 成功断言全量打印）跑出
SIGSEGV（B-PLANET-03 发现，基线即有）。定位根因并修；修后 `-s` 全量跑通，且原有
**380 用例 / 823,621 断言**不回退。

### 2. 要求

1. 先复现：确切命令行、退出码/信号、栈回溯或 coredump 证据；不许"大概是因为"
2. 定位到具体语句与根因（越界/空指针/生命周期/栈溢出…），给最小修复；**不许为了绕过崩溃而跳测**
3. 复现前后命令与输出存档到本日志

### 3. 约束

- 白名单：`Native/tests/**`、`Native/zplanet/**`、本日志
- 不 commit、不改看板；UTF-8 无 BOM + LF；不启动 Unity
- 原生构建/测试沿用 `Native/` 既有链（`Tools/build_native.ps1` / `zrender_test.exe`）

---

## 二、施工报告（施工子代理，2026-11-15）

### 2.0 执行摘要

**根因（已用栈回溯实证）**：崩溃不在被测代码，在 doctest 的**成功断言值字符串化**路径。
`CHECK(heap == nullptr)`（`heap` 为 `std::unique_ptr<char[]>`）被 MSVC STL 的
`operator<<(ostream&, const unique_ptr&)`（`<memory>:3792`，实现为 `_Out << _Px.get()`）接管，
`char[]` 的 `get()` 是 `char*`，被按 **C 字符串**输出 → 指针为空时 `strlen(nullptr)` 读 `0x0`。
doctest 只在**需要打印断言值**时才字符串化——即 `-s`（成功断言也打印）；不带 `-s` 时成功断言
不打印，所以基线全绿、`-s` 必崩。**被测逻辑与生产 dll 零缺陷**。

**修复**：断言里的智能指针改为比较 `.get()`（3 处，语义恒等），断言总数不变。
**结果**：`-s` 全量 **380 用例 / 823,621 断言 / 0 failed / exit 0**（输出 2,476,343 行）；
不带 `-s` 仍 380/823,621；`zrender.dll` sha256 与修前逐位相同（双端 `ea97a375…d12c2`）。
变异验证：把一处 `.get()` 改回 `unique_ptr` 本体 → 该用例在 `-s` 下必红（SIGSEGV），逐字节还原后
sha256 回到 `515baade…` 并复绿。

### 2.1 复现（修复前，基线）

**环境**：Windows 11 / AMD 780M / Release（MSVC 2022 19.44）/ 仓库根为 CWD（与 ctest 一致）。

```bash
cd C:/Users/1/Desktop/zcode/GameFramework
./Native/build/bin/Release/zrender_test.exe -s
```

**输出（尾部）**：

```
C:\...\Native\tests\test_zfg_env.cpp(124): SUCCESS: CHECK( len == 64u ) is correct!
  values: CHECK( 64 == 64 )

C:\...\Native\tests\test_zfg_env.cpp(117): FATAL ERROR: test case CRASHED: SIGSEGV - Segmentation violation signal

===============================================================================
[doctest] test cases:    210 |    209 passed | 1 failed | 170 skipped
[doctest] assertions: 702173 | 702173 passed | 0 failed |
[doctest] Status: FAILURE!
```

- **退出码 1**，doctest 报 `SIGSEGV - Segmentation violation signal`（Windows 侧即 SEH
  `STATUS_ACCESS_VIOLATION` 0xC0000005）
- 崩溃点：`test_zfg_env.cpp:117` 的 TEST_CASE，**第一条断言（:124）绿、第二条（:125
  `CHECK(heap == nullptr)`）在打印前崩**——崩溃前该用例只登记了 1 条断言（应是 2 条）
- 全量跑到第 210 个用例即中断（后 170 个用例被计为 skipped）

**同命令不带 `-s`（对照）**：

```bash
./Native/build/bin/Release/zrender_test.exe
# → [doctest] test cases: 380 | 380 passed | 0 failed | 0 skipped
#   [doctest] assertions: 823621 | 823621 passed | 0 failed |  exit=0
```

**隔离复现（把范围压到单用例，排除前序用例污染）**：

```bash
./Native/build/bin/Release/zrender_test.exe -tc="zfg env: dynamic read rejects*" -s
# → assertions: 1 | 1 passed → FATAL ERROR: test case CRASHED: SIGSEGV   exit=1
./Native/build/bin/Release/zrender_test.exe -tc="zfg env: dynamic read rejects*"
# → assertions: 4 | 4 passed → Status: SUCCESS!                          exit=0
```

单用例、干净进程下同样只在 `-s` 崩 → **与用例顺序/前序状态无关，与 `-s` 这个"打印成功断言"开关强相关**。

### 2.2 根因定位（栈回溯 + 故障地址，实跑取证）

Release 无 PDB，故用**最小复现程序**（同一 doctest 头 `Native/ThirdParty/doctest/doctest.h`，
MSVC 同版本 /Zi + dbghelp 符号化，程序与产物放系统 temp，不入库）：

```cpp
TEST_CASE("variant A") { std::unique_ptr<char[]> heap; CHECK(heap == nullptr); }
TEST_CASE("variant B") { std::unique_ptr<char[]> heap; CHECK(heap.get() == nullptr); }
TEST_CASE("variant C") { std::unique_ptr<char[]> heap(new char[8]); CHECK(heap != nullptr); }
```

**运行 `repro.exe -s`（VEH 抓 SEH + StackWalk64/SymFromAddr）**：

```
[VEH] ACCESS_VIOLATION kind=0 faultaddr=0x0
[#0] strlen+0x31
[#1] std::operator<<<std::char_traits<char> >+0x38
[#2] doctest::detail::stringifyBinaryExpr<std::unique_ptr<char [0],std::default_delete<char [0]> >,std::nullptr_t>+0xaf
[#3] doctest::detail::Expression_lhs<std::unique_ptr<char [0],std::default_delete<char [0]> > const &>::operator==<std::nullptr_t>+0x7a
[#4] DOCTEST_ANON_FUNC_28+0xf4
[#5] doctest::Context::run+0x124f
```

`kind=0` = **读**访问违规，`faultaddr=0x0` = **读地址 0**，`#0 strlen` = 空指针当 C 字符串求长。

**变体对照（同程序、同开关）**：

| 变体 | 断言 | `-s` 结果 | 打印出的断言值 |
|---|---|---|---|
| A | `CHECK(heap == nullptr)`（`heap` 为空 `unique_ptr<char[]>`） | **SIGSEGV**（`faultaddr=0x0`） | —（崩在打印前） |
| B | `CHECK(heap.get() == nullptr)` | SUCCESS | `CHECK( nullptr == nullptr )` |
| C | `CHECK(heap != nullptr)`（非空，内容 `"abc"`） | SUCCESS | `CHECK( abc != nullptr )` ← **被当 C 字符串打内容** |

变体 C 是同一缺陷的"苟活形态"：指针非空时同样把 `char*` 当字符串流化，只是没崩。

**最小复现源（可复核；本轮放在系统 temp，未入库）**——把下面 40 行存成 `repro.cpp`，用 MSVC
`cl /std:c++17 /O2 /Zi /EHsc /utf-8 /I <仓库>/Native/ThirdParty repro.cpp /link dbghelp.lib`，
然后 `repro.exe -s`：

```cpp
#define DOCTEST_CONFIG_IMPLEMENT
#include "doctest/doctest.h"
#include <cstdio>
#include <memory>
#include <windows.h>
#include <dbghelp.h>
#pragma comment(lib, "dbghelp.lib")

static void PrintFrame(const char* tag, DWORD64 addr)
{
    char buf[sizeof(SYMBOL_INFO) + 256] = {};
    SYMBOL_INFO* sym = reinterpret_cast<SYMBOL_INFO*>(buf);
    sym->SizeOfStruct = sizeof(SYMBOL_INFO);
    sym->MaxNameLen = 255;
    DWORD64 disp = 0;
    if (::SymFromAddr(::GetCurrentProcess(), addr, &disp, sym))
        std::fprintf(stderr, "[%s] %s+0x%llx\n", tag, sym->Name,
            static_cast<unsigned long long>(disp));
}

static LONG WINAPI Veh(EXCEPTION_POINTERS* ep)
{
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION)
        return EXCEPTION_CONTINUE_SEARCH;
    static bool ready = false;
    if (!ready) { ::SymInitialize(::GetCurrentProcess(), nullptr, TRUE); ready = true; }
    std::fprintf(stderr, "[VEH] ACCESS_VIOLATION kind=%llu faultaddr=0x%llx\n",
        static_cast<unsigned long long>(ep->ExceptionRecord->ExceptionInformation[0]),
        static_cast<unsigned long long>(ep->ExceptionRecord->ExceptionInformation[1]));
    CONTEXT ctx = *ep->ContextRecord;
    STACKFRAME64 f = {};
    f.AddrPC.Offset = ctx.Rip;    f.AddrPC.Mode = AddrModeFlat;
    f.AddrFrame.Offset = ctx.Rbp; f.AddrFrame.Mode = AddrModeFlat;
    f.AddrStack.Offset = ctx.Rsp; f.AddrStack.Mode = AddrModeFlat;
    for (int i = 0; i < 8; ++i) {
        if (!::StackWalk64(IMAGE_FILE_MACHINE_AMD64, ::GetCurrentProcess(),
                ::GetCurrentThread(), &f, &ctx, nullptr, ::SymFunctionTableAccess64,
                ::SymGetModuleBase64, nullptr)) break;
        char tag[8]; std::snprintf(tag, sizeof(tag), "#%d", i);
        PrintFrame(tag, f.AddrPC.Offset);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

TEST_CASE("variant A: CHECK(unique_ptr<char[]> == nullptr)") // 空指针 → 崩
{ std::unique_ptr<char[]> heap; CHECK(heap == nullptr); }
TEST_CASE("variant B: CHECK(heap.get() == nullptr)")        // 修复写法 → 绿
{ std::unique_ptr<char[]> heap; CHECK(heap.get() == nullptr); }

int main(int argc, char** argv)
{
    ::AddVectoredExceptionHandler(1, &Veh);
    doctest::Context ctx; ctx.applyCommandLine(argc, argv); return ctx.run();
}
```

**完整因果链（逐环节带代码位置）**：

1. **触发条件**：doctest 只在打印断言值时构造"值字符串"；`-s`（`--success`）让**成功断言也打印**
   → 每次 `CHECK/REQUIRE` 都走字符串化。不带 `-s` 时成功断言不打印 → 全程不触发。
2. **doctest 选路**：`doctest.h:1089-1093` 的 `StringMaker<T>` 在 `has_insertion_operator<T>`
   为真时走 `StringMakerBase<true>`（`doctest.h:1080-1085`）→ `toStream`（`:1072-1077`）→
   `filldata<T>::fill` → `operator<<(stream, value)`（`doctest.h:1169-1176`）。
3. **哪来的 `operator<<`**：MSVC STL `<memory>:3792-3796`
   ```cpp
   template <class _Elem, class _Traits, class _Yty, class _Dx, enable_if_t<_Can_stream_unique_ptr<...>::value, int> = 0>
   basic_ostream<_Elem, _Traits>& operator<<(basic_ostream<_Elem, _Traits>& _Out, const unique_ptr<_Yty, _Dx>& _Px) {
       _Out << _Px.get();   // 转发裸指针
   }
   ```
   `_Can_stream_unique_ptr` 的条件是 **`get()` 可流化**，`char*` 可流化 → 该重载参与。
4. **致命细节**：`_Out << (char*)` 命中 `operator<<(ostream&, const char*)` = **按 C 字符串输出**，
   对 `nullptr` 即 `strlen(nullptr)` → 读 0x0。
   （若指针非空，则把堆内容打到日志里——变体 C；这里顺带把"日志里出现整段缓冲区内容"
   解释清楚：不是 bug 的巧合，是同一条转发路径。）
5. **为什么只在 `test_zfg_env.cpp:125/129` 崩**：全仓断言中的智能指针只有这两处用
   `unique_ptr` 与 `nullptr` 比较（`grep` 全 `Native/tests/**`：其余 `== nullptr` 均为裸指针，
   命中 `filldata<T*>` → 打印地址，安全）。`:112` 的 `REQUIRE(heap != nullptr)` 是同一路径但
   缓冲区非空（打印内容不崩）；一旦它真的失败（heap 为空），同样会在**打印失败信息时**崩，
   把真正的断言失败掩盖成"用例 CRASHED"。
6. **与生产无关**：`zfg::ReadEnvDynamicA` 的两条语义（拒绝超上限 → `*out` 空并返回所需长度；
   未设置 → 0 且 `*out` 空）在不带 `-s` 时被 4 条断言全绿验证；崩溃前后 `heap` 均为空指针，
   无悬垂/双重释放。

### 2.3 修复（最小 diff，语义恒等）

`Native/tests/test_zfg_env.cpp` 三处断言改为比较 `.get()`，并把"为什么必须这么写"写进用例
头部注释（防止后人再写回去）：

| 行（修前 → 修后） | 修前 | 修后 |
|---|---|---|
| 112 → 118 | `REQUIRE(heap != nullptr)` | `REQUIRE(heap.get() != nullptr)` |
| 125 → 135 | `CHECK(heap == nullptr)` | `CHECK(heap.get() == nullptr)` |
| 129 → 139 | `CHECK(heap == nullptr)` | `CHECK(heap.get() == nullptr)` |

- **语义零变化**：`unique_ptr` 与 `nullptr` 比较 ⇔ `get()` 与 `nullptr` 比较（标准库定义）
- **断言数零变化**：仍 4 条/用例，全量仍 823,621
- **不跳测**：用例保留，四条断言原样保留，只换被测表达式的写法
- 生产代码、`zrender.dll`、`zrender.def`、Unity 侧全部零改动（`git status` 仅该测试文件 1 处 M）

### 2.4 修复后验证（本机亲跑）

```bash
cd C:/Users/1/Desktop/zcode/GameFramework
# 1) 全量（构建脚本内置，无 -s）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1
#    → ALL GREEN；[doctest] 380 cases / 823621 assertions / 0 failed

# 2) 全量 -s（原崩溃命令）
./Native/build/bin/Release/zrender_test.exe -s > /tmp/final_s.txt
#    → exit=0；test cases: 380 | 380 passed | 0 failed | 0 skipped
#      assertions: 823621 | 823621 passed | 0 failed | Status: SUCCESS!
#      输出 2,476,343 行（成功断言逐条打印），FATAL ERROR 计数 0

# 3) 原崩溃用例单跑 -s
./Native/build/bin/Release/zrender_test.exe -tc="zfg env: dynamic read rejects*" -s
#    → 4/4 绿：CHECK( nullptr == nullptr ) ×2（不再崩）
```

**变异验证（证明修复点是真因，不是"改了就不崩了"）**：

1. 把 `:135` 临时改回 `CHECK(heap == nullptr)` → 重编 → `-tc=... -s` →
   **`test_zfg_env.cpp(127): FATAL ERROR: test case CRASHED: SIGSEGV`，exit=1**（红）
2. 用修前备份逐字节还原 → `sha256sum` = `515baade8c8216982b46909fd24208458245662743e37fdf5d5428fed179a809`
   （与变异前一致）→ 重编 → 全量 `-s` 复绿（上文 2 条）

**dll 一致性（证明零生产改动）**：

```
ea97a3758cfc18b679f131a8922bfa2c37fa8d99c76ca51873a4c9d4a67d12c2  Native/build/bin/Release/zrender.dll
ea97a3758cfc18b679f131a8922bfa2c37fa8d99c76ca51873a4c9d4a67d12c2  UnityProject/Assets/Plugins/ZRender/zrender.dll
```

与 B-PLANET-08 验收记录的 dll 哈希（`ea97a375…d12c2`）逐位相同 → 本次只改测试断言写法。

> **口径备注（同批次另一任务）**：同轮施工的 B-PLANET-13 在 `test_planet_channel.cpp` 新增
> 1 个用例 / 19 条断言，因此**本任务收尾时的仓库终态是 381 用例 / 823,640 断言**（`-s` 与非 `-s`
> 均 exit 0）。本任务自身的口径基线是 380 / 823,621 —— 两者差额全部归因 B-PLANET-13，不是
> 本任务的回退或增补。

### 2.5 改动文件清单

```
 M Native/tests/test_zfg_env.cpp     (+13/−4，断言写法 + 注释)
 A Docs/Logs/TASK-X-DIAG-02.md       （本日志）
```

白名单符合性：`Native/tests/**` ✓；`Native/zplanet/**` 零改动；`zplugin.h` 零改动；
`UnityProject/**` 零改动；TASKS/HANDOFF/WORKLOG 零改动；无 commit。

### 2.6 环境坑（本轮真实遇到，记录以便复核）

- `Tools/build_native.ps1` 首跑 **FAILED**：`error MSB6001: "CL.exe" 的命令行开关无效
  … 已添加项。字典中的关键字:"HTTP_PROXY"所添加的关键字:"https_proxy"`（exit=1）
  —— 交接档案 §3.2 的已知环境坑（代理变量大小写重复让 MSBuild 的 ProcessStartInfo 构造抛异常）。
  按 §3.2 在同一进程内清 8 个代理变量后重跑 → ALL GREEN。**与被测代码无关**，但会让复核者误判为
  "构建坏"，故记录。
- `cmake` 走 PATH 上的 MSYS2 版本时对 `Native/build/` 写 `CMakeCache.txt` 被拒（沙箱权限），
  显式用 VS BuildTools 自带 cmake 即正常（脚本内部的探测逻辑同样会挑到它）。

### 2.7 遗留与风险

1. **同类写法仅此一处**（全仓 `grep` 已核对）：其余 `== nullptr` 断言操作数均为裸指针。
   但这是一类**可复发的坑**：今后任何 `CHECK(unique_ptr/shared_ptr == nullptr)` 或
   `CHECK(smartPtr)` 都会在 `-s`/失败打印时踩同一路径（`shared_ptr` 在 MSVC `<memory>:1984`
   有同型 `operator<<`）。已在 `test_zfg_env.cpp` 头部登记纪律；是否在测试公共头里加
   `StringMaker<unique_ptr>` 特化（一次性根治）建议由主控裁决，本任务不做（避免扩大战线）。
2. **CI 未跑 `-s`**：CI/日常只跑默认（不带 `-s`），所以这条基线缺陷活了很久。建议在主控侧
   评估是否给原生段加一条 `-s` 的冒烟跑（成本：2.4M 行输出，建议重定向到文件）。

---

## 三、验收清单（主控写，验收代理逐条跑并出证据）

- **D1 复现证据**：修前现场可复核——`zrender_test.exe -s` 退出码 1、`test_zfg_env.cpp(117)`
  `FATAL ERROR: test case CRASHED: SIGSEGV`、断言数停在 1；不带 `-s` 同用例 4/4 绿（证明与 `-s` 强相关）
- **D2 根因证据**：栈回溯须落在 `strlen` ← `std::operator<<<char_traits<char>>` ←
  `doctest::detail::stringifyBinaryExpr<unique_ptr<char[]>, nullptr_t>`；故障地址 `0x0`、读访问；
  根因陈述与 MSVC `<memory>` 的 `unique_ptr::operator<<` 转发 `get()` 一致
- **D3 修复最小性**：diff 仅 `Native/tests/test_zfg_env.cpp`（3 处断言写法 + 注释），
  无生产/头文件/def/Unity 改动；断言语义与条数不变（4 条/用例）
- **D4 修复有效性**：`-s` 全量 **380/823,621 0 failed exit=0**；不带 `-s` 同口径；
  原崩溃用例 `-s` 单跑 4/4 绿
- **D5 变异复红**：把任一 `.get()` 改回 `unique_ptr` 本体 → 该用例 `-s` 必红（SIGSEGV）→
  逐字节还原（sha256 `515baade…`）→ 复绿
- **D6 不回退**：断言总数 ≥ 823,621 且用例数 = 380；`zrender.dll` sha256 双端一致且与
  B-PLANET-08 记录相同（`ea97a375…d12c2`）
- **D7 规范**：本日志四节齐全；触碰文件 UTF-8 无 BOM + LF；未 commit/未改看板

验收输出 PASS/FAIL + 逐条证据。

---

## 四、验收结论（验收代理填写）

- **验收**：验收子代理，2026-11-15（不采信施工数字，全部自跑；未启动 Unity；未 commit；HEAD 全程 `dfaf80b`）
- **判定：PASS（7/7）**

### 4.1 复现口径（本机实跑，CWD = 仓库根）

| 项 | 命令 | 结果 |
|---|---|---|
| 全量 `-s` | `./Native/build/bin/Release/zrender_test.exe -s > %TEMP%/acc_final_s.txt` | **exit 0 / 381 cases / 823,640 assertions / 0 failed / 0 skipped**，输出 2,476,420 行，`FATAL ERROR` 计数 **0** |
| 全量（无 `-s`） | 同上不带开关 | **exit 0 / 381 / 823,640 / 0 failed** |
| 基线（排除同批新用例） | `-tce="*B-PLANET-13*" -s` | **exit 0 / 380 cases / 823,621 assertions / 0 failed**（= 本任务口径基线，逐位吻合） |
| 本任务用例 `-s` 单跑 | `-tc="zfg env: dynamic read rejects*" -s` | 4/4 绿，exit 0（`CHECK( nullptr == nullptr )`） |

> 2,476,420 行 = 施工记录的 2,476,343 行 + 77 行（B-PLANET-13 的 1 用例 / 19 断言及其 MESSAGE），差额归因一致。

### 4.2 逐条

| 编号 | 判定 | 证据 |
|---|---|---|
| **D1 复现证据** | **PASS** | 历史现场源码已修，无法原样回放，故用**变异**回到修前写法（`:135` `heap.get()` → `heap`）重编后实测：`test_zfg_env.cpp(127): FATAL ERROR: test case CRASHED: SIGSEGV - Segmentation violation signal`、**exit=1**、`assertions: 1`（该用例应 4 条）——与施工记录的签名逐字相同。同一变异体**不带 `-s` 4/4 绿 exit=0** → 与 `-s` 强相关，实证成立 |
| **D2 根因证据** | **PASS（自建栈回溯）** | 验收侧独立重建最小复现（temp 目录，未入库，同版本 doctest 头 + MSVC 14.44.35207 `/O2 /Zi` + VEH/StackWalk64）实跑：`[VEH] ACCESS_VIOLATION kind=0 faultaddr=0x0`；`#0 strlen+0x31` ← `#1 std::operator<<<std::char_traits<char>>+0x38` ← `#2 doctest::detail::stringifyBinaryExpr<std::unique_ptr<char[0]>,std::nullptr_t>+0xaf` ← `#3 Expression_lhs<...>::operator==<nullptr_t>`。`kind=0`=读、`faultaddr=0x0`。变体 B（`.get()`）同程序同开关绿（`CHECK( nullptr == nullptr )`） | 
| **D2b 根因链代码核对** | **PASS** | ① doctest `StringMaker` 在 `has_insertion_operator<T>` 为真时选 `StringMakerBase<true>`（`Native/ThirdParty/doctest/doctest.h:1088-1092`）→ `toStream`；② MSVC `<memory>`：`_Can_stream_unique_ptr` 的 SFINAE 条件就是「`get()` 可流化」（`memory:3788-3792`），重载体 `_Out << _Px.get();`（`memory:3794`，施工写 3792-3796，实测行号吻合）；③ `char*` 走 `operator<<(ostream&, const char*)` → `strlen`。链成立，且崩溃只在 **需要打印断言值**（`-s` 或失败信息）时发生 |
| **D3 修复最小性** | **PASS** | `git diff --stat`：`Native/tests/test_zfg_env.cpp | 16 +-`（+13/−4）唯一改动；3 处由 `heap` 改为 `heap.get()`，**保留全部 4 条断言**（无删除/无 skip/无 `DOCTEST_CONFIG_DISABLE`）；`Native/zrhi/**`、`zplugin.h`、`zrender.def`、`UnityProject/**` 零 diff（`git status Native/` 仅两个测试文件 M）。读码确认是「改比 `.get()`」而非「绕过断言」：`:122/:135/:139` 三处均为真实比较，语义与 `unique_ptr == nullptr` 恒等 |
| **D4 修复有效性** | **PASS** | 见 4.1 四行：`-s` 与非 `-s` 均 **exit 0**，381/823,640 全绿；基线口径 380/823,621 不回退；原崩溃用例 `-s` 单跑 4/4 |
| **D5 变异复红** | **PASS** | 变异（`:135` 改回 `heap`）→ 重编 → `-tc=... -s`：**SIGSEGV，exit=1，assertions 停在 1**；逐字节还原（`cmp` 通过，sha256 = `515baade8c8216982b46909fd24208458245662743e37fdf5d5428fed179a809`，= 变异前）→ 重编 → 复绿 4/4 exit 0。**红-绿闭环成立，证明修复点即真因** |
| **D6 不回退 + dll 无改动** | **PASS** | 断言 823,621（排除新用例口径）≥ 基线；用例 380（同口径）；`zrender.dll` sha256 = `ea97a3758cfc18b679f131a8922bfa2c37fa8d99c76ca51873a4c9d4a67d12c2`，`Native/build/bin/Release` 与 `UnityProject/Assets/Plugins/ZRender` **双端一致**，且与 `TASK-B-PLANET-08.md:149/209` 与 `TASK-B-PLANET-09.md:99` 记录的历史值**逐位相同**；全程 dll mtime 保持 `07:11`（连构建都未重链该目标）→ 生产零改动实证 |
| **D7 规范** | **PASS** | 本日志四节齐全（本节为第四节）；触碰文件 UTF-8 **无 BOM**（首字节 `23 20 54`/`2f 2f 20`）+ **LF**（`od -c` 见 `\n` 无 `\r`）；未 commit（HEAD 仍 `dfaf80b`）；TASKS/HANDOFF/WORKLOG 零改动 |

### 4.3 变异记录（逐字节还原核对）

| 步 | 动作 | 文件 sha256 | 行为 |
|---|---|---|---|
| 0 | 施工完成态 | `test_zfg_env.cpp` = `515baade…79a809` | `-tc=... -s` 4/4 绿 |
| 1 | `:135` `heap.get()` → `heap` | 变更 | 重编 → `-s` **SIGSEGV exit=1**（assertions 1）；无 `-s` 4/4 绿 |
| 2 | 由备份 `cp` 还原 | 回到 `515baade…79a809`（`cmp` 通过） | 重编 → 4/4 绿 exit 0 |

构建产物还原说明：变异/还原三次重编后，`zrender_test.exe` 因 MSVC 链接非确定性产生不同字节（功能等价）。验收收尾已从备份**逐字节复原**为施工完成态 `5a7459c817e9161a48c4608c3051afd96810f02c47b1cfb97061d7b372f8409e`，并以该二进制复跑全量 `-s`/非 `-s` 双绿。

### 4.4 受限与观察（不影响判定）

1. **D1 属"变异复现"而非"历史现场回放"**：修复已落地，原崩溃现场无法原样重放；以同写法变异复现同一签名（行号/信号/断言计数一致）替代，证据强度等价。
2. **未做 coredump 级取证**：栈回溯来自验收侧最小复现程序（同 doctest 头、同编译器），非 `zrender_test.exe` 本体（Release 无 PDB）。机制与行号已由代码+变异+复现三方交叉确认。
3. **CI 仍不跑 `-s`**（施工 §2.7 遗留 2 未闭环）：本缺陷属于"只在 `-s`/失败打印路径触发"的类缺陷，建议主控评估加一条 `-s` 冒烟（重定向到文件）。
4. **同类写法复发风险**：`CHECK(smartPtr == nullptr)` / `shared_ptr` 在 MSVC `<memory>:1984` 有同型 `operator<<`。当前全仓仅本处，已在用例头登记纪律；是否在测试公共头加 `StringMaker` 特化仍待主控裁决（本任务未做，符合最小改动）。
