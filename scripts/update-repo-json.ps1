<#
.SYNOPSIS
    Merges a DalamudPackager plugin manifest into the custom-repository plugin master list.

.DESCRIPTION
    Reads the manifest emitted next to the packaged plugin zip and writes/updates the matching
    store entry in repo.json, the file served as a Dalamud custom repository URL.
    Store-only fields already present in repo.json (IsHide, DownloadCount, testing keys) are preserved.
    Runs on Windows PowerShell 5.1 and PowerShell 7+.

.EXAMPLE
    ./scripts/update-repo-json.ps1 `
        -ManifestPath src/RaidDebrief.Plugin/bin/x64/Release/RaidDebrief/RaidDebrief.json `
        -DownloadUrl https://github.com/ChrisChiu9016/RaidDebrief/releases/download/v0.1.0.0/RaidDebrief.zip
#>
[CmdletBinding()]
param(
    # Manifest produced by the Release build, next to latest.zip.
    [Parameter(Mandatory = $true)][string] $ManifestPath,

    # Absolute URL of the release artifact zip for this version.
    [Parameter(Mandatory = $true)][string] $DownloadUrl,

    # Defaults to repo.json beside this repository's root.
    [string] $RepoJsonPath,

    # Overrides the manifest AssemblyVersion; used when the build is versioned from a release tag.
    [string] $Version,

    [string] $RepoUrl = 'https://github.com/ChrisChiu9016/RaidDebrief'
)

$ErrorActionPreference = 'Stop'

function Read-JsonFile([string] $path) {
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function ConvertTo-Array($value) {
    if ($null -eq $value) { return @() }
    return @($value)
}

if ([string]::IsNullOrWhiteSpace($RepoJsonPath)) {
    $RepoJsonPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) 'repo.json'
}

$ManifestPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $ManifestPath))
$RepoJsonPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $RepoJsonPath))

$manifest = Read-JsonFile $ManifestPath
$internalName = $manifest.InternalName
if ([string]::IsNullOrWhiteSpace($internalName)) {
    throw "Manifest '$ManifestPath' has no InternalName."
}

$assemblyVersion = if ($Version) { $Version } else { $manifest.AssemblyVersion }
if ([string]::IsNullOrWhiteSpace($assemblyVersion)) {
    throw "No AssemblyVersion available for '$internalName'."
}

$entries = @()
$existing = $null
if (Test-Path -LiteralPath $RepoJsonPath) {
    $entries = ConvertTo-Array (Read-JsonFile $RepoJsonPath)
    $existing = $entries | Where-Object { $_.InternalName -eq $internalName } | Select-Object -First 1
}

function Get-Kept([string] $name, $fallback) {
    if ($null -ne $existing -and $null -ne $existing.PSObject.Properties[$name] -and $null -ne $existing.$name) {
        return $existing.$name
    }
    return $fallback
}

$entry = [ordered] @{
    Author                 = $manifest.Author
    Name                   = $manifest.Name
    InternalName           = $internalName
    Punchline              = $manifest.Punchline
    Description            = $manifest.Description
    AssemblyVersion        = $assemblyVersion
    TestingAssemblyVersion = Get-Kept 'TestingAssemblyVersion' $null
    RepoUrl                = if ($manifest.RepoUrl) { $manifest.RepoUrl } else { $RepoUrl }
    ApplicableVersion      = if ($manifest.ApplicableVersion) { $manifest.ApplicableVersion } else { 'any' }
    DalamudApiLevel        = $manifest.DalamudApiLevel
    Tags                   = ConvertTo-Array $manifest.Tags
    CategoryTags           = ConvertTo-Array $manifest.CategoryTags
    LoadPriority           = $manifest.LoadPriority
    IconUrl                = if ($manifest.IconUrl) { $manifest.IconUrl } else { Get-Kept 'IconUrl' $null }
    ImageUrls              = if ($manifest.ImageUrls) { ConvertTo-Array $manifest.ImageUrls } else { ConvertTo-Array (Get-Kept 'ImageUrls' $null) }
    Changelog              = $manifest.Changelog
    AcceptsFeedback        = [bool] $manifest.AcceptsFeedback
    IsHide                 = [bool] (Get-Kept 'IsHide' $false)
    IsTestingExclusive     = [bool] (Get-Kept 'IsTestingExclusive' $false)
    DownloadCount          = [long] (Get-Kept 'DownloadCount' 0)
    DownloadLinkInstall    = $DownloadUrl
    DownloadLinkUpdate     = $DownloadUrl
    DownloadLinkTesting    = Get-Kept 'DownloadLinkTesting' $DownloadUrl
    LastUpdate             = [long] [System.DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}

# Optional keys stay out of the store entry entirely when they carry no value.
foreach ($key in @($entry.Keys)) {
    $value = $entry[$key]
    if ($null -eq $value -or ($value -is [string] -and $value -eq '') -or ($value -is [array] -and $value.Count -eq 0)) {
        $entry.Remove($key)
    }
}

$merged = @()
$replaced = $false
foreach ($item in $entries) {
    if ($item.InternalName -eq $internalName) {
        $merged += [pscustomobject] $entry
        $replaced = $true
    }
    else {
        $merged += $item
    }
}
if (-not $replaced) {
    $merged += [pscustomobject] $entry
}

# Windows PowerShell mangles arrays passed to ConvertTo-Json (single entries are unwrapped, multiple
# entries gain "value"/"Count" wrappers), so serialize one entry at a time and assemble the array here.
$newLine = [Environment]::NewLine
$serialized = @()
foreach ($item in $merged) {
    $objectJson = ConvertTo-Json -InputObject $item -Depth 8
    $lines = $objectJson -split "`r?`n" | ForEach-Object { '  ' + $_ }
    $serialized += ($lines -join $newLine)
}
$json = '[' + $newLine + ($serialized -join (',' + $newLine)) + $newLine + ']'

[System.IO.File]::WriteAllText($RepoJsonPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding $false))

Write-Host "Updated $RepoJsonPath -> $internalName $assemblyVersion ($DownloadUrl)"
