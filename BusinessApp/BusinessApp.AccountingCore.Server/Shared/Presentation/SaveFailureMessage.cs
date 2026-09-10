namespace BusinessApp.AccountingCore.Server.Shared.Presentation;

using Codeer.LowCode.Blazor.DataIO;
using BusinessApp.AccountingCore.Server.Journals.Application;

/// <summary>
/// 保存が失敗したことを、<b>利用者の語</b>で知らせる（docs/21 §2-2「内部表現を出さない」）。
/// </summary>
/// <remarks>
/// <para><b>CLB は保存の失敗を例外ではなく <c>ModuleSubmitResult.ExceptionMessage</c> に詰めて返し、
/// その中身をそのままトーストに出す。</b> 中身は<b>枠組みの言葉</b>である——
/// <b>観測した文言の一覧と、中身を見ずにまるごと差し替える理由は qa/01 F-16 が持つ</b>
/// （CLB の版に紐づくので、台帳側で増える）。</para>
/// <para><b>ここは最後の網であって、一次の守りではない。</b> 差し戻しの理由は関門が
/// 利用者の語で作る（<see cref="Journals.JournalSubmitRequirements"/> ほか）。
/// ここに落ちてくるのは<b>関門が拾えなかった失敗</b>だけで、それは「関門が 1 つ足りない」という合図である。</para>
/// <para><b>差し替えた原文は捨てず、呼び出し側に渡す。</b> F-26 は<b>生のメッセージが見えたから
/// 見つかった</b>もので、黙って捨てるとこの網が次の F-26 を隠す。
/// <c>AccountingCore.Server</c> にロギングの依存を持ち込まないため、
/// ログに出すかどうかはホスト側に委ねる（既定は「何もしない」）。</para>
/// <para><b>置き場所は会計コアの入口（<see cref="AccountingSubmitPipeline"/>）である。</b>
/// いまの唯一のホストはここを通るので、取引先の保存もこの網の内側にある。
/// <b>取引先部品を単独で載せるときは、これを <c>BusinessApp.ServerSupport</c> へ移す</b>
/// （ADR-0025 §2 が「<b>先回りの共通化をしない</b>」と決めているので、
/// 実際に 2 つ目の部品が使う場面が来てから移す）。この帰結は <c>PartnerSubmitPipeline</c> 側にも書いてある。</para>
/// </remarks>
internal static class SaveFailureMessage
{
    /// <summary>差し替え後の文言。</summary>
    /// <remarks>
    /// <para><b>qa/01 F-16 が並べている失敗の「次にすべきこと」を、全部包む言い方にする</b>（docs/21 §2-3）——
    /// 直せば通るもの・開き直せば通るもの・<b>何度やっても通らないもの</b>が混ざるので、
    /// 「入力内容を確かめてください」だけでも「もう一度お試しください」だけでも嘘になる。</para>
    /// <para><b>改行を入れない</b>（トーストは改行できない。qa/01 D-12）。</para>
    /// <para><c>const</c> にしない。<b>定数式はミューテーションテストが変異を置けない</b>ので、
    /// 文言が空になっても気づけなくなる（そのとき CLB は何も表示せず、
    /// <b>保存が成功したように見える</b>）。</para>
    /// </remarks>
    public static readonly string Text =
        "保存できませんでした。入力内容を確かめ、画面を開き直してもう一度お試しください。"
        + "同じことが続くときは、管理者にお知らせください。";

    /// <summary>
    /// 保存の失敗を利用者の語に差し替える。<b>成功した結果には触れない。</b>
    /// </summary>
    /// <param name="results">CLB 本来の保存が返した結果。</param>
    /// <param name="onReplaced">
    /// 差し替えた<b>原文</b>を受け取る口。ログに出すのはホストの仕事なので、既定では何もしない。
    /// </param>
    public static List<ModuleSubmitResult> ToUserLanguage(
        List<ModuleSubmitResult> results, Action<string>? onReplaced = null)
    {
        foreach (var result in results.Where(r => !string.IsNullOrEmpty(r.ExceptionMessage)))
        {
            onReplaced?.Invoke(result.ExceptionMessage);
            result.ExceptionMessage = Text;
        }

        return results;
    }
}
