# Usage: set GEMINI_API_KEY and GROQ_API_KEY, then run
#   python run_llm.py --batch-size 5

import os
import sys
import json
import time
import argparse
import datetime as dt
from pathlib import Path

from analyze import call_cost

HERE = Path(__file__).resolve().parent

TEMPERATURE = 0.0

MODELS = {
    "gemini": {"provider": "gemini", "model_id": "gemini-3.6-flash", "env": "GEMINI_API_KEY"},
    "gemini-argon": {"provider": "gemini", "model_id": "gemini-4-argon", "env": "GEMINI_API_KEY"},
    "groq": {"provider": "groq", "model_id": "openai/gpt-oss-120b", "env": "GROQ_API_KEY"},
}

# Number of successful runs per query, failed calls are repeated later

def load_existing(path):

    done = {}

    if path.exists():
        with path.open(encoding="utf-8") as f:
            for line in f:
                if line.strip():
                    rec = json.loads(line)
                    if not rec.get("error"):
                        done[rec["query_id"]] = done.get(rec["query_id"], 0) + 1
    return done


# Error classification

def is_daily_quota(exc):
    text = str(exc).lower().replace("_", "")
    return "perday" in text or "per day" in text or "daily" in text


def is_retryable(exc):
    text = f"{type(exc).__name__} {exc}".lower()
    return any(s in text for s in ("429", "rate", "quota", "resource_exhausted", "503", "500",
                                   "unavailable", "overloaded", "timeout"))


# Providers

def call_gemini(client, model_id, prompt, max_tokens):

    from google.genai import types

    config = types.GenerateContentConfig(temperature=TEMPERATURE, max_output_tokens=max_tokens)
    parts, ttft, finish, usage = [], None, None, None

    t0 = time.perf_counter()
    for chunk in client.models.generate_content_stream(model=model_id, contents=prompt, config=config):
        try:
            text = chunk.text or ""
        except Exception:
            text = ""
        if text and ttft is None:
            ttft = time.perf_counter() - t0
        parts.append(text)
        if chunk.candidates and chunk.candidates[0].finish_reason:
            finish = str(chunk.candidates[0].finish_reason)
        if chunk.usage_metadata:
            usage = chunk.usage_metadata
    total = time.perf_counter() - t0

    prompt_tokens = getattr(usage, "prompt_token_count", None)
    completion_tokens = getattr(usage, "candidates_token_count", None)
    reasoning_tokens = getattr(usage, "thoughts_token_count", None)
    total_tokens = getattr(usage, "total_token_count", None)

    # If the thinking tokens are not reported separately, they are derived from the total

    if reasoning_tokens is None and None not in (total_tokens, prompt_tokens, completion_tokens):
        reasoning_tokens = max(0, total_tokens - prompt_tokens - completion_tokens)

    return {
        "ttft_s": ttft, "total_s": total, "output": "".join(parts), "finish_reason": finish,
        "prompt_tokens": prompt_tokens, "completion_tokens": completion_tokens,
        "reasoning_tokens": reasoning_tokens, "total_tokens": total_tokens,
    }


def call_groq(client, model_id, prompt, max_tokens):

    parts, ttft, finish, usage = [], None, None, None

    t0 = time.perf_counter()
    stream = client.chat.completions.create(
        model=model_id,
        messages=[{"role": "user", "content": prompt}],
        temperature=TEMPERATURE,
        max_completion_tokens=max_tokens,
        stream=True,
    )
    for chunk in stream:
        if chunk.choices:
            choice = chunk.choices[0]
            text = getattr(choice.delta, "content", None) or ""
            if text and ttft is None:
                ttft = time.perf_counter() - t0
            parts.append(text)
            if choice.finish_reason:
                finish = str(choice.finish_reason)
        xg = getattr(chunk, "x_groq", None)
        if xg is not None and getattr(xg, "usage", None) is not None:
            usage = xg.usage
    total = time.perf_counter() - t0

    details = getattr(usage, "completion_tokens_details", None)

    return {
        "ttft_s": ttft, "total_s": total, "output": "".join(parts), "finish_reason": finish,
        "prompt_tokens": getattr(usage, "prompt_tokens", None),
        "completion_tokens": getattr(usage, "completion_tokens", None),
        "reasoning_tokens": getattr(details, "reasoning_tokens", None),
    }


CALLERS = {"gemini": call_gemini, "groq": call_groq}


def make_client(provider, key):

    if provider == "gemini":
        from google import genai
        return genai.Client(api_key=key)

    from groq import Groq
    return Groq(api_key=key)


# Calls a model, retrying rate limits and transient errors with backoff

def call_with_retry(name, cfg, client, prompt, max_tokens, exhausted):

    result, error, retries = None, None, 0

    for attempt in range(6):
        try:
            result = CALLERS[cfg["provider"]](client, cfg["model_id"], prompt, max_tokens)
            return result, None, retries
        except Exception as exc:
            error = f"{type(exc).__name__}: {exc}"
            if is_daily_quota(exc):
                exhausted.add(name)
                print(f"  {name}: a napi kvóta elfogyott, ebben a batchben kimarad")
                break
            if attempt < 5 and is_retryable(exc):
                retries += 1
                wait = min(120, 10 * 2 ** attempt)
                print(f"  újrapróbálás {wait} s múlva ({error[:80]})")
                time.sleep(wait)
            else:
                break
    return result, error, retries


def main():

    ap = argparse.ArgumentParser()
    ap.add_argument("--models", nargs="+", default=list(MODELS), choices=list(MODELS))
    ap.add_argument("--runs", type=int, default=10, help="runs per query")
    ap.add_argument("--batch-size", type=int, default=0, help="new runs per query in one start (0 = all)")
    ap.add_argument("--delay", type=float, default=5.0, help="pause between calls (s)")
    ap.add_argument("--max-tokens", type=int, default=16384)
    ap.add_argument("--queries", default=str(HERE / "queries.json"))
    ap.add_argument("--prompt", default=str(HERE / "prompt.txt"))
    ap.add_argument("--out", default=str(HERE / "results"))
    args = ap.parse_args()

    queries = json.loads(Path(args.queries).read_text(encoding="utf-8"))
    template = Path(args.prompt).read_text(encoding="utf-8")
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    batch_id = dt.datetime.now().strftime("%Y%m%d-%H%M%S")

    clients, done, files = {}, {}, {}

    for name in args.models:
        cfg = MODELS[name]
        key = os.environ.get(cfg["env"])
        if not key:
            sys.exit(f"Hiányzó környezeti változó: {cfg['env']}")
        clients[name] = make_client(cfg["provider"], key)
        files[name] = out_dir / f"raw_{name}.jsonl"
        done[name] = load_existing(files[name])

    # Only the missing runs are planned, interleaving models and queries so that
    # every model sees similar network conditions

    plan = []

    for name in args.models:
        for q in queries:
            have = done[name].get(q["id"], 0)
            todo = max(0, args.runs - have)
            if args.batch_size > 0:
                todo = min(todo, args.batch_size)
            plan += [(have + i + 1, q, name) for i in range(todo)]
    plan.sort(key=lambda p: (p[0], p[1]["id"], p[2]))

    print(f"Batch {batch_id}: {len(plan)} hívás")
    exhausted = set()
    batch_cost = 0.0

    for i, (run, q, name) in enumerate(plan, 1):

        if name in exhausted:
            continue

        cfg = MODELS[name]
        prompt = template.replace("{sql}", q["sql"])
        started = dt.datetime.now().isoformat(timespec="seconds")

        result, error, retries = call_with_retry(name, cfg, clients[name], prompt, args.max_tokens, exhausted)

        if name in exhausted:
            continue

        rec = {
            "model": name, "model_id": cfg["model_id"], "query_id": q["id"], "run": run,
            "batch_id": batch_id, "started_at": started, "temperature": TEMPERATURE,
            "max_tokens": args.max_tokens, "retries": retries, "error": error,
        }
        rec.update(result or {"ttft_s": None, "total_s": None, "output": None, "finish_reason": None,
                              "prompt_tokens": None, "completion_tokens": None, "reasoning_tokens": None})

        with files[name].open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

        cost = None if error else call_cost(rec)
        if cost is not None:
            batch_cost += cost

        cost_text = f", {cost:.5f} $ (batch eddig: {batch_cost:.4f} $)" if cost is not None else ""
        status = "HIBA" if error else f"{rec['total_s']:.2f} s, {len(rec['output'] or '')} karakter, {rec['finish_reason']}{cost_text}"
        print(f"[{i}/{len(plan)}] {name} {q['id']} #{run}: {status}")

        if i < len(plan):
            time.sleep(args.delay)

    print(f"A batch becsült költsége listaáron: {batch_cost:.4f} $")


if __name__ == "__main__":
    main()
