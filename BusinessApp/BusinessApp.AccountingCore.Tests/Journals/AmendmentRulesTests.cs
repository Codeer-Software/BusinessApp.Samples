namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 取消・訂正に共通する原仕訳の側の規則のうち、<b>公開している 2 つ</b>の契約。
/// </summary>
/// <remarks>
/// <para>取消・訂正の経路そのもの（原仕訳の検証・摘要の組み立て）は
/// <c>JournalReversalTests</c>・<c>JournalCorrectionTests</c> が本番の形で見る。
/// ここで見るのは、<b>アプリケーション層に貸している 2 つ</b>（本文と断り）が
/// 呼び手を問わず同じ答えを返すこと——貸した先のテストは「借りている」ことしか見ない。</para>
/// </remarks>
public class AmendmentRulesTests
{
    private static readonly DateOnly Date = new(2026, 5, 20);

    private static JournalEntry Entry(EntryType type, string? description)
        => AccountingFixture.CashSale(Date) with { EntryType = type, Description = description };

    // --- 本文 -------------------------------------------------------------------

    /// <summary>通常の伝票は剥がさない。同じ形をしていても利用者が書いた文である。</summary>
    [Theory]
    [InlineData("5 月分の現金売上", "5 月分の現金売上")]
    [InlineData("伝票番号 12 の取消: 家賃", "伝票番号 12 の取消: 家賃")]
    [InlineData("  前後に空白  ", "前後に空白")]
    public void 通常の伝票の本文は前後の空白を落とすだけ(string description, string expected)
        => Assert.Equal(expected, AmendmentRules.Body(Entry(EntryType.Normal, description)));

    /// <summary>取消・訂正は、自分が付けた接頭辞を無くなるまで落とす。</summary>
    [Theory]
    [InlineData(EntryType.Reversal, "伝票番号 44 の取消: 5 月分の現金売上", "5 月分の現金売上")]
    [InlineData(EntryType.Correction, "伝票番号 44 の訂正: 5 月分の現金売上", "5 月分の現金売上")]
    [InlineData(EntryType.Correction, "伝票番号 5 の訂正: 伝票番号 3 の取消: 家賃", "家賃")]
    [InlineData(EntryType.Reversal, "伝票番号 44 の取消", "")]
    public void 取消と訂正の本文は接頭辞を落とした残り(EntryType type, string description, string expected)
        => Assert.Equal(expected, AmendmentRules.Body(Entry(type, description)));

    /// <summary>摘要が無ければ空文字（NULL にするかは呼び手が決める）。</summary>
    [Theory]
    [InlineData(EntryType.Normal)]
    [InlineData(EntryType.Reversal)]
    public void 摘要が無ければ本文は空文字(EntryType type)
        => Assert.Equal(string.Empty, AmendmentRules.Body(Entry(type, null)));

    [Fact]
    public void 原仕訳を渡さなければ止まる()
        => Assert.Equal(
            "original",
            Assert.Throws<ArgumentNullException>(() => AmendmentRules.Body(null!)).ParamName);

    // --- 断り -------------------------------------------------------------------

    /// <summary>対象にできない 4 種別それぞれの断り。<b>文の全文を表明する</b>（qa/03 L-17）。</summary>
    [Theory]
    [InlineData(EntryType.Reversal, "取消")]
    [InlineData(EntryType.Opening, "期首残高")]
    [InlineData(EntryType.Closing, "決算振替")]
    [InlineData(EntryType.Carryover, "繰越")]
    public void 対象にできない種別の断り(EntryType type, string label)
    {
        var violation = AmendmentRules.NotAmendableTarget(type);

        Assert.Equal(JournalViolationCodes.AmendmentTargetNotAmendable, violation.Code);
        Assert.Equal(
            $"種別が「{label}」の伝票は対象にできません。対象にできるのは通常の伝票と訂正だけです。",
            violation.Message);
    }

    /// <summary>対象にできる種別で断りを求めるのは呼び手の誤り。黙って断りを作らない。</summary>
    [Theory]
    [InlineData(EntryType.Normal)]
    [InlineData(EntryType.Correction)]
    public void 対象にできる種別に断りは無い(EntryType type)
        => Assert.Equal(
            "type",
            Assert.Throws<ArgumentException>(() => AmendmentRules.NotAmendableTarget(type)).ParamName);
}
