# Architecture

## Pipeline

```
Input Parsing                Intermediate Model            Exporters
──────────────                ──────────────────            ─────────
BOM/CSV list        ┐
                     ├─► LcscScraper ─► Parser ─► EdaComponent ─┬─► AltiumSchExporter ─► youeda.SchLib
Category + catalog  ┘        (fetch)     (parse)                │        (schematic symbol,
CSV filter                                                       │         via UserSymbolLibraryResolver)
                                                                   │
                                                                   └─► AltiumV2Exporter ─► youeda.PcbLib
                                                                            (footprint, pads, 3D model)
```

- **Input parsing** — either a flat BOM/CSV of LCSC part numbers (`--input`), or a category
  name filtered against a locally-supplied LCSC catalog export (`--category`/`--catalog` plus
  `--manufacturer`, `--package`, `--part-class`, `--resistance`, etc.). The catalog-filter path
  has no equivalent in `YouEDA` today — the desktop app only accepts a flat LCSC-code list — so
  it is new CLI-specific code, not a port.
- **Intermediate model** — `LcscScraper` fetches the raw EasyEDA component payload, `Parser`
  turns it into an `EdaComponent`. `UserSymbolLibraryResolver` (plus
  `NativeCommonSymbolPolicy`/`DiscreteNetworkSymbolPolicy`/`TransistorSymbolPolicy`/
  `EasyEdaSymbolPreference`) decides which schematic symbol source to use — native bundled
  catalog, exact `Desktop\Library` match, or EasyEDA-pin-derived fallback — per the correctness
  rules in `YouEDA`'s `AGENTS.md`.
- **Exporters** — `AltiumSchExporter` writes the schematic symbol into `youeda.SchLib`;
  `AltiumV2Exporter` (via `AltiumFootprintNaming` for consistent naming) writes the footprint,
  pads, and embedded STEP model into `youeda.PcbLib`. `BulkLibraryWriter` owns the single
  writer per output file and the checkpoint ledger.

## Why fork the engine into this repo now

`YouEDA-CLI` previously didn't own any engine code — its `.csproj` used MSBuild
`<Compile Include>` to link directly against files inside a `third_party/YouEDA` clone. That
keeps a single source of truth (one copy of `Parser.cs`, `AltiumSchExporter.cs`, etc.) but
makes `YouEDA-CLI` an unpublishable, unbuildable-standalone consumer of another repo's internal
file layout: any rename/refactor in `YouEDA` silently breaks this repo's build, and the linked
file list can drift out of sync with what those files actually depend on (see
MIGRATION_PLAN.md — the current link list is already missing several required dependencies).

Forking the relevant `Services`/`Models` files into `engine/` trades that for the opposite
tradeoff: `YouEDA-CLI` becomes a real, independently buildable/publishable repo with no
cross-repo file dependency, at the cost of two copies of the engine that can diverge. That
divergence is managed deliberately, not accidentally: MIGRATION_PLAN.md documents a later
phase that backports engine-side fixes made here into a version bump of the `YouEDA` desktop
app, rather than letting the two silently drift apart.

`AltiumSharp` is not forked — it remains a separate `third_party/AltiumSharp` clone, consistent
with how `YouEDA` itself depends on it (Apache-2.0, upstream-maintained SDK, not
project-specific conversion logic).

## Concurrency, caching, and checkpointing (carried over from YouEDA's real services)

This describes what actually exists in `YouEDA`'s services today, which `engine/` is expected
to fork in Phase 1 — not aspirational design for the CLI:

- **Worker pools:** the desktop app runs CAD lookups on 3 concurrent workers and up to 3
  concurrent STEP/OBJ model downloads, all sharing one download batch rather than one pool per
  worker (`ModelDownloadBatch`). Altium family export uses up to 3 exclusive whole-family
  workers with a one-worker serial fallback. The CLI's `--workers` flag is the equivalent knob
  for its own bounded worker pool.
- **Rate limiting:** one shared start-rate gate — at most one new request per second, including
  retries — with HTTP 429/5xx cooldowns and `Retry-After` (seconds or HTTP-date) honored. The
  CLI's `--rps` flag maps to this same shared gate.
- **Caching:** validated CAD payloads are cached for 7 days (`%LocalAppData%\YouEDA\ComponentCache`
  on desktop; the CLI's own raw-payload cache lives under `.youeda-bulk\cache\`). Model
  UUIDs are fetched once per batch and reused; identical footprint geometry is reused within a
  writer.
- **Single-writer libraries:** `BulkLibraryWriter` keeps one writer per output `.PcbLib`/`.SchLib`
  — concurrent writes to one native library file are never allowed, even though fetch/parse
  workers run in parallel. It checkpoints every 10 processed parts (desktop) and saves/reopens
  verified checkpoints before deferred prompts and at completion.
- **Checkpointing/resume:** completed parts are recorded in a completion ledger
  (`.youeda-bulk\completed.txt` for the CLI); `--resume` skips them. Failures go to a
  structured JSON-lines error log (`.youeda-bulk\errors.jsonl`) rather than aborting the run.
  A crash between checkpoints can lose work since the last checkpoint — this is explicitly not
  described as a crash-atomic transaction in `YouEDA`'s own documentation, and the CLI inherits
  that same limitation.

## Project layout

- `cli/` — entrypoint, argument parsing, logging, terminal UI. Depends only on `engine/`'s
  public surface.
- `engine/` — will hold the forked, UI-independent `Services`/`Models` conversion code.
  Must not reference anything CLI- or UI-specific.
- `tests/` — xUnit tests against `engine/`, in the spirit of `YouEDA`'s `tests/KiCadSmoke`.
