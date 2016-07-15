Write-Host "Installing RabbitMQ..." -ForegroundColor Cyan

Write-Host "Downloading..."
$exePath = "$($env:USERPROFILE)\rabbitmq-server-3.6.3.exe"
(New-Object Net.WebClient).DownloadFile('http://www.rabbitmq.com/releases/rabbitmq-server/v3.6.3/rabbitmq-server-3.6.3.exe', $exePath)

Write-Host "Installing..."
cmd /c start /wait $exePath /S

$rabbitPath = 'C:\Program Files (x86)\RabbitMQ Server\rabbitmq_server-3.6.3'

Write-Host "Installing service..."
Start-Process -Wait "$rabbitPath\sbin\rabbitmq-service.bat" "install"

Write-Host "Starting service..."
Start-Process -Wait "$rabbitPath\sbin\rabbitmq-service.bat" "start"

Get-Service "RabbitMQ"

Write-Host "RabbitMQ installed and started" -ForegroundColor Green