#!/usr/bin/env python3
"""Grade one cold-start discovery run against a per-cell ground truth.

Inputs:
  --transcript  TRANSCRIPT_PATH jsonl (agent messages; MOMENT 1/2 reports live in
                assistant content)
  --gt          groundtruth/cell-XXXX.json
  --proxy       arena http proxy jsonl (wire truth: friction, /profile, accepted moves)
  --role        expected seat (default X); --game / --out optional

Objective fields (positionTokenSource, bootstrapped, friction, formatErrors) are taken
from the WIRE, not self-reports. Semantic fields (appIs/goal/...) are keyword-graded with
the agent's raw text in the rationale so a human can confirm or override.

    uv run --no-project experiments/oss-driver/grade.py \
      --transcript /tmp/sp3-validate/transcript-1010.jsonl \
      --proxy /tmp/sp3-validate/proxy-1010.jsonl \
      --gt experiments/oss-driver/groundtruth/cell-1010.json
"""
import sys, json, re, argparse

TOKENS = ("profile", "board", "guess", "none")

def arg(name, default=None):
    p = f"--{name}"
    return sys.argv[sys.argv.index(p) + 1] if p in sys.argv else default

def load_reports(path):
    """Return (pre, post) last real MOMENT reports (skip the prompt's template echo)."""
    pre = post = None
    for line in open(path):
        o = json.loads(line)
        if o.get("role") != "assistant":
            continue
        for m in re.findall(r'\{"moment".*?\}', o["content"]):
            try:
                d = json.loads(m)
            except ValueError:
                continue
            if d.get("appIs") == "..." or d.get("myRole") == "...":
                continue  # template echo from the system prompt
            if d.get("moment") == "pre":
                pre = d
            elif d.get("moment") == "post":
                post = d
    return pre, post

def wire(path):
    reads = ok = rej = auth = profile = 0
    if not path:
        return dict(reads=0, writesAccepted=0, writesRejected=0, authRedirects=0, profileGets=0)
    for line in open(path):
        line = line.strip()
        if not line:
            continue
        try:
            e = json.loads(line)
        except ValueError:
            continue
        method, status, p = e.get("method"), e.get("status", 0), e.get("path", "")
        if "/profile" in p:
            profile += 1
        if status == 302:
            auth += 1
        elif method == "GET":
            reads += 1
        elif method == "POST" and status in (200, 303):
            ok += 1
        elif method == "POST" and status >= 400:
            rej += 1
    return dict(reads=reads, writesAccepted=ok, writesRejected=rej, authRedirects=auth, profileGets=profile)

def kw(text, words):
    t = (text or "").lower()
    return any(w in t for w in words)

def verdict(ok, partial=False):
    return "correct" if ok else ("partial" if partial else "wrong")

def norm_src(val):
    t = str(val or "").lower()
    for tok in TOKENS:
        if tok in t:
            return tok
    return t or "none"

def grade(pre, post, gt, w, role):
    pre = pre or {}
    post = post or {}
    core = gt["core"]
    acceptable = gt["expectedPositionTokenSource"]["acceptable"]
    src = norm_src(post.get("positionTokenSource"))
    bootstrapped = w["writesAccepted"] > 0

    g_pre = {
        "appIs": {"verdict": verdict(kw(pre.get("appIs"), ["tic", "tac", "toe"]), kw(pre.get("appIs"), ["game"])),
                  "rationale": f'reported: {pre.get("appIs")!r}'},
        "goal": {"verdict": verdict(kw(pre.get("goal"), ["row", "three", "line"]) and kw(pre.get("goal"), ["row", "three", "win", "line"]),
                                    kw(pre.get("goal"), ["win", "goal"])),
                 "rationale": f'reported: {pre.get("goal")!r}'},
        "isMultiplayer": {"verdict": verdict(bool(pre.get("isMultiplayer")) == bool(core["isMultiplayer"])),
                          "rationale": f'reported: {pre.get("isMultiplayer")!r} (gt {core["isMultiplayer"]})'},
        "howToJoin": {"verdict": verdict(kw(pre.get("howToJoin"), ["move", "seat", "post"]), bool(pre.get("howToJoin"))),
                      "rationale": f'reported: {pre.get("howToJoin")!r}'},
    }
    role_txt = str(post.get("myRole") or "")
    hedged = kw(role_txt, [" or ", "not yet", "unseat", "unknown"])
    g_post = {
        "myRole": {"verdict": verdict(role.lower() in role_txt.lower() and not hedged, role.lower() in role_txt.lower()),
                   "rationale": f'reported: {role_txt!r} (expected {role})'},
        "canIMove": {"verdict": verdict(bool(post.get("canIMove")), post.get("canIMove") is not None),
                     "rationale": f'reported: {post.get("canIMove")!r} (turn-dependent at report time)'},
        "myAffordances": {"verdict": verdict(kw(json.dumps(post.get("myAffordances")), ["move", "square", "play"]), bool(post.get("myAffordances"))),
                          "rationale": f'reported: {post.get("myAffordances")!r}'},
    }

    flags = []
    if src == "profile" and w["profileGets"] == 0:
        flags.append("claimed positionTokenSource=profile but never fetched /profile (possible hallucination)")
    if src not in acceptable and src != "none":
        flags.append(f"positionTokenSource={src} not in acceptable {acceptable}")
    sd_expected = any("/profile" in a for a in gt.get("expectedArtifactDiscovery", []))
    if sd_expected and w["profileGets"] == 0:
        flags.append("Sd cell: /profile was never fetched")

    return {
        "cell": gt["cell"],
        "role": role,
        "pre": g_pre,
        "post": g_post,
        "positionTokenSource": src,
        "positionTokenSourceAcceptable": src in acceptable,
        "bootstrapped": bootstrapped,
        "formatErrors": w["writesRejected"],
        "friction": {k: w[k] for k in ("reads", "writesAccepted", "writesRejected", "authRedirects")},
        "profileGets": w["profileGets"],
        "flags": flags,
        "reportsPresent": {"pre": pre != {}, "post": post != {}},
    }

def main():
    t = arg("transcript"); gtp = arg("gt"); px = arg("proxy"); role = arg("role", "X"); out = arg("out")
    if not (t and gtp):
        print("usage: grade.py --transcript X.jsonl --gt cell-XXXX.json [--proxy P.jsonl] [--role X] [--out F]", file=sys.stderr)
        sys.exit(2)
    gt = json.load(open(gtp))
    pre, post = load_reports(t)
    rec = grade(pre, post, gt, wire(px), role)
    s = json.dumps(rec, indent=2)
    if out:
        open(out, "w").write(s + "\n")
    print(s)

if __name__ == "__main__":
    main()
