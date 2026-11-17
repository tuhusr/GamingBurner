<#
  Builds GamingBurner and publishes it as a GitHub release. Installed copies see it and offer the update.

  Usage:
    .\release.ps1 -Version 1.0.1 -Notes "Fixed the overlay position"
    .\release.ps1 -Version 1.0.1 -LocalOnly      # build the installer in .\Releases without uploading

  Needs: the .NET SDK, the GitHub CLI signed in (gh auth login), and vpk (installed automatically).
  Versions must go up each release (1.0.0 -> 1.0.1 -> 1.1.0 ...).
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Notes = "",
    [switch]$LocalOnly
)
$ErrorActionPreference = 'Stop'
$repo = 'https://github.com/tuhusr/GamingBurner'
$vpkVersion = '1.2.161'   # keep in step with the Velopack package in GamingBurner.csproj
Set-Location $PSScriptRoot

$vpk = "$env:USERPROFILE\.dotnet\tools\vpk.exe"
if (-not (Test-Path $vpk)) { dotnet tool install -g vpk --version $vpkVersion | Out-Null }

# 1. Stamp the version into the project.
$proj = Join-Path $PSScriptRoot 'GamingBurner.csproj'
$text = [IO.File]::ReadAllText($proj) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText($proj, $text, (New-Object Text.UTF8Encoding($false)))

# 2. Build a self-contained copy, so friends do not need .NET installed.
Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish GamingBurner.csproj -c Release -r win-x64 --self-contained true -o publish -p:Version=$Version -nologo -v q
if ($LASTEXITCODE) { throw "Build failed." }

# 3. Release notes shown in the update prompt.
if (-not $Notes) { $Notes = "GamingBurner $Version" }
$notesFile = Join-Path $env:TEMP "gamingburner-notes-$Version.md"
[IO.File]::WriteAllText($notesFile, $Notes)

if (-not $LocalOnly) {
    $token = (gh auth token).Trim()
    if (-not $token) { throw "Sign in to GitHub first: gh auth login" }

    # Commit the version bump so the release points at the code it was built from.
    git add -A
    git diff --cached --quiet
    if ($LASTEXITCODE) { git commit -m "Release $Version" | Out-Null }
    git push origin main
    if ($LASTEXITCODE) { throw "git push failed." }

    # Fetch the previous release so the update can be a small delta instead of the whole app.
    & $vpk download github --repoUrl $repo --token $token -o Releases
}

# 4. Package: Setup.exe, a portable zip, and the update files.
& $vpk pack -u GamingBurner -v $Version -p publish -e GamingBurner.exe -i Assets\GamingBurner.ico `
    --packTitle GamingBurner --packAuthors tuhusr --releaseNotes $notesFile -o Releases
if ($LASTEXITCODE) { throw "Packaging failed." }

if ($LocalOnly) { Write-Host "Built Releases\GamingBurner-win-Setup.exe (not uploaded)."; return }

# 5. Publish the GitHub release; installed copies will find it.
& $vpk upload github -o Releases --repoUrl $repo --token $token --publish `
    --releaseName "GamingBurner $Version" --tag "v$Version" --targetCommitish main
if ($LASTEXITCODE) { throw "Upload failed." }
Write-Host "Published $repo/releases/tag/v$Version"