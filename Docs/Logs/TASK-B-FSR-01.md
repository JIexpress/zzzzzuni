# TASK-B-FSR-01 —— P0 修复：FSR4 探测传空设备假阴性

- **轨道**：B / FSR（U 盘改进计划 P0）
- **状态**：doing
- **派发**：主控，2026-11-14
- **来源**：`Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P0（对端机 RX 9060 XT 实测定位）+ `Docs/Planning/08-原始需求对照-2026-11-13.md` §二#16；本机历史 B-R6-02「FSR4 本机不可用=受限如实」即此根因

## 任务简报（主控写）

### 0. 背景（为什么改）

能力探测 `ZupProbeDeviceCaps`（`Native/zupscaler/upscaler_caps.cpp`）上半段已经用 DXGI 枚举到首个非软件适配器（vendorId/deviceId 都拿到），但下一行仍是：

```cpp
outCaps->fsr4Available = outCaps->fsrRuntimePresent && ZupFsr4Probe(nullptr);
```

`ZupFsr4Probe(void* nativeDevice)` 实现在 `Native/zupscaler/fsr_upscaler.cpp:521`，内部经 `ZupFsrEnumerateProviders` → `EnumerateProvidersImpl` 把设备交给 FFX 运行时枚举 provider。FFX 拿不到真实设备时返回的 provider 列表被截断：对端机实测 **null=2 条，真机=4 条（含 4.1.2）** → FSR4 被误标"不支持"。

**`upscaler_caps.cpp` 文件头注释「FFX 对 null 设备返回与真实设备一致的保守列表」已被实测证伪——改代码时必须同步把这句（及 `fsr_upscaler.cpp` 里同型表述，约 :131 附近）改为事实陈述，不得保留。**

### 1. 修法（方向）

1. 在 `ZupProbeDeviceCaps` 里用已枚举的 `IDXGIAdapter1*` 调 `D3D12CreateDevice(adapter, ...)` 创建真实设备（feature level 从高到低取首个成功的），把 `ID3D12Device*` 传给 `ZupFsr4Probe`，用完 Release
2. 设备创建失败（无硬件/CI 环境）→ 回退 nullptr 路径，行为与现状一致——**CI（GitHub Actions L1，无 GPU）必须仍绿**
3. `Native/tests/zrhi_d3d12_window.cpp:9907` 有一处同型调用 `ZupFsr4Probe(nullptr)`（窗口模式测试，本身有真设备上下文）——同步改传真设备
4. 检查 `ZR_Upscaler_QueryCaps`（`Native/zplugin/zplugin.cpp`）有无进程内缓存；有则保持缓存语义（首调探测一次，缓存命中不再创建设备）
5. **不动 ABI**：`ZrUpscalerCaps` 结构体与 `ZR_ApiVersion` 本任务不递增（P1 的 probeMode 字段在 B-FSR-02 才动）

### 2. 测试与验证

1. `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 全绿（/WX 零警告）；**构建前按交接档案 §3.2 清理重复代理变量**（HTTP_PROXY 与 http_proxy 并存会崩 MSBuild）
2. `Native/build/bin/Release/zrender_test.exe --success=false` 全绿（基线 349 用例/778,723 断言；新增用例后数字只增不减）
3. `test_upscaler.cpp` 补 doctest：caps 探测结构稳定性（无硬件环境下字段合法、布尔自洽；**不得断言"本机有 FSR4"**——CI 无 GPU 必须可过）
4. **本机真机对照（施工报告必须贴数字证据）**：用临时验证程序（或借窗口模式测试路径）在本机各跑一次 null 路径 vs 真设备路径的 `ZupProbeDeviceCaps`，打印 provider 条数与 `fsr4Available`。**两条路径结果不一致才算修好**。本机=780M(RDNA3)，FFX DLL 在 `Native/build/bin/Release/` 齐备，预期真设备路径 `fsr4Available=true`
5. 临时验证程序不作为正式探针入库（正式探针是 B-PROBE-01）；验证完删除，不留死代码

### 3. 铁律（约法三章，`Docs/Planning/约法三章-2026-11-13.md`）

- 不许把「未验证」写成「已验证」；真跑的结果带时间戳写进施工报告
- **不 git commit**（主控提交）；不改 TASKS.md/HANDOFF.md/WORKLOG.md（主控刷新）
- 构建产物 `zrender.dll` 入库（ADR-004）：按 build_native.ps1 的部署步骤把 dll 更新到 UnityProject，变更留给主控提交
- 文件规范：UTF-8 无 BOM / LF（.editorconfig 已强制）

### 4. 完成判据

- 本机实测：真设备路径 provider 条数 > null 路径，且真设备路径 `fsr4Available=true`（数字+时间戳进施工报告）
- 全量原生测试绿；无硬件回退路径行为不变（CI 口径不受影响）
- 两处失实注释已改为事实陈述（写明实测差异）
- 施工报告四节之「施工报告」节写全；验收由**不同**子代理出 PASS/FAIL
