#!/usr/bin/env python3
"""Build a release locally or in CI using reference-only NuGet assemblies; never publish."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile


def release_version(root, tag=None):
    manifest = json.loads((root / "mod_manifest.json").read_text())
    version = manifest["version"]
    project = ET.parse(root / "sts2-bridge.csproj")
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError(f"Invalid release version: {version}")
    if project.findtext("PropertyGroup/Version") != version:
        raise ValueError("Project and manifest versions must match.")
    if tag is not None and tag != f"v{version}":
        raise ValueError(f"Tag {tag!r} does not match manifest version v{version}.")
    if (manifest["id"] != "sts2-bridge" or manifest["pck_name"] != "sts2-bridge"
            or manifest["has_dll"] is not True or manifest["has_pck"] is not False):
        raise ValueError("Expected the DLL-only sts2-bridge manifest.")
    return version


def package(root, built_dll, output, version):
    output.mkdir(parents=True, exist_ok=True)
    dll = output / "sts2-bridge.dll"
    manifest = output / "sts2-bridge.json"
    license_file = output / "LICENSE"
    shutil.copyfile(built_dll, dll)
    shutil.copyfile(root / "mod_manifest.json", manifest)
    shutil.copyfile(root / "LICENSE", license_file)
    archive = output / f"sts2-bridge-v{version}.zip"
    # Allowlist individual files, never the build directory. It may contain
    # game references, test fixtures, PDBs, or unrelated/stale assemblies.
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for path in (dll, manifest, license_file):
            bundle.write(path, f"sts2-bridge/{path.name}")
    (output / "SHA256SUMS").write_text("".join(
        f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
        for path in (dll, manifest, license_file, archive)), encoding="utf-8")


def build_release(root, tag=None):
    version = release_version(root, tag)
    output = root / "dist/release"
    # Fail closed: a failed rebuild must not leave an earlier release to upload.
    if output.exists():
        shutil.rmtree(output)
    subprocess.run([
        "dotnet", "build", str(root / "sts2-bridge.csproj"),
        "--configuration", "Release", "--no-incremental",
        "-p:UseReferenceAssemblies=true",
    ], cwd=root, check=True)
    package(root, root / ".godot/mono/temp/bin/Release/sts2-bridge.dll", output, version)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", help="Require an exact v<manifest-version> tag match")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    try:
        output = build_release(root, args.tag)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release build failed: {error}\n")
    print(f"Release artifacts: {output}\nNothing installed, tagged, or published.")


if __name__ == "__main__":
    main()
