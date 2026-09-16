namespace BusinessApp.AccountingCore.Settings;

using System.Globalization;
using System.Text;

/// <summary>
/// 自社情報の<b>郵便番号の書式</b>（docs/12 §2-2）。
/// </summary>
/// <remarks>
/// <para><b>長さではなく書式で決める</b>（開発者の決定。2026-09-16。旧 Q-26 の案「書式で決める（<c>NNN-NNNN</c> の 8 文字）」）。
/// 日本の郵便番号は 7 桁で、区切りの「-」を入れて 8 文字ちょうどにしかならないので、
/// <b>「N 文字以内」という形の規則が当たらない</b>。</para>
/// <para><b>電話番号には書式を置かない</b>（同じ決定）——内線・国番号・区切り記号の形が割れるので、
/// 長さだけを見る（<c>MasterTextLength.PhoneNumber</c>）。<b>書式の規則を持つのは郵便番号だけである。</b></para>
/// <para><b>空白を落とす以外は書き換えない</b>（<b>Claude の判断。2026-09-16</b>。
/// ADR-0047 がマスタのコードに決めた線をそのまま当てる）——
/// 「1234567」に「-」を足して直すことはしない。<b>利用者が打った字を勝手に作り変えると、
/// 「自分が入れた値」と「保存された値」が違うことに気づけない。</b></para>
/// <para><b>受理する集合を DDL と同じにしてある</b>（<c>Designer/ddl/012_text_length.sql</c>）。
/// どちらかが広いと、<b>関門が通した値を DB が拒む</b>（利用者には定型文が出る。qa/03 の L-14・L-28）。
/// そのために<b>目に見えない字も断る</b>——SQLite の <c>GLOB</c> は
/// <b>途中の U+0000 で止まる</b>ので、書式が合った先にいくらでも隠せる（qa/03 の L-48 と同じ型）。</para>
/// <para><b>実在するかどうかは見ない。</b> 書式が合っていても、割り当てられていない番号はありうる。</para>
/// </remarks>
public static class PostalCode
{
    /// <summary>画面のラベル（docs/21 §2-6——欄の名前は画面のラベルを鉤括弧で括る）。</summary>
    public const string Label = "郵便番号";

    /// <summary>区切りの前の桁数。</summary>
    public const int PrefixLength = 3;

    /// <summary>区切りの後の桁数。</summary>
    public const int SuffixLength = 4;

    /// <summary>区切りの字。</summary>
    public const char Separator = '-';

    /// <summary>区切りを含めた文字数。</summary>
    public const int Length = PrefixLength + 1 + SuffixLength;

    /// <summary>
    /// 実例。<b>画面のプレースホルダはこの字である</b>（docs/21 §1——書式の欄は実例を出す）。
    /// </summary>
    /// <remarks>
    /// <b>桁から作る。</b> 「123-4567」と直に書くと、桁を変えたときに
    /// <b>説明と実例が食い違う</b>（「数字 4 桁 ＋ 「-」 ＋ 数字 4 桁（123-4567）」のような自己矛盾になる）。
    /// </remarks>
    public static readonly string Example =
        string.Concat(Enumerable.Range(1, PrefixLength).Select(Digit))
        + Separator
        + string.Concat(Enumerable.Range(PrefixLength + 1, SuffixLength).Select(Digit));

    /// <summary>
    /// 書式の説明。<b>差し戻しの文言で使う</b>（プレースホルダは <see cref="Example"/> のほう）。
    /// </summary>
    /// <remarks>
    /// <b>桁数は定数から作る</b>（<c>CorporateNumber.FormatDescription</c> と同じ作法）。
    /// 文言に数字を書くと、桁を変えたときに説明だけが古くなる。
    /// <b>欄の名前を含めない</b>——プレースホルダは欄の中に出るので、そこで欄名を名乗ると重複する。
    /// </remarks>
    public static readonly string FormatDescription =
        $"数字 {PrefixLength.ToString(CultureInfo.InvariantCulture)} 桁 ＋ 「{Separator}」 ＋ "
        + $"数字 {SuffixLength.ToString(CultureInfo.InvariantCulture)} 桁（{Example}）";

    /// <summary>差し戻しの文言で使う 1 文（<b>欄の名前つき</b>。docs/21 §2-6）。</summary>
    private static readonly string FormatSentence = $"「{Label}」は{FormatDescription}です。";

    /// <summary>前後の空白を落とした姿。<b>保存するのはこの形</b>。</summary>
    /// <remarks>
    /// <b>落とすのは前後の空白だけである</b>（docs/21 §0）。
    /// <c>Trim()</c> の既定は Unicode の空白すべてを落とすので、全角スペース・タブ・改行も落ちる。
    /// </remarks>
    public static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>書式が合っているか。</summary>
    public static bool IsWellFormed(string? value) => DescribeProblem(value) is null;

    /// <summary>
    /// 通らない理由を利用者の語で返す。<b>通れば <c>null</c>。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>空欄は呼ぶ側が先に落とす。</b> 郵便番号は任意の項目だが、
    /// 「空欄をどう保存するか」（NULL に倒すか）は画面ごとの話なので、ここでは決めない
    /// （<c>CorporateNumber.DescribeProblem</c> と同じ）。</para>
    /// <para><b>何が悪いかを名指しする</b>——長さが違うなら今の文字数、字が違うなら何文字目の何か。
    /// 画面は入力を止めない（docs/21 §1）ので、貼り付けた値はこの文言だけを手がかりに直すことになる。</para>
    /// </remarks>
    public static string? DescribeProblem(string? value)
    {
        var text = Normalize(value);

        // **符号点で数える**（docs/12 §2-2。`MasterTextLength.Count` と同じ判断）——
        // `string.Length` は UTF-16 の符号単位を数えるので、**基本多言語面の外の字が 2 と数えられる**。
        // **`EnumerateRunes()` は ref struct を返すので LINQ が使えない。**
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        if (count != Length)
        {
            var now = count == 0
                ? string.Empty
                : $"いまは {count.ToString(CultureInfo.InvariantCulture)} 文字あります。";
            return $"{FormatSentence}{now}入力し直してください。";
        }

        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            position++;
            if (position == PrefixLength + 1 ? rune.Value == Separator : rune.Value is >= '0' and <= '9')
            {
                continue;
            }

            // **目に見えない字は、言われないと探せない**（`MasterTextLength.DescribeProblem` と同じ作法）。
            var at = position.ToString(CultureInfo.InvariantCulture);
            return Rune.IsControl(rune)
                ? $"「{Label}」の {at} 文字目に、目に見えない文字が入っています。入力し直してください。"
                // **同じ断りに欄の名前を 2 回出さない**（docs/21 §2-6）——
                // 頭で名乗っているので、書式の側は名前を含まない字を使う。
                : $"「{Label}」の {at} 文字目の「{rune}」は使えません。{FormatDescription}で入力し直してください。";
        }

        return null;
    }

    /// <summary>実例の 1 桁（1 から順に、10 で折り返す）。</summary>
    private static string Digit(int position)
        => (position % 10).ToString(CultureInfo.InvariantCulture);
}
