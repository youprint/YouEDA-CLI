# YouEDA CLI

YouEDA CLI is a fast, lightweight, restartable Windows command-line converter that turns
EasyEDA/LCSC CAD data into native Altium `youeda.PcbLib` and `youeda.SchLib` libraries —
built for unattended catalog-scale jobs where the [YouEDA](https://github.com/youprint/YouEDA)
desktop app's interactive workflow doesn't fit.

It shares its conversion engine with YouEDA: geometry, layer mapping, 3D embedding, bundled
common symbols, and IC/MCU symbol generation stay consistent between the desktop app and this
CLI — the relevant `Services`/`Models` files are forked into [engine/](engine/), per
[MIGRATION_PLAN.md](MIGRATION_PLAN.md). See [ARCHITECTURE.md](ARCHITECTURE.md) for how the
pieces fit together and [ROADMAP.md](ROADMAP.md) for what's still ahead (packaging, benchmarks).

> Check every generated footprint, symbol, polarity, and 3D model against the manufacturer
> datasheet before production use.

## What makes it suitable for catalog jobs

- Bounded parallel EasyEDA fetch/parse workers.
- Global request-rate limiting and retry handling; defaults protect the public endpoint.
- Raw component JSON cache, so reruns do not re-download successful payloads.
- A single native Altium library writer: safe for the binary compound files while downloads
  remain parallel.
- Atomic checkpoints plus a completion ledger, enabling `Ctrl+C` + `--resume` without
  duplicating completed parts.
- Structured JSON-lines error log for failed parts.
- STEP download is opt-in with `--with-3d`, because a full catalog can consume substantial
  time and storage.

## Build

Prerequisites: Windows, .NET SDK 10, and Git.

```powershell
git clone https://github.com/youprint/YouEDA-CLI.git
cd YouEDA-CLI
git clone https://github.com/issus/AltiumSharp.git third_party/AltiumSharp
git -C third_party/AltiumSharp checkout ce72437f30cd54f549601d4e0ca5846d21272150
git -C third_party/AltiumSharp submodule update --init --recursive
git -C third_party/AltiumSharp apply ../../patches/altium-native-symbol-coordinates.patch
git -C third_party/AltiumSharp apply ../../patches/altium-sdk-sourcelink.patch
dotnet restore
dotnet build -c Release
```

`engine/` no longer links live into a `third_party/YouEDA` clone — its conversion code has
been forked in, so only the `AltiumSharp` writer dependency needs cloning. `AltiumSharp` itself
pulls in its own `OriginalCircuit.Eda.Abstractions`/`Eda.Rendering`/`Mech.*` submodules; the
`submodule update --init --recursive` step above is required or the build fails with missing
`Coord`/`CoordPoint`/etc. types (they live in those submodules).

The solution at the repo root (`YouEDA-CLI.sln`) builds `cli/`, `engine/`, and `tests/`
together. `dotnet test` runs the engine test suite.

## Commands

Import a BOM/list:

```powershell
dotnet run --project cli -- --input .\parts.csv --output C:\Libraries\YouEDA --workers 4 --rps 1
```

Import every resistor selected from a local official LCSC catalog export:

```powershell
dotnet run --project cli -- --category resistor --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Resistors --workers 4 --rps 1
```

Import only YAGEO 0402 resistors:

```powershell
dotnet run --project cli -- --category resistor --manufacturer YAGEO --package 0402 --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Yageo-0402 --workers 4 --rps 1
```

Limit any import to a specific LCSC assembly class (`basic`, `preferred`, or `extended`):

```powershell
dotnet run --project cli -- --category resistor --part-class basic --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Basic-Resistors --workers 4 --rps 1
```

For example, import every 10 µF extended YAGEO capacitor:

```powershell
dotnet run --project cli -- --category capacitor --capacitance 10uF --manufacturer YAGEO --part-class extended --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Yageo-10uF-Extended --workers 4 --rps 1
```

Further catalog filters include `--resistance`, `--inductance`, `--voltage`, `--tolerance`,
`--power`, `--dielectric`, `--mounting`, and repeatable `--contains`/`--exclude` conditions.
Every supplied condition is an AND filter.

**LCSC access disclaimer:** the category+catalog commands above are intentionally simple
once the catalog export is in place. LCSC's documented category-list API requires an
approved API key and request signature, so this project does **not** bypass access controls
or scrape undocumented catalogue pages. Obtain the CSV/JSON catalog through LCSC's approved
API/export process, then use it as the discovery manifest.

Use `--help` for all flags. The default output contains:

```text
youeda.PcbLib
youeda.SchLib
.youeda-bulk\cache\          raw EasyEDA payload cache
.youeda-bulk\completed.txt    parts successfully checkpointed
.youeda-bulk\errors.jsonl     failures that can be retried later
```

For a 100,000-part job, begin with `--workers 4 --rps 1 --checkpoint 100`, review a sample
batch in Altium, and only then adjust throughput within your authorized LCSC/EasyEDA request
limits. A single 100,000-component native library can become very large; plan disk space,
backups, and an eventual sharding policy if Altium becomes slow to open it.

## Current status

`--input` (BOM/CSV) and `--category`/`--catalog` (filtered catalog) imports both run for real:
live EasyEDA/LCSC fetch, native Altium export, checkpointing, and `--resume` all work end to
end. Verified against a live 351-part JLCPCB Basic Parts BOM (351/351 succeeded). Not yet done:
`--with-3d` is implemented and manually verified but has no automated test; packaging/benchmarks
(Phases 3-4 in [ROADMAP.md](ROADMAP.md)) haven't started. See [MIGRATION_PLAN.md](MIGRATION_PLAN.md)
for what was ported and what's still new CLI-only code.

The `--category`/`--catalog` filter has no fixed LCSC export schema to target, since LCSC
doesn't publish one official CSV layout. The filter matches likely column headers
case-insensitively (`LCSC Part Number`, `Manufacturer`, `Package`, `Category`, `Library Type`,
etc.) and falls back to searching the whole row's text, so it tolerates reasonable header
variations but isn't guaranteed to match every possible export's column naming.

## Performance

**TBD.** No comparable legacy implementation of this CLI currently exists to benchmark
against, so no throughput numbers are published here. Real numbers will be measured and
recorded once Phase 3 (see ROADMAP.md) has a working engine to profile.

## License and notices

MIT license for this project. The converter depends on
[OriginalCircuit.Altium](https://github.com/issus/AltiumSharp) (Apache-2.0). EasyEDA/LCSC
endpoint formats and catalog access policies can change without notice. This is an
independent project and is not affiliated with Altium, EasyEDA, JLCPCB, or LCSC.
