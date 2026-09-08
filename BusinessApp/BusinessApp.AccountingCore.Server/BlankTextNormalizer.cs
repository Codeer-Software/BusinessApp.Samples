namespace BusinessApp.AccountingCore.Server;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 空にした文字の欄を、空文字ではなく NULL で保存する（docs/04 §1 の A-5）。
/// </summary>
/// <remarks>
/// <para><b>CLB の既定は空文字である</b>（<c>TextEditEmptyType: StringEmpty</c>。qa/01 A-11）。
/// そのため<b>見た目は空なのに、空値検索が取りこぼす</b>——帳簿の空値検索は
/// <c>IS NULL OR = ''</c> しか見ないので、<b>空白だけの値はどちらにも当たらない</b>
/// （電帳通達 8-13 の「記録事項がない電磁的記録も検索できる」。docs/40 の D2）。</para>
/// <para><b>画面の設定だけでは足りない。</b> <c>TextEditEmptyType: Null</c> は
/// <b>値を消したときは効く</b>（2026-09-08 に実測）が、<c>ShouldTrimAfterEdit</c> が作る空文字は素通りする。
/// <b>だからサーバ側で寄せる</b>——<c>InvoiceRegistrationNumber.Normalize</c> と同じ置き方である。</para>
/// <para><b>空白だけの値も空とみなす。</b> 半角空白・全角空白・タブ・改行を落として空になるなら、
/// 利用者は空のつもりで入れている（docs/10 §4-4 の摘要と同じ判定）。</para>
/// <para><b>関門より先に走らせる。</b> 関門が「触った値」と「保存されている値」を比べるとき、
/// 片方が空文字・片方が NULL だと「変わった」と読んでしまう。</para>
/// <para><b>取引先だけを載せるホストでは、これが 1 つ足りなくなる</b>——
/// <c>SaveFailureMessage</c> と同じ形で、いまは会計コアの入口にだけ置いてある
/// （ADR-0025 §2「2 つ以上の部品が実際に使うものだけを共有へ出す」に従い、先回りして写しを作らない）。
/// <b>取引先だけのホストを作るときは <c>BusinessApp.ServerSupport</c> へ移してそちらからも呼ぶ。</b></para>
/// </remarks>
public static class BlankTextNormalizer
{
    /// <summary>
    /// 送られてきた文字の欄のうち、空白だけのものを NULL に直す。
    /// </summary>
    /// <remarks>
    /// <b>差分そのものを書き換える</b>（比べるときだけ落とす形にしない）。
    /// 比べるときだけ落とすと、関門が「同じ」と通した値が空文字のまま保存される。
    /// </remarks>
    public static void ToNull(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        ArgumentNullException.ThrowIfNull(transactionData);

        foreach (var data in transactionData.SelectMany(d => d.Add.Concat(d.Update)))
        {
            foreach (var text in data.Fields.Values.OfType<TextFieldData>())
            {
                if (text.Value is string value && value.Trim().Length == 0)
                {
                    text.Value = null;
                }
            }
        }
    }
}
