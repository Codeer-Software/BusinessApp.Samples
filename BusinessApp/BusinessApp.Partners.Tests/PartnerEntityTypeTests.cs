namespace BusinessApp.Partners.Tests;

using BusinessApp.Partners;

/// <summary>取引先の種別（docs/13 §1-2）。</summary>
public class PartnerEntityTypeTests
{
    [Theory]
    [InlineData(PartnerEntityType.Corporation, "法人")]
    [InlineData(PartnerEntityType.SoleProprietor, "個人事業者")]
    [InlineData(PartnerEntityType.UnincorporatedAssociation, "人格のない社団等")]
    [InlineData(PartnerEntityType.Other, "その他")]
    public void 利用者に見せる名前を持つ(PartnerEntityType type, string expected)
    {
        Assert.Equal(expected, type.DisplayName());
    }

    [Fact]
    public void 知らない種別の名前は求められない()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((PartnerEntityType)99).DisplayName());
    }
}
