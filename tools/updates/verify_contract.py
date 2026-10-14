"""Exercise Python-produced delta manifests with the real C# transaction engine."""
import json
from pathlib import Path
import platform
import subprocess
import tempfile
import zipfile

from tools.updates import build_incremental_updates as updates


def verify():
    repo = Path(__file__).resolve().parents[2]
    os_name = {"Windows": "win", "Linux": "linux", "Darwin": "osx"}[platform.system()]
    arch = "arm64" if platform.machine().lower() in {"arm64", "aarch64"} else "x64"
    with tempfile.TemporaryDirectory(prefix="mfa-update-contract-") as temporary:
        root = Path(temporary)
        old_files = {
            "MFAAvalonia.exe": "old executable",
            "interface.json": '{"version":"v1"}',
            "runtimes/native/MaaFramework.dll": "native 5.13.1",
            "python/Lib/site-packages/maafw-5.13.1.dist-info/METADATA": "Name: maafw\nVersion: 5.13.1\n",
            "python-dependencies.json": '{"packages":{"maafw":"5.13.1"}}',
            "resource/unchanged.json": "unchanged resource",
        }
        new_files = {**old_files,
                     "MFAAvalonia.exe": "new executable",
                     "interface.json": '{"version":"v2"}',
                     "runtimes/native/MaaFramework.dll": "native 5.14.2",
                     "python-dependencies.json": '{"packages":{"maafw":"5.14.2"}}',
                     "python/Lib/site-packages/maafw-5.14.2.dist-info/METADATA": "Name: maafw\nVersion: 5.14.2\n"}
        del new_files["python/Lib/site-packages/maafw-5.13.1.dist-info/METADATA"]
        for name, files in (("old", old_files), ("new", new_files)):
            with zipfile.ZipFile(root / (name + ".zip"), "w", zipfile.ZIP_DEFLATED) as archive:
                for path, content in files.items():
                    archive.writestr(path, content)
        old = updates.archive_manifest(root / "old.zip", "v1", f"{os_name}-{arch}")
        target = updates.archive_manifest(root / "new.zip", "v2", f"{os_name}-{arch}")
        delta = updates.delta_manifest(old, target)
        updates.write_delta(root / "new.zip", root / "delta.zip", delta)
        installed, payload = root / "installed", root / "payload"
        for archive, destination in (("old", installed), ("delta", payload)):
            with zipfile.ZipFile(root / (archive + ".zip")) as package:
                package.extractall(destination)
        empty_metadata = "python/Lib/site-packages/maafw-5.12.0.dist-info"
        (installed / empty_metadata).mkdir(parents=True)
        # Match ZIP modes on Unix, as the client's extractor does.
        if os_name != "win":
            for directory, manifest in ((installed, old), (payload, delta)):
                for file in manifest["files"]:
                    path = directory / file["path"]
                    if path.exists():
                        path.chmod(file["mode"])
        plan = {"sourceRoot": str(payload), "installRoot": str(installed), "dataRoot": str(installed),
                "backupRoot": str(root / "rollback"), "restartPath": str(installed / "MFAAvalonia.exe"),
                "previousRestartPath": str(installed / "MFAAvalonia.exe"), "manifest": delta,
                "deleted": delta["deleted"], "emptyDirectories": [empty_metadata]}
        plan_path = root / "plan.json"
        plan_path.write_text(json.dumps(plan), encoding="utf-8")
        subprocess.run(["dotnet", "run", "--project", "tests/Updates.SmokeTests/Updates.SmokeTests.csproj",
                        "--no-restore", "--", str(plan_path)], cwd=repo, check=True)
        assert (installed / "runtimes/native/MaaFramework.dll").read_text() == "native 5.14.2"
        assert not (installed / "python/Lib/site-packages/maafw-5.13.1.dist-info").exists()
        assert not (installed / empty_metadata).exists()
        assert (installed / "resource/unchanged.json").read_text() == "unchanged resource"
        print("Python release producer -> C# updater contract verified, including paired dependencies.")


if __name__ == "__main__":
    verify()
