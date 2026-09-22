# YouEDA CLI

YouEDA CLI is a high-throughput, restartable Windows command-line importer for turning EasyEDA/LCSC CAD data into native Altium `youeda.PcbLib` and `youeda.SchLib` libraries.

It deliberately shares the converter used by [YouEDA](https://github.com/youprint/YouEDA): geometry, layer mapping, 3D embedding, bundled common symbols, and IC/MCU symbol generation remain consistent between desktop and automation workflows.

> Check every generated footprint, symbol, polarity, and 3D model against the manufacturer datasheet before production use.

## What makes it suitable for catalog jobs

- Bounded parallel EasyEDA fetch/parse workers.
- Global request-rate limiting and retry handling; defaults protect the public endpoint.
- Raw component JSON cache, so reruns do not re-download successful payloads.
- A single native Altium library writer: safe for the binary compound files while downloads remain parallel.
- Atomic checkpoints plus a completion ledger, enabling `Ctrl+C` + `--resume` without duplicating completed parts.
- Structured JSON-lines error log for failed parts.
- STEP download is opt-in with `--with-3d`, because a full catalog can consume substantial time and storage.

## Build

Prerequisites: Windows, .NET SDK 10, and Git.

```powershell
git clone https://github.com/youprint/YouEDA-CLI.git
cd YouEDA-CLI
git clone https://github.com/youprint/YouEDA.git third_party/YouEDA
git clone https://github.com/issus/AltiumSharp.git third_party/AltiumSharp
dotnet restore .\src\YouEDA.CLI\YouEDA.CLI.csproj
dotnet build .\src\YouEDA.CLI\YouEDA.CLI.csproj -c Release
```

## Commands

Import a BOM/list:

```powershell
dotnet run --project .\src\YouEDA.CLI\YouEDA.CLI.csproj -- --input .\parts.csv --output C:\Libraries\YouEDA --workers 4 --rps 1
```

Import every resistor selected from a local official LCSC catalog export:

```powershell
dotnet run --project .\src\YouEDA.CLI\YouEDA.CLI.csproj -- --category resistor --catalog C:\Data\lcsc-catalog.csv --output C:\Libraries\Resistors --workers 4 --rps 1
```

The second command is intentionally simple once the catalog export is in place. LCSC's documented category-list API requires an approved API key and request signature, so this project does **not** bypass access controls or scrape undocumented catalogue pages. Obtain the CSV/JSON catalog through LCSC's approved API/export process, then use it as the discovery manifest.

Use `--help` for all flags. The default output contains:

```text
youeda.PcbLib
youeda.SchLib
.youeda-bulk\cache\          raw EasyEDA payload cache
.youeda-bulk\completed.txt    parts successfully checkpointed
.youeda-bulk\errors.jsonl     failures that can be retried later
```

For a 100,000-part job, begin with `--workers 4 --rps 1 --checkpoint 100`, review a sample batch in Altium, and only then adjust throughput within your authorized LCSC/EasyEDA request limits. A single 100,000-component native library can become very large; plan disk space, backups, and an eventual sharding policy if Altium becomes slow to open it.

## License and notices

MIT license for this project. The converter depends on [OriginalCircuit.Altium](https://github.com/issus/AltiumSharp) (Apache-2.0). EasyEDA/LCSC endpoint formats and catalog access policies can change without notice. This is an independent project and is not affiliated with Altium, EasyEDA, JLCPCB, or LCSC.
