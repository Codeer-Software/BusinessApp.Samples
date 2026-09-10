using BusinessApp.Server.Services;
using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO.Db;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BusinessApp.AccountingCore.Server.Journals.Presentation;

namespace BusinessApp.Server.Controllers
{
    /// <summary>
    /// 伝票に対する操作 (取り消す・訂正する・複製する)。ADR-0016 の Web API。
    /// </summary>
    /// <remarks>
    /// <para><b>ここは HTTP の殻である。</b> 会計の判断も、識別子の解釈も、トランザクションも
    /// 書かない。中身は AccountingCore.Server の JournalAmendmentEndpoint が持ち、
    /// 普通の C# として単体テストしてある (ADR-0008)。</para>
    /// <para><b>ここに増やさない。</b> このプロジェクトは CLB テンプレート由来なので
    /// カバレッジにもミューテーションにも載らない (ADR-0012 §3 の Include から外れている)。
    /// ここに書いた行は誰にも検査されない。</para>
    /// </remarks>
    [Authorize, AutoValidateAntiforgeryToken]
    [ApiController]
    [Route("api/journals")]
    public class JournalsController : ControllerBase, IAsyncDisposable
    {
        //モジュール定義 (*.mod.json) の DataSourceName と一致していなければならない。
        const string AccountingDataSourceName = "BusinessAppSQLite";

        readonly DataService _dataService;

        public JournalsController(DataService dataService)
            => _dataService = dataService;

        public async ValueTask DisposeAsync()
            => await _dataService.DisposeAsync();

        /// <summary>この伝票にできること (取り消せるか・訂正できるか・複製できるか) を返す。**何も書かない。**</summary>
        [HttpPost("availability")]
        public async Task<IActionResult> AvailabilityAsync(AmendRequest request)
            => Ok(await Endpoint().AvailabilityAsync(request?.OriginalEntryId));

        /// <summary>取り消す。</summary>
        [HttpPost("reverse")]
        public async Task<IActionResult> ReverseAsync(AmendRequest request)
            => Ok(await Endpoint().ReverseAsync(request?.OriginalEntryId));

        /// <summary>訂正する。</summary>
        [HttpPost("correct")]
        public async Task<IActionResult> CorrectAsync(AmendRequest request)
            => Ok(await Endpoint().CorrectAsync(request?.OriginalEntryId));

        /// <summary>複製する (ADR-0048)。</summary>
        [HttpPost("duplicate")]
        public async Task<IActionResult> DuplicateAsync(AmendRequest request)
            => Ok(await Endpoint().DuplicateAsync(request?.OriginalEntryId));

        /// <summary>
        /// 入口を組み立てる。
        /// </summary>
        /// <remarks>
        /// データソースは名前で引く。添字だと 2 件目が増えた瞬間に、
        /// 会計コアだけが別の DB を読み書きして静かに壊れる。
        /// DataService は IAuthenticationContext を実装しており、posted_by の記録に使う。
        /// </remarks>
        JournalAmendmentEndpoint Endpoint()
        {
            var dataSourceName = SystemConfig.Instance.DataSources
                .FirstOrDefault(e => e.Name == AccountingDataSourceName)?.Name
                ?? throw LowCodeException.Create($"データソース {AccountingDataSourceName} が設定にない");

            IDbAccessor accessor = _dataService.DbAccess;
            return JournalAmendmentEndpoint.Create(
                accessor, dataSourceName, TimeProvider.System, _dataService);
        }
    }
}
