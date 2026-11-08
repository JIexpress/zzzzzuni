# HANDOFF —— 项目当前状态快照

> 唯一权威状态源。接管者先读本文件，再读 `Docs/交接档案.md`（路线图+环境坑+技术债），再读 TASKS.md。
> ⚡ **给下一个 LLM 主控**：先读 \`Docs/LLM-HANDOFF-NOTES.md\`——132 个任务沉淀的全部操作手册（派发模板/变异验证法/故障处置手册/技术坑备忘/工具调用技巧/额度管理），照做即可满血接力。
> 历史交接记录已归档 git 历史（`git show G-113:HANDOFF.md`），WORKLOG.md 有全部任务索引。

**快照时间**: 2026-11-04 深夜（**主控交棒**）｜**刷新者**: 主控（ZCode，GLM 会话）｜**仓库**: `C:\Users\1\Desktop\zcode\GameFramework`｜**远程**: github.com/JIexpress/zzzzzuni（CI 绿）

## ⚡ 最新（2026-11-04 深夜，A-AI-01/A-AND-09 闭环）

- **A-AI-01 NPC 记忆与态度 PASS**：记忆条目（容量 32 滚动）→半衰期聚合→态度（Fear/Anger/Trust）→**调制效用触点**（恐惧→逃跑增益/愤怒→反击/信任→Follow，行号 NpcUtilityAgent.cs:566-640）；**个性×态度联动**：同事件胆小 NPC 逃跑 vs 勇敢 NPC 反击（锚点实证+变异复红）。1031 用例。
- **A-AND-09 人群四档 LOD PASS**：硬上限 const 锁死（L0≤20/L1≤50/L2≤512）+距离阶梯迟滞+切换无跳变（1e-4）+L2 billboard 跟随+L3 边界文档+kernel 单一数据源（每帧单次游标读，零逐实体 P/Invoke）；变异复红。PlayMode 80/80。
- 测试家底更新：原生 349/778,723 + EditMode **1031** + PlayMode **80**。

## 三十秒速览

| 项 | 值 |
|---|---|
| 任务看板 | **130 done / 0 doing / 0 todo**（TASKS.md） |
| 测试家底 | 原生 **349 用例 / 778,723 断言** + EditMode **983** + PlayMode **71** ≈ **1400 用例全绿**；三校验器 exit 0；CI GitHub Actions 绿 |
| 一键全量 | `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`（八段）；一键出包 `build_player.ps1`（Windows）/ `build_android.ps1`（Android） |
| 最新里程碑 | **三技术切片全 PASS**：自主 NPC 效用 AI（3 NPC 自主分化）、模块化装配（ConfigurableJoint 调参+10 零件稳定）、行星尺度（浮点原点重定位抖动消除 24,576×+球面重力轨道 Python 逐位复算）；**Android 真机可玩**、CI 云端 Vulkan 全量 349/349 |

## 一句话现状

**全部既有承诺清零**：方案 17 项+增强轮全交付（需求达成对照 Docs/需求达成对照.md 覆盖率 100%）；三大新技术方向切片全过（Docs/TECH-ROADMAP.md，含用户绑定约束：人物精度四档 L0≤20/L1≤50/L2 面片/L3 播片过场）。工作区干净、本地=origin（差 1-2 个 docs 提交待推）。**两个新任务简报就绪待派**（见下）。

## 接管者动作序列

1. `git log --oneline -5` + `git status`（应干净）
2. 读 `Docs/交接档案.md`（环境坑每条真实踩过）+ `Docs/TECH-ROADMAP.md`（三方向+用户约束档位表+未来方向池）
3. **立即派发两个就绪任务**（简报已写好，直接派）：
   - **A-AI-01**：NPC 记忆与态度系统（`Docs/Logs/TASK-A-AI-01.md`——简报在 Docs/TECH-ROADMAP.md 方向四，派发前把简报补成本文件结构：三节+验收清单 V1-V5）
   - **A-AND-09**：人群四档 LOD 管线（同上，方向四任务拆解表）
4. 治理循环：施工代理→**不同**验收代理→PASS 后 commit→刷新三件套→governance commit

## 下一步优先级（简报就绪度）

| # | 任务 | 简报状态 |
|---|---|---|
| 1 | A-AI-01 NPC 记忆与态度 | **简报就绪**（ROADMAP 方向四，派发时转正为独立日志） |
| 2 | A-AND-09 人群四档 LOD 管线 | **简报就绪**（同上） |
| 3 | A-TECH-04/05、B-TECH-04 二阶段 | **简报就绪**（Docs/Logs/TASK-*-04/05.md 已写好待派） |
| 4 | A-AI-02 AI 队友协作（GOAP 社交协调） | 待写简报 |
| 5 | C/D 类未来方向池 | 见 ROADMAP 尾部（等用户定向或排期） |

## 环境纪律速查（每条真实踩过，详见交接档案 §3）

- **Unity 批处理**：单实例（查进程+删 `UnityProject/Temp/UnityLockfile`）｜僵死（轮询 xml，5 分钟有果即 `taskkill //PID //F`）｜跑测试**勿带 -quit**｜证据以 xml 为准
- **子代理派发**：并发/额度限制 → 退避 5-10 分钟重试；停滞 10 分钟 → 查现场续派或轻量代理复核收尾（ADR-006）
- **代理变量重复**（HTTP_PROXY+http_proxy）崩 MSBuild——构建前按交接档案 §3.2 清理
- **Tuanjie Hub**：无头 CLI 不认（Android 子模块已用外部工具链绕行，勿重试）；unity.cn 页面是 SPA（shell 抓不到直链）
- **网络**：github raw 可能重置→REST API；含 % 中文勿用 python % 格式化（用 Edit 工具）
- **Android 工具链（已装配勿重装）**：`C:/Users/1/Android/`——JDK11/cmdline-tools 19.0/platform-tools/build-tools 34.0.0/NDK r23b；EditorPrefs 键名实测：`AndroidSdkRoot`/`AndroidNdkRootR23B`/`Jdk11Path`（硬校验 JDK major==11）
- **zrender.dll**：构建产物入库，提交前 `git checkout --` 丢弃非任务 M 噪声

## 待办备忘

- 本地 HEAD 领先 origin/main 1-2 个 docs 提交待推（下次 push 带上）
- `run_tests.ps1` BOM（HEAD 既有）可顺手清；`TestPanelView` 随包、接缝符号未擦（交接档案 §5）
- 真机装的是 Mono 开发包——演示前出 IL2CPP 正式包（一条命令）

## 最近任务日志

- 技术切片三件：TASK-A-TECH-01/02、B-TECH-03（全部 PASS，见 WORKLOG）
- 二阶段四件简报：TASK-A-TECH-04/05、B-TECH-04（就绪）
- 全部历史：WORKLOG.md（130+ 任务索引）

## 下一个接管者

一切以磁盘为准：AGENTS.md → HANDOFF（本文件）→ TASKS.md → 交接档案。写简报→派施工→派验收→闭环刷新。祝顺利。
