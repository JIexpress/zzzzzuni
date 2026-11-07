# TASK-X-SEC-01 —— DevConsole 认证 + rg.export 路径限定

> 缺口清单第 11 条，是六条里唯一有真实攻击面的。施工代理按本简报执行，第 2 节写报告。
> **禁止 git commit**。验收由另一代理执行。

## 现状

审计记录两条：
1. **原生 DevConsole 无客户端认证**：`devconsole_proto` 监听 127.0.0.1，命令白名单（`help/dump/spark/quality/fg/shot`），
   但**任何本机进程**连上就能发命令。白名单限制了危害面，但"谁都能连"本身不该是常态。
2. **`rg.export` 路径未限定目录**：可以把 RG 导出写到**任意路径**（本机任意写文件原语）。
   虽然只在 `--rg-export` 测试壳里，仍是"任意写"。

## 目标

把这两条从"默认信任"改成"默认拒绝，显式授权"。

## 任务

### 1）DevConsole 认证

任选其一或组合（说明理由）：
- **token 握手**：进程启动生成随机 token（`BCryptGenRandom`），打到 stderr/日志文件；
  客户端首帧必须带 token，不带或错则断开
- **对端校验**：仅接受**同用户**进程（Windows 可用 `GetExtendedTcpTable` 拿对端 PID 再查 token/SID）
- **默认关闭**：`--devconsole` 才监听（现状是否已如此？确认），且绑定 127.0.0.1 不许 0.0.0.0

**硬性**：
- token 比较用**常量时间**比较（防时序侧信道），不要 `strcmp`
- 认证失败要限流（连续失败 N 次断开/封一段时间），日志记录对端
- 不许明文把 token 写进会随包分发的位置；日志要标注"仅本地调试"

### 2）`rg.export` 路径限定

- 允许的根目录（择一或可配）：exe 同目录的子目录 / `%LOCALAPPDATA%\ZFrame\rg_export` / 显式 `--rg-export-dir`
- 收到的路径必须**规范化后**（`GetFullPathName` / `std::filesystem::weakly_canonical`）再判断
  是否在允许根下，**拒绝 `..` 穿越与符号链接逃逸**
- 拒绝时返回明确错误码 + 日志（不要静默失败）

## 判据

- 无 token / 错 token 连 DevConsole → 被拒绝并记录；正确 token → 命令可用
- `rg.export` 传 `../../windows/system32/xxx` 或绝对路径到允许根外 → **被拒绝**（附实际日志）
- 正常路径（允许根内）仍可用
- 回归：原生 317、`--draw` 60 帧、`--devconsole` 正常功能不退化
- 不改游戏内容

## 环境纪律（真实踩过）

- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- **PATH 上的 cmake 是 MSYS2 版无写权限** → 必须走 `Tools/build_native.ps1`
- Unity 批处理**单实例**：查进程 + 删 `UnityProject/Temp/UnityLockfile`；**勿加 `-quit`**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不动游戏内容；`.cs/.cpp/.h` 无 BOM、LF
- **不要为了"通过"把功能关掉**（比如直接禁用 DevConsole）——要的是"能用且安全"

---

## 施工报告（施工代理写）

> 施工代理：DeepSeek-V4.1-Flash（TASK-X-SEC-01）。未 commit，未改看板/交接/README，未动游戏内容。

### 0. 结论

两条攻击面都从"默认信任"改成"默认拒绝，显式授权"，且**功能仍可用**（没有禁用 DevConsole）：

| 项 | 结果 |
|---|---|
| DevConsole 认证 | 每连接首帧必须 `auth <32hex>`；无/错 token 拒绝并记录对端；5 次失败封 30s；正确 token 全命令可用 |
| token 比较 | 常量时间（逐字节异或累积、无短路、无 strcmp） |
| `rg.export` 路径 | 只允许写在 3 个允许根内；`..`/绝对越界/符号链接与 junction 逃逸全部拒绝，返回明确错误码 + 日志 |
| 回归 | 原生单测 **332/332**（基线 317 + 新增 15）；`--draw` 60 帧 OK；`hud/help/rg.export/quit/renderdoc.capture` 行为不退化 |
| 现场 | 无残留进程；工作树只剩源码 + 本任务日志（`build_native.ps1` 会顺带部署 `zrender.dll`，已还原，避免二进制混进改动） |

### 1. DevConsole 认证

#### 1.1 方案与理由

采用 **token 握手（BCryptGenRandom）+ 仅回环 + 默认关闭** 三者组合：

1. **token 握手**（新增 `zrender/devconsole_auth.*`）
   - 进程启动生成 128 位 CSPRNG token（`BCryptGenRandom`，`BCRYPT_USE_SYSTEM_PREFERRED_RNG`），
     32 个小写十六进制字符，打到 stdout：
     `DEVCONSOLE auth token (LOCAL DEBUG ONLY - do not ship this log): <32hex>`
   - 每连接首帧必须是 `auth <token>`；通过后回执 `{"ok":true,"authenticated":true}`（不关连接），
     第二行才是命令，命令响应后关连接。**协议仍是"一连接一命令"**，只是先握手。
2. **仅回环**：bind 本来就只有 `127.0.0.1`；本次在 `HandleConnection` 再按
   `ntohl(addr) >> 24 == 127` 做一次纵深校验（非回环直接 `auth_denied` 并记录）。
3. **默认关闭**：现状本来就是必须显式 `--devconsole` 才监听（`main` 分发处），本次未改变，
   并在启动日志/注释里固化这一事实。

**为什么没选"对端同用户校验"**：Windows 上用 `GetExtendedTcpTable` 反查对端 PID 再取 SID
需要 `OpenProcess`/`OpenProcessToken` 权限，PID 存在复用窗口，且本机低权限进程同样能伪造
时序；对本机调试端口而言，token 的授权语义更直接（谁能读到启动日志谁才能连），也不引入
额外的进程枚举与权限失败分支。token + 回环 + 默认关闭已覆盖"任意本机进程连上就能发命令"。

**为什么没选"直接禁用"**：简报明确要求"能用且安全"；token 方案保留了原有全部命令与
自动化用法（客户端只需多一行握手）。

#### 1.2 常量时间比较（`devconsole_auth.cpp`）

```cpp
bool ZrDevConsoleTokenEquals(const std::string& a, const std::string& b)
{
    const size_t rounds = a.size() >= b.size() ? a.size() : b.size();
    unsigned diff = (a.size() == b.size()) ? 0u : 1u;   // 长度不等：差异位恒置位
    for (size_t i = 0; i < rounds; ++i) {
        const unsigned char ca = i < a.size() ? (unsigned char)a[i] : 0u;
        const unsigned char cb = i < b.size() ? (unsigned char)b[i] : 0u;
        diff |= (unsigned)(ca ^ cb);
    }
    return diff == 0u;
}
```

- 轮数只取决于长度（token 定长 32，长度不是秘密），循环内**没有数据相关分支**、不提前返回。
- 不折叠大小写：token 是二进制转写，"A" 与 "a" 是不同 token。
- 空 token 特判：`Verify()` 在 `m_token.empty()` 时**直接拒绝**（防 `""==""` 的退化放行），
  `Start()` 也拒绝空 token 启动（fail-closed）。
- 单测覆盖：相等、首字节差、末字节差、长度 ±1、空串、大小写差（7 组断言）。

#### 1.3 认证失败限流

- 策略：**连续 5 次失败 → 封禁 30s**（`kZrDevConsoleAuthMaxFailures` / `kZrDevConsoleAuthLockoutMs`）。
- 计数对象：首帧不是 `auth <token>`（`missing_auth`）、token 错误（`bad_token`）；
  **连上不发首帧就断开不计数**（避免误连/端口探测把正常客户端拖入封禁）。
- 封禁期内任何连接立即 `auth_locked`（不做比较、不累加）；到期惰性解锁并清零计数；
  一次成功认证立即清零失败计数。
- 每次失败都记录对端 `peer=IP:port` 与计数/剩余时间，例如：

```
DEVCONSOLE AUTH DENIED peer=127.0.0.1:64870 reason=missing_auth failures=1/5 (LOCAL DEBUG ONLY)
DEVCONSOLE AUTH DENIED peer=127.0.0.1:64874 reason=bad_token failures=5/5 NOW_LOCKED (LOCAL DEBUG ONLY)
DEVCONSOLE AUTH DENIED peer=127.0.0.1:64875 reason=locked remaining_ms=29972 failures=5/5 (LOCAL DEBUG ONLY)
DEVCONSOLE AUTH OK peer=127.0.0.1:64883 (LOCAL DEBUG ONLY)
```

#### 1.4 token 的存放

- **只写 stdout**（启动日志），不写配置文件、不写注册表、不写进任何会随包分发的位置；
  日志行显式标注 `LOCAL DEBUG ONLY - do not ship this log`。
- token 每次启动重新生成（单测断言两次生成不同）；生成失败直接退出（不退化到弱随机/无认证）。
- `zrhi_d3d12_window.exe` 本身是测试壳程序，不在 Unity 插件/发布包内。

### 2. `rg.export` 路径限定

新增 `zrender/rg_export_path.*`（纯逻辑，可单测），窗口程序在**写盘前**调用
`ZrResolveRgExportPath(requested, roots)`，拒绝即返回错误码 + 日志，不静默失败。

#### 2.1 允许根（三个并列生效）

1. `<exe 所在目录>/rg_export`（构建产物区，例如 `Native/build/bin/Release/rg_export`）
2. `%LOCALAPPDATA%\ZFrame\rg_export`（缺省落点）
3. `--rg-export-dir <path>`（显式根；同时成为缺省落点，优先级最高）

缺省落点优先级：`--rg-export-dir` > `%LOCALAPPDATA%\ZFrame\rg_export` > `<exe>/rg_export`。
三者都不依赖进程 CWD。**行为变化（有意）**：`rg.export` 不带参数时不再写
`Native/build/renderdoc/rg_export.json`，而是写到上面选出的缺省根（本机实测为
`%LOCALAPPDATA%\ZFrame\rg_export`）。`--devconsole-dir` 仍只负责 RenderDoc 捕获目录。

#### 2.2 校验顺序（四层）

1. **形态**：拒绝含 NUL、超长（>4096）、盘符相对（`C:file`）、无盘符根相对（`\foo`）、
   UNC（`\\server\share\...`——规范化可能卡在网络等待上，兼作 DoS 面）、
   任何分量为 `..`（即使规范化后会落回根内也拒绝——规则更严格、更好审计）、含 `:` 的分量
   （ADS）、尾随 `.`/空格的目录名、保留设备名（`CON`/`NUL`/`COM1`…，含 `CON.txt`）。
2. **拼绝对路径**：相对路径以 `defaultRoot` 为基准，绝对路径按原样。
3. **规范化 + 逐分量包含判定**：`weakly_canonical`（跟随符号链接/junction 解析）后，
   与每个允许根的 canonical 形态做**逐分量**前缀比较（大小写不敏感），
   防止 `C:\...\rg_export_evil` 靠字符串前缀蒙混；要求目标严格深于根。
4. **根以下 reparse point**：对原始路径在根以下的分量逐个查
   `symlink_status` + `GetFileAttributesW(FILE_ATTRIBUTE_REPARSE_POINT)`，
   命中即 `path_symlink`（先于包含判定，保证 junction 指向根外时报的是"符号链接逃逸"
   而不是笼统的"越界"）。

错误码：`path_traversal` / `path_outside_root` / `path_symlink` / `path_invalid` /
`path_too_long` / `path_root_unavailable`，同时写 `detail`（含命中根与解析结果）。

#### 2.3 顺带堵住的同类旁路

`renderdoc.capture [name]` 原来把 name 直接拼进路径模板（`<dir>/<name>`），同样是任意路径写。
现在 name 走 `ZrRgExportValidateLeafName`：只允许纯文件名，拒绝分隔符、`..`、冒号、设备名；
拒绝时 `{"ok":false,"error":"bad_capture_name"}` + 日志。合法名字（如 `sceneA`）行为不变。

### 3. 改动清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `Native/zrender/devconsole_auth.h/.cpp` | 新增 | token 生成/形态校验/常量时间比较/失败限流状态机 |
| `Native/zrender/rg_export_path.h/.cpp` | 新增 | 允许根解析、`..`/设备名/规范化/逐分量包含/重解析点检查 |
| `Native/tests/test_devconsole_auth.cpp` | 新增 | 8 个用例（token 形态、比较语义、限流窗口、空 token fail-closed、策略参数） |
| `Native/tests/test_rg_export_path.cpp` | 新增 | 7 个用例（根内通过、穿越/越界/形态非法、junction 逃逸、leaf name） |
| `Native/tests/zrhi_d3d12_window.cpp` | 改 | 首帧认证、限流/日志、`--rg-export-dir`、`rg.export` 沙箱、capture name 校验；顺带修掉同连接多行粘包与拒绝响应被 RST 丢弃两个传输层缺陷 |
| `Native/CMakeLists.txt` | 改 | 新源文件 + `bcrypt` 链接 + 两个新测试 |

### 4. 实测（真进程 + 真 TCP 客户端）

脚本：`TempLogs/archive_pre_audit/TASK-X-SEC-01/e2e_sec01.ps1`（从启动日志解析 token，
全部断言 PASS）；日志：同目录 `e2e_result.log` / `devconsole_stdout.log`。

| 用例 | 客户端收到的响应 |
|---|---|
| 不发认证帧直接 `hud` | `{"ok":false,"error":"auth_required",...}` |
| 错 token | `{"ok":false,"error":"auth_failed","detail":"invalid DevConsole token"}` |
| 连续第 5 次错 token | `{"ok":false,"error":"auth_locked","detail":"too many failed attempts; retry in 29972 ms"}` |
| 封禁期内用正确 token | `auth_locked`（30s 到期后同一 token 通过） |
| 正确 token + `help` | `{"ok":true,"authenticated":true}` + `{"ok":true,"commands":[...5 条...]}` |
| 正确 token + `hud` | `{"ok":true,"drawCalls":3,"drawCallsPeak":3,"rtWidth":640,"rtHeight":480,"memoryBytes":4916880,...,"fps":90.13}` |
| `rg.export ../../windows/system32/evil.json` | `{"ok":false,"error":"path_traversal","detail":"'..' component is not allowed ..."}` |
| `rg.export C:\Windows\System32\evil.json` | `{"ok":false,"error":"path_outside_root","detail":"path resolves outside the allowed export roots (...); resolved='C:\\Windows\\System32\\evil.json'"}` |
| `rg.export ..\..\..\evil.txt` | `{"ok":false,"error":"path_traversal",...}` |
| `rg.export escape/evil.json`（根内 junction 指向根外） | `{"ok":false,"error":"path_symlink","detail":"reparse point (symlink/junction) under export root: '...\\export\\escape'"}` |
| `rg.export graph.json` | `{"ok":true,"path":"...\\rg_export\\graph.json","format":"json","bytes":2183,"passes":3,"barriers":5}`，文件存在 |
| `rg.export sub/dir/graph.txt text` | `{"ok":true,...,"bytes":910}`，嵌套目录自动创建，文件存在 |
| `rg.export out.xml xml` | `{"ok":false,"error":"bad_format",...}`（格式校验先于路径） |
| `renderdoc.capture` | `{"ok":false,"error":"renderdoc_not_attached",...}`（未挂接，明确报错不假成功） |
| `renderdoc.capture ../escape` | `{"ok":false,"error":"bad_capture_name",...}` |
| `quit` | `{"ok":true}` 且进程 exit=0 |

服务端日志（拒绝侧，节选原文）：

```
DEVCONSOLE rg.export REJECTED peer=127.0.0.1:64885 error=path_traversal req='../../windows/system32/evil.json' detail='..' component is not allowed (use a path inside the export root)
DEVCONSOLE rg.export REJECTED peer=127.0.0.1:64886 error=path_outside_root req='C:\Windows\System32\evil.json' detail=path resolves outside the allowed export roots (C:\...\Release\rg_export, C:\Users\1\AppData\Local\ZFrame\rg_export, C:\...\Temp\zr_sec01_5506669b3c1d473fa94c29ee76f6337a\export); resolved='C:\Windows\System32\evil.json'
DEVCONSOLE rg.export REJECTED peer=127.0.0.1:64887 error=path_traversal req='..\..\..\evil.txt' detail='..' component is not allowed (use a path inside the export root)
DEVCONSOLE rg.export REJECTED peer=127.0.0.1:64888 error=path_symlink req='escape/evil.json' detail=reparse point (symlink/junction) under export root: 'C:\...\export\escape'
DEVCONSOLE rg.export OK peer=127.0.0.1:64889 path='C:\...\export\graph.json' bytes=2183
DEVCONSOLE rg.export OK peer=127.0.0.1:64890 path='C:\...\export\sub\dir\graph.txt' bytes=910
```

### 5. 回归

| 项 | 结果 |
|---|---|
| 原生单测 | `332 | 332 passed | 0 failed | 0 skipped`（基线 317，新增 15 用例；`assertions 776924`） |
| `--draw` | `frames=60 center checks=6 passed=6`、`InfoQueue error count = 0`、`D3D12 FIRST DRAW OK`、exit 0 |
| DevConsole 功能 | `help/hud/rg.export(json/text/嵌套)/renderdoc.capture/quit` 全部实测可用（上表） |
| 构建 | `build_native.ps1` 全绿，`/W4 /WX` 零警告 |
| 游戏内容 | 未改；本次仅 `Native/` 下 C++ 源码 + 任务日志 |

### 6. 施工中发现并修掉的两个传输层缺陷（与安全直接相关）

1. **认证帧与命令帧粘连**：客户端把 `auth <token>\n<command>\n` 一次写出时，原读行函数
   只保留到第一个 `\n`、丢弃同段剩余字节，命令永远执行不到。改为每连接一个
   `ProtocolLineReader` 保留残余缓冲。
2. **拒绝响应被 RST 丢弃**：带着未读入站数据 `closesocket` 会让 TCP 回 RST，客户端看不到
   `auth_failed/path_*` 原因。`SendAndClose` 现在 `shutdown(SD_SEND)` 后最多排空 8KB/100ms
   再关闭（有上限，恶意客户端拖不住线程）。

### 7. 边界与建议（不阻塞验收）

- token 走 stdout，若把启动日志重定向到可分发目录仍可能泄漏——日志行已显式标注，部署脚本
  不应收集该日志。
- 监听线程仍是串行处理（原有设计），单连接最坏占用 ~5s（认证）/5s（命令）读超时；如需抗
  本机 DoS 可改为每连接独立线程或更短超时，属后续加固。
- 建议后续把 Unity 侧运行期 DevConsole（17322）对齐同一 token 契约；本任务范围只含原生壳。

---


## 验收清单（供验收代理）

- [ ] V1 无 token / 错 token 被拒（附日志），正确 token 可用
- [ ] V2 token 用常量时间比较
- [ ] V3 `rg.export` 路径穿越被拒（`..`、绝对路径、符号链接），正常路径可用
- [ ] V4 回归：317 / --draw 60 帧 / DevConsole 功能不退化
- [ ] V5 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

> 验收代理：与施工不同的代理。**不采信施工自述**，全部结论来自本代理自己发起的攻击请求、
> 自己构建的产物与自己读的源码。证据原始文件：`TempLogs/archive_pre_audit/TASK-X-SEC-01-verify/`
> （`client.py`/`run.py`/`extra.py`/`auth_extra.py` 为独立客户端，`server_stdout.log` 为服务端原文，
> `timing.cpp` 为常量时间测量）。构建走 `Tools/build_native.ps1`（MSBuild 17.14 + cmake 3.31.6-msvc6），
> 全绿、`/W4 /WX` 零警告。

### 逐条裁决

| 项 | 裁决 | 一行证据 |
|---|---|---|
| V1 认证 | **PASS** | 无认证帧→`auth_required`；错 token→`auth_failed`；第 5 次后连正确 token 也 `auth_locked`；31s 后同 token 通过并 `help/hud` 可用 |
| V2 常量时间 | **PASS** | `diff \|= ca^cb` 循环无 break/return、无数据相关分支、非 `strcmp/memcmp`；实测首字节不等 vs 末字节不等 66±2 ns/次（差 <3%，短路实现应差 ~32×） |
| V3 路径穿越 | **PASS** | 5 类攻击全部拒绝且错误码正确（见下），真实 junction 未逃逸（根外目录跑完仍为空），根内两种正常写成功 |
| V4 回归 | **PASS** | 原生 `332 \| 332 passed \| 0 failed`（assertions 776924）；`--draw` `frames=60 center checks=6 passed=6`、`InfoQueue error count = 0`、exit 0；`help/hud/rg.export/renderdoc.capture/quit` 全部实测可用 |
| V5 现场 | **PASS** | 未 commit（HEAD 仍 `64e05a4`）；`TASKS/HANDOFF/WORKLOG/README` 0 改动；游戏内容 0 改动（构建顺带覆盖的 `UnityProject/.../zrender.dll` 已还原，md5 与 HEAD 一致 `430478de…`）；无残留进程/端口 |

**最终裁决：PASS。**

### 1. 认证四情形（自己连的，原文响应）

每次连接都是独立 TCP 会话（源端口 54910/54911/… 各不相同，证明重连不重置计数）：

```
A1 无认证帧直接发 hud      < {"ok":false,"error":"auth_required","detail":"first frame must be: auth <token> (token is printed at startup)"}
A2 错 token ×4             < {"ok":false,"error":"auth_failed","detail":"invalid DevConsole token"}
A3 第 5 次后正确 token      < {"ok":false,"error":"auth_locked","detail":"too many failed attempts; retry in 29980 ms"}
A4 封禁期内发 hud           < {"ok":false,"error":"auth_locked","detail":"too many failed attempts; retry in 29979 ms"}
A5 31s 后正确 token(单次写出 auth+help)
                           < {"ok":true,"authenticated":true}
                           < {"ok":true,"commands":["help","hud","rg.export [path] [json|text]","renderdoc.capture [name]","quit"]}
A6 正确 token+hud          < {"ok":true,"authenticated":true}
                           < {"ok":true,"drawCalls":3,"drawCallsPeak":3,"rtWidth":640,"rtHeight":480,...,"fps":90.1732,"frames":60}
```

服务端拒绝侧原文（`server_stdout.log`，peer 逐条不同 = 每次都是新连接）：

```
DEVCONSOLE AUTH DENIED peer=127.0.0.1:54910 reason=missing_auth failures=1/5 (LOCAL DEBUG ONLY)
DEVCONSOLE AUTH DENIED peer=127.0.0.1:54914 reason=bad_token failures=5/5 NOW_LOCKED (LOCAL DEBUG ONLY)
DEVCONSOLE AUTH DENIED peer=127.0.0.1:54915 reason=locked remaining_ms=30000 failures=5/5 (LOCAL DEBUG ONLY)
DEVCONSOLE AUTH OK peer=127.0.0.1:54936 (LOCAL DEBUG ONLY)
```

追加旁路（自己加测，全部拒绝）：大写 token → `auth_failed`（不折叠大小写）；正确 token
截断 31 位 → `auth_failed`；正确 token 后追加 1 字符 → `auth_failed`；只发 `auth` 或 `auth ` →
`auth_required`。3 次失败后正确 token 仍可通过（成功即清零失败计数，未把正常客户端拖入封禁）。
`quit` → `{"ok":true}` 且 exit=0（`D3D12 DEVCONSOLE OK`）。

**默认关闭**：`DevConsoleServer` 全文件只有一处实例化（`zrhi_d3d12_window.cpp:13738`，在
`RunDevConsoleMode` 内），无 `--devconsole` 不会创建监听。**仅回环**：运行期 netstat 实测
`TCP 127.0.0.1:17391 ... LISTENING`，无 `0.0.0.0`/IPv6 监听。

### 2. 路径五类攻击（自己发的，实测错误码）

| 攻击 | 请求 | 实际响应 |
|---|---|---|
| 相对穿越 | `../../windows/system32/evil.json` | `{"ok":false,"error":"path_traversal",...}` |
| 绝对越界 | `C:\Windows\System32\evil.json` | `{"ok":false,"error":"path_outside_root","detail":"...resolved='C:\\Windows\\System32\\evil.json'"}`
| junction 逃逸 | `escape/evil.json`（`rg_export\escape` 为真实 junction → `outside`，`attributes=Directory, ReparsePoint`） | `{"ok":false,"error":"path_symlink","detail":"reparse point (symlink/junction) under export root: '...\\rg_export\\escape'"}`
| 前缀攻击（绝对） | `<base>\rg_export_evil\pwn.json` | `{"ok":false,"error":"path_outside_root",...}`（逐分量，不是 startsWith）
| 前缀攻击（相对） | `../rg_export_evil/pwn.json` | `{"ok":false,"error":"path_traversal",...}` |

攻击后取证：`outside/` **仍为空目录**（junction 未被写入），`rg_export_evil/` 只有我自己放的
`marker.txt`（未被写）。正常侧：`graph.json` → `{"ok":true,...,"bytes":2183}`、`sub/dir/graph.txt text`
→ `{"ok":true,...,"bytes":910}`，文件均实际落盘。

进程 CWD 特意设在 `rg_export` 的**父目录**（`TempLogs/verify_sec01`）：相对路径 `rg_export_evil/pwn2.json`
落在 `<root>\rg_export_evil\pwn2.json` 而**不是** `<base>\rg_export_evil\`，证明相对路径以
`defaultRoot` 为基准、与 CWD 无关。

补充形态类（自己加测）：ADS `graph.json:evil`、设备名 `CON`、尾随点 `graph.json.`、UNC
`\\localhost\C$\...`、盘符相对 `C:evil.json`、无盘符根相对 `\Windows\evil.json` → 全部 `path_invalid`；
8.3 短名 `ZCODE~1\...\rg_export_evil\pwn4.json` → `path_outside_root`（短名未展开但 fail-closed 拒绝）；
第二根 `%LOCALAPPDATA%\ZFrame\rg_export\audit_8f2.json` → `ok`（多根并列生效）。

### 3. 常量时间比较是否可信

源码 `Native/zrender/devconsole_auth.cpp:67-81`：轮数 = `max(len)`，循环体内
`diff |= (unsigned)(ca ^ cb)`，**无 break/return/条件跳过**，长度不等时预先置位；未使用
`strcmp`/`memcmp`/`std::string::compare`。空 token 由 `Verify()` 显式拒绝（`m_token.empty()` → Rejected），
不依赖 `""==""`。轮数仅取决于长度（token 定长 32，长度非秘密）。

经验验证（自建 `timing.cpp` 链接 `zrender_core.lib`，各 3,000,000 次）：
`equal 63.0ns`、`mismatch@0 60.7/67.0/65.9/69.9ns`、`mismatch@31 61.7/66.8/66.6/68.9ns` ——
首字节与末字节不等的耗时差在噪声内（<3%），且方向不固定；若为短路比较，`mismatch@0`
应约等于 `mismatch@31` 的 1/32。结论：**可信**。

### 4. token 是否落盘

- 代码面：全仓 `grep` `authToken` 仅见 `printf` 到 stdout（`zrhi_d3d12_window.cpp:13527-13529`），
  无 `ofstream/fopen/注册表/配置文件` 写入；行内含 `LOCAL DEBUG ONLY - do not ship this log`。
- 实测面：把本次运行产生的 6 个 token 在 `Native/zrender`、`Native/tests`、`Native/shaders`、`Docs`、
  `Tools`、`UnityProject/Assets` 全量搜索 → **零命中**；运行期新增/改写的文件只有
  `pso_cache.bin`、`rg_export/*`、空目录 `Native/build/renderdoc`。
- 唯一含 token 的文件是**我自己**把进程 stdout 重定向出来的 `server_stdout.log`（采集行为，非程序写盘）。
- 风险（不阻塞）：token 走 stdout，若部署脚本收集启动日志则会泄露——已在日志行内标注、与施工报告一致。

### 5. 限流绕过差异（实测）

计数对象是**服务进程级单一 `m_auth`**（`zrhi_d3d12_window.cpp:13413`），既不是 per-connection
也不是 per-IP：6 个不同源端口的独立连接把计数从 1/5 累到 5/5 并触发 `auth_locked` →
**重连不重置**（更严格，符合"按 IP/按全局则重连不重置"的预期）。反过来的代价是
**本机任何进程 5 次错 token 可把合法客户端一起锁 30s**（本机 DoS 面，fail-closed 方向，
属简报"认证失败要限流"的固有取舍，不判 FAIL）。换端口/重启进程会得到新 token 与新计数，
但那是攻击者无法做到的（需要控制进程启动）。

### 6. 认证失败是否泄露信息

统一回 `{"ok":false,"error":"auth_failed","detail":"invalid DevConsole token"}`，**不回显任何
token 片段/前缀匹配位数/长度差异**；仅暴露失败计数 `failures=n/5` 与锁定剩余 `remaining_ms`
（有利于防守方排查，不构成秘密泄露）。`auth_required` 的 detail 只说明协议格式。

### 7. 回归数字（自己构建后实测）

| 项 | 实测 |
|---|---|
| 原生单测 | `[doctest] test cases: 332 \| 332 passed \| 0 failed \| 0 skipped`，`assertions: 776924`，新增用例 8+7=15（`grep -c TEST_CASE` 核对） |
| `--draw` | `frames=60 center checks=6 passed=6`、`InfoQueue error count = 0`、`D3D12 FIRST DRAW OK`、exit 0 |
| DevConsole | `help`（5 条白名单）、`hud`（drawCalls=3/rt=640x480/fps≈90）、`rg.export json|text|嵌套`、`renderdoc.capture`（未挂接时 `renderdoc_not_attached`，不假成功）、`quit` exit=0 |
| `renderdoc.capture` 旁路 | `../escape`、`C:evil` → `bad_capture_name` + 日志；`sceneA` 行为不变（走到 `renderdoc_not_attached`） |
| 传输层修复 | 粘包：`auth <token>\nhelp\n` 一次 write → 两行响应均收到；RST：`auth <错>\nhelp\nquit\n` 一次 write → 仍收到 `auth_failed`（拒绝响应未被 RST 丢弃） |

### 8. 残余风险（不阻塞，建议后续）

1. **TOCTOU**：校验用 `weakly_canonical`，落盘用 `create_directories`+`ofstream`，两者之间可被
   同用户进程插入 junction（需对导出根有写权限，且时间窗需要抢）。
2. **导出根本身是重解析点**未被检查（只查根以下分量）；根由启动参数/环境决定，属受信输入。
3. 监听线程仍串行，单连接最长占 ~5s（原有设计，非本次引入）。
4. 限流为全局计数，存在本机 30s 自锁（见 §5）；如需抗本机 DoS 可改 per-peer 计数 + 独立线程。

### 9. 现场

- `git status`：仅本任务应有的 `Docs/Logs/TASK-X-SEC-01.md`、`Native/CMakeLists.txt`、
  `Native/tests/zrhi_d3d12_window.cpp` + 6 个新增源码/测试文件；无 commit（HEAD 仍 `64e05a4`）。
- 构建副作用（`build_native.ps1` 会把 `zrender.dll` 覆盖进 `UnityProject/Assets/Plugins/ZRender/`）
  已 `git checkout --` 还原，md5 回到 `430478def420d3970e036725c1d77665`（=HEAD）。
- 测试残留：本机 `%LOCALAPPDATA%\ZFrame\rg_export\audit_8f2.json` 已删；仓库内证据已归档到
  `TempLogs/archive_pre_audit/TASK-X-SEC-01-verify/`（`TempLogs/` 为 gitignore）。
- 进程/端口：`Get-Process` 无 `zrhi*`/`zrender*`/`Unity*` 残留；17391 无监听。
