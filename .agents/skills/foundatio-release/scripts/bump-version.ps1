#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Finds (and optionally rewrites) preview Foundatio package versions in one or more repos.
.DESCRIPTION
  DRY RUN by default: lists every file/line that contains -From as a Foundatio-family PackageReference
  version. With -Apply it edits files in the working tree only. It never commits or pushes; that is a
  deliberate manual step so each repo can be verified before and after.
  Only touches lines matching Include="Foundatio*" ... Version="<From>". Other occurrences (docs,
  non-Foundatio packages, differently versioned Foundatio refs such as TestHarness 13.0.2) are listed
  as "SKIPPED OCCURRENCES" so nothing is silently missed.
.EXAMPLE
  ./bump-version.ps1 -From 13.0.5-preview.0.16 -To 13.0.5 -Repos Foundatio.RabbitMQ
  ./bump-version.ps1 -From 13.0.5-preview.0.16 -To 13.0.5 -Repos Foundatio.RabbitMQ -Apply
#>
param(
  [Parameter(Mandatory)][string]$From,
  [Parameter(Mandatory)][string]$To,
  [Parameter(Mandatory)][string[]]$Repos,
  [string]$Root = "$HOME/code",
  [switch]$Apply
)

$ErrorActionPreference = "Stop"
$pattern = '(Include="Foundatio(?:\.[A-Za-z0-9]+)*"[^>]*?Version=")' + [regex]::Escape($From) + '(")'

foreach ($r in $Repos) {
  $d = Join-Path $Root $r
  $branch = git -C $d rev-parse --abbrev-ref HEAD
  $dirty = (git -C $d status --porcelain | Measure-Object).Count
  Write-Output "===== $r (branch=$branch dirty=$dirty)"
  if ($Apply -and ($branch -ne "main" -or $dirty -ne 0)) { Write-Output "  REFUSING to apply: must be on a clean main"; continue }

  $files = git -C $d grep -l -F -- $From -- '*.csproj' '*.props' '*.targets' 2>$null
  $changed = 0
  foreach ($f in $files) {
    $path = Join-Path $d $f
    $text = Get-Content $path -Raw
    $new = [regex]::Replace($text, $pattern, "`${1}$To`${2}")
    $n = ([regex]::Matches($text, $pattern)).Count
    if ($n -gt 0) {
      Write-Output "  $(if($Apply){'EDIT'}else{'would edit'}) $f ($n)"
      if ($Apply) { Set-Content -Path $path -Value $new -NoNewline }
      $changed += $n
    }
  }

  $remaining = git -C $d grep -n -F -- $From 2>$null
  $skipped = $remaining | Where-Object { $_ -notmatch 'Include="Foundatio[^"]*"[^>]*Version="' }
  if ($skipped) { Write-Output "  SKIPPED OCCURRENCES (review manually):"; $skipped | ForEach-Object { Write-Output "    $_" } }
  Write-Output "  total refs $(if($Apply){'edited'}else{'to edit'}): $changed"
}
