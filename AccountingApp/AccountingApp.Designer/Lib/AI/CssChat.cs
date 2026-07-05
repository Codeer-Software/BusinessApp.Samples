using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.Json;
using OpenAI.Chat;
using System.IO;

namespace AccountingApp.Designer.Lib.AI
{
    public class CssChat : IAIChat
    {
        readonly ICssEditor _editor;
        readonly ChatClient _chatClient;
        readonly IDesignerChatHost _host;
        readonly List<ChatMessage> _messages = new();

        public string Explanation => "cssを編集するためのチャットです";

        public CssChat(IDesignerChatHost host, AISettings settings, ICssEditor editor)
        {
            _host = host;
            _editor = editor;
            _chatClient = settings.CreateChatClient();
        }

        public void Clear() => _messages.Clear();

        public async Task<string> ProcessMessage(string message)
        {
            var currentCss = _editor.GetCss();

            // 仕様プロンプト(SystemPrompt + AppCss.md + デザイン情報)は会話の最初の1回だけ履歴に入れる。
            // 会話を重ねても AppCss.md を毎回送り直さないよう、以降は履歴に残った1回を使い回す。
            if (_messages.Count == 0)
            {
                _messages.Add(new SystemChatMessage(SystemPrompt));
                if (!string.IsNullOrEmpty(CssReference))
                    _messages.Add(new SystemChatMessage(
                        "## アプリケーションのCSS仕様（DOM構造・セレクタ・スタイリングルール）\n\n" + CssReference));
                _messages.Add(new SystemChatMessage(BuildDesignContextInfo()));
            }

            // 現在のCSSと指示は毎ターン追加する(CSSは編集で変わるため常に最新を渡す)。
            _messages.Add(new UserChatMessage(
                $"現在のCSS:\n```css\n{currentCss}\n```\n\n指示: {message}"));

            try
            {
                var result = await _chatClient.CompleteChatAsync(_messages,
                    new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() });
                var resultText = result.Value.Content.FirstOrDefault()?.Text ?? string.Empty;

                var response = JsonConverterEx.DeserializeObject<AIResponse>(resultText)!;
                _editor.Update(response.Css);

                _messages.Add(new AssistantChatMessage(resultText));

                return string.IsNullOrEmpty(response.Explanation)
                    ? "CSSを変更しました"
                    : response.Explanation;
            }
            catch (Exception ex)
            {
                // 失敗したターンのユーザーメッセージは履歴に残さない(次回の再送・文脈汚染を防ぐ)。
                if (_messages.Count > 0 && _messages[^1] is UserChatMessage)
                    _messages.RemoveAt(_messages.Count - 1);
                return $"エラーリトライしてください\r\n{ex.Message}";
            }
        }

        string BuildDesignContextInfo()
        {
            var lines = new List<string> { "## 現在のアプリケーション情報" };

            try
            {
                var designData = _host.GetDesignData();

                var moduleNames = designData.Modules.GetModuleNames();
                if (moduleNames.Any())
                {
                    lines.Add("\n### モジュール一覧（data-module / list-module セレクタで使用可能）");
                    foreach (var name in moduleNames)
                    {
                        var mod = designData.Modules.Find(name);
                        if (mod == null) continue;

                        var fieldNames = mod.Fields.Select(f => f.Name).ToList();
                        var classNames = new List<string>();

                        foreach (var kvp in mod.DetailLayouts)
                        {
                            if (!string.IsNullOrEmpty(kvp.Value.ClassName))
                                classNames.Add($"DetailLayout(\"{kvp.Key}\"): {kvp.Value.ClassName}");

                            CollectClassNames(kvp.Value.Layout, classNames);
                        }

                        lines.Add($"- {name}");
                        if (fieldNames.Any())
                            lines.Add($"  フィールド: {string.Join(", ", fieldNames)}");
                        if (classNames.Any())
                            lines.Add($"  ClassName: {string.Join(", ", classNames)}");
                    }
                }

                var pageFrameNames = designData.PageFrames.GetPageFrameNames();
                if (pageFrameNames.Any())
                {
                    lines.Add("\n### ページフレーム一覧（data-pageframe セレクタで使用可能）");
                    foreach (var name in pageFrameNames)
                    {
                        lines.Add($"- {name}");
                    }
                }
            }
            catch
            {
                lines.Add("（デザインデータの取得に失敗しました）");
            }

            return string.Join("\n", lines);
        }

        static void CollectClassNames(Codeer.LowCode.Blazor.Repository.Design.LayoutDesignBase? layout, List<string> classNames)
        {
            if (layout == null) return;

            if (layout is Codeer.LowCode.Blazor.Repository.Design.FieldLayoutDesign field)
            {
                if (!string.IsNullOrEmpty(field.ClassName))
                    classNames.Add(field.ClassName);
            }
            else if (layout is Codeer.LowCode.Blazor.Repository.Design.GridLayoutDesign grid)
            {
                foreach (var row in grid.Rows)
                    foreach (var col in row.Columns)
                        CollectClassNames(col.Layout, classNames);
            }
            else if (layout is Codeer.LowCode.Blazor.Repository.Design.TabLayoutDesign tab)
            {
                foreach (var tabLayout in tab.Layouts)
                    CollectClassNames(tabLayout, classNames);
            }
        }

        // CSSの仕様知識(DOM構造・セレクタ・スタイリングルール)は Lib/AI/AppCss.md を
        // 埋め込みリソースとして読み込む(csproj の EmbeddedResource を参照)。
        static readonly string CssReference = LoadCssReference();

        static string LoadCssReference()
        {
            try
            {
                var asm = typeof(CssChat).Assembly;
                using var stream = asm.GetManifestResourceStream("AccountingApp.Designer.Lib.AI.AppCss.md");
                if (stream == null) return string.Empty;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        private class AIResponse
        {
            public string Css { get; set; } = string.Empty;
            public string Explanation { get; set; } = string.Empty;
        }

        const string SystemPrompt = @"
あなたはローコードWebアプリケーションのCSSエディタです。
ユーザーの指示に基づいてapp.cssを編集し、結果をJSONで返してください。

## 基本ルール
- 元のCSSが渡されるので、ユーザーの指示に対して**必要最小限の変更**にしてください。
- 既存のCSSルールは指示がない限り変更・削除しないでください。
- 新しいルールは既存CSSの末尾に追加してください。
- コメントがある場合はそのまま保持してください。
- 別途渡される「## アプリケーションのCSS仕様」に記載されたDOM構造・セレクタ・CSS変数・スタイリングルールに必ず従ってください。

## 出力JSON形式

以下の形式でJSONを返してください:
{
  ""Css"": ""/* 変更後のCSS全体 */"",
  ""Explanation"": ""変更内容の説明""
}

- Css: 変更後のCSS全体を文字列として返す。改行は \n で表現する。
- Explanation: 何を変更したかの簡潔な日本語説明。
";
    }
}
