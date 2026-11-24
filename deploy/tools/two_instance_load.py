#!/usr/bin/env python3
"""Load and shared-state check against TWO running API instances (usage: two_instance_load.py URL_A URL_B).

1. Load: many different usernames sign in with a wrong password, spread over both instances. Every request
   goes through the shared sign-in throttle in the database. Expected answer is 401; anything else counts as an error.
2. Shared lockout: one username fails 5 times, split across the two instances; afterwards BOTH instances must refuse
   it (429), which only happens if the failure count lives in the database and not in each instance's memory.
Writes a Markdown table to $GITHUB_STEP_SUMMARY when set. Exits 1 on any error or a lockout that is not shared.
"""
import json, os, statistics, sys, time, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor

A, B = sys.argv[1].rstrip("/"), sys.argv[2].rstrip("/")
TOTAL = int(os.environ.get("LOAD_REQUESTS", "600"))
THREADS = int(os.environ.get("LOAD_THREADS", "32"))


def login(base, username, password="not-the-password"):
    body = json.dumps({"username": username, "password": password}).encode()
    req = urllib.request.Request(base + "/api/auth/login", body, {"Content-Type": "application/json"})
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            code = r.status
    except urllib.error.HTTPError as e:
        code = e.code
    except Exception:
        code = 0
    return code, (time.perf_counter() - start) * 1000


def one(i):
    return login(A if i % 2 == 0 else B, f"load-user-{i}")


# warm both instances
login(A, "warm-up-a"); login(B, "warm-up-b")

t0 = time.perf_counter()
with ThreadPoolExecutor(THREADS) as pool:
    results = list(pool.map(one, range(TOTAL)))
seconds = time.perf_counter() - t0
lat = sorted(ms for _, ms in results)
ok = sum(1 for c, _ in results if c == 401)
other = {}
for c, _ in results:
    if c != 401:
        other[c] = other.get(c, 0) + 1
p = lambda q: lat[min(len(lat) - 1, int(q * len(lat)))]

# shared lockout
user = f"lock-test-{int(time.time())}"
codes = []
for i in range(5):
    codes.append(login(A if i < 3 else B, user)[0])      # 3 failures on A, 2 on B
after_a, after_b = login(A, user)[0], login(B, user)[0]
shared = after_a == 429 and after_b == 429

lines = [
    "## Two-instance check",
    "",
    f"| Requests | Threads | Seconds | Per second | p50 ms | p95 ms | p99 ms | 401 (expected) | Other |",
    f"| --- | --- | --- | --- | --- | --- | --- | --- | --- |",
    f"| {TOTAL} | {THREADS} | {seconds:.1f} | {TOTAL / seconds:.1f} | {p(.5):.0f} | {p(.95):.0f} | {p(.99):.0f} | {ok} | {other or 0} |",
    "",
    f"Lockout shared between instances: failures A,A,A,B,B -> A answers {after_a}, B answers {after_b} "
    f"({'shared' if shared else 'NOT shared'}).",
]
text = "\n".join(lines)
print(text)
if os.environ.get("GITHUB_STEP_SUMMARY"):
    open(os.environ["GITHUB_STEP_SUMMARY"], "a").write(text + "\n")
sys.exit(0 if (ok == TOTAL and shared) else 1)
