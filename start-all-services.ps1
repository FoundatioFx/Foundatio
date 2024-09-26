$parentDirectory = Split-Path -Path $PSScriptRoot -Parent
$directories = Get-ChildItem -Path $parentDirectory -Directory -Filter "Foundatio.*"

foreach ($directory in $directories) {
    $composeFilePath = Join-Path -Path $directory.FullName -ChildPath "docker-compose.yml"
    if (Test-Path -Path $composeFilePath) {
        Set-Location -Path $directory.FullName
        Write-Host "Starting services in directory: $($directory.FullName)"
        docker compose -f $composeFilePath up --detach
    }
}

Write-Host "Services started successfully."
Set-Location -Path $PSScriptRoot
