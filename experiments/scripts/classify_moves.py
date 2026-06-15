#!/usr/bin/env python3
"""Per-cell analysis answering: (1) does the harness deliver refreshed state — i.e.
does an agent's view ever reflect an opponent's move (>=2 marks seen)? and (2) are
rejected moves STALE-view (acted on a pre-opponent-move snapshot) vs IGNORED-state
(acted against what the agent saw) vs BLOCKED (3rd agent, by design)?

Self-contained per transcript: each tool RESPONSE carries the server's true current
state, so pre-click view vs response = stale-vs-fresh without cross-log matching.

Usage: classify_moves.py <results_dir> <cell_id> [<cell_id> ...]
"""
import json, sys, re, os, glob

POS = ["TopLeft","TopCenter","TopRight","MiddleLeft","MiddleCenter","MiddleRight",
       "BottomLeft","BottomCenter","BottomRight"]

def jload(s):
    try: return json.loads(s) if s and s.strip().startswith(("{","[")) else None
    except: return None

# ---- browsegrab (Simple via browser) ----
def bg_parse(snap):
    """a11y snapshot text -> ({pos:'X'|'O'|''}, turn-hint, {ref:name}). Handles both
    enabled `button "TopLeft": · [ref=e1]` and disabled `button "X" [disabled, ref=e5]: X`
    renderings by taking the mark token after the LAST colon on the line."""
    board={}; refmap={}
    for ln in snap.splitlines():
        if 'button "' not in ln and 'link "' not in ln: continue
        nm=re.search(r'(?:button|link) "([^"]*)"', ln)
        if not nm: continue
        name=nm.group(1)
        rf=re.search(r'ref=(e\d+)', ln)
        if rf: refmap[rf.group(1)]=name
        if name in POS and ':' in ln:
            tail=ln.rsplit(':',1)[-1].strip()
            tok=tail.split()[0] if tail else ''
            board[name]='' if tok in ('·','.','•','') else tok
    turn=None
    if "not your turn" in snap.lower(): turn="not-your-turn"
    elif re.search(r"X's turn", snap): turn="X"
    elif re.search(r"O's turn", snap): turn="O"
    return board, turn, refmap

def bg_snapshot_of(out):
    o=jload(out); return o.get("snapshot","") if isinstance(o,dict) else ""

def server_accepted(cell_dir):
    """Authoritative accepted-move count from the server request log (status 200)."""
    p=os.path.join(cell_dir,"server-requests.jsonl")
    if not os.path.exists(p): return 0
    n=0
    for ln in open(p):
        o=jload(ln)
        if isinstance(o,dict) and o.get("status_code")==200 and o.get("method")=="POST": n+=1
    return n

def analyze_browsegrab(cell_dir):
    res={"accepted":0,"stale":0,"ignored":0,"blocked":0,"other_rej":0,
         "clicks":0,"max_marks_seen":0,"cross_refresh":False}
    res["accepted"]=server_accepted(cell_dir)  # authoritative; transcript heuristic over-counts
    for f in sorted(glob.glob(os.path.join(cell_dir,"transcripts","agent-*.json"))):
        d=json.load(open(f))
        view={}; refmap={}; turn=None
        for t in d.get("llm_turns",[]):
            for tc in t.get("tool_calls",[]):
                out=tc.get("output") or ""
                if tc["tool_name"]=="browser_click":
                    res["clicks"]+=1
                    ref=(jload(tc.get("input") or "{}") or {}).get("ref")
                    target=refmap.get(ref)
                    o=jload(out) or {}
                    resp_snap=o.get("snapshot","") or ""
                    rboard,rturn,_=bg_parse(resp_snap)
                    rmarks=sum(1 for v in rboard.values() if v)
                    res["max_marks_seen"]=max(res["max_marks_seen"],rmarks)
                    if rmarks>=2: res["cross_refresh"]=True
                    if target in POS:
                        pre_empty = view.get(target,'')==''
                        # response truth: did the move land? cell now this agent's mark & marks grew
                        landed = o.get("success") and rboard.get(target,'')!=''and (target not in view or view.get(target,'')=='')
                        taken_now = rboard.get(target,'')!='' and not landed
                        nyt = "not your turn" in resp_snap.lower()
                        if landed: pass  # accepted counted from server log
                        elif taken_now and pre_empty: res["stale"]+=1      # saw empty, was taken
                        elif taken_now and not pre_empty: res["ignored"]+=1 # saw taken, clicked anyway
                        elif nyt: res["stale" if turn not in ("not-your-turn",) else "ignored"]+=1
                        else: res["other_rej"]+=1
                # update running view from any response snapshot
                snap=bg_snapshot_of(out)
                if snap:
                    b,tn,rm=bg_parse(snap)
                    if rm: refmap=rm
                    if b: view, turn = b, tn
                    elif tn is not None: turn=tn
    return res

# ---- ERPC (RPC tools) ----
def analyze_erpc(cell_dir):
    res={"accepted":0,"stale":0,"ignored":0,"uuid_miscopy":0,"game_not_found":0,
         "make_move":0,"max_marks_seen":0,"cross_refresh":False}
    for f in sorted(glob.glob(os.path.join(cell_dir,"transcripts","agent-*.json"))):
        d=json.load(open(f))
        view={}; whose=None
        for t in d.get("llm_turns",[]):
            for tc in t.get("tool_calls",[]):
                out=tc.get("output") or ""; o=jload(out)
                if tc["tool_name"]=="get_state" and isinstance(o,dict):
                    b=o.get("board")
                    if isinstance(b,dict):
                        view={k:(v or '') for k,v in b.items()}
                        marks=sum(1 for v in view.values() if v)
                        res["max_marks_seen"]=max(res["max_marks_seen"],marks)
                        if marks>=2: res["cross_refresh"]=True
                    whose=o.get("whoseTurn")
                    if isinstance(o,dict) and o.get("error")=="GameNotFound":
                        res["game_not_found"]+=1
                if tc["tool_name"]=="get_state" and isinstance(o,dict) and o.get("error")=="GameNotFound":
                    res["game_not_found"]+=1
                if tc["tool_name"]=="make_move":
                    res["make_move"]+=1
                    inp=jload(tc.get("input") or "{}") or {}
                    target=inp.get("position")
                    if isinstance(o,dict) and "board" in o:
                        nb={k:(v or '') for k,v in o["board"].items()}
                        marks=sum(1 for v in nb.values() if v)
                        res["max_marks_seen"]=max(res["max_marks_seen"],marks)
                        if marks>=2: res["cross_refresh"]=True
                        landed = target and view.get(target,'')=='' and nb.get(target,'')!=''
                        if landed: res["accepted"]+=1
                        elif target and view.get(target,'')!='': res["ignored"]+=1
                        elif target and nb.get(target,'')!='': res["stale"]+=1
                        view=nb
                    elif isinstance(o,dict) and o.get("error") in ("GameNotFound",):
                        res["game_not_found"]+=1
        # uuid miscopy: a GameNotFound where list_games had returned a (different) id
        for t in d.get("llm_turns",[]):
            for tc in t.get("tool_calls",[]):
                if tc["tool_name"]=="get_state":
                    o=jload(tc.get("output") or "")
                    if isinstance(o,dict) and o.get("error")=="GameNotFound":
                        res["uuid_miscopy"]+=1
    return res

def main():
    base=sys.argv[1]; cells=sys.argv[2:]
    print(f"{'cell':28} {'acc':>3} {'stale':>5} {'ignor':>5} {'blkd':>4} {'maxMk':>5} {'xRefresh':>8}")
    for c in cells:
        cdir=os.path.join(base,c)
        if not os.path.isdir(os.path.join(cdir,"transcripts")):
            print(f"{c:28} (no transcripts yet)"); continue
        if "browsegrab" in c:
            r=analyze_browsegrab(cdir)
            print(f"{c:28} {r['accepted']:>3} {r['stale']:>5} {r['ignored']:>5} {r['blocked']:>4} "
                  f"{r['max_marks_seen']:>5} {str(r['cross_refresh']):>8}")
        else:
            r=analyze_erpc(cdir)
            extra=f"gnf={r['game_not_found']} mm={r['make_move']}"
            print(f"{c:28} {r['accepted']:>3} {r['stale']:>5} {r['ignored']:>5} {'-':>4} "
                  f"{r['max_marks_seen']:>5} {str(r['cross_refresh']):>8}  {extra}")

if __name__=="__main__":
    main()
