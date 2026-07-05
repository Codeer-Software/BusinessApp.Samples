namespace AccountingApp.Server.Services
{
    /// <summary>
    /// 国税庁 適格請求書発行事業者公表システム Web-API の設定。
    /// ApplicationId が空の間はモック動作（形式チェック＋疑似応答）。
    /// 実 ID は国税庁への発行届出（無償）で取得し appsettings に設定する。
    /// </summary>
    public class NtaInvoiceSettings
    {
        public string ApplicationId { get; set; } = string.Empty;
    }
}
