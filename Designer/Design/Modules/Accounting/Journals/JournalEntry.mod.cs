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
    // **初期値を先に入れる。** あとの ApplyPostedLock は状態を読んで表示を決めるので、
    // 入れる前に呼ぶと「これから初期化する値を読む」ことになる（2026-08-26 の自己レビュー）。
    if (IsNewData)
    {
        // 新規作成時の初期値。取引日と計上日は今日、状態は下書き、種別は通常。
        // 入力年月日（EnteredAt）はサーバが決めるのでここでは触らない。
        var today = DateOnly.FromDateTime(DateTime.Now);
        TransactionDate.Value = today;
        PostingDate.Value = today;
        Status.Value = EntryStatuses.Draft;
        EntryType.Value = EntryTypes.Normal;
        SelectFiscalYear(today);
    }

    UpdateTotals();
    ApplyPostedLock();
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

    // **押せるのに拒まれる欄を残さない**（docs/21 §1）。次の 3 つは、入力欄の形をしているのに
    // 利用者が決められるものではない。触れると、直しようのない差し戻しに当たる。
    //
    //   状態     ボタンを押した結果である。「計上済み」を選んで「下書き保存」を押すと
    //            計上まで進み、ボタンのラベルが嘘になる
    //   会計年度 計上日から決まる（下の SelectFiscalYear）。選んでも上書きされ、
    //            食い違えばサーバが差し戻す
    //   種別     **6 値のうち、利用者が選べるものが 1 つも無い**（qa/02 R26-25）。
    //            取消・訂正はサーバが作るもので、期首残高・決算振替・繰越は未実装なので、
    //            選ぶと必ず差し戻される。保存したあとは変えられもしない（関門とトリガが拒む）。
    //            **候補を絞る代わりに表示専用にしてある**——Candidates で絞ると
    //            デザイン enum を捨てることになり、既にある取消・訂正の伝票の表示が壊れる。
    //            種別が増えた日に、ここを戻す
    //
    // **これは見た目の話で、守りではない**（qa/01 F-24）。守るのはサーバ側の関門である。
    Status.IsViewOnly = true;
    FiscalYear.IsViewOnly = true;
    EntryType.IsViewOnly = true;

    // **計上済みの画面に必須の印の説明を出さない。** 計上済みは書き込み条件から外れていて
    // 画面全体が読むだけになる（qa/01 F-14）ので、「必須項目です」は
    // これから入力する人にしか意味が無い（docs/21 §1「書けない状態の画面は、書けないと見て分かる」）。
    // **印そのものは消せない**——ClassName はレイアウトに焼かれていて、状態で切り替えられない。
    RequiredLegendLabel.IsVisible = !posted;

    // 入力のときの注意書き。計上済みの伝票はもう直せないので出さない。
    // **文言の正典は docs/13 §1-4**（住所の写しを仕訳に作らないと決めた、その代わりの運用）。
    // ここと 13 に同じ日本語があるので、直すときは両方を見る。
    InvoiceNoticeLabel.IsVisible = !posted;

    // 訂正・取消は「計上済みの伝票に対する操作」なので、下書きでは出さない。
    // 下書きは自由に直せるし、いらなければ削除すればよい。
    CorrectButton.IsVisible = false;
    ReverseButton.IsVisible = false;

    // **複製の可否はサーバに聞く**（下の ApplyAmendmentAvailability）。
    // 原仕訳の状態には依らないが**種別には依る**（期首残高・決算振替・繰越は複製できない。
    // ADR-0048 の決定 6）ので、**押せるのに必ず断られるボタンを出さない**（docs/21 §1。
    // 2026-09-09 の自己レビュー）。**新規（まだ保存していない）伝票には出さない**——写す元がまだ無い。
    DuplicateButton.IsVisible = false;

    // **削除は下書きにだけ出す。** 計上済みは不変（ADR-0004）で、取消か訂正で表す。
    // 新規（まだ保存していない）伝票にも出さない——消すものがまだ無い。
    DeleteButton.IsVisible = !posted && !IsNewData;

    // 取り消された・訂正されたことの断り。計上済みを開いたときにサーバが答える。
    AmendmentNoticeLabel.IsVisible = false;

    ShowSnapshotPartner(posted);

    // **押せる状態に戻す。** 計上済みの伝票は書き込み条件（Status = draft）から外れており、
    // そのままだとボタンが残ったまま無反応になる（qa/01 D-01・F-14）。
    CorrectButton.IsViewOnly = false;
    ReverseButton.IsViewOnly = false;
    DuplicateButton.IsViewOnly = false;

    // **下書きでも聞く。** 複製は下書きからも押せるので、
    // 計上済みのときだけ聞くと下書きの画面に複製が出ない。
    if (!IsNewData) ApplyAmendmentAvailability();
}

// 計上済みの伝票は、取引先も計上時の姿で見せる（ADR-0037）。
//
// **欄は 1 つのまま、表示テキストだけを写しに差し替える。**
// 桝を 2 つ並べて出し分ける形は**実機で崩れた**（2026-09-03。隠した側の桝が幅を持ったまま残り、
// 取引先の値がラベルから大きく離れて右へ飛ぶ。qa/01 D-08 の症状で、その処方の CSS は
// このプロジェクトに入っていない）。**欄が 1 つなら、そもそも空の桝ができない。**
//
// **写しは `DataOnlyFields` で読む**（qa/01 F-34）。レイアウトに出していない欄は
// CLB が取ってこないので、書かないと**全件で空**になる。
//
// **写しが無ければ触らない。** 空になるのは「この列より前に計上された伝票」と
// 「取引先の無い伝票」で、前者で空欄にすると**記録が無いのか出せないのかが読めない**（docs/21 §1「書けないと見て分かる」の裏返し）。
// **振替伝票の一覧の `COALESCE(写し, 現在名)` と同じ見方**である。
//
// **計上済みの画面は読むだけ**なので（書き込み条件から外れている）、`LinkField` は
// 候補ダイアログもリンクも出さず、ただの文字列として出る。
//
// **入力の事実（入力年月日・作成者）と計上の事実（計上した日時・計上者）は出しっぱなしにする**
// （qa/02 R6-01・R5-08 の決着）。状態で消すと、幅 120px のラベルの列が空の桝として残る。
// 下書きでは計上の 2 つが空欄になるが、**「まだ計上していない」がそのまま読める**ので、消すより素直である。
void ShowSnapshotPartner(bool posted)
{
    if (!posted) return;
    if (string.IsNullOrEmpty(PartnerNameSnapshot.Value)) return;

    Partner.DisplayText = PartnerNameSnapshot.Value;
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
    DuplicateButton.IsVisible = $"{result.JsonObject.canDuplicate}".ToLower() == "true";

    var noticed = ShowAmendmentNotice(
        $"{result.JsonObject.reversalEntryNo}", $"{result.JsonObject.correctionEntryNo}");

    // 両方できないなら、その理由を出す。押せないボタンを探させない。
    //
    // **ただし、上の断りが同じことを言っているときは繰り返さない**（同じ画面に同じ文言を 2 度出さない——本モジュールの判断）。
    // 取り消し済みの伝票では理由も「既に取り消されています」なので、
    // 画面の 2 か所に同じ事実が並ぶ。**理由が要るのは、断りに出せない理由のとき**——
    // 会計期間が無い・種別が取消の対象外、といった場合である。
    if (!CorrectButton.IsVisible && !ReverseButton.IsVisible && !noticed)
    {
        // **操作を名乗る。** 関門の文は「見出しが結果を言う」前提で書いてあるので
        // （docs/21 §2-6）、見出しの無いここに置くと**何の対象か**が読めない。
        // **複製が押せる画面では「取消・訂正はできない」と読めることが要る**（2026-09-09 の自己レビュー）。
        var reason = $"{result.JsonObject.message}";
        if (!string.IsNullOrEmpty(reason))
        {
            TotalsLabel.Text = $"{TotalsLabel.Text}　（取消・訂正はできません: {reason}）";
        }
    }
}

// 「この伝票は取り消されています」を画面のいちばん上に出す（ADR-0027 §3）。
//
// **一覧と同じ事実を、詳細でも見せる。** 一覧は取消・訂正の有無を SQL で逆引きしているが、
// 詳細まで来て何も書いていないと、「取り消された伝票を見ている」ことに気づかないまま読める。
//
// **無いことは空文字で返る**（サーバが数値ではなく文字列で返す。JournalAmendmentEndpoint）。
// 判定を 1 つの見方（空かどうか）に揃えてある。
// **引数は文字列で受ける。** CLB のスクリプトはツリーウォーク型インタプリタなので、
// 枠組みの型（WebApiResult）を引数に取る形は避け、読み出しは呼び出し側に寄せる。
// **戻り値は「断りを出したか」。** 呼び出し側が、同じ事実を理由としてもう一度書かないために使う。
bool ShowAmendmentNotice(string reversalNo, string correctionNo)
{
    if (!string.IsNullOrEmpty(correctionNo))
    {
        // 訂正されている伝票は、必ず取り消されてもいる。**両方の行き先を出す。**
        AmendmentNoticeLabel.Text =
            $"この伝票は訂正されています（取消伝票 {reversalNo} ／ 訂正の伝票 {correctionNo}）。";
    }
    else if (!string.IsNullOrEmpty(reversalNo))
    {
        AmendmentNoticeLabel.Text = $"この伝票は取り消されています（取消伝票 {reversalNo}）。";
    }

    AmendmentNoticeLabel.IsVisible = !string.IsNullOrEmpty(AmendmentNoticeLabel.Text);
    return AmendmentNoticeLabel.IsVisible;
}

// 下書きを削除する。
//
// **一覧から詳細へ移した操作である**（ADR-0027 §4。一覧をクエリモジュールにしたので
// 標準の削除アイコンが無くなった。2026-08-28 開発者が承認）。
//
// **会計の判断はここに無い。** 計上済みか・締め済みの期間かはサーバ側の関門
// （JournalSubmitGate）が見て、差し戻しの文言まで返す（ADR-0008）。
// ここでするのは、確認を取ることと、消えたら一覧へ戻ることだけである。
void DeleteButton_OnClick()
{
    if (MessageBox.ShowWithTitle(
            "削除の確認",
            "この下書きを削除します。元に戻せません。よろしいですか？", "はい", "いいえ") != "はい")
    {
        return;
    }

    // **戻り値を必ず見る。** 子（明細）を持つ親の削除は、子側の失敗で false を返して
    // **静かに終わる**（qa/01 C-03）。関門が止めた場合の理由は CLB がトーストに出す。
    if (this.Delete() != true) return;

    NavigationService.NavigateTo(NavigationService.GetModuleUrl("JournalEntryBoard"));
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

    // **CLB 本来の入力検証を、自分で走らせる**（qa/01 F-15）。
    // 標準の SubmitButtonFieldDesign（「下書き保存」）はクリック時に全フィールドを検証するが、
    // スクリプトからの this.Submit() は走らせない。呼ばないと、必須の空欄がそのまま保存へ進み、
    // DB の NOT NULL に当たって生の SQLite のメッセージがトーストに出る（qa/03 L-16）。
    // ValidateInput() は ListField の子行（明細）まで見る。
    //
    // **状態を進める前に呼ぶ。** 進めてから戻す作りにすると、戻し忘れが
    // 「計上済みに見える下書き」になる。
    //
    // ここで止まるのは画面の話であって、守りではない。同じ違反はサーバ側の関門
    // （JournalSubmitRequirements）も止める——画面は経路の 1 本でしかない（ADR-0008）。
    if (!this.ValidateInput()) return;

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

// 複製する。サーバが同じ内容の下書きを作って返す（ADR-0048）。
//
// **何を写して何を写さないかはサーバが決める**——ここは頼んで開くだけである（ADR-0008）。
//
// **確認は「保存していない変更があるとき」だけ出す。** 複製そのものは帳簿を 1 行も動かさず、
// できるのは下書き 1 本なので、間違えても削除すればよい（ADR-0048 の決定 7）。
// **ただし写るのは保存済みの内容**なので、直しかけたまま押すと、
// その変更は複製にも入らず、離脱で消える（2026-09-09 の自己レビュー）。
void DuplicateButton_OnClick()
{
    var message = IsModified
        ? "保存していない変更があります。複製に写るのは保存済みの内容で、変更は失われます。よろしいですか？"
        : "";

    Amend("duplicate", "複製", message,
        "複製しました。いま開いているのは新しい下書きです。内容を確かめて計上してください。");
}

// 確認 → サーバに依頼 → 返ってきた伝票を開く。
//
// 取消は「計上済みの反対仕訳」、訂正は「これから直す下書き」を開く。
// どちらを開くかはサーバが決めて openEntryId で返すので、ここでは分岐しない。
// **入口を 1 本にしてある。** サーバ側が「入口を分けると片方に関門を書き忘れる」と言って
// 1 本にしたのと同じ理由で、画面も 1 本にする——文言を直す日に片方だけ直るのを避ける。
// **message が空なら確認を出さない**（複製。上の理由）。
// **done が空なら成功のトーストを出さない**（取消・訂正は開いた先の画面がそれを語る）。
void Amend(string operation, string noun, string message, string done = "")
{
    if (message != "" && MessageBox.ShowWithTitle($"{noun}の確認", message, "はい", "いいえ") != "はい") return;

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

    // **開いた先が何であるかを言う。** 複製は見た目が原仕訳と同じなので、
    // 言わないと「保存されなかった」と読まれる。
    if (done != "") Toaster.Success(done);

    NavigationService.NavigateTo(
        NavigationService.GetModuleDataUrl("JournalEntry", $"{result.JsonObject.openEntryId}"));
}
