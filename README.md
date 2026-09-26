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

## Download version 1.4.0

Download `YouEDA-CLI-1.4.0-win-x64.zip` from the
[v1.4.0 release](https://github.com/youprint/YouEDA-CLI/releases/tag/v1.4.0), extract it, and
run `YouEDA.CLI.exe --help`. Requires the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows 10/11
x64. Verify the published SHA-256 checksum before use — see the release notes, or
[docs/releases/1.4.0.md](docs/releases/1.4.0.md) for what's in this release. The .NET 10 SDK
is only needed to build from source (see [Build](#build) below).

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
- A live terminal progress bar — percentage, succeeded/failed counts, throughput, elapsed time,
  and ETA — in an interactive console; falls back to periodic plain-text progress lines when
  stdout is redirected (a log file, a pipe, CI), since an in-place redraw doesn't mean anything
  once captured.

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

## Command reference

Every example below uses `dotnet run --project cli --` during development; substitute
`YouEDA.CLI.exe` if you're running a [published build](#package). `--output <dir>` is always
required. Two mutually exclusive ways to pick parts: `--input <bom.csv>` (a fixed list) or
`--category <name> --catalog <file>` (filtered from a catalog export) — see each below.

### `--input`: import a BOM/CSV list of LCSC part numbers

```powershell
dotnet run --project cli -- --input .\parts.csv --output C:\Libraries\YouEDA --workers 4
```

`parts.csv` follows the same lightweight contract as `YouEDA` desktop's GUI import: one
unquoted `C` + digits code per line, or any file with a code column — headers, quantity,
value, description columns are all ignored (see [`CsvBomReader`](engine/Services/CsvBomReader.cs)).
Separators can be newline, comma, semicolon, tab, or space, and duplicates are removed
automatically. Both of these work:

```csv
LCSC Part
C11702
C8678
```

```csv
LCSC Part,Quantity,Value,Description
C11702,100,1kΩ,0402 resistor
C8678,10,SS34,Schottky diode
```

A quoted code like `"C11702"` is **not** accepted (matches `YouEDA` desktop's parser exactly).

### `--category` / `--catalog`: import filtered from a local LCSC catalog export

Import every resistor from a local catalog export:

```powershell
dotnet run --project cli -- --category resistor --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Resistors --workers 4
```

`--category` matches against the catalog's `Category`/`First Category`/`Second Category`
column if present, falling back to a whole-row text search otherwise — see
[`CatalogFilter`](engine/Services/CatalogFilter.cs) for exactly how column matching works.
Every filter flag below is combined with AND: adding more flags only narrows the result.

**`--manufacturer <name>`** — filter by manufacturer:

```powershell
dotnet run --project cli -- --category resistor --manufacturer YAGEO --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Yageo-Resistors
```

**`--package <name>`** — filter by package/footprint:

```powershell
dotnet run --project cli -- --category resistor --package 0402 --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\0402-Resistors
```

**`--part-class <basic|preferred|extended>`** — filter by LCSC/JLCPCB assembly class:

```powershell
dotnet run --project cli -- --category resistor --part-class basic --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Basic-Resistors
```

**`--resistance <value>`** — e.g. every 10 kΩ part:

```powershell
dotnet run --project cli -- --category resistor --resistance 10k --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\10k-Resistors
```

**`--capacitance <value>`** — accepts `µF`/`μF`/`uF` interchangeably (normalized internally):

```powershell
dotnet run --project cli -- --category capacitor --capacitance 10uF --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\10uF-Capacitors
```

**`--inductance <value>`**:

```powershell
dotnet run --project cli -- --category inductor --inductance 4.7uH --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\4.7uH-Inductors
```

**`--voltage <value>`** — voltage rating:

```powershell
dotnet run --project cli -- --category capacitor --voltage 50V --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\50V-Capacitors
```

**`--tolerance <value>`**:

```powershell
dotnet run --project cli -- --category resistor --tolerance 1% --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\1pct-Resistors
```

**`--power <value>`** — power rating:

```powershell
dotnet run --project cli -- --category resistor --power 0.25W --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Quarter-Watt-Resistors
```

**`--dielectric <value>`** — e.g. ceramic capacitor dielectric:

```powershell
dotnet run --project cli -- --category capacitor --dielectric X7R --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\X7R-Capacitors
```

**`--mounting <value>`** — e.g. `SMD` or `THT`:

```powershell
dotnet run --project cli -- --category resistor --mounting SMD --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\SMD-Resistors
```

**`--contains <text>`** / **`--exclude <text>`** — free-text AND filters, each repeatable:

```powershell
dotnet run --project cli -- --category diode --contains Schottky --exclude TVS --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Schottky-Diodes
```

**Combining filters** — every 10 µF, extended-class, YAGEO ceramic capacitor:

```powershell
dotnet run --project cli -- --category capacitor --capacitance 10uF --manufacturer YAGEO --part-class extended --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Yageo-10uF-Extended
```

**LCSC access disclaimer:** the commands above are intentionally simple once the catalog
export is in place. LCSC's documented category-list API requires an approved API key and
request signature, so this project does **not** bypass access controls or scrape undocumented
catalogue pages. Obtain the CSV/JSON catalog through LCSC's approved API/export process, then
use it as the discovery manifest.

### `--workers <n>`: parallel fetch/parse concurrency

```powershell
dotnet run --project cli -- --input .\parts.csv --output C:\Libraries\YouEDA --workers 8
```

Bounds how many parts are being fetched/parsed at once. It does **not** raise the request
rate — see [`--rps`](#--rps-n-request-rate) — so on a cold cache it mainly helps overlap
parsing/export work with the next fetch's wait; the real throughput gain shows up on a warm
cache (see [Performance](#performance)). Defaults to 1 if omitted.

### `--rps <n>`: request rate

```powershell
dotnet run --project cli -- --input .\parts.csv --output C:\Libraries\YouEDA --rps 2
```

Currently **informational only** — the underlying fetch gate is fixed at 1 request/second
(the same default `YouEDA` desktop protects the public EasyEDA endpoint with). Passing a
value other than `1` prints a note to that effect but doesn't change behavior.

### `--resume` + `--checkpoint <n>`: interrupt and continue safely

Start a large job, interrupt it with `Ctrl+C`, then continue where it left off:

```powershell
dotnet run --project cli -- --input .\parts.csv --output C:\Libraries\YouEDA --checkpoint 25
# ... Ctrl+C partway through ...
dotnet run --project cli -- --input .\parts.csv --output C:\Libraries\YouEDA --resume
```

`--resume` skips any part already recorded in `.youeda-bulk\completed.txt` for that output
directory. Without `--resume`, a run starts fresh and clears that ledger and the error log.
`--checkpoint <n>` controls how often the native libraries are saved and verified (every `n`
successfully processed parts, default 10) — a crash between checkpoints loses work only back
to the last one, never the whole run.

### `--with-3d`: download STEP 3D models

```powershell
dotnet run --project cli -- --input .\parts.csv --output C:\Libraries\YouEDA --with-3d
```

Opt-in because a full catalog run can download a large number of STEP files. Models land in
`<output>\models\` as `<model-name>.step` (e.g. `R0805_L2.0-W1.3-H0.6.step`) and are embedded
in `youeda.PcbLib`. Parts that share the same EasyEDA package/model reuse the same downloaded
payload internally rather than re-fetching it. A part with no supplied 3D model is skipped
without failing the import.

### `--version` / `--help`

```powershell
dotnet run --project cli -- --version
# YouEDA CLI 1.4.0.0

dotnet run --project cli -- --help
# full flag list
```

Running with no arguments at all also prints the flag list (and exits with code 1, so a
forgotten command in a script fails loudly instead of silently doing nothing).

### Output layout

```text
youeda.PcbLib
youeda.SchLib
models\                       STEP 3D models, only with --with-3d
.youeda-bulk\cache\            raw EasyEDA payload cache (valid 7 days)
.youeda-bulk\completed.txt     parts successfully checkpointed, used by --resume
.youeda-bulk\errors.jsonl      failures, one JSON object per line, safe to retry later
```

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success — every part imported, or `--version`/`--help` |
| `1` | Partial failure (some parts failed), no parts matched/to import, a required file was missing, or no arguments were given |
| `2` | Bad usage — unrecognized/malformed argument, missing `--output`, or neither `--input` nor `--category`+`--catalog` given |
| `130` | Cancelled with `Ctrl+C` |

### Large jobs

For a 100,000-part job, begin with `--workers 4 --checkpoint 100`, review a sample batch in
Altium, and only then adjust throughput within your authorized LCSC/EasyEDA request limits.
A single 100,000-component native library can become very large; plan disk space, backups,
and an eventual sharding policy if Altium becomes slow to open it.

## Current status

As of the [v1.4.0 release](https://github.com/youprint/YouEDA-CLI/releases/tag/v1.4.0),
`--input` (BOM/CSV) and `--category`/`--catalog` (filtered catalog) imports both run for real:
live EasyEDA/LCSC fetch, native Altium export, checkpointing, and `--resume` all work end to
end. Verified against a live 351-part JLCPCB Basic Parts BOM (351/351 succeeded). Not yet done:
`--with-3d` is implemented and manually verified but has no automated test; the export-side
geometry/checkpoint tests noted in [ROADMAP.md](ROADMAP.md)'s Phase 4 entry haven't been
ported yet. See [MIGRATION_PLAN.md](MIGRATION_PLAN.md) for what was ported and what's still
new CLI-only code.

The `--category`/`--catalog` filter has no fixed LCSC export schema to target, since LCSC
doesn't publish one official CSV layout. The filter matches likely column headers
case-insensitively (`LCSC Part Number`, `Manufacturer`, `Package`, `Category`, `Library Type`,
etc.) and falls back to searching the whole row's text, so it tolerates reasonable header
variations but isn't guaranteed to match every possible export's column naming.

## Performance

Two measured runs, `--input` against the full 351-part JLCPCB Basic Parts BOM, `--workers 4`,
default rate gate (fixed at 1 request/second):

| | Cold cache | Warm cache |
|---|---|---|
| Parts | 351/351 succeeded, 0 failed | 351/351 succeeded, 0 failed |
| Wall time | 6m03s | 35s |
| Throughput | ~58 parts/minute | ~600 parts/minute |
| Output | `youeda.PcbLib` 233 KB, `youeda.SchLib` 1.7 MB | same |

Cold-cache throughput is dominated by the fixed one-request-per-second fetch gate (the same
shared gate `YouEDA` desktop uses, see ARCHITECTURE.md) — `--workers` above 1 mainly lets
parse/export overlap with the next fetch's wait, it does not raise the request rate itself.
Warm cache (raw CAD payloads already in `.youeda-bulk/cache/`, valid for 7 days) skips the
network entirely and is bottlenecked by parsing and native library writes instead, which is
roughly 10x faster in this measurement. `--with-3d` timing has not been separately measured
yet. These are two data points, not a guarantee; your own network conditions and EasyEDA's
response time will vary the cold-cache number.

## Package

Build a framework-dependent Windows x64 folder, matching `YouEDA` desktop's own release
packaging:

```powershell
dotnet publish cli\YouEDA.CLI.csproj -c Release -r win-x64 --self-contained false -o dist\YouEDA-CLI
Get-FileHash dist\YouEDA-CLI\YouEDA.CLI.exe -Algorithm SHA256
```

The published folder includes `YouEDA.CLI.exe`, its dependencies, and the bundled
`Symbols\BundledUserSymbols.SchLib`/`Templates\AltiumTemplate.PcbLib` assets — everything
needed to run on a machine with the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
installed, no SDK required. Verify the published SHA-256 checksum before distributing it,
the same way `YouEDA` desktop releases do.

## License and notices

MIT license for this project. The converter depends on
[OriginalCircuit.Altium](https://github.com/issus/AltiumSharp) (Apache-2.0). EasyEDA/LCSC
endpoint formats and catalog access policies can change without notice. This is an
independent project and is not affiliated with Altium, EasyEDA, JLCPCB, or LCSC.
