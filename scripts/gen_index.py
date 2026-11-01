#!/usr/bin/env python3
"""生成发布索引 index.json。

主仓库只产出 zip / tar.gz 完整应用归档。本脚本扫描归档内的文件, 按来源模块
(worker / core / gui) 归类, 并记录模块依赖关系, 供软件源仓库 (app-source)
按索引拆分出独立的系统包 (deb / rpm / pacman)。

模块依赖链 (部署语义, 非程序集引用):
    worker (基础)  <-  core  <-  gui

用法:
    python3 scripts/gen_index.py --version 1.0.0-vibe --dir artifacts --out artifacts/index.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tarfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path

SCHEMA = "ai-shikikan.distribution-index/v1"

# 模块定义: 依赖方向按部署语义 (worker 为基础包, core 依赖 worker, gui 依赖 core)
MODULES = {
    "worker": {"kind": "executable", "dependsOn": []},
    "core": {"kind": "library", "dependsOn": ["worker"]},
    "gui": {"kind": "executable", "dependsOn": ["core"]},
}

VARIANTS = {"AOT", "dotnet", "selfcontained"}

NAME_RE = re.compile(
    r"^AIShikikan-(?P<version>.+?)-(?P<rid>(?:linux|win|osx)-(?:x64|arm64))-(?P<rest>.+?)(?P<ext>\.zip|\.tar\.gz)$"
)


def module_for(path: str) -> str:
    """按归档内路径判定文件来源模块。"""
    p = path.replace("\\", "/")
    if p.startswith("worker/"):
        return "worker"
    name = os.path.basename(p)
    if name == "AIShikikan.Core.dll" or name.startswith("AIShikikan.Core.resources"):
        return "core"
    return "gui"


def parse_artifact(name: str) -> dict | None:
    m = NAME_RE.match(name)
    if not m:
        return None
    rest = m.group("rest")
    # release: <variant>; debug: <Configuration>-<variant>
    parts = rest.split("-")
    if parts[-1] not in VARIANTS:
        return None
    variant = parts[-1]
    configuration = "-".join(parts[:-1]) or None
    return {
        "version": m.group("version"),
        "rid": m.group("rid"),
        "variant": variant,
        "configuration": configuration,
        "format": "zip" if m.group("ext") == ".zip" else "tar.gz",
    }


def list_members(path: Path) -> list[str]:
    """列出归档内的普通文件 (不含目录), 去掉顶层 "./" 前缀。"""
    if path.suffix == ".zip":
        with zipfile.ZipFile(path) as zf:
            names = [i.filename for i in zf.infolist() if not i.is_dir()]
    else:
        with tarfile.open(path, "r:gz") as tf:
            names = [m.name for m in tf.getmembers() if m.isfile()]
    out = []
    for n in names:
        n = n.replace("\\", "/")
        while n.startswith("./"):
            n = n[2:]
        if n and n not in ("", ".", "/"):
            out.append(n)
    return sorted(out)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True)
    ap.add_argument("--dir", required=True, help="归档所在目录")
    ap.add_argument("--out", required=True, help="index.json 输出路径")
    args = ap.parse_args()

    src = Path(args.dir)
    if not src.is_dir():
        print(f"目录不存在: {src}", file=sys.stderr)
        return 1

    artifacts = []
    for path in sorted(src.iterdir()):
        if not path.is_file() or path.suffix not in (".zip", ".gz"):
            continue
        meta = parse_artifact(path.name)
        if not meta:
            continue
        members = list_members(path)
        files = {"gui": [], "core": [], "worker": []}
        for m in members:
            files[module_for(m)].append(m)

        bundled = [mod for mod, fs in files.items() if not fs]
        artifacts.append(
            {
                "file": path.name,
                "module": "gui",
                "archiveVersion": meta["version"],
                "rid": meta["rid"],
                "variant": meta["variant"],
                "configuration": meta["configuration"],
                "format": meta["format"],
                "size": path.stat().st_size,
                "sha256": sha256(path),
                # 归档内每个文件来自哪个模块
                "moduleFiles": files,
                # 该变体下未以独立文件形式出现的模块 (AOT / selfcontained 会把 Core 静态链接进可执行文件)
                "bundledModules": bundled,
            }
        )

    if not artifacts:
        print(f"在 {src} 中未找到符合 AIShikikan-<version>-<rid>-<variant> 命名规则的归档", file=sys.stderr)
        return 1

    versions = sorted({a["archiveVersion"] for a in artifacts})
    index = {
        "schema": SCHEMA,
        "version": args.version,
        "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "detectedVersions": versions,
        "modules": MODULES,
        "dependencies": {name: spec["dependsOn"] for name, spec in MODULES.items()},
        "artifacts": artifacts,
        "moduleArtifacts": {
            mod: [a["file"] for a in artifacts if a["moduleFiles"][mod]]
            for mod in MODULES
        },
    }

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    total = sum(len(a["moduleFiles"]["gui"]) + len(a["moduleFiles"]["core"]) + len(a["moduleFiles"]["worker"]) for a in artifacts)
    print(f"索引已生成: {out} ({len(artifacts)} 个归档, {total} 个文件条目)")
    for a in artifacts:
        print(
            f"  {a['file']}: gui={len(a['moduleFiles']['gui'])} "
            f"core={len(a['moduleFiles']['core'])} worker={len(a['moduleFiles']['worker'])}"
            + (f" (bundled: {','.join(a['bundledModules'])})" if a["bundledModules"] else "")
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
