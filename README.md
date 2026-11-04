# ZFrame / ZRender —— Unity 双轨游戏框架与自研渲染内核

> Unity 2022.3 LTS 上的双轨项目：**Track A（ZFrame）玩法框架** + **Track B（ZRender）自研渲染内核**（C# SRP 壳 + 原生 C++ 核心）。
> 全程由"主控/施工/验收"三角色治理，**102 个任务闭环**、每任务日志落盘（`Docs/Logs/`）。

## 1. 双轨架构（文字版）

```
GameFramework/
├─ UnityProject/                    # Unity 2022.3.62f3c1 工程
│   └─ Assets/
│       ├─ ZFrame/                  # Track A：玩法框架（asmdef 隔离）
│       │   ├─ Core                 #   EventBus / Services / ObjectPool / GameLoopDriver / Logger
│       │   ├─ Data                 #   ResourceService(IAssetProvider) / ConfigSystem(SO+CSV) / SaveSystem
│       │   ├─ Gameplay             #   FSM / InteractSystem / Command（纯 C#，禁引擎类型）
│       │   ├─ View                 #   UIFramework(MVP+面板栈) / SettingsSystem / SceneLoader / AudioService
│       │   ├─ Editor               #   架构校验器 / 编辑器工具（工作台/UI校验/命名检查）
│       │   └─ Tests                #   EditMode 880 用例 + PlayMode 48 用例
│       ├─ ZRender/                 # 双轨结合部：自研 SRP 壳 + P/Invoke 桥接
│       ├─ Game/                    # 垂直切片（Bootstrap / Domain / View，SCN_Boot→SCN_Game）
│       └─ Plugins/ZRender/         # zrender.dll（构建产物部署位，ADR-004）
├─ Native/                          # Track B：C++ 渲染内核（CMake，/W4 /WX 零警告）
│   ├─ zkernel                      #   数据内核：SoA + 128 位组件掩码 + BMI2（基准 406M 行/s）
│   ├─ zrhi                         #   RHI：Vulkan 语义抽象 + D3D12/Vulkan 双后端 + bindless + 时间戳
│   ├─ zrender                      #   RenderGraph（生命周期/别名复用/屏障合并/split barrier）+ 特性
│   ├─ zupscaler                    #   FSR 3.1（真机验证）/ FSR 4.1 路径 / DLSS5 骨架（能力门控）
│   ├─ zplugin                      #   Unity 原生插件入口（C ABI）
│   └─ tests/                       #   doctest 单测 311 用例 / 776,716 断言 + 窗口演示程序
├─ Tools/                           # build_native.ps1（原生一键构建）/ run_tests.ps1（全量测试一键）
└─ Docs/                            # 方案 / 标准 / 任务日志 / 演示 / 对等闸门材料
```

渲染管线层次：`Unity SRP 壳（剔除收集）→ P/Invoke → zplugin → RenderGraph 编排 → zrhi（D3D12 主 / Vulkan 第二后端）→ GPU`。
URP 14 保留为兜底管线，与 ZRender 可编辑期切换（运行期不跨管线热切，Unity 限制）。

## 2. 五步跑起来

环境：Windows x64 + Unity 2022.3.62f3c1 + VS2022 BuildTools（MSVC v143）+ CMake ≥3.2 + Git Bash。
命令均从仓库根执行；Unity 批处理一律走 Git Bash（授权握手限制，见 HANDOFF"重要注意事项"）。

**① 打开 Unity 工程**
Unity Hub → 打开 `UnityProject/`（2022.3.62f3c1）。首次打开较慢（导入 Library）。

**② 构建原生内核 + 部署 dll**
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1
```
cmake 配置 → Release 构建（零警告 /WX）→ doctest 单测 → 自动把 `zrender.dll` 复制进 `UnityProject/Assets/Plugins/ZRender/`。

**③ 一键全量测试（八段，任一失败非零退出）**
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1
```
依次：原生构建+单测+ctest → Unity EditMode（880）→ Unity PlayMode（48）→ 架构校验器 → 配置校验器 → 资产命名检查 → bench 回归 → player 出包（默认 SKIP，`PLAYER_BUILD=1` 开启）。
汇总报告写入仓库根 `TestReport.txt`（生成物，未入库）。Unity 批处理纪律已内置（单实例/锁清理/勿带 -quit/超时 taskkill/证据新鲜度校验）。

**④ 编辑器里玩垂直切片（Track A）**
打开 `Assets/Scenes/SCN_Boot.unity` → Play。启动链：Boot → 主菜单 → SCN_Game（WASD 移动 / E 交互 / 敌人 FSM / Esc 暂停 / 五页签设置 / 自动存读档）。
体验清单与 11 条诚实限制见 `Docs/Demo.md`。

**⑤ 跑渲染窗口演示（Track B）**
```bash
W=Native/build/bin/Release/zrhi_d3d12_window.exe
"$W"                      # 等价 --draw：首绘 cube
"$W" --lights             # 簇光源多 cube 场景
"$W" --shadows            # CSM 级联阴影（探针可证伪对比）
"$W" --r5                 # SSR + GI 探针 + NPR 卡通
"$W" --fsr on             # FSR 3.1 超分真机（对比 --fsr off）
"$W" --fsr4               # FSR 4.1 路径（本机核显无 4.x provider → 受限如实）
"$W" --parity             # URP 对等闸门场景（产出 Docs/Parity/parity_zrender.bmp）
"$W" --draw --backend both    # D3D12 vs Vulkan 双后端逐像素对照（maxDelta=0）
```

| 模式 | 预期输出（OK 标记） | 备注 |
|---|---|---|
| `--draw` | `D3D12 FIRST DRAW OK`（InfoQueue=0） | TASK-B-R4-03 首绘 |
| `--lights` | `D3D12 LIGHTS OK`（读回校验 6/6） | TASK-B-R5-01 簇光源 |
| `--shadows` | `D3D12 SHADOWS OK`（探针 delta=29 ≥10） | TASK-B-R5-02 CSM |
| `--r5` | `D3D12 R5 OK (ssr=134 gi=23 toon=141)` | TASK-B-R5-03 三特性 |
| `--fsr on` | `D3D12 FSR OK (ratio=1.346, backend=Fsr3 3.1.5)` | TASK-B-R6-01 真机 |
| `--fsr4` | 三向探测 + 本机受限说明 | 需 RX7000/9000 独显 |
| `--parity` | `D3D12 PARITY OK` + BMP 落盘 | TASK-B-R5-04 对等闸门 |
| `--backend vulkan\|both` | `PARITY OK (maxDelta=0)` | draw/lights/shadows 已接 VK；r5/fsr 显式 NOTICE |

## 3. 当前指标

| 指标 | 数值 | 出处 |
|---|---|---|
| 原生单测 | **311 用例 / 776,716 断言，全绿** | doctest（2026-11-04 八段基线实跑，Vulkan SDK 已恢复） |
| Unity EditMode | **880 用例，全绿** | NUnit（2026-11-04 A-R9-13 验收实跑） |
| Unity PlayMode | **48 用例，全绿** | 垂直切片冒烟 + 音频真放 + 千人演练场默认可见与像素证据（2026-11-04 A-R9-15 验收实跑） |
| 架构校验 | 25 asmdef 扫描 / 0 违规（含 Game/Domain 纯度扫描） | ArchitectureValidator（八段 exit 0） |
| DataKernel 基准 | 406M 行/s（BMI2 实证，零分配） | B-R1-01 |
| GPU 时间戳 | 实测 11.3ms（窗口场景） | B-R2-03 |
| 合批收益 | 绘制调用省 94.9%（对拍一致） | B-R4-02 |
| FSR 3.1 真机 | ratio=1.346（edge 0.159 vs 0.118），780M/provider 3.1.5 | B-R6-01 |
| 双后端一致性 | D3D12 vs Vulkan 逐像素 **maxDelta=0**；RG 屏障计数相等 | B-R7-01/02 |
| 千人演练场 | 1000 实体单次 `DrawMeshInstanced` 提交；默认可见 + 真实像素证据（开/关差异 3.97%）；只读游标 9.3 µs | A-R9-13 / A-R9-15 |

**里程碑状态**（定义见方案 §9）：A 轨 M0–M5 **全部收官**；B 轨 R0–R7 **全部收官**（R7 含 Vulkan 第二后端与收口）。
外部补验项（硬件受限，代码与单测已备好）：FSR 4.1 真机（需 RX7000/9000 独显——本机 780M 为核显，FFX 运行时无 4.x provider）、DLSS 5 真机（需 RTX 50 系）。

## 4. 文档索引

| 文档 | 内容 |
|---|---|
| `AGENTS.md` | 任何 AI/人接管必读（环境命令/角色铁律） |
| `HANDOFF.md` | 唯一权威状态快照（进度/活跃任务/注意事项） |
| `Docs/框架设计方案.md` | 总体方案 v2（双轨设计/里程碑/治理）+ **实现差异附录**（方案 vs 实际） |
| `Docs/Demo.md` | 垂直切片演示（怎么跑/看什么/限制清单） |
| `Docs/Parity/parity_report.md` | URP vs ZRender 对等闸门报告 |
| `Docs/Standards/file-conventions.md` | 文件与命名规范（UTF-8 无 BOM/LF 等） |
| `TASKS.md` / `WORKLOG.md` / `DECISIONS.md` | 任务看板 / 任务索引 / 决策记录 |
| `Docs/Logs/TASK-*.md` | 102 个已闭环任务日志（简报/施工/验收四节） |
