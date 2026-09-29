#!/usr/bin/env python3
"""Unit tests for sbom_add_runtime.py.

Run directly (`python scripts/release/test_sbom_add_runtime.py`) or with
`python -m unittest discover scripts/release`. Uses only the standard
library, matching every other script in scripts/design.
"""
from __future__ import annotations

import json
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import sbom_add_runtime as target  # noqa: E402


SAMPLE_SBOM = {
    "bomFormat": "CycloneDX",
    "specVersion": "1.4",
    "components": [
        {
            "type": "library",
            "name": "Npgsql",
            "version": "10.0.3",
            "purl": "pkg:nuget/Npgsql@10.0.3",
        }
    ],
}


class RuntimeComponentsTests(unittest.TestCase):
    def test_two_components_with_matching_version_and_rid(self) -> None:
        components = target.runtime_components("linux-x64", "10.0.1")
        self.assertEqual(len(components), 2)
        names = {c["name"] for c in components}
        self.assertEqual(
            names,
            {
                "Microsoft.NETCore.App.Runtime.linux-x64",
                "runtime.linux-x64.Microsoft.DotNet.ILCompiler",
            },
        )
        for component in components:
            self.assertEqual(component["version"], "10.0.1")
            self.assertTrue(component["purl"].endswith("@10.0.1"))
            self.assertIn("linux-x64", component["purl"])


class AddComponentsTests(unittest.TestCase):
    def test_appends_to_existing_components(self) -> None:
        sbom = json.loads(json.dumps(SAMPLE_SBOM))  # deep copy
        target.add_components(sbom, target.runtime_components("linux-x64", "10.0.1"))
        self.assertEqual(len(sbom["components"]), 3)
        names = {c["name"] for c in sbom["components"]}
        self.assertIn("Npgsql", names)
        self.assertIn("Microsoft.NETCore.App.Runtime.linux-x64", names)

    def test_creates_components_array_when_missing(self) -> None:
        sbom = {"bomFormat": "CycloneDX"}
        target.add_components(sbom, target.runtime_components("osx-arm64", "10.0.1"))
        self.assertEqual(len(sbom["components"]), 2)

    def test_is_idempotent_on_rerun(self) -> None:
        sbom = json.loads(json.dumps(SAMPLE_SBOM))
        target.add_components(sbom, target.runtime_components("linux-x64", "10.0.1"))
        target.add_components(sbom, target.runtime_components("linux-x64", "10.0.1"))
        names = [c["name"] for c in sbom["components"]]
        self.assertEqual(len(names), len(set(names)), "components must not duplicate on rerun")


class MajorMinorTests(unittest.TestCase):
    def test_extracts_major_minor_from_patch_version(self) -> None:
        self.assertEqual(target._major_minor("10.0.401"), "10.0")

    def test_rejects_a_single_component_version(self) -> None:
        with self.assertRaises(ValueError):
            target._major_minor("10")


class ReadSdkMajorMinorTests(unittest.TestCase):
    def test_reads_pinned_version_from_global_json(self, ) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            repo_root = Path(tmp)
            (repo_root / "global.json").write_text(
                json.dumps({"sdk": {"version": "10.0.401", "rollForward": "latestFeature"}}),
                encoding="utf-8",
            )
            self.assertEqual(target.read_sdk_major_minor(repo_root), "10.0")

    def test_falls_back_to_dotnet_version_without_global_json(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            repo_root = Path(tmp)
            fake_result = subprocess.CompletedProcess(
                args=["dotnet", "--version"], returncode=0, stdout="10.0.401\n"
            )
            with mock.patch("subprocess.run", return_value=fake_result) as run:
                self.assertEqual(target.read_sdk_major_minor(repo_root), "10.0")
                run.assert_called_once()


class DetectRuntimeVersionTests(unittest.TestCase):
    LIST_RUNTIMES_OUTPUT = (
        "Microsoft.AspNetCore.App 10.0.1 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n"
        "Microsoft.NETCore.App 8.0.11 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n"
        "Microsoft.NETCore.App 10.0.1 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n"
    )

    def test_picks_the_line_matching_major_minor(self) -> None:
        fake_result = subprocess.CompletedProcess(
            args=["dotnet", "--list-runtimes"],
            returncode=0,
            stdout=self.LIST_RUNTIMES_OUTPUT,
        )
        with mock.patch("subprocess.run", return_value=fake_result):
            self.assertEqual(target.detect_runtime_version("10.0"), "10.0.1")

    def test_raises_when_nothing_matches(self) -> None:
        fake_result = subprocess.CompletedProcess(
            args=["dotnet", "--list-runtimes"],
            returncode=0,
            stdout=self.LIST_RUNTIMES_OUTPUT,
        )
        with mock.patch("subprocess.run", return_value=fake_result):
            with self.assertRaises(RuntimeError):
                target.detect_runtime_version("9.0")


class RunEndToEndTests(unittest.TestCase):
    def test_patches_a_sample_sbom_file_on_disk(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            repo_root = Path(tmp)
            sbom_path = repo_root / "sample.cdx.json"
            sbom_path.write_text(json.dumps(SAMPLE_SBOM), encoding="utf-8")

            version = target.run(sbom_path, "linux-x64", "10.0.1", repo_root)

            self.assertEqual(version, "10.0.1")
            patched = json.loads(sbom_path.read_text(encoding="utf-8"))
            names = {c["name"] for c in patched["components"]}
            self.assertIn("Microsoft.NETCore.App.Runtime.linux-x64", names)
            self.assertIn("runtime.linux-x64.Microsoft.DotNet.ILCompiler", names)
            self.assertIn("Npgsql", names)

    def test_cli_main_reports_missing_file(self) -> None:
        exit_code = target.main(["/no/such/file.cdx.json"])
        self.assertEqual(exit_code, 1)

    def test_cli_main_end_to_end_with_explicit_version(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            sbom_path = Path(tmp) / "sample.cdx.json"
            sbom_path.write_text(json.dumps(SAMPLE_SBOM), encoding="utf-8")

            exit_code = target.main(
                [str(sbom_path), "--rid", "win-x64", "--version", "10.0.1"]
            )

            self.assertEqual(exit_code, 0)
            patched = json.loads(sbom_path.read_text(encoding="utf-8"))
            names = {c["name"] for c in patched["components"]}
            self.assertIn("Microsoft.NETCore.App.Runtime.win-x64", names)


if __name__ == "__main__":
    unittest.main()
