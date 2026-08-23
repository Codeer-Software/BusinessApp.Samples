namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 違反の表現。画面はコードで分岐するので、コードと文言を混ぜない。
/// コードの値そのものは、それを出すモジュールが持つ（Journals なら JournalViolationCodes）。
/// </summary>
public class ViolationTests
{
    [Fact]
    public void 伝票全体の違反は行番号を持たない()
    {
        var violation = new Violation("I-01", "貸借が一致していない。");

        Assert.Null(violation.LineNo);
        Assert.Equal("[I-01] 貸借が一致していない。", violation.ToString());
    }

    [Fact]
    public void 明細の違反は行番号を伴う()
    {
        var violation = new Violation("I-13", "部門が要る。", 2);

        Assert.Equal("[I-13] 2 行目: 部門が要る。", violation.ToString());
    }
}
