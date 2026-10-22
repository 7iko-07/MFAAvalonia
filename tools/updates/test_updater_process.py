"""Windows process integration tests; run: python -B -m unittest tools.updates.test_updater_process -v.

Builds the real updater and a tiny simulated application. All installation files,
locks, processes and restart markers are isolated in disposable test directories.
Native libraries are byte fixtures; these tests do not load real MaaFramework.
"""
import hashlib
import ctypes
from ctypes import wintypes
import importlib.metadata
import json
import os
from pathlib import Path
import random
import shutil
import subprocess
import tempfile
import time
import unittest
import zipfile

from tools.updates import build_incremental_updates as updates

REPO = Path(__file__).resolve().parents[2]
HOST_PROJECT = REPO / "tests/Updates.ProcessHost/Updates.ProcessHost.csproj"
HOST_BUILD = HOST_PROJECT.parent / "bin/Release/net10.0"
UPDATER_BUILD = REPO / "MFAUpdater/bin/Release/net8.0"
NATIVE = "runtimes/win-x64/native/MaaFramework.dll"
SITE = "python/Lib/site-packages"
JOURNAL = ".mfa-update-journal.json"


class WindowsJob:
    """Own only test processes and their descendants; close kills any leftovers."""
    def __init__(self):
        class Limits(ctypes.Structure):
            _fields_ = [("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64),
                        ("flags", wintypes.DWORD), ("min_working_set", ctypes.c_size_t),
                        ("max_working_set", ctypes.c_size_t), ("process_limit", wintypes.DWORD),
                        ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD)]

        class ExtendedLimits(ctypes.Structure):
            _fields_ = [("basic", Limits), ("io", ctypes.c_uint64 * 6),
                        ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                        ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]

        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        self.api.CreateJobObjectW.restype = wintypes.HANDLE
        self.api.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        self.api.SetInformationJobObject.restype = wintypes.BOOL
        self.api.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        self.api.AssignProcessToJobObject.restype = wintypes.BOOL
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.api.CloseHandle.restype = wintypes.BOOL
        self.handle = self.api.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        limits = ExtendedLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self.api.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            error = ctypes.get_last_error()
            self.close()
            raise ctypes.WinError(error)

    def add(self, child):
        if not self.api.AssignProcessToJobObject(self.handle, int(child._handle)):
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self):
        if self.handle:
            self.api.CloseHandle(self.handle)
            self.handle = None


@unittest.skipUnless(os.name == "nt", "Exercises Windows executable and file-lock behavior")
class UpdaterProcessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        for project in (HOST_PROJECT, REPO / "MFAUpdater/MFAUpdater.csproj"):
            result = subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-v", "quiet"],
                                    cwd=REPO, capture_output=True, text=True, encoding="utf-8", timeout=90)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="mfa-updater-process-中文-")
        self.addCleanup(self.cleanup_directory)
        self.root = Path(self.temporary.name).resolve()
        self.install = self.root / "installed"
        self.worker = self.root / "worker"
        shutil.copytree(UPDATER_BUILD, self.worker, ignore=shutil.ignore_patterns("logs"))
        self.children = []
        self.streams = []
        self.addCleanup(self.cleanup_processes)
        self.job = WindowsJob()
        self.addCleanup(self.job.close)

    def cleanup_processes(self):
        for child in reversed(self.children):
            if child.poll() is None:
                child.kill()
            child.wait(timeout=10)
        for stream in self.streams:
            stream.close()

    def cleanup_directory(self):
        # Restarted children exit immediately after writing their marker. Allow
        # their final executable handle to close before removing our own fixture.
        for attempt in range(40):
            try:
                self.temporary.cleanup()
                return
            except PermissionError:
                if attempt == 39:
                    raise
                time.sleep(0.05)

    def wait_until(self, predicate, description, child=None):
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            if predicate():
                return
            if child is not None and child.poll() is not None:
                self.fail(f"Process exited early ({child.returncode}): {description}\n{self.output()}")
            time.sleep(0.01)
        self.fail(f"Timed out: {description}\n{self.output()}")

    def output(self):
        saved = self.worker / "logs/updater_log.txt"
        if saved.exists():
            return saved.read_text(encoding="utf-8", errors="replace")
        path = self.root / "updater-output.txt"
        if not path.exists():
            return ""
        content = path.read_bytes()
        try:
            return content.decode("utf-8")
        except UnicodeDecodeError:
            # .NET console redirection can retain the Windows code page.
            code_page = ctypes.windll.kernel32.GetACP()
            return content.decode(f"cp{code_page}", errors="replace")

    def fixture(self, *, full=False, bad_python=False, empty_metadata=False):
        host = {p.name: p.read_bytes() for p in HOST_BUILD.iterdir() if p.is_file()}
        host["MFAAvalonia.exe"] = host.pop("Updates.ProcessHost.exe")
        old_files = {**host,
                     "interface.json": b'{"version":"v1"}', NATIVE: b"native 5.13.1",
                     "python-dependencies.json": b'{"packages":{"maafw":"5.13.1"}}',
                     f"{SITE}/maafw-5.13.1.dist-info/METADATA": b"Name: maafw\nVersion: 5.13.1\n",
                     "resource/unchanged.bin": random.Random(7).randbytes(2 * 1024 * 1024),
                     "resource/change.json": b"old resource", "zz-lock.bin": b"old locked file"}
        new_files = {**old_files,
                     # A PE overlay changes the executable hash while retaining
                     # a runnable test apphost, so the running EXE is replaced too.
                     "MFAAvalonia.exe": old_files["MFAAvalonia.exe"] + b"\nupdated test application\n",
                     "interface.json": b'{"version":"v2"}', NATIVE: b"native 5.14.2",
                     "python-dependencies.json": b'{"packages":{"maafw":"5.14.2"}}',
                     f"{SITE}/maafw-5.14.2.dist-info/METADATA":
                         f"Name: maafw\nVersion: {'5.99.0' if bad_python else '5.14.2'}\n".encode(),
                     "resource/change.json": b"new resource", "agent/new.py": b"new agent",
                     "zz-lock.bin": b"new locked file"}
        del new_files[f"{SITE}/maafw-5.13.1.dist-info/METADATA"]
        for name, files in (("old", old_files), ("new", new_files)):
            with zipfile.ZipFile(self.root / f"{name}.zip", "w", zipfile.ZIP_DEFLATED) as archive:
                for path, content in files.items():
                    archive.writestr(path, content)
        baseline = updates.archive_manifest(self.root / "old.zip", "v1", "win-x64")
        target = updates.archive_manifest(self.root / "new.zip", "v2", "win-x64")
        delta = updates.delta_manifest(baseline, target)
        updates.write_delta(self.root / "new.zip", self.root / "delta.zip", delta)
        with zipfile.ZipFile(self.root / "delta.zip") as archive:
            self.assertNotIn("resource/unchanged.bin", archive.namelist())
        self.assertLess((self.root / "delta.zip").stat().st_size, (self.root / "new.zip").stat().st_size)
        payload = self.root / "payload"
        for name, destination in (("old", self.install), ("new" if full else "delta", payload)):
            with zipfile.ZipFile(self.root / f"{name}.zip") as archive:
                archive.extractall(destination)
        (self.install / updates.MANIFEST).write_text(json.dumps(baseline), encoding="utf-8")
        (self.install / "config").mkdir()
        (self.install / "config/user.json").write_text("user settings", encoding="utf-8")
        directories = []
        if empty_metadata:
            directories = [f"{SITE}/maafw-5.12.0.dist-info"]
            (self.install / directories[0]).mkdir()
        self.plan = {"sourceRoot": str(payload), "installRoot": str(self.install), "dataRoot": str(self.install),
                     "backupRoot": str(self.root / "rollback"), "restartPath": str(self.install / "MFAAvalonia.exe"),
                     "previousRestartPath": str(self.install / "MFAAvalonia.exe"), "parentPid": 2147483647,
                     "parentStartTimeUtcTicks": 0, "manifest": target if full else delta,
                     "deleted": delta["deleted"], "emptyDirectories": directories}
        self.baseline, self.target = baseline, target
        self.unchanged_time = (self.install / "resource/unchanged.bin").stat().st_mtime_ns

    def start_holder(self, locked, *, installed=True):
        directory = self.install if installed else self.root / "holder"
        if not installed:
            shutil.copytree(HOST_BUILD, directory)
        executable = directory / ("MFAAvalonia.exe" if installed else "Updates.ProcessHost.exe")
        ready, gate = self.root / "ready.json", self.root / "exit.gate"
        child = subprocess.Popen([str(executable), "--wait", str(ready), str(gate), str(self.install / locked)],
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        self.children.append(child)
        self.job.add(child)
        self.wait_until(ready.exists, "simulated application startup", child)
        return child, json.loads(ready.read_text()), gate

    def start_updater(self, *, recover=False):
        plan_path = self.root / "plan.json"
        plan_path.write_text(json.dumps(self.plan), encoding="utf-8")
        output = (self.root / "updater-output.txt").open("wb")
        self.streams.append(output)
        command = ["dotnet", str(self.worker / "MFAUpdater.dll"),
                   "--recover" if recover else "--apply-plan",
                   str(self.install / JOURNAL) if recover else str(plan_path)]
        child = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        self.children.append(child)
        self.job.add(child)
        return child

    def restarted(self, version):
        marker = self.install / "config/restarted.json"
        self.wait_until(marker.exists, "application restart")
        self.assertEqual(json.loads(marker.read_text())["version"], version)

    def verify_installed(self, manifest):
        for file in manifest["files"]:
            path = self.install / file["path"]
            self.assertTrue(path.is_file(), file["path"])
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), file["sha256"], file["path"])
        self.assertEqual(json.loads((self.install / updates.MANIFEST).read_text())["version"], manifest["version"])
        self.assertEqual((self.install / "config/user.json").read_text(), "user settings")
        self.assertFalse((self.install / JOURNAL).exists())
        # Use Python's actual metadata discovery, not only the updater's parser.
        distributions = list(importlib.metadata.distributions(path=[str(self.install / SITE)]))
        self.assertEqual([(d.metadata["Name"], d.version) for d in distributions],
                         [("maafw", "5.14.2" if manifest["version"] == "v2" else "5.13.1")])

    def test_delta_waits_for_exit_installs_and_restarts(self):
        self.fixture(empty_metadata=True)
        parent, ready, gate = self.start_holder(NATIVE)
        self.plan.update(parentPid=ready["pid"], parentStartTimeUtcTicks=ready["startTimeUtcTicks"])
        updater = self.start_updater()
        self.wait_until(lambda: f"PID={parent.pid}" in self.output(), "updater waiting for application exit", updater)
        self.assertIsNone(parent.poll())
        self.assertFalse((self.install / JOURNAL).exists())
        self.assertEqual((self.install / NATIVE).read_bytes(), b"native 5.13.1")
        self.assertEqual((self.install / "MFAAvalonia.exe").read_bytes(), (HOST_BUILD / "Updates.ProcessHost.exe").read_bytes())
        gate.touch()
        self.assertEqual(parent.wait(timeout=10), 0)
        self.assertEqual(updater.wait(timeout=15), 0, self.output())
        self.restarted("v2")
        self.verify_installed(self.target)
        self.assertFalse((self.install / f"{SITE}/maafw-5.12.0.dist-info").exists())
        self.assertFalse((self.install / f"{SITE}/maafw-5.13.1.dist-info").exists())
        self.assertEqual((self.install / "resource/unchanged.bin").stat().st_mtime_ns, self.unchanged_time)

    def test_corrupt_payload_is_rejected_and_old_app_restarts(self):
        self.fixture()
        (Path(self.plan["sourceRoot"]) / NATIVE).write_bytes(b"corrupt download")
        updater = self.start_updater()
        self.assertEqual(updater.wait(timeout=15), 1, self.output())
        self.assertIn("更新文件校验失败", self.output())
        self.restarted("v1")
        self.verify_installed(self.baseline)

    def test_wrong_baseline_is_rejected_without_overwrite(self):
        self.fixture()
        path = self.install / "resource/unchanged.bin"
        path.write_bytes(b"locally modified")
        updater = self.start_updater()
        self.assertEqual(updater.wait(timeout=15), 1, self.output())
        self.assertIn("本地文件不符合增量包基准", self.output())
        self.restarted("v1")
        self.assertEqual(path.read_bytes(), b"locally modified")
        self.assertEqual((self.install / NATIVE).read_bytes(), b"native 5.13.1")
        self.assertFalse((self.install / JOURNAL).exists())

    def test_dependency_mismatch_rolls_back_and_restarts_old_app(self):
        self.fixture(bad_python=True)
        updater = self.start_updater()
        self.assertEqual(updater.wait(timeout=15), 1, self.output())
        self.assertIn("Python 依赖版本不匹配", self.output())
        self.assertIn("maafw，要求 5.14.2，实际 5.99.0", self.output())
        self.restarted("v1")
        self.verify_installed(self.baseline)
        self.assertFalse((self.install / "agent/new.py").exists())

    def test_updater_advertises_dependency_check_without_breaking_old_clients(self):
        for argument, expected in (("--protocol", "mfa-update-v1"),
                                   ("--dependency-check-protocol", "mfa-python-check-v1")):
            result = subprocess.run([str(self.worker / "MFAUpdater.exe"), argument],
                                    cwd=self.worker, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.strip(), expected)

    def test_killed_updater_can_recover_and_restart_old_app(self):
        self.fixture()
        holder, _, gate = self.start_holder("zz-lock.bin", installed=False)
        updater = self.start_updater()

        def reached_locked_file():
            path = self.install / JOURNAL
            if not path.exists():
                return False
            journal = json.loads(path.read_text(encoding="utf-8"))
            return any(record["path"] == "zz-lock.bin" for record in journal["records"])

        self.wait_until(reached_locked_file, "partial installation reached locked file", updater)
        self.assertEqual((self.install / NATIVE).read_bytes(), b"native 5.14.2")
        updater.kill()
        updater.wait(timeout=10)
        self.assertTrue((self.install / JOURNAL).exists())
        gate.touch()
        self.assertEqual(holder.wait(timeout=10), 0)
        recovery = self.start_updater(recover=True)
        self.assertEqual(recovery.wait(timeout=15), 0, self.output())
        self.restarted("v1")
        self.verify_installed(self.baseline)
        self.assertFalse((self.install / "agent/new.py").exists())

    def test_locked_file_preserves_journal_until_recovery_is_possible(self):
        self.fixture()
        holder, _, gate = self.start_holder("zz-lock.bin", installed=False)
        updater = self.start_updater()
        self.assertEqual(updater.wait(timeout=15), 1, self.output())
        # Rollback cannot replace the externally locked file either. Keep the
        # recovery journal and never start a partially updated application.
        self.assertTrue((self.install / JOURNAL).exists())
        self.assertFalse((self.install / "config/restarted.json").exists())
        gate.touch()
        self.assertEqual(holder.wait(timeout=10), 0)
        recovery = self.start_updater(recover=True)
        self.assertEqual(recovery.wait(timeout=15), 0, self.output())
        self.restarted("v1")
        self.verify_installed(self.baseline)

    def test_full_package_repairs_modified_baseline(self):
        self.fixture(full=True)
        (self.install / "resource/unchanged.bin").write_bytes(b"damaged local resource")
        updater = self.start_updater()
        self.assertEqual(updater.wait(timeout=15), 0, self.output())
        self.restarted("v2")
        self.verify_installed(self.target)


if __name__ == "__main__":
    unittest.main(verbosity=2)
