// FiscalYear.mod.cs — 会計年度
// 責務: 新規年度の既定値設定 / 期首日から12ヶ月の月次期間 (FiscalPeriod) の自動生成 /
//       翌期繰越の実行と**その陳腐化の表示** / 年度の締めと解除（ADR-0068）/
//       仕掛品（未成業務支出金）の期末振替と翌期首の振戻（ADR-0072）。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "open";
    }
    UpdateOpeningTotal();
    UpdateCarryOverStatus();
    UpdateWipStatus();
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

// 期首残高の合計を **DB から数え直す**（BUG-0091）。
// 画面の OpeningBalances は開いている年度のぶんしか持たないうえ、
// 「入力途中で保存しただけ」の状態と区別できない。確定の関門で使うのはこちら。
//
// **保存そのものは止めない。** 期首残高は科目を 1 行ずつ入れていくものなので、
// 入力途中は必ず貸借が合わない。合計が赤いのは「まだ途中」の合図であって誤りではない。
// 止めるべきは**確定の瞬間**——翌期へ繰り越すときと、年度を締めるときである
// （`docs/04 §6`「投入合計が貸借一致しないと**確定不可**」の「確定」はこれを指す）。
int OpeningBalanceTotalFromDb()
{
    var total = 0;
    var s = new ModuleSearcher<OpeningBalance>();
    s.AddEquals(o => o.FiscalYearId.Value, this.Id.Value);
    foreach (var row in s.Execute())
    {
        var b = (OpeningBalance)row;
        if (b.Balance.Value == null) continue;
        total = total + b.Balance.Value;
    }
    return total;
}

// 期首残高が貸借一致していないなら理由を出して false を返す（確定系の入口で呼ぶ）
bool CheckOpeningBalanced(string actionName)
{
    var total = OpeningBalanceTotalFromDb();
    if (total == 0) return true;
    var side = (total > 0) ? "借方" : "貸方";
    var abs = (total > 0) ? total : -total;
    Toaster.Error($"{Name.Value} の期首残高が貸借一致していません（{side}が {abs:#,0} 円多い）。"
        + $"このまま{actionName}と、以後の帳票がすべて同額ずれ続けます。期首残高を直してから実行してください");
    return false;
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

    // 貸借の合っていない期首を土台に繰り越すと、ずれたまま翌期へ伝播する（BUG-0091）
    if (!CheckOpeningBalanced("繰り越す")) return false;

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
    // 締めは決算の確定契機。貸借の合っていない期首のまま締めさせない（BUG-0091）
    if (!CheckOpeningBalanced("締める")) return;

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

    // 締めると 12 か月すべてが closed になり、以後は仕掛品の振替も取消もできなくなる（ADR-0070）。
    // **未実行・陳腐化のまま締めると誤った決算がその場で確定する**ので、締める前に必ず状態を見せる
    var wipText = "";
    var wipSt = FindWipStatus();
    if (wipSt != null)
    {
        var wipComputed = wipSt.ComputedAmount.Value ?? 0;
        var wipPosted = wipSt.PostedAmount.Value ?? 0;
        var wipEntries = wipSt.PostedEntries.Value ?? 0;
        if (wipEntries == 0 && wipComputed > 0)
        {
            wipText = $"【注意】仕掛品の期末振替がまだ実行されていません（対象 {wipSt.ProjectCount.Value ?? 0} 案件・{wipComputed:#,0} 円）。締めると振り替えられなくなります。";
        }
        else if (wipEntries > 0 && wipPosted != wipComputed)
        {
            wipText = $"【注意】仕掛品の期末振替が古くなっています（起票 {wipPosted:#,0} 円 / 現在の計算 {wipComputed:#,0} 円）。締めると打ち直せなくなります。";
        }
        // 人件費コストの未入力も締める前に伝える（BUG-0367。締めたあとでは直せない）
        var missNote = MissingSalaryNote(wipSt);
        if (missNote != "") { wipText = wipText + missNote; }
    }

    var answer = MessageBox.Show(
        $"{Name.Value} を締めます。翌期繰越を打ち直して確定し、進行中の月次期間 {openPeriods} か月をすべて締め済みにします。{draftText}{wipText}",
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

// ───────────────────────────────────────────────────────────────────────────
// 仕掛品（未成業務支出金）の期末振替（ADR-0072・BUG-0016）
//
// 受託開発では、期をまたぐ案件の原価が期末時点で発生済みなのに売上はまだ立っていない
// （検収が翌期）。この原価を仕掛品へ振り替えないと、当期の損益が過小・翌期が過大になる。
//
// 洗い替え方式にする。期末に「借方 仕掛品 / 貸方 仕掛品振替高」を起こし、翌期首に同額を戻す。
// 残高を積み上げる方式にしないのは、前期にいくら計上したかを持ち続ける必要があり、
// 翌期繰越（ADR-0068）で問題になった陳腐化と同じ罠を新しく作ってしまうから。
//
// 対象の判定と金額はビュー（Designer/ddl/720）に置く。画面・仕訳生成・不変条件検査が
// 同じものを読む（ADR-0060）。
// ───────────────────────────────────────────────────────────────────────────

// 期末振替の状態を表示し、ボタンの出し分けを決める
void UpdateWipStatus()
{
    WipStatusLabel.Text = "";
    WipStatusLabel.Color = "";
    WipTransferButton.IsVisible = !this.IsNewData;
    WipCancelButton.IsVisible = false;
    WipHeaderLabel.IsVisible = !this.IsNewData;
    if (this.IsNewData || this.Id.Value == null) return;

    var st = FindWipStatus();
    if (st == null) return;

    var computed = st.ComputedAmount.Value ?? 0;
    var posted = st.PostedAmount.Value ?? 0;
    var postedEntries = st.PostedEntries.Value ?? 0;
    var reversalEntries = st.ReversalEntries.Value ?? 0;
    var projects = st.ProjectCount.Value ?? 0;
    WipCancelButton.IsVisible = postedEntries > 0;

    if (postedEntries == 0)
    {
        // 振替を消したのに振戻だけ残っている（取消が途中で失敗した痕跡）。
        // 放っておくと翌期の原価が二重に費用化されるので、画面で気づけるようにする
        if (reversalEntries > 0)
        {
            WipStatusLabel.Text = "⚠ 仕掛品の振替: 期末の振替仕訳が無いのに、翌期首の振戻だけが残っています"
                + "（取消が途中で失敗した可能性があります）。「期末振替を取り消す」を押して残りを消してください";
            WipStatusLabel.Color = "#dc3545";
            WipCancelButton.IsVisible = true;
            return;
        }
        if (computed <= 0)
        {
            WipStatusLabel.Text = "仕掛品の振替: 対象なし（当期の原価が付いた未検収の受託案件がありません）";
            WipStatusLabel.Color = "#6c757d";
            return;
        }
        WipStatusLabel.Text = $"仕掛品の振替: 未実行（対象 {projects} 案件・合計 {computed:#,0} 円）。「仕掛品を期末振替」を押すと決算整理仕訳を起票し、翌期首に振り戻します{MissingSalaryNote(st)}";
        WipStatusLabel.Color = "#6c757d";
        return;
    }
    if (posted != computed)
    {
        WipStatusLabel.Text = $"⚠ 仕掛品の振替: 起票済み {posted:#,0} 円に対し、いま計算すると {computed:#,0} 円です（振替の後にこの年度の伝票か工数が動きました）。「仕掛品を期末振替」を押し直してください";
        WipStatusLabel.Color = "#dc3545";
        return;
    }
    var reversalText = (reversalEntries > 0) ? "・翌期首に振戻済み" : "・⚠ 翌期首の振戻がありません";
    WipStatusLabel.Text = $"仕掛品の振替: 済み（{projects} 案件・{posted:#,0} 円{reversalText}）{MissingSalaryNote(st)}";
    WipStatusLabel.Color = (reversalEntries > 0) ? "#198754" : "#dc3545";
}

// 人件費コストが未入力の「人 × 月」があるときの注意書き（BUG-0367）。
// 未入力の月の工数は配賦で 0 円として扱われるので、**仕掛品の金額が静かに過小になる**。
// 金額を勝手に補うことはできない（いくらか分からない）ので、気づけるようにするしかない
string MissingSalaryNote(WipStatus st)
{
    var n = st.MissingSalaryCount.Value ?? 0;
    if (n <= 0) return "";
    return $"　⚠ 人件費コストが未入力の月があります（{n} 人月）。その分の工数は配賦 0 円で計算されるため、仕掛品の金額が過小になります";
}

WipStatus FindWipStatus()
{
    var s = new ModuleSearcher<WipStatus>();
    var rows = s.Execute();
    foreach (var r in rows)
    {
        var st = (WipStatus)r;
        if ($"{st.FiscalYearId.Value}" == $"{this.Id.Value}") return st;
    }
    return null;
}

void WipTransfer_OnClick()
{
    if (this.IsNewData) { Toaster.Error("年度を保存してから実行してください"); return; }
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("仕掛品の期末振替は経理のみ実行できます");
        return;
    }

    var st = FindWipStatus();
    if (st == null) { Toaster.Error("仕掛品の状態を取得できませんでした"); return; }
    var computed = st.ComputedAmount.Value ?? 0;
    var projects = st.ProjectCount.Value ?? 0;
    var alreadyPosted = (st.PostedEntries.Value ?? 0) > 0;
    if (computed <= 0)
    {
        // 起票済みなのに対象が 0 になった＝振替の後に検収が確定した、といった順序で起きる。
        // ここで「対象がありません」と返すと、陳腐化の警告が「押し直してください」と言っているのに
        // 押しても何も起きない**行き止まり**になる。**取り消して整合を戻す**のが正しい出口
        if (alreadyPosted)
        {
            var ans0 = MessageBox.Show(
                "仕掛品に振り替える対象が無くなりました（振替の後に検収が確定した等）。"
                + "起票済みの期末振替と翌期首の振戻を取り消して、帳簿を整合させます。よろしいですか？",
                "取り消す", "やめる");
            if (ans0 != "取り消す") return;
            using var suspend0 = this.SuspendNotifyStateChanged();
            using var loading0 = LoadingService.StartLoading(0);
            if (!DeleteWipJournals(true)) return;
            UpdateWipStatus();
            Toaster.Success("仕掛品の期末振替を取り消しました（対象が無くなったため）");
            return;
        }
        Toaster.Info("仕掛品に振り替える対象がありません（当期の原価が付いた未検収の受託案件がありません）");
        return;
    }

    // 翌期が無いと振り戻せない。**期末だけ起こして振戻を忘れる**と翌期の原価が永久に消えるので、
    // 片方だけ起票することを許さない（翌期繰越と同じ前提条件）
    var ns = new ModuleSearcher<FiscalYear>();
    ns.AddGreaterThan(e => e.StartDate.Value, EndDate.Value);
    ns.OrderBy(e => e.StartDate.Value);
    ns.Limit(1);
    var next = ns.ExecuteFirstOrDefault();
    if (next == null)
    {
        Toaster.Error("翌期の会計年度がありません。先に翌期を作成してください（期末の振替と翌期首の振戻は必ず対で起票します）");
        return;
    }
    var typedNext = (FiscalYear)next;

    var already = alreadyPosted;
    var head = already ? "仕掛品の期末振替を打ち直します（既存の振替と振戻を削除して作り直します）。" : "仕掛品の期末振替を起票します。";
    var missing = st.MissingSalaryCount.Value ?? 0;
    var missingText = (missing > 0)
        ? $"【注意】人件費コストが未入力の月があります（{missing} 人月）。その分の工数は配賦 0 円で計算され、仕掛品の金額が過小になります。"
        : "";
    var answer = MessageBox.Show(
        head + missingText + $"対象 {projects} 案件・合計 {computed:#,0} 円。{Name.Value} の期末に「借方 仕掛品 / 貸方 仕掛品振替高」、{typedNext.Name.Value} の期首に同額の振戻を起票します。よろしいですか？",
        "実行", "キャンセル");
    if (answer != "実行") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 打ち直しは「消してから作る」。洗い替え方式なので、残っている伝票に足し込まない
    if (already && !DeleteWipJournals(true)) return;

    if (!PostWipJournals(typedNext)) return;
    UpdateWipStatus();
    Toaster.Success($"仕掛品の期末振替を起票しました（{projects} 案件・{computed:#,0} 円）");
}

void WipCancel_OnClick()
{
    if (this.IsNewData) return;
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("仕掛品の期末振替の取消は経理のみ実行できます");
        return;
    }
    var answer = MessageBox.Show(
        "仕掛品の期末振替を取り消します（期末の振替仕訳と翌期首の振戻仕訳を削除します）。よろしいですか？",
        "取り消す", "やめる");
    if (answer != "取り消す") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);
    if (!DeleteWipJournals(true)) return;
    UpdateWipStatus();
    Toaster.Success("仕掛品の期末振替を取り消しました");
}

// 期末の振替仕訳と翌期首の振戻仕訳を削除する。
// 締め済みの期間に入っている伝票は消さない（ADR-0070: 取消の期限は「締め」に置く）
bool DeleteWipJournals(bool showError)
{
    var targets = FindWipJournals();
    foreach (var je in targets)
    {
        if (IsPeriodClosedOn(je.EntryDate.Value))
        {
            if (showError)
            {
                Toaster.Error($"伝票 No.{je.JournalNo.Value}（{je.EntryDate.Value:yyyy/MM/dd}）の期間は締め済みです。締めを解除してから取り消すか、当期に反対仕訳を起こしてください");
            }
            return false;
        }
    }
    foreach (var je in targets)
    {
        var ls = new ModuleSearcher<JournalLine>();
        ls.AddEquals(l => l.JournalEntryId.Value, je.Id.Value);
        foreach (var row in ls.Execute())
        {
            var l = (JournalLine)row;
            if (l.Delete() != true) { if (showError) { Toaster.Error("仕訳明細の削除に失敗しました"); } return false; }
        }
        if (je.Delete() != true) { if (showError) { Toaster.Error("仕訳の削除に失敗しました"); } return false; }
    }
    return true;
}

List<JournalEntry> FindWipJournals()
{
    var result = new List<JournalEntry>();
    var s = new ModuleSearcher<JournalEntry>();
    s.AddIn(e => e.SourceType.Value, "wip", "wip_reversal");
    s.AddEquals(e => e.SourceId.Value, this.Id.Value);
    foreach (var row in s.Execute()) { result.Add((JournalEntry)row); }
    return result;
}

// その日付の月次期間が締め済みかどうか（月末日は辞書順比較で外すので月初日で引く）
bool IsPeriodClosedOn(var d)
{
    if (d == null) return false;
    var monthFirst = new DateOnly(d.Year, d.Month, 1);
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) return false;
    return ((FiscalPeriod)period).Status.Value == "closed";
}

// 期末の振替と翌期首の振戻を対で起票する
bool PostWipJournals(FiscalYear next)
{
    var wipAcc = FindAccountByRole("wip_asset");
    if (wipAcc == null) { Toaster.Error("仕掛品の科目が見つかりません（科目マスタの「役割」に wip_asset を設定してください）"); return false; }
    var transferAcc = FindAccountByRole("wip_transfer");
    if (transferAcc == null) { Toaster.Error("仕掛品振替高の科目が見つかりません（科目マスタの「役割」に wip_transfer を設定してください）"); return false; }

    var rows = FindWipCandidates();
    if (rows.Count == 0) { Toaster.Error("仕掛品に振り替える対象がありません"); return false; }

    if (IsPeriodClosedOn(EndDate.Value))
    {
        Toaster.Error("期末の月次期間が締め済みです。締めを解除してから実行してください");
        return false;
    }
    if (IsPeriodClosedOn(next.StartDate.Value))
    {
        Toaster.Error($"{next.Name.Value} の期首の月次期間が締め済みです。振戻を起票できないため中止しました");
        return false;
    }

    if (!PostOneWipJournal(this, EndDate.Value, "wip", $"仕掛品振替（期末）{Name.Value}", wipAcc, transferAcc, rows, false)) return false;
    if (!PostOneWipJournal(next, next.StartDate.Value, "wip_reversal", $"仕掛品振戻（期首）{Name.Value}分", wipAcc, transferAcc, rows, true)) return false;
    return true;
}

// reverse=false: 借方 仕掛品 / 貸方 仕掛品振替高（期末）
// reverse=true : 借方 仕掛品振替高 / 貸方 仕掛品（翌期首の振戻）
bool PostOneWipJournal(FiscalYear fy, var entryDate, string sourceType, string description,
                       Account wipAcc, Account transferAcc, List<WipCandidate> rows, bool reverse)
{
    // **行の内容はプリミティブの並行リストに組んでから確保する。**
    // モジュールのインスタンス（ここでは WipCandidate）を持ち回ったまま
    // `AddRows` を呼ぶと `Value cannot be null. (Parameter 'source')` で落ちる——
    // ISSUE-0006 で 3 回失敗した現象をここで**再現・特定した**（2026-08-18）。
    // 実績のある形は固定資産の処分・経費の仕訳・検収の売上仕訳と同じ「並行リスト」方式である。
    var dcList = new List<string>();
    var accList = new List<object>();
    var amtList = new List<int>();
    var projList = new List<object>();
    var deptList = new List<object>();
    var descList = new List<string>();
    foreach (var c in rows)
    {
        var amt = c.WipAmount.Value ?? 0;
        var desc = $"{c.ProjectCode.Value} {c.ProjectName.Value}";
        object proj = c.ProjectId.Value;
        object dept = c.DepartmentId.Value;
        dcList.Add("D");
        accList.Add(reverse ? transferAcc.Id.Value : wipAcc.Id.Value);
        amtList.Add(amt); projList.Add(proj); deptList.Add(dept); descList.Add(desc);
        dcList.Add("C");
        accList.Add(reverse ? wipAcc.Id.Value : transferAcc.Id.Value);
        amtList.Add(amt); projList.Add(proj); deptList.Add(dept); descList.Add(desc);
    }

    var nextNo = new JournalEntry().NextJournalNo(fy.Id.Value);
    var je = new JournalEntry();
    je.EntryDate.Value = entryDate;
    je.EntryType.Value = "adjust";
    je.Description.Value = description;
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = fy.Id.Value;
    je.SourceType.Value = sourceType;
    // 振戻は翌期の伝票だが、**どの年度の振替に対する戻しか**を指すので source_id は前期の年度 id
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(dcList.Count);

    var i = 0;
    foreach (var lr in je.Lines.Rows)
    {
        var l = (JournalLine)lr;
        l.LineNo.Value = i + 1;
        l.Dc.Value = dcList[i];
        l.Account.Value = accList[i];
        l.Amount.Value = amtList[i];
        l.InputAmount.Value = amtList[i];
        l.TaxInputMode.Value = "none";
        l.Description.Value = descList[i];
        l.ProjectRef.Value = projList[i];
        if (deptList[i] != null) { l.Department.Value = deptList[i]; }
        i = i + 1;
    }
    // 決算整理なので消費税の対象外（原価の付け替えであって取引ではない）
    je.MarkAllLinesOutOfScope();
    je.FillMissingDepartments();
    if (je.Submit() != true)
    {
        Toaster.Error($"{description} の起票に失敗しました");
        return false;
    }
    return true;
}

List<WipCandidate> FindWipCandidates()
{
    var result = new List<WipCandidate>();
    var s = new ModuleSearcher<WipCandidate>();
    foreach (var row in s.Execute())
    {
        var c = (WipCandidate)row;
        if ($"{c.FiscalYearId.Value}" != $"{this.Id.Value}") continue;
        if ((c.WipAmount.Value ?? 0) <= 0) continue;
        result.Add(c);
    }
    return result;
}

// 科目を役割で引く（コード直書きをしない。固定資産と同じ作法）
Account FindAccountByRole(string role)
{
    var s = new ModuleSearcher<Account>();
    s.AddEquals(e => e.AccountRole.Value, role);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (Account)found;
}
