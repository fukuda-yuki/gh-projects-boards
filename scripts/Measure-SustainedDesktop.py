"""Classify the existing sustained runner's desktop observations, never callbacks as pixels.

Exact viewport-content matches use settled images for the same requested offsets.
They remain INCONCLUSIVE until their task/value content is independently reviewed.
Reviews identify exact original RGB crops. Identical reviewed content can be
recognized in another run; its timestamps and capture quality remain independent.
"""
import argparse
import bisect
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path

from PIL import Image
import numpy as np


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def analyze_readiness(root):
    plan = read(root / "plan.json")
    frequency = plan["frequency"]
    driver = [json.loads(line) for line in (root / "driver.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    trials = [event["detail"] for event in driver if event["kind"] == "editor-readiness"]
    frames = read(root / "readiness-desktop/frames.json")
    lifetime = read(root / "readiness-desktop/lifetime.json")
    times = [frame["present"] for frame in frames]
    reviews = read(root / "editor-content-review.json") if (root / "editor-content-review.json").exists() else {}
    quality = bool(lifetime["complete"] and not lifetime["saturated"] and frames
                   and len(trials) == len(plan["readinessSchedule"])
                   and all(0 < f["present"] <= f["acquired"] <= f["copied"] and not f["masked"] for f in frames)
                   and all(a < b for a, b in zip(times, times[1:])))
    records = []
    for trial, declared in zip(trials, plan["readinessSchedule"]):
        quality &= all(trial[key] == declared[key] for key in ("Index", "Row", "Column", "Text"))
        rect, viewport = trial["rectangle"], trial["viewport"]
        x, y = rect["X"] - viewport["X"], rect["Y"] - viewport["Y"]
        crop = (x + 2, y + 2, x + rect["Width"] - 2, y + rect["Height"] - 2)
        selected = [frame for frame in frames if trial["begin"] - frequency / 5 <= frame["present"] <= trial["end"]]
        hashes = {}
        dark = True
        for frame in selected:
            if frame["path"] in hashes:
                continue
            with Image.open(root / "readiness-desktop" / frame["path"]) as image:
                pixels = image.crop(crop).convert("RGB")
                rgb = np.asarray(pixels)
                dark &= bool(np.mean(rgb.max(axis=2) < 100) > .5)
                hashes[frame["path"]] = (hashlib.sha256(pixels.tobytes()).hexdigest(),
                                         hashlib.sha256((rgb.min(axis=2) > 160).tobytes()).hexdigest())
        before = next((f for f in reversed(selected) if f["present"] <= trial["begin"]), None)
        after = [f for f in selected if f["present"] > trial["begin"]]
        # These are review candidates, not presumed correct results. A passive
        # observer may have no new desktop frame after the delayed UIA readback.
        references = {hashes[f["path"]][0]: f["path"] for f in after}
        known, incorrect = set(), set()
        for frame in selected:
            digest, ink = hashes[frame["path"]]
            review = reviews.get(f'{trial["Index"]}:{digest}', {})
            if review.get("frame") != frame["path"] or review.get("expected") != trial["expected"]:
                continue
            if review.get("correct") is True:
                known.add(ink)
            elif review.get("correct") is False:
                incorrect.add(ink)
        quality &= dark and not bool(known & incorrect)
        expected = next((f for f in after if hashes[f["path"]][1] in known), None)
        changed = next((f for f in after if before and hashes[f["path"]][0] != hashes[before["path"]][0]), None)
        expected_ms = (expected["present"] - trial["begin"]) * 1000 / frequency if expected else None
        changed_ms = (changed["present"] - trial["begin"]) * 1000 / frequency if changed else None
        complete = bool(after) and all(f["accumulated"] == 1 for f in after if expected is None or f["present"] <= expected["present"])
        # A focus/caret frame may still contain the old value. Only independently
        # rejected content and the unchanged pre-input value exclude an earlier
        # result; an unreviewed changed frame remains a possible early result.
        rejected = incorrect | ({hashes[before["path"]][1]} if before else set())
        quality &= not bool(known & rejected)
        possible = next((f for f in after if hashes[f["path"]][1] not in rejected), None)
        earliest_possible_ms = (possible["present"] - trial["begin"]) * 1000 / frequency if possible and complete else None
        records.append(dict(index=trial["Index"], row=trial["Row"], column=trial["Column"], expectedText=trial["expected"],
                            matched=trial["matched"], classBefore=trial["classBefore"], crop=crop,
                            nativeMs=trial.get("nativeMilliseconds"), delayedNativeReadbackMs=trial.get("delayedNativeReadbackMs"),
                            expectedAtMs=expected_ms, firstChangedAtMs=changed_ms,
                            earliestPossibleExpectedAtMs=earliest_possible_ms,
                            completeUpdates=complete, contentReviewed=expected is not None, before=before["path"] if before else None,
                            expected=expected["path"] if expected else None, references=references))
    def percentile(values):
        import math
        return sorted(values)[math.ceil(len(values) * .95) - 1] if values else None
    pixels = [r["expectedAtMs"] for r in records if r["expectedAtMs"] is not None]
    earliest = [r["earliestPossibleExpectedAtMs"] for r in records if r["earliestPossibleExpectedAtMs"] is not None]
    native = [event["detail"]["milliseconds"] for event in driver if event["kind"] == "typed-value" and event["detail"]["measured"]]
    visible_p95, native_p95 = percentile(pixels), percentile(native)
    if any(not r["matched"] for r in records):
        status = "FAIL"
    elif not quality or len(pixels) != len(plan["readinessSchedule"]) or not native:
        status = "INCONCLUSIVE"
    elif visible_p95 <= 100 and native_p95 <= 100:
        status = "PASS"
    elif native_p95 > 100 or (len(earliest) == len(records) and percentile(earliest) > 100):
        status = "FAIL"
    else:
        status = "INCONCLUSIVE"
    control = plan.get("readinessSurface") == "filter-control"
    result = dict(source=plan["source"], condition=plan["condition"], status="NOT_RUN" if control else status,
                  diagnosticOnly=control, surface=plan.get("readinessSurface", "sheet"),
                  observationQuality="PASS" if quality else "INCONCLUSIVE", selectionVisibleP95Ms=visible_p95,
                  continuingNativeP95Ms=native_p95, selectionSamples=len(records), continuingSamples=len(native),
                  keyDelayMs=plan.get("readinessKeyDelayMs", 0), trials=records,
                  boundary="Native selection dispatch including fixed click-to-key interval and activation, to independently reviewed DXGI cell pixels. Continuing native-value input is a separate distribution; no physical scanout claim.")
    (root / "editor-readiness-measurements.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


def analyze(root):
    plan = read(root / "plan.json")
    frequency = plan["frequency"]
    driver = [json.loads(line) for line in (root / "driver.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    trace = [json.loads(line) for line in (root / "app-trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    inputs = [e["detail"] for e in driver if e["kind"] == "scroll-input"]
    frames = read(root / "desktop/frames.json")
    lifetime = read(root / "desktop/lifetime.json")
    hashes = {}
    dark_frames = True
    for frame in frames:
        path = frame["path"]
        if path not in hashes:
            with Image.open(root / "desktop" / path) as image:
                # Exclude scrollbar chrome only. Keep every row number, title and value.
                pixels = image.crop((0, 0, image.width - 24, image.height - 24))
                rgb = np.asarray(pixels.convert("RGB"))
                dark_frames &= bool(np.mean(rgb.max(axis=2) < 100) > .5)
                hashes[path] = (hashlib.sha256(pixels.tobytes()).hexdigest(),
                                hashlib.sha256((rgb.min(axis=2) > 160).tobytes()).hexdigest())
        frame["contentHash"], frame["inkHash"] = hashes[path]
    times = [f["present"] for f in frames]
    profile = plan.get("scrollProfile", "stress")
    schedule = plan.get("scrollSchedule")
    schedule_complete = schedule is None or (len(inputs) == len(schedule) and all(
        command["index"] == declared["Index"] and command["axis"] == declared["Axis"]
        and command["apiArgument"] == declared["ApiArgument"] for command, declared in zip(inputs, schedule)))
    continuous_complete = profile != "continuous" or bool(inputs and all(
        frame["accumulated"] == 1 for frame in frames if frame["present"] >= inputs[0]["begin"])
        and all(command["actualMs"] - command["dueMs"] < 100 for command in inputs))
    quality = bool(lifetime["complete"] and not lifetime["saturated"] and frames and dark_frames
                   and schedule_complete and continuous_complete
                   and all(0 < f["present"] <= f["acquired"] <= f["copied"] and not f["masked"] for f in frames)
                   and all(a < b for a, b in zip(times, times[1:])))
    wheels = [e for e in trace if e["kind"] == "sheet-wheel"]
    views = [e for e in trace if e["kind"] == "scroll-view-changed"]
    records = []
    references = defaultdict(dict)
    for i, command in enumerate(inputs):
        begin = command["begin"]
        end = inputs[i + 1]["begin"] if i + 1 < len(inputs) else frames[-1]["present"] + 1
        axis = command["axis"]
        matching = [e for e in wheels if begin <= e["data"].get("handlerEntered", e["ticks"]) < end
                    and e["data"]["horizontal"] == (axis == "horizontal")]
        prior = [e for e in views if e["ticks"] < begin]
        previous = prior[-1]["data"]["state"] if prior else {"horizontalOffset": 0, "verticalOffset": 0}
        wheel = matching[0]["data"] if len(matching) == 1 else None
        if wheel:
            previous = dict(horizontalOffset=wheel["priorHorizontal"], verticalOffset=wheel["priorVertical"])
        offset = wheel["wheelHorizontal" if axis == "horizontal" else "wheelVertical"] if wheel else None
        target = [previous["horizontalOffset"], previous["verticalOffset"]]
        if offset is not None:
            target[0 if axis == "horizontal" else 1] = offset
        state = ",".join(f"{x:.2f}" for x in target)
        matching_views = [e for e in views if begin <= e["ticks"] < end and offset is not None
                          and abs(e["data"]["state"]["horizontalOffset"] - target[0]) < 1
                          and abs(e["data"]["state"]["verticalOffset"] - target[1]) < 1]
        reversed_view = [e for e in views if matching_views and matching_views[0]["ticks"] < e["ticks"] < end
                         and (abs(e["data"]["state"]["horizontalOffset"] - target[0]) >= 1
                              or abs(e["data"]["state"]["verticalOffset"] - target[1]) >= 1)]
        last_index = bisect.bisect_left(times, end) - 1
        last = frames[last_index] if last_index >= 0 else None
        # A settled reference needs both the requested app offset and later desktop
        # observation. This is only a candidate reference, not semantic acceptance.
        if last and matching_views and last["present"] > matching_views[0]["ticks"]:
            references[state][last["contentHash"]] = last["path"]
        records.append(dict(index=command["index"], begin=begin, end=end, axis=axis,
                            apiArgument=command.get("apiArgument", command["detents"]), state=state,
                            delivered=wheel, deliveredCount=len(matching),
                            requestedOffset=offset, priorOffset=previous[axis + "Offset"],
                            actualOffset=matching_views[0]["data"]["state"][axis + "Offset"] if matching_views else None,
                            actualDisplacement=matching_views[0]["data"]["state"][axis + "Offset"] - previous[axis + "Offset"] if matching_views else None,
                            unexpectedViewportChange=bool(reversed_view),
                            viewportMs=(matching_views[0]["ticks"] - begin) * 1000 / frequency if matching_views else None))
    reviews = read(root / "desktop-content-review.json") if (root / "desktop-content-review.json").exists() else {}
    # Dark fixture only: white foreground glyphs must match at every pixel. Native
    # hover-background fades are not part of readable-value completion. Review is
    # still tied to the original RGB image, never inferred from a changed gutter.
    known_ink = defaultdict(dict)
    incorrect_ink = defaultdict(dict)
    for digest, review in reviews.items():
        path = review.get("frame")
        if path not in hashes or hashes[path][0] != digest or not review.get("state"):
            continue
        if review.get("correct") is True:
            known_ink[review["state"]][hashes[path][1]] = digest
        elif review.get("correct") is False:
            incorrect_ink[review["state"]][hashes[path][1]] = digest
    review_conflicts = [state for state in known_ink if known_ink[state].keys() & incorrect_ink[state].keys()]
    quality = quality and not review_conflicts
    for record in records:
        begin, end = record["begin"], record["end"]
        before_index = bisect.bisect_right(times, begin) - 1
        before = frames[before_index] if before_index >= 0 else None
        response_end = end
        if profile == "continuous":
            deadline_frame = bisect.bisect_left(times, begin + frequency / 10)
            if deadline_frame < len(frames):
                # The next scheduled input can arrive just before this deadline.
                # Retain the first desktop update at/after 100 ms so a late prior
                # response is not censored into an inconclusive empty window.
                response_end = max(end, frames[deadline_frame]["present"] + 1)
        after = [f for f in frames[max(0, before_index + 1):] if begin < f["present"] < response_end]
        expected = next((f for f in after if f["inkHash"] in known_ink[record["state"]]), None)
        changed = next((f for f in after if before and f["contentHash"] != before["contentHash"]), None)
        unchanged = [f for f in after if changed is None or f["present"] < changed["present"]]
        old_ms = (unchanged[-1]["present"] - begin) * 1000 / frequency if unchanged else None
        expected_ms = (expected["present"] - begin) * 1000 / frequency if expected else None
        through = [f for f in after if expected is None or f["present"] <= expected["present"]]
        complete_updates = bool(through) and all(f["accumulated"] == 1 for f in through)
        no_op = record["requestedOffset"] is not None and abs(record["requestedOffset"] - record["priorOffset"]) < 1
        reviewed = expected is not None
        incorrect = next((f for f in after if (f["present"] - begin) * 1000 / frequency >= 100
                          and f["inkHash"] in incorrect_ink[record["state"]]), None)
        if not quality or record["deliveredCount"] != 1 or not before:
            status = "INCONCLUSIVE"
        elif no_op:
            status = "NOT_RUN"
        elif record["unexpectedViewportChange"] or incorrect is not None or old_ms is not None and old_ms >= 100:
            status = "FAIL"
        elif expected_ms is not None and expected_ms <= 100 and reviewed:
            status = "PASS"
        elif changed is not None and (changed["present"] - begin) * 1000 / frequency > 100 and complete_updates and reviewed:
            status = "FAIL"
        else:
            status = "INCONCLUSIVE"
        record.update(status=status, noOp=no_op, responseWindowEnd=response_end, lastOldAtMs=old_ms, expectedAtMs=expected_ms,
                      firstChangedAtMs=(changed["present"] - begin) * 1000 / frequency if changed else None,
                      completeUpdates=complete_updates, contentReviewed=reviewed,
                      incorrectAfterDeadline=incorrect["path"] if incorrect else None,
                      before=before["path"] if before else None, expected=expected["path"] if expected else None,
                      expectedHash=expected["contentHash"] if expected else None)
    result = dict(source=plan["source"], profile=plan.get("scrollProfile", "stress"), condition=plan["condition"],
                  observationQuality="PASS" if quality else "INCONCLUSIVE", darkFixture=dark_frames,
                  scheduleComplete=schedule_complete, continuousObservationComplete=continuous_complete,
                  reviewConflicts=review_conflicts,
                  counts=dict(Counter(r["status"] for r in records)),
                  references=dict(references), commands=records,
                  boundary="Driver dispatch to independent DXGI desktop content. Dark fixture only: exact neutral foreground mask (all RGB channels >160) across the full body except scrollbar chrome; original RGB reference needs task/value review. Hover background transitions do not delay readable completion. Not physical scanout.")
    (root / "desktop-measurements.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("observations", type=Path)
    args = parser.parse_args()
    result = analyze_readiness(args.observations) if read(args.observations / "plan.json")["mode"] == "readiness" else analyze(args.observations)
    print(json.dumps({k: v for k, v in result.items() if k not in ("commands", "references", "trials")}, indent=2))
