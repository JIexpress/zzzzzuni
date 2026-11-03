# TASK-A-CI-01 —— CI 接入（GitHub Actions L1 + 本地门禁）

> 状态：**本地部分已完成，待用户建仓后推送**。
> 本文档记录决策依据与实测数据，避免下次接手重复摸索。

## 一、为什么这个项目不能照抄普通 CI

三个绕不开的难点（都是实测/读代码确认的，不是猜的）：

| 难点 | 具体表现 | 依据 |
|---|---|---|
| **Unity 许可证** | 云端 runner 每次要激活；Personal 版也要 license 文件，且会过期需轮换 | Unity 批处理 `-runTests` 必须有有效许可 |
| **没有 GPU** | `playmode` 的像素对比测试在软件渲染下会失败或**假绿**；`bench` 比对 `bench_baseline.json`，云端性能波动会造成天天误报 | 已有 PixelParity 类用例 + `bench_regression.ps1` 的基线门禁 |
| **环境重装代价** | 云端 Windows runner 从零装 VS/Vulkan SDK/Unity/Android SDK 需 20 分钟起 | 本机这些全装好，跑全套仅几分钟 |

## 二、分层方案（已落地 L1）

| 层 | 内容 | 依赖 | 落点 |
|---|---|---|---|
| **L1 native** | `cmake` + VS2022 编译 + `zrender_test.exe`（本地 281 用例 / 771,162 断言） | CMake + VS | ✅ **云端 GitHub Actions** |
| L2 unity | `editmode` 811 + `arch`/`config`/`naming` 三校验器 | Unity 许可证 | ⏸ 未启用（配置内留注释），建议自托管 |
| L3 heavy | `playmode` 41 / `bench` / `player`+`android` 打包 | GPU + 稳定机器 | ⏸ 不进云端，本地或自托管 |

**L1 能在云端跑通的关键前提**（已核实）：`Native/CMakeLists.txt:52-55` 写明
"探测 `VULKAN_SDK` → 找不到则跳过 Vulkan 目标并打印说明（CI/他机友好；D3D12 不受影响）"，
所以 runner **不需要安装 Vulkan SDK**。
注意：无 Vulkan SDK 时用例数会少于 281（Vulkan 相关被跳过），
故 workflow 只验退出码，**不比对具体数字**。

## 三、挖出的仓库体积问题（本次最大意外收获）

`Tools/android_support_installer.exe` **456MB** 被误提交，占 `.git` 的 **89%**（511MB 里 455MB），
且全仓 `grep` ps1/md/cs/yml **0 引用**——纯死文件。

处置：`git rm --cached` + 加 `.gitignore`。**tracked 体积 556MB → 101.5MB**。
未重写历史（所有旧 commit hash 保持有效），文件仍在磁盘。

## 四、推送策略（用户已选：远程从当前快照起步）

- `main`（新根 `a5300dc`）：当前完整快照，**实际 pack 54MB**（压缩后）
- `legacy`（`6ec9127`）：完整旧历史，**仅本地保留，不推送**
- `.git` 本地仍 511MB（历史对象还在 legacy 分支上），远程只收 54MB

这么做的原因：完整历史推送要传 455MB 死文件，慢网络下大概率中断。

## 五、本地门禁（已生效，不依赖网络）

`.githooks/pre-push` + `git config core.hooksPath .githooks`

- 阈值 10MB；`Native/ThirdParty/` 白名单放行必需的 FFX dll（38MB/27MB）
- **只查当前快照**，不翻历史（查历史会误伤已移除的旧文件）
- **实测有牙**：`ZFRAME_LIMIT_MB=1` 时正确列出 6 个超限文件并 `exit 1`；10MB 下放行
- 逃生口令：`ZFRAME_ALLOW_BIG=1 git push`

⚠️ `core.hooksPath` 是**本地配置**，新克隆需执行一次：
```
git config core.hooksPath .githooks
```

## 五½、首次推送失败的坑（2026-11-03 23:37）

`git push` 把 54MB 传完（1.86 MiB/s，36 秒）后**被服务端拒绝**：

```
! [remote rejected] main -> main (refusing to allow a Personal Access Token to
  create or update workflow `.github/workflows/ci.yml` without `workflow` scope)
```

**原因**：classic PAT 只勾了 `repo`，没勾 `workflow`。只要提交里包含
`.github/workflows/*.yml`，就必须有 `workflow` scope。

**关键教训**：
1. **classic token 的 scope 生成后不能修改**，只能重新生成一个。所以第一次就该勾全。
2. 这个失败发生在**数据传完之后**（服务端校验引用时才拒），所以"传得很顺"不代表会成功。
3. 顺带验证了网络：**54MB / 36 秒 / 1.86 MiB/s**，GitHub 推送在这台机器上不是瓶颈。

## 六、待办（阻塞项）

用户需完成两步，之后可推送：
1. GitHub 建**空**私有仓库（不要勾 README/gitignore/license）
2. 生成 PAT（`repo` 权限），存到 `C:\Users\1\.zframe-github-token`（仓库外一行）

已知网络限制（实测）：
- `git ls-remote https://github.com/...` **成功** → git 通道可用
- `api.github.com` **不可达**（`20.205.243.168:443` 超时）→ **`gh` CLI 无法使用，自托管 runner 注册大概率也失败**
- `github.com` 网页响应 ~12s，慢但可打开

## 七、后续可选

- L2 启用：用 `game-ci/unity-test-runner`（需 `UNITY_LICENSE` secret）或自托管
- 若要异地备份完整历史：`git bundle create zframe-history.bundle --all`（约 511MB，压缩后可能 200MB+）
- 若接受历史重写：`git filter-repo --path Tools/android_support_installer.exe --invert-paths`
  ——**不要做**，会让文档里几十处 commit hash 失效

## 验收要点（推送后补测）

- [ ] Actions 页面出现 `CI / Native (build + doctest)` 运行记录
- [ ] 该 job 绿（exit 0）
- [ ] 上传的仓库体积接近 54MB 而非 500MB+
- [ ] 本地 `legacy` 分支仍可 `git log`（历史没丢）
