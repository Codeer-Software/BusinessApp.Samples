using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Extras.Services;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Journals;

namespace BusinessApp.Server.Services
{
    public class CustomizedModuleDataIO : ModuleDataIO
    {
        //一括INSERT (multi-row INSERT) の有効化。純Addのみの大量Submit (一括取込) がこの行数以上のとき束ねて挿入される。
        //-1 (コア既定) で無効
        static CustomizedModuleDataIO() => BulkAddThreshold = 100;

        readonly DesignData _designData;
        readonly JournalSubmitGate _journalGate;

        public CustomizedModuleDataIO(DesignData designData, IAuthenticationContext authenticationContext, IDbAccessor dbAccess, ITemporaryFileManager temporaryFileManager)
            : base(designData, authenticationContext, dbAccess, temporaryFileManager)
        {
            _designData = designData;

            //会計の関門 (ADR-0008)。仕訳の計上はここを必ず通る。
            var dataSourceName = SystemConfig.Instance.DataSources[0].Name;
            _journalGate = new JournalSubmitGate(
                new AccountingMasterLoader(dbAccess, dataSourceName),
                new JournalEntryStore(dbAccess, dataSourceName),
                new EntryNumberSequenceStore(dbAccess, dataSourceName),
                TimeProvider.System);
        }

        //トランザクション単位の入口。伝票の更新だけを見る UpdateAsync では、
        //同じ保存で送られてきた明細がまだ見えず、貸借一致を判定できない。
        //
        //保存を挟んで二段で通す (JournalSubmitGate 参照)。CLB は変更されたフィールドしか
        //送ってこないので、検証は「送られてきた差分」ではなく「書かれた姿」に対して行う。
        //違反があれば CompleteAsync が例外を投げ、この保存ごと巻き戻る。
        public override async Task<List<ModuleSubmitResult>> SubmitAsync(Guid transactionId, List<ModuleSubmitData> transactionData)
        {
            var pending = _journalGate.Prepare(transactionData);
            var results = await base.SubmitAsync(transactionId, transactionData);
            await _journalGate.CompleteAsync(pending, results);
            return results;
        }

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
