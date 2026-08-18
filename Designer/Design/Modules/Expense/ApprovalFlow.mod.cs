// ============================================================
// 新規申請時の初期化 (親モジュールの OnAfterInitialization から呼ぶ)
// parentId は親の @temporary:guid をそのまま入れて良い。
// 親 ↔ ApprovalFlow の双方向参照は CLB の TemporaryIdResolver がサイクル解決する。
// ============================================================
// 状態は "Draft"。明細を 1 件足した時点で申請が下書きとして保存されるようになったため（ADR-0066 の UI 改訂）、
// 初期化の時点で "Pending" にすると「申請していないのに進行中」の行が DB に残り、申請ボタンが消える。
// "Draft" → 申請ボタン押下で "Pending"（SubmitButton_OnClick）。複製ドラフトと同じ扱いになる
void Initialize(string parentModuleName, string parentId)
{
    Status.Value = "Draft";
    AttemptNo.Value = 1;
    ParentModuleName.Value = parentModuleName;
    ParentId.Value = parentId;
    // テンプレートは申請時に親（明細）から解決するのでここでは決めない（ADR-0066）
}

// ============================================================
// 承認段の合成（ADR-0066）
// 親が「行ごとに解決したテンプレート」を複数返してくるので、その段を 1 本の段列に重ね合わせる。
//
// 手続き:
//   1. 各テンプレートの段を「承認者キー」でまとめる
//      （役職段は role:manager / role:director、個人指名段は user:{承認者IDの昇順連結}）
//   2. 同じキーの段は 1 つに畳む
//   3. 並び順は「そのキーが登場したテンプレート内の order_no の最大値」の昇順。
//      同値なら manager → director → 個人指名 の順
//
// この手続きは「行ごとに必要な段を求めて重ねる」と「最も強いルールを採る」を同時に満たす。
// テンプレートに強さを表す列を足す必要が無く、明細が 1 行のときは従来と同一の段列に帰着する。
// ============================================================
List<ApprovalFlowTemplateMember> MembersOf(object templateOrderId)
{
    var result = new List<ApprovalFlowTemplateMember>();
    var s = new ModuleSearcher<ApprovalFlowTemplateMember>();
    s.AddEquals(m => m.TemplateOrderId.Value, templateOrderId);
    s.OrderBy(m => m.Id.Value);
    foreach (var m in s.Execute())
    {
        result.Add((ApprovalFlowTemplateMember)m);
    }
    return result;
}

// 段の同一性キー（役職段は役職、個人指名段は承認者の集合）
string StageKeyOf(List<ApprovalFlowTemplateMember> ms)
{
    if (ms.Count == 1)
    {
        var role = ms[0].ApproverRole.Value;
        if (role == "manager" || role == "director") return $"role:{role}";
    }
    var ids = new List<string>();
    foreach (var m in ms) { ids.Add($"{m.ApproverUser.Value}"); }
    ids.Sort();
    return "user:" + string.Join(",", ids);
}

// 並び順の第 2 キー（同じ order_no のときの優先度）: 課長 → 部長 → 個人指名
int StageRankOf(List<ApprovalFlowTemplateMember> ms)
{
    if (ms.Count == 1)
    {
        var role = ms[0].ApproverRole.Value;
        if (role == "manager") return 0;
        if (role == "director") return 1;
    }
    return 2;
}

// 複数テンプレートの段を合成して並べた段の一覧を返す（空なら合成できなかった）
List<ApprovalFlowTemplateOrder> BuildMergedOrders(List<object> templateIds)
{
    var keys = new List<string>();
    var orders = new List<ApprovalFlowTemplateOrder>();
    var seqs = new List<int>();
    var ranks = new List<int>();

    foreach (var tid in templateIds)
    {
        var os = new ModuleSearcher<ApprovalFlowTemplateOrder>();
        os.AddEquals(o => o.TemplateId.Value, tid);
        os.OrderBy(o => o.OrderNo.Value);
        foreach (var row in os.Execute())
        {
            var o = (ApprovalFlowTemplateOrder)row;
            var ms = MembersOf(o.Id.Value);
            if (ms.Count == 0) continue;
            var key = StageKeyOf(ms);
            var no = (int)(o.OrderNo.Value ?? 0);
            var idx = keys.IndexOf(key);
            if (idx < 0)
            {
                keys.Add(key);
                orders.Add(o);
                seqs.Add(no);
                ranks.Add(StageRankOf(ms));
            }
            else if (no > seqs[idx])
            {
                seqs[idx] = no;
            }
        }
    }

    // 選択ソート（seq → rank → key）。件数は多くても数段なので単純な実装で十分
    for (var i = 0; i < keys.Count; i++)
    {
        var best = i;
        for (var j = i + 1; j < keys.Count; j++)
        {
            var better = false;
            if (seqs[j] < seqs[best]) better = true;
            else if (seqs[j] == seqs[best])
            {
                if (ranks[j] < ranks[best]) better = true;
                else if (ranks[j] == ranks[best] && string.Compare(keys[j], keys[best]) < 0) better = true;
            }
            if (better) best = j;
        }
        if (best != i)
        {
            var tk = keys[i]; keys[i] = keys[best]; keys[best] = tk;
            var to = orders[i]; orders[i] = orders[best]; orders[best] = to;
            var ts = seqs[i]; seqs[i] = seqs[best]; seqs[best] = ts;
            var tr = ranks[i]; ranks[i] = ranks[best]; ranks[best] = tr;
        }
    }

    // 代表テンプレート（記録用）: 段数がいちばん多いもの＝合成結果にいちばん近い 1 件。
    // 合成に使った全テンプレートは申請履歴のコメントに残す（BuildTemplateNote）ので、
    // ここは「1 列にどれを書くか」だけの話であり、判定には使わない
    object bestId = null;
    var bestCount = -1;
    foreach (var tid in templateIds)
    {
        var cs = new ModuleSearcher<ApprovalFlowTemplateOrder>();
        cs.AddEquals(o => o.TemplateId.Value, tid);
        var c = cs.Execute().Count;
        if (c > bestCount) { bestCount = c; bestId = tid; }
    }
    if (bestId != null) { TemplateId.Value = bestId; }

    return orders;
}

// 合成した段の承認者集合を文字列化する（ルートが変わったかの比較に使う）
string BuildRequiredRouteKey(List<object> templateIds)
{
    var applicantId = ResolveApplicantUserId();
    var dept = FindUserDepartment(applicantId);
    var parts = new List<string>();
    List<object> prevRoleApprovers = null;
    foreach (var o in BuildMergedOrders(templateIds))
    {
        var ms = MembersOf(o.Id.Value);
        var isRole = false;
        List<object> approvers = new List<object>();
        if (ms.Count == 1)
        {
            var role0 = ms[0].ApproverRole.Value;
            if (role0 == "manager" || role0 == "director")
            {
                isRole = true;
                approvers = ResolveRoleApprovers(dept, role0, applicantId);
            }
        }
        if (!isRole)
        {
            foreach (var m in ms) { approvers.Add(ResolveFixedApproverAvoidingSelf(m.ApproverUser.Value, applicantId)); }
        }
        if (isRole && prevRoleApprovers != null && SameIdSet(prevRoleApprovers, approvers)) continue;
        if (isRole) prevRoleApprovers = approvers;
        else prevRoleApprovers = null;
        parts.Add(JoinApproverIds(approvers));
    }
    return string.Join("|", parts);
}

// 実際に生成済みの段（Orders/Members）の承認者集合を文字列化する
string BuildCurrentRouteKey()
{
    var parts = new List<string>();
    if (this.IsNewData) return "";
    var os = new ModuleSearcher<ApprovalFlowOrder>();
    os.AddEquals(o => o.ApprovalFlowId.Value, this.Id.Value);
    os.OrderBy(o => o.OrderNo.Value);
    foreach (var oRow in os.Execute())
    {
        var o = (ApprovalFlowOrder)oRow;
        var ms = new ModuleSearcher<ApprovalFlowMember>();
        ms.AddEquals(m => m.ApprovalFlowOrderId.Value, o.Id.Value);
        ms.OrderBy(m => m.Id.Value);
        var ids = new List<object>();
        foreach (var mRow in ms.Execute()) { ids.Add(((ApprovalFlowMember)mRow).ApproverUser.Value); }
        if (ids.Count == 0) continue;
        parts.Add(JoinApproverIds(ids));
    }
    return string.Join("|", parts);
}

string JoinApproverIds(List<object> ids)
{
    var s = new List<string>();
    foreach (var x in ids) { s.Add($"{x}"); }
    s.Sort();
    return string.Join(",", s);
}

// いま必要な承認ルートが、実際に承認された段構成と違うか（実費確定の再承認判定・ADR-0066）
bool IsRouteChanged(List<object> templateIds)
{
    return BuildRequiredRouteKey(templateIds) != BuildCurrentRouteKey();
}

// 申請ボタン押下時に Orders/Members を合成した段から生成する。
// UseIndexSort=true なので OrderNo は Submit 時に 0,1,... で自動採番される。
// テンプレメンバーの approver_role (manager/director) は申請者の所属部門の課長/部長へ動的解決。
// 失敗時 (テンプレ未設定・役職が解決できない) は行を作らず false を返す（中途半端な Orders を残さない）。
bool LoadFromTemplates(List<object> templateIds)
{
    return BuildOrdersFromTemplates(templateIds, false);
}

// validateOnly = true のときは行を作らず、事前パス（テンプレの構成と承認者が解決できるか）だけを走らせる。
// 「作れることを確かめてから消す」ために使う（BUG-0309）——同じ判定を 2 か所に書かないための引数である。
// オーバーロードにしないのは、CLB のスクリプトで解決されるか確証が無いため（名前を分ける方が安全）
bool BuildOrdersFromTemplates(List<object> templateIds, bool validateOnly)
{
    if (templateIds == null || templateIds.Count == 0) { Toaster.Error("承認テンプレートが未設定です"); return false; }
    var tmplOrders = BuildMergedOrders(templateIds);
    if (tmplOrders.Count == 0) { Toaster.Error("承認テンプレートに承認段階がありません"); return false; }

    var applicantId = ResolveApplicantUserId();
    var dept = FindUserDepartment(applicantId);

    // 事前パス: 全メンバーの承認者を解決できるか検証してから行を作る
    foreach (var tmplOrder in tmplOrders)
    {
        var checkSearcher = new ModuleSearcher<ApprovalFlowTemplateMember>();
        checkSearcher.AddEquals(m => m.TemplateOrderId.Value, tmplOrder.Id.Value);
        var checkMembers = checkSearcher.Execute();
        var roleMemberCount = 0;
        foreach (var tmplMember in checkMembers)
        {
            var role = tmplMember.ApproverRole.Value;
            if (role == "manager" || role == "director")
            {
                roleMemberCount++;
                var approvers = ResolveRoleApprovers(dept, role, applicantId);
                if (approvers.Count == 0)
                {
                    Toaster.Error("承認者を決定できません: 部門の課長/部長の設定、または自己承認の代替となる経理ユーザーを確認してください（自己承認は禁止です）");
                    return false;
                }
            }
            else
            {
                if (tmplMember.ApproverUser.Value == null)
                {
                    Toaster.Error("承認テンプレートに承認者未設定のメンバーがあります");
                    return false;
                }
                if (ResolveFixedApproverAvoidingSelf(tmplMember.ApproverUser.Value, applicantId) == null)
                {
                    Toaster.Error("承認者を決定できません: 自己承認の代替となる経理ユーザーを確認してください（自己承認は禁止です）");
                    return false;
                }
            }
        }
        // 役職メンバーは複数名に展開されるため、同一 Order に他メンバーと混載できない（ADR-0016 の前提）
        if (roleMemberCount > 0 && checkMembers.Count > 1)
        {
            Toaster.Error("承認テンプレート構成エラー: 役職指定の承認者は単独の承認段階にしてください");
            return false;
        }
    }

    if (validateOnly) return true;

    var first = true;
    List<object> prevRoleApprovers = null;  // 直前の役職段の承認者集合（重複段の圧縮用・ADR-0044）
    foreach (var tmplOrder in tmplOrders)
    {
        var memberSearcher = new ModuleSearcher<ApprovalFlowTemplateMember>();
        memberSearcher.AddEquals(m => m.TemplateOrderId.Value, tmplOrder.Id.Value);
        var tmplMembers = memberSearcher.Execute();

        // 役職段（事前パスで「役職は単独段」を保証済み）は先に解決し、繰上げの結果
        // 直前の役職段と同一メンバー集合になる段は生成しない（同一人物の連続2回承認を防ぐ・ADR-0044）
        var isRoleOrder = false;
        List<object> roleApprovers = null;
        object roleIsRequired = null;
        if (tmplMembers.Count == 1)
        {
            var m0 = tmplMembers[0];
            var role0 = m0.ApproverRole.Value;
            if (role0 == "manager" || role0 == "director")
            {
                isRoleOrder = true;
                roleApprovers = ResolveRoleApprovers(dept, role0, applicantId);
                roleIsRequired = m0.IsRequired.Value;
            }
        }
        if (isRoleOrder && prevRoleApprovers != null && SameIdSet(prevRoleApprovers, roleApprovers))
        {
            continue;
        }

        var newOrder = Orders.AddRow();
        newOrder.Status.Value = first ? "Active" : "Waiting";
        first = false;

        if (isRoleOrder)
        {
            // 役職の全員を同一 Order に並列展開。2名以上なら1人の承認で段完了の OR 型（ADR-0016）
            newOrder.ApprovalType.Value = roleApprovers.Count > 1 ? "any" : "all";
            foreach (var approver in roleApprovers)
            {
                var newMember = newOrder.Members.AddRow();
                newMember.IsRequired.Value = roleIsRequired;
                newMember.ApproverUser.Value = approver;
                newMember.Status.Value = "Waiting";
                newMember.ParentModuleName.Value = ParentModuleName.Value;
                newMember.ParentId.Value = ParentId.Value;
            }
            prevRoleApprovers = roleApprovers;
        }
        else
        {
            foreach (var tmplMember in tmplMembers)
            {
                var newMember = newOrder.Members.AddRow();
                newMember.IsRequired.Value = tmplMember.IsRequired.Value;
                newMember.ApproverUser.Value = ResolveFixedApproverAvoidingSelf(tmplMember.ApproverUser.Value, applicantId);
                newMember.Status.Value = "Waiting";
                newMember.ParentModuleName.Value = ParentModuleName.Value;
                newMember.ParentId.Value = ParentId.Value;
            }
            prevRoleApprovers = null;
        }
    }
    RecalculateCurrentApproverDisplay();
    return true;
}

// 承認者集合の同一判定（順序不問・ADR-0044 の重複段圧縮用）
bool SameIdSet(List<object> a, List<object> b)
{
    if (a == null || b == null) return false;
    if (a.Count != b.Count) return false;
    foreach (var x in a)
    {
        if (!ContainsId(b, x)) return false;
    }
    return true;
}

// ID の等値判定。LinkField/SelectField/IdField 由来で値の型（string/decimal）が揃わないことが
// ありうるため、文字列正規化で比較する（型不一致で != が常に true になる silent no-op を防ぐ）
bool IsSameId(object a, object b)
{
    return $"{a}" == $"{b}";
}

bool ContainsId(List<object> list, object id)
{
    foreach (var x in list)
    {
        if (IsSameId(x, id)) return true;
    }
    return false;
}

// 申請者 = 対象フローの最初の Submit の ActorUser（未申請フローなら操作者本人）。
// 実費超過の再承認 (ReapproveForOverrun) は経理等の代行操作がありうるため、
// ルート解決・自己承認除外は操作者 (CurrentUser) でなく申請者基準で行う (decisions/0010, 0016)
object ResolveApplicantUserId()
{
    if (!this.IsNewData)
    {
        var hs = new ModuleSearcher<ApprovalHistory>();
        hs.AddEquals(h => h.ApprovalFlowId.Value, this.Id.Value);
        hs.AddEquals(h => h.Action.Value, "Submit");
        var subHistory = hs.Execute();
        if (subHistory.Count > 0) return subHistory[0].ActorUser.Value;
    }
    return CurrentUser.Id.Value;
}

// 役職承認者の walk-up 解決＋自己承認禁止の一般化 (decisions/0010, 0016, 0044)
// 申請者の所属ノード（課 or 部）から親方向に辿り、最寄りの該当役職（申請者本人を除く）を返す。
// - manager（課長決裁）: 自課の課長 → 親の部の課長 → どこにも居なければ director 解決へ自動繰上げ
//   （課長空席・課長本人の申請＝自己承認回避が、特例ではなく walk-up の帰結として解決される）
// - director（部長決裁）: 最寄りの部長 → 居なければ経理代替（1名）
// - それでも決まらなければ空リスト（マスタ設定不備として呼び出し側で申請ブロック）
List<object> ResolveRoleApprovers(Department dept, string role, object applicantId)
{
    var found = WalkUpRole(dept, role, applicantId);
    if (found.Count > 0) return found;
    if (role == "manager")
    {
        found = WalkUpRole(dept, "director", applicantId);
        if (found.Count > 0) return found;
    }
    var result = new List<object>();
    var fallback = FindFallbackApprover(applicantId);
    if (fallback != null) result.Add(fallback);
    return result;
}

// 所属ノードから親方向に walk-up し、最初に該当役職（本人除外後）が見つかったノードの全員を返す（ADR-0044）
// 同一ノードに複数名いれば全員（同格の OR 承認・ADR-0016）。guard は循環参照の保険（階層は2段が正）
List<object> WalkUpRole(Department dept, string role, object applicantId)
{
    var node = dept;
    var guard = 0;
    while (node != null && guard < 5)
    {
        var candidates = ResolveDeptRoleAll(node, role);
        var filtered = new List<object>();
        foreach (var c in candidates)
        {
            if (!IsSameId(c, applicantId)) filtered.Add(c);
        }
        if (filtered.Count > 0) return filtered;
        node = FindParentDept(node);
        guard = guard + 1;
    }
    return new List<object>();
}

// 親ノード（課→部）を取得。部（parent_id なし）なら null
Department FindParentDept(Department dept)
{
    if (dept == null) return null;
    var pid = dept.ParentRef.Value;
    if (pid == null) return null;
    var ds = new ModuleSearcher<Department>();
    ds.AddEquals(d => d.Id.Value, pid);
    var rows = ds.Execute();
    if (rows.Count == 0) return null;
    return (Department)rows[0];
}

// 固定指定承認者の自己承認回避 (decisions/0010)。申請者本人・無効ユーザー(退職者)なら経理代替、解決不能なら null
object ResolveFixedApproverAvoidingSelf(object fixedApprover, object applicantId)
{
    if (fixedApprover == null) return null;
    if (!IsSameId(fixedApprover, applicantId) && IsActiveUser(fixedApprover)) return fixedApprover;
    return FindFallbackApprover(applicantId);
}

// 有効(在職)ユーザーか (Q4: 退職者は is_active=0。承認者に選ばれると承認が永久停滞するため除外する)
bool IsActiveUser(object userId)
{
    if (userId == null) return false;
    var s = new ModuleSearcher<AppUser>();
    s.AddEquals(u => u.Id.Value, userId);
    s.AddEquals(u => u.IsActive.Value, true);
    return s.Execute().Count > 0;
}

// 自己承認の代替承認者: 経理アクセスを持つユーザーのうち申請者以外で Id 最小のユーザー
object FindFallbackApprover(object applicantId)
{
    var s = new ModuleSearcher<AppUser>();
    s.AddEquals(u => u.HasAccountingAccess.Value, true);
    s.AddEquals(u => u.IsActive.Value, true);
    s.OrderBy(u => u.Id.Value);
    var users = s.Execute();
    foreach (var u in users)
    {
        var au = (AppUser)u;
        if (!IsSameId(au.Id.Value, applicantId)) return au.Id.Value;
    }
    return null;
}

// 指定ユーザーの所属部門を取得 (未設定なら null)
Department FindUserDepartment(object userId)
{
    var us = new ModuleSearcher<AppUser>();
    us.AddEquals(u => u.Id.Value, userId);
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

// 部門の役職 (manager=課長 / director=部長) の全員を department_managers から解決（ADR-0016）
// 重複登録は除去し、物理削除されたユーザーの残骸行（孤児 user_id）と無効ユーザー(退職者・Q4)は除外する
List<object> ResolveDeptRoleAll(Department dept, string role)
{
    var result = new List<object>();
    if (dept == null) return result;
    var s = new ModuleSearcher<DepartmentMember>();
    s.AddEquals(m => m.DepartmentId.Value, dept.Id.Value);
    s.AddEquals(m => m.Role.Value, role);
    s.OrderBy(m => m.Id.Value);
    var rows = s.Execute();
    foreach (var row in rows)
    {
        var userId = row.UserId.Value;
        if (userId == null) continue;
        if (ContainsId(result, userId)) continue;
        if (!IsActiveUser(userId)) continue;
        result.Add(userId);
    }
    return result;
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

        // 新規生成直後の Order は Id が @temporary のため DB 検索しない
        // （数値列に temporary Id をぶつけると FormatException。実費確定の再承認で赤トーストとして顕在化した実測 2026-07-08）
        var orderId = $"{o.Id.Value}";
        if (!orderId.StartsWith("@temporary"))
        {
            var ms = new ModuleSearcher<ApprovalFlowMember>();
            ms.AddEquals(m => m.ApprovalFlowOrderId.Value, o.Id.Value);
            ms.AddEquals(m => m.Status.Value, "Waiting");
            var dbMembers = ms.Execute();
            if (dbMembers.Count > 0)
            {
                CurrentApprover.Value = dbMembers[0].ApproverUser.Value;
                return;
            }
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
    // （フロー自身が新規のケースも同様: 複製ドラフトは親=保存済み・フロー=新規）
    if (GetParentModule().IsNewData || this.IsNewData)
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

    // 各 Order のメンバー (Order.Status は遅延ロードで取れないことがあるので「最初に Waiting を含む Order が現在の段」戦略)
    // 並列 OR 承認では現在の段の Waiting 全員に ▶ を付ける（どちらが承認してもよいことが読み取れるように）
    var currentMarked = false;
    foreach (var o in Orders.Rows)
    {
        var hasWaiting = false;
        foreach (var m in o.Members.Rows)
        {
            var ms0 = m.Status.Value;
            if (ms0 != "Approved" && ms0 != "Rejected" && ms0 != "Skipped") hasWaiting = true;
        }
        var isCurrentOrder = hasWaiting && !currentMarked;
        if (isCurrentOrder) currentMarked = true;

        var names = new List<string>();
        foreach (var m in o.Members.Rows)
        {
            var ms = m.Status.Value;
            var mark = "";
            if (ms == "Approved") mark = "✓";
            else if (ms == "Rejected") mark = "✗";
            else if (ms == "Skipped") mark = "—";
            else if (isCurrentOrder) mark = "▶";
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

// 「開く」ボタン: 各申請モジュール (LeaveRequest / ExpenseRequest) に遷移。
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
    // 申請者用モジュールには行フィルタ Creator == CurrentUser が掛かっている（ADR-0069）。
    // 自分の申請でなければ、読む側に合わせたモジュールへ写像しないと**静かに空の画面**が開く
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl(ViewerModule(parentModule), parentId));
}

// 読む側に合わせた申請モジュールを返す（ApprovalInbox.mod.cs:ToApproverModule と同じ写像に
// 経理を足したもの。申請種別が増えたら両方に足す）
string ViewerModule(string parentModuleName)
{
    if (parentModuleName != "ExpenseRequest") return parentModuleName;
    if (IsCurrentUserCreator()) return "ExpenseRequest";
    if (CurrentUser.HasAccountingAccess.Value == true) return "ExpenseRequestAccounting";
    if (CurrentUser.IsApprover.Value == true) return "ExpenseRequestApproval";
    return "ExpenseRequest";
}

// 現在ユーザーが申請者か (parent.Creator.Value は LinkField 遅延ロードで取れないため履歴ベース)
bool IsCurrentUserCreator()
{
    // フロー未保存時は履歴も無い（temporary Id を数値列に検索するとエラーになるためガード）
    if (this.IsNewData) return false;
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

// Reject/Cancel 等で残りの Order/Member を Skipped にする。
// 判定は DB を正とし（メモリ Rows の Status は遅延ロード #60 で空がありうる）、対応するメモリ行へ反映して
// 親 Submit で保存する。直前にメモリ上で Rejected/Approved にした行は上書きしない
void SkipRemainingOrdersAndMembers()
{
    if (this.IsNewData) return;
    var os = new ModuleSearcher<ApprovalFlowOrder>();
    os.AddEquals(o => o.ApprovalFlowId.Value, this.Id.Value);
    var dbOrders = os.Execute();
    foreach (var oRow in dbOrders)
    {
        var dbOrder = (ApprovalFlowOrder)oRow;
        var dbOrderStatus = dbOrder.Status.Value;
        foreach (var o in Orders.Rows)
        {
            if (!IsSameId(o.Id.Value, dbOrder.Id.Value)) continue;
            if (dbOrderStatus == "Waiting" || dbOrderStatus == "Active")
            {
                var cur = o.Status.Value;
                if (cur != "Rejected" && cur != "Approved") o.Status.Value = "Skipped";
            }
            var ms = new ModuleSearcher<ApprovalFlowMember>();
            ms.AddEquals(m => m.ApprovalFlowOrderId.Value, dbOrder.Id.Value);
            ms.AddEquals(m => m.Status.Value, "Waiting");
            var dbMembers = ms.Execute();
            foreach (var mRow in dbMembers)
            {
                var dbMember = (ApprovalFlowMember)mRow;
                foreach (var m in o.Members.Rows)
                {
                    if (!IsSameId(m.Id.Value, dbMember.Id.Value)) continue;
                    var cs = m.Status.Value;
                    if (cs != "Rejected" && cs != "Approved") m.Status.Value = "Skipped";
                    break;
                }
            }
            break;
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

// UI 側: 現在ユーザー宛の Waiting Member が「Active な Order」にあるか DB から判定
// （ADR-0016 二重承認の多層防御: OR 承認で段が完了した後の残メンバーには承認させない）
bool HasWaitingMemberForCurrentUser()
{
    var parent = GetParentModule();
    if (parent.IsNewData || this.IsNewData) return false;
    var os = new ModuleSearcher<ApprovalFlowOrder>();
    os.AddEquals(o => o.ApprovalFlowId.Value, this.Id.Value);
    os.AddEquals(o => o.Status.Value, "Active");
    var activeOrders = os.Execute();
    foreach (var oRow in activeOrders)
    {
        var o = (ApprovalFlowOrder)oRow;
        var s = new ModuleSearcher<ApprovalFlowMember>();
        s.AddEquals(m => m.ApprovalFlowOrderId.Value, o.Id.Value);
        s.AddEquals(m => m.ApproverUser.Value, CurrentUser.Id.Value);
        s.AddEquals(m => m.Status.Value, "Waiting");
        if (s.Execute().Count > 0) return true;
    }
    return false;
}

// Approve/Reject 実行時: DB の Active Order から自分宛の Waiting Member を検索 + 対応するメモリ Member を返す
// （Active でない Order のメンバーは対象外 = 完了済みの段への stale な承認操作を弾く）
ApprovalFlowMember GetCurrentMemberForUserStrict()
{
    if (this.IsNewData) return null;
    var os = new ModuleSearcher<ApprovalFlowOrder>();
    os.AddEquals(o => o.ApprovalFlowId.Value, this.Id.Value);
    os.AddEquals(o => o.Status.Value, "Active");
    var activeOrders = os.Execute();
    foreach (var oRow in activeOrders)
    {
        var dbOrder = (ApprovalFlowOrder)oRow;
        var s = new ModuleSearcher<ApprovalFlowMember>();
        s.AddEquals(m => m.ApprovalFlowOrderId.Value, dbOrder.Id.Value);
        s.AddEquals(m => m.ApproverUser.Value, CurrentUser.Id.Value);
        s.AddEquals(m => m.Status.Value, "Waiting");
        var members = s.Execute();
        if (members.Count == 0) continue;
        var dbMember = members[0];
        foreach (var o in Orders.Rows)
        {
            if (!IsSameId(o.Id.Value, dbOrder.Id.Value)) continue;
            foreach (var m in o.Members.Rows)
            {
                if (IsSameId(m.Id.Value, dbMember.Id.Value)) return m;
            }
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

    // 申請ボタン: フローが未開始なら表示（通常の新規申請＝親新規／複製ドラフト＝Status "Draft" の既存行）
    SubmitButton.IsVisible   = isNewParent || this.IsNewData || s == "Draft";
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
    // 「初回申請」の判定はフロー自身の状態で行う（親の新規性ではなく）。
    // 通常の新規申請＝フロー新規／「この申請を複製」の下書き＝Status "Draft" の既存行（2026-07-08）
    var wasNew = this.IsNewData || Status.Value == "Draft";

    if (wasNew)
    {
        var valid = parent.ValidateForApply();
        if (valid != true) return;
    }

    using var suspend = parent.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    if (wasNew)
    {
        // 下書き（新規・複製とも Status "Draft"）: 申請に向けて Pending 化する
        if (Status.Value == null || Status.Value == "" || Status.Value == "Draft")
        {
            Status.Value = "Pending";
            if (AttemptNo.Value == null) { AttemptNo.Value = 1; }
            if (ParentModuleName.Value == null || ParentModuleName.Value == "") { ParentModuleName.Value = "ExpenseRequest"; }
            if (ParentId.Value == null || ParentId.Value == "") { ParentId.Value = $"{parent.Id.Value}"; }
        }

        // 申請時点の明細でテンプレートを解決する（行ごとに解決した結果が複数返る・ADR-0066）
        var tmplIds = parent.SelectTemplateIds();
        if (tmplIds.Count == 0) return;   // 解決できない理由は親側でトースト済み

        if (!LoadFromTemplates(tmplIds)) return;
        AddHistory("Submit", BuildTemplateNote(tmplIds));
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
// 送信の実体は基盤の Notification.Send() に一元化（ADR-0045。Slack/メール mock・
// 他人宛 Submit 戻り値の吸収も Send 側）。支払期限リマインドは見送り（売掛残高一覧の期限超過表示で代替）。
// ============================================================
void NotifyUser(object recipientUserId, string title, string body, string linkModule, string linkId)
{
    new Notification().Send(recipientUserId, title, body, linkModule, linkId);
}

// 申請モジュール名の表示名 (通知文言用)
string ModuleDisplayName(string moduleName)
{
    if (moduleName == "ExpenseRequest") return "経費申請";
    return moduleName;
}

// 通知文言用: 対象申請の件名・金額まで解決する（「どの申請か」が通知一覧だけで分かるように）
// 現状の対象は ExpenseRequest のみ。他モジュールをフローに載せたらここに分岐を足す
string TargetSummary(ApprovalFlow flow)
{
    if (flow == null) return "申請";
    var moduleName = flow.ParentModuleName.Value;
    var disp = ModuleDisplayName(moduleName);
    if (moduleName == "ExpenseRequest" && flow.ParentId.Value != null)
    {
        // 読む側によってモジュールを変える（ADR-0069）。申請者用 ExpenseRequest には
        // 行フィルタ Creator == CurrentUser が掛かるので、承認者がこれを検索すると
        // **エラーにならず 0 件**になり、通知の文面から件名と金額が静かに落ちる。
        // 承認者は承認者用モジュール（同じ expense_request テーブル・人ゲートは IsApprover）から読む
        var viewer = ViewerModule(moduleName);
        if (viewer == "ExpenseRequest")
        {
            var s = new ModuleSearcher<ExpenseRequest>();
            s.AddEquals(e => e.Id.Value, flow.ParentId.Value);
            var found = s.ExecuteFirstOrDefault();
            if (found != null)
            {
                var er = (ExpenseRequest)found;
                return FormatTarget(disp, er.Title.Value, er.Amount.Value);
            }
        }
        else if (viewer == "ExpenseRequestAccounting")
        {
            // 経理が超過再承認を起こす経路がある。経理は IsApprover を持たないことがあるので、
            // 承認者用ではなく経理用から読む（そうしないと通知の文面から件名と金額が静かに落ちる）
            var s = new ModuleSearcher<ExpenseRequestAccounting>();
            s.AddEquals(e => e.Id.Value, flow.ParentId.Value);
            var found = s.ExecuteFirstOrDefault();
            if (found != null)
            {
                var er = (ExpenseRequestAccounting)found;
                return FormatTarget(disp, er.Title.Value, er.Amount.Value);
            }
        }
        else
        {
            var s = new ModuleSearcher<ExpenseRequestApproval>();
            s.AddEquals(e => e.Id.Value, flow.ParentId.Value);
            var found = s.ExecuteFirstOrDefault();
            if (found != null)
            {
                var er = (ExpenseRequestApproval)found;
                return FormatTarget(disp, er.Title.Value, er.Amount.Value);
            }
        }
    }
    return disp;
}

// 承認者あての導線は承認者用モジュールへ写像する（ApprovalInbox.mod.cs:ToApproverModule と同じ写像。
// 申請種別が増えたら両方に足す）。Notification.ResolveLinkUrl がこの名前を URL に解決する
string ToApproverModule(string parentModuleName)
{
    if (parentModuleName == "ExpenseRequest") return "ExpenseRequestApproval";
    return parentModuleName;
}

string FormatTarget(string disp, string title, int? amount)
{
    var t = title ?? "";
    if (amount != null) { return $"{disp}「{t}」（{amount:#,0}円）"; }
    return $"{disp}「{t}」";
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
    // 通知のリンク先は「誰に送る通知か」で決める。これは承認者あての通知なので承認者用モジュールを指す
    // （parent_module_name は DB に保存された申請者側の名前＝常に "ExpenseRequest"）。
    // 受信者の権限で当てにいくと、承認者かつ申請者である課長が自分の申請の通知を開いたときに
    // 承認者用の画面へ飛んでしまう。送り手は宛先を知っているのだから、送り手が決めるのが素直
    var linkModule = ToApproverModule(flow.ParentModuleName.Value);
    var linkId = $"{flow.ParentId.Value}";
    var body = $"{TargetSummary(flow)}の承認をお願いします";
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
// 承認・却下・キャンセルは取り消す導線が無い（＝不可逆）ので確認する（ADR-0062）。
// LoadingService はダイアログより後に開始する（オーバーレイがダイアログを覆う既知の罠）
void Approve_OnClick()
{
    var answer = MessageBox.Show("この申請を承認します。承認を取り消すことはできません。よろしいですか？",
        "承認する", "キャンセル");
    if (answer != "承認する") return;

    using var suspend = GetParentModule().SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var member = GetCurrentMemberForUserStrict();
    if (member == null) { Toaster.Error("承認権限がありません"); return; }

    member.Status.Value = "Approved";
    member.ActorUser.Value = CurrentUser.Id.Value;
    member.ApprovedAt.Value = DateTime.Now;

    var order = GetOrderOfMember(member);
    if (order != null && IsOrderCompleted(order, member.Id.Value))
    {
        order.Status.Value = "Approved";
        SkipRemainingWaitingMembers(order, member.Id.Value);
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
            NotifyCreator(flow, "承認されました", $"{TargetSummary(flow)}が最終承認されました");
        }
        else
        {
            NotifyActiveApprovers(flow, "承認依頼");
        }
    }
}

// メンバーが属するメモリ上の Order を Member.Id の突き合わせで特定する
// （member.ApprovalFlowOrderId は LinkField で遅延ロード #60 により空がありうるため使わない）
ApprovalFlowOrder GetOrderOfMember(ApprovalFlowMember member)
{
    foreach (var o in Orders.Rows)
    {
        foreach (var m in o.Members.Rows)
        {
            if (IsSameId(m.Id.Value, member.Id.Value)) return o;
        }
    }
    return null;
}

// Order の承認完了判定（ADR-0016: DB ベース。メモリ Rows の遅延ロード #60 を信用しない）
// approval_type='any' なら誰か1人 Approved で完了（複数課長/部長の並列 OR 承認）。
// 'all'/NULL は従来どおり: 必須メンバー全員 Approved、または必須ゼロで誰か1人 Approved。
// justApprovedMemberId: 直前にメモリ上で Approved にした Member（この時点で DB 未反映）を Approved とみなす
bool IsOrderCompleted(ApprovalFlowOrder order, object justApprovedMemberId)
{
    // approval_type も遅延ロードがありうるため DB から解決する
    var os = new ModuleSearcher<ApprovalFlowOrder>();
    os.AddEquals(o => o.Id.Value, order.Id.Value);
    var dbOrders = os.Execute();
    var approvalType = dbOrders.Count > 0 ? dbOrders[0].ApprovalType.Value : null;

    var ms = new ModuleSearcher<ApprovalFlowMember>();
    ms.AddEquals(m => m.ApprovalFlowOrderId.Value, order.Id.Value);
    var members = ms.Execute();

    int requiredCount = 0, requiredApproved = 0, anyApproved = 0;
    foreach (var mRow in members)
    {
        var m = (ApprovalFlowMember)mRow;
        var status = m.Status.Value;
        if (IsSameId(m.Id.Value, justApprovedMemberId)) status = "Approved";
        if (status == "Approved") anyApproved++;
        if (m.IsRequired.Value == true)
        {
            requiredCount++;
            if (status == "Approved") requiredApproved++;
        }
    }
    if (approvalType == "any") return anyApproved >= 1;
    if (requiredCount > 0) return requiredApproved == requiredCount;
    return anyApproved >= 1;
}

// OR 承認で段が完了したら、同一 Order の残り Waiting メンバーを Skipped にする（二重承認防止・履歴の可読性）
// DB の Waiting メンバーを対応するメモリ行に反映して Submit で保存する
void SkipRemainingWaitingMembers(ApprovalFlowOrder order, object approvedMemberId)
{
    var s = new ModuleSearcher<ApprovalFlowMember>();
    s.AddEquals(m => m.ApprovalFlowOrderId.Value, order.Id.Value);
    s.AddEquals(m => m.Status.Value, "Waiting");
    var dbMembers = s.Execute();
    foreach (var dbRow in dbMembers)
    {
        var dbMember = (ApprovalFlowMember)dbRow;
        if (IsSameId(dbMember.Id.Value, approvedMemberId)) continue;
        foreach (var m in order.Members.Rows)
        {
            if (IsSameId(m.Id.Value, dbMember.Id.Value))
            {
                m.Status.Value = "Skipped";
                break;
            }
        }
    }
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
    var answer = MessageBox.Show("この申請を却下します。却下を取り消すことはできません（申請者は作り直しになります）。よろしいですか？",
        "却下する", "キャンセル");
    if (answer != "却下する") return;

    using var suspend = GetParentModule().SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var member = GetCurrentMemberForUserStrict();
    if (member == null) { Toaster.Error("却下権限がありません"); return; }

    member.Status.Value = "Rejected";
    member.ActorUser.Value = CurrentUser.Id.Value;
    member.ApprovedAt.Value = DateTime.Now;

    var order = GetOrderOfMember(member);
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
        var selfFlow = FetchSelfFromDb();
        NotifyCreator(selfFlow, "却下されました", $"{TargetSummary(selfFlow)}が却下されました（コメント: {rejectComment}）");
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

    // **消す前に、作れることを確かめる**（BUG-0309）。
    // 先に全段を消してから失敗すると、削除と Status="Pending" がメモリに残り、
    // その後の親 Submit（明細の追加など）が**段 0 件の「進行中」フロー**を永続化してしまう。
    // そうなると申請ボタンも承認者も出ず、画面からは復旧できない
    var tmplIds = parent.SelectTemplateIds();
    if (tmplIds.Count == 0) return false;   // 解決できない理由は親側でトースト済み
    if (!BuildOrdersFromTemplates(tmplIds, true)) return false;   // 事前パスだけ走らせる（行は作らない）

    var rowsToDelete = new List<ApprovalFlowOrder>();
    foreach (var o in Orders.Rows) rowsToDelete.Add(o);
    foreach (var o in rowsToDelete) Orders.DeleteRow(o);

    return BuildOrdersFromTemplates(tmplIds, false);
}

// 監査用: 合成に使ったテンプレートを履歴コメントに残す
// （承認フローの template_id には代表 1 件しか入らないため、実際の根拠をここで保全する）
string BuildTemplateNote(List<object> templateIds)
{
    var names = new List<string>();
    foreach (var tid in templateIds)
    {
        var s = new ModuleSearcher<ApprovalFlowTemplate>();
        s.AddEquals(t => t.Id.Value, tid);
        var found = s.ExecuteFirstOrDefault();
        if (found != null) names.Add($"{((ApprovalFlowTemplate)found).Name.Value}");
    }
    if (names.Count == 0) return "";
    if (names.Count == 1) return $"承認ルート: {names[0]}";
    return $"承認ルート（合成）: {string.Join(" ＋ ", names)}";
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
    var answer = MessageBox.Show("この申請を取り下げます。取り下げると元には戻せません。よろしいですか？",
        "取り下げる", "キャンセル");
    if (answer != "取り下げる") return;

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
