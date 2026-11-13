"""Map herdr panes to Claude Code peer-messaging names.

Joins `herdr pane list` (agent_session.value) with the Claude Code session
registry (~/.claude/sessions/<pid>.json: sessionId -> name), so you can tell
which SendMessage address belongs to which herdr pane.

Usage:
    python -X utf8 scripts/claude_peers.py            # table of all panes
    python -X utf8 scripts/claude_peers.py --json     # machine-readable
    python -X utf8 scripts/claude_peers.py w7:p2      # one pane -> name
"""
import argparse
import glob
import json
import os
import subprocess
import sys


def claude_sessions():
    """Return {sessionId: registry entry} for live Claude Code sessions."""
    out = {}
    pattern = os.path.join(os.path.expanduser("~"), ".claude", "sessions", "*.json")
    for path in glob.glob(pattern):
        try:
            with open(path, encoding="utf-8") as f:
                d = json.load(f)
        except (OSError, ValueError):
            continue
        sid = d.get("sessionId")
        if sid:
            out[sid] = {
                "name": d.get("name"),
                "status": d.get("status"),
                "pid": d.get("pid"),
                "cwd": d.get("cwd"),
            }
    return out


def herdr_panes():
    herdr = os.environ.get("HERDR_BIN_PATH", "herdr")
    res = subprocess.run([herdr, "pane", "list"], capture_output=True,
                         text=True, encoding="utf-8")
    if res.returncode != 0:
        sys.exit(f"herdr pane list failed: {res.stderr.strip()}")
    return json.loads(res.stdout)["result"]["panes"]


def build_rows():
    sessions = claude_sessions()
    rows = []
    for p in herdr_panes():
        sess = p.get("agent_session") or {}
        peer = sessions.get(sess.get("value")) if p.get("agent") == "claude" else None
        rows.append({
            "pane": p["pane_id"],
            "agent": p.get("agent", "-"),
            "status": p.get("agent_status"),
            "title": p.get("terminal_title_stripped", ""),
            "peer_name": peer["name"] if peer else None,
            "cwd": p.get("cwd"),
        })
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("pane", nargs="?", help="herdr pane id (e.g. w7:p2); prints only its peer name")
    ap.add_argument("--json", action="store_true", help="print JSON instead of a table")
    args = ap.parse_args()

    rows = build_rows()
    if args.pane:
        match = [r for r in rows if r["pane"] == args.pane]
        if not match or not match[0]["peer_name"]:
            sys.exit(f"no Claude peer found for pane {args.pane}")
        print(match[0]["peer_name"])
        return
    if args.json:
        print(json.dumps(rows, ensure_ascii=False, indent=2))
        return
    me = os.environ.get("HERDR_PANE_ID")
    for r in rows:
        mark = "*" if r["pane"] == me else " "
        print(f"{mark}{r['pane']:<7} {r['agent']:<7} {r['peer_name'] or '-':<42} {r['title'][:50]}")


if __name__ == "__main__":
    main()
