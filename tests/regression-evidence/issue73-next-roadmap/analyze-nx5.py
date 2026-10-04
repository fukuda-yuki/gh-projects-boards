"""Summarize retained NX5 runs without rewriting original observations."""
import hashlib
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path

root = next(path for path in Path(__file__).resolve().parents if (path / "GhProjectsBoards.sln").is_file())
run_names = sys.argv[1:] or ["nx5-baseline-2000-d", "nx5-candidate-2000-b", "nx5-baseline-2000-e", "nx5-candidate-2000-c"]

def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))

def lines(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]

def stats(values):
    ordered = sorted(values)
    if not ordered:
        return {"count": 0}
    at = (len(ordered) - 1) * .95
    lower = int(at)
    upper = min(lower + 1, len(ordered) - 1)
    return {"count": len(ordered), "median": statistics.median(ordered),
            "p95": ordered[lower] + (ordered[upper] - ordered[lower]) * (at - lower),
            "max": max(ordered), "sum": sum(ordered)}

def summarize(run):
    folder = root / "TestResults" / "sheet-diagnostic" / run
    observations = folder / "observations"
    driver = lines(observations / "driver.jsonl")
    trace = lines(folder / "app-trace.jsonl")
    frequency = trace[0]["data"]["frequency"]
    plan = read(observations / "plan.json")
    first = next(event["ticks"] for event in driver if event["kind"] == "driver-action-start"
                 and event["details"]["action"] == "select-visible-title")
    last = next(event["ticks"] for event in driver if event["kind"] == "driver-action-start"
                and event["details"]["action"] == "commit-pending-title")

    def interval(begin, end):
        groups = defaultdict(list)
        allocations = defaultdict(list)
        for event in trace:
            data = event.get("data", {})
            if event["kind"] == "ui-span" and begin <= data["start"] and data["end"] <= end:
                groups[data["kind"]].append(data["elapsedTicks"] * 1000 / frequency)
                if data.get("managedAllocatedBytesOnThread") is not None:
                    allocations[data["kind"]].append(data["managedAllocatedBytesOnThread"])
        callbacks = [event["ticks"] for event in trace if event["kind"] == "rendering-callback" and begin <= event["ticks"] <= end]
        return {"startTicks": begin, "endTicks": end, "elapsedMs": (end - begin) * 1000 / frequency,
                "uiSpans": {name: {"durationMs": stats(groups[name]), "managedBytesOnThread": stats(allocations[name])}
                            for name in sorted(groups)},
                "renderingCallbackGapsMs": stats([(right - left) * 1000 / frequency for left, right in zip(callbacks, callbacks[1:])])}

    captures = {}
    for path in sorted(observations.glob("*-frames.json")):
        metadata = read(path)
        captured = metadata["captured"]
        captures[metadata["phase"]] = {
            "input": metadata["input"], "inputStart": metadata["inputStart"], "inputEnd": metadata["inputEnd"],
            "inputDispatchMs": (metadata["inputEnd"] - metadata["inputStart"]) * 1000 / frequency,
            "gdiCaptureDurationMs": stats([(frame["end"] - frame["begin"]) * 1000 / frequency for frame in captured]),
            "gdiSampleIntervalsMs": stats([(right["begin"] - left["begin"]) * 1000 / frequency for left, right in zip(captured, captured[1:])]),
            "source": str(path), "appDuringCapture": interval(captured[0]["begin"], captured[-1]["end"]),
            "pixelReview": "Not classified by this script; original PNGs remain the pixel evidence."}
    measured_actions = []
    for start in driver:
        if start["kind"] != "driver-action-start":
            continue
        details = start["details"]
        end = next((event for event in driver if event["kind"] == "driver-action-end"
                    and event["ticks"] >= start["ticks"]
                    and event["details"]["action"] == details["action"]
                    and event["details"]["sample"] == details["sample"]), None)
        if end is not None:
            measured_actions.append({"action": details["action"], "sample": details["sample"],
                                     "warmup": details["warmup"],
                                     "boundary": end["details"]["boundary"],
                                     "appDuringDriverCall": interval(start["ticks"], end["ticks"])})
    return {"run": run, "driverRevision": plan["driverRevision"], "rows": plan["rows"], "fields": plan["fields"],
            "executableSha256": plan["executableSha256"], "appDllSha256": plan["appDllSha256"],
            "testAssemblySha256": plan["testAssemblySha256"], "sourceTraceSha256": hashlib.sha256((folder / "app-trace.jsonl").read_bytes()).hexdigest(),
            "lifetime": read(observations / "lifetime.json"),
            "failures": [event for event in driver if event["kind"] == "diagnostic-failure"],
            "blockedPointerTargets": [event for event in driver if event["kind"] == "native-cell-click-blocked"],
            "earlyPhasesBeforeCommit": interval(first, last), "frameCaptures": captures,
            "fullTrace": interval(trace[0]["ticks"], trace[-1]["ticks"]),
            "measuredActions": measured_actions,
            "aggregateSpans": [event for event in trace if event["kind"] == "ui-span"
                               and event["data"]["kind"] == "update-aggregate-presentation"]}

result = {"boundary": "Early phases end before title commit. App spans are synchronous managed work; span kinds overlap and their sums must not be added together. Rendering callback gaps are not presented frames. GDI captures are independent sampled pixels; sampling and capture durations are not product frame rate or physical scanout latency.",
          "runs": [summarize(run) for run in run_names]}
destination = root / "TestResults" / "next-roadmap" / "perf-analysis.json"
destination.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(destination)
for run in result["runs"]:
    spans = run["earlyPhasesBeforeCommit"]["uiSpans"]
    print(run["run"], "completed=", run["lifetime"]["completed"], "driver=", run["driverRevision"])
    for name in ["update", "update-aggregate-presentation", "update-cell-presentation", "rebind-row"]:
        print(name, spans.get(name))
    for name, capture in run["frameCaptures"].items():
        print(name, "callbacks", capture["appDuringCapture"]["renderingCallbackGapsMs"], "GDI sampling", capture["gdiSampleIntervalsMs"])
