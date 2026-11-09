# WORKLOG —— 任务日志索引（倒序）

> 每任务闭环后由主控追加一行。详情见各任务日志。

| 日期 | 任务 | 结果 | commit |
|---|---|---|---|
| 2026-11-09 | A-AI-05 NPC 自主模式+预约态 | held（暂缓）：简报已写、零半成品代码；用户进入正式开发后置，恢复按简报 §五派施工 | — |
| 2026-11-09 | A-AI-04 可供性接场景 | done：SceneAffordances 真实锚点 + AffordanceSeeker 走过去占用；验收 PASS 7/7，1140+110 | 89f3497 |
| 2026-11-09 | A-AI-03 可供性扫描 | done：Affordance/Registry(容量/预算/最近可用)+Action 接效用；验收 PASS 7/7，1140+102 | ccb5897 |
| 2026-11-09 | A-WORLD-01 双精度世界 | done：WorldPosition/WorldOriginMath + WorldOrigin(4km 重定位)；100km float ULP 7.8mm vs double 4e-11m；验收 PASS 7/7，1127+101 | fc08132 |
| 2026-11-09 | A-ASM-03 建造模式 | done：B 开关/QE 五件/吸附连接/右键删除/K-L 蓝图存档；验收 PASS 6/6，1113+98 | 2dba3c7 |
| 2026-11-09 | A-LOOP-02 战斗闭环 | done：敌人 2 血/受击闪白 + J 扇形攻击 + 击杀计数；验收 PASS 6/6，1113+93 | 4394a2d |
| 2026-11-09 | A-LOOP-01 最小可玩循环 | done：守火堆 90s（纯逻辑状态机/波次曲线/耐久/IMGUI HUD/重开）；1111+91 全绿，变异复红 | ddfb03c |
| 2026-11-09 | A-CONT-01 内容管线 | done：6 张程序化可平铺贴图 + 7 材质挂图（底色归一白）+ 地面换草地材质；内容守卫 2 例；细节量 +56% | 42f5a26 |
| 2026-11-09 | A-CUT-01 L3 片源+简报 | 片源入库（Blender Workbench 3s/640×360）+过场简报（门闸/播片/分支改造/V1-V5） | 657f06c |
| 2026-11-09 | A-ANIM-03 脚步 IK | done：中性踝高学习+坡度/落差权重纯函数+双脚射线贴合；1103+89 全绿，斜板 0.3+/平地 0.05-，变异复红 | 0d65dd3 |
| 2026-11-09 | A-R9-19 色彩管线迁移（P2-P5） | done：Gamma→Linear + 两档 Tonemapping、后处理重新分层（1.48x）、ZRender 桥 UNORM；Linear 1102+88 全绿；灯光接受新基准 | 82daf86 |
| 2026-11-09 | A-R9-19 P1 ZRender 桥 Linear 兼容 | done(P1)：共享纹理改线性视图，Linear 全量 EditMode 1102/1102、dxgi 报错 0、Gamma 回归 1102+88；typeless 方案证伪留 P1-b | b70cc38 |
| 2026-11-09 | A-ANIM-02 跳跃接真实垂直位移 | done：施工→验收 PASS（纯函数弧线 v0=√2gh/T=2v0/g、Grounded 真值、控制器 Idle→Jump/Jump→Run、无二段跳；1102+88 用例，变异双复红 sha256 还原） | d938565 |
| 2026-11-09 | A-AND-10 触屏打磨包 | done：施工→验收 PASS（浮动摇杆/多点/触觉/参数化；1099+85 用例） | 044c827 |
| 2026-11-04 | A-AI-02 AI 队友协作 | done：施工→验收 PASS（1082 用例、变异复红、三桥集成零破坏） | fec368f |
| 2026-11-04 | A-AND-09 人群四档LOD管线 | done：施工→验收 PASS（80/80 用例、硬上限变异复红、kernel 单源实证） | 04e965a |
| 2026-11-04 | A-AI-01 记忆与态度 | done：施工→验收 PASS（1031 用例、同事件异反应锚点、调制公式独立复算） | 079d89c |
| 2026-11-04 | A-CI-02 CI Vulkan 补齐 | done：施工→验收 PASS（云端首轮 SUCCESS 349/349、独立 curl 核对 run）——CI 全绿 | 963c63a |
| 2026-11-04 | A-AND-08 DataKernel 玩法规模化 | done：施工→验收 PASS（989+74 用例、P/Invoke 2003→10/帧、A/B 三样本复核）——DataKernel 最后承诺兑现 | da27f8b |
| 2026-11-04 | B-TECH-04 球面地形+大气 | done：施工→验收 PASS（6144chunk seams=0 逐位、LOD 迟滞、三海拔截图、7.9ms 帧耗）——行星地表雏形 | b27804e |
| 2026-11-04 | A-TECH-05 装配图+序列化 | done：施工→验收 PASS（983+71 用例、变异复红、包络判据独立复核物理正确） | a8af2f5 |
| 2026-11-04 | A-R9-14 音频打磨包 | done：两次接手→续建完成（中性验证 873/873 后收尾）；SFX+BGM 接线 AudioService | (工作树) |
| 2026-11-04 | A-TECH-04 个性权重+GOAP | done：接手续建→验收 PASS（5NPC 分化实证、GOAP 两步链、CFG 表驱动） | (同上) |
| 2026-11-04 | A-ANIM-01 人形骨骼动画 | done：施工→验收 PASS（AI 驱动状态机+Look-at IK；Kenney 骨骼动画；非播片） | (同上) |
| 2026-11-04 | B-TECH-03 行星尺度切片 | done：施工→验收 PASS（抖动消除 24,576x、轨道数值 Python 逐位复算） | da5bc78 |
| 2026-11-04 | A-TECH-02 模块化装配切片 | done：施工→验收 PASS with受限 4 项（物理调参核心发现：软关节=正确动力学） | a44e7af |
| 2026-11-04 | A-TECH-01 自主NPC效用AI | done：接手半成品续建→验收 PASS（928 用例、三 NPC 自主分化实证） | 83c277d |
| 2026-11-05 | **G-124 A-R9-16 闭环 + 离线渲染体检 + 数字校准** | done：21 条剥离规则 / 计数工具 / 36 用例入库；主控只读体检（材质 shader 悬空 0、Assets 零位图、SRP Batcher 运行期材质 11 处待目测）；四份治理文档统一到 EditMode 916 / 103 done | G-124（本次治理提交） |
| 2026-11-05 | **A-R9-16 URP 变体收敛** | done：独立验收 **PASS with 受限**，走简报「允许降级出口」。21 条规则（核对三档矩阵后推翻候选表 `_MAIN_LIGHT_SHADOWS_CASCADE`）、纯函数、`IPreprocessShaders`（默认关闭 + 16 白名单 + 可回滚）、`Tools/run_urp_variant_counter.sh`、36 条 EditMode 用例。EditMode **916/916/0**、PlayMode 48、native 311 持平、三校验器 exit 0；验收亲自复做变异 916/916/0→916/912/4→916/916/0。**关键口径**：同命令 stripped `0→14` 是**规则命中数**（73 关键字中命中 21 规则的个数），**不是减少的变体数**；batchmode 无构建 → `IPreprocessShaders` 不触发、变体日志为 0、`ShaderUtil.GetShaderVariantCount` 在 2022.3 不存在。**真实构建变体数/时长对照、剥离器实际删除、12288 重测均列未验证**，未宣称收敛 | acef951 |
| 2026-11-04 | **G-123 A-R9-15 闭环 + 数字校准** | done：群体默认可见 + 首次真实像素证据；README/HANDOFF/TASKS/WORKLOG 统一到 PlayMode 48 / 102 done | G-123（本次治理提交） |
| 2026-11-04 | **A-R9-15 群体开箱可见 + 真实像素证据** | done：查实 A-R9-13「可能污染 A-R9-10 差分」从未量化、实测确会架空差分 → 只在 A-R9-10 用例 SetUp/TearDown 隔离群体，断言/门限一字未动；`AutoEnable` 默认 true + `sceneLoaded` 门控；落盘 `Docs/Parity/crowd_r9_15_off/on.png`，URP 相机 `RenderTexture+ReadPixels` 同轮 A/B，施工 2287/57600=3.97%、验收独立重算 2245/57600=3.90%（阈值 1%），验收用 ViewImage 亲眼复核两图。独立验收 **PASS with 受限**：EditMode 880/880/0、PlayMode **48/48/0**、native 311 持平、三校验器 exit 0；变异 48/48/0→48/47/1→48/48/0。**唯一核心受限**：真实 GPU DrawCall 在 batchmode 只有整场景总值 86、无 A/B，未编数字 | c378f49 |
| 2026-11-04 | **G-121 A-R9-13 闭环 + 数字再校准** | done：A-R9-13 闭环并立 A-R9-15 承接受限项；README/HANDOFF/TASKS/WORKLOG 统一到 EditMode 880 / PlayMode 46 / 101 done | G-121（本次治理提交） |
| 2026-11-04 | **A-R9-13 千人演练场（DataKernel 玩法侧）** | done：3 个 unmanaged 组件 + 单个 `ZKernelWorld` 管 1000 实体，`ITickQueue` Logic 游标迭代 / View 单次 `DrawMeshInstanced`（100 vs 1000 均 1 次提交、URP 兼容、零资产）；独立验收 **PASS with 受限**——EditMode 880/880/0、PlayMode 46/46/0、三校验器 exit 0、native 311 持平；验收亲自复做变异 46/46/0→46/45/1→46/46/0。**受限**：AutoEnable 默认 false 使「群体可见」未开箱达成（用例显式 EnsureCrowd，只证组件/提交层）、真实 GPU DrawCall 与像素未取证、IL2CPP/Android 未验、URP 1023 实例上限未探测 → 立 A-R9-15 | 5f49a42 |
| 2026-11-04 | **G-119 三任务闭环 + 八段基线 + 数字校准** | done：3 个 doing 全部闭环（含 A-R9-14 返工 1 轮）；`run_tests.ps1` 八段 **ALL PASS**（7 passed / 1 skipped，9m57s；native 311/776,716、EditMode 873、PlayMode 44、三校验器 exit 0、bench 7/7）；README/HANDOFF/TASKS/WORKLOG 漂移数字统一到实测口径；立 A-R9-13 简报（待派发）并记录 URP 侧离线体检判据 | G-119（本次治理提交） |
| 2026-11-04 | **A-R9-14 音频实战接线** | done：接手时施工报告空白 + PlayMode 44/43/1 红。补齐六节施工报告 → 派施工返工（根因：auto-boot 单例跨三用例不销毁、TearDown 不复位的共享 AudioService 状态，只改测试 SetUp，产品代码零改动）→ 独立验收 **PASS with 受限**；验收亲自复做变异 44/44/0 → 44/43/1 → 44/44/0；三校验器 exit 0；BGM URL 缺口如实登记不编造 | c09f1ef |
| 2026-11-04 | **A-GPU-01 性能 HUD** | done：独立验收 **PASS with 受限**（V2/V3/V4/V5 PASS，V1 批处理无法目测 F3 如实受限，V3 变异因约束未做、以静态推理 + 门禁用例为绿替代）；HUD 自身 14/14、EditMode 873/873/0；PlayMode 唯一失败经定位属并行任务 A-R9-14 污染，未记为 HUD 缺陷 | a2a2c9e / 2d80a85 |
| 2026-11-04 | **B-R9-17 DataKernel C ABI + C# 绑定** | done：接手时验收 PASS 但代码全部未提交 → 主控按白名单精确分簇提交 30 文件（含本任务产物 `zrender.dll`，未按 §3.7 丢弃）；12 个 `ZK_*` 导出、游标式批量迭代、原生 311/776,716、EditMode 873、定向变异复红 | 2a848da |
| 2026-11-04 | B-R9-16 GPU 工具链 | done：施工→验收 PASS（301 用例、hud/rg.export/renderdoc 三命令实测、变异复红）——§5.7 兑现 | 724e123 |
| 2026-11-04 | A-R9-12 CommandSystem | done：施工→验收 PASS（852 用例、双栈/真实回滚、变异 2 处复红）——原始清单 17/17 满员 | fd010cb |
| 2026-11-03 | **A-AND-08 Release 签名** | done：验收 **PASS 6/6**。debug 证书(CN=Android Debug，不可分发)→本地自签 keystore(CN=ZFrame，v2=true)。keystore/密码在仓库外；环境变量优先+properties 兜底，**缺失即中止且绝不静默退回 debug**（实测移走 properties → BuildPipeline 之前 exit=1，APK 哈希未变）。密钥隔离五路扫描 0 命中。产物 0.1.0.24/versionCode 2。另把两个 debug 签名遗留包归档到 legacy-debug-signed/ 防误分发 | 88275f0 |
| 2026-11-03 | **A-AND-07 版本元数据** | done：验收 **PASS 6/6**。versionCode 改独立字段自增（不从 build 派生——build 被重置过，派生必违反单调，而 versionCode 倒退后是商店端不可逆）+ 读到 ≤1 中止构建的守卫；build 19→**23** 止住倒挂，下次产出 0.1.0.24（>旧包 0.1.0.22）。逻辑抽 `ReleaseVersion.cs`。EditMode 798→**811**、PlayMode 41。守卫有牙由验收独立复做（改回 1 → 干跑 exit 1 中止，单测 12/13 恰守卫那条红） | 2b7e927 |
| 2026-11-03 | **A-AND-06 IL2CPP 正式包** | done：验收 **PASS with 受限**。出 `ZFrame.apk` 36.32MB / arm64-v8a only / versionName 0.1.0.19 / 无 DEVELOPMENT_BUILD，29 分 16 秒（URP 12288 变体 ≈10min + IL2CPP 1418 .o + Gradle）。真机未验（设备未连）。**验收查出两个发布阻塞**：versionCode 写死为 1（商店拒第二次上传）、签名是 CN=Android Debug（不可分发）；另发现版本倒挂 0.1.0.19 < 旧包 0.1.0.22。已立 A-AND-07(todo)/A-AND-08(blocked 待 keystore) | 9da8db9 |
| 2026-11-03 | **A-AUDIT-03 修审计余项** | done：验收 **PASS**。修 4 条——**A-4** UIRoot 幂等判据只信内存字典(domain reload 后必空而子 canvas 存活)→重复建 8 canvas、两套 UI 叠着，改为先从真实子对象重建字典再补缺；**B-1** 存档先移走 main 再写且无 try/catch→写失败丢档，改为写 tmp 成功后才滚动备份+失败重抛；**B-3** EnsureEventSystem 不校验持久性→场景自带 ES 时 A-AND-04 修复复发，改为校验后另建；**B-5** DevConsole 超时后仍执行→选"超时即放弃"使重试安全。四条变异由验收独立复做全红后还原。EditMode 797→**798**、PlayMode 38→**41**、原生 281/771,162 | e495ebc |
| 2026-11-03 | **A-AUDIT-01/02 第二轮审计 + P0 修复** | 审计 9 条(P0×1/P1×3/P2×5)；修前 3 条。**A-1(P0 安全)**：DevServer 默认开启 + 通配 ACAO + `/menu` 直通 `ExecuteMenuItem` → 任意网页一个 form POST 即可让 Editor 执行任意菜单项并读走 Console；根因是"验收用不带 Origin 的 curl"在方法上不可见。修：Host 只认本机 / Origin 三态 / 去通配 ACAO / `/menu` 收紧，验收亲手打攻击请求验到 403。**A-2** 补并发队列上限(8/64)。**A-3** 触屏控件 `Destroy` 延迟窗口去重 → 第二次进游戏摇杆消失（桌面验收永远绿），修+用例锁死。回归 EditMode 797 / PlayMode **38** / 原生 281/771,162 | 6a094e6 + b488073 |
| 2026-11-02 | **A-DEV-03/04 运行期 DevConsole（三层开口之 B）** | done：在游戏进程里挂 `127.0.0.1:17322` 白名单命令台（help/dump/spark/quality/fg/shot），门控默认仅 `Debug.isDebugBuild`。实测 `spark` 粒子 0→20、`quality` 回读 0/2、空转 0.257µs/帧、`dump` 2ms、坏输入不崩。**硬化**：三根能翻转门控的钩子收敛为 `internal + #if UNITY_INCLUDE_TESTS`——release 的 `ZFrame.View.dll` 里三符号全 0、运行零 socket、连接 REFUSED，对照组证明 grep 有分辨力；加 8 并发 / 50qps / shot 限频与配额；删掉 Mono 下恒 0 的 `Process.Threads` 假断言。**真抓到一个假绿**：旧 1.5s 超时窗口 3/3 判 TIMEOUT（本机 REFUSED 要 2.0-2.1s），8s 窗口才判对。验收 12/12 + 10/10；PlayMode 21→36 | b7c5432 / 1d5ac14 |
| 2026-11-01 | **A-DEV-01/02 Editor 常驻服务** | done：在已开着的 Editor 里挂 `127.0.0.1:17321` 常驻服务（`TcpListener` 手写 HTTP/1.1，不用 `HttpListener`——需 URL ACL 会被拒），七端点 + `Tools/dev.ps1`，后台线程只收发 socket、Unity API 全经 `EditorApplication.update` 泵出。**797 个 editmode 用例：6.0s vs 批处理 44.5s（≈7.4×）**。返工修掉 playmode 拿不到结果（根因：进播 reload 清 `_runs` 且带走 `ICallbacks`；修法：状态落 `SessionState` + 每次 domain load 重挂 callbacks，只有真 `RunFinished` 才写 done）与 gameview 截图（播放态真抓 1651×817，与 camera 图 md5/内容均不同源）。A-DEV-01 13/14、A-DEV-02 10/10 | 5f98291 / 7efc198 |
| 2026-11-01 | **A-AND-05 EventSystem 回归用例** | done：验收 **PASS 5/5**。只加测试不改产品代码——PlayMode 用例经一次单模式 `LoadScene` 后断言 `EventSystem.current != null` 且 `IsChildOf(uiRoot)`（锁死修法本身）。**有牙验证施工/验收各做一遍**：去掉 `SetParent(root,false)` → 恰 1 条红，还原后产品代码 md5 一致。PlayMode 20→**21**、EditMode 797、原生 281/771,162 | 2d5664a |
| 2026-11-01 | **A-AND-04 触屏虚拟摇杆** | done：验收 **PASS with 受限 8 项**。真机可玩成立——推杆角色真位移 `(0,1.08,0)→(-1.51,1.08,5.56)`、松手归零、互动键、暂停面板。选型 A3（自研 UGUI，不动 EventSystem 模块；验收 dump inputactions 证明 UI map 无 `<Touchscreen>` 绑定，换模块必"按钮全灭"）。摇杆走既有 `MoveInputEvent` 链路无旁路。**顺带修阻断缺陷**：EventSystem 是同级 root 被 SCN_Game 单模式加载带走 → 所有 UGUI 派发死。EditMode **797**/PlayMode 20/原生 281/771,162。受限：真机双指无机器证据、无端到端 WASD、EventSystem 修复缺回归用例（立 A-AND-05） | fcc59ad |
| 2026-10-31 | A-AND-03 Android 黑屏修复 | done：设备文件日志仪器+真因(无相机灰屏+UIRoot WorldSpace quirk)修复→真机菜单/游戏双实证+门控收尾；验收 PASS+1 FAIL 已修 | (见TASKS) |
| 2026-10-31 | A-AND-02 Android 工具链+APK | done：施工(纠错简报2处)→验收 PASS（APK 双包 36MB IL2CPP arm64、工具链清单核对、281 用例零回归）；A-AND-01 构建部分被覆盖,真机部署待手机 | (见TASKS) |
| 2026-10-31 | B-R9-14 审计遗留清零 | done：重复派发判定(A-10/B-6 已在 R10-07 闭环)+变异验证既有修复 | 840d489 |
| 2026-10-31 | B-R9-15 Player 构建管线 | done：施工→验收 PASS 5/5（八段套件、冒烟、三方版本一致） | (见TASKS) |
| 2026-10-31 | A-R9-11 Dropdown 模板修复 | done：接手半成品续建→验收 PASS（785 用例、根因 uGUI 源码实证、变异独立复红） | df2cbd1 |
| 2026-10-31 | A-R9-10 建模接入实战+Demo 换代 | done：施工(中断续建修 2 缺口)→验收 FAIL(文档 2 处不实)→H-07 更正轮→终验 PASS 5/5 闭环（782+20 用例、像素实证、指南可复现） | b66f608 |
| 2026-10-31 | B-R9-12 DataKernel 并行Each | done：施工→验收 PASS 5/5（276 用例、平台内存带宽瓶颈夹逼归因验收独立复现） | e98b6ac |
| 2026-10-31 | B-R9-11 VK 离屏 readback | done：施工→验收 PASS 5/5（真根因=帧闸离屏永久关闭，272 用例、门禁 7 指标） | 767351d |
| 2026-10-31 | A-R9-09 BT 死亡事件统一 | done：施工→验收 PASS 5/5（775 用例、双脑对称、粒子零改动） | 712eaad |
| 2026-10-31 | A-R9-08 Volume 权重渐变 | done：施工→验收 PASS 5/5（771+18 用例、31帧单调渐变、完成逐位一致）——方案§6.4兑现 | 01bc1da |
| 2026-10-31 | B-R9-10 自动跑分回归 | done：施工→验收 PASS 5/5（270 用例、注入回归 exit1 实证、quiet 护栏） | df10295 |
| 2026-10-31 | B-R9-09 Vulkan 交换链真重建 | done：施工→验收 PASS 5/5（258 用例、RESIZE OK 4 连跑、变异证伪、D3D12 零改动）——R10-03 遗留清零 | cab268c |
| 2026-10-31 | A-R9-07 粒子事件源补全 | done：施工→验收 PASS 5/5（747+16 用例、三源接线、对话产品判断评审） | 604d94b |
| 2026-10-31 | A-R9-06 粒子接入玩法 | done：施工→验收 PASS 5/5（736+15 用例、双事件源、同帧合并实证、Domain 零粒子引用） | ce73325 |
| 2026-10-31 | A-R9-05 粒子系统 | done：接手半成品续建(审计零返工+独立变异复核)→验收 PASS(Player像素证据验收亲测补齐；PlayMode 12/12+EditMode 719+native 253) | 8beab3e |
| 2026-10-30 | **A-R9-04 后处理真生效** | done：侦察发现"配置齐、画面零效果"——主相机缺 `UniversalAdditionalCameraData`，URP 14 缺省 `renderPostProcessing=false`，Bloom/ColorAdjustments profile 全是死的。**先证伪**（开/关读数完全一致、57,600 像素零变动），再修（零资产改动的运行期注入，幂等）。三档真差异 Low 0 / Medium 0.1753 / High 0.4469（brighter 占比）；新增 PlayMode 像素证据用例**带阈值**，回退注入即 7/8 红。验收 PASS with 受限 10/10（验收代理亲自回退+独立复算到 5 位小数）。受限：GPU 成本未证、同场景后建相机需 `ApplyToAllCameras` | 8feee7e |
| 2026-10-30 | **R10-07 补审计漏项** | done：核对发现审计 17 条只覆盖 15 条，漏 A-10/B-6。**A-10**：`RgGraph::Execute` 只遍历内联批次、从不下发 split 屏障 → 公开 API 调用的图若有 DSV 写→深度读会缺 `DEPTH_WRITE→DEPTH_READ`（花屏/脏读），修法让 split 退化为"优化"同时产出等价内联屏障；场景 9 改真 `Execute` 并断言 `DepthStencilReadOnlyOptimal`（修前=0）。**B-6**：可注入接缝让测试真跑到失败分支、失败整组原子提交、失败计数暴露，并修 `RetireCaptureBank` 同族真泄漏（每次 resize 漏 6 个读回资源）。验收 PASS with 受限 10/10：验收代理自己回退三行→计数回 0 变红、变异注释清理段→红；native 253/711,537、EditMode 719、PlayMode 7 | 98d377b |
| 2026-10-30 | **R10-06 A-9 相机还原** | done：`BindCamera` 换目标前先还原旧目标；`Restore` 改表式多相机（已改写相机→原值，键快照后全量还原）；FG 关闭复用同一 Restore（消灭"关掉也救不回来"）；fake-null 安全跳过。**校验：EditMode 715→719(+4)，验收代理亲自复做"修前必红"（临时注释→3 红→复原→719 绿）**，六段连跑三次 ALL PASS。验收 PASS with 受限 6/6 | fa671ba |
| 2026-10-30 | **R10-05 验收闭环（补跑）** | done：验收代理独立复跑 251 用例/711,498 断言、selftest 21/21、变异抽 2 条复做（B-1/B-7 均改回旧值即红）。**三处越界的追认条件"默认路径逐位不变"全部核实**（A-3 的 nullptr 默认 + 回归钉有牙、C2 尾部追加 28→32 static_assert、C3 导出与 detach 兜底风险如实披露）。PASS with 受限 8/8。遗留：`s_shutdownGate` 置 1 后不可复位（当前无生产消费方） | 6157fbd |
| 2026-10-29 | R10-04 UI淡入软锁修复 | done：施工→验收 PASS 5/5（715+7 用例、三路中断全落 TCS、独立变异复红） | bbda420 |
| 2026-10-29 | R10-03 审计P1原生批 | done：接手 workbuddy 半成品续建→验收 PASS 6/6（222 用例、vtable 槽 24/尺寸状态机/参数表/帧闸，变异验证法） | 9aa17b8 |
| 2026-10-29 | **R10-02 独立代码审计** | done：验收 **"部分可信"**（17 条：P0 1 / P1 9 / P2 7；验收复核 8 条、**0 误报**，纠正 2 处分级 + 1 处文件位置）。**真缺陷举例**：①DXGI 代理对 7 参导出只转发 4 参（野地址写，但本仓无调用点 → 降 P1）；②**工厂 vtable 槽 17 装错**（17 实为 `GetSharedResourceAdapterLuid`，Composition 在 24）——B-R9-03 的"20 导出全转发"门禁照不到这层；③交换链重建后尺寸不更新 → FG 静默失效 + 读回越界；④UI 淡入 Task 无超时/无 finally → UI 软锁（EditMode 走 `!isPlaying` 分支所以永远绿）；⑤Vulkan 把 `OUT_OF_DATE` 当"打印继续"+ 丢弃 `vkQueueSubmit` 返回值。审计还自曝 A-3 被本仓日志证伪并降级 | 见下条 |
| 2026-10-29 | **B-R10-01 交付包干净化** | done：验收 **PASS with 受限**（V1–V6 全 PASS）。`R908Harness` 从 Player 剥离（**构建后静态检查 0 命中**，同命令对旧产物命中 6 作对照）；`BindQualityTargetsForTests` 收为 `internal`+IVT，**定向变异改回 public → 恰好 1 条守卫红**；**自查还发现并修掉 3 处更严重的**：整个测试支撑程序集 `ZFrame.Data.Tests.Support` 曾被编进 Player（验收用 A/B 构建独立证实）、另有 5 个 `*ForTests` 公开接缝。六段 native 196/710,837、EditMode **706**（+4 守卫）、PlayMode 6、exit 0。受限：`TestPanelView` 仍随包、接缝符号名未擦、C1 无常驻守卫 | 见下条 |
| 2026-10-29 | **B-R9-08 相机深度真还原 + 有牙测试** | done：验收 **PASS with 受限 7/7**。N1 改存 `requiresDepthOption` 枚举（开前 `UsePipelineSettings(2)`→开 `On(1)`→关 `(2)`，施工与验收各自多次读回一致）；N2 **定向变异**：删还原 → **恰好 2 条新用例红**（`Expected: 4 But was: 1`、`Expected: Off But was: On`），B-R9-07 旧用例仍绿（复现"删还原也全绿"）；N3 `SuffixFor` 显式 case；N4 B-R9-07 报告加更正小节。关态采集归零（`submit`/`enq` 增量 0），强制深度 On 对照 −13~−25% 四跑方向一致。六段 native 196/710,837、editmode **702**、playmode 6、exit 0。**验收新发现**：本机帧率受 `CompatTelRunner` 干扰，仅空载可信 | 2af50dd |
| 2026-10-29 | **B-R9-07 FG 收尾（MSAA 告知/恢复 + 采集常驻 + 干净基线）** | done：验收 **PASS with 受限**（V1/V4/V5/V6/V7 PASS、V2/V3 PARTIAL）。G1 行标签 `插帧（抗锯齿降至 1×）`+四档不可用各自说明；G2 MSAA 开前 2→1→关 2 无需重启（读 URP 每帧消费字段）；G3 关态 `ZFG_FgSubmitInputs` 增量 0、呈现比 1.000×；G4 同批产物六段 native 196/710,837、editmode 700、playmode 6、exit 0，开态 60s `dGen/dReal=1.000`、2.000×、59.98fps。**遗留（转 B-R9-08）**：相机深度开关"伪还原"（应存 `requiresDepthOption` 而非 bool）、关态仍有残留采集开销 | 见下条 |
| 2026-10-29 | **B-R9-06 FG 落地接线** | done：验收 **PASS with 受限 10/10**（门禁真伪/关=真没插/运行中开→关→开全过）；受限项 = 开 FG 把 MSAA 2→1 未对玩家告知 → B-R9-07 | ad2cb64 |
| 2026-10-29 | **B-R9-05 安装竞态 + 非 D3D12 门控** | done：`Install()` 的 `g_installed` bool 换三态机(`InterlockedCompareExchange`) + 超时放弃 + 半状态回滚；`IsProbeCode` 拒绝把自家函数当原始函数（自递归兜底）；建链前 `QueryInterface(IID_ID3D12Device)` 门控，非 D3D12 纯透传；新增测试用放大器 `ZFG_PROBE_INSTALL_STAGGER_MS`(默认 0 零影响)。**修掉 B-R9-04 验收撞到的阻断级偶发启动崩溃(12 次崩 1 次) 与 D3D11 必崩**。验收 PASS 10/10（放大 20 轮 patches 恒 1 零崩溃） | 0a0928f |
| 2026-10-29 | **B-R9-04 双交换链接管** | done：阶段1 给每条链建档（创建栈模块归属 + 独立 Present 计数 + DXGI GetLastPresentCount 对照）→ 坐实第二条链由 FFX 自建；**真根因**：FFX Present1 内部再调同一代理对象的 Present 被当成第二个应用帧 → frameID 双推进；修复为只 wrap 宿主链、副链原生透传(`appLevel = isWrapped && !nested`)，恢复 1 Prepare:1 Present。60s 实跑 generated/real 0.943、prepareFail=0、2.0x 呈现、59.9fps。验收 PASS with 受限 8/8 | 1403186 |
| 2026-10-29 | **B-R9-03 dxgi 代理 + FG 联调** | done：验收 **PASS with 受限**。**阶段 A 门禁 PASS**——导出表逐条比对（系统 20 个具名导出 / 代理 25 个=20 相同+5 私有 `ZFG_*`，**缺失 0、序号 20/20 一致**），`ZFG_HOOK` 未设时零干预；**阶段 B PASS**（wrap 有真像素读回、失败安全回退原始 DXGI）；**阶段 C FAIL**（真插帧未达：呈现 1.7× + 第二条交换链未接管，卡点属实、施工未伪造）。F2 已修（`jitterCancellation` 默认 false + wrap 显式 false）。六段 native **196**/710,837、editmode 678、playmode 6。验收独立复现出 7 处与施工报告不符（已记录） | 见下条 |
| 2026-10-29 | **B-R9-02 depth/MV 可获取性** | done：门禁 **PASS** + 验收 **PASS with 受限**。修正前提——`ZRenderPipeline.cs` 仅 27 行（`Render()` 只 `context.Submit()`），Unity 真渲染是 URP、自研 SRP 是壳。URP 14 无 `supportsMotionVectors` 开关（验收独立复核），改用"克隆管线资产 + `ConfigureInput(Motion|Depth)`"纯运行期拿 MV，**零资产改动**；深度 `R32_SFloat`/MV `R16G16_SFloat` ptr 均非 0；静止 mv=0、相机+X −0.003479（预测 −0.003480）。新增 C ABI `ZR_FrameGen_ValidateInputs`，导出 11/11（dumpbin 实测）。六段 native **196**/710,835、editmode 678、playmode 6。**遗留 F2（高）：`jitterCancellation` 默认 true，Unity 侧必须显式传 false，否则联调必错** | 3c329ab |
| 2026-10-29 | **B-R9-01 FG 接线 spike** | done(**门禁 STOP**)：交换链路线可行——Unity 走 `CreateSwapChainForHwnd`（fmt=28 RGBA8/buffers=3/FLIP_DISCARD/用 `Present1`），FFX 代理链返回后 Unity 呈现 1680 次不崩；但"真插帧"卡在 depth/MV，且插件惰性加载需进程注入。产物 `Native/zfgprobe/*`（vtable hook/代理链/注入器，自测 13/13，全部 `EXCLUDE_FROM_DEFAULT_BUILD`）。六段未回归。**实测推翻旧注释：FG 在本机 780M 可用（real=60 gen=60）** | f7e5d1e |
| 2026-10-28 | **A-R9-03 清 A-R9-02 遗留** | done：施工→验收 **PASS 8/8**（5 条独立探针 + 2 次定向变异 M1→恰好 T4 红/M2→恰好 T5 红 + 六段独立重跑）。核心判断纠正："过期条目是安全失败（G3 拒绝恢复），缺失条目才是不可逆损失"→ **宁留勿删**：回滚被拒保留条目、`Save` 失败不整份删。editmode **678**=676+2；既有断言一条未改（diff 143/0 纯新增）。新发现既有问题 1 条（日志成功但资产写被拒 → lastWritten 超前 → G3 拒恢复） | 1ff4fbe |
| 2026-10-28 | **A-R9-02 DRS 残留自愈** | done：三轮治理——第1轮施工→验收 **FAIL**（日志写盘失败仍写资产，且过期 `lastWritten` 会导致资产永久钉死）→ 第2轮按主控契约返工（落盘成功为写资产前置条件/失效化过期条目/会话级停用/单条告警）→ 第3轮验收 **PASS**（独立构造"首次失败/先成功后失败/真·下一次启动 `Recover==NoJournal`"；定向变异删一行→恰好1红=T2 证明非恒真）。六段全绿 exit 0：native 190/710800 未变、editmode **676**=663+13、playmode 6。无 URP 资产污染 | 5a8fcb1 |
| 2026-10-28 | **A-R9-01 视频页接 FrameGovernor** | done：新增 `video.dynamicResolution`(Toggle) + `video.frameRateTarget`(30/60/120，默认 60) 两行；新增 `SettingsBootApplier`(RuntimeInitializeOnLoadMethod) **修掉「Load 只在打开面板时调用、冷启动不生效」的根因**；非 URP 按既有标签后缀标注`（仅降档）`；文案与 `帧率上限` 互斥并加宽度测量断言。范围裁决：**FG 不做开关**（无 C ABI/无 dll），另立 B-R9-01。editmode 651→663(+12)，验收 PASS 8/8 | 91d031e |
| 2026-10-28 | **G-66 收口 G-65 残留** | done：`run_tests.ps1:77/:145` 两处裸 `Remove-Item` 统一走 best-effort；**新增 stale 证据真伪判定**（启动前记 LastWriteTimeUtc+大小，读出前校验，不符即判 STALE/BLOCKED）——堵住"旧 results.xml 被当成本轮证据"的假绿通道；`build_native.ps1` 同法加固。验收 PASS 7/7（自造 651 假 xml 用独占锁实测被拒读） | 7cc1050 |
| 2026-10-28 | **G-65 测试健壮性** | done：F1 报告删除失败降级告警+`[STALE]`标记；F2 SPIR-V 改按 exe 目录推导（`GetModuleFileNameA`，历史三候选保留）→ 任意 CWD 直跑 exe 不再误报；F3 报错从 `SPIR-V missing: 1` 变为含文件名+候选目录；F4 `Push/Pop-Location` 复位调用者 CWD。验收 PASS 7/7 | e1a59b8 |
| 2026-10-28 | **G-63 六段测试回归修复** | done：接管核验实跑六段发现 native 段恒 FAIL → 根因 `add_test` 缺 `WORKING_DIRECTORY`（ctest CWD=Native/build，SPIR-V 三个相对候选全部落空）+ `build_native.ps1` 无 `Set-Location` → 修复后**仓库根与 `C:\Windows` 各跑一次六段全量均 exit 0**（190用例/710800断言 + EditMode 651 + PlayMode 6 + 三校验器）；阴性对照移走 `shaders_spv` 必红（189/1 failed，ctest exit 8）证明用例仍有牙；验收 PASS 6/6 | e5c6c84 |
| 2026-10-28 | **G-64 文档对齐（接管）** | done：TASKS 断表修复（R8 六行原在「里程碑对照」下方）+ `B-R8-03` doing→done(be2adcf) + HANDOFF 表头刷新（原「R8 3/4、B-R8-02 待派」）+ README 计数（40→52 任务、161→190 用例） | 见本条 |
| 2026-10-27 | B-R8-02 帧生成 | done：施工→验收 PASS 6/6（超预期：核显被 FG3.1.6 接受、翻倍证据、190用例）——**R8 全部完成** | d27445d |
| 2026-10-27 | A-R8-03 行为树+敌人 AI | done：接手半成品续建→验收 PASS 6/6（651 用例、三态/防重入/双脑实证） | 450a5d3 |
| 2026-10-27 | A-R8-02 Addressables 切换验证 | done：施工→验收 PASS 5/5（601+6 用例、Fake 计数实证、零破坏切换） | 6fcc17b |
| 2026-10-27 | B-R8-01 Vulkan r5 接线 | done：施工→验收 PASS 6/6（170 用例、108 探针 maxDelta=0、双后端全特性对等） | 6c8d85a |
| 2026-10-26 | A-R8-01 FrameGovernor | done：施工→验收 PASS 6/6（588用例、迟滞500轮零振荡、只减法语义） | 9d2a029 |
| 2026-10-26 | H-06 GameHud Shutdown 接缝 | done：修复 H-01 D4 测试踩 EditMode OnDestroy 不回调约定；验收 PASS（557/557） | (见git) |
| 2026-10-26 | **H-05 返工（H 轨闭环）** | done：返工 F1–F6 → **第三轮独立验收 PASS with 受限**（`--clean-first` 全量重编 50 TU 零警告、**168 用例/705,077 断言/0 failed**、独立 `sizeof(ZrDeviceStats)=28` 探针、旧"只增没人读"计数器已删、F2–F6 全达成、"记录不修"4 项时间窗取证未动）｜受限如实：C# 未编译（Unity 不可用） | 71c82f2(合并) |
| 2026-10-26 | H-03 原生验收 | **FAIL 1/13**：Vulkan 描述符分配失败计数"只声明+自增、全仓无读取方"，Release `NDEBUG` 下 `assert` 被编译掉 → 行为与修复前等价却自称"已可观测"；其余 12 项 PASS（含独立复跑 166/705052 零警告、双后端 maxDelta=0） | 71c82f2(合并) |
| 2026-10-26 | H-04 C# 验收 | **PASS with 受限**：D1「不回滚 override」取舍成立（回滚会毁用户旧自定义键）、D2 首存分支真实覆盖、D3 回滚边界无误释放、V7 测试改写不是"为过而改"；指出 3 处契约注释仍写旧语义 | 71c82f2(合并) |
| 2026-10-26 | H-02 原生防御施工 | done：簇网格容量校验（64 位乘积，新增返回码 -3）/FSR Resize 输出上限/Present HRESULT 粘滞状态/Bilinear 泄漏；+5 用例 72 断言（166/705052 零警告） | 71c82f2(合并) |
| 2026-10-26 | H-01 C# 健壮性施工 | done：重绑定保存失败走单一出口 `Complete(Failed)`/settings.json 原子写/in-flight 面板栈查重/`OnDestroy` 销毁 HUD；+9 用例 | 71c82f2(合并) |
| 2026-10-26 | **收口后独立审查** | 实跑复核基线（原生 161/704980 全绿、`--draw`/`--shadows`/`--backend both` 逐像素 maxDelta=0、阴影视差 delta=29 与 README 一致）+ 分域代码审计；发现 10+ 项「测试绿灯照不到」缺陷（D3D12 槽越界、Vulkan 池规范违规、存档不回退备份、脚本 0 用例也报 PASS 等）→ 立项 H 轨。治理如实：其中 4 项由主控越界直接施工，后交独立代理补验收 | 71c82f2(合并) |

| 2026-10-26 | FINAL-01 项目总收口 | done：施工→验收 PASS 5/5（run_tests 六段全绿+注入失败验证、README 实跑核对、差异附录 27+ 项溯源）——**全项目 41 任务完成** | 3a79c65 |
| 2026-10-26 | B-R7-02 Vulkan 接线 | done：接手 workbuddy 半成品→续建→验收 PASS 7/7（lights/shadows 双后端 maxDelta=0；fsr/r5 如实受限） | f5099fe |
| 2026-10-25 | B-R7-01 Vulkan 第二后端 | done：施工→**独立验收 PASS with 受限**（160用例/704967断言、`--draw --backend both` 逐像素 maxDelta=0、RG屏障计数两后端相等、VK零泄漏、validation 0错误）｜受限如实：--lights/--shadows/--r5/--fsr Vulkan 接线未做→转 B-R7-02 | 见下条 |
| 2026-10-25 | A-M5-01 编辑器工具集 | done：施工→**独立验收 PASS 7/7**（542用例 +53、三工具批处理入口 exit0、复用 CsvExporter/ConfigValidator、命名检查只读不修复、存量违规全白名单处置）——**A 轨 M5 里程碑收官**（仅剩 B-R7-01 + FINAL-01） | 见本行 |
| 2026-10-25 | A-M4-02 垂直切片收口 | done：施工→**独立验收 PASS 7/7**（EditMode 489 + **PlayMode 5/5**、启动全链 Boot→主菜单→SCN_Game→暂停/存读档、Demo.md 可复跑+11条诚实限制）——**M4 里程碑收官，垂直切片可跑** | 见本行 |
| 2026-10-25 | B-R6-03 超分设置实接 | done：施工→**独立验收 PASS 7/7**（489用例 +23、P/Invoke 三封装降级不抛、视频页 Enum→SetPreferred 链路、能力门控、既有测试 diff 无放宽）——**R6 里程碑收官**（FSR4/DLSS 真机化列外部补验） | 见本行 |
| 2026-10-25 | A-M4-01 交互系统 | done：续做（接手审计修复前身 EnemyFsm 五缺失类型）+补完 View 扫描/HUD/输入桥接/SCN_Game→**主控代验收 PASS 7/7**（466用例 +94、Domain 零引擎依赖、FSM 无绕过、BuildSettings 三号位）——**M4 前半落地** | e438a36 |
| 2026-10-25 | B-R6-02 FSR4+DLSS骨架 | done：施工（子代理代码）→主控代验收 **PASS**（152用例/704749断言、C ABI dumpbin 实证、`--fsr4` 三向）｜**受限如实**：本机 780M 为核显，FFX 运行时无 4.x provider——FSR4 真机化需独显（外部补验），简报原假设修正 | 01c0e83 |
| 2026-10-25 | A-M3-04 移动键四向收尾 | done：施工（四向分行 + 按 path 匹配索引 + 手柄说明行）→**独立验收 PASS 6/6**（372用例 +11、跨方向冲突真断言、RebindService 核心逐字未改、三测试文件 diff 无放宽）——**A 轨 M3 里程碑全收口** | 见治理提交 |
| 2026-10-25 | A-M3-03 输入重绑定 | done：续做（补 BindingConflictDetector/RebindService/PlayerInputGate/InputRuntime + SettingType.Binding 操作页签实接 + InputBlocker 联动）→**独立验收 PASS 7/7**（361用例 +48、冲突检测纯函数零引擎依赖、持久化 roundtrip 真写盘、既有测试 diff 判定"无放宽"）｜遗留：移动键仅重绑 W → 转 A-M3-04 | 见治理提交 |
| 2026-10-25 | G-45 文档对齐 | done：修 6 类"文档与实际不符"——①TASKS 重写（3 组重复行、里程碑对照插在表中间、A-M2-01/B-R5-01 仍挂 doing）②WORKLOG 补记 A-M2-01/B-R5-01 两条漏记 ③HANDOFF 环境节 URP 版本改 14.0.11、inputsystem 标已装、测试家底改 143/704651 ④AGENTS 的 `run_tests.ps1` 标注为 FINAL-01 交付物（当前不存在）⑤两个任务日志状态行收口 ⑥A-M3-03 日志"洗牌"错字 | 见本条 |
| 2026-10-25 | B-R6-01 FSR 接入 | done：施工（前身主体+接手审计修复 MV prevAngle 分母缺陷）→**独立验收 PASS 7/7**（143用例/704651断言、`--fsr on` ratio 1.346≥1.2、`--fsr off` 基线、两图 md5 不同差异 39,490B、`--r5` 回归 OK） | 58e265a |
| 2026-10-25 | G-44 接管核验（WorkBuddy） | done：实跑取证（原生143/704651全绿、FSR真机ratio1.346、Unity313/313）→发现 A-M3-03 半成品致 Unity 编译断链→修复（asmdef 加 Unity.InputSystem + 架构矩阵登记）→复核全绿；HANDOFF/TASKS 按真实状态刷新 | 见本行 commit |
| 2026-10-25 | A-M3-02 设置系统五页签 | done：施工→验收 PASS 7/7（313用例、数据驱动Smoke实证、实接核对） | a4708c9 |
| 2026-10-25 | B-R5-04 对等闸门产出 | done：施工→验收 PASS 7/7（双截图+指标+报告）；主控目检描述已录，**正式判定待用户** | cfbb372 |
| 2026-10-25 | A-M3-01 UI框架 | done：施工→验收 PASS 7/7（261用例、栈语义/全序/零反射装配实证） | 63b6600 |
| 2026-10-25 | B-R5-03 SSR+GI+NPR | done：施工→验收 PASS 7/7（128用例、三特性falsifiable全过、简化边界如实）——R5特性收官 | 3338829 |
| 2026-10-25 | B-R5-02 CSM 级联阴影 | done：施工(修复R4-03矩阵行列向量历史缺陷)→验收 PASS 7/7（探针手算复算吻合、delta=29、110用例） | 695babb |
| 2026-10-25 | A-M2-03 URP+PipelineManager | done：施工→验收 PASS 7/7（226用例零破坏、三档差异表实证、零粉红）——A轨M2收口 | 7306087 |
| 2026-10-24 | B-R5-01 簇光源+PBR前向 | done：施工→验收 PASS 7/7（102用例703774断言、簇分配几何独立互证、`--lights` 6/6 readback、簇索引 CPU↔PS 逐式一致）｜**2026-10-25 补记**（原漏记，G-45） | 170ad0b |
| 2026-10-24 | A-M2-01 SceneLoader | done：施工→验收 PASS 7/7（169用例、进度纯函数三段断言、Payload读后即焚、InputBlocker嵌套配对、卸载钩子时序）｜**2026-10-25 补记**（原漏记，G-45） | 8ca8d23 |
| 2026-10-24 | A-M2-02 AudioService | done：施工→验收 PASS 6/6（203用例、纯C#三件、ITickable驱动链、Mixer四Group核对） | d41a634 |
| 2026-10-24 | B-R4-03 PSO+首绘 | done：施工(调试修2缺陷+发现CCW)→验收 PASS 7/7（89用例、FIRST DRAW OK、中心像素随旋转变化）——B轨R4真正收口 | c0bd8a6 |
| 2026-10-24 | A-M1-03 SaveSystem | done：施工→验收 PASS 7/7（142用例、原子写/备份/损坏链/迁移实证）——A轨M1收口 | e0f65f0 |
| 2026-10-24 | B-R4-02 绘制列表一体化 | done：施工(自修2缺陷)→验收 PASS 7/7（84用例、对拍一致、合批省94.9%绘制调用）——B轨R4收口 | 2e2c667 |
| 2026-10-24 | A-M1-02 ConfigSystem | done：施工(接手修6缺陷)→验收 PASS 7/7（113用例、roundtrip/错误全报/校验三类实证） | 35f3485 |
| 2026-10-24 | B-R4-01 资产烘焙 .zmesh | done：施工(中断接手)→验收 PASS(机器项主控中立取证+独立代码复核,ADR-006 变通)（70/91用例、zmeshcheck 24顶点36索引、六导出、头对齐双侧断言） | a422666 |
| 2026-10-24 | A-M1-01 ResourceService | done：施工(停滞)→收尾代理→验收 PASS 7/7（91用例、计数/池联动实证） | 22a6701 |
| 2026-10-23 | B-R3-02 RG接真D3D12 | done：施工(含接手中断遗留修3缺陷)→验收 PASS 7/7（RG SMOKE OK、别名真复用2<3、split 60对、60用例） | edb66a6 |
| 2026-10-23 | A-M0-05 调试面板+校验器 | done：施工→验收 PASS 7/7（EditMode 75/75、校验器exit0零违规、零反向依赖）——**A轨M0收口** | cab51c4 |
| 2026-10-23 | B-R3-01 RenderGraph 纯逻辑层 | done：施工→验收 PASS 7/7（57用例、屏障合并量化4→2、别名双向、split计划） | 3e57a67 |
| 2026-10-23 | A-M0-04 驱动+Logger+GameRoot | done：施工→验收 PASS 7/7（EditMode 64/64、纯C#纯度实证、Error→事件锁定） | 2135d50 |
| 2026-10-23 | B-R2-03 D3D12 资源/bindless/时间戳 | done：施工→验收 PASS 7/7（44用例7121断言、资源自检SMOKE OK、时间戳实测11.3ms） | 6425281 |
| 2026-10-23 | A-M0-03 Services+对象池 | done：施工→验收 PASS 7/7（EditMode 38/38、复用零实例化、IPoolable成对、Resetter实证） | 98ab884 |
| 2026-10-23 | B-R0-02 SRP壳+桥接验证 | done：施工(含中断续做)→验收 PASS(代码级+主控中立14/14)；dll三导出从Unity实证调通 | a98e3d6 |
| 2026-10-23 | A-M0-02 EventBus | done：施工→验收 PASS(8用例+代码审查零装箱/快照派发) | 1562649 |
| 2026-10-23 | B-R2-02 D3D12 后端基座 | done：施工→验收 PASS 7/7（34用例6651断言、窗口模式60帧颜色readback校验OK、零隐式转换审查、dll已部署UnityProject） | f7daeac |
| 2026-10-23 | A-M0-01 Unity 工程(重派) | done：许可证激活后施工→验收 PASS 7/7（编译零错误、8 asmdef矩阵一致、SCN_Boot首位、dll+meta入库） | 0af8ab8 |
| 2026-10-23 | B-R2-01 RHI 核心语义层 | done：施工→验收 PASS 7/7（15新用例、全库31用例6473断言、零Vulkan头、Vk位值static_assert对齐） | 876d5d5 |
| 2026-10-23 | B-R1-01 DataKernel 数据内核 | done：施工→验收 PASS 7/7（16用例431断言、BMI2 实证、基准 406M行/s、零分配审查通过） | 17177c6 |
| 2026-10-23 | B-R0-01 CMake 原生构建基座 | done：施工全绿→验收 PASS 7/7（5 用例 16 断言、/WX 零警告、3 符号导出实证） | 3c63b41 |
| 2026-10-23 | G-01 治理基座 | done: 治理文档全套+git init+首提交 | f781cfb |
| 2026-10-30 | R10-05 审计P2原生批(8条) | 施工完成全绿(251用例,变异3条)——**验收待跑**(用户外出前落盘) | (WIP 38759ce) |
