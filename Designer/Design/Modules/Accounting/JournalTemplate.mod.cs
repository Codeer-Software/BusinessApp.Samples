// JournalTemplate.mod.cs — 定型仕訳（仕訳辞書）D-4
// 「この定型から伝票を起票」: テンプレート明細から draft の振替伝票を生成し、
// 伝票画面へ遷移する（金額を確認・修正して確定する運用）。
// 税行はここでは作らない（伝票確定時の RegenerateTaxLines が自動生成する）。
//
// ShowInList（旧 IsActive）は「一覧に出すかどうか」だけを表すフラグ（ADR-0054）。
// 使えなくするフラグではないので、起票はガードしない——非表示の定型でも、直接開けば起票できる。
// 定型仕訳を指す参照フィールドはどこにも無く、入口は一覧だけなので「候補から外す」効き方が存在しない。
// もう使わせたくない定型は削除する、が正しい運用。

void Detail_OnAfterInit()
{
    // 新規は「一覧に表示する」を既定 ON（既定 OFF だと作った直後に一覧から消える）
    if (this.IsNewData) { ShowInList.Value = true; }
    UpdateHiddenNote();
}

// 非表示の定型を開いたときだけ、その理由と「起票はできる」ことを案内する
void UpdateHiddenNote()
{
    HiddenNote.IsVisible = !this.IsNewData && ShowInList.Value != true;
}

void ShowInList_OnDataChanged()
{
    UpdateHiddenNote();
}

void CreateEntry_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("定型を保存してから起票してください");
        return;
    }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // テンプレート明細は画面のメモリ行を信用せず DB から取り直す（#60 の罠対策）
    var ls = new ModuleSearcher<JournalTemplateLine>();
    ls.AddEquals(e => e.TemplateId.Value, this.Id.Value);
    ls.OrderBy(e => e.LineNo.Value);
    var tmplLines = ls.Execute();
    if (tmplLines.Count == 0)
    {
        Toaster.Error("明細がありません。明細を登録してから起票してください");
        return;
    }

    // 会計年度の解決（本日基準・境界日の罠を避けるため月初日で解決）
    var today = DateOnly.FromDateTime(DateTime.Today);
    var firstDay = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null)
    {
        Toaster.Error("本日に対応する会計年度がありません");
        return;
    }
    var typedFy = (FiscalYear)fy;

    var je = new JournalEntry();
    je.EntryDate.Value = today;
    je.EntryType.Value = "transfer";
    je.Status.Value = "draft";
    je.Description.Value = Name.Value;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "template";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(tmplLines.Count);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        var t = (JournalTemplateLine)tmplLines[idx];
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Dc.Value = t.Dc.Value;
        l.Account.Value = t.Account.Value;
        l.Amount.Value = t.Amount.Value;
        l.TaxCategory.Value = t.TaxCategoryRef.Value;
        l.TaxInputMode.Value = t.TaxInputMode.Value;
        l.Description.Value = t.Description.Value;
        // 定型の明細が部門を持っていれば下書きに引き継ぐ（ADR-0056）。
        // 家賃→全社共通のように毎月同じ部門になる仕訳を、起票のたびに選び直さずに済む
        l.Department.Value = t.DepartmentRef.Value;
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ret = je.Submit();
    if (ret != true)
    {
        Toaster.Error("伝票の作成に失敗しました");
        return;
    }

    // 作成した draft 伝票へ遷移（id は DB から引く: この定型由来の最新 draft）
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "template");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    js.OrderByDescending(e => e.Id.Value);
    js.Limit(1);
    var created = js.ExecuteFirstOrDefault();
    if (created == null)
    {
        Toaster.Error("作成した伝票が見つかりません");
        return;
    }
    var typedCreated = (JournalEntry)created;
    Toaster.Success($"下書き伝票を作成しました。金額を確認して確定してください");
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("JournalEntry", $"{typedCreated.Id.Value}"));
}

// 一覧の既定は「表示中の定型のみ」。非表示のものも見たいときは条件をクリアして検索する
// （Notification の未読既定と同じ方式）。
void Search_OnInit()
{
    ShowInList.SearchValue = true;
}
