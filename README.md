# VictoryTool

VictoryTool is a cross-platform .NET 10/Avalonia application for editing
character data and creating `.vrchara` packages and mod projects.

## Build and run

```sh
dotnet restore VictoryTool.slnx
dotnet build VictoryTool.slnx --no-restore
dotnet run --project src/VictoryTool.Desktop/VictoryTool.Desktop.csproj --no-build
```

The application asks the user to select a compatible game-data directory and
does not modify that source directory. Project data is stored in the platform
application-data directory.

The version is shared by `Directory.Build.props`. To update two checkouts to
the same version:

```sh
./scripts/sync-version.sh 1.0.0 /path/to/private /path/to/public
```

On Windows, use `scripts/sync-version.ps1` from PowerShell with the same three
arguments.

## Projects

- `VictoryTool.CfgBin`: structured game-data readers and conservative writers.
- `VictoryTool.G4`: G4TX and NXTCH texture support.
- `VictoryTool.Application`: character models, package persistence and export
  planning.
- `VictoryTool.Desktop`: the Avalonia desktop application.
