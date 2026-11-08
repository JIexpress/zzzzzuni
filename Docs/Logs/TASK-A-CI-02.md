# TASK-A-CI-02 —— CI 云端 Vulkan 用例补齐（249/281→全绿）

状态: doing（验收 PASS，待闭环） | 派发: 2026-11-04 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: fd38ea4（施工 commit，仅含 workflow；日志随验收/闭环 commit 入库）

## 任务简报（主控写）

### 背景

云端 CI 跑 249/281（Vulkan 相关用例条件化跳过——云端 runner 无 vulkan.h）。A-CI-01 记录了差异原因。本任务：在 workflow 里装 Vulkan SDK（LunarG 官方 GitHub Action `VulkanGroup/install-vulkan-sdk` 或 lucg安装器）让云端全量 281 用例跑通，CI 从"部分绿"变"全绿"。

### 设计要点

1. 读现有 workflow（`.github/workflows/*.yml`）——native 段结构、Vulkan 条件化在哪（测试源条件化 commit 50d4e21）
2. workflow 加 Vulkan SDK 安装步（官方 action 或 winget LunarG.VulkanSDK；SDK 装后 VULKAN_SDK env 设置）
3. 测试条件化逻辑调整：有 VULKAN_SDK 环境时启用 VK 用例（保留无 SDK 环境的跳过逻辑——本地仍兼容）
4. **验证受限**：主控无法直接触发 Actions（gh 未认证）——验证方式：①本地预演（设 VULKAN_SDK env 模拟）②推送后由用户/workbuddy 在 GitHub 页面核对 Actions 结果；③推送是唯一触发方式——**推前先本地全量绿**
5. 若安装步在 runner 上失败：如实报告并回滚 workflow 改动（保持 CI 绿的状态），列替代方案

### 约束

- 白名单：`.github/workflows/**`、`Native/tests/`（条件化宏调整）、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF
- **推送远程**：本任务允许 `git push`（CI 触发必需——用户已建远程并授权 CI 方向）；推前本地八段全绿
- 基线：native 349/778,723（本地含 VK 全量）

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 日期: 2026-11-08 ｜ 施工 commit: `fd38ea4`

### 一、选型报告（简报要求的说明）

| 候选 | 核实结果 | 结论 |
|---|---|---|
| `VulkanGroup/install-vulkan-sdk@v1`（简报首选） | **不存在**——`api.github.com/repos/VulkanGroup/install-vulkan-sdk` 返回 404，git 层面亦无法建立连接。简报中的这个 action 名有误 | ❌ 弃用 |
| **`jakoch/install-vulkan-sdk-action@v1`** | 真实且活跃：最后 push 2026-11-05、最新 tag v1.8.1（`refs/tags/v1` 存在→`7fe8428`）、MIT、TypeScript、node24 运行时、GitHub Marketplace 在列。从 LunarG 官方 `vulkan.lunarg.com` / `sdk.lunarg.com` 下载（符合 file-conventions §7 仅 https 官方来源） | ✅ **选用** |
| winget `LunarG.VulkanSDK` | 官方源但失败面大：数百 MB 交互式安装器、静默参数因版本而异、装后须自行解析 VULKAN_SDK 路径、无缓存键管理 | ⏸ 备选未用 |

**选 jakoch 的决定性理由（源码级核实，`src/main.ts` / `src/installer_vulkan.ts`）**：
1. 装完自动 `core.exportVariable('VULKAN_SDK', installPath)` → **正好命中** `Native/CMakeLists.txt` 的 `$ENV{VULKAN_SDK}` 探测（50d4e21 实现），零胶水代码；
2. 自动 `core.addPath(installPath/bin)` → `glslc.exe` 进 PATH，CMake 的 SPIR-V 编译步可用；
3. `install_runtime: true` 会把 `vulkan-1.dll` loader 装到 `<SDK>\runtime\x64\`——**这是无 GPU runner 上的生死项**（见下）。

### 二、关键风险与对策（施工中的两个发现）

1. **简报"条件化宏调整"实际无需改动**。Vulkan 用例条件化已在 50d4e21 完整落地：`Native/CMakeLists.txt` 探测 `VULKAN_SDK`（env 优先，`C:/VulkanSDK/*` glob 兜底）→ `ZR_HAS_VULKAN` → 6 个 VK 测试文件 `target_sources` 条件追加；测试侧 `#if defined(ZR_HAS_VULKAN)` 宏已就位。本任务 `Native/tests/` **零改动**，白名单内仅动 `.github/workflows/ci.yml`。
2. **运行期 loader 缺失会让 exe 启动即败（比编译失败更隐蔽）**。`Native/zrhi/vulkan/` 无 LoadLibrary/volk 延迟加载——`vulkan-1.dll` 是静态导入。本机 loader 在 `C:\Windows\System32\`（装 GPU 驱动时装的），SDK 的 `Bin\` 目录只有头/库/glslc、**没有** vulkan-1.dll；CI runner 无驱动 → 无 loader → `zrender_test.exe` 无法启动。对策：workflow 显式 `install_runtime: true` + 追加一步把 `$env:VULKAN_SDK\Bin` 与 `$env:VULKAN_SDK\runtime\x64` 都加入 `GITHUB_PATH`（存在哪个命中哪个，冗余无害）。

### 三、workflow 改动（`.github/workflows/ci.yml`，+35/-4 行）

native job 在 checkout 后新增两步，并同步更新三处注释：

1. **Install Vulkan SDK (LunarG)**：`jakoch/install-vulkan-sdk-action@v1`，`vulkan_version: 1.4.363.0`（钉定=本地验证版本，避免 latest 漂移）、`install_runtime: true`、`cache: true`；
2. **Put Vulkan loader on PATH**（pwsh）：两候选目录存在即加入 `GITHUB_PATH`；
3. 注释更新：文件头分层策略注明"Vulkan 用例全量入云、真设备用例降级不假绿"；Configure 步注明 VULKAN_SDK 已由安装步 export；doctest 步注明云端与本地同为全量、B-5/F1 走降级路径。

### 四、本地验证（推送前，全部实跑）

| 验证 | 命令 | 结果 |
|---|---|---|
| **无 SDK 跳过路径保留**（简报要点 3 的"保留"侧） | `VULKAN_SDK=C:/__nonexistent_sdk__` 走 `Tools/build_native.ps1` | `ZRender Vulkan backend: SKIPPED` 打印、build exit 0；`zrender_test.exe` → **317 用例 / 771,902 断言全绿**（= 249 + 后续任务新增 68 非 VK 用例；32 个 VK 用例被条件排除）。已知噪声 `cmake_probe_tmp Permission denied` 与 HANDOFF 记录一致，非回归 |
| **有 SDK 全量**（V2 / 推送前置） | `Tools/run_tests.ps1` 八段 | **ALL PASS（6 passed / 2 skipped / 0 failed）**：native **349 / 778,723**（= 简报基线）、editmode 983/983、playmode 71/71、arch/config/naming exit 0；bench 因 machine busy 判 SKIP（脚本空载护栏口径）、player 默认 SKIP |

### 五、推送与 CI 首轮结果（V3）

- 推送：网络干扰严重（github.com:443 直连十余次 Could not connect / Connection was reset，代理 `127.0.0.1:60759` 端口不可达），重试循环约 30 秒后抓住间歇窗口成功：`84b1d7e..fd38ea4 main -> main`。施工 commit 仅含 `ci.yml`（推送是 CI 触发必需载体，故做了 commit；按治理规则不做任务闭环的 governance commit，日志文件不入施工 commit）。
- **CI 首轮 run：SUCCESS** ｜ run URL: https://github.com/JIexpress/zzzzzuni/actions/runs/42458776146 （head_sha `fd38ea4`，attempt 1，native job 07:26:05–07:30:22Z ≈ **4 分 17 秒**，较此前 1 分 4 秒增加的正是 SDK 下载安装耗时）
- **云端日志取证**（经 token 拉取 job 128663340914 日志，446 行）：
  - `-- ZRender Vulkan backend: ENABLED (VULKAN_SDK=C:\VulkanSDK\1.4.363.0)` —— action 安装被 CMake 探测命中，钉定版本生效；
  - `test_zrhi_barrier_truncate.cpp(43): MESSAGE: Vulkan 1.3 设备不可用 -> B-5 读链用例降级`、`test_zrhi_device_stats.cpp(62): MESSAGE: ... F1 ...降级` —— 两个真设备用例按设计降级（`ZrVulkanCreateDevice()` 返回 nullptr → MESSAGE + return，用例仍计入 passed；已核 `Create()` 全链失败路径干净返回 nullptr，无 crash/assert）；
  - **`[doctest] test cases: 349 | 349 passed | 0 failed | 0 skipped`**
  - **`[doctest] assertions: 778572 | 778572 passed | 0 failed |`**
- **数字口径（重要，防误读）**：云端 349 = 本地 349（用例全量，此前云端 249 的差距已消除）；断言 778,572 = 本地 778,723 − 151，差的正是 B-5/F1 真设备分支的断言（降级路径不执行），是设计行为不是丢失。**"249/281→全绿"目标达成**（简报标题中的 249/281 为派发时口径，现基线已演进为 317/349，本轮后云端=本地=349）。

### 六、受限与遗留

1. **SDK 版本下架风险**：`vulkan_version: 1.4.363.0` 直通 LunarG 下载清单（源码核实无版本列表校验）。若 LunarG 未来下架该安装包，CI 将在安装步红——届时改 `vulkan_version` 为现行版本即可（或改回 `latest`，代价是行为漂移）。`cache: true` 已开，正常情况下二次运行命中缓存约 1 分钟。
2. **断言差 151**：云端无 GPU 属永久差异（B-5/F1 降级），workflow 只验退出码不比对数字，无需处理；若未来 CI 断言数相对 778,572 出现非 151 的变化，按真实回归排查。
3. **L2/L3 未接**：Unity 层（需许可证）与 playmode/bench/打包（需 GPU）仍留本地八段，与 A-CI-01 口径一致。
4. **文件规范**：`ci.yml` UTF-8 无 BOM、LF、2 空格缩进、无 tab（已核）。`Native/tests/` 零改动。


## 验收清单（主控写）

- [x] V1 workflow 含 Vulkan SDK 安装步（yaml 核对）
- [x] V2 本地模拟验证：设 VULKAN_SDK → 条件化逻辑启用 VK 用例（本地全量 349 全绿证据）
- [x] V3 推送后 Actions 结果（施工代理等待首轮 run 结果并记录——或列"待观察"由主控/用户核对）
- [x] V4 无 SDK 环境跳过逻辑保留（本地清 env 复测跳过路径）
- [x] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**PASS** ｜ 验收: general-purpose 验收子代理 ｜ 日期: 2026-11-08 ｜ 独立执行 V1–V5（未跑 Unity、未改代码、未 commit；每项证据均独立取证，非转抄施工报告）

- **V1 PASS（workflow 核对）**：`git show fd38ea4 --name-only` 仅 `.github/workflows/ci.yml`（+35/−4）；安装步 `uses: jakoch/install-vulkan-sdk-action@v1`（ci.yml:59）+ `vulkan_version: 1.4.363.0`（:61）+ `install_runtime: true`（:62）+ loader PATH 步（ci.yml:65–76）齐备。**条件化宏零改动属实**：VULKAN_SDK 探测/ZR_HAS_VULKAN 为既有实现（50d4e21 "Vulkan 测试源条件化" 引入，fd38ea4 未触碰 Native/CMakeLists.txt 与 Native/tests/，`fd38ea4..HEAD` 区间亦无改动）。
- **V2 PASS（本地模拟亲跑，双路径均测）**：本机有真 SDK（C:/VulkanSDK/1.4.363.0），无需引用云端日志补证启用路径。跳过路径：`VULKAN_SDK=C:/__nonexistent_sdk__` 在独立构建目录 configure → `ZRender Vulkan backend: SKIPPED (VULKAN_SDK not found ...)`，build exit 0，测试 exe → **317 用例 / 771,902 断言全绿**（与施工报告逐位一致；用独立构建目录而非 build_native.ps1，避免其第 4/4 步覆盖并行任务工作区中的 Unity 插件 DLL）；启用路径：VK 全量构建亲跑 → **349 用例 / 778,723 断言全绿**（=简报基线）。临时构建目录已清理。
- **V3 PASS（Actions 独立核对；一项受限如实注明）**：`curl api.github.com` 匿名核对 run 42458776146：`conclusion: success`、`head_sha: fd38ea4d...`（=施工 commit）、attempt 1；native job 128663340914 success（07:26:05–07:30:22Z，与报告逐秒一致），"Install Vulkan SDK (LunarG)" 与 "Put Vulkan loader on PATH" 两步均 success。**受限**：job 原始日志下载端点匿名返回 403（需 admin 权限），故 doctest 349/349、断言 778,572 两个数字以施工报告引录的云端日志为准（数字本身未独立复核）；**151 断言差已源码级复核成立**——B-5/F1 真设备分支降级设计（`CreateDevice()==nullptr → MESSAGE + return`）与报告引录的云端 MESSAGE 逐字吻合（test_zrhi_barrier_truncate.cpp:43 / test_zrhi_device_stats.cpp:62），属设计行为非丢失。
- **V4 PASS（隐蔽风险规避核对）**：`dumpbin //DEPENDENTS` 实测 VK 全量构建的 zrender_test.exe **第一依赖即 vulkan-1.dll**（源码无 LoadLibrary/volk/GetProcAddress，`target_link_libraries(zrhi PUBLIC vulkan-1)` 系静态导入）→ loader 缺失则进程启动即败，比编译失败更隐蔽。实测本机 SDK `Bin/` 确无 vulkan-1.dll（仅头/库/glslc.exe），本机 loader 在 System32（GPU 驱动带入）——云端 runner 无驱动则两处皆无，故 `install_runtime: true`（ci.yml:62，loader 装至 `<SDK>\runtime\x64\`）+ PATH 步（ci.yml:65–76，Bin 与 runtime\x64 双候选兜底）是必要且正确的对策。机理复述与规避方案核实成立。
- **V5 PASS（推送/规范/报告）**：`git ls-remote origin main` = `fd38ea4d...` = 本地 HEAD = origin/main（三方一致）；ci.yml 无 BOM（首字节 `6e 61 6d`）、0 CRLF、0 tab（.gitattributes eol=lf，committed 版本同为 LF）；`Native/tests/` 零改动核对属实；施工报告四节结构完整（简报/施工报告六小节/验收清单/本结论）。**网络重试记录评估**：commit 时间 15:20:39+0800 → CI run created 15:26:02+0800，约 5.5 分钟间隔与"十余次连接失败、重试约 30 秒后抓住间歇窗口"的记载吻合，且推送结果经 ls-remote 独立证实——重试记载可信、非事后编造。

附加核对：native job 唯一 annotation 为 actions/checkout@v4 的 Node.js 20 deprecation 平台级警告，与本任务无关。施工报告§六遗留事项（SDK 版本下架风险、断言差 151 口径、L2/L3 未接）已如实记录，不构成本任务 FAIL 项。

**验收裁决：PASS（V1–V5 全过）。**
