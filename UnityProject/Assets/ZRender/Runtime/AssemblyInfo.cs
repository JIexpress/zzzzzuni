// AssemblyInfo.cs —— ZFrame.Render 程序集的友元声明（TASK-B-R10-01）。
//
// 背景：B-R9-08 为了让 EditMode 能验证"开→覆盖、关→还原"，在
// ZrFrameGenerationRuntime 上开了 public 的 BindQualityTargetsForTests 注入点。
// 那是测试需要，不是产品 API —— 公开它等于把测试细节编进 Player 的可见面。
//
// 现在该注入点收为 internal，只对测试程序集开放：
//   ZFrame.Render.Tests（Assets/ZRender/Tests，Editor-only）
//
// 注意：程序集名必须与 asmdef 的 "name" 字段逐字一致，改名时这里要同步。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ZFrame.Render.Tests")]
