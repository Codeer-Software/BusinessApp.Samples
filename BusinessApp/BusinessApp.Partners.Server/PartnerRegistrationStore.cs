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
    /// その取引先の、その日から始まる登録の<b>識別子</b>を全部返す。
    /// </summary>
    /// <remarks>
    /// <para><b>「他にあるか」ではなく「どれがあるか」を返す。</b> 自分自身を除くだけでは足りず、
    /// <b>同じ保存の中で日付が動く行</b>も数えてはいけない（入れ替えが誤って止まる。
    /// 2026-08-31 の自己レビュー）。どれを数えないかは保存の全体を知っている関門が決める。</para>
    /// <para><b><c>date()</c> で包んで比べる。</b> CLB は日付の列に
    /// <c>"2023-10-01 00:00:00"</c> と時刻付きで書く（アプリ全体でそう。2026-08-26 実測）ので、
    /// <c>valid_from = '2023-10-01'</c> という文字列の比較は<b>いつも外れる</b>。
    /// 外れても例外は出ず、<b>二重登録が黙って通る</b>だけである
    /// （実機操作テストで発見。qa/03 L-12）。</para>
    /// </remarks>
    public async Task<IReadOnlyList<long>> FindRegistrationIdsFromAsync(
        PartnerId partnerId, DateOnly validFrom)
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            """
            select id from partner_invoice_registrations
             where partner_id = @p1 and date(valid_from) = @p2
            """,
            new()
            {
                { "@p1", Param(partnerId.Value) },
                { "@p2", Param(validFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) },
            });

        return [.. rows.Select(r => Convert.ToInt64(r["id"], CultureInfo.InvariantCulture))];
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

    /// <summary>
    /// 1 つの取引先の登録の行を、<b>識別子つきで</b>全部読む（期間の検査用）。
    /// </summary>
    /// <remarks>
    /// <para><see cref="LoadRegistrationsAsync"/> と分けてあるのは用途が違うからである——
    /// あちらは計上時の写し（識別子は要らない）、こちらは保存の関門が
    /// 「保存後にできあがる履歴」を組み立てるために、<b>差分の行と保存済みの行を
    /// 識別子で突き合わせる</b>（docs/13 §3-5 R-I4）。</para>
    /// </remarks>
    public async Task<IReadOnlyList<RegistrationRow>> LoadRegistrationRowsAsync(PartnerId id)
    {
        var rows = await QueryAsync(
            """
            select id, valid_from, ended_on, end_reason
            from partner_invoice_registrations where partner_id = @p1
            """,
            id.Value);

        return rows.Select(r => new RegistrationRow(
            DbValue.ToLong(r["id"]),
            DbValue.ToDate(r["valid_from"]),
            DbValue.IsNull(r["ended_on"]) ? null : DbValue.ToDate(r["ended_on"]),
            DbValue.IsNull(r["end_reason"]) ? null : DbValue.ToText(r["end_reason"]))).ToList();
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

/// <summary>保存済みの登録 1 行（期間の検査に要る列だけ）。</summary>
public readonly record struct RegistrationRow(
    long Id, DateOnly ValidFrom, DateOnly? EndedOn, string? EndReason);
