// AssemblyInfo.cs —— ZFrame.Render.Bindings 程序集的友元声明（TASK-B-PLANET-07）。
//
// 与 Runtime/AssemblyInfo.cs 同策：ZrPlanetChannel.ResetForTests 是测试隔离入口，
// 不是产品 API——收为 internal，只对测试程序集开放：
//   ZFrame.Render.Tests（Assets/ZRender/Tests，Editor-only）
//
// 注意：程序集名必须与 asmdef 的 "name" 字段逐字一致，改名时这里要同步。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ZFrame.Render.Tests")]
