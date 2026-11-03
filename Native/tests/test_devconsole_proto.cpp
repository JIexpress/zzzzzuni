// =============================================================================
// test_devconsole_proto.cpp —— 原生 DevConsole 协议纯逻辑单测（TASK-B-R9-16）
//
// 覆盖简报要求的「renderdoc 命令的参数与错误路径」协议层部分：
//   - 白名单五命令（help/hud/rg.export/renderdoc.capture/quit）的解析与参数个数
//   - 错误路径：空行 / 未知命令 / 参数过多（bad_arguments + usage detail）
//   - 信封组装：ok/error 结构化输出、detail 转义（引号/反斜杠/控制字符）
//   - 单行上限常量 = A-DEV-03 口径 4096B
// 传输层（TCP/线程）在窗口程序内，不在本单测范围（无 IO 纯逻辑层）。
// =============================================================================
#include "doctest/doctest.h"

#include <string>
#include <vector>

#include "zrender/devconsole_proto.h"

// ---- 1. 白名单解析 -----------------------------------------------------------------
TEST_CASE("devconsole-proto: whitelist commands parse with args")
{
    using zrender::ZrDevConsoleCommand;
    using zrender::ZrDevConsoleParse;

    // help / hud / quit：零参数
    zrender::ZrDevConsoleRequest r = ZrDevConsoleParse("help");
    CHECK(r.valid);
    CHECK(r.command == ZrDevConsoleCommand::Help);
    CHECK(r.args.empty());
    r = ZrDevConsoleParse("hud");
    CHECK(r.valid);
    CHECK(r.command == ZrDevConsoleCommand::Hud);
    r = ZrDevConsoleParse("quit");
    CHECK(r.valid);
    CHECK(r.command == ZrDevConsoleCommand::Quit);

    // 行首尾空白 + \r\n（传输层残尾）都容忍
    r = ZrDevConsoleParse("  hud \r\n");
    CHECK(r.valid);
    CHECK(r.command == ZrDevConsoleCommand::Hud);

    // rg.export：0..2 参数（缺省路径/格式）
    r = ZrDevConsoleParse("rg.export");
    CHECK(r.valid);
    CHECK(r.command == ZrDevConsoleCommand::RgExport);
    CHECK(r.args.empty());
    r = ZrDevConsoleParse("rg.export Native/build/renderdoc/rg.json");
    CHECK(r.valid);
    REQUIRE(r.args.size() == 1u);
    CHECK(r.args[0] == "Native/build/renderdoc/rg.json");
    r = ZrDevConsoleParse("rg.export rg.txt text");
    CHECK(r.valid);
    REQUIRE(r.args.size() == 2u);
    CHECK(r.args[0] == "rg.txt");
    CHECK(r.args[1] == "text");

    // renderdoc.capture：0..1 参数（缺省捕获名）
    r = ZrDevConsoleParse("renderdoc.capture");
    CHECK(r.valid);
    CHECK(r.command == ZrDevConsoleCommand::RenderDocCapture);
    r = ZrDevConsoleParse("renderdoc.capture sceneA");
    CHECK(r.valid);
    REQUIRE(r.args.size() == 1u);
    CHECK(r.args[0] == "sceneA");
}

TEST_CASE("devconsole-proto: multiple spaces collapse into separate args")
{
    using zrender::ZrDevConsoleParse;
    const zrender::ZrDevConsoleRequest r = ZrDevConsoleParse("rg.export   a.json    text");
    CHECK(r.valid);
    REQUIRE(r.args.size() == 2u);
    CHECK(r.args[0] == "a.json");
    CHECK(r.args[1] == "text");
}

// ---- 2. 错误路径（结构化，不崩溃）---------------------------------------------------
TEST_CASE("devconsole-proto: error paths are structured")
{
    using zrender::ZrDevConsoleParse;

    // 空行（含纯空白）
    zrender::ZrDevConsoleRequest r = ZrDevConsoleParse("");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "empty_line");
    r = ZrDevConsoleParse("   \t \r\n");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "empty_line");

    // 未知命令：错误码 + detail 带命令名
    r = ZrDevConsoleParse("bogus");
    CHECK_FALSE(r.valid);
    CHECK(r.command == zrender::ZrDevConsoleCommand::None);
    CHECK(r.error == "unknown_command");
    CHECK(r.detail.find("bogus") != std::string::npos);

    // 未知命令不因前缀相似而误中（rg.export vs rg）
    r = ZrDevConsoleParse("rg");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "unknown_command");

    // 参数过多：bad_arguments + usage
    r = ZrDevConsoleParse("help me");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "bad_arguments");
    CHECK(r.detail.find("usage: help") != std::string::npos);
    r = ZrDevConsoleParse("hud now");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "bad_arguments");
    r = ZrDevConsoleParse("rg.export a b c");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "bad_arguments");
    CHECK(r.detail.find("rg.export [path] [json|text]") != std::string::npos);
    r = ZrDevConsoleParse("renderdoc.capture a b");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "bad_arguments");
    r = ZrDevConsoleParse("quit now");
    CHECK_FALSE(r.valid);
    CHECK(r.error == "bad_arguments");

    // 单行长度上限常量 = A-DEV-03 口径
    CHECK(zrender::kZrDevConsoleMaxLineBytes == 4096u);
}

TEST_CASE("devconsole-proto: command name table matches whitelist")
{
    const std::vector<std::string> names = zrender::ZrDevConsoleCommandNames();
    REQUIRE(names.size() == 5u);
    CHECK(names[0] == "help");
    CHECK(names[1] == "hud");
    CHECK(names[2] == "rg.export [path] [json|text]");
    CHECK(names[3] == "renderdoc.capture [name]");
    CHECK(names[4] == "quit");
}

// ---- 3. 信封组装 -------------------------------------------------------------------
TEST_CASE("devconsole-proto: ok/error envelopes are single-line JSON")
{
    using zrender::ZrDevConsoleError;
    using zrender::ZrDevConsoleJsonEscape;
    using zrender::ZrDevConsoleOk;

    CHECK(ZrDevConsoleOk("") == "{\"ok\":true}");
    CHECK(ZrDevConsoleOk("\"drawCalls\":3") == "{\"ok\":true,\"drawCalls\":3}");

    // 错误信封：无 detail 省略键；有 detail 带 detail
    CHECK(ZrDevConsoleError("renderdoc_not_attached", "")
        == "{\"ok\":false,\"error\":\"renderdoc_not_attached\"}");
    CHECK(ZrDevConsoleError("bad_arguments", "usage: help")
        == "{\"ok\":false,\"error\":\"bad_arguments\",\"detail\":\"usage: help\"}");

    // detail 转义：引号/反斜杠/换行不破坏 JSON 单行契约
    CHECK(ZrDevConsoleError("e", "has \"quote\" and \\ slash")
        == "{\"ok\":false,\"error\":\"e\",\"detail\":"
           "\"has \\\"quote\\\" and \\\\ slash\"}");
    CHECK(ZrDevConsoleError("e", "line1\nline2")
        == "{\"ok\":false,\"error\":\"e\",\"detail\":\"line1\\nline2\"}");
    CHECK(ZrDevConsoleJsonEscape("tab\\there").find("\\t") != std::string::npos);
}

TEST_CASE("devconsole-proto: object payloads merge into the ok envelope")
{
    using zrender::ZrDevConsoleMergeObject;
    using zrender::ZrDevConsoleOk;

    // ZrHudFormatJson 产出单对象 JSON → 剥外壳并入信封 = 合法单行
    const std::string stats = "{\"drawCalls\":3,\"fps\":90.0000}";
    CHECK(ZrDevConsoleMergeObject(stats) == "\"drawCalls\":3,\"fps\":90.0000");
    CHECK(ZrDevConsoleOk(ZrDevConsoleMergeObject(stats))
        == "{\"ok\":true,\"drawCalls\":3,\"fps\":90.0000}");

    // 嵌套对象只剥最外层外壳（内层原样保留）
    CHECK(ZrDevConsoleMergeObject("{\"a\":{\"b\":1}}") == "\"a\":{\"b\":1}");

    // 非 "{...}" 形态：返回空串（信封退化为 {"ok":true}，绝不产出非法 JSON）
    CHECK(ZrDevConsoleMergeObject("") == "");
    CHECK(ZrDevConsoleMergeObject("no braces") == "");
    CHECK(ZrDevConsoleMergeObject("{broken") == "");
    CHECK(ZrDevConsoleOk(ZrDevConsoleMergeObject("broken")) == "{\"ok\":true}");
}
