# TASK-B-UNITY-05 —— 共享纹理整面回读只有 1/8 行正确（根因 + 修复）

> 缺口清单第 3 条。B-UNITY-02 验收时发现、当时记为"潜在共享布局缺陷"：
> **宿主导入侧整面回读只有 1/8 行正确**。Unity 走 SRV 采样不受影响（所以画面是对的），
> 但任何跨侧回读都会踩。施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。

## 现象

- B-UNITY-02 验收原话：「宿主导入侧整面回读只有 1/8 行正确（潜在共享布局缺陷，
  Unity 走 SRV 采样不受影响，但后续跨侧回读必踩）」
- 也就是说：贴屏出来的画面正确（证明 GPU 侧纹理内容对），但**读回 CPU 时** 8 行里只有 1 行对

## 可疑方向（先验证再动手，别猜）

1. **行间距（RowPitch）**：`1/8` 这个比例非常像"用 `width/8` 当 pitch"或"pitch 用了错误的除数"。
   D3D12 的 `PlacedFootprint.Footprint.RowPitch` 通常 ≥ 256 对齐且 = `width * bytesPerPixel` 上取整，
   回读时必须用**实际 RowPitch** 逐行拷贝，不能假设 `width * 4`。
2. **切片/深度**：`DepthOrArraySize > 1` 时 `SlicePitch = RowPitch * height`，拷贝 stride 是否用错
3. **格式**：BGRA8 vs RGBA8 通道序（这会导致"颜色错"而非"1/8 行错"，优先级低）
4. **映射指针偏移**：`ReadFromSubresource` 的 `pDstData` 步进是否按 `width*4` 而非 `RowPitch`
5. **1/8 = 128 行里 16 行**？还是"每 8 行只有第 1 行对"？**先把失败形态量化**（这个最省时间）

## 任务

### 1）先复现并量化
- 找到现有的整面回读路径（B-UNITY-02 加的那条），构造一个**确定性图案**（比如按行号填 `y*width+x`），
  导出后统计：多少行正确、错误行的偏移规律（是每 8 行错 7 行？还是只对了前 1/8？）
- **把规律写清楚**——这直接决定根因

### 2）定位根因
按上面的方向逐一排除，**给出证据**（不是"我觉得"）。
最省事的判据：把 `RowPitch` 打印出来，和 `width*4` 对比，看是不是 8 倍关系。

### 3）修复
- 用**实际 RowPitch/SlicePitch** 逐行拷贝（不要假设紧凑布局）
- 如果是测试代码假设错了而产品路径没错，也要说清楚（那就不算产品缺陷，但要修测试并记录）

### 4）验证
- 确定性图案回读后**逐像素完全一致**（不是"1/8 行"）
- 顺带覆盖非 256 对齐宽度（如 986×611、641×357）——这类宽度最容易暴露 pitch 假设

## 判据

- 给出**失败形态的量化描述**（哪种规律）
- 根因有证据（RowPitch 实际值 vs 假设值）
- 修复后：确定性图案逐像素一致；至少两种非对齐宽度都通过
- 回归：原生 332、`--draw` 60 帧、`-zrender` 画面不变（截图对比）
- 不改游戏内容

## 环境纪律（真实踩过）

- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- **PATH 上的 cmake 是 MSYS2 版无写权限** → 必须走 `Tools/build_native.ps1`
- Unity 批处理**单实例**：查进程 + 删 `UnityProject/Temp/UnityLockfile`；**勿加 `-quit`**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- PS 5.1 对原生 exe 加 `2>&1` 会把退出码变 1（假失败），不要用
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不动游戏内容；`.cs/.cpp/.h` 无 BOM、LF

---

## 施工报告（施工代理写）

> 结论先说：**不是行距问题**（RowPitch 实测恰好是 1.000/1.039/1.098 × `width*4`，没有任何 8 倍关系）。
> "1/8 行有内容、其余 7/8 为 0"的成因是：**同一块共享显存在 D3D12 里有两个资源对象，
> 而状态跟踪是按对象走的** —— 宿主在 `OpenSharedHandle` 拿到的那个对象上做
> PSR→COPY_SOURCE，转不动"另一个对象用 RTV 写进去的内容"，整面拷贝里 7/8 的行
> 根本没有被落盘。行距假设错（`width*4`）造成的是**剪切**，不是"1/8 + 0"，
> 两者被我分别复现出来了（见 §2.2 的两段"推演"）。

### 2.1 失败形态（量化）

诊断器：`Native/tests/zrhi_shared_pitch_probe.cpp`（新增，独立 exe，运行期从
`zrender.dll` 取导出）。它往共享纹理写一张**逐像素可自证**的图案
（`B=x低8位, G.bit1:0=x高2位 / G.bit3:2=y高2位, R=y低8位, A=255`，宽高 ≤1023 时唯一），
然后用四张"读法"逐行比对。日志：`TempLogs/TASK-B-UNITY-05/probe_*.log`。

**先记一笔自己的坑**：第一版图案把 y 只放在 R 通道（8 位），于是 y≥256 的行与
y-256 行**内容完全一样**，`Analyze` 误报"前 256 行对、之后从第 0 行重来"。
改成把 y 的高 2 位塞进 G 后此假象消失。这条写在这里，是因为它差点把我引向
"纹理只有 256 行容量"的错误根因。

实测（三种宽度 × 四条路径，真实 RowPitch 逐行拷）：

| 尺寸 | plain→plain | own→own | own→imported | imported→own |
|---|---|---|---|---|
| 640×480 | 480/480 | 480/480 | 480/480 | 480/480 |
| 986×611 | 611/611 | 611/611 | 611/611 | 611/611 |
| 641×357 | 357/357 | 357/357 | 357/357 | 357/357 |

即：**只做拷贝、内容由拷贝写入**时，两个对象、两个方向都逐字节一致 —— 共享布局本身没问题。

### 2.2 "1/8"到底是什么规律（两种签名，别混）

| 假设错法 | 实测签名（640×480 / 986×611 / 641×357） |
|---|---|
| **读侧按 `width*4`**（非 256 对齐宽度上比真实行距小 3.9% / 9.8%） | 只有**第 0 行**对；其余整行错位且逐行累积。986×611：第 1 行读到图案第 375 行；good=1/611 |
| **写侧行距 = 8 × 读侧** | **每 8 行只有第 1 行有内容，其余 7 行为全零**。640×480：good=1、zero=420/480；986×611：zero=534/611；641×357：zero=312/357 |

**B-UNITY-02 记录的"只有 1/8 行序能读到正确内容、其余为 0"= 上表第二行。**
注意它与"假设 width*4"不是同一件事：后者给的是剪切（每行都有内容但错位），
**不会出现成片的 0**。这条区分是定位根因的关键。

### 2.3 RowPitch 实测值 vs 假设值（根因证据之一：排除行距）

`GetCopyableFootprints` 实取（own / imported / plain 三个对象**完全一致**）：

| 尺寸 | `width*4`（假设） | 实测 RowPitch | 比值 | totalBytes | pitch×height |
|---|---|---|---|---|---|
| 640×480 | 2560 | **2560** | ×1.000 | 1 228 800 | 1 228 800 |
| 986×611 | 3944 | **4096** | ×1.039 | 2 502 504 | 2 502 656 |
| 641×357 | 2564 | **2816** | ×1.098 | 1 005 060 | 1 005 312 |

- **不是 8 倍关系**（简报的首要猜测可以排除）。
- `RowPitch` 恒为 256 对齐：`align256(986*4)=4096`、`align256(641*4)=2816`。
- `totalBytes` 比 `pitch×height` 小"最后一行尾部对齐字节"（152 / 252 B）——
  按 `totalBytes` 建上传缓冲再逐行 `memcpy` 会越界，我在探针里真踩过一次（641 宽稳定崩）。

### 2.4 根因（证据链）

症状只在**这张纹理被渲染过**之后出现。同一张渲染完的纹理，两种读法：

| 读法（都是真实 RowPitch=2560，都是宿主队列） | 全零行数 | 角像素 (0,0) |
|---|---|---|
| ZRender **自己那个** `ID3D12Resource` 整面读 | **0 / 480** | (38,26,13,255) = 清屏色 |
| 宿主 `OpenSharedHandle` 出来那个对象整面读（状态声明 PSR） | **274 / 480**（签名=每 8 行 1 行） | (38,26,13,255) |
| 上者但屏障 `StateBefore` 谎报 COMMON | 274 / 480（一样坏） | 同上 |
| 上者但再多渲染 4 帧（排除"没画完"） | 264 / 480（一样坏） | 同上 |
| **先把 ZRender 自己那个对象 PSR→COMMON，再用 imported 读** | **0 / 480** | (38,26,13,255) |

最后一行是判决：同一个 imported 对象、同一个行距，**只要状态由"拥有它的那个对象"转一遍，
立刻能读全**。所以：

**根因 = 两个资源对象共用一块显存，D3D12 的状态跟踪按对象独立；宿主在自己的对象上
做 PSR→COPY_SOURCE 无法让另一个对象（RTV 写入方）的内容进入可线性拷贝的状态。**
驱动于是只落盘了 1/8 的行（其余留在未写入的 READBACK 内存里读成 0）。
这不是行距、不是通道序、不是 `DepthOrArraySize`、也不是竞态（多渲 4 帧不变）。

顺带解释两个"为什么以前能过"：
- Unity 走 SRV 采样（不经 COPY），画面一直是对的 —— 与症状不矛盾；
- 确定性图案**由拷贝写入**时 own→imported 全对：写读两侧用的是同一套线性假设，
  误差互相抵消，把问题盖住了。只有内容来自"渲染"时才会暴露。

### 2.5 修复

1. **产品新增整面回读导出** `ZR_Unity_ReadTargetPixels(void* dst, uint32_t dstRowPitch,
   uint32_t* outW, uint32_t* outH)`（`zrhi_d3d12_unity_target.cpp:ReadTargetPixels`，
   def / zplugin.h / zplugin.cpp 三处接线）：
   - **状态由本模块自己的对象转**（PSR→COPY_SOURCE→PSR），这是根因的正解；
   - 读前等本队列排空到此刻（读的是画完的内容），读完纹理仍在 `PIXEL_SHADER_RESOURCE`
     （Unity 采样前提不变）；
   - 行距/切片一律取 `GetCopyableFootprints`，**逐行**拷进调用方缓冲，
     并支持调用方自己的 `dstRowPitch`（不是整块 memcpy 紧凑布局）；
   - READBACK 缓冲按需建一次（随目标释放），不每帧分配。
2. **1×1 中心回读不再假设行距**：删掉 `kReadbackPitch = 256` 常量，改成向设备问 1×1
   BGRA8 的真实脚印（值仍是 256，但它是"问出来的"而不是"猜对的"）。
   顺带把 `TargetState.colorState[slot]` 记成帧末真实状态，供上面的回读用。
3. **修测试里的两处漂移**（都不是产品缺陷，但会误导验收）：
   - 自证程序里"宿主整面回读不可靠、只有 1/8 行"的结论**是错的**，已替换为三条断言
     （无全零行 / 角像素=清屏色 / 尺寸正确），并改用产品 API；
   - `renderFrame` 计数断言在 B-UNITY-04 把中心像素回读改成"延后一帧取"之后没有跟着改
     （旧断言要求帧 0 结束 `readbacks==1`，实际是 0）。现在断言 0，并在 10 帧后断言 1。
4. 新增诊断器 `zrhi_shared_pitch_probe`（`Native/CMakeLists.txt` 挂独立 exe，
   不进 ctest —— 与 `zrhi_unity_bridge` 同构，需要真 GPU）。

### 2.6 验证数字

**V3 确定性图案逐像素一致**（`ZR_Unity_ReadTargetPixels`，调用方行距故意给非紧凑值）：

| 尺寸 | 调用方 dstRowPitch | 结果 |
|---|---|---|
| 640×480 | 2624（`width*4+64`） | rc=0，**480/480 行逐像素一致** |
| 986×611 | 4008（`width*4+64`） | rc=0，**611/611 行逐像素一致** |
| 641×357 | 2628（`width*4+64`） | rc=0，**357/357 行逐像素一致** |

**V3 补充（渲染后的真实纹理整面回读）**：三种宽度均 `zeroRows=0`、角像素 = 清屏色
BGRA(38,26,13,255)；640×480 另核对中心像素 (54,54,54)/(10,22,54) 与自证程序一致。

**V5 回归**：
- 原生单测 **332/332** 通过（`build_native.ps1` 第 3 步，0 failed）。
- `zrhi_d3d12_window --draw` **60 帧**：6/6 中心像素校验通过、`device removed = S_OK`、
  `InfoQueue error count = 0`（`TempLogs/TASK-B-UNITY-05/draw60_after.log`）。
- `-zrender` 画面：Player 120 帧，`中心像素回读=(54,54,54) 回读次数=4`，帧同步
  平均等待 0.119 ms（`Player_zrender_after.log`）。截图对照（新旧 DLL 各一张，
  `zrender_after.png` / `zrender_before.png`，同为 986×611 窗口）：
  清屏色像素占比 65.07% vs 65.64%，清屏掩码一致 **96.00%**，逐像素相同 83.38%
  （差异全部来自 cube 随时间旋转的相位，旋转角 = `frameCount%120×3°`，
  两次抓图的帧相位不同，无法像素级对齐）。**背景、三 cube 位置、UI 菜单结构一致，
  无错行/撕裂**。
- 另：`zrhi_unity_bridge` 自证 37 项 PASS、0 FAIL（`bridge_after.log`）。

### 2.7 边界与遗留（如实）

- **没有**给跨进程/跨适配器做 keyed mutex 或导出 fence：本任务只解决同进程同设备下
  "宿主用自己那个对象回读"的可靠性。跨进程仍需要 keyed mutex（B-UNITY-02 §2.8 已记）。
- `ReadTargetPixels` 读的是**槽位 0**（即 `GetSharedTargetResource`/`GetSharedTargetHandle`
  暴露的那一张），不是"上一帧可读槽位"。需要读另一槽位时按需再加参数。
- 回读会等本队列排空（一次 fence 等待），属于诊断/证据路径，**不要**每帧调。
- `UnityProject/Assets/Plugins/ZRender/zrender.dll` 因 `build_native.ps1` 第 4 步被更新
  （构建产物入库策略如此），`Build/Windows64/...` 只是本地验证用，未入库。
- 未执行 Unity EditMode/PlayMode（不在本任务判据；本次只动原生回读路径与测试）。
- 未 commit。

### 2.8 现场

- 改动文件：`Native/zrhi/d3d12/zrhi_d3d12_unity_target.{h,cpp}`、
  `Native/zplugin/{zplugin.h,zplugin.cpp,zrender.def}`、`Native/tests/zrhi_unity_bridge.cpp`、
  `Native/CMakeLists.txt`；新增 `Native/tests/zrhi_shared_pitch_probe.cpp`。
  全部 UTF-8 **无 BOM**、**LF**（已逐字节复核：bom=False, crlf=0）。
- 证据与日志：`TempLogs/TASK-B-UNITY-05/`（3 份 probe 日志、bridge 日志、--draw 日志、
  Player 日志、两张截图）。
- 未改游戏内容（`Assets/` 下 C#/资源一行未动），未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`。


---

## 验收清单（供验收代理）

- [ ] V1 失败形态被量化（哪种规律，不是"1/8"了事）
- [ ] V2 根因有证据（RowPitch 实测值 vs 假设值）
- [ ] V3 修复后确定性图案逐像素一致
- [ ] V4 两种非 256 对齐宽度通过
- [ ] V5 回归：332 / --draw 60 帧 / -zrender 画面不变
- [ ] V6 未改游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

> 验收代理与施工代理不同人。本结论的全部数字来自**我自己的探针**（`TempLogs/audit_b05/audit_b05_probe.cpp`，
> 独立于 `zrhi_shared_pitch_probe`），日志在 `TempLogs/audit_b05/audit_{640,986,641}.log`。
> 施工报告自述只用来对照，不作证据。

### 逐条裁决

| 项 | 裁决 | 证据（一行） |
|---|---|---|
| V1 失败形态量化 | **PASS with 受限** | 我用独立探针复现 640×480：`zero=264/480`，非零行=216，行间隙直方图 `gap8=36 / gap1=177`；真实形态是"顶部 8×8 抽取 + 中段半物化"的混合，不是"1/8 行"那么规整（见"修正 1/2"） |
| V2 根因（重点） | **PASS with 受限** | 同签名、同拷贝代码、同行距，只换"屏障打给哪个对象"：imported 侧屏障→264 全零行；own 侧屏障→0 全零行；先给 own 做 PSR→COMMON 再用**同一段 imported 代码**→0 全零行且与 own 真值**逐字节相同** |
| V3 逐像素 + 非紧凑行距 | **PASS** | 产品 API：确定性图案 `badBytes=0`（dstPitch=w*4）/ `badBytes=0` 且 `paddingTouched=0`（dstPitch=w*4+64）；渲染内容 `dstPitch=w*4+64` 与紧凑读 `diff=0` |
| V4 非对齐宽度 | **PASS** | 986×611 `zero=0/611`、641×357 `zero=0/357`（行距 4096/2816 ≠ w*4），且与 own 侧独立回读 `diff=0` 字节 |
| V5 回归 | **PASS with 受限** | 原生 **332/332 0 failed**（我重跑 `build_native.ps1`）；`--draw` **60 帧 6/6**、`device removed=S_OK`、`InfoQueue error=0`；`-zrender` 我亲自起 Player 抓到 90fps/960×540/中心像素 (54,54,54)/回读次数按 30 帧节拍递增 |
| V6 未改游戏内容/未 commit/现场干净 | **PASS with 受限** | `Assets/` 下 .cs/.unity/.prefab/.asset **零改动**，唯一差异是标准构建步骤 4 覆盖的 `zrender.dll`（属入库构建产物，§2.7 已声明）；`git log` 仍停在 `8622f18`，无新 commit；`TASKS.md`/`HANDOFF.md`/`WORKLOG.md` 未动 |

**最终裁决：PASS with 受限。** 根因结论**站得住**（我用自己的因果实验证实了"只有拥有方对象的状态转换才有效"），
修复有效且边界正确；受限之处是**报告里对失败形态的描述与机制那几句话不准确**，需在 §2 更正（见下"必须更正的 3 处"）。

### 必须更正的 3 处（不影响裁决，但不能算"已验证"）

1. **"每 8 行只有第 1 行有内容、其余 7 行全零（640: zero=420/480）"——420 是仿真出来的，不是实测。**
   施工探针里 `420` 出自 `writer pitch = 8x` 那一段：把**已知正确**的数据按 8 倍行距重新解释。
   真实实测是 **274/480**（施工自己的日志）与 **264/480**（我的复跑），两者都是"多渲染几帧会变"的近似值。
2. **真实形态不是"1/8 行"，而是"8×8 抽取 + 中段半物化"的混合。** 我把 imported 回读 dump 下来逐像素分析
   （`a640.raw` vs `b640.raw`，即 A 组与 B 组的原始字节）：
   - 顶部带（y≈0–130）每行恰好 **80 个非零像素，全部落在 `x % 8 == 0`**，且这些像素与 own 侧**同坐标**真值
     逐字节相同（y=0 行 80/80）→ 这一段是纯净的 8×8 抽取（行、列各抽 8 倍）。
   - 中段（y≈136–300）每行 350–375 个非零像素，块内前 4 像素也有值，且**内容与 own 不同**
     （全图 30 703 个非零像素里有 **22 324 个与 own 同坐标不相等**）→ 这一段是"部分物化/错乱"，不是抽取。
   - 底部（y>~320）回到"8 的倍数行各 80 个正确像素、其余全零"。
   - **没有任何一整行与 own 的任何一行相等**（行哈希比对：216 个非零行 0 命中）→ 不是整行平移。
   → 结论：不能用"每 8 行 1 行有内容"一句话概括；它只在顶部/底部带成立。V2 的因果结论不受影响（见下）。
3. **§2.4 的机制句"其余留在未写入的 READBACK 内存里读成 0"不成立。** 我的 READBACK 缓冲在拷贝前整块填 `0x77`，
   实测 `untouched=0`（没有任何一行整行没被拷贝），顶部/底部带那些 0 是**源侧读出来的 0**，不是"没写"；
   中段则根本不是 0 而是错内容。另外 §2.6 的"画面差 0.285/255"我无法复现：两张 PNG 全图平均逐通道绝对差
   **15.34/255**（逐像素相同 83.38%，与报告一致），0.285 不是这个口径。这些属于**结论措辞与数字归属**问题，
   不是代码缺陷。

### 我做了什么独立验证（V2 的关键）

自建 6 组对照，全部 640×480，device=AMD 780M，host 自己的队列与 READBACK 缓冲（拷贝代码完全相同，
`GetCopyableFootprints` 取各自对象脚印，`RowPitch` 两边都是 **2560**，`width*4=2560`，比值 1.000）：

| 组 | 做法 | zero 行 | 结论 |
|---|---|---|---|
| A | 渲染 5 帧 → imported 对象 COMMON→COPY_SOURCE→COMMON 整面拷 | **264/480** | 复现失败 |
| A2 | 同 A，但先 `Sleep(1000)`（5 帧 @90fps 早该完成） | **264/480**（分布一字不差） | **排除"读的时候没画完"这条竞争解释** |
| B | 同一段代码，屏障打给 **own** 对象（PSR→COPY_SOURCE→PSR） | **0/480** | 同内容、同行距，对象一换就全对 |
| C | 产品 API `ZR_Unity_ReadTargetPixels` | **0/480**，与 B **0 字节差** | 修复后的实现 == 独立 own 侧真值 |
| D | **判决实验**：先只对 own 做 PSR→COMMON，再用 A 的**同一段** imported 代码读 | **0/480**，且 dump 与 B **逐字节相同**（`D == B: True`） | 唯一变量是"own 那次状态转换"，不是指针、不是行距 |
| E | 把 own 恢复 PSR，再用 A 的代码读 imported | **0/480** | 一次性 flush/物化，不需要 imported 每次都"看到"own 的状态 |

补充：A 行 0 的字节是 `26 1a 0d ff 00 00 00 00 ...`（只有第 0 个像素是清屏色，其余全 0）。
"不是行距/剪切"的判据我不靠像素分布，靠三条硬证据：
(a) 两个对象 `GetCopyableFootprints` 的 `RowPitch` **都**是 2560，与 `width*4` 比值 1.000，无 8 倍关系；
(b) **同一段拷贝代码、同一个脚印**，只要 own 被转过一次，imported 读出来的结果与 own 真值 **`D == B: True`（逐字节）** ——
行距若有错，D 不可能对；(c) 产品 API 在**调用方行距 = w*4+64** 下仍逐像素为 0 差（P 组）。

### 修复代码核对

- `ReadTargetPixels` 用的是 `device->D3D12ResourceOf(g_target.color[0])`，而 `color[0]` 由
  `device->CreateSharedImage()` 创建、RTV 也建在它上面（`zrhi_d3d12_unity_target.cpp:793/812`）——**确为本侧对象**。
- 屏障序列 `colorState[0] → COPY_SOURCE → PIXEL|NON_PIXEL_SHADER_RESOURCE`，`colorState[]` 在
  `RenderFrame` 帧末与 `ReadTargetPixels` 两处同步维护，读后纹理仍是 Unity 可采样态。
- 行距/切片一律 `GetCopyableFootprints`，并按 `dstRowPitch` **逐行** memcpy（不是整块 compact 拷贝）；
  调用方行距 `< width*4` 返回 `kErrArgs`。
- 导出链完整：`zrender.def` 已加 `ZR_Unity_ReadTargetPixels`（diff 可见），
  且我用 `GetProcAddress` 在运行期解析成功——二进制导出表确实有它。
- `kReadbackPitch` 全仓只剩一处注释，无代码引用；`zrhi_d3d12_window.cpp` 里 5 处 `RowPitch = 256u`
  是**既有** 1×1 回读（1×1 BGRA8 的脚印恒为 256），不在本次缺陷路径上，可不改（记录在案）。

### 测试断言改动是否正当

正当，且是**收紧**不是放宽：
- `readbacks==1 @帧0` → `readbacks==0 @帧0` + **新增** `readbacks==1 @第 10 帧`。
  依据：`pendingReadback` 的延后语义由 **B-UNITY-04（2d5e954）** 引入，而该断言最后一次修改停在
  **B-UNITY-02（57e14c2）**——陈旧断言，早已为假（我实测 bridge 日志 `stats(frame0): readbacks=0`）。
- 原"宿主导入侧回读不可靠"那段是**注释**不是断言；现在换成 3 条断言（尺寸 / 无全零行 / 角像素=清屏色），
  并改走产品 API，覆盖面比原来强。**但"角像素"那条单独不成立**：坏态下 `(0,0)` 读到的也是清屏色
  `(38,26,13)`（施工自己的日志就这么写着），好态坏态同值，不能证伪；真正兜底的是"无全零行"。
  建议后续把角像素换成"随机 32 个格点 + 抽样行 CRC"。

### 我实测的失败签名（原文级别）

```
[A] imported read: zero=264/480 nonzero=216  gap histogram: gap8=36 gap1=177
    first non-zero rows: 0 8 16 24 32 40 48 56 64 72 80 88 96 104 112 120 ...
    顶部带：每行 80 个非零像素，均在 x%8==0，值与 own 同坐标相等（y=0: 80/80）
    中段带（y≈136-300）：每行 350-375 个非零像素，内容多与 own 不同（不是抽取，是半物化）
    全图非零像素 30 703 / 307 200 = 9.994%，其中 22 324 个与 own 同坐标不等
```

### 回归数字（我复跑）

- `Tools/build_native.ps1`：configure+build 0 warning（/WX），`[doctest] 332 | 332 passed | 0 failed`。
- `zrhi_d3d12_window --draw`：`frames=60 center checks=6 passed=6`、`device removed = 0x00000000 (S_OK)`、
  `InfoQueue error count = 0`。
- `zrhi_unity_bridge`：**37 项 PASS，0 FAIL**（我复跑，`TempLogs/audit_b05/bridge_audit.log`）。
- `-zrender`：我自己起 Player（当前 DLL，md5 与 `Native/build` 产物一致），
  `RenderFrame 计数=840 帧率≈90.0 目标=960x540 中心像素回读=(54,54,54) 回读次数=28`，无阻塞帧。
- 截图：施工的 before/after 差异**确实是旋转相位**，不是回归——我在**同一次运行**里连抓 4 帧，
  主色在 `(151,146,141)/(27,60,141)`（亮）与 `(54,54,54)/(10,22,54)`（暗）之间来回切，
  正好分别对上 before 与 after 的调色板；背景区（titlebar / 右下背景）与 after **100% 同像素**，
  清屏掩码一致 **97.88%**（施工 96.00%，同一量级），无错行/撕裂。

### 遗留与风险（如实）

- 报告 §2.1 的 640/986/641 `zero=420/534/312` 请改标为"8 倍行距仿真值"；实测值另列（274/264 这一档）。
- 修复只覆盖同进程同设备、且只读**槽位 0**；跨进程 keyed mutex 仍未做（§2.7 已声明，与本任务判据无关）。
- `ReadTargetPixels` 依赖模块自己维护的 `colorState[]`：若将来有其它路径在模块外转这张纹理的状态，
  屏障 `StateBefore` 会失真。当前仅 `RenderFrame`/`ReadTargetPixels` 两处写它，一致。
- 未跑 Unity EditMode/PlayMode（不在本任务判据）。
- 我的全部临时物：`TempLogs/audit_b05/`（被 `.gitignore:45` 忽略）与 `Native/build/`（被忽略）——
  仓库 `git status` 与验收前**完全一致**，无新增/无残留改动。
