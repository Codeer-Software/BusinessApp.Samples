namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.Partners;

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
        ValidateItemDescriptions(entry, violations);
        ValidateStructure(entry, violations);
        ValidateDates(entry, context.Calendar, violations);
        ValidatePartners(entry, context.Partners, violations);
        ValidateLines(entry, context, violations);
        return violations;
    }

    /// <summary>
    /// 伝票と明細が指す取引先が、マスタに実在し、有効か。
    /// </summary>
    /// <remarks>
    /// <para><b>科目・補助科目・部門と同じ形</b>（<c>E-*-UNKNOWN</c> / <c>E-*-INACTIVE</c>）。取引先だけ見ていなかった——
    /// マスタに無い識別子は DB の外部キーの生の失敗になり、無効にした取引先も新たな計上に使えた（qa/03 L-14 の型。2026-09-10）。</para>
    /// <para><b>伝票の取引先は伝票として、明細の取引先は行として見る。</b> 明細が空なら伝票の値が実効値になる
    /// （<see cref="JournalEntry.PartnerOf"/>）が、<b>実効値では見ない</b>——
    /// 断りは<b>利用者が触れる欄</b>を指す必要があり、実効値で見ると「伝票の欄を直す」と「行の欄を直す」が混ざる。
    /// <b>同じ取引先が何か所にあっても断りは 1 件</b>にまとめ、<b>場所は文に並べる</b>（docs/21 §2-6）。</para>
    /// <para><b>重さは、利用者が直せるかで決める</b>（<see cref="ReversalOnlySeverity"/> の注記と同じ線）。
    /// <b>伝票の取引先も明細の取引先も、訂正の下書きで選び直せる</b>ので、外すのは取消だけ（<see cref="ReversalOnlySeverity"/>）——
    /// 訂正でも外すと、無効にした相手の新しい記帳を訂正経由で帳簿へ入れられる。
    /// <b>明細の取引先も、訂正の下書きで選び直せる</b>——明細の一覧に取引先の列があるからである（ADR-0062）。
    /// <b>行き止まりにはならない</b>——利用者は下書きで選び直すか、空にできる。
    /// <b>ただし消費税行はシステムが作り、利用者は直接編集できない</b>（docs/11 §2。実装はフェーズ 3）ので、
    /// <b>税行を作る回にこの重さを見直す</b>（ADR-0062 の帰結 2）。</para>
    /// <para><b>「マスタに無い」も同じ重さで扱う。</b> DDL の取引先のトリガは、取消の明細が計上済みの原仕訳の写しなら
    /// 取引先が <c>partners</c> に無くても通す（<c>trg_journal_entries_partner_presence_when_posted</c>）ので、関門も同じ広さにする。
    /// <b>アプリの経路では外部キーが先に止める</b>（取消は原仕訳の取引先を写して INSERT するので、マスタに無い取引先は
    /// 検証に届く前に落ちる）——この重さが効くのは外部キーを切った経路だけで、関門の重さで取り消せなくなる伝票を作らないための整合である。</para>
    /// <para><b>警告は、いまはまだどこにも届かない</b>（計上の側は Error だけを読む）。「警告に落とす」は「止めない」の意味である。
    /// <b>取消の分は届け先が決まった</b>——取消の応答に載せて黄色のトーストにする（ADR-0061。実装はフェーズ 4）。
    /// <b>訂正の再計上の分はまだ決まっていない</b>（docs/04 §5）。</para>
    /// </remarks>
    private static void ValidatePartners(JournalEntry entry, PartnerCatalog partners, List<Violation> violations)
    {
        // **同じ直し先の断りを、行数だけ並べない**（規則は docs/21 §2-6。理由もそちらが持つ）。
        // **見つけたのは 2026-09-16 の自己レビュー**（qa/02 のラウンド 109。直したのはラウンド 118）。
        //
        // ここが持つのは実装の事情だけである——
        // **まとめる鍵は取引先の識別子**（違反の種類ではない。2 社が無効なら 2 件出る）。
        // **場所は伝票が先、次に行番号の順**（ドメインの型は並びを持たないので、ここで決める）。
        //
        // **場所を集めるのと、断るのを、同じ並びで 2 度回す**——
        // **重ねて断らないのは控えの側の仕事**（`SayOnce`）なので、ここでは素直に全部を回す。
        var used = new List<(PartnerId PartnerId, int? LineNo)>();
        if (entry.PartnerId is PartnerId entryPartner)
        {
            used.Add((entryPartner, null));
        }

        used.AddRange(entry.Lines
            .Where(l => l.PartnerId is not null)
            .OrderBy(l => l.LineNo)
            .Select(l => (l.PartnerId!.Value, PlaceOf(l))));

        var places = new RejectionPlaces<PartnerId>();
        foreach (var (partnerId, lineNo) in used)
        {
            places.Note(partnerId, lineNo);
        }

        foreach (var (partnerId, _) in used)
        {
            ValidatePartner(partnerId, places, entry, partners, violations);
        }
    }

    private static void ValidatePartner(
        PartnerId partnerId, RejectionPlaces<PartnerId> places, JournalEntry entry, PartnerCatalog partners,
        List<Violation> violations)
    {
        // 伝票の取引先も明細の取引先も、訂正の下書きで選び直せる（明細の列は 2026-09-16 に足した）。
        var severity = ReversalOnlySeverity(entry);

        // **次の一手まで言う**（docs/21 §2-3）。読み手の経理担当は取引先を保守する役でもある（docs/02）。
        var partner = partners.Find(partnerId);
        if (partner is null)
        {
            if (places.TrySayOnce(partnerId, out var unknownLine, out var unknownRows))
            {
                violations.Add(new Violation(
                    JournalViolationCodes.PartnerUnknown,
                    $"取引先が取引先マスタにありません。{UsedIn(unknownRows)}"
                    + "別の取引先を選ぶか、取引先マスタに登録してください。",
                    unknownLine,
                    severity));
            }

            return;
        }

        if (!partner.IsActive && places.TrySayOnce(partnerId, out var lineNo, out var rows))
        {
            violations.Add(new Violation(
                JournalViolationCodes.PartnerInactive,
                $"取引先「{partner.Name}」は「有効」がオフです。{UsedIn(rows)}"
                + "別の取引先を選ぶか、取引先マスタで有効に戻してください。",
                lineNo,
                severity));
        }
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
                $"「{JournalLineRules.DescriptionLabel}」が入っていません。何の取引かを書いてください。"));
            return;
        }

        // **長さも計上の側で見る。** 保存の関門（`JournalSubmitRequirements`）は
        // **差分に載った欄しか見ない**（qa/01 の F-12）ので、
        // **上限を置く前に書かれた長い下書きは、保存せずに「計上する」を押すだけで計上でき、以後不変になる**
        // （I-05）。**投入 API も同じ検証を通る**（docs/10 §10）。
        var length = JournalLineRules.CountCharacters(entry.Description);
        if (length > JournalLineRules.TextMaxLength)
        {
            violations.Add(new Violation(
                JournalViolationCodes.DescriptionTooLong, JournalLineRules.DescriptionTooLong(length)));
        }
    }

    /// <summary>明細の「内容」の長さ（docs/10 §4-2-1）。</summary>
    /// <remarks>
    /// <b>摘要と同じ理由で計上の側にも置く</b>（上の注記）。
    /// <b>必須ではない</b>ので、見るのは長さだけである。
    /// </remarks>
    private static void ValidateItemDescriptions(JournalEntry entry, List<Violation> violations)
    {
        foreach (var line in entry.Lines)
        {
            var length = JournalLineRules.CountCharacters(line.ItemDescription);
            if (length > JournalLineRules.TextMaxLength)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.ItemDescriptionTooLong,
                    JournalLineRules.ItemDescriptionTooLong(length),
                    line.LineNo));
            }
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
                JournalViolationCodes.LineNoInvalid, JournalLineRules.LineNoDuplicatedAt(lineNo)));
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
        // **同じ直し先の断りを、行数だけ並べない**（docs/21 §2-6）。
        // **無効にした 1 科目を 3 行で使えば、直すのは勘定科目マスタの 1 行なのに同じ文が 3 つ並ぶ**
        // ——取引先より起きやすい（2026-09-16 の自己レビュー。qa/02 のラウンド 118）。
        // **断る前に場所を集める**ので、行を回す前に 1 度数える。
        // **行番号の順に集める**（ドメインの型は並びを持たないので、ここで決める）。
        var accountPlaces = new RejectionPlaces<AccountId>();
        var departmentPlaces = new RejectionPlaces<DepartmentId>();
        var subAccountPlaces = new RejectionPlaces<SubAccountId>();

        // **要件の断りは、科目ごとに 1 件にまとめる**（docs/21 §2-6）。
        // **文が科目の名前を持つ**ので、同じ科目の行では同じ文が並ぶ
        // ——売掛金を 5 行書けば「取引先を要する」の断りが 5 つ出ていた（2026-09-16 の自己レビュー）。
        // **鍵は科目、場所は行**である。
        var requirements = new RequirementPlaces();

        foreach (var line in entry.Lines.OrderBy(l => l.LineNo))
        {
            accountPlaces.Note(line.AccountId, PlaceOf(line));

            if (line.DepartmentId is DepartmentId departmentId)
            {
                departmentPlaces.Note(departmentId, PlaceOf(line));
            }

            if (context.Accounts.Find(line.AccountId) is not AccountDefinition owner)
            {
                continue;
            }

            // **要件の断りは、その断りに届く行だけ控える。**
            // **判定は断る側と同じ述語を使う**ので、2 か所に同じ条件を書かない。
            //
            // **場所は行そのもの**（`PlaceOf` で親へ畳まない）。
            // **税行が本体行から継ぐのは部門だけ**である（<see cref="ValidateTaxLine"/> が強制するのは
            // 借方貸方・部門・税区分・用途区分）。**取引先と補助科目を親へ畳むと、
            // 文が名乗る科目と指す行が食い違い**、**本体行を直しても税行の分が残って弾かれ続ける**。
            if (NeedsPartner(line, entry, owner))
            {
                requirements.Partner.Note(line.AccountId, line.LineNo);
            }

            if (NeedsSubAccount(line, owner))
            {
                requirements.SubAccount.Note(line.AccountId, line.LineNo);
            }

            if (HasUnwantedSubAccount(line, owner, context.SubAccounts))
            {
                requirements.SubAccountNotAllowed.Note(line.AccountId, line.LineNo);
            }

            // **部門だけは親へ畳む**——税行の部門は本体行と同じであることを強制しているので、
            // 本体行を直せば税行も直る（<see cref="PlaceOf"/>）。
            if (NeedsDepartment(line, owner))
            {
                requirements.Department.Note(line.AccountId, PlaceOf(line));
            }

            // **補助科目は、無効の断りに届く行だけ控える**（<see cref="ReachesSubAccountRejection"/>）。
            // 「補助科目を使う」がオフの行や親が違う行を控えると、
            // **「有効に戻せば直る場所」に、有効に戻しても直らない行が混ざる**。
            if (line.SubAccountId is SubAccountId subAccountId
                && ReachesSubAccountRejection(owner, subAccountId, context.SubAccounts))
            {
                subAccountPlaces.Note(subAccountId, PlaceOf(line));
            }
        }

        // **断るのも行番号の順**——ドメインの型は並びを持たないので、
        // 渡された順のままだと「①行 2: …②行 1: …」になる。
        foreach (var line in entry.Lines.OrderBy(l => l.LineNo))
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
            ValidateDepartment(line, entry, context.Departments, departmentPlaces, violations);

            var account = context.Accounts.Find(line.AccountId);
            if (account is null)
            {
                if (accountPlaces.TrySayOnce(line.AccountId, out var unknownLine, out var unknownRows))
                {
                    violations.Add(new Violation(
                        JournalViolationCodes.AccountUnknown,
                        $"勘定科目が勘定科目マスタにありません。{UsedIn(unknownRows)}"
                        + "別の勘定科目を選ぶか、勘定科目マスタに登録してください。",
                        unknownLine));
                }

                continue;
            }

            if (!account.IsActive
                && accountPlaces.TrySayOnce(line.AccountId, out var inactiveLine, out var inactiveRows))
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AccountInactive,
                    $"勘定科目「{account.Name}」は「有効」がオフです。{UsedIn(inactiveRows)}"
                    + "別の勘定科目を選ぶか、勘定科目マスタで有効に戻してください。",
                    inactiveLine,
                    InactiveSeverity(entry)));
            }

            if (NeedsDepartment(line, account)
                && requirements.Department.TrySayOnce(line.AccountId, out var missingLine, out var missingRows))
            {
                violations.Add(new Violation(
                    JournalViolationCodes.DepartmentMissing,
                    $"勘定科目「{account.Name}」には「部門」が必要です。{At(missingRows)}「部門」を選んでください。",
                    missingLine));
            }

            ValidatePartner(line, entry, account, context.HasSelectablePartner, requirements.Partner, violations);
            ValidateSubAccount(
                line, entry, account, context.SubAccounts, subAccountPlaces,
                requirements.SubAccount, requirements.SubAccountNotAllowed, violations);
        }
    }

    private static void ValidateDepartment(
        JournalLine line, JournalEntry entry, DepartmentCatalog departments,
        RejectionPlaces<DepartmentId> places, List<Violation> violations)
    {
        if (line.DepartmentId is not DepartmentId departmentId)
        {
            return;
        }

        var department = departments.Find(departmentId);
        if (department is null)
        {
            if (places.TrySayOnce(departmentId, out var unknownLine, out var unknownRows))
            {
                violations.Add(new Violation(
                    JournalViolationCodes.DepartmentUnknown,
                    $"部門が部門マスタにありません。{UsedIn(unknownRows)}"
                    + "別の部門を選ぶか、部門マスタに登録してください。",
                    unknownLine));
            }

            return;
        }

        if (!department.IsActive && places.TrySayOnce(departmentId, out var lineNo, out var rows))
        {
            violations.Add(new Violation(
                JournalViolationCodes.DepartmentInactive,
                $"部門「{department.Name}」は「有効」がオフです。{UsedIn(rows)}"
                + "別の部門を選ぶか、部門マスタで有効に戻してください。",
                lineNo,
                InactiveSeverity(entry)));
        }
    }

    /// <summary>
    /// 取引先を要する科目の明細に取引先があるか（<c>E-PARTNER-REQUIRED</c>。docs/15 §1-2）。
    /// </summary>
    /// <remarks>
    /// <para><b>見るのは実効値である</b>（<see cref="JournalEntry.PartnerOf"/>）——
    /// 明細が持っていなければ伝票のものが帳簿に載るので、伝票に 1 つ選んであれば足りる。
    /// <b>明細だけを見ると、帳簿には取引先が載る行を関門が拒む</b>ことになる。
    /// <b>画面では伝票と明細のどちらの取引先も選べる</b>（明細の一覧の列。ADR-0062）ので、
    /// 案内は両方の欄を名指しする。判定は実効値のままである。</para>
    /// <para><b>片側だけの規則である。</b> 補助科目の 2 値（ADR-0038 §3）と違い、
    /// 要しない科目に取引先が付いていても止めない——<b>取引先は科目に属さない</b>ので、
    /// どの科目の行にも意味のある相手方がありうる（現金の行の支払先など）。</para>
    /// </remarks>
    private static void ValidatePartner(
        JournalLine line, JournalEntry entry, AccountDefinition account, bool hasSelectablePartner,
        RejectionPlaces<AccountId> places, List<Violation> violations)
    {
        // 補助科目と同じく、**踏めない案内をしない**（docs/21 §2-3。qa/02 R53-06）。
        // **どちらの欄かを括って言う**——同じ画面に「取引先」というラベルの欄が 2 つある。
        // **どちらを選んでも実効値が埋まる**（明細の一覧の列は ADR-0062 で足した）。
        // **`Lines` は `CanNavigateToDetail: false`** なので、行の詳細は開けない——
        // 案内できるのは**一覧の列**と**伝票の欄**の 2 つだけである。
        // **選べる取引先が 1 件も無いときは、次の一手が「登録」か「有効に戻す」に変わる**——
        // 取引先は運用で無効にされるマスタなので（docs/13）、登録済みで全部無効なこともある。
        if (!NeedsPartner(line, entry, account)
            || !places.TrySayOnce(line.AccountId, out var lineNo, out var rows))
        {
            return;
        }

        violations.Add(new Violation(
            JournalViolationCodes.PartnerRequired,
            hasSelectablePartner
                ? $"勘定科目「{account.Name}」は「取引先を要する」がオンです。"
                  + $"伝票の「取引先」か、{RowAt(lineNo, rows)}「取引先」を選んでください。"
                : $"勘定科目「{account.Name}」は「取引先を要する」がオンですが、選べる取引先がありません。"
                  + "取引先マスタに登録するか、無効にした取引先を有効に戻してから、"
                  + $"伝票の「取引先」か、{RowAt(lineNo, rows)}「取引先」を選んでください。",
            lineNo,
            ReversalOnlySeverity(entry)));
    }

    private static void ValidateSubAccount(
        JournalLine line, JournalEntry entry, AccountDefinition account, SubAccountCatalog subAccounts,
        RejectionPlaces<SubAccountId> places, RejectionPlaces<AccountId> wanted,
        RejectionPlaces<AccountId> unwanted, List<Violation> violations)
    {
        if (line.SubAccountId is not SubAccountId subAccountId)
        {
            if (NeedsSubAccount(line, account)
                && wanted.TrySayOnce(line.AccountId, out var wantedLine, out var wantedRows))
            {
                // **次の一手は、選べる補助科目があるかで変わる。** 1 つも無い科目に
                // 「選んでください」と言うと、候補ダイアログが 0 件で開くだけで踏めない
                // （docs/21 §1・§2-3。qa/02 R45-17 と同じ型）。
                violations.Add(new Violation(
                    JournalViolationCodes.SubAccountRequired,
                    subAccounts.HasSelectable(account.Id)
                        ? $"勘定科目「{account.Name}」は「補助科目を使う」がオンです。"
                          + $"{At(wantedRows)}「補助科目」を選んでください。"
                        : $"勘定科目「{account.Name}」は「補助科目を使う」がオンですが、選べる補助科目がありません。"
                          + $"補助科目マスタに登録してから、{At(wantedRows)}「補助科目」を選んでください。",
                    wantedLine,
                    ReversalOnlySeverity(entry)));
            }
            return;
        }

        // **実在は先に見る。** ここを 2 値の検査より後ろに置くと、取消では 2 値が警告なので
        // **マスタに無い補助科目が関門を素通りし、DB の外部キーで落ちる**（qa/03 L-14 の型）。
        var subAccount = subAccounts.Find(subAccountId);
        if (subAccount is null)
        {
            if (places.TrySayOnce(subAccountId, out var unknownLine, out var unknownRows))
            {
                violations.Add(new Violation(
                    JournalViolationCodes.SubAccountUnknown,
                    $"補助科目が補助科目マスタにありません。{UsedIn(unknownRows)}"
                    + "別の補助科目を選ぶか、補助科目マスタに登録してください。",
                    unknownLine));
            }

            return;
        }

        // **補助科目は 2 値である**（ADR-0038 §3）。使わない科目は補助科目を持てない——
        // 持てると「全補助科目の合計＝科目の残高」が崩れ、補助元帳に載らない残高ができる。
        // **親の一致より先に見る。** どちらも「その補助科目でよいか」の話だが、
        // ここで断られる利用者に必要なのは「この科目では補助科目を使わない」であって、
        // 別の補助科目を選び直せという案内ではない。
        if (!account.UsesSubAccount)
        {
            if (unwanted.TrySayOnce(line.AccountId, out var unwantedLine, out var unwantedRows))
            {
                violations.Add(new Violation(
                    JournalViolationCodes.SubAccountNotAllowed,
                    $"勘定科目「{account.Name}」は「補助科目を使う」がオフです。"
                    + $"{At(unwantedRows)}「補助科目」を空にしてください。",
                    unwantedLine,
                    ReversalOnlySeverity(entry)));
            }

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

        if (!subAccount.IsActive && places.TrySayOnce(subAccountId, out var lineNo, out var rows))
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountInactive,
                $"補助科目「{subAccount.Name}」は「有効」がオフです。{UsedIn(rows)}"
                + "別の補助科目を選ぶか、補助科目マスタで有効に戻してください。",
                lineNo,
                InactiveSeverity(entry)));
        }
    }

    /// <summary>
    /// <b>要件の断りの控え</b>（docs/21 §2-6）。<b>断りごとに別の控えを持つ</b>——
    /// 1 つを分け合うと、<b>先に来たほうが後を黙らせる</b>（<see cref="RejectionPlaces{TKey}"/> の注記）。
    /// </summary>
    /// <remarks>
    /// <para><b>4 つのうち <see cref="SubAccount"/> と <see cref="SubAccountNotAllowed"/> は排他である</b>
    /// （前者は「補助科目を使う」がオン、後者はオフ。鍵はどちらも科目）。
    /// <b>この 2 つだけは共有しても結果が変わらない</b>ので、検体では撃てない——
    /// 分けてあるのは<b>形を揃えるため</b>である。</para>
    /// <para><b>この控えは、1 つの鍵の中で重さが一様であることに依存している。</b>
    /// 重さは伝票の種別だけで決まる（<see cref="ReversalOnlySeverity"/>）ので、いまは一様である。
    /// <b>行ごとに重さを変える日</b>（税行を作る回。<see cref="ValidatePartners"/> の注記）<b>には、
    /// 鍵を（科目, 重さ）にするか控えを分けること</b>——でないと警告が差し戻しを食う。</para>
    /// </remarks>
    private sealed class RequirementPlaces
    {
        internal readonly RejectionPlaces<AccountId> Partner = new();
        internal readonly RejectionPlaces<AccountId> SubAccount = new();
        internal readonly RejectionPlaces<AccountId> SubAccountNotAllowed = new();
        internal readonly RejectionPlaces<AccountId> Department = new();
    }

    /// <summary>その行に<b>取引先が要る</b>のに無いか（<c>E-PARTNER-REQUIRED</c>）。</summary>
    /// <remarks><b>控える側と断る側が同じ述語を使う</b>——2 か所に同じ条件を書くと、片方だけ動く。</remarks>
    private static bool NeedsPartner(JournalLine line, JournalEntry entry, AccountDefinition account)
        => account.RequiresPartner && entry.PartnerOf(line) is null;

    /// <summary>その行に<b>補助科目が要る</b>のに無いか（<c>E-SUBACCOUNT-REQUIRED</c>）。</summary>
    private static bool NeedsSubAccount(JournalLine line, AccountDefinition account)
        => line.SubAccountId is null && account.UsesSubAccount;

    /// <summary>
    /// その行に<b>持てない補助科目が付いている</b>か（<c>E-SUBACCOUNT-NOT-ALLOWED</c>）。
    /// </summary>
    /// <remarks>
    /// <b>控える側だけが使う。</b> 断る側（<see cref="ValidateSubAccount"/>）は打ち切りの順で
    /// <b>補助科目があること・実在すること</b>が確定しているので、<c>!UsesSubAccount</c> だけを見る。
    /// <b>ここで 3 条件とも見るのは、前処理が打ち切りを通らないから</b>である。
    /// </remarks>
    private static bool HasUnwantedSubAccount(
        JournalLine line, AccountDefinition account, SubAccountCatalog subAccounts)
        => line.SubAccountId is SubAccountId subAccountId
            && subAccounts.Find(subAccountId) is not null
            && !account.UsesSubAccount;

    /// <summary>その行に<b>部門が要る</b>のに無いか（<c>I-13</c>）。</summary>
    private static bool NeedsDepartment(JournalLine line, AccountDefinition account)
        => account.Category.IsProfitAndLoss() && line.DepartmentId is null;

    /// <summary>場所の並びを「〜で使っています。」の 1 文にする（場所が 1 つ以下なら空）。</summary>
    private static string UsedIn(string rows)
        => rows.Length == 0 ? string.Empty : $"{rows} で使っています。";

    /// <summary>
    /// 欄の名前に付ける場所（<b>行の側を名乗る形</b>）。
    /// <b>場所が 1 つなら「その行の」</b>——接頭辞（「行 3: 」）が行番号を運ぶ（docs/21 §3）。
    /// <b>字は画面の凡例に合わせてある</b>
    /// （「行ごとに相手方が違うときは、その行の「取引先」を選んでください。」）。
    /// <b>場所が 1 つも無いなら「明細の」</b>——接頭辞も付かないので、
    /// 「その行」が何も指さなくなる（行番号が 0 以下の行だけで使ったとき）。
    /// </summary>
    private static string RowAt(int? lineNo, string rows)
        => rows.Length > 0 ? $"{rows} の"
            : lineNo is null ? "明細の"
            : "その行の";

    /// <summary>欄の名前に付ける場所（場所が 1 つ以下なら空）。</summary>
    private static string At(string rows)
        => rows.Length == 0 ? string.Empty : $"{rows} の";

    /// <summary>
    /// <b>断りが指す場所</b>。<b>消費税行は本体行で代表させる</b>。
    /// </summary>
    /// <remarks>
    /// <b>消費税行はシステムが作り、利用者は直接編集できない</b>（docs/11 §2）ので、
    /// <b>税行の行番号を並べても踏めない</b>——しかも税行の部門は本体行と同じであることを
    /// <see cref="ValidateTaxLine"/> が強制するので、<b>同じ部門が必ず 2 行に現れる</b>。
    /// 本体行で代表させると控えが重なり、<see cref="RejectionPlaces{TKey}.Note"/> が 1 つに畳む。
    /// <b>本体行を指していない税行は自分の行番号で数える</b>——そちらは
    /// <see cref="ValidateTaxLine"/> が別に断るので、ここで隠さない。
    /// </remarks>
    private static int? PlaceOf(JournalLine line)
        => line.IsTaxLine ? line.ParentLineNo ?? line.LineNo : line.LineNo;

    /// <summary>
    /// その行が<b>補助科目の「マスタに無い」「無効」の断りに届く</b>か。
    /// </summary>
    /// <remarks>
    /// <b><see cref="ValidateSubAccount"/> の打ち切りと同じ判断</b>である——
    /// 実在しない補助科目は「補助科目を使う」を見ずに断り、
    /// 使わない科目の行と親が違う行は<b>無効を見る前に別の断りで終わる</b>。
    /// <b>2 か所に同じ判断があるので、片方だけ動かすと場所がずれる</b>——
    /// 打ち切りの 1 つ 1 つを検体が固定している（<c>JournalEntryValidatorTests</c>）。
    /// </remarks>
    private static bool ReachesSubAccountRejection(
        AccountDefinition account, SubAccountId subAccountId, SubAccountCatalog subAccounts)
        => subAccounts.Find(subAccountId) is not SubAccountDefinition subAccount
            || (account.UsesSubAccount && subAccount.AccountId == account.Id);

    /// <summary>
    /// 無効にしたマスタを使っていることの重さ。
    /// </summary>
    /// <remarks>
    /// <para><b>取消と訂正では止めない。</b> 新たな計上には使えないが、どちらも
    /// 「過去に計上したものを打ち消す・直す」操作なので、後からマスタを無効にしたせいで
    /// <b>訂正も取消もできない仕訳が帳簿に残る</b>という最悪の状態を作ってはいけない
    /// （docs/15 §1・ADR-0004）。</para>
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
    /// <b>取消でだけ止めない</b>ことの重さ。
    /// <b>補助科目の 2 値</b>（ADR-0038 §3）・<b>取引先を要する科目</b>（docs/15 §1-2）・
    /// <b>伝票と明細の取引先の実在・有効</b>（docs/15 §1-3。明細は 2026-09-16 からこちら）が使う。
    /// </summary>
    /// <remarks>
    /// <para><b>外すのは取消だけである。</b> 取消の明細はサーバが原仕訳を反転して作り
    /// （<c>JournalReversalPosting</c>）、<b>利用者が直す手立てが無い</b>。
    /// Error にすると、規則より前に計上された伝票を<b>取り消せなくなる</b>——
    /// 開発機には「補助科目を使わない科目に補助科目が付いた計上済み明細」も
    /// 「取引先を要する科目に取引先の無い計上済み明細」も実在する
    /// （<b>件数と数え方は qa/04</b>——取消がそれを写すので増える。ここに写さない）。</para>
    /// <para><b>訂正（再計上）は外さない。</b> 再計上の中身は利用者が決め、サーバは一切書き換えない
    /// （<c>JournalCorrectionPosting</c> の注記）ので、<b>補助科目を空にすれば通る</b>——
    /// 行き止まりにならない。外すと、訂正を経由して<b>規則より後の違反を新しく帳簿へ入れられる</b>
    /// （自己レビューで見つけた。2026-09-08）。</para>
    /// <para><b>「要る」側にも同じ線を引く。</b> 使う科目に変えられた後の過去の明細は
    /// 補助科目を持たないので、Error のままだと同じく取り消せなくなる
    /// （開発機では 0 行。2026-09-08 実測）。</para>
    /// <para><b>DDL のトリガはここより狭い</b>（docs/10 §4-2-1 の二層の広さ）——
    /// ここは取消をすべて外すが、トリガは<b>計上済みの原仕訳を写しただけの明細</b>だけを外す
    /// （<c>trg_journal_entries_sub_account_presence_when_posted</c>・
    /// <c>trg_journal_entries_partner_presence_when_posted</c>）。
    /// <c>entry_type</c> は取込・CLI・手打ちの SQL が自由に書ける列だからである。
    /// <b>アプリからは差が出ない</b>——取消の明細は <c>JournalReversalPosting</c> が原仕訳から作るので、
    /// 必ず写しになる。<b>関門を通らない経路のためだけに、あちらを狭くしてある。</b></para>
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

    /// <summary>
    /// 同じ直し先の断りを 1 件にまとめるための、<b>使った場所の控え</b>（docs/21 §2-6）。
    /// </summary>
    /// <remarks>
    /// <para><b>断る前に、その直し先を使っている場所を全部知っている必要がある</b>——
    /// 1 件にまとめるとき、文に並べるのは<b>場所の全部</b>だからである。
    /// だから<b>使った場所を先に 1 度集め</b>（<see cref="Note"/>）、
    /// 断るときに<b>最初の 1 回だけ</b>文を作る（<see cref="TrySayOnce"/>）。</para>
    /// <para><b>場所が 1 つなら行番号で指し、複数なら文に並べる</b>（docs/21 §2-6・§3）。
    /// 1 つのときに並べ書きを出さないのは、<b>「行 N:」で足りるところに同じことを 2 回書かない</b>ため。</para>
    /// <para><b>まとめる鍵は直し先の識別子</b>である——違反の種類ではない。
    /// 無効なマスタが 2 つあれば 2 件出る（片方の直し忘れに気づけなくなるから）。</para>
    /// <para><b>同じ鍵で、種類の違う断りが 2 つ立つことは無い。</b>
    /// 「マスタに無い」と「無効」はカタログの引き当てが決めるので<b>排他</b>で、前者は必ずその場で打ち切る。
    /// <b>3 つ目の検査をこの控えに足すときは、先に来たほうが後を黙らせないかを確かめる</b>——
    /// 重さが違うと、警告が差し戻しを食う。</para>
    /// <para><b><see cref="TrySayOnce"/> を呼ぶ鍵は、必ず先に <see cref="Note"/> してあること。</b>
    /// 控えの無い鍵では落ちる——<b>落とすのは意図である</b>。場所を知らないまま断ると、
    /// 「場所は捨てない」という規則に反した文が静かに出る。</para>
    /// </remarks>
    private sealed class RejectionPlaces<TKey>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, List<int?>> _used = [];
        private readonly HashSet<TKey> _said = [];

        /// <summary>その直し先を使っている場所を控える（<c>null</c> は伝票の欄）。</summary>
        /// <remarks>
        /// <b>同じ場所は 1 度しか控えない</b>——行番号が重なった伝票（<c>E-LINE-NO</c>）でも
        /// 「行 1・行 1 で使っています。」とは言わない。
        /// </remarks>
        internal void Note(TKey key, int? lineNo)
        {
            if (!_used.TryGetValue(key, out var places))
            {
                _used[key] = places = [];
            }

            if (!places.Contains(lineNo))
            {
                places.Add(lineNo);
            }
        }

        /// <summary>
        /// いま断るなら <c>true</c>。<b>同じ直し先の 2 回目からは <c>false</c></b>。
        /// </summary>
        /// <param name="lineNo">場所が 1 つならその行番号（伝票の欄なら <c>null</c>）。0 か複数なら <c>null</c>。</param>
        /// <param name="rows">
        /// 場所が複数なら「伝票・行 1・行 2」の並び。1 つ以下なら空。
        /// <b>文にするのは呼ぶ側である</b>——断りごとに言い方が違う
        /// （「〜で使っています。」と「〜の「取引先」を選んでください。」）。
        /// </param>
        internal bool TrySayOnce(TKey key, out int? lineNo, out string rows)
        {
            lineNo = null;
            rows = string.Empty;
            if (!_said.Add(key))
            {
                return false;
            }

            // **指せない行番号は場所に出さない。** 0 や負の行番号を並べると
            // 「行 0 で使っています」と、存在しない行を名指しすることになる
            // （同じ線は ValidateStructure が引いている）。**全部落ちたら場所は言わない。**
            // **値で並べる**（控えに入れた順ではない）。**伝票（`null`）が先、次に行番号の順。**
            // 入れた順に頼ると、親へ畳む場所（<see cref="PlaceOf"/>）が混ざったときに逆順になる。
            var places = _used[key]
                .Where(place => place is not int no || no > 0)
                .OrderBy(place => place.HasValue)
                .ThenBy(place => place)
                .ToList();
            if (places.Count == 1)
            {
                lineNo = places[0];
            }
            else if (places.Count > 1)
            {
                var written = places.Select(place => place is int no ? $"行 {no}" : "伝票");
                rows = string.Join("・", written);
            }

            return true;
        }
    }
}
