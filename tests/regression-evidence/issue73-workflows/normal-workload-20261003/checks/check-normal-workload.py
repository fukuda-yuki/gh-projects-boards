"""Read-only evidence audit. No product import, scheduler call, UI input or network.

Run from this evidence directory or supply its original root as argv[1].
Expected dates are the packet fixed before execution. Assignment representation
is checked explicitly against the documented legacy-preservation contract.
"""
from pathlib import Path
import copy
import hashlib
import json
import sys
from datetime import datetime, timezone

root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).parent
checks = []

def read(name):
    return json.loads((root / name).read_text(encoding="utf-8-sig"))

def sha(name):
    return hashlib.sha256((root / name).read_bytes()).hexdigest().upper()

def check(name, ok, detail):
    checks.append(dict(name=name, passed=bool(ok), detail=detail))

def key(field):
    return tuple(field["Key"].get(x) for x in ("Kind", "NodeId", "ProjectId", "FieldId"))

def value(field):
    change = field["Change"]
    return (None if change["Clear"] else change["Value"]) if change else field["Baseline"]

before, after = read("before.json"), read("after-restart.json")
packet = read("packet.json")
identity = read("identity-after.json")
bp, ap = before["Planning"][0], after["Planning"][0]
bt = {t["Id"]: t for t in bp["Tasks"]}
at = {t["Id"]: t for t in ap["Tasks"]}
bf = {key(f): f for f in before["Fields"]}
af = {key(f): f for f in after["Fields"]}

check("frozen source and original inputs", not identity["sourceMismatches"]
      and identity["buildInputs"] == 272
      and sha("packet.json") == "9E55FE2C46D0B1CEAFAB0A7056AA0F8A8199236BD845BBCC1129C4678F901733"
      and sha("before.json") == packet["fixture"]["beforeSha256"], identity)
for run in (1, 2):
    running = read(f"running-{run}.json")
    check(f"ordinary WinUI process {run} exact candidate", bool(running["winuiModules"])
          and running["appSha256"] == packet["fixture"]["appSha256"]
          and running["coreSha256"] == packet["fixture"]["coreSha256"], running)
check("normal exit and byte-identical restart checkpoint", read("process-exit-1.json")["exited"]
      and sha("after-close.json") == sha("after-restart.json"),
      {"afterClose": sha("after-close.json"), "afterRestart": sha("after-restart.json")})
check("separate live root unchanged", all(f["equal"] for f in identity["liveFiles"]), identity["liveFiles"])
check("all task and field identities retained", set(bt) == set(at)
      and len(bt) == len(at) == len(bp["Tasks"]) == len(ap["Tasks"]) == 1000
      and set(bf) == set(af) and len(bf) == len(af) == len(before["Fields"]) == len(after["Fields"]),
      {"tasks": len(at), "fields": len(af)})
check("full registrations, snapshots and second Project retained", before["Registrations"] == after["Registrations"]
      and len(after["Registrations"][0]["Snapshot"]["Items"]) == 1000
      and len(after["Registrations"][1]["Snapshot"]["Items"]) == 20,
      {"projects": 2, "itemCounts": [len(r["Snapshot"]["Items"]) for r in after["Registrations"]]})

task_differences = []
for task_id, original in bt.items():
    expected = copy.deepcopy(original)
    if expected["Assignment"] is None:
        expected["Assignment"] = {"Assignees": [], "Complete": False, "Legacy": True}
    if task_id == "I301":
        expected["Actuals"] = [{"PersonId": "U1", "Hours": 7, "ReportedThrough": "2026-10-09"}]
    if expected != at[task_id]:
        task_differences.append({"id": task_id, "expected": expected, "actual": at[task_id]})
check("all 1000 task contracts preserved with only specified report change", not task_differences,
      {"unexpectedDifferences": task_differences,
       "explicitMigration": "v1 to v3; null assignment becomes empty/incomplete/Legacy=true. Owner and semantics stay unchanged.",
       "manualTasks": sum(t["Mode"] == 2 for t in bt.values()),
       "completedTasks": sum(t["Progress"] == 2 for t in bt.values()),
       "report": at["I301"]["Actuals"]})
expected_plan = copy.deepcopy(bp)
for name in ("Version", "Stamp", "Cutoff", "Calendar", "Tasks"):
    expected_plan.pop(name)
unchanged_plan = {k:v for k,v in ap.items() if k not in ("Version", "Stamp", "Cutoff", "Calendar", "Tasks")}
check("20 people, weights, start and mappings retained", expected_plan == unchanged_plan
      and len(ap["People"]) == 20 and bp["Version"] == 1 and ap["Version"] == 3,
      {"people": ap["People"], "beforeVersion": bp["Version"], "afterVersion": ap["Version"]})
expected_calendar = copy.deepcopy(bp["Calendar"])
expected_calendar.pop("Revision")
expected_calendar["Exceptions"] = [{"Date": "2026-10-13", "PersonId": "U2",
                                    "Intervals": [{"StartMinute": 840, "EndMinute": 1080}]}]
actual_calendar = {k:v for k,v in ap["Calendar"].items() if k != "Revision"}
check("only chosen cutoff and U2 calendar exception changed", expected_calendar == actual_calendar
      and ap["Cutoff"] == "2026-10-09T18:00:00"
      and bool(ap["Calendar"]["Revision"]) and ap["Calendar"]["Revision"] != bp["Calendar"]["Revision"],
      {"cutoff": ap["Cutoff"], "exceptions": ap["Calendar"]["Exceptions"], "holidaysPreserved": bp["Calendar"]["Holidays"] == ap["Calendar"]["Holidays"]})

raw_differences, provenance_differences, changed_values = [], [], []
allowed_numeric = {("Number", "P1T301", "P1", "F-Actual"): "7",
                   ("Number", "P1T301", "P1", "F-Remaining"): "3"}
for field_key, original in bf.items():
    current = af[field_key]
    for prop in ("Baseline", "SourceProject", "RetrievedAt", "Observation", "Conflict", "Buffer"):
        if original[prop] != current[prop]:
            provenance_differences.append({"key": field_key, "property": prop, "before": original[prop], "after": current[prop]})
    expected_value = allowed_numeric.get(field_key, value(original))
    is_derived_date = field_key[0] == "Date" and field_key[2] == "P1" and field_key[3] in ("F-Start", "F-Finish")
    if not is_derived_date and value(current) != expected_value:
        raw_differences.append({"key": field_key, "expected": expected_value, "actual": value(current)})
    if value(original) != value(current):
        changed_values.append({"key": field_key, "before": value(original), "after": value(current)})
check("raw values and original field provenance preserved", not raw_differences and not provenance_differences,
      {"unexpectedRawDifferences": raw_differences, "unexpectedProvenanceDifferences": provenance_differences,
       "numericChanges": [d for d in changed_values if d["key"][0] != "Date"],
       "derivedDateChanges": sum(d["key"][0] == "Date" for d in changed_values)})
buffers = [{"key": k, "value": f["Buffer"]} for k,f in af.items() if f["Buffer"] is not None]
check("exact independent pending value and undone estimate retained", buffers == [
    {"key": ("Number", "P1T1000", "P1", "F-Estimate"), "value": "24未確定"}]
    and value(af[("Number", "P1T302", "P1", "F-Estimate")]) == "16",
    {"buffers": buffers, "I302Estimate": value(af[("Number", "P1T302", "P1", "F-Estimate")])})
check("original history retained and two committed operations remain", after["History"][:len(before["History"])] == before["History"]
      and len(after["History"]) == len(before["History"]) + 2,
      {"beforeHistory": len(before["History"]), "afterHistory": len(after["History"]),
       "meaning": "Daily report and planning settings remain; the mistaken estimate was undone once in observations 40-42."})
unchanged_top = [k for k in before if k not in ("Revision", "Fields", "History", "Planning")]
check("remaining checkpoint content, journal and creation state unchanged",
      all(before[k] == after[k] for k in unchanged_top) and after["Journal"] == [] and after["LocalRows"] == [],
      {"properties": unchanged_top, "journalEntries": len(after["Journal"]), "localRows": len(after["LocalRows"]),
       "networkBoundary": "No network instrumentation claimed. Ordinary observations show cached unverified identity; no connection, Apply or creation operation was requested."})

observations = {p.stem: json.loads(p.read_text(encoding="utf-8-sig")) for p in (root/"observations").glob("*.json")}
def observation(prefix):
    matches = [v for k,v in observations.items() if k.startswith(prefix + "-")]
    if len(matches) != 1:
        raise ValueError(f"Expected one observation {prefix}, found {len(matches)}")
    return matches[0]["accessibility"]["tree"]

for task_id, prefixes in {"I301": ["27", "55"], "I302": ["29", "42", "56"],
                          "I303": ["42", "57"], "I330": ["43"], "I350": ["44"], "I1000": ["45"]}.items():
    expected = packet["expectedFinal"][task_id]
    interval = expected["start"].replace("T", " ")[:16] + " → " + expected["finish"].replace("T", " ")[:16]
    for prefix in prefixes:
        lines = [line.strip() for line in observation(prefix).splitlines() if f"ID: GanttRow-P1T{task_id[1:]}" in line]
        check(f"ordinary adopted interval {task_id} observation {prefix}", len(lines) == 1 and interval in lines[0],
              {"expected": interval, "observed": lines, "evidence": prefix})
check("mistake visibly changed I302 to the independent expected finish", any(
    "2026-10-13 14:00 → 2026-10-16 15:00" in line for line in observation("40").splitlines()
    if "ID: GanttRow-P1T302" in line), {"evidence": "40-mistake-gantt-settled"})
for prefix in ("47", "53"):
    tree = observation(prefix)
    check(f"pending input remains separate from calculation observation {prefix}",
          "入力途中: 24未確定" in tree and "確定値: 16" in tree and "日程計算は確定値を使用" in tree,
          {"evidence": prefix, "pending": "24未確定", "confirmed": "16"})

result = {"atUtc": datetime.now(timezone.utc).isoformat(),
          "method": "Independent standard-library JSON audit of retained checkpoints and UI observations; no production calculator",
          "inputs": {name: sha(name) for name in ("packet.json", "before.json", "after-close.json", "after-restart.json")},
          "executed": len(checks), "passed": sum(c["passed"] for c in checks),
          "failed": sum(not c["passed"] for c in checks), "skipped": 0, "checks": checks}
destination = root / "independent-readback.json"
if destination.exists():
    raise FileExistsError("Retain prior outcomes: choose a fresh evidence root instead of overwriting this result")
destination.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({k:v for k,v in result.items() if k != "checks"}, ensure_ascii=False, indent=2))
for c in checks:
    if not c["passed"]:
        print(json.dumps(c, ensure_ascii=False, indent=2))
sys.exit(0 if result["failed"] == 0 else 1)
