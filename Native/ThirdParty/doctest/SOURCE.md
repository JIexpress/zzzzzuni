# doctest 来源说明（第三方，MIT License）

| 项 | 值 |
|---|---|
| 名称 | doctest（C++ 单头测试框架） |
| 版本 | 2.5.3（官方 release tag `v2.5.3`） |
| 上游仓库 | https://github.com/doctest/doctest |
| 文件路径（仓库内） | `doctest/doctest.h`（tag `v2.5.3`，blob sha1 `0b36ccafaed7f4802cdf1b1c322977489b1dd658`） |
| 下载日期 | 2026-10-23 |
| 本地文件 | `Native/ThirdParty/doctest/doctest.h`（362,930 字节） |
| SHA256 | `cfd518a3ef90f67e1f3ba514df23fb3627437de1a2feeba78cf5062a40021421` |
| 许可证 | MIT（文件头内含完整许可证文本） |

## 下载方式与完整性校验

- 标准入口 `https://github.com/doctest/doctest/raw/v2.5.3/doctest/doctest.h` 与
  `https://raw.githubusercontent.com/doctest/doctest/v2.5.3/doctest/doctest.h`
  在本机网络下连接被重置（Connection reset），无法直接拉取。
- 降级为 GitHub 官方 REST API（域名 `api.github.com`，https）：
  `GET https://api.github.com/repos/doctest/doctest/contents/doctest/doctest.h?ref=v2.5.3`
  返回 base64 内容，本地解码后落盘。
- 完整性：解码结果按 `blob <size>\0<content>` 计算 git blob sha1，与 API 返回的
  `sha` 字段（即上游 git 对象哈希）逐字节一致，证明与官方仓库内容完全相同、无篡改。

> 本文件由 TASK-B-R0-01 施工代理生成；升级 doctest 时请同步更新上表。
