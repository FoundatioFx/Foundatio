param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
function Median($Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0 }
    if ($sorted.Count % 2) { return $sorted[[int][math]::Floor($sorted.Count / 2)] }
    return ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2
}
$results = @(Get-ChildItem $Directory -Filter 'round*.json' | ForEach-Object {
    $result = Get-Content $_.FullName -Raw | ConvertFrom-Json
    $o = $result.Options
    [pscustomobject]@{ File = $_.Name; Key = "$($o.Engine)/$($o.Transport) $($o.Scenario) p$($o.ProducerConcurrency) c$($o.ConsumerConcurrency) s$($o.DeliveryCopies) $($o.PayloadBytes)B b$($o.BatchSize) r$($o.RatePerSecond) w$($o.MaxOutstanding) pf$($o.Prefetch)"; Result = $result }
})
$environments = @($results | Where-Object { $_.Result.Success } | ForEach-Object {
    $e = $_.Result.Environment
    $o = $_.Result.Options
    "$($e.Runtime)|$($e.OS)|$($e.Architecture)|$($e.LogicalProcessors)|$($e.ServerGC)|$($e.Foundatio)|$($e.MassTransit)|$($e.SqsSdk)|$($e.SnsSdk)|$($o.DurationSeconds)|$($o.WarmupSeconds)|$($o.MaxMessages)"
} | Select-Object -Unique)
if ($environments.Count -gt 1) { throw 'Results mix runtime, library, duration or tracking configurations. Summarize each configuration in a separate directory.' }
$awsEnvironments = @($results | Where-Object { $_.Result.Success -and $_.Result.Options.Transport -eq 'sqs' } | ForEach-Object {
    $e = $_.Result.Environment
    "$($e.Broker)|$($e.AwsMode)|$($e.AwsRegion)"
} | Select-Object -Unique)
if ($awsEnvironments.Count -gt 1) { throw 'Results mix AWS modes or regions. Summarize LocalStack and each AWS region in separate directories.' }
$rows = @($results | Where-Object { $_.Result.Success } | Group-Object Key | ForEach-Object {
    $metrics = @($_.Group.Result.Measurement)
    [pscustomobject]@{
        Case = $_.Name; Trials = $_.Count
        InputsPerSecond = [math]::Round((Median $metrics.InputsPerSecond), 1)
        MinInputsPerSecond = [math]::Round(($metrics.InputsPerSecond | Measure-Object -Minimum).Minimum, 1)
        MaxInputsPerSecond = [math]::Round(($metrics.InputsPerSecond | Measure-Object -Maximum).Maximum, 1)
        DeliveriesPerSecond = [math]::Round((Median $metrics.DeliveriesPerSecond), 1)
        P50Milliseconds = [math]::Round((Median $metrics.DeliveryLatency.P50Milliseconds), 3)
        P95Milliseconds = [math]::Round((Median $metrics.DeliveryLatency.P95Milliseconds), 3)
        P99Milliseconds = [math]::Round((Median $metrics.DeliveryLatency.P99Milliseconds), 3)
        BytesPerInput = [math]::Round((Median $metrics.AllocatedBytesPerInput), 1)
        CpuMillisecondsPerInput = [math]::Round((Median @($metrics | ForEach-Object { $_.CpuMilliseconds / $_.Inputs })), 4)
        PeakWorkingSetMiB = [math]::Round((Median @($metrics | ForEach-Object { $_.PeakWorkingSetBytes / 1MB })), 1)
        TotalInputs = ($metrics.Inputs | Measure-Object -Sum).Sum
        Duplicates = ($metrics.Duplicates | Measure-Object -Sum).Sum
        Missing = ($metrics.Missing | Measure-Object -Sum).Sum
    }
})
$rows | Export-Csv (Join-Path $Directory 'summary.csv') -NoTypeInformation
$lines = @('# Messaging benchmark results', '', 'Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.', '')
if ($awsEnvironments.Count -eq 1) {
    $aws = ($results | Where-Object { $_.Result.Success -and $_.Result.Options.Transport -eq 'sqs' } | Select-Object -First 1).Result.Environment
    $lines += @("AWS target: $($aws.Broker); mode: $($aws.AwsMode); region: $($aws.AwsRegion).", '')
}
$lines += @(
'| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |',
'| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
foreach ($row in $rows) { $lines += "| $($row.Case) | $($row.Trials) | $($row.InputsPerSecond) ($($row.MinInputsPerSecond)-$($row.MaxInputsPerSecond)) | $($row.DeliveriesPerSecond) | $($row.P50Milliseconds) / $($row.P95Milliseconds) / $($row.P99Milliseconds) | $($row.BytesPerInput) | $($row.CpuMillisecondsPerInput) |" }
$failures = @($results | Where-Object { -not $_.Result.Success })
$lines += @('', "Failed trials: $($failures.Count).")
foreach ($failure in $failures) { $lines += "- $($failure.File): $($failure.Result.Error)" }
$lines | Set-Content (Join-Path $Directory 'summary.md')
$rows | Format-Table Case, Trials, InputsPerSecond, P99Milliseconds, BytesPerInput
