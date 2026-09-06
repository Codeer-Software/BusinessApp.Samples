namespace BusinessApp.Partners;

/// <summary>
/// 適格請求書発行事業者の登録番号の書式（docs/13 §3-2）。
/// </summary>
/// <remarks>
/// <para><b>「T ＋ 数字 13 桁」の計 14 桁</b>（国税庁のリソース定義書で確認済み。
/// docs/research/2026-08-25_取引先の識別番号と公表システム.md §3-3）。</para>
/// <para><b>DB は書式を検査しない。</b> ここが唯一の関門である（同 §3-2 の決定）。
/// 書式の壊れた番号を通すと、計上のときに<b>そのまま明細へ焼き込まれる</b>。
/// 計上済みは不変（ADR-0004）なので、あとから直せない。</para>
/// <para><b>13 桁部分が法人番号と同一かは未確認</b>なので、法人番号との突合には使わない
/// （同 §3-2）。ここが見るのは書式だけで、実在するかどうかは見ない。</para>
/// </remarks>
public static class InvoiceRegistrationNumber
{
    /// <summary>「T」に続く数字の桁数。</summary>
    public const int DigitCount = 13;

    /// <summary>全体の長さ（<c>T</c> ＋ <see cref="DigitCount"/>）。</summary>
    public const int Length = DigitCount + 1;

    /// <summary>利用者に見せる書式の説明。<b>差し戻しの文言と画面の注記で同じ文を使う。</b></summary>
    public const string FormatDescription = "登録番号は「T」で始まる 14 桁（T のあとに数字 13 桁）です。";

    /// <summary>書式に合っているか。</summary>
    /// <remarks>
    /// <para><b>前後の空白は書式違反として扱わない</b>——貼り付けで紛れ込むだけで、
    /// 利用者の意図ではない。<see cref="Normalize"/> が落とした姿で判定する。</para>
    /// <para>小文字の <c>t</c> は<b>受け付けない</b>。公表システムが出す形は大文字であり、
    /// ここで揺れを許すと、同じ番号が 2 通りの文字列で保存されて突合が壊れる。</para>
    /// </remarks>
    public static bool IsWellFormed(string? value)
    {
        var text = Normalize(value);

        if (text.Length != Length || text[0] != 'T')
        {
            return false;
        }

        // char.IsDigit は全角数字も真になる。ここで通すと、見た目が同じで
        // バイト列が違う番号が保存され、突合が静かに外れる。
        return text.AsSpan(1).ContainsAnyExceptInRange('0', '9') is false;
    }

    /// <summary>前後の空白を落とした姿。<b>保存するのはこの形</b>。</summary>
    public static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
