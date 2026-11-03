// =============================================================================
// devconsole_proto.cpp —— 原生 DevConsole 协议纯逻辑实现（TASK-B-R9-16）
// =============================================================================
#include "zrender/devconsole_proto.h"

#include <cstdio>

namespace zrender {

// =============================================================================
// 解析
// =============================================================================
ZrDevConsoleRequest ZrDevConsoleParse(const std::string& line)
{
    ZrDevConsoleRequest out;

    // 去掉行尾 \n / \r 与首尾空白（协议一行一请求；尾随换行由传输层残留）
    size_t begin = 0;
    size_t end = line.size();
    const auto isSpace = [](unsigned char c) {
        return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\v' || c == '\f';
    };
    while (begin < end && isSpace(static_cast<unsigned char>(line[begin]))) {
        ++begin;
    }
    while (end > begin && isSpace(static_cast<unsigned char>(line[end - 1]))) {
        --end;
    }
    if (begin == end) {
        out.error = "empty_line";
        out.detail = "request line is empty";
        return out;
    }

    // 按空白分词（连续空白视作一个分隔符；空 token 丢弃）
    std::vector<std::string> tokens;
    std::string current;
    for (size_t i = begin; i < end; ++i) {
        const unsigned char c = static_cast<unsigned char>(line[i]);
        if (isSpace(c)) {
            if (!current.empty()) {
                tokens.push_back(current);
                current.clear();
            }
        } else {
            current += static_cast<char>(c);
        }
    }
    if (!current.empty()) {
        tokens.push_back(current);
    }
    if (tokens.empty()) {
        out.error = "empty_line";
        out.detail = "request line has no tokens";
        return out;
    }

    // 白名单 + 参数个数校验（硬编码表——绝不反射、绝不 eval）
    const std::string& name = tokens[0];
    const size_t argCount = tokens.size() - 1;
    if (name == "help") {
        if (argCount != 0u) {
            out.error = "bad_arguments";
            out.detail = "usage: help";
            return out;
        }
        out.valid = true;
        out.command = ZrDevConsoleCommand::Help;
        return out;
    }
    if (name == "hud") {
        if (argCount != 0u) {
            out.error = "bad_arguments";
            out.detail = "usage: hud";
            return out;
        }
        out.valid = true;
        out.command = ZrDevConsoleCommand::Hud;
        return out;
    }
    if (name == "rg.export") {
        if (argCount > 2u) {
            out.error = "bad_arguments";
            out.detail = "usage: rg.export [path] [json|text]";
            return out;
        }
        out.valid = true;
        out.command = ZrDevConsoleCommand::RgExport;
        out.args.assign(tokens.begin() + 1, tokens.end());
        return out;
    }
    if (name == "renderdoc.capture") {
        if (argCount > 1u) {
            out.error = "bad_arguments";
            out.detail = "usage: renderdoc.capture [name]";
            return out;
        }
        out.valid = true;
        out.command = ZrDevConsoleCommand::RenderDocCapture;
        out.args.assign(tokens.begin() + 1, tokens.end());
        return out;
    }
    if (name == "quit") {
        if (argCount != 0u) {
            out.error = "bad_arguments";
            out.detail = "usage: quit";
            return out;
        }
        out.valid = true;
        out.command = ZrDevConsoleCommand::Quit;
        return out;
    }

    out.error = "unknown_command";
    out.detail = "unknown command '" + name + "' (try: help)";
    return out;
}

std::vector<std::string> ZrDevConsoleCommandNames()
{
    return { "help", "hud", "rg.export [path] [json|text]", "renderdoc.capture [name]", "quit" };
}

// =============================================================================
// 响应组装
// =============================================================================
std::string ZrDevConsoleJsonEscape(const std::string& s)
{
    std::string out;
    out.reserve(s.size() + 8u);
    for (const char ch : s) {
        const unsigned char c = static_cast<unsigned char>(ch);
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20u) {
                    char tmp[8];
                    std::snprintf(tmp, sizeof(tmp), "\\u%04X", c);
                    out += tmp;
                } else {
                    out += static_cast<char>(c);
                }
                break;
        }
    }
    return out;
}

std::string ZrDevConsoleOk(const std::string& payloadFields)
{
    std::string out = "{\"ok\":true";
    if (!payloadFields.empty()) {
        out += ',';
        out += payloadFields;
    }
    out += '}';
    return out;
}

std::string ZrDevConsoleError(const std::string& code, const std::string& detail)
{
    std::string out = "{\"ok\":false,\"error\":\"";
    out += ZrDevConsoleJsonEscape(code);
    out += '"';
    if (!detail.empty()) {
        out += ",\"detail\":\"";
        out += ZrDevConsoleJsonEscape(detail);
        out += '"';
    }
    out += '}';
    return out;
}

std::string ZrDevConsoleMergeObject(const std::string& objectJson)
{
    const size_t begin = objectJson.find('{');
    const size_t end = objectJson.rfind('}');
    if (begin == std::string::npos || end == std::string::npos || end <= begin) {
        return ""; // 非 "{...}" 形态：不拼接（调用方得到 {"ok":true} 而非非法 JSON）
    }
    return objectJson.substr(begin + 1u, end - begin - 1u);
}

} // namespace zrender
