# Spike #58 — Datastar same-URL SSE feasibility → NOT ADOPTED

**Question:** Will Datastar open an SSE connection against the SAME page URL (`/`,
`/games/{id}`) so a resource can content-negotiate html vs. event-stream on one URL?

**Finding: technically feasible, but NOT the right shape. Decision: keep a dedicated
stream endpoint (`/sse`).**

## Why feasible

Datastar's stream transport is fetch-based (fetch-event-source), not the browser's native
`EventSource` — so it is not restricted to GET and the target URL is arbitrary. A client
could point at the page's own URL and the server could branch on the request.

## Why not adopted

1. **Landing-page lifecycle mismatch.** An SSE stream stays open until natural close. Having
   the landing page's own URL serve an open-until-close stream conflicts with it also being a
   normal, navigable, finite html document. A dedicated stream endpoint is the natural shape.
2. **No framework support for the dispatch.** Frank compiles each `(method, handler)` into its
   own route endpoint, so two GET handlers on one resource collide — there is no built-in
   Accept-based dispatch between a synchronous representation and an SSE stream. Adding that to
   Frank is real work and is **deferred** (also: Frank.Datastar's fetch-based SSE is any-method,
   so a GET-only negotiated op would be too narrow).

## Adopted design (this milestone)

Progressive enhancement with a **dedicated stream**:

- **Initial load:** `GET /` (and `GET /games/{id}`) server-render the full current state as
  html — discoverable and playable with no JS (#59). Refresh re-fetches current state (#61).
- **After initial load:** the JS client connects to the existing dedicated **`/sse`** stream
  for live updates (unchanged).
- a11y: `aria-live` on the board status region (#61).

Same-URL content negotiation (former Issue #60) and the Phase B same-URL repoint are dropped
from this milestone; revisit if/when Frank grows an Accept-dispatch capability.
