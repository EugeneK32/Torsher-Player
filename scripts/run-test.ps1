param(
  [Parameter(Mandatory=$true)][string]$Url,
  [string]$Title = 'Torsher Player test',
  [double]$UiScale = 1.8,
  [switch]$Windowed
)
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\win-x64-portable\TorsherPlayer.exe'
if (-not (Test-Path $exe)) { throw 'Сначала запустите build.cmd' }
$args = @('--url',$Url,'--title',$Title,'--media-key','manual:test','--ui-scale',$UiScale.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($Windowed) { $args += '--windowed' }
& $exe @args
