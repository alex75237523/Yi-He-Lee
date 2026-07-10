using YiHeLee.Domain;

namespace YiHeLee.Application.Abstractions;

/// <summary>
/// 鉅亨網多頭／空頭排列與官方自算均線交叉驗證紀錄（CnyesCrossValidation）的存取層。
/// 只負責參數化 SQL 與查詢；比對規則（日期是否相符、是否在清單中、誤差門檻）一律由
/// <see cref="IStockPriceValidationService"/> 決定，本層不得覆蓋或修改官方資料。
/// </summary>
public interface IStockPriceValidationRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>整批寫入單一交易日的驗證紀錄；同一交易日、市場、股票代碼、視窗天數重跑時更新既有列。</summary>
    Task SaveValidationRecordsAsync(IReadOnlyList<CnyesValidationRecord> records, CancellationToken cancellationToken);

    Task<IReadOnlyList<CnyesValidationRecord>> GetValidationRecordsAsync(DateOnly tradeDate, CancellationToken cancellationToken);
}
