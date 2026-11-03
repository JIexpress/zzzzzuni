# TASK-B-R9-16 —— GPU 工具链（方案 §5.7）：DrawCall/显存 HUD + RenderDoc 一键捕获 + RG 导出

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

### 背景

需求达成对照（Docs/需求达成对照.md）第二区确认缺口：方案 §5.7 承诺的 GPU 工具链未建——且已被两次验收实际感知（B-R9-07 "描述符级证明需 RenderDoc 之类外部工具"、R9-10 的 VK 离屏排查全程盲飞）。

**目标（三件，全部走已有 DevServer/调试面板基建）**：
1. **DrawCall/显存 HUD**：调试面板（A-M0-05 的 DebugPanelRoot）新增渲染统计页——每帧 DrawCall 数（从命令表计数器——B-R2-02 起窗口程序有 DrawIndexed 计数，**经 DevConsole 暴露**）、RT 分辨率、显存（已建资源字节统计 zrhi_device——GetDeviceStats 扩展 or 现有 stats 汇总）
2. **RenderDoc 一键捕获**：DevConsole 新增白名单命令 `renderdoc.capture`——通过 RenderDoc in-application API（`renderdoc_app.h` 通用头，公开单头文件）在目标帧触发捕获；不引第三方运行时（RenderDoc 本体仍是用户手动开），产出 .rdc 到固定目录+返回路径。若 RenderDoc API 不可用（未挂接）→ 明确返回错误
3. **RG 导出**：RenderGraph 编译产物导出为文本/JSON（pass 列表+依赖边+屏障批次+别名计划——B-R3-01 数据都在）经 DevConsole 命令 `rg.export` 写盘；给排查"哪个 pass 黑屏/性能"提供数据

### 约束与要求

- 单测：JSON/文本导出格式纯函数、HUD 统计聚合纯函数、renderdoc 命令的参数与错误路径
- 变异验证 ≥1 条（如 HUD 统计断言改错即红）
- 自测：build 全绿（基线 281/771,162 只增不减）+ `--lights`/`--assets` 回归 + DevConsole 命令实测（`--devconsole` 若需开 flag 沿用 A-DEV-03 机制）
- **不碰 Unity**（Unity 侧 HUD 由 A 轨后续任务经 DevConsole 读数，本任务只做原生暴露）

**约束**：只动 `Native/zrender/`、`Native/zfgprobe/`（若 renderdoc 挂接在窗口程序层则不动）、`Native/tests/`、`Native/CMakeLists.txt`、`Native/ThirdParty/renderdoc/`（单头文件+SOURCE.md）、本任务日志；不 git commit；/W4 /WX；UTF-8 无 BOM/LF。

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减，数字写入结论）
- [ ] V2 亲跑：HUD 统计命令返回 DrawCall/显存数字（对照 --lights 已知 DrawCall=16 口径）；rg.export 产出文件（内容抽查 pass 名与屏障批次）
- [ ] V3 renderdoc.capture：未挂接时明确报错（证据）；挂接路径代码审查（行号）
- [ ] V4 变异复做 1 次（HUD 断言改错即红）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
