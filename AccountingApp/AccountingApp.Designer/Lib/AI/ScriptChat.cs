using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.DesignLogic.Check;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.Repository.Design;
using OpenAI.Chat;
using System.IO;
using System.Text;

namespace AccountingApp.Designer.Lib.AI
{
    public class ScriptChat : IAIChat
    {
        readonly IScriptEditor _editor;
        readonly ChatClient _chatClient;
        readonly IDesignerChatHost _host;
        readonly List<ChatMessage> _messages = new();

        public string Explanation => "スクリプトを編集するためのチャットです";

        public ScriptChat(IDesignerChatHost host, AISettings settings, IScriptEditor editor)
        {
            _host = host;
            _editor = editor;
            _chatClient = settings.CreateChatClient();
        }

        public void Clear() => _messages.Clear();

        public async Task<string> ProcessMessage(string message)
        {
            var currentScript = _editor.GetScript();

            // 仕様プロンプト(SystemPrompt + スクリプト仕様Docs + モジュール情報)は会話の最初の1回だけ履歴に入れる。
            // 会話を重ねても仕様Docsを毎回送り直さないよう、以降は履歴に残った1回を使い回す。
            if (_messages.Count == 0)
            {
                _messages.Add(new SystemChatMessage(SystemPrompt));
                if (!string.IsNullOrEmpty(ScriptReference))
                    _messages.Add(new SystemChatMessage(
                        "## スクリプト仕様（言語仕様・Module/Field API・組み込み/拡張サービス・規約）\n\n" + ScriptReference));
                var context = BuildModuleContextInfo();
                if (!string.IsNullOrEmpty(context))
                    _messages.Add(new SystemChatMessage(context));
            }

            // 現在のスクリプトと指示は毎ターン追加する(スクリプトは編集で変わるため常に最新を渡す)。
            _messages.Add(new UserChatMessage(
                $"現在のスクリプト:\n```csharp\n{currentScript}\n```\n\n指示: {message}"));

            // 生成 → デザインチェック → エラーがあればAIに返して再生成、を上限まで繰り返す自己修正ループ。
            const int maxAttempts = 3;
            var lastScript = string.Empty;
            var lastExplanation = string.Empty;
            var lastErrors = new List<DesignCheckInfo>();

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                AIResponse response;
                string resultText;
                try
                {
                    var result = await _chatClient.CompleteChatAsync(_messages,
                        new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() });
                    resultText = result.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
                    response = JsonConverterEx.DeserializeObject<AIResponse>(resultText)!;
                }
                catch (Exception ex)
                {
                    // 失敗したターンのユーザーメッセージは履歴に残さない(次回の再送・文脈汚染を防ぐ)。
                    if (_messages.Count > 0 && _messages[^1] is UserChatMessage)
                        _messages.RemoveAt(_messages.Count - 1);
                    return $"エラーリトライしてください\r\n{ex.Message}";
                }

                _messages.Add(new AssistantChatMessage(resultText));
                lastScript = response.Script;
                lastExplanation = response.Explanation;

                var errors = ValidateScript(response.Script);
                if (errors.Count == 0)
                {
                    _editor.Update(response.Script);
                    return string.IsNullOrEmpty(response.Explanation)
                        ? "スクリプトを変更しました"
                        : response.Explanation;
                }

                lastErrors = errors;
                if (attempt < maxAttempts)
                {
                    // デザインチェックエラーをAIに返して修正を促す
                    _messages.Add(new UserChatMessage(
                        "生成されたスクリプトに次のデザインチェックエラーがあります。修正してスクリプト全体を再度出力してください。\n"
                        + FormatErrors(errors)));
                }
            }

            // 上限まで試してもエラーが残った: 最後の生成を適用しつつ警告する(ユーザーが手で直せるように)。
            _editor.Update(lastScript);
            var head = string.IsNullOrEmpty(lastExplanation) ? "スクリプトを変更しました" : lastExplanation;
            return $"{head}\r\n（注意: デザインチェックエラーが残っています。内容を確認してください）\r\n{FormatErrors(lastErrors)}";
        }

        List<DesignCheckInfo> ValidateScript(string script)
        {
            try
            {
                return _editor.CheckScript(script);
            }
            catch
            {
                // 検証自体が失敗した場合はエラーなし扱い(検証でチャットを止めない)
                return new();
            }
        }

        static string FormatErrors(List<DesignCheckInfo> errors)
            => string.Join("\n", errors.Select(e => $"- {e.GetPositionText()}: {e.Message}"));

        string BuildModuleContextInfo()
        {
            try
            {
                var designData = _host.GetDesignData();
                var lines = new List<string>();

                // 全モジュール一覧（ダイアログ表示等で他モジュールを参照する際に必要）
                var allModuleNames = designData.Modules.GetModuleNames();
                if (allModuleNames.Count > 0)
                {
                    lines.Add("## プロジェクト内の全モジュール一覧");
                    lines.Add("ダイアログ表示（new ModuleName()）やModuleSearcher<ModuleName>()で使用可能なモジュール:");
                    foreach (var name in allModuleNames)
                    {
                        var m = designData.Modules.Find(name);
                        if (m == null) continue;
                        var fieldNames = m.Fields.Select(f => f.Name).ToList();
                        var fieldSummary = fieldNames.Count > 0
                            ? $" - フィールド: {string.Join(", ", fieldNames)}"
                            : "";
                        lines.Add($"- {name}{fieldSummary}");
                    }
                    lines.Add("");
                }

                // 現在編集中のモジュールの詳細情報
                var moduleName = GetModuleName();
                if (!string.IsNullOrEmpty(moduleName))
                {
                    var mod = designData.Modules.Find(moduleName);
                    if (mod != null)
                    {
                        lines.Add($"## 現在編集中のモジュール: {moduleName}");

                        // フィールド一覧（Name + 型名）
                        if (mod.Fields.Count > 0)
                        {
                            lines.Add("\n### フィールド一覧");
                            foreach (var f in mod.Fields)
                            {
                                var typeName = f.GetType().Name.Replace("Design", "");
                                var extra = GetFieldExtra(f);
                                lines.Add(extra.Length > 0
                                    ? $"- {f.Name} ({typeName}) {extra}"
                                    : $"- {f.Name} ({typeName})");
                            }
                        }

                        // DetailLayoutのイベント
                        foreach (var kvp in mod.DetailLayouts)
                        {
                            var layoutKey = string.IsNullOrEmpty(kvp.Key) ? "デフォルト" : kvp.Key;
                            var events = new List<string>();
                            if (!string.IsNullOrEmpty(kvp.Value.OnBeforeInitialization))
                                events.Add($"OnBeforeInitialization: {kvp.Value.OnBeforeInitialization}");
                            if (!string.IsNullOrEmpty(kvp.Value.OnAfterInitialization))
                                events.Add($"OnAfterInitialization: {kvp.Value.OnAfterInitialization}");
                            if (!string.IsNullOrEmpty(kvp.Value.OnLocationChanging))
                                events.Add($"OnLocationChanging: {kvp.Value.OnLocationChanging}");
                            if (!string.IsNullOrEmpty(kvp.Value.OnFieldDataChanged))
                                events.Add($"OnFieldDataChanged: {kvp.Value.OnFieldDataChanged}");
                            if (events.Count > 0)
                            {
                                lines.Add($"\n### DetailLayout({layoutKey})のイベント");
                                lines.AddRange(events.Select(e => $"- {e}"));
                            }
                        }

                        // ListLayoutのイベント
                        foreach (var kvp in mod.ListLayouts)
                        {
                            var layoutKey = string.IsNullOrEmpty(kvp.Key) ? "デフォルト" : kvp.Key;
                            var events = new List<string>();
                            if (!string.IsNullOrEmpty(kvp.Value.OnBeforeInitialization))
                                events.Add($"OnBeforeInitialization: {kvp.Value.OnBeforeInitialization}");
                            if (!string.IsNullOrEmpty(kvp.Value.OnAfterInitialization))
                                events.Add($"OnAfterInitialization: {kvp.Value.OnAfterInitialization}");
                            if (!string.IsNullOrEmpty(kvp.Value.OnFieldDataChanged))
                                events.Add($"OnFieldDataChanged: {kvp.Value.OnFieldDataChanged}");
                            if (events.Count > 0)
                            {
                                lines.Add($"\n### ListLayout({layoutKey})のイベント");
                                lines.AddRange(events.Select(e => $"- {e}"));
                            }
                        }

                        // SearchLayoutのイベント
                        foreach (var kvp in mod.SearchLayouts)
                        {
                            var layoutKey = string.IsNullOrEmpty(kvp.Key) ? "デフォルト" : kvp.Key;
                            if (!string.IsNullOrEmpty(kvp.Value.OnSearchInitialization))
                            {
                                lines.Add($"\n### SearchLayout({layoutKey})のイベント");
                                lines.Add($"- OnSearchInitialization: {kvp.Value.OnSearchInitialization}");
                            }
                        }

                        // 関連モジュール名（LinkField, ListField等の参照先）
                        var relatedModules = new HashSet<string>();
                        foreach (var f in mod.Fields)
                        {
                            var searchModuleName = GetSearchModuleName(f);
                            if (!string.IsNullOrEmpty(searchModuleName))
                                relatedModules.Add(searchModuleName);
                        }
                        if (relatedModules.Count > 0)
                        {
                            lines.Add($"\n### 関連モジュール: {string.Join(", ", relatedModules)}");
                        }
                    }
                }

                return lines.Count > 0 ? string.Join("\n", lines) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        string? GetModuleName()
        {
            var name = _editor.GetModuleName();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        static string GetFieldExtra(FieldDesignBase field)
        {
            var parts = new List<string>();
            // イベントハンドラ名を収集
            AddEventIfSet(field, "OnClick", parts);
            AddEventIfSet(field, "OnDataChanged", parts);
            AddEventIfSet(field, "OnSearchDataChanged", parts);
            AddEventIfSet(field, "OnSearchButtonClicked", parts);
            AddEventIfSet(field, "OnSearched", parts);
            AddEventIfSet(field, "OnSelectedIndexChanged", parts);
            AddEventIfSet(field, "OnSelectedIndexChanging", parts);
            AddEventIfSet(field, "OnTransaction", parts);
            return parts.Count > 0 ? $"[{string.Join(", ", parts)}]" : string.Empty;
        }

        static void AddEventIfSet(FieldDesignBase field, string propertyName, List<string> parts)
        {
            var prop = field.GetType().GetProperty(propertyName);
            if (prop == null) return;
            var value = prop.GetValue(field) as string;
            if (!string.IsNullOrEmpty(value))
                parts.Add($"{propertyName}: {value}");
        }

        static string? GetSearchModuleName(FieldDesignBase field)
        {
            var prop = field.GetType().GetProperty("SearchCondition");
            if (prop == null) return null;
            var condition = prop.GetValue(field);
            if (condition == null) return null;
            var moduleNameProp = condition.GetType().GetProperty("ModuleName");
            var moduleName = moduleNameProp?.GetValue(condition) as string;
            return string.IsNullOrEmpty(moduleName) ? null : moduleName;
        }

        // スクリプトの仕様知識(言語仕様・Module/Field API・組み込み/拡張サービス・規約)は
        // Lib/AI 配下の各 .md を埋め込みリソースとして読み込んで連結する(csproj の EmbeddedResource を参照)。
        static readonly string ScriptReference = LoadScriptReference();

        static string LoadScriptReference()
        {
            var resourceNames = new[]
            {
                "AccountingApp.Designer.Lib.AI.Scripts.md",
                "AccountingApp.Designer.Lib.AI.ScriptGuidelines.md",
                "AccountingApp.Designer.Lib.AI.ScriptExtensions.md",
                "AccountingApp.Designer.Lib.AI._ScriptApi.md",
            };

            var sb = new StringBuilder();
            var asm = typeof(ScriptChat).Assembly;
            foreach (var name in resourceNames)
            {
                try
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    sb.AppendLine(reader.ReadToEnd());
                    sb.AppendLine();
                }
                catch
                {
                    // 読み込めないファイルはスキップ
                }
            }
            return sb.ToString();
        }

        private class AIResponse
        {
            public string Script { get; set; } = string.Empty;
            public string Explanation { get; set; } = string.Empty;
        }

        const string SystemPrompt = @"
あなたはローコードWebアプリケーションのスクリプトエディタです。
ユーザーの指示に基づいてC#ライクなスクリプト（*.mod.cs）を編集し、結果をJSONで返してください。

## 基本ルール
- 元のスクリプトが渡されるので、ユーザーの指示に対して**必要最小限の変更**にしてください。
- 既存のコードは指示がない限り変更・削除しないでください。
- 新しいメソッドは既存コードの末尾に追加してください。
- コメントがある場合はそのまま保持してください。
- 別途渡される「## スクリプト仕様」（言語仕様・Module/Field API・組み込み/拡張サービス・規約）に必ず従ってください。存在しないクラスやメソッドを生成しないこと。

## 出力JSON形式

{
  ""Script"": ""変更後のスクリプト全体"",
  ""Explanation"": ""変更内容の説明""
}

- Script: 変更後のスクリプト全体を文字列として返す。改行は \n で表現する。
- Explanation: 何を変更したかの簡潔な日本語説明。

## 主要フィールド型の固有API（クイックリファレンス。詳細は「## スクリプト仕様」を参照）

- **TextField**: Value(string?), SearchValue, SearchComparison
- **NumberField**: Value(decimal?), SearchMin, SearchMax
- **BooleanField**: Value(bool?)
- **DateField**: Value(DateOnly?), SearchMin, SearchMax
- **DateTimeField**: Value(DateTime?), SearchMin, SearchMax
- **TimeField**: Value(TimeOnly?), SearchMin, SearchMax
- **SelectField**: Value(string?), DisplayText, SetCandidates(...), ReloadCandidates(), SetAdditionalCondition(searcher)
- **LinkField**: Value(string?), DisplayText, SetAdditionalCondition(searcher)
- **LabelField**: Text(string)
- **ButtonField**: Text(string)
- **ListField/DetailListField**: Rows, SelectedIndex, Reload(), AddRow(), DeleteRow(), SetAdditionalCondition(searcher)
- **SearchField**: ExecuteSearch(), ExecuteClear()
- **FileField**: FileName, GetMemoryStream(), Download(), SetFile(name, content), ClearFile()
- **ModuleField**: ChildModule, SetModule(moduleName, layoutName)
- **ImageViewerField**: Base64Data, SetBase64Data(name, value)
- **ApexChartField/ApexRadialChartField**: AllowLoad(bool), Reload(), SetAdditionalCondition(searcher), AddAnnotation(name, annotation), RemoveAnnotation(name), ClearAnnotation()

## 列挙型
- TransactionMode: Insert, Update, Delete
- MatchComparison: Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual, Like, In, NotIn
- ModuleLayoutType: Detail, List, Search
- PanelAlignment: Left, Right
- MidpointRounding: AwayFromZero, ToEven
";
    }
}
