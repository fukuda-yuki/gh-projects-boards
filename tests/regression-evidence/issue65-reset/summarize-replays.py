"""Summarize this bounded experiment from original traces, captures and test results."""
import json
import pathlib
import sys
import xml.etree.ElementTree as ET

base, destination = map(pathlib.Path, sys.argv[1:])
if destination.exists():
    raise SystemExit("Retain existing output; choose a new destination")
read = lambda p: json.loads(p.read_text(encoding="utf-8-sig"))
results = []
for run in sorted(base.glob("reset-m[12]-*")):
    root = run / "observations"
    plan = read(root / "plan.json")
    trace = [json.loads(line) for line in (root / "app-trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    driver = [json.loads(line) for line in (root / "driver.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    classification = read(root / "native-command-classification.json")
    caps = read(root / "scroll-captures.json")
    frequency = plan["frequency"]
    milliseconds = lambda ticks: ticks * 1000 / frequency
    terminal = [e for e in trace if e["kind"] == "trace-end"]
    sequences = sorted(e["sequence"] for e in trace if "sequence" in e)
    complete = (len(terminal) == 1 and all(terminal[0]["data"][k] == 0 for k in ("queued", "dropped", "failed"))
                and all(b == a + 1 for a, b in zip(sequences, sequences[1:])))
    commands = []
    for command in classification["commands"]:
        a, b = command["begin"], command["end"]
        spans = [e["data"] for e in trace if e["kind"] == "ui-span" and e["data"]["start"] < b and e["data"]["end"] > a]
        rows = [s for s in spans if s["kind"] == "realize-row"]
        checkpoints = [s for e in trace if e["kind"] == "core-checkpoint-trace" for s in e["data"]["samples"] if s["Start"] < b and s["End"] > a]
        commands.append(dict(**command, realizedRows=len(rows),
                             rowRealizationMs=sum(milliseconds(s["elapsedTicks"]) for s in rows),
                             overlappingCheckpointSpans=checkpoints,
                             gcOverlappingSpans=[s for s in spans if any(s[k] for k in ("gc0", "gc1", "gc2"))],
                             callbackSamples=[dict(atMs=milliseconds(e["ticks"]-a), **e["data"]) for e in trace if e["kind"] == "rendering-callback" and a <= e["ticks"] < b]))
    env = read(run / "environment.json")
    before = [e for e in driver if e["kind"] == "input-phase-end" and e["detail"]["phase"] == "number"][0]
    results.append(dict(run=run.name, source=plan["source"], condition=plan["condition"],
                        earlyScroll=plan["earlyScroll"], appSha256=plan["appSha256"], coreSha256=plan["coreSha256"],
                        driverSha256=plan["driverSha256"], seedSha256=env["seedSha256"], seedCoreSha256=env["seedCoreSha256"],
                        testCommand=env["command"], os=env["os"], sdk=env["sdk"],
                        counts=ET.parse(run / "diagnostic.trx").find(".//{*}Counters").attrib,
                        lifetime=read(root / "lifetime.json"), durableReadback=read(root / "durable-readback.json"),
                        traceComplete=complete, captureCount=len(caps),
                        captureMaxBeginGapMs=max(milliseconds(b["begin"]-a["begin"]) for a, b in zip(caps, caps[1:])),
                        captureMaxDurationMs=max(milliseconds(c["end"]-c["begin"]) for c in caps),
                        inputToScrollSeparationMs=milliseconds(commands[0]["begin"]-before["ticks"]),
                        diagnosticWaitEvents=[e for e in driver if e["kind"].startswith("diagnostic-save-wait")],
                        inputPhases=[e for e in driver if e["kind"] in ("input-phase-start", "input-phase-end")],
                        inactivePresentersCreated=sum(e["kind"] == "inactive-diagnostic-text" for e in trace),
                        commands=commands, caveat="Capture brackets are independent GDI observations, not exact presentation or scanout times. GC counts only establish overlap. Row spans and checkpoint spans are not additive CPU time. These five jumps do not establish continuous scrolling."))
assert len(results) == 6
assert len({r["driverSha256"] for r in results}) == 1
assert all(r["traceComplete"] and r["durableReadback"]["passed"] and r["lifetime"]["normal"] and not r["lifetime"]["forced"] for r in results)
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(json.dumps(results, indent=2), encoding="utf-8")
for r in results:
    print(r["run"], "captures", r["captureCount"], "max gap ms", round(r["captureMaxBeginGapMs"], 3), "input/scroll separation ms", round(r["inputToScrollSeparationMs"], 3))
