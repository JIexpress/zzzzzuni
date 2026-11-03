# AGENTS.md —— 接管本项目的任何 AI 代理或人类必读

> 本文件是平台中立的指令文件（ZCode / workbuddy / DeepSeek harness / 人类均适用）。
> 项目一切权威状态在磁盘上，不依赖任何对话记忆。

## 项目是什么

**ZFrame / ZRender**：Unity 2022.3 LTS 上的双轨游戏框架。
- **Track A（ZFrame）**：玩法框架——分层架构（Core/Data/Gameplay/View/Editor，asmdef 隔离）、事件总线、服务定位器、对象池、资源/配置/存档、UI 框架、设置界面。
- **Track B（ZRender）**：自研渲染内核——C# 自研 SRP 壳 + 原生 C++ 核心（数据内核 SoA+128位掩码、手写 RenderGraph、Vulkan 语义 RHI 多后端、bindless、FSR4.1/3.1 超分、DLSS5 留路径）。

总体设计：`Docs/框架设计方案.md`（v2）。里程碑与验收标准也在其中 §9。

## 接管的前三步（强制顺序）

1. 读 **HANDOFF.md**（唯一权威状态快照：当前进度、活跃任务、环境命令、注意事项）
2. 读 **TASKS.md**（任务看板）。若有 `doing` 状态任务：先 `git status` + `git log --oneline -5` + 该任务的 `Docs/Logs/TASK-*.md`，判断续做或重派
3. 按 HANDOFF 的"如何继续"行动。正常流程：从 TASKS.md 领 `todo` 任务

## 角色与铁律

- **主控**：拆任务/写任务简报/派发/验收裁决/git 提交。**不写业务代码**
- **施工代理**：按简报实现+自测+写施工报告到任务日志。**不 git commit**
- **验收代理**：与施工不同的代理，独立跑验收清单，出 PASS/FAIL+证据
- 施工与验收**必须是不同代理**；FAIL 重派上限 3 轮，超限挂起标记人工介入
- **一任务一 commit**，格式 `TASK-XXX: 摘要`；**禁止** force push、改历史 commit、推远程前确认
- 每个任务的日志（`Docs/Logs/TASK-XXX.md`）四节齐全：任务简报/施工报告/验收清单/验收结论，缺一节=验收不通过
- 每任务闭环后必须刷新 HANDOFF.md 与 TASKS.md、WORKLOG.md

## 环境与命令（Windows，Git Bash / PowerShell）

| 项 | 值 |
|---|---|
| 仓库根 | `C:\Users\1\Desktop\zcode\GameFramework` |
| Unity 2022.3.62 | `C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe` |
| CMake | 4.4.3（全局 PATH） |
| MSVC/MSBuild | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools`（vcvars64.bat 在其 VC/Auxiliary/Build/） |
| git | 2.55（身份已设仓库级，勿改） |

常用命令：
```bash
# Unity 批处理（编译检查/跑测试），首次较慢
"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" -logFile -
# 原生构建（zrender.dll + 单测）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1
# 原生单测
Native/build/bin/Release/zrender_test.exe
# 一键全部测试 —— 【尚不存在】Tools/run_tests.ps1 是 FINAL-01 的交付物，勿照抄执行
# 当前做法：build_native.ps1（原生）+ Unity -runTests（EditMode）+ 架构校验器，三步分别跑
```

## 文件规范（详见 Docs/Standards/file-conventions.md）

- UTF-8 **无 BOM**、LF 换行（.ps1/.bat 例外 CRLF）；.editorconfig/.gitattributes 已强制
- 任务 ID：`A-M0-01`（A 轨）/`B-R2-03`（B 轨）/`G-01`（治理）
- 不动：`Library/ Temp/ obj/ Native/build/`（构建产物，不入库）；不手改 Unity 自动生成的 .meta

## 停下来问人的情况

- 验收 3 轮 FAIL；需要安装 >2GB 软件；需要花钱/账号；改 DECISIONS.md 里已定的决策；对外发网络请求（开源依赖下载除外，仅限 https 且校验域名）
