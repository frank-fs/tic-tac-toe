You are an autonomous agent. You have been handed exactly ONE thing: a URL — {URL}. Nothing else: no docs, no API description, no instructions about what the app is. Use `curl` for everything.

Your job: (1) figure out what this app is, then (2) interact with it toward whatever its goal turns out to be. {FIRST}

Identity — the ONE mechanic you are told (everything else you must discover):
- The app uses cookie sessions. Establish your identity ONCE: `curl -c {JAR} -L {URL_BASE}/login` (if that 404s, GET the URL you were given with `-c {JAR}` instead).
- On EVERY later request send `-b {JAR}`. NEVER use `-c` again — it overwrites your identity and you lose your place.
- If a request redirects you to /login, retry the SAME request with `-b {JAR}`. Do not log in a second time.

Pacing: this is not a race. After you act, wait ~3-5 seconds, then re-read. Do not poll constantly. Cap your total requests at ~25. If the server stops responding, the app has almost certainly finished — stop and report; do NOT call it a crash. Never reset, delete, or create anything.

You MUST emit two JSON reports, exactly these shapes, on their own lines:

MOMENT 1 (emit after your first reads, BEFORE you do anything that changes state):
{"moment":"pre","appIs":"...","goal":"...","isMultiplayer":true,"howToJoin":"... or UNKNOWN"}

MOMENT 2 (emit after you have attempted to participate):
{"moment":"post","myRole":"...","myAffordances":["..."],"canIMove":true,"positionTokenSource":"profile|board|guess|none"}

For positionTokenSource, say honestly where any move details came from: a profile/contract document you fetched, the rendered page, a guess, or none.

After MOMENT 2, keep interacting toward the goal until it is reached or you are blocked. Finally report: every request you made (method, path, status) and the final state you observed.
