# Pull sources
if (Test-Path asyncex.coordination.zip) {
	del asyncex.coordination.zip
}
Invoke-WebRequest https://github.com/StephenClearyArchive/AsyncEx.Coordination/archive/v1.0.2.zip -OutFile asyncex.coordination.zip
if (Test-Path asyncex.coordination-temp) {
	rmdir '.\asyncex.coordination-temp' -Recurse -Force
}
[System.Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($pwd.Path + "\asyncex.coordination.zip", $pwd.Path + "\asyncex.coordination-temp")

if (Test-Path Nito.AsyncEx.Coordination) {
	rmdir '.\Nito.AsyncEx.Coordination' -Recurse -Force
}

cd 'asyncex.coordination-temp\AsyncEx.Coordination*'
Copy-Item 'Src\Nito.AsyncEx.Coordination' -Destination '..\..\' -Recurse
Copy-Item 'LICENSE' -Destination '..\..\Nito.AsyncEx.Coordination'
cd '..\..\'

rmdir '.\asyncex.coordination-temp' -Recurse -Force
del asyncex.coordination.zip

Get-ChildItem '.\Nito.AsyncEx.Coordination' *.cs -recurse |
    Foreach-Object {
        $c = ($_ | Get-Content) 
        $c = $c -replace 'Nito.AsyncEx','Foundatio.AsyncEx'
        $c | Set-Content $_.FullName
    }

del '.\Nito.AsyncEx.Coordination\*.csproj' -Force
del '.\Nito.AsyncEx.Coordination\*.xproj' -Force
del '.\Nito.AsyncEx.Coordination\*project.json' -Force