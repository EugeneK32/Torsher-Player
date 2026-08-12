param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'native\native-dependencies.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$property = $manifest.windows.PSObject.Properties[$Rid]
$spec = if ($property) { $property.Value } else { $null }
if (-not $spec) { throw "No native dependency manifest for $Rid" }

$cache = Join-Path $root ".cache\native\$Rid"
$target = Join-Path $root "native\$Rid"
$temp = Join-Path $cache 'extract'
New-Item -ItemType Directory -Force -Path $cache, $target | Out-Null

function Get-VerifiedFile([string]$Name, [string]$Url, [string]$Sha256) {
    $extension = [IO.Path]::GetExtension(([Uri]$Url).AbsolutePath)
    $path = Join-Path $cache "$Name$extension"
    if ($Force -or -not (Test-Path $path)) {
        Write-Host "Downloading $Name..."
        Invoke-WebRequest -Uri $Url -OutFile $path
    }
    $actual = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        Remove-Item $path -Force -ErrorAction SilentlyContinue
        throw "$Name SHA-256 mismatch. Expected $Sha256, got $actual"
    }
    return $path
}

function Resolve-7Zip {
    foreach ($name in @('7z.exe', '7z', '7zz.exe', '7zz')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    $common = @(
        "$env:ProgramFiles\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
    )
    foreach ($path in $common) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    throw '7-Zip is required to unpack the pinned mpv .7z archive. Install 7-Zip and rerun.'
}

$mpvArchive = Get-VerifiedFile 'mpv' $spec.mpv.url $spec.mpv.sha256
$sdlArchive = Get-VerifiedFile 'sdl3' $spec.sdl3.url $spec.sdl3.sha256

if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
$mpvTemp = Join-Path $temp 'mpv'
$sdlTemp = Join-Path $temp 'sdl3'
New-Item -ItemType Directory -Force -Path $mpvTemp, $sdlTemp | Out-Null

$sevenZip = Resolve-7Zip
& $sevenZip x '-y' "-o$mpvTemp" $mpvArchive | Out-Host
if ($LASTEXITCODE) { throw "7-Zip failed with exit code $LASTEXITCODE" }
Expand-Archive -Path $sdlArchive -DestinationPath $sdlTemp -Force

# Clear generated binaries but keep .gitkeep.
Get-ChildItem $target -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne '.gitkeep' } |
    Remove-Item -Force

$mpvDlls = @(Get-ChildItem $mpvTemp -Recurse -File -Filter '*.dll')
$mpvAnchor = @($mpvDlls | Where-Object { $_.Name -match '(?i)^(lib)?mpv(?:-\d+)?\.dll$|^mpv-\d+\.dll$' }) | Select-Object -First 1
if (-not $mpvAnchor) {
    throw "Pinned mpv archive contains no libmpv runtime DLL. Files:`n$($mpvDlls.FullName -join "`n")"
}

# Copy all DLLs sitting in the same directory as libmpv. This preserves any
# native codec/runtime dependencies used by that particular Windows build.
Get-ChildItem $mpvAnchor.Directory.FullName -File -Filter '*.dll' |
    Copy-Item -Destination $target -Force

$sdlDll = Get-ChildItem $sdlTemp -Recurse -File -Filter 'SDL3.dll' | Select-Object -First 1
if (-not $sdlDll) { throw 'Pinned SDL3 archive contains no SDL3.dll' }
Copy-Item $sdlDll.FullName -Destination (Join-Path $target 'SDL3.dll') -Force

$hasMpv = Get-ChildItem $target -File | Where-Object { $_.Name -match '(?i)^(lib)?mpv(?:-\d+)?\.dll$|^mpv-\d+\.dll$' }
if (-not $hasMpv) { throw 'libmpv copy verification failed' }
if (-not (Test-Path (Join-Path $target 'SDL3.dll'))) { throw 'SDL3 copy verification failed' }

Write-Host "Native dependencies ready: $target"
Get-ChildItem $target -File | Select-Object Name, Length | Format-Table -AutoSize
