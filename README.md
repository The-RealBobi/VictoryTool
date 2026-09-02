# VictoryTool

VictoryTool is a tool for adding your own custom characters to the game as if they were part of the original roster.

You can create a new character based on an existing one and customize parameters such as:

* Name
* Game of origin
* Affinity
* Special Moves
* Gender
* Uniform
* Team

## Installing your character

To install the generated mod, you will need a modding tool such as [Viola](https://github.com/SuperTavor/Viola).

## Obtaining your character in-game

Once the mod has been installed:

1. Go to **Info → Get Promotions**.
2. Find the promotion containing your character's spirit.
3. Summon the character.

## Build and run
dotnet run --project ./src/VictoryTool.Desktop/VictoryTool.Desktop.csproj

To create a standalone executable, use scripts/build.sh on macOS/Linux or
./scripts/build.ps1 on Windows.

The application asks the user to select a compatible game-data directory and
does not modify that source directory. Project data is stored in the platform
application-data directory.

Diagnostic logs are written to the same platform data location. On macOS the
file is `~/Library/Application Support/VictoryTool/VictoryTool.log`; on
Windows it is `%LOCALAPPDATA%\\VictoryTool\\VictoryTool.log`. The file is
reset when the application starts and can be opened from the application.
Detailed parser and asset tracing is opt-in with
`VICTORYTOOL_LOG_LEVEL=debug`.

The version is shared by `Directory.Build.props`. To update two checkouts to
the same version:

```sh
./scripts/sync-version.sh 1.0.0 /path/to/private /path/to/public
```

On Windows, use `scripts/sync-version.ps1` from PowerShell with the same three
arguments.

For a self-contained single-file build, run `scripts/build.sh` on macOS/Linux
or `scripts/build.ps1` on Windows. The output is written under `dist/`.

## Projects

- `VictoryTool.CfgBin`: structured game-data readers and conservative writers.
- `VictoryTool.G4`: G4TX and NXTCH texture support.
- `VictoryTool.Application`: character models, package persistence and export
  planning.
- `VictoryTool.Desktop`: the Avalonia desktop application.
