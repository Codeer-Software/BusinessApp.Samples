namespace BusinessApp.AccountingCore.Tests.Accounts;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;

/// <summary>科目の参照。検証が「マスタにない科目」を確実に弾けることの土台。</summary>
public class AccountCatalogTests
{
    private static readonly AccountDefinition Cash =
        new(new AccountId(1), "1100", "現金", AccountCategory.Asset);

    [Fact]
    public void 登録した科目を引ける()
    {
        var catalog = new AccountCatalog([Cash]);

        Assert.Equal(Cash, catalog.Find(new AccountId(1)));
    }

    [Fact]
    public void 無い科目はnullを返す()
    {
        var catalog = new AccountCatalog([Cash]);

        Assert.Null(catalog.Find(new AccountId(999)));
        Assert.Null(catalog.Find(default));
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
    public void 科目コードが同じでも識別子が違えば別の科目として扱う()
    {
        // コードは利用者が見る自然キー、識別子は DB の主キー。両者を混同しない。
        var another = Cash with { Id = new AccountId(2) };
        var catalog = new AccountCatalog([Cash, another]);

        Assert.Equal(new AccountId(1), catalog.Find(new AccountId(1))?.Id);
        Assert.Equal(new AccountId(2), catalog.Find(new AccountId(2))?.Id);
    }

    [Fact]
    public void 無効な科目も引ける()
    {
        // 入力候補から外れても、過去データの表示・検索は妨げない（docs/10 §6）。
        var retired = Cash with { Id = new AccountId(9), Name = "廃止した科目", IsActive = false };
        var catalog = new AccountCatalog([retired]);

        Assert.Equal(retired, catalog.Find(new AccountId(9)));
    }

    [Fact]
    public void 既定税区分は任意である()
    {
        // 「値が入っていない行の穴埋め」に使わないので、持たない科目があってよい（docs/10 §6）。
        var withDefault = Cash with { DefaultTaxCategoryId = new TaxCategoryId(3) };

        Assert.Null(Cash.DefaultTaxCategoryId);
        Assert.Equal(new TaxCategoryId(3), withDefault.DefaultTaxCategoryId);
    }
}
