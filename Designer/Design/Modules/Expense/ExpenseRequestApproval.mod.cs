// ExpenseRequestApproval.mod.cs — 経費申請の承認（ADR-0069 段階6）
//
// expense_request を指す 3 本目の「対象者別モジュール」。人ゲート＝IsApprover。
// 持つのは「ヘッダ読取＋明細読取＋承認欄」だけで、申請の作成・編集・実費確定・仕訳生成は一切持たない
// （それぞれ申請者用 ExpenseRequest／経理用 ExpenseRequestAccounting の仕事）。
//
// 明細は ExpenseRequestLineApproval（同じ expense_request_lines を指す承認者用モジュール）から読む。
// 申請者用の ExpenseRequestLine には行条件（Creator == CurrentUser）が入っているので、
// 承認者がそちらを見ると **エラーにならず静かに空になる**（_specs/ModuleDesign.md:141-149）。
// 承認者が「金額の内訳も領収書も見ずに承認する」状態はこれが原因で、本モジュールがその穴を塞ぐ。
//
// ------------------------------------------------------------
// 行条件（DataReadCondition / DataWriteCondition）を空にした理由 — 意図的な判断
// ------------------------------------------------------------
// 実査（docs/qa/30_レポート/2026-08-17_案A認可設計の実査.md §5）の設計案は
// DataReadCondition = SettlementStatus != "draft" だったが、**入れていない**。
//
//  1. 却下は settlement_status を "draft" に戻す（ExpenseRequest.mod.cs:696-699）。
//     Reject_OnClick は NotifyParentStatusChanged() → GetParentModule().Submit() の順で、
//     **送信する値が既に draft** になっている（ApprovalFlow.mod.cs:1142-1144）。
//     CLB が行条件を更新の前像で見るか後像で見るかは仕様書に記載が無く、後像なら却下が丸ごと弾かれる。
//  2. 仮に前像評価で書き込めても、保存直後にその行は条件の外へ出る。承認者がいま立っている画面の
//     読み戻しが条件外になるため、却下後に画面が壊れる筋が残る。
//  3. 一方この行条件で守れるのは「下書きを URL 直打ちで覗かれない」ことだけ。提出済みの他人の申請は
//     どちらの設計でも開ける（実査 §1 が受け入れた残留リスク。行単位の担保は
//     ApprovalFlow.GetCurrentMemberForUserStrict() が承認操作を止めることで果たす）。
//     得るものが小さく、失うもの（却下が動かない）が大きい。
//  4. 本モジュールは一覧ページを持たない詳細専用で、入口は ApprovalInbox（自分担当の行しか出ない）。
//     粗さは URL 直打ちに限定される。
//
// 前像評価であること・却下後の読み戻しが無事なことを実機で確認できたら、この判断は撤回して
// SettlementStatus != "draft" を入れ直してよい（そのときは却下 → 再申請 → 再承認まで通しで流すこと）。

// ============================================================
// 画面の出し分け
// ============================================================

void OnAfterInitialization()
{
    UpdateVisibility();
}

// 支払先区分に応じて「精算対象者」か「支払取引先」の片方だけ出す（経理用と同じ規約）
void UpdateVisibility()
{
    var toPartner = (PayeeType.Value == "partner");
    PayeeUserLabel.IsVisible = !toPartner;
    PayeeUser.IsVisible = !toPartner;
    PayeePartnerLabel.IsVisible = toPartner;
    PayeePartner.IsVisible = toPartner;
}

// ============================================================
// ApprovalFlow との契約メソッド
// （ApprovalFlow.mod.cs:777-781 のコメントが定める親モジュールの契約。
//   承認・却下は NotifyParentStatusChanged() → GetParentModule().Submit() を通るので、
//   OnApprovalFlowStatusChanged はここに必ず要る）
// ============================================================

// 承認フローの状態変化を精算ステータスへ反映する（親 Submit の直前に呼ばれる）
void OnApprovalFlowStatusChanged(string flowStatus)
{
    if (flowStatus == "Approved")
    {
        // 経理処理以降へ進んでいる場合は巻き戻さない（申請者用 ExpenseRequest と同じ規約）
        var st = SettlementStatus.Value;
        if (st == null || st == "" || st == "draft" || st == "applying") { SettlementStatus.Value = "approved"; }
    }
    else if (flowStatus == "Rejected" || flowStatus == "Cancelled")
    {
        SettlementStatus.Value = "draft";
    }
    else if (flowStatus == "Pending")
    {
        // 承認者の画面から申請・再申請は起こせない（ボタンが出ない）ので通常ここへは来ない。
        // 来たときに備えてステータスだけ合わせる。**部門・見込み額のスナップショットは取らない** ——
        // ここで CurrentUser.所属部 を書くと承認者の部門が申請に焼き付いて部門別予実が壊れる
        SettlementStatus.Value = "applying";
    }
}

// 申請時の業務チェック（契約メソッド）。承認者の画面からは申請できない
bool ValidateForApply()
{
    Toaster.Error("承認者の画面からは申請できません。申請者本人の経費申請画面から操作してください");
    return false;
}

// 承認ルートの解決（契約メソッド）。同上（空を返すと ApprovalFlow 側が静かに中断する）
List<object> SelectTemplateIds()
{
    return new List<object>();
}
