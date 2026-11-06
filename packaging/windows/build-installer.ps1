param(
  [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\dist\win\DUSMP-Launcher-Setup.exe'),
  [string]$LauncherDir = '',
  [string]$UpdaterDir = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $LauncherDir) { $LauncherDir = Join-Path $repoRoot 'dist\win\launcher' }
if (-not $UpdaterDir) { $UpdaterDir = Join-Path $repoRoot 'dist\win\updater' }
foreach ($requiredFile in @((Join-Path $launcherDir 'DusmpLauncher.exe'), (Join-Path $updaterDir 'DusmpUpdater.exe'))) {
  if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
    throw "Missing publish output: $requiredFile. Publish the Windows launcher and updater first."
  }
}

$iexpress = Join-Path $env:WINDIR 'System32\iexpress.exe'
if (-not (Test-Path -LiteralPath $iexpress)) { throw 'Windows IExpress is required to create the self-extracting installer.' }

$stage = Join-Path ([IO.Path]::GetTempPath()) ('dusmp-installer-' + [guid]::NewGuid().ToString('N'))
$stageFiles = Join-Path $stage 'payload'
$sedPath = Join-Path $stage 'installer.sed'
$stagedOutput = Join-Path $stage 'DUSMP-Launcher-Setup.exe'
$output = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path $stageFiles, (Split-Path -Parent $output) | Out-Null

try {
  Copy-Item (Join-Path $launcherDir 'DusmpLauncher.exe') $stageFiles
  Copy-Item (Join-Path $updaterDir 'DusmpUpdater.exe') $stageFiles
  Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $stageFiles
  Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $stageFiles

  $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvSize=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=DUSMP Launcher is ready.
TargetName=$stagedOutput
FriendlyName=DUSMP Launcher Setup
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="DusmpLauncher.exe"
FILE1="DusmpUpdater.exe"
FILE2="install.ps1"
FILE3="uninstall.ps1"
[SourceFiles]
SourceFiles0=$stageFiles\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
"@
  Set-Content -LiteralPath $sedPath -Value $sed -Encoding ASCII
  $process = Start-Process -FilePath $iexpress -ArgumentList @('/N', '/Q', $sedPath) -WindowStyle Hidden -Wait -PassThru
  if ($process.ExitCode -ne 0) {
    Get-ChildItem -LiteralPath $stage -Filter '*.RPT' -Force | ForEach-Object { Get-Content -LiteralPath $_.FullName }
    Get-ChildItem -LiteralPath $stage -Filter '*.DDF' -Force | ForEach-Object { Get-Content -LiteralPath $_.FullName }
    throw "IExpress failed with exit code $($process.ExitCode)."
  }
  if (-not (Test-Path -LiteralPath $stagedOutput -PathType Leaf)) { throw "Installer was not created at $stagedOutput." }
  Copy-Item -LiteralPath $stagedOutput -Destination $output -Force
  Write-Output $output
} finally {
  $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
  $resolvedStage = [IO.Path]::GetFullPath($stage)
  if ($resolvedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
      [IO.Path]::GetFileName($resolvedStage).StartsWith('dusmp-installer-', [StringComparison]::Ordinal)) {
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force -ErrorAction SilentlyContinue
  }
}
