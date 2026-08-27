namespace BusinessApp.Partners.Server;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 取引先部品の保存の関門をつなぐ。<b>取引先の保存の入口はここ 1 本だけ。</b>
/// </summary>
/// <remarks>
/// <para><b>組み立てを部品の側に置く理由。</b> 本番の配線は CLB のホスト
/// （<c>CustomizedModuleDataIO</c>）にあり、そこは<b>カバレッジにもミューテーションにも
/// 載らない</b>（ADR-0012 §3 の Include から外れている）。関門を足したのに配線し忘れても、
/// テストは全部緑のままになる。<b>つなぎ方を検査の中に置き、本番もテストも同じ組み立てを呼ぶ。</b></para>
/// <para><b>会計コアと一緒に載せるときも、取引先だけを載せるときも、ここを通る。</b>
/// 会計側（<c>AccountingSubmitPipeline</c>）はこれを 1 本呼ぶだけにしてある——
/// 取引先の関門が増えたときに<b>会計側を直さなくてよい</b>のが要で、
/// 直さなければならない形にすると、別ホストへ載せた取引先だけ関門が 1 つ足りない、
/// という壊れ方をする（ADR-0025 §2 が約束した「取引先だけのデプロイ」の受け皿）。</para>
/// <para>関門はどちらも保存の前に検査するだけなので、順番に意味は無い。
/// どちらが例外を投げても、呼び出し側のトランザクションごと巻き戻る。</para>
/// </remarks>
public sealed class PartnerSubmitPipeline(
    PartnerRegistrationSubmitGate registrations,
    PartnerSubmitGate partners)
{
    /// <summary>部品の組み立て。<b>本番もテストもここを通す。</b></summary>
    public static PartnerSubmitPipeline Create(IDbAccessor dbAccessor, string dataSourceName)
        => new(new PartnerRegistrationSubmitGate(new PartnerRegistrationStore(dbAccessor, dataSourceName)),
               new PartnerSubmitGate(new PartnerStore(dbAccessor, dataSourceName)));

    /// <summary>保存を包む。<paramref name="save"/> は CLB 本来の保存処理。</summary>
    public Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(save);

        return registrations.SubmitAsync(
            transactionData,
            () => partners.SubmitAsync(transactionData, save));
    }
}
