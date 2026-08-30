# VictoryTool

VictoryTool is a cross-platform .NET 10/Avalonia application for editing character data, creating `.vrchara` packages, and composing reproducible multi-character mod projects.

The application validates a compatible game-data directory without modifying it, supports localized character editing, previews portraits and uniforms, and writes reproducible project exports.

## Build and run

```sh
dotnet restore VictoryTool.slnx
dotnet build VictoryTool.slnx --no-restore
dotnet run --project src/VictoryTool.Desktop/VictoryTool.Desktop.csproj --no-build
```

To create a distributable build, use `scripts/build.sh` on macOS/Linux or
`scripts/build.ps1` on Windows. Both scripts read the version from
`Directory.Build.props` and place the ZIP in `dist/`.

The application stores its global dump setting and recovery files under the platform application-data directory. All source dump files remain read-only.

## Projects

- `VictoryTool.CfgBin`: structured game-data readers and conservative writers.
- `VictoryTool.G4`: G4TX and NXTCH texture support.
- `VictoryTool.Application`: character models, package persistence and export planning.
- `VictoryTool.Desktop`: the Avalonia desktop application.
