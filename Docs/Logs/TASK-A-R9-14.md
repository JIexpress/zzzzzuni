# TASK-A-R9-14 —— 听觉打磨包：AudioService 实战接线 + BGM/SFX 入库

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

### 背景

AudioService（A-M2-02）建好后**从未真正响过**——BGM 只入库了一首（`Assets/Audio/Music/BGM_Cave_Theme.ogg`，CC0，主控已下载），SFX 为零。本任务：音频全链实战 + 补齐 SFX 资产 + Demo.md 音频段。

### 目标

1. **SFX 资产补齐**（3-5 个，CC0/CC-BY 如实记录）：pickup（拾取）、door（开关门）、hit/attack（攻击）、ui_click——来源候选：OGA（**优先 .zip 格式包或单文件 ogg/wav 直链**；7z 包解压受限慎选）；主控已放 `Assets/Audio/SOURCES.md` 模板——补齐来源行；若下载 7z 包解压失败，换 zip 包或单文件（**不必死磕**，够 3 个 SFX 即可）
2. **接线**（`Game/View/Effects/GameplayFxBindings.cs` 扩展或新 `GameAudioBindings`）：场景/事件→AudioService：
   - BGM：主菜单开 BGM_A（可复用 Cave 主题）→ 进 SCN_Game 切游戏轨（同一首也可——语义：SceneLoader 事件驱动 SetBgm；报告说明单轨现状的取舍）
   - SFX：ItemPickedUp→pickup.ogg、DoorStateChanged→door.ogg、EnemyAttackRequested→hit.ogg（同帧去重 AudioService 已有）
   - 全部走 IAudioService（音量走设置页四总线——已实接）；Domain 零音频引用（校验器红线）
3. **PlayMode 可证伪**：拾取事件→AudioService 的 SFX AudioSource `isPlaying==true` 断言（用真接线，mock 不可代替全部——报告说明 EditMode mock 与 PlayMode 真放的比例）
4. Demo.md 补"音频"段（怎么加一个新音效：放文件→SOURCES→事件接线→总线——按实际路径）
5. 自测：EditMode+PlayMode 全绿（基线 785/20 只增不减）+ Validate exit 0

### 约束

- 白名单：`Assets/Audio/**`、`Assets/Game/View/**`、`Assets/ZFrame/View/Audio/**`（若服务需小扩展，报告说明）、`Tests/**`、`Docs/Demo.md`、本日志
- SFX 文件 ogg/wav（Unity 原生支持），单文件 ≤2MB，总量 ≤10MB
- Domain 零音频引用（校验器红线）；不 git commit；UTF-8 无 BOM/LF

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；事件→SFX 映射用例真实（行号）
- [ ] V2 PlayMode：拾取→SFX isPlaying 断言亲跑；BGM 轨活跃断言
- [ ] V3 SOURCES.md 齐全（每文件 URL/许可证）；Domain 零音频引用（grep）
- [ ] V4 Validate exit 0（自跑）；文件规范抽查
- [ ] V5 施工报告四节完整

## 验收结论（验收代理写）

（待填）
