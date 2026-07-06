// ============================================================
// 新規申請時の初期化 (親モジュールの OnAfterInitialization から呼ぶ)
// parentId は親の @temporary:guid をそのまま入れて良い。
// 親 ↔ ApprovalFlow の双方向参照は CLB の TemporaryIdResolver がサイクル解決する。
// ============================================================
void Initialize(string parentModuleName, string parentId, string templateName)
{
    Status.Value = "Pending";
    AttemptNo.Value = 1;
    ParentModuleName.Value = parentModuleName;
    ParentId.Value = parentId;

    var s = new ModuleSearcher<ApprovalFlowTemplate>();
    s.AddEquals(t => t.Name.Value, templateName);
    var tmpls = s.Execute();
    if (tmpls.Count > 0) TemplateId.Value = tmpls[0].Id.Value;
}

// 申請ボタン押下時に Orders/Members をテンプレから生成する。
// UseIndexSort=true なので OrderNo は Submit 時に 0,1,... で自動採番される。
// テンプレメンバーの approver_role (manager/director) は申請者の所属部門の課長/部長へ動的解決。
// 失敗時 (テンプレ未設定・役職が解決できない) は行を作らず false を返す（中途半端な Orders を残さない）。
bool LoadFromTemplate()
{
    if (TemplateId.Value == null) { Toaster.Error("承認テンプレートが未設定です"); return false; }
    var tmplSearcher = new ModuleSearcher<ApprovalFlowTemplateOrder>();
    tmplSearcher.AddEquals(o => o.TemplateId.Value, TemplateId.Value);
    tmplSearcher.OrderBy(o => o.OrderNo.Value);
    var tmplOrders = tmplSearcher.Execute();
    if (tmplOrders.Count == 0) { Toaster.Error("承認テンプレートに承認段階がありません"); return false; }

    var dept = FindCurrentUserDepartment();

    // 事前パス: 全メンバーの承認者を解決できるか検証してから行を作る
    foreach (var tmplOrder in tmplOrders)
    {
        var checkSearcher = new ModuleSearcher<ApprovalFlowTemplateMember>();
        checkSearcher.AddEquals(m => m.TemplateOrderId.Value, tmplOrder.Id.Value);
        var checkMembers = checkSearcher.Execute();
        foreach (var tmplMember in checkMembers)
        {
            var role = tmplMember.ApproverRole.Value;
            if (tmplMember.ApproverUser.Value == null && role != "manager" && role != "director")
            {
                Toaster.Error("承認テンプレートに承認者未設定のメンバーがあります");
                return false;
            }
            var resolved = ResolveApproverAvoidingSelf(dept, role, tmplMember.ApproverUser.Value);
            if (resolved == null)
            {
                Toaster.Error("承認者を決定できません: 部門の課長/部長の設定、または自己承認の代替となる経理ユーザーを確認してください（自己承認は禁止です）");
                return false;
            }
        }
    }

    var first = true;
    foreach (var tmplOrder in tmplOrders)
    {
        var newOrder = Orders.AddRow();
        newOrder.Status.Value = first ? "Active" : "Waiting";
        first = false;

        var memberSearcher = new ModuleSearcher<ApprovalFlowTemplateMember>();
        memberSearcher.AddEquals(m => m.TemplateOrderId.Value, tmplOrder.Id.Value);
        var tmplMembers = memberSearcher.Execute();
        foreach (var tmplMember in tmplMembers)
        {
            var approver = ResolveApproverAvoidingSelf(dept, tmplMember.ApproverRole.Value, tmplMember.ApproverUser.Value);

            var newMember = newOrder.Members.AddRow();
            newMember.IsRequired.Value = tmplMember.IsRequired.Value;
            newMember.ApproverUser.Value = approver;
            newMember.Status.Value = "Waiting";
            newMember.ParentModuleName.Value = ParentModuleName.Value;
            newMember.ParentId.Value = ParentId.Value;
        }
    }
    RecalculateCurrentApproverDisplay();
    return true;
}

// 承認者の解決＋自己承認の禁止 (decisions/0010)
// 役職解決/固定指定が申請者本人の場合: manager→director へ格上げ、
// それも本人（or director/固定が本人）→ 経理ロールの他ユーザー(最小Id)を代替承認者にする。
// 解決不能なら null（呼び出し側で申請ブロック）
object ResolveApproverAvoidingSelf(Department dept, string role, object fixedApprover)
{
    var resolved = fixedApprover;
    if (role == "manager" || role == "director") resolved = ResolveDeptRole(dept, role);
    if (resolved == null) return null;
    if (resolved != CurrentUser.Id.Value) return resolved;

    // 申請者本人だった: まず部門の部長へ格上げ (manager 解決時のみ意味を持つ)
    if (role == "manager" && dept != null)
    {
        var director = dept.DirectorUser.Value;
        if (director != null && director != CurrentUser.Id.Value) return director;
    }
    return FindFallbackApprover();
}

// 自己承認の代替承認者: 経理ロール (accounting) のうち申請者以外で Id 最小のユーザー
object FindFallbackApprover()
{
    var s = new ModuleSearcher<AppUser>();
    s.AddEquals(u => u.Role.Value, "accounting");
    s.OrderBy(u => u.Id.Value);
    var users = s.Execute();
    foreach (var u in users)
    {
        var au = (AppUser)u;
        if (au.Id.Value != CurrentUser.Id.Value) return au.Id.Value;
    }
    return null;
}

// 申請者 (CurrentUser) の所属部門を取得 (未設定なら null)
Department FindCurrentUserDepartment()
{
    var us = new ModuleSearcher<AppUser>();
    us.AddEquals(u => u.Id.Value, CurrentUser.Id.Value);
    var users = us.Execute();
    if (users.Count == 0) return null;
    var deptId = users[0].所属部門.Value;
    if (deptId == null) return null;
    var ds = new ModuleSearcher<Department>();
    ds.AddEquals(d => d.Id.Value, deptId);
    var depts = ds.Execute();
    if (depts.Count == 0) return null;
    return (Department)depts[0];
}

// 部門の役職 (manager=課長 / director=部長) を承認者ユーザー ID に解決
object ResolveDeptRole(Department dept, string role)
{
    if (dept == null) return null;
    if (role == "manager") return dept.ManagerUser.Value;
    if (role == "director") return dept.DirectorUser.Value;
    return null;
}

// 現在 Active な Order の最初の Waiting Member の承認者を CurrentApprover にセット
// (複数並列のときは最初の 1 人。検索しやすさ重視で代表者を持つ)
// Member の Status は遅延ロードで空のことがある (#60) ため DB を優先し、
// DB 未保存 (新規申請直後) のみメモリから解決する。
void RecalculateCurrentApproverDisplay()
{
    foreach (var o in Orders.Rows)
    {
        if (o.Status.Value != "Active") continue;

        var ms = new ModuleSearcher<ApprovalFlowMember>();
        ms.AddEquals(m => m.ApprovalFlowOrderId.Value, o.Id.Value);
        ms.AddEquals(m => m.Status.Value, "Waiting");
        var dbMembers = ms.Execute();
        if (dbMembers.Count > 0)
        {
            CurrentApprover.Value = dbMembers[0].ApproverUser.Value;
            return;
        }

        foreach (var m in o.Members.Rows)
        {
            if (m.Status.Value != "Waiting") continue;
            CurrentApprover.Value = m.ApproverUser.Value;
            return;
        }
    }
    CurrentApprover.Value = null;
}

// 承認フローを「状態：進行中 申請者→(B,C)→▶D→E」形式の1行文字列にする
// マーカー: ✓=承認済 / ✗=却下 / —=スキップ / ▶=現在の担当 / (空)=未着手
// UseIndexSort=true なので Orders.Rows は OrderNo 昇順保証 → ソート不要
void UpdateFlowSummary()
{
    // 新規申請時は Id が @temporary:guid なので ModuleSearcher で数値列にぶつけるとエラー
    if (GetParentModule().IsNewData)
    {
        FlowSummary.Value = "";
        return;
    }

    var parts = new List<string>();

    // 先頭に申請者 (履歴の最古 Submit から取る — 親の LinkField は遅延ロードで取れないため)
    var hs = new ModuleSearcher<ApprovalHistory>();
    hs.AddEquals(h => h.ApprovalFlowId.Value, this.Id.Value);
    hs.AddEquals(h => h.Action.Value, "Submit");
    var subHistory = hs.Execute();
    if (subHistory.Count > 0)
    {
        var creatorName = subHistory[0].ActorUser.DisplayText;
        if (!string.IsNullOrEmpty(creatorName)) parts.Add("✓" + creatorName);
    }

    // 各 Order のメンバー (Order.Status は遅延ロードで取れないことがあるので「最初の Waiting に ▶」戦略)
    var currentMarked = false;
    foreach (var o in Orders.Rows)
    {
        var names = new List<string>();
        foreach (var m in o.Members.Rows)
        {
            var ms = m.Status.Value;
            var mark = "";
            if (ms == "Approved") mark = "✓";
            else if (ms == "Rejected") mark = "✗";
            else if (ms == "Skipped") mark = "—";
            else if (!currentMarked) { mark = "▶"; currentMarked = true; }
            names.Add(mark + m.ApproverUser.DisplayText);
        }
        if (names.Count == 0) continue;
        if (names.Count == 1) parts.Add(names[0]);
        else parts.Add("(" + string.Join(",", names) + ")");
    }

    var flowStr = string.Join("→", parts);
    var statusDisplay = Status.DisplayText;
    if (string.IsNullOrEmpty(statusDisplay)) statusDisplay = Status.Value;
    FlowSummary.Value = "状態:" + statusDisplay + "  " + flowStr;
}

// 承認待ち一覧の検索初期値: 現在の承認者 = 自分
void OnSearchInitialization()
{
    CurrentApprover.SearchValue = CurrentUser.Id.Value;
}

// 承認待ち一覧の「開く」ボタン: 各申請モジュール (LeaveRequest / ExpenseRequest) に遷移。
// ListField 経由の行 Module は LinkField/TextField の .Value が遅延ロードで空のことがあるので、
// 自分の Id で ModuleSearcher 再取得して値を確実に取る。
void OpenRequest_OnClick()
{
    var s = new ModuleSearcher<ApprovalFlow>();
    s.AddEquals(f => f.Id.Value, Id.Value);
    var rs = s.Execute();
    if (rs.Count == 0) return;
    var parentModule = rs[0].ParentModuleName.Value;
    var parentId = rs[0].ParentId.Value;
    if (string.IsNullOrEmpty(parentModule) || string.IsNullOrEmpty(parentId)) return;
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl(parentModule, parentId));
}

// 現在ユーザーが申請者か (parent.Creator.Value は LinkField 遅延ロードで取れないため履歴ベース)
bool IsCurrentUserCreator()
{
    var hs = new ModuleSearcher<ApprovalHistory>();
    hs.AddEquals(h => h.ApprovalFlowId.Value, this.Id.Value);
    hs.AddEquals(h => h.Action.Value, "Submit");
    var subHistory = hs.Execute();
    if (subHistory.Count == 0) return false;
    return subHistory[0].ActorUser.Value == CurrentUser.Id.Value;
}

// ============================================================
// 履歴ヘルパー
// ============================================================
void AddHistory(string action, string comment)
{
    var h = History.AddRow();
    // AttemptNo はメモリの ChildModule 値が遅延ロードで空のことがある（#60）。
    // 再申請直後はメモリ側が最新（DB+1）なのでメモリ優先、null のときだけ DB から解決する
    h.AttemptNo.Value = AttemptNo.Value ?? ResolveDbAttemptNo();
    h.ActorUser.Value = CurrentUser.Id.Value;
    h.Action.Value = action;
    h.ActedAt.Value = DateTime.Now;
    h.Comment.Value = comment;
}

// Reject/Cancel 等で残りの Order/Member を Skipped にする
void SkipRemainingOrdersAndMembers()
{
    foreach (var o in Orders.Rows)
    {
        if (o.Status.Value == "Waiting" || o.Status.Value == "Active")
            o.Status.Value = "Skipped";
        foreach (var m in o.Members.Rows)
        {
            if (m.Status.Value == "Waiting") m.Status.Value = "Skipped";
        }
    }
}

// ============================================================
// 初期化 + ボタン出し分け
// ============================================================
void OnAfterInitialization()
{
    UpdateFlowSummary();
    UpdateButtons();
}

// UI 側: 現在ユーザー宛の Waiting Member がいるか DB から判定 (Order.Id 経由)
bool HasWaitingMemberForCurrentUser()
{
    var parent = GetParentModule();
    if (parent.IsNewData) return false;
    foreach (var o in Orders.Rows)
    {
        var s = new ModuleSearcher<ApprovalFlowMember>();
        s.AddEquals(m => m.ApprovalFlowOrderId.Value, o.Id.Value);
        s.AddEquals(m => m.ApproverUser.Value, CurrentUser.Id.Value);
        s.AddEquals(m => m.Status.Value, "Waiting");
        if (s.Execute().Count > 0) return true;
    }
    return false;
}

// Approve/Reject 実行時: Order.Id 経由で DB から自分宛の Waiting Member を検索 + メモリ Member を返す
ApprovalFlowMember GetCurrentMemberForUserStrict()
{
    foreach (var o in Orders.Rows)
    {
        var s = new ModuleSearcher<ApprovalFlowMember>();
        s.AddEquals(m => m.ApprovalFlowOrderId.Value, o.Id.Value);
        s.AddEquals(m => m.ApproverUser.Value, CurrentUser.Id.Value);
        s.AddEquals(m => m.Status.Value, "Waiting");
        var members = s.Execute();
        if (members.Count == 0) continue;
        var dbMember = members[0];
        foreach (var m in o.Members.Rows)
        {
            if (m.Id.Value == dbMember.Id.Value) return m;
        }
    }
    return null;
}

// ボタン可視性更新
void UpdateButtons()
{
    var parent = GetParentModule();
    var isNewParent = parent.IsNewData;
    var s = Status.Value;
    var isPending = s == "Pending";
    var isRejected = s == "Rejected";
    var isCancelled = s == "Cancelled";
    var canApprove = isPending && HasWaitingMemberForCurrentUser();
    var isCreator = !isNewParent && IsCurrentUserCreator();

    SubmitButton.IsVisible   = isNewParent;
    ApproveButton.IsVisible  = !isNewParent && canApprove;
    RejectButton.IsVisible   = !isNewParent && canApprove;
    ResubmitButton.IsVisible = !isNewParent && (isRejected || isCancelled) && isCreator;
    CancelButton.IsVisible   = !isNewParent && isPending && isCreator;

    Comment.IsEnabled = canApprove;
}

// ============================================================
// 申請ボタン
// ============================================================
// 親モジュール (申請モジュール) の契約: SelectTemplateName() / ValidateForApply() /
// OnApprovalFlowStatusChanged(string) を実装すること。
// ValidateForApply は業務チェック (必須項目・費目固有の例外項目) を行い bool を返す。
// OnApprovalFlowStatusChanged はフロー状態の変化 (Pending/Approved/Rejected/Cancelled) を
// 親側のステータス (経費なら精算ステータス) に反映するために、親 Submit の直前に呼ばれる。

// 親モジュールへ承認フローの状態変化を通知する
void NotifyParentStatusChanged()
{
    GetParentModule().OnApprovalFlowStatusChanged(Status.Value);
}

void SubmitButton_OnClick()
{
    var parent = GetParentModule();
    var wasNew = parent.IsNewData;

    if (wasNew)
    {
        var valid = parent.ValidateForApply();
        if (valid != true) return;
    }

    using var suspend = parent.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    if (wasNew)
    {
        // 申請時点の入力値でテンプレートを再解決する
        // (Initialize 時は金額・費目が未入力のため、その時点の TemplateId は仮値)
        var tmplName = parent.SelectTemplateName();
        var ts = new ModuleSearcher<ApprovalFlowTemplate>();
        ts.AddEquals(t => t.Name.Value, tmplName);
        var tmpls = ts.Execute();
        if (tmpls.Count == 0) { Toaster.Error($"承認テンプレート '{tmplName}' が見つかりません"); return; }
        TemplateId.Value = tmpls[0].Id.Value;

        if (!LoadFromTemplate()) return;
        AddHistory("Submit", "");
        NotifyParentStatusChanged();
    }

    var ret = parent.Submit();
    if (ret != true) { Toaster.Error("申請に失敗しました"); return; }
    Toaster.Success("申請しました");

    // 申請成功後: 最初の承認者へ通知 (メモリの Id は temporary のため DB から取り直す)
    if (wasNew)
    {
        NotifyActiveApprovers(FetchLatestOwnFlowFromDb(), "承認依頼");
    }
}

// ============================================================
// アプリ内通知 (B-9)
// Slack/メール連携は将来 NotifyUser から呼ぶ（現状は Logger のみ=口だけ実装・作業合意）。
// 支払期限リマインドは見送り（売掛残高一覧の期限超過表示で代替）。
// ============================================================
void NotifyUser(object recipientUserId, string title, string body, string linkModule, string linkId)
{
    if (recipientUserId == null) return;
    var n = new Notification();
    n.RecipientUser.Value = recipientUserId;
    n.Title.Value = title;
    n.Body.Value = body;
    n.LinkModule.Value = linkModule;
    n.LinkId.Value = linkId;
    n.IsRead.Value = false;
    n.CreatedAt.Value = DateTime.Now;
    var ret = n.Submit();
    if (ret != true) { Logger.Warn($"通知の作成に失敗: {title}"); }
    Logger.Log($"SLACK(mock): to user#{recipientUserId} {title} - {body}");
}

// 申請モジュール名の表示名 (通知文言用)
string ModuleDisplayName(string moduleName)
{
    if (moduleName == "ExpenseRequest") return "経費申請";
    return moduleName;
}

// 実 Id・実 ParentId を DB から解決した自フロー (メモリの遅延ロードを信用しない)
ApprovalFlow FetchSelfFromDb()
{
    var s = new ModuleSearcher<ApprovalFlow>();
    s.AddEquals(f => f.Id.Value, this.Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (ApprovalFlow)found;
}

// AttemptNo の DB 解決（メモリの ChildModule 値が遅延ロードで空/古いことへの対処。#60）。
// DB にも無ければ 1（初回申請）
int ResolveDbAttemptNo()
{
    if (!this.IsNewData)
    {
        var self = FetchSelfFromDb();
        if (self != null && self.AttemptNo.Value != null) return (int)self.AttemptNo.Value;
    }
    return (int)(AttemptNo.Value ?? 1);
}

// 新規申請直後: メモリの Id が temporary のため、自分が直近に作成したフローを DB から特定する
// (同一ユーザーの同時多重申請はブラウザ操作上起こらない前提)
ApprovalFlow FetchLatestOwnFlowFromDb()
{
    var s = new ModuleSearcher<ApprovalFlow>();
    s.AddEquals(f => f.Creator.Value, CurrentUser.Id.Value);
    s.OrderByDescending(f => f.Id.Value);
    s.Limit(1);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (ApprovalFlow)found;
}

// 現在 Active な Order の Waiting Member 全員へ通知 (メンバー列挙は既存の DB 検索と同じ規模)
void NotifyActiveApprovers(ApprovalFlow flow, string title)
{
    if (flow == null) return;
    var linkModule = flow.ParentModuleName.Value;
    var linkId = $"{flow.ParentId.Value}";
    var body = $"{ModuleDisplayName(linkModule)}の承認をお願いします";
    var os = new ModuleSearcher<ApprovalFlowOrder>();
    os.AddEquals(o => o.ApprovalFlowId.Value, flow.Id.Value);
    os.AddEquals(o => o.Status.Value, "Active");
    var orders = os.Execute();
    foreach (var oRow in orders)
    {
        var o = (ApprovalFlowOrder)oRow;
        var ms = new ModuleSearcher<ApprovalFlowMember>();
        ms.AddEquals(m => m.ApprovalFlowOrderId.Value, o.Id.Value);
        ms.AddEquals(m => m.Status.Value, "Waiting");
        var members = ms.Execute();
        foreach (var mRow in members)
        {
            var m = (ApprovalFlowMember)mRow;
            NotifyUser(m.ApproverUser.Value, title, body, linkModule, linkId);
        }
    }
}

// 申請者へ通知 (申請者 = 履歴の最初の Submit の ActorUser。IsCurrentUserCreator と同じ解決法)
void NotifyCreator(ApprovalFlow flow, string title, string body)
{
    if (flow == null) return;
    var hs = new ModuleSearcher<ApprovalHistory>();
    hs.AddEquals(h => h.ApprovalFlowId.Value, flow.Id.Value);
    hs.AddEquals(h => h.Action.Value, "Submit");
    var subHistory = hs.Execute();
    if (subHistory.Count == 0) return;
    var actor = ((ApprovalHistory)subHistory[0]).ActorUser.Value;
    NotifyUser(actor, title, body, flow.ParentModuleName.Value, $"{flow.ParentId.Value}");
}

// ============================================================
// 承認ボタン
// ============================================================
void Approve_OnClick()
{
    using var suspend = GetParentModule().SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var member = GetCurrentMemberForUserStrict();
    if (member == null) { Toaster.Error("承認権限がありません"); return; }

    member.Status.Value = "Approved";
    member.ActorUser.Value = CurrentUser.Id.Value;
    member.ApprovedAt.Value = DateTime.Now;

    var order = GetOrderById(member.ApprovalFlowOrderId.Value);
    if (order != null && IsOrderCompleted(order))
    {
        order.Status.Value = "Approved";
        AdvanceToNextOrder();
    }

    AddHistory("Approve", Comment.Value);
    Comment.Value = "";
    RecalculateCurrentApproverDisplay();
    UpdateFlowSummary();
    NotifyParentStatusChanged();

    var ret = GetParentModule().Submit();
    if (ret == true) Toaster.Success("承認しました");

    // 承認成功後の通知: 最終承認なら申請者へ、次段へ進んだなら次の承認者へ
    if (ret == true)
    {
        var flow = FetchSelfFromDb();
        if (Status.Value == "Approved")
        {
            NotifyCreator(flow, "承認されました", "申請が最終承認されました");
        }
        else
        {
            NotifyActiveApprovers(flow, "承認依頼");
        }
    }
}

ApprovalFlowOrder GetOrderById(string orderId)
{
    foreach (var o in Orders.Rows)
        if (o.Id.Value == orderId) return o;
    return null;
}

// Order の承認完了判定: 必須メンバー全員 Approved、または必須ゼロで誰か1人 Approved
bool IsOrderCompleted(ApprovalFlowOrder order)
{
    int requiredCount = 0, requiredApproved = 0, optionalApproved = 0;
    foreach (var m in order.Members.Rows)
    {
        if (m.IsRequired.Value == true)
        {
            requiredCount++;
            if (m.Status.Value == "Approved") requiredApproved++;
        }
        else
        {
            if (m.Status.Value == "Approved") optionalApproved++;
        }
    }
    if (requiredCount > 0) return requiredApproved == requiredCount;
    return optionalApproved >= 1;
}

// 次の Waiting な Order を Active 化。なければフロー全体を Approved に。
// 注意: ChildModule の Status.Value は遅延ロードで空のことがある (#60 の罠) ため、
// メモリの Rows を直接判定せず DB から Waiting Order を検索し、対応するメモリ行を更新する。
// (2段テンプレの1段目承認で「2段目 Waiting なのにフロー全体 Approved」になる実測バグを 2026-07-05 修正)
void AdvanceToNextOrder()
{
    var s = new ModuleSearcher<ApprovalFlowOrder>();
    s.AddEquals(o => o.ApprovalFlowId.Value, this.Id.Value);
    s.AddEquals(o => o.Status.Value, "Waiting");
    s.OrderBy(o => o.OrderNo.Value);
    var waiting = s.Execute();
    if (waiting.Count > 0)
    {
        var nextId = waiting[0].Id.Value;
        foreach (var o in Orders.Rows)
        {
            if (o.Id.Value == nextId)
            {
                o.Status.Value = "Active";
                return;
            }
        }
        // メモリ行が見つからなくても DB に Waiting がある以上、承認済にはしない
        return;
    }

    // DB に Waiting なし → メモリ側の未保存 Waiting を最終確認 (通常は無い)
    foreach (var o in Orders.Rows)
    {
        if (o.Status.Value == "Waiting")
        {
            o.Status.Value = "Active";
            return;
        }
    }
    Status.Value = "Approved";
    CurrentApprover.Value = null;
}

// ============================================================
// 却下ボタン
// ============================================================
void Reject_OnClick()
{
    using var suspend = GetParentModule().SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var member = GetCurrentMemberForUserStrict();
    if (member == null) { Toaster.Error("却下権限がありません"); return; }

    member.Status.Value = "Rejected";
    member.ActorUser.Value = CurrentUser.Id.Value;
    member.ApprovedAt.Value = DateTime.Now;

    var order = GetOrderById(member.ApprovalFlowOrderId.Value);
    if (order != null) order.Status.Value = "Rejected";

    Status.Value = "Rejected";
    SkipRemainingOrdersAndMembers();
    var rejectComment = Comment.Value;
    AddHistory("Reject", rejectComment);
    Comment.Value = "";
    RecalculateCurrentApproverDisplay();
    UpdateFlowSummary();
    NotifyParentStatusChanged();

    var ret = GetParentModule().Submit();
    if (ret == true) Toaster.Info("却下しました");

    // 却下成功後: 申請者へ通知
    if (ret == true)
    {
        NotifyCreator(FetchSelfFromDb(), "却下されました", $"申請が却下されました（コメント: {rejectComment}）");
    }
}

// ============================================================
// 再申請ボタン
// ============================================================
void Resubmit_OnClick()
{
    if (Status.Value != "Rejected" && Status.Value != "Cancelled") return;
    var parent = GetParentModule();

    using var suspend = parent.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    if (!IsCurrentUserCreator()) { Toaster.Error("自分の申請のみ再申請できます"); return; }

    var valid = parent.ValidateForApply();
    if (valid != true) return;

    // メモリ値は遅延ロードで空/古いことがあるため DB の AttemptNo を基準に増分（#60）
    AttemptNo.Value = ResolveDbAttemptNo() + 1;
    Status.Value = "Pending";

    if (!RebuildOrdersFromTemplate()) return;
    AddHistory("Resubmit", "");
    NotifyParentStatusChanged();

    var ret = parent.Submit();
    if (ret == true) Toaster.Success("再申請しました");
    else if (ret == false) Toaster.Error("再申請に失敗しました");

    // 再申請成功後: 新ルートの最初の承認者へ通知 (既存データのため自 Id は実 Id)
    if (ret == true)
    {
        NotifyActiveApprovers(FetchSelfFromDb(), "（再）承認依頼");
    }
}

// 古い Orders を削除し、親のテンプレ再解決 → Orders/Members を再構築する
bool RebuildOrdersFromTemplate()
{
    var parent = GetParentModule();

    var rowsToDelete = new List<ApprovalFlowOrder>();
    foreach (var o in Orders.Rows) rowsToDelete.Add(o);
    foreach (var o in rowsToDelete) Orders.DeleteRow(o);

    var tmplName = parent.SelectTemplateName();
    var ts = new ModuleSearcher<ApprovalFlowTemplate>();
    ts.AddEquals(t => t.Name.Value, tmplName);
    var tmpls = ts.Execute();
    if (tmpls.Count == 0) { Toaster.Error($"承認テンプレート '{tmplName}' が見つかりません"); return false; }
    TemplateId.Value = tmpls[0].Id.Value;
    return LoadFromTemplate();
}

// 実費超過の再承認 (親モジュールの実費確定処理から呼ばれる)
// 承認済フローを Pending に戻し、実費でテンプレを再解決して承認をやり直す
void ReapproveForOverrun(string reason)
{
    var parent = GetParentModule();

    using var suspend = parent.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // メモリ値は遅延ロードで空/古いことがあるため DB の AttemptNo を基準に増分（#60）
    AttemptNo.Value = ResolveDbAttemptNo() + 1;
    Status.Value = "Pending";

    if (!RebuildOrdersFromTemplate()) return;
    AddHistory("Resubmit", reason);
    NotifyParentStatusChanged();

    var ret = parent.Submit();
    if (ret == true) Toaster.Success("実費が見込みを超過したため再承認を依頼しました");
    else Toaster.Error("再承認の依頼に失敗しました");

    // 超過再承認の成功後: 新ルートの最初の承認者へ通知
    if (ret == true)
    {
        NotifyActiveApprovers(FetchSelfFromDb(), "（再）承認依頼");
    }
}

// ============================================================
// キャンセルボタン
// ============================================================
void Cancel_OnClick()
{
    using var suspend = GetParentModule().SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    if (Status.Value != "Pending") return;
    var parent = GetParentModule();
    if (!IsCurrentUserCreator()) { Toaster.Error("自分の申請のみキャンセルできます"); return; }

    Status.Value = "Cancelled";
    SkipRemainingOrdersAndMembers();
    AddHistory("Cancel", "申請をキャンセルしました");
    RecalculateCurrentApproverDisplay();
    UpdateFlowSummary();
    NotifyParentStatusChanged();

    var ret = parent.Submit();
    if (ret == true) Toaster.Success("キャンセルしました");
}
