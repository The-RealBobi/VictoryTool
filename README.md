# VictoryTool

VictoryTool is a cross-platform .NET 10/Avalonia workspace for researching INAZUMA ELEVEN: Victory Road character data, creating single-character `.vrchara` packages, and composing reproducible multi-character mod projects.

## Current capabilities

- Validates an extracted dump containing `common/gamedata` without modifying it.
- Indexes character identity, variants, skills and localized names/descriptions from CFGBIN, together with PC/DX11 or Switch/NX portrait resources.
- Browses the complete CFGBIN inventory in read-only mode.
- Clones an immutable character inventory entry into a symbolic character draft.
- Reads and writes one-character `.vrchara` ZIP packages with `manifest.json`.
- Maintains ordered, enabled/disabled batch entries and atomic `.vrproject` persistence.
- Previews known candidate dependency families and reports unresolved graphs plus separate T2B, RDBNP and localization blockers.
- Parses structured T2B records, performs byte-identical unmodified roundtrips and safely replaces existing numeric values.
- Reads G4TX portrait containers and performs fixed-size, fixed-dimension native-template replacement for verified DDS/NXTCH payloads while preserving opaque bytes.

Typed localized T2B rows can now be appended inside an atomic export staging directory. Character/gameplay rows, Shop rows, RDBNP writing, image encoding and a fully game-ready export remain intentionally blocked until their independent evidence gates pass.

## Build and run

```sh
dotnet restore VictoryTool.slnx
dotnet test VictoryTool.slnx --no-restore
dotnet run --project src/VictoryTool.Desktop/VictoryTool.Desktop.csproj --no-build
```

The application stores its global dump setting and recovery files under the platform application-data directory. All source dump files remain read-only.

## Projects

- `VictoryTool.CfgBin`: clean-room structured T2B reader and conservative value writer.
- `VictoryTool.G4`: clean-room G4TX/NXTCH metadata and native-template replacement.
- `VictoryTool.Application`: game-independent models, package/project persistence, indexing, comparison, and export planning.
- `VictoryTool.Desktop`: Avalonia management-center UI.
