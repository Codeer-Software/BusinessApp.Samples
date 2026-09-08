namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 仕訳を計上できるかを検査する（docs/10 §1）。
/// </summary>
/// <remarks>
/// <para>純粋関数であり、副作用も外部依存も持たない。<b>同じ実装をクライアントの即時
/// フィードバックとサーバの関門の両方で走らせる</b>ためである（ADR-0008）。</para>
/// <para>見つかった違反を<b>すべて</b>返す。最初の 1 件で打ち切ると、利用者は直しては弾かれを繰り返す。</para>
/// <para><b>戻り値が空かどうかで判定しない。</b> 警告は返るが計上はできる。
/// 計上の可否は <see cref="ViolationEnumerableExtensions.HasError"/> で判定する。</para>
/// </remarks>
public static class JournalEntryValidator
{
    public static IReadOnlyList<Violation> ValidateForPosting(JournalEntry entry, PostingContext context)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(context);

        var violations = new List<Violation>();
        ValidateState(entry, violations);
        ValidateDescription(entry, violations);
        ValidateStructure(entry, violations);
        ValidateDates(entry, context.Calendar, violations);
        ValidateLines(entry, context, violations);
        return violations;
    }

    /// <summary>
    /// 計上できる状態か（I-05）。計上済みの伝票をもう一度通せると、
    /// 呼び出し側が「検証が通った＝保存してよい」と解釈して二重計上の経路になる。
    /// </summary>
    private static void ValidateState(JournalEntry entry, List<Violation> violations)
    {
        if (entry.Status == EntryStatus.Posted)
        {
            violations.Add(new Violation(
                JournalViolationCodes.AlreadyPosted,
                "この伝票は計上済みです。訂正・取消は反対仕訳で行います。"));
        }

        if (entry.EntryNo is not null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.EntryNoNotAllowed,
                "伝票番号は計上のときに自動で付きます。計上前の伝票に番号があってはいけません。"));
        }
    }

    /// <summary>
    /// 摘要が入っていること（docs/10 §4-2-1）。
    /// </summary>
    /// <remarks>
    /// <para><b>仕訳帳の法定記載事項「内容」は、伝票の摘要が持つ</b>（法税規則 55 ①。
    /// 明細の「内容」は消税法 30 ⑧③の「資産又は役務の内容」で別の欄である）。
    /// <b>下書き保存では求めない</b>（開発者の決定。2026-09-05）——条文が求めるのは帳簿への記載で、
    /// 計上を止めれば記載事項を欠いた行は帳簿に載らない。</para>
    /// <para><b>空白だけも「入っていない」とみなす。</b> 全角空白・タブも同じで、
    /// <see cref="string.IsNullOrWhiteSpace"/> がそこまで見る。
    /// <b>DDL のトリガも同じ字を落として比べる</b>（最後の守り。ddl/005_journals.sql）。</para>
    /// <para><b>ここは <see cref="ValidateStructure"/> の外に置く。</b> あちらは明細が 0 行なら
    /// 早く返るので、中に入れると<b>明細を入れ忘れた伝票で摘要の違反が出ない</b>。
    /// 直すところが 2 つあるなら 2 つとも見せる。</para>
    /// <para><b>「内容」の語を使わない。</b> 同じ画面に明細の「内容」欄があり、
    /// そこへ書けと言う注記まで出ているので、「取引の内容を書いてください」は
    /// <b>別の欄を指して読める</b>（2026-09-08 の自己レビュー）。</para>
    /// <para><b>文に「計上できません」を入れない。</b> 計上の違反は
    /// <c>JournalPostingRejectedException</c> が「計上できません（n 件）。」という見出しに束ねるので、
    /// 各文が結果を繰り返すと <b>1 つの断りに「計上できません」が 2 回出る</b>（実機で見た。2026-09-08）。
    /// 束ねる関門では、各文は<b>事実と次の一手</b>だけを持つ（docs/21 §2-6）。</para>
    /// </remarks>
    private static void ValidateDescription(JournalEntry entry, List<Violation> violations)
    {
        if (string.IsNullOrWhiteSpace(entry.Description))
        {
            violations.Add(new Violation(
                JournalViolationCodes.DescriptionMissing,
                "「摘要」が入っていません。何の取引かを書いてください。"));
        }
    }

    private static void ValidateStructure(JournalEntry entry, List<Violation> violations)
    {
        if (entry.Lines.Count == 0)
        {
            violations.Add(new Violation(JournalViolationCodes.NoLines, "明細が 1 行もありません。"));
            return;
        }

        // 借方だけ・貸方だけの伝票は、金額が正であること（E-AMOUNT）と
        // 貸借一致（I-01）の組み合わせで塞がっている。意図はテストで固定してある。
        if (!entry.IsBalanced)
        {
            violations.Add(new Violation(
                JournalViolationCodes.Unbalanced,
                $"借方合計 {entry.DebitTotal} 円と貸方合計 {entry.CreditTotal} 円が一致していません。"));
        }

        foreach (var lineNo in entry.Lines.GroupBy(l => l.LineNo).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            violations.Add(new Violation(
                JournalViolationCodes.LineNoInvalid, JournalLineRules.LineNoDuplicated, lineNo));
        }

        // **行番号を添えない。** 添えると「0 行目: 行番号が正しくありません」と、
        // 存在しない行を名指しすることになる（負の値なら「-1 行目」）。
        // 行を特定する手段がその行番号そのものなので、壊れているときは指せない。
        if (entry.Lines.Any(l => l.LineNo <= 0))
        {
            violations.Add(new Violation(
                JournalViolationCodes.LineNoInvalid, JournalLineRules.LineNoNotStorable));
        }

        if (entry.EntryType.RequiresOriginalEntry() && entry.OriginalEntryId is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.OriginalEntryMissing,
                "訂正・取消の伝票には、元の伝票の指定が必要です。"));
        }
    }

    private static void ValidateDates(JournalEntry entry, FiscalCalendar calendar, List<Violation> violations)
    {
        // 取引が起きる前に帳簿へ載せることはできない。
        // 逆に取引日が過年度であることは正常なので検査しない（遅れて起票した取引を当期に計上する）。
        if (entry.PostingDate < entry.TransactionDate)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PostingDateBeforeTransaction,
                $"計上日（{entry.PostingDate:yyyy/MM/dd}）が取引日（{entry.TransactionDate:yyyy/MM/dd}）より前になっています。"));
        }

        var period = calendar.ResolvePeriod(entry.PostingDate);
        if (period is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodNotFound,
                $"計上日（{entry.PostingDate:yyyy/MM/dd}）に対応する会計期間がありません。"));
            return;
        }

        if (period.Status == PeriodStatus.Closed)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodClosed,
                $"会計期間 {period.Period} は締め済みです。計上日を開いている期間に直してください。"));
            return;
        }

        var fiscalYear = calendar.FindFiscalYear(period.FiscalYearId);
        if (fiscalYear is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodOrphaned,
                $"会計期間 {period.Period} が属する会計年度がありません。マスタの設定を確認してください。"));
            return;
        }

        if (fiscalYear.Status == PeriodStatus.Closed)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodClosed,
                $"会計年度「{fiscalYear.Label}」は締め済みです。計上日を開いている年度に直してください。"));
            return;
        }

        // 伝票が持つ会計年度と、計上日から引いた会計年度が違うと、
        // 別の年度の番号列から伝票番号が出る（I-17 の一連番号が壊れる）。
        if (entry.FiscalYearId != period.FiscalYearId)
        {
            violations.Add(new Violation(
                JournalViolationCodes.FiscalYearMismatch,
                $"伝票の会計年度が、計上日（{entry.PostingDate:yyyy/MM/dd}）の属する「{fiscalYear.Label}」と食い違っています。"));
        }
    }

    private static void ValidateLines(JournalEntry entry, PostingContext context, List<Violation> violations)
    {
        foreach (var line in entry.Lines)
        {
            if (!line.Amount.IsPositive)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AmountNotPositive, JournalLineRules.AmountNotPositive, line.LineNo));
            }

            // 既定値のまま（未設定）の税区分を通さない。NULL と「対象外」を 2 通りで表さない（docs/11 §1）。
            if (line.TaxCategoryId == default)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.TaxCategoryMissing, JournalLineRules.TaxCategoryMissing, line.LineNo));
            }

            ValidateTaxLine(line, entry, violations);
            ValidateDepartment(line, entry, context.Departments, violations);

            var account = context.Accounts.Find(line.AccountId);
            if (account is null)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AccountUnknown,
                    "勘定科目が勘定科目マスタにありません。",
                    line.LineNo));
                continue;
            }

            if (!account.IsActive)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AccountInactive,
                    $"勘定科目「{account.Name}」は無効なので、新しい計上には使えません。",
                    line.LineNo,
                    InactiveSeverity(entry)));
            }

            if (account.Category.IsProfitAndLoss() && line.DepartmentId is null)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.DepartmentMissing,
                    $"損益科目「{account.Name}」の行には部門が必要です。",
                    line.LineNo));
            }

            ValidateSubAccount(line, entry, account, context.SubAccounts, violations);
        }
    }

    private static void ValidateDepartment(
        JournalLine line, JournalEntry entry, DepartmentCatalog departments, List<Violation> violations)
    {
        if (line.DepartmentId is not DepartmentId departmentId)
        {
            return;
        }

        var department = departments.Find(departmentId);
        if (department is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.DepartmentUnknown,
                "部門が部門マスタにありません。",
                line.LineNo));
            return;
        }

        if (!department.IsActive)
        {
            violations.Add(new Violation(
                JournalViolationCodes.DepartmentInactive,
                $"部門「{department.Name}」は無効なので、新しい計上には使えません。",
                line.LineNo,
                    InactiveSeverity(entry)));
        }
    }

    private static void ValidateSubAccount(
        JournalLine line, JournalEntry entry, AccountDefinition account, SubAccountCatalog subAccounts,
        List<Violation> violations)
    {
        if (line.SubAccountId is not SubAccountId subAccountId)
        {
            if (account.UsesSubAccount)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.SubAccountRequired,
                    $"勘定科目「{account.Name}」は補助科目を使います。補助科目を選んでください。",
                    line.LineNo,
                    ReversalOnlySeverity(entry)));
            }
            return;
        }

        // **実在は先に見る。** ここを 2 値の検査より後ろに置くと、取消では 2 値が警告なので
        // **マスタに無い補助科目が関門を素通りし、DB の外部キーで落ちる**（qa/03 L-14 の型）。
        var subAccount = subAccounts.Find(subAccountId);
        if (subAccount is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountUnknown,
                "補助科目が補助科目マスタにありません。",
                line.LineNo));
            return;
        }

        // **補助科目は 2 値である**（ADR-0038 §3）。使わない科目は補助科目を持てない——
        // 持てると「全補助科目の合計＝科目の残高」が崩れ、補助元帳に載らない残高ができる。
        // **親の一致より先に見る。** どちらも「その補助科目でよいか」の話だが、
        // ここで断られる利用者に必要なのは「この科目では補助科目を使わない」であって、
        // 別の補助科目を選び直せという案内ではない。
        if (!account.UsesSubAccount)
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountNotAllowed,
                $"勘定科目「{account.Name}」は補助科目を使いません。補助科目を空にしてください。",
                line.LineNo,
                ReversalOnlySeverity(entry)));
            return;
        }

        // 「現金の補助科目を普通預金の明細に付ける」を塞ぐ。
        // 補助元帳は補助科目が正しい親に属していることを前提にする。
        if (subAccount.AccountId != account.Id)
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountMismatch,
                $"補助科目「{subAccount.Name}」は勘定科目「{account.Name}」のものではありません。",
                line.LineNo));
            return;
        }

        if (!subAccount.IsActive)
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountInactive,
                $"補助科目「{subAccount.Name}」は無効なので、新しい計上には使えません。",
                line.LineNo,
                    InactiveSeverity(entry)));
        }
    }

    /// <summary>
    /// 無効にしたマスタを使っていることの重さ。
    /// </summary>
    /// <remarks>
    /// <para><b>取消と訂正では止めない。</b> 新たな計上には使えないが、どちらも
    /// 「過去に計上したものを打ち消す・直す」操作なので、後からマスタを無効にしたせいで
    /// <b>訂正も取消もできない仕訳が帳簿に残る</b>という最悪の状態を作ってはいけない
    /// （docs/10 §6・ADR-0004）。</para>
    /// <para><b>訂正を含めるのは 2026-08-25 の自己レビューで直した。</b> 訂正は取消を先に計上してから
    /// 再計上の下書きを開く（ADR-0015）。ここが Error のままだと、原仕訳が無効なマスタを使っていた場合に
    /// <b>取消だけが確定して再計上は永久に計上できない</b>——利用者から見れば、訂正しようとしたら
    /// 取り消されただけで詰む。ADR-0015 の「誤って取り消したときの復旧」も同じ理由で塞がれていた。</para>
    /// <para><b>補助科目の 2 値（ADR-0038 §3）には、この重さを使わない。</b>
    /// あちらは<b>取消だけ</b>を外す（<see cref="ReversalOnlySeverity"/>）——
    /// 訂正の再計上は利用者が補助科目を空にできるので、止めても行き止まりにならない。</para>
    /// </remarks>
    private static ViolationSeverity InactiveSeverity(JournalEntry entry)
        => entry.EntryType is EntryType.Reversal or EntryType.Correction
            ? ViolationSeverity.Warning
            : ViolationSeverity.Error;

    /// <summary>
    /// <b>取消でだけ止めない</b>ことの重さ（補助科目の 2 値。ADR-0038 §3）。
    /// </summary>
    /// <remarks>
    /// <para><b>外すのは取消だけである。</b> 取消の明細はサーバが原仕訳を反転して作り
    /// （<c>JournalReversalPosting</c>）、<b>利用者が直す手立てが無い</b>。
    /// Error にすると、規則より前に計上された伝票を<b>取り消せなくなる</b>——
    /// 開発機に「補助科目を使わない科目に補助科目が付いた計上済み明細」が 1 行ある
    /// （2026-09-08 実測。数え方は qa/04）。</para>
    /// <para><b>訂正（再計上）は外さない。</b> 再計上の中身は利用者が決め、サーバは一切書き換えない
    /// （<c>JournalCorrectionPosting</c> の注記）ので、<b>補助科目を空にすれば通る</b>——
    /// 行き止まりにならない。外すと、訂正を経由して<b>規則より後の違反を新しく帳簿へ入れられる</b>
    /// （自己レビューで見つけた。2026-09-08）。</para>
    /// <para><b>「要る」側にも同じ線を引く。</b> 使う科目に変えられた後の過去の明細は
    /// 補助科目を持たないので、Error のままだと同じく取り消せなくなる
    /// （開発機では 0 行。2026-09-08 実測）。</para>
    /// <para><b>DDL のトリガも同じ広さにしてある</b>（docs/10 §4-2-1 の二層の広さ。
    /// <c>trg_journal_entries_sub_account_presence_when_posted</c> が
    /// <c>entry_type &lt;&gt; 'reversal'</c> で同じ線を引く）。</para>
    /// </remarks>
    private static ViolationSeverity ReversalOnlySeverity(JournalEntry entry)
        => entry.EntryType is EntryType.Reversal ? ViolationSeverity.Warning : ViolationSeverity.Error;

    private static void ValidateTaxLine(JournalLine line, JournalEntry entry, List<Violation> violations)
    {
        if (!line.IsTaxLine)
        {
            if (line.ParentLineNo is not null)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.TaxLineParentInvalid,
                    "本体行には親行を指定できません。",
                    line.LineNo));
            }
            return;
        }

        var parent = entry.Lines.FirstOrDefault(l => l.LineNo == line.ParentLineNo);
        if (parent is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineParentInvalid,
                "消費税行は、同じ伝票にある本体行を指してください。",
                line.LineNo));
            return;
        }

        if (parent.IsTaxLine)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineParentInvalid,
                "消費税行が別の消費税行を親に指しています。",
                line.LineNo));
            return;
        }

        // 消費税行は本体行から貸借・部門・税区分・用途区分を引き継ぐ（docs/11 §2）。
        // 引き継がないと、税区分別集計・部門別税集計が本体行と突き合わなくなる。
        if (line.DebitCredit != parent.DebitCredit)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の借方貸方は、本体行と同じにしてください。",
                line.LineNo));
        }

        if (line.DepartmentId != parent.DepartmentId)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の部門は、本体行と同じにしてください。",
                line.LineNo));
        }

        if (line.TaxCategoryId != parent.TaxCategoryId)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の税区分は、本体行と同じにしてください。",
                line.LineNo));
        }

        if (line.TaxTreatment != parent.TaxTreatment)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の用途区分は、本体行と同じにしてください。",
                line.LineNo));
        }
    }
}
