# Claude Code－修復、建置與實作任務

## 任務目標

在保留現有架構與既有功能的前提下，讓 `Yi He Lee` 可以在 Windows 正常 Restore、Build、Test、Publish，並符合完整需求。

> 2026-07-13 更新：「均價高於任一價格即觸發通知」已成為現行正式規則（分支 `fix/reverse-dual-price-moving-average-condition`），
> 取代 2026-07-12「進場價與現價需同時大於或等於均價」的舊方向。現行條件為 MA5／MA20／MA120 任一均價
> `>= 進場價/平均價` 或 `>= 現價` 即成立；MA60 不參與觸發。詳見 `docs/05_異動紀錄.md`、`docs/06_建置驗證結果.md`。

## 第一階段：只做診斷

1. 顯示目前分支及工作樹：

```powershell
git status
git branch --show-current
```

2. 顯示環境：

```powershell
dotnet --info
where.exe dotnet
```

3. 依序執行：

```powershell
dotnet restore .\YiHeLee.sln
dotnet build .\YiHeLee.sln -c Debug
dotnet build .\YiHeLee.sln -c Release
dotnet test .\tests\YiHeLee.Tests\YiHeLee.Tests.csproj -c Release
```

4. 先輸出錯誤分類：

- SDK／Targeting Pack。
- NuGet 套件。
- C# 編譯錯誤。
- 專案參考。
- Windows Forms。
- Office Interop。
- Playwright。
- 測試錯誤。

不要在沒有錯誤證據前大量改檔案。

## 第二階段：最小修復

- 一次修一個根因。
- 每次修正後重新 Build。
- 不要直接把整套專案改成另一種 UI 或資料庫。
- 保留 `Microsoft.Data.Sqlite`。
- 任何套件版本調整都要說明原因。

## 第三階段：可攜式 SQLite

修改：

- `AppPaths.cs`。
- README 路徑說明。
- 首次啟動目錄建立。
- 設定、Log、備份、SQLite 路徑。
- `.gitignore`。
- 自動測試。

預設：

```text
AppContext.BaseDirectory\settings.json
AppContext.BaseDirectory\Data\yi-he-lee.db
AppContext.BaseDirectory\Logs\
AppContext.BaseDirectory\Backups\
```

## 第四階段：功能核對

逐項核對：

- 系統匣。
- 中央結果視窗。
- 13:35 台北排程。
- 固定來源不可停用。
- 兩市場完整清單。
- 當日日期驗證。
- SQLite Transaction／Upsert。
- Excel 表頭辨識。
- 無「自持股」文字。
- 已出場表排除。
- 顏色與文字排除。
- 代碼前導零。
- MA5／20／120 任一成立。
- MA60 只顯示。
- 無資料清單。
- Excel 備份、寫入、儲存、COM 釋放。
- 可重試／不可重試分類。

## 第五階段：發布

執行：

```powershell
dotnet publish .\src\YiHeLee.App\YiHeLee.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\publish\win-x64
```

檢查發布目錄可寫，並建立空的 Data、Logs、Backups 或由首次啟動自動建立。

## 第六階段：回報

不得只回覆「已完成」。要列出：

- 實際錯誤。
- 根因。
- 修改檔案。
- Build／Test 結果。
- 實機未驗證項目。
- 使用者下一步操作。
