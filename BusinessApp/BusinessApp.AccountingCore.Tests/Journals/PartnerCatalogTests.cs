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
    /// <b>読む前の目録では引けない</b>——空の目録で検証を通すと、参照している取引先が全部「マスタに無い」になる。
    /// 「選べる取引先があるか」だけは読む前でも答える。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 読む前の目録では取引先を引けない(bool hasSelectable)
    {
        var unloaded = PartnerCatalog.Unloaded(hasSelectable);

        Assert.False(unloaded.IsLoaded);
        Assert.Equal(hasSelectable, unloaded.HasSelectable);
        var error = Assert.Throws<InvalidOperationException>(() => unloaded.Find(AccountingFixture.Partner));
        Assert.Contains("WithPartnersAsync", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 読んだ目録は空でも引ける()
    {
        var empty = new PartnerCatalog([], hasSelectable: true);

        Assert.True(empty.IsLoaded);
        Assert.Null(empty.Find(AccountingFixture.Partner));
    }
}
