"""Run on Linux: python3 -m unittest discover -s tests/deploy -v."""
import fcntl
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "deploy" / "publish-release.sh"


class PublishReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.stage = self.root / "incoming with spaces"
        self.live = self.root / "releases"
        self.stage.mkdir()
        self.live.mkdir()
        (self.stage / "Planner-win-Setup.exe").write_bytes(b"installer")
        self.asset = self.package("1.1.0")
        self.feed([self.asset])

    def package(self, version, content=b"package"):
        name = f"Planner-{version}-full.nupkg"
        (self.stage / name).write_bytes(content)
        return {"PackageId": "Planner", "Version": version, "Type": "Full",
                "FileName": name, "Size": len(content),
                "SHA256": hashlib.sha256(content).hexdigest()}

    def feed(self, assets):
        (self.stage / "releases.win.json").write_text(json.dumps({"Assets": assets}))

    def publish(self, success=True):
        result = subprocess.run(["bash", str(SCRIPT), str(self.stage), str(self.live)],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
        self.assertFalse(list(self.live.glob(".publish.*")), "Temporary files were not cleaned up")
        return result

    def test_publish_and_retry_preserve_stage_and_old_packages(self):
        (self.live / "Planner-1.0.0-full.nupkg").write_bytes(b"old")
        (self.stage / "Planner-win-Portable.zip").write_bytes(b"portable")
        self.publish()
        self.publish()
        for name in [self.asset["FileName"], "Planner-win-Setup.exe", "Planner-win-Portable.zip", "releases.win.json"]:
            self.assertEqual((self.stage / name).read_bytes(), (self.live / name).read_bytes())
            self.assertEqual((self.live / name).stat().st_mode & 0o777, 0o644)
        self.assertEqual((self.live / "Planner-1.0.0-full.nupkg").read_bytes(), b"old")

    def test_feed_can_reference_existing_live_package(self):
        old = self.package("1.0.0", b"old")
        (self.stage / old["FileName"]).rename(self.live / old["FileName"])
        self.feed([self.asset, old])
        self.publish()

    def test_missing_or_corrupt_package_does_not_change_public_files(self):
        (self.live / "releases.win.json").write_bytes(b"original index")
        (self.live / "Planner-win-Setup.exe").write_bytes(b"original installer")
        package = self.stage / self.asset["FileName"]
        package.unlink()
        self.publish(False)
        package.write_bytes(b"corrupt")
        self.publish(False)
        self.assertEqual((self.live / "releases.win.json").read_bytes(), b"original index")
        self.assertEqual((self.live / "Planner-win-Setup.exe").read_bytes(), b"original installer")

    def test_conflicting_existing_package_is_rejected_before_publication(self):
        target = self.live / self.asset["FileName"]
        target.write_bytes(b"different")
        self.publish(False)
        self.assertEqual(target.read_bytes(), b"different")
        self.assertFalse((self.live / "Planner-win-Setup.exe").exists())

    def test_invalid_feed_and_path_traversal_are_rejected(self):
        (self.stage / "releases.win.json").write_text("not json")
        self.publish(False)
        self.asset["FileName"] = "../outside.nupkg"
        self.feed([self.asset])
        self.publish(False)
        self.assertFalse((self.live / "releases.win.json").exists())

    def test_concurrent_publisher_is_rejected(self):
        with Path(str(self.live) + ".publish.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            result = self.publish(False)
            self.assertIn("already running", result.stderr)


if __name__ == "__main__":
    unittest.main()
