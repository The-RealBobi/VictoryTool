[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Version,
    [Parameter(Mandatory = $true, Position = 1)]
    [string] $PrivateRepository,
    [Parameter(Mandatory = $true, Position = 2)]
    [string] $PublicRepository
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Invalid version '$Version'; expected X.Y.Z."
}

foreach ($RepositoryRoot in @($PrivateRepository, $PublicRepository)) {
    $props = Join-Path $RepositoryRoot "Directory.Build.props"
    if (-not (Test-Path (Join-Path $RepositoryRoot ".git"))) {
        throw "Not a git repository: $RepositoryRoot"
    }
    if (-not (Test-Path $props)) {
        throw "Missing Directory.Build.props: $props"
    }

    $content = Get-Content -Raw -LiteralPath $props
    if ($content -notmatch '<VersionPrefix>[0-9]+\.[0-9]+\.[0-9]+</VersionPrefix>') {
        throw "Missing or invalid VersionPrefix in $props"
    }
}

foreach ($RepositoryRoot in @($PrivateRepository, $PublicRepository)) {
    $props = Join-Path $RepositoryRoot "Directory.Build.props"
    $content = Get-Content -Raw -LiteralPath $props
    $content = [regex]::Replace(
        $content,
        '<VersionPrefix>[0-9]+\.[0-9]+\.[0-9]+</VersionPrefix>',
        "<VersionPrefix>$Version</VersionPrefix>",
        1
    )
    Set-Content -LiteralPath $props -Value $content -NoNewline
}

Write-Output "Synchronized VersionPrefix=$Version"
