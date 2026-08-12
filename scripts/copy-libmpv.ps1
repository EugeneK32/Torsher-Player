param(
    [Parameter(Mandatory=$true)][string]$PackageDir,
    [Parameter(Mandatory=$true)][string]$DestinationDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PackageDir)) {
    throw "libmpv NuGet package directory not found: $PackageDir"
}

$candidates = @(Get-ChildItem $PackageDir -Recurse -File -Filter '*.dll' |
    Where-Object { $_.Name -match '(?i)^(lib)?mpv(?:-\d+)?\.dll$|^mpv-\d+\.dll$' })

if ($candidates.Count -eq 0) {
    throw "Endpne.LibMPV.Windows contains no libmpv runtime DLL under: $PackageDir"
}

$x64 = @($candidates | Where-Object { $_.FullName -match '(?i)[\\/](win-)?x64[\\/]' })
if ($x64.Count -eq 1) {
    $anchor = $x64[0]
}
elseif ($x64.Count -gt 1) {
    # Prefer the shortest path: it is normally the canonical release/native payload.
    $anchor = $x64 | Sort-Object { $_.FullName.Length } | Select-Object -First 1
}
elseif ($candidates.Count -eq 1) {
    $anchor = $candidates[0]
}
else {
    throw "Cannot unambiguously select x64 libmpv DLL. Candidates:`n$($candidates.FullName -join "`n")"
}

New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null

# Copy all runtime DLLs sitting next to libmpv; some builds keep codec/runtime
# dependencies beside the main library.
Get-ChildItem $anchor.Directory.FullName -File -Filter '*.dll' | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $DestinationDir $_.Name) -Force
}

Write-Host "libmpv runtime: $($anchor.FullName)"
Write-Host "libmpv copied to: $DestinationDir"
