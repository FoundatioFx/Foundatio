#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Binary/API compatibility gate for Foundatio core packages. Builds a git ref in a throwaway worktree
  and packs every src project with SDK package validation against a baseline stable version.
.DESCRIPTION
  Uses EnablePackageValidation + PackageValidationBaselineVersion, which resolves framework references
  correctly. Do NOT use the raw `apicompat` global tool on extracted nupkgs: it cannot resolve references
  and its output is meaningless.
  Exit 0 = no breaking changes. Exit 1 = at least one CP#### error: STOP and flag to the user.
.EXAMPLE
  ./api-compat.ps1 -Ref v13.0.5 -Baseline 13.0.4 -Version 13.0.5
  ./api-compat.ps1 -Ref origin/main -Baseline 13.0.4 -Version 13.0.5
#>
param(
  [Parameter(Mandatory)][string]$Ref,
  [Parameter(Mandatory)][string]$Baseline,
  [Parameter(Mandatory)][string]$Version,
  [string]$Repo = "$HOME/code/Foundatio"
)

$ErrorActionPreference = "Stop"
$wt = Join-Path ([IO.Path]::GetTempPath()) "foundatio-apicompat-$([guid]::NewGuid().ToString('N').Substring(0,8))"
$out = Join-Path $wt "_pack"
git -C $Repo fetch origin --tags --quiet
git -C $Repo worktree add --detach $wt $Ref | Out-Null
$failed = @()
try {
  $env:MINVERVERSIONOVERRIDE = $Version
  dotnet build (Join-Path $wt "Foundatio.slnx") -c Release -p:MinVerVersionOverride=$Version -v q | Out-Null
  foreach ($p in Get-ChildItem (Join-Path $wt "src") -Filter *.csproj -Recurse) {
    $o = dotnet pack $p.FullName -c Release -o $out --no-build -p:MinVerVersionOverride=$Version -p:EnablePackageValidation=true -p:PackageValidationBaselineVersion=$Baseline 2>&1
    $packExit = $LASTEXITCODE
    $cp = $o | Select-String -Pattern 'error CP\d{4}' | ForEach-Object { ($_.Line -replace '^.*error ','').Trim() } | Select-Object -Unique
    if ($packExit -ne 0 -or $cp) {
      $failed += $p.BaseName
      Write-Output "BREAK  $($p.BaseName)"
      $cp | ForEach-Object { Write-Output "         $_" }
      if (-not $cp) { $o | Select-String 'error' | Select-Object -First 5 | ForEach-Object { Write-Output "         $($_.Line.Trim())" } }
    }
    else {
      Write-Output "clean  $($p.BaseName)"
    }
  }
}
finally {
  git -C $Repo worktree remove --force $wt 2>$null | Out-Null
}
if ($failed) { Write-Output "`nAPI COMPAT: BREAKING CHANGES in: $($failed -join ', ')"; exit 1 }
Write-Output "`nAPI COMPAT: clean against $Baseline"
exit 0
