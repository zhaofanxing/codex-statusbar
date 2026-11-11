#!/usr/bin/env python3
"""
Independent verification of the three primary metrics against raw Codex data.

This script deliberately shares NO code with the C# monitor. It re-derives the three
numbers straight from the rollout JSONL so the monitor's output can be checked by hand.

Definitions implemented here (and nothing else):
    conversation_total_tokens = SUM(input_tokens) + SUM(output_tokens)
    cache_hit_rate            = SUM(cached_input_tokens) / SUM(input_tokens) * 100
    turn_tps                  = paired output_tokens / paired generation_ms * 1000

Usage:
    python tools/verify_metrics.py [thread-id ...]
Without arguments it verifies the most recently updated desktop threads.
"""
import json
import os
import sqlite3
import sys
from datetime import datetime, timedelta, timezone

MODEL_ITEM_TYPES = {"Reasoning", "AgentMessage"}
NON_MODEL_ITEM_TYPES = {
    "UserMessage", "CommandExecution", "FileChange", "McpToolCall",
    "WebSearch", "ImageView", "Compact", "Sleep",
}
EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


def timestamp_ms(record):
    try:
        value = datetime.fromisoformat(record["timestamp"].replace("Z", "+00:00"))
        return (value - EPOCH) // timedelta(milliseconds=1)
    except (AttributeError, KeyError, TypeError, ValueError):
        return None


def derive_response_totals(records):
    """Replay response boundaries independently of the C# parser."""
    turns = {}
    turn = None
    seen = set()
    output = 0
    high_water = 0
    boundary = 0
    legacy = None
    windows = []
    calls = []
    result_ms = None

    def clear_response(end):
        nonlocal boundary, result_ms
        if end is not None:
            boundary = max(boundary, end)
        windows.clear()
        calls.clear()
        result_ms = None

    def skip_legacy():
        nonlocal legacy
        if legacy is not None:
            turns[turn]["observed"] += 1
            known_ends = [e for _, e in windows + calls if e is not None]
            clear_response(legacy[1] if legacy[1] is not None else max(known_ends, default=boundary))
            legacy = None

    for record in records:
        rtype = record.get("type")
        payload = record.get("payload") or {}
        event = payload.get("type") if rtype == "event_msg" else None
        if event == "task_started" and payload.get("turn_id") and payload["turn_id"] != turn:
            turn = payload["turn_id"]
            turns[turn] = {"tokens": 0, "ms": 0, "timed": 0, "observed": 0}
            seen.clear()
            boundary, high_water, legacy = 0, output, None
            clear_response(None)

        cumulative = None
        last = None
        if rtype == "token_usage_record":
            cumulative = (payload.get("thread_token_usage") or {}).get("output_tokens")
        elif event == "token_count":
            info = payload.get("info") or {}
            cumulative = (info.get("total_token_usage") or {}).get("output_tokens")
            last = (info.get("last_token_usage") or {}).get("output_tokens")
        if cumulative is not None:
            output = cumulative
        if turn is None or payload.get("turn_id") not in (None, turn):
            continue

        summary = turns[turn]
        if event in ("task_complete", "turn_aborted"):
            skip_legacy()
            if windows or calls:
                summary["observed"] += 1
            turn = None
        elif event == "item_completed" and (payload.get("item") or {}).get("type") in MODEL_ITEM_TYPES:
            start, end = payload.get("started_at_ms"), payload.get("completed_at_ms")
            if end is not None and end <= boundary:
                continue
            if legacy and (legacy[1] is None or start is not None and start >= legacy[1]):
                skip_legacy()
            if result_ms is not None and start is not None and start >= result_ms:
                summary["observed"] += 1
                crosses_result = any(e is not None and e > result_ms for _, e in windows + calls)
                clear_response(result_ms)
                if crosses_result:
                    windows.append((None, None))
            windows.append((start, end))
        elif rtype == "response_item":
            kind, call_id = payload.get("type") or "", payload.get("call_id")
            end = timestamp_ms(record)
            if kind.endswith("_call") and (end is None or end > boundary):
                calls.append((call_id, end))
            elif kind.endswith("_call_output") and end is not None:
                if any(at is not None and at <= end and (call_id is None or cid == call_id) for cid, at in calls):
                    result_ms = end if result_ms is None else min(result_ms, end)
        elif event == "token_count" and cumulative is not None and cumulative > high_water and last and last > 0:
            skip_legacy()
            high_water = cumulative
            legacy = (cumulative, timestamp_ms(record))
        elif rtype == "token_usage_record" and payload.get("turn_id") == turn:
            response = payload.get("response_id")
            if response and response in seen:
                continue
            if response:
                seen.add(response)
            summary["observed"] += 1
            end = timestamp_ms(record)
            start = min((s for s, _ in windows if s is not None and s > 0), default=None)
            if end is not None and (end < boundary or start is not None and end < start):
                continue
            if legacy and cumulative != legacy[0]:
                skip_legacy()
                start = None
            legacy = None
            high_water = max(high_water, cumulative or 0)
            tokens = (payload.get("usage") or {}).get("output_tokens", 0)
            if response and tokens > 0 and start is not None and end is not None and end > start:
                duration = end - start
                if (all(s is not None and e is not None and max(1, boundary) <= s <= e <= end for s, e in windows)
                        and all(e is not None and start <= e <= end
                                and (result_ms is None or e <= result_ms) for _, e in calls)
                        and 1000 * tokens / duration <= 100_000):
                    summary["tokens"] += tokens
                    summary["ms"] += duration
                    summary["timed"] += 1
            known_ends = [e for _, e in windows + calls if e is not None]
            clear_response(end if end is not None else max(known_ends, default=boundary))
    return turns


def read_records(path):
    with open(path, "r", encoding="utf-8", errors="replace") as stream:
        for line in stream:
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                continue


def codex_home() -> str:
    return os.environ.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex")


def find_rollout(home: str, thread_id: str) -> str | None:
    root = os.path.join(home, "sessions")
    hit = None
    for dp, _dn, fn in os.walk(root):
        for f in fn:
            if f.endswith(".jsonl") and thread_id in f:
                p = os.path.join(dp, f)
                if hit is None or os.path.getmtime(p) > os.path.getmtime(hit):
                    hit = p
    return hit


def recent_desktop_threads(home: str, limit: int) -> list[str]:
    """Most recently viewed threads, read-only, from state_5.sqlite."""
    p = os.path.join(home, "state_5.sqlite")
    if not os.path.exists(p):
        return []
    con = sqlite3.connect(f"file:{p}?mode=ro", uri=True)
    try:
        rows = con.execute(
            "SELECT id, originator, cli_version, tokens_used, recency_at_ms, title "
            "FROM threads WHERE archived = 0 AND originator = 'Codex Desktop' "
            "ORDER BY recency_at_ms DESC LIMIT ?", (limit,)
        ).fetchall()
    finally:
        con.close()
    for r in rows:
        print(f"  candidate {r[0]}  cli={r[2]}  db_tokens_used={r[3]:,}  title={(r[5] or '')[:40]}")
    return [r[0] for r in rows]


def analyze(path: str):
    """Walk the rollout in file order and print the raw + derived numbers."""
    print("=" * 96)
    print("ROLLOUT:", path)
    print(f"size: {os.path.getsize(path)/1048576:.2f} MB")
    print("=" * 96)

    # ---- official cumulative usage: trust the LAST token_usage_record ----------
    thread_usage = None          # last thread_token_usage seen
    per_response = []            # (response_id, output_tokens, reasoning, input, cached)
    turns = {}                   # turn_id -> dict
    order = []
    first_line_meta = None
    item_types_seen = {}

    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for lineno, line in enumerate(fh):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if lineno == 0:
                first_line_meta = rec
            rtype = rec.get("type")
            pl = rec.get("payload") or {}

            if rtype == "token_usage_record":
                u = pl.get("usage") or {}
                thread_usage = pl.get("thread_token_usage") or thread_usage
                per_response.append((
                    (pl.get("response_id") or "")[-8:],
                    u.get("input_tokens"), u.get("cached_input_tokens"),
                    u.get("output_tokens"), u.get("reasoning_output_tokens"),
                ))
                tid = pl.get("turn_id")
                t = turns.setdefault(tid, {})
                if tid not in order:
                    order.append(tid)
                continue

            if rtype == "event_msg" and pl.get("type") == "item_completed":
                tid = pl.get("turn_id")
                t = turns.setdefault(tid, {})
                if tid not in order:
                    order.append(tid)
                item = pl.get("item") or {}
                itype = item.get("type")
                item_types_seen[itype] = item_types_seen.get(itype, 0) + 1
                s, c = pl.get("started_at_ms"), pl.get("completed_at_ms")
                dur = (c - s) if (s is not None and c is not None) else None
                t.setdefault("all_items", []).append((itype, dur))
                continue

            if rtype == "event_msg" and pl.get("type") == "task_complete":
                tid = pl.get("turn_id")
                t = turns.setdefault(tid, {})
                t["duration_ms"] = pl.get("duration_ms")
                t["ttft_ms"] = pl.get("time_to_first_token_ms")
                t["status"] = "completed"
                continue

    # ---- report ---------------------------------------------------------------
    meta = (first_line_meta or {}).get("payload") or {}
    print(f"thread_id     : {meta.get('id')}")
    print(f"originator    : {meta.get('originator')}   source={meta.get('source')}  cli={meta.get('cli_version')}")
    print(f"cwd           : {meta.get('cwd')}")
    print(f"item types    : {item_types_seen}")
    print()

    if not thread_usage:
        print("!! no thread_token_usage found in this rollout")
        return
    inp = thread_usage.get("input_tokens") or 0
    cached = thread_usage.get("cached_input_tokens") or 0
    out = thread_usage.get("output_tokens") or 0
    reas = thread_usage.get("reasoning_output_tokens") or 0
    total = thread_usage.get("total_tokens") or 0

    print("--- RAW (official, cumulative for this conversation) ---")
    print(f"thread_token_usage.input_tokens            = {inp:,}")
    print(f"thread_token_usage.cached_input_tokens     = {cached:,}")
    print(f"thread_token_usage.output_tokens           = {out:,}")
    print(f"thread_token_usage.reasoning_output_tokens = {reas:,}")
    print(f"thread_token_usage.total_tokens            = {total:,}")
    print()
    print("--- DERIVED (what the status bar must show) ---")
    print(f"total = input + output                     = {inp:,} + {out:,} = {inp + out:,}")
    print(f"  .. equals official total_tokens?         = {inp + out == total}")
    print(f"  .. WRONG formula input+cached+output     = {inp + cached + out:,}  (must NOT be used)")
    print(f"uncached = input - cached                  = {inp - cached:,}")
    if inp > 0:
        print(f"cache_hit_rate = cached/input*100          = {cached / inp * 100:.4f}%  -> display {cached / inp * 100:.0f}%")
    else:
        print("cache_hit_rate                             = -- (input == 0, must not be NaN)")
    grand = inp + out
    shown = (f"{grand / 1_000_000:.1f}M" if grand >= 1_000_000 else
             (f"{grand / 1000:.1f}K" if grand >= 1000 else str(grand)))
    print(f"display total                              = {grand:,} -> {shown} tok")
    print()
    print(f"--- PER-RESPONSE official usage (last {min(8, len(per_response))}) ---")
    print(f"{'resp':>9} {'input':>10} {'cached':>10} {'output':>8} {'reasoning':>10}")
    for r in per_response[-8:]:
        print(f"{r[0]:>9} {r[1]:>10,} {r[2]:>10,} {r[3]:>8,} {r[4]:>10,}")
    print()
    timings = derive_response_totals(read_records(path))
    print("--- PER-TURN TPS (paired response output tokens / generation ms) ---")
    print(f"{'turn':>10} {'output':>8} {'gen_ms':>9} {'tps':>8} {'timed':>7} {'turn_ms':>9} {'ttft_ms':>8}")
    for tid in order:
        t = turns[tid]
        timing = timings.get(tid, {"tokens": 0, "ms": 0, "timed": 0, "observed": 0})
        ms, tokens = timing["ms"], timing["tokens"]
        rate = f"{tokens / ms * 1000:.1f}" if ms else "--"
        coverage = f"{timing['timed']}/{timing['observed']}"
        print(f"{(tid or '')[-8:]:>10} {tokens:>8,} {ms:>9,} {rate:>8} {coverage:>7} "
              f"{t.get('duration_ms', ''):>9} {t.get('ttft_ms', ''):>8}")
    print()
    non_model_ms = 0
    for t in turns.values():
        for itype, dur in t.get("all_items", []):
            if itype in NON_MODEL_ITEM_TYPES and dur:
                non_model_ms += dur
    print(f"sum of tool/IO/user item durations across all turns = {non_model_ms:,} ms")


def main():
    home = codex_home()
    print("CODEX_HOME =", home)
    ids = sys.argv[1:]
    if not ids:
        print("\nrecent Codex Desktop threads in state_5.sqlite:")
        ids = recent_desktop_threads(home, 3)[:2]
    for tid in ids:
        p = find_rollout(home, tid)
        if not p:
            print(f"!! no rollout file for {tid}")
            continue
        analyze(p)


if __name__ == "__main__":
    main()
