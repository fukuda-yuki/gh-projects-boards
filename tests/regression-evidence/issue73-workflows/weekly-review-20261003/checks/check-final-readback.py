"""Read-only comparison of the independent GitHub snapshot, fixed oracle and journal."""
import hashlib
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent
STATE = ROOT / "sandbox-fixture/state.json"
MATERIALIZED = ROOT / "sandbox-fixture/materialization.json"
PACKET = ROOT / "independent-packet.json"
FINAL = ROOT / "sandbox-fixture/runs/20261003T040755Z-snapshot-3c31f0b23d34487badc8f60fadd99b65/app-final.json"
CHECKPOINT = ROOT / "live-app-data/Drafts/E192BF56CA77E46DC06147205E2B163526A65C7B67E946D9513123207E7EDBF7.json"
OUTPUT = ROOT / "checks/app-final-independent-readback.json"
inputs = {}


def load(path):
    raw = path.read_bytes()
    inputs[str(path)] = hashlib.sha256(raw).hexdigest().upper()
    return json.loads(raw.decode("utf-8-sig"))


def canon(value):
    if isinstance(value, dict):
        return {k: canon(v) for k, v in sorted(value.items())}
    if isinstance(value, list):
        return sorted((canon(v) for v in value), key=lambda v: json.dumps(v, sort_keys=True))
    return value


checks = []


def check(name, expected, actual):
    checks.append({"name": name, "expected": expected, "actual": actual,
                   "status": "pass" if canon(expected) == canon(actual) else "fail"})


state = load(STATE)
materialized = load(MATERIALIZED)
packet = load(PACKET)
baseline = load(Path(state["afterInit"]))
final = load(FINAL)
checkpoint = load(CHECKPOINT)
check("fixture.hasNoUncertainWrites", False, bool(state["uncertain"] or state["pending"]))
check("scope", baseline["scope"], final["scope"])
check("fields", baseline["fields"], final["fields"])
old_items = {v["id"]: v for v in baseline["items"]}
new_items = {v["id"]: v for v in final["items"]}
check("project.membership", sorted(old_items), sorted(new_items))
old_issues = {v["id"]: v for v in baseline["issues"]}
new_issues = {v["id"]: v for v in final["issues"]}
check("repository.issueSet", sorted(old_issues), sorted(new_issues))
owners = materialized["issues"]
roles = {k: v["id"] for k, v in materialized["fields"].items()}
allowed = {(owners[v["task"]]["itemId"], roles[v["field"]]): v
           for kind in ("numberChanges", "dateChanges")
           for v in packet["expectedFinalBusinessChanges"][kind]}
actual_changes = []
for item_id, before in old_items.items():
    after = new_items[item_id]
    check(f"item.{item_id}.identity", {k: v for k, v in before.items() if k != "fieldValues"},
          {k: v for k, v in after.items() if k != "fieldValues"})
    check(f"item.{item_id}.complete", False, after["fieldValues"]["pageInfo"]["hasNextPage"])
    old_values = {v["field"]["id"]: v for v in before["fieldValues"]["nodes"]}
    new_values = {v["field"]["id"]: v for v in after["fieldValues"]["nodes"]}
    for field_id in sorted(set(old_values) | set(new_values)):
        expected = old_values.get(field_id)
        if (item_id, field_id) in allowed:
            entry = allowed[(item_id, field_id)]
            expected = dict(expected)
            value_key = "date" if "date" in expected else "number"
            check(f"input.{entry['task']}.{entry['field']}.baseline", entry["before"], expected[value_key])
            expected[value_key] = entry["after"]
        check(f"item.{item_id}.{field_id}", expected, new_values.get(field_id))
        if canon(old_values.get(field_id)) != canon(new_values.get(field_id)):
            actual_changes.append([item_id, field_id])
check("exactlyTheFiveScalarChanges", sorted([list(v) for v in allowed]), sorted(actual_changes))

for task, owned in owners.items():
    oracle = packet["weeklyOracle"][task]
    values = {v["field"]["id"]: v for v in new_items[owned["itemId"]]["fieldValues"]["nodes"]}
    for role, key in [("Estimate", "estimate"), ("Remaining", "remaining"), ("Actual", "actual"),
                      ("Start", "dateStart"), ("Finish", "dateFinish")]:
        value = values.get(roles[role], {})
        check(f"oracle.{task}.{role}", oracle[key], value.get("date", value.get("number")))

for issue_id, before in old_issues.items():
    after = new_issues[issue_id]
    check(f"issue.{before['number']}.unrelatedContent", {k: v for k, v in before.items() if k not in ("blockedBy", "blocking")},
          {k: v for k, v in after.items() if k not in ("blockedBy", "blocking")})
    for relation in ("blockedBy", "blocking"):
        expected = {v["id"] for v in before[relation]["nodes"]}
        if issue_id == owners["successor"]["id"] and relation == "blockedBy":
            expected.add(owners["carryover"]["id"])
        if issue_id == owners["carryover"]["id"] and relation == "blocking":
            expected.add(owners["successor"]["id"])
        check(f"issue.{before['number']}.{relation}", sorted(expected), sorted(v["id"] for v in after[relation]["nodes"]))
        check(f"issue.{before['number']}.{relation}.complete", False, after[relation]["pageInfo"]["hasNextPage"])

journal = checkpoint["Journal"]
check("journal.oneBatch", 1, len(journal))
operations = journal[0]["Operations"]
check("journal.noCreation", [], journal[0]["Creations"])
expected_operations = {(item, field, str(v["after"])) for (item, field), v in allowed.items()}
expected_operations.add((owners["successor"]["itemId"], owners["carryover"]["id"], "present"))
check("journal.onlyApprovedSixOperations", sorted(expected_operations),
      sorted((op["ItemId"], op["Key"]["FieldId"], op["Intended"]["Value"]) for op in operations))
for op in operations:
    check(f"journal.{op['Id']}.succeeded", 2, op["State"])
    check(f"journal.{op['Id']}.verifiedValue", op["Intended"]["Value"], op["Verification"]["Value"])
    check(f"journal.{op['Id']}.oneAttempt", 1, len(op["Attempts"]))
check("local.noRemainingFieldChanges", 0, sum(v["Change"] is not None for v in checkpoint["Fields"]))
check("local.noPendingText", 0, sum(v["Buffer"] is not None for v in checkpoint["Fields"]))
check("inputBytesUnchangedDuringRead", True,
      all(hashlib.sha256(Path(p).read_bytes()).hexdigest().upper() == sha for p, sha in inputs.items()))
counts = dict(Counter(v["status"] for v in checks))
result = {"atUtc": datetime.now(timezone.utc).isoformat(), "mechanism": "Independent Python JSON comparison; no production scheduler, app mutation or remote call",
          "inputs": inputs, "checkerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest().upper(),
          "counts": counts, "passed": not counts.get("fail"), "checks": checks,
          "limits": "GitHub DATE has no exact minute. Exact adopted Auto/Manual endpoints are independently compared with ordinary UI observations and stored local metadata. No human acceptance or comparative timing."}
with OUTPUT.open("x", encoding="utf-8") as handle:
    json.dump(result, handle, ensure_ascii=False, indent=2)
    handle.write("\n")
print(json.dumps({"output": str(OUTPUT), "counts": counts, "passed": result["passed"]}))
raise SystemExit(0 if result["passed"] else 1)
