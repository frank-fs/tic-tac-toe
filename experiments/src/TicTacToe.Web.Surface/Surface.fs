module TicTacToe.Web.Surface.Surface

/// The 2^4 surface-factorial cell. Each flag toggles exactly one discovery
/// factor; the cube is their product. Flag order is A, C, Sd, So.
type Surface =
    { A: bool   // affordances: turn/role-aware available actions
      C: bool   // accessibility: ARIA roles / landmarks / live region
      Sd: bool  // semantic discovery: Link/Allow/OPTIONS, /profile, /.well-known/home
      So: bool } // semantic ontology: JSON-LD schema.org Game typing

/// All factors off — cell 0000, the discovery floor.
let floor = { A = false; C = false; Sd = false; So = false }

/// Parse a 4-char cell flag (order A,C,Sd,So), each char '0' or '1'.
/// Fail-fast (Holzmann R12): malformed input throws — never boot a
/// misconfigured surface.
let parse (raw: string) : Surface =
    if isNull raw then nullArg "raw"
    if raw.Length <> 4 then
        failwithf "TICTACTOE_CELL must be exactly 4 chars (order A,C,Sd,So); got length %d: %s" raw.Length raw
    let bit (i: int) =
        match raw.[i] with
        | '0' -> false
        | '1' -> true
        | c -> failwithf "TICTACTOE_CELL char %d must be '0' or '1'; got '%c'" i c
    { A = bit 0; C = bit 1; Sd = bit 2; So = bit 3 }

/// Read TICTACTOE_CELL once at startup. Unset/empty -> floor (never an
/// accidental rich surface).
let fromEnvironment () : Surface =
    match System.Environment.GetEnvironmentVariable "TICTACTOE_CELL" with
    | null | "" -> floor
    | raw -> parse raw
