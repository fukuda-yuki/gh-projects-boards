"""Read retained #70 evidence; never rewrite its original FAIL reports or raw records."""
import json
import pathlib
import sys

base, destination = map(pathlib.Path, sys.argv[1:])
if destination.exists():
    raise SystemExit("Retain existing output; choose a new destination")
read = lambda p: json.loads(p.read_text(encoding="utf-8-sig"))
result = []
for run in sorted(base.glob("post69-viewport-final-*")):
    root = run / "observations"
    commands = read(root / "native-command-classification.json")["commands"]
    gaps = read(root / "native-scroll-intervals.json")
    records = []
    for gap in gaps:
        related = [c for c in commands if c["begin"] < gap["end"] and c["end"] > gap["start"]]
        late = any(c["classification"] == "corroborated-app-delay" for c in related)
        insufficient = any(c["classification"] in ("missing-before", "no-changed-capture", "capture-bracket-crosses-100ms", "visible-delay-attribution-incomplete") for c in related)
        records.append(dict(start=gap["start"], end=gap["end"], callbackGapMs=gap["ms"],
                            managedUnionMs=gap["managedUiSpanUnionMs"], commands=related,
                            disposition="overlaps demonstrated late response" if late else
                            "observation insufficient; callback gap alone is not late presentation" if insufficient else
                            "diagnostic gap without established late presentation"))
    result.append(dict(run=run.name, intervals=records,
                       visibleUncertain=[c for c in commands if c["classification"] == "visible-delay-attribution-incomplete"],
                       continuousMovement="Not established by isolated jump-response brackets; retained sustained-scroll gate stays unaccepted"))
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(dict(runs=len(result), callbackIntervals=sum(len(r["intervals"]) for r in result),
                      visibleUncertain=sum(len(r["visibleUncertain"]) for r in result))))
