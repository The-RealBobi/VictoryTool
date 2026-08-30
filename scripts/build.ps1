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
$architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
    "Arm64" { "arm64"; break }
    "X64" { "x64"; break }
    default { throw "Could not infer the CPU architecture; pass a runtime identifier explicitly." }
}
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $platform = if ($env:OS -eq "Windows_NT" -or $IsWindows) { "win" }
        elseif ($IsMacOS) { "osx" }
        elseif ($IsLinux) { "linux" }
        else { throw "Could not infer the operating system; pass a runtime identifier explicitly." }
    $RuntimeIdentifier = "$platform-$architecture"
}
$flavor = $RuntimeIdentifier
$publishRoot = Join-Path $OutputRoot "v$version\$flavor"
$archive = Join-Path $OutputRoot "VictoryTool-$version-$flavor.zip"

if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
dotnet restore $project --runtime $RuntimeIdentifier
$publishArgs = @(
    $project, "-c", $Configuration, "--no-restore", "-r", $RuntimeIdentifier,
    "--self-contained", "true", "-o", $publishRoot,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeAllContentForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)
dotnet publish @publishArgs

Get-ChildItem -Path $publishRoot -File -Recurse |
    Where-Object { $_.Extension -in @(".pdb", ".dbg") } |
    Remove-Item -Force
$publishedFiles = @(Get-ChildItem -Path $publishRoot -File -Recurse)
if ($publishedFiles.Count -ne 1) {
    $paths = $publishedFiles | ForEach-Object { $_.FullName }
    throw "Single-file publish produced $($publishedFiles.Count) files in $publishRoot.`n$($paths -join "`n")"
}
if (Test-Path $archive) { Remove-Item -Force $archive }
Compress-Archive -Path $publishedFiles[0].FullName -DestinationPath $archive
Write-Output "Created $archive"
