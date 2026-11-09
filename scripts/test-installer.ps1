# Destructive installer testing is restricted to a disposable GitHub-hosted Windows runner.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Run installer lifecycle tests only in a disposable GitHub-hosted runner.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $root
. (Join-Path $PSScriptRoot 'installer-test-common.ps1')
function Find-Msi([string]$Directory) {
    $packages = @(Get-ChildItem -LiteralPath $Directory -Filter 'Airplay*.msi' -File)
    if ($packages.Count -ne 1) { throw "Expected exactly one Airplay MSI in $Directory; found $($packages.Count)." }
    return $packages[0].FullName
}
$msi = Find-Msi 'dist'
$exeHash = (Get-FileHash dist/Airplay.exe).Hash
$msiHash = (Get-FileHash $msi).Hash
# Resolve the predecessor before touching user data or installing any package.
# Upgrade from this fork's own published releases; upstream AirFlash MSIs are a different product.
$releaseRepo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'weixuanng/AirFlash_Wei' }
$releases = gh api "repos/$releaseRepo/releases" | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Cannot inspect preceding releases.' }
$fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path dist/Airplay.exe))
$current = [Version]('{0}.{1}.{2}' -f $fileInfo.FileMajorPart,$fileInfo.FileMinorPart,$fileInfo.FileBuildPart)
$previous = Select-AirFlashInstallerPredecessor -Releases $releases -CurrentVersion $current
$fixtureVersion = if (-not $previous) { Get-AirFlashInstallerFixtureVersion -CurrentVersion $current }
# GetFullPath gives Windows separators, which the firewall filter's exact path match needs.
$installed = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Airplay/Airplay.exe'))
$installedEngine = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Airplay/airflash-engine.exe'))
$engineHash = (Get-FileHash native/airflash-engine/target/release/airflash-engine.exe).Hash
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# Find the MSI's rule by name, then confirm it names the installed engine (case-insensitive path).
function Firewall-Rules {
    @(Get-NetFirewallRule -DisplayName 'Airplay audio engine' -ErrorAction SilentlyContinue | Where-Object {
        ($_ | Get-NetFirewallApplicationFilter).Program -ieq $installedEngine
    })
}
function Check-Removed([string]$Stage) {
    if ((Test-Path $installed) -or (Test-Path $installedEngine) -or @(Entries).Count -ne 0) { throw "$Stage left application or registration." }
    if (@(Firewall-Rules).Count -ne 0) { throw "$Stage left the engine firewall rule." }
    if ($null -ne (Get-ItemProperty $runKey -Name Airplay -ErrorAction SilentlyContinue)) { throw "$Stage left the startup entry." }
}
$data = Join-Path $env:APPDATA 'Airplay'
New-Item $data -ItemType Directory -Force | Out-Null
$sentinel = Join-Path $data 'release-test-sentinel.txt'
Set-Content $sentinel 'preserve-user-data'
New-Item artifacts/installer-test -ItemType Directory -Force | Out-Null
function Run-Msi([string[]]$Arguments, [string]$Name) {
    $log = Join-Path $root "artifacts/installer-test/$Name.log"
    $process = Start-Process msiexec.exe -ArgumentList ($Arguments + @('/qn','/norestart','/L*v',"`"$log`"")) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0,3010)) { Get-Content $log -Tail 60; throw "MSI $Name failed: $($process.ExitCode)" }
}
function Entries {
    @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -EQ 'Airplay')
}
function Check-Installed {
    if (@(Entries).Count -ne 1) { throw 'Expected exactly one uninstall entry.' }
    if ((Get-FileHash $installed).Hash -ne $exeHash) { throw 'Installed EXE hash mismatch.' }
    if ((Get-FileHash $installedEngine).Hash -ne $engineHash) { throw 'Installed engine hash mismatch.' }
    if (@(Firewall-Rules).Count -lt 1) { throw 'Engine firewall rule missing.' }
    $run = (Get-ItemProperty $runKey -Name Airplay -ErrorAction SilentlyContinue).Airplay
    if ($run -ne ('"' + $installed + '" --startup')) { throw "Startup entry mismatch: $run" }
    if (-not (Test-Path $sentinel)) { throw 'User data was deleted.' }
}
Run-Msi @('/i',"`"$msi`"") 'fresh-install'
Check-Installed
Run-Msi @('/fa',"`"$msi`"") 'repair'
Check-Installed
Run-Msi @('/x',"`"$msi`"") 'first-uninstall'
Check-Removed 'First uninstall'

# Choose a strictly lower version than this MSI's executable, including for
# unofficial validation builds that can lag today's public release.
$oldDir = Join-Path $root 'artifacts/installer-test/previous'
New-Item $oldDir -ItemType Directory -Force | Out-Null
if ($previous) {
    gh release download $previous.tag_name --repo $releaseRepo --pattern 'Airplay*.msi' --dir $oldDir
    if ($LASTEXITCODE) { throw 'Cannot download previous MSI.' }
} else {
    # WiX can hardlink its output from obj. Never reuse that intermediate tree for a different package.
    $fixtureProject = Join-Path $root 'artifacts/installer-test/fixture-project'
    New-Item $fixtureProject -ItemType Directory -Force | Out-Null
    foreach ($file in @('AirFlash.Package.wixproj','Installer.props','Package.wxs','License.rtf','Assets')) {
        Copy-Item (Join-Path $root "installer/$file") (Join-Path $fixtureProject $file) -Recurse
    }
    & ./scripts/dotnet.ps1 build (Join-Path $fixtureProject 'AirFlash.Package.wixproj') -c Release "-p:ProductVersion=$fixtureVersion" "-p:AppExe=$root/dist/Airplay.exe" "-p:AppIcon=$root/desktop/AirFlash.App/Assets/app.ico" "-p:UiLicenseFile=$root/installer/License.rtf" "-p:EngineExe=$root/native/airflash-engine/target/release/airflash-engine.exe" "-p:OutputPath=$oldDir/" '-p:DebugType=none'
    if ($LASTEXITCODE) { throw 'Cannot build upgrade fixture.' }
}
if ((Get-FileHash $msi).Hash -ne $msiHash -or (Get-FileHash dist/Airplay.exe).Hash -ne $exeHash) { throw 'Fixture build changed release assets.' }
$oldMsi = Find-Msi $oldDir
Run-Msi @('/i',"`"$oldMsi`"") 'old-install'
$oldCode = (Entries).PSChildName
Run-Msi @('/i',"`"$msi`"") 'upgrade'
Check-Installed
if ((Entries).PSChildName -eq $oldCode) { throw 'Upgrade did not replace old MSI registration.' }
Run-Msi @('/fa',"`"$msi`"") 'upgrade-repair'
Check-Installed
Run-Msi @('/x',"`"$msi`"") 'final-uninstall'
Check-Removed 'Uninstall'
# After distributing a preview, the next stable MSI must upgrade it cleanly.
if ($env:RELEASE_CHANNEL -eq 'stable') {
    $preview = $releases | Where-Object { $_.prerelease -and $_.tag_name -match '^v(\d+\.\d+\.\d+)-rc\.1$' -and [Version]$Matches[1] -lt $current } | Select-Object -First 1
    if ($preview) {
        $previewDir = Join-Path $root 'artifacts/installer-test/preview'
        New-Item $previewDir -ItemType Directory -Force | Out-Null
        gh release download $preview.tag_name --repo $releaseRepo --pattern 'Airplay*.msi' --dir $previewDir
        if ($LASTEXITCODE) { throw 'Cannot download preceding preview MSI.' }
        $previewMsi = Find-Msi $previewDir
        Run-Msi @('/i',"`"$previewMsi`"") 'preview-install'
        $previewCode = (Entries).PSChildName
        Run-Msi @('/i',"`"$msi`"") 'preview-upgrade'
        Check-Installed
        if ((Entries).PSChildName -eq $previewCode) { throw 'Stable MSI did not replace preview registration.' }
        Run-Msi @('/x',"`"$msi`"") 'preview-upgrade-uninstall'
        Check-Removed 'Preview upgrade uninstall'
    }
}
foreach ($folder in @([Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('Programs'),[Environment]::GetFolderPath('CommonPrograms'))) {
    if (Get-ChildItem $folder -Filter Airplay.lnk -Recurse -ErrorAction SilentlyContinue) { throw 'Uninstall left a shortcut.' }
}
if ((Get-Content $sentinel -Raw).Trim() -ne 'preserve-user-data') { throw 'User data changed.' }
if ((Get-FileHash $msi).Hash -ne $msiHash) { throw 'Installer tests changed release MSI.' }
Write-Host 'MSI fresh install, repair, upgrade, uninstall, engine, firewall rule, startup entry, hashes and user-data preservation passed.'
