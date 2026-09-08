namespace BusinessApp.ServerSupport;

using System.Globalization;
using System.Text;

/// <summary>
/// マスタのコードの書式（docs/12 §2-1。ADR-0047）。
/// </summary>
/// <remarks>
/// <para><b>コードを持つ 6 つの表すべてが、同じ規則に従う</b>——
/// <c>fiscal_years</c> / <c>tax_categories</c> / <c>accounts</c> / <c>sub_accounts</c> /
/// <c>departments</c> / <c>partners</c>。会計コアと取引先部品にまたがるので、
/// <b>どちらからも参照できるここに置く</b>（ADR-0025 §2「2 つ以上の部品が実際に使うものだけを共有へ出す」）。</para>
/// <para><b>書き換えるのは前後の空白だけである。</b> 全角英数を半角へ寄せない・英字を大文字へ寄せない
/// （docs/21 §0 の境目——利用者が入れたつもりのものを変えない）。
/// <c>InvoiceRegistrationNumber</c> と <c>CorporateNumber</c> が同じ骨格である。</para>
/// <para><b>同一性の判定はここでは見ない。</b> 大小を無視した重複は保存の関門が問い合わせ、
/// 最後は DB の <c>UNIQUE ... COLLATE NOCASE</c> が止める（ADR-0047 の決定 7）。</para>
/// <para><b>DDL のトリガが同じ規則を持つ</b>（<c>Designer/ddl/008_master_code_format.sql</c>）。
/// **関門とトリガが同じ字を通すことは <c>MasterCodeGuardTests</c> が符号位置の総当たりで、長さは <c>FieldLengthConsistencyTests</c> が C# と DDL とデザインの 3 か所で突き合わせる**（docs/20 §4）。</para>
/// </remarks>
public static class MasterCode
{
    /// <summary>最長の長さ。<b>制度的な根拠は無い</b>（ADR-0047 の理由）。</summary>
    public const int MaxLength = 20;

    /// <summary>区切りに使える記号。<b>先頭・末尾には置けず、連続もできない。</b></summary>
    public const string Separators = "-_";

    /// <summary>
    /// 利用者に見せる書式の説明。<b>差し戻しの文言と画面の注記で同じ文を使う。</b>
    /// </summary>
    /// <remarks>
    /// <b>長さは <see cref="MaxLength"/> から作る。</b> 文言に数字を書くと、上限を変えたときに
    /// 説明だけが古くなる（<c>CorporateNumber.FormatDescription</c> と同じ作法）。
    /// </remarks>
    public static readonly string FormatDescription =
        $"コードは半角の英数字と「-」「_」で、{MaxLength.ToString(CultureInfo.InvariantCulture)} 文字以内です。"
        + "「-」「_」は先頭と末尾には置けません。";

    /// <summary>前後の空白を落とした姿。<b>保存するのはこの形</b>。</summary>
    /// <remarks>
    /// <b>ここが唯一の書き換えである</b>（ADR-0047 の決定 5）。<c>Trim()</c> の既定は
    /// Unicode の空白すべてを落とすので、全角スペース・タブ・改行も落ちる。
    /// </remarks>
    public static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// 通らない理由を利用者の語で返す。<b>通れば <c>null</c>。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>最初に当たった 1 つだけを返す</b>（開発者の指示。2026-09-08。逐語「即エラー。次へ進まない」）。
    /// 1 つの欄に 4 つの断りを並べても、利用者は最初の 1 つしか直せない。</para>
    /// <para><b>空欄は呼ぶ側が先に落とす。</b>「必須かどうか」は欄ごとの話なので、ここでは決めない
    /// （<c>CorporateNumber</c> と同じ分担）。<b>空白だけの値は <see cref="Normalize"/> が空にする</b>ので、
    /// 呼ぶ側は正規化した値で必須を見ること。</para>
    /// <para><b>順は 字種 → 先頭末尾 → 連続 → 長さ である。</b> この順を変えると、面外の字が入ったときに
    /// 長さの数え方（C# は UTF-16 の単位、SQLite の <c>LENGTH</c> はコードポイント）の食い違いが表に出る。
    /// <b>字種が面外を先に断つので、いまは食い違わない。</b></para>
    /// </remarks>
    public static string? DescribeProblem(string? value)
    {
        var text = Normalize(value);

        if (text.Length == 0)
        {
            return null;
        }

        // **コードポイントで数える。** char 単位で回すと、面外の字（絵文字など）を
        // 2 文字と数え、文言に壊れた片割れを埋め込む。
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            position++;
            if (!IsAllowed(rune))
            {
                return Unusable(rune, position);
            }
        }

        if (IsSeparator(text[0]) || IsSeparator(text[^1]))
        {
            return $"コードの最初と最後に「-」「_」は使えません。{FormatDescription}";
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (IsSeparator(text[i]) && IsSeparator(text[i - 1]))
            {
                return $"コードの「-」「_」は続けて使えません。{FormatDescription}";
            }
        }

        // ここまで来た文字はすべて 1 コードポイント = 1 char なので、position が長さである。
        return position > MaxLength
            ? $"コードは {MaxLength.ToString(CultureInfo.InvariantCulture)} 文字以内です。"
              + $"いまは {position.ToString(CultureInfo.InvariantCulture)} 文字あります。"
            : null;
    }

    /// <summary>使える字か。<b>半角英数字と区切り記号だけ</b>。</summary>
    /// <remarks>
    /// <c>char.IsLetterOrDigit</c> は全角英数もかな漢字も真になる。ここで通すと、
    /// 見た目が同じでバイト列が違うコードが保存される（登録番号と法人番号が同じ理由で
    /// <c>ContainsAnyExceptInRange</c> を使っている）。
    /// </remarks>
    private static bool IsAllowed(Rune rune)
        => rune.Value is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
           || (rune.IsBmp && IsSeparator((char)rune.Value));

    private static bool IsSeparator(char c) => Separators.Contains(c, StringComparison.Ordinal);

    /// <summary>使えない字を、利用者が直せる形で示す。</summary>
    /// <remarks>
    /// <b>目に見えない字は「見えない文字」と言い、位置を添える</b>（ADR-0047 の決定 11）。
    /// ゼロ幅空白や制御文字に「使えない文字があります」とだけ返すと、
    /// 画面には何も見えないので直しようがない。
    /// </remarks>
    private static string Unusable(Rune rune, int position)
    {
        var at = position.ToString(CultureInfo.InvariantCulture);
        var what = IsInvisible(rune)
            ? $"{at} 文字目に、目に見えない文字が入っています"
            : $"{at} 文字目の「{rune}」は使えません";

        return $"コードの {what}。{FormatDescription}";
    }

    /// <summary>画面で見分けの付かない字か（制御文字・空白・書式用の字）。</summary>
    /// <remarks>
    /// <b>前後の空白は <see cref="Normalize"/> が落としているので、ここに来る空白は字の間にあるもの</b>である。
    /// </remarks>
    private static bool IsInvisible(Rune rune)
        => Rune.IsControl(rune) || Rune.IsWhiteSpace(rune)
           || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format;
}
