"""Classify the existing sustained runner's desktop observations, never callbacks as pixels.

Exact viewport-content matches use settled images for the same requested offsets.
They remain INCONCLUSIVE until their task/value content is independently reviewed.
The review file records the exact image digest, so a different run cannot inherit it.
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
        after = [f for f in frames[max(0, before_index + 1):] if begin < f["present"] < end]
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
        record.update(status=status, noOp=no_op, lastOldAtMs=old_ms, expectedAtMs=expected_ms,
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
    result = analyze(args.observations)
    print(json.dumps({k: v for k, v in result.items() if k not in ("commands", "references")}, indent=2))
