// =============================================================================
// test_devconsole_auth.cpp —— DevConsole 客户端认证纯逻辑单测（TASK-X-SEC-01）
//
// 覆盖：
//   - token 生成形态：32 个十六进制字符、两次生成不同（CSPRNG 非确定性）
//   - 常量时间比较的语义：相等/单字节差异/长度差异/空串
//   - 限流窗口：连续 N 次失败 -> 封禁；封禁期拒绝且不消耗比较；到期自动解锁；
//     成功一次清零失败计数（避免正常客户端被历史失败拖累）
// 时间源注入（nowMs），不依赖真实时钟——用例确定性。
// =============================================================================
#include "doctest/doctest.h"

#include <cstdint>
#include <string>

#include "zrender/devconsole_auth.h"

using zrender::ZrDevConsoleAuthenticator;
using zrender::ZrDevConsoleGenerateToken;
using zrender::ZrDevConsoleIsHexToken;
using zrender::ZrDevConsoleTokenEquals;
using zrender::ZrDevConsoleTokenToHex;

// ---- 1. token 生成与形态 ------------------------------------------------------------
TEST_CASE("devconsole-auth: token is 128-bit lowercase hex and random")
{
    std::string error;
    const std::string token = ZrDevConsoleGenerateToken(error);
    CHECK(error.empty());
    REQUIRE(token.size() == zrender::kZrDevConsoleTokenHexChars);
    CHECK(ZrDevConsoleIsHexToken(token));

    // 全小写（协议层按二进制转写，不做大小写折叠）
    for (const char ch : token) {
        const bool lowerHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
        CHECK(lowerHex);
    }

    std::string secondError;
    const std::string second = ZrDevConsoleGenerateToken(secondError);
    CHECK(secondError.empty());
    CHECK(second.size() == token.size());
    CHECK(second != token); // 128 位随机，碰撞概率 2^-128
}

TEST_CASE("devconsole-auth: hex token shape validation rejects malformed input")
{
    CHECK(ZrDevConsoleIsHexToken("0123456789abcdef0123456789abcdef"));
    CHECK(ZrDevConsoleIsHexToken("0123456789ABCDEF0123456789ABCDEF"));
    CHECK_FALSE(ZrDevConsoleIsHexToken(""));
    CHECK_FALSE(ZrDevConsoleIsHexToken("0123456789abcdef0123456789abcde"));   // 31
    CHECK_FALSE(ZrDevConsoleIsHexToken("0123456789abcdef0123456789abcdef0")); // 33
    CHECK_FALSE(ZrDevConsoleIsHexToken("0123456789abcdef0123456789abcdeg"));  // 非 hex
    CHECK_FALSE(ZrDevConsoleIsHexToken("0123456789abcdef0123456789abcde "));  // 尾空格
}

TEST_CASE("devconsole-auth: hex encoder is lowercase and length-stable")
{
    const uint8_t bytes[4] = {0x00u, 0xAFu, 0x10u, 0xFFu};
    CHECK(ZrDevConsoleTokenToHex(bytes, sizeof(bytes)) == "00af10ff");
    CHECK(ZrDevConsoleTokenToHex(nullptr, 4u).empty());
}

// ---- 2. 常量时间比较（语义面；实现见 devconsole_auth.cpp 无短路循环）--------------
TEST_CASE("devconsole-auth: constant-time compare semantics")
{
    const std::string token = "0123456789abcdef0123456789abcdef";
    CHECK(ZrDevConsoleTokenEquals(token, token));
    CHECK(ZrDevConsoleTokenEquals("", ""));

    // 只差最后一个字符（最坏时序位置）也必须判定不等
    std::string lastDiff = token;
    lastDiff.back() = 'e';
    CHECK_FALSE(ZrDevConsoleTokenEquals(token, lastDiff));

    // 只差第一个字符
    std::string firstDiff = token;
    firstDiff[0] = 'f';
    CHECK_FALSE(ZrDevConsoleTokenEquals(token, firstDiff));

    // 长度不同：短路比较的常见漏洞点
    CHECK_FALSE(ZrDevConsoleTokenEquals(token, token.substr(0u, token.size() - 1u)));
    CHECK_FALSE(ZrDevConsoleTokenEquals(token, token + "0"));
    CHECK_FALSE(ZrDevConsoleTokenEquals(token, ""));
    CHECK_FALSE(ZrDevConsoleTokenEquals("", token));

    // 大小写不同 = 不同 token（不折叠）
    std::string upper = token;
    for (char& ch : upper) {
        if (ch >= 'a' && ch <= 'f') {
            ch = static_cast<char>(ch - 'a' + 'A');
        }
    }
    CHECK_FALSE(ZrDevConsoleTokenEquals(token, upper));
}

// ---- 3. 失败限流 -------------------------------------------------------------------
TEST_CASE("devconsole-auth: consecutive failures lock the client out")
{
    const std::string token = "0123456789abcdef0123456789abcdef";
    ZrDevConsoleAuthenticator auth(token);

    // 前 4 次失败：拒绝但未封禁
    for (uint32_t i = 0u; i < zrender::kZrDevConsoleAuthMaxFailures - 1u; ++i) {
        CHECK(auth.Verify("wrong", 1000u) == ZrDevConsoleAuthenticator::Verdict::Rejected);
        CHECK_FALSE(auth.IsLocked(1000u));
    }
    CHECK(auth.ConsecutiveFailures() == zrender::kZrDevConsoleAuthMaxFailures - 1u);

    // 第 5 次：达到阈值 -> 进入封禁
    CHECK(auth.Verify("wrong", 1000u) == ZrDevConsoleAuthenticator::Verdict::Rejected);
    CHECK(auth.IsLocked(1000u));
    CHECK(auth.LockRemainingMs(1000u) == zrender::kZrDevConsoleAuthLockoutMs);

    // 封禁期内：即使 token 正确也拒绝（Locked，不再做比较/累加）
    CHECK(auth.Verify(token, 2000u) == ZrDevConsoleAuthenticator::Verdict::Locked);
    CHECK(auth.Verify("wrong", 2000u) == ZrDevConsoleAuthenticator::Verdict::Locked);
    CHECK(auth.LockRemainingMs(2000u) == zrender::kZrDevConsoleAuthLockoutMs - 1000u);

    // 封禁到期：自动解锁，正确 token 通过，失败计数清零
    const uint64_t afterLock = 1000u + zrender::kZrDevConsoleAuthLockoutMs;
    CHECK_FALSE(auth.IsLocked(afterLock));
    CHECK(auth.ConsecutiveFailures() == 0u);
    CHECK(auth.Verify(token, afterLock) == ZrDevConsoleAuthenticator::Verdict::Accepted);
}

TEST_CASE("devconsole-auth: a success resets the failure streak")
{
    const std::string token = "0123456789abcdef0123456789abcdef";
    ZrDevConsoleAuthenticator auth(token);

    for (uint32_t i = 0u; i < zrender::kZrDevConsoleAuthMaxFailures - 1u; ++i) {
        CHECK(auth.Verify("wrong", 0u) == ZrDevConsoleAuthenticator::Verdict::Rejected);
    }
    CHECK(auth.Verify(token, 0u) == ZrDevConsoleAuthenticator::Verdict::Accepted);
    CHECK(auth.ConsecutiveFailures() == 0u);

    // 复位后再失败 4 次仍不封禁（历史失败不累计）
    for (uint32_t i = 0u; i < zrender::kZrDevConsoleAuthMaxFailures - 1u; ++i) {
        CHECK(auth.Verify("wrong", 10u) == ZrDevConsoleAuthenticator::Verdict::Rejected);
    }
    CHECK_FALSE(auth.IsLocked(10u));
}

TEST_CASE("devconsole-auth: no token configured always rejects")
{
    // 空 token：HasToken=false（调用方据此拒绝启动，绝不"无 token 即放行"）。
    // 注意 ""=="" 本身相等——安全性来自 Verify 对空 token 的显式拒绝，而不是比较结果。
    ZrDevConsoleAuthenticator auth;
    CHECK_FALSE(auth.HasToken());
    CHECK(auth.Token().empty());
    CHECK(auth.Verify("", 0u) == ZrDevConsoleAuthenticator::Verdict::Rejected);
    CHECK(auth.Verify("0123456789abcdef0123456789abcdef", 0u)
        == ZrDevConsoleAuthenticator::Verdict::Rejected);
}

TEST_CASE("devconsole-auth: policy parameters are honoured")
{
    const std::string token = "0123456789abcdef0123456789abcdef";
    ZrDevConsoleAuthenticator auth(token, 2u, 500u);
    CHECK(auth.Verify("wrong", 0u) == ZrDevConsoleAuthenticator::Verdict::Rejected);
    CHECK_FALSE(auth.IsLocked(0u));
    CHECK(auth.Verify("wrong", 0u) == ZrDevConsoleAuthenticator::Verdict::Rejected);
    CHECK(auth.IsLocked(0u));
    CHECK(auth.LockRemainingMs(200u) == 300u);
    CHECK_FALSE(auth.IsLocked(500u));
}
