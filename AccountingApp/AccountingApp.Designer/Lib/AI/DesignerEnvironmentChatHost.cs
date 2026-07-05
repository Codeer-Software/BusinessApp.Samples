using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Collections.Generic;
using System.Windows;

namespace AccountingApp.Designer.Lib.AI
{
    // 本番用の IDesignerChatHost 実装。DesignerEnvironment をラップし、DDL はモードレスの DDLWindow で提示する。
    public sealed class DesignerEnvironmentChatHost : IDesignerChatHost
    {
        readonly DesignerEnvironment _env;

        public DesignerEnvironmentChatHost(DesignerEnvironment env) => _env = env;

        public DesignData GetDesignData() => _env.GetDesignData();

        public IReadOnlyList<DataSource> GetDataSources() => _env.GetDesignerSettings().DataSources;

        public List<DbTableDefinition> GetDbInfo(string dataSourceName) => _env.GetDbInfo(dataSourceName);

        public string CurrentFileDirectory => _env.CurrentFileDirectory;

        public void ShowDdl(DataSource dataSource, string ddl)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.Invoke(() =>
            {
                new DDLWindow
                {
                    DesignerEnvironment = _env,
                    DataSource = dataSource,
                    DisplayText = ddl,
                    Owner = app.MainWindow,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Title = "DDL",
                }.Show();
            });
        }
    }
}
