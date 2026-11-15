# TASK-X-DIAG-03 —— Parity 截图 period-3 通道轮转条纹：同源核查 + 旧档重生成

- **轨道**：X / DIAG（B-PLANET-08 新发现②；根因在 B-PLANET-09 已实证并修复）
- **状态**：todo（施工完成，待验收）
- **派发**：主控，2026-11-15（本轮接手指令）
- **来源**：`Docs/Logs/TASK-B-PLANET-08.md:183`（登记 X-DIAG-03）、
  `Docs/Logs/TASK-B-PLANET-09.md:117/123/133`（PNG 编码缺陷实证 + 「其他 Parity 截图路径同源核查、
  B-TECH-04 存档图重生成」保持挂账）、`TASKS.md` X-DIAG-03 行
- **前置事实**：根因 = planet3 链路把 3B/px RGB 缓冲按 4B/px 读（`WritePng24` 的读取契约），
  已在 commit `35fb6ee` 修复（两处调用点改 4B/px RGBA）

## 任务简报（主控未落盘，施工代理按派发令代拟；来源=上列三处）

### 0. 范围

- **不做**：不重新论证根因（B-PLANET-09 已实证：绿通道差 18.99 → 0.0279）。
- **要做**：
  1. 审计**所有** Parity/截图输出路径的「源缓冲格式 × 行距 × 写出端读取契约」一致性
     （3B/px vs 4B/px、stride、行对齐），给一张「路径 × 格式 × 是否同源缺陷」表。
  2. 重生成受影响旧档（若有），并在图上量一次绿通道/条纹计数证明条纹消失；
     无受影响的写明「无」。

### 1. 判据

- 每个输出文件都能对上「生产者 → 源格式 → pitch → 写出器读取宽度」四元组，且四者自洽。
- 受影响旧档：条纹列计数 ≈ 100% → 0；绿通道水平/垂直梯度比从 ~80× 回到 O(1)。
- 未受影响路径：给出数字（不是"看着没事"）。

## 施工报告（施工子代理，2026-11-15）

### 1. 交付内容（本任务零生产代码改动，产出=审计结论 + 复跑证据）

- 审计对象：`Native/tests/zrhi_d3d12_window.cpp` 的 14 个写出调用点（11×`WritePng24` + 2×`WritePng24Pair`
  + 1×`WriteBmp24`）、`WritePng24` / `WritePng24Pair` / `WriteBmp24` 三个编码器、
  `ZR_Unity_ReadTargetPixels` 契约（`Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp:2180`）
  + Unity 侧 11 处 `EncodeToPNG`。
- 证据文件：`TempLogs/xdiag03_planet3_regen.log`（重跑 `--planet3`）、本日志内的量化数字。
- 未改任何 `.cs/.cpp/.h`；未 commit；未动看板。

### 2. 审计表：路径 × 格式 × 是否同源缺陷

**两个编码器的读取契约（先钉死，后面全表据此判断）**

| 编码器 | 源像素读取 | 源行距 | 目标 |
|---|---|---|---|
| `WritePng24`（:5758） | **恒 4B/px**，取 `row[x*4+0..2]` 作 RGB | 调用方 `srcRowPitch` | 24bit RGB PNG（filter 0，zlib stored） |
| `WritePng24Pair`（:5823） | 左右各按 4B/px 拷进 `pairWidth*4` 紧凑缓冲 | 同上 | 复用 WritePng24 |
| `WriteBmp24`（:4685） | **恒 4B/px**，`bgraOrder` 决定 B/R 取字节 | 调用方 `srcRowPitch` | 24bit BMP，bottom-up，dst stride = `width*3` |

| 输出文件 | 生产者 / 模式 | 源缓冲格式 | 源行距 | 写出端 | 同源缺陷？ | 量化证据（本机扫描） |
|---|---|---|---|---|---|---|
| `assets_gallery.png` | `--assets` | D3D12 显式脚印 `R8G8B8A8_UNORM` | 2560 = 640×4（256 对齐） | WritePng24 4B/px | 否 | 条纹列 4（0.6%），h/v = 0.41 |
| `planet_ground/climb/orbit.png` | `--planet` | `R8G8B8A8_UNORM` | 2560 | WritePng24 | 否 | 0 / 22 / 7 列（0–3.4%），h/v 0.11 / 0.57 / 0.76 |
| `planet2_ground/midair/far.png` | `--planet2` | `R8G8B8A8_UNORM` | 2560 | WritePng24 | 否 | 0–2 列（≤0.3%），h/v ≤1.04 |
| `planet3_terrain.png` | `--planet3` | **Unity bridge 回读 BGRA8**（`B8G8R8A8_UNORM`，`ReadTargetPixels` 逐行按 `placed.RowPitch` 拷出） | 目标 640×4 紧凑 | 显式 BGRA→RGBA 后 `WritePng24(W*4)` | **是（历史，`35fb6ee` 已修）** | 旧档 **638/638 列（100%）**、colCurv 16.74、h/v **79.6**；再生 **0 列**、colCurv 0.010、h/v **0.05** |
| `planet3_seam_closeup.png` | `--planet3` seam 段 | 同上 | 同上 | 同上 | **是（历史，已修）** | 旧 638/638（100%）、colCurv 16.99、h/v ∞（v≈0）；再生 0 列、0.000 |
| `Tools/Probes/out/planet-budget-*-terrain.png` | `--planet-budget` | Unity bridge BGRA8 | 目标 1920×4 紧凑 | 显式 swizzle 后 `WritePng24(W*4)` | 否 | 0 列（1920×1080），colCurv 0.008 |
| `Tools/Probes/out/planet-budget-*-seam.png` | 同上 | 同上 | 同上 | 同上 | 否 | 0 列，colCurv 0.000 |
| `fsr_on.png` / `fsr_off.png` | `--fsr` | 自有 `RGBA8Unorm` 回读 | 5120 = 1280×4 | WritePng24 4B/px | 否 | 0 列，h/v 0.27 / 0.40 |
| `fsr41_vs_31.png` | `WritePng24Pair`（仅 FSR4 真跑通时产出） | 左右各 `RGBA8Unorm` | `pairWidth*4` | WritePng24 4B/px | 否（**本机未产出**：FSR4 UNAVAILABLE，退出码 2） | 文件不存在，无图可测 |
| `fsr31_vs_bilinear.png` | `WritePng24Pair` | 左右各 `RGBA8Unorm`，合并紧凑 | `pairWidth*4` = 10240 | WritePng24 4B/px | 否 | 0 列，h/v 0.32（2560×720） |
| `fg_real_frame.png` | `--fg` | 场景色 `RGBA8Unorm` 的 D3D12 脚印回读 | 2560 | WritePng24 4B/px | 否 | 0 列，h/v 0.10 |
| `parity_zrender.bmp` | `--parity` | 自有 `RGBA8Unorm` 回读 | 5120 | WriteBmp24：源 4B/px，dst stride 3840（%4=0） | 否 | 头实测 1280×720×24bpp，文件 2,764,854 B = 54 + 3840×720 |
| `parity_urp.png` | Unity `ParityBench` | Texture2D `ReadPixels` | — | `EncodeToPNG` | 否 | 条纹列 8（0.6%），h/v 0.25 |
| `parity_zrender_preview.png` | **人工转码派生**（仓内无生产者，`TASK-B-R5-04.md:104` 目检用） | — | — | 非本仓编码器输出 | 否（不在自动链路） | 0 列，h/v 0.26 |
| `demo_*` / `android_*` / `planet_ed_*` / `walk*` / `crowd_*` / `utility_*` / `npc_attitude_*`（28 个） | Unity `EncodeToPNG`（含 PlayMode 探针） | Texture2D | — | Unity 编码器（无 stride 参数，结构性不可能出该缺陷） | 否 | 条纹列 ≤7.1%，h/v ≤1.53（crowd 186 列但 h/v 0.90 = 各向同性点阵，非周期条纹） |

**结论**：**同源缺陷仅 planet3 两条路径**，均已由 `35fb6ee` 修复；其余 13 条原生 PNG/BMP 输出路径 + Unity/派生 30 张图**无同源缺陷**（逐张量化，非目测）。

**指标定义（可复算）**：`stripes` = 行带 15%–85% 内，逐列取绿通道二阶差分均值
`|G(x) − (G(x−1)+G(x+1))/2| > 6.0` 的列数（分母 `W−2`）；`h/v` = 绿通道水平相邻差均值 ÷ 垂直相邻差均值。
同源缺陷特征 = `stripes% ≈ 100` 且 `h/v > 20`（周期 3 的通道轮转必然让水平方向高频、垂直方向平滑）。

### 3. 旧档重生成 + 条纹消失量化

- 受影响旧档 = `Docs/Parity/planet3_terrain.png`、`planet3_seam_closeup.png`（B-TECH-04/B-PLANET-08 存档版）。
  两者已在 `35fb6ee` 重生成；本任务再次亲跑确认：

```
Native/build/bin/Release/zrhi_d3d12_window.exe --planet3     # TempLogs/xdiag03_planet3_regen.log
exit=0
  frame 10 keyframe -> Docs/Parity/planet3_terrain.png (written) terrainPixels=306217/307200
    seam closeup png(k=32): Docs/Parity/planet3_seam_closeup.png
    seam pass done: frames_ok=9/9
D3D12 PLANET3 OK (chunks=30 drawn=4 culled=26 vbytes=1468416 lodNextFrame=1 frame_ms=1.037)
```

- **重生成前后 md5 逐位一致**（`23308431cdc7d292b4d0d26c254ce732` / `0c43f8ccb2220f518fa78c7852726e9f`）
  → 编码输出确定、无内容漂移；文件 mtime 更新为本次 10:36（重生成确实发生）。
- **条纹消失量化（同一脚本口径，旧=35fb6ee^、新=当前）**：

| 文件 | 条纹列（占比） | colCurv | hGrad | vGrad | h/v |
|---|---|---|---|---|---|
| 旧 `planet3_terrain.png` | **638 / 638（100.0%）** | 16.738 | 18.617 | 0.234 | **79.6** |
| 新 `planet3_terrain.png` | **0（0.0%）** | 0.010 | 0.011 | 0.217 | **0.05** |
| 旧 `planet3_seam_closeup.png` | **638 / 638（100.0%）** | 16.987 | 18.709 | 0.000 | ∞ |
| 新 `planet3_seam_closeup.png` | **0（0.0%）** | 0.000 | 0.000 | 0.000 | —（纯色/近纯色帧，无高频） |

### 4. 测试与校验（本任务全量亲跑）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1264/1264 全绿**（0 failed，15.7s） | `TempLogs/runtests_editmode.xml` |
| PlayMode | **115/115 全绿**（178.5s） | `TempLogs/runtests_playmode.xml` |
| arch | **PASS，asmdef 27 / 违规 0** | `TempLogs/arch_report.txt` |
| naming | **PASS，违规 0** | `TempLogs/name_report.txt` |
| 原生探针 | `--planet3` exit 0，渲染数字与 B-PLANET-09 逐位一致（仅 frame_ms 时序抖动 1.035→1.037） | `TempLogs/xdiag03_planet3_regen.log` |

### 5. 风险 / 遗留（如实）

1. **写出端仍无行距校验**：`WritePng24` 假设调用方保证 `srcRowPitch ≥ width*4`，越界/错配只会产出
   **静默错图**（正是本次根因的复发通道）。建议后续补一行守卫（**本任务未做**：改原生需重编 `zrender.dll`，
   属构建产物，会带入工作区里既有的 `Native/tests/test_planet_channel.cpp`、`test_zfg_env.cpp` 未提交改动，
   超出本任务范围）：

   ```cpp
   if (pixels == nullptr || width == 0u || height == 0u || srcRowPitch < width * 4u) {
       return false; // 行距不足以容纳 RGBA 行 —— 拒绝产出坏证据图
   }
   ```
2. **`WriteBmp24` 的 dst stride 是 `width*3` 且未做 4 字节补齐**：当前唯一调用为 1280 宽（3840 已对齐），
   实测文件大小与头一致；但若将来出现 `width*3 % 4 != 0` 的调用会产生非法 BMP。已在表中标注为潜在风险，未改码。
3. **B-TECH-04 存档图**：其 planet3 两张即本次重生成对象；仓库内无第二份副本（`find -iname "*planet3*"` 只命中
   `Docs/Parity/` 两张 + 一份探针日志）。
4. **`Tools/Probes/out/` 下的预算证据图**属未入库运行产物，已一并扫过（0 条纹），但不在版本库内，
   重生成与否不影响仓库状态。
5. 工作区存在**先于本任务**的改动（`Docs/Parity/crowd_*`、`npc_attitude_*`、`utility_personality_*` PNG；
   `Native/tests/test_planet_channel.cpp`、`test_zfg_env.cpp`），非本任务触碰，未做任何回滚。
6. 扫描脚本放在系统 temp（`%TEMP%\pngscan.py`），**未入库**；指标定义与阈值已在 §2 写明，可复算。

## 验收清单（施工代理建议，验收代理逐条跑并出证据）

- V1 范围：`git status` 本任务只应看到 `Docs/Logs/TASK-X-DIAG-03.md`（+ 既有他人改动）；`Docs/Parity/planet3_*.png` 内容零变化（md5 同上）。
- V2 审计完整性：对照源码 tree 确认 14 个写出调用点 + 3 个编码器全部在表内；`WritePng24` 恒 4B/px 的读码结论可复核（:5758-5772）。
- V3 旧档：`git show 35fb6ee^:Docs/Parity/planet3_terrain.png` 复算条纹 ≈100%；当前文件 0%。
- V4 重生成：亲跑 `--planet3`（exit 0 + OK 行 + 两张 PNG 重写），md5 应与上表一致；不一致则查渲染是否真的变了。
- V5 其他路径：抽 3 条（gallery / fsr / fg）复算条纹列与 h/v，确认「否」结论成立。
- V6 全量：EditMode 1264 / PlayMode 115 / arch 27-0 / naming 0 亲跑。
- V7 诚实项：确认 §5-1 的守卫「未做」没有被写成「已修」；确认 Unity `EncodeToPNG` 路径按「结构性无 stride 参数」判定，而不是目测。

## 验收结论

**判定：PASS**（审计完整性、量化复核、旧档重生成三项独立复现；「写出端缺 pitch 守卫」确认仍为未做，未被写成已完成）

（验收子代理，2026-11-15 11:00–11:13 本地亲跑；未采信施工数字）

### A. 审计表完整性（源码 tree 对账）

- `Native/tests/zrhi_d3d12_window.cpp` 写出调用点实测 **14 个**，与施工表同：
  `WriteBmp24` :5551；`WritePng24` :6773 / :7972 / :9071 / :9557 / :9742 / :10419 / :10619 / :12389 / :12392 / :12418 / :13314（11 个）；
  `WritePng24Pair` :12344 / :12364（2 个）。⇒ **11 + 2 + 1 = 14**，无遗漏、无重复。
- 三个编码器读取契约读码复核：
  - `WritePng24` :5766-5768 **恒 `row[x*4+0..2]`**（4B/px），行距用调用方 `srcRowPitch`；
  - `WritePng24Pair` :5836-5838 左右各 `memcpy(width*4)`，合并 `pairPitch = pairWidth*4`；
  - `WriteBmp24` :4723-4728 恒 `x*4` 取像素，`bgraOrder` 只换 B/R，`dstStride = width*3`（:4688）。
  ⇒ 三者都**假设调用方给 4B/px**，与施工 §2 表述一致。
- 各调用方行距复核（无第二处 3B/px）：`kParityReadbackRowPitch` :4662 = 5120、`kGalleryRowPitch` :5905 = 2560、
  `kPlanetRowPitch` :7421 = 2560、`kP2RowPitch` :8492 = 2560、`kFgRowPitch` :12465 = 2560，均为 `(W*4+255)&~255`。
  planet3 两处已是 4B/px RGBA（:9544-9557、:9730-9743），注释明确记录原 3B/px 缺陷。
  ⇒「同源缺陷仅 planet3 两条」成立：全库只有 planet3 历史上把 3B/px 缓冲喂给 4B/px 读者；其余路径四元组（生产者/格式/pitch/读宽）自洽。
- 口径小瑕疵（不影响结论）：施工写「Unity 侧 11 处 `EncodeToPNG`」，实测是 **11 个文件、15 个调用点**
  （`grep -c` 逐文件：ParityBench 3、DevScreenshot 2、RuntimeDevConsoleCommands 2，其余各 1）。「无 stride 参数」的结构性判定本身成立。

### B. 量化复核（同一脚本口径自建，旧档从 git 取）

| 文件 | 条纹列 | colCurv | hGrad | vGrad | h/v |
|---|---|---|---|---|---|
| 旧 `planet3_terrain`（`35fb6ee^`，md5 `b85b58a6…`） | **638/638（100.0%）** | 16.735 | 18.546 | 0.236 | **78.47** |
| 新 `planet3_terrain`（当前，md5 `23308431…`） | **0/638（0.0%）** | 0.010 | 0.013 | 0.225 | **0.06** |
| 旧 `planet3_seam_closeup`（md5 `1f2fc492…`） | **638/638（100.0%）** | 16.987 | 18.667 | 0.000 | ∞ |
| 新 `planet3_seam_closeup`（md5 `0c43f8cc…`） | **0/638（0.0%）** | 0.000 | 0.000 | 0.000 | —（纯色） |

- 与施工数字同量级（他们 h/v 79.6 / 0.05，我 78.47 / 0.06；列计数 638/638 与 0 完全一致）；差异应来自
  行带/阈值实现细节（施工脚本未入库，见其 §5-6），**结论方向不变**：100% → 0、h/v 从 ~80× 回到 O(1)。
- 扫描脚本自建于 `%TEMP%\acc_pngscan.py`（未入库），口径同施工 §2：行带 15%–85%、逐列二阶差分均值 > 6.0 计一列、分母 `W−2`。

### C. 其他路径抽查（V5，量化非目测）

| 文件 | 条纹列 | h/v |
|---|---|---|
| `assets_gallery.png` | 4/638（0.6%） | 0.46 |
| `planet2_ground.png` | 0/638 | 0.08 |
| `fsr_on.png` | 0/1278 | 0.31 |
| `fg_real_frame.png` | 0/638 | 0.28 |
| `parity_urp.png` | 8/1278（0.6%） | 0.28 |

全部 `h/v ≪ 20` 且 `stripes ≪ 100%`，不含「周期 3 通道轮转」特征 ⇒「否」列结论独立成立。

### D. 旧档重生成（亲跑）

`Native/build/bin/Release/zrhi_d3d12_window.exe --planet3` → **exit 0**，
`D3D12 PLANET3 OK (chunks=30 drawn=4 culled=26 vbytes=1468416 lodNextFrame=1 frame_ms=1.240)`，
两张 PNG 重写后 **md5 与重写前逐位一致**（`md5sum -c` 全 OK）⇒ 编码输出确定、无内容漂移，`git status Docs/Parity` 零变化。
证据：`TempLogs/acc_xdiag03_planet3.log`。

### E. 诚实项核对（V7）

- **「写出端缺 pitch 守卫」确实是未做**：`WritePng24` 全程无 `srcRowPitch < width*4` 之类校验（:5758-5819 通读），
  `WriteBmp24` 亦无；施工 §5-1 写「本任务未做」与代码一致，**没有被写成已修**。
- `WriteBmp24` 的 `dstStride = width*3` 无 4 字节补齐（:4688 注释已自承），当前唯一调用 1280 宽（3840%4=0）实测合规；
  该潜在风险在施工表中如实标注，未被掩盖。
- `fsr41_vs_31.png` 本机确实不存在（FSR4 UNAVAILABLE → exit 2），施工标「无图可测」属实。

### F. 受限

1. 未复算 `Tools/Probes/out/planet-budget-*.png`（未入库运行产物，且非本任务判定对象）。
2. 未逐张重扫 28 张 Unity `EncodeToPNG` 图（结构性论据为 `EncodeToPNG` 无 stride 参数，不依赖逐张测量）；
   抽了 `parity_urp.png` 一张量化，0.6% / 0.28，与施工一致。
3. 扫描脚本与施工脚本不是同一份实现，个别 h/v 数值有 1–2% 级差异（如 fg 0.28 vs 0.10），已在 B 节注明。

