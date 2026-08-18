// FiscalYear.mod.cs — 会計年度
// 責務: 新規年度の既定値設定 / 期首日から12ヶ月の月次期間 (FiscalPeriod) の自動生成 /
//       翌期繰越の実行と**その陳腐化の表示** / 年度の締めと解除（ADR-0068）。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "open";
    }
    UpdateOpeningTotal();
    UpdateCarryOverStatus();
    UpdateYearButtons();
}

// 年度の状態は「締める」「締めを解除する」ボタンでしか変えない（ADR-0026 状態遷移ボタンの一元化）。
// 状態欄そのものはレイアウトで読み取り専用にしてある——手で closed にされると、
// 締めに紐づく処理（翌期繰越の確定・月次期間の一括締め）が丸ごと素通りするため
void UpdateYearButtons()
{
    var closed = Status.Value == "closed";
    CloseYearButton.IsVisible = !this.IsNewData && !closed;
    ReopenYearButton.IsVisible = !this.IsNewData && closed;

    // 締めた年度は中身を触らせない。月次期間をここから個別に開けてしまうと
    // 「年度は締め済みなのにその月には記帳できる」状態が作れてしまい、締めの意味が消える。
    // 直すときは必ず「締めを解除する」を通す（ADR-0068 の 4「再オープンを要求する」）
    Periods.IsViewOnly = closed;
    OpeningBalances.IsViewOnly = closed;
    // 月次期間を読み取り専用にしても、生成ボタンが生きていると締めた年度に 12 行足せてしまう
    GeneratePeriodsButton.IsVisible = !closed;
    // 「翌期繰越を実行」は締めた年度でも押せるままにする。締めた年度は動かないので打ち直しは no-op だが、
    // **翌期の期首残高を誰かが手で壊したときの復旧手段**がこれしかない（洗い替えの冪等性が事故復旧に効く）
}

void OpeningBalances_OnDataChanged()
{
    UpdateOpeningTotal();
}

// 期首残高の合計（符号付き借方正）。0 なら貸借一致
void UpdateOpeningTotal()
{
    var total = 0;
    foreach (var row in OpeningBalances.Rows)
    {
        var b = (OpeningBalance)row;
        if (b.Balance.Value == null) continue;
        total += b.Balance.Value;
    }
    OpeningTotal.Value = total;
    if (total == 0)
    {
        OpeningTotal.Color = "";
    }
    else
    {
        OpeningTotal.Color = "#dc3545";
    }
}

// 翌期繰越の状態を表示する（ADR-0068・BUG-0060）。
//
// 繰越は「実行した瞬間のスナップショット」なので、実行後に当年度の伝票を足す／直すと
// 翌期の期首残高は黙って誤りになる。翌期の BS は年度内で閉じているため貸借は一致したままで、
// 試算表・BS・PL・C/F・月次推移・元帳のどれにも異常として現れない。
// だから「陳腐化しているかどうか」を画面に出すのが唯一の気づく手立てになる。
//
// 判定そのものはビュー v_carryover_staleness に置き、ここでは読むだけにする
// （同じ計算を画面と検査で二重に書かない・ADR-0060 の教訓）。
void UpdateCarryOverStatus()
{
    CarryOverStatusLabel.Text = "";
    CarryOverStatusLabel.Color = "";
    if (this.IsNewData || this.Id.Value == null) return;

    var s = new ModuleSearcher<CarryOverStatus>();
    var rows = s.Execute();
    foreach (var r in rows)
    {
        var st = (CarryOverStatus)r;
        if ($"{st.FiscalYearId.Value}" != $"{this.Id.Value}") continue;
        var nextName = st.NextYearName.Value ?? "翌期";
        var state = st.State.Value;
        if (state == "not_carried")
        {
            CarryOverStatusLabel.Text = $"繰越の状態: {nextName} へ未繰越（期首残高がまだありません）";
            CarryOverStatusLabel.Color = "#6c757d";
        }
        else if (state == "stale")
        {
            var accounts = st.DiffAccounts.Value ?? 0;
            var amount = st.DiffAmount.Value ?? 0;
            CarryOverStatusLabel.Text = $"⚠ 繰越の状態: {nextName} の期首残高が古くなっています"
                + $"（{accounts} 科目・合計 {amount:#,0} 円のずれ）。"
                + "この年度の伝票が繰越の後に動いたためです。「翌期繰越を実行」を押し直してください";
            CarryOverStatusLabel.Color = "#dc3545";
        }
        else
        {
            CarryOverStatusLabel.Text = $"繰越の状態: {nextName} へ繰越済み（当期末と一致）";
            CarryOverStatusLabel.Color = "#198754";
        }
        return;
    }
    // 行が無い＝翌期が無い、または繰越の元（期首残高・確定仕訳）がまだ無い年度。判定対象外
}

// 翌期繰越: BS 科目の期末残高（期首+当期仕訳）＋繰越利益剰余金への当期純利益加算を
// 翌期の opening_balances に SQL 一発で洗い替え生成する（decisions/0006）
void CarryOver_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("年度を保存してから実行してください");
        return;
    }
    RunCarryOver(true);
}

// 翌期繰越の実処理。confirm=false のときは確認ダイアログを出さない
// （年度の締めから呼ぶときは、締めの確認ダイアログで一度了解を取っているため）。
// 戻り値: 繰り越したら true / 対象が無くて何もしなかったら false（呼び出し側が続行を判断する）
bool RunCarryOver(bool confirm)
{
    var s = new ModuleSearcher<FiscalYear>();
    s.AddGreaterThan(e => e.StartDate.Value, EndDate.Value);
    s.OrderBy(e => e.StartDate.Value);
    s.Limit(1);
    var next = s.ExecuteFirstOrDefault();
    if (next == null)
    {
        // 締めから呼ばれたときは黙って抜ける（最終年度を締めるのは正常な操作で、繰り越す先が無いだけ）
        if (confirm) { Toaster.Error("翌期の会計年度がありません。先に翌期を作成してください"); }
        return false;
    }
    var typedNext = (FiscalYear)next;

    // 繰越元の実体チェック: 期首残高も確定仕訳も無い年度からの繰越は、
    // 翌期の期首残高（システム移行時の開始残高など）を空データで洗い替えて破壊するため中止する。
    // （総合テストで実測した事故パターン。移行初年度での誤操作防止）
    var obs = new ModuleSearcher<OpeningBalance>();
    obs.AddEquals(o => o.FiscalYearId.Value, this.Id.Value);
    obs.Limit(1);
    var hasOpening = obs.ExecuteFirstOrDefault() != null;
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.FiscalYearRef.Value, this.Id.Value);
    js.AddEquals(e => e.Status.Value, "posted");
    js.Limit(1);
    var hasJournal = js.ExecuteFirstOrDefault() != null;
    if (!hasOpening && !hasJournal)
    {
        if (confirm) { Toaster.Error("この年度には期首残高も確定仕訳もありません。繰越を実行すると翌期の期首残高が失われるため中止しました"); }
        return false;
    }

    if (confirm)
    {
        var answer = MessageBox.Show($"{typedNext.Name.Value} の期首残高を作成します（既存の期首残高は洗い替えされます）。よろしいですか？", "実行", "キャンセル");
        if (answer != "実行") return false;
    }

    // ExecuteSqlField はスクリプトから直接実行できない（全メンバー ScriptHide）ため、
    // Update タイミングの CarryOverSql を「NextYearId をセットして Submit」で発火させる。
    // SQL 側は NextYearId が NULL のとき no-op ガード付き（通常の保存では何もしない）。
    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);
    NextYearId.Value = typedNext.Id.Value;
    var ret = this.Submit();
    NextYearId.Value = null;
    // null（送信データなし）は「Update が起きていない＝ CarryOverSql が発火していない」を意味する。
    // false と同じく失敗として扱う（成功と報せてしまうと、繰越されていない期首で決算を進めてしまう）
    if (ret != true)
    {
        Toaster.Error("繰越に失敗しました");
        return false;
    }

    // 2 回目は「NextYearId を NULL に戻す」後始末。**ここを検査しないと詰む**——
    // 失敗して next_year_id が DB に残ると、CarryOverSql は Timing: Update なので
    // **以後この年度を保存するたびに繰越 SQL が発火し、翌期の期首残高を無言で洗い替える**。
    // 年度名を直した・状態を締め済みにした、といった無関係な保存でも起きるうえ、
    // next_year_id は画面に出ないので気づく手立てが無い
    var retClear = this.Submit();
    if (retClear != true)
    {
        Toaster.Error($"{typedNext.Name.Value} への繰越は完了しましたが、繰越フラグの解除に失敗しました。"
            + "このままこの年度を保存すると、そのたびに翌期の期首残高が繰越値で上書きされます。"
            + "画面を開き直して保存し直し、解除されたことを確認してください");
        return false;
    }
    UpdateCarryOverStatus();
    Toaster.Success($"{typedNext.Name.Value} への繰越が完了しました");
    return true;
}

// 年度を締める（ADR-0068 の 3）。
//
// 締めは繰越の前提条件ではなく「確定契機」である。決算が固まるまで暫定繰越で回し、
// 締めた瞬間に繰越を打ち直して確定させる——こうすると「再繰越の押し忘れ」を締めの一点に集約できる。
// あわせて 12 か月の月次期間をすべて締める。**記帳を止めているのは月次期間なので**
// （年度の状態だけを closed にしても伝票は入ってしまう）、年度を締めるとは月次を全部締めることに等しい。
// 判定と関門を月次期間に一本化することで、10 か所以上に散らばる締めガードをそのまま活かせる。
void CloseYear_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("年度を保存してから実行してください");
        return;
    }
    if (Status.Value == "closed")
    {
        Toaster.Error("この年度は既に締め済みです");
        return;
    }

    // 下書きのまま残っている伝票は締めても確定されない（＝この年度の帳簿に載らない）ので、件数を先に伝える
    var ds = new ModuleSearcher<JournalEntry>();
    ds.AddEquals(e => e.FiscalYearRef.Value, this.Id.Value);
    ds.AddEquals(e => e.Status.Value, "draft");
    var draftCount = ds.Execute().Count;
    var draftText = draftCount > 0
        ? $"（下書きの伝票が {draftCount} 件残っています。締めると起票できなくなるので、先に確定するか削除してください）"
        : "";

    var openPeriods = 0;
    foreach (var row in Periods.Rows)
    {
        var pr = (FiscalPeriod)row;
        if (pr.Status.Value != "closed") { openPeriods = openPeriods + 1; }
    }

    var answer = MessageBox.Show(
        $"{Name.Value} を締めます。翌期繰越を打ち直して確定し、進行中の月次期間 {openPeriods} か月をすべて締め済みにします。{draftText}",
        "締める", "キャンセル");
    if (answer != "締める") return;

    // 先に繰越を確定させる（この中で Submit が 2 回走る）。翌期が無ければ何もしないで続行する
    RunCarryOver(false);

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);
    foreach (var row in Periods.Rows)
    {
        var pr = (FiscalPeriod)row;
        // 既に締め済みの行は触らない。FiscalPeriod.Status_OnDataChanged が行ごとに下書きを数えて
        // 警告トーストを出すので、無変更の行まで書き換えると同じ警告が何度も画面を覆う
        if (pr.Status.Value == "closed") continue;
        pr.Status.Value = "closed";
    }
    Status.Value = "closed";
    var ret = this.Submit();
    if (ret != true)
    {
        Toaster.Error("締めに失敗しました。画面を開き直してもう一度実行してください");
        return;
    }
    UpdateYearButtons();
    UpdateCarryOverStatus();
    Toaster.Success($"{Name.Value} を締めました（月次 {openPeriods} か月を締め済みにしました）");
}

// 締めを解除する。年度の状態だけを戻し、月次期間は開けない——
// 「決算のどこを直すのか」を月単位で選ばせるためで、全部開けてしまうと
// 締めた月に無関係な伝票が入り込む余地を作ってしまう（ADR-0068 の 4）
void ReopenYear_OnClick()
{
    if (Status.Value != "closed")
    {
        Toaster.Error("この年度は締め済みではありません");
        return;
    }
    var answer = MessageBox.Show(
        $"{Name.Value} の締めを解除します。月次期間は締め済みのままなので、記帳するには直す月の期間を「進行中」に戻してください。",
        "解除する", "キャンセル");
    if (answer != "解除する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    Status.Value = "open";
    var ret = this.Submit();
    if (ret != true)
    {
        Toaster.Error("解除に失敗しました。画面を開き直してもう一度実行してください");
        return;
    }
    UpdateYearButtons();
    Toaster.Success($"{Name.Value} の締めを解除しました。直す月の月次期間を「進行中」に戻してください");
}

void GeneratePeriods_OnClick()
{
    if (StartDate.Value == null)
    {
        StartDate.SetError("期首日を入力してください");
        return;
    }
    if (Periods.Rows.Count > 0)
    {
        MessageBox.Show("月次期間は既に存在します。生成し直す場合は既存の行を削除してから実行してください。");
        return;
    }

    using var suspend = this.SuspendNotifyStateChanged();

    var start = StartDate.Value;

    // 期末日が未入力なら 期首 + 12ヶ月 - 1日 を自動設定
    if (EndDate.Value == null)
    {
        EndDate.Value = start.AddMonths(12).AddDays(-1);
    }

    Periods.AddRows(12);
    int i = 0;
    foreach (var row in Periods.Rows)
    {
        var p = (FiscalPeriod)row;
        var s = start.AddMonths(i);
        p.PeriodNo.Value = i + 1;
        p.StartDate.Value = s;
        p.EndDate.Value = s.AddMonths(1).AddDays(-1);
        p.Status.Value = "open";
        i = i + 1;
    }
}
