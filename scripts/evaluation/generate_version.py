"""Generate the shared, synthetic 第2027.04版 WBS; never contacts GitHub."""
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
SUBJECTS = ["受注", "出荷", "請求", "在庫", "顧客", "仕入", "売上", "入金", "支払", "契約"]
ACTIONS = ["データの外部連携", "画面の改善", "帳票の追加", "締め処理の見直し"]
DEFAULT_PATH = Path(__file__).resolve().parents[2] / "evaluation" / "version-plan.json"


def generate():
    rows = []
    for requirement in range(1, 41):
        parent = f"R{requirement:02d}"
        rows.append(dict(key=parent, title=f"{parent} {SUBJECTS[(requirement - 1) % 10]}{ACTIONS[(requirement - 1) // 10]}",
                         parent=None, phase=None, estimate=None, person=None, predecessors=[], start=None))
        second_wave = requirement > 20
        gates = {"SA": "2027-01-18" if second_wave else "2026-10-13",
                 "IT": "2027-03-15" if second_wave else "2026-12-07",
                 "ST": "2027-04-05" if second_wave else "2027-01-04",
                 "OT": "2027-04-12" if second_wave else "2027-01-12"}
        previous = None
        for phase, tasks in PHASES:
            for number, (title, hours) in enumerate(tasks, 1):
                key = f"{parent}-{phase}-{number:03d}"
                predecessors = [previous] if previous else []
                start = gates.get(phase) if number == 1 else None
                # Parallel analysis tasks produce a one-day spike hidden by the week's idle Tuesday.
                if requirement == 1 and phase == "SA" and number in (1, 2):
                    hours, predecessors, start = 8, [], "2026-10-14"
                elif requirement == 1 and phase == "SA" and number == 3:
                    predecessors = ["R01-SA-001", "R01-SA-002"]
                rows.append(dict(key=key, title=f"{parent} {phase}-{number:03d} {title}", parent=parent,
                                 phase=phase, estimate=hours, person=f"U{(requirement - 1) % 20 + 1}",
                                 predecessors=predecessors, start=start))
                previous = key
    return dict(title="第2027.04版", statusDate="2026-10-05", projectStart="2026-10-13", shippingDate="2027-04-30",
                people=[dict(identity=f"U{i}", name=f"person-U{i}", rate=100, allowance=900 if i == 2 else 1040)
                        for i in range(1, 21)], rows=rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_PATH)
    parser.add_argument("--check", action="store_true", help="Verify the checked-in manifest without writing.")
    args = parser.parse_args()
    expected = generate()
    if args.check:
        if json.loads(args.output.read_text(encoding="utf-8")) != expected:
            raise SystemExit("The version manifest differs from its generator.")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(expected, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(expected['rows'])} Issues: 40 requirements, 1,000 tasks, 20 people")


if __name__ == "__main__":
    main()
