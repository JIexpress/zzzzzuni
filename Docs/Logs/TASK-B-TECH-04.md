# TASK-B-TECH-04 —— 行星尺度第二阶段：球面地形 chunk + LOD + 大气渐变

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

### 背景

B-TECH-03 验证了数学（浮点原点/球面重力/球体渲染雏形）。第二阶段把它推向"真的行星地表"：**球面地形 chunk 化**（cube-sphere 六面→grid 贴球）、**程序化高度噪声**（顶点位移）、**距离驱动 LOD**（近高分/远低分）、**大气天色渐变**（贴地蓝→高空黑，方案"飞出大气层"的视觉层）。

### 设计要点

1. **Cube-sphere**：立方体六面 grid→投影到球面（标准化立方体映射，避免极点挤压）——`PlanetChunk`：面 id+grid 位置+尺寸；顶点高度=噪声（value/simplex 纯函数——种子可配）
2. **LOD**：每面 quadtree 两档起步（远档 16×16、近档 64×64）——按 chunk 中心与相机距离切换；**无缝**：共享边顶点必须一致（同噪声函数同采样——T-junction 裂缝用裙边 skirt 或一致采样，选型报告说明）
3. **大气**：全屏或天穹着色——天顶色/地平线色随相机海拔插值（贴地蓝天→2 万 km 黑）；地平线雾近似
4. **重定位与重力沿用 B-TECH-03**（planet_math.h 不改语义——新功能增量）
5. **性能预算**：chunk 总数（如 6 面×2 档×N 块）与帧耗报告（`--planet2` 模式 300 帧均值）；DrawCall 数（B-R9-16 的 hud 命令实测）
6. 可证伪：①chunk 缝隙无裂缝（相邻 chunk 共享边顶点位置逐位一致——doctest 断言）②LOD 切换前后同点采样高度一致（噪声纯函数保证）③大气渐变像素证据（贴地 vs 高空截图天色差异）④帧耗在预算内（报告数字 vs B-R9-11 基线口径）
7. 通过行：`D3D12 PLANET2 OK (chunks=N lods=2 seams=0 frame_ms=X)`

### 约束

- 白名单：`Native/tests/`（planet_math 扩展+窗口程序 --planet2+doctest）、`Native/shaders/`（planet.hlsl 扩展或新增）、`Native/CMakeLists.txt`、`Docs/Parity/planet2_*.png`、本任务日志（Docs/Logs/TASK-B-TECH-04.md 自建三节）
- planet_math.h 既有函数语义不改（只增量）；零 zrhi 改动优先（需要时加法报告说明）；不碰 Unity；不 git commit；/W4 /WX；UTF-8 无 BOM/LF/#pragma once
- 基线：native 342/777,011 只增不减

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减，数字写入结论）
- [ ] V2 亲跑 `--planet2`：PLANET2 OK 数字行+三张截图（贴地/中空/远观）Read 亲验（地形起伏可见+天色渐变）
- [ ] V3 无裂缝断言（共享边逐位一致——行号）；LOD 档位切换逻辑（距离阈值/迟滞——行号）
- [ ] V4 大气渐变像素证据（贴地 vs 高空截图对比数字）
- [ ] V5 帧耗预算报告（chunk 数/帧耗，与 B-R9-11 基线口径对照）+文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
