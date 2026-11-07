// =============================================================================
// devconsole_auth.cpp —— 原生 DevConsole 客户端认证实现（TASK-X-SEC-01）
// =============================================================================
#include "zrender/devconsole_auth.h"

#include <cstdio>
#include <utility>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <bcrypt.h>

namespace zrender {

std::string ZrDevConsoleTokenToHex(const uint8_t* bytes, size_t count)
{
    static const char kHex[] = "0123456789abcdef";
    std::string out;
    if (bytes == nullptr) {
        return out;
    }
    out.reserve(count * 2u);
    for (size_t i = 0; i < count; ++i) {
        out += kHex[(bytes[i] >> 4) & 0x0Fu];
        out += kHex[bytes[i] & 0x0Fu];
    }
    return out;
}

bool ZrDevConsoleIsHexToken(const std::string& value)
{
    if (value.size() != kZrDevConsoleTokenHexChars) {
        return false;
    }
    for (const char ch : value) {
        const bool digit = ch >= '0' && ch <= '9';
        const bool lower = ch >= 'a' && ch <= 'f';
        const bool upper = ch >= 'A' && ch <= 'F';
        if (!digit && !lower && !upper) {
            return false;
        }
    }
    return true;
}

std::string ZrDevConsoleGenerateToken(std::string& outError)
{
    uint8_t bytes[kZrDevConsoleTokenBytes] = {};
    // 系统首选 CSPRNG：不可预测（绝不用 rand()/时间戳退化）
    const NTSTATUS status = ::BCryptGenRandom(nullptr, bytes, sizeof(bytes),
        BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (status != 0) {
        char detail[64] = {};
        std::snprintf(detail, sizeof(detail), "0x%08lX",
            static_cast<unsigned long>(status));
        outError = std::string("BCryptGenRandom failed (NTSTATUS ") + detail + ')';
        return std::string();
    }
    return ZrDevConsoleTokenToHex(bytes, sizeof(bytes));
}

bool ZrDevConsoleTokenEquals(const std::string& a, const std::string& b)
{
    // 常量时间：轮数只取决于长度（token 定长，长度不保密），每轮做一次异或
    // 累积，不因首个不等字节提前返回。长度不等时差异位始终保持置位。
    const size_t rounds = a.size() >= b.size() ? a.size() : b.size();
    unsigned diff = (a.size() == b.size()) ? 0u : 1u;
    for (size_t i = 0; i < rounds; ++i) {
        const unsigned char ca
            = i < a.size() ? static_cast<unsigned char>(a[i]) : 0u;
        const unsigned char cb
            = i < b.size() ? static_cast<unsigned char>(b[i]) : 0u;
        diff |= static_cast<unsigned>(ca ^ cb);
    }
    return diff == 0u;
}

ZrDevConsoleAuthenticator::ZrDevConsoleAuthenticator(std::string token,
    uint32_t maxFailures, uint64_t lockoutMs)
    : m_token(std::move(token))
    , m_maxFailures(maxFailures == 0u ? 1u : maxFailures)
    , m_lockoutMs(lockoutMs)
{
}

void ZrDevConsoleAuthenticator::SetToken(std::string token)
{
    m_token = std::move(token);
    m_failures = 0u;
    m_lockUntilMs = 0u;
}

bool ZrDevConsoleAuthenticator::IsLocked(uint64_t nowMs)
{
    if (m_lockUntilMs == 0u) {
        return false;
    }
    if (nowMs < m_lockUntilMs) {
        return true;
    }
    // 封禁到期：惰性解锁并清零计数（不依赖后台定时器）
    m_lockUntilMs = 0u;
    m_failures = 0u;
    return false;
}

uint64_t ZrDevConsoleAuthenticator::LockRemainingMs(uint64_t nowMs)
{
    if (!IsLocked(nowMs)) {
        return 0u;
    }
    return m_lockUntilMs - nowMs;
}

void ZrDevConsoleAuthenticator::RecordFailure(uint64_t nowMs)
{
    if (m_failures < m_maxFailures) {
        ++m_failures;
    }
    if (m_failures >= m_maxFailures) {
        m_lockUntilMs = nowMs + m_lockoutMs;
    }
}

ZrDevConsoleAuthenticator::Verdict ZrDevConsoleAuthenticator::Verify(
    const std::string& presented, uint64_t nowMs)
{
    if (IsLocked(nowMs)) {
        return Verdict::Locked;
    }
    // 空 token = 未配置：一律拒绝（绝不出现 ""=="" 即放行的退化）
    if (m_token.empty()) {
        RecordFailure(nowMs);
        return Verdict::Rejected;
    }
    if (ZrDevConsoleTokenEquals(presented, m_token)) {
        m_failures = 0u;
        m_lockUntilMs = 0u;
        return Verdict::Accepted;
    }
    RecordFailure(nowMs);
    return Verdict::Rejected;
}

} // namespace zrender
