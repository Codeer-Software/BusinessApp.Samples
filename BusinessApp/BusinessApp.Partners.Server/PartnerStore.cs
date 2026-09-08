namespace BusinessApp.Partners.Server;

using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 取引先の素性（docs/13 §1-2）のうち、保存の関門が突き合わせるものを読む。
/// </summary>
/// <remarks>
/// <para><b>保存されている値が要るのは、CLB が変更されたフィールドしか送ってこないからである</b>
/// （qa/01 F-11）。画面で法人番号だけを直した保存には種別が載らない。
/// そこで検査をやめると、<b>個人事業者に法人番号を後から付けられる</b>——
/// 最後は DDL の CHECK が止めるが、利用者には DB の言葉で書かれた失敗しか見えない。</para>
/// <para>登録（<see cref="PartnerRegistrationStore"/>）とは読む対象が違うので分けてある。</para>
/// </remarks>
public sealed class PartnerStore(IDbAccessor dbAccessor, string dataSourceName)
{
    /// <summary>保存されている素性。取引先が無ければ <c>null</c>。</summary>
    public async Task<PartnerProfile?> FindProfileAsync(PartnerId id)
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            "select entity_type, corporate_number from partners where id = @p1",
            new() { { "@p1", Param(id.Value) } });

        return rows.Count == 0
            ? null
            : new PartnerProfile(
                DbValue.ToDefinedEnum<PartnerEntityType>(rows[0]["entity_type"]),
                DbValue.ToNullableText(rows[0]["corporate_number"]));
    }

    /// <summary>
    /// 名寄せの親として使えるかを見るために要る、その取引先の<b>親子まわりの姿</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>2 つを 1 回で読む。</b> 「その取引先が親を持っているか」と
    /// 「その取引先が誰かの親になっているか」は、どちらも深さ 1 の森（ADR-0028 §2）を守るために要る。
    /// 別々に問い合わせると、関門 1 回で DB を 2 往復することになる。</para>
    /// <para>取引先が無ければ <c>null</c>。<b>新規作成の相手を指しているときは呼ばない</b>——
    /// 仮の識別子は数値として読めないので、呼ぶ側が先に落とす。</para>
    /// </remarks>
    public async Task<PartnerLineage?> FindLineageAsync(PartnerId id)
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            """
            select p.parent_partner_id                                             as parent_id,
                   exists (select 1 from partners c where c.parent_partner_id = p.id) as has_children
              from partners p
             where p.id = @p1
            """,
            new() { { "@p1", Param(id.Value) } });

        return rows.Count == 0
            ? null
            : new PartnerLineage(
                DbValue.ToNullableText(rows[0]["parent_id"]) is string parent
                    ? new PartnerId(long.Parse(parent, System.Globalization.CultureInfo.InvariantCulture))
                    : null,
                Convert.ToInt64(rows[0]["has_children"], System.Globalization.CultureInfo.InvariantCulture) != 0);
    }

    /// <summary>
    /// その取引先を親にしている取引先の<b>種別</b>（重複を除く）。
    /// </summary>
    /// <remarks>
    /// <b>親の側を直す方向を見るために要る。</b> 子から親を見るだけでは、
    /// 「親の種別を変えて食い違わせる」保存が素通りする——ADR-0028 の帰結が
    /// 「親と子のどちらを直す場合も検査が要る」と名指ししていた方向である
    /// （2026-08-31 の自己レビューで、実装されていないことが分かった）。
    /// </remarks>
    public async Task<IReadOnlyList<PartnerEntityType?>> FindChildEntityTypesAsync(PartnerId id)
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            "select distinct entity_type from partners where parent_partner_id = @p1",
            new() { { "@p1", Param(id.Value) } });

        return [.. rows.Select(r => DbValue.ToDefinedEnum<PartnerEntityType>(r["entity_type"]))];
    }

    /// <summary>
    /// 問い合わせ用のパラメータに包む。
    /// </summary>
    /// <remarks>
    /// <b><c>QueryAsync</c> と <c>ExecuteAsync</c> でパラメータ辞書の型が違う</b>（qa/01 C-12）。
    /// </remarks>
    /// <summary>
    /// 同じコードの取引先が既にあれば、<b>その行に保存されている字</b>を返す（<b>大小を無視して探す</b>。自分自身は除く）。
    /// </summary>
    /// <remarks>
    /// <b>大小の畳み方は DB の <c>COLLATE NOCASE</c> に任せる</b>——C# 側で畳むと、
    /// DDL の一意索引と畳み方がずれたときに気づけない（docs/20 §4）。
    /// </remarks>
    public async Task<string?> FindConflictingCodeAsync(string code, PartnerId? id)
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            "select code from partners where code = @p1 collate nocase and (@p2 is null or id <> @p2) limit 1",
            new() { { "@p1", Param(code) }, { "@p2", Param(id?.Value) } });

        return rows.Count == 0 ? null : DbValue.ToText(rows[0]["code"]);
    }

    private static ParamAndRawDbTypeName Param(object? value) => new() { Value = value };
}

/// <summary>
/// 取引先の素性のうち、種別と法人番号の組。
/// </summary>
/// <remarks>
/// <b>この 2 つは互いを縛る</b>（個人事業者に法人番号は指定されない。docs/13 §1-2）ので、
/// 片方だけ読んでも判定できない。組で返す。
/// </remarks>
/// <param name="EntityType">種別。<c>null</c> は未分類。</param>
/// <param name="CorporateNumber">法人番号。<c>null</c> は未入力。</param>
public sealed record PartnerProfile(PartnerEntityType? EntityType, string? CorporateNumber);

/// <summary>
/// 名寄せの親子まわりの姿（ADR-0028 §2 の「深さ 1 の森」を判定するのに要る 2 つ）。
/// </summary>
/// <param name="ParentId">その取引先が指している親。<c>null</c> なら根である。</param>
/// <param name="HasChildren">その取引先を親にしている取引先があるか。</param>
public readonly record struct PartnerLineage(PartnerId? ParentId, bool HasChildren);
