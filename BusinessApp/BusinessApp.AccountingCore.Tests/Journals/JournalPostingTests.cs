namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>下書きから計上への遷移（docs/10 §5）。ここを通らない計上経路を作らない。</summary>
public class JournalPostingTests
{
    private static readonly DateOnly Ordinary = new(2026, 5, 20);
    private static readonly DateTimeOffset PostedAt = new(2026, 5, 21, 9, 30, 0, TimeSpan.FromHours(9));

    private static EntryNumberSequence Sequence => EntryNumberSequence.StartOf(AccountingFixture.FiscalYear);

    [Fact]
    public void 計上すると伝票番号と計上日時が付く()
    {
        var result = JournalPosting.Post(AccountingFixture.CashSale(Ordinary), AccountingFixture.Context(), Sequence, PostedAt);

        Assert.True(result.IsPosted);
        Assert.Equal(EntryStatus.Posted, result.PostedEntry!.Status);
        Assert.Equal(1, result.PostedEntry.EntryNo);
        Assert.Equal(PostedAt, result.PostedEntry.PostedAt);
        Assert.Equal(2, result.NextSequence!.Value.NextValue);
    }

    [Fact]
    public void 計上しても入力年月日は動かない()
    {
        // 入力年月日は「最初に記録された日時」であり、計上で書き換わってはいけない。
        var draft = AccountingFixture.CashSale(Ordinary);

        var result = JournalPosting.Post(draft, AccountingFixture.Context(), Sequence, PostedAt);

        Assert.Equal(draft.EnteredAt, result.PostedEntry!.EnteredAt);
    }

    [Fact]
    public void 検証に落ちれば計上されず採番も進まない()
    {
        var unbalanced = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 99));

        var result = JournalPosting.Post(unbalanced, AccountingFixture.Context(), Sequence, PostedAt);

        Assert.False(result.IsPosted);
        Assert.Null(result.NextSequence);
        Assert.Contains(result.Violations, v => v.Code == JournalViolationCodes.Unbalanced);
    }

    [Fact]
    public void 別の年度の採番からは番号を出せない()
    {
        var otherYear = EntryNumberSequence.StartOf(AccountingFixture.OtherFiscalYear);

        var result = JournalPosting.Post(AccountingFixture.CashSale(Ordinary), AccountingFixture.Context(), otherYear, PostedAt);

        Assert.False(result.IsPosted);
        Assert.Contains(result.Violations, v => v.Code == JournalViolationCodes.FiscalYearMismatch);
    }

    [Fact]
    public void 計上済みの伝票をもう一度計上できない()
    {
        var result = JournalPosting.Post(AccountingFixture.CashSale(Ordinary), AccountingFixture.Context(), Sequence, PostedAt);
        var again = JournalPosting.Post(result.PostedEntry!, AccountingFixture.Context(), result.NextSequence!.Value, PostedAt);

        Assert.False(again.IsPosted);
        Assert.Contains(again.Violations, v => v.Code == JournalViolationCodes.AlreadyPosted);
    }

    [Fact]
    public void 続けて計上すると番号が連番になる()
    {
        var context = AccountingFixture.Context();

        var first = JournalPosting.Post(AccountingFixture.CashSale(Ordinary), context, Sequence, PostedAt);
        var second = JournalPosting.Post(AccountingFixture.CashSale(Ordinary), context, first.NextSequence!.Value, PostedAt);

        Assert.Equal(1, first.PostedEntry!.EntryNo);
        Assert.Equal(2, second.PostedEntry!.EntryNo);
    }

    [Fact]
    public void nullでは計上できない()
    {
        Assert.Throws<ArgumentNullException>(
            () => JournalPosting.Post(null!, AccountingFixture.Context(), Sequence, PostedAt));
        Assert.Throws<ArgumentNullException>(
            () => JournalPosting.Post(AccountingFixture.CashSale(Ordinary), null!, Sequence, PostedAt));
    }
}
