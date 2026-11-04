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

**施工**: 施工代理（TASK-A-R9-14）｜**日期**: 2026-11-04

> **取证与复跑声明**：本报告的交付物、代码改动、资产来源与测试数字，全部取自当前工作区磁盘（`git status` / `git diff` / 文件正文 / `TempLogs` 下已有的 `results.xml` 与 `.log`）。**本次未运行 Unity 批处理、未复跑 EditMode / PlayMode 测试、未复跑架构校验器**——另一验收代理此刻正占用 Unity 单实例，并发会冲突。凡测试结论均逐条标明「证据来源 + 施工期/工作区已跑、本次未复跑」；未找到的证据写「证据待验收代理实跑补齐」，不补编任何数字。仅写本节，不动「验收结论」。

### 一、交付物清单

**修改（M，3 个）**

| 文件 | 性质 | 说明 |
|---|---|---|
| `UnityProject/Assets/ZFrame/View/Audio/AudioBus.cs` | 修改 | 仅 XML doc 文案：Mixer 路径 `Assets/Audio/MIX_AudioMixer.asset` → `Assets/Audio/Resources/MIX_AudioMixer.asset`，并注明「moved under Resources in TASK-A-R9-14 so the runtime lookup resolves」。枚举值 `Master/Bgm/Sfx/Ui` 与逻辑零改动。 |
| `UnityProject/Assets/ZFrame/View/Audio/AudioService.cs` | 修改 | 新增 `AssetPrefix = "AUD_"` 常量；`TryLoadClip` 的 `LoadAsync` 键与日志改用 `AssetPrefix + key`；`ReleaseBgmKey` 的 `Release` 键同步改为 `AssetPrefix + key`；注释/日志文案更新（详见 §四）。 |
| `Docs/Demo.md` | 修改 | 本任务音频段：§2 第 9 条体验项、§3.5「如何添加一个新音效」、§4 音频能力清单、§5 限制第 13/14 条。**本次未改动该文件**（按要求）。 |

**删除（D，3 个）——不是资产丢失，是移入 `Assets/Audio/Resources/` 并按 `AUD_` 前缀改名**

`git status` 中的 3 个 `D` 与 `?? Resources/` 是同一次搬迁的两端：旧路径删除、新路径未跟踪（git 未识别为 rename）。字节级校验如下（`git show HEAD:<旧路径>` 与工作区新文件对比，本次实测）：

```
MIX_AudioMixer.asset        : 5263 B, sha256 14982d6f95ee9591550223ad88723c20956f3bcb7b91190c70010b629517f072  (旧=新)
MIX_AudioMixer.asset.meta   : sha256 a8b0d115c5a47f072bbabc8c13d3d6d916ce433767bd423c59f46c9d420179db  (旧=新)
BGM_Cave_Theme.ogg          : 1490626 B, sha256 463fec1e34db1492e08f976c5bde7e9321cde62fbd9050ee59870386324add9c  (旧=新，仅改名)
```

| 旧路径（D） | 新路径 | 处置 |
|---|---|---|
| `Assets/Audio/MIX_AudioMixer.asset` | `Assets/Audio/Resources/MIX_AudioMixer.asset` | 移入 Resources，内容不变 |
| `Assets/Audio/MIX_AudioMixer.asset.meta` | `Assets/Audio/Resources/MIX_AudioMixer.asset.meta` | 随资产一起移入（guid 不变） |
| `Assets/Audio/Music/BGM_Cave_Theme.ogg` | `Assets/Audio/Resources/AUD_bgm_cave.ogg` | 移入 Resources + 按命名规范加 `AUD_` 前缀并归一命名，内容不变 |

**新增（??）**

| 文件 | 性质 | 说明 |
|---|---|---|
| `UnityProject/Assets/Audio/Resources.meta` + `Resources/` | 新增 | Resources 目录（6 个资产 + 6 个 `.meta`），使运行期 `Resources.Load` 可解析 |
| `UnityProject/Assets/Audio/Resources/AUD_*.ogg` ×5 + `.meta` ×5 | 新增 | 5 枚音频（1 BGM + 4 SFX），见下表 |
| `UnityProject/Assets/Audio/Resources/MIX_AudioMixer.asset` + `.meta` | 新增（移入） | 四总线 Mixer，见上 |
| `UnityProject/Assets/Audio/SOURCES.md` + `.meta` | 新增 | 音频来源与许可证逐条登记（含 BGM URL 缺口） |
| `UnityProject/Assets/Game/View/Effects/GameAudioBindings.cs` + `.meta` | 新增 | 事件→`IAudioService` 单点接线适配器（零轮询、EventScope） |
| `UnityProject/Assets/Game/Tests/View/GameAudioBindingsTests.cs` + `.meta` | 新增 | EditMode 10 用例（真 EventBus + recording mock） |
| `UnityProject/Assets/ZFrame/Tests/PlayMode/GameAudioBindingsPlayModeTests.cs` + `.meta` | 新增 | PlayMode 3 用例（`AudioSource.isPlaying` 真断言） |
| `Docs/Logs/TASK-A-R9-14.md` 本节 | 修改 | 本次补齐施工报告 |

**5 个 ogg 与 Mixer 的对应关系**（键→文件规则：`AudioService.AssetPrefix`，键保持 `bgm_/sfx_`，文件带 `AUD_`）

| Resources 文件 | 资源键 | 用途 / 事件 | 来源包内原文件 |
|---|---|---|---|
| `AUD_bgm_cave.ogg` | `bgm_cave` → BGM id `cave` | `SceneLoadedEvent` → `PlayBgm("cave")` | 原 `Assets/Audio/Music/BGM_Cave_Theme.ogg`（**URL 缺口，见 §二**） |
| `AUD_sfx_pickup.ogg` | `sfx_pickup` | `ItemPickedUp` → 拾取「叮」（3D） | `OGG/handleCoins.ogg` |
| `AUD_sfx_door.ogg` | `sfx_door` | `DoorStateChanged` → 门开/关「吱呀」（3D） | `OGG/doorOpen_1.ogg` |
| `AUD_sfx_hit.ogg` | `sfx_hit` | `EnemyAttackRequested` → 攻击「劈砍」（3D） | `OGG/chop.ogg` |
| `AUD_sfx_ui_click.ogg` | `sfx_ui_click` | `PanelOpenedEvent` → 面板「咔哒」（2D） | `Audio/click_001.ogg` |
| `MIX_AudioMixer.asset` | — | 四总线 `Master / BGM / SFX / UI`（设置页滑杆） | 本项目生成（`ZFrame.View.AudioMixerSetup`，A-M5-01），无外部许可证 |

> 提交提醒：3 个 `D` 必须与 `Resources/` 下的新文件**一起入库**，若只提交删除侧会造成资产真丢失。

### 二、SFX 来源与许可证（照 `Assets/Audio/SOURCES.md` 逐条如实登记）

| 文件 | 来源 | 许可证 | 包内原文件 |
|---|---|---|---|
| `AUD_sfx_pickup.ogg` | OGA「50 RPG sound effects」（Kenney Vleugels / kenney.nl）`https://opengameart.org/content/50-rpg-sound-effects` | CC0 1.0 | `OGG/handleCoins.ogg` |
| `AUD_sfx_door.ogg` | 同上（OGA「50 RPG sound effects」）`https://opengameart.org/content/50-rpg-sound-effects` | CC0 1.0 | `OGG/doorOpen_1.ogg` |
| `AUD_sfx_hit.ogg` | 同上（OGA「50 RPG sound effects」）`https://opengameart.org/content/50-rpg-sound-effects` | CC0 1.0 | `OGG/chop.ogg` |
| `AUD_sfx_ui_click.ogg` | OGA「Interface Sounds」（Kenney Vleugels / kenney.nl）`https://opengameart.org/content/interface-sounds` | CC0 1.0 | `Audio/click_001.ogg` |
| `MIX_AudioMixer.asset` | 本项目生成（A-M5-01） | — | — |

- **BGM 来源 URL 缺口（如实登记，严禁编 URL）**：`AUD_bgm_cave.ogg`（原 `BGM_Cave_Theme.ogg`）由主控 2026-11-04 采集入库，任务简报口径为「CC0，主控已下载」，**未随附来源 URL，也无许可证原文凭证**。`SOURCES.md` 第 5 行已如实标注「未随附 URL——URL 缺口见施工报告」，`Docs/Demo.md` §5 限制第 14 条同记。本报告**不填写、不推测任何 BGM URL**；补齐路径 = 查主控采集记录后回填 `SOURCES.md`。
- **原始 zip 留存**：`_asset_staging/sfx_a_r9_14/rpg/RPGsounds_Kenney.zip`（690952 B）、`_asset_staging/sfx_a_r9_14/ui/kenney_interfaceSounds.zip`（834536 B），均在 `.gitignore` 目录、不入库（本次已核存在，与 `SOURCES.md` 记录的 691KB / 834KB 一致）；解压出的 `handleCoins.ogg`(25394 B)、`doorOpen_1.ogg`(22848 B)、`chop.ogg`(9370 B)、`click_001.ogg`(4876 B) 与 4 枚 `AUD_sfx_*` 字节数逐一对应。
- 全部 SFX 与 BGM 简报口径均为 CC0；文件名 `AUD_` 前缀依 `Docs/Standards/file-conventions.md` §5。

### 三、事件接线（`Game/View/Effects/GameAudioBindings.cs`）

零轮询薄适配器：只在事件处理函数里干活，无 `Update`、无逐帧扫描；全部经 `IAudioService` 转发，音量走设置页四总线。`Build()` 在一个 `EventScope` 里订阅 5 个源（`:171–175`），`Shutdown()` 批量退订（`:210–227`），并自举挂载（`RuntimeInitializeOnLoadMethod(AfterSceneLoad)`，在 AudioService 的 `BeforeSceneLoad` 之后，保证生产 resolve 拿得到服务）。

| 事件 | 转发 | 位置/形态 |
|---|---|---|
| `SceneLoadedEvent` | `StartBgmForScene` → `BgmIdForScene` → `PlayBgm(bgmId)` / `StopBgm()`（`:240–243`, `:273–294`） | BGM；`SCN_Boot`/`SCN_Game` → `cave`，未映射场景淡出 |
| `ItemPickedUp` | `RequestSfx("pickup", ...)`（`:245–249`） | 3D，`evt.Position` 经 `ToWorld` 映射（X→X、Y→Z、固定高度 `DefaultSfxHeight=1`） |
| `DoorStateChanged` | `RequestSfx("door", ...)`（`:251–256`） | 3D，开门与关门各响一次 |
| `EnemyAttackRequested` | `RequestSfx("hit", ...)`（`:258–263`） | 3D，取 `evt.PlayerPosition`（挥砍落点/受击位，与命中火花同约定） |
| `PanelOpenedEvent` | `RequestSfx("ui_click", null, ...)`（`:265–269`） | 2D（`spatialBlend=0`），UI 事件不带世界坐标 |

- **Domain 零音频引用是架构校验器红线**：`GameAudioBindings` 是唯一同时知道 Domain 事件与音频层的地方（`Game/View/Effects/`），Domain 层不引用任何音频类型；架构校验器对 `Assets/Game/Domain` 做纯度扫描。本次未复跑校验器，但工作区已有 `TempLogs/arch_report.txt`（02:43:40）记录纯度扫描目录含 `Assets/Game/Domain`、**违规数 0 / PASS**（详见 §五）。
- **同帧去重由 `AudioService.SfxLimiter` 承担**：接线方不做去重；`AudioService.PlaySfx` 在同步加载前先 `_limiter.TryPlay(id)`（`AudioService.cs:257`），同 id 同帧与每帧预算 `MaxSfxPerFrame=4` 兜底，`Tick` 里 `BeginFrame()` 重置预算（`:330`）。因此一串拾取事件只出一声音。
- **单轨 BGM 现状与取舍**：曲库只有一首 `AUD_bgm_cave.ogg`，`BgmIdForScene`（`:100–108`）把 `SCN_Boot`（主菜单）与 `SCN_Game` 都映射到 `"cave"`，未知场景返回 `null` → `StopBgm()` 淡出。取舍：`PlayBgm` 仍走真实的双轨等功率交叉淡化（`AudioService.PlayBgm`/`Tick`），「SceneLoadedEvent→切轨」语义真实、可证伪，只是两场景当前指向同一 loop、听感无变化；第二首曲目 = 放一枚 `AUD_bgm_<名>.ogg` + 映射表加一行，无需改接线。此外 `Build()` 会对**当前活动场景**补一次映射（`:190–191`），因为 `SCN_Boot` 不经 SceneLoader、其事件早于绑定存在。

### 四、`AudioBus.cs` / `AudioService.cs` 改动目的与 Mixer 移入 Resources 的原因

**读 `git diff` 后的结论（不猜）**：

- `AudioBus.cs`：**仅注释**，把 doc 里的 Mixer 路径改成 `Assets/Audio/Resources/MIX_AudioMixer.asset` 并注明「移入 Resources 使运行期 lookup 可解析」。枚举与总线逻辑零改动。
- `AudioService.cs`：唯一的**新逻辑**是常量 `AssetPrefix = "AUD_"`（`:71`）。其余三处是围绕它的配套：
  1. `TryLoadClip`：`resources.LoadAsync<AudioClip>(key)` → `LoadAsync(AssetPrefix + key)`，日志中的键也换成 `assetPath`（`:405–430`）——让公开 API 键 `PlaySfx("pickup")` / `PlayBgm("cave")`（`sfx_`/`bgm_` 前缀，保持稳定）映射到按 `file-conventions.md §5` 命名的真实文件 `AUD_sfx_pickup` / `AUD_bgm_cave`；`ResourcesProvider` 把路径 1:1 落到 Resources 目录，故资产必须位于 `Assets/Audio/Resources/`。
  2. `ReleaseBgmKey`：`resources.Release(key)` → `Release(AssetPrefix + key)`（`:437–449`）——`ResourceService` 缓存以 `LoadAsync` 传入的字符串为键，释放必须用同一字符串，否则缓存释放不到（diff 注释明确写了这一点）。
  3. 注释/日志文案：说明 `AssetPrefix` 映射、Mixer 新路径、以及 `FindBootMixer` 在 `BeforeSceneLoad` 解析；`FindBootMixer()` 方法体本身（`Resources.Load<AudioMixer>("MIX_AudioMixer")`，`:114–126`）**未改**。

**Mixer 移入 Resources 的原因**：`FindBootMixer()` 一直用 `Resources.Load<AudioMixer>("MIX_AudioMixer")`，而原 asset 位于 `Assets/Audio/`（不在任何 Resources 目录下），运行期必然返回 `null` → 服务退化为「音量仅缓存、等场景 wiring 调 `SetMixer` 才生效」，设置页四总线滑杆并不真正作用到 Mixer 组。本任务把 `MIX_AudioMixer.asset` 连同 `.meta` 移入 `Assets/Audio/Resources/` 后，`AutoBoot`（`BeforeSceneLoad`）→ `Initialize(FindBootMixer())` 能直接解析到 Mixer，`SetMixer` 随即解析 `BGM/SFX/UI` 三个 group 并把缓存音量应用到暴露参数（`MasterVolume/BGMVolume/SFXVolume/UIVolume`）——**四总线真生效**。注意：使四总线生效的是「资产位置移动」，不是改 lookup 代码（diff 里 `FindBootMixer` 逻辑未动），故 `AudioBus.cs` 的注释更新与资产搬迁是配套的一体改动。

### 五、测试证据（工作区已有，**本次未复跑**）

> 统一声明：下列所有 `results.xml` / `.log` / 报告均为**施工期或并行代理在工作区已生成**的证据文件，本次施工代理**只读取、未复跑**（Unity 单实例被验收代理占用）。验收清单 V1–V4 的「亲跑 / exit 0」仍需不同代理独立实跑后写入「验收结论」。

**EditMode（全绿，多份独立证据一致）**

- `UnityProject/TempLogs/r914_editmode.xml`（文件时间 11-04 08:39，xml `start-time="2026-11-04 00:39:36Z"`）：`<test-run total="873" passed="873" failed="0" result="Passed">`。其中 `GameAudioBindingsTests` fixture（`:597`）`testcasecount="10" total="10" passed="10" failed="0" result="Passed"`，10 个用例逐条 `Passed`（`:599–626`）：`BgmIdForScene_MapsMenuAndGameToTheLibraryTrack`、`Build_ResolvesTheRegisteredServiceAsProductionWould`、`Build_SubscribesExactlyOncePerSource`、`DoorStateChanged_PlaysDoorSfxOnOpenAndCloseAlike`、`EnemyAttackRequested_PlaysHitSfxAtTheTargetPosition`、`ItemPickedUp_PlaysPickupSfxAtTheItemPosition`、`PanelOpened_PlaysUiClickSfx_2D`、`SceneLoadedEvent_StartsBgmOnMappedScenes_AndFadesOutOnUnmapped`、`Shutdown_UnsubscribesAllSources`、`WithoutService_BuildStaysPassiveAndNeverThrows`。
- 同源全量 EditMode 证据（均 `873/873/0 Passed`）：`TempLogs/runtests_editmode.xml`（07:18）、`TempLogs/b_r9_17_verify.xml`（07:20，B-R9-17 验收）、`TempLogs/a14_neutral2.xml`（08:29）。
- `TempLogs/r914_editmode_wrapper.log`：`RESULT: ALL PASS (1 passed, 0 skipped, 0 failed)`、`[07:19:05] attempt 1 exit=0`（editmode 段）。
- 源码点数与 fixture 一致：`Game/Tests/View/GameAudioBindingsTests.cs` 有 **10** 个 `[Test]`（`:64/74/121/151/182/193/206/219/232/243`）；`Docs/Demo.md` §4 亦写「EditMode mock 映射 10 例」。
- **可引用的并行任务出处 + 口径差异（如实登记）**：`Docs/Logs/TASK-B-R9-17.md:87`、`:93`、`:111` 记载「工作区含 A-R9-14 已入库的 **11 个 GameAudioBindings 用例、EditMode 873**」，且 `TempLogs/b_r9_17_verify.xml` 的 873 与上列证据一致。但本任务源码点数与 `r914_editmode.xml` 的 fixture 实证均为 **10** 个 EditMode 用例。`11` 与 `10` 的统计口径差异（可能含其它套件引用或不同计数方式）**请验收代理核对**，本报告不擅自改写该数字、不据此编造结论。

**PlayMode（未全绿——重要证据缺口）**

三份全量 PlayMode xml 均为 `total="44" passed="43" failed="1" result="Failed(Child)"`，且失败用例在 A-R9-14 自身用例间漂移：

| 证据文件 | 时间 | 失败用例 | 失败断言 |
|---|---|---|---|
| `TempLogs/runtests_playmode.xml` | 07:23 | `DoorHitAndUiClickEvents_EachRingTheirMappedClip`（`:2027`） | `a door toggle must really play the door clip` → `But was: null`（堆栈 `GameAudioBindingsPlayModeTests.cs:181`；输出含 `ResourceService: Release called for 'bgm_cave' which is not cached. Ignoring.`） |
| `UnityProject/TempLogs/r914_playmode.xml` | 08:42 | `DoorHitAndUiClickEvents_EachRingTheirMappedClip`（`:2143`，fixture `:2138` 为 `3/2/1`） | 同上分组 |
| `UnityProject/TempLogs/r914_playmode2.xml` | 08:57 | `SceneLoadedEvent_StartsAReallyPlayingBgmTrack_VolumeRidesTheLoopTick`（`:2109`） | `volume > 0.5` 的交叉淡化爬升断言 → `But was: 0.0f`（堆栈 `GameAudioBindingsPlayModeTests.cs:144`） |

- `UnityProject/TempLogs/r914_playmode2.xml` 的 `GameAudioBindingsPlayModeTests` fixture（`:2094`）`total="3" passed="2" failed="1"`：`ItemPickedUp_PlaysAReallyPlayingSfxSource_AtTheItemPosition`（`:2104`，**简报目标 3 强制的 `isPlaying` 真断言**）与 `DoorHitAndUiClickEvents_EachRingTheirMappedClip`（`:2099`）为 `Passed`，BGM 用例为 `Failed`。即 **pickup→SFX 真放稳定通过，BGM 轨活跃/交叉淡化断言在当前证据里为红**。
- wrapper / 报告旁证：`TempLogs/r914_playmode_wrapper.log` `RESULT: FAILED (1 failed: playmode)`、`[07:23:06] attempt 1 exit=1`；`TempLogs/r914_playmode_attempt.log` `playmode FAIL total=44 passed=43 failed=1 (Unity exit=2)`；仓库根 `TestReport.txt` 同样记 `[1] playmode -> FAIL / total=44 passed=43 failed=1`。
- 诊断/过滤小 xml（非全量，供定位参考）：`TempLogs/r914_filter.xml`（3/1/2 Failed）、`TempLogs/r914_probe.xml`（1/0/1 Failed）、`UnityProject/TempLogs/r914_diag.xml`（1/0/1 Failed）；对应 `.out` 为 `UNITY_EXIT_CODE=2 / 1 / 2`。
- **结论（如实）**：简报目标 5「EditMode+PlayMode 全绿（基线 785/20 只增不减）」中的 **PlayMode 全绿未达成**；V2 的「BGM 轨活跃断言」在当前工作区证据里为红。**不宣称全绿、不编数字**；`证据待验收代理实跑补齐`（需独立亲跑 PlayMode 并定位 door 分组 / BGM crossfade 用例的失败根因，例如共享 AudioService 实例与 Tick 驱动时序）。

**架构 / 配置 / 命名校验器（Validate 类证据）**

- `TempLogs/arch_report.txt`（02:43:40）：asmdef 扫描 25，**纯度扫描目录含 `Assets/Game/Domain`**，违规数 0，结果 PASS —— 支撑「Domain 零音频引用」红线。
- `TempLogs/config_report.txt`（02:44:51）：违规数 0 / PASS；`TempLogs/name_report.txt`（02:45:43）：扫描 `Assets/`、违规数 0 / PASS（含 `AUD_` 命名）。
- `TempLogs/runtests_arch.log:32` 显示 `ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` 被调用、`:329` 报告写入 `arch_report.txt`、`:334` `结果: PASS`；并行任务 `Docs/Logs/TASK-B-R9-17.md:89` 另记「架构校验器 `ValidateFromCommandLine` **exit 0**」（出处为该并行任务验收日志）。
- **未找到 A-R9-14 专属的「Validate exit 0」实跑记录**；V4 的「Validate exit 0（自跑）」**证据待验收代理实跑补齐**。上述三份报告仅作为施工期已生成的校验器结果引用，本次未复跑。

### 六、已知受限与风险

1. **BGM 来源 URL 缺口（合规首要项）**：`AUD_bgm_cave.ogg` 只有简报口径「CC0，主控已下载」，无 URL、无许可证原文，无法自证来源。`SOURCES.md` 与 `Docs/Demo.md` §5.14 已如实登记；对外发布/合规审计前须查主控采集记录回填 URL 与许可证凭证。本报告严禁编 URL。
2. **单轨 BGM**：曲库仅 1 首，`SCN_Boot` 与 `SCN_Game` 同指 `"cave"`（`BgmIdForScene`），交叉淡化双轨语义真实但听感上「菜单→游戏无切歌」；补第二轨 = 一枚 `AUD_bgm_*.ogg` + 映射表一行（§3.5 第 2 步）。
3. **PlayMode 真放比例与稳定性**：EditMode 的 10 例全部是 recording mock（真 EventBus、假 `IAudioService`，只证映射）；真链路（`Resources` 真导入 → `AudioSource.isPlaying`）只有 3 个 PlayMode 用例，且三份全量证据均 `43/44`，失败在 `DoorHitAndUiClick` 分组与 BGM crossfade 用例间漂移，`pickup isPlaying` 稳定通过、BGM 的 `volume>0.5` 断言为红。**PlayMode 全绿未达成，证据待验收代理实跑补齐/定位**，请勿据此判定目标 5 完成。
4. **CC0 对外发布的署名 / 留存注意事项**：(a) CC0 法律上无需署名，仍建议随发布物附 `SOURCES.md` 的来源与许可证清单（OGA 两包 URL + 作者 Kenney / kenney.nl + CC0 1.0）以便溯源；(b) 原始 zip 与页面快照只留在 `_asset_staging/`（gitignore、**不入库**），仓库内无原始包副本，长期审计前应确认该目录有独立备份；(c) 若后续混入 CC-BY 素材，须按 `Docs/Demo.md` §3.5 第 1 步加署名；(d) 当前 BGM 的「CC0」仅为简报口径，对外发布前必须补齐凭证，否则该条不可自证。
5. **EditMode 用例数口径差异**：B-R9-17 日志称「11 个 GameAudioBindings 用例」，本任务源码与 `r914_editmode.xml` 实证为 10；验收时请统一口径（`Docs/Demo.md` §4 亦按 10 记）。
6. **3 个 `D` 的提交风险**：它们是移动到 `Resources/`，不是丢失（sha256 已证）；提交时必须把 `Resources/` 下新文件与旧路径删除一起入库，且 Unity 自动生成的 `.meta` 须随资产同提交，否则会造成资产真丢失或 guid 漂移。


## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；事件→SFX 映射用例真实（行号）
- [ ] V2 PlayMode：拾取→SFX isPlaying 断言亲跑；BGM 轨活跃断言
- [ ] V3 SOURCES.md 齐全（每文件 URL/许可证）；Domain 零音频引用（grep）
- [ ] V4 Validate exit 0（自跑）；文件规范抽查
- [ ] V5 施工报告四节完整

## 验收结论（验收代理写）

**验收代理**: 独立验收代理 A-R9-14（与施工 `fixer-audio` / `builder-audio` 不同实例；本节全部数字为本次会话自跑 / 自查，施工日志数字仅作旁证并逐处标注）｜**日期**: 2026-11-04（20:04–20:21 北京时间）｜**总体结论**: **PASS with 受限**

> 取证原则：只认本次会话实跑的 `TempLogs/verifier_a_r9_14_*.xml`（脚本以本次新写 results.xml 为证据；跑前已 `mv` 归档施工代理的 `runtests_*.xml`，不复用冒充）与本次自查命令输出。施工报告 §五 / §七、`TempLogs/r914_*`、`b_r9_17_verify.xml` 等仅作旁证并明确标注。跑前 PowerShell 侧清 8 个代理变量（`[Environment]::SetEnvironmentVariable($n,$null,'Process')`）、脚本内置单实例检查 / 删 `UnityLockfile`、`-runTests` 未带 `-quit`；证据一律以 results.xml 为准，不看日志猜。

#### V1 EditMode 全绿 —— PASS

- 本次实跑（Run A，`-Segments editmode,playmode,arch,config,naming`）：`TempLogs/verifier_a_r9_14_runA_editmode.xml`（归档 20:14:29，506419 B，xml `start-time="2026-11-04 12:05:29Z"`）根 `<test-run testcasecount="873" result="Passed" total="873" passed="873" failed="0" skipped="0">`；脚本 SUMMARY：`editmode PASS total=873 passed=873 failed=0`。
  - 该段出现「xml 已写但 Unity 5 分钟未退」→ 脚本按纪律 `taskkill /PID 18436 /F`，`Unity exit=1` 为僵尸被杀、非测试失败；判定以本次新鲜 results.xml 为准（受限 §8）。
- 事件→SFX 映射**是真断言、非恒真**（`Game/Tests/View/GameAudioBindingsTests.cs` 行号）：
  - `ItemPickedUp_PlaysPickupSfxAtTheItemPosition:183–191`（`:187` `Assert.AreEqual(1, SfxPlays.Count)`、`:188` `Assert.AreEqual("pickup", SfxPlays[0].Id)`、`:189` 位置等值）；
  - `DoorStateChanged_PlaysDoorSfxOnOpenAndCloseAlike:194–204`（`:199` count=2、`:200–201` id="door"）；
  - `EnemyAttackRequested_PlaysHitSfxAtTheTargetPosition:207–217`（`:214` id="hit"）；
  - `PanelOpened_PlaysUiClickSfx_2D:220–228`（`:225` id="ui_click"、`:226` `Assert.IsFalse(Position.HasValue)`）；
  - BGM 映射 `BgmIdForScene_MapsMenuAndGameToTheLibraryTrack:233–241`、`SceneLoadedEvent_StartsBgmOnMappedScenes_AndFadesOutOnUnmapped:244–255`（`:248` "cave"、`:252` StopBgmCalls=1）。
  - 均为对 `RecordingAudioService` 记录的等值 / 计数断言，不是 `Assert.Pass()` 或恒真式。
- **用例数口径裁决 = 10**：源码 `[Test]` 实测 10 个（`:64/74/121/151/182/193/206/219/232/243`），本次 xml fixture `GameAudioBindingsTests` `testcasecount="10" total="10" passed="10" failed="0"`。故施工报告 §五与 `Docs/Demo.md` §4 的「10 例」成立；`Docs/Logs/TASK-B-R9-17.md` 的「11 个 GameAudioBindings 用例」与源码 / 本次 fixture 不符，不采信（疑为跨套件引用或不同计数方式）。按要求**不修改历史文档**，仅在此登记裁决（受限 §7）。
- 旁证（仅标注）：施工期 `UnityProject/TempLogs/r914_editmode.xml`（873/873/0、fixture 10）、返工轮 `r914_rework_edit1.xml`（873/873/0）。
- 限制：EditMode 10 例全是 recording mock（真 EventBus + 假 `IAudioService`），只证「事件→映射」，不证真放 / 听感（受限 §3）。

#### V2 PlayMode 全绿 —— PASS

- 本次实跑三次全绿 + 一次定向红（对照实验见 V5），均用 verifier 前缀**本次新写** xml：

| 轮次 | xml（归档时间 / `start-time` UTC） | test-run | fixture |
|---|---|---|---|
| Run A | `TempLogs/verifier_a_r9_14_runA_playmode.xml`（20:14:29 / `12:11:26Z`） | `total=44 passed=44 failed=0 result="Passed"` | `GameAudioBindingsPlayModeTests 3/3/0` |
| Run B（第 2 次连跑） | `TempLogs/verifier_a_r9_14_runB_playmode.xml`（20:16:40 / `12:15:47Z`） | `44/44/0 Passed` | `3/3/0` |
| Run C（变异） | `TempLogs/verifier_a_r9_14_runC_mutant.xml`（20:19:18 / `12:18:20Z`） | `44/43/1 Failed(Child)` | `3/2/1` |
| Run D（还原复绿） | `TempLogs/verifier_a_r9_14_runD_restore.xml`（20:21:35 / `12:20:22Z`） | `44/44/0 Passed` | `3/3/0` |

- 三用例逐项核对（`ZFrame/Tests/PlayMode/GameAudioBindingsPlayModeTests.cs` 本次文件内容）：
  - `ItemPickedUp_PlaysAReallyPlayingSfxSource_AtTheItemPosition:162–183`：`Assert.IsTrue(sfx.isPlaying)`（`:174`）真断言，`:177` clip 名、`:179` 3D `spatialBlend=1`、`:181` 位置。
  - `SceneLoadedEvent_StartsAReallyPlayingBgmTrack_VolumeRidesTheLoopTick:133–159`：BGM 轨活跃 `Assert.IsNotNull(bgm)`（`:140`）+ `Assert.IsTrue(bgm.isPlaying)`（`:143`）+ clip/loop（`:144/:146`）；`TickQueue.TickAll(View,0.1f)`×10（`:150–154`）后 `Assert.Greater(bgm.volume, 0.5f)`（`:156`）交叉淡化爬升断言。
  - `DoorHitAndUiClickEvents_EachRingTheirMappedClip:186–209`：door / hit / ui_click 各自 `FindPlayingSfxSource(DoorClipName/HitClipName/UiClickClipName)` + `IsNotNull` + `IsTrue(isPlaying)`（`:192–206`）；ui_click `Assert.AreEqual(0f, click.spatialBlend, 0.01f)`（`:207`）为 2D。
  - 本次 Run A xml 三用例 id `1014 / 1013 / 1012` 全 `result="Passed"`。
- **失败不再漂移**：本次 Run A / Run B / Run D 三次 `44/44/0`（不同 `start-time`、均本次新写）。施工期三份全量 `43/44/1` 且失败用例在 A-R9-14 自身用例间漂移（`runtests_playmode.xml` 07:23、`r914_playmode.xml` 08:42、`r914_playmode2.xml` 08:57）与返工轮 Run1/Run2/Run4 三次 `44/44/0` 均**仅作旁证**。

#### V3 资产与红线 —— PASS（含 1 项如实登记的受限）

- `UnityProject/Assets/Audio/SOURCES.md` 逐条核对 4 个 SFX：URL（`https://opengameart.org/content/50-rpg-sound-effects` ×3 / `https://opengameart.org/content/interface-sounds` ×1）、许可证 CC0 1.0、包内原文件名 `OGG/handleCoins.ogg` / `OGG/doorOpen_1.ogg` / `OGG/chop.ogg` / `Audio/click_001.ogg` 全部与表格一致；`_asset_staging/sfx_a_r9_14/rpg/x/license.txt`（`License (CC0) http://creativecommons.org/publicdomain/zero/1.0/`）与 `ui/x/License.txt`（`License: (Creative Commons Zero, CC0)`）原文交叉印证。4 枚 `AUD_sfx_*.ogg` 与 staging 解压原文件 sha256 逐一 MATCH（handleCoins `8a91f969…`、doorOpen_1 `4ab93bab…`、chop `d00c2b3c…`、click_001 `ccfb7fa0…`）。
- **BGM URL 缺口按「如实登记」判为受限项，不判 FAIL、不代编 URL**：`SOURCES.md:5` 与施工报告 §二 / §六.1 均如实标注「主控 2026-11-04 采集入库、未随附 URL、无许可证原文凭证」，简报口径仅「CC0」；补齐路径 = 查主控采集记录回填 `SOURCES.md`。本次验收未替它编造任何 URL。
- Domain 零音频引用：精确扫描 `Assets/Game/Domain`（`AudioSource|AudioClip|AudioService|AudioBus|AudioMixer|IAudioService|PlayBgm|PlaySfx|SetBusVolume|ZFrame.View`）**零命中**；唯一含 "audio" 字样者为 `Interaction/InteractionRegistry.cs:14` 的 XML doc 注释文字「(prompt HUD, audio, tutorial)」，非代码引用，如实登记。架构校验器报告（本次 20:12:42）纯度扫描目录含 `Assets/Game/Domain`、违规 0，双重印证。
- **3 个 `D` 与 `Assets/Audio/Resources/` 新文件是同一次搬迁**（`git show HEAD:<旧路径>` 导出到仓库外 `/tmp/a_r9_14_verify/` 后 sha256 + `cmp` 字节级比对）：
  - `MIX_AudioMixer.asset` sha256 `14982d6f95ee9591550223ad88723c20956f3bcb7b91190c70010b629517f072`（旧=新，`cmp` 零差异）；
  - `MIX_AudioMixer.asset.meta` sha256 `a8b0d115c5a47f072bbabc8c13d3d6d916ce433767bd423c59f46c9d420179db`（旧=新，`cmp` 零差异）；
  - `BGM_Cave_Theme.ogg` → `AUD_bgm_cave.ogg` sha256 `463fec1e34db1492e08f976c5bde7e9321cde62fbd9050ee59870386324add9c`、1490626 B（旧=新仅改名，`cmp` 零差异）。
  - 结论：一起入库不会丢资产；只提交删除侧会造成真丢失（受限 §4）。
- 5 枚 ogg 与 Mixer 的 `.meta` 全部存在（Resources 下 6 个资产 + 6 个 `.meta`，逐一点名 OK）。

#### V4 校验器与规范 —— PASS

- 三校验器本次实跑（Run A 同一批处理），**exit code 全 0**，报告均为本次新写（非 stale）：
  - `TempLogs/arch_report.txt`（20:12:42，asmdef 扫描 25、纯度扫描目录含 `Assets/Game/Domain`、违规 0 / PASS）；
  - `TempLogs/config_report.txt`（20:13:19，违规 0 / PASS）；
  - `TempLogs/name_report.txt`（20:14:00，扫描 `Assets/`、违规 0 / PASS）。
  - 脚本 SUMMARY：`arch / config / naming` 三段 `PASS exit=0`。
- 编码抽查（字节级）：12 个新增 `.cs` / `.md` / `.meta`（`SOURCES.md`、`GameAudioBindingsTests.cs`、`GameAudioBindings.cs`、`GameAudioBindingsPlayModeTests.cs`、各 `.meta`、`Resources.meta`、本日志）`first3` 均非 `EF BB BF`、`CRbytes=0`、`LFbytes>0` → UTF-8 无 BOM + LF。
- `AUD_` 命名规范：5 枚 ogg 全部带 `AUD_` 前缀，命名校验器 exit 0。
- 环境说明：脚本对三份报告出现 `safe-delete` 删除警告，但报告时间戳为本次 20:12–20:14、内容为本次「违规 0 / PASS」，未复用旧报告（受限 §8）。

#### V5 报告与变异 —— PASS

- 施工报告结构核对：`Docs/Logs/TASK-A-R9-14.md` 四节齐全（任务简报 / 施工报告六节 / 验收清单 / 验收结论），另有 §七 返工轮（第 1 轮）。
- **亲自复做「修前必红」定向变异**（唯一例外授权，仅动 `GameAudioBindingsPlayModeTests.cs` 的 SetUp；断言 / 用例体 / helper 一字未动）：
  1. 变异前 `cp` 自有备份 `TempLogs/verifier_a_r9_14_playmode_pristine.bak`（20:17:09，12975 B，sha256 `fe25457db6047e5cd871dad40495c71235217bc8db582a0667303922d2532093`），`cmp` 与当前文件零差异；与施工代理 `r914_rework_fixed_setup.bak` 交叉 `cmp` 零差异（旁证）。
  2. 定向变异：SetUp 精确改回旧逻辑（`if (AudioService.Instance == null)` 才新建 owned、不拆共享单例、不 `Shutdown`）。`diff -u` 仅落在 SetUp 段（备份 `:51–78` → 变异后 `:51–65`）；`sed -n '/\[UnityTest\]/,$p'` 两段对比**零差异**（用例体 / 断言 / helper 未动）；变异后仍无 BOM + LF。
  3. **变红**：Run C `TempLogs/verifier_a_r9_14_runC_mutant.xml`（`start-time=12:18:20Z`）`total=44 passed=43 failed=1 result="Failed(Child)"`，fixture `3/2/1`；**变红用例 = `SceneLoadedEvent_StartsAReallyPlayingBgmTrack_VolumeRidesTheLoopTick`（id 1012，变红用例数 1）**；失败断言 `volume > 0.5`（`Expected: greater than 0.5f / But was: 0.0f`，xml `:1977–1979`），与施工期 08:57 / 返工轮 §7.5 同型。前后数字：修复态 `44/44/0` → 变异态 `44/43/1` → 还原态 `44/44/0`。
  4. **立即字节级还原**：`cp` 自有备份覆盖回来，`cmp -s` 零差异，sha256 双方 `fe25457d…2093`；与施工代理 `fixed_setup.bak` 交叉 `cmp` 零差异；`git diff --stat` 仍只有 7 个原有 tracked 改动、无任何本轮新增 tracked 改动；`git status --short` 与变异前完全一致；全仓 `grep 'VERIFIER-A-R9-14 directional mutation'` **零命中**（无变异残留）。
  5. **复跑复绿**：Run D `TempLogs/verifier_a_r9_14_runD_restore.xml`（`start-time=12:20:22Z`）`44/44/0 Passed`、fixture `3/3/0`。
  - 结论：三个 PlayMode 用例未被放宽成恒真，SetUp 隔离修复是 `44/44/0` 的必要条件；「修前必红」由验收代理本次亲自复现。
- **施工代理「只改 SetUp、断言一字未动、产品代码零改动」核对**：
  - `git diff --name-status` 本次仅 7 个 tracked 改动（`Docs/Demo.md`、本日志、3 个 `D`、`AudioBus.cs`、`AudioService.cs`），全属施工报告 §一–§四 交付物，**无返工轮新增 tracked 改动** → 返工轮未碰产品代码（说法自洽）。
  - `git diff AudioBus.cs / AudioService.cs`：改动为 `AssetPrefix = "AUD_"` 常量、`TryLoadClip` / `ReleaseBgmKey` 的键加前缀、注释 / 日志文案、Mixer 路径注释，与 §四逐条吻合，不含任何测试隔离相关改动。
  - 断言一字未动：当前 `Assert.Greater(bgm.volume, 0.5f)`（`:156`）、`Assert.IsTrue(sfx.isPlaying)`（`:174`）、`Assert.IsNotNull(door, …)`（`:193`）、`Assert.AreEqual(0f, click.spatialBlend, 0.01f)`（`:207`）与 §7.2 描述的「修复后 `:156` / `:174` / `:193`」及本次变异前后 diff 一致；本次变异能检红，反证断言未被放宽。
  - 「只改 SetUp」佐证：施工期 xml 失败堆栈 BGM 断言 `:144` vs 当前 `:156`，差 12 行 = §7.2「SetUp 扩展 12 行」；`r914_rework_fixed_setup.bak` 与当前文件字节一致。
  - **证据边界（如实）**：该 PlayMode 测试文件是 untracked（本任务新增），git 无「返工前版本」可逐字 diff，故「只改 SetUp / 断言一字未动」无法用 git 历史逐字证明，只能以文件内容比对 + 施工报告自述 + `fixed_setup.bak` + 本次对照实验作旁证；本次未发现任何反证。
- 旁证（仅标注）：施工报告 §七.4 表格（Run1 `44/44/0` / Run2 `44/44/0` / Run3 变异 `44/43/1` / Run4 `44/44/0`）、`TempLogs/r914_rework_*.xml`。

#### 受限清单（如实登记，均不构成 FAIL）

1. **BGM 来源 URL 缺口（合规首要项）**：`AUD_bgm_cave.ogg` 无 URL、无许可证原文凭证，仅简报口径「CC0」；`SOURCES.md:5` 已如实登记。本次按「如实登记」判受限，**不判 FAIL、未代编 URL**。对外发布 / 合规审计前须查主控采集记录回填 URL 与许可证凭证。
2. **单轨 BGM**：曲库仅 1 首，`BgmIdForScene`（`Game/View/Effects/GameAudioBindings.cs:100–108`）把 `SCN_Boot` 与 `SCN_Game` 同指 `"cave"`；交叉淡化双轨语义真实可证伪，但听感上「菜单→游戏无切歌」。
3. **V1 编辑器目测类限制**：EditMode 10 例全部是 recording mock（真 EventBus + 假 `IAudioService`），只证事件→映射，不证 `AudioSource.isPlaying` 真放 / clip 导入 / 听感；真链路仅 3 个 PlayMode 用例。本次为批处理，未在 Unity Editor 内做人工听感目测，也未做真机 / Player 听感验证。
4. **3 个 `D` 的提交风险**：必须与 `Resources/` 下新文件一起入库（sha256 已证为同次搬迁）、`.meta` 随资产同提交；只提交删除侧会造成资产真丢失（本次未 `git add` / `git commit`，由主控处置）。
5. **CC0 署名 / 留存**：原始 zip 与页面快照仅在 `_asset_staging/`（gitignore、不入库），仓库内无原始包副本；CC0 法律上无需署名，仍建议发布物附 `SOURCES.md` 清单；若后续混入 CC-BY 素材须另加署名。
6. **返工轮改动无 git 历史版本可逐字比对**（untracked 文件）：见 V5 证据边界。
7. **用例数口径 11 vs 10**：裁决 10；`TASK-B-R9-17.md` 的「11」未采信，按要求未改历史文档。
8. **本机批处理行为**：Run A 的 EditMode 段「xml 已写但 Unity 5 分钟未退」→ 按纪律 `taskkill`、以本次新鲜 results.xml 判 PASS（`Unity exit=1` 为僵尸被杀，非测试失败）；三校验器报告有 `safe-delete` 删除警告但报告为本次新写、违规 0；均非代码缺陷。
9. **失败漂移的微观机制未逐帧 trace**：以「改回旧逻辑 → 同型 `43/44` 红 → 还原后 3 次 `44/44/0`」对照实验为准（同施工报告 §7.6.2），未逐帧断言单一微观机制。
10. **URL / 许可证未做外网复核**：本次仅核对 `SOURCES.md` 登记、包内 `license.txt` 原文与 sha256 对应关系，未发起外网请求核验 OGA 页面（治理铁律：对外发网络请求需停下来问人）。

#### 现场状态

- **HEAD**：`2d80a85129946b195a7ce1cafdde7439368f4017`（`main`），与本次验收开始时一致；未 `git add` / 未 `git commit`。
- **git status 摘要**：7 个 tracked 改动 + 9 项 untracked 新增，**全部属本任务**（音频资产 / `Resources/` / `SOURCES.md` / `GameAudioBindings.cs` / 两个测试文件 / 各 `.meta` / `Docs/Demo.md` / 本日志）；**无别的任务文件混入**；`URP_*.asset` / `QualitySettings.asset` / `ProjectSettings` / `zrender.dll` 均未被 Editor 改写（零命中）→ 无需针对这几个文件还原。
- **未触碰**：`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md` / `ZFrame/View/Hud/**` / `Native/**` / `ZRender/**`；除 V5 例外（已还原）外未改任何业务代码。
- **无残留**：无 `Unity.exe` 进程、无 `UnityProject/Temp/UnityLockfile`、全仓 `grep` 变异标记零命中、测试文件 sha256 == 变异前备份。清理只用 `mv` 归档（旧 `runtests_*.xml` → `TempLogs/verifier_a_r9_14_preexisting_*.xml`），未用 `rm -rf`。
- **本次新写证据清单**（`TempLogs/verifier_a_r9_14_*`）：`runA_editmode.xml`、`runA_playmode.xml`、`runB_playmode.xml`、`runC_mutant.xml`、`runD_restore.xml`、`playmode_pristine.bak`；旧证据归档 `preexisting_editmode.xml` / `preexisting_playmode.xml`。三校验器报告 `TempLogs/{arch,config,name}_report.txt`（20:12–20:14 本次新写）。
- 本日志「验收结论」一节保持 UTF-8 无 BOM + LF（写后复核）。

---

### 七、返工轮（第 1 轮）

**施工**: 施工代理（TASK-A-R9-14 返工轮）｜**日期**: 2026-11-04｜**裁定**: 主控判定 PlayMode 全量 `44/43/1` 为红、红着不能提交，本轮定位真因并修到全绿。

> 取证声明：本节数字全部来自本轮**亲跑** `Tools/run_tests.ps1`（PowerShell 工具、跑前清代理变量、单实例、`-runTests` 不带 `-quit`、以本次新写 results.xml 为准），xml 逐份另存 `TempLogs/r914_rework_*.xml`。未沿用施工期旧 xml，未改动 §一–§六、验收清单、验收结论。
>
> 行号基准：本节 `GameAudioBindingsPlayModeTests.cs` 的 file:line 除注明「修复后」外，均指**返工前现场版本**（主控裁定 `44/43/1` 的版本，BGM 断言 `:144`、door 断言 `:181`）；本轮 SetUp 扩展 12 行，其后行号 = 现场行号 + 12（如 BGM 断言修复后 `:156`、door `:193`）。其余文件行号为当前工作区版本。

#### 7.1 根因（测试侧真实缺陷：共享 auto-boot 实例跨用例状态污染）

- 现场 `44/43/1` 且失败用例在 A-R9-14 自身三用例间漂移；三用例执行顺序固定（xml id 1014 Door → 1013 ItemPickedUp → 1012 BGM），故**不是顺序随机**，而是**进入用例时的共享状态不同**。
- `SetUp`（现场 `:47–79`）只在 `AudioService.Instance == null` 时新建 owned 实例（现场 `:58`）；而 `AudioService.AutoBoot`（`AudioService.cs:96–107`，`RuntimeInitializeOnLoadMethod(BeforeSceneLoad)`）创建的 `DontDestroyOnLoad` 共享实例在三个用例间始终存活，`Instance` 永不为空 → owned 分支**永不触发**，三用例全程复用同一实例。
- `TearDown`（现场 `:81–118`）对共享实例只走 `else` 分支 `StopAllSources()`（现场 `:116`，helper 现场 `:267–302` 仅 `AudioSource.Stop()` + `clip = null`），**不调用 `AudioService.Shutdown()`**（`AudioService.cs:481–504`）。于是 `CrossfadeState._target/_fading/_elapsed`（`CrossfadeState.cs:19–23`）、`SfxLimiter._playedThisFrame/_passedThisFrame`（`SfxLimiter.cs:22–23`）、`_bgmKeys[]`（`AudioService.cs:81`）全部跨用例残留；同时 `Services` 的 `IResourceService` 每用例被 `Unregister` 并换成 `new ResourceService(new ResourcesProvider())`（现场 `:68–75`），TearDown 再恢复（现场 `:92–102`）。
- `GameAudioBindings.Build()` 对当前活动场景补一次映射（`GameAudioBindings.cs:190–191, 273–294`）：runner 场景未映射 → `StopBgm()`；随后 BGM 用例 `EventBus.Fire(new SceneLoadedEvent("SCN_Boot"))` → `PlayBgm("cave")`。`StopBgm`/`PlayBgm` 都按 `incoming = 1 - _crossfade.Target` 选轨（`AudioService.cs:215` / `:236`）并 `_crossfade.Start(incoming, fade)`（`:227` / `:243`；`CrossfadeState.cs:48–65`）。**从上一条用例残留的 fade 状态出发选轨**，使显式 `TickQueue.TickAll(TickStage.View, 0.1f)`×10（现场 `:138–142`）后交叉淡化权重未爬升到 `FindPlayingBgmTrack()` 返回的那条轨，`volume > 0.5` 断言（现场 `:144`）拿到 `0.0f`；同一残留状态在 door/hit 分组上偶发表现为拿不到 playing source（现场 `:181` `But was: null`）。即 `44/43/1` 漂移。
- **主控点名核对（结论：不是真因，当前代码不成立）**：`ReleaseBgmKey`（`AudioService.cs:437–449`）用 `resources.Release(AssetPrefix + key)`（`:445`）= `AUD_bgm_cave`，与 `TryLoadClip` 的 `LoadAsync(AssetPrefix + key)`（`:405–406`）缓存键**一致**；`_bgmKeys[track]` 存裸 key（`bgm_cave`）只是键的一部分，加载/释放两侧都拼 `AssetPrefix`。全仓 grep 无第二处用裸 key 对 `IResourceService.Release` 调用；`ResourceService.Release`（`ResourceService.cs:97–113`）对未缓存键只 `LogWarning` 并 return，`ResourcesProvider.Release`（`ResourcesProvider.cs:49–52`）是 no-op，**不会卸载真实 AudioClip**。现场那条 `Release called for 'bgm_cave' which is not cached` 裸 key 日志属**改动前的旧代码/旧证据**；本轮 4 次 PlayMode `.log` 与变异 `.log` 均无此日志。故「clip 被提前释放」不成立。
- 补充核对：`GameRoot.Initialize` 用同一实例注册 `TickQueue`/`ITickQueue`（`GameRoot.cs:127–128`），测试 `Services.Get<TickQueue>()` 与生产注册是同一队列，未误判为两个实例。

#### 7.2 修法（改测试代码，逐条理由）

本轮**只改测试隔离**：不改产品代码、不改任何断言、不加 `[Ignore]`/`[Explicit]`、不放宽 `volume > 0.5` / `isPlaying` 真断言、不加重试 / 加 yield 碰运气 / 放宽超时。

- **改动 1（唯一功能改动，`SetUp` 修复后 `:47–91`）**：改为**先拆掉共享实例、每用例建全新 owned 实例**——`if (AudioService.Instance != null) { shared.Shutdown(); Object.Destroy(shared.gameObject); }`（`Shutdown()` 先清 `Instance` 与 `IAudioService`/`ITickQueue` 注册，避免 `AudioService.Initialize` 的「another IAudioService is already registered」提前 return，`AudioService.cs:146–151`），随后 `Services.Unregister<IAudioService>()`（no-op when absent，`Services.cs:66–70`）保证干净 `Register`，最后 `new GameObject(...).AddComponent<AudioService>().Initialize(FindBootMixer())`。
  - 理由：消除跨用例残留 —— 每用例 crossfade 停在 track A idle（`CrossfadeState.cs:28–34`）、limiter 空、`_bgmKeys` 全 null、AudioSources 全新，且完整注册到 `ITickQueue(View)`，BGM 用例的显式 `TickAll` 必然驱动到本用例自己的实例。TearDown 的 owned 分支（修复后 `:116–125`，`Shutdown()` + `Destroy`）现在每用例都执行，形成确定性 teardown。
- **改动 2（仅注释）**：`SetUp` 顶部注释从「shared instance is destroyed」（与代码不符）改为如实描述销毁的是 `GameAudioBindings` host，并补一段说明为何必须拆 `AudioService` 单例。理由：原注释误导，是本次误判来源之一。
- 三个 PlayMode 用例体（`SceneLoadedEvent...` / `ItemPickedUp...` / `DoorHitAndUiClick...`）**一字未改**：`Assert.Greater(bgm.volume, 0.5f)`（修复后 `:156`）、`Assert.IsTrue(sfx.isPlaying)`（`:174`）、`Assert.IsNotNull(door, ...)`（`:193`）等全部原样；`FindPlayingBgmTrack`/`FindPlayingSfxSource` helper 原样保留。

#### 7.3 改了哪些文件

| 文件 | 性质 | 说明 |
|---|---|---|
| `UnityProject/Assets/ZFrame/Tests/PlayMode/GameAudioBindingsPlayModeTests.cs` | 修改（untracked，A-R9-14 新增文件） | 仅 `SetUp` 一段：拆共享单例 + 每用例建全新 owned 实例 + 注释订正。用例体/断言/helper 零改动。UTF-8 无 BOM + LF。 |
| `Docs/Logs/TASK-A-R9-14.md` | 追加 | 仅末尾追加本节（§七）；§一–§六、验收清单、验收结论未动。 |

`Docs/Demo.md` 本轮**未改**：音频链路/用例语义未发生事实变化，无需同步。产品代码（`AudioService.cs` / `GameAudioBindings.cs` / `AudioBus.cs`）**本轮零改动**。未 `git add` / 未 `git commit`；未触碰 HUD / `Native/**` / `ZRender/**` / `TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md`。

#### 7.4 实跑证据（本轮亲跑，xml 均为本次新写并另存）

| 轮次 | 命令 `-Segments` | 结果 | xml（另存路径） | xml `start-time`(UTC) / 归档时间(北京) |
|---|---|---|---|---|
| Run 1 修完 | `editmode,playmode,arch,config,naming` | PlayMode **44/44/0**、EditMode **873/873/0**、三校验器 exit 0 | `TempLogs/r914_rework_play1.xml`、`TempLogs/r914_rework_edit1.xml` | `11:42:09Z` / 19:44；EditMode `11:36:18Z` |
| Run 2 复验 | `playmode` | PlayMode **44/44/0** | `TempLogs/r914_rework_play2.xml` | `11:47:22Z` / 19:48 |
| Run 3 变异 | `playmode`（旧 SetUp） | PlayMode **44/43/1**（红） | `TempLogs/r914_rework_mutant.xml` | `11:51:55Z` / 19:52 |
| Run 4 还原复绿 | `playmode` | PlayMode **44/44/0** | `TempLogs/r914_rework_restore.xml` | `11:56:01Z` / 19:57 |

- 两次正式 PlayMode（Run 1 / Run 2）均 `total=44 passed=44 failed=0`，`GameAudioBindingsPlayModeTests` fixture 均 `3/3/0`，**失败不再漂移**。
- EditMode `873/873/0`（`TempLogs/r914_rework_edit1.xml`，`start-time=11:36:18Z`），不低于基线 873/873/0。
- 架构/配置/命名校验器：Run 1 三段均 `PASS / exit 0`；报告 `TempLogs/arch_report.txt`（19:43:21，违规 0，纯度扫描含 `Assets/Game/Domain`）、`config_report.txt`（19:43:59，违规 0）、`name_report.txt`（19:44:30，违规 0）。
- 环境纪律：每次跑前 PowerShell 侧清空 8 个代理变量、查 `Unity.exe`（无）、脚本内置删 `UnityLockfile`；`-runTests` 未带 `-quit`。Run 1 的 EditMode 段出现「xml 已写但 Unity 5 分钟未退」→ 脚本按纪律 `taskkill` 并以**新鲜 results.xml** 判 PASS（`Unity exit=1` 为僵尸被杀，非测试失败）；PlayMode 与其余段 Unity 正常 `exit 0`。跑完 `git status` 无 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings` 改写，未做任何 `git checkout`。

#### 7.5 定向变异（证明用例仍有牙）

- **变异点**：把 `SetUp` 修复段**精确改回旧逻辑**（`if (AudioService.Instance == null)` 才新建、不拆共享实例），即 §7.1 根因状态；其余文件不动。变异前后 `diff` 确认仅该段变化。
- **变红**：Run 3 `TempLogs/r914_rework_mutant.xml` → `total=44 passed=43 failed=1 result="Failed(Child)"`，**变红用例数 = 1**，挂在 `SceneLoadedEvent_StartsAReallyPlayingBgmTrack_VolumeRidesTheLoopTick`（`volume > 0.5` → `But was: 0.0f`，堆栈现场 `:144`），与 08:57 / 19:04 现场同型；`ItemPickedUp` 的 `isPlaying` 真断言与 `DoorHitAndUiClick` 分组本轮通过（漂移本身即现场特征）。证明三个用例**没有被放宽成恒真**、仍能有效检出状态污染。
- **立即还原**：`cp` 修复版字节副本（`TempLogs/r914_rework_fixed_setup.bak`）覆盖回来，`cmp -s` 字节级一致。
- **复跑复绿**：Run 4 `TempLogs/r914_rework_restore.xml` → `44/44/0`。
- **零变异残留**：`GameAudioBindingsPlayModeTests.cs` 是 untracked（A-R9-14 新增），`git diff` 不覆盖它，故以 `cmp` 字节级比对为主证据（还原后 == 修复版备份），辅证：`git diff --stat` 仅显示 A-R9-14 原有 7 个 tracked 改动（`Docs/Demo.md`、`Docs/Logs/TASK-A-R9-14.md`、`AudioBus.cs`、`AudioService.cs`、3 个 `D`），**无任何本轮新增 tracked 改动**；`git status --short` 清单与变异前一致；无 Unity 残留进程。

#### 7.6 仍存的受限项

1. §六 1–6 项（BGM 来源 URL 缺口、单轨 BGM、CC0 署名/留存、EditMode 用例数口径 11 vs 10、3 个 `D` 的提交风险）本轮未涉及，**仍然成立**；对外发布/合规审计前须补齐 BGM URL 与许可证凭证。
2. 本轮把「用例间漂移」修到确定全绿，但**旧逻辑下 door/hit 分组偶发 `But was: null` 的逐帧触发链**（共享 `SfxLimiter` 预算/去重残留与 crossfade 残留的交互）未做逐帧 trace；结论以「改回旧逻辑 → 同型 43/44 红 → 隔离后 3 次 44/44/0」的对照实验为准，未逐帧断言单一微观机制。
3. 现场 `Release called for 'bgm_cave' which is not cached` 裸 key 日志经核为改动前旧证据，本轮 4 次实跑日志均无；若后续别处重现裸 key `Release` 需另案排查，但**不是**本轮 `44/43/1` 的成因。
4. EditMode 段本次批处理「xml 已写但 Unity 5 分钟未退」由脚本按纪律 `taskkill`、以新鲜 results.xml 判 PASS；属本机批处理退出行为，非代码缺陷，口径已在 §7.4 注明。
5. 本节只做返工轮第 1 轮的定位与修复；「验收结论」仍待验收代理填写，本轮未代填、未勾选 V1–V5。
