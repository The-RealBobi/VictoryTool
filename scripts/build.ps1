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
if (Test-Path $archive) { Remove-Item -Force $archive }

if ($RuntimeIdentifier -like "osx-*") {
    $publishedFiles = @(Get-ChildItem -Path $publishRoot -File -Recurse)
    if ($publishedFiles.Count -ne 1) {
        $paths = $publishedFiles | ForEach-Object { $_.FullName }
        throw "macOS single-file publish produced $($publishedFiles.Count) files in $publishRoot.`n$($paths -join "`n")"
    }

    $appRoot = Join-Path $publishRoot "VictoryTool.app"
    $macOsRoot = Join-Path $appRoot "Contents\MacOS"
    $resourcesRoot = Join-Path $appRoot "Contents\Resources"
    New-Item -ItemType Directory -Force -Path $macOsRoot, $resourcesRoot | Out-Null
    Move-Item -Force $publishedFiles[0].FullName (Join-Path $macOsRoot "VictoryTool")
    Copy-Item (Join-Path $repositoryRoot "src\VictoryTool.Desktop\Assets\AppIcon.icns") (Join-Path $resourcesRoot "AppIcon.icns")
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDisplayName</key>
    <string>VictoryTool</string>
    <key>CFBundleExecutable</key>
    <string>VictoryTool</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.victorytool.desktop</string>
    <key>CFBundleName</key>
    <string>VictoryTool</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
</dict>
</plist>
"@
    Set-Content -Path (Join-Path $appRoot "Contents\Info.plist") -Value $plist -Encoding UTF8
    if ($IsMacOS) { & chmod +x (Join-Path $macOsRoot "VictoryTool") }
    Compress-Archive -Path $appRoot -DestinationPath $archive
}
else {
    $publishedFiles = @(Get-ChildItem -Path $publishRoot -File -Recurse)
    if ($publishedFiles.Count -ne 1) {
        $paths = $publishedFiles | ForEach-Object { $_.FullName }
        throw "Single-file publish produced $($publishedFiles.Count) files in $publishRoot.`n$($paths -join "`n")"
    }
    Compress-Archive -Path $publishedFiles[0].FullName -DestinationPath $archive
}
Write-Output "Created $archive"
