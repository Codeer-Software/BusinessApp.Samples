namespace BusinessApp.AccountingCore.Tests.Validation;

using BusinessApp.AccountingCore.Validation;

/// <summary>違反の表現。画面はコードで分岐するので、コードと文言を混ぜない。</summary>
public class ViolationTests
{
    [Fact]
    public void 伝票全体の違反は行番号を持たない()
    {
        var violation = new Violation(ViolationCodes.Unbalanced, "貸借が一致していない。");

        Assert.Null(violation.LineNo);
        Assert.Equal("[I-01] 貸借が一致していない。", violation.ToString());
    }

    [Fact]
    public void 明細の違反は行番号を伴う()
    {
        var violation = new Violation(ViolationCodes.DepartmentMissing, "部門が要る。", 2);

        Assert.Equal("[I-13] 2 行目: 部門が要る。", violation.ToString());
    }

    [Fact]
    public void 不変条件のコードは番号をそのまま使う()
    {
        Assert.Equal("I-01", ViolationCodes.Unbalanced);
        Assert.Equal("I-03", ViolationCodes.PeriodNotFound);
        Assert.Equal("I-04", ViolationCodes.PeriodClosed);
        Assert.Equal("I-06", ViolationCodes.OriginalEntryMissing);
        Assert.Equal("I-13", ViolationCodes.DepartmentMissing);
    }
}
