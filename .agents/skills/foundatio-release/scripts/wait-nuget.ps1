#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Polls nuget.org until every listed package is restorable at the given version.
.DESCRIPTION
  A green tag build with "Publish Release Packages: success" only means nuget.org accepted the push
  ("Created"). Indexing/validation can take 10+ minutes and a package page can 200 while the flat
  container still lacks the version. Dependents cannot restore until the flat container lists it.
  Exit 0 = all listed. Exit 1 = timeout with the missing packages printed.
.EXAMPLE
  ./wait-nuget.ps1 -Version 13.0.5
  ./wait-nuget.ps1 -Version 13.0.5 -Packages Foundatio,Foundatio.Redis -TimeoutMinutes 40
#>
param(
  [Parameter(Mandatory)][string]$Version,
  [string[]]$Packages = @("Foundatio","Foundatio.Extensions.Hosting","Foundatio.TestHarness","Foundatio.Xunit.v3","Foundatio.Xunit","Foundatio.MessagePack","Foundatio.DataProtection","Foundatio.JsonNet","Foundatio.Utf8Json"),
  [int]$TimeoutMinutes = 30,
  [int]$IntervalSeconds = 20
)

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
do {
  $missing = @()
  foreach ($p in $Packages) {
    try { $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($p.ToLower())/index.json").versions } catch { $versions = @() }
    if ($versions -notcontains $Version) { $missing += $p }
  }
  if (-not $missing) { Write-Output "ALL LISTED on nuget.org at $Version ($($Packages.Count) packages)"; exit 0 }
  Write-Output "$(Get-Date -Format HH:mm:ss) waiting; missing: $($missing -join ', ')"
  Start-Sleep -Seconds $IntervalSeconds
} while ((Get-Date) -lt $deadline)

Write-Output "TIMEOUT after $TimeoutMinutes min; still missing: $($missing -join ', ')"
exit 1
