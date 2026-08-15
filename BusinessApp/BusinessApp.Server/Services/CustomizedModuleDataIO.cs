using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Extras.Services;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;

namespace BusinessApp.Server.Services
{
    public class CustomizedModuleDataIO : ModuleDataIO
    {
        //一括INSERT (multi-row INSERT) の有効化。純Addのみの大量Submit (一括取込) がこの行数以上のとき束ねて挿入される。
        //-1 (コア既定) で無効
        static CustomizedModuleDataIO() => BulkAddThreshold = 100;

        readonly DesignData _designData;
        readonly IDbAccessor _dbAccess;

        public CustomizedModuleDataIO(DesignData designData, IAuthenticationContext authenticationContext, IDbAccessor dbAccess, ITemporaryFileManager temporaryFileManager)
            : base(designData, authenticationContext, dbAccess, temporaryFileManager)
        {
            _designData = designData;
            _dbAccess = dbAccess;
        }

        protected override async Task<string> AddAsync(Guid transactionId, Guid moduleSubmitId, ModuleData data)
        {
            var moduleDesign = _designData.Modules.Find(data.Name);
            if (moduleDesign == null) throw LowCodeException.Create("invalid design");

            EnforcePasswordPolicy(moduleDesign, data);
            PasswordHashHelper.ApplyPasswordHash(moduleDesign, data);
            return await base.AddAsync(transactionId, moduleSubmitId, data);
        }

        //一括INSERT (大量取込) は行ごとの AddAsync を通らないため、同じ加工をこちらでも行う
        protected override async Task BulkAddAsync(Guid transactionId, List<ModuleData> datas)
        {
            var moduleDesign = _designData.Modules.Find(datas.FirstOrDefault()?.Name ?? string.Empty);
            if (moduleDesign == null) throw LowCodeException.Create("invalid design");

            foreach (var data in datas)
            {
                EnforcePasswordPolicy(moduleDesign, data);
                PasswordHashHelper.ApplyPasswordHash(moduleDesign, data);
            }
            await base.BulkAddAsync(transactionId, datas);
        }

        protected async override Task UpdateAsync(Guid transactionId, Guid moduleSubmitId, ModuleData data)
        {
            var moduleDesign = _designData.Modules.Find(data.Name);
            if (moduleDesign == null) throw LowCodeException.Create("invalid design");

            EnforcePasswordPolicy(moduleDesign, data);
            PasswordHashHelper.ApplyPasswordHash(moduleDesign, data);
            await base.UpdateAsync(transactionId, moduleSubmitId, data);
        }

        /// <summary>
        /// パスワードポリシーの強制（ADR-0059）。
        /// ハッシュ化の直前＝CLB 経由の全パスワード書き込みが必ず通る 1 点で判定する
        /// （システム管理者のユーザー管理・一括取込のどちらもここを通る）。
        /// 判定そのものは PasswordPolicyService に一本化し、クライアント側には複製しない。
        /// パスワード欄が空のとき＝「変更しない」なので検証しない（ApplyPasswordHash も同じ扱い）。
        /// </summary>
        void EnforcePasswordPolicy(ModuleDesign moduleDesign, ModuleData data)
        {
            var passwordDesign = moduleDesign.Fields.OfType<PasswordFieldDesign>().FirstOrDefault();
            if (passwordDesign == null) return;
            if (!data.Fields.TryGetValue(passwordDesign.Name, out var passwordData)) return;
            if (passwordData is not PasswordFieldData password) return;
            if (string.IsNullOrEmpty(password.Value)) return;

            var policy = PasswordPolicyService.Load(_dbAccess);
            var error = PasswordPolicyService.Validate(password.Value, FindUserName(moduleDesign, data), policy);
            if (error != null) throw LowCodeException.Create(error);
        }

        /// <summary>
        /// ログイン識別名を送信データから拾う。列名は appsettings の PasswordCheckUserTableInfo が正
        /// （フィールド名「ユーザー識別名」を直に書くと、モジュール側のリネームで静かに効かなくなる）。
        /// 見つからないときは空を返し、「識別名と同一の禁止」だけが適用されない（長さ・文字種は効く）。
        /// </summary>
        static string FindUserName(ModuleDesign moduleDesign, ModuleData data)
        {
            var userNameColumn = SystemConfig.Instance.PasswordCheckUserTableInfo.UserNameColumn;
            if (string.IsNullOrEmpty(userNameColumn)) return string.Empty;

            foreach (var field in moduleDesign.Fields)
            {
                if (field is not TextFieldDesign text) continue;
                if (text.DbColumn != userNameColumn) continue;
                if (!data.Fields.TryGetValue(text.Name, out var fieldData)) return string.Empty;
                if (fieldData is TextFieldData textData) return textData.Value ?? string.Empty;
                return string.Empty;
            }
            return string.Empty;
        }
    }
}
