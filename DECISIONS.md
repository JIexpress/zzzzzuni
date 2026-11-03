# DECISIONS.md —— 决策记录（ADR 简版）

> 已定决策不再重议；要改需用户明确批准并在本文件追加新条目。

## ADR-001 双轨架构（2026-10-23，用户确认方案 v2）
Track A 玩法框架（纯 C# 分层）+ Track B 渲染内核（自研 SRP 壳+原生 C++）。R5 对等闸门前 URP/默认管线兜底。

## ADR-002 技术选型全按方案推荐执行（2026-10-23，用户："有推荐的选项就按你推荐的来"）
- UI=UGUI+TMP；玩法框架零第三方依赖；配置 SO+CSV 双轨；存档明文 JSON（可后加加密开关）；代号 ZFrame/ZRender
- Track B 启动时机：M0 治理基座先行后 A/B 并行
- 超分：FSR4.1（AMD 主路径，本机可验）+ FSR3.1（非 AMD 回退）首期必做；DLSS5 走 Streamline 骨架（本机不可验，受限项）；XeSS 默认放弃（R6 时可重议）；帧生成 R6 后可选
- Vulkan SDK（约 1.5GB）批准在 R2 前安装
- R5/R6 视觉对等由用户人工过目各一次
- 允许引入开源项目辅助（下载仅 https+可信域名，体积上限 2GB 级）

## ADR-003 工程治理三角色（2026-10-23）
主控/施工/验收分离；施工与验收必须不同子代理；FAIL 重派≤3轮；一任务一 commit；HANDOFF.md 唯一权威状态源；一切状态落盘防中断（用户明确要求强制执行）。

## ADR-004 构建产物入库策略（2026-10-23）
`Native/build/`、Unity `Library/ Temp/` 不入库；`UnityProject/Assets/Plugins/ZRender/zrender.dll` **入库**（clone 即可跑 Unity 侧，无需 CMake 环境）。dll 由 build_native.ps1 自动拷贝并附带版本头。

## ADR-005 test-framework 包追认（2026-10-23）
A-M0-02 施工中为运行 EditMode 测试补装 com.unity.test-framework 1.1.33（标准官方包，主控追认）。工程缺该包是 2022.3 新建工程的模板差异。

## ADR-006 验收适应性变通（2026-10-23）
Unity 批处理存在退出僵死问题（已遇 2 次）。当验收代理因此停滞时，允许：主控中立运行测试取证 + 独立代理做代码级复核，二者合成验收结论；Unity 批处理纪律（锁检查/轮询/taskkill）写入 HANDOFF 环境节，所有代理必须遵守。

## ADR-007 R5 对等闸门通过（2026-10-25）
自动指标+主控目检描述齐备后，用户回复"继续"，按 ADR-002 授权记录为闸门通过（差异清单 9 条留存可返工）。R5 里程碑全部收口。
