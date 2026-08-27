namespace BusinessApp.Partners;

/// <summary>取引先の種別（docs/07 §1-2）。</summary>
/// <remarks>
/// <para><b>2 値では足りない。</b> 人格のない社団等は法人番号を持ちうるが、
/// 公表は代表者の同意がある場合のみで、法人とも個人事業者とも扱いが違う。</para>
/// <para>DB は NULL 可（＝未分類）である。<b>未分類を列挙子で表さない</b>——
/// 「分類していない」は種別の一種ではなく、種別が無いことだからである。
/// C# 側では <c>PartnerEntityType?</c> の <c>null</c> がそれに当たる。</para>
/// </remarks>
public enum PartnerEntityType
{
    /// <summary>法人（設立登記法人）。</summary>
    Corporation,

    /// <summary>個人事業者。<b>法人番号は指定されない</b>（docs/07 §1-2）。</summary>
    SoleProprietor,

    /// <summary>人格のない社団等。法人番号を持ちうる。</summary>
    UnincorporatedAssociation,

    /// <summary>その他。</summary>
    Other,
}

public static class PartnerEntityTypeExtensions
{
    /// <summary>
    /// 利用者に見せる名前。<b>列挙子をそのまま文言に混ぜない</b>（CLAUDE.md §2-7）。
    /// </summary>
    /// <remarks>
    /// CLB のデザイン enum（<c>Enums/PartnerEntityTypes.enum.json</c>）と一致することを
    /// <c>EnumConsistencyTests</c> が検査する。
    /// </remarks>
    public static string DisplayName(this PartnerEntityType type) => type switch
    {
        PartnerEntityType.Corporation => "法人",
        PartnerEntityType.SoleProprietor => "個人事業者",
        PartnerEntityType.UnincorporatedAssociation => "人格のない社団等",
        PartnerEntityType.Other => "その他",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "知らない取引先の種別。"),
    };
}
