# Roadmap

## Phase 1 — Core Engine Porting & Decoupling ✅

Forked the `Services`/`Models` files listed in [MIGRATION_PLAN.md](MIGRATION_PLAN.md) into
`engine/`, closing the dependency gap in the previous `.csproj` linkage (missing
`AltiumFootprintNaming`, `NativeCommonSymbolPolicy`, `DiscreteNetworkSymbolPolicy`,
`TransistorSymbolPolicy`, `EasyEdaSymbolPreference`). Wrote the new category/catalog filtering
logic (`CatalogFilter`) that has no `YouEDA` equivalent. `engine/` builds with zero UI
dependencies and has offline correctness tests (in the spirit of `YouEDA`'s `tests/KiCadSmoke`)
covering the documented AGENTS.md identities: diode/transistor polarity rejection,
native-symbol preservation (polarised capacitor, optoisolator, MB10S bridge), and the
confirmed-LDO preference list — plus one end-to-end test against the real bundled catalog
asset. Not yet ported: the export-side geometry/checkpoint tests (KiCad output, family-merge
concurrency) that `KiCadSmoke` also covers — deferred to Phase 4, where `tests/` is expanded
to the full engine surface.

## Phase 2 — CLI Interface & Parameter Handling ✅

`cli/`'s argument parser is wired to the real `engine/` pipeline: `--input` BOM/CSV import,
`--category`/`--catalog` filtered import (all documented filter flags implemented in
`CatalogFilter`), `--with-3d`, and `--resume`/`--checkpoint` against `BulkLibraryWriter`'s
ledger — all verified against live LCSC/EasyEDA data, including a full 351-part run. A live
throughput/ETA progress indicator (periodic summary line: done/total, rate, elapsed, ETA)
runs alongside the per-part `[OK]`/`[FAIL]` log lines. The structured JSON-lines error log
is done (`ImportDiagnostics`, `errors.jsonl`).

## Phase 3 — Performance Benchmarking & Async Operations ✅ (initial numbers)

Measured real throughput for the documented worker-pool/rate-limit design (see
ARCHITECTURE.md) against the full 351-part JLCPCB Basic Parts BOM: ~58 parts/minute cold
cache (bottlenecked by the fixed 1 request/second fetch gate), ~600 parts/minute warm cache
(bottlenecked by parse/write instead). See README.md's Performance section. Not yet measured:
throughput at different `--workers` counts (cold-cache throughput is gate-bound regardless, so
this mainly matters for warm-cache/parse-bound runs), and `--with-3d` download overhead.

## Phase 4 — Packaging, Distribution & Testing

CI now builds and tests on every push/PR (`.github/workflows/ci.yml`), following the README's
documented build recipe. Remaining: a framework-dependent Windows x64 publish, matching
`YouEDA`'s own release packaging (SHA-256 checksum, bundled templates/symbol catalog); and
expanding `tests/` with the export-side geometry/checkpoint tests noted in Phase 1, including
an offline acceptance run against a real LCSC catalog export.

## Phase 5 — Backport Engine Fixes to the YouEDA Desktop App

Re-sync correctness fixes made in `engine/` back into `YouEDA`'s desktop `Services/`/`Models/`,
planned as a version bump there (see MIGRATION_PLAN.md's "Later phase" note). Out of scope
until Phases 1–4 are stable; not executed as part of this restart.
