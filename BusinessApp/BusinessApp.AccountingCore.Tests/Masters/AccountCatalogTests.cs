namespace BusinessApp.AccountingCore.Tests.Masters;

using BusinessApp.AccountingCore.Masters;

/// <summary>科目の参照。検証が「マスタにない科目」を確実に弾けることの土台。</summary>
public class AccountCatalogTests
{
    private static readonly AccountDefinition Cash = new("1100", "1100", "現金", AccountCategory.Asset);

    [Fact]
    public void 登録した科目を引ける()
    {
        var catalog = new AccountCatalog([Cash]);

        Assert.Equal(Cash, catalog.Find("1100"));
    }

    [Fact]
    public void 無い科目はnullを返す()
    {
        var catalog = new AccountCatalog([Cash]);

        Assert.Null(catalog.Find("9999"));
        Assert.Null(catalog.Find(null!));
    }

    [Fact]
    public void 科目IDは大文字小文字を区別する()
    {
        var catalog = new AccountCatalog([Cash with { Id = "abc" }]);

        Assert.NotNull(catalog.Find("abc"));
        Assert.Null(catalog.Find("ABC"));
    }

    [Fact]
    public void nullの一覧では作れない()
    {
        Assert.Throws<ArgumentNullException>(() => new AccountCatalog(null!));
    }

    [Fact]
    public void 科目IDが重複していれば作れない()
    {
        Assert.Throws<ArgumentException>(() => new AccountCatalog([Cash, Cash with { Name = "小口現金" }]));
    }

    [Fact]
    public void 無効な科目も引ける()
    {
        // 入力候補から外れても、過去データの表示・検索は妨げない（docs/04 §6）。
        var retired = Cash with { Id = "5900", Name = "廃止した科目", IsActive = false };
        var catalog = new AccountCatalog([retired]);

        Assert.Equal(retired, catalog.Find("5900"));
    }
}
