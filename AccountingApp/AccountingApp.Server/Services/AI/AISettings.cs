namespace AccountingApp.Server.Services.AI
{
    public class AISettings
    {
        /// <summary>
        /// AI プロバイダの切替: "Mock"（既定。キー不要の疑似応答） / "Claude"（Anthropic API） / "AzureOpenAI"（従来）
        /// Mock はデモ・開発用。Claude は画像/PDF を直接読めるため Document Intelligence 不要。
        /// </summary>
        public string Provider { get; set; } = "Mock";

        // --- Claude (Anthropic) ---
        public string ClaudeApiKey { get; set; } = string.Empty;
        public string ClaudeModel { get; set; } = "claude-opus-4-8";

        // --- Azure OpenAI + Document Intelligence（従来構成） ---
        public string OpenAIEndPoint { get; set; } = string.Empty;
        public string OpenAIKey { get; set; } = string.Empty;
        public string ChatModel { get; set; } = string.Empty;
        public string DocumentAnalysisEndPoint { get; set; } = string.Empty;
        public string DocumentAnalysisKey { get; set; } = string.Empty;
    }
}
