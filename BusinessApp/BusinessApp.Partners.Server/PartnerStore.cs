namespace BusinessApp.Partners.Server;

using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 取引先の素性（docs/07 §1-2）のうち、保存の関門が突き合わせるものを読む。
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
    /// 問い合わせ用のパラメータに包む。
    /// </summary>
    /// <remarks>
    /// <b><c>QueryAsync</c> と <c>ExecuteAsync</c> でパラメータ辞書の型が違う</b>（qa/01 C-12）。
    /// </remarks>
    private static ParamAndRawDbTypeName Param(object? value) => new() { Value = value };
}

/// <summary>
/// 取引先の素性のうち、種別と法人番号の組。
/// </summary>
/// <remarks>
/// <b>この 2 つは互いを縛る</b>（個人事業者に法人番号は指定されない。docs/07 §1-2）ので、
/// 片方だけ読んでも判定できない。組で返す。
/// </remarks>
/// <param name="EntityType">種別。<c>null</c> は未分類。</param>
/// <param name="CorporateNumber">法人番号。<c>null</c> は未入力。</param>
public sealed record PartnerProfile(PartnerEntityType? EntityType, string? CorporateNumber);
