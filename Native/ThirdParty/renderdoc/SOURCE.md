# RenderDoc in-application API 来源说明（第三方，MIT License）

| 项 | 值 |
|---|---|
| 名称 | RenderDoc in-application API（`renderdoc_app.h` 公开单头） |
| 版本 | RenderDoc v1.37（官方 release tag `v1.37`） |
| 上游仓库 | https://github.com/baldurk/renderdoc |
| 文件路径（上游仓库内） | `renderdoc/api/app/renderdoc_app.h`（git blob sha1 `c01e05932e2d4d424a269a254192baa55e20b811`） |
| 下载日期 | 2026-11-04 |
| 本地文件 | `Native/ThirdParty/renderdoc/renderdoc_app.h`（31,606 字节） |
| SHA256 | `0ab27bfa81235ebfc8f4c48111399ea81477eb4f77665e6209c9247daee91b94`（本地实算） |
| 许可证 | MIT（RenderDoc 仓库 LICENSE.md；本头文件头部含版权声明） |

## 用途（TASK-B-R9-16）

RenderDoc **in-application API**：应用进程被 RenderDoc UI 注入 `renderdoc.dll` 后，
经 `GetModuleHandleA("renderdoc.dll")` + `GetProcAddress("RENDERDOC_GetAPI")` 取得
`RENDERDOC_API_1_6_0` 函数表，即可编程触发帧捕获（`TriggerCapture`）并指定
.rdc 落盘路径模板（`SetCaptureFilePathTemplate`）。**不引入第三方运行时**——
RenderDoc 本体（UI/replay）仍由用户手动启动并从其 UI 启动目标程序。

## 下载方式与完整性校验

- `raw.githubusercontent.com` 直连被重置（B-R6-01/doctest 同款网络环境），
  走 GitHub 官方 REST API（域名 `api.github.com`，https）：
  `GET https://api.github.com/repos/baldurk/renderdoc/contents/renderdoc/api/app/renderdoc_app.h?ref=v1.37`
  返回 base64 内容，本地解码后落盘。
- 完整性：解码结果按 `blob <size>\0<content>` 计算 git blob sha1，与 API 返回的
  `sha` 字段（上游 git 对象哈希）逐字节一致（`c01e0593…811`）——与官方仓库
  v1.37 tag 内容完全相同、无篡改。
- API 请求版本：`eRENDERDOC_API_Version_1_6_0`（该头支持的最高 v1.x）；
  RenderDoc 1.6+ 的注入层对更高请求会给出 ≤ 请求版本的最高可用表。

## 消费方式（zrender 侧封装）

- 唯一 include 点：`Native/zrender/renderdoc_capture.cpp`（内部实现文件，
  公共头 `renderdoc_capture.h` 不泄漏第三方符号）。
- 未挂接（进程内无 `renderdoc.dll`）→ 明确错误码 `ModuleNotFound`，
  DevConsole 命令 `renderdoc.capture` 返回
  `{"ok":false,"error":"renderdoc_not_attached",...}`——不静默、不假成功。

> 本文件由 TASK-B-R9-16 施工代理生成；升级头文件时请同步更新上表。
