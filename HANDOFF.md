# HANDOFF —— 项目当前状态快照

> 唯一权威状态源。每任务闭环后由主控刷新。接管者先读本文件，再读 `Docs/交接档案.md`（路线图+环境坑+技术债），再读 TASKS.md。
> 历史交接记录（R8/R9/R10 各轮）已归档于 git 历史（`git show G-113:HANDOFF.md`），WORKLOG.md 有全部 85+ 任务索引。

**快照时间**: 2026-11-04 20:40（**三任务闭环 + 八段基线 ALL PASS；工作区干净**）｜**刷新者**: 主控（WorkBuddy / Hy）｜**仓库**: `C:\Users\1\Desktop\zcode\GameFramework`

## 三十秒速览

| 项 | 值 |
|---|---|
| 任务看板 | **100 done / 0 doing / 1 todo（A-R9-13）/ 0 blocked**（TASKS.md） |
| 测试家底 | 原生 **311 用例 / 776,716 断言** + EditMode **873** + PlayMode **44** = **1228 用例全绿**；三校验器 exit 0；bench 7/7 在容差内；player 默认 SKIP |
| 一键全量 | `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`（八段：native/editmode/playmode/arch/config/naming/bench/player；2026-11-04 实跑 9m57s ALL PASS） |
| 一键出包 | `Tools/build_player.ps1`（Windows，版本单源 Tools/version.json 自增）；`Tools/build_android.ps1`（Android APK） |
| 最新里程碑 | **B-R9-17 DataKernel C ABI / A-GPU-01 性能 HUD / A-R9-14 音频实战三任务闭环**，八段基线全绿；A-R9-13 千人演练场简报已备待派发 |

## 一句话现状

方案承诺 + 两轮增强全部闭环：A 轨框架+垂直切片+**真机可玩**，B 轨渲染内核+双后端（D3D12/Vulkan 全特性 maxDelta=0）+超分全家桶+帧生成；A-AND / A-DEV / A-AUDIT / A-CI 各轮均已闭环。**2026-11-04 接手时 3 个 doing、工作区 13 M + 33 ?? 的未完成现场已全部收口**，工作区干净、0 doing。下一次派发见「下一步优先级」（A-R9-13 简报已备）。

## ✅ 最近完成（2026-11-04，三任务闭环 + 八段基线全绿）

**接手时现场**：`main @ 70e14d2`，工作区 13 处改动 + 33 个未跟踪文件；HANDOFF 快照（00:00）写「0 doing」而 TASKS 实有 3 个 doing，上一轮代理约 07:25 中止未收尾。按「先冻结 → 按任务分簇 → 施工与验收不同代理」收口：

| 任务 | 处置 | commit |
|---|---|---|
| **B-R9-17** DataKernel C ABI + C# 绑定 | 验收已 PASS（V1–V5、12 个 `ZK_*` 导出、定向变异复红）但代码全部未提交 → 主控按白名单精确分簇提交 30 文件。**`zrender.dll` 是本任务产物，未按 §3.7 丢弃** | `2a848da` |
| **A-GPU-01** 性能 HUD | 代码已在 `a2a2c9e`，派**独立验收代理**补 V1–V6 → **PASS with 受限**（V1 批处理无法目测 F3、V3 变异因约束未做，均以静态证据 + 门禁用例替代）；HUD 自身 14/14 全绿；PlayMode 唯一失败被正确归因到并行任务污染 | `2d80a85` |
| **A-R9-14** 音频实战接线 | 补齐六节施工报告 → 查出 PlayMode **44/43/1** 红 → 返工 1 轮（根因：auto-boot 单例跨三用例不销毁、`TearDown` 不复位 CrossfadeState/SfxLimiter；只改测试 `SetUp`，产品代码零改动）→ 独立验收 **PASS with 受限**，验收亲自复做变异 `44/44/0 → 44/43/1 → 44/44/0`；3 个 `D` 经 sha256 证实为搬迁，git 识别 `R100` | `c09f1ef` |

**八段基线（2026-11-04 20:36 实跑，9m57s）**：`Tools/run_tests.ps1` **ALL PASS（7 passed / 1 skipped / 0 failed）**——native **311 / 776,716**、EditMode **873/873/0**、PlayMode **44/44/0**、arch/config/naming 三校验器 exit 0、bench 7/7 在容差内、player 默认 SKIP。Vulkan SDK 已自动恢复（`VULKAN_SDK=C:\VulkanSDK\1.4.363.0`），故本地 311 ≠ 云端 249 属 `if(ZR_HAS_VULKAN)` 预期分支。`zrender.dll` 的构建时间戳噪声按 §3.7 丢弃。
**已知环境噪声（非回归）**：EditMode 段「xml 已写但 Unity 5 分钟未退」→ 按纪律 taskkill（exit=1 是僵尸被杀，证据取本次新写 results.xml）；`run_tests.ps1:354` 的 `cmake_probe_tmp` 创建报 Permission denied（probe 临时目录，native 段仍 PASS）；三份校验器报告的 safe-delete 告警（报告为本次新写、违规 0）。

**管线判定（锁 Unity 2022.3.62f3c1 + URP 14.0.11 + 780M 核显 / Android OXF-AN00）**：`GraphicsSettings.m_CustomRenderPipeline` guid `8b1b4d79…` = `URP_Medium.asset`，manifest 只有 URP、无 HDRP；`ZRenderPipeline.cs` 仅 27 行、`Render()` 只 `context.Submit()` → **Unity 内真渲染是 URP，自研 SRP 是空壳**，ZRender 真渲染在原生窗口程序 + DXGI 代理/FG 通道。Unity 侧效果链落点必须是 URP Volume + `UniversalAdditionalCameraData`（A-R9-04 已实证），**不要**往自研 SRP 加 pass。

## ✅ 最近完成（2026-11-03，第二轮独立审计 + P0 安全修复）

**A-AUDIT-01 审计（`6a094e6`）→ A-AUDIT-02 修复（`b488073`）**

审计出 **9 条**（P0×1 / P1×3 / P2×5，另 6 条"已排除"、4 条"未坐实"），已修最要紧的 3 条。

**A-1 是真正的安全问题（P0），根因值得记住**：
`Editor/DevServer/` 默认开启监听 `127.0.0.1:17321`、每个响应回 `Access-Control-Allow-Origin: *`、
`/menu` 直通 `ExecuteMenuItem` 且无白名单 → 开发者开着 Editor 时，**浏览器打开任意第三方页面，一个 form POST
（简单请求、不触发预检）就能让 Editor 执行任意菜单项**，还能把 2000 条 Console 读走。
**为什么一直没被发现**：验收手段是不带 `Origin` 的 curl —— 这个缺陷在验收方法上不可见。
修法：Host 只认本机（挡 DNS rebinding）+ Origin 三态（无头放行 / localhost 放行 / 其他 403）+ 去掉通配 ACAO + `/menu` 收紧。
验收亲手发攻击请求：恶意 Origin→**403 且未执行**、无 Origin→正常、`Host: evil.example`→403、响应头无 `ACAO: *`。

**A-2** DevServer 补了并发/队列上限（照 B 层 8/64 口径，A 层原来漏了）。
**A-3** 触屏控件 `Spawn` 去重踩了 `Destroy` 延迟窗口 → **第二次进 SCN_Game 摇杆静默消失**，
而桌面验收永远绿（桌面不生成触屏控件）。已修 + PlayMode 用例锁死，变异改回旧逻辑恰 1 条红。

回归：EditMode 797 / PlayMode **38** / 原生 281/771,162 全绿。

**已知受限**：A-2 队列上限实际不可达（并发上限先拦住了）；A-3 用例只锁机制不锁真机症状。
审计余 6 条：**A-4/B-1/B-3/B-5 已立 A-AUDIT-03 在修**；**B-2（SO_REUSEADDR）、B-4（Stop 不停已接连接）主控判定不修**（见下方"已知接受"）。

**一条小技术债**：`Tools/dev.ps1` 提交时 Git 报 "LF will be replaced by CRLF"（`.gitattributes` 没覆盖 `.ps1`），
换行符可能在不同机器上漂移，需要时补规则。

## ✅ CI 已通过（A-CI-01 闭环，2026-11-03 23:59）

**`CI #4 / 50d4e21` 绿，1 分 4 秒**｜https://github.com/JIexpress/zzzzzuni/actions

| 项 | 值 |
|---|---|
| 远程 | `https://github.com/JIexpress/zzzzzuni`（私有） |
| 分支 | `main`（新根，54MB pack）；`legacy` 仅本地存旧历史 |
| L1 门禁 | `Native (build + doctest)`：云端 **249 用例 / 764,341 断言** 全绿 |
| L2/L3 | 未接（Unity 许可证 / GPU），继续本地跑 `Tools/run_tests.ps1` |

### ⚠️ 数字差异是预期的，不是回归

| 环境 | 用例 | 断言 | 原因 |
|---|---|---|---|
| 本地（有 Vulkan SDK） | **281** | **771,162** | 含 6 个 Vulkan 测试文件 |
| 云端（无 Vulkan SDK） | **249** | **764,341** | 上述 32 个用例被条件排除 |

**别看到云端 249 就以为丢了测试**——这是 `if(ZR_HAS_VULKAN)` 的正常分支。

### CI 第一次失败的真实根因（记下来，别再踩）

`error C1083: Cannot open include file: 'vulkan/vulkan.h'`（编译 `test_zrhi_vulkan_bindless.cpp`）。

CMakeLists 宣称「找不到 VULKAN_SDK 就跳过 Vulkan 目标（CI/他机友好）」，`ZR_VULKAN_SOURCES` 确实条件化了，
**但 6 个 Vulkan 测试文件仍被无条件加进 `add_executable(zrender_test ...)`**，它们经
`zrhi/vulkan/zrhi_vulkan_device.h` 间接 include vulkan 头。已改为 `if(ZR_HAS_VULKAN) target_sources(...)` 追加。

**本地预演云端条件的办法（可复制）**：把 `VULKAN_SDK` 设成假值后跑 `Tools/build_native.ps1`
（见 `Docs/Logs/TASK-A-CI-01.md` §五⑤ 的完整命令），应打印 `Vulkan backend: SKIPPED` 且 249 用例全绿——
这样在本地就能预演云端条件，不用等 CI 试错。

### 推送与凭据

- 推送必须清代理：`env -u http_proxy -u https_proxy -u HTTP_PROXY -u HTTPS_PROXY -u all_proxy -u ALL_PROXY git push origin main`
  （宿主透明代理 `127.0.0.1:60759` 对 git 大流量返回 502）
- 直连偶发 `Connection was reset` → **重试，通常第 2 次成功**
- token：`C:\Users\1\.zframe-github-token` + `~/.git-credentials`；需 `repo` + **`workflow`** 两个 scope

### 遗留

- 本地 `Native/build` 当前是**无 Vulkan 配置**（被这次验证改成 SKIPPED）。
  下次正常跑 `Tools/build_native.ps1`（不设假 `VULKAN_SDK`）会自动重新探测 `C:/VulkanSDK` 并恢复完整构建。
- `actions/checkout@v4` 有 Node.js 20 deprecated 警告（不影响结果），可择机升 v5。
- token 已在对话中出现过，建议轮换。

## 🔵 （历史）CI 接入过程记录



### 这一刻的状态（中断后从这里接）

| 项 | 值 |
|---|---|
| 远程仓库 | `https://github.com/JIexpress/zzzzzuni`（私有，空仓起步） |
| 推送内容 | 本地 `main`（新根，实际 pack **54MB**） |
| 凭据位置 | `C://Users//1//.zframe-github-token`（**仓库外**，一行 token） |
| 推送命令 | `git push -u origin main` |
| 中断后怎么办 | **直接重跑上面那条命令**；git 会重传，不需要别的清理 |
| **状态** | ✅ **已推送成功**（2026-11-03 23:47，`f83ab85`） |
| 凭据 | remote 已清成干净 URL；token 在 `~/.git-credentials`（`credential.helper=store`）与 `C://Users//1//.zframe-github-token` |
| 网络实测 | 54MB / 36 秒（1.86 MiB/s）——带宽不是瓶颈，**干扰才是** |

**推送踩过的两个坑（下次照做可省 20 分钟）**：
1. **环境里 4 个代理变量指向 `127.0.0.1:60759`**（宿主透明代理），对 git 大流量返回 `CONNECT tunnel failed, 502`。
   推送必须：`env -u http_proxy -u https_proxy -u HTTP_PROXY -u HTTPS_PROXY -u all_proxy -u ALL_PROXY git push ...`
2. 直连也偶发 `Connection was reset` / `Could not connect`（GFW 干扰）。**重试即可**——第 1 次失败、第 2 次就通了。

⚠️ **token 已经在对话里明文出现过，这个仓库跑通后建议去 GitHub 吊销重发一个**，
重新写进 `C://Users//1//.zframe-github-token` 即可，代码不用改。

### 推送完成后要做的三件事

1. 打开 `https://github.com/JIexpress/zzzzzuni/actions`，看 `CI / Native (build + doctest)` 是否自动跑起来
2. 把 remote 里的 token 清掉（避免明文留在 `.git/config`）：
   `git remote set-url origin https://github.com/JIexpress/zzzzzuni.git`
3. 确认仓库体积接近 54MB（不是 500MB+）——若超了，说明历史被带上了，检查是否误推了 `legacy`



**分层**：L1 native（云端跑，不需 Unity 许可证/GPU）✅ 已配 ｜ L2 unity ⏸ 未启用（需许可证，建议自托管）
｜ L3 playmode/bench/打包 ⏸ 不进云端（无 GPU 会假绿、bench 会误报）。

**最大意外**：`Tools/android_support_installer.exe` **456MB** 被误提交，占 `.git` 的 **89%**，
全仓 0 引用。已 `git rm --cached` + `.gitignore`，**tracked 556MB → 101.5MB**。

**仓库结构变了（接手必读）**：
| 分支 | 内容 | 是否推送 |
|---|---|---|
| `main`（`a5300dc`） | 当前完整快照，**实际 pack 54MB** | 推 |
| `legacy`（`6ec9127`） | 完整旧历史（含那 456MB 的旧提交） | **不推，仅本地** |

想看 2026-11-03 之前的提交历史，用 `git log legacy`；文档里引用的旧 commit hash 仍可用 `git show <hash>` 访问。

**网络实测（决定方案的关键）**：`git ls-remote` 到 github.com **成功**，但 `api.github.com` **不可达**
→ `gh` CLI 用不了、自托管 runner 注册大概率失败。所以 L1 走云端，L2/L3 留本地。
`github.com` 网页响应约 12s。

**本地门禁已生效**：`.githooks/pre-push`（10MB 阈值，只查当前快照，`Native/ThirdParty/` 白名单放行 FFX dll）。
实测有牙（1MB 阈值下准确拦截并 exit 1）。⚠️ 新克隆要执行一次 `git config core.hooksPath .githooks`。

**待办**：用户建空私有仓库 + 生成 PAT 存 `C://Users//1//.zframe-github-token`（仓库外），然后推送。

## ✅ 最近完成（2026-11-03）**A-AND-08 签名已换成 release 证书**

`Build/Android/ZFrame.apk` 现在签名是 **`CN=ZFrame`**（v2 验证通过），**可以分发**了。
证书 SHA-256：`54:13:B0:48…2C:B0`。产物 `0.1.0.24 / versionCode 2`。

**密钥在哪（很重要，别弄丢）**：
- keystore：`C:/Users/1/Android/keys/zframe-release.keystore`
- 密码：`C:/Users/1/Android/keys/keystore.properties`（alias `zframe`）
- **丢了就用同一个包名永久无法更新**，只能换包名重发、用户全丢。请自行备份这两个文件（不要进仓库）。
- 细节见 `Docs/交接档案.md` §3.8。

**三条安全约束（后续任何人别破坏）**：
1. 密码**不硬编码、不打印进日志**；仓库里只允许有路径。
2. 环境变量 `ZFRAME_*` 优先、properties 兜底；**两者都没有必须中止构建**。
3. **绝不允许静默退回 debug 签名**——那会产出"以为是正式包其实是 debug"的包。
   验收实测：移走 properties 后构建在 `BuildPipeline.BuildPlayer` 之前就退出（exit=1，`SIGNING ABORT`），
   APK 哈希未变（没被覆盖）。

**已归档防误分发**：`Build/Android/legacy-debug-signed/` 放了 11-01 那两个 debug 签名包（MonoDev/MonoVerify）
并附 README——它们是取证用的，`CN=Android Debug` 不可分发，留在外面容易被误发出去。

## ✅ 最近完成（2026-11-03）**A-AND-06 IL2CPP 正式包已出**

`Build/Android/ZFrame.apk`｜**36.32 MB**｜**arm64-v8a only**｜`versionName 0.1.0.19`｜无 `DEVELOPMENT_BUILD`｜构建 29 分 16 秒（`9da8db9`）

真机原来那版是 `ZFrame_MonoDev.apk`（`DEVELOPMENT_BUILD` + 调试红字 + DeviceFileLogger），**只能取证不能对外**，
现在这版才是可对外的正式包。耗时大头是 URP 着色器变体编译（12288 个变体 ≈ 10 分钟）+ IL2CPP C++ 编译（1418 个 .o）+ Gradle。

**真机验证已补测通过（2026-11-04 00:30）**——华为 OXF-AN00：卸载旧 debug 包 → 装 release 包 →
主菜单（无调试红字）→ 点开始 → SCN_Game 完整渲染（HUD/阴影/粒子/触屏摇杆）→ logcat 无崩溃。
另验「暂停→回主菜单→再次进入」，**摇杆仍在**（A-AND-03 那个只有真机能暴露的缺陷已确认不复发）。

### 两个"想发布就必须解决"的阻塞（验收顺带查出，已立项）

| 项 | 现状 | 后果 |
|---|---|---|
| ~~**A-AND-07** versionCode 写死为 `1`~~ | **已修**（`2b7e927`）：独立字段自增 + 单调守卫 | — |
| ~~**A-AND-08** 签名是 `CN=Android Debug`~~ | **已修**（`88275f0`）：本地自签 keystore，`CN=ZFrame`，v2 通过 | — |

**版本倒挂已修**：`version.json` 的 `build` 19 → **23**，下次构建产出 `0.1.0.24`（> 旧包 0.1.0.22）。

**A-AND-07 的设计要点（别改回去）**：`versionCode` 用**独立字段**而非从 `build` 派生——
因为 `build` 历史上被重置过（18→19 就是倒挂成因），派生必然违反单调递增；
而 versionCode 一旦倒退，Android 会拒绝安装更高 versionName 的包，**商店端不可逆**。
守卫在读到 ≤1 时直接中止构建。

## ✅ 最近完成（2026-11-03）**第二轮审计 9 条全部着落**

| 条目 | 级 | 处置 | commit |
|---|---|---|---|
| A-1 DevServer 任意网页可驱动 Editor（通配 ACAO + `/menu` 直通） | P0 | 已修 | `b488073` |
| A-2 DevServer 无并发/队列上限 | P1 | 已修 | `b488073` |
| A-3 触屏控件第二次进游戏静默消失 | P1 | 已修 | `b488073` |
| A-4 UIRoot 重收养重复建 8 个 canvas（两套 UI 叠着） | P1 | 已修 | `e495ebc` |
| B-1 存档先移走 main 再写（写失败丢档） | P2→上调 | 已修 | `e495ebc` |
| B-3 EnsureEventSystem 不校验持久性（A-AND-04 修复会复发） | P2 | 已修 | `e495ebc` |
| B-5 DevConsole 超时后仍执行 | P2 | 已修 | `e495ebc` |
| B-2 `SO_REUSEADDR` 放弃端口互斥 | P2 | **不修**（见已知接受） | — |
| B-4 `Stop()` 不停已接连接 | P2 | **不修**（见已知接受） | — |

**A-4 与 B-1 值得记住的两个模式**：
- **A-4**：幂等判据只信内存字典，而那个字段在 domain reload 后必空、场景子对象却还活着 → 越"幂等"越重复。
  修法是先**从真实子对象重建字典**再补缺。施工还自曝过一处翻车：第一版用例只清静态 `_instance` 没清实例字典，
  **变异下是绿的**（假牙），改成两者都清才必红——这类"用例看着对、变异不红"的坑要警惕。
- **B-1**：存档注释写着"先写 tmp 再替换"，代码却是**先移走 main 再写**，且无 `try/catch`。
  现在改成"写 tmp 成功后才滚动备份"，失败时 `throw` 重抛不吞。

**已知接受（主控判定不修，别再当缺陷重报）**

| 条目 | 为什么不修 |
|---|---|
| **B-2** 两服务都用 `SO_REUSEADDR`，Windows 下放弃端口互斥 | 需同机另一个进程配合才能踩到，不泄数据；改绑定成本与收益不匹配 |
| **B-4** DevConsole `Stop()` 只停 accept 线程，已接连接活到 10s | 只影响"停止后 10 秒内仍有旧连接"的观感，无功能错误 |

## 🔵 当前没有进行中的任务（三层开口 A/B 已闭环，2026-11-02 凌晨）

用户点的「AI 直操作三层开口」= **A. Editor 常驻服务 + B. 运行期 DevConsole + C. 原生 CLI**。A、B 已完成，C 早已有。

### A 层 · Editor 常驻服务（`5f98291` + `7efc198`）

`127.0.0.1:17321`，七端点 + `Tools/dev.ps1`。**797 个 editmode 用例 6.0s，批处理 44.5s → 约 7.4×**。
后台线程只收发 socket，Unity API 全经 `EditorApplication.update` 泵；domain reload 自恢复（状态进 `SessionState`）。

### B 层 · 运行期 DevConsole（`b7c5432` + `1d5ac14`）

`127.0.0.1:17322`，白名单命令 **`help` `dump` `spark` `quality` `fg` `shot`**，一行请求/一行 JSON。
给的是「跑起来长什么样」——真机/Player 里没有 Inspector、Console、Play 按钮，以前只能重打包+肉眼看。

**安全模型（这层在发布包里，别当玩具）**：
- 默认仅 `Debug.isDebugBuild` 开启；`ZBF_RT_CONSOLE=0` / `-zbf-noconsole` 优先关闭；非开发包**无法重开**。
- 三根能翻转门控的钩子（`SetOverrideForTests` / `DevelopmentBuildProbe` / `TryStart`）已收敛为 `internal + #if UNITY_INCLUDE_TESTS`。
  **证据**：release 的 `ZFrame.View.dll` 里三符号（含子串）全 0；release 产物运行**零 socket**、连接 REFUSED、日志无 `ZBF-RT`；
  对照组（dev 包相邻符号各 1、`RuntimeDevConsole` 两边均 17）证明 grep 有分辨力。
- 限流：并发 **8** / **50 qps** / `shot` **≥200ms + 目录 ≤200 PNG**；全部结构化错误，不静默丢。

**两个真发现的坑**：
1. **`Process.Threads.Count` 在 Mono 恒 0** —— 用它做线程断言等于没断言，已删，改看 `ListenThreads`。
2. **超时窗口太短会假绿**：同端口下 1.5s 窗口 **3/3 判 TIMEOUT**，8s 窗口 **3/3 判 REFUSED**（本机 REFUSED 要 2.0–2.1s）。
   凡是"连接被拒"的断言，必须区分 `Refused`/`Timeout` 四态，否则慢机器上会绿着骗人。

**未验证（别当成已验证）**：真机 `adb forward` 未跑（本机无 adb，按纪律不装）；IL2CPP Release 包未验（本工程 Mono）；`fg` 的"支持平台"分支未验。
## ✅ 最近完成（2026-11-01，WorkBuddy 接手第一件）

**A-AND-04 触屏虚拟摇杆 + 屏上交互键** —— done，验收 **PASS with 受限 8 项**（`fcc59ad`）

真机（OXF-AN00）已实证：推杆角色真位移 `(0,1.08,0) → (-1.51,1.08,5.56)`、松手归零、互动键、暂停面板。
**手机现在是真能玩了**，不再依赖键盘。测试 EditMode **797**、PlayMode 20、原生 281/771,162。

**选型（下一个接手者别改回去）**：走 A3 备选——自研 UGUI 摇杆，**不动 EventSystem 模块**。
原因是验收 dump 了 `ZFrameControls.inputactions`：UI map 的 `Point`/`Click` 只绑 `<Mouse>/*`、
**没有任何 `<Touchscreen>/*` 绑定**，所以换 `InputSystemUIInputModule` 必然"按钮全灭"。
`UIHierarchyTests:74` 的 `StandaloneInputModule` 断言因此原样保留。

**顺带修掉一个阻断级缺陷（超出本任务简报范围，已单列留痕）**：
`UIRoot.EnsureCreated()` 只给 host 加 `DontDestroyOnLoad`，而 EventSystem 当时是**同级 root 对象** →
单模式加载 SCN_Game 时随 SCN_Boot 一起被卸载，且 `_instance != null` 早退不再补建 →
**SCN_Game 内所有 UGUI 派发全死**（摇杆/按钮/暂停面板全废）。修法：EventSystem 挂到 UIRoot 下继承持久化
（3 处 18 行，未改模块类型）。这条影响**所有场景的所有 UGUI**，不只是摇杆。

**已知受限（不要当成已验证）**：
1. **真机双指无机器证据**——adbd 串行化，注入双指时 `touchCount` 恒为 1；只有组件级用例（合成 PointerEventData）。
2. **无端到端 WASD 用例**——键盘侧只做到"代码等价"（`OnMove` 提取为 `ForwardMove`），批处理下无法合成按键，未亲按。
3. ~~EventSystem 修复缺回归用例~~ → **已由 A-AND-05 补上**（`2d5664a`）：PlayMode 加载第二个场景后断言 `EventSystem.current != null` 且 `IsChildOf(uiRoot)`；变异去掉 `SetParent` 恰 1 条红，施工与验收各独立做了一遍。

## 接管者动作序列

1. `git log --oneline -5` + `git status`（应干净，HEAD `c09f1ef` 或更新；本轮治理提交后再看 git log）
2. 读 `Docs/交接档案.md`（三十秒速览/环境坑/治理规则——**注意其 §0/§2 数字是 10-29 版，看板数字以 TASKS.md 与本文件为准**）
3. 领任务：从下方"下一步优先级"写简报（模板见任一 Docs/Logs/TASK-*.md）→ 派施工代理 → 派**不同**验收代理 → PASS 后 commit（`TASK-XXX: 摘要`）→ 刷新 TASKS/WORKLOG/HANDOFF → governance commit
4. 历史教训与环境纪律全部在 交接档案 §3（每条都真实踩过）+ 本文件"环境纪律速查"

## 下一步优先级（未承诺排期，按价值排序）

| # | 方向 | 内容 | 状态 |
|---|---|---|---|
| 1 | ~~触屏虚拟摇杆~~ | **已完成**（A-AND-04，`fcc59ad`）——真机可玩成立 | ✅ done |
| 2 | ~~AI 直操作三层开口~~ | **已完成**（A `5f98291`/`7efc198`、B `b7c5432`/`1d5ac14`、C 早已有） | ✅ done |
| 3 | ~~IL2CPP 正式包重出~~ | **已完成**（A-AND-06/07/08，`9da8db9`/`2b7e927`/`88275f0`）：release 签名 `CN=ZFrame`、versionCode 单调守卫、真机补测通过 | ✅ done |
| 4 | ~~第二轮独立代码审计~~ | **已完成**（A-AUDIT-01/02/03，9 条修 7 接受 2，含 P0 DevServer 任意网页驱动 Editor） | ✅ done |
| 5 | ~~CI 远程化~~ | **已完成**（A-CI-01，`50d4e21`）：CI #4 绿 1m4s，云端 249/764,341；L2/L3 仍留本地 | ✅ done |
| 6 | ~~GPU 工具链（方案 §5.7）~~ | **已完成**（B-R9-16 `724e123` + A-GPU-01 `a2a2c9e`） | ✅ done |
| 7 | **A-R9-13 千人演练场** | B-R9-17 ABI 已就绪；简报 `Docs/Logs/TASK-A-R9-13.md` 已落盘（1000 实体混合架构、批量绘制、`ITickQueue` 驱动、10 µs 基线对照、URP 兼容约束） | **简报已备，看板 todo 待派发** |
| 8 | **URP 侧收口** | 下方「URP 侧离线体检」给出判据：变体剥离 / SRP Batcher 兼容性 / 三档降级 / Depth-Opaque 纹理开关 | 待立项 |
| 9 | 真机 FSR4.1 + DLSS5 / Metal 后端 | 需 RX7000/9000 独显、RTX 50 系、Apple 硬件 | 等硬件 |

### URP 侧离线体检（2026-11-04，只读核对，未改任何资产）

| 事实（文件 + 字段） | 判据 / 下一步 |
|---|---|
| `URP_Medium.asset`：`m_UseSRPBatcher: 1`、`m_MSAA: 2`、`m_RenderScale: 1`、`m_SupportsHDR: 1`、`m_ShadowDistance: 40` | SRP Batcher 开关已开，但**运行期创建的材质**（粒子 / 后处理）是否满足 CBUFFER 合并未核验 → 用 Frame Debugger 看实际 SRP Batch 数，别只看开关 |
| `m_RequireDepthTexture: 0`、`m_RequireOpaqueTexture: 0` | 景深 / SSR 类效果当前拿不到深度与不透明纹理输入 → 按效果逐档开启并给出带宽代价，不要全局打开 |
| `GraphicsSettings`：4 个 `*Stripping` 全 0、`m_PreloadedShaders: []`、`m_ShaderVariantLogLevel: 0`、`m_AlwaysIncludedShaders` 8 项 | A-AND-06 构建中 **URP 着色器变体 12288 个 ≈10 分钟**是明确成本中心 → 打开变体日志 → `IPreprocessShaders` 剥离 / `shader_feature` 收敛 → 用「变体数 + 构建时长」做改前改后对照，不承诺帧率 |
| `QualitySettings` 三档：Low `antiAliasing:0` / Medium `2` / High `0` | **URP 下 MSAA 由 URP asset 接管**，QualitySettings 的 `antiAliasing` 不生效——别照这个字段判三档；降级应落在 URP_Low/Medium/High asset + Volume 档位，需实测各档 DrawCall / 带宽 / 发热 |
| 适用边界 | 以上仅适用于 Unity 2022.3.62f3c1 + URP 14.0.11 + 780M 核显 / Android arm64；换 Unity 大版本或管线需重新判定 |

## 环境纪律速查（每条真实踩过，详见交接档案 §3）

- **Unity 批处理**：单实例（跑前查进程+删 `UnityProject/Temp/UnityLockfile`）｜退出可能僵死（轮询 results.xml，5 分钟有 xml 无退出即 `taskkill //PID //F`）｜跑测试**勿带 -quit**｜证据以 results.xml 为准
- **子代理派发**：`model concurrency limit exceeded`/`exceed quota limit` → 退避 5-10 分钟重试；停滞（10 分钟无活动）→ 查现场（进程/锁/TempLogs 证据）→ 续派或让轻量代理代码复核收尾（ADR-006）
- **代理变量重复**（HTTP_PROXY+http_proxy）崩 MSBuild——构建前按交接档案 §3.2 清理
- **Tuanjie Hub（中国版）**：无头 CLI `install-modules` 不认（报 No editor found）；CDN 直链全 404——Android 子模块走的是**外部工具链绕行**（见下）
- **网络**：github raw 可能被重置→走 REST API；unity.cn SPA 页面 shell 抓不到直链（浏览器可达）；含 % 的中文文案勿用 python % 格式化（用 Edit 工具）
- **Android 工具链（已装配，勿重装）**：`C:/Users/1/Android/`——JDK11（Microsoft 11.0.32.1）/cmdline-tools 19.0/platform-tools/build-tools 34.0.0/NDK r23b；**Unity EditorPrefs 键名实测**：`AndroidSdkRoot`/`AndroidNdkRootR23B`（非 AndroidNdkRoot）/`Jdk11Path`（Unity 2022.3 硬校验 JDK major==11）
- **zrender.dll**：构建产物入库（ADR-004），提交前 `git checkout --` 丢弃非任务产生的 M 噪声

## 待办备忘（小）

- **BGM 来源 URL 缺口**（A-R9-14 受限项）：`AUD_bgm_cave.ogg` 只有简报口径 CC0、无 URL / 许可证原文；对外发布前须查采集记录回填 `Assets/Audio/SOURCES.md`，验收未代编 URL
- **CI token 轮换**：A-CI-01 的 PAT 曾在对话明文出现，建议吊销重发并更新 `C:\Users\1\.zframe-github-token`
- **main 领先 origin/main**：本轮多个提交未推；推送前清 8 个代理变量（宿主透明代理对 git 大流量 502），新克隆需 `git config core.hooksPath .githooks`
- bench 基线已 quiet 重跑闭环；`run_tests.ps1` 的 BOM（HEAD 既有）可顺手清；`TestPanelView` 随 Player 包、接缝符号名未擦（交接档案 §5 技术债）
- `shot` 目录配额 200 个文件 ≈ **1–1.6 GB** 偏宽（可改 50 个 + 总字节上限）；DevConsole 的 **ThreadPool 线程数无看护**
- `run_tests.ps1:354` 的 `cmake_probe_tmp` Permission denied 与 EditMode 僵尸 taskkill 属已知环境噪声，非回归
- 真机当前装的是 **IL2CPP release 包**（A-AND-06 真机补测通过、无调试红字）；旧「Mono 开发包」备忘已过期

## 最近任务日志（R9-10 轮）

- `Docs/Logs/TASK-A-AND-03.md`（done，真机可玩，40cd2a1）
- `Docs/Logs/TASK-A-AND-02.md`（done，APK 双包，758b528）
- 更早全部：WORKLOG.md（85 任务索引）+ git log（202 commits）

## 下一个接管者（ZCode 新会话/workbuddy/其他）

一切以磁盘为准：AGENTS.md（治理规则）→ HANDOFF（本文件）→ TASKS.md（看板）→ 交接档案（环境坑）。写简报→派施工→派验收→闭环刷新，与历史任一轮相同。祝顺利。
