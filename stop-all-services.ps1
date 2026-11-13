Write-Host "Stopping services in directory: $PSScriptRoot"
docker compose -f (Join-Path -Path $PSScriptRoot -ChildPath "docker-compose.yml") down --remove-orphans

# These providers now live in this repo; skip old clones of them next to it.
$inRepoProviders = @("Foundatio.Redis", "Foundatio.AWS", "Foundatio.Kafka", "Foundatio.AzureServiceBus", "Foundatio.RabbitMQ")
$parentDirectory = Split-Path -Path $PSScriptRoot -Parent
$directories = Get-ChildItem -Path $parentDirectory -Directory -Filter "Foundatio.*" | Where-Object { $_.Name -notin $inRepoProviders }

foreach ($directory in $directories) {
    $composeFilePath = Join-Path -Path $directory.FullName -ChildPath "docker-compose.yml"
    if (Test-Path -Path $composeFilePath) {
        Set-Location -Path $directory.FullName
        Write-Host "Stopping services in directory: $($directory.FullName)"
        docker compose -f $composeFilePath down --remove-orphans
    }
}

Write-Host "Stopped services"
Set-Location -Path $PSScriptRoot
