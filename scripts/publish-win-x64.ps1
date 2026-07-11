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

    # 產生 buildinfo.json：記錄 Git Commit SHA、分支與 Build 時間，供啟動 Log 與主畫面顯示，
    # 方便確認實際啟動的是本次 Publish 而非舊資料夾中的舊 EXE。取不到 Git 資訊（例如非 Git 目錄）
    # 時以 "unknown" 標示，不得讓 Publish 因此失敗。
    $gitSha = 'unknown'
    $gitBranch = 'unknown'
    try {
        $gitSha = (git rev-parse HEAD 2>$null)
        if ([string]::IsNullOrWhiteSpace($gitSha)) { $gitSha = 'unknown' }
    } catch { $gitSha = 'unknown' }
    try {
        $gitBranch = (git rev-parse --abbrev-ref HEAD 2>$null)
        if ([string]::IsNullOrWhiteSpace($gitBranch)) { $gitBranch = 'unknown' }
    } catch { $gitBranch = 'unknown' }

    $exePath = Join-Path $root 'publish\win-x64\Yi He Lee.exe'
    $version = 'unknown'
    if (Test-Path $exePath) {
        try { $version = (Get-Item $exePath).VersionInfo.FileVersion } catch { $version = 'unknown' }
    }

    $buildInfo = [ordered]@{
        Version      = $version
        GitCommitSha = $gitSha
        GitBranch    = $gitBranch
        BuildTimeUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $buildInfoPath = Join-Path $root 'publish\win-x64\buildinfo.json'
    ($buildInfo | ConvertTo-Json) | Out-File -FilePath $buildInfoPath -Encoding utf8 -Force
    Write-Host "已產生 buildinfo.json：$buildInfoPath（Commit=$gitSha，分支=$gitBranch）"
}
finally {
    Pop-Location
}
