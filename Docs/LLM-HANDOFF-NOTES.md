# LLM 交接手册 —— 给下一个 AI 主控/子代理的操作手册

> 本文是 2026-10-24~11-09、132 个任务、三平台（ZCode 主控/workbuddy/DeepSeek）、多次额度中断的**全部经验蒸馏**。
> 配套阅读：AGENTS.md（治理）、HANDOFF.md（状态）、Docs/交接档案.md（环境坑）、Docs/Standards/file-conventions.md（代码规范）。

## 1. 治理体系怎么跑（ proven 流程）

### 1.1 任务生命周期
1. **主控写简报**（Docs/Logs/TASK-<ID>.md）：背景/设计要点（给足实现方向与坑位提示）/约束（白名单文件清单！）/自测要求/**验收清单 V1-Vn（可证伪、带数字阈值）**
2. **派施工代理**（general-purpose）：提示词=必读清单（AGENTS/HANDOFF/简报/参照代码）+严格按简报+自测命令+白名单+"不 git commit"+施工报告四节（摘要/清单/自测数字/风险）+"回传 ≤20 行摘要，长内容进文件"
3. **派验收代理**（必须不同代理）：逐项独立跑验收清单+**变异验证**+行号证据→写验收结论节
4. **主控闭环**：commit（`TASK-XXX: 摘要`）→刷新 TASKS/WORKLOG/HANDOFF→governance commit（`G-NNN: ...`）

### 1.2 变异验证法（本项目的杀手锏，验收必用）
"改回旧值即红"：把修复点临时改回缺陷状态→相关新用例必须变红→逐字节恢复（cmp/sha256 断言）→复跑全绿。这能证明用例**真的测到了修复**，而不是空洞断言。历史上抓住过：transition 落盘丢失、Remove 越位写、key 移位反、互斥放行、看门狗缺失——全靠它。
- 施工方自查 + 验收方独立复做各一次 = 双保险
- 恢复用 sha256/md5 断言逐位一致，别只 git diff（行尾会被 autocrlf 糊弄）

### 1.3 并行代理协调
- Unity 单实例：并行代理必然撞锁。纪律=撞锁等 60s×10 重试 + 轮询对方文件静默窗口
- **跨代理代修**：对方文件编译错误阻塞全项目时，最小修复（如补一行 using）被认可过两次——**必须留痕报告**（语义零改动声明）
- 文件归属分簇提交：并行任务收口时按路径 `git add <各自白名单>` 分笔 commit，构建产物 dll 用 `git checkout --` 丢弃
- 既有测试被并行任务跑红：先判归属（读红用例的断言对象是谁的产物），再隔离复跑（SetUp 隔离/定向过滤），最后语义裁决

## 2. 故障处置手册（全部真实发生过）

| 故障 | 处置 |
|---|---|
| 子代理派发报 `model concurrency limit exceeded`/`exceed quota limit` | 退避 5-10 分钟重试（曾 5 轮后恢复）；**重试前先把状态落盘 HANDOFF**；长期不恢复→交棒（HANDOFF 重写+记忆更新+commit） |
| 子代理停滞（600s 无活动自动取消） | 查现场：tasklist Unity/进程、git status 半成品、TempLogs 证据文件。证据已在盘→派轻量代理"只做代码复核收尾"（ADR-006）；主控可中立跑测试取证，验收代码级复核仍交独立代理 |
| Unity 批处理退出僵死 | 轮询 results.xml——**xml 出现即算完成**（证据有效），进程挂 5 分钟再 taskkill //PID //F；跑测试**勿带 -quit**（会跳过测试）；首轮误加 -quit 致空跑的坑踩过 3 次 |
| Unity 报"another instance running" | tasklist 查 Unity.exe→僵尸 taskkill→删 UnityLockfile→重跑 |
| MSBuild 崩 "已添加项 HTTP_PROXY" | 环境有大小写重复代理变量——构建前按交接档案 §3.2 清理 |
| github raw 连接重置 | 走 GitHub REST API（`api.github.com`）下载，sha256 校验 |
| unity.cn 页面抓不到直链 | 页面是 SPA——用**浏览器**下载（用户可达）；或找 OGA 等直链源 |
| Tuanjie Hub（中国版）无头 CLI 不认 | `install-modules` 报 No editor found——Android 子模块别走 Hub，用外部工具链绕行（见 §4） |
| python 含 % 的中文文案 | `%` 格式化炸 ValueError——用 Edit 工具或避免 % 格式化 |
| 一个任务零代码改动 | 审计类任务发现"已被其他会话修完"=正常（判定重复派发，变异验证既有修复有效即可闭环，不算浪费） |

## 3. 技术坑备忘（代码级，每条都咬过人）

### Unity/UGUI
- **EditMode 下 DestroyImmediate 不回调 OnDestroy**——需要确定性析构就做显式 `Shutdown()` 接缝（先例：GameRoot/AudioService/GameHud）
- **EditMode 下 Awake 不触发**——组件初始化提取 `Initialize()` 公共 seam
- UGUI Dropdown 必须有 Template→Viewport→Content→Item(Toggle) 结构——程序化创建用 `DefaultControls.CreateDropdown`
- Canvas SetParent 会被 Unity **静默转 WorldSpace**——SetParent 后必须重申 renderMode（影响所有程序化 UI 挂载）
- URP 相机缺 `UniversalAdditionalCameraData` 组件时 `renderPostProcessing=false`——"配置齐但画面零效果"的典型根因；**先证伪再修**（像素差分对比）
- `SceneManager.sceneLoaded` 与 `LoadSceneAsync` 直连是两条不同链——事件门控假设要实测
- Kenney 头骨 transform.forward 与身体前方差 **-134°**——Look-at 断言须先 IK-off 标定视线向量
- uGUI transition 程序化创建不走 `AddTransition(dest,false)` 重载会在 SaveAssets **丢 destination/conditions**

### C++/原生
- NSIS 安装器 `/S /D=<路径>`：/D 必须**最后且不加引号**；写 Program Files 需要提权（沙箱内提权会被静默拒→让用户双击）
- D3D12_BOX 字段序是 (left,top,front,right,bottom,back)——写反会设备移除
- 质量**消失**系统（燃料燃烧减质量）ΔP=F·t 不成立——判据改包络 (F·t/m_start, F·t/m_end)
- ConfigurableJoint 贴面间隙 0 落在 contactOffset 内必抖——DockGap 1.5cm；"软关节"偏置焊接受单侧冲量必自旋=正确动力学，别当 bug 修
- Unity 转 FBX：容器声明 Y-up 但几何可能 Z-up（躺倒）——烘焙前必查 Mesh.bounds 与站立修正表
- **负视口下 VK frontFace 要 CW**（与 D3D12 FCC=FALSE 同向）——双后端绕序对照必测
- VK 离屏路径无 AcquireNextImage→帧闸 CanSubmit 恒 false→**Submit 被静默跳过**——离屏必须 SetSwapchainAttached
- EmitBarriers 超 128 静默截断——截断必须告警+计数

### Android
- Unity 2022.3 **硬校验 JDK major==11**（17 被拒）；EditorPrefs 键名实测：`AndroidSdkRoot`/`AndroidNdkRootR23B`/`Jdk11Path`
- cmdlinetools 必须 `cmdline-tools/<版本号>/` 布局（latest 不被认）；新版 23.0 CLI 有分号参数拆分缺陷→用 19.0
- 华为/鸿蒙 ROM **抑制第三方应用 logcat**——诊断靠 ProjectMenu（*#*#2846579#*#*）开 AP logs，或**游戏内文件日志**（DeviceFileLogger，长期资产）
- 质量**消失**的动量：燃料烧掉带走动量——ΔP 判据改包络
- 真 Unity 黑屏排查顺序：①logcat 无 Unity 输出→文件日志 ②SCN_Boot 无相机=灰屏正常（启动链查日志）③Canvas renderMode 被 SetParent 静默改

### 通用
- **性能测试的安静护栏**：机器带载（FinalShell/并行任务）会令时序断言假红——`check_machine_quiet.ps1` 先跑；带载下拒采（BUSY→SKIP）
- 基准数字跨机解读谨慎（T2 扩展比 1.26~2.27x 离散大）——**平台瓶颈用探针夹逼归因**（内存带宽饱和探针+计算密集对照近线性）
- 像素证据的门限要给数字依据（0.010/0.005 这类），并且**先证伪**（修前跑一遍确认门限可区分）

## 4. 工具调用技巧

- **run_tests.ps1**：八段全量；`-Segments editmode` 单段跑要经 `powershell -Command`（-File 不拆逗号数组）；`BENCH_GATE=1` 才门禁；`PLAYER_BUILD=1` 才出包
- **adb**：`exec-out screencap -p > x.png`、`run-as com.ZFrame.ZFrame cat <path>`（内部沙箱日志）、`logcat -d --pid=<pid>`；华为 logcat 抑制→DeviceFileLogger 文件日志；真机黑屏排查三步（§3 Android）
- **dumpbin**：`C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\<ver>\bin\Hostx64\x64\dumpbin.exe //exports <dll>`
- **GitHub Actions 核对**：`curl api.github.com/repos/<owner>/<repo>/actions/runs?per_page=N`（公开仓匿名可查；私有需 token）
- **后台哨兵模式**：文件/目录出现轮询（`for i in seq; do sleep N; if [ -f target ]; then break; fi; done`）——等下载/等安装/等用户操作时挂上，完成自动通知
- **大文件下载**：curl 断点续传循环（`-C -` + 重试 40 次）+ 完成后 SHA256 校验入 SOURCE.md
- **Blender 无头转换**：`blender --background x.blend --python-expr "import bpy; bpy.ops.export_scene.fbx(filepath='out.fbx')"`——.blend→.fbx 解锁无 FBX 模型
- **模拟触控**（PlayMode）：Input System 按钮用 `PressAndRelease`，摇杆用 pointer 模拟+画布缩放坐标换算（循环断言陷阱：用画布缩放非循环值）
- **PNG 逐像素对比**：`ReadPixels`+自写 diff（brighter/meanAbs/darker 三口径+门限）——修前必红口径的标配

## 5. 派发提示词模板（直接抄改）

```
你是 ZFrame/ZRender 项目的【施工子代理】，任务 <ID>：<标题>。
仓库根：C:\Users\1\Desktop\zcode\GameFramework
必读（按序）：1. AGENTS.md 与 HANDOFF.md（环境纪律节——Unity 批处理纪律必须遵守：清锁/轮询 xml/taskkill/跑测试勿带 -quit；GitBash 纪律；与并行代理共用 Unity：撞锁等 60s×10 重试） 2. Docs/Logs/<ID>.md（简报=唯一需求来源） 3. 现有代码：<参照文件> 4. Docs/Standards/file-conventions.md
执行要求：严格按简报实现：<要点>；自测：<命令+预期>；约束：白名单 <路径>、不 git commit、UTF-8 无 BOM/LF、架构校验器 exit 0；完成后施工报告四节写入日志（头部施工:填 general-purpose 施工子代理）；回传 ≤20 行摘要，长内容进文件
```
验收版：把"施工"换"验收（独立亲证/变异复做/不采信施工结论）"，产出=验收结论节+逐项证据。

## 6. 额度管理

- 主控上下文告急的信号：用户提醒/长对话——**立刻转纯派发模式**（简报写透→派发→收摘要→闭环），产出全落盘
- 交棒三件套：HANDOFF 重写（状态+接管序列）→ 记忆更新 → commit
- 子代理也有独立配额池——主控额度紧张时子代理反而能跑
- 新模型上线后本对话可能重新可用：届时读本文件+HANDOFF 即满血复活

## 7. 未来方向（拆解到任务级，见 HANDOFF"下一步优先级"）

按优先级：触屏摇杆已完成→**游戏本体设计**（星球探索+装配生存概念，消费全部技术）→ L3 播片过场（VideoPlayer+固定机位，管线已留位）→ 动画扩展（Jump 接线/脚步 IK/程序化层）→ DataKernel 规模化收尾（英雄侧接入）→ GPU 工具链 HUD 接 Unity 侧（B-R9-16 原生三件已有，Unity 调试面板读取即可）→ DLSS/FSR4.1 真机（等硬件）→ CI 云端 EditMode/PlayMode（需 Unity 云授权）→ Metal 后端 → itch/Steam 发布
