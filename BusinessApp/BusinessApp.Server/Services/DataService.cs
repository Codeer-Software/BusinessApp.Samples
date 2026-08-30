using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DbAccess;
using Codeer.LowCode.Blazor.Extras.Server.FileManagement;
using System.Security.Claims;

namespace BusinessApp.Server.Services
{
    public class DataService : IAuthenticationContext, IAsyncDisposable
    {
        public DbAccessor DbAccess { get; }
        public TemporaryFileManager TemporaryFileManager { get; }
        public CustomizedModuleDataIO ModuleDataIO { get; }
        readonly IHttpContextAccessor _httpContextAccessor;

        public DataService(IHttpContextAccessor httpContextAccessor, ILogger<DataService> logger)
        {
            _httpContextAccessor = httpContextAccessor;
            DbAccess = new DbAccessor(SystemConfig.Instance.DataSources);
            TemporaryFileManager = new TemporaryFileManager(DbAccess, SystemConfig.Instance.TemporaryFileTableInfo, SystemConfig.Instance.FileStorages);
            //保存の失敗は利用者の語に差し替えて返す (qa/01 F-16)。**原文はここで残す。**
            //差し替えだけして捨てると、次の F-26 (枠組みが英語で返す拒否) を見つける手段が消える。
            //ログに出すのはホストの仕事で、AccountingCore.Server にロギングの依存は持ち込まない。
            ModuleDataIO = new CustomizedModuleDataIO(
                DesignerService.GetDesignData(), this, DbAccess, TemporaryFileManager,
                message => logger.LogError("保存が失敗しました（利用者へ出す前の原文）: {Message}", message));
        }

        public Task<string> GetCurrentUserIdAsync()
            => Task.FromResult(GetCurrentUserId(_httpContextAccessor.HttpContext));

        public static string GetCurrentUserId(HttpContext? httpContext)
            => httpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

        public async ValueTask DisposeAsync()
            => await DbAccess.DisposeAsync();
    }
}
