using System.Data;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DbAccess;
using Dapper;

namespace BusinessApp.Server.Services
{
    /// <summary>
    /// パスワードポリシー（password_policies テーブルの 1 行）。閾値はコードに埋めずマスタから読む。
    /// 既定値は「テーブルが無い / 行が無い」ときのフォールバックで、安全側（厳しめ）に倒してある。
    /// </summary>
    public class PasswordPolicy
    {
        public int MinLength { get; set; } = 12;

        /// <summary>英大文字 / 英小文字 / 数字 / 記号 のうち最低何種類を含めるか（0〜4。0 = 制限なし）</summary>
        public int RequiredKinds { get; set; } = 3;

        public bool AllowSameAsUserName { get; set; }

        public bool ForbidReuseCurrent { get; set; } = true;

        public string Note { get; set; } = string.Empty;

        /// <summary>画面に出す規則の説明文。検証と同じ 1 か所から作り、文言と実装がズレないようにする。</summary>
        public string GuidanceText()
        {
            var rules = new List<string> { $"{MinLength} 文字以上" };
            if (RequiredKinds > 0)
                rules.Add($"英大文字・英小文字・数字・記号のうち {RequiredKinds} 種類以上を含む");
            if (!AllowSameAsUserName)
                rules.Add("ユーザー識別名と同じものは使えません");
            if (ForbidReuseCurrent)
                rules.Add("現在のパスワードと同じものには変更できません");
            return string.Join(" ／ ", rules);
        }
    }

    /// <summary>
    /// パスワード検証の唯一の実装（ADR-0059）。
    /// 呼び出し元は 2 つ:
    ///   ・PasswordController … 利用者自身のパスワード変更
    ///   ・CustomizedModuleDataIO … CLB 経由の全パスワード書き込み（システム管理者のユーザー管理）
    /// クライアント側には同じ判定を置かない（規則の説明文だけ GuidanceText() を API 経由で配る）。
    /// </summary>
    public static class PasswordPolicyService
    {
        public static PasswordPolicy Load(IDbConnection conn)
        {
            try
            {
                var row = conn.QueryFirstOrDefault<PasswordPolicyRow>(
                    "SELECT min_length AS MinLength, required_kinds AS RequiredKinds," +
                    " allow_same_as_user_name AS AllowSameAsUserName, forbid_reuse_current AS ForbidReuseCurrent," +
                    " note AS Note FROM password_policies WHERE id = 1");
                if (row == null) return new PasswordPolicy();
                return new PasswordPolicy
                {
                    MinLength = row.MinLength,
                    RequiredKinds = row.RequiredKinds,
                    AllowSameAsUserName = row.AllowSameAsUserName != 0,
                    ForbidReuseCurrent = row.ForbidReuseCurrent != 0,
                    Note = row.Note ?? string.Empty
                };
            }
            catch
            {
                // テーブル未作成の環境でもログインとユーザー管理は動き続けるべきなので、既定値へ落とす
                return new PasswordPolicy();
            }
        }

        /// <summary>データソース名は AppUser モジュール（＝CurrentUserModuleDesignName）の接続先に合わせる。</summary>
        public static PasswordPolicy Load(IDbAccessor dbAccess)
            => Load(dbAccess.GetConnection(GetUserDataSourceName()));

        public static string GetUserDataSourceName()
        {
            var designData = DesignerService.GetDesignData();
            return designData.Modules.Find(designData.AppSettings.CurrentUserModuleDesignName)?.DataSourceName
                ?? string.Empty;
        }

        /// <summary>問題があればエラーメッセージ、無ければ null を返す。</summary>
        public static string? Validate(string password, string userName, PasswordPolicy policy)
        {
            if (string.IsNullOrEmpty(password)) return "パスワードを入力してください";

            if (password.Length < policy.MinLength)
                return $"パスワードは {policy.MinLength} 文字以上で入力してください（現在 {password.Length} 文字）";

            if (policy.RequiredKinds > 0)
            {
                var kinds = CountCharKinds(password);
                if (kinds < policy.RequiredKinds)
                    return $"パスワードは英大文字・英小文字・数字・記号のうち {policy.RequiredKinds} 種類以上を含めてください（現在 {kinds} 種類）";
            }

            if (!policy.AllowSameAsUserName
                && !string.IsNullOrEmpty(userName)
                && string.Equals(password, userName, StringComparison.OrdinalIgnoreCase))
                return "ユーザー識別名と同じパスワードは使用できません";

            return null;
        }

        static int CountCharKinds(string password)
        {
            var upper = false;
            var lower = false;
            var digit = false;
            var symbol = false;
            foreach (var c in password)
            {
                if (char.IsUpper(c)) upper = true;
                else if (char.IsLower(c)) lower = true;
                else if (char.IsDigit(c)) digit = true;
                else symbol = true;
            }
            var kinds = 0;
            if (upper) kinds++;
            if (lower) kinds++;
            if (digit) kinds++;
            if (symbol) kinds++;
            return kinds;
        }

        class PasswordPolicyRow
        {
            public int MinLength { get; set; }
            public int RequiredKinds { get; set; }
            public int AllowSameAsUserName { get; set; }
            public int ForbidReuseCurrent { get; set; }
            public string? Note { get; set; }
        }
    }
}
