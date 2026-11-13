# HANDOFF —— 项目当前状态快照

> 唯一权威状态源。接管者先读本文件，再读 `Docs/交接档案.md`（路线图+环境坑+技术债），再读 TASKS.md。
> ⚡ **给下一个 LLM 主控**：先读 `Docs/LLM-HANDOFF-NOTES.md`——132 个任务沉淀的全部操作手册（派发模板/变异验证法/故障处置手册/技术坑备忘/工具调用技巧/额度管理），照做即可满血接力。
> 历史交接记录已归档 git 历史（`git show G-113:HANDOFF.md`），WORKLOG.md 有全部任务索引。

**快照时间**: 2026-11-14（**双机分工确立+U盘交接材料入仓 G-158；引擎补全轮启动**）｜**刷新者**: 主控（ZCode，GLM 会话）｜**仓库**: `C:\Users\1\Desktop\zcode\GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni

## ⚡ 最新（2026-11-14，双机分工 + 材料入仓 + P0 派发）

- **双机分工（用户 2026-11-14 拍板）**：本机（780M/RDNA3）=**引擎改进机**；另一台机（RX 9060 XT/RDNA4，`D:\Dev\ZFrame` 只读）=**游戏开发机**——第一版 EVA 修复循环用 URP（清单 `Docs/Planning/07-第一版清单-2026-11-13.md`）。本机本轮=按 U 盘《ZRender-改进计划-2026-11-13》补齐全部未完成项。
- **交接材料入仓（G-158）**：`Docs/Planning/`（07 清单/08 需求对照/改进计划/约法三章）；探针家底移 `Tools/Probes/`（env-probe.js 仓库自适应两机可跑，out/ 存两机证据）。**约法三章为硬规矩**：真跑/真输入/缺就报缺/可复核；**全仓写死能力表面一律视为伪造候选**（对端 demo FSR 选项写死被真机戳穿的教训，用户明确要求）。
- **08 对照结论（对端 AI 实测，基线 cdf4eb2=本机 HEAD，无漂移）**：框架要求基本全实现；ZRender 实为"旁路渲染器"（C ABI 只收 float 矩阵+rgba）；未做到=XeSS/FSR3.5/多视图共享可见性（B-VIS-01 裁决：共享可见性已实现）；半=Config 代码生成（已闭环 A-CONF-01）/替代 Unity/满血 bindless/3 周期实测（已闭环 B-KERNEL-01：实测 4~5/5，目标 3 未达如实记）/跨平台。**改进计划口径：不把 ZRender 做成完整管线，只做行星 chunk 专用通道（P2，第二章前，4–8 周）。**
- **P0 已闭环（B-FSR-01，ae03f62，验收 PASS 11P+1MISSING裁决）**：upscaler_caps.cpp 枚举适配器后建真设备传 `ZupFsr4Probe`。**实测修正——780M(iGPU)+仓内 SDK v2.3.0 即使真设备也无 4.x provider（null=真设备=2 条：3.1.5/2.3.4）**：「FSR4 本机不可用」是硬件真相（B-R6-02 结论成立），假阴性差异只在支持硬件出现（对端 RX 9060 XT null=2 vs 真=4 含 4.1.2）。修复价值=支持硬件上消除假阴性，本机负对照证无误报。350 用例/778,737 断言全绿（+1/+14）；失实注释 4 文件改事实陈述；fg_ffx.cpp/.h 同型遗留移交 B-FSR-02；**阳性对照移交 B-PROBE-01（两机探针，对端补验）**。
- **本机 env-probe 首跑**：HEAD cdf4eb2 / 556 C# / 6 场景 / 许可证在位；**本机 Build/Windows64 播放器缺 FFX DLL**（对端是手工补的 3 FFX+1 AGS）→ 引擎机玩家包当前不能跑 FSR（属实记录）；原生测试 exe 目录 FFX DLL 齐备，真机验证走 `zrender_test.exe`。
- **本轮进度（2026-11-14 凌晨）**：B-VIS-01 done（132d183）｜B-FSR-01 P0 done（ae03f62）｜B-FSR-02 P1 done（ce4bdf4）｜A-CONF-01 done（d15faad）｜**B-PLANET-01 done（80d82bc，FAIL一轮→修订→复验 PASS）**：P2 行星 ABI 设计稿定稿（`Docs/Planning/P2-行星ABI设计.md`，四项签名可抄、B-PLANET-02..12 已登看板）｜B-KERNEL-01 done（fcbcb95，验收 PASS K1-K7 变异亲做）——08 对照 #7 从「未验证」改判「已验证」：**实测 a1 4~5 / a2 端到端 5 / a3 含 idiv 17 cycles/op，设计目标约 3 未达（如实记录）**；BMI2 表述 6 文件纠正为 POPCNT/TZCNT｜**B-PLANET-02 done（f5e6aa1，验收 PASS P1-P7）**：行星 ABI 头落地——§14 汇编入 zplugin.h（接口冻结）、ApiVersion=3 六链同步、ZrPlanetNative 门面+门控 ≥3、def 零 diff｜**X-NAMES-01 done（42e981f，验收 PASS）**：naming 段 0 违规（A-CONT-01 的 6 处红灯清零）｜**B-PLANET-03 done（11a477f，验收 PASS Z1-Z6）**：行星数学生产化——Native/zplanet/ 在库、15 条 doctest 逐位回归、§② 重力留 tests（shim）｜**B-PLANET-05 done（3d3ac1c，验收 PASS C1-C7）**：SetCameraOrigin/GetCameraOrigin 实现、def 46→48、行星量级 round-trip 逐位 doctest｜**B-PLANET-06 done（63fa2f4，验收 PASS D1-D10）**：SetWorld+SampleHeight、def 48→50、**12,864 chunk 顶点采样 0 失配**（玩法贴地=渲染网格逐位）、并发 38,400 次零撕裂｜**B-PLANET-04 done（8b03db6，验收 PASS E1-E10）**：**P2 最大件落地**——chunk 通道六导出 50→56、RenderFrame 集成同深度缓冲、--planet3 真渲染实证（30 chunk 剔除 26/30.6 万像素非清屏色/截图入库）、相机原点迁移零 doctest 破坏、账目恒等式+变异复红、单 chunk 近档 7.4ms 实测补 MISSING；G1 uv 语义设计稿勘误、G4 改判 v1 全档核查（开销项转 07）｜**B-PROBE-01 done（74ec9bd，验收 PASS F1-F7）**：fsr4-probe 两机可跑（便携模式 exe+4DLL）、本机负对照三方逐位互证、FG 族双设备对照首次有数、**阳性对照待对端 RX 9060 XT 补验**（步骤在 Tools/Probes/README.md）｜**B-PLANET-07 done（b73c0c5，验收 PASS J1-J8）**：Unity 托管桥——chunk 流式管理器（分帧预算+近档硬帽）、**WorldOrigin 首次真接线**（ToCameraRelativeView 唯一边界）、账目 A1-A12、EditMode 1211/1211｜**B-PLANET-08 done（dce7e49，验收 PASS M1-M7）**：接缝专项——VB 缝宽 MISSING 清零（240 对三层口径，实测 ≈1×ULP）、三方案定版 A 维持裙边、neighborMask 三面锁定、**G4-R1 原生门：远档 seam 固定税 920ms→0.019ms**（B-PLANET-07 测得的 ~150µs/块税已消失）｜**B-PLANET-09 doing（planet-budget 探针 1080p）**｜待派队列：B-PLANET-10 abi-precision→11/12。backlog 新增：X-DIAG-02（test_zfg_env.cpp:117 基线 SIGSEGV）。backlog：A-CONF-02、X-NAMES-01、B-VIS-02。**推远程待用户确认（AGENTS.md 铁律）**。
- **git 现场**：main 领先 origin/main **51+ 提交**未推（推远程需用户确认——AGENTS.md 铁律）。

## ⚡ 最新-2026-11-09（A-AI-04 闭环——NPC 会自己走去找事做）

- **看板真实口径**：**144 done / 0 doing / 0 todo / 1 held**（held = A-AI-05，NPC 自主模式+预约态，简报已写未动工，正式开发前暂缓后置；A-AI-01/A-AND-09/A-AI-02/A-AND-10 早已闭环，**不要再派**）。
- **A-AI-05 暂缓（held，本次收尾登记）**：NPC 自主模式（G 键开关）+ 预约态（到达才占）。简报已写、**零半成品代码**（`NpcAutonomyMode.cs` 不存在、`AffordanceRegistry.cs` 无预约痕迹）。用户决定进入正式游戏开发，此功能后置，恢复时按简报 §五验收清单直接派施工。
- **测试真家底（实测）**：原生 **349 用例 / 778,723 断言全绿**（`zrender_test.exe`）；EditMode **1140** + PlayMode **110**（Linear 下实跑）；合计 **1599 用例**。架构校验 **26 asmdef / 0 违规**。
- **A-ANIM-02 动画扩展第一刀（d938565）**：跳跃从「只触发动画」变成真垂直位移——纯函数弧线（v0=√2gh、T=2v0/g、峰值 0.7500m、落地差 0.0000）、`Grounded` 由真值驱动、控制器补 Idle→Jump 与 Jump→Run（6 过渡）、空中二次请求被拒。两处变异复红后 sha256 还原。
- **A-ANIM-03 脚步 IK（0d65dd3）**：平地零干预（权重<0.05）、25° 斜板权重>0.3 且脚贴合地面法线；中性踝高在平地学习，避免「平地也拽脚」；变异关掉 IK → 用例必红后 sha256 还原。
- **A-AI-04 可供性接场景（89f3497）**：`SceneAffordances` 按真实锚点（桶/树/NPC）注册 3 条可供性、**零可见物**；`AffordanceSeeker` 走过去并占用（默认不挂、10s 超时）。修掉「自占用导致目标抖动」的 bug。验收 PASS 7/7。**受限**：Claim 发生在选中时而非到达时。
- **A-AI-03 可供性扫描（ccb5897）**：`Affordance`/`AffordanceRegistry`（容量/64 预算/最近可用）+ `AffordanceAction` 接入效用打分（`needWeight×clamp01(1-d/max)×available`）。既有 AI 四文件零改动。验收 PASS 7/7。
- **已知偶发**：物理边界用例 `ConnectedChain_...` 曾出现 0.0500005335 vs 0.05 的边缘红，复跑即过（验收已记录，未修）。
- **A-WORLD-01 双精度世界（fc08132）**：`WorldPosition`（double）+ `WorldOriginMath` 纯函数 + `WorldOrigin`（4000m 注册制重定位）。100km 处 float ULP **7.8mm** vs double 守恒 **4.4e-11m**；验收 PASS 7/7。**未挂载到任何场景**（零影响）。
- **A-ASM-03 建造模式（2dba3c7）**：SCN_Game 里按 **B** 进建造：Q/E 切 5 种零件、鼠标吸附连接（≤1.2m）、右键删除、**K 存 / L 读**蓝图（`slot1`，走 AssemblyGraphSync+SnapshotCodec+Rebuilder）。默认待机零可见物。验收 PASS 6/6，PlayMode 98。
- **A-LOOP-02 战斗闭环（4394a2d）**：敌人 2 点血 + 受击闪白，J 键扇形攻击（2.5m/±60°/0.35s），击杀计入 HUD；活敌才扣火堆耐久。验收 PASS 6/6。
- **技术验证报告**：`Docs/TECH-VALIDATION.md`（AI 自主 NPC / 装配 / 行星尺度三条线的现状-差距-路径-验证方式，含建议任务顺序）。
- **A-LOOP-01 最小可玩循环（ddfb03c）**：守住火堆 90s——纯逻辑状态机（Idle/Running/Won/Lost）、波次曲线（人数递增/间隔 6s×0.85ⁿ 下限 3s）、火堆耐久、IMGUI HUD、F 开始/R 重开；**待机态不建任何可见物**（像素用例零影响）。EditMode 1111/PlayMode 91。
- **A-CONT-01 内容管线（42f5a26）**：6 张程序化可平铺贴图 + 7 个环境材质挂 `_BaseMap`（底色归一白，否则二次压暗）+ SCN_Game 地面换成草地材质；内容守卫 2 例（贴图槽/导入设置/底色）。画面细节量 **+56%**。
- **A-CUT-01 L3 过场（accffb5）**：`CutsceneDirector` 门闸 + VideoPlayer→RT→全屏 RawImage；过场期间人群全局抑制（L0 隐藏、L1/L2 不提交、kernel 不读），**分配器仍不产出 L3**（A-AND-09 硬上限语义不变）。batchmode 下真解码、帧号推进；变异复红。
- **A-R9-19 色彩管线迁移（82daf86）**：Gamma→Linear + Medium/High 补 Tonemapping(Neutral) + 两档后处理重新分层（High/Medium 差 1.48x）+ ZRender 桥改 UNORM 视图。Linear 下 **1102+88 全绿**，SCN_Game 实拍均值 +8%、无裁切。
- **git 现场**：HEAD `57ecb98`（A-ART-01），工作区有 3 处未提交改动（TASKS.md 登记 A-AI-05、`Docs/Logs/TASK-A-AI-05.md` 收尾、`Docs/接入指南-给下一个Agent.md` 新增），**领先 origin/main 50 个提交未推**（A-AND-10 触屏包、A-AI-02、A-AND-09、A-AI-01、G-136~G-140 治理与交接手册）。
- **文档漂移已修（G-141）**：README 的 103 任务/916/48/311/25 asmdef、TASKS 行内统计 114、A-R9-18 空 commit、ROADMAP 方向四 doing/todo 全部对齐实测值。
- **盘点新发现（已处置，见下「渲染体检」）**：色彩空间 Gamma → **已迁 Linear**；HDR 无 Tonemapping → **已补**；`Art/Textures` **0 张位图**、动画 0、预制体 5（内容短板仍在）。

## 三十秒速览

| 项 | 值 |
|---|---|
| 任务看板 | **144 done / 0 doing / 0 todo / 1 held**（TASKS.md，held=A-AI-05 暂缓） |
| 测试家底 | 原生 **349 / 778,723 断言** + EditMode **1102** + PlayMode **88** ≈ **1539 用例**；三校验器 exit 0 |
| 代码规模 | C# 530 文件 / 86,755 行；C++ 175 文件 / 77,106 行（生产 27k + 测试 34k） |
| 可交付物 | `Build/Windows64`（81.8MB Mono 包）、`Build/Installer/ZFrame-Setup-0.1.0.32.exe`（24MB）、`Build/Android/ZFrame.apk`（IL2CPP，minSdk 22，Vulkan→GLES3） |
| 管线 | Unity 2022.3.62f3c1 + **URP 14.0.11**（活跃）；ZRender 自研管线为可切换的第二管线（D3D12 门控） |
| 最新里程碑 | 三技术切片全 PASS；AI 深水区（记忆/态度/协作/人群 LOD）收官；Android 触屏打磨完成 |

## 一句话现状

**功能侧无未闭环任务（1 项 held 暂缓）**：双轨（玩法框架 + 原生渲染内核）全部里程碑收口，Windows/Android 都能出包。**下一棒不是补欠账，是选方向**——用户已决定进入正式游戏开发；技术侧最值钱的三件事在「下一步优先级」，内容侧最大短板是零贴图零动画。

## 渲染体检（2026-11-09 主控只读扫描，未改任何渲染资产）

| 项 | 实测 | 判据 |
|---|---|---|
| 活跃管线 | `GraphicsSettings.m_CustomRenderPipeline` = `URP_Medium.asset`；包内只有 URP 14.0.11 | ZRender 需手动切换 |
| 三档映射 | QualitySettings 三档 → Low/Medium/High 三个 URP asset；MSAA 1/2/4，阴影距离 20/40/60，软阴影仅 High；三档 `m_UseSRPBatcher:1` | 映射正确 |
| 材质/Shader | 10 个 `.mat` **全部**引用 URP/Lit（guid `933532a4…`）；自定义 shader 仅 `ZRenderBlit.shader` | 无粉红风险 |
| 后处理链 | Low 无 override（无 HDR，按设计）；Medium = Tonemapping(Neutral) + Bloom 0.6；High = Tonemapping + Bloom 2.5 + ColorAdjustments(exposure 0.5/sat 14) | 三档分层，差 1.48x |
| 色彩空间 | `m_ActiveColorSpace: 1` = **Linear**（2026-11-09 A-R9-19 迁移） | 正确 |
| 深度/不透明纹理 | `m_RequireDepthTexture:0` / `m_RequireOpaqueTexture:0`；RendererFeatures 空 | 做 SSAO/软粒子前必须开 |
| 内容资产 | 贴图 **6**（程序化环境套件，256²）、动画 **0**、预制体 5、模型 25 FBX、音频 7 | 环境已有材质；角色仍是纯色 |

**渲染缺陷处置结果（2026-11-09 A-R9-19 已全部落地，Linear 下 1102+88 全绿）**：

| # | 缺陷 | 处置 | 证据 |
|---|---|---|---|
| 1 | Gamma 色彩空间 + URP（HDR/Bloom 在线性空间外混合） | 切 **Linear** | SCN_Game 均值 116.5→125.9（+8%）、pct>32=0.89%、无裁切；全量 1102+88 |
| 2 | HDR 开但无 Tonemapping（高光直接裁剪） | Medium/High 补 **Tonemapping(Neutral)** | 同上 |
| 3 | 三档后处理不连贯 | 重新分层：Low 无后处理 / Medium Tonemap+Bloom 0.6 / High +exposure 0.5+sat 14 | High/Medium on-off 差 **1.48x**（门限 1.35） |

代价与边界（如实）：
- **P4 灯光未逐场景重标定**：`m_LightsUseLinearIntensity` 随迁移翻 1，画面整体略亮——这是"接受新基准"，不是"调完"。某场景偏亮/偏暗就单独调该场景 `Light.intensity`。
- **ZRender 桥**在 Linear 下改用 UNORM 视图（`CreateExternalTexture(linear:true)`），dxgi 报错消失；代价是这条**非活跃**管线（活跃的是 URP）画面与原生直出存在亮度差。**2026-11-14 B-PROBE-02 实测（72126a0）：桥输出较 URP 直出亮 12.5%（luma 61.22→68.88）——"略暗"形容词被推翻**；差值是复合差（原生 Lambert+环境 0.45/sRGB 基色当线性 vs URP PBR+环境 0.12），背景逐字节相同（p50=0）；换视图前须先拆着色/颜色语义项（P1-b 前置）。数字+差值图：`Tools/Probes/out/linear-bridge-*.json`（探针 `ZRender>Probes>Linear Bridge Compare`，两次独立跑逐位可复现）。可消除该代价的 typeless 方案实测触发设备移除，留作 P1-b。
- 迁移工具 `Assets/Game/Editor/ColorPipelineSetup.cs` 保留为幂等重放入口，带 `ZF_COLOR_MIGRATION_OK=1` 闸门。

⚠ **踩坑记录（咬了两次）**：切色彩空间后的**第一次运行不可信**——首张截图全黑、首轮粒子像素用例假红（0.0003 vs 门限 0.0040），复跑即恢复。任何色彩空间/管线切换的证据都必须复跑一次再判定。

## 接管者动作序列

1. `git log --oneline -5` + `git status`（有 3 处未提交改动：TASKS.md 登记 A-AI-05、A-AI-05 日志收尾、接入指南新增）＋确认 `git rev-list --count origin/main..HEAD`（当前 50）
2. 读 `Docs/LLM-HANDOFF-NOTES.md`（操作手册）+ `Docs/交接档案.md`（环境坑/技术债）
3. 先跑一次基线再动手：`./Native/build/bin/Release/zrender_test.exe --success=false`（应 349/778,723）；Unity 五段用 `Tools/run_tests.ps1 -Segments editmode,playmode,arch,config,naming`
4. 从「下一步优先级」选一项 → 写简报 → 派施工 → 派**不同**验收 → commit → 刷新三件套

## 下一步优先级（按价值/成本排序，均无需新硬件）

| # | 事项 | 现状 | 代价 |
|---|---|---|---|
| 1 | 渲染三缺陷（已知会连锁，需按 P1→P5 做） | 试验已证伪并回滚，方案齐 | 大，含原生桥与美术标定 |
| 2 | 动画扩展续（脚步 IK → 程序化层） | 跳跃已闭环；IK 只有 look-at | 中，PlayMode 可自证 |
| 3 | SRP Batcher 与 DrawCall 可测化（A/B 测量，替代 Frame Debugger 目测） | 只有开关，无数字 | 中，PlayMode 用例可自证 |
| 4 | 内容管线起步（贴图/材质/动画） | 0 贴图 0 动画 | 大，决定「像不像游戏」 |
| 5 | 游戏本体（星球探索 + 装配生存） | 用户构思中，勿抢跑 | 大 |
| 6 | Windows IL2CPP 正式包 | 当前 Mono | 小，一条命令 + 验证 |
| 7 | 推 50 个提交 / token 轮换 | 待用户确认 | 极小 |

## 环境纪律速查（每条真实踩过，详见交接档案 §3）

- **Unity 批处理**：单实例（查进程+删 `UnityProject/Temp/UnityLockfile`）｜僵死（轮询 xml，5 分钟有果即 `taskkill //PID //F`）｜跑测试**勿带 -quit**｜证据以 xml 为准
- **子代理派发**：并发/额度限制 → 退避 5-10 分钟重试；停滞 10 分钟 → 查现场续派或轻量代理复核收尾（ADR-006）
- **代理变量重复**（HTTP_PROXY+http_proxy）崩 MSBuild——构建前按交接档案 §3.2 清理
- **Tuanjie Hub**：无头 CLI 不认（Android 子模块已用外部工具链绕行，勿重试）；unity.cn 页面是 SPA（shell 抓不到直链）
- **网络**：github raw 可能重置→REST API；含 % 中文勿用 python % 格式化（用 Edit 工具）
- **Android 工具链（已装配勿重装）**：`C:/Users/1/Android/`——JDK11/cmdline-tools 19.0/platform-tools/build-tools 34.0.0/NDK r23b；EditorPrefs 键名实测：`AndroidSdkRoot`/`AndroidNdkRootR23B`/`Jdk11Path`（硬校验 JDK major==11）
- **zrender.dll**：构建产物入库（ADR-004），提交前 `git checkout --` 丢弃非任务 M 噪声

## 待办备忘

- **50 个提交未推**（推远程前必须用户确认，AGENTS.md 铁律）；CI token 轮换仍成立（用户给的令牌与仓内凭据逐字节相同，非新令牌）
- `Tools/android_support_installer.exe` 456MB 已在 .gitignore（未入库），可删本地副本
- 仓库 515MB：FSR 两个 DLL 占 65MB、`Docs/Parity` PNG 约 20MB——ADR-004 允许，但 clone 成本高
- `TestPanelView` 仍随包（技术债 §5）；`run_tests.ps1` BOM 可顺手清
- CI 只跑 L1（native+static），Unity 层注释在 ci.yml 文末——**云端绿 ≠ Unity 绿**，别把 CI 当全量证据

## 最近任务日志

- 触屏打磨 A-AND-10（PlayMode 85 / EditMode 1099）、AI 队友 A-AI-02（1082）、人群 LOD A-AND-09、NPC 记忆 A-AI-01
- 全部历史：WORKLOG.md（132 任务索引）

## 下一个接管者

一切以磁盘为准：AGENTS.md → HANDOFF（本文件）→ TASKS.md → 交接档案 → LLM-HANDOFF-NOTES。写简报→派施工→派验收→闭环刷新。祝顺利。
