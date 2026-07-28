using System.Text.Json;
using System.Text.RegularExpressions;
using BusinessApp.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessApp.Server.Controllers
{
    /// <summary>
    /// インボイス登録番号の実在チェック（C-3）。
    /// 国税庁 適格請求書発行事業者公表システム Web-API を叩く（サーバ経由の定石: CORS/キー秘匿）。
    /// リクエスト仕様: GET https://web-api.invoice-kohyo.nta.go.jp/1/num?id={appId}&number=T{13桁}&type=21
    /// ApplicationId 未設定時はモック（形式チェック＋疑似応答）で動作する。
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/invoice_check")]
    public class InvoiceCheckController : ControllerBase
    {
        static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        const string NtaEndpoint = "https://web-api.invoice-kohyo.nta.go.jp/1/num";

        [HttpGet]
        public async Task<IActionResult> CheckAsync(string? number)
        {
            var regNo = (number ?? string.Empty).Trim().ToUpperInvariant();
            if (!regNo.StartsWith("T")) regNo = "T" + regNo;

            // 形式: T + 13桁数字（法人番号）
            if (!Regex.IsMatch(regNo, @"^T\d{13}$"))
            {
                return Ok(new
                {
                    status = "invalid",
                    isMock = false,
                    message = "登録番号の形式が不正です（T+13桁の数字）"
                });
            }

            var appId = SystemConfig.Instance.NtaInvoiceSettings.ApplicationId;
            if (string.IsNullOrWhiteSpace(appId))
            {
                // モック: 形式が正しければ「登録あり」の疑似応答（末尾が 0 の番号のみ「該当なし」にして両分岐をテスト可能に）
                var notFound = regNo.EndsWith("0");
                return Ok(new
                {
                    status = notFound ? "not_found" : "ok",
                    isMock = true,
                    registratedNumber = regNo,
                    name = notFound ? "" : "モック株式会社（疑似応答）",
                    address = notFound ? "" : "東京都千代田区モック1-2-3",
                    registrationDate = notFound ? "" : "2023-10-01",
                    message = notFound
                        ? "該当する事業者が見つかりません（モック応答）"
                        : "適格請求書発行事業者として登録されています（モック応答。実照合は国税庁アプリケーションID設定後）"
                });
            }

            var url = $"{NtaEndpoint}?id={Uri.EscapeDataString(appId)}&number={regNo}&type=21&history=0";
            using var res = await _http.GetAsync(url);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                return Ok(new
                {
                    status = "error",
                    isMock = false,
                    message = $"国税庁 Web-API エラー ({(int)res.StatusCode})"
                });
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("announcement", out var ann)
                && ann.ValueKind == JsonValueKind.Array && ann.GetArrayLength() > 0)
            {
                var a = ann[0];
                string S(string key) => a.TryGetProperty(key, out var v) ? (v.GetString() ?? "") : "";
                return Ok(new
                {
                    status = "ok",
                    isMock = false,
                    registratedNumber = S("registratedNumber"),
                    name = S("name"),
                    address = S("address"),
                    registrationDate = S("registrationDate"),
                    message = "適格請求書発行事業者として登録されています"
                });
            }

            return Ok(new
            {
                status = "not_found",
                isMock = false,
                message = "該当する事業者が見つかりません（番号を確認してください）"
            });
        }
    }
}
