"""Restore this evidence archive and its losslessly deduplicated PNGs into a new directory."""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

if len(sys.argv) != 2:
    raise SystemExit("Usage: python unpack-evidence.py NEW_OUTPUT_DIRECTORY")
target = Path(sys.argv[1]).resolve()
if target.exists():
    raise SystemExit("Preserve earlier evidence: select a new output directory.")
target.mkdir(parents=True)

def destination(name):
    path = (target / name).resolve()
    if not path.is_relative_to(target) or path == target:
        raise ValueError(f"Unsafe archive path: {name}")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path

with zipfile.ZipFile(Path(__file__).with_name("raw-evidence.zip")) as archive:
    for entry in archive.infolist():
        if not entry.is_dir():
            destination(entry.filename).write_bytes(archive.read(entry))
    mapping = json.loads(archive.read("capture-index.json"))
    for entry in mapping:
        data = archive.read(entry["object"])
        if hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise ValueError(f"Capture digest mismatch: {entry['path']}")
        destination(entry["path"]).write_bytes(data)
print(f"Restored {len(mapping)} original PNG paths to {target}")
