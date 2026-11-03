// AssemblyInfo.cs —— ZFrame.View 程序集的友元声明（TASK-B-R10-01）。
//
// 背景：UI 框架的三处 ResetForTests（UIRoot / UIManager / SettingsEntry）是测试收尾用的
// 静态引用清理钩子，生产路径从不调用。它们曾以 public 形式出现在产品程序集的公开面上，
// 现收为 internal，只对下列测试程序集开放：
//   ZFrame.Debug.Tests（Assets/ZFrame/Tests/Debug，Editor-only）
//   Game.View.Tests   （Assets/Game/Tests/View，Editor-only）
//
// 注意：程序集名必须与 asmdef 的 "name" 字段逐字一致，改名时这里要同步。
//
// TASK-A-AND-04 追加：ZFrame.View.Input.Tests 需要 VirtualJoystick.ApplyLocalOffset
// （摇杆几何步进：屏幕点→摇杆局部偏移→归一化值）这一内部接缝。真机/编辑器里
// UGUI 指针路径不可在批处理测试中合成，所以把"指针换算之后的那一步"单独暴露给
// 测试直接驱动；公开面仍只有 IPointerDown/IDrag/IPointerUp。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ZFrame.Debug.Tests")]
[assembly: InternalsVisibleTo("Game.View.Tests")]
[assembly: InternalsVisibleTo("ZFrame.View.Input.Tests")]
