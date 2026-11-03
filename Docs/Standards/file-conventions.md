# 文件与命名规范（Standards/file-conventions.md）

> 所有施工任务强制遵守；验收代理按本文件核对。机器强制：.editorconfig + .gitattributes。

## 1. 文本基础

- 编码：UTF-8 **无 BOM**；换行 LF（例外：`*.ps1` `*.bat` CRLF）
- 文件末尾保留一个换行符；不引入尾随空格
- 制表符：一律空格（C#/C++/HLSL/MD 4 空格，JSON/YAML 2 空格）

## 2. C#（Track A + 渲染壳）

- 风格基线：Microsoft Framework Design Guidelines；一个类型一个文件，文件名=类型名
- 命名：类/方法/属性 PascalCase；接口 `I` 前缀；私有字段 `_camelCase`；常量 `PascalCase`；公共 API 写 XML doc 注释（`///`）
- 程序集名 = asmdef 名（ZFrame.Core 等）；命名空间与程序集对应（`ZFrame.Core`、`ZFrame.Render.Bindings`）
- 禁止：`public` 字段（用属性或 [SerializeField]）、上帝类、`using` 通配符（`using System.*` 允许）
- 测试类与被测类同名加 `Tests` 后缀

## 3. C++（Native/）

- 风格基线：LLVM 变体——类型/函数 PascalCase；成员 `m_camelCase`；静态 `s_`；宏 `ZR_` 前缀全大写
- 头文件护栏 `#pragma once`；每个模块对外只暴露一个公共头（`zkernel/zkernel.h` 等），内部头不出模块目录
- 跨 DLL 边界只用 C ABI（`extern "C"` + POD 结构），托管侧 P/Invoke 声明集中在 `ZFrame.Render.Bindings`
- 错误处理：句柄+返回码（Vulkan 风格），异常禁止跨越 ABI 边界
- 测试：doctest，文件 `tests/test_<module>_<topic>.cpp`

## 4. HLSL

- 文件小写蛇形（`pbr_ggx.hlsl`、`tonemap_pass.ps.hlsl`）；include 相对 `Shaders/` 根
- 常量缓冲命名 `CBuf` 后缀，全部走 UnityPerMaterial 等价约定（SRP Batcher 兼容不适用自研管线，但 CB 对齐规则一致）

## 5. 资产命名（UnityProject/Assets）

| 前缀 | 用途 | 示例 |
|---|---|---|
| CHR_ | 角色 prefab/模型 | CHR_Hero.prefab |
| ENV_ | 场景物件 | ENV_Door.prefab |
| WPN_ | 武器道具 | WPN_Sword.prefab |
| UI_ | UI prefab | UI_PanelSettings.prefab |
| CFG_ | 配置 SO | CFG_EnemyTable.asset |
| MAT_ / TX_ / AUD_ / SCN_ | 材质/贴图/音频/场景 | MAT_Hero_Body.mat |

- 目录即约定（方案 §3.3）；场景放 `Scenes/`：`SCN_Boot`、`SCN_MainMenu`、`SCN_Game`
- `.meta` 文件必须与资产一同提交；禁止手改 meta 的 guid

## 6. 文档与日志

- 任务日志：`Docs/Logs/TASK-<ID>.md`，四节（简报/施工报告/验收清单/验收结论）齐全才算闭环
- 系统设计文档：`Docs/Design/<系统名>.md`（施工前由主控或施工代理产出）
- 本文件、AGENTS/HANDOFF/TASKS/WORKLOG/DECISIONS：只能由主控修改（施工代理发现错误→在报告里提）

## 7. 第三方代码

- 位置：`Native/ThirdParty/<name>/`（带 LICENSE 与版本来源注释）；Unity 侧包经 Package Manager
- 下载仅 https；来源限 GitHub 官方仓库/厂商官网；报告里注明 URL 与版本
