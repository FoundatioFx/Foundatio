#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Read-only release-readiness status for the Foundatio repos. Never modifies anything.
.DESCRIPTION
  For each repo: local branch and sync with origin/main, dirty files, head commit on GitHub main,
  Foundatio-family package versions pinned on origin/main, and the CI run(s) for that exact commit
  (conclusion plus the xUnit "Test run summary" counts parsed from the log).
  Always matches CI to the exact HEAD commit of main (never "latest run on branch").
.EXAMPLE
  ./ci-status.ps1
  ./ci-status.ps1 -Repos Foundatio.Redis,Foundatio.Kafka -SkipTests
#>
param(
  [string[]]$Repos = @("Foundatio","Foundatio.Repositories","Foundatio.Redis","Foundatio.RabbitMQ","Foundatio.Parsers","Foundatio.AzureStorage","Foundatio.AzureServiceBus","Foundatio.AWS","Foundatio.Aliyun","Foundatio.Storage.SshNet","Foundatio.Kafka","Foundatio.Mediator","Foundatio.CommandQuery","Foundatio.Lucene"),
  [string]$Root = "$HOME/code",
  [string]$Org = "FoundatioFx",
  [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$ansi = '\x1b\[[\d;]*m'

foreach ($r in $Repos) {
  $d = Join-Path $Root $r
  git -C $d fetch origin --tags --quiet 2>&1 | Out-Null
  $branch = git -C $d rev-parse --abbrev-ref HEAD
  $dirty = (git -C $d status --porcelain | Measure-Object).Count
  $sha = gh api "repos/$Org/$r/commits/main" --jq .sha
  $title = gh api "repos/$Org/$r/commits/main" --jq '.commit.message | split("\n")[0]'
  $inSync = ((git -C $d rev-parse HEAD) -eq $sha) -and ($branch -eq "main")
  $lastTag = git -C $d describe --tags --abbrev=0 origin/main 2>$null
  $ahead = git -C $d rev-list --count "$lastTag..origin/main" 2>$null

  $refs = git -C $d grep -h -o -E 'Include="Foundatio(\.[A-Za-z0-9]+)*" Version="[^"]+"' origin/main -- '*.csproj' '*.props' '*.targets' 2>$null |
    ForEach-Object { $_ -replace 'Include="([^"]+)" Version="([^"]+)"', '$1=$2' } | Sort-Object -Unique

  Write-Output ""
  Write-Output "===== $r"
  Write-Output "  local:   $branch  inSyncWithMain=$inSync  dirtyFiles=$dirty"
  Write-Output "  main:    $($sha.Substring(0,7))  $title"
  Write-Output "  tag:     last=$lastTag  commitsSince=$ahead"
  Write-Output "  refs:    $($refs -join '; ')"

  $runs = gh run list -R "$Org/$r" --commit $sha --json databaseId,workflowName,status,conclusion,event --limit 20 | ConvertFrom-Json
  if (-not $runs) { Write-Output "  CI:      NO RUNS on this commit"; continue }
  foreach ($run in $runs | Where-Object { $_.workflowName -in @("Build","CodeQL","Docs") }) {
    $line = "  CI:      $($run.workflowName) $($run.databaseId) $($run.status)/$($run.conclusion) [$($run.event)]"
    if (-not $SkipTests -and $run.workflowName -eq "Build" -and $run.status -eq "completed") {
      $log = gh run view $run.databaseId -R "$Org/$r" --log 2>&1 | ForEach-Object { ($_ -replace '^.*?\d{4}-\d\d-\d\dT[\d:.]+Z\s*','') -replace $ansi,'' }
      $summary = $log | Select-String -Pattern '^\s*(total|succeeded|failed|skipped): \d+' | ForEach-Object { $_.Line.Trim() }
      $line += "  tests: " + (($summary | Select-Object -First 4) -join ' ')
      if (-not $summary) { $line += "  tests: NONE FOUND (build failed before tests, or no summary)" }
    }
    Write-Output $line
  }
}
