$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet restore .\YiHeLee.sln
    dotnet build .\YiHeLee.sln -c Release --no-restore
    dotnet test .\tests\YiHeLee.Tests\YiHeLee.Tests.csproj -c Release --no-build
}
finally {
    Pop-Location
}
