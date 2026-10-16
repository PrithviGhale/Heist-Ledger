param(
    [string]$OutputDirectory = 'artifacts/updater-release',
    [switch]$DownloadPrevious
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    [xml]$project = Get-Content 'src/HeistLedger/GTAEarningsTracker.csproj'
    $version = [string]$project.Project.PropertyGroup.Version
    $outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
    $publishPath = Join-Path $outputPath 'publish'
    $releasesPath = Join-Path $outputPath 'Releases'
    if (Test-Path -LiteralPath (Join-Path $releasesPath "HeistLedger-$version-full.nupkg")) {
        throw 'This output already contains the current version. Use a new -OutputDirectory for a local rebuild, or increase the project version for a new release.'
    }
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the updater packaging tool.' }
    if ($DownloadPrevious) {
        dotnet vpk download github --repoUrl 'https://github.com/PrithviGhale/Heist-Ledger' --channel win --outputDir $releasesPath
        if ($LASTEXITCODE -ne 0) { throw 'Could not download previous updater packages. Omit -DownloadPrevious for the first updater release.' }
    }
    dotnet publish 'src/HeistLedger/GTAEarningsTracker.csproj' -p:PublishProfile=UpdaterWindowsX64 --artifacts-path (Join-Path $outputPath 'build') -o $publishPath -p:NuGetAudit=false /nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $notes = Join-Path $projectRoot "docs/RELEASE_NOTES_v$version.md"
    dotnet vpk pack --packId HeistLedger --packTitle 'Heist Ledger' --packAuthors 'Prithvi Ghale' --packVersion $version --packDir $publishPath --mainExe HeistLedger.exe --icon 'src/HeistLedger/Assets/AppIcon.ico' --channel win --runtime win-x64 --framework vcredist143-x64 --releaseNotes $notes --noPortable --outputDir $releasesPath
    if ($LASTEXITCODE -ne 0) { throw 'Updater packaging failed.' }
    Get-ChildItem $releasesPath -File | Select-Object Name,Length
    Write-Host "Local installer and update feed ready in $releasesPath. Nothing has been uploaded."
} finally { Pop-Location }
