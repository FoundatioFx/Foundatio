# Pull sources
if (Test-Path tasks.zip) {
	del tasks.zip
}
Invoke-WebRequest https://github.com/StephenClearyArchive/AsyncEx.Tasks/archive/v1.0.0-delta-4.zip -OutFile tasks.zip
if (Test-Path tasks-temp) {
	rmdir '.\tasks-temp' -Recurse -Force
}
[System.Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($pwd.Path + "\tasks.zip", $pwd.Path + "\tasks-temp")

if (Test-Path Nito.AsyncEx.Tasks) {
	rmdir '.\Nito.AsyncEx.Tasks' -Recurse -Force
}

cd 'tasks-temp\AsyncEx.Tasks*'
Copy-Item 'Src\Nito.AsyncEx.Tasks' -Destination '..\..\' -Recurse
Copy-Item 'LICENSE' -Destination '..\..\Nito.AsyncEx.Tasks'
cd '..\..\'

rmdir '.\tasks-temp' -Recurse -Force
del tasks.zip

Get-ChildItem '.\Nito.AsyncEx.Tasks' *.cs -recurse |
    Foreach-Object {
        $c = ($_ | Get-Content) 
        $c = $c -replace 'Nito.AsyncEx','Foundatio.AsyncEx'
        $c | Set-Content $_.FullName
    }

del '.\Nito.AsyncEx.Tasks\*.csproj' -Force
del '.\Nito.AsyncEx.Tasks\*.xproj' -Force
del '.\Nito.AsyncEx.Tasks\*project.json' -Force