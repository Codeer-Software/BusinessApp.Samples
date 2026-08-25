namespace BusinessApp.AccountingCore.Partners;

/// <summary>
/// 登録の履歴から「その日の登録」を引く（docs/07 §3-1）。
/// </summary>
/// <remarks>
/// <para>引く日付は<b><c>journal_lines.tax_point</c>（課税仕入れを行った日）</b>であって、
/// 計上日ではない。<b>ただし <c>tax_point</c> が空の行は伝票の取引日で引く</b>
/// （呼ぶ側が決める。<c>LedgerSnapshotWriter.TaxPointOf</c>・docs/07 §4-1）。</para>
/// <para><b>ここが決めているのは記録のための選択であって、制度上の判定ではない</b>
/// （[ADR-0018](../../../docs/decisions/0018-帳簿の記載事項は計上時に写して固定する.md)。
/// 税額計算を支配するのは利用者が選んだ税区分であり、相手の登録状況ではない）。</para>
/// </remarks>
public static class InvoiceRegistrationHistory
{
    /// <summary>
    /// <paramref name="date"/> 時点の登録。無ければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para><b>終わりの日を含める</b>（<c>EndedOn == date</c> の行も引く）。
    /// 公表システムの取消年月日・失効年月日が<b>効力の最終日なのか、効力を失った初日なのか</b>は
    /// 一次情報で確認できていない（docs/07 §3-2 が<b>フェーズ 3 送り</b>と決めた論点）。
    /// 含める側に倒したのは、ここが<b>根拠の記録</b>だからである——
    /// 境界の 1 日で番号を落とすと、計上済みは不変なので後から補えない。
    /// 番号が残っていれば、後で解釈が決まったときに読み替えられる。</para>
    /// <para><b>同じ日から始まる登録が 2 件あったら止める。</b> どちらを焼いても
    /// 5 割の確率で誤った番号が<b>永久に</b>残る。黙って選ばない。</para>
    /// </remarks>
    /// <param name="registrations">1 つの取引先の登録（順序は問わない）。</param>
    /// <param name="date">引く日付。</param>
    public static InvoiceRegistration? InEffectOn(IReadOnlyList<InvoiceRegistration> registrations, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var candidates = registrations
            .Where(r => r.ValidFrom <= date && (r.EndedOn is not DateOnly ended || date <= ended))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // 再登録は「前の登録が終わった日に次が始まる」ことがありうるので、
        // 候補が 2 件になるのは異常ではない。新しいほうを採る。
        var newest = candidates.Max(r => r.ValidFrom);
        var newestRows = candidates.Where(r => r.ValidFrom == newest).ToList();

        if (newestRows.Count > 1)
        {
            throw new InvalidOperationException(
                $"{newest:yyyy-MM-dd} から始まる登録が {newestRows.Count} 件ある"
                + $"（{string.Join(" / ", newestRows.Select(r => r.RegistrationNo))}）。"
                + "どれを写すか決められないので、取引先の登録を直すこと。");
        }

        return newestRows[0];
    }
}
