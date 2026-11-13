# 探针 · 登记表

规则见 `../Docs/Planning/约法三章-2026-11-13.md` 第三节。一句话：**真跑、真输入、缺就报缺、可复核。**
探针不接入引擎任何流程、不改引擎行为，只负责回答"到底是什么"。产出物写 `out/`，带时间戳。

## 登记

| 探针 | 测什么 | 怎么跑 | 最近一次结果 | 状态 |
|---|---|---|---|---|
| `env-probe.js` | 引擎提交、C#/场景文件数、Unity 版本与许可证、播放器 FFX 运行时、磁盘（仓库自适应，两台开发机均可跑） | `node Tools/Probes/env-probe.js` | 本机 2026-11-13：10 项取到 / 3 项 MISSING（见 out/env-2026-11-13T16-56-35.json 与修正后 17 时档） | 可用 |
| `fsr4-probe` | 本机 FSR 4.1 真实能力（真设备 vs null 对照） | 未编写；修复后由 TASK-B-PROBE-01 交付 | — | **缺** |
| `linear-bridge` | ZRender 输出与 URP 同机位亮度差 | 未编写；TASK-B-PROBE-02 交付 | — | **缺** |
| `planet-budget` | 1080p 下 chunk 数/帧耗/接缝计数 | 未编写；P2（TASK-B-PLANET-*）验收前置 | — | **缺** |
| `abi-precision` | double→float 提交在 100m/1km/10km/100km 的 ULP 损失 | 未编写；P2 验收前置 | — | **缺** |
| `gpu-probe` | 显卡型号/显存/驱动 | 未编写（需 DXGI/WMI 采集器） | — | **缺** |

## 已存档的两机基线（env-probe）

| 机 | 时间 | 关键结果 | 证据 |
|---|---|---|---|
| 游戏机（RX 9060 XT/RDNA4） | 2026-11-13 | HEAD cdf4eb2；Unity 2022.3.62f3c1；FFX 运行时 3 个（另有 1 个 amd_ags_x64.dll 属 AGS 非 FFX）；**许可证 MISSING**（批处理不可用）；磁盘 D 剩 359.5GB | `out/env-2026-11-13T07-11-24.json`、`out/env-2026-11-13T09-20-14.json` |
| 引擎机（780M/RDNA3，本机） | 2026-11-13 | HEAD cdf4eb2；556 C# / 6 场景；许可证在位；**Build/Windows64 播放器无 FFX DLL（MISSING，如实记录）**；磁盘 C 剩 124GB | `out/` 本机时间戳档 |

## 已发现的环境事实（不填默认值）

1. **播放器 FFX DLL 两机不一致**：游戏机 Build/Windows64 有 3 个 FFX DLL（手工补的，HANDOFF 曾记"4 个"，多出的 1 个实为 AMD AGS）；引擎机播放器 0 个。→ 引擎机玩家包当前不能跑 FSR（属实）；原生测试 exe 目录（`Native/build/bin/Release/`）FFX DLL 齐备，引擎侧真机验证走 `zrender_test.exe`。
2. **引擎内现有 caps 探测传空设备**（`upscaler_caps.cpp:61`），结果不可信——修复见 TASK-B-FSR-01；正式探针见 TASK-B-PROBE-01。
3. 纯 Node 取不到显卡信息（gpu-probe 仍缺），**没有编造显卡型号**。
