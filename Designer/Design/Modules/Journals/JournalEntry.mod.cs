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

    // 訂正・取消は「計上済みの伝票に対する操作」なので、下書きでは出さない。
    // 下書きは自由に直せるし、いらなければ削除すればよい。
    CorrectButton.IsVisible = false;
    ReverseButton.IsVisible = false;

    // **押せる状態に戻す。** 計上済みの伝票は書き込み条件（Status = draft）から外れており、
    // そのままだとボタンが残ったまま無反応になる（qa/01 D-01・F-14）。
    CorrectButton.IsViewOnly = false;
    ReverseButton.IsViewOnly = false;

    if (posted) ApplyAmendmentAvailability();
}

// できない操作のボタンは出さない。
//
// **可否はサーバに聞く。** 種別・取消済みかどうか・会計期間が開いているかを画面で判定すると、
// 同じ規則がサーバと画面の 2 か所に分かれ、片方だけ古くなる（ADR-0008）。
// 出さない理由も一緒に返るので、ここでは表示するだけでよい。
void ApplyAmendmentAvailability()
{
    var body = new JsonObject();
    body.OriginalEntryId = $"{Id.Value}";

    var result = WebApiService.Post("/api/journals/availability", body);
    if (result.StatusCode != 200) return;   // 分からないときは出さない（安全側）

    CorrectButton.IsVisible = $"{result.JsonObject.canCorrect}".ToLower() == "true";
    ReverseButton.IsVisible = $"{result.JsonObject.canReverse}".ToLower() == "true";

    // 両方できないなら、その理由を出す。押せないボタンを探させない。
    if (!CorrectButton.IsVisible && !ReverseButton.IsVisible)
    {
        var reason = $"{result.JsonObject.message}";
        if (!string.IsNullOrEmpty(reason)) TotalsLabel.Text = $"{TotalsLabel.Text}　（{reason}）";
    }
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

    // **先に空にする。** 該当する年度が無いときに古い年度が残ると、
    // 「計上日は翌年度なのに会計年度は前年度」という食い違った伝票ができ、
    // サーバ側の検証で弾かれるまで気づけない。**空欄なら、その場で分かる。**
    FiscalYear.Value = "";
    FiscalYear.DisplayText = "";

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

// 訂正する。サーバが取消を計上し、原仕訳を写した訂正の下書きを作って返す（ADR-0015・ADR-0016）。
//
// **会計の判断はここに 1 行も無い。** 取り消せるか・二重訂正でないか・年度はどれかは、
// すべてサーバ側の AccountingCore が決める。ここがするのは、頼むことと開くことだけ。
void CorrectButton_OnClick()
{
    Amend("correct", "訂正",
        "この伝票を訂正します。取消を計上し、内容を写した訂正の下書きを開きます。よろしいですか？");
}

// 取り消す。サーバが反対仕訳を作って計上まで進める。
void ReverseButton_OnClick()
{
    Amend("reverse", "取消",
        "この伝票を取り消します。取消は帳簿に残り、あとから消せません。よろしいですか？");
}

// 確認 → サーバに依頼 → 返ってきた伝票を開く。
//
// 取消は「計上済みの反対仕訳」、訂正は「これから直す下書き」を開く。
// どちらを開くかはサーバが決めて openEntryId で返すので、ここでは分岐しない。
void Amend(string operation, string noun, string message)
{
    if (MessageBox.ShowWithTitle($"{noun}の確認", message, "はい", "いいえ") != "はい") return;

    var body = new JsonObject();
    // Id は文字列で持つ。数値に直してから渡すと、桁と型の解釈が 2 か所に分かれる。
    body.OriginalEntryId = $"{Id.Value}";

    var result = WebApiService.Post($"/api/journals/{operation}", body);

    // **業務の差し戻しも 200 で返ってくる**（qa/01 K-01）。200 以外はサーバ側の想定外で、
    // そのとき本文は読めないので定型の文言にする。
    if (result.StatusCode != 200)
    {
        Toaster.Error($"{noun}できませんでした。しばらくしてからもう一度お試しください（サーバ応答 {result.StatusCode}）。");
        return;
    }

    // 成否は本文の status で分かる。**キーが無いと JsonObject 自身が返る**ので
    // （型名が画面に出る）、"ok" と一致するかで判定し、それ以外は差し戻しとして扱う。
    if ($"{result.JsonObject.status}" != "ok")
    {
        Toaster.Error($"{result.JsonObject.message}");
        return;
    }

    NavigationService.NavigateTo(
        NavigationService.GetModuleDataUrl("JournalEntry", $"{result.JsonObject.openEntryId}"));
}
