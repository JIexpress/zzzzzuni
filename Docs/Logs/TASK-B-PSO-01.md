# TASK-B-PSO-01 —— PSO 磁盘缓存（消除首次加载/切场景的着色器编译卡顿）

> 缺口清单第 9 条。施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 现状（已核实）

- PSO 创建：`zrhi_d3d12_device.cpp:905` `CreatePipelineState(desc)`，每进程现编
- **全仓库没有任何缓存机制**（grep `cachedblob`/`pipelibrary`/`.pso` 无结果）
- 后果：每次启动、每次新 PSO 组合都要驱动现编 → 首次加载/切场景卡顿

## 目标

首次运行把 PSO 编译结果落盘，后续运行直接加载，**跳过重复编译**。

## 方案（用 Windows 官方机制，别自己发明序列化）

`ID3D12Device1::CreatePipelineLibrary` —— 官方 PSO 库，可 `Serialize()`/反序列化：

1. 设备创建后，尝试从磁盘加载 `pso_cache.bin` → `CreatePipelineLibrary(blob, size)`
2. `CreatePipelineState` 走 `library->CreateGraphicsPipelineState(desc, name)`（name 用 desc 的稳定哈希）
3. 未命中或库不可用 → 回退到 `ID3D12Device::CreateGraphicsPipelineState`（**必须有这条兜底**）
4. 进程退出/显式刷写时 `library->Serialize()` → 原子写盘（写临时→替换，同 SaveSystem 的做法）
5. 库损坏/版本不匹配/驱动换了 → 捕获失败 → **删掉缓存文件重建**，不能因此崩溃

## 硬性要求

- **缓存路径**：默认 exe 同目录或 `%LOCALAPPDATA%\ZFrame\pso_cache.bin`；用环境变量可覆盖（便于测试）
- **失效判定**：desc 哈希要覆盖 shader 字节码 + 所有状态位；哈希算法变了要能识别（加版本号前缀）
- **兜底优先**：任何缓存路径失败都必须退回"直接创建"，**不许因缓存问题起不来**
- **可观测**：日志打出「命中 N / 未命中 M / 加载耗时 X ms / 序列化字节数」
- 多进程并发写同一缓存文件要安全（写临时+原子替换，或加锁）

## 判据

- 连续启动两次：第二次的 PSO 创建总耗时**明显下降**（给出两次的数字，粗略即可）
- 删除缓存文件后能正常启动（不崩）
- **故意写坏缓存文件**（截断/改字节）→ 能识别并重建，不崩
- 回归：原生 317、`--draw` 60 帧、`-zrender` 画面正常
- 多进程同时启动不自相残杀（至少说明策略）

## 环境纪律（真实踩过）

- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- **PATH 上的 cmake 是 MSYS2 版**，对某些目录无写权限 → 必须走 `Tools/build_native.ps1`（会回退 VS cmake）
- Unity 批处理**单实例**：查进程 + 删 `UnityProject/Temp/UnityLockfile`；**勿加 `-quit`**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不动游戏内容、不动 Governor 三件套；`.cs/.cpp/.h` 无 BOM、LF
- `ID3D12PipelineLibrary` 在部分系统可能不支持 → **必须探测能力**，不支持就静默走无缓存路径

---

## 施工报告（施工代理写）

> 施工代理：TASK-B-PSO-01 施工（未 commit、未改看板）。全部证据日志在
> `TempLogs/TASK-B-PSO-01/`（gitignore 目录）。

### 1. 怎么接的 library（官方机制，未自造序列化）

新增 `Native/zrhi/d3d12/zrhi_d3d12_pso_cache.h/.cpp`（类 `ZrD3D12PsoCache`），
由 `ZrD3D12Device` 持有（`zrhi_d3d12_device.h:381`）：

| 时机 | 动作 |
|---|---|
| `InitializeRuntime` 末尾（`zrhi_d3d12_device.cpp:242`） | 探测 `ID3D12Device1` → 读盘 → `CreatePipelineLibrary(blob)`；失败/缺失 → `CreatePipelineLibrary(nullptr, 0)` 空库；空库也失败 → 静默禁用 |
| `CreatePipelineState`（`zrhi_d3d12_device.cpp:978`） | 先 `library->LoadGraphicsPipeline(name, desc, …)`；未命中/失败 → **`ID3D12Device::CreateGraphicsPipelineState`（兜底）** → `library->StorePipeline(name, pso)` 入册 |
| 析构（`zrhi_d3d12_device.cpp:264`）/ `FlushPsoCache()`（Unity 显式刷写） | `GetSerializedSize` + `Serialize` → 头 + name 表 + blob 写入 `.tmp.<pid>.<tick>` → `MoveFileEx(REPLACE_EXISTING\|WRITE_THROUGH)`（原子替换） |

- 路径：`ZFRAME_PSO_CACHE` 环境变量优先（`0/off/disable` = 关闭，便于基线测量）；
  未设置时 exe 同目录可写就用 `pso_cache.bin`，否则
  `%LOCALAPPDATA%\ZFrame\pso_cache.bin`（Unity 播放器/编辑器走后者或 exe 目录，均可）。
- **踩坑并修正**：`CreatePipelineLibrary` 的输入 blob 在库生命周期内不能释放
  （首版第二进程在首次 `LoadGraphicsPipeline` 直接 AV 崩溃）。现把读盘 blob 保活到
  `m_diskBlob`，崩溃消失（见下方“坏缓存测试”与两次启动数字）。
- **Unity 侧补漏**：Unity 的设备包装按设计不在 `ReleaseTarget` 销毁（DLL 卸载才回收），
  析构刷写不会发生。已在 `zrhi_d3d12_unity_target.cpp:1129` 的 `ReleaseTarget()`
  显式调 `FlushPsoCache()`（幂等，无新条目不写盘）。实测播放器优雅退出写出 10452B，
  第二次启动文件 mtime/md5 不变（全命中、未重写）。

### 2. 哈希怎么算

`HashPipelineDesc`（`zrhi_d3d12_pso_cache.cpp:544`）：FNV-1a 64（offset basis
14695981039346656037），**版本前缀** `"ZPSO-HASH-v1|translate-v1|rootsig-v1"`
（改哈希内容/根签名布局/translate 固定状态时必须提升，旧缓存自然失效）。覆盖：

- 根签名布局计数 `rootCbvCount` / `srvTableCount`（D3D12 desc 里只有指针，不能哈希身份）；
- VS/PS/DS/HS/GS 全部字节码（长度前缀 + 原始字节）；
- desc 全状态位：NodeMask/Flags、BlendState（按位）、SampleMask、RasterizerState（按位）、
  DepthStencilState（按位）、PrimitiveTopologyType、NumRenderTargets、8×RTVFormats、
  DSVFormat、SampleDesc、IBStripCutValue、StreamOutput 计数、CachedPSO 内容；
- 输入布局：元素数 + 逐元素（语义字符串、SemanticIndex、Format、InputSlot、
  AlignedByteOffset、InputSlotClass、InstanceDataStepRate）。

名字 = 哈希的 16 位小写十六进制宽字符串（`l"a1b2…"`），psName 唯一且跨进程稳定。

### 3. 损坏怎么识别

缓存文件是「72B 自描述头 + uint64 name 表 + D3D12 库 blob」：magic `PSOCA001`、
格式版本、头尺寸、blob 尺寸、blob FNV-1a、name 数量、name 表 FNV-1a、
GPU 指纹（vendorId/deviceId/subSysId/revision）。读盘后逐项校验；**任一失败都
打印原因 → 删文件 → 空库重建 → 继续启动**（`ReadCacheFile` `:389`）：

| 注入 | 日志 | 结果 |
|---|---|---|
| 截断到 100B | `corrupt: size fields inconsistent` | 重建，exit 0 |
| 翻转 blob 中 1 字节（偏移 5000） | `corrupt: blob checksum mismatch` | 重建，exit 0 |
| 9000B 随机垃圾 | `corrupt: bad magic` | 重建，exit 0 |
| 头版本 1→2 | `corrupt: format version mismatch` | 重建，exit 0 |
| 翻转 name 表 1 字节 | `corrupt: name table checksum mismatch` | 重建，exit 0 |
| 改 vendorId（模拟换卡） | `stale: GPU adapter changed` | 重建，exit 0 |
| 删缓存 | `missing (first run)` | 重建，exit 0 |

另有两条运行期防线：①`CreatePipelineLibrary(blob)` 被驱动拒绝（驱动版本变化/内容
不兼容）→ 删盘+空库；②索引声称存在、`LoadGraphicsPipeline` 却失败 → 删盘+空库重建一次
（`m_rebuilt` 防死循环）。上面 7 组每组都复跑了一次，重建后的文件下一次启动
`loaded=1 hits=24`，证明重建文件有效。

### 4. 兜底怎么走（优先级：能起来 > 有缓存）

1. `ID3D12Device1` 不支持 / 环境变量禁用 / 空库创建失败 → `supported=0`，
   **全部直连 `ID3D12Device::CreateGraphicsPipelineState`**（日志
   `pipeline library unavailable: using direct PSO creation (no cache)`；
   `ZFRAME_PSO_CACHE=off` 实测 `direct=24`、exit 0）；
2. 库可用但本次未命中 → 直接创建（编译）→ `StorePipeline` 入册；
3. `StorePipeline` 失败 → 本进程禁用库、删半残缓存，余下全部直接创建；
4. 上述任何一步失败都不改 `InitializeRuntime` 返回值——**缓存问题不会让设备起不来**。

### 5. 两次启动数字（可复现命令）

基准模式 `--psocache [salt]`（`Native/tests/zrhi_d3d12_window.cpp:13783`）：用同一对
着色器建 24 个互不相同的 PSO。salt 扰动像素着色器常量，产生**驱动从未编译过**的
DXBC（`1e-7*(salt+1)`），用于复现“首次启动冷编译”；同一 salt 的第二次启动走本库。

```bash
# 第一次（无缓存）
rm -f /c/.../zpso_final.bin
ZFRAME_PSO_CACHE=C:/.../zpso_final.bin zrhi_d3d12_window.exe --psocache 20261112
# 第二次（有缓存，同一 salt）
ZFRAME_PSO_CACHE=C:/.../zpso_final.bin zrhi_d3d12_window.exe --psocache 20261112
```

| 运行 | PSO 创建总耗时 | 命中/未命中 | 其中编译 / 入库 | 加载耗时 | 序列化字节 |
|---|---|---|---|---|---|
| 第 1 次 | **67.257 ms** | 0 / 24 | 65.958 / 0.266 ms | 3.094 ms | 8264 B |
| 第 2 次 | **3.449 ms** | 24 / 0 | 0 / 0 ms | 3.195 ms | 8264 B（未重写） |

→ **下降 94.9%**，命中率 24/24 = 100%。同一机器冷驱动首次实测过 296.6ms（同一
24 PSO 负载，salt 不同），第 2 次 4.3ms。真实场景交叉验证：

| 场景 | 第 1 次 | 第 2 次 | 命中率 |
|---|---|---|---|
| `--r5`（9 个 PBR/Toon/阴影/SSR PSO） | 12.254 ms | 1.977 ms（−83.9%） | 9/9 |
| `--draw`（1 个 PSO，60 帧） | 0.575 ms | 0.380 ms（−33.9%） | 1/1 |

说明：小着色器直连创建的绝对耗时本就只有 0.1~0.5ms，缓存收益随编译成本增长；本次
数字来自强制冷着色器（salt）与真实 R5 负载，不拿“已预热驱动缓存”的对照组冒充。

### 6. 多进程并发

写盘 = 唯一临时名（pid + GetTickCount64）+ `MoveFileEx(REPLACE_EXISTING|
WRITE_THROUGH)`，读盘共享标志 `READ|WRITE|DELETE`；读者只会看到完整旧文件或完整
新文件（最后写入者胜）。实测 4 进程同时起、同一路径同一负载：2 进程 `loaded=1
hits=24`（读到别人刚写的），2 进程 `misses=24` 各自写；全部 exit 0，事后文件
8528B 有效，第 5 次启动 `hits=24`。

### 7. 回归（本机实跑）

| 项 | 结果 |
|---|---|
| 原生单测 | **317 / 317 passed**，776,740 断言 0 失败（`build_native.ps1` 全绿） |
| `--draw` | 两次均 60/60 帧、中心像素 6/6 判 CUBE、`D3D12 FIRST DRAW OK`、InfoQueue 0 错误 |
| `--r5` | 两次均 `D3D12 R5 OK (ssr=134 gi=23 toon=141)`、设备移除 0 |
| `-zrender` 播放器 | 986×611 截图与 B-UNITY-04 参考 `audit_zrender.png`：**平均像素差 0.285/255、仅 0.289% 像素 >24**（参考任务自身为 0.49/255、0.547%）；Player.log 13 条 RenderFrame 至 1440+ 帧、≈90fps、中心像素 (54,54,54)，无撕裂/上下颠倒/花屏 |

`-zrender` 做法：把本次 `zrender.dll` 临时替换进 `Build/Windows64/ZFrame_Data/Plugins/
x86_64/`（原 dll 已备份并还原，md5 8ba4cee3…），GUI 播放器（**未加 -force-d3d12**）
点开始 → 采图 → 优雅退出。

### 8. 现场

- 未 git commit（HEAD 仍 `296d970`）；未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`；
  未动游戏内容/Governor 三件套；无 `URP_*.asset`/`QualitySettings.asset` 改写。
- `git status`：5 改（CMakeLists、device.h/.cpp、unity_target.cpp、window.cpp）+
  2 新增（pso_cache.h/.cpp）+ `UnityProject/Assets/Plugins/ZRender/zrender.dll`
  （构建产物，本任务新代码就在其中）。
- 新增/修改的 `.cpp/.h` 全部 **无 BOM、CR=0（LF）**。
- 临时证据在 `TempLogs/TASK-B-PSO-01/`（gitignore）；播放器构建目录已还原原 dll。

### 9. 已知边界（如实）

1. 库 blob 必须保活（已修）；若未来再次出现第二进程 AV，先查这里。
2. Unity 包装进程被强杀（非 WM_CLOSE）时最后一次 `ReleaseTarget` 前的入库会丢，
   下次启动重编这几个 PSO；优雅退出路径已实测落盘。
3. 缓存与 GPU 指纹/驱动绑定；`kHashVersion` 需在根签名布局或 translate 固定状态
   变化时手动提升（代码内已注明）。
4. 空库探测依赖驱动接受 `CreatePipelineLibrary(nullptr, 0)`；不接受的驱动走
   “库不可用 → 直连”路径（已留日志与代码分支）。


---

## 验收清单（供验收代理）

- [ ] V1 第二次启动 PSO 创建耗时明显下降（两次数字）
- [ ] V2 删缓存后能启动
- [ ] V3 **故意写坏缓存**能识别并重建，不崩
- [ ] V4 无 `ID3D12PipelineLibrary` 支持时静默回退（有探测证据或代码路径）
- [ ] V5 回归：317 / --draw 60 帧 / -zrender 正常
- [ ] V6 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

> 验收代理：TASK-B-PSO-01 独立验收（与施工非同一代理）。**未 commit、未改看板**。
> 方法：自建 Release（`Tools/build_native.ps1`，VS cmake 回退路径）→ 用自己指定的
> `ZFRAME_PSO_CACHE` 临时路径复跑；全部原始日志/截图/被破坏的缓存样本在
> `TempLogs/verify_pso/`（gitignore）。未采信施工报告的任何数字。

### 结论表

| 项 | 结论 | 一行证据 |
|---|---|---|
| V1 二次启动耗时下降 | **PASS** | 新 salt 冷启动 `76.738 ms / 0 命中` → 第二次 `2.924 ms / 24 命中`（−96.2%）；第 3、4 次仍 24 命中、exit 0、文件 md5 不变 |
| V2 删缓存后启动 | **PASS** | 删默认 `pso_cache.bin` → `missing (first run)`、0/24、64.070 ms、exit 0；第二次 24 命中、5.343 ms，文件重建 8528 B |
| V3 坏缓存识别重建 | **PASS** | 截断→`size fields inconsistent`、改字节→`blob checksum mismatch`、随机垃圾→`bad magic`；均 exit 0、删文件重建、下次 `loaded=1 hits=24` |
| V4 无库时兜底 | **PASS** | `ZFRAME_PSO_CACHE=off` → `direct=24 support=0`、exit 0；传入目录当缓存路径 → `write failed (err=5)` 仍 exit 0；代码路径 `QueryInterface(ID3D12Device1)` 失败即整条直连 |
| V5 回归 | **PASS** | 原生 **317/317、776740 断言**；`--draw` 两次 60/60 帧、6/6 中心校验、InfoQueue 0；`--r5` 两次 OK；`-zrender` 场景截图与参考平均差 1.06/255、仅 1.55% 像素 >24 |
| V6 现场 | **PASS** | HEAD 仍 `296d970`；仅 7 改 + 2 新增源文件；TASKS/HANDOFF/WORKLOG/README 无改动；无 BOM、CR=0；播放器 dll 已还原 `8ba4cee3…`；无残留进程 |

### V1 实测（自己跑，非施工数字）

```
# salt=987654，全新缓存文件，连续 4 个进程
RUN1 exit=0  created=24 create_total_ms=76.738 hits=0  misses=24 loaded=0 load_ms=3.900
RUN2 exit=0  created=24 create_total_ms=2.924  hits=24 misses=0  loaded=1 load_ms=4.226
RUN3 exit=0  created=24 create_total_ms=2.971  hits=24 misses=0  loaded=1 load_ms=4.215
RUN4 exit=0  created=24 create_total_ms=5.389  hits=24 misses=0  loaded=1 load_ms=4.730
```
- 缓存文件四次均为 8528 B、md5 `31E4CB83…` 不变 → 纯命中不重写盘。
- **专门验“第二进程 AV”坑**：上述第 2/3/4 次连续磁盘加载不崩；验收期间共 17 次
  “读盘 + LoadGraphicsPipeline”进程全部 exit 0，未复现 AV。`m_diskBlob` 保活有效。
- 诚实补充：若 salt 不变且驱动已预热（施工用过的 20261112），实测为
  `3.586 ms → 3.583 ms`——命中 24/24 但时间不降，因为驱动自身已缓存该 DXBC。
  **收益只在“驱动没见过该字节码”时成立**；冷字节码下数字可复现（76.7→2.9 ms）。
  施工的 67.257 ms 与本次 76.738 ms 同量级，可采信。

### V2 实测（默认路径，未设环境变量）

```
RUN1 exit=0 cache file: missing (first run)
     created=24 create_total_ms=64.070 hits=0 misses=24 loaded=0
RUN2 exit=0 loaded=1 names=24
     created=24 create_total_ms=5.343 hits=24 misses=0
```
删后自动重建 8528 B；默认落点确为 exe 同目录（`Native/build/bin/Release/pso_cache.bin`）。

### V3 实测（自己注入，日志原文）

1) 截断一半（8528→4264 B）：
```
[zrhi][d3d12][pso-cache] cache file: corrupt: size fields inconsistent -> delete & rebuild
[zrhi][d3d12][pso-cache] flush: hits=0 misses=24 ... serialize_bytes=8264
# 下次：loaded=1 names=24 -> hits=24 misses=0
```
2) 中间翻 6 字节（偏移 3000..3005，blob 区）：
```
[zrhi][d3d12][pso-cache] cache file: corrupt: blob checksum mismatch -> delete & rebuild
# exit 0；下次 loaded=1 names=24 -> hits=24
```
3) 写 9000 字节随机垃圾：
```
[zrhi][d3d12][pso-cache] cache file: corrupt: bad magic -> delete & rebuild
# exit 0；下次 loaded=1 names=24 -> hits=24
```
额外 2 组破坏（施工声称的另两类）也复核通过：
```
formatVersion 1->2 : corrupt: format version mismatch -> delete & rebuild  (下次 hits=24)
vendorId 改 0x1234 : stale: GPU adapter changed -> delete & rebuild         (下次 hits=24)
```
另做 2 组“更强”的一致性破坏（校验和全部正确，仅语义错配）：
- 改 name 表首个条目并重算 nameHash → 不崩；命中按 desc 真实名字判定，
  第 2 次 `names=25`（索引漂移被自动补登），无错误命中。
- 把 A 缓存的 name 表拼到 B 缓存的 blob 上 → 精确触发运行期重建分支：
  ```
  [zrhi][d3d12][pso-cache] known entry failed to load hr=0x80070057:
  dropping cache and rebuilding empty library
  ```
  exit 0、24 次直连、重建后下次 `hits=24`。**证明“库能开但条目失效”也有自愈路径。**

### V4 实测

```
ZFRAME_PSO_CACHE=off:
  [pso-cache] disabled by ZFRAME_PSO_CACHE=off
  flush: hits=0 misses=0 direct=24 support=0   exit=0
ZFRAME_PSO_CACHE=<一个目录>:
  cache file: missing (first run) → 24 misses → write failed (err=5) → exit=0
```
代码路径核对：`Initialize` 先 `QueryInterface(ID3D12Device1)`，失败打印
`ID3D12Device1 unavailable ... no-cache path` 并返回 false；此后
`CreateGraphicsPipelineState` 走 `m_library == nullptr` 分支直连设备。空库创建失败
同样落到该分支；**任何失败都不影响 `InitializeRuntime` 返回值**（纯优化）。

### V5 回归（本机实跑）

| 项 | 结果 |
|---|---|
| 原生单测 | **317 / 317，776740 断言 0 失败**（`build_native.ps1` ALL GREEN） |
| `--draw` ×2 | `frames=60 center checks=6 passed=6`、`InfoQueue error count = 0`、`D3D12 FIRST DRAW OK`；退出码 0 |
| `--r5` ×2 | `D3D12 R5 OK (ssr=134 gi=23 toon=141)`、device removed 0；第 1 次 9 miss / 2.941 ms，第 2 次 9 hit / 2.023 ms |
| `-zrender` 播放器 | 960×540，点「开始」后 `场景: 绘制=15 帧数=1080 mesh 种类=12`、≈90 fps、中心像素 (57,60,66)；截图见 `zrender_scene.png`，与 `TASK-B-UNITY-04/audit_zrender.png` 最优对齐后**平均差 1.0565/255、1.55% 像素 >24**（差异来自守卫/角色动画相位，非花屏/黑屏/上下颠倒） |

播放器侧：新 dll 临时替换进 `Build/Windows64/.../Plugins/x86_64/`，测后已还原
（md5 `8ba4cee33f5bdc7e8f5056e5c593564f`，与替换前一致）；`ReleaseTarget` 刷写
实际产出 `player_cache.bin` 10452 B，且第 2/3 次启动 mtime 不变（全命中不重写）。

### 额外必查

- **原子写**：`临时名 = path + ".tmp." + pid + tick` → `WriteFile` 三段 → `CloseHandle`
  → `MoveFileExW(REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)`（`pso_cache.cpp:527-531`）。
  目录项替换原子、读者只会看到完整旧文件或完整新文件（4 进程并发实测无半截、无残留 tmp）。
  **受限**：未调 `FlushFileBuffers`、临时文件也没带 `FILE_FLAG_WRITE_THROUGH`，断电时
  文件数据本身可能未落盘（rename 已持久化）。后果是“下次启动读到坏文件”，由
  blob/name 双 FNV 校验识别并重建——**不会崩，但断电后可能白丢一次缓存**。
- **多进程**：4 进程同启、同一缓存：全部 exit 0；1 个 `misses=24` 写盘，3 个
  `loaded=1 hits=24`（读到别人写好的）；最终 8528 B 有效、无 `.tmp` 残留，第 5 次
  `hits=24`。
- **缓存内容**：仅 `72B` 数字头 + 24 个 uint64 名字哈希 + 驱动 blob；可打印串只有
  `ZPSOCA01`、`dxbc2dxil`、`POSITION/NORMAL/TEXCOORD/SV_POSITION` 等，**无绝对路径、
  无用户名/邮箱/token**。
- **哈希真覆盖字节码（同文件对照，非换文件）**：
  - 同一缓存文件先跑 salt=424242（24 名），改 salt=987654 → `hits=0 misses=24`；
    切回 424242 → `names=48 hits=24`。PS 常量改动即 miss，无错误命中。
  - 改 VS 一行（`input.pos * 1.000001f`）→ 同文件 `misses=1`（重编 27.683 ms），
    换回原 shader → `hits=1`。**VS/PS 字节码均在哈希内，实测可信。**
  - 代码侧另覆盖：根 CBV/SRV 计数、全部固定状态位（按位）、8×RTV、DSV、采样、
    输入布局逐元素、CachedPSO；版本前缀 `ZPSO-HASH-v1|translate-v1|rootsig-v1` 存在。

### 非阻断风险（不影响本次 PASS，建议后续修）

1. **潜在 UAF（低概率路径）**：运行期“已知条目加载失败”时先
   `DeleteCacheFile()`（内部 `m_diskBlob.clear()`）再 `CreateEmptyLibrary()`
   （内部 `m_library.Reset()`）。按 D3D12 契约，`CreatePipelineLibrary` 的输入 blob
   需活到库释放；此顺序是“先释放 blob 再释放库”。本次用“名字表/库内容错配”
   精确触发该路径，AMD 驱动上未崩（exit 0、自愈成功），但顺序仍应调整为
   **先 Reset 库、后清 blob**。`StorePipeline` 失败路径同序。
2. **断电持久化**：见上“原子写”，建议写临时文件后 `FlushFileBuffers` 再 rename，
   把“断电丢缓存”降为 0；当前靠校验和自愈，不会崩。

### 最终裁决

**PASS（可交付）**。V1–V6 全部达成；缓存收益、坏文件自愈、兜底、回归均有独立实测
证据。两项风险为代码质量/持久化增强项，不阻断本任务验收；`m_diskBlob` 保活这一
“最易假绿点”已用 3 次以上连续第二进程 + 17 次读盘进程证伪失败（未崩）。

