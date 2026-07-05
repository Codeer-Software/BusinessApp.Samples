using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.AI.OpenAI;
using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Design.Samples.AIDocumentAnalyzer;
using OpenAI.Chat;
using System.ClientModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccountingApp.Server.Services.AI
{
    public static class AITextAnalyzeService
    {
        public static async Task<ModuleData> FileToDataAsync(ModuleDataIO moduleDataIO, string? moduleName, string? fieldName, string? fileName, MemoryStream memoryStream)
        {
            var text = await ExtractTextFromFile(memoryStream);
            var source = $@"テキストは[{fileName}]をドキュメント解析した JSON です。
構造: ページ配列で、各ページは lines(表の外にあるテキスト行。text と座標 rect{{t,l,b,r}}) と tables(表) を持ちます。
表は rowCount/columnCount と rows(行×列の2次元配列) を持ちます。rows[行番号][列番号] が各セルの文字列で、空セルは "" です。
結合セルは左上のマスにのみ値が入り、覆われた残りのマスは "" です。"" のマスは値なし(空欄)として扱い、隣や下の値で埋めないでください。
空欄も含めて列位置は厳密に揃っているので、途中に空欄があっても列をずらさず、見出し行/見出し列との対応で値を抽出してください。";
            return await TextToDataAsync(moduleDataIO, moduleName, fieldName, text, source);
        }

        public static async Task<ModuleData> TextToDataAsync(ModuleDataIO moduleDataIO, string? moduleName, string? fieldName, string text)
        => await TextToDataAsync(moduleDataIO, moduleName, fieldName, text, string.Empty);


        public static async Task<ModuleData> TextToDataAsync(ModuleDataIO moduleDataIO, string? moduleName, string? fieldName, string text, string source)
        {
            var json = await DocumentAnalysisByText(DesignerService.GetDesignData().Modules, moduleName ?? string.Empty, fieldName ?? string.Empty, text, source);
            return await CreateModule(DesignerService.GetDesignData().Modules, moduleName ?? string.Empty,
                new FieldCandidatesResolver(moduleDataIO, DesignerService.GetDesignData().Modules, FindCandidatesByAI),
                JsonSerializer.Deserialize<JsonElement>(json));
        }

        static async Task<string?> FindCandidatesByAI(Dictionary<string, string> candidates, string text)
        {
            var config = SystemConfig.Instance.AISettings;

            var azureClient = new AzureOpenAIClient(
                new Uri(config.OpenAIEndPoint),
                new ApiKeyCredential(config.OpenAIKey));
            var chatClient = azureClient.GetChatClient(config.ChatModel);

            var completion = await chatClient.CompleteChatAsync(
                [
                    new SystemChatMessage(@"
提供された選択肢から最も可能性の高い一致を1つ選び、その値のみを返してください。
一致が見つからない場合は ""???"" を返してください。
応答はプログラムによって解釈されるため、絶対に追加情報を含めないでください。
答えを囲んだり、""了解しました"" のような確認の文言で返答したりしないでください。
"),
                    new UserChatMessage(string.Join(Environment.NewLine, candidates.Keys)),
                    new UserChatMessage(text),
                ]);
            return completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
        }

        static async Task<string> DocumentAnalysisByText(IModuleDesigns moduleDesigns, string moduleName, string? fieldName, string text, string source)
        {
            var config = SystemConfig.Instance.AISettings;

            var mod = moduleDesigns.Find(moduleName);
            var field = mod?.Fields.FirstOrDefault(e => e.Name == fieldName) as AITextAnalyzerFieldDesign;
            if (field == null) throw LowCodeException.Create($"Invalid Field {moduleName}.{fieldName}");

            var azureClient = new AzureOpenAIClient(
                new Uri(config.OpenAIEndPoint),
                new ApiKeyCredential(config.OpenAIKey));
            var chatClient = azureClient.GetChatClient(config.ChatModel);

            var remarks = string.IsNullOrWhiteSpace(field.Remarks) ? "" : $@"

# 補足指示
{field.Remarks}";
            var completion = await chatClient.CompleteChatAsync(
                [
                    new SystemChatMessage(@$"あなたはテキストから特定のデータを抽出する役割を担います。
私が取得すべきデータの指示とテキストを提示します。
{source}
抽出結果は JSON 形式で返してください。
指示には項目名が含まれ、必要に応じて補助名や型を括弧内に（補助名: 型）の形式で示します。
JSON 出力では、その項目名をキーとして使用してください。
フィールド名は絶対に省略しないでください。いきなり配列になることはありません。それを格納するフィールドがあるのでそこに格納してください。
配列が含まれる場合は、子要素の項目指示を再帰的に [{{子要素の項目指示}}] の形で指定します。
値が見つからない項目は null にしてください。表や本文に存在しない値を推測で補完しないでください。{remarks}"),
                    new UserChatMessage(CreateJsonExplanation(moduleDesigns, moduleName)),
                    new UserChatMessage(text),
                ], new()
                {
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
                    Temperature = 0,
                });
            return completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
        }

        static async Task<string> ExtractTextFromFile(MemoryStream stream)
        {
            var cfg = SystemConfig.Instance.AISettings;
            var client = new DocumentAnalysisClient(
                new Uri(cfg.DocumentAnalysisEndPoint),
                new AzureKeyCredential(cfg.DocumentAnalysisKey));

            if (stream.CanSeek) stream.Position = 0;

            var options = new AnalyzeDocumentOptions { Locale = "ja" }; // locale
            // prebuilt-layout は文字行に加えて表をセル構造(行/列)で返す。表中心の帳票でも罫線で区切られた格子構造をモデルに渡せる。
            var op = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", stream, options);
            var doc = op.Value;

            double R(double v) => Math.Round(v, 3);

            // 表のセルが占めるテキスト範囲(Span)。表内の行を lines として二重出力しない(=トークン節約)ための判定に使う。
            var tableSpans = doc.Tables
                .SelectMany(t => t.Cells)
                .SelectMany(c => c.Spans)
                .ToList();

            bool IsInTable(DocumentLine line) =>
                line.Spans.Any(ls => tableSpans.Any(ts => ls.Index < ts.Index + ts.Length && ts.Index < ls.Index + ls.Length));

            var pagesOut = new List<object>(doc.Pages.Count);

            foreach (var p in doc.Pages)
            {
                // 表の外にあるテキスト行のみ(タイトル・備考など)を座標付きで出力する。
                var linesOut = new List<object>();
                foreach (var line in p.Lines)
                {
                    if (IsInTable(line)) continue;

                    var xs = line.BoundingPolygon.Select(pt => pt.X);
                    var ys = line.BoundingPolygon.Select(pt => pt.Y);
                    double left = xs.Min(), right = xs.Max();
                    double bottom = ys.Min(), top = ys.Max();

                    linesOut.Add(new
                    {
                        text = line.Content,
                        rect = new
                        {
                            t = R(top),
                            l = R(left),
                            b = R(bottom),
                            r = R(right)
                        }
                    });
                }

                // このページ上の表を密なグリッド(行×列の2次元配列)で出力。
                // 空セルも "" として全位置を明示することで、途中に空欄があっても列がずれない(DIは空セルを省略することがあるため)。
                // 結合セル(rowSpan/colSpan)は左上の1マスにのみ値を入れ、覆われた残りのマスは "" のままにする
                // (範囲に複製すると、本来空の隣/下のマスに値が入ってしまうため)。
                var tablesOut = new List<object>();
                foreach (var table in doc.Tables.Where(t => t.BoundingRegions.Any(br => br.PageNumber == p.PageNumber)))
                {
                    var grid = new string[table.RowCount][];
                    for (int r = 0; r < table.RowCount; r++)
                    {
                        grid[r] = new string[table.ColumnCount];
                        Array.Fill(grid[r], "");
                    }
                    foreach (var cell in table.Cells)
                    {
                        if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
                            grid[cell.RowIndex][cell.ColumnIndex] = cell.Content ?? "";
                    }
                    tablesOut.Add(new
                    {
                        rowCount = table.RowCount,
                        columnCount = table.ColumnCount,
                        rows = grid
                    });
                }

                pagesOut.Add(new { lines = linesOut, tables = tablesOut });
            }

            return JsonSerializer.Serialize(
                pagesOut,
                new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                });
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
