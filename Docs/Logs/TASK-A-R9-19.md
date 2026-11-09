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
