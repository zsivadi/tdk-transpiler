# Usage: python analyze.py [--billed-gemini <billed amount of the Gemini project in USD>]
# Reads results/raw_*.jsonl and writes results/summary.csv

import re
import csv
import json
import argparse
import statistics
from pathlib import Path

HERE = Path(__file__).resolve().parent

MODEL_ORDER = ["gemini", "gemini-argon", "groq"]

TRUNCATED = ("length", "max_tokens", "finishreason.max_tokens")

# List prices in USD per 1M tokens

PRICES = {
    "gemini": {"input": 0.75, "output": 3.75},
    "gemini-argon": {"input": 2.00, "output": 10.00},
    "groq": {"input": 0.15, "output": 0.60},
}

# Cost

def call_cost(r):

    price = PRICES.get(r["model"])
    if not price or r.get("prompt_tokens") is None:
        return None

    out = r.get("completion_tokens") or 0

    # Gemini reports thinking tokens separately and bills them as output,
    # Groq's completion_tokens already includes them

    if r["model"].startswith("gemini"):
        out += r.get("reasoning_tokens") or 0

    return (r["prompt_tokens"] * price["input"] + out * price["output"]) / 1e6


def model_costs(records, billed_gemini=None):

    costs = {}

    for model in PRICES:
        recs = [r for r in records if r["model"] == model and not r.get("error")]
        if recs:
            costs[model] = {"calls": len(recs), "estimated": False,
                            "cost_usd": sum(c for c in (call_cost(r) for r in recs) if c is not None)}

    # gemini-argon does not report its thinking tokens, so its real cost is the billed
    # amount of the Gemini project minus the exactly known cost of the other Gemini model

    if billed_gemini is not None and "gemini-argon" in costs and "gemini" in costs:
        costs["gemini-argon"]["cost_usd"] = billed_gemini - costs["gemini"]["cost_usd"]
        costs["gemini-argon"]["estimated"] = True
    return costs


# Output comparison

def normalize(code):
    return "".join((code or "").split())


# Also ignores the name of the DbContext variable and a closing semicolon

def normalize_structure(code):
    return re.sub(r"\b(dbContext|context|ctx|db)\.", "", normalize(code)).rstrip(";")


# Statistics

def load(results):

    records = []

    for path in sorted(results.glob("raw_*.jsonl")):
        with path.open(encoding="utf-8") as f:
            records += [json.loads(l) for l in f if l.strip()]
    return records


def summarize(records):

    groups, failed = {}, {}

    for r in records:
        key = (r["model"], r["query_id"])
        if r.get("error"):
            failed[key] = failed.get(key, 0) + 1
        else:
            groups.setdefault(key, []).append(r)

    rows = []

    for (model, qid), recs in groups.items():
        times = [r["total_s"] for r in recs if r.get("total_s") is not None]
        ttfts = [r["ttft_s"] for r in recs if r.get("ttft_s") is not None]
        outputs = [r.get("output") or "" for r in recs]
        non_empty = [o for o in outputs if o.strip()]
        rows.append({
            "model": model, "query_id": qid, "n": len(recs), "errors": failed.get((model, qid), 0),
            "mean_s": statistics.mean(times) if times else None,
            "median_s": statistics.median(times) if times else None,
            "min_s": min(times) if times else None,
            "max_s": max(times) if times else None,
            "stdev_s": statistics.stdev(times) if len(times) > 1 else 0.0 if times else None,
            "mean_ttft_s": statistics.mean(ttfts) if ttfts else None,
            "empty": len(outputs) - len(non_empty),
            "truncated": sum(1 for r in recs if str(r.get("finish_reason") or "").lower() in TRUNCATED),
            "distinct_outputs": len({normalize(o) for o in non_empty}),
            "distinct_structures": len({normalize_structure(o) for o in non_empty}),
            "cost_usd": sum(c for c in (call_cost(r) for r in recs) if c is not None),
        })
    rows.sort(key=lambda r: (MODEL_ORDER.index(r["model"]) if r["model"] in MODEL_ORDER else 99, r["query_id"]))
    return rows


def write_csv(rows, path):

    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)


def main():

    ap = argparse.ArgumentParser()
    ap.add_argument("--results", default=str(HERE / "results"))
    ap.add_argument("--billed-gemini", type=float, default=None, help="billed amount of the Gemini project (USD)")
    args = ap.parse_args()

    results = Path(args.results)
    records = load(results)

    if not records:
        raise SystemExit("Nincs feldolgozható eredmény a results mappában.")

    rows = summarize(records)
    write_csv(rows, results / "summary.csv")

    for r in rows:
        print(f"{r['model']:<13} {r['query_id']}  n={r['n']:<3} átlag={r['mean_s']:.2f} s  szórás={r['stdev_s']:.2f} s  "
              f"eltérő={r['distinct_outputs']} szerkezet={r['distinct_structures']} "
              f"üres={r['empty']} csonka={r['truncated']} hiba={r['errors']}")
    print()

    for model, c in model_costs(records, args.billed_gemini).items():
        mark = " (becslés a számlázott összegből)" if c["estimated"] else ""
        print(f"{model:<13} {c['calls']} hívás, {c['cost_usd']:.4f} $, {c['cost_usd'] / c['calls']:.5f} $ / hívás{mark}")

    print(f"\nElkészült: {results / 'summary.csv'}")


if __name__ == "__main__":
    main()
