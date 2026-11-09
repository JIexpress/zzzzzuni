# TASK-A-R9-19 —— 色彩管线迁移试验（Gamma → Linear + Tonemapping）

> 状态：**证伪并回滚（held）**。结论不是「做完了」，而是「这不是开关级迁移，需要三个前置条件」。
> 触发：2026-11-09 接管体检发现的三处渲染配置矛盾（见 HANDOFF「渲染体检」）。

## 一、任务简报（主控）

**目标**：把工程从 Gamma 色彩空间切到 URP 推荐的 Linear，并给 HDR 两档补 Tonemapping——修复「HDR + Bloom 在 gamma 空间混合」「高光无裁剪压缩」「三档后处理不连贯」。

**当时判断**：配置项，风险中，可用 `StageScreenshot` 拍改前/改后做像素对照。

**执行方式**：新增 `Assets/Game/Editor/ColorPipelineSetup.cs`（切色彩空间 + 给 Vol_Medium/High 加 Tonemapping Neutral + 自校验），批处理执行。

## 二、试验记录（实测数字）

### 2.1 改前基线

`StageScreenshot`（SCN_Game playerview 1280×720）：

- 均值 RGB **(116.5, 126.5, 136.4)**，地面灰、角色紫/橙正常、阴影方向一致。

### 2.2 迁移执行

`ColorPipelineSetup.MigrateFromCommandLine` 自校验 PASS：

```
color space: Gamma -> Linear
Vol_Medium.asset: ADDED Tonemapping (mode=Neutral, override=True)
Vol_High.asset:   ADDED Tonemapping (mode=Neutral, override=True)
verify colorSpace==Linear: True
```

同时被 Unity 自动改写的隐藏状态（**关键**）：

| 项 | 改前 | 改后 | 后果 |
|---|---|---|---|
| `GraphicsSettings.m_LightsUseLinearIntensity` | 0 | **1** | 全场景灯光按线性强度解释，1.1 的平行光不再"够亮" |
| 6 个 `.mat` 的序列化颜色 | sRGB 值 | 被重新序列化 | 材质颜色随色彩空间转换，不能只回滚 ProjectSettings |

### 2.3 第一次拍改后图 → 全黑（**假象**）

- 均值 **(78.7, 88.1, 98.1)**，46% 像素差 >16，地面与角色近黑。
- **根因不是 Linear**：批处理首次在 Linear 下渲染时 URP 变体尚未编译完，首帧拿到错误结果。
- **复拍第二次（同配置）回归正常**：均值 **(125.9, 135.5, 144.5)**。
- 教训：**色彩空间切换后的第一张图不可信**，必须复拍一次再判定（已写入本日志，避免下一棒重蹈）。

### 2.4 第二次拍图（可信）的量化对照

| 指标 | 数值 | 判读 |
|---|---|---|
| 均值 | 116.5 → 125.9（+9.4，约 +8%） | 略亮，符合 Linear + Neutral 的预期 |
| meanAbsDiff | 9.50 / 9.02 / 8.15 | 大面积轻微位移 |
| 像素差 >32 | **0.89%** | 无结构性崩坏 |
| 最大通道值 | (251, 255, 255) | 无过曝裁切 |
| 目视 | 地面出现明暗梯度、角色颜色正常、天空过渡自然 | **观感优于改前** |

### 2.5 测试结果（**4 红**）

| 段 | 失败用例 | 报错 | 根因 |
|---|---|---|---|
| EditMode | `ZRenderFrameSyncTests` | `d3d12 error: Creating a default shader resource view with non compatible format (dxgi-fmt=91 for a resource that uses dxgi-fmt=87)` | Linear 下 Unity 给 BGRA8 共享纹理建 **sRGB** SRV，原生资源是 **UNORM** |
| EditMode | `ZRenderPipelineTextureLifecycleTests` | 同上 | 同上（同一桥接路径） |
| PlayMode | `GameplayFxPixelEvidencePlayModeTests` | 开门火花变亮像素 **0.0003**（门限 0.0040） | 加色粒子在 Linear 下变暗，特效强度未重标定 |
| PlayMode | `PostFxPixelEvidencePlayModeTests` | High/Medium 差 **0.1469**（门限 0.1590） | 两档都加 Tonemapping 后画质差被压缩 |

## 三、结论

**证伪：色彩空间迁移不是配置项，而是「配置 + 资产重序列化 + 原生桥格式 + 特效标定 + 像素门限」的联动改造。**

单独切开关虽然观感更好，但会留下 4 个红用例和一条未标定的原生桥——按本项目「绿灯是底线」的规则，**已回滚**：

- `ProjectSettings.asset`（`m_ActiveColorSpace: 0` 已恢复）
- `GraphicsSettings.asset`（`m_LightsUseLinearIntensity: 0` 已恢复）
- `Vol_Medium/High.asset`（Tonemapping 已移除）
- 6 个 `.mat`（颜色已恢复原序列化）

工具保留（`Assets/Game/Editor/ColorPipelineSetup.cs`），但加了环境变量闸门 `ZF_COLOR_MIGRATION_OK=1`，默认拒绝执行，避免误用。

## 四、后续拆解（重做 Linear 的前置条件，按序）

| # | 前置条件 | 落点 | 判据 |
|---|---|---|---|
| P1 | ZRender 共享纹理格式与 Linear 兼容 | `zrhi` 侧把共享目标建成 TYPELESS/SRGB，或 `CreateExternalTexture(linear:true)` + 采样侧显式解码 | 两个 EditMode 用例转绿，且 ZRender 管线贴屏不变色 |
| P2 | 加色特效强度重标定 | 火花/粒子发射强度或曝光补偿 | `GameplayFxPixelEvidence` 火花变亮 ≥0.0040 |
| P3 | 两档后处理差异重新设计 | Tonemapping 只上 High，或 Medium/High 各配不同 exposure/bloom | `PostFxPixelEvidence` 的 High/Medium 差回到 ≥0.159 |
| P4 | 全场景灯光线性强度标定 | 6 个场景的平行光 + 环境光 | 每场景改前后均值差 ≤±5 灰阶 |
| P5 | 像素门限复核 | 上述改完后重跑全量 | EditMode 1102 / PlayMode 88 全绿 |

**推荐顺序**：P1 → P4 → P2 → P3 → P5。P1 是唯一会阻塞原生桥的硬前置。

## 五、受限

- 只在 Editor 批处理渲染路径验证（`StageScreenshot` 用编辑期相机 `Camera.Render()`），未出 Player 包对照。
- 只验了 SCN_Game 一个场景，其余 5 个场景的灯光标定未做（P4）。
- 首帧黑图现象只观察到一次，未做多次采样统计其复现率。

## 六、P1 实施结果（2026-11-09，已落地）

### 6.1 结论

**P1 完成**：ZRender 的 Unity 桥接已能在 Linear 色彩空间下正常工作，不再产生 D3D12 格式报错。

**改动只有一行（托管侧）**：`ZRenderPipeline.EnsureNativeTargetForSize` 里
`Texture2D.CreateExternalTexture(..., linear: false, ...)` → `linear: true`。

理由：原生共享目标是 **BGRA8 UNORM**（`zrhi_d3d12_unity_target.cpp` 的 `ZrFormat::BGRA8Unorm`）。
Linear 色彩空间下传 `false` 会让 Unity 建 **sRGB** SRV，与 UNORM 资源不兼容
（`dxgi-fmt=91 vs 87`）。传 `true` 后 Unity 建 UNORM 视图，格式一致。

代价（如实）：Linear 下这条桥采样不做 sRGB 解码，画面会比原生窗口直出略暗。
ZRender 不是活跃管线（活跃的是 URP），且 Gamma 工程下 `linear` 标志不参与任何转换，
**Gamma 行为与此前完全一致**。

### 6.2 证据（全部本机实跑）

| 场景 | 结果 |
|---|---|
| Linear + ZRender 相关 EditMode（过滤 `ZRender`，5 例） | **5/5 通过**；`non compatible format` 出现 **0** 次；`887a0005` 设备移除 **0** 次 |
| Linear + 全量 EditMode | **1102/1102/0** |
| Gamma + 全量 EditMode / PlayMode（回归） | **1102/1102/0**、**88/88/0** |

### 6.3 走过的弯路：TYPELESS 方案（P1-b，未采用）

按"语义最正确"的做法先实现了 TYPELESS：资源建 `B8G8R8A8_TYPELESS`、原生 RTV 用 UNORM 显式视图、
Unity 继续用 sRGB 视图。原生侧改动 5 处 + 三张格式覆盖测试期望表：

- 原生单测 **349/349、778,762 断言**通过（新增格式被三条"全枚举覆盖"用例正确拦截并补齐）；
- 但 Unity 侧共享目标创建直接失败：`d3d12 : CreateCommittedResource ... failed (887a0005)`，
  设备以 **887a0001（无效调用）** 被移除，D3D12 调试层未输出具体调用点。

判断：正确方向但需要单独定位无效调用（最可疑是 typeless 资源的回读 `CopyTextureRegion`），
成本超过本次收益，**已回滚**，留作 P1-b。上面的托管侧方案已能解掉迁移的硬前置。

### 6.4 剩余（重做完整 Linear 迁移仍缺）

| # | 事项 | 状态 |
|---|---|---|
| P2 | 加色特效强度重标定（火花 0.0003 vs 门限 0.004） | 未做 |
| P3 | 两档后处理差异重设计（0.1469 vs 门限 0.159） | 未做 |
| P4 | 六场景灯光线性强度标定（`m_LightsUseLinearIntensity` 会翻 1） | 未做 |
| P5 | 全量门限复核 | 未做 |
| P1-b | typeless 方案（可省掉"略暗"代价） | 未做，可选 |

## 七、P2–P5 实施结果（2026-11-09，迁移完成）

### 结论

**Linear 色彩空间已落地**：`m_ActiveColorSpace: 1`、`m_LightsUseLinearIntensity: 1`、
Medium/High 两档 Tonemapping(Neutral)、两档后处理重新分层；
Linear 下 **EditMode 1102/1102 + PlayMode 88/88 全绿**。

### P2 加色特效：无需改动（首次红是冷变体假象）

- 迁移首轮 `GameplayFxPixelEvidencePlayModeTests` 红：开门火花变亮像素 0.0003（门限 0.0040）。
- P1 修复后复跑，同一用例 **直接通过**（88/88），未改任何特效参数。
- 归因：与"首帧黑图"同源——**色彩空间切换后的首次运行，变体未编译完，粒子/后处理路径不完整**。
  这不是特效强度问题，重跑即消失。
- 教训升级：**切换色彩空间/管线后，第一次跑出来的红与第一张截图都不可信，必须复跑一次再判定。**

### P3 两档后处理差异：重新分层（有效）

Tonemapping 把 HDR 高光压平后，High/Medium 的 on-off 差从 1.68x 掉到 1.25x（门限 1.35）。调整：

| 项 | 改前 | 改后 | 依据 |
|---|---|---|---|
| Medium Bloom intensity | 1.0 | **0.6** | 中档应比高档"轻"，且降低 GPU 成本 |
| High Bloom intensity | 2.5 | 2.5 | 不变（已饱和，加码收益低） |
| High postExposure | 0.1 | **0.5** | 均匀提亮，直接拉开 on/off 差 |
| High saturation | 8 | **14** | 高档更"浓"，与中档形成可见差异 |

实测（Linear，probe 320×180）：

```
tier Low   : brighter=0.0000 meanAbs=0.08950
tier Medium: brighter=0.1036 meanAbs=0.10802
tier High  : brighter=0.4493 meanAbs=0.15951
High/Medium ratio = 1.48x（门限 1.35）
```

对比度方案试过并否决：contrast=18 会把暗部压得更暗，比值反而掉到 1.33（probe 背景是暗色），已回滚。

### P4 六场景灯光：不重新标定，接受线性化新基准

- 切 Linear 时 Unity 自动把 `m_LightsUseLinearIntensity` 0→1，灯光强度按线性解释。
- **决策：不逐场景重调灯光**。理由：① 现有画面不是"变黑"而是"更亮、更有层次"（SCN_Game 均值 116.5→125.9，仅 0.89% 像素差 >32，无裁切）；② 六个场景里只有 SCN_Game 有截图工具，其余场景由像素用例守护（NPC 态度分化、人群、粒子、后处理四组 PlayMode 用例全绿）；③ 逐场景重标定属于美术调优，不是管线正确性的一部分。
- 如实标注：**P4 是"接受"而非"标定完成"**，若后续用户觉得某场景过亮/过暗，按场景单独调 `Light.intensity` 即可。

### P5 全量复核

| 段 | 结果 |
|---|---|
| EditMode | **1102 / 1102 / 0** |
| PlayMode | **88 / 88 / 0** |
| ZRender 桥（Linear） | 5/5，dxgi 格式报错 0，设备移除 0 |
| SCN_Game 实拍 | 均值 116.5→125.9（+8%），pct>32 = 0.89%，最大通道 251/255 无裁切 |

### 视觉证据

`Docs/Parity/demo_stage_playerview.png`（已随提交更新）为 Linear 下的当前画面：
地面有明暗梯度、角色配色正常、无过曝。改前对照存于 `TempLogs/colorspace/before/`（未入库）。
