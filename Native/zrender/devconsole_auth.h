// =============================================================================
// devconsole_auth.h —— 原生 DevConsole 客户端认证（TASK-X-SEC-01）
//
// 背景：--devconsole 只绑 127.0.0.1，但**任何本机进程**都能连上发命令
// （白名单限制了危害面，"谁都能连"本身不该是常态）。本模块把"默认信任"
// 改成"默认拒绝，显式授权"：
//   - token 握手：进程启动用 BCryptGenRandom 生成 128 位随机 token，打到
//     stdout 日志（标注"仅本地调试"）；客户端首帧必须是 `auth <32hex>`；
//   - 常量时间比较：逐字节异或累积，不短路，不用 strcmp；
//   - 失败限流：连续 N 次认证失败 → 封 lockoutMs 毫秒，期间一律拒绝；
//     每次失败由调用方记录对端（peer），本模块只维护计数与时间窗。
//
// 纯逻辑（除 token 生成调用系统 CSPRNG 外无 IO/无全局状态），时间源由调用方
// 注入（nowMs），单测可直接驱动限流窗口。
// 命名遵循 file-conventions.md §3；前缀 ZrDevConsole。
// =============================================================================
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace zrender {

/// token 熵（字节）——128 位 = 32 个十六进制字符
constexpr uint32_t kZrDevConsoleTokenBytes = 16u;
/// token 十六进制字符数（协议层固定长度）
constexpr uint32_t kZrDevConsoleTokenHexChars = kZrDevConsoleTokenBytes * 2u;
/// 连续认证失败阈值（达到即封禁）
constexpr uint32_t kZrDevConsoleAuthMaxFailures = 5u;
/// 封禁时长（毫秒）
constexpr uint64_t kZrDevConsoleAuthLockoutMs = 30000u;

/// 字节序列 -> 小写十六进制（不做大小写归一；输入为空返回空串）
std::string ZrDevConsoleTokenToHex(const uint8_t* bytes, size_t count);

/// 形态校验：长度 = 32 且全部为 [0-9a-f]（大小写不敏感接受，但比较前不折叠——
/// 见 ZrDevConsoleTokenEquals 的说明）
bool ZrDevConsoleIsHexToken(const std::string& value);

/// 用 BCryptGenRandom(BCRYPT_USE_SYSTEM_PREFERRED_RNG) 生成 128 位 token。
/// 失败返回空串并在 outError 写原因（不抛异常，不退化到 rand()）。
std::string ZrDevConsoleGenerateToken(std::string& outError);

/// 常量时间字符串比较：O(max(len)) 轮，逐字节异或累积，无数据相关分支/短路。
/// 长度不等时结果必为 false（长度本身不是秘密：token 定长 32 字符）。
/// 不折叠大小写——token 是二进制转写，大小写即不同 token。
bool ZrDevConsoleTokenEquals(const std::string& a, const std::string& b);

/// 认证状态机（仅监听线程访问；不内建锁）
class ZrDevConsoleAuthenticator {
public:
    enum class Verdict : uint32_t {
        Accepted = 0, // token 正确
        Rejected,     // token 错误（已计入失败）
        Locked,       // 处于封禁期，未做比较
    };

    explicit ZrDevConsoleAuthenticator(std::string token = std::string(),
        uint32_t maxFailures = kZrDevConsoleAuthMaxFailures,
        uint64_t lockoutMs = kZrDevConsoleAuthLockoutMs);

    void SetToken(std::string token);
    bool HasToken() const { return !m_token.empty(); }
    const std::string& Token() const { return m_token; }

    /// 是否处于封禁期；封禁自然到期时清零失败计数（惰性解锁）
    bool IsLocked(uint64_t nowMs);
    /// 剩余封禁毫秒（未封禁返回 0）
    uint64_t LockRemainingMs(uint64_t nowMs);

    /// 校验一次（含限流判定）。Accepted 时清零失败计数。
    Verdict Verify(const std::string& presented, uint64_t nowMs);

    uint32_t ConsecutiveFailures() const { return m_failures; }
    uint32_t MaxFailures() const { return m_maxFailures; }

private:
    void RecordFailure(uint64_t nowMs);

    std::string m_token;
    uint32_t m_maxFailures = kZrDevConsoleAuthMaxFailures;
    uint64_t m_lockoutMs = kZrDevConsoleAuthLockoutMs;
    uint32_t m_failures = 0u;
    uint64_t m_lockUntilMs = 0u; // 0 = 未封禁（绝对毫秒时间轴由调用方提供）
};

} // namespace zrender
