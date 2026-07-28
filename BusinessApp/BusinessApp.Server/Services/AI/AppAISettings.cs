using Codeer.LowCode.Blazor.Extras.Server.AI;

namespace BusinessApp.Server.Services.AI
{
    /// <summary>
    /// AI プロバイダ切替つき設定（Extras 標準 AISettings の拡張）。
    /// Provider: "Mock"（既定。キー不要の疑似応答） / "Claude"（Anthropic API） /
    /// "AzureOpenAI"（Extras.Server 標準実装に委譲。OpenAIEndPoint 等の基底プロパティを使用）
    /// Mock はデモ・開発用。Claude は画像/PDF を直接読めるため Document Intelligence 不要。
    /// 実キーは .NET User Secrets へ（docs/decisions/0024）。
    /// </summary>
    public class AppAISettings : AISettings
    {
        public string Provider { get; set; } = "Mock";

        // --- Claude (Anthropic) ---
        public string ClaudeApiKey { get; set; } = string.Empty;
        public string ClaudeModel { get; set; } = "claude-opus-4-8";
    }
}
