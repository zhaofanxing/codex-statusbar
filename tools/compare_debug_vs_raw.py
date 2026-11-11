#!/usr/bin/env python3
r"""
Cross-check the monitor's own debug output against an independent re-derivation
from the raw rollout file.

This is the acceptance check: it does not trust the monitor and it does not trust the
monitor's parser. It reads what the monitor *printed* and what the rollout *says*, and
compares them field by field.

Usage:
    python tools/compare_debug_vs_raw.py                 # uses LOCALAPPDATA\CodexStatusbar\debug.log
    python tools/compare_debug_vs_raw.py --log <path>
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from verify_metrics import codex_home, derive_response_totals, find_rollout, read_records  # noqa: E402


def parse_debug_log(path):
    """Return the LAST diagnostics block in the log as a dict."""
    if not os.path.exists(path):
        print(f"!! debug log not found: {path}")
        return None
    text = open(path, encoding="utf-8", errors="replace").read()
    # blocks start with "Current thread:"
    starts = [m.start() for m in re.finditer(r"^Current thread:", text, re.M)]
    if not starts:
        print(f"!! no diagnostics block found in {path}")
        return None
    block = text[starts[-1]:]
    out = {}
    for line in block.splitlines():
        if ":" not in line:
            continue
        k, _, v = line.partition(":")
        out[k.strip()] = v.strip()
    return out


def as_int(d, key):
    v = d.get(key, "")
    m = re.search(r"-?\d+", v.replace(",", ""))
    return int(m.group()) if m else None


def as_float(d, key):
    v = d.get(key, "")
    if v in ("--", "", "None"):
        return None
    m = re.search(r"-?\d+(?:\.\d+)?", v)
    return float(m.group()) if m else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=os.path.join(
        os.environ.get("LOCALAPPDATA", ""), "CodexStatusbar", "debug.log"))
    args = ap.parse_args()

    d = parse_debug_log(args.log)
    if not d:
        return 1

    print("=== what the monitor printed ===")
    for k, v in d.items():
        print(f"    {k:<22} {v}")

    thread = d.get("Current thread", "").strip()
    home = codex_home()
    path = find_rollout(home, thread) if thread else None
    if not path:
        print(f"\n!! could not locate the rollout file for thread {thread!r}")
        return 1

    print(f"\n=== independent re-derivation from ===\n    {path}\n")
    recs = list(read_records(path))
    inp = cached = out = total = None
    for r in recs:
        pl = r.get("payload") or {}
        if r.get("type") == "token_usage_record" and pl.get("thread_token_usage"):
            tu = pl["thread_token_usage"]
            inp, cached, out, total = (tu.get("input_tokens"), tu.get("cached_input_tokens"),
                                       tu.get("output_tokens"), tu.get("total_tokens"))
        elif r.get("type") == "event_msg" and pl.get("type") == "token_count":
            tu = (pl.get("info") or {}).get("total_token_usage") or {}
            if tu:
                inp, cached, out, total = (tu.get("input_tokens"), tu.get("cached_input_tokens"),
                                           tu.get("output_tokens"), tu.get("total_tokens"))
    if inp is None:
        print("!! no usage record found in the rollout")
        return 1

    checks = [
        ("Input", as_int(d, "Input"), inp),
        ("Cached", as_int(d, "Cached"), cached),
        ("Output", as_int(d, "Output"), out),
        ("Total", as_int(d, "Total"), inp + out),
    ]
    shown_cache = as_float(d, "Cache hit")
    want_cache = (cached / inp * 100) if inp else None

    print(f"{'field':<28} {'monitor':>14} {'raw-derived':>14}   verdict")
    bad = 0
    for name, got, want in checks:
        ok = got == want
        bad += not ok
        print(f"{name:<28} {str(got):>14} {str(want):>14}   {'OK' if ok else 'MISMATCH'}")
    if want_cache is None:
        ok = d.get("Cache hit", "").strip().startswith("--")
        print(f"{'Cache hit':<28} {str(shown_cache):>14} {'-- (input==0)':>14}   {'OK' if ok else 'MISMATCH'}")
        bad += not ok
    else:
        ok = shown_cache is not None and abs(shown_cache - want_cache) < 0.011
        bad += not ok
        print(f"{'Cache hit %':<28} {str(shown_cache):>14} {round(want_cache, 4):>14}   {'OK' if ok else 'MISMATCH'}")
    if total != inp + out:
        print("!! warning: official total_tokens != input+output in this file")
        bad += 1

    timing = derive_response_totals(recs).get(d.get("Turn"), {"tokens": 0, "ms": 0, "timed": 0, "observed": 0})
    print()
    for name, want in (("Paired generation ms", timing["ms"]), ("Paired output tokens", timing["tokens"])):
        got = as_int(d, name)
        ok = got == want
        bad += not ok
        print(f"{name:<28} {str(got):>14} {str(want):>14}   {'OK' if ok else 'MISMATCH'}")
    coverage = f"{timing['timed']}/{timing['observed']}"
    shown_coverage = d.get("Timed responses")
    ok = shown_coverage == coverage
    bad += not ok
    print(f"{'Timed responses':<28} {str(shown_coverage):>14} {coverage:>14}   {'OK' if ok else 'MISMATCH'}")

    # During generation, the display smooths the ratio and retains the prior completed turn's TPS.
    if d.get("Streaming") == "false":
        got = as_float(d, "Last completed TPS")
        want = timing["tokens"] * 1000 / timing["ms"] if timing["ms"] else None
        ok = got is None if want is None else got is not None and abs(got - want) < 0.051
        bad += not ok
        print(f"{'Last completed TPS':<28} {str(got):>14} {str(want):>14}   {'OK' if ok else 'MISMATCH'}")

    print()
    if bad:
        print(f"RESULT: {bad} mismatch(es) — investigate before trusting the strip")
        return 1
    print("RESULT: monitor output agrees with the raw rollout data")
    return 0


if __name__ == "__main__":
    sys.exit(main())