namespace BusinessApp.ServerSupport;

using System.Globalization;

/// <summary>
/// 差し戻しの文を<b>「見出し。①…②…」</b>の形に組む（docs/21 §2-6 の (b)）。<b>形の定義はここ 1 か所だけにある。</b>
/// </summary>
/// <remarks>
/// <para><b>会計コアの仕訳の関門も、マスタ・自社情報・取引先・登録番号の関門も、ここを通る。</b>
/// 関門ごとに組むと、同じ差し戻しが別物に見える（1 か所で組むのは Claude の設計）。
/// 束ねて返すのは開発者の決定（2026-09-20。docs/21 §2-6）である。</para>
/// <para><b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。qa/01 D-12）。
/// 代わりに<b>件数と番号</b>で区切る——1 行に繋がっても「あと何を直すか」が読み取れる。</para>
/// </remarks>
public static class RejectionMessage
{
    /// <summary>
    /// 並べられる番号。<b>⑳まで持つ</b>——理由はたいてい 2〜3 文なので、番号が切れると次の理由が前の理由の続きに見える。
    /// それを超えたら番号なしで続ける。
    /// </summary>
    private static readonly string[] Numbers =
        ["①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩", "⑪", "⑫", "⑬", "⑭", "⑮", "⑯", "⑰", "⑱", "⑲", "⑳"];

    /// <summary>
    /// 見出しと理由から、利用者に見せる 1 本の文を組む。
    /// </summary>
    /// <param name="headline">押したボタンで決まる見出し（「登録できません」ほか。句点は付けない）。</param>
    /// <param name="reasons">理由。各文は句点で終わる。</param>
    /// <remarks>
    /// <para><b>1 件のときは件数も番号も付けない</b>（docs/21 §2-4）。</para>
    /// <para><b>同じ文を畳まない</b>（1 件に減らさない。docs/21 §2-6）——件数が場所の唯一の手がかりになる理由がある
    /// （仕訳の保存の関門は、差分に行番号の載らない明細の違反を行番号なしで言う。qa/01 F-12）。
    /// <b>畳むべき同じ文は、それを作る関門が自分で 1 つにする。</b></para>
    /// <para><b>理由が 0 件なら見出しだけを返す。</b> 関門はどれも「理由があるときだけ投げる」ので、
    /// 本番の経路では起きない。<b>それでも止めない</b>——ここで例外にすると、利用者の語で断るはずの差し戻しが
    /// 定型文に化ける（保存の入口は <see cref="RejectedException"/> の派生だけを利用者の語として渡す。ADR-0051）。</para>
    /// </remarks>
    public static string Compose(string headline, IEnumerable<string> reasons)
    {
        ArgumentException.ThrowIfNullOrEmpty(headline);
        ArgumentNullException.ThrowIfNull(reasons);

        var all = reasons.ToList();
        if (all.Count <= 1)
        {
            return $"{headline}。{string.Concat(all)}";
        }

        var numbered = all.Select((reason, index) => $"{Number(index)}{reason}");
        var count = all.Count.ToString(CultureInfo.InvariantCulture);
        return $"{headline}（{count} 件）。{string.Concat(numbered)}";
    }

    private static string Number(int index)
        => index < Numbers.Length ? Numbers[index] : string.Empty;
}
