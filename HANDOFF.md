# HANDOFF —— 项目当前状态快照

> 唯一权威状态源。接管者先读本文件，再读 `Docs/交接档案.md`（路线图+环境坑+技术债），再读 TASKS.md。
> ⚡ **给下一个 LLM 主控**：先读 `Docs/LLM-HANDOFF-NOTES.md`——132 个任务沉淀的全部操作手册（派发模板/变异验证法/故障处置手册/技术坑备忘/工具调用技巧/额度管理），照做即可满血接力。
> 历史交接记录已归档 git 历史（`git show G-113:HANDOFF.md`），WORKLOG.md 有全部任务索引。

**快照时间**: 2026-11-09 凌晨（**WorkBuddy 主控接棒，全量盘点后校准**）｜**刷新者**: 主控（WorkBuddy，银橙AI 会话）｜**仓库**: `C:\Users\1\Desktop\zcode\GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni

## ⚡ 最新（2026-11-09 04:40，接管盘点 + 数字校准）

- **看板真实口径**：**132 行全 done / 0 doing / 0 todo**（旧文档写 130 是 11-04 的快照；A-AI-01/A-AND-09/A-AI-02/A-AND-10 早已闭环，**不要再派**）。
- **测试真家底（本轮实测）**：原生 **349 用例 / 778,723 断言全绿**（我自己跑的 `zrender_test.exe`）；EditMode **1099** + PlayMode **85**（01:53 五段报告 ALL PASS）；合计 **1533 用例**。架构校验 **26 asmdef / 0 违规**。
- **git 现场**：HEAD `be3091d`，工作区干净，**领先 origin/main 18 个提交未推**（A-AND-10 触屏包、A-AI-02、A-AND-09、A-AI-01、G-136~G-140 治理与交接手册）。
- **文档漂移已修（G-141）**：README 的 103 任务/916/48/311/25 asmdef、TASKS 行内统计 114、A-R9-18 空 commit、ROADMAP 方向四 doing/todo 全部对齐实测值。
- **盘点新发现（文档此前未记录，见下「渲染体检」）**：色彩空间是 **Gamma** 而 URP+HDR+Bloom 按线性工作；Medium/High 开 HDR 但全工程无 Tonemapping override；`Art/Textures` **0 张位图**、动画 0、预制体 5。

## 三十秒速览

| 项 | 值 |
|---|---|
| 任务看板 | **132 done / 0 doing / 0 todo**（TASKS.md） |
| 测试家底 | 原生 **349 / 778,723 断言** + EditMode **1099** + PlayMode **85** ≈ **1533 用例**；三校验器 exit 0 |
| 代码规模 | C# 530 文件 / 86,755 行；C++ 175 文件 / 77,106 行（生产 27k + 测试 34k） |
| 可交付物 | `Build/Windows64`（81.8MB Mono 包）、`Build/Installer/ZFrame-Setup-0.1.0.32.exe`（24MB）、`Build/Android/ZFrame.apk`（IL2CPP，minSdk 22，Vulkan→GLES3） |
| 管线 | Unity 2022.3.62f3c1 + **URP 14.0.11**（活跃）；ZRender 自研管线为可切换的第二管线（D3D12 门控） |
| 最新里程碑 | 三技术切片全 PASS；AI 深水区（记忆/态度/协作/人群 LOD）收官；Android 触屏打磨完成 |

## 一句话现状

**功能侧无未闭环任务**：双轨（玩法框架 + 原生渲染内核）全部里程碑收口，Windows/Android 都能出包。**下一棒不是补欠账，是选方向**——技术侧最值钱的三件事在「下一步优先级」，内容侧最大短板是零贴图零动画。

## 渲染体检（2026-11-09 主控只读扫描，未改任何渲染资产）

| 项 | 实测 | 判据 |
|---|---|---|
| 活跃管线 | `GraphicsSettings.m_CustomRenderPipeline` = `URP_Medium.asset`；包内只有 URP 14.0.11 | ZRender 需手动切换 |
| 三档映射 | QualitySettings 三档 → Low/Medium/High 三个 URP asset；MSAA 1/2/4，阴影距离 20/40/60，软阴影仅 High；三档 `m_UseSRPBatcher:1` | 映射正确 |
| 材质/Shader | 10 个 `.mat` **全部**引用 URP/Lit（guid `933532a4…`）；自定义 shader 仅 `ZRenderBlit.shader` | 无粉红风险 |
| 后处理链 | Low **无任何 override**；Medium 只有 Bloom；High = Bloom + ColorAdjustments；**全工程无 Tonemapping** | ⚠ 见缺陷 1/2 |
| 色彩空间 | `m_ActiveColorSpace: 0` = **Gamma** | ⚠ 见缺陷 1 |
| 深度/不透明纹理 | `m_RequireDepthTexture:0` / `m_RequireOpaqueTexture:0`；RendererFeatures 空 | 做 SSAO/软粒子前必须开 |
| 内容资产 | 贴图 **0**、动画 **0**、预制体 5（4 UI + 1 测试立方）、模型 25 个 FBX、音频 7 | 玩法只能靠运行时生成几何 |

**三个待裁决的渲染缺陷（我不擅自改，因为会动画面）**：
1. **Gamma 色彩空间 + URP**：URP 官方要求 Linear。当前 HDR + Bloom 在 Gamma 下混合，光照与泛光都不物理；改 Linear 会整体变亮/对比变化，需要你点头并做像素对照。
2. **HDR 开但无 Tonemapping**：Medium/High `m_SupportsHDR:1`，没有任何 Tonemapping override → 高光直接裁剪。要么补 Tonemapping（Neutral/ACES），要么关 HDR 保持三档一致。
3. **三档后处理不连贯**：Low 什么都没有、Medium 只有 Bloom、High 多一个 ColorAdjustments——降档时画面会跳，建议按「Low=无后处理 / Medium=Bloom+Tonemap / High=再加 ColorAdjustments」重排。

## 接管者动作序列

1. `git log --oneline -5` + `git status`（应干净）＋确认 `git rev-list --count origin/main..HEAD`（当前 18）
2. 读 `Docs/LLM-HANDOFF-NOTES.md`（操作手册）+ `Docs/交接档案.md`（环境坑/技术债）
3. 先跑一次基线再动手：`./Native/build/bin/Release/zrender_test.exe --success=false`（应 349/778,723）；Unity 五段用 `Tools/run_tests.ps1 -Segments editmode,playmode,arch,config,naming`
4. 从「下一步优先级」选一项 → 写简报 → 派施工 → 派**不同**验收 → commit → 刷新三件套

## 下一步优先级（按价值/成本排序，均无需新硬件）

| # | 事项 | 现状 | 代价 |
|---|---|---|---|
| 1 | 渲染三缺陷裁决（Gamma / Tonemapping / 三档后处理） | 已定位，未改 | 小，但动画面，需用户点头 |
| 2 | SRP Batcher 与 DrawCall 可测化（A/B 测量，替代 Frame Debugger 目测） | 只有开关，无数字 | 中，PlayMode 用例可自证 |
| 3 | 内容管线起步（贴图/材质/动画） | 0 贴图 0 动画 | 大，决定「像不像游戏」 |
| 4 | 游戏本体（星球探索 + 装配生存） | 技术全备，无设计 | 大 |
| 5 | Windows IL2CPP 正式包 | 当前 Mono | 小，一条命令 + 验证 |
| 6 | 推 18 个提交 / token 轮换 | 待用户确认 | 极小 |

## 环境纪律速查（每条真实踩过，详见交接档案 §3）

- **Unity 批处理**：单实例（查进程+删 `UnityProject/Temp/UnityLockfile`）｜僵死（轮询 xml，5 分钟有果即 `taskkill //PID //F`）｜跑测试**勿带 -quit**｜证据以 xml 为准
- **子代理派发**：并发/额度限制 → 退避 5-10 分钟重试；停滞 10 分钟 → 查现场续派或轻量代理复核收尾（ADR-006）
- **代理变量重复**（HTTP_PROXY+http_proxy）崩 MSBuild——构建前按交接档案 §3.2 清理
- **Tuanjie Hub**：无头 CLI 不认（Android 子模块已用外部工具链绕行，勿重试）；unity.cn 页面是 SPA（shell 抓不到直链）
- **网络**：github raw 可能重置→REST API；含 % 中文勿用 python % 格式化（用 Edit 工具）
- **Android 工具链（已装配勿重装）**：`C:/Users/1/Android/`——JDK11/cmdline-tools 19.0/platform-tools/build-tools 34.0.0/NDK r23b；EditorPrefs 键名实测：`AndroidSdkRoot`/`AndroidNdkRootR23B`/`Jdk11Path`（硬校验 JDK major==11）
- **zrender.dll**：构建产物入库（ADR-004），提交前 `git checkout --` 丢弃非任务 M 噪声

## 待办备忘

- **18 个提交未推**（推远程前必须用户确认，AGENTS.md 铁律）；CI token 轮换仍成立（用户给的令牌与仓内凭据逐字节相同，非新令牌）
- `Tools/android_support_installer.exe` 456MB 已在 .gitignore（未入库），可删本地副本
- 仓库 515MB：FSR 两个 DLL 占 65MB、`Docs/Parity` PNG 约 20MB——ADR-004 允许，但 clone 成本高
- `TestPanelView` 仍随包（技术债 §5）；`run_tests.ps1` BOM 可顺手清
- CI 只跑 L1（native+static），Unity 层注释在 ci.yml 文末——**云端绿 ≠ Unity 绿**，别把 CI 当全量证据

## 最近任务日志

- 触屏打磨 A-AND-10（PlayMode 85 / EditMode 1099）、AI 队友 A-AI-02（1082）、人群 LOD A-AND-09、NPC 记忆 A-AI-01
- 全部历史：WORKLOG.md（132 任务索引）

## 下一个接管者

一切以磁盘为准：AGENTS.md → HANDOFF（本文件）→ TASKS.md → 交接档案 → LLM-HANDOFF-NOTES。写简报→派施工→派验收→闭环刷新。祝顺利。
