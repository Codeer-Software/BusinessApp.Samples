namespace BusinessApp.AccountingCore.Server.Journals;

using System.Globalization;
using System.Text.Json.Serialization;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Shared;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 「取り消す」「訂正する」の入口のうち、<b>HTTP でない部分</b>（ADR-0016）。
/// </summary>
/// <remarks>
/// <para>コントローラ（<c>BusinessApp.Server</c>）に残るのは認証・経路・データソース名の解決だけで、
/// <b>識別子の解釈・トランザクション・結果への写像はここが持つ</b>。</para>
/// <para><b>なぜ移したか。</b> コントローラは <c>BusinessApp.Server</c>（CLB テンプレート由来）に
/// あってカバレッジにもミューテーションにも載らず、<b>テストが 1 本も無かった</b>——
/// JSON の項目名を 1 つ落としても、<c>[Authorize]</c> を消しても緑だった（qa/02 R4-03）。
/// ここへ出せば既存のゲートに載る。</para>
/// <para><b>業務の差し戻しは例外ではなく戻り値で表す。</b> 「取り消せない」「既に訂正されている」は
/// 要求が正しく処理された結果である。想定外の例外はそのまま投げ、コントローラの外側の
/// 例外ハンドラに任せる（ADR-0016）。</para>
/// </remarks>
public sealed class JournalAmendmentEndpoint(IDbAccessor accessor, JournalAmendmentService service)
{
    /// <summary>本番もテストもここで組み立てる（<see cref="JournalAmendmentService.Create"/> と同じ理由）。</summary>
    public static JournalAmendmentEndpoint Create(
        IDbAccessor dbAccessor, string dataSourceName, TimeProvider timeProvider,
        IAuthenticationContext authenticationContext)
        => new(
            dbAccessor,
            JournalAmendmentService.Create(dbAccessor, dataSourceName, timeProvider, authenticationContext));

    /// <summary>この伝票にできること（取り消せるか・訂正できるか）を返す。<b>何も書かない。</b></summary>
    public Task<AmendResult> AvailabilityAsync(string? originalEntryId)
        => RunAsync(originalEntryId, async originalId =>
        {
            var available = await service.DescribeAsync(originalId);
            return AmendResult.Available(available.CanReverse, available.CanCorrect, available.Reason);
        });

    /// <summary>取り消す。反対仕訳を作って計上まで進め、その伝票の識別子を返す。</summary>
    public Task<AmendResult> ReverseAsync(string? originalEntryId)
        => RunAsync(originalEntryId, async originalId =>
        {
            var id = await service.ReverseAsync(originalId);
            return AmendResult.Ok(id.Value, id.Value);
        });

    /// <summary>
    /// 訂正する。取消を計上し、原仕訳を写した再計上の下書きを作って、
    /// <b>その下書きの識別子</b>を返す。画面はそれを開く。
    /// </summary>
    public Task<AmendResult> CorrectAsync(string? originalEntryId)
        => RunAsync(originalEntryId, async originalId =>
        {
            var started = await service.CorrectAsync(originalId);
            return AmendResult.Ok(started.ReversalId.Value, started.CorrectionId.Value);
        });

    /// <summary>
    /// 識別子を解釈し、トランザクションを張って操作を 1 つ実行する。
    /// </summary>
    /// <remarks>
    /// <b>識別子は文字列で受ける。</b> CLB のスクリプトは値を動的に扱うので、
    /// 数値として送らせると「型が違うから 400」という読めない失敗になる。
    /// </remarks>
    private async Task<AmendResult> RunAsync(
        string? originalEntryId, Func<JournalEntryId, Task<AmendResult>> operation)
    {
        if (!long.TryParse(originalEntryId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return AmendResult.Rejected(
                "対象の伝票が指定されていません。",
                [new AmendViolation(JournalViolationCodes.AmendmentTargetNotFound, "対象の伝票が指定されていません。", null)]);
        }

        try
        {
            return await DbTransactionScope.RunAsync(accessor, () => operation(new JournalEntryId(id)));
        }
        catch (JournalPostingRejectedException e)
        {
            // ここに来た時点で巻き戻っている（DbTransactionScope が投げ直す前に戻す）。
            return AmendResult.Rejected(e.Message, [.. e.Violations.Select(AmendViolation.From)]);
        }
    }
}

/// <summary>取り消す・訂正する対象の伝票。</summary>
public record AmendRequest([property: JsonPropertyName("originalEntryId")] string? OriginalEntryId);

/// <summary>
/// 結果。<b>成否は HTTP ステータスではなく <c>status</c> で表す</b>（ADR-0016）。
/// </summary>
/// <remarks>
/// <para><b>業務の差し戻しも 200 で返す。</b> 「取り消せない」「既に訂正されている」は
/// 要求が正しく届いて処理された結果であって、通信や書式の失敗ではない。
/// 実務上の理由もある——<b>CLB の <c>WebApiService</c> は 2xx 以外の応答本文を読めず</b>、
/// 代わりに CLB 自身のエラートーストが出る（qa/01 K-01）。400 で返すと利用者に見えるのは
/// 「Invalid Error Code400」だけになり、差し戻しの理由を伝えるという目的が果たせない。</para>
/// <para>JSON の名前は属性で固定する。既定の命名規則に任せると、
/// <b>設定を変えたときに誰も気づかないまま画面が値を読めなくなる</b>
/// （スクリプト側はキーを文字列で引くので、コンパイルでは分からない）。</para>
/// </remarks>
/// <param name="Status">"ok" か "rejected"。</param>
/// <param name="OpenEntryId">
/// 画面が開くべき伝票。取消では計上した反対仕訳、訂正では利用者が直す再計上の下書き。
/// </param>
/// <param name="ReversalId">計上した取消の伝票。</param>
/// <param name="Message">差し戻しの文言。そのまま画面に出せる。</param>
/// <param name="Violations">差し戻しの内訳。画面はコードで分岐できる。</param>
/// <param name="CanReverse">取り消せるか（<see cref="Available"/> のときだけ意味を持つ）。</param>
/// <param name="CanCorrect">訂正できるか（<see cref="Available"/> のときだけ意味を持つ）。</param>
public record AmendResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("openEntryId")] long OpenEntryId,
    [property: JsonPropertyName("reversalId")] long ReversalId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("violations")] IReadOnlyList<AmendViolation> Violations,
    [property: JsonPropertyName("canReverse")] bool CanReverse = false,
    [property: JsonPropertyName("canCorrect")] bool CanCorrect = false)
{
    /// <summary>成功したときの文字列（画面はこれと一致するかで判定する）。</summary>
    public const string Succeeded = "ok";

    /// <summary>業務として差し戻したときの文字列。</summary>
    public const string RejectedStatus = "rejected";

    public static AmendResult Ok(long reversalId, long openEntryId)
        => new(Succeeded, openEntryId, reversalId, string.Empty, []);

    public static AmendResult Rejected(string message, IReadOnlyList<AmendViolation> violations)
        => new(RejectedStatus, 0, 0, message, violations);

    /// <summary>できること。<b>成否ではないので status は ok</b> で、内容は 2 つの真偽値で表す。</summary>
    public static AmendResult Available(bool canReverse, bool canCorrect, string reason)
        => new(Succeeded, 0, 0, reason, [], canReverse, canCorrect);
}

/// <summary>差し戻しの内訳 1 件。</summary>
/// <param name="Code">違反の識別子。画面はこれで分岐できる。</param>
/// <param name="Message">利用者に見せる説明。</param>
/// <param name="LineNo">対象の明細行。伝票全体の違反は null。</param>
public record AmendViolation(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("lineNo")] int? LineNo)
{
    /// <summary>ドメインの違反から作る。</summary>
    public static AmendViolation From(Violation violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        return new AmendViolation(violation.Code, violation.Message, violation.LineNo);
    }
}
