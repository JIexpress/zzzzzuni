# TASK-X-HYGIENE-01 —— 缺口卫生清理（体积口径 / gpu_hud 语义 / PSO 两条）

> 缺口清单第 7、12、13 条 + 第 9 条的两个尾巴。都是小修，但会误导人，一并清掉。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 背景

验收和审计反复记到这几处"不致命但会误导"的问题。逐条修，每条给证据。

## 任务

### 1）构建报告体积口径（缺口 #7）

`Tools/build_player.ps1:60` `$BuildDir = Join-Path $RepoRoot 'Build'`，
`:359` 用它 `Get-ChildItem -Recurse` 统计，导致报告出的是**整个 `Build/`（385MB）**，
而实际 Player 只有 `Build/Windows64`（83MB）。加个安装包后 `Build/Installer` 又混进去。

修法：分别统计并分项打印
- `Player : Build\Windows64`（这是"玩家拿到的体积"）
- `Installer : Build\Installer\*.exe`（可无）
- `Other : Build 下其余`（BuildReport、中间产物等，可选）

**明确标注哪个是"可分发音量"**，别让人再猜。

### 2）gpu_hud 文档与实现不符（缺口 #12）

`Native/zrender/gpu_hud.h:33` 文档写 `ceil(w/2^m/blockW)`，
`gpu_hud.cpp:33` 实现是 `(width + blockW - 1u) / blockW`（对传入的 width 上取整）。

两者差在"mip 尺寸是 `floor(w/2^m)` 还是 `ceil(w/2^m)`"。**先判断哪个是对的**：
- 若调用方传的是**已右移的 mip 尺寸**（`w >> m`），则实现是 `ceil(floor(w/2^m)/blockW)`，
  文档应改成这个，并说明"以 floor 后的 mip 尺寸为基准"
- 若调用方传**原始 w + m**，则实现少了 `2^m`，是**真 bug**，要修实现

给出判断依据（调用点代码），然后让文档与实现对齐；如果是 bug 就修实现并补一条用例。

### 3）PSO 两条尾巴（缺口 #9 遗留）

验收原话："运行期重建路径先 `m_diskBlob.clear()` 再 `m_library.Reset()`，按 D3D12 契约属潜在 UAF；顺序应反过来。"
另有"断电持久化缺 `FlushFileBuffers`"。

- **UAF**：找到运行期重建那段（`zrhi_d3d12_pso_cache.cpp` 里 `m_diskBlob.clear()` 与 `m_library.Reset()` 相邻处），
  按 D3D12 契约调整顺序（库对象持有 blob 引用时不能先释放 blob）。给出你依据的契约条文/文档说明。
- **持久化**：写盘后补 `FlushFileBuffers`（或打开文件时加 `FILE_FLAG_WRITE_THROUGH`），
  说明代价（每次刷盘的开销）与是否只在"进程退出/显式刷写"时做（推荐后者，不要每帧刷）。

## 判据

- 构建报告能分别看到 Player / Installer 体积，且明确哪个是可分发音量
- gpu_hud 的文档与实现一致；若是 bug 修复，附一条用例
- PSO 顺序调整有契约依据；`FlushFileBuffers` 只在退出/显式刷写路径
- 回归：原生 317、`--draw` 60 帧、PSO 命中率不回退（跑两次看 hits）
- 不改游戏内容

## 环境纪律（真实踩过）

- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- **PATH 上的 cmake 是 MSYS2 版无写权限** → 必须走 `Tools/build_native.ps1`
- Unity 批处理**单实例**：查进程 + 删 `UnityProject/Temp/UnityLockfile`；**勿加 `-quit`**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不动游戏内容；`.cs/.cpp/.h/.ps1` 无 BOM、LF（.ps1 若被 git 转 CRLF 属正常）

---

## 施工报告（施工代理写）

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 构建报告分项体积，且标明可分发音量
- [ ] V2 gpu_hud 文档与实现一致；若修了 bug 有用例
- [ ] V3 PSO 顺序有契约依据；FlushFileBuffers 不在热路径
- [ ] V4 回归：317 / --draw 60 帧 / PSO 两次命中率
- [ ] V5 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
