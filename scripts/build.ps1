param(
    [ValidateSet('win-x64','win-arm64')]
    [string]$Rid = 'win-x64',
    [ValidateSet('portable','single','all')]
    [string]$Target = 'portable',
    [switch]$SkipNativeFetch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TorsherPlayer\TorsherPlayer.csproj'
$dist = Join-Path $root 'dist'

if (-not $SkipNativeFetch) {
    & (Join-Path $PSScriptRoot 'fetch-native.ps1') -Rid $Rid
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw '.NET 10 SDK is required for source builds. Install it from dotnet.microsoft.com.' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

& dotnet restore $project -r $Rid
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& dotnet build $project -c Release -r $Rid --no-restore
if ($LASTEXITCODE) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

function Publish([bool]$SingleFile, [string]$Folder) {
    $out = Join-Path $dist $Folder
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $single = if ($SingleFile) { 'true' } else { 'false' }
    & dotnet publish $project -c Release -r $Rid --no-restore --self-contained true `
        -p:PublishSingleFile=$single `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false -o $out
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    if (-not (Test-Path (Join-Path $out 'TorsherPlayer.exe'))) { throw "Publish output has no TorsherPlayer.exe: $out" }
    Write-Host "Published: $out"
}

if ($Target -in @('portable','all')) { Publish $false "$Rid-portable" }
if ($Target -in @('single','all')) { Publish $true "$Rid-single" }
