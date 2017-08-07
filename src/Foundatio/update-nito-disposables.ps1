# Pull sources
if (Test-Path disposables.zip) {
	del disposables.zip
}
Invoke-WebRequest https://github.com/StephenCleary/Disposables/archive/v1.0.0.zip -OutFile disposables.zip
if (Test-Path disposables-temp) {
	rmdir '.\disposables-temp' -Recurse -Force
}
[System.Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($pwd.Path + "\disposables.zip", $pwd.Path + "\disposables-temp")

if (Test-Path Nito.Disposables) {
	rmdir '.\Nito.Disposables' -Recurse -Force
}

cd 'disposables-temp\Disposables*'
Copy-Item 'Src\Nito.Disposables' -Destination '..\..\' -Recurse
cd '..\..\'

rmdir '.\disposables-temp' -Recurse -Force
del disposables.zip

Get-ChildItem '.\Nito.Disposables' *.cs -recurse |
    Foreach-Object {
        $c = ($_ | Get-Content) 
        $c = $c -replace 'Nito.Disposables','Foundatio.Disposables'
        $c | Set-Content $_.FullName
    }

del '.\Nito.Disposables\*.csproj' -Force
del '.\Nito.Disposables\*.xproj' -Force
del '.\Nito.Disposables\*project.json' -Force