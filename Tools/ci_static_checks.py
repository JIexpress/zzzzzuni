#!/usr/bin/env python3
"""CI 静态门禁：不需要 Unity 许可证也能拦住的四类问题。

用法：python Tools/ci_static_checks.py
退出码：0 通过；1 有问题（逐条打印）
"""
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FAILS = []


def fail(msg):
    FAILS.append(msg)
    print("FAIL: " + msg)


def warn(msg):
    print("warn: " + msg)


def git_files(*patterns):
    """取 git 跟踪的文件（避免扫描 build/Library 目录）。"""
    out = subprocess.run(
        ["git", "ls-files", "-z", "--"] + list(patterns),
        cwd=REPO, capture_output=True, check=True,
    ).stdout
    return [p for p in out.decode("utf-8", "replace").split("\0") if p]


def check_bom():
    """C# 与原生源码禁止 UTF-8 BOM（Windows 工具链会因此出编码问题）。"""
    targets = git_files("UnityProject/Assets/*.cs", "Native/*.cpp", "Native/*.h")
    bad = []
    for rel in targets:
        path = os.path.join(REPO, rel)
        try:
            with open(path, "rb") as f:
                if f.read(3) == b"\xef\xbb\xbf":
                    bad.append(rel)
        except OSError:
            continue
    if bad:
        fail(f"{len(bad)} 个文件带 UTF-8 BOM：{bad[:5]}")
    else:
        print(f"ok: BOM 检查（{len(targets)} 文件）")


def check_meta():
    """Assets 下每个资源/脚本都要有 .meta，否则 Unity 每次导入都会重新生成 GUID。"""
    targets = git_files("UnityProject/Assets/*")
    missing = []
    for rel in targets:
        base = os.path.basename(rel)
        if rel.endswith(".meta") or base.startswith("."):
            continue
        # 只查会被 Unity 序列化的类型，别的不管
        if not rel.endswith((".cs", ".asset", ".prefab", ".unity", ".mat", ".png", ".shader", ".asmdef", ".controller", ".physicMaterial", ".anim")):
            continue
        if not os.path.exists(os.path.join(REPO, rel + ".meta")):
            missing.append(rel)
    if missing:
        fail(f"{len(missing)} 个资产缺 .meta：{missing[:5]}")
    else:
        print("ok: .meta 完整性")


def check_large_files():
    """仓库不放 >10MB 的二进制（防再犯 456MB 误提交）。
    例外：第三方 SDK 的预编译运行时（FSR/FG 的 dll）必须入库。"""
    limit = 10 * 1024 * 1024
    allow_prefix = ("Native/ThirdParty/",)
    big = []
    for rel in git_files():
        if rel.startswith(allow_prefix):
            continue
        path = os.path.join(REPO, rel)
        try:
            size = os.path.getsize(path)
        except OSError:
            continue
        if size > limit:
            big.append((rel, size))
    if big:
        for rel, size in big:
            fail(f"大文件 {rel}: {size / 1024 / 1024:.1f} MB")
    else:
        print("ok: 大文件检查（ThirdParty 白名单除外）")


def check_asmdef_baseline():
    """汇编定义的 name 不能重复（Unity 会静默用错程序集）。"""
    import json
    seen = {}
    dup = []
    for rel in git_files("UnityProject/Assets/*.asmdef"):
        try:
            with open(os.path.join(REPO, rel), "r", encoding="utf-8") as f:
                name = json.load(f).get("name", "")
        except (OSError, ValueError):
            fail(f"asmdef 解析失败：{rel}")
            continue
        if not name:
            fail(f"asmdef 缺 name：{rel}")
        elif name in seen:
            dup.append((name, seen[name], rel))
        else:
            seen[name] = rel
    if dup:
        fail(f"{len(dup)} 组重复 asmdef 名：{dup[:3]}")
    else:
        print(f"ok: asmdef 唯一性（{len(seen)} 个）")


def main():
    print(f"== CI 静态门禁 @ {REPO} ==")
    check_bom()
    check_meta()
    check_large_files()
    check_asmdef_baseline()
    print()
    if FAILS:
        print(f"== 失败 {len(FAILS)} 项 ==")
        return 1
    print("== 全部通过 ==")
    return 0


if __name__ == "__main__":
    sys.exit(main())
