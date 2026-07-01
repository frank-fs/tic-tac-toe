# Behavior-coding rubric — API-client vs browser-user

Classifies how a seat obtained the requests it sent, from its transcript + logs.
The axis is the thesis's "conduct" dimension: does the agent actuate rendered
controls (browser-user) or reverse-engineer the API from priors (API-client)?

## Axis

- **browser-user** — actuates a control the *current page rendered*: submits a
  shown form, follows a shown link. Reads, then acts on what is there.
- **API-client** — constructs a request from priors: a path, field, or value
  **not present** on the current page. Treats HTML as an API to reverse-engineer.

## Per-action code

For each request a seat issued, matched against the immediately-preceding
rendered page (the prior `user`-turn HTML in `<seat>.transcript.jsonl`):

| code           | criteria                                                                                          | axis        | method     |
|----------------|---------------------------------------------------------------------------------------------------|-------------|------------|
| `FOLLOW`       | POST path == a form `action` on the prior page AND `player`/`position` appear in that form        | browser-user| mechanical |
| `READ`         | GET of the current arena page (re-read)                                                            | neutral     | mechanical |
| `WAIT`         | GET while not the seat's turn / nothing actionable                                                 | browser-user| mech+LLM   |
| `INVENT_PATH`  | request to a path NOT present as a form action/link on the prior page (`/move`, `/play`, `/arenas`)| API-client  | mechanical |
| `INVENT_PARAM` | POST to a valid form path but a `player`/`position` value the page did not offer                   | API-client  | mechanical |
| `DESTRUCT`     | POST `/delete` or `/restart`                                                                       | out-of-spec | mechanical |

Server verdict (HTTP status / game-events `reason`) is **metadata, not the
label**: a `FOLLOW` may be rejected (stale board → refresh); an `INVENT` may
accidentally succeed. Label reflects agent *intent/source*, not outcome.

`WAIT` disambiguation (mechanical → LLM only when ambiguous): a GET-when-not-your-
turn is mechanically `WAIT`; escalate to the LLM judge only when intent is unclear
(e.g. repeated GETs that could be patient waiting OR stuck-looping). The judge runs
through the driver's existing `LlmClient` (same OpenRouter path) — no separate tool
or language.

## Per-seat classification

- counts: `nFOLLOW`, `nINVENT` (= `INVENT_PATH` + `INVENT_PARAM`), `nREAD`, `nWAIT`, `nDESTRUCT`
- `conduct_ratio = nFOLLOW / (nFOLLOW + nINVENT)` → 1.0 pure browser-user, 0.0 pure API-client
- label: **browser-user** ≥ 0.8 · **mixed** 0.2–0.8 · **API-client** ≤ 0.2 (thresholds tunable)
- **cross-check vs self-report**: MOMENT-2 `positionTokenSource` (`board`=browser,
  `guess`=API, `profile`=discovery). Flag mismatch (claims `board` but high `nINVENT`).

## Data sources

- `<seat>.transcript.jsonl` — `assistant` turns = intent + reasoning; `user` turns
  = server responses = the rendered page that is the "prior page" for the next action
- `http.jsonl` — actual method/path/status (ground truth of what was sent)
- `game-events.jsonl` — server verdicts (accepted/rejected + reason)
- result JSON / MOMENT reports — self-reported `positionTokenSource`

## Implementation

F# grader subcommand (extend `Grader.fs`): parse transcript + http log, emit
per-action codes and per-seat `conduct_ratio` + label. Mechanical string-match for
all codes except ambiguous `WAIT`, which calls `LlmClient`. Output JSON per seat so
the ladder analysis can aggregate conduct across cells × prompts.
