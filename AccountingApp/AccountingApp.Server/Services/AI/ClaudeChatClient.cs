using System.Text;
using System.Text.Json;

namespace AccountingApp.Server.Services.AI
{
    /// <summary>
    /// Anthropic Messages API の最小クライアント（SDK 非依存）。
    /// 領収書 OCR は Claude のマルチモーダル入力（画像/PDF の base64）で行うため、
    /// Azure Document Intelligence を使わずに 1 コールで抽出できる。
    /// </summary>
    public static class ClaudeChatClient
    {
        static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string ApiVersion = "2023-06-01";

        public static async Task<string> CompleteAsync(string system, List<object> userContent, int maxTokens = 4096)
        {
            var cfg = SystemConfig.Instance.AISettings;
            if (string.IsNullOrWhiteSpace(cfg.ClaudeApiKey))
                throw new InvalidOperationException("AISettings.ClaudeApiKey が未設定です（appsettings.Development.json）");

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Add("x-api-key", cfg.ClaudeApiKey);
            req.Headers.Add("anthropic-version", ApiVersion);

            var body = new
            {
                model = string.IsNullOrWhiteSpace(cfg.ClaudeModel) ? "claude-opus-4-8" : cfg.ClaudeModel,
                max_tokens = maxTokens,
                temperature = 0,
                system,
                messages = new[] { new { role = "user", content = userContent } }
            };
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req);
            var resText = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Claude API error {(int)res.StatusCode}: {Truncate(resText, 500)}");

            using var doc = JsonDocument.Parse(resText);
            foreach (var c in doc.RootElement.GetProperty("content").EnumerateArray())
            {
                if (c.GetProperty("type").GetString() == "text")
                    return c.GetProperty("text").GetString() ?? string.Empty;
            }
            return string.Empty;
        }

        public static object TextBlock(string text)
            => new { type = "text", text };

        /// <summary>拡張子から画像/PDF のコンテンツブロックを作る。対応外はエラー。</summary>
        public static object FileBlock(string fileName, byte[] bytes)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var b64 = Convert.ToBase64String(bytes);
            return ext switch
            {
                ".png" => ImageBlock("image/png", b64),
                ".jpg" or ".jpeg" => ImageBlock("image/jpeg", b64),
                ".gif" => ImageBlock("image/gif", b64),
                ".webp" => ImageBlock("image/webp", b64),
                ".pdf" => new { type = "document", source = new { type = "base64", media_type = "application/pdf", data = b64 } },
                _ => throw new InvalidOperationException($"Claude プロバイダで解析できるのは画像(png/jpg/gif/webp)と PDF のみです: {fileName}")
            };
        }

        static object ImageBlock(string mediaType, string b64)
            => new { type = "image", source = new { type = "base64", media_type = mediaType, data = b64 } };

        /// <summary>モデル出力から JSON 本体を取り出す（コードフェンス・前置きの除去）。</summary>
        public static string ExtractJson(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return text;
            return text.Substring(start, end - start + 1);
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
