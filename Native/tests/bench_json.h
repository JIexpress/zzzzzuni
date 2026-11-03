// =============================================================================
// bench_json.h —— benchmark 程序的 --json 单行输出生成（TASK-B-R9-10）
//
// 设计（任务简报设计点 1/2）：
//   · 程式化输出优先：三个 benchmark 程序（zkernel_bench / zrender_bench /
//     zrhi_d3d12_window --lights）各支持 `--json`，在**既有人类可读输出之后**
//     追加一行 `BENCH_JSON {...}`（单行 JSON，带 BENCH_JSON 前缀标记，
//     解析器按行首标记提取，避免与普通输出混淆；不破坏任何既有输出格式）。
//   · 纯函数 + 无状态写入器，便于 doctest 直接断言生成文本（test_bench_json.cpp）。
//   · 数字统一 %.4f 定点格式：确定性（两次运行格式逐位一致）、无科学计数法、
//     ConvertFrom-Json 可直接解析。整数指标走 MetricU64（无小数点）。
//   · 本头文件只被 tests/ 下程序与单测包含，不进任何模块公共头（file-conventions §3）。
// =============================================================================
#pragma once

#include <cstdio>
#include <cstring>
#include <string>

namespace zbench {

/// JSON 字符串转义：`"` 与 `\` 加反斜杠，控制字符转 \u00XX（当前调用方只传
/// 程序名/指标名等受控字面量，转义是防御性的，保持生成器与输入解耦）。
inline std::string JsonEscape(const char* s)
{
    std::string out;
    if (s == nullptr) {
        return out;
    }
    for (const char* p = s; *p != '\0'; ++p) {
        const unsigned char c = static_cast<unsigned char>(*p);
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
                    std::snprintf(tmp, sizeof(tmp), "\\u%04X", static_cast<unsigned>(c));
                    out += tmp;
                } else {
                    out += static_cast<char>(c);
                }
                break;
        }
    }
    return out;
}

/// 单行 JSON 写入器：{"program":"<名>","metrics":{<名>:<值>,...}}
/// 用法：构造 → 若干 Metric/MetricU64 → Finish() 取整行（含 BENCH_JSON 前缀）。
/// Finish 后写入器即定型（不可再追加），单行保证：缓冲内不含 '\n'。
class JsonLineWriter {
public:
    explicit JsonLineWriter(const char* program)
    {
        buf_ = "{\"program\":\"";
        buf_ += JsonEscape(program);
        buf_ += "\",\"metrics\":{";
    }

    void Metric(const char* name, double value)
    {
        AppendName(name);
        char tmp[64];
        std::snprintf(tmp, sizeof(tmp), "%.4f", value);
        buf_ += tmp;
    }

    void MetricU64(const char* name, unsigned long long value)
    {
        AppendName(name);
        char tmp[32];
        std::snprintf(tmp, sizeof(tmp), "%llu", value);
        buf_ += tmp;
    }

    /// 收尾：闭合 metrics 与根对象，返回带 `BENCH_JSON ` 前缀的单行文本。
    /// 幂等（重复调用返回同一文本，不会重复闭合）——防御误用，契约仍是"只 Finish 一次"。
    std::string Finish()
    {
        if (!finished_) {
            buf_ += "}}";
            finished_ = true;
        }
        return "BENCH_JSON " + buf_;
    }

private:
    void AppendName(const char* name)
    {
        if (!first_) {
            buf_ += ',';
        }
        first_ = false;
        buf_ += '"';
        buf_ += JsonEscape(name);
        buf_ += "\":";
    }

    std::string buf_;
    bool first_ = true;
    bool finished_ = false;
};

/// argv 中是否出现 --json（工具程序共用；参数顺序无关）。
inline bool HasJsonFlag(int argc, char** argv)
{
    for (int i = 1; i < argc; ++i) {
        if (argv[i] != nullptr && std::strcmp(argv[i], "--json") == 0) {
            return true;
        }
    }
    return false;
}

} // namespace zbench
