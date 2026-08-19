// Invoice.mod.cs — 請求書
// 責務: 請求書番号採番 (INV-{yy}-{seq}) / 明細の行番号・金額・合計の再計算と
//        請求額(税抜)・消費税額(SALES_10 税率)の自動反映 / 支払期限の既定=翌月末
// 入金消込・売掛管理は B4-4 で実装する

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    // 採番は機械が決める。人に触らせない（BUG-0426）
    InvoiceNo.IsViewOnly = true;

    if (this.IsNewData)
    {
        // 手作成の請求書は「下書き」始まり（U4-5・2026-07-16 ユーザー決定。見積と対称にする）。
        // 検収・定期請求・SES からの自動生成は各スクリプトが直接 issued を書くため影響なし。
        Status.Value = "draft";
        if (InvoiceSource.Value == null || InvoiceSource.Value == "") { InvoiceSource.Value = "manual"; }
        IssueDate.Value = DateOnly.FromDateTime(DateTime.Today);
        DueDate.Value = EndOfNextMonth();
        InvoiceNo.Value = NextInvoiceNo();
        // 部門の初期値: 作成者の所属部（主所属が課でも伝票部門は部・ADR-0044。自動生成は各スクリプトが設定）
        if (DepartmentRef.Value == null) { DepartmentRef.Value = CurrentUser.所属部.Value; }
    }
    // **init では表示用の合計だけを作り直す**（BUG-0132）。
    // 旧実装は `RecalcTotal()` を無条件に呼んでおり、これは DB 列である `Amount` / `TaxAmount` を
    // メモリ上の `Lines.Rows` の合計で上書きする。明細が遅延ロードで空のまま開いた回に
    // 状態遷移ボタン（発行取消・下書きに戻す等はいずれも `Submit()` する）を押すと、
    // **請求額 0 円がそのまま保存される**。同じファイルの `PrintInvoice` が
    // 「明細は DB から取り直す（メモリ行の遅延ロード対策）」として `ModuleSearcher` に
    // 切り替えているとおり、この遅延ロードは実測済みの現象である。
    SeedAmountTrace();
    RefreshTotalDisplay();
    UpdateButtons();
}

// 表示用の合計だけを更新する（DB 列 `Amount` / `TaxAmount` には触らない）。
// 保存済みの請求書では**明細を DB から取り直す**——メモリの `Lines.Rows` は遅延ロードで空のことがある
void RefreshTotalDisplay()
{
    var total = 0;
    foreach (var l in GetLinesForTotal())
    {
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    TotalAmount.Value = total;
    UpdateOverAcceptanceWarning();
}

// 合計に使う明細行。編集中（メモリに行がある）ならそれを、
// 保存済みで空なら DB から取り直す（遅延ロード対策・`PrintInvoice` と同じ方針）
List<InvoiceLine> GetLinesForTotal()
{
    var result = new List<InvoiceLine>();
    foreach (var row in Lines.Rows) { result.Add((InvoiceLine)row); }
    if (result.Count > 0) return result;
    if (this.IsNewData || this.Id.Value == null) return result;

    var ls = new ModuleSearcher<InvoiceLine>();
    ls.AddEquals(e => e.InvoiceId.Value, this.Id.Value);
    ls.OrderBy(e => e.LineNo.Value);
    foreach (var row in ls.Execute()) { result.Add((InvoiceLine)row); }
    return result;
}

// 保存済みの行について「金額は数量×単価のまま（＝人が触っていない）」かどうかの痕跡を作る（BUG-0134）。
// 非 DB 項目なので開き直すたびに空になる。ここで埋めておかないと、
// 開き直したあとに単価を直しても金額が追随しなくなる
void SeedAmountTrace()
{
    foreach (var row in Lines.Rows)
    {
        var l = (InvoiceLine)row;
        if (l.Amount.Value == null) continue;
        if (l.Qty.Value == null || l.UnitPrice.Value == null) continue;
        int auto = l.Qty.Value * l.UnitPrice.Value;
        if (auto == l.Amount.Value) { l.AmountAutoValue.Value = $"{auto}"; }
    }
}

// 状態遷移はボタン経由に一本化（ADR-0026）。状態セレクトは表示専用。
// draft: 発行する・削除 ／ issued: 下書きに戻す・取消にする ／ void: 取消を戻す ／
// partial・paid: 遷移ボタンなし（入金側で自動遷移）
void UpdateButtons()
{
    // 部門は経理のみ変更可（2026-07-25 ユーザー要望）。一般・承認者は自部門（初期値）固定
    // （請求書自体は経理専用モジュールだが、権限方針を見積・受注と揃えて明示しておく）
    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
    if (!isAccounting) { DepartmentRef.IsViewOnly = true; }

    var st = Status.Value;
    // 定期請求・SES の自動生成分は「下書きに戻す」の代わりに「発行を取り消す」（生成仕訳ごと削除・ADR-0033）
    var generated = IsGeneratedInvoice();
    IssueButton.IsVisible = !this.IsNewData && (st == "draft");
    DeleteInvoiceButton.IsVisible = !this.IsNewData && (st == "draft");
    RevertToDraftButton.IsVisible = !this.IsNewData && (st == "issued") && !generated;
    CancelIssueButton.IsVisible = !this.IsNewData && (st == "issued") && generated;
    VoidButton.IsVisible = !this.IsNewData && (st == "issued");
    UnvoidButton.IsVisible = !this.IsNewData && (st == "void");

    // 編集できるのは下書きのみ（2026-07-25 ユーザー要望。見積と同じ確定文書ロック）。
    // 発行済・一部入金・入金済・取消は閲覧専用——修正は「下書きに戻す」で明示的に差し戻してから行う
    var editable = this.IsNewData || (st == "draft");
    this.IsViewOnly = !editable;
    SubmitButton.IsVisible = editable;
    if (!editable)
    {
        // CLB 1.3: モジュール全体を閲覧専用にするとボタンの OnClick も発火しなくなるため、
        // 閲覧専用中も使う操作ボタンだけ個別に閲覧専用を解除する
        RevertToDraftButton.IsViewOnly = false;
        CancelIssueButton.IsViewOnly = false;
        VoidButton.IsViewOnly = false;
        UnvoidButton.IsViewOnly = false;
        PrintExcelButton.IsViewOnly = false;
        PrintPdfButton.IsViewOnly = false;
    }

    // 検収に紐づく請求書の明細は「検収明細の写し」（ADR-0049）なので読み取り専用にする（ADR-0067）。
    // 警告を出すだけでは、検収額を超える請求を画面から作れてしまう（BUG-0058 が現に残骸として残っている）。
    // 保存時にブロックする案は採らない——締め済み期間の訂正で詰むため（ISSUE-0002 S6）。
    // **そもそも入力させない**のがいちばん静かで、直す入口が検収の 1 か所に定まる
    if (AcceptanceRef.Value != null)
    {
        Lines.IsViewOnly = true;
        LinesLabel.Text = "明細（検収の写しなので直せません。金額を変えるときは検収を訂正するか、増額なら変更契約として新しい受注・検収を起こしてください）";
    }
    else
    {
        LinesLabel.Text = "明細";
    }

    UpdateStateActionHint();  // 状態遷移ボタンの効き方の説明は、ボタンの出し分けと必ず対で更新する
}

// 請求書の取消（issued→void）: 二重発行・宛先ミス・貸倒れなどで請求を無効化し、
// 売掛残高の対象から外す。入金記録がある請求書は不可（先に入金の取消を）。
//
// **この請求書自身が起票した売上仕訳は赤伝（反対仕訳）で必ず打ち消す**（BUG-0129）。
// 売掛残高一覧は void を除外する（ReceivableBalance.Query.sql）ので、仕訳を残すと
// GL の売掛金にだけ残高が残り、補助簿と恒久的に食い違う。二重発行・宛先ミスは貸倒れではないので
// 「別途振替伝票で処理してください」と案内しても誰も起票せず、実際にそのまま残っていた。
//
// 元仕訳を削除せず赤伝を起票するのは、**「取消を戻す」で元に戻せる必要がある**ため。
// 定期請求は void を恒久的な「再生成しない」印にしているので（RecurringRun.BuildMonthlyPlanRow）、
// 元仕訳を消すと売上を復元する手段が無くなる。赤伝方式なら締め済み・締め前を同じ規則で扱え、
// 「締めた期の数字は動かさない」も自動的に満たせる（赤伝の日付は下の CreateReversalJournal 参照）。
//
// **検収由来の売上仕訳（source_type='acceptance'）は触らない。** 収益は検収の確定で認識しており、
// 請求書はその写しにすぎない。ここで消すと「確定済みなのに売上仕訳が無い検収」＝BUG-0128 の形を
// 作ってしまう。代わりに、売上が検収側に残ることを確認ダイアログと画面の警告で必ず知らせる
// （取消後は検収のロックが外れる〔BUG-0130〕ので、再請求するか検収の確定を取り消すかを選べる）。
void Void_OnClick()
{
    if (Status.Value != "issued") { Toaster.Error("発行済の請求書のみ取消にできます"); return; }
    if (HasConfirmedReceipts()) { Toaster.Error("消込済みの入金記録があるため取消にできません（先に入金の取消を行ってください）"); return; }

    // この請求書「自身」が起票した仕訳。定期請求（月額・年額・按分振替）と SES 精算だけが該当する
    // （journal_entries.source_id に請求書 id を入れる source_type 群）。検収由来はここに入らない
    var ownJs = new ModuleSearcher<JournalEntry>();
    ownJs.AddEquals(e => e.SourceId.Value, this.Id.Value);
    ownJs.AddIn(e => e.SourceType.Value, "ses", "recurring", "recurring_annual", "recurring_defer");
    var journals = ownJs.Execute();

    // 起票できるかを**先に全件**確かめる。途中まで起票して止まると、原因を直して押し直したときに
    // 済んだ分の赤伝がもう一度作られる（赤伝には「どの仕訳を打ち消したか」を辿る列が無いため）
    foreach (var row in journals)
    {
        var je = (JournalEntry)row;
        if (!CanReverseJournal(je)) return;
    }

    var msg = $"請求書「{InvoiceNo.Value}」を取消にしますか？（入金消込・売掛残高の対象から外れます）";
    if (journals.Count > 0)
    {
        msg = msg + $" この請求書が起票した仕訳 {journals.Count} 本は、赤伝（反対仕訳）を起票して打ち消します。";
    }
    var accNote = BuildAcceptanceSalesNote();
    if (accNote != null)
    {
        msg = msg + $" なお、この請求書の売上は {accNote} で計上済みです——取消にしても売上と売掛金は帳簿に残ります。請求しないなら検収の「確定を取り消す」で売上ごと取り消してください。";
    }
    var result = MessageBox.Show(msg, "取消にする", "キャンセル");
    if (result != "取消にする") return;

    using var loading = LoadingService.StartLoading(0);

    // 仕訳を先に片付けてから状態を動かす。逆順にすると、赤伝の起票に失敗した瞬間に
    // 「void なのに売上仕訳が残っている」＝いま直そうとしている状態そのものが出来上がる。
    // この順序なら失敗しても請求書は発行済のままなので、原因を直して押し直せる
    var reversedNos = new List<string>();
    foreach (var row in journals)
    {
        var je = (JournalEntry)row;
        var newNo = CreateReversalJournal(je, "invoice_void", "売上取消");
        if (newNo == 0)
        {
            Toaster.Error($"仕訳 No.{je.JournalNo.Value} の赤伝を起票できなかったため取消にできませんでした（請求書は発行済のままです）");
            return;
        }
        reversedNos.Add($"No.{newNo}");
    }

    this.IsViewOnly = false;  // 発行済ロック中でも状態遷移は許可（UpdateButtons が再ロックする）
    Status.Value = "void";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "issued";
        Toaster.Error("取消に失敗しました（起票した赤伝は残っています。仕訳一覧を確認してください）");
        UpdateButtons();
        return;
    }
    DeletePendingReceipts();  // 未確定の入金予定は取消と同時に片付ける（消込対象から外す）
    UpdateButtons();
    if (reversedNos.Count > 0)
    {
        Toaster.Success($"請求書 {InvoiceNo.Value} を取消にし、赤伝 {string.Join("・", reversedNos)} で売上を打ち消しました");
    }
    else
    {
        Toaster.Success($"請求書 {InvoiceNo.Value} を取消にしました");
    }
}

// 取消の取り消し（void→issued）: 誤って取消にした場合のリカバリ。
// 取消で起票した赤伝を打ち消して売上を帳簿に戻す（取消と対称にする）。
// 赤伝が開いている期間にあれば赤伝そのものを削除し（帳簿に赤黒のゴミを残さない）、
// 締め済みなら赤伝の反対仕訳を当期に起票する
void Unvoid_OnClick()
{
    if (Status.Value != "void") { Toaster.Error("取消状態の請求書のみ戻せます"); return; }
    using var loading = LoadingService.StartLoading(0);

    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    js.AddEquals(e => e.SourceType.Value, "invoice_void");
    var reversals = js.Execute();

    // 取消と同じく、締め済みの赤伝を打ち消す反対仕訳が全部起票できるかを先に確かめる
    foreach (var row in reversals)
    {
        var je = (JournalEntry)row;
        if (!IsClosedPeriodAt(je.EntryDate.Value)) continue;
        if (!CanReverseJournal(je)) return;
    }

    var restored = 0;
    foreach (var row in reversals)
    {
        var je = (JournalEntry)row;
        if (IsClosedPeriodAt(je.EntryDate.Value))
        {
            var newNo = CreateReversalJournal(je, "invoice_unvoid", "取消の取り消し");
            if (newNo == 0)
            {
                Toaster.Error($"赤伝 No.{je.JournalNo.Value} を打ち消せなかったため発行済に戻せませんでした");
                return;
            }
        }
        else
        {
            if (!DeleteJournalEntryWithLines(je))
            {
                Toaster.Error($"赤伝 No.{je.JournalNo.Value} の削除に失敗したため発行済に戻せませんでした");
                return;
            }
        }
        restored = restored + 1;
    }

    this.IsViewOnly = false;  // 取消ロック中でも状態遷移は許可（UpdateButtons が再ロックする）
    Status.Value = "issued";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "void";
        Toaster.Error("発行済への変更に失敗しました（赤伝の取り消しは済んでいます。仕訳一覧を確認してください）");
        UpdateButtons();
        return;
    }
    CreatePendingReceipt();  // 取消の取り消しで消込対象に復帰するため、入金予定も作り直す
    UpdateButtons();
    if (restored > 0)
    {
        Toaster.Success($"請求書 {InvoiceNo.Value} を発行済に戻し、赤伝 {restored} 本を取り消して売上を戻しました");
    }
    else
    {
        Toaster.Success($"請求書 {InvoiceNo.Value} を発行済に戻しました");
    }
}

// 指定日が属する月次期間（無ければ null）。
// 境界日知見: 月末日は辞書順比較で失敗するため月初日で解決する
FiscalPeriod FindPeriodAt(DateOnly? d)
{
    if (d == null) return null;
    var monthFirst = new DateOnly(d.Year, d.Month, 1);
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var found = ps.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalPeriod)found;
}

bool IsClosedPeriodAt(DateOnly? d)
{
    var p = FindPeriodAt(d);
    if (p == null) return false;
    return p.Status.Value == "closed";
}

// 反対仕訳を起票する日付: 元仕訳の期間が開いていれば元と同じ日／締まっていれば今日。
// 締め済み期間に新しい伝票を足すのも「締めた期の数字を動かす」ことなので、その場合だけ当期に打つ。
// 元の期間が開いているなら同じ月に赤伝を置く方が正しい——その月の損益が発行と取消で相殺され、
// 月次推移に「請求していない売上」が残らない
DateOnly? ResolveReversalDate(JournalEntry src)
{
    DateOnly? d = src.EntryDate.Value;
    if (d == null || IsClosedPeriodAt(d)) { return DateOnly.FromDateTime(DateTime.Today); }
    return d;
}

// 反対仕訳を起票できるか（起票先の会計年度・月次期間・元仕訳の明細）。
// 理由は Toaster に出すので、呼び出し側は false なら黙って戻ってよい
bool CanReverseJournal(JournalEntry src)
{
    var useDate = ResolveReversalDate(src);
    var typedFy = FindFiscalYearAt(useDate);
    if (typedFy == null)
    {
        Toaster.Error($"{useDate:yyyy/MM/dd} に対応する会計年度がありません（会計期間マスタを確認してください）");
        return false;
    }
    var period = FindPeriodAt(useDate);
    if (period == null)
    {
        Toaster.Error($"{useDate:yyyy/MM/dd} に対応する月次期間がありません（会計期間マスタを確認してください）");
        return false;
    }
    if (period.Status.Value == "closed")
    {
        Toaster.Error($"仕訳 No.{src.JournalNo.Value} を打ち消す反対仕訳の起票先（{useDate:yyyy/MM/dd}）も締め済みです。当月の月次締めを開いてから実行してください");
        return false;
    }
    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(l => l.JournalEntryId.Value, src.Id.Value);
    if (ls.Execute().Count == 0)
    {
        Toaster.Error($"仕訳 No.{src.JournalNo.Value} に明細がありません（仕訳一覧で内容を確認してください）");
        return false;
    }
    return true;
}

// 指定日が属する会計年度（無ければ null）。境界日知見は FindPeriodAt と同じ
FiscalYear FindFiscalYearAt(DateOnly? d)
{
    if (d == null) return null;
    var monthFirst = new DateOnly(d.Year, d.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var found = ys.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalYear)found;
}

// 元仕訳の借方貸方を入れ替えた反対仕訳を起票する。成功したら新しい伝票番号、失敗したら 0 を返す。
// 金額は元仕訳の確定値をそのまま写し、TaxInputMode は none にして再計算させない
// （税行も IsTaxLine / ParentLineNo ごと写すので、消費税集計表は貸借の向きで正しく減算される）
int CreateReversalJournal(JournalEntry src, string sourceType, string kind)
{
    if (!CanReverseJournal(src)) { return 0; }
    var useDate = ResolveReversalDate(src);
    var typedFy = FindFiscalYearAt(useDate);

    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(l => l.JournalEntryId.Value, src.Id.Value);
    ls.OrderBy(l => l.LineNo.Value);
    var srcLines = ls.Execute();

    // 伝票採番（正典: JournalEntry.NextJournalNo。BUG-0069 で一本化）
    var nextNo = new JournalEntry().NextJournalNo(typedFy.Id.Value);

    var je = new JournalEntry();
    je.EntryDate.Value = useDate;
    je.EntryType.Value = "auto";
    je.Description.Value = $"{kind}（{InvoiceNo.Value}）: 仕訳 No.{src.JournalNo.Value} の反対仕訳";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = sourceType;
    je.SourceId.Value = this.Id.Value;
    je.PartnerRef.Value = PartnerRef.Value;  // 電帳法の検索要件（取引先で探せること・BUG-0003）
    je.Lines.AddRows(srcLines.Count);   // 引数は 1 文で確定させる（ISSUE-0006）
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var dst = (JournalLine)row;
        var s = (JournalLine)srcLines[idx];
        idx = idx + 1;
        // 行番号は元のまま写す（税行の ParentLineNo が元の行番号を指しているため）
        dst.LineNo.Value = (s.LineNo.Value == null) ? idx : s.LineNo.Value;
        dst.Dc.Value = (s.Dc.Value == "D") ? "C" : "D";
        dst.Account.Value = s.Account.Value;
        dst.SubAccount.Value = s.SubAccount.Value;
        dst.Department.Value = s.Department.Value;
        dst.ProjectRef.Value = s.ProjectRef.Value;
        dst.TaxCategory.Value = s.TaxCategory.Value;
        dst.TaxInputMode.Value = "none";
        dst.IsTaxLine.Value = s.IsTaxLine.Value;
        dst.ParentLineNo.Value = s.ParentLineNo.Value;
        dst.Amount.Value = s.Amount.Value;
        dst.InputAmount.Value = s.Amount.Value;
        dst.Description.Value = s.Description.Value;
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。元仕訳から写しているので通常は素通り（ADR-0056）
    // 貸借一致の検証（BUG-0068）。**Submit の前**に見るので、止めれば伝票は生まれない
    var imbalance = je.ValidateBalanced();
    if (imbalance != "")
    {
        Toaster.Error($"仕訳の生成を中止しました（{imbalance}）");
        return 0;
    }
    var ret = je.Submit();
    if (ret != true) { return 0; }
    return nextNo;
}

// この請求書の売上を裏付けている「検収の売上仕訳」の案内文（無ければ null）。
// 直接請求（acceptance_id）と合算請求（acceptances.billed_invoice_id）の両方を見る。
// 売上仕訳は検収のものなので請求書側からは動かさない——だからこそ、どこに残るのかを画面で言う
string BuildAcceptanceSalesNote()
{
    if (this.IsNewData) return null;
    var acceptanceIds = new List<object>();
    if (AcceptanceRef.Value != null) { acceptanceIds.Add(AcceptanceRef.Value); }
    var accs = new ModuleSearcher<Acceptance>();
    accs.AddEquals(e => e.BilledInvoiceRef.Value, this.Id.Value);
    foreach (var row in accs.Execute())
    {
        var a = (Acceptance)row;
        if ($"{a.Id.Value}" == $"{AcceptanceRef.Value}") continue;
        acceptanceIds.Add(a.Id.Value);
    }
    if (acceptanceIds.Count == 0) return null;

    var parts = new List<string>();
    foreach (var acceptanceId in acceptanceIds)
    {
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "acceptance");
        js.AddEquals(e => e.SourceId.Value, acceptanceId);
        var found = js.ExecuteFirstOrDefault();
        if (found == null) continue;
        var je = (JournalEntry)found;
        parts.Add($"検収 {FindAcceptanceNo(acceptanceId)} の売上仕訳 No.{je.JournalNo.Value}");
    }
    if (parts.Count == 0) return null;
    return string.Join(" ／ ", parts);
}

string FindAcceptanceNo(object acceptanceId)
{
    var s = new ModuleSearcher<Acceptance>();
    s.AddEquals(e => e.Id.Value, acceptanceId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return "";
    return ((Acceptance)found).AcceptanceNo.Value ?? "";
}

// 「発行を取り消す」「下書きに戻す」「取消にする」は名前だけでは帳簿への効き方が読めない
// （2026-08-17 ユーザー指摘）。どのボタンが何を消すのかを画面に常時書いておく。
// 取消済みの請求書には、売上が検収側に残っていないかも赤字で出す（BUG-0129 の再発検知）
void UpdateStateActionHint()
{
    StateActionHint.IsVisible = false;
    StateActionHint.Text = "";
    VoidSalesWarning.IsVisible = false;
    VoidSalesWarning.Text = "";
    if (this.IsNewData) return;

    var st = Status.Value;
    if (st == "issued")
    {
        var lines = new List<string>();
        if (RevertToDraftButton.IsVisible == true)
        {
            lines.Add("「下書きに戻す」＝ 発行前に戻して内容を直す（帳簿は動きません。入金予定だけ取り消します）");
        }
        if (CancelIssueButton.IsVisible == true)
        {
            lines.Add("「発行を取り消す」＝ 生成そのものを無かったことにする（この請求書・起票した仕訳・入金予定をまとめて削除し、定期請求の実行／SES精算・請求でやり直せる状態に戻します）");
        }
        lines.Add("「取消にする」＝ 請求書を記録として残したまま無効にする（売掛残高一覧から外れ、この請求書が起票した売上仕訳は赤伝で打ち消します）。二重発行・宛先ミス・貸倒れはこちら");
        StateActionHint.Text = string.Join("　／　", lines);
        StateActionHint.IsVisible = true;
    }
    else if (st == "void")
    {
        StateActionHint.Text = "この請求書は取消済みです（売掛残高一覧・入金消込の対象外）。「取消を戻す」で発行済に戻すと、取消時の赤伝も取り消して売上を帳簿に戻します";
        StateActionHint.IsVisible = true;
    }

    if (st == "void")
    {
        var accNote = BuildAcceptanceSalesNote();
        if (accNote != null)
        {
            VoidSalesWarning.Text = $"⚠ 取消済みですが、売上は {accNote} として帳簿に残っています（売掛金も計上されたままです）。"
                + "この検収をあらためて請求するなら検収画面から請求書を作り直してください。請求しないなら検収の「確定を取り消す」で売上ごと取り消してください。"
                + "どちらもしないと、総勘定元帳の売掛金だけが売掛残高一覧より多い状態が続きます。";
            VoidSalesWarning.IsVisible = true;
        }
    }
}

void Issue_OnClick()
{
    if (Status.Value != "draft") { Toaster.Error("下書きの請求書のみ発行できます"); return; }
    var hasLine = false;
    foreach (var row in Lines.Rows) { hasLine = true; break; }
    if (!hasLine) { Toaster.Error("明細を入力してから発行してください"); return; }
    // 金額 0 の請求書を発行させない（BUG-0377）。明細はあるが金額が入っていない、という形で作れてしまい、
    // **0 円の入金予定**が経理の消込キューに残る（入金の確定は金額 0 を弾くので、消すことも確定することもできない）
    if ((Amount.Value ?? 0) + (TaxAmount.Value ?? 0) <= 0)
    {
        Toaster.Error("請求額が 0 円です。明細の数量・単価を入力してから発行してください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);
    Status.Value = "issued";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "draft";
        Toaster.Error("発行に失敗しました");
        return;
    }
    CreatePendingReceipt();
    Toaster.Success($"請求書 {InvoiceNo.Value} を発行しました（入金予定を自動作成。入金一覧の「未確定」から消込できます）");
    UpdateButtons();
}

// この請求書に入金記録があるか（下書き削除のガード。未確定の入金予定も孤児にしないため含める）
bool HasReceipts()
{
    // 同上。入金の有無は消込明細で見る（ADR-0071）
    var rls = new ModuleSearcher<ReceiptLine>();
    rls.AddEquals(l => l.InvoiceRef.Value, this.Id.Value);
    return rls.Execute().Count > 0;
}

// 消込済み（消込仕訳が存在する）入金があるか（取消・巻き戻しのガード。
// 発行時に自動作成される未確定の入金予定はブロックしない——それは DeletePendingReceipts で片付ける）
bool HasConfirmedReceipts()
{
    // **消込明細で見る**（ADR-0071）。合算入金のヘッダは 1 件目の請求書しか指していないので、
    // ヘッダで探すと「合算で消し込まれた請求書」を取りこぼし、取消・発行取消のガードが素通りする
    var rls = new ModuleSearcher<ReceiptLine>();
    rls.AddEquals(l => l.InvoiceRef.Value, this.Id.Value);
    foreach (var row in rls.Execute())
    {
        var rl = (ReceiptLine)row;
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, rl.ReceiptId.Value);
        if (js.Execute().Count > 0) { return true; }
    }
    return false;
}

// 入金予定（未確定入金）の自動作成（2026-07-25 ユーザー要望）。
// 発行と同時に入金一覧へ「未確定」の行ができ、それがそのまま経理の消込 ToDo になる。
// 入金日は支払期限を予定日として仮置き・金額は税込請求額——確定時に経理が実額へ修正する
void CreatePendingReceipt()
{
    // 入金の有無は**消込明細**で見る（ADR-0071）。合算されると入金ヘッダの請求書欄では引けない
    var rls = new ModuleSearcher<ReceiptLine>();
    rls.AddEquals(l => l.InvoiceRef.Value, this.Id.Value);
    if (rls.Execute().Count > 0) { return; }  // 既に入金記録がある請求書には作らない（二重作成ガード）
    var r = new Receipt();
    r.InvoiceRef.Value = this.Id.Value;
    r.ReceiptDate.Value = DueDate.Value;
    r.Method.Value = "bank";
    r.Amount.Value = (Amount.Value ?? 0) + (TaxAmount.Value ?? 0);
    r.Note.Value = "請求書の発行時に自動作成された入金予定です（入金日・金額を実額に修正して確定してください）";
    var ok = r.Submit();
    if (ok != true) { Toaster.Warn("入金予定の自動作成に失敗しました（入金画面から手動で登録してください）"); }
}

// 未確定（消込仕訳なし）の入金予定を削除する（下書きへの巻き戻し・取消時の後始末）
void DeletePendingReceipts()
{
    // この請求書を指す**消込明細**から辿る（ADR-0071）。
    // 合算された入金は他の請求書の分も抱えているので、**入金ごと消さずにこの行だけ外す**
    var rls = new ModuleSearcher<ReceiptLine>();
    rls.AddEquals(l => l.InvoiceRef.Value, this.Id.Value);
    foreach (var lrow in rls.Execute())
    {
        var rl = (ReceiptLine)lrow;
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, rl.ReceiptId.Value);
        if (js.Execute().Count > 0) { continue; }   // 消込済みは触らない

        var sib = new ModuleSearcher<ReceiptLine>();
        sib.AddEquals(l => l.ReceiptId.Value, rl.ReceiptId.Value);
        var sibCount = sib.Execute().Count;

        var rs = new ModuleSearcher<Receipt>();
        rs.AddEquals(e => e.Id.Value, rl.ReceiptId.Value);
        var found = rs.ExecuteFirstOrDefault();
        if (found == null) { continue; }
        var r = (Receipt)found;

        if (sibCount <= 1)
        {
            var ok = r.Delete();   // 明細もトリガで消える（ddl/780）
            if (ok != true) { Toaster.Warn("未確定の入金予定の削除に失敗しました（入金一覧から手動で削除してください）"); }
            continue;
        }
        var restAmount = (r.Amount.Value ?? 0) - (rl.Amount.Value ?? 0);
        if (rl.Delete() != true)
        {
            Toaster.Warn("合算入金から消込明細を外せませんでした（入金一覧から手動で直してください）");
            continue;
        }
        r.Amount.Value = restAmount;
        if (r.Submit() != true) { Toaster.Warn("合算入金の金額更新に失敗しました（入金一覧から手動で直してください）"); }
    }
}

// 定期請求・SES の実行で自動生成された請求書か（発行の取り消し＝生成物の一括削除の対象）
bool IsGeneratedInvoice()
{
    var src = InvoiceSource.Value;
    return src == "ses" || src == "recurring" || src == "recurring_annual";
}

// 発行の取り消し（定期請求・SES の自動生成分・ADR-0033）: 生成された仕訳・入金予定・請求書本体を
// 丸ごと削除して「未生成」に戻す。冪等ガードは請求書の存在で判定しているため、削除すれば
// 「定期請求の実行」「SES精算・請求」で正しく再生成できる。
// 締め済み期間の仕訳が絡む場合は削除せず、赤黒訂正＋「取消にする」（以後の按分停止）を案内する
void CancelIssue_OnClick()
{
    if (Status.Value != "issued") { Toaster.Error("発行済の請求書のみ発行を取り消せます"); return; }
    if (!IsGeneratedInvoice()) { Toaster.Error("この操作は定期請求・SES で生成された請求書専用です"); return; }
    if (HasConfirmedReceipts()) { Toaster.Error("消込済みの入金記録があるため取り消せません（先に入金の取消を行ってください）"); return; }

    // 関連仕訳の収集（月額/SES=売上仕訳、年額=前受計上＋全按分振替）と締め済みチェック
    // 取消（void）で起票した赤伝・その取り消しの反対仕訳も一緒に消す。
    // 残すと削除済み請求書を source に持つ孤児になる（金額としては赤黒で相殺されているが、
    // 出どころを辿れない伝票を帳簿に置かない）
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    js.AddIn(e => e.SourceType.Value, "ses", "recurring", "recurring_annual", "recurring_defer", "invoice_void", "invoice_unvoid");
    var journals = js.Execute();
    foreach (var row in journals)
    {
        var je = (JournalEntry)row;
        var d = je.EntryDate.Value;
        if (d == null) continue;
        var monthFirst = new DateOnly(d.Year, d.Month, 1);
        var ps = new ModuleSearcher<FiscalPeriod>();
        ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
        ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
        var period = ps.ExecuteFirstOrDefault();
        if (period != null && ((FiscalPeriod)period).Status.Value == "closed")
        {
            Toaster.Error($"仕訳 No.{je.JournalNo.Value}（{d:yyyy/MM/dd}）の期間が締め済みのため発行を取り消せません（締めた期の仕訳は削除できません）。「取消にする」を使ってください——締め済み分は当期に赤伝を起票して打ち消し、以後の按分振替も止まります");
            return;
        }
    }

    var result = MessageBox.Show($"請求書「{InvoiceNo.Value}」の発行を取り消します。生成された仕訳 {journals.Count} 本・未確定の入金予定・請求書本体を削除し、未生成の状態に戻します（「定期請求の実行」「SES精算・請求」で再生成できます）。よろしいですか？", "発行を取り消す", "キャンセル");
    if (result != "発行を取り消す") return;

    using var loading = LoadingService.StartLoading(0);
    foreach (var row in journals)
    {
        var je = (JournalEntry)row;
        if (!DeleteJournalEntryWithLines(je))
        {
            Toaster.Error($"仕訳 No.{je.JournalNo.Value} の削除に失敗しました（処理を中断します）");
            return;
        }
    }
    DeletePendingReceipts();
    this.IsViewOnly = false;  // 発行済ロック中でも削除は許可
    var ok = this.Delete();
    if (ok != true)
    {
        Toaster.Error("請求書の削除に失敗しました（生成仕訳は削除済みです。再生成してやり直してください）");
        return;
    }
    Toaster.Success($"請求書 {InvoiceNo.Value} の発行を取り消しました（必要なら定期請求の実行／SES精算・請求で再生成できます）");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("Invoice"));
}

// 仕訳を明細→親の順に物理削除する。子持ちモジュールの検索インスタンス Delete() は
// 親単独では静かに失敗する（実測）ため、行ごとに削除し全戻り値を検証する（Receipt と同型）
// 正典は JournalEntry.DeleteWithLines（BUG-0148）。**ここに書き写さない**——
// 書き写した結果、同じ部分失敗が 5 か所に増えていた。
// 失敗理由は呼び元でそのままトーストに出す（伝票番号と残った状態が入っている）
string lastDeleteError = "";

bool DeleteJournalEntryWithLines(JournalEntry je)
{
    lastDeleteError = je.DeleteWithLines();
    return lastDeleteError == "";
}

// 定期請求・SES など「生成と同時に売上仕訳が起票される」請求書か
// （journal_entries.source_id に請求書 id が入る source_type 群）
bool HasGenerationJournal()
{
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var rows = js.Execute();
    foreach (var row in rows)
    {
        var je = (JournalEntry)row;
        var st = je.SourceType.Value;
        if (st == "ses" || st == "recurring" || st == "recurring_annual" || st == "recurring_defer")
        {
            return true;
        }
    }
    return false;
}

// 発行の巻き戻し（issued→draft）。入金・売上仕訳が絡む場合は不可
void RevertToDraft_OnClick()
{
    if (Status.Value == "partial" || Status.Value == "paid")
    {
        Toaster.Error("入金記録があるため下書きに戻せません（先に入金の取消を行ってください）");
        return;
    }
    if (Status.Value != "issued") { Toaster.Error("発行済の請求書のみ下書きに戻せます"); return; }
    if (HasConfirmedReceipts())
    {
        Toaster.Error("消込済みの入金記録があるため下書きに戻せません（先に入金の取消を行ってください）");
        return;
    }
    if (HasGenerationJournal())
    {
        Toaster.Error("この請求書は発行時に売上仕訳が起票されています（定期請求/SES）。修正は赤黒訂正で行ってください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);
    this.IsViewOnly = false;  // 発行済ロック中でも状態遷移は許可（UpdateButtons が再ロックする）
    Status.Value = "draft";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "issued";
        Toaster.Error("下書きへの変更に失敗しました");
        UpdateButtons();
        return;
    }
    DeletePendingReceipts();  // 発行時に自動作成した入金予定は巻き戻しと同時に片付ける
    Toaster.Success("請求書を下書きに戻しました");
    UpdateButtons();
}

// 下書きの削除（ADR-0026: 削除は詳細画面の条件付きボタンのみ・一覧の削除ボタンは撤去）
void DeleteInvoice_OnClick()
{
    if (Status.Value != "draft") { Toaster.Error("下書きの請求書のみ削除できます"); return; }
    if (HasReceipts()) { Toaster.Error("入金記録があるため削除できません"); return; }
    if (HasGenerationJournal()) { Toaster.Error("売上仕訳が起票されているため削除できません"); return; }
    var result = MessageBox.Show($"請求書「{InvoiceNo.Value}」を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;

    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("請求書を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("Invoice"));
}

void Lines_OnDataChanged()
{
    if (inLinesHandler) return;
    inLinesHandler = true;
    var no = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (InvoiceLine)row;
        no = no + 1;
        l.LineNo.Value = no;
        if (l.Qty.Value == null) l.Qty.Value = 1;
        // 税区分は必須（ADR-0050）。新しい行には既定として課税売上 10% を入れる
        if (l.TaxCategoryRef.Value == null) l.TaxCategoryRef.Value = DefaultSalesTaxCategoryId();
        // 検収明細から写した行の金額は「検収金額」そのもの。数量×単価で再計算してはならない
        // （分割検収では 検収金額 < 数量×単価 になるため。ADR-0049 / 改善候補 A-1 の真因）。
        // 手で足した行だけ、入力の手間を省くために 数量×単価 を自動で入れる。
        // ただし**人が手で入れた金額は上書きしない**（BUG-0134）。
        // 合算請求書（複数検収を手動の請求書にまとめる正規フロー）の明細は
        // `AcceptanceLineRef` が NULL なので、旧実装では全行が上書き対象だった。
        // 分割検収の按分額（数量 1 × 単価 2,000,000 だが請求は 1,200,000）を入れても、
        // 別の行を触った瞬間に 2,000,000 へ戻ってしまう。
        // 自動で入れた値を痕跡に控え、**金額が痕跡と一致している間だけ**追随させる
        // （税区分の追随 BUG-0067 / BUG-0182 と同じ型）
        if (l.AcceptanceLineRef.Value == null && l.UnitPrice.Value != null)
        {
            int auto = l.Qty.Value * l.UnitPrice.Value;
            var trace = l.AmountAutoValue.Value ?? "";
            var isUntouched = (l.Amount.Value == null) || (trace != "" && trace == $"{l.Amount.Value}");
            if (isUntouched)
            {
                l.Amount.Value = auto;
                l.AmountAutoValue.Value = $"{auto}";
            }
        }
    }
    RecalcTotal();
    inLinesHandler = false;
}

// 明細合計 → 表示用合計・請求額(税抜)・消費税額 (SALES_10 税率で切り捨て) を更新
void RecalcTotal()
{
    var total = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (InvoiceLine)row;
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    TotalAmount.Value = total;
    Amount.Value = total;
    TaxAmount.Value = CalcTaxByLine();
    UpdateOverAcceptanceWarning();
}

// 請求明細が、元になった検収明細の金額を超えていないかを行単位で突き合わせ、
// 超過していれば画面に赤字で即時警告する（発行前に気づけるようにするのが狙い）。
// 検収に紐づく請求書だけが対象。合算請求書（手動作成）は acceptance_id が NULL なので自然に外れる。
// 検収を選んだら、その受注の取引先が請求先と一致するかを確かめる（BUG-0452）。
//
// 検収選択の候補は `Status = 'confirmed'` でしか絞っていない（`Invoice.mod.json`）。
// つまり**別会社の検収を選べる**。選ぶと売上・売掛は検収から立つので、
// **帳簿上の債権は A 社、請求書と入金は B 社**という状態になり、取引先別売掛が両側で合わなくなる。
// 実データにも 1 件あった（INV-26-010 は アルタイル商事 宛なのに、A-26-005 の受注先は グランメゾン印刷）。
//
// 逆方向（検収から合算先の請求書を選ぶ）は `Acceptance.BilledInvoiceRef_OnDataChanged` で
// 既に同じ検査をしている。**片方向にしか無かった**のがこの穴の正体
void AcceptanceRef_OnDataChanged()
{
    if (AcceptanceRef.Value == null) return;
    if (PartnerRef.Value == null) return;

    var acs = new ModuleSearcher<Acceptance>();
    acs.AddEquals(a => a.Id.Value, AcceptanceRef.Value);
    var ac = acs.ExecuteFirstOrDefault();
    if (ac == null) return;
    var soId = ((Acceptance)ac).SalesOrderRef.Value;
    if (soId == null) return;

    var sos = new ModuleSearcher<SalesOrder>();
    sos.AddEquals(o => o.Id.Value, soId);
    var so = sos.ExecuteFirstOrDefault();
    if (so == null) return;
    var orderPartner = ((SalesOrder)so).PartnerRef.Value;
    if (orderPartner == null) return;
    if ($"{orderPartner}" == $"{PartnerRef.Value}") return;

    var badNo = ((Acceptance)ac).AcceptanceNo.Value;
    AcceptanceRef.Value = null;
    Toaster.Error($"検収 {badNo} は取引先が違うため紐づけられません"
        + "（請求先と検収の受注先が同じでなければなりません）");
}

// ブロックはしない——検収後の増額は「変更契約として新しい受注を起こす」のが本アプリの運用規約
// （ISSUE-0002）なので、止めるのではなく「それは変更契約の話ですよ」と気づかせる役割。
void UpdateOverAcceptanceWarning()
{
    OverAcceptanceWarning.IsVisible = false;
    OverAcceptanceWarning.Text = "";
    if (AcceptanceRef.Value == null) return;

    var overLines = new List<string>();
    var totalOver = 0;

    foreach (var row in Lines.Rows)
    {
        var l = (InvoiceLine)row;
        if (l.Amount.Value == null) continue;
        if (l.AcceptanceLineRef.Value == null) continue;   // 手で足した行は突合対象外

        var s = new ModuleSearcher<AcceptanceLine>();
        s.AddEquals(al => al.Id.Value, l.AcceptanceLineRef.Value);
        var found = s.ExecuteFirstOrDefault();
        if (found == null) continue;
        var accepted = ((AcceptanceLine)found).Amount.Value ?? 0;
        if (l.Amount.Value <= accepted) continue;

        var over = l.Amount.Value - accepted;
        totalOver = totalOver + over;
        var desc = l.Description.Value ?? "";
        overLines.Add($"{l.LineNo.Value}行目「{desc}」 請求 {l.Amount.Value:#,0} 円 > 検収 {accepted:#,0} 円（+{over:#,0}）");
    }

    if (overLines.Count == 0) return;

    var head = $"⚠ 検収額を超えている明細が {overLines.Count} 行あります（超過 合計 {totalOver:#,0} 円）。";
    var body = string.Join(" ／ ", overLines);
    OverAcceptanceWarning.Text = $"{head} {body} — 増額する場合は変更契約として新しい受注・検収を起こしてください。";
    OverAcceptanceWarning.IsVisible = true;
}

// 明細の税区分から消費税額を計算する（ADR-0050）。
// インボイス制度は「一の適格請求書につき、税率ごとに 1 回の端数処理」と定めるため、
// 税率ごとに本体を合計してから 1 回だけ切り捨てる（行ごとに切ってから足すのは不可）。
// 課税売上（taxable_sales）の行のみが対象。非課税・免税・不課税・税区分なしは税額 0。
int CalcTaxByLine()
{
    // 税率(%) ごとの本体合計
    var rates = new List<decimal>();
    var bases = new List<int>();

    // 税区分が未設定の行は従来どおり課税売上 10% とみなす（黙って非課税にすると過少計上になるため）
    decimal defaultPct = GetSalesTaxRatePercent();

    foreach (var row in Lines.Rows)
    {
        var l = (InvoiceLine)row;
        if (l.Amount.Value == null) continue;
        decimal pct = l.TaxCategoryRef.Value == null
            ? defaultPct
            : ResolveTaxableSalesRatePercent(l.TaxCategoryRef.Value);
        if (pct <= 0) continue;   // 非課税・免税・不課税・対象外、または税率なし

        var idx = rates.IndexOf(pct);
        if (idx < 0) { rates.Add(pct); bases.Add(l.Amount.Value); }
        else { bases[idx] = bases[idx] + l.Amount.Value; }
    }

    var tax = 0;
    for (var i = 0; i < rates.Count; i++)
    {
        tax = tax + (int)(bases[i] * rates[i] / 100);   // 税率ごとに 1 回だけ切り捨て
    }
    return tax;
}

// 税区分 ID → 課税売上ならその税率(%)、それ以外・未設定なら 0
decimal ResolveTaxableSalesRatePercent(long? taxCategoryId)
{
    if (taxCategoryId == null) return 0;
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Id.Value, taxCategoryId);
    var found = cs.ExecuteFirstOrDefault();
    if (found == null) return 0;
    var tcat = (TaxCategory)found;
    if (tcat.TaxationType.Value != "taxable_sales") return 0;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    return ((TaxRate)foundRate).RatePercent.Value ?? 0;
}

// 売上伝票の既定税区分を「マスタから」解決する（ADR-0050）。
// 「ふつうは 10%」はこの時点の制度でしかないので、コードに税区分を直書きしない。
// 税制マスタ > 税区分 の「既定として使う」で切り替えられる（tax_categories.default_for='sales'）。
long? DefaultSalesTaxCategoryId()
{
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.DefaultFor.Value, "sales");
    cs.AddEquals(c => c.IsActive.Value, true);
    var found = cs.ExecuteFirstOrDefault();
    if (found == null) return null;
    return ((TaxCategory)found).Id.Value;
}

// 既定税区分の税率(%)。税区分が未設定の明細（スクリプト経由で書かれた行など）の
// 保険的な既定として使う。IsRequired は画面入力しか縛れないため。
decimal GetSalesTaxRatePercent()
{
    return ResolveTaxableSalesRatePercent(DefaultSalesTaxCategoryId());
}

// 請求書番号採番【正典】: INV-{西暦下2桁}-{連番3桁}（BUG-0133）。
// 請求書を作る全経路（この画面の新規・検収からの作成・定期請求・SES 一括）がここを呼ぶ——
// 他モジュールからは `new Invoice().NextInvoiceNo()` で呼べる（Project.md 2026-07-26）。
// 番号自体に西暦下 2 桁が入るので、一意の範囲は**全期間**（ddl/610 の部分ユニークインデックス）。
// 同時発行で衝突したら INSERT が UNIQUE で弾かれる。**欠番は許す**（2026-08-17 ユーザー決定）。
string NextInvoiceNo()
{
    var prefix = $"INV-{DateTime.Today:yy}-";
    var s = new ModuleSearcher<Invoice>();
    s.OrderByDescending(e => e.InvoiceNo.Value);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    var seq = 1;
    if (last != null)
    {
        var lastNo = ((Invoice)last).InvoiceNo.Value;
        if (lastNo != null && lastNo.StartsWith(prefix))
        {
            // **落ちない採番**（BUG-0426）。数字として読めない番号は「無かったこと」にして続ける
            var tail = 0;
            if (int.TryParse(lastNo.Substring(prefix.Length), out tail)) { seq = tail + 1; }
        }
    }
    return $"{prefix}{seq:000}";
}

// 翌月末日 (支払サイト: 月末締め翌月末払いの既定)
DateOnly EndOfNextMonth()
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
    var firstOfMonthAfterNext = firstOfThisMonth.AddMonths(2);
    return firstOfMonthAfterNext.AddDays(-1);
}

// ============ 請求書の帳票出力（Excel / PDF） ============
// Resources/invoice_template.xlsx（プレースホルダ差し込み方式）に1件分を転記してダウンロードする。
// 自社名・住所・振込先はテンプレート直書き（自社で編集する運用）。明細はテンプレの10行まで。

void PrintExcel_OnClick()
{
    PrintInvoice(false);
}

void PrintPdf_OnClick()
{
    PrintInvoice(true);
}

void PrintInvoice(bool asPdf)
{
    if (this.IsNewData)
    {
        Toaster.Error("請求書を保存してから出力してください");
        return;
    }
    using var loading = LoadingService.StartLoading(0);

    var stream = Resources.GetMemoryStream("invoice_template.xlsx");
    if (stream == null)
    {
        Toaster.Error("請求書テンプレート（Resources/invoice_template.xlsx）が見つかりません");
        return;
    }

    // 明細は DB から取り直す（メモリ行の遅延ロード対策）
    var ls = new ModuleSearcher<InvoiceLine>();
    ls.AddEquals(e => e.InvoiceId.Value, this.Id.Value);
    ls.OrderBy(e => e.LineNo.Value);
    var lines = ls.Execute();

    // 取引先名
    var partnerName = "";
    if (PartnerRef.Value != null)
    {
        var pc = new ModuleSearcher<Partner>();
        pc.AddEquals(p => p.Id.Value, PartnerRef.Value);
        var pt = pc.ExecuteFirstOrDefault();
        if (pt != null) { partnerName = ((Partner)pt).Name.Value ?? ""; }
    }

    var subtotal = Amount.Value ?? 0;
    var tax = TaxAmount.Value ?? 0;
    var total = subtotal + tax;
    var issueStr = "";
    if (IssueDate.Value != null) { issueStr = $"{IssueDate.Value:yyyy年M月d日}"; }
    var dueStr = "";
    if (DueDate.Value != null) { dueStr = $"{DueDate.Value:yyyy年M月d日}"; }

    // ファイル名: 請求書_{発行日}_{請求書番号}_{相手方}_{件名}（2026-07-25 ユーザー要望。見積書と同形式）
    var issueForFile = "";
    if (IssueDate.Value != null) { issueForFile = $"{IssueDate.Value:yyyyMMdd}"; }
    var fileName = SanitizeFileName($"請求書_{issueForFile}_{InvoiceNo.Value}_{partnerName}_{Title.Value}") + ".xlsx";

    using (var excel = new Excel(stream, fileName))
    {
        SetByMarker(excel, "{{PARTNER}}", $"{partnerName}　御中");
        SetByMarker(excel, "{{INVOICE_NO}}", InvoiceNo.Value ?? "");
        SetByMarker(excel, "{{ISSUE_DATE}}", issueStr);
        SetByMarker(excel, "{{DUE_DATE}}", dueStr);
        SetByMarker(excel, "{{TITLE}}", Title.Value ?? "");
        SetByMarker(excel, "{{TOTAL}}", $"￥{total:#,0} -");
        SetByMarker(excel, "{{SUBTOTAL}}", $"{subtotal:#,0}");
        SetByMarker(excel, "{{TAX}}", $"{tax:#,0}");
        SetByMarker(excel, "{{TOTAL2}}", $"{total:#,0}");
        SetByMarker(excel, "{{NOTE}}", Note.Value ?? "");

        var baseCell = excel.FindCellByText("{{LINES}}");
        if (baseCell != null)
        {
            excel.SetCellValue(baseCell, "");
            var i = 0;
            foreach (var m in lines)
            {
                if (i >= 10) break;  // テンプレートの明細枠は10行
                var l = (InvoiceLine)m;
                var rowCell = baseCell.GetNext(i, 0);
                excel.SetCellValue(rowCell, l.LineNo.Value ?? 0);
                excel.SetCellValue(rowCell.GetNext(0, 1), l.Description.Value ?? "");
                excel.SetCellValue(rowCell.GetNext(0, 2), l.Qty.Value ?? 0);
                excel.SetCellValue(rowCell.GetNext(0, 3), l.Unit.Value ?? "");
                excel.SetCellValue(rowCell.GetNext(0, 4), $"{l.UnitPrice.Value ?? 0:#,0}");
                excel.SetCellValue(rowCell.GetNext(0, 5), $"{l.Amount.Value ?? 0:#,0}");
                i = i + 1;
            }
            if (lines.Count > 10)
            {
                Toaster.Warn($"明細が10行を超えています（{lines.Count}行）。11行目以降は出力されません");
            }
        }

        var ok = false;
        if (asPdf) { ok = excel.DownloadPdf(); }
        else { ok = excel.Download(); }
        if (!ok)
        {
            Toaster.Error("請求書の出力に失敗しました");
            return;
        }
    }
    if (asPdf) { Toaster.Success($"請求書 {InvoiceNo.Value} を PDF でダウンロードしました"); }
    else { Toaster.Success($"請求書 {InvoiceNo.Value} を Excel でダウンロードしました"); }
}

void SetByMarker(Excel excel, string marker, object value)
{
    var cell = excel.FindCellByText(marker);
    if (cell != null) { excel.SetCellValue(cell, value); }
}

// Windows で使えないファイル名文字を「-」に置換（取引先名・件名由来の事故防止）
string SanitizeFileName(string name)
{
    var s = name ?? "";
    s = s.Replace("\\", "-").Replace("/", "-").Replace(":", "-").Replace("*", "-").Replace("?", "-").Replace("\"", "-").Replace("<", "-").Replace(">", "-").Replace("|", "-");
    return s;
}
