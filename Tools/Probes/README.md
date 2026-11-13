# 探针 · 登记表

规则见 `../Docs/Planning/约法三章-2026-11-13.md` 第三节。一句话：**真跑、真输入、缺就报缺、可复核。**
探针不接入引擎任何流程、不改引擎行为，只负责回答"到底是什么"。产出物写 `out/`，带时间戳。

## 登记

| 探针 | 测什么 | 怎么跑 | 最近一次结果 | 状态 |
|---|---|---|---|---|
| `env-probe.js` | 引擎提交、C#/场景文件数、Unity 版本与许可证、播放器 FFX 运行时、磁盘（仓库自适应，两台开发机均可跑） | `node Tools/Probes/env-probe.js` | 本机 2026-11-13：10 项取到 / 3 项 MISSING（见 out/env-2026-11-13T16-56-35.json 与修正后 17 时档） | 可用 |
| `fsr4-probe` | 本机 FSR 4.1 真实能力（真设备 vs null 对照） | 未编写；修复后由 TASK-B-PROBE-01 交付 | — | **缺** |
| `linear-bridge` | ZRender 输出与 URP 同机位亮度差（Editor 工具，程序化契约场景，不依赖任何 .unity） | Unity 批处理 `-executeMethod ZFrame.Render.Probes.LinearBridgeProbe.RunFromCommandLine`（勿带 -quit，自退出）；或菜单 ZRender > Probes > Linear Bridge Compare | 本机 2026-11-14（HEAD 06f3478，780M，Linear + URP_Medium）：**桥较 URP 直出亮 12.5%**（帧级均值 luma 61.22→68.88，0-255 域；中心半幅 +8.5%）；p50=0、p95=57.85、p99=77.86（\|Δ\| 分位）；原生中心像素回读 (13,26,38) 与 kClearColor 注释值逐字节一致 | 可用 |
| `planet-budget` | 1080p 下 chunk 数/帧耗/接缝计数 | 未编写；P2（TASK-B-PLANET-*）验收前置 | — | **缺** |
| `abi-precision` | double→float 提交在 100m/1km/10km/100km 的 ULP 损失 | 未编写；P2 验收前置 | — | **缺** |
| `gpu-probe` | 显卡型号/显存/驱动 | 未编写（需 DXGI/WMI 采集器） | — | **缺** |

## 已存档的两机基线（env-probe）

| 机 | 时间 | 关键结果 | 证据 |
|---|---|---|---|
| 游戏机（RX 9060 XT/RDNA4） | 2026-11-13 | HEAD cdf4eb2；Unity 2022.3.62f3c1；FFX 运行时 3 个（另有 1 个 amd_ags_x64.dll 属 AGS 非 FFX）；**许可证 MISSING**（批处理不可用）；磁盘 D 剩 359.5GB | `out/env-2026-11-13T07-11-24.json`、`out/env-2026-11-13T09-20-14.json` |
| 引擎机（780M/RDNA3，本机） | 2026-11-13 | HEAD cdf4eb2；556 C# / 6 场景；许可证在位；**Build/Windows64 播放器无 FFX DLL（MISSING，如实记录）**；磁盘 C 剩 124GB | `out/` 本机时间戳档 |

## linear-bridge 探针（TASK-B-PROBE-02，2026-11-14 交付）

- **怎么跑**（第三次复跑数字应逐位一致——两次独立跑 61.2226/68.877 完全相同）：
  ```bash
  "C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode \
    -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" \
    -logFile Tools/Probes/out/linear-bridge-run.log \
    -executeMethod ZFrame.Render.Probes.LinearBridgeProbe.RunFromCommandLine
  # 勿带 -quit（方法自带 EditorApplication.Exit）；退出码 0=已写档（OK 或 MISSING 都是数据）
  ```
  产物：`out/linear-bridge-<UTC时间戳>.json` + `-urp.png` + `-zrbridge.png` + `-diff.png`（文件名白名单 + out 目录边界断言，同 env-probe 口径）。
- **口径**：程序化临时场景（--parity 契约常量：机位 (0,1.6,-4) 朝 +Z、1280×720、地面 20×20 灰 0.5 + 两 cube；探针差异：关阴影、URP 背景取原生清屏色同值）。URP 帧 = 活跃管线直出（sRGB 显示域）；桥帧 = 既有 Bindings 驱动（attach→共享目标→UNORM 外部视图→SetCamera/DrawMesh→RenderFrame×3→生产 ZRender/Blit 直通回读），与 `ZRenderPipeline.cs:243` 同参——被量化的正是这条 UNORM 视图链。
- **结果解读（"略暗"从此有数，但方向反了）**：HANDOFF 渲染体检记"UNORM 视图画面偏暗是已知代价"。实测桥较 URP 直出**亮** 12.5%（帧级）。差值不是背景（p50=0，背景两管线逐字节相同），来自物件区：原生场景着色器是 Lambert+固定环境项 0.45（`zrhi_d3d12_unity_target.cpp:1100-1121`），且桥把 Unity 材质的 sRGB 基色当线性值直接乘光——URP 侧是 PBR+环境 0.12+sRGB 解码。这是**复合差**（着色模型+颜色语义+视图链），JSON 已记录双方全部光照常量可拆读。对 P1-b（typeless 方案）的含义：换 sRGB 视图只影响视图链一项，先要拆出着色/颜色语义项才能看到它的净效果。
- **交叉验证锚点**：原生 `CenterRGBA=(13,26,38)` 与 `kClearColor` 注释"8bit (13,26,38)"逐字节一致；cube B -X 面 ndl=0 → 桥帧该面 = albedo×ambient 精确吻合；两帧几何逐像素对齐（矩阵链复刻正确）。

## 已发现的环境事实（不填默认值）

1. **播放器 FFX DLL 两机不一致**：游戏机 Build/Windows64 有 3 个 FFX DLL（手工补的，HANDOFF 曾记"4 个"，多出的 1 个实为 AMD AGS）；引擎机播放器 0 个。→ 引擎机玩家包当前不能跑 FSR（属实）；原生测试 exe 目录（`Native/build/bin/Release/`）FFX DLL 齐备，引擎侧真机验证走 `zrender_test.exe`。
2. **引擎内现有 caps 探测传空设备**（`upscaler_caps.cpp:61`），结果不可信——修复见 TASK-B-FSR-01；正式探针见 TASK-B-PROBE-01。
3. 纯 Node 取不到显卡信息（gpu-probe 仍缺），**没有编造显卡型号**。
