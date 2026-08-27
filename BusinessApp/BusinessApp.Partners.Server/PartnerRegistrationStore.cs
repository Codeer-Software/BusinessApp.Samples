namespace BusinessApp.Partners.Server;

using System.Globalization;

using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 取引先の名称と、適格請求書発行事業者の登録を読む。
/// </summary>
/// <remarks>
/// <para><b>計上のたびに引く取引先だけを読む。</b> 会計マスタ（科目・部門・税区分）は全件先読みだが
/// （<c>AccountingMasterLoader</c>）、取引先は数が読めない——名寄せ前の実データでは
/// 千件を超えうるので、同じやり方をすると 1 伝票の計上のために全件を運ぶことになる。</para>
/// <para>型付き識別子と生の <c>long</c> の変換をここに閉じ込める（ADR-0014）。</para>
/// </remarks>
public sealed class PartnerRegistrationStore(IDbAccessor dbAccessor, string dataSourceName)
{
    /// <summary>取引先の現在の名称。無ければ <c>null</c>。</summary>
    public async Task<string?> FindNameAsync(PartnerId id)
    {
        var rows = await QueryAsync("select name from partners where id = @p1", id.Value);
        return rows.Count == 0 ? null : DbValue.ToText(rows[0]["name"]);
    }

    /// <summary>1 つの取引先の登録を全部読む。<b>期間の判定はここでしない</b>。</summary>
    /// <remarks>
    /// <para>絞り込みを SQL に書かないのは、<b>「その日の登録」を決める規則を
    /// <see cref="InvoiceRegistrationHistory"/> の 1 か所に置く</b>ためである。
    /// SQL と C# の両方に境界の条件を書くと、片方だけ直したときに
    /// 「画面では有効なのに写しが空」という形で静かにずれる。</para>
    /// <para>1 つの取引先の登録は多くて数件（再登録のたびに 1 行）なので、全部読んでよい。</para>
    /// </remarks>
    public async Task<IReadOnlyList<InvoiceRegistration>> LoadRegistrationsAsync(PartnerId id)
    {
        var rows = await QueryAsync(
            """
            select registration_no, valid_from, ended_on
            from partner_invoice_registrations where partner_id = @p1
            """,
            id.Value);

        return rows.Select(r => new InvoiceRegistration(
            DbValue.ToText(r["registration_no"]),
            DbValue.ToDate(r["valid_from"]),
            DbValue.IsNull(r["ended_on"]) ? null : DbValue.ToDate(r["ended_on"]))).ToList();
    }

    /// <summary>
    /// 同じ取引先に、同じ日から始まる<b>別の</b>登録があるか。
    /// </summary>
    /// <remarks>
    /// <b>「別の」を行の識別子で決める。</b> 登録番号が一致するかどうかで自分自身を見分けようとすると、
    /// 番号が差分に載っていない更新（他の項目だけ直した保存）を自分自身と区別できない。
    /// </remarks>
    /// <param name="id">保存しようとしている登録。新規なら <c>null</c>。</param>
    /// <remarks>
    /// <para><b><c>date()</c> で包んで比べる。</b> CLB は日付の列に
    /// <c>"2023-10-01 00:00:00"</c> と時刻付きで書く（アプリ全体でそう。2026-08-26 実測）ので、
    /// <c>valid_from = '2023-10-01'</c> という文字列の比較は<b>いつも外れる</b>。
    /// 外れても例外は出ず、<b>二重登録が黙って通る</b>だけである
    /// （実機操作テストで発見。qa/03 L-12）。</para>
    /// </remarks>
    public async Task<bool> HasOtherRegistrationFromAsync(PartnerId partnerId, DateOnly validFrom, long? id)
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            """
            select 1 from partner_invoice_registrations
             where partner_id = @p1 and date(valid_from) = @p2 and (@p3 is null or id <> @p3)
            """,
            new()
            {
                { "@p1", Param(partnerId.Value) },
                { "@p2", Param(validFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) },
                { "@p3", Param(id) },
            });

        return rows.Count > 0;
    }

    /// <summary>
    /// 登録 1 行が指している取引先。<b>更新の差分に取引先が載っていないときに使う。</b>
    /// </summary>
    /// <remarks>
    /// CLB は変更されたフィールドしか送ってこない（qa/01 F-11）ので、
    /// 画面で登録年月日だけを直した保存には取引先が載らない。
    /// <b>そこで検査をやめると、二重登録が黙って通る</b>（2026-08-26 の自己レビュー指摘。qa/02）。
    /// </remarks>
    public async Task<PartnerId?> FindPartnerOfAsync(long registrationId)
    {
        var rows = await QueryAsync(
            "select partner_id from partner_invoice_registrations where id = @p1", registrationId);

        return rows.Count == 0 ? null : new PartnerId(DbValue.ToLong(rows[0]["partner_id"]));
    }

    private async Task<IReadOnlyList<IDictionary<string, object>>> QueryAsync(string sql, long parameter)
        => await dbAccessor.QueryAsync(dataSourceName, sql, new() { { "@p1", Param(parameter) } });

    /// <summary>
    /// 問い合わせ用のパラメータに包む。
    /// </summary>
    /// <remarks>
    /// <b><c>QueryAsync</c> と <c>ExecuteAsync</c> でパラメータ辞書の型が違う</b>（qa/01 C-12）。
    /// 包み忘れても <c>new()</c> の型推論が通してしまい、実行時に Dapper が落とす。
    /// </remarks>
    private static ParamAndRawDbTypeName Param(object? value) => new() { Value = value };
}
