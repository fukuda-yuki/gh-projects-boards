import json, hashlib, sys
from collections import Counter
from pathlib import Path
from PIL import Image

root = Path(sys.argv[1])
read = lambda name: json.loads((root / name).read_text(encoding='utf-8-sig'))
plan = read('plan.json'); frequency = plan['frequency']
commands = read('native-command-classification.json')['commands']
frames = read('desktop/frames.json')
lifetime = read('desktop/lifetime.json')
assert lifetime['complete'] and not lifetime['saturated'] and frames
for frame in frames:
    assert frame['present'] <= frame['acquired'] <= frame['copied']
    assert not frame['masked']
    with Image.open(root / 'desktop' / frame['path']) as im:
        frame['vertical'] = hashlib.sha256(im.crop((0, 0, 50, im.height - 22)).tobytes()).hexdigest()
        frame['horizontal'] = hashlib.sha256(im.crop((550, 0, im.width - 30, im.height - 22)).tobytes()).hexdigest()
assert all(a['present'] < b['present'] for a, b in zip(frames, frames[1:]))
records = []
for command in commands:
    begin, end, axis = command['begin'], command['end'], command['axis']
    prior = [f for f in frames if f['present'] <= begin]
    after = [f for f in frames if begin < f['present'] < end]
    assert prior and after
    before = prior[-1]
    changed = next((f for f in after if f[axis] != before[axis]), None)
    through_change = [f for f in after if changed is None or f['present'] <= changed['present']]
    unchanged = [f for f in after if changed is None or f['present'] < changed['present']]
    old_at = (unchanged[-1]['present'] - begin) * 1000 / frequency if unchanged else 0
    update_at = (changed['present'] - begin) * 1000 / frequency if changed else None
    copied_by = (changed['copied'] - begin) * 1000 / frequency if changed else None
    complete_updates = all(f['accumulated'] == 1 for f in through_change)
    if old_at >= 100:
        label = 'old-desktop-pixels-after-100ms'
    elif update_at is not None and update_at <= 100:
        label = 'desktop-update-by-100ms'
    elif update_at is not None and complete_updates:
        label = 'first-desktop-update-after-100ms'
    else:
        label = 'insufficient-desktop-observation'
    records.append(dict(index=command['index'], axis=axis, requestedOffset=command['requestedOffset'], viewportMs=command['viewportMs'],
        oldPixelsAtMs=old_at, changedDesktopAtMs=update_at, pixelsCopiedByMs=copied_by, completeUpdatesThroughChange=complete_updates,
        before=before['path'], lastUnchanged=unchanged[-1]['path'] if unchanged else None, firstChanged=changed['path'] if changed else None, classification=label))
result = dict(source=plan['source'], run=root.parent.name, condition=plan['condition'], counts=Counter(r['classification'] for r in records), commands=records,
    boundary='Independent DXGI desktop-update QPC timestamps and CPU pixel availability; full-viewport images require expected-destination/value review; not physical scanout.')
(root / 'desktop-classification.json').write_text(json.dumps(result, indent=2))
print(json.dumps(result, indent=2))
