# Streamline vendor 子集来源记录（TASK-B-R6-02）

> ⚠️ **本文件为主控代笔补写**（2026-10-25）：原施工代理在写入本文件前因网络中断，
> 仅 CMake 注释（`Native/CMakeLists.txt` L204 附近）间接记载了获取方式。
> 凡本文件无法亲证之处均标注"待补"，不假装完整。

## 来源

| 项 | 值 |
|---|---|
| 上游 | NVIDIA Streamline 官方仓库 `github.com/NVIDIAGraphics/Streamline`（任务简报指定，唯一允许的取用处） |
| 头文件版本 | **2.14.16**（`include/sl_version.h` 实测：`SL_VERSION_MAJOR/MINOR/PATCH = 2/14/16`） |
| tag / commit | **待补**（原代理未及记录；可按头文件版本号 2.14.16 回溯对应 release 复核） |
| 获取方式 | **待补**（原代理中断遗失）。CMake 注释记载：官方 SDK zip 276 MB 且 release CDN 在本机不可达，故改为**头文件子集 + 构建期由官方 `sl_interposer.def` 生成导入库**（不经手任何二进制） |
| 许可证 | **MIT**（各头文件头部 NVIDIA 版权声明 + MIT 许可文本，已逐文件核验存在） |

## 子集清单（20 文件，SHA256 前 16 位）

| 文件 | 字节 | SHA256（前16位） |
|---|---|---|
| `Native/ThirdParty/streamline/include/sl.h` | 2201 | `e1e81a7428d15b30…` |
| `Native/ThirdParty/streamline/include/sl_appidentity.h` | 206 | `1337385ac9867d66…` |
| `Native/ThirdParty/streamline/include/sl_consts.h` | 12814 | `17de74afda2cb962…` |
| `Native/ThirdParty/streamline/include/sl_core_api.h` | 17127 | `328dc3a2c1dee579…` |
| `Native/ThirdParty/streamline/include/sl_core_types.h` | 34283 | `f420cb052ce14489…` |
| `Native/ThirdParty/streamline/include/sl_device_wrappers.h` | 205 | `af7741305b1a468c…` |
| `Native/ThirdParty/streamline/include/sl_dlss.h` | 8489 | `d2c8c61fa71794c8…` |
| `Native/ThirdParty/streamline/include/sl_dlss_d.h` | 8222 | `6e51cf9815301c59…` |
| `Native/ThirdParty/streamline/include/sl_dlss_g.h` | 10941 | `1fc18cbe004e280d…` |
| `Native/ThirdParty/streamline/include/sl_helpers.h` | 16950 | `48629edbf25dfdb6…` |
| `Native/ThirdParty/streamline/include/sl_matrix_helpers.h` | 12405 | `a65758d85abba1e1…` |
| `Native/ThirdParty/streamline/include/sl_nis.h` | 3805 | `f45be3cf149200e1…` |
| `Native/ThirdParty/streamline/include/sl_pcl.h` | 5726 | `f43c5135fc8d5349…` |
| `Native/ThirdParty/streamline/include/sl_reflex.h` | 10565 | `3b623a1189e04a68…` |
| `Native/ThirdParty/streamline/include/sl_result.h` | 2535 | `2a0f6c12863bdc00…` |
| `Native/ThirdParty/streamline/include/sl_security.h` | 20501 | `a36b1c20394402be…` |
| `Native/ThirdParty/streamline/include/sl_struct.h` | 4839 | `28deda67ea1a7437…` |
| `Native/ThirdParty/streamline/include/sl_template.h` | 2353 | `af2d4a61361f2603…` |
| `Native/ThirdParty/streamline/include/sl_version.h` | 3641 | `2a43f565b45a07d4…` |
| `Native/ThirdParty/streamline/lib/sl_interposer.def` | 1232 | `b24d90308c6a91de…` |
## 用途与边界

- 仅用于 `DlssStreamlineUpscaler`（`Native/zupscaler/dlss_upscaler.cpp`）的 DLSS 5 骨架封装；
- **本机无 N 卡**：运行验证为受限项（需 RTX 50 系硬件补验），验收口径 = 编译链接通过
  （`streamline_link_check` 目标：取 SL_API 函数地址强制解析导入符号，不运行）+ 能力矩阵单测（mock vendor）；
- 导入库 `sl.interposer.lib` **不入库**：构建时由 `sl_interposer.def` 经 CMake custom target 生成
  （`Native/CMakeLists.txt` `streamline_importlib` 目标），避免 vendor 二进制。
