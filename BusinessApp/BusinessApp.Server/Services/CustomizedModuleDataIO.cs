using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Extras.Services;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Shared.Presentation;

namespace BusinessApp.Server.Services
{
    public class CustomizedModuleDataIO : ModuleDataIO
    {
        //一括INSERT (multi-row INSERT) の有効化。純Addのみの大量Submit (一括取込) がこの行数以上のとき束ねて挿入される。
        //-1 (コア既定) で無効
        static CustomizedModuleDataIO() => BulkAddThreshold = 100;

        //モジュール定義 (*.mod.json) の DataSourceName と一致していなければならない。
        const string AccountingDataSourceName = "BusinessAppSQLite";

        readonly DesignData _designData;
        readonly AccountingSubmitPipeline _accounting;

        public CustomizedModuleDataIO(DesignData designData, IAuthenticationContext authenticationContext, IDbAccessor dbAccess, ITemporaryFileManager temporaryFileManager, Action<string>? onSaveFailure = null)
            : base(designData, authenticationContext, dbAccess, temporaryFileManager)
        {
            _designData = designData;

            //会計の関門 (ADR-0008)。仕訳の計上はここを必ず通る。
            //データソースは名前で引く。添字だと 2 件目が増えた瞬間に、
            //会計コアだけが別の DB を読み書きして静かに壊れる。
            var dataSourceName = SystemConfig.Instance.DataSources
                .FirstOrDefault(e => e.Name == AccountingDataSourceName)?.Name
                ?? throw LowCodeException.Create($"データソース {AccountingDataSourceName} が設定にない");
            //認証コンテキストは posted_by（計上した人）の記録に使う（qa/02 R2-05）。
            //**つなぎ方はここに書かない。** このファイルはカバレッジにもミューテーションにも
            //載らないので、ここで組み立てると配線の間違いを誰も検査できない（AccountingSubmitPipeline）。
            _accounting = AccountingSubmitPipeline.Create(
                dbAccess, dataSourceName, TimeProvider.System, authenticationContext, onSaveFailure);
        }

        //トランザクション単位の入口。伝票の更新だけを見る UpdateAsync では、
        //同じ保存で送られてきた明細がまだ見えず、貸借一致を判定できない。
        //
        //関門が保存そのものを包む。順番も入れ子も AccountingSubmitPipeline が持つので、
        //ここから呼び忘れも並べ替えもできない。違反があれば関門が例外を投げ、この保存ごと巻き戻る。
        public override Task<List<ModuleSubmitResult>> SubmitAsync(Guid transactionId, List<ModuleSubmitData> transactionData)
            => _accounting.SubmitAsync(transactionData, () => base.SubmitAsync(transactionId, transactionData));

        protected override async Task<string> AddAsync(Guid transactionId, Guid moduleSubmitId, ModuleData data)
        {
            var moduleDesign = _designData.Modules.Find(data.Name);
            if (moduleDesign == null) throw LowCodeException.Create("invalid design");

            PasswordHashHelper.ApplyPasswordHash(moduleDesign, data);
            return await base.AddAsync(transactionId, moduleSubmitId, data);
        }

        //一括INSERT (大量取込) は行ごとの AddAsync を通らないため、同じ加工をこちらでも行う
        protected override async Task BulkAddAsync(Guid transactionId, List<ModuleData> datas)
        {
            var moduleDesign = _designData.Modules.Find(datas.FirstOrDefault()?.Name ?? string.Empty);
            if (moduleDesign == null) throw LowCodeException.Create("invalid design");

            foreach (var data in datas) PasswordHashHelper.ApplyPasswordHash(moduleDesign, data);
            await base.BulkAddAsync(transactionId, datas);
        }

        protected async override Task UpdateAsync(Guid transactionId, Guid moduleSubmitId, ModuleData data)
        {
            var moduleDesign = _designData.Modules.Find(data.Name);
            if (moduleDesign == null) throw LowCodeException.Create("invalid design");

            PasswordHashHelper.ApplyPasswordHash(moduleDesign, data);
            await base.UpdateAsync(transactionId, moduleSubmitId, data);
        }
    }
}
