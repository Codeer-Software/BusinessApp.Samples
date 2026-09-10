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
/// 呼び出し側は参照している識別子を漏らさず渡す義務を負う（そのテストは Server 側にある）。</para>
/// <para><b>「まだ読んでいない」は型で表す</b>（<see cref="Unloaded"/>）。読んでいない目録で <see cref="Find"/> を呼ぶと止まる——
/// 空の目録を渡して検証を通すと、参照している取引先が<b>全部「マスタに無い」になる</b>のに、
/// それを人の注意でしか防げない（2026-09-10 の自己レビュー）。</para>
/// <para><b>「選べる取引先があるか」は別に持つ。</b> 参照している分だけでは分からない（1 件も参照していない伝票で
/// 「取引先を選んでください」と言ってよいかの判定に要る。docs/21 §2-3）。読んでいない目録でも答えられる。</para>
/// </remarks>
public sealed class PartnerCatalog
{
    private readonly IReadOnlyDictionary<PartnerId, PartnerDefinition>? _byId;

    /// <param name="partners">伝票が参照している取引先。</param>
    /// <param name="hasSelectable">取引先マスタに、選べる（有効な）取引先が 1 件でもあるか。</param>
    public PartnerCatalog(IEnumerable<PartnerDefinition> partners, bool hasSelectable)
    {
        ArgumentNullException.ThrowIfNull(partners);
        _byId = partners.ToDictionary(p => p.Id);
        HasSelectable = hasSelectable;
    }

    private PartnerCatalog(bool hasSelectable)
    {
        HasSelectable = hasSelectable;
    }

    /// <summary>まだ取引先を読んでいない目録。<see cref="Find"/> は止まる。</summary>
    public static PartnerCatalog Unloaded(bool hasSelectable) => new(hasSelectable);

    /// <summary>取引先マスタに、選べる（有効な）取引先が 1 件でもあるか。</summary>
    public bool HasSelectable { get; }

    /// <summary>伝票が参照している取引先を読んであるか。</summary>
    public bool IsLoaded => _byId is not null;

    /// <summary>目録に無ければ <c>null</c>（マスタに無いか、呼び出し側が読んでいない）。</summary>
    /// <exception cref="InvalidOperationException">まだ読んでいない目録（<see cref="Unloaded"/>）。</exception>
    public PartnerDefinition? Find(PartnerId partnerId)
    {
        if (_byId is null)
        {
            throw new InvalidOperationException(
                "取引先の目録をまだ読んでいない。計上の前に、伝票が参照する取引先を目録に足す（AccountingMasterLoader.WithPartnersAsync）。");
        }

        return _byId.TryGetValue(partnerId, out var partner) ? partner : null;
    }
}
