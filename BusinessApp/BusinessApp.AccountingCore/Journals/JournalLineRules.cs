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
    // **利用者に「番号を直せ」と言わない**——直す欄が画面に無い（docs/21 §2-3）。

    /// <summary>行番号が 1 以上の整数でない（<see cref="JournalViolationCodes.LineNoInvalid"/>）。</summary>
    public const string LineNoNotStorable = "明細の行番号が正しくありません。行番号は画面が自動で振るので、明細を入力し直してください。";

    /// <summary>同じ行番号が 2 つある（同上）。</summary>
    /// <remarks>
    /// <b>番号は文に埋める</b>（<c>Violation.LineNo</c> に載せない）。「行 3: 行番号が重なっています」では、
    /// 行 3 が 2 つあるという事実を読み手に推測させる（2026-09-10 の自己レビュー）。
    /// </remarks>
    public static string LineNoDuplicatedAt(int lineNo)
        => $"行番号 {lineNo} が 2 つの明細に付いています。行番号は画面が自動で振るので、明細を入力し直してください。";

    /// <summary>税区分が空（<see cref="JournalViolationCodes.TaxCategoryMissing"/>）。</summary>
    public const string TaxCategoryMissing = "税区分を選んでください。税に関係のない行にも「対象外」を選びます。";

    // --- 利用者が触ってよい場面が無い欄（qa/03 L-30）---
    //
    // 画面では閲覧専用にしてある。ここで断るのは、画面を通らない経路（取込・API）のため。

    // 「自動で」と言う——「伝票番号は計上のときに自動で付きます」（JournalEntryValidator）と同じ語。「システム」は画面の語彙に無い。
    public const string OriginalEntryNotEditable =
        "元の伝票は、取消・訂正のときに自動で入ります。手で入れたり消したりはできません。";

    // --- 同時操作（qa/03 L-31）---
    //
    // **入力内容は 1 つも悪くない。** 悪いのは「開いたあとに別の人が変えた」ことなので、
    // 「入力内容を確かめ」とは言わず、開き直して相手の変更を確かめるよう案内する（docs/21 §2-3）。
    // **「もう一度入力」とは言わない**——何も直さず「計上する」を押しただけでも起きる。
    // **消えた伝票に「開き直せ」とは言わない**——開き直す対象が無い。一覧へ戻す。

    public const string ChangedByOthers =
        "この伝票は、あなたが開いたあとに別の人が変更しました。画面を開き直して、その変更を確かめてから、もう一度操作してください。";

    /// <summary>消えた伝票を<b>保存</b>しようとした。手元の入力をどうすればよいかまで言う。</summary>
    public const string DeletedByOthers =
        "この伝票は、あなたが開いたあとに別の人が削除しました。振替伝票の一覧に戻ってください。"
        + "この内容が必要なら、新しい振替伝票として入力し直してください。";

    /// <summary>消えた伝票を<b>削除</b>しようとした。入力は無いので、戻り先だけを言う。</summary>
    public const string AlreadyDeletedByOthers =
        "この伝票は、あなたが開いたあとに別の人が削除しました。振替伝票の一覧に戻ってください。";

    /// <summary>
    /// 変える・消す明細が消えていた。<b>件数で束ねる</b>——差分に行番号は無いので行は指せず、
    /// 行ごとに同じ文を並べても読み手には同じ文が並ぶだけになる（2026-09-10 の自己レビュー）。
    /// </summary>
    // **伝票ごと消えている場合もこの文になる**——明細だけを直した保存には伝票の差分が無く（qa/01 F-41）、
    // 消えた明細の親は分からない。開き直す先が無いときの行き先まで言う。
    public static string LinesDeletedByOthers(int count)
        => $"明細 {count} 行が、あなたが開いたあとに別の人に削除されています。画面を開き直して、残っている明細を確かめてください。"
           + "伝票そのものが無ければ、振替伝票の一覧に戻ってください。";

    // --- 選択肢の値が DDL の CHECK の外（すべて JournalViolationCodes.ChoiceNotStorable）---
    //
    // **画面からは起こらない。** 貸借・状態・種別はどれも選択欄で、候補は画面が出す。
    // **鉤括弧の中は画面のラベルそのもの**（docs/21 §2-6）。明細の見出しは「貸借」である。
    // 起こるのは画面を通らない経路（取込・API）だけなので、
    // **「選び直してください」ではなく「入力し直してください」と言う**（docs/21 §2-3。
    // 直す欄が画面に無い状態で「選べ」と言わない——行番号の 2 つと同じ扱い）。

    /// <summary>借方貸方が `debit` / `credit` のどちらでもない。</summary>
    public const string DebitCreditNotStorable = "「貸借」の値が正しくありません。明細を入力し直してください。";

    /// <summary>状態が「下書き」「計上済み」のどちらでもない。</summary>
    public const string StatusNotStorable = "伝票の状態が正しくありません。伝票を入力し直してください。";

    /// <summary>種別が、扱える 6 種のどれでもない。</summary>
    public const string EntryTypeNotStorable = "伝票の種別が正しくありません。伝票を入力し直してください。";

    /// <summary>用途区分が、扱える 3 種のどれでもない（使い始めるのはフェーズ 3）。</summary>
    public const string TaxTreatmentNotStorable = "明細の用途区分が正しくありません。明細を入力し直してください。";

    // --- 入っていない項目（すべて JournalViolationCodes.RequiredValueMissing）---
    //
    // **画面に出ている見出しの語をそのまま使う**（docs/21 §2-2「内部表現を出さない」）。
    // 見出しの現在形はデザイン JSON が持つので、ここに一覧を写さない——変えたときに嘘になる。

    public const string LineNoMissing = "「行」が入っていません。明細の行を入れ直してください。";

    public const string DebitCreditMissing = "「貸借」で借方か貸方かを選んでください。";

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
