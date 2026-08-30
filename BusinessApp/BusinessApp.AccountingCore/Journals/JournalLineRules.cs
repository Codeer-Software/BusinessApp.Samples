namespace BusinessApp.AccountingCore.Journals;

/// <summary>
/// 仕訳明細の 1 行だけで決まる規則と、そのとき利用者へ返す文言。
/// </summary>
/// <remarks>
/// <para><b>同じ規則を 2 か所が見る。</b> ひとつは保存の手前に置く網
/// （サーバ側の <c>JournalSubmitRequirements</c>。DB が拒む値を保存へ渡さない）、
/// もうひとつは計上の検証（<see cref="JournalEntryValidator"/>）である。
/// 前者は「<b>保存そのものが失敗する値は検証まで届かない</b>」ために要り
/// （qa/03 L-16）、後者は保存経路を通らない伝票（取消・訂正はサーバが組み立てる）を守る。</para>
/// <para><b>文言をここに集める理由。</b> 同じ原因に 2 通りの言い方があると、
/// <b>どちらが先に捕まえたかで利用者に出る言葉が変わる</b>——利用者からは別の問題に見える。
/// 違反コードについては <see cref="JournalViolationCodes"/> 冒頭が
/// 「同じコードを別の原因に使い回さない」と定めており、これはその裏返しである。</para>
/// </remarks>
public static class JournalLineRules
{
    /// <summary>金額として保存できる値か。</summary>
    /// <remarks>
    /// <para><b>上限がある。</b> <c>journal_lines.amount</c> は SQLite の INTEGER（64 ビット）で、
    /// <c>CHECK (amount &gt; 0 AND typeof(amount) = 'integer')</c> が付いている。
    /// <see cref="long"/> に収まらない値は整数として格納できないので DB が拒む。</para>
    /// <para><b>上限を書かないと、関門自身が DB の受理集合の外の値を通す</b>ことになる
    /// （qa/03 L-14 で取引先の法人番号に起きたのと同じ形）。</para>
    /// </remarks>
    public static bool IsStorableAmount(decimal value)
        => value >= 1m && value <= long.MaxValue && value == decimal.Truncate(value);

    /// <summary>行番号として保存できる値か。</summary>
    /// <remarks>
    /// 上限が <see cref="int"/> なのは DB ではなく<b>こちらの都合</b>である——
    /// 差し戻しに「N 行目」と添えるための <see cref="Shared.Violation.LineNo"/> が <c>int?</c> で、
    /// そこに収まらない値は行番号として扱えない。
    /// </remarks>
    public static bool IsStorableLineNo(decimal value)
        => value >= 1m && value <= int.MaxValue && value == decimal.Truncate(value);

    // --- 差し戻しの文言（ここが正典。両方の層がこれを使う）---

    /// <summary>金額が 0 以下（<see cref="JournalViolationCodes.AmountNotPositive"/>）。</summary>
    public const string AmountNotPositive = "金額は 1 円以上にしてください。減額は借方と貸方を入れ替えて表します。";

    /// <summary>金額に 1 円未満の端数がある（<see cref="JournalViolationCodes.AmountNotStorable"/>）。</summary>
    public const string AmountHasFraction = "金額は 1 円単位で入力してください。";

    /// <summary>金額が扱える大きさを超えている（同上）。</summary>
    public const string AmountTooLarge = "金額が大きすぎます。桁を確かめてください。";

    // 行番号は画面が自動で振る（利用者は編集できない。開発者の決定。2026-08-30）。
    // だから下の 2 つは、画面を通らない経路（取込・API）でしか出ない。
    // **利用者に「番号を直せ」と言わない**——直す欄が画面に無い（docs/09 §2-3）。

    /// <summary>行番号が 1 以上の整数でない（<see cref="JournalViolationCodes.LineNoInvalid"/>）。</summary>
    public const string LineNoNotStorable = "明細の行番号が正しくありません。行番号は画面が自動で振るので、明細を入力し直してください。";

    /// <summary>同じ行番号が 2 つある（同上）。</summary>
    public const string LineNoDuplicated = "明細の行番号が重なっています。行番号は画面が自動で振るので、明細を入力し直してください。";

    /// <summary>税区分が空（<see cref="JournalViolationCodes.TaxCategoryMissing"/>）。</summary>
    public const string TaxCategoryMissing = "税区分を選んでください。税に関係のない行にも「対象外」を選びます。";

    // --- 入っていない項目（すべて JournalViolationCodes.RequiredValueMissing）---
    //
    // **画面に出ている見出しの語をそのまま使う**（docs/09 §2-2「内部表現を出さない」）。
    // 見出しの現在形はデザイン JSON が持つので、ここに一覧を写さない——変えたときに嘘になる。

    public const string LineNoMissing = "「行」が入っていません。明細の行を入れ直してください。";

    public const string DebitCreditMissing = "「借貸」で借方か貸方かを選んでください。";

    public const string AccountMissing = "勘定科目を選んでください。";

    public const string AmountMissing = "金額を入力してください。";

    public const string TransactionDateMissing = "取引日を入力してください。";

    public const string PostingDateMissing = "計上日を入力してください。";

    /// <summary>
    /// 会計年度が空。
    /// </summary>
    /// <remarks>
    /// 画面は計上日から会計年度を引いて入れるので、空になるのは
    /// <b>その計上日を含む会計年度が登録されていないとき</b>である。
    /// 「選んでください」と書くと、選べる年度があるのに選んでいないように読める。
    /// </remarks>
    public const string FiscalYearMissing =
        "計上日に対応する会計年度がありません。計上日を確かめるか、会計年度を登録してください。";
}
