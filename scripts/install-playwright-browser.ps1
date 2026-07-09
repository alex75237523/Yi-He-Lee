$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build .\src\YiHeLee.Infrastructure\YiHeLee.Infrastructure.csproj -c Release
    $playwrightScript = Get-ChildItem -Path .\src\YiHeLee.Infrastructure\bin\Release -Filter playwright.ps1 -Recurse | Select-Object -First 1
    if ($null -eq $playwrightScript) {
        throw '找不到 Playwright 安裝腳本，請先確認 Microsoft.Playwright 套件已還原。'
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $playwrightScript.FullName install chromium
}
finally {
    Pop-Location
}
