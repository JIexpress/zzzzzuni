// =============================================================================
// zfg_env.h —— GetEnvironmentVariableA 的安全读取（TASK-R10-05 B-2）
//
// 历史缺陷（R10-02 B-2）：zfgprobe 全部环境变量读取都用"固定小缓冲 + 不看返回值"
// 的写法。缓冲不足时 GetEnvironmentVariableA 返回所需长度但**不保证 NUL 结尾**
// （文档口径：缓冲内容未定义）→ 后续 strcmp/路径拼接越界读，日志路径变成乱码 →
// CreateFileA 失败 → 取证证据静默丢失。
//
// 这里给两个读取器，产品代码与单测共用（头文件内联，无 cpp）：
//   · ReadEnvA       —— 固定缓冲三态读取；Truncated 时清空缓冲并如实上报，
//                       调用方按"明确拒绝/回退默认"处理（绝不带病续读）；
//   · ReadEnvDynamicA—— 两段式动态长度读取（先探长度再按需分配），用于日志路径
//                       这类"超长也该保真"的值；超过安全上限时明确拒绝。
//
// 语义钉死（调用方注释引用这里）：
//   · Unset    = 变量未设置（或读取瞬时失败）→ 走"未设置"分支；
//   · Truncated= 值 ≥ 缓冲容量 → 输出缓冲已被清零，调用方不得把内容当数据用；
//   · 动态读取拒绝 = 值超过 maxChars（防敌意环境变量撑爆内存）→ *out 为空。
// =============================================================================
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstring>
#include <memory>

namespace zfg {

/// 固定缓冲读取结果
enum class EnvRead {
    Unset = 0,     ///< 未设置（调用方走默认/关闭分支）
    Set = 1,       ///< 已读取；out 内为完整值（API 已写 NUL）
    Truncated = 2, ///< 值 ≥ cap：out 已被**清零**，调用方必须按"明确拒绝"处理
};

/// 安全读取（固定缓冲）。cap 以字节计（含 NUL 空间）。
/// 历史缺陷对照：`char mode[32]; GetEnvironmentVariableA("ZFG_HOOK", mode, 32);`
/// 不看返回值 → 值 ≥ 32 字符时缓冲无 NUL，strcmp 越界读（R10-02 B-2）。
inline EnvRead ReadEnvA(const char* name, char* out, size_t cap)
{
    if (name == nullptr || out == nullptr || cap == 0u || cap > 0x7FFFFFFFull) {
        return EnvRead::Unset;
    }
    const DWORD need = ::GetEnvironmentVariableA(name, out, static_cast<DWORD>(cap));
    if (need == 0u) {
        return EnvRead::Unset; // 未设置（ERROR_ENVVAR_NOT_FOUND；空值等同未设）
    }
    if (static_cast<size_t>(need) >= cap) {
        // 缓冲不足：API 写入的截断内容不可信（无 NUL/内容未定义口径）→ 清零上报
        std::memset(out, 0, cap);
        return EnvRead::Truncated;
    }
    return EnvRead::Set;
}

/// 动态长度读取（两段式：先探所需长度，再按需分配）。用于日志路径这类
/// "值超长也该保真"的场景（B-2 用例：≥32 字符值不越界、日志路径正确）。
///
/// 返回值 = 值的字符数（不含 NUL）：
///   0              —— 未设置（*out 保持空）
///   > maxChars     —— 超过安全上限，明确拒绝（*out 保持空）
///   1..maxChars    —— 成功；*out = 堆缓冲（new char[]，unique_ptr 自动释放）
///
/// MSDN 口径：缓冲不足时 GetEnvironmentVariableA 返回"容纳值 + 终止 NUL 所需的
/// 字符数"（含 NUL）——所以拒绝路径返回 need-1 才是值的真实字符数。
inline size_t ReadEnvDynamicA(
    const char* name, std::unique_ptr<char[]>* out, size_t maxChars)
{
    if (name == nullptr || out == nullptr) {
        return 0u;
    }
    out->reset();
    char probe[4] = {}; // 第一遍只探长度；截断内容丢弃（不读）
    const DWORD need = ::GetEnvironmentVariableA(name, probe, static_cast<DWORD>(sizeof(probe)));
    if (need == 0u) {
        return 0u; // 未设置
    }
    const size_t valueChars = static_cast<size_t>(need) - 1u; // 去掉 NUL 计数
    if (valueChars > maxChars) {
        return valueChars; // 明确拒绝（*out 为空）
    }
    std::unique_ptr<char[]> buf(new (std::nothrow) char[valueChars + 1u]);
    if (buf == nullptr) {
        return valueChars; // 分配失败：拒绝但不谎报"未设置"
    }
    const DWORD got = ::GetEnvironmentVariableA(name, buf.get(), need);
    if (got == 0u || static_cast<size_t>(got) > valueChars) {
        return 0u; // 两次调用之间值被并发改动 → 按未设置处理（下次重读）
    }
    buf.get()[got] = '\0';
    *out = std::move(buf);
    return static_cast<size_t>(got);
}

} // namespace zfg
