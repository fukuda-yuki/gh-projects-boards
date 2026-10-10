"""Generate the shared, synthetic waterfall versions; never contacts GitHub.

Each of the 20 people owns two requirements end to end. Within a phase they
finish the first requirement's tasks, then the second's, so phases run as
windows across the version. IT, ST and OT start on version-wide dates. Rows
carry the team's true work where it differs from the estimate; the fixture
simulates the weeks from the project start with the real scheduler.
"""
import argparse
import json
from pathlib import Path

PHASES = [
    ("SA", [("現行業務の調査", 24), ("要求事項の整理", 16), ("要求仕様書のレビュー", 8)]),
    ("UI", [("画面・帳票の設計", 32), ("外部インタフェースの設計", 16), ("外部設計書のレビュー", 8)]),
    ("SS", [("方式の検討", 24), ("基本設計書の作成", 24), ("基本設計書のレビュー", 8)]),
    ("PS", [("詳細設計 処理", 32), ("詳細設計 データ", 24), ("詳細設計書のレビュー", 8)]),
    ("PG", [("製造 画面", 40), ("製造 バッチ", 32), ("製造 共通部品", 24), ("コードレビュー", 8)]),
    ("PT", [("単体テスト仕様書の作成", 24), ("単体テストの実施", 32), ("単体テスト結果のレビュー", 8)]),
    ("IT", [("結合テスト仕様書の作成", 24), ("結合テストの実施", 32), ("結合テスト結果のレビュー", 8)]),
    ("ST", [("総合テストの実施", 24), ("総合テスト結果のレビュー", 8)]),
    ("OT", [("運用テストの実施", 16)]),
]
# UI and SS share one window: each person designs a requirement's screens, then its architecture.
WINDOWS = [["SA"], ["UI", "SS"], ["PS"], ["PG"], ["PT"], ["IT"], ["ST"], ["OT"]]
SUBJECTS = ["受注", "出荷", "請求", "在庫", "顧客", "仕入", "売上", "入金", "支払", "契約"]
ACTIONS = ["データの外部連携", "画面の改善", "帳票の追加", "締め処理の見直し"]
EVALUATION = Path(__file__).resolve().parents[2] / "evaluation"
DEFAULT_PATH = EVALUATION / "version-plan.json"
LONG_PATH = EVALUATION / "version-plan-12m.json"

# Gates and milestones follow the baseline schedule the fixture test reports: a milestone is the
# Friday of the week its phase is planned to finish.
VERSIONS = {
    "2027.04": dict(title="第2027.04版", scale=1.0, projectStart="2026-10-13", projectEnd="2027-04-30",
                    gates=dict(IT="2027-03-15", ST="2027-04-08", OT="2027-04-21"),
                    statusDates=["2026-12-16", "2027-01-13", "2027-02-17"], statusDate="2027-01-13",
                    milestones=dict(SA="2026-10-30", UI="2026-12-04", SS="2026-12-11", PS="2027-01-08", PG="2027-02-19",
                                    PT="2027-03-12", IT="2027-04-09", ST="2027-04-23", OT="2027-04-30")),
    "2027.10": dict(title="第2027.10版", scale=1.85, projectStart="2026-10-13", projectEnd="2027-10-29",
                    gates=dict(IT="2027-07-26", ST="2027-09-08", OT="2027-10-04"),
                    statusDates=["2027-02-03", "2027-03-24", "2027-06-02"], statusDate="2027-03-24",
                    milestones=dict(SA="2026-11-13", UI="2027-01-15", SS="2027-02-05", PS="2027-03-19", PG="2027-06-04",
                                    PT="2027-07-16", IT="2027-09-10", ST="2027-10-01", OT="2027-10-15")),
}
PERSON_FACTORS = [0.94, 0.97, 1.0, 1.03]
SPLITS = [0.1, -0.05, 0.0, 0.08, -0.1]
OVERRUN_PEOPLE = {3, 4, 5, 6, 7, 8}   # PS takes half as long again; the phase finishes late.
EARLY_PEOPLE = {15, 16}               # Early design phases finish with a quarter of the estimate unused.
SHARED_PARTS = range(2, 7)            # R02–R06 製造 共通部品 go to U7 on top of U7's own work.
OPEN_AT_ZERO = "R09-SS-003"           # Remaining set to 0, Issue left open.


def generate(version="2027.04"):
    spec = VERSIONS[version]
    tasks = {}
    rows = []
    for requirement in range(1, 41):
        parent = f"R{requirement:02d}"
        person = (requirement - 1) % 20 + 1
        split = SPLITS[person % 5] * (1 if requirement <= 20 else -1)
        factor = spec["scale"] * PERSON_FACTORS[(person - 1) % 4] * (1 + split)
        rows.append(dict(key=parent, title=f"{parent} {SUBJECTS[(requirement - 1) % 10]}{ACTIONS[(requirement - 1) // 10]}",
                         parent=None, phase=None, estimate=None, person=None, predecessors=[], start=None))
        for phase, phase_tasks in PHASES:
            for number, (title, hours) in enumerate(phase_tasks, 1):
                key = f"{parent}-{phase}-{number:03d}"
                estimate = max(4, round(hours * factor))
                row = dict(key=key, title=f"{parent} {phase}-{number:03d} {title}", parent=parent, phase=phase,
                           estimate=estimate, person=f"U{person}", predecessors=[], start=None)
                if phase == "PS" and person in OVERRUN_PEOPLE:
                    row["work"] = round(estimate * 1.5)
                elif phase in ("SA", "UI", "SS") and person in EARLY_PEOPLE:
                    row["work"] = round(estimate * 0.75)
                if phase == "PG" and number == 3 and requirement in SHARED_PARTS:
                    row["person"] = "U7"
                if key == OPEN_AT_ZERO:
                    row["openAtZero"] = True
                tasks[key] = row
                rows.append(row)
    for person in range(1, 21):
        previous = None
        for window in WINDOWS:
            for requirement in (person, person + 20):
                for phase in window:
                    for number in range(1, len(dict(PHASES)[phase]) + 1):
                        row = tasks[f"R{requirement:02d}-{phase}-{number:03d}"]
                        if previous:
                            row["predecessors"] = [previous]
                        elif phase == "SA":
                            row["start"] = spec["projectStart"]
                        if requirement == person and number == 1 and phase in spec["gates"]:
                            row["start"] = spec["gates"][phase]
                        previous = row["key"]
    return dict(title=spec["title"], projectStart=spec["projectStart"], projectEnd=spec["projectEnd"],
                statusDate=spec["statusDate"], statusDates=spec["statusDates"],
                phases=[phase for phase, _ in PHASES],
                milestones=[dict(phase=phase, title=f"{phase} 完了", due=due) for phase, due in spec["milestones"].items()],
                people=[dict(identity=f"U{i}", name=f"person-U{i}", rate=100, allowance=None) for i in range(1, 21)],
                rows=rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Verify the checked-in manifests without writing.")
    args = parser.parse_args()
    for version, path in (("2027.04", DEFAULT_PATH), ("2027.10", LONG_PATH)):
        expected = generate(version)
        if args.check:
            if json.loads(path.read_text(encoding="utf-8")) != expected:
                raise SystemExit(f"{path.name} differs from its generator.")
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(expected, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"{expected['title']}: {len(expected['rows'])} Issues, 40 requirements, 1,000 tasks, 20 people")


if __name__ == "__main__":
    main()
