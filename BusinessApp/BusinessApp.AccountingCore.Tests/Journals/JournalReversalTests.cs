namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
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

        var result = JournalReversal.Reverse(original, ReversedOn, EnteredAt);

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
        var result = JournalReversal.Reverse(Posted(), ReversedOn, EnteredAt);

        Assert.Equal(TransactionDate, result.Reversal!.TransactionDate);
        Assert.Equal(ReversedOn, result.Reversal.PostingDate);
        Assert.Equal(EnteredAt, result.Reversal.EnteredAt);
    }

    [Fact]
    public void 取消した相手が摘要から分かる()
    {
        var withDescription = Posted() with { Description = "5 月分の売上" };

        var result = JournalReversal.Reverse(withDescription, ReversedOn, EnteredAt);

        // 原仕訳の摘要を消さない。取消だけを見て何が起きたかを追えなければならない。
        Assert.Equal("伝票番号 1 の取消: 5 月分の売上", result.Reversal!.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 摘要が無い原仕訳でも取消と分かる(string? description)
    {
        var result = JournalReversal.Reverse(Posted() with { Description = description }, ReversedOn, EnteredAt);

        Assert.Equal("伝票番号 1 の取消", result.Reversal!.Description);
    }

    [Fact]
    public void 下書きは取り消せない()
    {
        // 下書きは帳簿ではないので、取り消すのではなく削除する。
        var result = JournalReversal.Reverse(AccountingFixture.CashSale(TransactionDate), ReversedOn, EnteredAt);

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.ReversalTargetNotPosted, result.Violations.Select(v => v.Code));
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
        var result = JournalReversal.Reverse(original, ReversedOn, EnteredAt);

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.ReversalTargetUnidentified, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 原仕訳より前の日付では取り消せない()
    {
        var result = JournalReversal.Reverse(Posted(), TransactionDate.AddDays(-1), EnteredAt);

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.ReversalBeforeOriginal, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 同じ日に取り消すのは通る()
    {
        // 計上したその日に気づいて取り消すのは、ごく普通の操作である。
        var result = JournalReversal.Reverse(Posted(), TransactionDate, EnteredAt);

        Assert.True(result.Created);
    }

    [Fact]
    public void 取消の取消は作れない()
    {
        // 元に戻したいなら、同じ内容の仕訳を計上し直す。取消の連鎖は帳簿を読めなくするだけ。
        var result = JournalReversal.Reverse(Posted(entryType: EntryType.Reversal), ReversedOn, EnteredAt);

        Assert.False(result.Created);
        Assert.Contains(JournalViolationCodes.ReversalOfReversal, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void 既に取り消された仕訳は取り消せない()
    {
        // 二重取消は残高を狂わせる。反対仕訳が 2 本残っても、元の取引は 1 回しか無い。
        var result = JournalReversal.Reverse(
            Posted(), ReversedOn, EnteredAt, new ReversalContext(IsAlreadyReversed: true));

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
            new ReversalContext(IsAlreadyReversed: true));

        Assert.Equal(5, result.Violations.Count);
    }

    [Fact]
    public void 取消も計上経路を通す_検証を省く道を作らない()
    {
        var original = Posted();
        var reversal = JournalReversal.Reverse(original, ReversedOn, EnteredAt).Reversal!;

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
    public void 原仕訳は何も変わらない()
    {
        var original = Posted();

        JournalReversal.Reverse(original, ReversedOn, EnteredAt);

        Assert.Equal(EntryStatus.Posted, original.Status);
        Assert.Equal(1, original.EntryNo);
        Assert.Equal(DebitCredit.Debit, original.Lines[0].DebitCredit);
    }
}
