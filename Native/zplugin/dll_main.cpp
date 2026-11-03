// dll_main.cpp —— zrender.dll 锚点翻译单元（TASK-B-R0-01）
//
// CMake 的 SHARED 目标至少需要一个源文件；符号导出清单见同目录 zrender.def，
// 链接器据此从五模块静态库抽取 ZR_* 实现并写入导出表。
// 本文件不定义 DllMain：MSVC CRT 提供默认 DLL 入口（仅进程/线程附加通知，
// 本模块无需处理），从而避免引入 <windows.h> 依赖。
