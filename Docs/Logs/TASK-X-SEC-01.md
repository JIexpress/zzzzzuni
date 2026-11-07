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

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 无 token / 错 token 被拒（附日志），正确 token 可用
- [ ] V2 token 用常量时间比较
- [ ] V3 `rg.export` 路径穿越被拒（`..`、绝对路径、符号链接），正常路径可用
- [ ] V4 回归：317 / --draw 60 帧 / DevConsole 功能不退化
- [ ] V5 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
