namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Tests.Fixtures;
using BusinessApp.Partners;

/// <summary>
/// 取引先の目録。<b>参照している分だけ</b>を持ち、無いものは <c>null</c>。
/// </summary>
public class PartnerCatalogTests
{
    [Fact]
    public void 目録にある取引先は名前と有効を読み戻せる()
    {
        var catalog = AccountingFixture.Partners();

        var partner = catalog.Find(AccountingFixture.RetiredPartner)!;

        Assert.Equal("取引をやめた先", partner.Name);
        Assert.False(partner.IsActive);
        Assert.True(catalog.Find(AccountingFixture.Partner)!.IsActive);
    }

    /// <summary>読んでいない識別子と、マスタに無い識別子は区別しない（呼び出し側が漏らさず読む）。</summary>
    [Fact]
    public void 目録に無い取引先は見つからない()
        => Assert.Null(AccountingFixture.Partners().Find(AccountingFixture.UnknownPartner));

    /// <summary>「選べる取引先があるか」は目録の中身とは別に持つ（参照している分だけでは分からない）。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 選べる取引先があるかは別に持つ(bool hasSelectable)
        => Assert.Equal(hasSelectable, new PartnerCatalog([], hasSelectable).HasSelectable);

    [Fact]
    public void 一覧を渡さなければ止まる()
        => Assert.Equal(
            "partners",
            Assert.Throws<ArgumentNullException>(() => new PartnerCatalog(null!, hasSelectable: true)).ParamName);

    /// <summary>
    /// <b>取引先を足す前の型は別</b>（<see cref="AccountingMasters"/>）——足して初めて計上検証に渡せる文脈になる。
    /// 足し忘れは実行時ではなくコンパイルで落ちるので、「読む前の目録」という状態はこの型に無い。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 取引先を足して計上検証の文脈になる(bool hasSelectable)
    {
        var masters = AccountingFixture.Masters(hasSelectablePartner: hasSelectable);

        var context = masters.WithPartners(new PartnerCatalog([], hasSelectable));

        Assert.Equal(hasSelectable, context.HasSelectablePartner);
        Assert.Same(masters.Calendar, context.Calendar);
        Assert.Same(masters.Accounts, context.Accounts);
        Assert.Null(context.Partners.Find(AccountingFixture.Partner));
        Assert.Throws<ArgumentNullException>(() => masters.WithPartners(null!));
    }
}
