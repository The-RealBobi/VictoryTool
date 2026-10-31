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
- Reads and modifies T2B/RDBNP records through the external CFGBINManager API, including typed values, CFG interchange and catalog-managed counts/references.
- Reads G4TX portrait containers and performs fixed-size, fixed-dimension native-template replacement for verified DDS/NXTCH payloads while preserving opaque bytes.

Typed rows can be edited and cloned inside an atomic export staging directory. Game-specific export validation and legality handling remain in VictoryTool; CFGBINManager owns the native formats and their structural maintenance.

## Animation exports

Animation banks with different output routes are generated concurrently, bounded by CPU count and an estimated memory budget. Contributions to the same EVENT bank remain ordered. A single bank can sample multiple clips concurrently without nesting full bank and clip worker pools. Hip proportion adjustment affects vertical motion independently of horizontal travel.

EVENT bank selection uses the incorporated character's runtime skeleton independently of the Blender model template. When those skeletons differ, export recalibrates the animation source rest pose and proportions while retaining the custom rig.

CHR animations use the character's allocated animation family. EVENT animations replace the original event routes and retain their internal resource names. Shared EVENT banks keep the native animation tracks and add the custom rig tracks so other actors can use the same event. Conflicting custom rigs with overlapping bone names are rejected during export.

The export folder contains the contents of the game's `data` directory. When packing with Viola, place those contents under `data` in its input folder and update the current installation's `cpk_list.cfg.bin`. Register EVENT replacements as well as newly added files; otherwise the game can continue resolving the original bank from its CPK. VictoryTool does not include a dump-derived registry that could overwrite registrations from other installed mods.

## Build and run

VictoryTool requires the .NET 10 SDK. CFGBINManager 1.0.4 and the other dependencies are restored from NuGet.org. The schema bundled with CFGBINManager is embedded into the application; no separate schema download is required.

```sh
dotnet restore VictoryTool.slnx
dotnet build src/VictoryTool.Desktop/VictoryTool.Desktop.csproj --no-restore
dotnet run --project src/VictoryTool.Desktop/VictoryTool.Desktop.csproj --no-build
```

The application stores its global dump setting and recovery files under the platform application-data directory. All source dump files remain read-only.

## Projects

- `VictoryTool.CfgBin`: CFGBINManager compatibility adapters, bundled schema access and game-specific delivery/face-scene writers.
- `VictoryTool.G4`: clean-room G4TX/NXTCH metadata and native-template replacement.
- `VictoryTool.Application`: game-independent models, package/project persistence, indexing, comparison, and export planning.
- `VictoryTool.Desktop`: Avalonia management-center UI.

### Generated character resource checks

Before publishing an export, VictoryTool checks the generated model-to-body registration, body model family, skeleton object config, target skeleton and generated CHR-family link. When animation generation is selected, the exported skeleton checksum must match the target rig in the animation profile. The export report includes the validated resource paths, skeleton checksum and joint count. These checks validate resource consistency; they do not confirm in-game rendering or playback.

### Custom equipment selection

Uniforms and shoes use the same custom selection flow: choose the New entry, then browse for a G4MD file. For a new custom uniform, enable Force Uniform to expose that entry. An already selected custom uniform remains accessible when Force Uniform is disabled. Choosing native equipment clears the corresponding custom path; Unequipped is available for uniforms, shoes and gloves. Appearance file dialogs follow the application language and reject unsupported extensions without replacing the current selection.
