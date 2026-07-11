using System.Reflection;
using System.Runtime.Loader;
using YiHeLee.App.Infrastructure;
using YiHeLee.Application.Services;
using YiHeLee.Infrastructure.Crawlers;
using YiHeLee.Infrastructure.Data;
using YiHeLee.Infrastructure.Excel;
using YiHeLee.Infrastructure.Logging;
using YiHeLee.Infrastructure.MarketData;
using YiHeLee.Infrastructure.Settings;
using YiHeLee.Infrastructure.Time;

namespace YiHeLee.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        RegisterOfficeInteropAssemblyResolver();
        ApplicationConfiguration.Initialize();

        using var singleInstance = new SingleInstanceGuard(@"Local\YiHeLee.TrayApp.SingleInstance");
        if (!singleInstance.IsPrimaryInstance)
        {
            MessageBox.Show("Yi He Lee 已在右下角系統匣執行。", "Yi He Lee",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var paths = new AppPaths();
        var logger = new FileAppLogger(paths.LogDirectory);
        var clock = new TaipeiClock();
        var validationService = new SettingsValidationService();
        var settingsStore = new JsonSettingsStore(paths.SettingsPath, validationService);

        // 啟動時立即記錄實際版本、Git Commit SHA、Build 時間與各項完整路徑，方便確認目前啟動的
        // 不是舊資料夾裡的舊 EXE。Excel 路徑取自設定檔，設定檔不存在時 LoadAsync 會建立預設值。
        LogStartupBuildInfo(paths, settingsStore, logger);
        var repository = new SqliteYiHeLeeRepository(paths.DatabasePath, clock);
        var marketDataRepository = new SqliteMarketDataRepository(paths.DatabasePath, clock);
        var userInteraction = new WinFormsUserInteraction();
        var holdingRowExclusionService = new HoldingRowExclusionService();
        var excelService = new ExcelWorkbookService(paths.BackupDirectory, logger, holdingRowExclusionService);
        var cnyesCrawler = new CnyesTechnicalAlignmentCrawler(clock, logger);
        var crawlerRegistry = new CrawlerRegistry([cnyesCrawler]);

        // TWSE／TPEx 官方每日收盤價：獨立於鉅亨網爬蟲之外的 HTTP Provider，供均線改用官方資料計算。
        using var marketDataHttpClient = new HttpClient();
        var twseProvider = new TwseMarketDataProvider(marketDataHttpClient, logger, clock);
        var tpexProvider = new TpexMarketDataProvider(marketDataHttpClient, logger, clock);
        var emergingProvider = new EmergingMarketDataProvider(marketDataHttpClient, logger, clock);
        var marketPriceService = new MarketPriceService(twseProvider, tpexProvider, emergingProvider, marketDataRepository, clock, logger);
        var dailyMarketDataJob = new DailyMarketDataJob(marketPriceService);
        var historicalBackfillJob = new HistoricalBackfillJob(marketPriceService);
        var movingAverageService = new MovingAverageService(marketDataRepository);

        // 歷史收盤價查詢畫面／立即回補：使用者手動觸發，市場＋交易日期為工作單位，
        // 進度另存於 StockPriceImportJob／StockPriceImportTask，與既有每日排程的 OfficialPriceBatch 分開記錄。
        var stockPriceImportRepository = new SqliteStockPriceImportRepository(paths.DatabasePath, clock);
        var stockHistoryImportService = new StockHistoryImportService(marketPriceService, stockPriceImportRepository, clock, logger);
        var stockPriceValidationRepository = new SqliteStockPriceValidationRepository(paths.DatabasePath);
        var stockPriceValidationService = new CnyesStockPriceValidationService(movingAverageService, stockPriceValidationRepository, clock, logger);

        var strategyService = new StrategyEvaluationService();
        var stockIdentityResolutionService = new StockIdentityResolutionService(marketDataRepository);
        var dailyJobService = new DailyJobService(
            clock,
            settingsStore,
            crawlerRegistry,
            repository,
            marketDataRepository,
            excelService,
            userInteraction,
            logger,
            dailyMarketDataJob,
            historicalBackfillJob,
            marketPriceService,
            movingAverageService,
            strategyService,
            validationService,
            stockIdentityResolutionService);
        var scheduleCoordinator = new DailyScheduleCoordinator(
            dailyJobService,
            clock,
            settingsStore,
            repository,
            logger);
        var startupManager = new WindowsStartupManager();

        try
        {
            repository.InitializeAsync().GetAwaiter().GetResult();
            marketDataRepository.InitializeAsync().GetAwaiter().GetResult();
            stockPriceImportRepository.InitializeAsync().GetAwaiter().GetResult();
            stockPriceValidationRepository.InitializeAsync().GetAwaiter().GetResult();

            using var context = new TrayApplicationContext(
                args,
                paths,
                dailyJobService,
                scheduleCoordinator,
                settingsStore,
                validationService,
                repository,
                userInteraction,
                startupManager,
                logger,
                marketDataRepository,
                stockHistoryImportService,
                stockPriceImportRepository,
                crawlerRegistry,
                stockPriceValidationService);
            System.Windows.Forms.Application.Run(context);
        }
        catch (Exception ex)
        {
            logger.Error("程式啟動失敗。", ex);
            MessageBox.Show(
                $"Yi He Lee 啟動失敗：\r\n{ex.Message}\r\n\r\nLog：{paths.LogDirectory}",
                "Yi He Lee－啟動失敗",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            try { scheduleCoordinator.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { cnyesCrawler.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }

    private static void LogStartupBuildInfo(AppPaths paths, JsonSettingsStore settingsStore, FileAppLogger logger)
    {
        string workbookPath;
        try
        {
            var settings = settingsStore.LoadAsync().GetAwaiter().GetResult();
            workbookPath = string.IsNullOrWhiteSpace(settings.WorkbookPath) ? "（尚未設定）" : settings.WorkbookPath;
        }
        catch (Exception ex)
        {
            workbookPath = $"（讀取設定失敗：{ex.Message}）";
        }

        logger.Info(
            "程式啟動。" +
            $" 程式版本={BuildInfo.Version}；" +
            $" Git Commit SHA={BuildInfo.GitCommitSha}；" +
            $" Git 分支={BuildInfo.GitBranch}；" +
            $" Build 時間(UTC)={BuildInfo.BuildTimeUtc}；" +
            $" 執行檔完整路徑={BuildInfo.ExecutablePath}；" +
            $" SQLite 完整路徑={paths.DatabasePath}；" +
            $" Excel 完整路徑={workbookPath}；" +
            $" 是否為 Publish 腳本產生={BuildInfo.IsFromPublishScript}");
    }

    private static void RegisterOfficeInteropAssemblyResolver()
    {
        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            var fileName = assemblyName.Name switch
            {
                "office" => "office.dll",
                "Microsoft.Vbe.Interop" => "Microsoft.Vbe.Interop.dll",
                _ => null
            };

            if (fileName is null)
            {
                return null;
            }

            var assemblyPath = Path.Combine(AppContext.BaseDirectory, fileName);
            return File.Exists(assemblyPath)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath)
                : null;
        };
    }
}
