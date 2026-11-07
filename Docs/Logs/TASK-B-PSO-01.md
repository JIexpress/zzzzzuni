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

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 第二次启动 PSO 创建耗时明显下降（两次数字）
- [ ] V2 删缓存后能启动
- [ ] V3 **故意写坏缓存**能识别并重建，不崩
- [ ] V4 无 `ID3D12PipelineLibrary` 支持时静默回退（有探测证据或代码路径）
- [ ] V5 回归：317 / --draw 60 帧 / -zrender 正常
- [ ] V6 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
