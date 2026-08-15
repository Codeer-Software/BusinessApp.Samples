using BusinessApp.Server.Services;
using Codeer.LowCode.Blazor.Extras.Services;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessApp.Server.Controllers
{
    /// <summary>
    /// 利用者自身のパスワード変更（ADR-0059）。
    ///
    /// なぜサーバ拡張なのか（CLAUDE.md §2「CLB 単体で無理な所だけ C#」の判断）:
    ///   ・現在のパスワードの照合はハッシュ照合であり、CLB スクリプト（WASM）では原理的にできない。
    ///   ・AppUser は CurrentUser のソースなので UserWriteCondition を付けられない
    ///     （付けるとマイプロフィール・LinkField 表示などが全部壊れる＝CLB の仕様）。
    ///     したがって「自分の行だけ更新する」という制約を CLB 側で表現できない。
    ///     このエンドポイントは Claim の NameIdentifier だけを更新対象にするので、
    ///     他人の行を書き換える経路が構造的に存在しない。
    ///
    /// 画面（PasswordChange モジュール）は DB に紐づかない表示専用モジュールで、
    /// ここを叩くだけ。パスワードが AppUser の書き込み経路に乗らないのが要点。
    /// </summary>
    [Authorize]
    [ApiController, AutoValidateAntiforgeryToken]
    [Route("api/password")]
    public class PasswordController : ControllerBase
    {
        readonly DataService _dataService;

        public PasswordController(DataService dataService)
        {
            _dataService = dataService;
        }

        public class ChangeRequest
        {
            public string? CurrentPassword { get; set; }
            public string? NewPassword { get; set; }
        }

        /// <summary>画面に出す規則の説明文（検証と同じ 1 か所から生成される）。</summary>
        [HttpGet("policy")]
        public IActionResult GetPolicy()
        {
            var policy = PasswordPolicyService.Load(_dataService.DbAccess);
            return Ok(new
            {
                minLength = policy.MinLength,
                requiredKinds = policy.RequiredKinds,
                guidance = policy.GuidanceText(),
                note = policy.Note
            });
        }

        /// <summary>
        /// 入力途中／保存前の検証だけを行う（システム管理者のユーザー管理画面から呼ぶ）。
        /// 実際の書き込みは CLB 経由で、そこでもサーバ側が同じ検証をかける（ここは UX 用の先出し）。
        /// </summary>
        [HttpPost("validate")]
        public IActionResult Validate([FromBody] ValidateRequest? request)
        {
            var policy = PasswordPolicyService.Load(_dataService.DbAccess);
            var error = PasswordPolicyService.Validate(request?.Password ?? string.Empty, request?.UserName ?? string.Empty, policy);
            return Ok(new { ok = error == null, message = error ?? string.Empty, guidance = policy.GuidanceText() });
        }

        public class ValidateRequest
        {
            public string? Password { get; set; }
            public string? UserName { get; set; }
        }

        [HttpPost("change")]
        public async Task<IActionResult> ChangeAsync([FromBody] ChangeRequest? request)
        {
            var userId = DataService.GetCurrentUserId(HttpContext);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var tableInfo = SystemConfig.Instance.PasswordCheckUserTableInfo;
            var conn = _dataService.DbAccess.GetConnection(PasswordPolicyService.GetUserDataSourceName());

            // 更新対象はログイン中の本人だけ。リクエストからは対象ユーザーを指定できない
            var user = (await conn.QueryAsync<PasswordCheckUser>(
                $"SELECT {tableInfo.IdColumn} AS Id, {tableInfo.UserNameColumn} AS UserName," +
                $" {tableInfo.HashColumn} AS Hash, {tableInfo.SaltColumn} AS Salt" +
                $" FROM {tableInfo.TableName} WHERE {tableInfo.IdColumn} = @Id",
                new { Id = userId })).FirstOrDefault();
            if (user == null) return Ok(new { ok = false, message = "ログイン中のユーザーが見つかりません" });

            var current = request?.CurrentPassword ?? string.Empty;
            var next = request?.NewPassword ?? string.Empty;

            if (!PasswordHashHelper.VerifyHash(current, user.Hash, user.Salt))
                return Ok(new { ok = false, message = "現在のパスワードが違います" });

            var policy = PasswordPolicyService.Load(conn);
            var error = PasswordPolicyService.Validate(next, user.UserName, policy);
            if (error != null) return Ok(new { ok = false, message = error });

            if (policy.ForbidReuseCurrent && PasswordHashHelper.VerifyHash(next, user.Hash, user.Salt))
                return Ok(new { ok = false, message = "現在と同じパスワードには変更できません" });

            var hashData = PasswordHashHelper.CreateHash(next);
            await conn.ExecuteAsync(
                $"UPDATE {tableInfo.TableName} SET {tableInfo.HashColumn} = @Hash, {tableInfo.SaltColumn} = @Salt" +
                $" WHERE {tableInfo.IdColumn} = @Id",
                new { Hash = hashData.Hash ?? string.Empty, Salt = hashData.Salt ?? string.Empty, Id = userId });

            return Ok(new { ok = true, message = "パスワードを変更しました" });
        }
    }
}
