"""Conservative input/capture/viewport ledger; never equates callbacks to scanout.

Run with Python and Pillow against one retained observations directory. Captures
are read without modification. Each response bracket is local to one command;
sampling and possible preceding-command aliasing are retained as limitations.
"""
import bisect
import hashlib
import json
import sys
from collections import Counter
from pathlib import Path

from PIL import Image

root = Path(sys.argv[1])
plan = json.loads((root / "plan.json").read_text(encoding="utf-8-sig"))
frequency = plan["frequency"]
driver = [json.loads(line) for line in (root / "driver.jsonl").read_text(encoding="utf-8-sig").splitlines()]
frames = json.loads((root / "scroll-captures.json").read_text(encoding="utf-8-sig"))
trace_file = root / "app-trace.jsonl"
trace = [json.loads(line) for line in trace_file.read_text(encoding="utf-8-sig").splitlines()] if trace_file.exists() else []
for frame in frames:
    with Image.open(root / frame["path"]) as pixels:
        # Row numbers exclude the caret/hover and horizontal scrollbar. Later
        # columns show horizontal displacement independently of the frozen title.
        frame["vertical"] = hashlib.sha256(pixels.crop((0, 0, 50, pixels.height - 22)).tobytes()).hexdigest()
        frame["horizontal"] = hashlib.sha256(pixels.crop((550, 0, pixels.width - 30, pixels.height - 22)).tobytes()).hexdigest()

def ms(ticks):
    return ticks * 1000 / frequency

inputs = [event for event in driver if event["kind"] == "scroll-input"]
views = [event for event in trace if event["kind"] == "scroll-view-changed"]
frame_ends = [frame["end"] for frame in frames]
commands = []
for number, event in enumerate(inputs):
    begin = event["detail"]["begin"]
    end = inputs[number + 1]["detail"]["begin"] if number + 1 < len(inputs) else frames[-1]["end"]
    axis = event["detail"].get("axis", "horizontal" if event["detail"]["index"] % 6 in (4, 5) else "vertical")
    before_index = bisect.bisect_right(frame_ends, begin) - 1
    if before_index < 0:
        commands.append(dict(index=event["detail"]["index"], begin=begin, end=end, classification="missing-before-frame"))
        continue
    before = frames[before_index]
    after = [frame for frame in frames[before_index + 1:] if begin <= frame["begin"] < end]
    change = next((frame for frame in after if frame[axis] != before[axis]), None)
    unchanged = [frame for frame in after if change is None or frame["begin"] < change["begin"]]
    lower = ms(unchanged[-1]["begin"] - begin) if unchanged else 0
    upper = ms(change["end"] - begin) if change else None
    wheels = [record for record in trace if record["kind"] == "sheet-wheel" and begin <= record["ticks"] < end]
    observed = [record for record in views if begin <= record["ticks"] < end]
    prior = next((record for record in reversed(views) if record["ticks"] < begin), None)
    offset = axis + "Offset"
    previous_offset = prior["data"]["state"][offset] if prior else 0
    target = wheels[0]["data"]["wheelHorizontal" if axis == "horizontal" else "wheelVertical"] if wheels else None
    expected = abs(target - previous_offset) > 0.5 if target is not None else None
    delivery = ms(wheels[0]["ticks"] - begin) if wheels else None
    matching_view = next((record for record in observed if target is not None and abs(record["data"]["state"][offset] - target) < 0.5), None)
    view_latency = ms(matching_view["ticks"] - begin) if matching_view else None
    if expected is False:
        classification = "clamped-or-no-op"
    elif expected is None:
        classification = "trace-disabled-or-missing-delivery"
    elif lower >= 100:
        classification = "application-viewport-delay" if delivery is not None and delivery < 20 and view_latency is not None and view_latency >= 100 else "visible-delay-unattributed"
    else:
        classification = "no-100ms-stable-capture-proven"
    commands.append(dict(index=event["detail"]["index"], begin=begin, end=end, axis=axis,
        previousOffset=previous_offset, requestedOffset=target, expectedMovement=expected,
        nativeDeliveryMs=delivery, observedViewportMs=view_latency, before=before["path"],
        firstChanged=change["path"] if change else None, unchangedThroughMs=lower, changedByMs=upper,
        classification=classification, wheels=wheels, views=observed))

def covered(intervals, start, end):
    merged = []
    for left, right in sorted((max(start, a), min(end, b)) for a, b in intervals if a < end and b > start):
        if merged and left <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], right)
        else:
            merged.append([left, right])
    return ms(sum(right - left for left, right in merged))

phase = next(event["ticks"] for event in driver if event["kind"] == "scroll-phase-start")
phase_end = next(event["ticks"] for event in driver if event["kind"] == "scroll-phase-end")
spans = [record["data"] for record in trace if record["kind"] == "ui-span"]
gaps = []
for record in trace:
    gap = record.get("data", {}).get("previousGapTicks") if record["kind"] == "rendering-callback" else None
    if not gap or ms(gap) < 100 or not phase <= record["ticks"] <= phase_end:
        continue
    start, end = record["ticks"] - gap, record["ticks"]
    overlapping = [command for command in commands if command["begin"] <= end and command["end"] >= start]
    classes = sorted(set(command["classification"] for command in overlapping))
    captured = [frame for frame in frames if start <= frame["begin"] <= end]
    changing = len({frame["vertical"] for frame in captured}) > 1 or len({frame["horizontal"] for frame in captured}) > 1
    if "application-viewport-delay" in classes:
        classification = "correlated-application-viewport-delay"
    elif "visible-delay-unattributed" in classes:
        classification = "correlated-visible-delay-unattributed"
    elif classes == ["clamped-or-no-op"]:
        classification = "clamped-or-no-op"
    elif changing:
        classification = "callback-gap-with-captured-progress"
    else:
        classification = "unexplained-callback-gap"
    overlapping_spans = [span for span in spans if span["start"] <= end and span["end"] >= start]
    attributed = covered([(span["start"], span["end"]) for span in overlapping_spans], start, end)
    gaps.append(dict(index=len(gaps), start=start, end=end, milliseconds=ms(gap),
        commands=[command["index"] for command in overlapping], classification=classification,
        observedUiSpanUnionMs=attributed, outsideMeasuredUiSpansMs=max(0, ms(gap) - attributed),
        frames=captured, spans=overlapping_spans))

result = dict(plan=plan, commands=commands, callbackGaps=gaps,
    commandCounts=dict(Counter(command["classification"] for command in commands)),
    callbackCounts=dict(Counter(gap["classification"] for gap in gaps)),
    boundary="Independent GDI sheet pixels, native-wheel delivery and public viewport events; not physical scanout. Stable-frame lower bounds and first-change upper bounds include sampling. An application-viewport-delay requires expected movement, native delivery under 20ms, a viewport event at/after 100ms and independently unchanged frames beyond 100ms. Other visible delays remain unattributed. Gaps outside instrumented spans are unexplained, not automatically GC or idle. No per-run GC attribution from a separate profile.")
(root / "classified-scroll.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(dict(run=root.parent.name, commands=result["commandCounts"], callbackGaps=result["callbackCounts"],
    maximumCallbackGapMs=max((gap["milliseconds"] for gap in gaps), default=None)), ensure_ascii=False))
