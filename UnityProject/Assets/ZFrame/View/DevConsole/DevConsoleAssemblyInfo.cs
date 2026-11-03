// DevConsoleAssemblyInfo.cs —— TASK-A-DEV-04（H1）给 DevConsole 的测试接缝开友元。
//
// 为什么单独一个文件：本任务的改动白名单只到 Assets/ZFrame/View/DevConsole/**，
// 不动 ZFrame/View/AssemblyInfo.cs（那是别的任务的产物）。
//
// 为什么是 InternalsVisibleTo 而不是 public：H1 的要求是"发布包里不该存在能翻转门控的入口"。
// TryEnableForTests / ActiveClientsForTests 都是 internal，且只在
// UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEVELOPMENT_BUILD 下编译——
// 非开发包（发布包）的 ZFrame.View.dll 里根本没有这些符号（证据见 Docs/Logs/TASK-A-DEV-04.md）。
// 程序集名必须与 asmdef 的 "name" 字段逐字一致。
#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEVELOPMENT_BUILD
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ZFrame.PlayMode.Tests")]
#endif
