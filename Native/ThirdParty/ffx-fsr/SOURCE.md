# FSR SDK 来源记录

- 上游: https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK
- Release: v2.3.0 (2026-06-24)
- 资产: FidelityFX-Samples-v2.3.0-prebuilt.zip（预编译签名 DLL+样例库，无媒体）
- URL: https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/download/v2.3.0/FidelityFX-Samples-v2.3.0-prebuilt.zip
- 大小: 127451548 字节 (121.5 MiB)
- SHA256: F90890B9323BB2F4F2404AC4CDC9395E8495ECDAC6F7AA0BCDF1AD1848422273
- 下载方式: curl 断点续传（本机直连 ~30KB/s，多段续传完成）；完整性: ZIP 可开 231 entries
- 许可证: MIT（每个官方头文件内嵌完整 MIT 文本；`3rdpartynotice.md` 为包内第三方组件声明，经 tag 源取出）
- 备注: v2.3.0 含 FSR Upscaling 4.1.1（RDNA3 支持）与 3.1.x 效果，经 ffx_api 集成；v1 效果已废弃

## vendor 子集清单（解压/取用明细，27.5 MiB）

zip 为 Samples 预编译包（不含头文件与 docs），头文件从同 tag 源码树经 GitHub
REST API 取出。两侧均已做哈希核验（B-R6-01 施工时）：

| 本地路径（相对本目录） | 来源 | 核验 |
|---|---|---|
| `bin/amd_fidelityfx_upscaler_dx12.dll` | zip `Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release/` | SHA256 与 zip 内条目一致（d0dcccc7…） |
| `bin/amd_fidelityfx_loader_dx12.dll` | zip 同上目录 | SHA256 与 zip 内条目一致（e2d85aa0…） |
| `api/include/ffx_api.h` | tag v2.3.0 `Kits/FidelityFX/api/include/` | git blob sha1 与 tag 树一致（484ec23f…） |
| `api/include/ffx_api_types.h` | 同上 | 07c0da20… 一致 |
| `api/include/ffx_api_loader.h` | 同上 | ac05ec4b… 一致 |
| `api/include/dx12/ffx_api_dx12.h` | 同上 | 1b711807… 一致 |
| `upscalers/include/ffx_upscale.h` | tag v2.3.0 `Kits/FidelityFX/upscalers/include/` | 021ac23c… 一致 |
| `3rdpartynotice.md` | tag v2.3.0 包内声明 | 内容随源 |

- 链接方式：**运行时 LoadLibrary（无 .lib 导入库）**——组件 dll
  `amd_fidelityfx_upscaler_dx12.dll` 直接导出 ffx_api 五函数
  （ffxCreateContext/ffxDestroyContext/ffxDispatch/ffxQuery/ffxConfigure），
  `ffx_api_loader.h` 的 `ffxLoadFunctions` 经 GetProcAddress 取表；loader dll
  仅作聚合入口回退。CMake 在 zrhi_d3d12_window 构建后把两个 dll 拷至 exe
  目录（Native/CMakeLists.txt POST_BUILD）。
- DLL 结构依据：v2.x 按效果拆分 DLL（upscaler 效果=本子集的
  `amd_fidelityfx_upscaler_dx12.dll`）；zip 不含 docs/ffx-api.md，DLL 结构按
  ffx_api.h/ffx_api_loader.h 头文件契约与包内 Samples/Upscalers 实际布局确认。
- git 纪律：zip 不入库（`.gitignore` `Native/ThirdParty/ffx-fsr/*.zip`）；dll
  入库遵循 ADR-004（共 27.5 MiB）；本机实测 provider 档位=3.1.5（fsr: provider[0]
  id=0xF5A5CA1E00C01005 name=3.1.5，真机 780M/RDNA3）。

## TASK-B-R8-02 追加（帧生成 vendor 子集，68.4 MiB）

简报指定：FG 组件 dll 从同一 zip 提取；FG 效果头从 tag v2.3.0 源码树取出。
提取/取用与核验（B-R8-02 施工时）：

| 本地路径（相对本目录） | 来源 | 核验 |
|---|---|---|
| `bin/amd_fidelityfx_framegeneration_dx12.dll` | zip `Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release/` | SHA256 `02297bee…2f18` 与 zip 内条目一致（40,085,776 B）；dumpbin 实测直接导出 ffx_api 五函数（ffxCreateContext/ffxDestroyContext/ffxDispatch/ffxQuery/ffxConfigure），导入表仅系统 dll（dxgi/MSVCP140/VCRUNTIME140/winmm/version 等，ags 为延迟/同目录依赖） |
| `bin/amd_ags_x64.dll` | zip 同上目录 | SHA256 `b27b070c…8da2` 与 zip 内条目一致（179,408 B；AMD AGS 库，FG 运行时依赖） |
| `framegeneration/include/ffx_framegeneration.h` | tag v2.3.0 `Kits/FidelityFX/framegeneration/include/` | git blob sha1 与 tag 树一致（5ca54864…） |
| `framegeneration/include/ffx_framegeneration_api_types.h` | 同上 | e7e514dd… 一致 |
| `framegeneration/include/dx12/ffx_api_framegeneration_dx12.h` | tag v2.3.0 `Kits/FidelityFX/framegeneration/include/dx12/` | 8bc71ca2… 一致 |

- 目录布局镜像官方 SDK 树（`framegeneration/include/`），三个 FG 头内的相对
  include（`../../api/include/*`、`../../../api/include/dx12/*`）无需改动即可解析。
- 头版本：`FFX_FRAMEGENERATION_VERSION` = 4.0.1（帧生成效果）、
  `FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION` = 3.1.7（FG 交换链）——两处
  version desc 均按 SDK 2.1+ 要求链入创建链（集成契约见 zupscaler/fg_ffx.h 注）。
- 官方文档依据：`Kits/FidelityFX/docs/techniques/frame-interpolation-api.md` 与
  `frame-interpolation-swap-chain.md`（tag v2.3.0；本包 zip 不含 docs，与 B-R6-01
  同策经 GitHub API 按 blob sha1 核验取出）。
- dll 入库遵循 ADR-004；zip 内同目录的 D3D12Core/dxcompiler/dxil/pdb/sample exe
  等**非** FG 运行时依赖，不 vendor。
