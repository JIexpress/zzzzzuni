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

**结论**：三处都改完，均为小改，未改动任何游戏内容；原生 317 全绿、`--draw` 60 帧 PASS、
PSO 两次跑 hits 0→24 无回退。未 commit，未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`。

### 1）构建报告体积口径（`Tools/build_player.ps1`）

**旧口径**：`:359`（改前）`Get-ChildItem $BuildDir -Recurse` 统计整个 `Build/`，报告里只有
一行 `Files: 598 files, total 386.00 MB (404755052 bytes)`，Top-20 里第 1、3、4 全是
`Android\*.apk`、第 2/5 是 `DevConsoleProbe\*\UnityPlayer.dll` —— "Player 体积"从来没被单独量过。

**新口径**：三个互斥桶（互斥靠 FullName 集合差，不是靠路径前缀猜），只读一次全树：

| 桶 | 范围 | 现在的数（`Build/build_report.txt`） |
|---|---|---|
| Player | `Build\Windows64`（递归） | **81.92 MB**（85,903,797 B，166 files）**= 可分发音量** |
| Installer | `Build\Installer\*.exe`（**不递归**） | **23.97 MB**（25,133,483 B，1 file：`ZFrame-Setup-0.1.0.32.exe`）**= 可分发音量（压缩分发）** |
| Other | 全树 − Player − Installer | **385.99 MB**（404,744,261 B，597 files） |
| Total | 三者之和 | 491.89 MB（515,781,541 B，仅诊断口径） |

- 报告正文对每个桶都打印一行 `==> SHIPPABLE ... = DISTRIBUTION SIZE` / `==> NOT SHIPPED`，
  不再需要人猜哪个数是玩家体积；`Total` 明确标注 `diagnostic only, NOT a shipping size`。
- `Installer` 特意**不递归**：`Build\Installer\staging\` 是打包中间产物（一份完整 Player 副本），
  递归会把 82MB staging + 里面的 `ZFrame.exe` 算进"安装包"（第一版实现就踩了这个坑，
  量出来 3 files / 25.73 MB，已改）。
- Top-20 改为只列 Player 内文件（相对路径以 `Build\Windows64` 为根）；安装包单独小节列出；
  Other 只留 top-5 供诊断，避免再次用中间产物占满表格。
- 旁证：`du -sm Build/Windows64` = 83MB（按块取整），报告按文件实际字节 = 81.92MB。

验证：`Tools/build_player.ps1 -SmokeOnly` 跑通（不重编、不动 `version.json`），
控制台同时打印四行分项，`Build/build_report.txt` 内容见上表（该文件被 `.gitignore` 忽略）。

### 2）gpu_hud 文档与实现（判断：**文档错，实现对**）

**判断依据（调用点优先）**：

1. **签名里没有 mip 入参**。`gpu_hud.h:39` `uint64_t ZrEstimateImageBytes(const ZrImageDesc& desc)`
   —— 只吃 desc，既没有 mip 索引，也没有"已右移的尺寸"参数。所以简报里"调用方传已右移的
   尺寸"这个分支在本 API 上**不可能发生**，实现必然自己走 mip 链。
2. **全部调用点传的都是原始尺寸的完整 desc**：
   - `Native/tests/test_gpu_hud.cpp:29/36/44/52/61`（`rt`/`depth`/`mips`/`odd`/`shadowArray`，全是 `width=` 原始值）
   - `Native/tests/zrhi_d3d12_window.cpp:13400/13406/13408`（`swapchainImageDesc` / `offDesc` / `depthDesc`，`width = kWidth` 等原始值）
   没有一处传 `w >> m`。
3. **实现的 mip 规则就是 D3D12 的规则**：`gpu_hud.cpp` 循环里 `width = width > 1 ? width/2 : 1`，
   即 `max(1, floor(w/2^m))`；块向上取整只发生在 `blocksX = (width + blockW - 1)/blockW`。
   正确口径 = `ceil(max(1, floor(w/2^m)) / blockW)`，文档写的 `ceil(w/2^m/blockW)` 错在把
   块取整的 ceil 误写成了 mip 尺寸的 ceil。
4. **既有用例已把 floor 语义钉死**：`test_gpu_hud.cpp:46-52`，3×3 两级 mip 断言 `40`
   （`9*4 + 1*4`）。若按文档的 `ceil(3/2)=2`，结果应是 56 —— 该用例能通过，说明实现是 floor。

**改法（只改注释，实现零改动）**：
- `gpu_hud.h:33-38`：公式改为 `Σ_mip ceil(max(1, floor(w/2^m))/blockW) × ceil(max(1, floor(h/2^m))/blockH) × bytesPerBlock`，
  并写明"mip 尺寸先按 D3D12 规则 floor 减半（下限 1），再向上取整到块；不是 ceil(w/2^m)"、
  "本函数只吃 desc（无 mip 索引参数），mip 链由实现逐级 w/=2 走"。
- `gpu_hud.cpp:32-33`：注释写成同一公式并标 `**不是** ceil(w/2^m)，勿改回`。

因为不是实现 bug，**没有新增用例**；floor/ceil 的判别由既有 `3x3 → 40`（ceil 则为 56）承担。

### 3）PSO 两条尾巴（`Native/zrhi/d3d12/zrhi_d3d12_pso_cache.cpp`）

**（a）UAF：顺序反了，已改；契约依据**

Microsoft Learn `ID3D12Device1::CreatePipelineLibrary` 的 `pLibraryBlob` 参数原文：

> "The pointer provided as input to this method must remain valid for the lifetime of the object
> returned. For efficiency reasons, the data is not copied."

即库对象内部**只存这个裸指针**（不拷贝），blob 内存必须活到库对象释放之后。本文件的
`m_diskBlob`（`pso_cache.h:100` 注释"反序列化用的磁盘 blob 保活"）就是这条契约的落点。

原代码把释放顺序写反了：`DeleteCacheFile()` 第一行就是 `m_diskBlob.clear()`，而运行期重建
路径是

```
DeleteCacheFile();          // <- 先把 blob 释放
m_names.clear(); ...
CreateEmptyLibrary();       // <- 内部才 m_library.Reset()
```

库对象此时仍活着并持有已被释放的 blob → 之后驱动任何一次惰性访问都是 UAF（`StorePipeline`
失败降级路径 `DeleteCacheFile(); m_library.Reset();` 同理）。

改法：
- `DeleteCacheFile()`（`:470`）**不再碰 `m_diskBlob`**，只删文件（注释写明为什么）。
- 三处调用点统一为 `m_library.Reset()` → `m_diskBlob.clear()` → `DeleteCacheFile()`：
  `:340` 初始化时 CreatePipelineLibrary 失败、`:679` 运行期 known-entry 加载失败重建、
  `:719` StorePipeline 失败降级。

**（b）断电持久化：已补 `FlushFileBuffers`，不在热路径**

- 原写盘链：`WriteAll` → `CloseHandle` → `MoveFileEx(REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)`。
  `MOVEFILE_WRITE_THROUGH` 只保证**改名这一步**的元数据落盘，`WriteFile` 写进去的数据块还
  可能停在系统文件缓存里 —— 掉电可能留下 0 字节/半截文件（下次启动会因校验和失败删掉重建，
  不崩，但缓存白丢）。
- 改：`WriteAll` 成功后、`CloseHandle` 之前 `FlushFileBuffers(file)`（`:541`）。失败只打
  warning，不改变写盘结论——缓存是纯优化，刷盘失败不该影响退出（与本文件"缓存问题绝不影响
  启动"的既有原则一致）。
- **代价与位置**：`FlushFileBuffers` 阻塞到存储栈确认，毫秒级、随 blob 大小线性增长。
  唯一调用链是 `Flush()` ← `~ZrD3D12Device`（设备析构）/ Unity `ReleaseTarget` /
  显式 `FlushPsoCache`；`CreateGraphicsPipelineState` 只置 `m_dirty`，**从不写盘**，
  所以每帧路径上没有这次 syscall。第二次运行 `Flush()` 时 `m_dirty=false`，连写盘都不发生
  （见下方 RUN2 的 `serialize_bytes` 未变、无 write 日志）。

### 4）回归（全部实测）

| 项 | 命令 | 结果 |
|---|---|---|
| 原生构建 + 单测 | `Tools/build_native.ps1` | ALL GREEN，`test cases: 317 \| 317 passed`，`assertions: 776740 passed` |
| `--draw` | `zrhi_d3d12_window.exe --draw` | `frames=60 center checks=6 passed=6 undeclared uses=0`，`InfoQueue error count = 0`，`D3D12 FIRST DRAW OK` |
| PSO 第一次（冷） | `ZFRAME_PSO_CACHE=%TEMP%\pso_hygiene01.bin ... --psocache 20261107` | `loaded=0 hits=0 misses=24 fallbacks=0`，`create_total_ms=11.709`，`serialize_bytes=8264`，落盘 8528 B |
| PSO 第二次（热） | 同上，不删缓存 | `loaded=1 hits=24 misses=0`，`create_total_ms=4.509`，`compile_ms=0.000 store_ms=0.000` |

→ hits 由 0 升到 24（24/24 全命中），**无回退**；缓存文件经 `FlushFileBuffers` + 原子替换后
可被下一次进程完整反序列化（`names=24 blob_bytes=8264`）。

**现场与文件卫生**
- `.cpp/.h` 无 BOM、LF（`git ls-files --eol` 四个文件均 `w/lf`）；`build_player.ps1` 保持
  `w/crlf`、无 BOM，且**新增文本全为 ASCII**（PS 5.1 读无 BOM 的 UTF-8 会把中文按 ANSI 解码成
  乱码，第一版加了中文注释直接把脚本解析搞崩，已全部改英文）。
- `git status` 仅 5 个源码/脚本改动 + `UnityProject/Assets/Plugins/ZRender/zrender.dll`
  （后者是 `build_native.ps1` 第 4 步"部署到 Unity 插件"的固定产物，非手工改动）。
- 未 commit；未创建仓库内临时文件（PSO 缓存写在 `%TEMP%`，报告写在被忽略的 `Build/`）。

---

## 验收清单（供验收代理）

- [ ] V1 构建报告分项体积，且标明可分发音量
- [ ] V2 gpu_hud 文档与实现一致；若修了 bug 有用例
- [ ] V3 PSO 顺序有契约依据；FlushFileBuffers 不在热路径
- [ ] V4 回归：317 / --draw 60 帧 / PSO 两次命中率
- [ ] V5 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

**验收代理**：独立代理（非施工方），全部条目本机复跑，未采信施工自述。日期 2026-11-07。
**裁决：PASS with 受限**（V1 PASS / V2 PASS / V3 PASS with 受限 / V4 PASS / V5 PASS）

### V1 体积口径 — PASS

自跑 `Tools/build_player.ps1 -SmokeOnly`（控制台首行 `[smoke-only] existing build (no Unity launch)`，
未重编、未动 version.json；smoke PASS），`Build/build_report.txt` 复现：

| 桶 | 字节 | 文件数 |
|---|---|---|
| Player（`Build\Windows64` 递归） | 85,903,797 B = 81.92 MB | 166 |
| Installer（`Build\Installer\*.exe` 非递归） | 25,133,483 B = 23.97 MB | 1 |
| Other | 404,744,155 B = 385.99 MB | 597 |
| Total | 515,781,435 B = 491.89 MB | — |

- **独立交叉核对（不经脚本）**：`Get-ChildItem Build\Windows64 -Recurse -File` → 166 files /
  85,903,797 B，与报告 Player 行逐字节相同（`du -sm` = 83 MB 为块取整）⇒ **81.92 MB 确实只含
  Windows64**，不含 Android APK / DevConsoleProbe。
- Installer 递归统计 = 166 files / 111,020,631 B，非递归 = 1 file / 25,133,483 B ⇒ staging
  （165 个打包中间产物）确实被排除在"安装包"之外，"不递归"的实现有效。
- 互斥性：166 + 1 + 597 = 764 = 全树文件数；三者字节和 = 全树字节。（Other/Total 相对施工报告
  各 +23 B，Player/Installer 逐字节不变；Other 含报告自身，该项自指，未深究。）
- 标注：Player 行 `==> SHIPPABLE PAYLOAD = DISTRIBUTION SIZE`、Installer 行
  `==> SHIPPABLE = DISTRIBUTION SIZE`、Other 行 `==> NOT SHIPPED`、Total `diagnostic only,
  NOT a shipping size`。判据满足。

### V2 gpu_hud — PASS（我的结论：文档错、实现对；但施工报告的支撑算术 56 是错的）

- **签名无 mip 参数**：`gpu_hud.h:39 uint64_t ZrEstimateImageBytes(const ZrImageDesc& desc)` ——
  既无 mip 索引也无"已右移尺寸"，简报里"调用方传 `w>>m`"的分支在本 API 上不可能成立。
- **调用点全传原始尺寸**：全仓只有 6 处（test 5 + window 3 处调用），`zrhi_d3d12_window.cpp:13400
  /13406/13408` 分别传 `swapchainImageDesc`、`offDesc`(kWidth×kHeight)、`depthDesc`(kWidth×kHeight)，
  `mipLevels` 全为默认 1；**无一处传右移后的尺寸**。
- **实现 = D3D12 mip 规则**：`gpu_hud.cpp:31-38` 每级 `width = width > 1 ? width/2 : 1`（floor，
  下限 1），块数 `(width + blockW - 1)/blockW`（ceil）⇒ `ceil(max(1,floor(w/2^m))/blockW)`，正确。
- **3×3 两级手算**：floor → `36 + 1*1*4 = 40`（与 `test_gpu_hud.cpp:52` 断言一致，钉死 floor）；
  若按 `ceil(3/2)=2` → `36 + 2*2*4 = **52**`，**不是施工写的 56**。施工此处算术错误，但"40≠52
  仍能证伪 ceil 口径"的论证成立，结论不受影响。
- **改动性质**：`git diff` 显示两文件仅注释（.cpp 1 行注释、.h 6 行注释），实现零改动 ⇒ 与
  "文档错、实现对"自洽；因不是 bug，不新增用例可接受。
- 结论：**文档错、实现对**，与施工一致。需更正的是报告里 56 → 52（否则等于用错算术证对结论）。

### V3 PSO — PASS with 受限

- **三处顺序（本机逐行复读）**：`:340-342`（Initialize 磁盘库失败）、`:679-681`（运行期
  known-entry 加载失败重建）、`:719-721`（StorePipeline 失败降级）——**全部** `m_library.Reset()`
  → `m_diskBlob.clear()` → `DeleteCacheFile()`。
- `DeleteCacheFile()`（:470-487）现只做 `DeleteFileW`，**已不再碰 `m_diskBlob`**（原首行
  `m_diskBlob.clear()` 被删，diff 可证）。delta 前该路径确为"先 clear blob、库仍存活"。
- **契约依据我独立抓取**：Microsoft Learn `ID3D12Device1::CreatePipelineLibrary` 原文 ——
  "If the input library blob is not empty, then it is validated for integrity, parsed, **and the
  pointer is stored**. The pointer provided as input to this method must remain valid for the
  lifetime of the object returned. For efficiency reasons, the data is not copied."
  （施工只引后半句；前半句"pointer is stored"更直接印证 UAF 风险。）
- **FlushFileBuffers 位置**：`:541`，在 `WriteAll(header/names/blob)`（:528-534）之后、
  `CloseHandle(file)`（:547）之前；`if (ok && !FlushFileBuffers(file))` 仅 `fprintf` warning，
  不改 `ok`、不影响返回值 ⇒ "失败只 warning" 属实。
- **不在热路径**：`WriteCacheFile` 唯一调用者是 `Flush()`（:758）；`Flush()` 调用者仅
  `~ZrD3D12Device`、`ZrD3D12Device::FlushPsoCache`（Unity `ReleaseTarget`，unity_target.cpp:1133）、
  测试显式刷写；`CreateGraphicsPipelineState` 只置 `m_dirty`。实测第二次运行 `m_dirty=false`，
  缓存文件 mtime 未变（连写盘都没发生）。
- **缓存未失效/未回退**：RUN1 `serialize_bytes=8264`、落盘 8528 B；RUN2 `loaded=1 names=24
  blob_bytes=8264 hits=24`，与施工报告数字一致。
- **受限项（残余风险，不属本任务清单，但同属该契约）**：`pso_cache.h:99-100` 成员声明为
  `m_library` 在前、`m_diskBlob` 在后；C++ 析构按声明逆序 ⇒ `m_diskBlob` 先释放、`m_library`
  后释放，对象销毁时仍有"库对象晚于 blob 死亡"的窗口（析构前已 Flush，进程退出期影响面小）。
  建议后续把 `m_diskBlob` 声明挪到 `m_library` 之前（或加显式析构先 Reset）。**非本次三处调用点
  的回退，不据此判 FAIL。**

### V4 回归 — PASS

| 项 | 复跑命令 | 结果 |
|---|---|---|
| 原生 | `Tools/build_native.ps1` → ALL GREEN；再直接跑 `zrender_test.exe` | `test cases: 317 \| 317 passed`、`assertions: 776740` 全过，exit 0 |
| `--draw` | `zrhi_d3d12_window.exe --draw` | `frames=60 center checks=6 passed=6 undeclared uses=0`、`InfoQueue error count = 0`、`D3D12 FIRST DRAW OK`、device removed reason `S_OK` |
| PSO 冷 | 删缓存后 `--psocache 20261107` | `hits=0 misses=24 fallbacks=0 create_total_ms=6.120 serialize_bytes=8264`，落盘 8528 B |
| PSO 热 | 同一缓存再跑 | `loaded=1 hits=24 misses=0`、`compile_ms=0.000 store_ms=0.000`，文件 mtime 未变 |

退出码备注：首轮后台调用写成 `build_native.ps1 2>&1 | Select-Object`，包装进程报 exit 1（PS 5.1 原生 exe
带 `2>&1` 的已知假失败，工具文档亦注明）；去掉 `2>&1` 重跑 `$LASTEXITCODE = 0`，输出末行
`ALL GREEN: configure + build + test succeeded`、317 全过 ⇒ **脚本本身无失败路径**，非回归问题。

→ hits **0 → 24**，无回退；写盘经 `FlushFileBuffers` + 原子替换后可被下一进程完整反序列化
（`names=24 blob_bytes=8264`），持久化改动未损伤缓存有效性。

### V5 现场 — PASS

- `git status --porcelain`：7 个 `M`（本日志、gpu_hud.cpp/.h、pso_cache.cpp/.h、
  Tools/build_player.ps1、UnityProject/Assets/Plugins/ZRender/zrender.dll），**无 `??` 未跟踪**；
  HEAD 仍为 `7e4987e chore: 派发 X-HYGIENE-01` ⇒ **未 commit**。
- **未改** TASKS.md / HANDOFF.md / WORKLOG.md / README.md（不在改动列表）。
- 游戏内容未改：`UnityProject/Assets` 下唯一改动是 `zrender.dll`（build_native.ps1 第 4 步固定
  部署产物，施工已声明；本次复跑也会重新生成，非人工修改）。
- 换行/编码：gpu_hud.h/.cpp、pso_cache.cpp 均 `w/lf`；build_player.ps1 `w/crlf`（符合
  .gitattributes），其新增行全 ASCII（对 diff 做非 ASCII 过滤，无输出）；`/Build/` 已被忽略。
- 我的临时物：PSO 缓存写在 `%TEMP%\pso_audit_hygiene01.bin`（仓库外），复跑完已 `rm`；仓库内
  未新增任何文件，TempLogs 未新增目录。

### 最终裁决

**PASS with 受限。** 三条承诺全部本机复现：体积三桶 81.92 / 23.97 / 385.99 且 81.92 只含
Windows64；gpu_hud 判断"文档错、实现对"成立（签名无 mip、调用点传原始尺寸、用例 40 钉死 floor，
ceil 应为 52）；PSO 三处调用点顺序全为 Reset → clear → DeleteFile，`DeleteCacheFile` 不再碰 `m_diskBlob`，
`FlushFileBuffers` 在 WriteAll 后 / CloseHandle 前且不在热路径；317 / 60 帧 / hits 0→24 全绿。

两条非阻断记录：
1. 施工报告 V2 支撑算术 `56` 应更正为 `52`（结论不变，但别用错算术证对）。
2. `pso_cache.h` 成员声明顺序使析构期仍"blob 先于库"释放（残余契约违约，建议后续小修）。
