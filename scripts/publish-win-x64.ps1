$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet publish .\src\YiHeLee.App\YiHeLee.App.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o .\publish\win-x64
    Write-Host "發佈完成：$root\publish\win-x64"
}
finally {
    Pop-Location
}
