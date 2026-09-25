<#
.SYNOPSIS
  Tests, builds and publishes a DuoSync release on GitHub Releases (architecture 8a).
  Both installed copies pick it up by themselves within 6 hours (or by "Check for updates" in the tray).
.EXAMPLE
  powershell -File tools/release.ps1 -Notes "First line of what is new.", "Second line."
.NOTES
  Version comes from Directory.Build.props: bump it and commit before running.
#>
param([Parameter(Mandatory = $true)][string[]]$Notes)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object Text.UTF8Encoding $false

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'Version not found in Directory.Build.props' }
$tag = "v$version"
if (git -C $root tag --list $tag) { throw "Tag $tag already exists: bump Version in Directory.Build.props" }
if (git -C $root status --porcelain) { throw 'Working tree is not clean: commit first' }

dotnet test (Join-Path $root 'DuoSync.slnx')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

$out = Join-Path $root "artifacts\release\$version"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root 'src\DuoSync\DuoSync.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$exe = Join-Path $out 'DuoSync.exe'
$info = [ordered]@{
    version = $version
    exe     = 'DuoSync.exe'
    size    = (Get-Item $exe).Length
    sha256  = (Get-FileHash -Algorithm SHA256 $exe).Hash
    notes   = @($Notes)
}
$releaseJson = Join-Path $out 'release.json'
[IO.File]::WriteAllText($releaseJson, ($info | ConvertTo-Json -Compress), $utf8)
$notesFile = Join-Path $out 'notes.md'
[IO.File]::WriteAllText($notesFile, ((@($Notes) | ForEach-Object { "- $_" }) -join "`n"), $utf8)

git -C $root tag $tag
git -C $root push -q origin $tag
if ($LASTEXITCODE -ne 0) { throw 'Tag push failed' }
gh release create $tag $exe $releaseJson --repo Eshachki/duosync --title "DuoSync $version" --notes-file $notesFile --latest
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "Released $tag"
