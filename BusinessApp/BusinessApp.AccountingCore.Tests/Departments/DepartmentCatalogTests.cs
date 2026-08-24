namespace BusinessApp.AccountingCore.Tests.Departments;

using BusinessApp.AccountingCore.Departments;

/// <summary>部門の参照。損益科目の明細に必須なので、実在と有効を確かめられる必要がある。</summary>
public class DepartmentCatalogTests
{
    private static readonly DepartmentDefinition Sales = new(new DepartmentId(1), "20", "営業部");

    [Fact]
    public void 登録した部門を引ける()
    {
        Assert.Equal(Sales, new DepartmentCatalog([Sales]).Find(new DepartmentId(1)));
    }

    [Fact]
    public void 無い部門はnullを返す()
    {
        Assert.Null(new DepartmentCatalog([Sales]).Find(new DepartmentId(999)));
    }

    [Fact]
    public void 全社共通は既定では立っていない()
    {
        // 「全社共通」は利用者が意図して選ぶときだけ使う枠であり、既定にしない（docs/04 §9-1）。
        Assert.False(Sales.IsCompanyWide);
        Assert.True(new DepartmentDefinition(new DepartmentId(2), "00", "全社共通", IsCompanyWide: true).IsCompanyWide);
    }

    [Fact]
    public void nullの一覧では作れない()
    {
        Assert.Throws<ArgumentNullException>(() => new DepartmentCatalog(null!));
    }
}
