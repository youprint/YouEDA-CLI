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

## Phase 2 — CLI Interface & Parameter Handling ✅ (mostly)

`cli/`'s argument parser is wired to the real `engine/` pipeline: `--input` BOM/CSV import,
`--category`/`--catalog` filtered import (all documented filter flags implemented in
`CatalogFilter`), `--with-3d`, and `--resume`/`--checkpoint` against `BulkLibraryWriter`'s
ledger — all verified against live LCSC/EasyEDA data, including a full 351-part run. Remaining:
a live terminal progress UI (worker/queue state, throughput, ETA) beyond the current per-part
log lines; the structured JSON-lines error log is already done (`ImportDiagnostics`,
`errors.jsonl`).

## Phase 3 — Performance Benchmarking & Async Operations

Once a working engine exists, measure real throughput under the worker-pool/rate-limit design
described in ARCHITECTURE.md and replace the README's "Performance: TBD" section with actual
numbers — parts/minute at various `--workers`/`--rps` settings, cache-hit vs. cold-fetch timing,
and 3D-download overhead with `--with-3d`. No numbers are invented before this phase runs.

## Phase 4 — Packaging, Distribution & Testing

Framework-dependent Windows x64 publish, matching `YouEDA`'s own release packaging
(SHA-256 checksum, bundled templates/symbol catalog). Expand `tests/` beyond the current
skeleton to cover the full engine surface, including an offline acceptance run against a
real LCSC catalog export.

## Phase 5 — Backport Engine Fixes to the YouEDA Desktop App

Re-sync correctness fixes made in `engine/` back into `YouEDA`'s desktop `Services/`/`Models/`,
planned as a version bump there (see MIGRATION_PLAN.md's "Later phase" note). Out of scope
until Phases 1–4 are stable; not executed as part of this restart.
