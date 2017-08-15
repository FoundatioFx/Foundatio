$work_dir = Resolve-Path "$PSScriptRoot"
$src_dir = Resolve-Path "$PSScriptRoot\..\src\Foundatio"

Function UpdateSource {
    param( [string]$sourceUrl, [string]$name )

    If (Test-Path $work_dir\$name.zip) {
        Remove-Item $work_dir\$name.zip
    }
    Invoke-WebRequest $sourceUrl -OutFile $work_dir\$name.zip
    If (Test-Path $work_dir\$name) {
        Remove-Item $work_dir\$name -Recurse -Force
    }
    [System.Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory("$work_dir\$name.zip", "$work_dir\$name")

    Remove-Item $work_dir\$name.zip

    If (Test-Path $src_dir\$name) {
        Remove-Item $src_dir\$name -Recurse -Force
    }

    $dir = (Get-ChildItem $work_dir\$name | Select-Object -First 1).FullName

    New-Item $src_dir\$name -Type Directory
    Copy-Item "$dir\LICENSE" -Destination "$src_dir\$name" -Recurse -Force

    Get-ChildItem -Path "$dir\src\$name" -Filter *.cs -Recurse |
        Foreach-Object {
            $c = ($_ | Get-Content)
            $c = $c -replace 'namespace Nito','namespace Foundatio'
            $c = $c -replace 'using Nito','using Foundatio'
            $c | Set-Content $_.FullName.Replace("$dir\src\$name", "$src_dir\$name")
        }

    Remove-Item $work_dir\$name -Recurse -Force
}

UpdateSource "https://github.com/StephenCleary/Deque/archive/v1.0.0.zip" "Nito.Collections.Deque"
UpdateSource "https://github.com/StephenClearyArchive/AsyncEx.Tasks/archive/v1.0.0-delta-4.zip" "Nito.AsyncEx.Tasks"
UpdateSource "https://github.com/StephenClearyArchive/AsyncEx.Coordination/archive/v1.0.2.zip" "Nito.AsyncEx.Coordination"
UpdateSource "https://github.com/StephenCleary/Disposables/archive/v1.0.0.zip" "Nito.Disposables"