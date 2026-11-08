# TASK-A-CI-02 —— CI 云端 Vulkan 用例补齐（249/281→全绿）

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

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

（待填）

## 验收清单（主控写）

- [ ] V1 workflow 含 Vulkan SDK 安装步（yaml 核对）
- [ ] V2 本地模拟验证：设 VULKAN_SDK → 条件化逻辑启用 VK 用例（本地全量 349 全绿证据）
- [ ] V3 推送后 Actions 结果（施工代理等待首轮 run 结果并记录——或列"待观察"由主控/用户核对）
- [ ] V4 无 SDK 环境跳过逻辑保留（本地清 env 复测跳过路径）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
