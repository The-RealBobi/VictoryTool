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

> [!WARNING]
> **VictoryTool may cause irreversible damage to your save data.**
>
> Make sure to create a backup of your save file before using the tool, in case anything goes wrong.
