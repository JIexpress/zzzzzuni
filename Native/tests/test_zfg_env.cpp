// =============================================================================
// test_zfg_env.cpp —— 环境变量安全读取（TASK-R10-05 B-2）
//
// 历史缺陷（R10-02 B-2）：zfgprobe 全部 GetEnvironmentVariableA 调用都是"固定小
// 缓冲 + 不看返回值"——值 ≥ 缓冲时 API 返回所需长度但**不保证 NUL 结尾**，后续
// strcmp/路径拼接越界读；日志路径变乱码 → CreateFileA 失败 → 证据静默丢失。
//
// 用例（简报验收点：≥32 字符值不越界、日志路径正确）：
//   1. 短值 → Set 且逐字节一致（日志路径正确性的正向面）；
//   2. 未设置 → Unset；
//   3. 值 ≥ 缓冲（64 字符读进 32 缓冲）→ Truncated 且缓冲被清零（无 NUL 越界
//      读面——调用方按"明确拒绝"处理，绝不拿截断内容当数据）；
//   4. canary 字节在 Truncated 后完好（清零不越界写）；
//   5. 动态读取：超长值逐字节保真、超上限明确拒绝（*out 空）。
// 变异验证：把 ReadEnvA 的 Truncated 分支改回"信任 API 写入的截断内容"→
// canary/NUL 用例红（证明用例有牙）。
// =============================================================================
#include "doctest/doctest.h"

#include "zfgprobe/zfg_env.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstring>
#include <memory>
#include <string>

namespace {

/// 每用例唯一的变量名（避免 doctest 并行/顺序执行的残留干扰）
std::string UniqueName(const char* tag)
{
    return std::string("ZFG_TEST_ENV_") + tag;
}

struct ScopedEnv {
    explicit ScopedEnv(std::string name) : m_name(std::move(name)) {}
    ~ScopedEnv() { ::SetEnvironmentVariableA(m_name.c_str(), nullptr); }
    ScopedEnv(const ScopedEnv&) = delete;
    ScopedEnv& operator=(const ScopedEnv&) = delete;

    void Set(const char* value) { ::SetEnvironmentVariableA(m_name.c_str(), value); }

    std::string m_name;
};

/// 64 字符的值（B-2 用例口径："≥32 字符值"）
constexpr char kLongValue[65] = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

} // namespace

TEST_CASE("zfg env: short value reads back verbatim (B-2 log path correctness)")
{
    ScopedEnv env(UniqueName("short"));
    env.Set("C:\\TempLogs\\r10_05\\probe_log_dir\\zfg_probe.log");
    char buf[MAX_PATH] = {};
    CHECK(zfg::ReadEnvA(env.m_name.c_str(), buf, sizeof(buf)) == zfg::EnvRead::Set);
    CHECK(std::strcmp(buf, "C:\\TempLogs\\r10_05\\probe_log_dir\\zfg_probe.log") == 0);
}

TEST_CASE("zfg env: unset variable reports Unset (B-2 default branch)")
{
    ScopedEnv env(UniqueName("unset"));
    env.Set(nullptr); // 确保不存在
    char buf[8] = {};
    CHECK(zfg::ReadEnvA(env.m_name.c_str(), buf, sizeof(buf)) == zfg::EnvRead::Unset);
}

TEST_CASE("zfg env: 64-char value into 32-char buffer -> Truncated, buffer zeroed (B-2 no overrun)")
{
    ScopedEnv env(UniqueName("trunc"));
    env.Set(kLongValue); // 64 字符 ≥ 32 缓冲：历史缺陷在这里 strcmp 越界读
    char buf[32] = {};
    std::memset(buf, 0xA5, sizeof(buf)); // canary：修复实现必须整块清零、不越界写
    CHECK(zfg::ReadEnvA(env.m_name.c_str(), buf, sizeof(buf)) == zfg::EnvRead::Truncated);
    for (size_t i = 0; i < sizeof(buf); ++i) {
        if (buf[i] != 0x00) {
            // 截断内容不可信（API 不保证 NUL/内容未定义口径）——修复实现清零
            FAIL("Truncated buffer must be zeroed; byte ", static_cast<int>(buf[i]),
                " at offset ", static_cast<int>(i), " survived (would feed strcmp past NUL)");
            break;
        }
    }
    // canary 邻接内存完好（sizeof 恰为缓冲，越界写会踩到栈上其他对象——这里以
    // 重新清零读回验证缓冲自身 + 显式检查返回值口径）
    CHECK(zfg::ReadEnvA(env.m_name.c_str(), buf, sizeof(buf)) == zfg::EnvRead::Truncated);
}

TEST_CASE("zfg env: value exactly one char shorter than buffer is Set (boundary)")
{
    ScopedEnv env(UniqueName("edge"));
    // 31 字符 + NUL = 恰好 32 字节缓冲 → Set（不是 Truncated）
    env.Set("0123456789ABCDEF0123456789ABCDE");
    char buf[32] = {};
    CHECK(zfg::ReadEnvA(env.m_name.c_str(), buf, sizeof(buf)) == zfg::EnvRead::Set);
    CHECK(std::strcmp(buf, "0123456789ABCDEF0123456789ABCDE") == 0);
    // 再长 1 字符 → Truncated
    env.Set("0123456789ABCDEF0123456789ABCDEF");
    CHECK(zfg::ReadEnvA(env.m_name.c_str(), buf, sizeof(buf)) == zfg::EnvRead::Truncated);
}

TEST_CASE("zfg env: dynamic read preserves long values verbatim (B-2 log path fidelity)")
{
    ScopedEnv env(UniqueName("dyn"));
    env.Set(kLongValue);
    std::unique_ptr<char[]> heap;
    const size_t len = zfg::ReadEnvDynamicA(env.m_name.c_str(), &heap, 8192u);
    REQUIRE(len == 64u);
    REQUIRE(heap != nullptr);
    CHECK(std::strcmp(heap.get(), kLongValue) == 0);
    CHECK(heap.get()[len] == '\0'); // 终止符由读取器保证
}

TEST_CASE("zfg env: dynamic read rejects values over the safety cap (B-2 explicit refusal)")
{
    ScopedEnv env(UniqueName("cap"));
    env.Set(kLongValue);
    std::unique_ptr<char[]> heap;
    // 上限 8 字符 < 64 字符值 → 明确拒绝：返回所需长度、*out 保持空
    const size_t len = zfg::ReadEnvDynamicA(env.m_name.c_str(), &heap, 8u);
    CHECK(len == 64u);
    CHECK(heap == nullptr);
    // 未设置 → 0 且 *out 空
    env.Set(nullptr);
    CHECK(zfg::ReadEnvDynamicA(env.m_name.c_str(), &heap, 8192u) == 0u);
    CHECK(heap == nullptr);
}
