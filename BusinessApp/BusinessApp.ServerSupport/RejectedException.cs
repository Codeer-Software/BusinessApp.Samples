namespace BusinessApp.ServerSupport;

/// <summary>
/// 関門が<b>利用者の操作を差し戻す</b>ときの例外の基底。<see cref="Exception.Message"/> は<b>そのまま利用者に見せてよい</b>文である。
/// </summary>
/// <remarks>
/// <para><b>この型かどうかが、画面に出してよい文言かどうかの線である。</b> 保存の入口（<c>AccountingSubmitPipeline</c>）は、
/// この型の例外だけを利用者の語として CLB へ返し、それ以外の例外（<see cref="UnreadableFieldException"/>・DB の生の失敗・
/// 開発者向けの <c>InvalidOperationException</c> など）は定型文に差し替えて原文をログへ回す。
/// 型で線を引くのは、文言の中身を見て「利用者向けか」を判定できないからである
/// （ホストの例外ハンドラが <c>Message</c> をそのまま画面へ返し、「仮 ID …」「仕訳 … が見つからない」が
/// 利用者に届いていた。qa/02 R57-35）。</para>
/// <para><b>差し戻しは CLB へ例外ではなく結果（<c>ModuleSubmitResult.ExceptionMessage</c>）で返す</b>——例外で返すと
/// CLB が自分の定型文「更新に失敗しました」をもう 1 枚出す（qa/02 R25-08。ADR-0051）。派生型は例外のまま投げてよい。
/// 変換は入口が 1 か所で行う。</para>
/// <para>派生型が守ること：見出しから始める（「計上できません。」「登録できません。」）・改行を入れない（qa/01 D-12）・
/// 内部表現を出さない（docs/21 §2-2）。</para>
/// </remarks>
public abstract class RejectedException(string message, Exception? inner = null) : Exception(message, inner);
