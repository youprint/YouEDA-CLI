# Migration Plan

This describes which `YouEDA` files get forked into `engine/`, in what order, and which
`AGENTS.md` behavioral rules must survive the port. It is a plan for Phase 1
(see [ROADMAP.md](ROADMAP.md)) — no files have been copied yet in this pass.

All source paths below are relative to `reference/YouEDA` (a read-only clone of
`youprint/YouEDA`, gitignored, not committed to this repo).

## Order and components

### 1. Model

| File | AGENTS.md rules to preserve |
|---|---|
| `src/Models/EdaComponent.cs` | None directly — this is the model the rest of the rules attach fields/behavior to. |

### 2. Fetch and parse

| File | AGENTS.md rules to preserve |
|---|---|
| `src/Services/LcscScraper.cs` | Shared one-request-per-second start gate; honor `Retry-After` and HTTP 429/5xx cooldowns. |
| `src/Services/Parser.cs` | Preserve every LCSC symbol/parameter from the payload; don't skip an uncached per-part request just because the package name looks familiar. |

### 3. Symbol selection

| File | AGENTS.md rules to preserve |
|---|---|
| `src/Services/UserSymbolLibraryResolver.cs` | Treat the user's native `.SchLib` collection as source of truth; native symbol preservation (artwork, pin mapping, arcs, text params, line styles) over guessed approximations; bundled catalog only for safe family matches; EasyEDA-generated geometry is the last fallback. |
| `src/Services/NativeCommonSymbolPolicy.cs` | 71-symbol bundled catalog membership; ferrite beads keep native Ferrite Chip artwork even with an `L` designator; elliptical-arc inductor coils (RECORD=11) are not "missing artwork." |
| `src/Services/DiscreteNetworkSymbolPolicy.cs` | Diode networks/TVS/bridges/four-pin switches require exact supplier+MPN profiles, not pin-count matching (see `docs/discrete-network-symbol-profiles.md`); preserve topology, every terminal, permanent switch pairs. |
| `src/Services/TransistorSymbolPolicy.cs` | Must prove NPN/PNP/NMOS/PMOS, not just B/C/E or G/S/D; numeric-only pins need a documented per-part datasheet mapping; reject conflicting polarity/functions (see `docs/transistor-symbol-profiles.md`). |
| `src/Services/EasyEdaSymbolPreference.cs` | The seven confirmed LDO/voltage-reference exceptions (C14289, C5446, C58069, C6186, C6187, C71136, C3113) stay pinned even with sparse metadata; manual overrides still win. |

### 4. Export

| File | AGENTS.md rules to preserve |
|---|---|
| `src/Services/AltiumFootprintNaming.cs` | Footprint named from `packageDetail.title`, falling back to the LCSC number. |
| `src/Services/AltiumSchExporter.cs` | Upsert semantics per LCSC part number; hidden `Value` parameter for R/C; native line-size enum mapping (`0=Smallest…3=Large`). |
| `src/Services/AltiumV2Exporter.cs` | Shared footprint entries across parts with the same EasyEDA footprint title; correct drill-radius-to-diameter conversion; slot metadata preserved. |
| `src/Services/EasyEda3dModelDownloader.cs` | STEP download is opt-in (`--with-3d`); 7-day validated cache; don't negative-cache failures. |
| `src/Services/ModelAssetCache.cs` | Reuse one fetched model UUID's immutable payload across parts sharing it; each part keeps its own pose. |
| `src/Services/ModelDownloadBatch.cs` | One shared 3-slot download batch across all workers, not one pool per worker; shared pacing/backoff. |

### 5. Pricing and classification

| File | AGENTS.md rules to preserve |
|---|---|
| `src/Services/JlcPcbPricingService.cs` | Lowest valid purchase-tier price, never an unqualified bulk offer when tiers exist; `JLCPCB Unit Price` is always per-one-component with `JLCPCB Price Quantity=1`; MOQ and full tier list kept separately; price lookup is best-effort and never blocks CAD import. |

### 6. Orchestration and reliability

| File | AGENTS.md rules to preserve |
|---|---|
| `src/Services/AltiumExportBatch.cs` | Bounded parallel workers (`--workers`); single writer per output library — never concurrent writes to one `.PcbLib`/`.SchLib`. |
| `src/Services/BulkLibraryWriter.cs` | Checkpoint every N processed parts (`--checkpoint`); verified temporary sibling replaces each library file; not a crash-atomic transaction — document that limitation, don't paper over it. |
| `src/Services/ImportDiagnostics.cs` | Opt-in, local JSON-lines diagnostics; never dump CAD/STEP payloads, headers, or credentials; logging failure must never fail the import. |

### 7. New CLI-specific code (not a port)

The category+catalog filtering surface (`--category`, `--catalog`, `--manufacturer`,
`--package`, `--part-class`, `--resistance`, `--capacitance`, `--inductance`, `--voltage`,
`--tolerance`, `--power`, `--dielectric`, `--mounting`, `--contains`, `--exclude`) has no
equivalent in `YouEDA` — the desktop GUI only accepts a flat LCSC-code list (see its "CSV/text
GUI import contract" in `AGENTS.md`). This must be written fresh against the forked
`EdaComponent`/`Parser` model, not copied from an existing file.

## Known gap in the current (pre-fork) linkage

`YouEDA-CLI`'s previous `.csproj` (`src/YouEDA.CLI/YouEDA.CLI.csproj`, replaced by this
scaffold) linked `UserSymbolLibraryResolver.cs` and both Altium exporters but omitted
`AltiumFootprintNaming.cs`, `NativeCommonSymbolPolicy.cs`, `DiscreteNetworkSymbolPolicy.cs`,
`TransistorSymbolPolicy.cs`, and `EasyEdaSymbolPreference.cs` — all of which those files call
directly. As wired, that project would not have compiled. Phase 1 must include the full
dependency set above, not just the previously-linked subset.

## Not WPF-dependent

All files listed above are plain C# with no `System.Windows`/XAML references. The only two
WPF-touching files in `YouEDA`'s `Services/` are `ThemeService.cs` (desktop theming) and a
comment-only mention in `BatchComponentLookup.cs` — neither is needed by the CLI and neither
is in this plan.

## Later phase: backporting fixes to YouEDA

Bugs or correctness fixes made to the forked `engine/` code during CLI development are not
automatically reflected in the `YouEDA` desktop app, since the two now contain independent
copies. A later phase (Phase 5 in ROADMAP.md) re-syncs such fixes back into `YouEDA`, planned
as a version bump there (e.g. 1.4.0 → 1.4.1), reviewed the same way any other `YouEDA` change
would be. That phase is out of scope for this restart.
