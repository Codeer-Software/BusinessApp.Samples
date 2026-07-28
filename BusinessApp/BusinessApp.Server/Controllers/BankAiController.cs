using System.Text.Json;
using BusinessApp.Server.Services;
using BusinessApp.Server.Services.AI;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Chat;
using System.ClientModel;

namespace BusinessApp.Server.Controllers
{
    /// <summary>
    /// 銀行明細の勘定科目 AI 推定（D-2）。
    /// マッチングルール未該当の明細について、摘要と入出金から相手勘定科目を推定する。
    /// プロバイダは AISettings.Provider（Mock / Claude / AzureOpenAI）に従う（C-1 と同じ切替基盤）。
    /// リクエスト: { "Candidates": "code 科目名\n...", "Lines": "id|摘要|入金|出金\n..." }
    /// レスポンス: { "suggestions": [ { "id": 123, "code": "6130" }, ... ] }
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/bank_ai")]
    public class BankAiController : ControllerBase
    {
        public record SuggestRequest(string? Candidates, string? Lines);

        static bool IsMock => string.Equals(SystemConfig.Instance.AISettings.Provider, "Mock", StringComparison.OrdinalIgnoreCase)
                              || string.IsNullOrWhiteSpace(SystemConfig.Instance.AISettings.Provider);
        static bool IsClaude => string.Equals(SystemConfig.Instance.AISettings.Provider, "Claude", StringComparison.OrdinalIgnoreCase);

        const string SystemPrompt = @"あなたは日本の中小企業の経理担当を支援する会計 AI です。
銀行口座の入出金明細の摘要から、複式簿記の相手勘定科目を推定します。
まず勘定科目の候補一覧（コードと名称）を提示し、続いて明細一覧を「id|摘要|入金額|出金額」の形式で提示します。
各明細に最も適切な勘定科目コードを 1 つ選んでください。
- 出金は費用・資産の取得・負債の返済（例: サーバー利用料→通信費、事務所家賃→地代家賃、振込手数料→支払手数料）
- 入金は収益・債権の回収（例: 取引先からの振込→売掛金）
- 判断できない場合は出金なら雑費、入金なら売掛金のコードを選ぶ
応答は次の JSON のみを出力してください（前置き・コードフェンス禁止）:
{""suggestions"":[{""id"":<明細id>,""code"":""<科目コード>""}]}";

        [HttpPost("suggest")]
        public async Task<IActionResult> SuggestAsync([FromBody] SuggestRequest req)
        {
            var candidates = req.Candidates ?? string.Empty;
            var lines = req.Lines ?? string.Empty;
            if (string.IsNullOrWhiteSpace(lines))
                return Ok(new { suggestions = Array.Empty<object>(), isMock = IsMock });

            if (IsMock)
                return Ok(new { suggestions = MockSuggest(candidates, lines), isMock = true });

            string raw;
            if (IsClaude)
            {
                raw = await ClaudeChatClient.CompleteAsync(SystemPrompt, new List<object>
                {
                    ClaudeChatClient.TextBlock("勘定科目の候補一覧:\n" + candidates),
                    ClaudeChatClient.TextBlock("明細一覧 (id|摘要|入金額|出金額):\n" + lines)
                });
                raw = ClaudeChatClient.ExtractJson(raw);
            }
            else
            {
                var config = SystemConfig.Instance.AISettings;
                var azureClient = new AzureOpenAIClient(new Uri(config.OpenAIEndPoint), new ApiKeyCredential(config.OpenAIKey));
                var chatClient = azureClient.GetChatClient(config.ChatModel);
                var completion = await chatClient.CompleteChatAsync(
                    [
                        new SystemChatMessage(SystemPrompt),
                        new UserChatMessage("勘定科目の候補一覧:\n" + candidates),
                        new UserChatMessage("明細一覧 (id|摘要|入金額|出金額):\n" + lines),
                    ], new()
                    {
                        ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
                        Temperature = 0,
                    });
                raw = completion.Value.Content.FirstOrDefault()?.Text ?? "{}";
            }

            // モデル出力の検証: id が数値で code が候補に実在するものだけ通す
            var valid = ParseAndValidate(raw, candidates);
            return Ok(new { suggestions = valid, isMock = false });
        }

        static List<object> ParseAndValidate(string raw, string candidates)
        {
            var validCodes = new HashSet<string>(
                candidates.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim().Split(' ')[0])
                    .Where(c => c.Length > 0));
            var result = new List<object>();
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("suggestions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return result;
                foreach (var e in arr.EnumerateArray())
                {
                    if (!e.TryGetProperty("id", out var idEl) || !e.TryGetProperty("code", out var codeEl)) continue;
                    if (!idEl.TryGetInt64(out var id)) continue;
                    var code = codeEl.GetString() ?? "";
                    if (!validCodes.Contains(code)) continue;
                    result.Add(new { id, code });
                }
            }
            catch (JsonException)
            {
                // モデルが不正 JSON を返した場合は空（クライアント側は「推定なし」として扱う）
            }
            return result;
        }

        // モック: キーワードの素朴なヒューリスティック（実キーなしで E2E を通すための疑似応答）
        static List<object> MockSuggest(string candidates, string lines)
        {
            var candList = candidates.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Select(l => (code: l.Split(' ')[0], name: l.Contains(' ') ? l[(l.IndexOf(' ') + 1)..] : ""))
                .Where(c => c.code.Length > 0 && c.name.Length > 0)
                .ToList();
            string CodeOf(string name) => candList.FirstOrDefault(c => c.name == name).code ?? "";

            var result = new List<object>();
            foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length < 4 || !long.TryParse(parts[0].Trim(), out var id)) continue;
                var desc = parts[1];
                var isIn = long.TryParse(parts[2].Trim(), out var inAmt) && inAmt > 0;

                // 摘要に科目名（2文字以上）が含まれればそれを採用
                var hit = candList.FirstOrDefault(c => c.name.Length >= 2 && desc.Contains(c.name));
                var code = hit.code;
                if (string.IsNullOrEmpty(code))
                    code = isIn ? CodeOf("売掛金") : CodeOf("雑費");
                if (string.IsNullOrEmpty(code)) continue;
                result.Add(new { id, code });
            }
            return result;
        }
    }
}
