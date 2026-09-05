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
public sealed class JournalAmendmentEndpoint(
    IDbAccessor accessor, JournalAmendmentService service, AccountingRoleStore roles)
{
    /// <summary>会計の役割を持たない利用者に返す文言。</summary>
    /// <remarks>
    /// <b>何が足りないかを言う</b>（docs/09 §2-3）。ただし<b>誰に頼めばよいかまでは書かない</b>——
    /// 役割を与える人は会社によって違う。
    /// </remarks>
    public const string NotAuthorized = "この操作を行う権限がありません。会計の権限を持つ利用者で操作してください。";

    /// <summary>本番もテストもここで組み立てる（<see cref="JournalAmendmentService.Create"/> と同じ理由）。</summary>
    public static JournalAmendmentEndpoint Create(
        IDbAccessor dbAccessor, string dataSourceName, TimeProvider timeProvider,
        IAuthenticationContext authenticationContext)
        => new(
            dbAccessor,
            JournalAmendmentService.Create(dbAccessor, dataSourceName, timeProvider, authenticationContext),
            new AccountingRoleStore(dbAccessor, dataSourceName, authenticationContext));

    /// <summary>この伝票にできること（取り消せるか・訂正できるか）を返す。<b>何も書かない。</b></summary>
    public Task<AmendResult> AvailabilityAsync(string? originalEntryId)
        => RunAsync(originalEntryId, async originalId =>
        {
            var available = await service.DescribeAsync(originalId);
            return AmendResult.Available(available);
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
        // **ここが、この経路にとって唯一の権限の関門である**（qa/03 L-22）。
        // CLB のモジュールの条件は `IDbAccessor` を直に使うこの経路に届かない。
        // **調べる操作（できること）も閉じる**——伝票が在るか・取り消せるかは会計のデータである。
        if (await roles.FindCurrentRoleAsync() is not AccountingRole role || !role.CanAmendJournals())
        {
            return AmendResult.Rejected(
                NotAuthorized,
                [new AmendViolation(JournalViolationCodes.NotAuthorized, NotAuthorized, null)]);
        }

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
/// <param name="ReversalEntryNo">
/// 既に取り消されているなら、その取消伝票の伝票番号。無ければ空文字。
/// <b>数値ではなく文字列で返す</b>——<c>originalEntryId</c> を文字列で受けているのと同じ理由で、
/// CLB のスクリプトは値を動的に扱い、<c>null</c> を読ませると型名が画面に出る（qa/01 K-02 の型）。
/// 空文字なら「無い」と、画面が 1 つの見方で判定できる。
/// </param>
/// <param name="CorrectionEntryNo">既に訂正されているなら、その再計上の伝票番号。無ければ空文字。</param>
public record AmendResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("openEntryId")] long OpenEntryId,
    [property: JsonPropertyName("reversalId")] long ReversalId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("violations")] IReadOnlyList<AmendViolation> Violations,
    [property: JsonPropertyName("canReverse")] bool CanReverse = false,
    [property: JsonPropertyName("canCorrect")] bool CanCorrect = false,
    [property: JsonPropertyName("reversalEntryNo")] string ReversalEntryNo = "",
    [property: JsonPropertyName("correctionEntryNo")] string CorrectionEntryNo = "")
{
    /// <summary>成功したときの文字列（画面はこれと一致するかで判定する）。</summary>
    public const string Succeeded = "ok";

    /// <summary>業務として差し戻したときの文字列。</summary>
    public const string RejectedStatus = "rejected";

    public static AmendResult Ok(long reversalId, long openEntryId)
        => new(Succeeded, openEntryId, reversalId, string.Empty, []);

    public static AmendResult Rejected(string message, IReadOnlyList<AmendViolation> violations)
        => new(RejectedStatus, 0, 0, message, violations);

    /// <summary>
    /// できること。<b>成否ではないので status は ok</b> で、内容は 2 つの真偽値で表す。
    /// </summary>
    /// <remarks>
    /// <b>調べた結果をそのまま受け取る。</b> 項目を 1 つずつ渡す形にすると、
    /// 調べる側に項目が増えたときに<b>ここで落としても誰も気づかない</b>
    /// （画面には既定値が届き、取消済みの断りが黙って消える）。
    /// </remarks>
    public static AmendResult Available(AmendmentAvailability available)
        => new(Succeeded, 0, 0, available.Reason, [],
               available.CanReverse, available.CanCorrect,
               EntryNoText(available.ReversalEntryNo), EntryNoText(available.CorrectionEntryNo));

    /// <summary>伝票番号を画面へ渡す形にする。<b>無いことは空文字で表す</b>（上の注記）。</summary>
    private static string EntryNoText(int? entryNo)
        => entryNo?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
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
