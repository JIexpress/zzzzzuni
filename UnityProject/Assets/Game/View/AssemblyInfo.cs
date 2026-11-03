// AssemblyInfo.cs —— Game.View 程序集的友元声明（TASK-B-R10-01）。
//
// 背景：MainMenuView.ReplayPrimaryForTests / PausePanelView.InvokeResumeForTests 是
// PlayMode 冒烟用例"模拟玩家点击"的接缝，生产路径从不调用。它们曾以 public 形式出现在
// 产品程序集的公开面上，现收为 internal，只对下列测试程序集开放：
//   ZFrame.PlayMode.Tests（Assets/ZFrame/Tests/PlayMode）
// TASK-A-R9-06 追加：GameplayFxBindings.FrameSource（同帧合并的帧号源）只服务于
// EditMode 合并用例（EditMode 推进不了 Time.frameCount），同样 internal + 友元。
//
// 注意：程序集名必须与 asmdef 的 "name" 字段逐字一致，改名时这里要同步。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ZFrame.PlayMode.Tests")]
[assembly: InternalsVisibleTo("Game.View.Tests")]
