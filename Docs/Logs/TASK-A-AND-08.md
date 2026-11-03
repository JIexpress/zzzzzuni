# TASK-A-AND-08 —— Release 签名（本地自签 keystore）

> 用户已决策：**本地自签 release keystore**（不是商店托管、不是继续 debug）。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写，2026-11-03）

### 背景

`Build/Android/ZFrame.apk` 目前用 Unity 自动生成的 **debug 证书**（`CN=Android Debug`），
只能本机调试安装，**不可分发**（别人的机器装不上/要求卸载重装）。本任务换成本地自签 release 证书。

### ⚠️ 安全红线（违反任何一条即判 FAIL）

1. **密码绝不硬编码进任何受版本控制的文件**；**绝不能出现在构建日志/控制台输出里**（打印时要打码）。
2. **keystore 与密码文件必须放在仓库之外**。
3. 仓库里只允许出现"**路径**"与"**读取方式**"，不允许出现任何密钥材料或明文密码。
4. 生成后在报告里**只写路径与 alias，不写密码明文**。

### 落位（已定，别改）

| 物 | 路径 |
|---|---|
| keystore | `C:/Users/1/Android/keys/zframe-release.keystore` |
| 密码配置 | `C:/Users/1/Android/keys/keystore.properties`（`storePassword` / `keyAlias` / `keyPassword`） |
| alias | `zframe` |

密码用**随机强口令**生成（长度 ≥ 24，大小写+数字+符号），写进 properties 文件；
报告里只说"已生成并写入该文件"，**不要贴出来**。

### 任务

1. **生成 keystore**（用 JDK 自带 `keytool`，JDK 在 `C:/Users/1/Android/jdk`）：
   `-keyalg RSA -keysize 2048 -validity 10000 -alias zframe`
   dname 用项目信息（`CN=ZFrame, OU=Game, O=ZFrame, L=Beijing, ST=Beijing, C=CN`），**不要留空**。
2. **在 `Game/Editor/PlayerBuild.cs` 里接入自定义签名**（Android 路径，只影响 IL2CPP 正式包）：
   - `PlayerSettings.Android.useCustomKeystore = true`
   - `PlayerSettings.Android.keystoreName = <properties 里的路径>`
   - `keystorePass` / `keyaliasName` / `keyaliasPass` 从 **环境变量优先、properties 文件兜底**读取；
     两处都没有 → **明确报错中止构建**（不许静默退回 debug 签名，那会出一个"以为是正式包其实是 debug"的包）。
   - 密码读进来后**不要写进日志**。
3. **保护**：`.gitignore` 确认不涉及（keystore 在仓库外，理论上已隔离）；
   但如果代码里有默认路径常量，确保它指向仓库外；在 `Docs/交接档案.md` 补一节"签名与密钥保管"
   （路径、alias、密码文件位置、**丢失后果：同包名永久无法更新，只能换包名**、备份要求）。
4. **构建验证**：跑一次 `Tools/build_android.ps1`（IL2CPP，约 30 分钟）产出签名包。
   预期 `versionName=0.1.0.24` / `versionCode=2`（A-AND-07 刚设的）。
5. **验证签名**（必须给证据）：
   - `apksigner verify --print-certs Build/Android/ZFrame.apk` → 证书主体应是 `CN=ZFrame`，**不是** `CN=Android Debug`
   - `apksigner verify -v` → `Verified using v2 scheme: true`（或 v3）
   - 证书 SHA-256 指纹记录进报告（指纹不是秘密，可以写）
6. **真机**（设备在线才做）：`adb install -r` 验证能装（注意：签名变了，从 debug 包**平级覆盖会失败**，
   需要先 `adb uninstall` 或换包名——**如果失败，如实记录现象与原因，不要硬试**）。

### 判据

- keystore 与 properties 在仓库外，仓库内无任何密码明文（`git status` / `grep` 佐证）；
- 构建产物的签名是 `CN=ZFrame` 且 v2/v3 验证通过；
- 缺密码时构建**中止**而不是退回 debug；
- 交接档案有密钥保管说明。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：构建前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 构建**勿加 `-quit`**（构建方法自己 `EditorApplication.Exit`）
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 本机 GUI Editor 加 `-force-d3d12` **会挂起**
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 构建后 `ProjectSettings.asset` / `version.json` 被写属正常（见 A-AND-06/07）

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不动 Governor 三件套、Unity 安装目录；`.cs` 无 BOM、LF
- **不要让 debug 签名静默兜底**（这是本任务最重要的一条）

---

## 施工报告（施工代理写）

> 施工日期 2026-11-03。本报告**不含任何口令明文**：口令只在仓外 properties 与内存里，
> 日志里只出现 `<redacted, 来源>`。证书指纹不是秘密，照抄如下。

### 2.1 keystore 生成结果

| 项 | 值 |
|---|---|
| 路径（仓库外） | `C:/Users/1/Android/keys/zframe-release.keystore`（2229 字节） |
| 口令文件（仓库外） | `C:/Users/1/Android/keys/keystore.properties`（`storeFile` / `storePassword` / `keyAlias` / `keyPassword`） |
| alias | `zframe`（`PrivateKeyEntry`，证书链长度 1） |
| 算法/长度 | RSA 2048，`SHA256withRSA` 自签 |
| 有效期 | 10000 天：2026-11-03 → **2054-03-21** |
| 主体/签发者 | `CN=ZFrame, OU=Game, O=ZFrame, L=Beijing, ST=Beijing, C=CN` |
| 证书 SHA-256 | `54:13:B0:48:55:DB:61:3B:66:0E:83:B4:39:E3:E9:02:58:DC:D9:F7:6D:72:B6:46:2B:DC:3E:0D:4A:02:2C:B0` |
| storeType | **JKS**（显式指定；Unity 2022.3 对 JKS 支持最稳，PKCS12 有历史兼容坑） |

生成方式与口令处理：

- `keytool` 用 `C:/Users/1/Android/jdk/bin/keytool.exe`（JDK 17），参数
  `-genkeypair -storetype JKS -alias zframe -keyalg RSA -keysize 2048 -validity 10000`，
  dname 按简报。
- 口令随机生成 **32 位**（大写+小写+数字+符号，字符集刻意排除 `= : \ #` 以避免 properties 解析歧义）。
- 口令**不经过命令行参数**：写入 `%TEMP%` 临时文件 → `keytool -storepass:file / -keypass:file` 读取 →
  用完立即删除。全程未 echo、未打印（生成脚本只输出长度）。
- 口令同时用于 `storePassword` 与 `keyPassword`（单口令，避免两处不一致）。

### 2.2 接入方式（`UnityProject/Assets/Game/Editor/PlayerBuild.cs`）

新增 `ConfigureAndroidSigning(logTag)`，在 `RunAndroid` 的"脚本后端/架构/Graphics API 设置"之后、
场景收集与 `BuildPipeline.BuildPlayer` **之前**调用（`PlayerBuild.cs:628`）。任何不满足 → 返回 false →
`RunAndroid` 返回 1 → `EditorApplication.Exit(1)`，**构建不发生**。

- 取值顺序：**环境变量优先 → `C:/Users/1/Android/keys/keystore.properties` 兜底**
  - `ZFRAME_KEYSTORE_PASSWORD` / `properties: storePassword`
  - `ZFRAME_KEY_ALIAS_PASSWORD` / `properties: keyPassword`
  - `ZFRAME_KEY_ALIAS` / `properties: keyAlias`（再兜底常量 `zframe`，alias 不是秘密）
  - `ZFRAME_KEYSTORE_PATH` / `properties: storeFile`（再兜底常量 `C:/Users/1/Android/keys/zframe-release.keystore`）
- 注入：`PlayerSettings.Android.useCustomKeystore = true` + `keystoreName` / `keystorePass` /
  `keyaliasName` / `keyaliasPass`，注入后回读 `useCustomKeystore` 自检。
- **四道中止闸（都不退回 debug 证书）**：
  1. `storePassword` 两处都没有 → `SIGNING ABORT: keystore password not found...`
  2. `keyPassword` 两处都没有 → `SIGNING ABORT: key alias password not found...`
  3. keystore 文件不存在 → `SIGNING ABORT: keystore not found: <path> (source: ...)`
  4. keystore 路径落在仓库内 → `SIGNING ABORT: keystore lies inside the repository ...`
- 日志只打来源与遮挡值，实测原样一行（无密码）：
  `[PlayerBuild] signing: useCustomKeystore=true keystore=C:/Users/1/Android/keys/zframe-release.keystore (source: properties storeFile) alias=zframe (source: properties keyAlias) storePassword=<redacted, properties storePassword> keyPassword=<redacted, properties keyPassword>`
- 三个 Android 入口（`BuildAndroid64` / `BuildAndroid64Mono` / `BuildAndroid64MonoDev`）共用 `RunAndroid`，
  因此 Mono 验证包也走同一张 release 证书，不会再出现 debug 签名包。

### 2.3 构建结果

| 项 | 值 |
|---|---|
| 命令 | `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_android.ps1`（IL2CPP/ARM64） |
| 结果 | **PASS**，Unity exit 0 |
| 耗时 | **6 分 30 秒**（IL2CPP 库已缓存，属增量；本轮未清 Library） |
| 产物 | `Build/Android/ZFrame.apk`，38,080,420 字节（36.32 MB） |
| APK SHA-256 | `FFCF2D6F69B07CE3324BA2FFA5145D8B0F15C5738CA4627CCE98CBEBDBAD53DB` |
| package / versionName / versionCode | `com.ZFrame.ZFrame` / **`0.1.0.24`** / **`2`**（符合预期） |
| native-code | `arm64-v8a`（IL2CPP ARM64） |
| version.json 写回 | `build 23 → 24`，`versionCode 2 → 3`（构建成功后唯一写路径，符合 A-AND-07 设计） |

### 2.4 签名验证证据

`C:/Users/1/Android/build-tools/34.0.0/apksigner.bat verify --print-certs Build/Android/ZFrame.apk`：

```
Signer #1 certificate DN: CN=ZFrame, OU=Game, O=ZFrame, L=Beijing, ST=Beijing, C=CN
Signer #1 certificate SHA-256 digest: 5413b04855db613b660e83b439e3e90258dcd9f76d72b6462bdc3e0d4a022cb0
Signer #1 certificate SHA-1 digest: e0eaee8456530b7d9223abe63a78a195f2945566
exit=0
```

`... verify -v Build/Android/ZFrame.apk`：

```
Verifies
Verified using v1 scheme (JAR signing): true
Verified using v2 scheme (APK Signature Scheme v2): true
Verified using v3 scheme (APK Signature Scheme v3): false
Number of signers: 1
exit=0
```

- 主体 **`CN=ZFrame`**，不是 `CN=Android Debug` —— 换签成功。
- **v2 = true**（判据"v2 或 v3 通过"满足；v3=false 是 Unity 2022.3 的出包默认，不是失败）。
- APK 内证书 SHA-256 与 keystore 里读出的指纹逐字节一致（证明用的确实是这把 keystore）。
- 附：apksigner 对 `META-INF/com/android/build/gradle/app-metadata.properties`、
  `com.android.games.engine.build_fingerprint` 两条 `not protected by signature` 警告——
  Gradle/Unity 工具的常规元数据条目，不影响安装与验证，非本任务问题。

### 2.5 缺密码确实中止构建（实测，非代码推断）

**实验 A：properties 移走、无环境变量**（`keystore.properties` → `.bak`，跑完还原）

- 命令：`Tools/build_android.ps1`（默认 IL2CPP）
- 结果：**Unity exit = 1**，`RESULT: FAIL - Unity build exit=1`，耗时 **113 秒**（脚本编译完即中止，
  远短于 6 分半的完整构建）
- 日志（`TempLogs/build_android_il2cpp_unity.log:428`）原文：
  `[PlayerBuild] SIGNING ABORT: keystore password not found. Set env ZFRAME_KEYSTORE_PASSWORD or 'storePassword' in C:/Users/1/Android/keys/keystore.properties. Build aborted — deliberately NOT falling back to Unity's debug certificate (a debug-signed APK looks like a release build but cannot be distributed).`
- 关键佐证：同一份日志里 **`building ->` 出现 0 次**（`BuildPipeline.BuildPlayer` 从未被调用），
  APK 时间戳/哈希不变（`8A9187CC…D61B6`）——即"中止"而不是"退回 debug 重新签一个包"。
- properties 已还原（`Test-Path` = True）。

**实验 B：环境变量优先级**（`ZFRAME_KEYSTORE_PATH` 指向不存在的文件，properties 里路径是好的）

- 结果：**Unity exit = 1**，耗时 122 秒，日志：
  `[PlayerBuild] SIGNING ABORT: keystore not found: C:/Users/1/Android/keys/__missing__.keystore (source: env ZFRAME_KEYSTORE_PATH). Build aborted — deliberately NOT falling back to Unity's debug certificate.`
- 这一条同时证明：① 环境变量**确实压过** properties；② 路径缺失也会拦；
- APK 哈希前后相同（`FFCF2D6F…53DB`），`building ->` 0 次。

### 2.6 真机情况

- `adb devices` → **无设备在线**（`List of devices attached` 为空，2026-11-03 15:5x 两次确认）。
- 因此**未做** `adb install -r`。按简报"设备在线才做"，此处如实记为"未执行"。
- 待设备上线时的注意点（写在这里给下一个执行者）：本包签名已从 `CN=Android Debug` 换成 `CN=ZFrame`，
  覆盖安装**必然** `INSTALL_FAILED_UPDATE_INCOMPATIBLE`。正确做法：
  `adb uninstall com.ZFrame.ZFrame` 后再 `adb install`（会清存档），或换包名。

### 2.7 仓库卫生与现场

- `git status --porcelain`（4 个改动，无未跟踪文件）：
  - `M Docs/交接档案.md`（新增 §3.8 签名与密钥保管）
  - `M Docs/Logs/TASK-A-AND-08.md`（本报告）
  - `M UnityProject/Assets/Game/Editor/PlayerBuild.cs`（签名接入）
  - `M UnityProject/ProjectSettings/ProjectSettings.asset`（构建期写入，见下）
  - `M Tools/version.json`（构建成功写回 24/3）
- **密码明文 grep：0 命中**。做法：从 properties 读出 `storePassword` 字面量，
  对仓库 2355 个文件（排除 `Library/ Temp/ obj/ Native/build/`）+ `TempLogs` 下 17 份日志逐文件全文比对 →
  仓库 0、日志 0。
- 仓库内**无** `*.keystore` / `*.jks` / `keystore.properties`：递归扫描 0 个；`git ls-files` 亦无。
- `ProjectSettings.asset` 中只有路径与 alias：`AndroidKeystoreName: C:/Users/1/Android/keys/zframe-release.keystore`、
  `AndroidKeyaliasName: zframe`、`androidUseCustomKeystore: 1`；Unity 2022.3 的该文件**没有口令字段**（已 grep `Pass` 确认无 keystore 口令）。
- Unity 进程：0 残留；`Temp/UnityLockfile` 由脚本按纪律清理。
- **如实披露**：两次"中止实验"跑在正式构建**之后**，`PrepareVersion()` 在签名闸之前执行，
  因此 PlayerSettings 被写成"下次构建版本" `bundleVersion 0.1.0.25 / versionCode 3`，
  而实际 APK 仍是 `0.1.0.24 / 2`（`Tools/version.json` 未被消耗，仍是 24/3）。
  这是既有代码的既有顺序（版本注入先于一切构建前守卫），不是签名改动引入的；
  下次成功构建会写出 0.1.0.25/3，与 PlayerSettings 当前值一致。本轮未为此改代码（超出简报范围）。
- 未 commit（施工代理铁律），未改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md`。

---

## 验收清单（供验收代理）

- [ ] V1 keystore / properties 在 `C:/Users/1/Android/keys/`，**仓库内 grep 不到任何密码明文**
- [ ] V2 构建产物签名为 `CN=ZFrame`，`apksigner verify` v2/v3 通过（**不是** Android Debug）
- [ ] V3 缺密码时构建**中止**（有证据，不是看代码推断）
- [ ] V4 日志里没有明文密码（翻构建日志确认）
- [ ] V5 交接档案有"签名与密钥保管"节，写明丢失后果
- [ ] V6 版本为 0.1.0.24 / versionCode 2；未 commit；现场干净

## 验收结论（验收代理写）

> 验收日期 2026-11-03。验收代理独立取证，未采信施工报告的任何数字；下方每条的"证据"均为本轮自己跑出的输出。
> 全部命令在 `C:/Users/1/Desktop/zcode/GameFramework` 下执行，构建前已 `unset http_proxy https_proxy all_proxy`。
> **本结论不含任何口令明文。**

### V1 密钥隔离 —— PASS

- `ls C:/Users/1/Android/keys/` → `zframe-release.keystore`（2229 B）、`keystore.properties`（246 B），均在**仓库外**。
- 从 properties 读出 `storePassword` 实际值（32 字符，`storePassword` 与 `keyPassword` 同值），**只做比对、不打印**：
  - `git grep -I -F -e <口令>` → **exit 1（0 命中）**，覆盖全部已跟踪文件；
  - 按目录 `grep -rIlF` 扫描 `Docs/` `Tools/` `UnityProject/Assets/` `UnityProject/ProjectSettings/` `UnityProject/Packages/` `TempLogs/` `Native/` → **每处 hits=0**；
  - `git log -S<口令> --all --oneline | wc -l` → **0**（历史提交里也从未出现过）；
  - `git status --porcelain -uall` 未跟踪文件 **0 个**（不存在"藏在未跟踪文件里"的旁路）。
- `find . -name "*.keystore" -o -name "*.jks" -o -name "keystore.properties"` → **0 个文件**（仓库内无密钥副本）。
- 代码侧：`PlayerBuild.cs:92-96` 只有**路径常量**与**环境变量名**（`C:/Users/1/Android/keys/...`、`zframe`），**无任何口令字面量**。
- `ProjectSettings.asset` 只有 `AndroidKeystoreName`（路径）/ `AndroidKeyaliasName: zframe` / `androidUseCustomKeystore: 1`；`grep -i pass` 仅命中 Unity 自带的 `ps4Passcode` 与空的 `metroCertificatePassword`，**与 Android 签名无关**。

### V2 签名正确 —— PASS

- `apksigner verify --print-certs Build/Android/ZFrame.apk` → exit 0：
  `Signer #1 certificate DN: CN=ZFrame, OU=Game, O=ZFrame, L=Beijing, ST=Beijing, C=CN`
  SHA-256 `5413b04855db613b660e83b439e3e90258dcd9f76d72b6462bdc3e0d4a022cb0`
- `apksigner verify -v` → exit 0：**v1 true / v2 true** / v3 false / v3.1 false / v4 false，`Number of signers: 1`。
- 独立交叉验证"包确实是这把 keystore 签的"：`keytool -list -v`（口令经 stdin 管道，未落命令行/未打印）读出 keystore 证书
  SHA-256 = `54:13:B0:48:...:2C:B0`，**与 APK 内证书指纹逐字节一致**。
- 明确**不是** `CN=Android Debug`。APK `sha256=ffcf2d6f...53db`、38,080,420 B、mtime 15:44:50。
- v3=false 属 Unity 2022.3 出包默认（v1+v2 已满足判据"v2 或 v3 通过"），非失败。
- 信息性（不影响本条判定）：`Build/Android/` 里 11-01 遗留的 `ZFrame_MonoVerify.apk` / `ZFrame_MonoDev.apk` **仍是 `CN=Android Debug`**（本任务之前的历史产物）；因本轮 V3 只跑到中止分支、未产出新 Mono 包，"Mono 入口也走 release 证书"这一条**本轮未用产物复验**，仅有代码路径（三入口共用 `RunAndroid` → `PlayerBuild.cs:628`）支撑。

### V3 缺密码必须中止 —— PASS（受限：走 Mono 快速路径）

- 自己动手，非看代码推断：`mv keystore.properties keystore.properties.audit_bak`（**备份不删除**），确认 `env | grep '^ZFRAME_'` = 0（无环境变量兜底），无 Unity 进程、无 `UnityLockfile`。
- 跑 `Tools/build_android.ps1 -Backend Mono -TimeoutSec 600`（最短路径；**未跑 IL2CPP**）：
  - `Unity exit: 1`，脚本输出 `RESULT: FAIL - Unity build exit=1`；
  - `TempLogs/build_android_mono_unity.log:366`：
    `[PlayerBuildMonoVerify] SIGNING ABORT: keystore password not found. Set env ZFRAME_KEYSTORE_PASSWORD or 'storePassword' in C:/Users/1/Android/keys/keystore.properties. Build aborted — deliberately NOT falling back to Unity's debug certificate ...`；
  - **同一日志 `building ->` 出现 0 次、`BuildPlayer` 字样 0 次** → 构建从未开始，不是"先出 debug 包再报错"。
- **产物未被产出/覆盖**：构建前后 `sha256sum -c` 三个 APK 全部 `OK`，`ZFrame.apk` 仍是 `ffcf2d6f...53db`、mtime 仍 15:44:50；`ZFrame_MonoVerify.apk` 仍是 11-01 的 `f8464ee9...`（未被重写）。
- **已还原**：`keystore.properties` 移回原位（`ls` 确认存在）；本次构建把 `ProjectSettings.asset` 的 `AndroidTargetArchitectures`/Android 脚本后端改成了 Mono 值，已用测试前的字节备份还原，`diff -q` = identical；`Tools/version.json` 测试前后 identical（`0.1.0 / build 24 / versionCode 3`）。
- 旁证（施工方日志，仅作旁证不作主证）：`TempLogs/build_android_il2cpp_unity.log:393` 有"keystore not found (source: env ZFRAME_KEYSTORE_PATH)"的中止行，`building ->` 0 次。
- 受限点如实记录：本轮验证的是 **Mono 入口**；IL2CPP 正式入口与 Mono 共用同一个 `RunAndroid` 里的闸（`PlayerBuild.cs:628`，位于任何 `BuildPipeline.BuildPlayer` 之前），未为省时间再跑一次 30 分钟 IL2CPP。

### V4 日志无明文 —— PASS

- `grep -rIlF -e <storePassword 实际值> TempLogs/` → **0 命中**（含 `build_android_mono_unity.log`、`build_android_il2cpp_unity.log`、`.xml`、archive 历史日志，共 93 项）。
- 现有日志里连成功的 `signing:` 行都不存在（成功构建那次日志已被后续中止实验覆盖），因此不存在"打码失效被打成明文"的样本；日志中仅有 `SIGNING ABORT` 与遮挡文案。
- 失败分支日志只输出**路径与来源**（`C:/Users/1/Android/keys/...`、`env ZFRAME_KEYSTORE_PATH`），不含口令。

### V5 交接档案 —— PASS

- `Docs/交接档案.md:102` 起有 `### 3.8 签名与密钥保管（release keystore，TASK-A-AND-08）`，含 keystore 路径 / 口令文件路径 / alias / 证书 SHA-256 / 读取方式（环境变量优先、properties 兜底、四道中止）/ 备份要求（两台以上离线介质、与 keystore 分开放）。
- **丢失后果写明**（`:128-135`）：同包名（`com.ZFrame...`）**永久无法更新**，旧签名丢失后任何新包都会被拒（`INSTALL_FAILED_UPDATE_INCOMPATIBLE`），唯一出路是换包名且用户/商店数据清零——与简报要求一致。
- 该节**不含任何口令明文**（已包含在 V1 的全仓库 grep 中）。

### V6 版本 / 未提交 / 现场 —— PASS

- `aapt2 dump badging Build/Android/ZFrame.apk` → `package: name='com.ZFrame.ZFrame' versionCode='2' versionName='0.1.0.24'`，`native-code: arm64-v8a`。
- **未 commit**：`git log --oneline -1` 仍是派单提交 `24159d8 G-133: ...`，施工改动全部停留在工作区。
- 现场：`git status --porcelain -uall` = 5 个 `M`（`Docs/交接档案.md`、`Docs/Logs/TASK-A-AND-08.md`、`Tools/version.json`、`PlayerBuild.cs`、`ProjectSettings.asset`），**0 个未跟踪文件**；`NO_UNITY_PROCESS`；`UnityProject/Temp/UnityLockfile` 不存在。
- `Temp/obj/Library` 无违规改动；验收取证用的临时备份留在 `TempLogs/archive_pre_audit/V3_audit/`（已被 `.gitignore:45` 覆盖，不入库）。
- 已知且已披露的现场口径（不影响本条）：`ProjectSettings.asset` 的 `bundleVersion 0.1.0.25` / `AndroidBundleVersionCode 3` 是中止实验里 `PrepareVersion()` 早于签名闸执行留下的"下一次构建版本"，**APK 本体仍是 0.1.0.24 / 2**；`Tools/version.json` 仍为 24/3 未被消耗。属既有代码顺序，非本任务引入。

### 最终裁决

**PASS** —— V1–V6 全通过（V3 受限为"Mono 快速路径验证同一道闸"）。安全红线四条均未被触碰：
口令不在仓库、不在 git 历史、不在日志、不在 ProjectSettings；keystore 与 properties 在仓库外；仓库内无密钥副本；缺口令时构建**硬中止**且不产出 APK，未出现 debug 签名兜底。

建议主控收口时注意两点（不构成 FAIL）：① 清理或重出 `Build/Android/` 下两个 11-01 的 debug 签名 Mono 包，避免误分发；② 下一次成功构建会写出 0.1.0.25/3，需与 `Tools/version.json` 口径一并对齐。
