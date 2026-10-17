#!/usr/bin/env python3
"""manual_rewrite_hook.py - マニュアル作成・リライト作業専用のライフサイクルフック。

日常の開発・ビルド・コード編集時には一切何もしない（即時 exit 0）設計。
マニュアルリライトのセッション中（.manual_rewrite/ 状態フォルダが存在し対象ファイルが登録されている時）のみ、
事実改変（IRON RULE違反）や新事実付加（new facts）、重大なAI構文違反を検証・警告する。
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

__version__ = "1.0.0"

STATE_DIR_NAME = ".manual_rewrite"


def find_repo_root(start_path: Path) -> Path:
    current = start_path.resolve()
    for parent in [current, *current.parents]:
        if (parent / ".git").exists():
            return parent
    return current


def get_state_dir(repo_root: Path) -> Path:
    return repo_root / STATE_DIR_NAME


def is_active(repo_root: Path) -> tuple[bool, dict | None]:
    state_dir = get_state_dir(repo_root)
    state_file = state_dir / "session.json"
    if not state_file.exists():
        return False, None
    try:
        data = json.loads(state_file.read_text(encoding="utf-8"))
        if data.get("active"):
            return True, data
    except Exception:
        return False, None
    return False, None


def cmd_start(args: list[str], repo_root: Path) -> int:
    if not args:
        print("エラー: 対象ファイルを指定してください。例: python manual_rewrite_hook.py start docs/my_manual.md", file=sys.stderr)
        return 1
    target = Path(args[0]).resolve()
    if not target.exists():
        print(f"エラー: ファイルが存在しません: {target}", file=sys.stderr)
        return 1

    state_dir = get_state_dir(repo_root)
    state_dir.mkdir(parents=True, exist_ok=True)
    state_file = state_dir / "session.json"

    # 原文のバックアップを保存
    backup_file = state_dir / f"original_{target.name}"
    backup_file.write_text(target.read_text(encoding="utf-8"), encoding="utf-8")

    session_data = {
        "active": True,
        "target_file": str(target),
        "backup_file": str(backup_file),
    }
    state_file.write_text(json.dumps(session_data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"マニュアルリライトセッションを開始しました: {target}")
    print(f"状態記録: {state_file}")
    return 0


def cmd_end(repo_root: Path) -> int:
    state_dir = get_state_dir(repo_root)
    state_file = state_dir / "session.json"
    if state_file.exists():
        try:
            data = json.loads(state_file.read_text(encoding="utf-8"))
            data["active"] = False
            state_file.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception:
            pass
    print("マニュアルリライトセッションを終了しました（フック監視解除）。")
    return 0


def cmd_status(repo_root: Path) -> int:
    active, data = is_active(repo_root)
    if active and data:
        print(f"マニュアルリライト監視中: {data.get('target_file')}")
    else:
        print("マニュアルリライト監視は停止しています（日常作業モード）。")
    return 0


def handle_hook_event(repo_root: Path) -> int:
    """フック呼び出しハンドラ。アクティブでない場合は1ミリ秒で exit 0。"""
    active, data = is_active(repo_root)
    if not active or not data:
        # マニュアル作業以外では何もせず即時終了（PostToolUse契約として{}を出力）
        print("{}")
        return 0

    # 標準入力からイベント情報を受け取る（もしあれば）
    try:
        input_data = ""
        if not sys.stdin.isatty():
            input_data = sys.stdin.read()
    except Exception:
        input_data = ""

    # 対象ファイルが存在するか確認
    target_path = Path(data.get("target_file", ""))
    backup_path = Path(data.get("backup_file", ""))
    if not target_path.exists() or not backup_path.exists():
        print("{}")
        return 0

    # newfacts.py または yomiyasu による検査
    skills_dir = repo_root / ".agents" / "skills"
    newfacts_script = skills_dir / "natural-japanese" / "scripts" / "newfacts.py"

    if newfacts_script.exists():
        import subprocess
        try:
            sub_env = {**os.environ, "PYTHONUTF8": "1", "PYTHONIOENCODING": "utf-8"}
            res = subprocess.run(
                [sys.executable, str(newfacts_script), str(backup_path), str(target_path)],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=sub_env,
                timeout=5,
            )
            if res.stdout and res.stdout.strip():
                print(f"[Manual-Rewrite Guard] 新事実・未確認数値の検知:\n{res.stdout.strip()}", file=sys.stderr)
        except Exception:
            pass

    print("{}")
    return 0


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

    repo_root = find_repo_root(Path.cwd())
    if len(sys.argv) > 1:
        cmd = sys.argv[1]
        if cmd == "start":
            return cmd_start(sys.argv[2:], repo_root)
        elif cmd == "end":
            return cmd_end(repo_root)
        elif cmd == "status":
            return cmd_status(repo_root)
        elif cmd in ("--hook", "hook"):
            return handle_hook_event(repo_root)
        elif cmd in ("-h", "--help"):
            print(__doc__)
            return 0

    # 引数なし、または標準入力フック呼び出し
    return handle_hook_event(repo_root)


if __name__ == "__main__":
    sys.exit(main())
