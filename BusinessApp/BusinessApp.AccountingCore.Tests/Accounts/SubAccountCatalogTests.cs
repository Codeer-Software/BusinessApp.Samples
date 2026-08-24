namespace BusinessApp.AccountingCore.Tests.Accounts;

using BusinessApp.AccountingCore.Accounts;

/// <summary>補助科目の参照。補助元帳は補助科目が正しい親に属していることを前提にする。</summary>
public class SubAccountCatalogTests
{
    private static readonly SubAccountDefinition MainBank =
        new(new SubAccountId(1), new AccountId(1), "01", "みずほ銀行");

    [Fact]
    public void 登録した補助科目を引ける()
    {
        Assert.Equal(MainBank, new SubAccountCatalog([MainBank]).Find(new SubAccountId(1)));
    }

    [Fact]
    public void 無い補助科目はnullを返す()
    {
        Assert.Null(new SubAccountCatalog([MainBank]).Find(new SubAccountId(999)));
    }

    [Fact]
    public void 補助科目は親の勘定科目を持つ()
    {
        Assert.Equal(new AccountId(1), MainBank.AccountId);
    }

    [Fact]
    public void nullの一覧では作れない()
    {
        Assert.Throws<ArgumentNullException>(() => new SubAccountCatalog(null!));
    }
}
