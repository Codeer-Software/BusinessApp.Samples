namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.Partners;

/// <summary>
/// 計上検証が見る取引先。<b>伝票が参照している取引先だけ</b>を持つ（全件は読まない）。
/// </summary>
/// <param name="Id">取引先の識別子。</param>
/// <param name="Name">名称（断りの文に出す）。</param>
/// <param name="IsActive">有効か。無効にした取引先は新たな計上に使えない（科目・補助科目・部門と同じ）。</param>
public sealed record PartnerDefinition(PartnerId Id, string Name, bool IsActive);

/// <summary>
/// 取引先の目録。<b>伝票が参照している分だけ</b>と、「選べる取引先が 1 件でもあるか」を持つ。
/// </summary>
/// <remarks>
/// <para><b>科目・部門の目録と違い、全件は持たない。</b> 取引先は数千件になりうる（docs/13）ので、
/// 呼び出し側が<b>伝票の取引先と明細の取引先の識別子を集めて、その分だけ読んで渡す</b>
/// （<c>AccountingMasterLoader</c>。ADR-0008——ドメインは DB を知らない）。
/// 目録に無い識別子は「マスタに無い」と判定される——<b>読み忘れと不在を区別しない</b>ので、
/// 呼び出し側は参照している識別子を漏らさず渡す義務を負う（そのテストは Server 側にある）。
/// <b>「まだ読んでいない」状態はこの型に無い</b>——読む前は <see cref="AccountingMasters"/>（取引先を持たない型）で、
/// 読んで初めて <see cref="PostingContext"/> になる。足し忘れはコンパイルで落ちる。</para>
/// <para><b>「選べる取引先があるか」は別に持つ。</b> 参照している分だけでは分からない（1 件も参照していない伝票で
/// 「取引先を選んでください」と言ってよいかの判定に要る。docs/21 §2-3）。</para>
/// </remarks>
public sealed class PartnerCatalog
{
    private readonly IReadOnlyDictionary<PartnerId, PartnerDefinition> _byId;

    /// <param name="partners">伝票が参照している取引先。</param>
    /// <param name="hasSelectable">取引先マスタに、選べる（有効な）取引先が 1 件でもあるか。</param>
    public PartnerCatalog(IEnumerable<PartnerDefinition> partners, bool hasSelectable)
    {
        ArgumentNullException.ThrowIfNull(partners);
        _byId = partners.ToDictionary(p => p.Id);
        HasSelectable = hasSelectable;
    }

    /// <summary>取引先マスタに、選べる（有効な）取引先が 1 件でもあるか。</summary>
    public bool HasSelectable { get; }

    /// <summary>目録に無ければ <c>null</c>（マスタに無いか、呼び出し側が読んでいない）。</summary>
    public PartnerDefinition? Find(PartnerId partnerId)
        => _byId.TryGetValue(partnerId, out var partner) ? partner : null;
}
