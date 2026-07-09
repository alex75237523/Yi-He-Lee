# 可攜式 SQLite 正式規格

## 1. 最終決定

本專案只使用 SQLite。先前 LocalDB／MSSQL 設計全部作廢。

## 2. 路徑

預設根目錄：

```csharp
AppContext.BaseDirectory
```

建議：

```text
settings.json
Data\yi-he-lee.db
Logs\
Backups\
```

不得強制寫入 `%LOCALAPPDATA%`。若未來要支援「安裝模式」，必須另外設計，不得破壞目前可攜式預設。

## 3. 權限

- 程式放在使用者可寫目錄。
- 不建議放在 `Program Files`。
- 啟動時檢查根目錄、Data、Logs、Backups 可建立及可寫。
- 失敗時中央顯示明確路徑與權限原因。

## 4. 連線

- `Microsoft.Data.Sqlite`。
- `ReadWriteCreate`。
- 開啟外鍵。
- 設定合理的 `busy_timeout`。
- 單一應用程式執行個體，避免多程序同時寫入。

## 5. 備份

若使用 WAL：

- 不能只複製主 `.db` 就認定是完整備份。
- 優先使用 SQLite 連線 Backup API 或 `VACUUM INTO`。
- 備份檔名包含台北時間。
- 備份完成後驗證檔案存在、大小大於零、可開啟並查詢 SchemaVersion／主要資料表。

## 6. 冪等

`TechnicalIndicatorDaily` 唯一鍵：

```text
TradeDate + IndicatorType + MarketType + StockCode
```

同日重跑：

- 不得新增重複股票。
- 不得新增重複技術資料。
- 不得新增重複持股快照。
- 不得新增重複策略通知。
- 完整批次應使用 Transaction，避免只寫一半。

## 7. 遷移

- 建議增加 `SchemaVersions` 或 `PRAGMA user_version`。
- 不可每次啟動只靠一大段無版本 Schema 直接覆蓋。
- 每次 Schema 變更都要有版本、升級步驟、Rollback／備份說明。

## 8. Git

`.gitignore` 至少排除：

```text
Data/*.db
Data/*.db-shm
Data/*.db-wal
Logs/
Backups/
settings.json
```
