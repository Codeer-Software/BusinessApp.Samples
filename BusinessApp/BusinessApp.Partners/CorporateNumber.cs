namespace BusinessApp.Partners;

/// <summary>
/// 法人番号の書式と検査用数字（docs/13 §1-2）。
/// </summary>
/// <remarks>
/// <para><b>13 桁は「1 桁の検査用数字 ＋ 12 桁の基礎番号」</b>である（番号法施行令 35 ①）。
/// 算式と計算例はリサーチに引いてある
/// （docs/research/2026-08-25_取引先の識別番号と公表システム.md §1-1）。</para>
/// <para><b>DB は桁と数字だけを見る</b>と決めてある（docs/13 §1-2）ので、
/// 検査用数字を見るのはここだけである。見ないと、打ち間違えた番号がそのまま
/// <b>名寄せの自然キー</b>（同 §2-2）になる——別の法人に化けて黙って束なるか、
/// 束なるべきものが束ならないかのどちらかで、どちらも画面には何も出ない。</para>
/// <para><b>実在するかどうかは見ない。</b> 桁と検査用数字が合っていても、
/// 指定されていない番号はありうる。実在の確認は公表システムとの突合（フェーズ 6）の仕事である。</para>
/// </remarks>
public static class CorporateNumber
{
    /// <summary>法人番号の桁数。</summary>
    public const int Length = 13;

    /// <summary>基礎番号の桁数（検査用数字を除いた残り）。</summary>
    public const int BaseNumberLength = Length - 1;

    /// <summary>
    /// 利用者に見せる書式の説明。<b>差し戻しの文言で使う。</b>
    /// </summary>
    /// <remarks>
    /// <b>桁数は <see cref="Length"/> から作る。</b> 文言に数字を書くと、桁を変えたときに
    /// 説明だけが古くなり、「13 桁です」と言いながら 14 桁を要求する関門になる。
    /// </remarks>
    public static readonly string FormatDescription =
        $"法人番号は数字 {Length} 桁です。";

    /// <summary>
    /// 検査用数字が合っていないときに利用者へ見せる文言。
    /// </summary>
    /// <remarks>
    /// <b>「打ち間違い」と言い切る。</b> 書式が合っていて検査用数字だけが合わない番号は、
    /// 転記や打鍵の誤り以外では生まれない（実在しない番号を意図して入れる場面が無い）。
    /// </remarks>
    public const string CheckDigitDescription =
        "法人番号が正しくありません。打ち間違いの可能性があります。"
        + "国税庁の法人番号公表サイトで確かめて入力し直してください。";

    /// <summary>
    /// 通らない理由を利用者の語で返す。<b>通れば <c>null</c>。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>2 か所が同じ番号を検査する</b>——取引先の法人番号（docs/13 §1-2）と、
    /// 自社の法人番号（docs/12 の自社情報。qa/02 R12-11）。
    /// 判定と文言をここ 1 か所に置き、<b>投げる例外の型だけを呼ぶ側が決める</b>。</para>
    /// <para><b>空欄は呼ぶ側が先に落とす。</b> どちらの画面でも任意の項目だが、
    /// 「空欄をどう保存するか」（NULL に倒すか）は画面ごとの話なので、ここでは決めない。</para>
    /// </remarks>
    public static string? DescribeProblem(string? value)
    {
        if (!IsWellFormed(value))
        {
            return $"{FormatDescription}入力し直してください。";
        }

        return HasValidCheckDigit(value) ? null : CheckDigitDescription;
    }

    /// <summary>前後の空白を落とした姿。<b>保存するのはこの形</b>。</summary>
    public static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>桁と字種が合っているか。<b>検査用数字は見ない。</b></summary>
    /// <remarks>
    /// <c>char.IsDigit</c> は全角数字も真になる。ここで通すと、見た目が同じでバイト列が違う番号が
    /// 保存され、名寄せの突合が静かに外れる（登録番号と同じ理由。<see cref="InvoiceRegistrationNumber"/>）。
    /// </remarks>
    public static bool IsWellFormed(string? value)
    {
        var text = Normalize(value);
        return text.Length == Length && text.AsSpan().ContainsAnyExceptInRange('0', '9') is false;
    }

    /// <summary>
    /// 先頭の 1 桁が、残り 12 桁から算出される検査用数字と一致するか。
    /// </summary>
    /// <remarks>
    /// 書式が崩れている値には <c>false</c> を返す（呼ぶ側の順序に依存させない）。
    /// 書式と検査用数字で文言を変えたいときは <see cref="IsWellFormed"/> を先に見る。
    /// </remarks>
    public static bool HasValidCheckDigit(string? value)
    {
        var text = Normalize(value);
        return IsWellFormed(text) && text[0] - '0' == CheckDigitOf(text.AsSpan(1));
    }

    /// <summary>
    /// 基礎番号 12 桁から検査用数字を求める。
    /// </summary>
    /// <remarks>
    /// <para><b>最下位を 1 桁目</b>として、奇数桁の和 ＋ 偶数桁の和 × 2 を 9 で割った余りを 9 から引く。
    /// 余りが 0 なら 9 になり、施行令が定める「一から九までの整数」に収まる。</para>
    /// <para>桁を数える向きを取り違えても、ほとんどの番号は<b>それらしく通ってしまう</b>ので、
    /// 国税庁の計算例をゴールデンテストで固定してある。</para>
    /// </remarks>
    private static int CheckDigitOf(ReadOnlySpan<char> baseNumber)
    {
        var sum = 0;

        for (var digit = 1; digit <= BaseNumberLength; digit++)
        {
            var value = baseNumber[^digit] - '0';
            sum += digit % 2 == 1 ? value : value * 2;
        }

        return 9 - (sum % 9);
    }
}
