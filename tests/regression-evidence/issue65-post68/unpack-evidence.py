"""Restore original evidence bytes into a new directory; no existing data is replaced."""
import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path, PurePosixPath

args = argparse.ArgumentParser(description=__doc__)
args.add_argument('archive', type=Path)
args.add_argument('destination', type=Path)
args.add_argument('--run', help='Restore only this run, e.g. post68-final-cold-01')
options = args.parse_args()
destination = options.destination.resolve()
if destination.exists():
    raise SystemExit('Destination exists; use a fresh directory to retain earlier evidence.')
if options.run and not re.fullmatch(r'[a-zA-Z0-9_-]+', options.run):
    raise SystemExit('Invalid run name.')

def target(name):
    relative = PurePosixPath(name)
    if relative.is_absolute() or '..' in relative.parts or '\\' in name or ':' in name:
        raise ValueError('Unexpected archive path')
    return destination.joinpath(*relative.parts)

with zipfile.ZipFile(options.archive) as archive:
    if archive.testzip() is not None:
        raise SystemExit('Archive integrity failed.')
    pixels = json.loads(archive.read('pixel-map.json'))
    names = [n for n in archive.namelist() if not n.startswith('pixels/') and n != 'pixel-map.json']
    if options.run:
        names = [n for n in names if n.startswith(options.run + '/')]
        pixels = {n:h for n,h in pixels.items() if n.startswith(options.run + '/')}
    if not names:
        raise SystemExit('No matching evidence.')
    for name in names + list(pixels): target(name)
    destination.mkdir(parents=True)
    for name in names:
        path=target(name); path.parent.mkdir(parents=True,exist_ok=True)
        path.write_bytes(archive.read(name))
    for name,digest in pixels.items():
        if not re.fullmatch(r'[A-F0-9]{64}',digest): raise ValueError('Invalid pixel digest')
        data=archive.read('pixels/'+digest+'.png')
        if hashlib.sha256(data).hexdigest().upper()!=digest: raise ValueError('Pixel hash mismatch')
        path=target(name); path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(data)
print(f'Restored {len(names)} records and {len(pixels)} original captures to {destination}')
