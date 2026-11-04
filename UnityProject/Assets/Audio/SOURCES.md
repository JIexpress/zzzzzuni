# 音频资产来源与许可证（TASK-A-R9-14，2026-11-04 施工采集）

| 文件 | 来源 | 许可证 | 说明 |
|---|---|---|---|
| Resources/AUD_bgm_cave.ogg | 主控 2026-11-04 采集入库（TASK-A-R9-14 简报："CC0，主控已下载"，未随附 URL——URL 缺口见施工报告） | CC0（简报口径） | 原名 `BGM_Cave_Theme.ogg`（`Assets/Audio/Music/`），本任务按资源键约定改名入 `Resources/` |
| Resources/AUD_sfx_pickup.ogg | OGA "50 RPG sound effects"（Kenney Vleugels / kenney.nl）https://opengameart.org/content/50-rpg-sound-effects | CC0 1.0 | 包内原文件 `OGG/handleCoins.ogg` |
| Resources/AUD_sfx_door.ogg | 同上（"50 RPG sound effects"，CC0）https://opengameart.org/content/50-rpg-sound-effects | CC0 1.0 | 包内原文件 `OGG/doorOpen_1.ogg` |
| Resources/AUD_sfx_hit.ogg | 同上（"50 RPG sound effects"，CC0）https://opengameart.org/content/50-rpg-sound-effects | CC0 1.0 | 包内原文件 `OGG/chop.ogg` |
| Resources/AUD_sfx_ui_click.ogg | OGA "Interface Sounds"（Kenney Vleugels / kenney.nl）https://opengameart.org/content/interface-sounds | CC0 1.0 | 包内原文件 `Audio/click_001.ogg` |
| Resources/MIX_AudioMixer.asset | 本项目生成（ZFrame.View.AudioMixerSetup，A-M5-01） | — | 本任务从 `Assets/Audio/` 移入 `Resources/`，使 AudioService.FindBootMixer 运行期可解析（四总线真实生效） |

- 原始 zip 留存 `_asset_staging/sfx_a_r9_14/`（gitignore，不入库）：`rpg/RPGsounds_Kenney.zip`（691KB）、`ui/kenney_interfaceSounds.zip`（834KB）。
- 全部条目 CC0，无需署名；文件名 `AUD_` 前缀按 Docs/Standards/file-conventions.md §5，资源键映射见 `AudioService.AssetPrefix`（键 `sfx_pickup` → 文件 `AUD_sfx_pickup`）。
