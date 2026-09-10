namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 訂正（取消 ＋ 正しい内容の再計上）。docs/10 §5・ADR-0015。
/// </summary>
/// <remarks>
/// <b>ここで守っているのは「取引が帳簿に二重に載らない」ことである。</b>
/// 原仕訳を生かしたまま再計上を足す道が 1 つでも開いていると、
/// 帳簿は静かに 2 倍の売上を持つ。
/// </remarks>
public class JournalCorrectionTests
{
    private static readonly DateOnly TransactionDate = new(2026, 5, 20);
    private static readonly DateOnly CorrectedOn = new(2026, 6, 10);
    private static readonly DateTimeOffset EnteredAt = new(2026, 6, 10, 9, 0, 0, TimeSpan.FromHours(9));

    private static ReversalContext StartContext(bool alreadyReversed = false, FiscalYearId? fiscalYearId = null)
        => new(alreadyReversed, fiscalYearId ?? AccountingFixture.FiscalYear);

    /// <summary>既定は「取消済み・訂正なし」。取り消されていない状態は <see cref="NotReversed"/> で作る。</summary>
    private static CorrectionContext PostingContext(
        DateOnly? reversedOn = null, bool alreadyCorrected = false)
        => new(reversedOn ?? CorrectedOn, alreadyCorrected);

    /// <summary>
    /// 原仕訳がまだ取り消されていない状態。
    /// <b>既定値の <c>?? CorrectedOn</c> に潰されるので、<c>PostingContext(null)</c> では作れない。</b>
    /// </summary>
    private static CorrectionContext NotReversed(bool alreadyCorrected = false)
        => new(null, alreadyCorrected);

    private static JournalEntry Posted(
        JournalEntry? draft = null, int entryNo = 1, EntryType entryType = EntryType.Normal)
        => (draft ?? AccountingFixture.CashSale(TransactionDate)) with
        {
            Status = EntryStatus.Posted,
            EntryType = entryType,
            EntryNo = entryNo,
            PostedAt = new DateTimeOffset(2026, 5, 20, 18, 0, 0, TimeSpan.FromHours(9)),
        };

    // --- 訂正を始める ---

    [Fact]
    public void 取消と再計上が組でできる()
    {
        var original = Posted();

        var result = JournalCorrection.Start(original, CorrectedOn, EnteredAt, StartContext());

        Assert.True(result.Started);
        Assert.Equal(EntryType.Reversal, result.Drafts!.Value.Reversal.EntryType);
        Assert.Equal(EntryType.Correction, result.Drafts!.Value.Correction.EntryType);

        // どちらも**原仕訳を**指す。2 本を結ぶ列は持たない（ADR-0015）。
        Assert.Equal(original.Id, result.Drafts!.Value.Reversal.OriginalEntryId);
        Assert.Equal(original.Id, result.Drafts!.Value.Correction.OriginalEntryId);
    }

    [Fact]
    public void 再計上は原仕訳をそのまま写した下書きになる()
    {
        var original = Posted();

        var correction = JournalCorrection.Start(original, CorrectedOn, EnteredAt, StartContext()).Drafts!.Value.Correction;

        // 貸借は**入れ替えない**。取消と違い、再計上は「正しい取引」そのものである。
        Assert.Equal(original.Lines.Select(l => l.DebitCredit), correction.Lines.Select(l => l.DebitCredit));
        Assert.Equal(original.Lines.Select(l => l.Amount), correction.Lines.Select(l => l.Amount));
        Assert.Equal(original.Lines.Select(l => l.AccountId), correction.Lines.Select(l => l.AccountId));
        Assert.Equal(original.Lines.Select(l => l.DepartmentId), correction.Lines.Select(l => l.DepartmentId));
        Assert.Equal(original.Lines.Select(l => l.LineNo), correction.Lines.Select(l => l.LineNo));

        Assert.Equal(EntryStatus.Draft, correction.Status);
        Assert.Null(correction.EntryNo);
        Assert.Equal(original.PartnerId, correction.PartnerId);
        Assert.Equal(EnteredAt, correction.EnteredAt);
    }

    [Fact]
    public void 再計上の取引日は原仕訳と同じで_計上日だけが後ろにずれる()
    {
        var correction = JournalCorrection.Start(Posted(), CorrectedOn, EnteredAt, StartContext()).Drafts!.Value.Correction;

        Assert.Equal(TransactionDate, correction.TransactionDate);
        Assert.Equal(CorrectedOn, correction.PostingDate);
    }

    [Fact]
    public void 投入元は写し_冪等キーだけ落とす()
    {
        // **落とすのは冪等キーだけ。** 一意なのはそれだけで（I-14）、写せば必ず衝突する。
        // 投入元（部品名・外部伝票 ID）まで落とすと、投げた側が自分の伝票の訂正を帳簿から辿れなくなる。
        var original = Posted() with
        {
            SourceComponent = "expense",
            SourceDocumentId = "EXP-001",
            IdempotencyKey = "expense/EXP-001",
        };

        var drafts = JournalCorrection.Start(original, CorrectedOn, EnteredAt, StartContext()).Drafts!.Value;

        foreach (var draft in new[] { drafts.Correction, drafts.Reversal })
        {
            Assert.Equal("expense", draft.SourceComponent);
            Assert.Equal("EXP-001", draft.SourceDocumentId);
            Assert.Null(draft.IdempotencyKey);
        }
    }

    [Fact]
    public void 訂正した相手が摘要から分かる()
    {
        var original = Posted() with { Description = "5 月分の売上" };

        var correction = JournalCorrection.Start(original, CorrectedOn, EnteredAt, StartContext()).Drafts!.Value.Correction;

        Assert.Equal("伝票番号 1 の訂正: 5 月分の売上", correction.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 摘要が無い原仕訳でも訂正と分かる(string? description)
    {
        var original = Posted() with { Description = description };

        var correction = JournalCorrection.Start(original, CorrectedOn, EnteredAt, StartContext()).Drafts!.Value.Correction;

        Assert.Equal("伝票番号 1 の訂正", correction.Description);
    }

    [Fact]
    public void 訂正を重ねても摘要が入れ子にならない()
    {
        // **実機で見つけた。** 素朴に前置きすると
        // 「伝票番号 5 の訂正: 伝票番号 3 の訂正: 8 月分の…」と伸び続ける（2026-08-25）。
        // 摘要欄は取引の説明であって履歴ではない。履歴は原仕訳への参照で辿れる。
        var corrected = Posted(entryType: EntryType.Correction, entryNo: 5) with
        {
            Description = "伝票番号 3 の訂正: 8 月分のサーバ利用料",
        };

        var result = JournalCorrection.Start(corrected, CorrectedOn, EnteredAt, StartContext());

        Assert.Equal("伝票番号 5 の訂正: 8 月分のサーバ利用料", result.Drafts!.Value.Correction.Description);
        Assert.Equal("伝票番号 5 の取消: 8 月分のサーバ利用料", result.Drafts!.Value.Reversal.Description);
    }

    [Fact]
    public void 摘要の無い訂正伝票を訂正しても接頭辞の剥がしが落ちない()
    {
        // **規則より前に計上された伝票には摘要が無い**（docs/10 §4-2-1。稼働 DB に 2 件）。
        // その伝票を直した訂正伝票を、さらに訂正する経路を通す。
        // 接頭辞を剥がす側は原仕訳が訂正・取消のときだけ走るので、
        // **摘要が null の訂正伝票**を通さないと、この分岐は一度も踏まれない。
        var corrected = Posted(entryType: EntryType.Correction, entryNo: 7) with { Description = null };

        var result = JournalCorrection.Start(corrected, CorrectedOn, EnteredAt, StartContext());

        Assert.Equal("伝票番号 7 の訂正", result.Drafts!.Value.Correction.Description);
        Assert.Equal("伝票番号 7 の取消", result.Drafts!.Value.Reversal.Description);
    }

    [Fact]
    public void 何段重なった接頭辞も落とす()
    {
        // 直す前に作られた伝票が既に入れ子を抱えていても、そこから先は伸びない。
        var nested = Posted(entryType: EntryType.Correction, entryNo: 9) with
        {
            Description = "伝票番号 5 の訂正: 伝票番号 3 の取消: 伝票番号 1 の訂正: 本文",
        };

        var result = JournalCorrection.Start(nested, CorrectedOn, EnteredAt, StartContext());

        Assert.Equal("伝票番号 9 の訂正: 本文", result.Drafts!.Value.Correction.Description);
    }

    [Fact]
    public void 接頭辞しかない摘要は本文なしになる()
    {
        var prefixOnly = Posted(entryType: EntryType.Correction, entryNo: 4) with
        {
            Description = "伝票番号 3 の取消",
        };

        var result = JournalCorrection.Start(prefixOnly, CorrectedOn, EnteredAt, StartContext());

        Assert.Equal("伝票番号 4 の訂正", result.Drafts!.Value.Correction.Description);
    }

    [Fact]
    public void 通常の仕訳の摘要は_接頭辞と同じ形でも落とさない()
    {
        // **通常の仕訳の摘要は利用者が書いた文である。** たまたま同じ形をしていることがあり
        // （紙の伝票番号を書いた・過去の取消について書いた）、無条件に剥がすと本文が空のまま計上され、
        // 計上済みは二度と直せない（I-05）。剥がすのは原仕訳が取消・訂正のときだけ。
        var lookalike = Posted() with { Description = "伝票番号 12 の取消" };

        var result = JournalCorrection.Start(lookalike, CorrectedOn, EnteredAt, StartContext());

        Assert.Equal("伝票番号 1 の訂正: 伝票番号 12 の取消", result.Drafts!.Value.Correction.Description);
    }

    [Fact]
    public void 似ているだけの摘要は落とさない()
    {
        // 「伝票番号 3 の訂正について打ち合わせ」のような本文を、接頭辞と間違えて削らない。
        var lookalike = Posted() with { Description = "伝票番号 3 の訂正について打ち合わせ" };

        var result = JournalCorrection.Start(lookalike, CorrectedOn, EnteredAt, StartContext());

        Assert.Equal(
            "伝票番号 1 の訂正: 伝票番号 3 の訂正について打ち合わせ",
            result.Drafts!.Value.Correction.Description);
    }

    [Fact]
    public void 年度をまたぐ訂正は_両方とも計上日の属する年度に載る()
    {
        var lastMarch = new DateOnly(2026, 3, 20);
        var next = new FiscalYearId(19);

        var result = JournalCorrection.Start(
            Posted(AccountingFixture.CashSale(lastMarch)),
            new DateOnly(2026, 4, 1),
            EnteredAt,
            StartContext(fiscalYearId: next));

        Assert.Equal(next, result.Drafts!.Value.Reversal.FiscalYearId);
        Assert.Equal(next, result.Drafts!.Value.Correction.FiscalYearId);
    }

    [Fact]
    public void 取り消せない仕訳は訂正も始められない()
    {
        // **再計上だけが手に入る道を作らない。** 取消を伴わない再計上は二重計上そのものである。
        var result = JournalCorrection.Start(
            Posted(), CorrectedOn, EnteredAt, StartContext(alreadyReversed: true));

        Assert.False(result.Started);
        Assert.Null(result.Drafts);
        Assert.Contains(JournalViolationCodes.AlreadyReversed, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 下書きは訂正できない()
    {
        var result = JournalCorrection.Start(
            AccountingFixture.CashSale(TransactionDate), CorrectedOn, EnteredAt, StartContext());

        Assert.False(result.Started);
        Assert.Contains(JournalViolationCodes.AmendmentTargetNotPosted, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 訂正の伝票を訂正できる()
    {
        // 直した内容がまた誤っていたときに手が無くならないようにする（ADR-0015）。
        var result = JournalCorrection.Start(
            Posted(entryType: EntryType.Correction), CorrectedOn, EnteredAt, StartContext());

        Assert.True(result.Started);
    }

    [Fact]
    public void 原仕訳は何も変わらない()
    {
        var original = Posted();

        JournalCorrection.Start(original, CorrectedOn, EnteredAt, StartContext());

        Assert.Equal(EntryStatus.Posted, original.Status);
        Assert.Equal(1, original.EntryNo);
        Assert.Equal(DebitCredit.Debit, original.Lines[0].DebitCredit);
    }

    // --- 再計上を計上する ---

    private static JournalEntry Correction(
        DateOnly? postingDate = null, JournalEntry? original = null)
        => JournalCorrection.Start(
            original ?? Posted(), postingDate ?? CorrectedOn, EnteredAt, StartContext()).Drafts!.Value.Correction;

    [Fact]
    public void 取り消された原仕訳の再計上は通る()
    {
        var original = Posted();

        var violations = JournalCorrection.ValidateForPosting(
            Correction(original: original), original, PostingContext());

        Assert.False(violations.HasError());
    }

    [Fact]
    public void 原仕訳が取り消されていなければ再計上できない()
    {
        // **これが訂正の要。** 原仕訳が生きたまま再計上が載ると、取引が帳簿に二重に計上される。
        var original = Posted();

        var violations = JournalCorrection.ValidateForPosting(
            Correction(original: original), original, NotReversed());

        Assert.Contains(JournalViolationCodes.OriginalNotReversed, violations.Select(v => v.Code));
    }

    [Fact]
    public void 取消より前の日付では再計上できない()
    {
        // 取消の前に再計上が載ると、その間の期間だけ二重計上になる。
        var original = Posted();
        var correction = Correction(postingDate: new DateOnly(2026, 6, 1), original: original);

        var violations = JournalCorrection.ValidateForPosting(
            correction, original, PostingContext(reversedOn: new DateOnly(2026, 6, 10)));

        Assert.Contains(JournalViolationCodes.CorrectionBeforeReversal, violations.Select(v => v.Code));
        Assert.Equal(
            "訂正の計上日（2026/06/01）が、元の伝票を取り消した日（2026/06/10）より前になっています。",
            violations.Single(v => v.Code == JournalViolationCodes.CorrectionBeforeReversal).Message);
    }

    [Fact]
    public void 取消と同じ日の再計上は通る()
    {
        // 訂正は 1 操作なので、取消と再計上が同じ日になるのが普通である。
        var original = Posted();

        var violations = JournalCorrection.ValidateForPosting(
            Correction(original: original), original, PostingContext(reversedOn: CorrectedOn));

        Assert.False(violations.HasError());
    }

    [Fact]
    public void 同じ原仕訳を二度訂正できない()
    {
        // 再計上が 2 本載れば、直した内容がそのまま二重になる。
        var original = Posted();

        var violations = JournalCorrection.ValidateForPosting(
            Correction(original: original), original, PostingContext(alreadyCorrected: true));

        Assert.Contains(JournalViolationCodes.AlreadyCorrected, violations.Select(v => v.Code));
    }

    [Fact]
    public void 計上済みでない原仕訳の再計上は通さない()
    {
        // 原仕訳が下書きに戻る道は無いが、別経路から呼ばれたときの守りとして見る。
        var correction = Correction();

        var violations = JournalCorrection.ValidateForPosting(
            correction, AccountingFixture.CashSale(TransactionDate), PostingContext());

        Assert.Contains(JournalViolationCodes.AmendmentTargetNotPosted, violations.Select(v => v.Code));
    }

    [Fact]
    public void 再計上の中身は検査しない()
    {
        // 中身は利用者が決めるもので、貸借一致・期間・部門は通常の仕訳と同じ検証が見る。
        // **ここで二重に見ると、規則が 2 か所に散って片方だけ緩む。**
        var original = Posted();
        var rewritten = Correction(original: original) with
        {
            Description = "利用者が書き直した摘要",
            Lines = [AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1)],
        };

        var violations = JournalCorrection.ValidateForPosting(rewritten, original, PostingContext());

        Assert.False(violations.HasError());
    }

    [Fact]
    public void 差し戻しの文言は訂正のことばで書かれる()
    {
        // 規則は取消と共有しているので、**共有した先で操作を取り違えると**
        // 「訂正する」を押したのに「取り消せない」と出る。文言まで含めて固定する。
        var violations = JournalCorrection.ValidateForPosting(
            Correction() with { PostingDate = TransactionDate.AddDays(-1) },
            AccountingFixture.CashSale(TransactionDate),
            PostingContext());

        // **「訂正できません」は本文には出ない**（取消側と同じ理由。qa/02 R25-10）。
        // 操作の語は差し戻しの見出しが持ち、JournalAmendmentServiceTests が固定している。
        // ここが守るのは「取消と共有した本文が、訂正のときも同じ文で出る」ことである。
        Assert.Equal(
            "この伝票はまだ計上されていません。下書きは削除してください。",
            Message(violations, JournalViolationCodes.AmendmentTargetNotPosted));
        Assert.Equal(
            "元の伝票を特定できません（保存されていないか、伝票番号がありません）。",
            Message(violations, JournalViolationCodes.AmendmentTargetUnidentified));
        // **完全一致で固定する**（qa/02 R8-09）。
        Assert.Equal(
            "訂正の計上日（2026/05/19）が、元の伝票の計上日（2026/05/22）より前になっています。",
            Message(violations, JournalViolationCodes.AmendmentBeforeOriginal));
    }

    private static string Message(IReadOnlyList<Violation> violations, string code)
        => violations.Single(v => v.Code == code).Message;

    // --- 訂正をやり直す（ADR-0052） ---

    private static CorrectionResumeContext ResumeContext(
        bool reversed = true, bool alreadyCorrected = false, bool hasDraft = false)
        => new(reversed, alreadyCorrected, hasDraft, AccountingFixture.FiscalYear);

    /// <summary>
    /// <b>取消が済んでいれば、再計上の下書きだけを起こせる。</b> 訂正の下書きを消してしまった利用者の受け皿。
    /// </summary>
    [Fact]
    public void 取消済みなら再計上の下書きだけができる()
    {
        var original = Posted();

        var result = JournalCorrection.Resume(original, CorrectedOn, EnteredAt, ResumeContext());

        Assert.True(result.Resumed);
        var draft = result.Draft!;
        Assert.Equal(EntryType.Correction, draft.EntryType);
        Assert.Equal(EntryStatus.Draft, draft.Status);
        Assert.Equal(original.Id, draft.OriginalEntryId);
        Assert.Equal(original.TransactionDate, draft.TransactionDate);
        Assert.Equal(CorrectedOn, draft.PostingDate);
        Assert.Equal(original.Lines, draft.Lines);
        // **Start が作る再計上と同じ姿である**（同じ関数で作る）。明細の並びは参照が違うので別に見た。
        Assert.Equal(Correction(original: original) with { Lines = draft.Lines }, draft);
    }

    /// <summary>取消が無いのに再計上だけを起こすと、取引が帳簿に二重に載る。</summary>
    [Fact]
    public void 取り消されていなければやり直せない()
    {
        var result = JournalCorrection.Resume(Posted(), CorrectedOn, EnteredAt, ResumeContext(reversed: false));

        Assert.False(result.Resumed);
        Assert.Equal(
            "元の伝票はまだ取り消されていません。訂正は取消と再計上の組で行います。",
            Message(result.Violations, JournalViolationCodes.OriginalNotReversed));
    }

    [Fact]
    public void 訂正の下書きが残っていればやり直せない()
    {
        var result = JournalCorrection.Resume(Posted(), CorrectedOn, EnteredAt, ResumeContext(hasDraft: true));

        Assert.False(result.Resumed);
        Assert.Equal(
            "この伝票の訂正の下書きは既にあります。振替伝票の一覧から、その下書きを開いて直してください。",
            Message(result.Violations, JournalViolationCodes.CorrectionDraftExists));
    }

    [Fact]
    public void 計上済みの訂正があればやり直せない()
    {
        var result = JournalCorrection.Resume(Posted(), CorrectedOn, EnteredAt, ResumeContext(alreadyCorrected: true));

        Assert.False(result.Resumed);
        Assert.Equal(
            "この伝票は既に訂正されています。やり直すときは、その訂正の伝票を訂正してください。",
            Message(result.Violations, JournalViolationCodes.AlreadyCorrected));
    }

    /// <summary>原仕訳の側の規則は Start と同じ（下書きは対象外）。</summary>
    [Fact]
    public void 下書きはやり直せない()
    {
        var result = JournalCorrection.Resume(
            Posted() with { Status = EntryStatus.Draft }, CorrectedOn, EnteredAt, ResumeContext());

        Assert.False(result.Resumed);
        Assert.Contains(result.Violations, v => v.Code == JournalViolationCodes.AmendmentTargetNotPosted);
    }

    [Fact]
    public void やり直しの違反は全件返す()
    {
        var result = JournalCorrection.Resume(
            Posted(), CorrectedOn, EnteredAt, ResumeContext(reversed: false, alreadyCorrected: true, hasDraft: true));

        Assert.Equal(
            [
                JournalViolationCodes.OriginalNotReversed,
                JournalViolationCodes.AlreadyCorrected,
                JournalViolationCodes.CorrectionDraftExists,
            ],
            result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void やり直しの原仕訳がnullなら例外()
        => Assert.Throws<ArgumentNullException>(
            () => JournalCorrection.Resume(null!, CorrectedOn, EnteredAt, ResumeContext()));

    [Fact]
    public void 違反は全件返す()
    {
        var violations = JournalCorrection.ValidateForPosting(
            Correction() with { PostingDate = TransactionDate.AddDays(-1) },
            Posted() with { Id = null, EntryType = EntryType.Reversal, Status = EntryStatus.Draft },
            NotReversed(alreadyCorrected: true));

        Assert.Equal(
            [
                JournalViolationCodes.AmendmentBeforeOriginal,
                JournalViolationCodes.AmendmentTargetUnidentified,
                JournalViolationCodes.AmendmentTargetNotPosted,
                JournalViolationCodes.AmendmentTargetNotAmendable,
                JournalViolationCodes.AlreadyCorrected,
                JournalViolationCodes.OriginalNotReversed,
            ],
            violations.Select(v => v.Code).OrderBy(c => c, StringComparer.Ordinal));
    }
}
