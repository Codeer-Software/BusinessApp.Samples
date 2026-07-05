using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.SystemSettings;

namespace AccountingApp.Designer.Lib.AI
{
    // 各 AI チャットがデザイナ環境へアクセスするための抽象。本番は DesignerEnvironmentChatHost、テストはフェイクを差し込む。
    public interface IDesignerChatHost
    {
        DesignData GetDesignData();
        IReadOnlyList<DataSource> GetDataSources();
        List<DbTableDefinition> GetDbInfo(string dataSourceName);
        string CurrentFileDirectory { get; }

        // 生成した DDL をユーザーに提示する(本番: モードレスの DDLWindow / テスト: 記録のみ)。
        void ShowDdl(DataSource dataSource, string ddl);
    }
}
