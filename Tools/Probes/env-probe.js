#!/usr/bin/env node
'use strict';
// 环境探针：只读本机与引擎仓库的真值。拿不到的项一律 MISSING，不填默认值。
// 规约见 Docs/Planning/约法三章-2026-11-13.md：真跑、真输入、缺就报缺、可复核。
// 用法：node Tools/Probes/env-probe.js
// 产出：Tools/Probes/out/env-<时间戳>.json + 控制台表格
// 说明：ENGINE 取探针所在仓库根（PROJECT_DIR），两台开发机均可直接运行。

const fs = require('fs');
const path = require('path');

const PROBE_DIR = __dirname;
const PROJECT_DIR = path.resolve(PROBE_DIR, '..', '..'); // Tools/Probes → 仓库根
const OUT_DIR = path.join(PROBE_DIR, 'out');

const ENGINE = PROJECT_DIR;
const UNITY_EXE = 'C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe';
const UNITY_LIC_DIR = 'C:/ProgramData/Unity';

function probe(name, what, fn) {
  try {
    const value = fn();
    if (value === null || value === undefined) {
      return { name, what, status: 'MISSING', reason: '读到了空值' };
    }
    return { name, what, status: 'OK', value };
  } catch (e) {
    return { name, what, status: 'MISSING', reason: (e && e.message) || String(e) };
  }
}

function readText(p) { return fs.readFileSync(p, 'utf8'); }
function exists(p) { return fs.existsSync(p); }

function listDir(p) {
  if (!exists(p)) throw new Error('路径不存在: ' + p);
  return fs.readdirSync(p).sort();
}

function countFilesRecursive(dir, ext) {
  let n = 0;
  const stack = [dir];
  while (stack.length) {
    const cur = stack.pop();
    let ents;
    try { ents = fs.readdirSync(cur, { withFileTypes: true }); }
    catch (e) { continue; }
    for (const d of ents) {
      const full = path.join(cur, d.name);
      if (d.isDirectory()) stack.push(full);
      else if (d.isFile() && full.toLowerCase().endsWith(ext)) n++;
    }
  }
  return n;
}

function readGitHead(repo) {
  const headRaw = readText(path.join(repo, '.git', 'HEAD')).trim();
  if (!headRaw.startsWith('ref: ')) {
    return { ref: '(detached)', sha: headRaw, short: headRaw.slice(0, 7) };
  }
  const ref = headRaw.slice(5).trim();
  const loose = path.join(repo, '.git', ref);
  if (exists(loose)) {
    const sha = readText(loose).trim();
    return { ref, sha, short: sha.slice(0, 7) };
  }
  const packed = path.join(repo, '.git', 'packed-refs');
  if (exists(packed)) {
    for (const line of readText(packed).split('\n')) {
      const t = line.trim();
      if (!t || t.startsWith('#')) continue;
      const parts = t.split(' ');
      if (parts.length >= 2 && parts[1] === ref) {
        return { ref, sha: parts[0], short: parts[0].slice(0, 7) };
      }
    }
  }
  throw new Error('找不到 ref 指向的提交: ' + ref);
}

function diskFree(drive) {
  const st = fs.statfsSync(drive);
  const free = Number(st.bavail) * Number(st.bsize);
  const total = Number(st.blocks) * Number(st.bsize);
  const gb = (x) => Math.round((x / 1024 / 1024 / 1024) * 10) / 10;
  return { drive, freeGB: gb(free), totalGB: gb(total) };
}

const results = [];

// —— 引擎仓库 ——
results.push(probe('engine.head', '引擎仓库当前提交', () => readGitHead(ENGINE)));
results.push(probe('engine.git_clean_hint', '工作区是否干净（仅看 .git 是否存在，判断改动需用 git 命令）', () => {
  if (!exists(path.join(ENGINE, '.git'))) throw new Error('不是 git 仓库');
  return '未采（需 git 命令，探针不调外部进程）';
}));

// —— 引擎内容规模 ——
results.push(probe('engine.cs_files', 'Unity 侧 C# 文件数', () =>
  countFilesRecursive(path.join(ENGINE, 'UnityProject', 'Assets'), '.cs')));
results.push(probe('engine.scenes', '场景文件数', () =>
  countFilesRecursive(path.join(ENGINE, 'UnityProject', 'Assets', 'Scenes'), '.unity')));

// —— Unity 引擎 ——
results.push(probe('unity.exe', 'Unity 编辑器可执行文件', () => {
  const st = fs.statSync(UNITY_EXE);
  return { exists: true, sizeMB: Math.round(st.size / 1024 / 1024) };
}));
results.push(probe('unity.project_version', '工程声明的引擎版本', () =>
  readText(path.join(ENGINE, 'UnityProject', 'ProjectSettings', 'ProjectVersion.txt')).trim()));
results.push(probe('unity.license', '许可证文件（缺则任何批处理会退出）', () => {
  if (!exists(UNITY_LIC_DIR)) return null;
  const files = listDir(UNITY_LIC_DIR);
  if (files.length === 0) return null;
  return files;
}));

// —— FFX / FSR 运行时 ——
results.push(probe('fsr.runtime_dlls', '播放器目录里的 FFX 运行时 DLL', () => {
  const dir = path.join(ENGINE, 'Build', 'Windows64');
  if (!exists(dir)) throw new Error('播放器目录不存在: ' + dir);
  const dlls = listDir(dir).filter((f) => /fidelityfx|ffx/i.test(f));
  if (dlls.length === 0) return null;
  return dlls;
}));

// —— 磁盘（仓库所在盘 + C 盘；盘符不存在时如实 MISSING） ——
results.push(probe('disk.repo', '仓库所在盘空间', () => diskFree(path.parse(ENGINE).root)));
results.push(probe('disk.c', 'C 盘空间', () => diskFree('C:/')));

// —— 明确不采的项（不许编） ——
results.push({
  name: 'gpu.adapter', what: '显卡型号与显存', status: 'MISSING',
  reason: '本探针用纯 Node 读文件，取不到设备信息；需要 DXGI/WMI 采集器，尚未编写',
});
results.push({
  name: 'fsr4.real_caps', what: 'FSR 4.1 真实能力（这台机器到底支不支持）', status: 'MISSING',
  reason: '需要 DXGI 枚举真实适配器后问运行时，尚未编写；引擎内现有探测传的是空设备，结果不可信（见 TASK-B-FSR-01）',
});

// —— 输出 ——
function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  const payload = {
    probedAt: new Date().toISOString(),
    host: process.platform + ' ' + process.arch + ' node ' + process.version,
    engine: ENGINE,
    results,
    summary: {
      ok: results.filter((r) => r.status === 'OK').length,
      missing: results.filter((r) => r.status === 'MISSING').length,
    },
  };
  const outFile = path.join(OUT_DIR, 'env-' + stamp + '.json');
  fs.writeFileSync(outFile, JSON.stringify(payload, null, 2), 'utf8');

  for (const r of results) {
    const tag = r.status === 'OK' ? 'OK     ' : 'MISSING';
    let detail;
    if (r.status === 'OK') detail = JSON.stringify(r.value);
    else detail = r.reason;
    console.log(tag + ' | ' + r.name.padEnd(22) + ' | ' + r.what);
    console.log('        ' + detail);
  }
  console.log('');
  console.log('取到 ' + payload.summary.ok + ' 项，缺 ' + payload.summary.missing + ' 项 → ' + outFile);
  if (payload.summary.missing > 0) {
    console.log('缺的项没有填默认值，按 MISSING 记录。');
  }
}

main();
