# TASK-A-PKG-01 —— Windows 安装包（Inno Setup）

> 缺口清单第 6 条：此前只有目录形式 exe（83MB），无法像软件一样安装/卸载。
> 本任务由主控直接实施（构建工具类，不含游戏逻辑），交付脚本 + 已验证产物。

## 做法

- `Tools/installer/ZFrame.iss`：Inno Setup 6 脚本，LZMA2/ultra64 压缩
- `Tools/build_installer.ps1`：读 `Tools/version.json` → 版本 `0.1.0.32` → staging → 调 ISCC

## 三个踩到的坑（都已修）

1. **ISCC 路径**：`winget install JRSoftware.InnoSetup` 装到 `%LOCALAPPDATA%\Programs\Inno Setup 6\`，
   不在 `Program Files (x86)`；脚本已加该候选路径
2. **`Source: "dir\*"` + `Excludes` 对递归子目录不可靠**：Burst 调试目录排不掉（86.2MB vs 83MB）。
   改为**物理 staging**（robocopy `/E /XD ZFrame_BurstDebugInformation_DoNotShip`）再打包
3. **`$outDir` 在定义前被使用**：PowerShell 把 staging 建到了仓库根，导致打进空目录（2MB 包）。
   已调整定义顺序
4. **`PrivilegesRequired=admin` 在非交互环境静默安装失败（exit 2）**：
   改为 `lowest` + `PrivilegesRequiredOverridesAllowed=dialog`（默认装用户目录免 UAC，
   向导里仍可选"为所有人安装"）

## 验证（实际执行）

| 项 | 结果 |
|---|---|
| 构建 | exit 0，48.8s |
| 安装包体积 | **24 MB**（83MB 源 → 24MB，LZMA2 压缩约 3.5:1） |
| 静默安装 | exit 0，`/VERYSILENT /DIR=...` |
| 安装后 | **167 文件 / 86.2 MB** |
| Burst 调试目录 | **已排除**（`burst_present=False`） |
| 卸载器 | `unins000.exe` 生成 |
| 数据完整性 | `ZFrame_Data\globalgamemanagers` 在 |
| 插件 | `ZFrame_Data\Plugins\x86_64\zrender.dll` 在 |

## 已知限制

- **安装界面是英文**：Inno Setup 6.7.3 未自带简体中文语言包（Languages 目录 30 种，无中文）。
  补法：把官方 `ChineseSimplified.isl` 放进 Inno 的 `Languages` 目录，取消 `.iss` 里那行注释即可
- 未做代码签名（无证书），Windows SmartScreen 会提示
- 未验证：安装后实际启动运行（只验了文件完整性 + 卸载器存在）

## 产物

`Build/Installer/ZFrame-Setup-0.1.0.32.exe`（24MB，gitignore 覆盖，不入库）
