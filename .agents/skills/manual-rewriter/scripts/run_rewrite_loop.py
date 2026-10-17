#!/usr/bin/env python3
"""run_rewrite_loop.py - マニュアルリライト品質保証ループ（機械的チェック統合ツール）。

yomiyasu_lint（マニュアルモード）、natural-japanese lint、newfacts（新事実検証）を一括実行し、
指摘件数・未解決項目をレポートする。
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path


def find_repo_root(start_path: Path) -> Path:
    current = start_path.resolve()
    for parent in [current, *current.parents]:
        if (parent / ".git").exists():
            return parent
    return current


def run_checks(original_path: Path, draft_path: Path, repo_root: Path, doc_type: str = "manual") -> dict:
    skills_dir = repo_root / ".agents" / "skills"
    yomiyasu_lint_py = skills_dir / "yomiyasu" / "scripts" / "yomiyasu_lint.py"
    natural_lint_py = skills_dir / "natural-japanese" / "scripts" / "lint.py"
    newfacts_py = skills_dir / "natural-japanese" / "scripts" / "newfacts.py"

    report = {
        "original": str(original_path),
        "draft": str(draft_path),
        "doc_type": doc_type,
        "yomiyasu_findings": [],
        "natural_japanese_findings": [],
        "new_facts": [],
        "all_clear": False,
    }

    sub_env = {**os.environ, "PYTHONUTF8": "1", "PYTHONIOENCODING": "utf-8"}

    # 1. Yomiyasu Lint
    if yomiyasu_lint_py.exists():
        cmd = [
            sys.executable,
            str(yomiyasu_lint_py),
            str(draft_path),
            "--json",
            f"--doc-type={doc_type}",
            "--reader=informed",
        ]
        res = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=sub_env)
        if res.stdout and res.stdout.strip():
            try:
                yomiyasu_data = json.loads(res.stdout)
                candidates = yomiyasu_data.get("candidates", [])
                report["yomiyasu_findings"] = candidates
                report["yomiyasu_score"] = yomiyasu_data.get("rule_penalty_score", 100)
            except Exception as e:
                report["yomiyasu_error"] = str(e)

    # 2. Natural Japanese Lint
    if natural_lint_py.exists():
        cmd = [
            sys.executable,
            str(natural_lint_py),
            str(draft_path),
            "--json",
            "--genre",
            "tech",
        ]
        res = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=sub_env)
        if res.stdout and res.stdout.strip():
            try:
                nat_data = json.loads(res.stdout)
                report["natural_japanese_findings"] = nat_data.get("findings", [])
            except Exception as e:
                report["natural_japanese_error"] = str(e)

    # 3. New Facts Check
    if newfacts_py.exists():
        cmd = [
            sys.executable,
            str(newfacts_py),
            str(original_path),
            str(draft_path),
            "--json",
        ]
        res = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", env=sub_env)
        if res.stdout and res.stdout.strip():
            try:
                nf_data = json.loads(res.stdout)
                report["new_facts"] = nf_data.get("new_items", [])
            except Exception as e:
                report["new_facts_error"] = str(e)

    # All Clear 判定:
    # yomiyasu の warn/error が 0件、natural-japanese の warn/critical が 0件、new_facts が 0件
    yomi_warns = [f for f in report["yomiyasu_findings"] if f.get("severity") in ("warn", "error")]
    nat_warns = [f for f in report["natural_japanese_findings"] if f.get("severity") in ("warn", "critical")]
    new_facts = report["new_facts"]

    report["remaining_issues_count"] = len(yomi_warns) + len(nat_warns) + len(new_facts)
    report["all_clear"] = (report["remaining_issues_count"] == 0)

    return report


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="マニュアルリライト品質保証チェック")
    parser.add_argument("original", help="原文ファイルパス")
    parser.add_argument("draft", help="改定案ファイルパス")
    parser.add_argument("--doc-type", default="manual", help="文書種別（既定: manual）")
    parser.add_argument("--json", action="store_true", help="JSON形式で出力")
    args = parser.parse_args()

    original_path = Path(args.original).resolve()
    draft_path = Path(args.draft).resolve()
    repo_root = find_repo_root(original_path)

    if not original_path.exists():
        print(f"エラー: 原文ファイルが見つかりません: {original_path}", file=sys.stderr)
        return 2
    if not draft_path.exists():
        print(f"エラー: 改定案ファイルが見つかりません: {draft_path}", file=sys.stderr)
        return 2

    report = run_checks(original_path, draft_path, repo_root, doc_type=args.doc_type)

    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print("=== 品質保証ループ（チェック結果） ===")
        print(f"原文: {original_path}")
        print(f"改定案: {draft_path}")
        print(f"未解決の指摘件数 (Warn/Critical/NewFacts): {report['remaining_issues_count']}")
        print(f"品質基準クリア (All Clear): {'[PASS] 指摘ゼロ' if report['all_clear'] else '[FAIL] 未解消の指摘あり'}")

        if report["new_facts"]:
            print("\n[新事実・無断付加の疑い (new facts)]:")
            for item in report["new_facts"]:
                word = item.get("word") if isinstance(item, dict) else item
                print(f"  - {word}")

        yomi_warns = [f for f in report["yomiyasu_findings"] if f.get("severity") in ("warn", "error")]
        if yomi_warns:
            print(f"\n[yomiyasu 指摘 ({len(yomi_warns)}件)]:")
            for f in yomi_warns:
                print(f"  - L{f.get('line')}: [{f.get('rule')}] {f.get('message', f.get('hint', ''))}")

        nat_warns = [f for f in report["natural_japanese_findings"] if f.get("severity") in ("warn", "critical")]
        if nat_warns:
            print(f"\n[natural-japanese 指摘 ({len(nat_warns)}件)]:")
            for f in nat_warns:
                print(f"  - L{f.get('line')}: [{f.get('category')}] {f.get('detail', f.get('excerpt', ''))}")

    return 0 if report["all_clear"] else 1


if __name__ == "__main__":
    sys.exit(main())
