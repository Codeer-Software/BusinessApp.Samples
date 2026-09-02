namespace BusinessApp.AccountingCore.Journals;

/// <summary>
/// 仕訳モジュールが出す違反の識別子。
/// </summary>
/// <remarks>
/// docs/04 §1 の不変条件と 1 対 1 に対応するものは番号（<c>I-xx</c>）をそのまま使い、
/// それ以外は <c>E-</c> で始める。画面はこのコードで分岐し、文言は表示にだけ使う。
/// **同じコードを別の原因に使い回さない**（対処が違うものを画面が区別できなくなる）。
/// </remarks>
public static class JournalViolationCodes
{
    /// <summary>伝票単位で借方合計＝貸方合計。</summary>
    public const string Unbalanced = "I-01";

    /// <summary>仕訳明細は有効な会計期間に属する。</summary>
    public const string PeriodNotFound = "I-03";

    /// <summary>締め済み期間に計上・訂正・取消できない。</summary>
    public const string PeriodClosed = "I-04";

    /// <summary>計上済み仕訳は変更も削除もされない。もう一度計上できない。</summary>
    public const string AlreadyPosted = "I-05";

    /// <summary>訂正・取消は原仕訳を一意に特定する情報を持つ。</summary>
    public const string OriginalEntryMissing = "I-06";

    /// <summary>損益科目の仕訳明細には部門がある。</summary>
    public const string DepartmentMissing = "I-13";

    /// <summary>伝票番号は欠番を埋め直さず再利用しない。下書きが番号を持ってはいけない。</summary>
    public const string EntryNoNotAllowed = "I-17";

    /// <summary>明細が 1 行も無い。</summary>
    public const string NoLines = "E-LINES-EMPTY";

    /// <summary>
    /// <b>DDL の <c>NOT NULL</c> に当たる項目が入っていない。</b>
    /// 伝票（取引日・計上日・会計年度）と明細（借方貸方・勘定科目・金額・行番号）の両方に使う。
    /// <b>税区分の欠落だけは <see cref="TaxCategoryMissing"/></b>——計上の検証が先に固有のコードを
    /// 持っており、同じ原因に 2 つのコードを作らないため。
    /// </summary>
    /// <remarks>
    /// <para><b>これは検証ではなく、保存の手前に置く網である。</b> 入っていない値は
    /// 保存そのものが失敗するので <see cref="JournalEntryValidator"/> まで届かない
    /// （qa/03 L-16）。届かせると、利用者には DB の言葉で書かれた失敗が出る。</para>
    /// <para><b>「マスタに無い」とは別である。</b> 空欄は本コード、
    /// 値は入っているがマスタに無いのは <see cref="AccountUnknown"/> 等。
    /// 対処が違う（入れる／選び直す）ので分ける。</para>
    /// </remarks>
    public const string RequiredValueMissing = "E-REQUIRED";

    /// <summary>金額が正でない。金額は常に正で持ち、向きは借方貸方で表す。</summary>
    public const string AmountNotPositive = "E-AMOUNT";

    /// <summary>
    /// 金額をそのまま保存できない——<b>1 円未満の端数がある</b>か、<b>扱える大きさを超えている</b>。
    /// </summary>
    /// <remarks>
    /// <para><see cref="AmountNotPositive"/> と分けているのは、<b>対処が違う</b>からである
    /// （こちらは桁と単位を直す、あちらは借方貸方を入れ替える）。</para>
    /// <para>正でない値はここに含めない。<b>2 つの原因を 1 つのコードに束ねているのは
    /// 「金額をそのまま持てない」という同じ対処に収まるから</b>で、
    /// <see cref="LineNoInvalid"/> が重複と非整数を束ねているのと同じ扱いである。</para>
    /// </remarks>
    public const string AmountNotStorable = "E-AMOUNT-FORM";

    /// <summary>行番号が重複している、または正の整数でない。</summary>
    public const string LineNoInvalid = "E-LINE-NO";

    /// <summary>
    /// 選択肢の値が、DDL の <c>CHECK</c> が並べている値のどれでもない。
    /// </summary>
    /// <remarks>
    /// <b>「入っていない」（<see cref="RequiredValueMissing"/>）とは別である。</b>
    /// 空欄なら入れさせればよいが、こちらは<b>画面が出さない値が入っている</b>——
    /// 画面を通らない経路（取込・API）でしか起こらない。対処も違う（入れる／入力し直す）ので分ける。
    /// </remarks>
    public const string ChoiceNotStorable = "E-CHOICE";

    /// <summary>計上日が取引日より前になっている。</summary>
    public const string PostingDateBeforeTransaction = "E-DATE-ORDER";

    /// <summary>伝票の会計年度と、計上日が属する会計期間の会計年度が食い違っている。</summary>
    public const string FiscalYearMismatch = "E-FISCAL-YEAR";

    /// <summary>
    /// 会計期間はあるのに、それが属する会計年度がない。<b>マスタが壊れている</b>ので
    /// 利用者ではなく運用者への通知が要る。「期間がない」（I-03）と混ぜない。
    /// </summary>
    public const string PeriodOrphaned = "E-PERIOD-ORPHAN";

    /// <summary>勘定科目がマスタに無い。</summary>
    public const string AccountUnknown = "E-ACCOUNT-UNKNOWN";

    /// <summary>無効な勘定科目を新たに使っている。</summary>
    public const string AccountInactive = "E-ACCOUNT-INACTIVE";

    /// <summary>補助科目がマスタに無い。</summary>
    public const string SubAccountUnknown = "E-SUBACCOUNT-UNKNOWN";

    /// <summary>無効な補助科目を新たに使っている。</summary>
    public const string SubAccountInactive = "E-SUBACCOUNT-INACTIVE";

    /// <summary>補助科目が、その明細の勘定科目に属していない。</summary>
    public const string SubAccountMismatch = "E-SUBACCOUNT-MISMATCH";

    /// <summary>補助科目を使う科目なのに補助科目が無い。</summary>
    public const string SubAccountRequired = "E-SUBACCOUNT-REQUIRED";

    /// <summary>部門がマスタに無い。</summary>
    public const string DepartmentUnknown = "E-DEPARTMENT-UNKNOWN";

    /// <summary>無効な部門を新たに使っている。</summary>
    public const string DepartmentInactive = "E-DEPARTMENT-INACTIVE";

    /// <summary>税区分が空。税に意味のない行にも「対象外」を明示する。</summary>
    public const string TaxCategoryMissing = "E-TAX-CATEGORY";

    /// <summary>消費税行の親行の指定が不正。</summary>
    public const string TaxLineParentInvalid = "E-TAX-PARENT";

    /// <summary>消費税行が本体行から引き継ぐべき値を引き継いでいない。</summary>
    public const string TaxLineNotInherited = "E-TAX-INHERIT";

    // --- 取消・訂正に共通（原仕訳の側の規則。docs/04 §5・AmendmentRules）---

    /// <summary>取り消す／訂正しようとした仕訳が存在しない。</summary>
    public const string AmendmentTargetNotFound = "E-AMEND-NOT-FOUND";

    /// <summary>計上していない仕訳を取り消す／訂正しようとした。</summary>
    public const string AmendmentTargetNotPosted = "E-AMEND-NOT-POSTED";

    /// <summary>保存されていない仕訳を取り消す／訂正しようとした（原仕訳を特定できない）。</summary>
    public const string AmendmentTargetUnidentified = "E-AMEND-NO-ID";

    /// <summary>取消・訂正の計上日が原仕訳より前になっている。</summary>
    public const string AmendmentBeforeOriginal = "E-AMEND-DATE";

    /// <summary>対象にできない種別（取消・期首残高・決算振替・繰越）を取り消す／訂正しようとした。</summary>
    public const string AmendmentTargetNotAmendable = "E-AMEND-TARGET-TYPE";

    // --- 取消 ---

    /// <summary>既に取り消されている仕訳を、もう一度取り消そうとした。</summary>
    public const string AlreadyReversed = "E-REVERSAL-DUPLICATE";

    // --- 訂正 ---

    /// <summary>
    /// 取り消されていない原仕訳を訂正しようとした。
    /// <b>原仕訳が生きたまま再計上が載ると、取引が帳簿に二重に計上される。</b>
    /// </summary>
    public const string OriginalNotReversed = "E-CORRECTION-ORIGINAL-LIVE";

    /// <summary>同じ原仕訳に対する再計上を 2 本目も計上しようとした。</summary>
    public const string AlreadyCorrected = "E-CORRECTION-DUPLICATE";

    /// <summary>再計上の計上日が、原仕訳を取り消した日より前になっている。</summary>
    public const string CorrectionBeforeReversal = "E-CORRECTION-BEFORE-REVERSAL";

    /// <summary>まだ実装していない種別の仕訳を計上しようとした。</summary>
    public const string EntryTypeNotSupported = "E-ENTRY-TYPE";

    /// <summary>
    /// 既にある伝票の種別を変えようとした。
    /// <b>種別ごとの関門が丸ごと外れる</b>ので、下書きのうちでも変えさせない。
    /// </summary>
    public const string EntryTypeImmutable = "E-ENTRY-TYPE-IMMUTABLE";

    /// <summary>
    /// 取引先の登録が同じ日から 2 件あり、明細へ写す登録番号を決められない。
    /// </summary>
    /// <remarks>
    /// 入力の時点で止めるのが本筋（<c>PartnerRegistrationSubmitGate</c>）だが、
    /// 取込（フェーズ 6）など別の経路で入った場合の最後の砦としてここでも止める。
    /// <b>黙ってどちらかを選ばない</b>——計上済みは不変（ADR-0004）なので、
    /// 誤った番号は永久に残る。
    /// </remarks>
    public const string AmbiguousRegistration = "E-REGISTRATION-AMBIGUOUS";
}
