namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 訂正（取消 ＋ 正しい内容の再計上）。docs/04 §5・ADR-0015。
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
    public void 外部投入の印は再計上に写さない()
    {
        // 冪等キーは一意（I-14）。写せば必ず衝突して、訂正そのものができなくなる。
        var original = Posted() with
        {
            SourceComponent = "expense",
            SourceDocumentId = "EXP-001",
            IdempotencyKey = "expense/EXP-001",
        };

        var correction = JournalCorrection.Start(original, CorrectedOn, EnteredAt, StartContext()).Drafts!.Value.Correction;

        Assert.Null(correction.SourceComponent);
        Assert.Null(correction.SourceDocumentId);
        Assert.Null(correction.IdempotencyKey);
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

        Assert.Contains("訂正できない", Message(violations, JournalViolationCodes.AmendmentTargetNotPosted), StringComparison.Ordinal);
        Assert.Contains("訂正できない", Message(violations, JournalViolationCodes.AmendmentTargetUnidentified), StringComparison.Ordinal);
        Assert.StartsWith("訂正の計上日", Message(violations, JournalViolationCodes.AmendmentBeforeOriginal), StringComparison.Ordinal);
    }

    private static string Message(IReadOnlyList<Violation> violations, string code)
        => violations.Single(v => v.Code == code).Message;

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
