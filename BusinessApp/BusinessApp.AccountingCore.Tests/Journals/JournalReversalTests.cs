namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 計上済みの仕訳を取り消す反対仕訳（docs/04 §5）。
/// </summary>
/// <remarks>
/// 計上済みは変更も削除もしない。取消は<b>帳簿に 1 本足す</b>ことで表す。
/// ここが緩むと ADR-0004 の「規則を迂回する経路を作らない」が崩れる。
/// </remarks>
public class JournalReversalTests
{
    private static readonly DateOnly TransactionDate = new(2026, 5, 20);
    private static readonly DateOnly ReversedOn = new(2026, 6, 10);
    private static readonly DateTimeOffset EnteredAt = new(2026, 6, 10, 9, 0, 0, TimeSpan.FromHours(9));

    /// <summary>会計年度は取消の計上日から呼び出し側が引く。既定は原仕訳と同じ年度。</summary>
    private static ReversalContext Context(bool alreadyReversed = false, FiscalYearId? fiscalYearId = null)
        => new(alreadyReversed, fiscalYearId ?? AccountingFixture.FiscalYear);

    private static JournalEntry Posted(
        JournalEntry? draft = null, int entryNo = 1, EntryType entryType = EntryType.Normal)
        => (draft ?? AccountingFixture.CashSale(TransactionDate)) with
        {
            Status = EntryStatus.Posted,
            EntryType = entryType,
            EntryNo = entryNo,
            PostedAt = new DateTimeOffset(2026, 5, 20, 18, 0, 0, TimeSpan.FromHours(9)),
        };

    [Fact]
    public void 貸借を入れ替えた下書きができる()
    {
        var original = Posted();

        var result = JournalReversal.Reverse(original, ReversedOn, EnteredAt, Context());

        Assert.True(result.Created);
        var reversal = result.Reversal!;
        Assert.Equal(EntryStatus.Draft, reversal.Status);
        Assert.Equal(EntryType.Reversal, reversal.EntryType);
        Assert.Equal(original.Id, reversal.OriginalEntryId);
        Assert.Null(reversal.EntryNo);

        // 総額方式。金額はそのままで、借貸だけが入れ替わる。
        Assert.Equal(
            original.Lines.Select(l => l.DebitCredit.Opposite()),
            reversal.Lines.Select(l => l.DebitCredit));
        Assert.Equal(original.Lines.Select(l => l.Amount), reversal.Lines.Select(l => l.Amount));
        Assert.Equal(original.Lines.Select(l => l.AccountId), reversal.Lines.Select(l => l.AccountId));
        Assert.Equal(original.Lines.Select(l => l.DepartmentId), reversal.Lines.Select(l => l.DepartmentId));
        Assert.Equal(original.Lines.Select(l => l.LineNo), reversal.Lines.Select(l => l.LineNo));
        Assert.True(reversal.IsBalanced);
    }

    [Fact]
    public void 取引日は原仕訳と同じで_計上日だけが後ろにずれる()
    {
        // 帳簿の「取引年月日」は取引そのものを説明する欄であって、訂正作業の日ではない（docs/04 §5）。
        var result = JournalReversal.Reverse(Posted(), ReversedOn, EnteredAt, Context());

        Assert.Equal(TransactionDate, result.Reversal!.TransactionDate);
        Assert.Equal(ReversedOn, result.Reversal.PostingDate);
        Assert.Equal(EnteredAt, result.Reversal.EnteredAt);
    }

    [Fact]
    public void 取消した相手が摘要から分かる()
    {
        var withDescription = Posted() with { Description = "5 月分の売上" };

        var result = JournalReversal.Reverse(withDescription, ReversedOn, EnteredAt, Context());

        // 原仕訳の摘要を消さない。取消だけを見て何が起きたかを追えなければならない。
        Assert.Equal("伝票番号 1 の取消: 5 月分の売上", result.Reversal!.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 摘要が無い原仕訳でも取消と分かる(string? description)
    {
        var result = JournalReversal.Reverse(Posted() with { Description = description }, ReversedOn, EnteredAt, Context());

        Assert.Equal("伝票番号 1 の取消", result.Reversal!.Description);
    }

    [Fact]
    public void 下書きは取り消せない()
    {
        // 下書きは帳簿ではないので、取り消すのではなく削除する。
        var result = JournalReversal.Reverse(AccountingFixture.CashSale(TransactionDate), ReversedOn, EnteredAt, Context());

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.AmendmentTargetNotPosted, result.Violations.Select(v => v.Code));
    }

    public static TheoryData<JournalEntry> Unidentifiable => new()
    {
        Posted() with { Id = null },
        Posted() with { EntryNo = null },
    };

    [Theory]
    [MemberData(nameof(Unidentifiable))]
    public void 原仕訳を特定できなければ取り消せない(JournalEntry original)
    {
        // 識別子も伝票番号も、帳簿の側から原仕訳を指す手段である。
        // どちらかが欠けると相互関連性（規則 5 ⑤一ロ）が切れる。
        var result = JournalReversal.Reverse(original, ReversedOn, EnteredAt, Context());

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.AmendmentTargetUnidentified, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 原仕訳より前の日付では取り消せない()
    {
        var result = JournalReversal.Reverse(Posted(), TransactionDate.AddDays(-1), EnteredAt, Context());

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.AmendmentBeforeOriginal, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 計上したその日に取り消すのは通る()
    {
        // 計上したその日に気づいて取り消すのは、ごく普通の操作である。
        // 比べるのは**計上日**であって取引日ではない（フィクスチャは 2 日ずらしてある）。
        var original = Posted();

        var result = JournalReversal.Reverse(original, original.PostingDate, EnteredAt, Context());

        Assert.True(result.Created);
    }

    [Fact]
    public void 取引日には戻れない()
    {
        // 取引日と計上日を取り違えていると、ここが通ってしまう
        // （取引日は計上日より前なので、取消を原仕訳より前に載せられることになる）。
        var original = Posted();

        var result = JournalReversal.Reverse(original, original.TransactionDate, EnteredAt, Context());

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.AmendmentBeforeOriginal, result.Violations.Select(v => v.Code));
    }

    [Theory]
    [InlineData(EntryType.Reversal)]
    [InlineData(EntryType.Opening)]
    [InlineData(EntryType.Closing)]
    [InlineData(EntryType.Carryover)]
    public void 対象にできない種別の仕訳は取り消せない(EntryType entryType)
    {
        // 取消の連鎖は帳簿を読めなくするだけ。期首残高・決算振替・繰越を反対仕訳で打ち消すと、
        // 残高の前提（I-11・I-12）と繰越の再実行が噛み合わなくなる。
        var result = JournalReversal.Reverse(Posted(entryType: entryType), ReversedOn, EnteredAt, Context());

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.AmendmentTargetNotAmendable, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 訂正の伝票は取り消せる()
    {
        // 訂正を間違えたときに、その訂正を取り消せないと詰む（ADR-0015）。
        var result = JournalReversal.Reverse(
            Posted(entryType: EntryType.Correction), ReversedOn, EnteredAt, Context());

        Assert.True(result.Created);
        Assert.Equal(EntryType.Reversal, result.Reversal!.EntryType);
    }

    [Fact]
    public void 年度をまたぐ取消は_計上日の属する年度に載る()
    {
        // 3 月の仕訳を 4 月に取り消せば、反対仕訳は新しい年度の伝票である。
        // 原仕訳の年度を写すと「作れたのに計上できない」行き止まりになる。
        var lastMarch = new DateOnly(2026, 3, 20);
        var original = Posted(AccountingFixture.CashSale(lastMarch));
        var next = new FiscalYearId(19);

        var result = JournalReversal.Reverse(
            original, new DateOnly(2026, 4, 1), EnteredAt, Context(fiscalYearId: next));

        Assert.True(result.Created);
        Assert.Equal(next, result.Reversal!.FiscalYearId);

        // 取引日は原仕訳のまま（3 月）。年度だけが新しくなる。
        Assert.Equal(lastMarch, result.Reversal.TransactionDate);
        Assert.Equal(new DateOnly(2026, 4, 1), result.Reversal.PostingDate);
    }

    [Fact]
    public void 既に取り消された仕訳は取り消せない()
    {
        // 二重取消は残高を狂わせる。反対仕訳が 2 本残っても、元の取引は 1 回しか無い。
        var result = JournalReversal.Reverse(
            Posted(), ReversedOn, EnteredAt, Context(alreadyReversed: true));

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.AlreadyReversed, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 違反は全件返す()
    {
        // 直しては弾かれを繰り返させない。
        var result = JournalReversal.Reverse(
            AccountingFixture.CashSale(TransactionDate) with { Id = null, EntryType = EntryType.Reversal },
            TransactionDate.AddDays(-1),
            EnteredAt,
            Context(alreadyReversed: true));

        // 件数だけ見ると、規則が 1 つ消えて別の 1 つが増えても気づけない。
        Assert.Equal(
            [
                JournalViolationCodes.AmendmentBeforeOriginal,
                JournalViolationCodes.AmendmentTargetUnidentified,
                JournalViolationCodes.AmendmentTargetNotPosted,
                JournalViolationCodes.AmendmentTargetNotAmendable,
                JournalViolationCodes.AlreadyReversed,
            ],
            result.Violations.Select(v => v.Code).OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void 取消も計上経路を通す_検証を省く道を作らない()
    {
        var original = Posted();
        var reversal = JournalReversal.Reverse(original, ReversedOn, EnteredAt, Context()).Reversal!;

        // 作ったのは下書きなので、そのままでは帳簿に載らない。計上は JournalPosting を通る。
        var posted = JournalPosting.Post(
            reversal,
            AccountingFixture.Context(),
            new EntryNumberSequence(AccountingFixture.FiscalYear, 2),
            new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.FromHours(9)));

        Assert.True(posted.IsPosted);
        Assert.Equal(2, posted.PostedEntry!.EntryNo);
        Assert.Equal(original.Id, posted.PostedEntry.OriginalEntryId);
    }

    [Fact]
    public void 差し戻しの文言は取消のことばで書かれる()
    {
        // 規則は訂正と共有しているので、**共有した先で操作を取り違えると**
        // 「取り消す」を押したのに「訂正できない」と出る。文言まで含めて固定する。
        //
        // **「取り消せません」は本文には出ない。** それは差し戻しの見出しで、
        // JournalPostingRejectedException が押されたボタンから決める（2026-08-31。qa/02 R25-10）。
        // 本文が同じ語を繰り返すと「取り消せません。①…は取り消せません。」になる。
        // 見出しの側は JournalAmendmentServiceTests が固定している。
        var result = JournalReversal.Reverse(
            AccountingFixture.CashSale(TransactionDate), TransactionDate.AddDays(-1), EnteredAt, Context());

        Assert.Equal(
            "この伝票はまだ計上されていません。下書きは削除してください。",
            Message(result, JournalViolationCodes.AmendmentTargetNotPosted));
        Assert.Equal(
            "元の伝票を特定できません（保存されていないか、伝票番号がありません）。",
            Message(result, JournalViolationCodes.AmendmentTargetUnidentified));
        // **完全一致で固定する。** 前半だけを見ていると、日付をはさんだ後半
        //（＝文をつないでいる側）が無防備になる（qa/02 R8-09）。
        Assert.Equal(
            "取消の計上日（2026-05-19）が、元の伝票の計上日（2026-05-22）より前になっています。",
            Message(result, JournalViolationCodes.AmendmentBeforeOriginal));
    }

    [Fact]
    public void 対象にできない種別の差し戻しには種別名と対象が出る()
    {
        var result = JournalReversal.Reverse(
            Posted(entryType: EntryType.Reversal), ReversedOn, EnteredAt, Context());

        Assert.Equal(
            "種別が「取消」の伝票は対象にできません。対象にできるのは通常の伝票と訂正だけです。",
            Message(result, JournalViolationCodes.AmendmentTargetNotAmendable));
    }

    private static string Message(ReversalResult result, string code)
        => result.Violations.Single(v => v.Code == code).Message;

    [Fact]
    public void 原仕訳は何も変わらない()
    {
        var original = Posted();

        JournalReversal.Reverse(original, ReversedOn, EnteredAt, Context());

        Assert.Equal(EntryStatus.Posted, original.Status);
        Assert.Equal(1, original.EntryNo);
        Assert.Equal(DebitCredit.Debit, original.Lines[0].DebitCredit);
    }
}
