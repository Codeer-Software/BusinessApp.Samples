namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>伝票の集計（I-01）。金額は正で持ち、向きは借方貸方が表す。</summary>
public class JournalEntryTests
{
    private static readonly DateOnly Ordinary = new(2026, 5, 20);

    [Fact]
    public void 借方と貸方を別々に合計する()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 30_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 20_000),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.OtherPayable, 50_000));

        Assert.Equal(Yen.From(50_000), entry.DebitTotal);
        Assert.Equal(Yen.From(50_000), entry.CreditTotal);
        Assert.True(entry.IsBalanced);
    }

    [Fact]
    public void 貸借がずれていれば不一致になる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 50_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 49_999));

        Assert.False(entry.IsBalanced);
        Assert.Equal(Yen.From(1), entry.DebitTotal - entry.CreditTotal);
    }

    [Fact]
    public void 明細のない伝票は貸借零で一致する()
    {
        var entry = AccountingFixture.Entry(Ordinary);

        Assert.Equal(Yen.Zero, entry.DebitTotal);
        Assert.Equal(Yen.Zero, entry.CreditTotal);
        Assert.True(entry.IsBalanced);
    }

    [Fact]
    public void 明細の順序を変えても合計は変わらない()
    {
        var lines = new[]
        {
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 10_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 20_000),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.OtherPayable, 30_000),
        };

        var forward = AccountingFixture.Entry(Ordinary, lines);
        var reversed = AccountingFixture.Entry(Ordinary, lines.Reverse().ToArray());

        Assert.Equal(forward.DebitTotal, reversed.DebitTotal);
        Assert.Equal(forward.CreditTotal, reversed.CreditTotal);
    }

    [Fact]
    public void 明細の取引先が伝票のものより優先される()
    {
        // **帳簿に載る取引先はこの値である**（docs/10 §4-1）。写しを書く LedgerSnapshotWriter と
        // 計上の関門（E-PARTNER-REQUIRED）が同じ規則を見るために、ここ 1 か所に置いてある。
        var line = AccountingFixture.Line(
            1, DebitCredit.Debit, AccountingFixture.Cash, 1_000, partner: AccountingFixture.Partner);
        var entry = AccountingFixture.Entry(Ordinary, line) with { PartnerId = AccountingFixture.OtherPartner };

        Assert.Equal(AccountingFixture.Partner, entry.PartnerOf(line));
    }

    [Fact]
    public void 明細が取引先を持たなければ伝票のものを使う()
    {
        var line = AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000);
        var entry = AccountingFixture.Entry(Ordinary, line) with { PartnerId = AccountingFixture.OtherPartner };

        Assert.Equal(AccountingFixture.OtherPartner, entry.PartnerOf(line));
    }

    [Fact]
    public void どちらも持たなければ取引先は無い()
    {
        var line = AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000);

        Assert.Null(AccountingFixture.Entry(Ordinary, line).PartnerOf(line));
    }

    // --- 基準日（docs/11 §5-2） ---

    /// <summary>
    /// <b>行の基準日は「課税仕入れの日、無ければ伝票の取引日」</b>である。
    /// </summary>
    /// <remarks>
    /// <b>定義はここにしか置かない。</b> 計上時の登録番号の写しと、取消・訂正の確認文が
    /// <b>同じ日を見る</b>——写した日と断りの日が違うと、帳簿と画面が別の課税期間の話をする。
    /// </remarks>
    [Fact]
    public void 行の基準日は課税仕入れの日で取引日を上書きする()
    {
        var withTaxPoint = AccountingFixture
            .Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000) with
        { TaxPoint = new DateOnly(2026, 3, 20) };
        var withoutTaxPoint = AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000);
        var entry = AccountingFixture.Entry(Ordinary, withTaxPoint, withoutTaxPoint);

        Assert.Equal(new DateOnly(2026, 3, 20), entry.BasisDateOf(withTaxPoint));
        Assert.Equal(Ordinary, entry.BasisDateOf(withoutTaxPoint));
    }

    [Fact]
    public void 行を渡さなければ基準日は決められない()
        => Assert.Throws<ArgumentNullException>(
            () => AccountingFixture.Entry(Ordinary).BasisDateOf(null!));

    /// <summary>
    /// <b>伝票の基準日は、いちばん古い行の基準日</b>である。
    /// </summary>
    /// <remarks>
    /// <b>1 行でも前の課税期間に届けば届く。</b> 遅い行に合わせると、
    /// 前の期の税額が動くのに黙ることになる。
    /// <b>古い日を後ろの行に置く</b>ので、最初の行で止める実装はここで落ちる。
    /// </remarks>
    [Fact]
    public void 伝票の基準日はいちばん古い行の日である()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000) with
            { TaxPoint = new DateOnly(2026, 6, 10) },
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000) with
            { TaxPoint = new DateOnly(2026, 3, 20) });

        Assert.Equal(new DateOnly(2026, 3, 20), entry.EarliestBasisDate);
    }

    /// <summary>
    /// <b>明細が無ければ伝票の取引日</b>。下書きは明細 0 行で作れる。
    /// </summary>
    [Fact]
    public void 明細が無ければ伝票の基準日は取引日である()
        => Assert.Equal(Ordinary, AccountingFixture.Entry(Ordinary).EarliestBasisDate);

    /// <summary>
    /// <b>課税仕入れの日が 1 行も入っていなければ、伝票の取引日</b>になる。
    /// </summary>
    /// <remarks>
    /// <b>いまの製品で起きるのはこの形だけ</b>である（画面が <c>tax_point</c> を入力させない）。
    /// <b>取引日を返す経路が「明細が無いとき」だけになっていないこと</b>を、ここで釘付けする。
    /// </remarks>
    [Fact]
    public void 課税仕入れの日が無ければ伝票の基準日は取引日である()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.OtherPayable, 1_000));

        Assert.Equal(Ordinary, entry.EarliestBasisDate);
    }
}
