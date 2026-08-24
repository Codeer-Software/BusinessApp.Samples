using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.Server.Services;
using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO.Db;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessApp.Server.Controllers
{
    /// <summary>
    /// 計上済みの伝票に対する操作（取り消す・訂正する）。ADR-0016 の Web API。
    /// </summary>
    /// <remarks>
    /// <para><b>ここは HTTP の殻である。</b> 会計の判断は 1 行も書かない
    /// (ADR-0008)。中身は AccountingCore.Server の JournalAmendmentService が持ち、
    /// 普通の C# として単体テストしてある。**本体が 1〜2 行を超えたらサービスへ押し戻す。**</para>
    /// <para>保存経路 (CustomizedModuleDataIO.SubmitAsync) には乗らない。
    /// 「訂正する」は利用者が保存を押した契機を持たないためで、
    /// だからこそ<b>トランザクションはここで張る</b>。訂正は「取消を計上する」
    /// 「再計上の下書きを作る」の 2 つで 1 操作であり、途中で失敗したときに
    /// 取消だけが残ってはいけない (ADR-0015)。</para>
    /// </remarks>
    [Authorize, AutoValidateAntiforgeryToken]
    [ApiController]
    [Route("api/journals")]
    public class JournalsController : ControllerBase, IAsyncDisposable
    {
        //モジュール定義 (*.mod.json) の DataSourceName と一致していなければならない。
        const string AccountingDataSourceName = "BusinessAppSQLite";

        readonly DataService _dataService;

        public JournalsController(DataService dataService)
            => _dataService = dataService;

        public async ValueTask DisposeAsync()
            => await _dataService.DisposeAsync();

        /// <summary>取り消す。反対仕訳を作って計上まで進め、その伝票の識別子を返す。</summary>
        [HttpPost("reverse")]
        public Task<IActionResult> ReverseAsync(AmendRequest request)
            => RunAsync(request, async (service, originalId) =>
            {
                var id = await service.ReverseAsync(originalId);
                return AmendResult.Ok(id.Value, id.Value);
            });

        /// <summary>
        /// 訂正する。取消を計上し、原仕訳を写した再計上の下書きを作って、
        /// <b>その下書きの識別子</b>を返す。画面はそれを開く。
        /// </summary>
        [HttpPost("correct")]
        public Task<IActionResult> CorrectAsync(AmendRequest request)
            => RunAsync(request, async (service, originalId) =>
            {
                var started = await service.CorrectAsync(originalId);
                return AmendResult.Ok(started.ReversalId.Value, started.CorrectionId.Value);
            });

        /// <summary>
        /// トランザクションを張って操作を 1 つ実行し、業務の差し戻しを本文に載せて返す。
        /// </summary>
        /// <remarks>
        /// <para><b>業務の差し戻しも HTTP は 200 で返す。</b> 「取り消せない」「既に訂正されている」は
        /// 要求が正しく届いて処理された結果であって、通信や書式の失敗ではない。
        /// 成否は本文の status で表す。</para>
        /// <para>実務上の理由もある。<b>CLB の WebApiService は 2xx 以外の応答本文を読めず、
        /// 代わりに CLB 自身のエラートーストが出る</b>（qa/01 K-01）。400 で返すと
        /// 利用者に見えるのは「Invalid Error Code400」だけになり、
        /// 差し戻しの理由を伝えるという目的がまるごと果たせない。</para>
        /// <para>想定外の例外はここで握らない。既存の UseExceptionHandlerSendToFront が
        /// 500 で返し、画面は「サーバ応答 500」と出す。</para>
        /// </remarks>
        async Task<IActionResult> RunAsync(
            AmendRequest request, Func<JournalAmendmentService, JournalEntryId, Task<AmendResult>> operation)
        {
            //識別子は文字列で受ける。CLB のスクリプトは値を動的に扱うので、
            //数値として送らせると「型が違うから 400」という読めない失敗になる。
            if (!long.TryParse(request?.OriginalEntryId, out var originalEntryId))
            {
                return Ok(AmendResult.Rejected(
                    "対象の仕訳が指定されていない。",
                    [new AmendViolation(JournalViolationCodes.AmendmentTargetNotFound, "対象の仕訳が指定されていない。", null)]));
            }

            var dataSourceName = SystemConfig.Instance.DataSources
                .FirstOrDefault(e => e.Name == AccountingDataSourceName)?.Name
                ?? throw LowCodeException.Create($"データソース {AccountingDataSourceName} が設定にない");

            //トランザクションの API は IDbAccessor 側にある。
            IDbAccessor accessor = _dataService.DbAccess;
            var service = JournalAmendmentService.Create(accessor, dataSourceName, TimeProvider.System);

            accessor.StartTransaction();
            try
            {
                var result = await operation(service, new JournalEntryId(originalEntryId));
                await accessor.CommitAsync();
                return Ok(result);
            }
            catch (JournalPostingRejectedException e)
            {
                await accessor.RollbackAsync();
                return Ok(AmendResult.Rejected(
                    e.Message,
                    [.. e.Violations.Select(v => new AmendViolation(v.Code, v.Message, v.LineNo))]));
            }
            catch
            {
                await accessor.RollbackAsync();
                throw;
            }
        }
    }

    /// <summary>取り消す・訂正する対象の伝票。</summary>
    public record AmendRequest([property: JsonPropertyName("originalEntryId")] string? OriginalEntryId);

    /// <summary>
    /// 結果。<b>成否は HTTP ステータスではなく <c>status</c> で表す</b>（RunAsync の説明を参照）。
    /// </summary>
    /// <remarks>
    /// JSON の名前は属性で固定する。既定の命名規則に任せると、
    /// <b>設定を変えたときに誰も気づかないまま画面が値を読めなくなる</b>
    /// （スクリプト側はキーを文字列で引くので、コンパイルでは分からない）。
    /// </remarks>
    /// <param name="Status">"ok" か "rejected"。</param>
    /// <param name="OpenEntryId">
    /// 画面が開くべき伝票。取消では計上した反対仕訳、訂正では利用者が直す再計上の下書き。
    /// </param>
    /// <param name="ReversalId">計上した取消の伝票。</param>
    /// <param name="Message">差し戻しの文言。そのまま画面に出せる。</param>
    /// <param name="Violations">差し戻しの内訳。画面はコードで分岐できる。</param>
    public record AmendResult(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("openEntryId")] long OpenEntryId,
        [property: JsonPropertyName("reversalId")] long ReversalId,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("violations")] IReadOnlyList<AmendViolation> Violations)
    {
        public static AmendResult Ok(long reversalId, long openEntryId)
            => new("ok", openEntryId, reversalId, string.Empty, []);

        public static AmendResult Rejected(string message, IReadOnlyList<AmendViolation> violations)
            => new("rejected", 0, 0, message, violations);
    }

    public record AmendViolation(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("lineNo")] int? LineNo);
}
