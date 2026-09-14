namespace BusinessApp.ServerSupport;

using System.Globalization;
using System.Text;

/// <summary>
/// マスタと取引先の<b>文字の欄の上限</b>（docs/12 §2-2。2026-09-13 の決着）。
/// </summary>
/// <remarks>
/// <para><b>会計コアと取引先部品にまたがる</b>ので、<see cref="MasterCode"/> と同じくここに置く
/// （ADR-0025 §2「2 つ以上の部品が実際に使うものだけを共有へ出す」）。</para>
/// <para><b>上限で入力を止めない</b>（docs/21 §1）。<b>画面はプレースホルダで先に見せ、
/// 関門が「いま何文字あるか」まで言って断る。</b>
/// <c>MaxLength</c> を使わない理由は <see cref="MasterCode"/> と同じである——
/// HTML の <c>maxlength</c> は<b>黙って切る</b>（qa/01 の A-10。docs/21 §0 が「いちばん悪い」と名指しした形）。</para>
/// <para><b>DDL のトリガが同じ上限を持つ</b>（<c>Designer/ddl/012_text_length.sql</c>）。
/// <b>3 か所（C# の定数・DDL のトリガ・デザインのプレースホルダ）の一致は
/// <c>FieldLengthConsistencyTests</c> が見る</b>（docs/20 §4 の「已むを得ない重複」）。</para>
/// <para><b>受理する集合を DDL と同じにしてある。</b> どちらかが広いと、
/// <b>関門が通した値を DB が拒む</b>（利用者には定型文が出る。qa/03 の L-14・L-28）。
/// そのために <see cref="DescribeProblem"/> は<b>長さだけでなく U+0000 も断る</b>
/// ——DDL は <c>LENGTH()</c> が数えられない値として NUL を拒むからである。</para>
/// </remarks>
public static class MasterTextLength
{
    /// <summary>
    /// 勘定科目・補助科目・部門・税区分・会計年度の<b>名前</b>の上限。
    /// </summary>
    /// <remarks>
    /// <b>市販は 20〜40 が中心で、freee と MF が 30</b>（docs/12 §2-2 のリサーチ）。
    /// <b>摘要と同じ桁で設計している社は 1 社も無い。</b>
    /// </remarks>
    public const int MasterName = 30;

    /// <summary>取引先の<b>名前・カナ</b>の上限。</summary>
    /// <remarks>
    /// <b>法人の正式名称が入る余地</b>を採りつつ、一覧が崩れない側に寄せた（docs/12 §2-2）。
    /// </remarks>
    public const int PartnerName = 100;

    /// <summary>取引先の<b>所在地</b>の上限。</summary>
    /// <remarks>
    /// <b>1 列で持つ</b>と決めてある（docs/13 §1-2）ので、
    /// 市区町村・番地と建物名を 2 欄に分ける社より 1 欄あたりを長く採る。
    /// </remarks>
    public const int Address = 200;

    /// <summary>前後の空白を落とした姿。<b>保存するのはこの形</b>。</summary>
    /// <remarks>
    /// <para><b>落とすのは利用者の意図でない混入だけである</b>（docs/21 §0）。
    /// <c>Trim()</c> の既定は Unicode の空白すべてを落とすので、全角スペース・タブ・改行も落ちる
    /// （<see cref="MasterCode.Normalize"/> と同じ）。</para>
    /// <para><b>呼ぶ側は、落とした姿を差分に書き戻すこと。</b> 比べるときだけ落とすと、
    /// <b>関門が数えた長さと DB が数える長さが食い違う</b>（qa/03 の L-14 の型。qa/02 の R45-02 で実際に踏んだ）。</para>
    /// <para><b><c>null</c> は <c>null</c> のままにする。</b> 空文字に寄せると、
    /// <c>NOT NULL</c> の列に空文字が入って「無いは NULL」（docs/20 §7）が崩れる。</para>
    /// </remarks>
    public static string? Normalize(string? value) => value?.Trim();

    /// <summary>
    /// 通らない理由を利用者の語で返す。<b>通れば <c>null</c>。</b>
    /// </summary>
    /// <param name="label">利用者に見せる欄の名前（「科目名」など）。</param>
    /// <param name="value">入力された値。<b>呼ぶ側が <see cref="Normalize"/> を通しておくこと。</b></param>
    /// <param name="maxLength">この欄の上限。</param>
    /// <remarks>
    /// <para><b>最初に当たった 1 つだけを返す</b>（開発者の指示。2026-09-08。逐語「即エラー。次へ進まない」）。</para>
    /// <para><b>必須かどうかはここで見ない。</b> 空を断るのは欄ごとの決まりで、
    /// <b>長さの関門が「空です」と言い出すと、責任の境目がぼやける</b>。</para>
    /// <para><b>次の一手を入れる</b>（docs/21 §2-6）。上限と現在の文字数だけでは、
    /// 貼り付けた利用者は「どうすればよいか」を自分で組み立てることになる。</para>
    /// </remarks>
    public static string? DescribeProblem(string label, string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // **U+0000 は DDL が断る**ので、ここでも断る（受理する集合を同じにする）。
        // **位置まで言う**——目に見えない字は、言われないと利用者が探せない（`MasterCode` と同じ作法）。
        var position = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            position++;
            if (rune.Value == 0)
            {
                return $"「{label}」の {position.ToString(CultureInfo.InvariantCulture)} 文字目に、"
                       + "目に見えない文字が入っています。入力し直してください。";
            }
        }

        // ここまで数え切っているので、`position` がそのまま符号点の数である。
        return position > maxLength
            ? $"「{label}」は {maxLength.ToString(CultureInfo.InvariantCulture)} 文字以内です。"
              + $"いまは {position.ToString(CultureInfo.InvariantCulture)} 文字あります。"
              + "短くして入力し直してください。"
            : null;
    }

    /// <summary>
    /// <b>Unicode の符号点で数える</b>（docs/12 §2-2。Claude の判断。2026-09-13）。
    /// </summary>
    /// <remarks>
    /// <para><b><c>string.Length</c> を使わない。</b> あれは UTF-16 の符号単位を数えるので、
    /// <b>基本多言語面の外の字（𠮟・🙂 など）が 2 と数えられる</b>——
    /// <b>SQLite の <c>LENGTH()</c> は同じ字を 1 と数える</b>ので、
    /// <b>関門が断った値を DDL が通す</b>（あるいはその逆の）ずれが生まれる。</para>
    /// <para><b>書記素では数えない。</b> 結合文字や異体字セレクタを 1 と数えるには
    /// <c>StringInfo</c> が要るが、<b>SQLite にそれは無い</b>。
    /// <b>2 つの守りが同じ数を返すことのほうが、見た目の直感より大事である</b>
    /// ——ずれると、画面で通ったものが保存で落ちる（qa/03 の L-28）。
    /// <b>その代わり、見た目 1 字でも 2 と数える字が残る</b>（docs/12 §2-2 に明記した）。</para>
    /// <para><b>全角と半角を区別しない</b>（docs/12 §2-2）。
    /// 市販でも freee・MF・弥生 Next が「全角・半角を問わず」で、
    /// 半角換算の桁数を使うのはデスクトップ製品だけである。</para>
    /// <para><b>数え方は <see cref="MasterCode"/> と同じ <c>EnumerateRunes</c> に寄せてある</b>
    /// ——同じアセンブリで 2 通りに書くと、片方だけが直る。</para>
    /// </remarks>
    public static int Count(string? value)
    {
        // **`EnumerateRunes()` は ref struct を返すので LINQ が使えない。** `foreach` で数える。
        var count = 0;
        if (value is not null)
        {
            foreach (var _ in value.EnumerateRunes())
            {
                count++;
            }
        }

        return count;
    }
}
