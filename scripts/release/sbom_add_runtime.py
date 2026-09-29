#!/usr/bin/env python3
"""Adds the .NET runtime pack to a CycloneDX SBOM as a component.

dotnet-CycloneDX walks the NuGet dependency graph of PgNimbus.App.csproj, so
the SBOM it produces lists every restored NuGet package (direct and
transitive) and nothing else. A NativeAOT publish statically links in the
.NET runtime pack too -- the GC, TLS and crypto code -- and that pack is not
a <PackageReference>: the SDK resolves and downloads it at publish time based
on the target RID, so a NuGet-graph-only tool never sees it. This script
appends it, and the matching NativeAOT ahead-of-time compiler toolchain pack,
as two ordinary CycloneDX `components[]` entries.

Usage:
    sbom_add_runtime.py <sbom.cdx.json> [--rid linux-x64] [--version X.Y.Z]

With no --version, the runtime version is read from `dotnet --list-runtimes`,
filtered to the SDK's own major.minor (from global.json's pinned sdk.version,
or `dotnet --version` if global.json has none). --version overrides that for
testing, so the detection logic can be exercised without a working `dotnet`
on PATH.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Optional


def read_sdk_major_minor(repo_root: Path) -> str:
    """Read the pinned SDK's major.minor from global.json, else `dotnet --version`."""
    global_json = repo_root / "global.json"
    if global_json.exists():
        data = json.loads(global_json.read_text(encoding="utf-8"))
        version = data.get("sdk", {}).get("version")
        if version:
            return _major_minor(version)

    result = subprocess.run(
        ["dotnet", "--version"], capture_output=True, text=True, check=True
    )
    return _major_minor(result.stdout.strip())


def _major_minor(version: str) -> str:
    parts = version.split(".")
    if len(parts) < 2:
        raise ValueError(f"not a major.minor(.patch) version: {version!r}")
    return f"{parts[0]}.{parts[1]}"


def detect_runtime_version(major_minor: str) -> str:
    """Read the Microsoft.NETCore.App runtime version matching major_minor.

    `dotnet --list-runtimes` lists every side-by-side shared runtime a
    machine has installed (GitHub-hosted runners carry several major
    versions); this picks the one whose major.minor matches the SDK actually
    building the release, not just the first line.
    """
    result = subprocess.run(
        ["dotnet", "--list-runtimes"], capture_output=True, text=True, check=True
    )
    pattern = re.compile(rf"^Microsoft\.NETCore\.App ({re.escape(major_minor)}\.\S+)")
    for line in result.stdout.splitlines():
        match = pattern.match(line.strip())
        if match:
            return match.group(1)
    raise RuntimeError(
        "no Microsoft.NETCore.App "
        f"{major_minor}.x runtime found in `dotnet --list-runtimes`"
    )


def runtime_components(rid: str, version: str) -> list[dict]:
    """The two CycloneDX components a NativeAOT publish for `rid` links in."""
    return [
        {
            "type": "library",
            "name": f"Microsoft.NETCore.App.Runtime.{rid}",
            "version": version,
            "purl": f"pkg:nuget/Microsoft.NETCore.App.Runtime.{rid}@{version}",
            "description": (
                "The .NET runtime pack a NativeAOT publish statically links "
                "into the app (GC, TLS and crypto code). Resolved by the SDK "
                "at publish time for the target RID, not a <PackageReference>, "
                "so a NuGet-dependency-graph SBOM tool never lists it on its own."
            ),
        },
        {
            "type": "library",
            "name": f"runtime.{rid}.Microsoft.DotNet.ILCompiler",
            "version": version,
            "purl": f"pkg:nuget/runtime.{rid}.Microsoft.DotNet.ILCompiler@{version}",
            "description": (
                "The NativeAOT ahead-of-time compiler toolchain pack used to "
                "produce the shipped binary for this RID."
            ),
        },
    ]


def add_components(sbom: dict, components: list[dict]) -> dict:
    """Append `components` to `sbom["components"]`, skipping any already present by name."""
    existing = sbom.setdefault("components", [])
    existing_names = {c.get("name") for c in existing}
    for component in components:
        if component["name"] not in existing_names:
            existing.append(component)
    return sbom


def run(
    sbom_path: Path,
    rid: str,
    version: Optional[str],
    repo_root: Path,
) -> str:
    """Patch the SBOM at `sbom_path` in place; returns the runtime version used."""
    if not version:
        major_minor = read_sdk_major_minor(repo_root)
        version = detect_runtime_version(major_minor)

    sbom = json.loads(sbom_path.read_text(encoding="utf-8"))
    add_components(sbom, runtime_components(rid, version))
    sbom_path.write_text(json.dumps(sbom, indent=2) + "\n", encoding="utf-8")
    return version


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "sbom", type=Path, help="Path to the CycloneDX JSON SBOM to patch, in place"
    )
    parser.add_argument(
        "--rid", default="linux-x64", help="Runtime identifier the runtime pack is for"
    )
    parser.add_argument(
        "--version", help="Skip detection and use this runtime pack version"
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path("."),
        help="Repo root, for reading global.json's pinned SDK version",
    )
    args = parser.parse_args(argv)

    if not args.sbom.exists():
        print(f"sbom_add_runtime: no such file: {args.sbom}", file=sys.stderr)
        return 1

    try:
        version = run(args.sbom, args.rid, args.version, args.repo_root)
    except (RuntimeError, ValueError, subprocess.CalledProcessError) as exc:
        print(f"sbom_add_runtime: {exc}", file=sys.stderr)
        return 1

    print(
        f"sbom_add_runtime: added the {args.rid} runtime pack "
        f"(version {version}) to {args.sbom}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
