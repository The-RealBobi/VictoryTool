[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = $env:RID,
    [string]$Configuration = $(if ($env:CONFIGURATION) { $env:CONFIGURATION } else { "Release" }),
    [string]$OutputRoot = $(if ($env:OUTPUT_ROOT) { $env:OUTPUT_ROOT } else { Join-Path $PSScriptRoot "..\dist" })
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repositoryRoot "src\VictoryTool.Desktop\VictoryTool.Desktop.csproj"
$props = Get-Content (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$versionMatch = [regex]::Match($props, '<VersionPrefix>(?<version>[0-9]+\.[0-9]+\.[0-9]+)</VersionPrefix>')
if (-not $versionMatch.Success) { throw "Invalid VersionPrefix in Directory.Build.props." }
$version = $versionMatch.Groups["version"].Value
$flavor = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) { "framework-dependent" } else { $RuntimeIdentifier }
$publishRoot = Join-Path $OutputRoot "v$version\$flavor"
$archive = Join-Path $OutputRoot "VictoryTool-$version-$flavor.zip"

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
dotnet restore $project
$publishArgs = @($project, "-c", $Configuration, "--no-restore", "-o", $publishRoot)
if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $publishArgs += @("-r", $RuntimeIdentifier, "--self-contained", "true")
}
dotnet publish @publishArgs

if (Test-Path $archive) { Remove-Item -Force $archive }
Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archive
Write-Output "Created $archive"
