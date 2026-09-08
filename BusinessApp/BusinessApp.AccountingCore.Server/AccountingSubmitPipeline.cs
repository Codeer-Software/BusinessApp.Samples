namespace BusinessApp.AccountingCore.Server;

using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Masters;
using BusinessApp.AccountingCore.Server.Settings;
using BusinessApp.Partners.Server;
using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 保存を包む関門をつなぐ（ADR-0008）。<b>会計コアの保存の入口はここ 1 本だけ。</b>
/// </summary>
/// <remarks>
/// <para><b>つなぎ方をここに閉じ込める理由。</b> 本番の配線は
/// <c>BusinessApp.Server</c> の <c>CustomizedModuleDataIO</c> にあるが、
/// そこは<b>カバレッジにもミューテーションにも載らない</b>（ADR-0012 §3 の Include から外れている）。
/// 関門を足したのに配線し忘れても、テストは全部緑のままになる
/// （2026-08-26 の自己レビューで実際に指摘された。qa/02）。
/// <b>つなぎ方を検査の中に置き、本番もテストも同じ組み立てを呼ぶ。</b></para>
/// <para><b>順番は仕訳が外・取引先が内。</b> 仕訳の関門は保存の前後でやることがある
/// （入力年月日を打つ・保存後に読み直して計上する）ので、保存そのものを包む必要がある。
/// 取引先部品の関門は保存の前に検査するだけなので、内側でよい。
/// どれが例外を投げても保存ごと巻き戻る。</para>
/// <para><b>自社情報の関門は保存の前に検査するだけ</b>なので、取引先と同じく内側でよい。
/// 会計コアの関門が 1 つ増えたので、ここが「会計コアの関門を数える場所」になった——
/// <b>足したら必ずここに繋ぐ</b>（本番の配線は検査に載らない。上の理由）。</para>
/// <para><b>取引先の関門は 1 つずつ数えず、部品の入口（<see cref="PartnerSubmitPipeline"/>）を
/// 1 本呼ぶ。</b> ここで数えると、取引先部品に関門が増えたときに会計側を直さないと
/// 1 つ足りないまま通る——しかもテストは緑のままである（ADR-0025 §6 の「持ち出し忘れ」の型）。</para>
/// <para><b>マスタの関門（<see cref="MasterMeaningGate"/>。ADR-0038）も保存の前に検査するだけ</b>なので内側でよい。
/// 2026-09-07 に足した（docs/04 §1 の A-1）。</para>
/// <para><b>値の関門（<see cref="MasterSubmitGate"/>。docs/04 §1 の B-1・B-2）は、意味の凍結の内側に置く。</b>
/// <b>先に返すべきは意味の凍結のほう</b>——あちらは直す手立てが無い（新しい行を作るしかない）が、
/// こちらは値を直せば通るからである（<c>MasterMeaningGate</c> が同じ理由で
/// 意味を決める列の断りを一方通行の列より先に返している）。2026-09-09 に足した。</para>
/// </remarks>
public sealed class AccountingSubmitPipeline(
    JournalSubmitGate journals,
    CompanyProfileSubmitGate companyProfile,
    MasterMeaningGate masters,
    MasterSubmitGate masterValues,
    PartnerSubmitPipeline partners,
    Action<string>? onSaveFailure = null)
{
    /// <summary>部品の組み立て。<b>本番もテストもここを通す。</b></summary>
    /// <param name="onSaveFailure">
    /// 利用者の語に差し替えた<b>原文</b>を受け取る口（<see cref="SaveFailureMessage"/>）。
    /// <b>ログに出すのはホストの仕事</b>——この層にロギングの依存を持ち込まない。
    /// 渡さなければ原文は捨てられる。
    /// </param>
    public static AccountingSubmitPipeline Create(
        IDbAccessor dbAccessor, string dataSourceName, TimeProvider timeProvider,
        IAuthenticationContext authenticationContext, Action<string>? onSaveFailure = null)
        => new(JournalSubmitGate.Create(dbAccessor, dataSourceName, timeProvider, authenticationContext),
               new CompanyProfileSubmitGate(),
               MasterMeaningGate.Create(dbAccessor, dataSourceName),
               MasterSubmitGate.Create(dbAccessor, dataSourceName),
               PartnerSubmitPipeline.Create(dbAccessor, dataSourceName),
               onSaveFailure);

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    /// <remarks>
    /// <b>いちばん外で、保存の失敗を利用者の語に差し替える</b>（<see cref="SaveFailureMessage"/>）。
    /// 関門が拾えなかった失敗はここまで DB の言葉のまま上がってきて、CLB がそれをトーストに出す
    /// （qa/01 F-16）。差し替えを内側の関門に置くと、関門を 1 つ足すたびに置き場所を考えることになる。
    /// </remarks>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        // **いちばん先に、空白だけの文字の欄を NULL へ寄せる**（docs/04 §1 の A-5）。
        // 関門より後ろに置くと、関門が「触った値」と「保存されている値」を比べるときに
        // 片方が空文字・片方が NULL で「変わった」と読んでしまう。
        BlankTextNormalizer.ToNull(transactionData);

        List<ModuleSubmitResult> results;
        try
        {
            results = await journals.SubmitAsync(
                transactionData,
                () => companyProfile.SubmitAsync(
                    transactionData,
                    () => masters.SubmitAsync(
                        transactionData,
                        () => masterValues.SubmitAsync(
                            transactionData,
                            () => partners.SubmitAsync(transactionData, save)))));
        }
        catch (UnreadableFieldException unreadable)
        {
            // **欄の型が読めないのは利用者の誤りではない**ので、利用者には定型文だけを見せ、
            // 中身はホストのログへ回す（<see cref="SaveFailureMessage"/> と同じ分担）。
            // **投げ直す例外に内側を残さない**——ホストの例外ハンドラは
            // InnerException の文言まで連ねて返すので、残すと結局そのまま画面に出る。
            onSaveFailure?.Invoke(unreadable.Message);
            throw new InvalidOperationException(SaveFailureMessage.Text);
        }

        return SaveFailureMessage.ToUserLanguage(results, onSaveFailure);
    }
}
