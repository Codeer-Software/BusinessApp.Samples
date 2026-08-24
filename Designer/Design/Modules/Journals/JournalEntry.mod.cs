// 仕訳伝票の画面。
//
// **会計の判断はここに書かない**（ADR-0008）。スクリプトに書いてよいのは
// 画面上の値の出し入れだけで、貸借一致・期間・部門・税区分の検証はすべて
// サーバ側の関門が AccountingCore を呼んで行う。
//
// ここでの貸借合計や初期値は、利用者が入力中に気づく・打鍵を減らすためのもので、
// 正しさの保証ではない。省いても計上の可否は変わらない。

void Detail_OnAfterInitialization()
{
    UpdateTotals();
    ApplyPostedLock();

    if (!IsNewData) return;

    // 新規作成時の初期値。取引日と計上日は今日、状態は下書き、種別は通常。
    // 入力年月日（EnteredAt）はサーバが決めるのでここでは触らない。
    var today = DateOnly.FromDateTime(DateTime.Now);
    TransactionDate.Value = today;
    PostingDate.Value = today;
    Status.Value = EntryStatuses.Draft;
    EntryType.Value = EntryTypes.Normal;
    SelectFiscalYear(today);
}

// 計上済みの伝票では操作ボタンを消す。
//
// **これは見た目の話であって、守りではない。** 不変性を守るのは DB のトリガと
// サーバ側の関門（ADR-0004）で、ここで消さなくても計上済みは書き換わらない。
// ボタンを残すと「押しても何も起きない」ことになり、静かな失敗に見える。
void ApplyPostedLock()
{
    var posted = Status.Value == EntryStatuses.Posted;
    PostButton.IsVisible = !posted;
    SubmitButton.IsVisible = !posted;
}

// 計上日を変えたら会計年度を付け直す。
//
// 期末をまたいで計上日を直したとき（3/31 → 4/1）に旧年度が残ると、サーバ側の検証で
// 弾かれる。安全側ではあるが、利用者に「会計年度も直せ」と言うことになる。
// **会計年度は計上日から決まるものなので、選ばせない。**
void PostingDate_OnDataChanged()
{
    if (PostingDate.Value == null) return;
    SelectFiscalYear(PostingDate.Value);
}

// 明細の追加・変更・削除で発火する（ListField の OnDataChanged）。
void Lines_OnDataChanged()
{
    FillRowDefaults();
    UpdateTotals();
}

// 追加された行の空欄を埋める。**既に入っている値は触らない**
// （導出を無条件に走らせると、外から入れた値を同じイベントで潰す）。
void FillRowDefaults()
{
    decimal maxLineNo = 0;
    foreach (var row in Lines.Rows)
    {
        var line = (JournalLine)row;
        if (line.LineNo.Value != null && line.LineNo.Value > maxLineNo)
        {
            maxLineNo = line.LineNo.Value;
        }
    }

    foreach (var row in Lines.Rows)
    {
        var line = (JournalLine)row;

        if (line.LineNo.Value == null)
        {
            maxLineNo += 1;
            line.LineNo.Value = maxLineNo;
        }

        // 借方から書き始めるのが振替伝票の作法。貸方にしたい行は利用者が変える。
        if (string.IsNullOrEmpty(line.DebitCredit.Value))
        {
            line.DebitCredit.Value = DebitCredits.Debit;
        }
    }
}

// 借方計と貸方計を出す。合っていないことに入力中に気づけるようにする。
void UpdateTotals()
{
    decimal debit = 0;
    decimal credit = 0;

    foreach (var row in Lines.Rows)
    {
        var line = (JournalLine)row;
        if (line.Amount.Value == null) continue;

        // 借貸が未設定の行はどちらにも数えない。既定で貸方に寄せると、
        // 入力途中の行のせいで「貸借が合った」ように見えてしまう。
        if (line.DebitCredit.Value == DebitCredits.Debit)
        {
            debit += line.Amount.Value;
        }
        else if (line.DebitCredit.Value == DebitCredits.Credit)
        {
            credit += line.Amount.Value;
        }
    }

    var mark = debit == credit ? "" : "  ← 一致していません";
    TotalsLabel.Text = $"借方計 {debit:#,0} / 貸方計 {credit:#,0}{mark}";
}

// 計上日が属する会計年度を入れる。毎回選ばせないための補助でしかなく、
// 会計年度と計上日の整合はサーバ側の検証が判定する。
void SelectFiscalYear(DateOnly postingDate)
{
    var target = Iso(postingDate);

    // 会計年度は多くて十数件なので、全件取って画面側で選ぶ。
    // 日付の大小はフィールド値のままでは比較できないので ISO 文字列に寄せる（qa/01 B-09）。
    foreach (var year in new ModuleSearcher<FiscalYear>().Execute())
    {
        if (year.StartDate.Value == null || year.EndDate.Value == null) continue;

        if (string.CompareOrdinal(Iso(year.StartDate.Value), target) <= 0
            && string.CompareOrdinal(target, Iso(year.EndDate.Value)) <= 0)
        {
            FiscalYear.Value = year.Id.Value;
            FiscalYear.DisplayText = year.Label.Value;
            return;
        }
    }
}

string Iso(DateOnly date)
{
    return $"{date.Year:0000}-{date.Month:00}-{date.Day:00}";
}

// 計上する。状態を計上済みにして保存するだけで、検証・採番・計上日時はサーバが行う。
// 計上の経路を保存経路と同じ 1 本にしておくと、迂回路が生まれない（ADR-0004）。
void PostButton_OnClick()
{
    if (Status.Value == EntryStatuses.Posted)
    {
        Toaster.Warn("この伝票は計上済みです。");
        return;
    }

    Status.Value = EntryStatuses.Posted;

    var submitted = this.Submit();
    if (submitted != true)
    {
        // 失敗したら画面の状態を戻す。戻さないと「計上済みに見える下書き」が残る。
        Status.Value = EntryStatuses.Draft;
    }
}
