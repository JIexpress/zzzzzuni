// =============================================================================
// test_bench_json.cpp —— bench 回归 JSON 单行输出生成器的 doctest 单测
// （TASK-B-R9-10：任务简报设计点 4——"C++ 侧 json 行生成正确性 doctest"）
//
// 被测对象：Native/tests/bench_json.h（zkernel_bench / zrender_bench /
// zrhi_d3d12_window --lights 三个程序共用的单行 JSON 生成纯函数）。
// 断言策略：对受控输入做**逐字节精确比较**（生成格式即契约——
// Tools/bench_regression.ps1 的解析器按 `BENCH_JSON ` 前缀 + 单行 + 合法 JSON 提取，
// 格式漂移 = 解析失败 = 门禁失明，故这里把格式钉死）。
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include <string>

#include "bench_json.h"

// -----------------------------------------------------------------------------
// JsonEscape：字符串转义
// -----------------------------------------------------------------------------

TEST_CASE("bench_json: JsonEscape 普通字符串原样通过")
{
    CHECK(zbench::JsonEscape("zkernel_bench") == "zkernel_bench");
    CHECK(zbench::JsonEscape("frame_ms_avg") == "frame_ms_avg");
    CHECK(zbench::JsonEscape("") == "");
}

TEST_CASE("bench_json: JsonEscape 转义引号/反斜杠/控制字符")
{
    CHECK(zbench::JsonEscape("a\"b") == "a\\\"b");
    CHECK(zbench::JsonEscape("a\\b") == "a\\\\b");
    CHECK(zbench::JsonEscape("a\nb") == "a\\nb");
    CHECK(zbench::JsonEscape("a\tb") == "a\\tb");
    // 注意："\x01b" 的十六进制转义会贪婪吞掉后续 hex 位（0x01B），
    // 必须用字符串拼接截断："\x01" "b" 才是 0x01 + 'b'
    CHECK(zbench::JsonEscape(std::string("a\x01"
                                   "b")
                                 .c_str())
          == "a\\u0001b");
}

TEST_CASE("bench_json: JsonEscape nullptr 安全返回空串")
{
    CHECK(zbench::JsonEscape(nullptr) == "");
}

// -----------------------------------------------------------------------------
// JsonLineWriter：单行 JSON 生成（格式即契约，逐字节断言）
// -----------------------------------------------------------------------------

TEST_CASE("bench_json: 单指标生成精确文本（BENCH_JSON 前缀 + 定点 %.4f）")
{
    zbench::JsonLineWriter w("prog");
    w.Metric("m1", 1.5);
    CHECK(w.Finish() == "BENCH_JSON {\"program\":\"prog\",\"metrics\":{\"m1\":1.5000}}");
}

TEST_CASE("bench_json: 多指标逗号分隔、无尾逗号、闭合完整")
{
    zbench::JsonLineWriter w("p");
    w.Metric("a", 1.0);
    w.Metric("b", 2.0);
    w.MetricU64("n", 7);
    CHECK(w.Finish()
          == "BENCH_JSON {\"program\":\"p\",\"metrics\":{\"a\":1.0000,\"b\":2.0000,\"n\":7}}");
}

TEST_CASE("bench_json: 整数指标不带小数点（batches/frames 类）")
{
    zbench::JsonLineWriter w("p");
    w.MetricU64("frames", 59);
    w.MetricU64("batches", 12345);
    CHECK(w.Finish() == "BENCH_JSON {\"program\":\"p\",\"metrics\":{\"frames\":59,\"batches\":12345}}");
}

TEST_CASE("bench_json: 生成结果保证单行（缓冲内零 '\\n'）")
{
    zbench::JsonLineWriter w("zrender_bench");
    for (int i = 0; i < 16; ++i) {
        w.Metric("metric_name_x", 1234.5678);
    }
    const std::string line = w.Finish();
    CHECK(line.find('\n') == std::string::npos);
    CHECK(line.find('\r') == std::string::npos);
    CHECK(line.rfind("BENCH_JSON ", 0) == 0);           // 前缀在行首
    CHECK(line.substr(line.size() - 2) == "}}");        // metrics 与根对象闭合
}

TEST_CASE("bench_json: 数字格式确定性——同输入同输出（两次运行逐位一致）")
{
    zbench::JsonLineWriter w1("p");
    zbench::JsonLineWriter w2("p");
    w1.Metric("x", 0.0001);
    w1.Metric("y", 12345678.901234);
    w2.Metric("x", 0.0001);
    w2.Metric("y", 12345678.901234);
    const std::string l1 = w1.Finish();
    const std::string l2 = w2.Finish();
    CHECK(l1 == l2);
    CHECK(l1 == "BENCH_JSON {\"program\":\"p\",\"metrics\":{\"x\":0.0001,\"y\":12345678.9012}}");
}

TEST_CASE("bench_json: 程序名/指标名含特殊字符时转义且整体仍单行")
{
    zbench::JsonLineWriter w("pro\"g");
    w.Metric("na\"me", 1.0);
    const std::string line = w.Finish();
    CHECK(line == "BENCH_JSON {\"program\":\"pro\\\"g\",\"metrics\":{\"na\\\"me\":1.0000}}");
    CHECK(line.find('\n') == std::string::npos);
}

TEST_CASE("bench_json: 零指标仍是合法闭合结构")
{
    zbench::JsonLineWriter w("p");
    CHECK(w.Finish() == "BENCH_JSON {\"program\":\"p\",\"metrics\":{}}");
}

// -----------------------------------------------------------------------------
// HasJsonFlag：argv 扫描
// -----------------------------------------------------------------------------

TEST_CASE("bench_json: HasJsonFlag 任意位置命中 --json")
{
    char a0[] = "zkernel_bench.exe";
    char a1[] = "--json";
    char a2[] = "--other";
    char* argv1[] = {a0, a1, a2};
    char* argv2[] = {a0, a2, a1};
    char* argv3[] = {a0, a2};
    CHECK(zbench::HasJsonFlag(3, argv1));
    CHECK(zbench::HasJsonFlag(3, argv2));
    CHECK(!zbench::HasJsonFlag(2, argv3));
    CHECK(!zbench::HasJsonFlag(1, argv3));
}

TEST_CASE("bench_json: HasJsonFlag 不接受带值变体（--json=1 非开关）")
{
    char a0[] = "prog.exe";
    char a1[] = "--json=1";
    char* argv1[] = {a0, a1};
    CHECK(!zbench::HasJsonFlag(2, argv1));
}
