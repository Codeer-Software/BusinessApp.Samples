namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 伝票を複製する（[ADR-0048]）。<b>会計補助（ステートレス）</b>であり、会計コアではない（ADR-0049）。
/// </summary>
/// <remarks>
/// <para><b>アプリケーション層に置く。</b> 写す欄の取捨選択はユーザビリティのためのもので、
/// 会計コアのドメイン知識ではない（ADR-0049 の決定 5。ドメイン層に置きたくなったら、
/// 本当に補助か再検討する）。ドメインから借りるのは、取消・訂正の対象にできる種別
/// （<see cref="EntryTypeExtensions.IsAmendable"/>）とその断り（<see cref="AmendmentRules.NotAmendableTarget"/>）、
/// 自分が付けた接頭辞を落とした本文（<see cref="AmendmentRules.Body"/>）だけである。</para>
/// <para><b>複製は新しい記帳である。</b> 取消・訂正とは別の操作で、原仕訳の状態を何も見ない
/// ——下書きからも計上済みからも、取消済み・訂正済みの<b>原仕訳</b>からも作れる。</para>
/// <para><b>写すのは取引の内容だけで、出来事の記録は 1 つも写さない</b>（ADR-0048 の決定 1・2）。
/// <b>「写さない」を既定にする</b>——写し忘れは利用者が入れ直せるが、
/// <b>写しすぎは帳簿に嘘の記録を残す</b>。だから明細も <c>with</c> で複製せず、
/// <b>写す欄だけを書き出して組み立てる</b>（欄が増えた日に、黙って写されないようにする。
/// <c>JournalDuplicationTests</c> がその一覧を毎回突き合わせる）。</para>
/// <para>純粋関数である。DB も時計も知らない（ADR-0008）。</para>
/// </remarks>
public static class JournalDuplication
{
    /// <summary>
    /// 原仕訳と同じ内容の下書きを 1 本作る。
    /// </summary>
    /// <param name="original">複製する伝票。<b>状態は問わない。</b></param>
    /// <param name="postingDate">計上日。<b>複製した日</b>であって原仕訳の計上日ではない（I-04）。</param>
    /// <param name="enteredAt">入力年月日。システムが決める（docs/10 §2）。</param>
    /// <param name="fiscalYearId">計上日の属する会計年度。<b>原仕訳の年度ではない。</b></param>
    public static DuplicationResult Duplicate(
        JournalEntry original,
        DateOnly postingDate,
        DateTimeOffset enteredAt,
        FiscalYearId fiscalYearId)
    {
        ArgumentNullException.ThrowIfNull(original);

        // **元にできるのは、取消・訂正の対象にできる種別だけ**（ADR-0048 の決定 6）。
        // 期首残高・決算振替・繰越を通常の伝票として写すと残高の前提が崩れ、
        // 取消伝票は貸借の反転した内容しか写せない（戻したいなら原仕訳を複製する）。
        if (!original.EntryType.IsAmendable())
        {
            // **断りも取消・訂正のものを借りる。** 集合が同じなので、別のコードや文を作ると
            // 「対象にできない種別」が 2 つの名前を持つ（見出し「複製できません」は差し戻しの側が付ける）。
            return new DuplicationResult([AmendmentRules.NotAmendableTarget(original.EntryType)]);
        }

        var draft = new JournalEntry
        {
            FiscalYearId = fiscalYearId,

            // **取引日は写す。** 複製がいちばん効くのは「同じ取引をもう一度起こす」場面で、
            // 訂正の下書きを消したあとの作り直しがまさにそれである（ADR-0048 の決定 5）。
            TransactionDate = original.TransactionDate,
            PostingDate = postingDate,
            Status = EntryStatus.Draft,

            // **訂正を複製しても、できるのは通常の記帳である。**
            // 種別を写すと、原仕訳を持たない訂正伝票ができて I-06 を破る。
            EntryType = EntryType.Normal,

            // **訂正の接頭辞は落とす**（ADR-0048 の決定 4）。写すと「伝票番号 44 の訂正」と名乗る
            // 通常の伝票ができ、**していない訂正を帳簿に書く**ことになる。
            // **本文が空なら NULL**——空文字を残すと空値検索が取りこぼす（docs/10 §4-4）。
            // 計上には摘要が要るので、利用者はそこで何の取引かを書く（それが正しい）。
            Description = AmendmentRules.Body(original) is { Length: > 0 } body ? body : null,
            PartnerId = original.PartnerId,
            EnteredAt = enteredAt,
            Lines = [.. Copy(original.Lines)],
        };

        return new DuplicationResult([], draft);
    }

    /// <summary>
    /// 明細を写す。<b>消費税行は落とし、行番号を 1 から振り直す。</b>
    /// </summary>
    /// <remarks>
    /// 税行はシステムが計上時に作る（docs/11 §2）ので、写すと
    /// <b>利用者が直せない行だけが古い金額のまま残る</b>。落とすと行番号に穴が空くので振り直す。
    /// </remarks>
    private static IEnumerable<JournalLine> Copy(IReadOnlyList<JournalLine> lines)
        => lines.Where(line => !line.IsTaxLine)
                .Select((line, index) => new JournalLine
                {
                    LineNo = index + 1,
                    DebitCredit = line.DebitCredit,
                    AccountId = line.AccountId,
                    SubAccountId = line.SubAccountId,
                    DepartmentId = line.DepartmentId,
                    PartnerId = line.PartnerId,
                    Amount = line.Amount,
                    TaxCategoryId = line.TaxCategoryId,
                    TaxTreatment = line.TaxTreatment,

                    // **課税仕入れの時点は写す。** 入っていなければ NULL のままで、
                    // 登録番号を引く日が取引日になる（<c>LedgerSnapshotWriter.TaxPointOf</c>）——
                    // つまり**写しても、既定の伝票では取引日に従う**。
                    // **入っている値は「取引の事実」である**——支払日で起票した未払金の決済や
                    // 締め日基準の一括計上では、取引日と課税仕入れの日がずれる（docs/13 の保留リスト）。
                    // 落とすと同じ取引なのに課税仕入れの日が黙って変わり、
                    // **画面にこの欄が無い**ので利用者は入れ直せない（2026-09-09 の自己レビュー）。
                    TaxPoint = line.TaxPoint,
                    ItemDescription = line.ItemDescription,
                    BookOnlyDeduction = line.BookOnlyDeduction,
                });
}

/// <summary>
/// 複製した結果。
/// </summary>
/// <remarks>
/// <para><b>違反が空かどうかで判定しない</b>（<see cref="ReversalResult"/> と同じ作法）。</para>
/// <para><b>兄弟（<see cref="ReversalResult"/>・<c>CorrectionStartResult</c>）と同じ形にしてある。</b>
/// 誰も呼ばない複製コンストラクタは <c>GeneratedCopyConstructorTests</c> が 1 行触って埋める
/// （ADR-0012 §3 が「カバレッジを埋めるためのテストを書かない」に対して認めた、理由つきの例外）
/// ——<b>同じ役目の型が 2 通りの形になるほうが読みにくい</b>（2026-09-09 の自己レビュー）。</para>
/// </remarks>
/// <param name="Violations">見つかった違反。</param>
/// <param name="Draft">作れたときの下書き。作れなかったときは <c>null</c>。</param>
public sealed record DuplicationResult(IReadOnlyList<Violation> Violations, JournalEntry? Draft = null)
{
    /// <summary>作れたか。</summary>
    public bool Created => Draft is not null;
}
