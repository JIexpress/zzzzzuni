# TASKS.md —— 任务看板

> 状态: todo / doing / blocked / done / held(人工介入)。**一行一任务，改状态就改原行**（禁止追加新行替代旧行——2026-10-25 曾因此产生 3 组重复行，见 G-45）。
> 规则: 领任务改 doing 并在简报路径处建日志; 闭环填 commit。详见 AGENTS.md。

| ID | 轨道/里程碑 | 标题 | 状态 | 简报路径 | commit |
|---|---|---|---|---|---|
| G-01 | 治理 | 治理文档基座+git init+首提交 | done | — | f781cfb |
| A-M0-01 | A/M0 | Unity 工程创建与骨架 | done | Docs/Logs/TASK-A-M0-01.md | 0af8ab8 |
| A-M0-02 | A/M0 | EventBus 强类型事件总线+单测 | done | Docs/Logs/TASK-A-M0-02.md | 1562649 |
| A-M0-03 | A/M0 | Services 服务定位器+ObjectPool+单测 | done | Docs/Logs/TASK-A-M0-03.md | 98ab884 |
| A-M0-04 | A/M0 | GameLoopDriver+Logger+GameRoot 启动 | done | Docs/Logs/TASK-A-M0-04.md | 2135d50 |
| A-M0-05 | A/M0 | 调试面板 v1(FPS/Console)+架构校验器 | done | Docs/Logs/TASK-A-M0-05.md | cab51c4 |
| A-M1-01 | A/M1 | ResourceService+IAssetProvider+池联动 | done | Docs/Logs/TASK-A-M1-01.md | 22a6701 |
| A-M1-02 | A/M1 | ConfigSystem SO+CSV+校验 | done | Docs/Logs/TASK-A-M1-02.md | 35f3485 |
| A-M1-03 | A/M1 | SaveSystem 原子写/备份/迁移 | done | Docs/Logs/TASK-A-M1-03.md | e0f65f0 |
| A-M2-01 | A/M2 | SceneLoader 异步加载/Payload/InputBlocker | done(验收PASS 7/7) | Docs/Logs/TASK-A-M2-01.md | 8ca8d23 |
| A-M2-02 | A/M2 | AudioService 四总线/交叉淡化 | done | Docs/Logs/TASK-A-M2-02.md | d41a634 |
| A-M2-03 | A/M2 | URP 接入+PipelineManager 档位矩阵 | done | Docs/Logs/TASK-A-M2-03.md | 7306087 |
| A-M3-01 | A/M3 | UI框架 MVP+分层+面板栈 | done | Docs/Logs/TASK-A-M3-01.md | 63b6600 |
| A-M3-02 | A/M3 | 设置系统+选项界面五页签 | done | Docs/Logs/TASK-A-M3-02.md | a4708c9 |
| A-M3-03 | A/M3 | 输入重绑定(InputSystem) | done(验收PASS 7/7；361用例) | Docs/Logs/TASK-A-M3-03.md | 见下条治理提交 |
| A-M3-04 | A/M3 | 移动键四向重绑定收尾(复合轴 W/A/S/D) | done(验收PASS 6/6；372用例) | Docs/Logs/TASK-A-M3-04.md | 见下条治理提交 |
| A-M4-01 | A/M4 | InteractSystem+输入桥接+FSM敌人 | done(验收PASS 7/7；466用例) | Docs/Logs/TASK-A-M4-01.md | e438a36 |
| B-R6-03 | B/R6 | 超分设置实接(Unity Bindings+视频页Enum) | done(验收PASS 7/7；489用例) | Docs/Logs/TASK-B-R6-03.md | 0433961 |
| A-M4-02 | A/M4 | 垂直切片收口+PlayMode冒烟+Demo | done(验收PASS 7/7；EditMode 489+PlayMode 5) | Docs/Logs/TASK-A-M4-02.md | 5246293 |
| A-M5-01 | A/M5 | 编辑器工具集(工作台/UI校验/命名检查) | done(验收PASS 7/7；542用例) | Docs/Logs/TASK-A-M5-01.md | 见下条 |
| B-R0-01 | B/R0 | CMake 原生构建基座+zrender.dll | done | Docs/Logs/TASK-B-R0-01.md | 3c63b41 |
| B-R0-02 | B/R0 | 自研 SRP 壳+Unity 加载 dll(桥接验证) | done | Docs/Logs/TASK-B-R0-02.md | a98e3d6 |
| B-R1-01 | B/R1 | DataKernel 掩码/SoA/查询+基准 | done | Docs/Logs/TASK-B-R1-01.md | 17177c6 |
| B-R2-01 | B/R2 | RHI 核心语义层(类型/句柄/屏障模型) | done | Docs/Logs/TASK-B-R2-01.md | 876d5d5 |
| B-R2-02 | B/R2 | D3D12 后端:设备/交换链/清屏+窗口 | done | Docs/Logs/TASK-B-R2-02.md | f7daeac |
| B-R2-03 | B/R2 | D3D12 资源/描述符/bindless/时间戳 | done | Docs/Logs/TASK-B-R2-03.md | 6425281 |
| B-R3-01 | B/R3 | RenderGraph 声明/编译/别名/屏障合并(mock) | done | Docs/Logs/TASK-B-R3-01.md | 3e57a67 |
| B-R3-02 | B/R3 | RenderGraph 接真D3D12(split落地/别名真复用) | done | Docs/Logs/TASK-B-R3-02.md | edb66a6 |
| B-R4-01 | B/R4 | 资产烘焙 Mesh→SoA .zmesh 双侧 | done | Docs/Logs/TASK-B-R4-01.md | a422666 |
| B-R4-02 | B/R4 | 共享可见性+sortkey+绘制列表(radix/合批) | done | Docs/Logs/TASK-B-R4-02.md | 2e2c667 |
| B-R4-03 | B/R4 | PSO+首绘(RG驱动真渲染cube) | done | Docs/Logs/TASK-B-R4-03.md | c0bd8a6 |
| B-R5-01 | B/R5 | 簇光源+PBR 前向(多光源真渲染) | done(验收PASS 7/7) | Docs/Logs/TASK-B-R5-01.md | 170ad0b |
| B-R5-02 | B/R5 | CSM 级联阴影+可证伪对比 | done | Docs/Logs/TASK-B-R5-02.md | 695babb |
| B-R5-03 | B/R5 | SSR+GI探针+NPR(R5收官) | done | Docs/Logs/TASK-B-R5-03.md | 3338829 |
| B-R5-04 | B/R5 | 对等闸门:URP vs ZRender 同场景对比 | done(闸门通过) | Docs/Logs/TASK-B-R5-04.md | cfbb372 |
| B-R6-01 | B/R6 | FSR SDK 接入+UpscalerChain 框架 | done(验收PASS 7/7) | Docs/Logs/TASK-B-R6-01.md | 58e265a |
| B-R6-02 | B/R6 | FSR4.1本机实测+DLSS骨架+C ABI(纯原生) | done(验收PASS；FSR4本机不可用=受限如实) | Docs/Logs/TASK-B-R6-02.md | 01c0e83 |
| B-R7-01 | B/R7 | Vulkan 第二后端(前置:装Vulkan SDK) | done(验收PASS with受限；160用例) | Docs/Logs/TASK-B-R7-01.md | 见下条 |
| B-R7-02 | B/R7 | Vulkan路径接线收尾(lights/shadows/r5/fsr) | done(验收PASS 7/7;2/4接线如实,fsr受限/r5未接) | Docs/Logs/TASK-B-R7-02.md | f5099fe |
| FINAL-01 | 收口 | 一键测试+README+差异附录 | done(验收PASS 5/5)——**项目阶段收口** | Docs/Logs/TASK-FINAL-01.md | 3a79c65 |
| H-01 | 收口后修复/A | C# 健壮性：重绑定失败软锁/settings原子写/UI栈泄漏/HUD泄漏 | done(验收PASS with受限——未编译；H-04) | Docs/Logs/TASK-H-01.md | 71c82f2(合并) |
| H-02 | 收口后修复/B | 原生防御：簇网格容量/FSR Resize上限/Present HRESULT/Bilinear泄漏 | done(验收FAIL 1项→H-05返工；H-03) | Docs/Logs/TASK-H-02.md | 71c82f2(合并) |
| H-03 | 收口后修复/验收 | 原生与工具链独立验收（含主控越界施工部分） | done(FAIL：Vulkan失败计数无人读) | Docs/Logs/TASK-H-03.md | 71c82f2(合并) |
| H-04 | 收口后修复/验收 | C# 侧独立验收（含主控越界施工部分） | done(PASS with受限) | Docs/Logs/TASK-H-04.md | 71c82f2(合并) |
| H-05 | 收口后修复/返工 | 修 H-03/H-04 验收发现项（Vulkan可观测性/容量自洽/bindless槽/契约注释） | done(验收PASS with受限；168用例/705077断言；H-05) | Docs/Logs/TASK-H-05.md | 71c82f2(合并) |
| A-R8-01 | A/R8 | FrameGovernor 动态分辨率+画质阶梯 | done(验收PASS 6/6；588用例) | Docs/Logs/TASK-A-R8-01.md | 9d2a029 |
| B-R8-01 | B/R8 | Vulkan r5 接线(双后端全对等) | done(验收PASS 6/6；170用例) | Docs/Logs/TASK-B-R8-01.md | 6c8d85a |
| A-R8-02 | A/R8 | Addressables 切换验证 | done(验收PASS 5/5；601+6用例) | Docs/Logs/TASK-A-R8-02.md | 6fcc17b |
| A-R8-03 | A/R8 | 行为树框架+敌人 AI 升级 | done(验收PASS 6/6；651用例) | Docs/Logs/TASK-A-R8-03.md | 450a5d3 |
| B-R8-03 | B/R8 | DataKernel 架型迁移+chunk 收缩 | done(验收PASS 6/6；180用例/710633断言) | Docs/Logs/TASK-B-R8-03.md | be2adcf |
| B-R8-02 | B/R8 | 帧生成 FFX FG 集成+能力实证 | done(验收PASS 6/6;超预期:核显被接受,real60→gen120)——**R8 全部完成** | Docs/Logs/TASK-B-R8-02.md | d27445d |
| G-63 | 治理/工具链 | 六段测试回归修复：ctest WORKING_DIRECTORY 钉仓库根 + build_native Set-Location | done(验收PASS 6/6;六段 exit 0;190用例/710800断言) | Docs/Logs/TASK-G-63.md | e5c6c84 |
| G-65 | 治理/工具链 | 测试健壮性：消除 CWD 依赖(exe 目录推导)/删除兜底/报错可读 | done(验收PASS 7/7；CWD 复位含失败路径) | Docs/Logs/TASK-G-65.md | e1a59b8 |
| G-66 | 治理/工具链 | 收口 G-65 残留：另两处删除点 + stale 证据真伪判定 | done(验收PASS 7/7；假 xml 独占锁实测拒读) | Docs/Logs/TASK-G-66.md | 7cc1050 |
| A-R9-01 | A/R9 | 视频页接 FrameGovernor：动态分辨率+帧率目标(含冷启动生效根因修复) | done(验收PASS 8/8；editmode 663) | Docs/Logs/TASK-A-R9-01.md | 91d031e |
| A-R9-02 | A/R9 | DRS 残留自愈：写前落盘恢复日志，崩溃后不留脏 renderScale | done(验收PASS 第3轮；editmode 676=663+13) | Docs/Logs/TASK-A-R9-02.md | 5a8fcb1 |
| A-R9-03 | A/R9 | 清 A-R9-02 遗留：回滚失败保留条目 / `Save` 失败不整份删 | done(验收PASS 8/8；editmode 678=676+2) | Docs/Logs/TASK-A-R9-03.md | 1ff4fbe |
| A-R9-04 | A/R9 | 后处理真生效：配置齐但画面零效果（相机缺 UniversalAdditionalCameraData） | done(验收PASS with受限 10/10；PlayMode 8) | Docs/Logs/TASK-A-R9-04.md | 8feee7e |
| A-R9-05 | A/R9 | 粒子系统运行期创建(零资产/池化/像素证据) | done(验收PASS;Player像素证据验收亲测补齐) | Docs/Logs/TASK-A-R9-05.md | 8beab3e |
| B-R9-01 | B/R9 | FG 的 C ABI + Unity 接线（DXGI swapchain hook 路线） | done(**spike 门禁 STOP**；交换链路线可行，卡在 depth/MV + 插件惰性加载) | Docs/Logs/TASK-B-R9-01.md | f7e5d1e |
| B-R9-02 | B/R9 | Unity(URP/D3D12) 下 depth/MV 可获取性与正确性 | done(验收PASS with受限；native 190→196；遗留F2必办) | Docs/Logs/TASK-B-R9-02.md | 3c329ab |
| B-R9-03 | B/R9 | dxgi.dll 代理 + FG 联调（Unity Player 真插帧） | done(验收PASS with受限；A/B硬门禁过，C未达) | Docs/Logs/TASK-B-R9-03.md | 见下条 |
| B-R9-04 | B/R9 | 双交换链接管 + prepare/present 对齐（FG 阶段 C 收口） | done(验收PASS with受限 8/8；0.943/1:1/2.0x) | Docs/Logs/TASK-B-R9-04.md | 1403186 |
| B-R9-05 | B/R9 | 安装竞态守卫 + 非 D3D12 门控（修阻断级启动崩溃） | done(验收PASS 10/10；20轮 patches恒1零崩溃) | Docs/Logs/TASK-B-R9-05.md | 0a0928f |
| B-R9-06 | B/R9 | FG 落地接线：每帧提交 depth/MV + 设置页开关（真生效） | done(验收PASS with受限 10/10；MSAA未告知→R9-07) | Docs/Logs/TASK-B-R9-06.md | ad2cb64 |
| B-R9-07 | B/R9 | FG 收尾：MSAA 告知与恢复 + 采集常驻 + 干净基线 | done(验收PASS with受限；V2/V3 PARTIAL→R9-08) | Docs/Logs/TASK-B-R9-07.md | 4686e13 |
| B-R9-08 | B/R9 | 相机深度开关真还原 + 还原行为有牙测试 | done(验收PASS with受限 7/7；变异2红有牙) | Docs/Logs/TASK-B-R9-08.md | 2af50dd |
| B-R10-01 | B/R10 | 交付包干净化：剥离测试夹具 + 收敛测试接缝 | done(验收PASS with受限；Player零夹具/706用例) | Docs/Logs/TASK-B-R10-01.md | 1c4a6a9 |
| R10-02 | 跨轨 | 独立代码审计（测试绿灯照不到的缺陷） | done(验收"部分可信"：17条,复核8条0误报) | Docs/Logs/TASK-R10-02.md | 6a912a1 |
| R10-03 | B/R10 | 修复审计P1（原生批：vtable槽/交换链尺寸/转发参数/Vulkan错误） | done(验收PASS 6/6；222用例/711,190断言) | Docs/Logs/TASK-R10-03.md | 9aa17b8 |
| R10-04 | A/R10 | 修复审计P1（C#批：UI 淡入 Task 软锁） | done(验收PASS 5/5,715+7用例) | Docs/Logs/TASK-R10-04.md | bbda420 |

| R10-05 | B/R10 | 修复审计P2原生批(8条) | done(验收PASS with受限 8/8；251用例/711,498断言；三处越界追认条件成立) | Docs/Logs/TASK-R10-05.md | 06a3660 |
| R10-06 | A/R10 | 修复审计P2 C#批(A-9相机还原) | done(验收PASS with受限 6/6；EditMode 719) | Docs/Logs/TASK-R10-06.md | fa671ba |
| R10-07 | B/R10 | 补审计漏项(A-10 split屏障 + B-6 采集泄漏) | done(验收PASS with受限 10/10；native 253/711,537) | Docs/Logs/TASK-R10-07.md | 98d377b |
| A-ANIM-02 | A/ANIM | 动画扩展一：跳跃接真实垂直位移（Grounded 真值 + 控制器 Idle→Jump/Jump→Run） | done(验收PASS；纯函数弧线峰值0.7500/落地差0.0000；1102+88用例；变异双复红) | Docs/Logs/TASK-A-ANIM-02.md | d938565 |
| A-ANIM-03 | A/ANIM | 动画扩展二：脚步 IK（斜坡/台阶贴地，平地零干预） | done(主控实跑；1103+89 全绿；25° 斜板权重>0.3、平地<0.05；变异复红 sha256 还原) | Docs/Logs/TASK-A-ANIM-03.md | 待填 |
| A-CUT-01 | A/CUT | L3 播片过场（固定机位 + VideoPlayer，人群切 L3） | done(主控实跑；1104+90 全绿；真解码帧号推进；变异复红 sha256 还原；L3 契约不变) | Docs/Logs/TASK-A-CUT-01.md | 待填 |
| A-CONT-01 | A/CONT | 内容管线起步：程序化贴图套件 + 环境材质升级 + 内容守卫 | done(主控实跑；1106+90 全绿；细节量 +56%；地面/道具上贴图) | Docs/Logs/TASK-A-CONT-01.md | 42f5a26 |
| A-LOOP-01 | A/LOOP | 最小可玩循环：守住火堆 90 秒（唯一动词=守） | done(主控实跑；1111+91 全绿；纯逻辑状态机+波次曲线+耐久+HUD/重开；变异复红 sha256 还原) | Docs/Logs/TASK-A-LOOP-01.md | ddfb03c |
| A-LOOP-02 | A/LOOP | 战斗闭环：敌人血量/受击 + 玩家攻击 + 击杀计数 | done(验收PASS 6/6；1113+93；变异复红 sha256 还原；背后攻击为设计边界) | Docs/Logs/TASK-A-LOOP-02.md | 4394a2d |
| A-ASM-03 | A/ASM | 装配玩法化①：建造模式 + 吸附连接 + 蓝图存档（进 SCN_Game） | done(验收PASS 6/6；1113+98；变异复红 sha256 还原；既有装配类零改动) | Docs/Logs/TASK-A-ASM-03.md | 2dba3c7 |
| A-WORLD-01 | A/WORLD | 双精度世界坐标 + 浮点原点（4000m 重定位） | done(验收PASS 7/7；1127+101；变异复红 sha256 还原；100km 处 float ULP 7.8mm vs double 4e-11m) | Docs/Logs/TASK-A-WORLD-01.md | fc08132 |
| A-AI-03 | A/AI | 可供性扫描：Affordance 注册/容量/预算 + 接入效用打分 | done(验收PASS 7/7；1140+102；变异复红 sha256 还原；既有 AI 四文件零改动) | Docs/Logs/TASK-A-AI-03.md | ccb5897 |
| A-AI-04 | A/AI | 可供性接场景：真实锚点注册 + NPC 走过去占用 | done(验收PASS 7/7；1140+110；变异复红 sha256 还原；修自占用抖动；默认零影响) | Docs/Logs/TASK-A-AI-04.md | 89f3497 |
| A-ART-01 | A/ART | 换掉抽象小人：接入 Mixamo Vanguard 人形（Humanoid/Idle-Run-Walk/装甲材质） | done(1140+110 全绿；SCN_Game/SCN_Anim 已换；白模已补装甲配色) | Docs/Logs/TASK-A-ART-01.md | 待填 |
| A-R9-19 | A/URP | 色彩管线迁移（Gamma→Linear + HDR 两档 Tonemapping） | done(主控实跑验收；Linear 下 1102+88 全绿；ZRender 桥改 UNORM 视图；两档重新分层差1.48x；灯光接受新基准未重标定) | Docs/Logs/TASK-A-R9-19.md | 82daf86 |
| A-AI-05 | A/AI | NPC 自主模式（G 键开关）+ 预约态（到达才占） | held(暂缓：简报已写、未动工；用户进入正式游戏开发，此功能后置。无半成品代码，恢复时按简报直接派施工) | Docs/Logs/TASK-A-AI-05.md | — |

**统计**：done 144 ／ doing 0 ／ todo 0 ／ blocked 0 ／ held 1（2026-11-09：A-AI-05 登记为 held——简报已写未动工，暂缓后置）

> **2026-10-28 G-64 文档对齐（接管）**：修三处失真 ①R8 六行原被追加在「里程碑对照」标题**下方**导致表格断裂、`B-R8-03` 仍挂 `doing`（实际已 PASS，commit `be2adcf`）——已并入主表并改正状态
> ②`HANDOFF.md` 表头仍写「R8 推进 3/4、B-R8-02 待派」（实际 47 任务全闭环）③`README.md` 与 HANDOFF 注意事项里的任务数/用例数/`TestReport.txt` 是否入库均过期。
> 同时新增 G-63（接管时实跑六段发现的真实回归）。

> **H 轨闭环（2026-10-26）**：H-01/H-02 施工 → H-03(原生,FAIL 1项)/H-04(C#,PASS with受限) 验收 →
> H-05 返工 → 第三轮验收 PASS with 受限。最终原生基线 **168 用例 / 705,077 断言 / 0 failed / 零警告**。
>
> **遗留建议（第三轮验收 §3，不阻塞，记录备改）**：
> 1. `test_zrhi_device_stats.cpp:57-60` 在无 Vulkan 机器上 `MESSAGE` 后 `return`，用例仍计 2 passed（护栏无声失效）
>    → 建议改 `REQUIRE(device != nullptr)` 或显式 skip
> 2. `descriptorSetAllocFailureCount` 目前无人消费（窗口程序已轮询 `presentFailureCount`），上层待接入
> 3. `SaveSystem.cs` 类注释把 "unreadable" 与 "renamed to .corrupt_" 并列，而 `IOException` 分支只记日志不 quarantine
> 4. `bindings[9]` 上界依赖 `CreatePipelineState` 的 Release 静默 return（非法 `srvTableCount>2` 时无日志），与 D3D12 同策

> 2026-10-26 收口后独立审查追加 H 轨（Hotfix）。其中 4 项由**主控越界直接施工**（违反
> AGENTS.md「主控不写业务代码」），已交 H-03/H-04 独立验收补上程序正义：
> D3D12 描述符槽越界、Vulkan 描述符池规范违规+分配失败静默、SaveSystem 持久化/备份回退、
> 构建脚本环境健壮性。
> **验收口径**：原生与脚本部分已独立实跑取证（构建零警告、166 用例/705,052 断言、双后端
> maxDelta=0）；**C# 部分因本机 Unity 不可用（UPM IPC + Bee cache-folder）仅完成静态验收，
> 未编译、未运行任何用例**——不得表述为"实测通过"。H-03 判 FAIL 的 1 项由 H-05 返工。

## 里程碑对照

Track A: M0(骨架)→M1(数据)→M2(表现)→M3(UI+设置)→M4(交互切片)→M5(工具+测试)
Track B: R0(基座)→R1(数据内核)→R2(RHI+D3D12)→R3(RenderGraph)→R4(剔排批)→R5(光照阴影+对等闸门)→R6(超分+治理)→R7(Vulkan+收口)
Track B(续): R8(愿望清单前 4 项落地：FrameGovernor / Vulkan r5 / Addressables / 行为树 / 帧生成 / DataKernel 架型)

## R9 候选（未承诺排期，按用户 2026-10-28 选择保留）
设置页接 FrameGovernor+FG 开关 ｜ 粒子/后处理 Volume 系统 ｜ 构建管线+CI ｜ GPU 工具链+自动跑分回归
| A-R9-06 | A/R9 | 粒子接入玩法(事件驱动火花) | done(验收PASS 5/5;像素证据4.9x门限) | Docs/Logs/TASK-A-R9-06.md | ce73325 |
| A-R9-07 | A/R9 | 粒子事件源补全(门/对话/死亡) | done(验收PASS 5/5) | Docs/Logs/TASK-A-R9-07.md | 604d94b |
| B-R9-09 | B/R9 | Vulkan 交换链真重建(R10-03遗留) | done(验收PASS 5/5) | Docs/Logs/TASK-B-R9-09.md | cab268c |
| B-R9-10 | B/R9 | 自动跑分回归(基线门禁) | done(验收PASS 5/5;270用例) | Docs/Logs/TASK-B-R9-10.md | df10295 |
| A-R9-08 | A/R9 | Volume 权重渐变(风格切换动画) | done(验收PASS 5/5;31帧单调渐变) | Docs/Logs/TASK-A-R9-08.md | 01bc1da |
| B-R9-11 | B/R9 | VK 离屏 readback 全零(复现/根因/修复) | done(验收PASS 5/5;272用例,门禁7指标) | Docs/Logs/TASK-B-R9-11.md | 767351d |
| A-R9-09 | A/R9 | 行为树死亡事件统一(BT 补发) | done(验收PASS 5/5) | Docs/Logs/TASK-A-R9-09.md | 712eaad |
| B-R9-12 | B/R9 | DataKernel 并行 Each(工作分块) | done(验收PASS 5/5;平台瓶颈论证独立复现) | Docs/Logs/TASK-B-R9-12.md | e98b6ac |
| A-R9-10 | A/R9 | 建模接入实战:SCN_Game 布置+Demo 换代 | done(验收FAIL→轻量更正轮→终验PASS 5/5 闭环) | Docs/Logs/TASK-A-R9-10.md | b66f608 |
| B-R9-13 | B/R9 | ZRender 资产画廊(4人物 zmesh) | done(验收PASS 5/5；281用例/771,162断言) | Docs/Logs/TASK-B-R9-13.md | 54c6c83 |
| A-R9-11 | A/R9 | 修复 Dropdown 模板无效错误(用户实报) | done(验收PASS;785用例) | Docs/Logs/TASK-A-R9-11.md | df2cbd1 |
| B-R9-14 | B/R10 | 审计遗留清零(A-10 split/B-6 泄漏) | done(重复派发判定——已由 R10-07 闭环 98d377b) | Docs/Logs/TASK-B-R9-14.md | 840d489 |
| B-R9-15 | B/R10 | Player 构建管线正式化 | done(验收PASS 5/5;八段套件) | Docs/Logs/TASK-B-R9-15.md | 840d489 |
| A-AND-01 | A/AND | Android 构建管线(APK+logcat冒烟) | done(构建部分被A-AND-02覆盖;真机logcat冒烟并入遗留——需手机连机) | Docs/Logs/TASK-A-AND-01.md | |
| A-AND-02 | A/AND | 外部工具链装配+首个APK(绕过Hub) | done(验收PASS;APK双包产出) | Docs/Logs/TASK-A-AND-02.md | f01fdde |
| A-AND-03 | A/AND | Android 黑屏诊断(文件日志+真因修复) | done(验收PASS+门控收尾;真机菜单/游戏双实证) | Docs/Logs/TASK-A-AND-03.md | 61f04bc |
| A-AND-04 | A/AND | 触屏虚拟摇杆+屏上交互键（含 EventSystem 跨场景修复） | done(验收PASS with受限 8项;EditMode 797/PlayMode 20;真机实证推杆位移) | Docs/Logs/TASK-A-AND-04.md | fcc59ad |
| A-AND-05 | A/AND | 补 EventSystem 换场景存活回归用例（A-AND-04 超范围修复的缺口） | done(验收PASS 5/5;变异±双方各做一遍;PlayMode 21) | Docs/Logs/TASK-A-AND-05.md | 2d5664a |
| A-DEV-01 | A/DEV | Editor 常驻本地服务（编译/测试/日志/截图/菜单 秒级反馈） | done(验收PASS with受限 13/14；服务 6.0s vs 批处理 44.5s) | Docs/Logs/TASK-A-DEV-01.md | 5f98291 |
| A-DEV-02 | A/DEV | Dev Server 返工：playmode 结果拿不到 + gameview 截图分支 | done(验收PASS 10/10；playmode 21/21、gameview 真出图) | Docs/Logs/TASK-A-DEV-02.md | 7efc198 |
| A-DEV-03 | A/DEV | 运行期 DevConsole（三层开口之 B：localhost TCP 白名单命令） | done(验收PASS with受限 12/12；PlayMode 33) | Docs/Logs/TASK-A-DEV-03.md | b7c5432 |
| A-DEV-04 | A/DEV | DevConsole 硬化：收紧 API 面 + 限流配额 + 六条测试脆弱性 | done(验收PASS with受限 10/10；release 零钩子/PlayMode 36) | Docs/Logs/TASK-A-DEV-04.md | 1d5ac14 |
| A-AUDIT-01 | A/AUDIT | 第二轮独立代码审计（测试绿灯照不到的缺陷） | done(9条:P0×1/P1×3/P2×5;P0为DevServer本地攻击面) | Docs/Logs/TASK-A-AUDIT-01.md | 6a094e6 |
| A-AUDIT-02 | A/AUDIT | 修审计 A-1/A-2/A-3（DevServer安全+并发上限+触屏去重） | done(验收PASS；恶意Origin→403/PlayMode 38) | Docs/Logs/TASK-A-AUDIT-02.md | b488073 |
| A-AUDIT-03 | A/AUDIT | 修审计余项 A-4/B-1/B-3/B-5（UIRoot重复canvas/存档原子性/EventSystem持久性/DevConsole超时语义） | done(验收PASS；四条变异全红后还原；EditMode 798/PlayMode 41) | Docs/Logs/TASK-A-AUDIT-03.md | e495ebc |
| A-AND-06 | A/AND | IL2CPP 正式包重出（ARM64 可对外，替换 Mono 开发包） | done(验收PASS；真机补测通过:主菜单→进游戏渲染/无调试红字/二次进摇杆仍在) | Docs/Logs/TASK-A-AND-06.md | 9da8db9 |
| A-AND-07 | A/AND | 发布元数据：versionCode 自增 + 修版本倒挂（0.1.0.19 < 旧包 0.1.0.22） | done(验收PASS 6/6；守卫有牙;EditMode 811) | Docs/Logs/TASK-A-AND-07.md | 2b7e927 |
| A-AND-08 | A/AND | Release 签名证书（本地自签 keystore，替换 CN=Android Debug） | done(验收PASS 6/6；CN=ZFrame/v2/密钥隔离0命中/缺密码中止) | Docs/Logs/TASK-A-AND-08.md | 88275f0 |
| A-CI-01 | A/CI | CI 接入（GitHub Actions L1 原生门禁 + pre-push 大文件钩子） | done(CI #4 绿 1m4s；云端 249 用例/764,341 断言；pre-push 钩子有牙) | Docs/Logs/TASK-A-CI-01.md | 50d4e21 |
| A-GPU-01 | A/GPU | 性能 HUD（DrawCall/SetPass/三角面/显存/FPS，含 release 门控） | done(独立验收 PASS with 受限；HUD 自身 14/14、V1 未目测 F3；PlayMode 唯一失败归因并行任务) | Docs/Logs/TASK-A-GPU-01.md | a2a2c9e / 2d80a85 |
| A-R9-12 | A/R9 | CommandSystem 命令模式+拾取撤销接线 | done(验收PASS;变异2处复红) | Docs/Logs/TASK-A-R9-12.md | fd010cb |
| B-R9-16 | B/R9 | GPU 工具链(HUD/RenderDoc/RG导出) | done(验收PASS;301用例) | Docs/Logs/TASK-B-R9-16.md | 724e123 |
| B-R9-17 | B/R9 | DataKernel C ABI+C# 绑定(玩法通道) | done(验收 PASS V1–V5；12 个 ZK_* 导出/游标迭代/变异复红；原生 311、EditMode 873) | Docs/Logs/TASK-B-R9-17.md | 2a848da |
| A-R9-14 | A/R9 | 音频打磨包(SFX 入库+事件接线) | done(返工 1 轮后独立验收 PASS with 受限；PlayMode 44/44/0；验收亲自复做变异；BGM URL 缺口如实登记) | Docs/Logs/TASK-A-R9-14.md | c09f1ef |
| A-R9-13 | A/R9 | 千人演练场：DataKernel 玩法侧规模化(混合架构) | done(验收 PASS with 受限；EditMode 880/PlayMode 46；验收亲自复做变异 46/46/0→46/45/1→46/46/0；V3 群体可见默认关受限) | Docs/Logs/TASK-A-R9-13.md | 5f49a42 |
| A-R9-15 | A/R9 | 千人演练场开箱可见 + 像素回归证据 | done(验收 PASS with 受限；PlayMode 48；真实像素差 3.97%；验收亲眼复核两图并复做变异；GPU DrawCall 仍受限) | Docs/Logs/TASK-A-R9-15.md | c378f49 |
| B-UNITY-01 | B/UNITY | ZRender 接入 Unity 第一步：原生接受外部 D3D12 设备 + 最小渲染 ABI | done(验收PASS；导出23→28；清屏回读整帧(13,26,38)；311用例) | Docs/Logs/TASK-B-UNITY-01.md | f1067bd |
| B-UNITY-02 | B/UNITY | ZRender 接入 Unity 第二步：共享纹理 + 管线接线（方案B，避开backbuffer状态/跨队列） | done(验收PASS with受限；画面已出现在Unity窗口;颜色变异证伪;317用例) | Docs/Logs/TASK-B-UNITY-02.md | 57e14c2 |
| B-UNITY-03 | B/UNITY | 可玩级第一刀：DrawRenderers（相机矩阵 + Unity 物件搬进 ZRender） | done(验收PASS with受限；位置(693,421)逐像素吻合；mesh只传12次；T-pose/标签为内容侧) | Docs/Logs/TASK-B-UNITY-03.md | 06f61bd |
| B-UNITY-04 | B/UNITY | 集成稳定性：矩阵/顶点单测 + 纹理重建泄漏 + 帧同步双缓冲 | done(验收PASS；9条单测有牙；WaitIdle→双缓冲阻塞0/720;纹理计数恒2) | Docs/Logs/TASK-B-UNITY-04.md | 2d5e954 |
| B-PSO-01 | B/PSO | PSO 磁盘缓存（ID3D12PipelineLibrary 落盘 + 失效重建 + 兜底） | done(验收PASS；76.7ms→2.9ms -96.2%；17进程无AV;坏缓存全识别) | Docs/Logs/TASK-B-PSO-01.md | 4feffbd |
| A-PKG-01 | A/PKG | Windows 安装包（Inno Setup，24MB，静默安装已验证） | done(实测exit0;167文件;Burts已排除;卸载器在) | Docs/Logs/TASK-A-PKG-01.md | — |
| X-HYGIENE-01 | X/HYG | 缺口卫生清理：体积口径 + gpu_hud 语义 + PSO 两条尾巴 | done(验收PASS with受限；Player 81.92MB分项;gpu_hud判文档错;PSO顺序+刷盘) | Docs/Logs/TASK-X-HYGIENE-01.md | 36e55a0 |
| X-SEC-01 | X/SEC | DevConsole 认证 + rg.export 路径限定（默认拒绝） | done(验收PASS；token常量时间实测;五种路径攻击全拒;332用例) | Docs/Logs/TASK-X-SEC-01.md | 0d4e115 |
| B-UNITY-05 | B/UNITY | 共享纹理整面回读只有 1/8 行正确（根因 + 修复） | done(验收PASS with受限；根因=跨对象状态跟踪;A/B/C/D/E五组对照;334用例) | Docs/Logs/TASK-B-UNITY-05.md | 2d1254d |
| X-RGDESC-01 | X/DIAG | rg.export 的 Imported 资源导出真实 desc | done(根因=ZrImageDesc默认就是1x1无法判"未填";加hasImageDesc;产品路径三参+诊断路径成员resolver;实测 BGRA8/D32Float 640x480) | — | 09437ef |
| A-R9-17 | A/URP | URP 变体剥离真实收益（变体数 + 构建时长对照） | done(验收PASS with受限；0收益 7830→7830;21条规则结构性冗余;发现官方开关m_StripUnusedPostProcessingVariants=0) | Docs/Logs/TASK-A-R9-17.md | ea7df8e |
| A-R9-18 | A/URP | 打开 m_StripUnusedPostProcessingVariants（已落地为 1） | done(验收PASS with受限；变体7830→7096 -9.4%;构建284.9→238.5s -16.3%;包体-175KB;Bloom/FinalPost/Particles未动;画面无可见回归;Android未验) | Docs/Logs/TASK-A-R9-18.md | f766716 |
| A-R9-16 | A/R9 | URP 变体收敛（12288 变体成本中心） | done(superseded by A-R9-17/18)(验收 PASS with 受限；按降级出口交付；EditMode 916；14 是规则命中数非变体减少；真实构建收益未验证) | Docs/Logs/TASK-A-R9-16.md | acef951 |
| A-TECH-01 | A/TECH | 自主 NPC 效用AI 最小切片 | done(验收PASS;928用例) | Docs/Logs/TASK-A-TECH-01.md | 83c277d |
| A-TECH-02 | A/TECH | 模块化装配系统最小切片 | done(验收PASS with受限4项) | Docs/Logs/TASK-A-TECH-02.md | a44e7af |
| B-TECH-03 | B/TECH | 行星尺度:浮点原点+球面重力切片 | done(验收PASS;Python独立复算) | Docs/Logs/TASK-B-TECH-03.md | da5bc78 |
| A-TECH-04 | A/TECH | 效用AI二阶:个性权重+GOAP原型 | done(验收PASS;960+65用例) | Docs/Logs/TASK-A-TECH-04.md | 81c3cbe |
| A-TECH-05 | A/TECH | 装配二阶:装配图+序列化+零件扩展 | done(验收PASS;变异复红) | Docs/Logs/TASK-A-TECH-05.md | a8af2f5 |
| B-TECH-04 | B/TECH | 球面地形chunk+LOD+大气渐变 | done(验收PASS;6144chunk seams=0) | Docs/Logs/TASK-B-TECH-04.md | b27804e |
| A-ANIM-01 | A/ANIM | 人形骨骼动画:AI驱动的状态机+IK(用户需求) | done(验收PASS;含A-TECH-04并行产物) | Docs/Logs/TASK-A-ANIM-01.md | 81c3cbe |
| A-AI-02 | A/AI | AI 队友协作(GOAP社交协调层) | done(验收PASS;变异复红) | Docs/Logs/TASK-A-AI-02.md | fec368f |
| A-AND-10 | A/AND | 触屏打磨:浮动摇杆/多点/触觉/参数化 | done(验收PASS;85+1099用例) | Docs/Logs/TASK-A-AND-10.md | 044c827 |
| G-158 | 治理 | U盘交接材料入仓(Docs/Planning四文档)+Tools/Probes探针家底转移+双机分工登记(本机=引擎机/对端=游戏机) | done | Docs/Planning/ | (本提交) |
| B-FSR-01 | B/FSR | P0修复:FSR4探测传空设备假阴性——枚举适配器后建真设备再问(改进计划P0/08对照#16) | done(验收PASS:11P+1MISSING按裁决,0FAIL;350用例全绿;负对照成立=硬件真相;阳性移交B-PROBE-01对端补验) | Docs/Logs/TASK-B-FSR-01.md | ae03f62 |
| B-FSR-02 | B/FSR | P1能力三态诚实化(probeMode)+全仓写死能力表面审计(demo/设置页/失实注释)(改进计划P1) | done(验收PASS V1-V10;ApiVersion2;审计15项8改7核;真机probeMode=0实证) | Docs/Logs/TASK-B-FSR-02.md | ce4bdf4 |
| B-PROBE-01 | B/PROBE | fsr4-probe探针:真设备/null对照可复核工具+本机存档(改进计划§5) | todo | Docs/Logs/TASK-B-PROBE-01.md | |
| B-PROBE-02 | B/PROBE | linear-bridge探针:ZRender vs URP同机位亮度差数字化(改进计划§5) | todo | Docs/Logs/TASK-B-PROBE-02.md | |
| A-CONF-01 | A/CONF | Config CSV→C#行类型代码生成器(08对照#11半项补全) | done(验收PASS V1-V10;四通道;EditMode 1167全绿;架构26/0;08对照#11改判已实现) | Docs/Logs/TASK-A-CONF-01.md | d15faad |
| A-CONF-02 | A/CONF | 表类薄壳+注册行自动化(连表类也零手写;A-CONF-01遗留§6.1) | todo | (简报待写) | |
| X-NAMES-01 | X/HYG | 资产命名规则扩容(T_/ENV_ 前缀)或 T_ENV_*.png 改名——清 6 处存量红灯(A-CONT-01 42f5a26 引入) | done(验收PASS N1-N5;naming段0违规;负例不放宽) | Docs/Logs/TASK-X-NAMES-01.md | 42e981f |
| B-KERNEL-01 | B/KERNEL | 组件定位周期实测断言+文档BMI2/POPCNT表述纠正(08对照#7) | done(验收PASS K1-K7变异亲做;实测4~5/5,目标3未达如实记;353全绿) | Docs/Logs/TASK-B-KERNEL-01.md | fcbcb95 |
| B-PLANET-02 | B/PLANET | 行星ABI头落地(zplugin.h+def+ApiVersion 2→3+Bindings声明+ERR_ARGS注释同步) | done(验收PASS P1-P7;§14↔头文件零签名偏离;接口冻结) | Docs/Logs/TASK-B-PLANET-02.md | f5e6aa1 |
| B-VIS-01 | B/VIS | 多视图共享可见性核实:B-R4-02交付范围审计,裁决是否需补实现(08对照#4) | done(裁决A:需求已满足,VisibilitySet一次录入六视图消费;注记:真frustum剔除缺→B-VIS-02) | Docs/Logs/TASK-B-VIS-01.md | (本提交) |
| B-VIS-02 | B/VIS | 真视锥剔除纯函数CullToViewMask+帧内录入/消费计数断言(不挡路,核实任务衍生加固) | todo | Docs/Logs/TASK-B-VIS-02.md | |
| B-PLANET-01 | B/PLANET | P2拆解+行星ABI设计稿(double原点/chunk句柄/LOD邻居/高度采样)(改进计划P2) | done(验收FAIL一轮→修订R1→复验PASS;F1-F7全落地;46导出裁决正确;D8接受) | Docs/Logs/TASK-B-PLANET-01.md | 80d82bc |
| B-PLANET-02 | B/PLANET | 行星ABI头落地(zplugin.h+def+ApiVersion 2→3+Bindings声明+ERR_ARGS注释同步) | todo | (简报待写) | |
| B-PLANET-03 | B/PLANET | 行星数学生产化(planet_math §①③④提升,语义零改动) | todo | (简报待写) | |
| B-PLANET-04 | B/PLANET | chunk通道生产实现(句柄表/生成/上传/RenderFrame集成/剔除移植/着色器) | todo | (简报待写) | |
| B-PLANET-05 | B/PLANET | SetCameraOrigin+round-trip doctest | todo | (简报待写) | |
| B-PLANET-06 | B/PLANET | SetWorld+SampleHeight(含与chunk顶点逐位一致断言) | todo | (简报待写) | |
| B-PLANET-07 | B/PLANET | Unity托管桥(流式管理器+WorldOrigin接线;首个生产消费者) | todo | (简报待写) | |
| B-PLANET-08 | B/PLANET | 接缝专项(float缝宽实测+neighborMask行为定版) | todo | (简报待写) | |
| B-PLANET-09 | B/PLANET | planet-budget探针(1080p/接缝/贴地扫描/双机) | todo | (简报待写) | |
| B-PLANET-10 | B/PLANET | abi-precision探针(四点ULP双口径) | todo | (简报待写) | |
| B-PLANET-11 | B/PLANET | 编辑器内验收集成(三档截图/1km行走/账目断言) | todo | (简报待写) | |
| B-PLANET-12 | B/PLANET | IL2CPP+Android各一次(Android按D8裁决=托管等效实现) | todo | (简报待写) | |
