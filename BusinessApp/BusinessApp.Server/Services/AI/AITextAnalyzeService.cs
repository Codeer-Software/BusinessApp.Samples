using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Extras.Designs;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BusinessApp.Server.Services.AI
{
    /// <summary>
    /// AI 読み取り（C-1）のプロバイダ切替ディスパッチ。
    /// Mock（キー不要の疑似応答）と Claude（マルチモーダルで画像/PDF を直接読む）はここで実装し、
    /// AzureOpenAI（Document Intelligence + チャット抽出の従来構成）は
    /// Extras.Server 標準の AITextAnalyzeService に委譲する。
    /// </summary>
    public static class AITextAnalyzeService
    {
        static AppAISettings Settings => SystemConfig.Instance.AISettings;

        // プロバイダ判定（AISettings.Provider: Mock / Claude / AzureOpenAI）
        static bool IsMock => string.Equals(Settings.Provider, "Mock", StringComparison.OrdinalIgnoreCase)
                              || string.IsNullOrWhiteSpace(Settings.Provider);
        static bool IsClaude => string.Equals(Settings.Provider, "Claude", StringComparison.OrdinalIgnoreCase);

        public static async Task<ModuleData> FileToDataAsync(ModuleDataIO moduleDataIO, string? moduleName, string? fieldName, string? fileName, MemoryStream memoryStream)
        {
            if (IsMock)
                return await BuildFromJsonAsync(moduleDataIO, moduleName, MockReceiptJson());

            if (IsClaude)
            {
                // Claude はマルチモーダル入力で画像/PDF を直接読める（Document Intelligence 不要）
                var (system, explanation) = BuildExtractionPrompt(moduleName ?? string.Empty, fieldName,
                    $"添付は[{fileName}]（領収書・請求書などの画像/PDF）です。記載内容を直接読み取ってください。");
                var content = new List<object>
                {
                    ClaudeChatClient.FileBlock(fileName ?? "file.png", memoryStream.ToArray()),
                    ClaudeChatClient.TextBlock(explanation)
                };
                var raw = await ClaudeChatClient.CompleteAsync(system, content);
                return await BuildFromJsonAsync(moduleDataIO, moduleName, ClaudeChatClient.ExtractJson(raw));
            }

            // AzureOpenAI: Extras.Server 標準実装（Document Intelligence でレイアウト解析 → チャットで抽出）に委譲
            return await new Codeer.LowCode.Blazor.Extras.Server.AI.AITextAnalyzeService(Settings).FileToDataAsync(
                moduleDataIO, DesignerService.GetDesignData().Modules,
                moduleName ?? string.Empty, GetRemarks(moduleName, fieldName), fileName, memoryStream);
        }

        public static async Task<ModuleData> TextToDataAsync(ModuleDataIO moduleDataIO, string? moduleName, string? fieldName, string text)
        {
            if (IsMock)
                return await BuildFromJsonAsync(moduleDataIO, moduleName, MockReceiptJson());

            if (IsClaude)
            {
                var (system, explanation) = BuildExtractionPrompt(moduleName ?? string.Empty, fieldName, string.Empty);
                var raw = await ClaudeChatClient.CompleteAsync(system,
                    new List<object> { ClaudeChatClient.TextBlock(explanation), ClaudeChatClient.TextBlock(text) });
                return await BuildFromJsonAsync(moduleDataIO, moduleName, ClaudeChatClient.ExtractJson(raw));
            }

            return await new Codeer.LowCode.Blazor.Extras.Server.AI.AITextAnalyzeService(Settings).TextToDataAsync(
                moduleDataIO, DesignerService.GetDesignData().Modules,
                moduleName ?? string.Empty, GetRemarks(moduleName, fieldName), text);
        }

        static string GetRemarks(string? moduleName, string? fieldName)
        {
            var mod = DesignerService.GetDesignData().Modules.Find(moduleName ?? string.Empty);
            var field = mod?.Fields.FirstOrDefault(e => e.Name == fieldName) as AITextAnalyzerFieldDesign;
            if (field == null) throw LowCodeException.Create($"Invalid Field {moduleName}.{fieldName}");
            return field.Remarks;
        }

        static async Task<ModuleData> BuildFromJsonAsync(ModuleDataIO moduleDataIO, string? moduleName, string json)
            => await CreateModule(DesignerService.GetDesignData().Modules, moduleName ?? string.Empty,
                new FieldCandidatesResolver(moduleDataIO, DesignerService.GetDesignData().Modules, ResolveCandidateAsync),
                JsonSerializer.Deserialize<JsonElement>(json));

        // モック応答: 領収書らしい固定値（存在しないフィールド名は CreateModule 側でスキップされるため
        // どのモジュールに対しても安全）。実キー投入前のデモ・UI 検証用
        static string MockReceiptJson()
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            return $@"{{
  ""Title"": ""会食代（AIモック読み取り）"",
  ""Amount"": 12800,
  ""TaxAmount"": 1163,
  ""ExpenseDate"": ""{today}"",
  ""UsedAt"": ""炭火焼鳥 とり菊 神田本店"",
  ""ExpenseCategoryRef"": ""交際費"",
  ""EntertainmentGuest"": ""株式会社アルタイル商事 佐藤様ほか"",
  ""EntertainmentCount"": 4,
  ""EntertainmentPurpose"": ""プロジェクト完了の御礼""
}}";
        }

        // 抽出プロンプト（システム＋項目説明）。Claude 用
        static (string system, string explanation) BuildExtractionPrompt(string moduleName, string? fieldName, string source)
        {
            var remarksText = GetRemarks(moduleName, fieldName);
            var remarks = string.IsNullOrWhiteSpace(remarksText) ? "" : $@"

# 補足指示
{remarksText}";
            var system = @$"あなたはテキストから特定のデータを抽出する役割を担います。
私が取得すべきデータの指示とテキストを提示します。
{source}
抽出結果は JSON 形式で返してください。JSON 以外の文字（前置き・コードフェンス等）を出力しないでください。
指示には項目名が含まれ、必要に応じて補助名や型を括弧内に（補助名: 型）の形式で示します。
JSON 出力では、その項目名をキーとして使用してください。
フィールド名は絶対に省略しないでください。いきなり配列になることはありません。それを格納するフィールドがあるのでそこに格納してください。
配列が含まれる場合は、子要素の項目指示を再帰的に [{{子要素の項目指示}}] の形で指定します。
値が見つからない項目は null にしてください。表や本文に存在しない値を推測で補完しないでください。{remarks}";
            return (system, CreateJsonExplanation(DesignerService.GetDesignData().Modules, moduleName));
        }

        // Select/Link の候補解決（Mock はローカル一致・Claude は API で選択）
        static async Task<string?> ResolveCandidateAsync(Dictionary<string, string> candidates, string text)
        {
            if (IsMock) return ResolveCandidateLocal(candidates, text);
            var raw = await ClaudeChatClient.CompleteAsync(@"
提供された選択肢から最も可能性の高い一致を1つ選び、その値のみを返してください。
一致が見つからない場合は ""???"" を返してください。
応答はプログラムによって解釈されるため、絶対に追加情報を含めないでください。
答えを囲んだり、""了解しました"" のような確認の文言で返答したりしないでください。",
                new List<object>
                {
                    ClaudeChatClient.TextBlock(string.Join(Environment.NewLine, candidates.Keys)),
                    ClaudeChatClient.TextBlock(text)
                }, 256);
            return raw.Trim();
        }

        // ローカル候補解決（Mock 用）: 完全一致 → 部分一致 → ???
        static string ResolveCandidateLocal(Dictionary<string, string> candidates, string text)
        {
            var t = (text ?? string.Empty).Trim();
            foreach (var k in candidates.Keys)
                if (string.Equals(k, t, StringComparison.OrdinalIgnoreCase)) return k;
            foreach (var k in candidates.Keys)
                if (!string.IsNullOrEmpty(t) && (k.Contains(t) || t.Contains(k))) return k;
            return "???";
        }

        static async Task<ModuleData> CreateModule(IModuleDesigns moduleDesigns, string moduleName, FieldCandidatesResolver candidateCache, JsonElement root)
        {
            var moduleDesign = moduleDesigns.Find(moduleName);
            if (moduleDesign == null) throw LowCodeException.Create($"Invalid Module {moduleName}");

            var moduleData = new ModuleData { Name = moduleDesign.Name };
            foreach (var element in root.EnumerateObject())
            {
                var fieldDesign = moduleDesign.Fields.FirstOrDefault(e => e.Name == element.Name);
                if (fieldDesign == null) continue;
                if (fieldDesign is IdFieldDesign id && !id.IsManualInput) continue;

                var value = GetValue(element.Value);
                var data = fieldDesign.CreateData();

                // List 以外で値が null/空のものはスキップ(AIが「見つからない」を null で返すため)。
                if (data is not ListFieldData && IsNullOrEmptyValue(value)) continue;

                try
                {
                    if (data is BooleanFieldData booleanData)
                    {
                        if (!TryParseBoolean(value, out var b)) continue;
                        booleanData.Value = b;
                    }
                    else if (data is IdFieldData idData) idData.Value = Convert.ToString(value);
                    else if (data is TextFieldData textData) textData.Value = Convert.ToString(value);
                    else if (data is NumberFieldData numberData)
                    {
                        if (!TryParseDecimal(value, out var num)) continue;
                        numberData.Value = num;
                    }
                    else if (data is DateFieldData dateData)
                    {
                        if (!TryParseDateTime(value, out var dt)) continue;
                        dateData.Value = DateOnly.FromDateTime(dt);
                    }
                    else if (data is DateTimeFieldData dateTimeData)
                    {
                        if (!TryParseDateTime(value, out var dt)) continue;
                        dateTimeData.Value = dt;
                    }
                    else if (data is TimeFieldData TimeData)
                    {
                        if (!TryParseTime(value, out var t)) continue;
                        TimeData.Value = t;
                    }
                    else if (data is ListFieldData ListData)
                    {
                        var childModuleName = ((ListFieldDesign)fieldDesign).SearchCondition.ModuleName;
                        if (element.Value.ValueKind != JsonValueKind.Array) continue;
                        foreach (var e in element.Value.EnumerateArray())
                        {
                            ListData.Children.Add(await CreateModule(moduleDesigns, childModuleName, candidateCache, e));
                        }
                    }
                    else if (data is SelectFieldData selectData) await candidateCache.GetSelectValue(moduleName, (SelectFieldDesign)fieldDesign, value?.ToString() ?? string.Empty, selectData);
                    else if (data is LinkFieldData linkData) await candidateCache.GetLinkValue(moduleName, (LinkFieldDesign)fieldDesign, value?.ToString() ?? string.Empty, linkData);
                    else continue;
                }
                catch
                {
                    continue;
                }
                moduleData.Fields[element.Name] = data;
            }
            return moduleData;
        }

        static object? GetValue(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) return element.GetString();
            else if (element.ValueKind == JsonValueKind.Number) return element.GetDecimal();
            else if (element.ValueKind == JsonValueKind.True) return true;
            else if (element.ValueKind == JsonValueKind.False) return false;
            return element;
        }

        static string CreateJsonExplanation(IModuleDesigns moduleDesigns, string moduleName)
        {
            var moduleDesign = moduleDesigns.Find(moduleName);
            if (moduleDesign == null) throw LowCodeException.Create($"Invalid Module {moduleName}");

            var list = new List<string>();
            foreach (var field in moduleDesign.Fields.Where(e => IsSupportedType(e)))
            {
                var info = new List<string>([GetJsonType(field)]);
                if (field is IDisplayName diplayName && !string.IsNullOrEmpty(diplayName.DisplayName)) info.Add(diplayName.DisplayName);
                var explanation = $"{field.Name}({string.Join(", ", info)})";
                if (field is ListFieldDesign listFieldDesign)
                {
                    explanation += $"[{CreateJsonExplanation(moduleDesigns, listFieldDesign.SearchCondition.ModuleName)}]";
                }
                list.Add(explanation);
            }
            return string.Join(",", list);
        }

        static bool IsSupportedType(FieldDesignBase? e) =>
            e is BooleanFieldDesign
             or IdFieldDesign
             or TextFieldDesign
             or NumberFieldDesign
             or DateFieldDesign
             or DateTimeFieldDesign
             or TimeFieldDesign
             or ListFieldDesign
             or SelectFieldDesign
             or LinkFieldDesign;

        static string GetJsonType(FieldDesignBase design)
        {
            if (design is BooleanFieldDesign booleanData) return "Boolean";
            else if (design is NumberFieldDesign numberData) return "Number";
            else if (design is ListFieldDesign ListData) return "Array";
            return "String";
        }

        static bool IsNullOrEmptyValue(object? value)
        {
            if (value is null) return true;
            if (value is string s) return string.IsNullOrWhiteSpace(s);
            if (value is JsonElement je)
                return je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || (je.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(je.GetString()));
            return false;
        }

        // 全角数字・記号を半角化(数値/日付パースの前処理)。
        static string NormalizeDigits(string s)
        {
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                if (c >= '０' && c <= '９') arr[i] = (char)(c - '０' + '0');
                else if (c == '．') arr[i] = '.';
                else if (c == '，' || c == '、') arr[i] = ',';
                else if (c == '－' || c == '−' || c == 'ー') arr[i] = '-';
                else if (c == '：') arr[i] = ':';
                else if (c == '／') arr[i] = '/';
            }
            return new string(arr);
        }

        static bool TryParseDecimal(object? value, out decimal result)
        {
            result = 0m;
            if (value is decimal dec) { result = dec; return true; }
            if (value is bool) return false;
            var s = value?.ToString();
            if (string.IsNullOrWhiteSpace(s)) return false;

            // 桁区切り・通貨記号・単位(%等)を除いて数値部分だけ取り出す。
            s = NormalizeDigits(s).Replace(",", "").Replace(" ", "").Replace("　", "");
            var m = Regex.Match(s, @"[-+]?\d*\.?\d+([eE][-+]?\d+)?");
            return m.Success && decimal.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        static readonly HashSet<string> _trueWords = new(StringComparer.OrdinalIgnoreCase)
        { "true", "1", "yes", "y", "t", "on", "はい", "○", "◯", "✓", "レ", "有", "あり", "オン" };
        static readonly HashSet<string> _falseWords = new(StringComparer.OrdinalIgnoreCase)
        { "false", "0", "no", "n", "f", "off", "いいえ", "×", "✗", "無", "なし", "オフ" };

        static bool TryParseBoolean(object? value, out bool result)
        {
            result = false;
            if (value is bool b) { result = b; return true; }
            if (value is decimal d) { result = d != 0m; return true; }
            var s = value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(s)) return false;
            if (_trueWords.Contains(s)) { result = true; return true; }
            if (_falseWords.Contains(s)) { result = false; return true; }
            return false;
        }

        static readonly CultureInfo _ja = CultureInfo.GetCultureInfo("ja-JP");
        static readonly string[] _dateFormats =
        {
            "yyyy/M/d", "yyyy-M-d", "yyyy.M.d", "yyyy年M月d日",
            "yyyy/M/d H:m", "yyyy/M/d H:m:s", "yyyy-M-dTH:m:s", "yyyy年M月d日 H時m分",
        };

        static bool TryParseDateTime(object? value, out DateTime result)
        {
            result = default;
            var s = value?.ToString();
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = NormalizeDigits(s).Trim();
            return DateTime.TryParse(s, _ja, DateTimeStyles.None, out result)
                || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
                || DateTime.TryParseExact(s, _dateFormats, _ja, DateTimeStyles.None, out result);
        }

        static bool TryParseTime(object? value, out TimeOnly result)
        {
            result = default;
            var s = value?.ToString();
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = NormalizeDigits(s).Trim();
            return TimeOnly.TryParse(s, _ja, DateTimeStyles.None, out result)
                || TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
        }
    }
}
