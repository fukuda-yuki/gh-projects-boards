"""Package only this task's synthetic evidence; retain originals and refuse overwrite."""
import hashlib
import json
import pathlib
import shutil
import zipfile

repo = pathlib.Path.cwd()
task = repo / "TestResults/issue65-reset"
dest = repo / "tests/regression-evidence/issue65-reset"
archive = dest / "raw-evidence.zip"
index = dest / "artifact-index.json"
if archive.exists() or index.exists():
    raise SystemExit("Existing package is retained; do not overwrite")
for source, name in [("existing-ledger.json", "existing-ledger.json"), ("replays-final.json", "replays.json"),
                     ("native-context.json", "native-context.json"), ("inactive-build-manifest.json", "diagnostic-build.json"),
                     ("core-il-comparison.json", "core-il-comparison.json")]:
    shutil.copy2(task / source, dest / name)
raw = []
for run in sorted((repo / "TestResults/issue61-65").glob("reset-m[12]-*")):
    for path in sorted(run.rglob("*")):
        if path.is_file() and "data" not in path.relative_to(run).parts:
            raw.append((path, "runs/" + str(path.relative_to(run.parent)).replace("\\", "/")))
for name in ["driver-build.log", "inactive-build.log", "inactive-build-02.log", "restored-product-build.log",
             "final-driver-build.log", "executed-driver.cs", "compare-core-il.ps1"]:
    raw.append((task / name, "execution/" + name))
classifier = repo / "TestResults/issue65-post69-native/portable-evidence/analysis-tools/classify.py"
raw.append((classifier, "analysis/classify.py"))
raw.append((repo / "scripts/Test-SustainedInput.ps1", "execution/Test-SustainedInput.ps1"))
entries = []
with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as package:
    for path, name in raw:
        assert path.suffix.lower() not in (".etl", ".etlx", ".pdb", ".dll", ".exe")
        data = path.read_bytes()
        package.writestr(name, data)
        entries.append(dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
files = []
for path in sorted(dest.iterdir()):
    if path.is_file() and path != index:
        data = path.read_bytes()
        files.append(dict(path=path.name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
index.write_text(json.dumps(dict(files=files, rawArchive=archive.name, rawEntries=entries,
                                 originalEvidence="../issue65-post69-native/raw-evidence.zip",
                                 privacy="Only task-owned synthetic-process evidence; no system trace, symbols, runtime binaries or real user data."), indent=2), encoding="utf-8")
with zipfile.ZipFile(archive) as package:
    assert package.testzip() is None
    for entry in entries:
        data = package.read(entry["path"])
        assert len(data) == entry["bytes"] and hashlib.sha256(data).hexdigest() == entry["sha256"]
print(json.dumps(dict(rawFiles=len(entries), archiveBytes=archive.stat().st_size, archiveSha256=hashlib.sha256(archive.read_bytes()).hexdigest(), verification="All archived bytes match original synthetic records")))
