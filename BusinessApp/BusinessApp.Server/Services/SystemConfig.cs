using BusinessApp.Client.Shared.Services;
using BusinessApp.Server.Services.AI;
using Codeer.LowCode.Blazor.Extras.Server.FileManagement;
using Codeer.LowCode.Blazor.Extras.Server.Mail;
using Codeer.LowCode.Blazor.SystemSettings;

namespace BusinessApp.Server.Services
{
    public class PasswordCheckUserTableInfo
    {
        public string TableName { get; set; } = string.Empty;
        public string IdColumn { get; set; } = string.Empty;
        public string UserNameColumn { get; set; } = string.Empty;
        public string HashColumn { get; set; } = string.Empty;
        public string SaltColumn { get; set; } = string.Empty;
    }

    public class SystemConfig
    {
        public static SystemConfig Instance { get; set; } = new();

        public bool CanScriptDebug { get; set; }
        public bool UseHotReload { get; set; }
        public DataSource[] DataSources { get; set; } = [];
        public FileStorage[] FileStorages { get; set; } = [];
        public TemporaryFileTableInfo[] TemporaryFileTableInfo { get; set; } = [];
        public string DesignFileDirectory { get; set; } = string.Empty;
        public string FontFileDirectory { get; set; } = string.Empty;
        public MailSettings MailSettings { get; set; } = new();
        public AppAISettings AISettings { get; set; } = new();
        public NtaInvoiceSettings NtaInvoiceSettings { get; set; } = new();
        public PasswordCheckUserTableInfo PasswordCheckUserTableInfo { get; set; } = new();
        public SystemConfigForFront ForFront() => new SystemConfigForFront { CanScriptDebug = CanScriptDebug, UseHotReload = UseHotReload };
    }
}
