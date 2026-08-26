namespace BusinessApp.AccountingCore.Server;

using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Partners;

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
/// <para><b>順番は仕訳が外・マスタが内。</b> 仕訳の関門は保存の前後でやることがある
/// （入力年月日を打つ・保存後に読み直して計上する）ので、保存そのものを包む必要がある。
/// 取引先と登録の関門は保存の前に検査するだけなので、内側でよい。
/// どれが例外を投げても保存ごと巻き戻る。</para>
/// </remarks>
public sealed class AccountingSubmitPipeline(
    JournalSubmitGate journals,
    PartnerRegistrationSubmitGate registrations,
    PartnerSubmitGate partners)
{
    /// <summary>部品の組み立て。<b>本番もテストもここを通す。</b></summary>
    public static AccountingSubmitPipeline Create(
        IDbAccessor dbAccessor, string dataSourceName, TimeProvider timeProvider,
        IAuthenticationContext authenticationContext)
        => new(JournalSubmitGate.Create(dbAccessor, dataSourceName, timeProvider, authenticationContext),
               new PartnerRegistrationSubmitGate(new PartnerRegistrationStore(dbAccessor, dataSourceName)),
               new PartnerSubmitGate(new PartnerStore(dbAccessor, dataSourceName)));

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(save);

        return journals.SubmitAsync(
            transactionData,
            () => registrations.SubmitAsync(
                transactionData,
                () => partners.SubmitAsync(transactionData, save)));
    }
}
