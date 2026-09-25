import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.manifest = {"id": "sts2-bridge", "pck_name": "sts2-bridge",
                         "version": "0.1.0", "has_dll": True, "has_pck": False}
        (self.root / "LICENSE").write_text("MIT license fixture")
        (self.root / "codex.py").write_text("# Standalone manual uploader fixture\n")
        (self.root / "mod_manifest.json").write_text(json.dumps(self.manifest))
        (self.root / "sts2-bridge.csproj").write_text(
            "<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>")

    def test_versions_and_identity_must_match(self):
        self.assertEqual(release.release_version(self.root, "v0.1.0"), "0.1.0")
        with self.assertRaisesRegex(ValueError, "Tag"):
            release.release_version(self.root, "v0.2.0")
        self.manifest["version"] = "0.2.0"
        (self.root / "mod_manifest.json").write_text(json.dumps(self.manifest))
        with self.assertRaisesRegex(ValueError, "versions must match"):
            release.release_version(self.root)
        self.manifest.update(version="0.1.0", id="FirstMod")
        (self.root / "mod_manifest.json").write_text(json.dumps(self.manifest))
        with self.assertRaisesRegex(ValueError, "DLL-only"):
            release.release_version(self.root)

    def test_archive_excludes_dependencies_and_checksums_match_assets(self):
        build = self.root / "build"
        build.mkdir()
        for name in ("sts2-bridge.dll", "sts2.dll", "0Harmony.dll", "GodotSharp.dll",
                     "BridgeFixtures.dll", "FirstMod.dll", "sts2-bridge.pdb"):
            (build / name).write_bytes(name.encode())
        output = self.root / "release"
        release.package(self.root, build / "sts2-bridge.dll", output, "0.1.0")
        self.assertEqual({p.name for p in output.iterdir()}, {
            "sts2-bridge.dll", "sts2-bridge.json", "sts2-bridge-v0.1.0.zip", "SHA256SUMS", "LICENSE", "codex.py"})
        self.assertEqual((output / "codex.py").read_text(), "# Standalone manual uploader fixture\n")
        with zipfile.ZipFile(output / "sts2-bridge-v0.1.0.zip") as bundle:
            self.assertEqual(set(bundle.namelist()), {
                "sts2-bridge/sts2-bridge.dll", "sts2-bridge/sts2-bridge.json", "sts2-bridge/LICENSE"})
            self.assertEqual(bundle.read("sts2-bridge/LICENSE"), b"MIT license fixture")
            self.assertEqual(bundle.read("sts2-bridge/sts2-bridge.dll"), b"sts2-bridge.dll")
            self.assertEqual(json.loads(bundle.read("sts2-bridge/sts2-bridge.json")), self.manifest)
        sums = {name: digest for digest, name in (
            line.split() for line in (output / "SHA256SUMS").read_text().splitlines())}
        self.assertEqual(set(sums), {"sts2-bridge.dll", "sts2-bridge.json", "sts2-bridge-v0.1.0.zip", "LICENSE", "codex.py"})
        for name, digest in sums.items():
            self.assertEqual(digest, hashlib.sha256((output / name).read_bytes()).hexdigest())

    def test_failed_build_removes_old_release(self):
        output = self.root / "dist/release"
        output.mkdir(parents=True)
        (output / "sts2-bridge.dll").write_bytes(b"stale")
        with patch("release.subprocess.run", side_effect=subprocess.CalledProcessError(1, "dotnet")):
            with self.assertRaises(subprocess.CalledProcessError):
                release.build_release(self.root)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
