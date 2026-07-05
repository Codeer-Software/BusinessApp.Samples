using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.DesignLogic.Check;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.Repository.Design;
using OpenAI.Chat;
using System.IO;
using System.Reflection;
using System.Text;

namespace AccountingApp.Designer.Lib.AI
{
    public class PageFrameChat : IAIChat
    {
        readonly IPageFrameEditor _editor;
        readonly ChatClient _chatClient;
        readonly IDesignerChatHost _host;
        readonly List<ChatMessage> _messages = new();

        public string Explanation => "ページフレームを編集するためのチャットです";

        public PageFrameChat(IDesignerChatHost host, AISettings settings, IPageFrameEditor editor)
        {
            _host = host;
            _editor = editor;
            _chatClient = settings.CreateChatClient();
        }

        public void Clear() => _messages.Clear();

        public async Task<string> ProcessMessage(string message)
        {
            var pageFrame = _editor.GetPageFrameDesign();

            // 仕様プロンプト(SystemPrompt + ページフレーム仕様Docs + デザイン情報)は会話の最初の1回だけ履歴に入れる。
            // 会話を重ねても仕様Docsを毎回送り直さないよう、以降は履歴に残った1回を使い回す。
            if (_messages.Count == 0)
            {
                _messages.Add(new SystemChatMessage(SystemPrompt));
                if (!string.IsNullOrEmpty(PageFrameReference))
                    _messages.Add(new SystemChatMessage(
                        "## ページフレーム仕様（構造・プロパティ定義・権限条件(UserReadCondition)の書き方・TypeFullName一覧）\n\n" + PageFrameReference));
                _messages.Add(new SystemChatMessage(BuildDesignContextInfo()));
            }

            // 現在のページフレーム定義と指示は毎ターン追加する(定義は編集で変わるため常に最新を渡す)。
            _messages.Add(new UserChatMessage(
                $"現在のページフレーム定義:\n{JsonConverterEx.SerializeObject(pageFrame)}\n\n指示: {message}"));

            // 生成 → デザインチェック → エラーがあればAIに返して再生成、を上限まで繰り返す自己修正ループ。
            // ページフレームは壊れると画面が開けなくなるため、検証を通らなかった定義は「適用しない」。
            const int maxAttempts = 3;
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

                // 未知プロパティ検証: 定義に存在しないプロパティをAIが書くと、通常のデシリアライズでは黙って捨てられ、
                // 値が反映されないのに成功扱いになる。strict 検証で検出し、デザインチェックと同様に再生成へ回す。
                var unmappedError = AiJsonValidation.GetUnmappedMemberError<AIResponse>(resultText);
                if (unmappedError != null)
                {
                    if (attempt < maxAttempts)
                    {
                        _messages.Add(new UserChatMessage(
                            "生成されたJSONに、定義に存在しないプロパティが含まれています。" +
                            "存在しないプロパティに書いた値は無視され、設定に反映されません。" +
                            "次のエラーを解消し、正しいプロパティ名・構造で定義全体を再度出力してください。\n" + unmappedError));
                        continue;
                    }
                    return $"生成されたJSONに定義外のプロパティが含まれており解消できなかったため、変更は適用していません(現在の定義は保持されます)。\r\n{unmappedError}";
                }

                var errors = ValidatePageFrame(response.PageFrame);
                if (errors.Count == 0)
                {
                    ApplyResponse(pageFrame, response);
                    return string.IsNullOrEmpty(response.Explanation)
                        ? "変更しました"
                        : response.Explanation;
                }

                lastErrors = errors;
                if (attempt < maxAttempts)
                {
                    // デザインチェックエラーをAIに返して修正を促す
                    _messages.Add(new UserChatMessage(
                        "生成されたページフレーム定義に次のデザインチェックエラーがあります。修正して定義全体を再度出力してください。\n"
                        + FormatErrors(errors)));
                }
            }

            // 上限まで試してもエラーが残った: 壊れた定義は適用せず、現在の定義を保持する。
            return $"デザインチェックエラーを解消できなかったため、変更は適用していません(現在の定義は保持されます)。\r\n{FormatErrors(lastErrors)}";
        }

        List<DesignCheckInfo> ValidatePageFrame(PageFrameDesign pageFrame)
        {
            try
            {
                return _editor.CheckPageFrame(pageFrame);
            }
            catch
            {
                // 検証自体が失敗した場合はエラーなし扱い(デシリアライズは成功=型として妥当。検証エンジンの想定外でチャットを止めない)
                return new();
            }
        }

        static string FormatErrors(List<DesignCheckInfo> errors)
            => string.Join("\n", errors.Select(e => $"- {e.GetPositionText()}: {e.Message}"));

        string BuildDesignContextInfo()
        {
            var lines = new List<string> { "## 現在のアプリケーション情報" };

            try
            {
                var designData = _host.GetDesignData();

                var moduleNames = designData.Modules.GetModuleNames();
                if (moduleNames.Any())
                {
                    lines.Add("\n### モジュール一覧（PageLinkのModuleに指定可能）");
                    foreach (var name in moduleNames)
                    {
                        var mod = designData.Modules.Find(name);
                        if (mod == null) continue;

                        var details = new List<string>();
                        if (mod.DetailLayouts.Any())
                            details.Add($"DetailLayout: {string.Join(", ", mod.DetailLayouts.Keys.Select(k => string.IsNullOrEmpty(k) ? "(default)" : k))}");
                        if (mod.ListLayouts.Any())
                            details.Add($"ListLayout: {string.Join(", ", mod.ListLayouts.Keys.Select(k => string.IsNullOrEmpty(k) ? "(default)" : k))}");
                        if (mod.SearchLayouts.Any())
                            details.Add($"SearchLayout: {string.Join(", ", mod.SearchLayouts.Keys.Select(k => string.IsNullOrEmpty(k) ? "(default)" : k))}");

                        lines.Add($"- {name}");
                        if (details.Any())
                            lines.Add($"  {string.Join(", ", details)}");
                    }
                }

                var pageFrameNames = designData.PageFrames.GetPageFrameNames();
                if (pageFrameNames.Any())
                {
                    lines.Add("\n### ページフレーム一覧（PageLinkのPageFrameに指定可能）");
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

        void ApplyResponse(PageFrameDesign pageFrame, AIResponse response)
        {
            CopyEditableSettings(response.PageFrame, pageFrame);
            _editor.Update();
        }

        // Name(識別名)と廃止プロパティ以外の編集可能プロパティを「すべて」反映する。
        // 列挙の書き漏れで設定が黙って消えるのを防ぐため、リフレクションで全プロパティをコピーする(将来追加分も自動)。
        internal static void CopyEditableSettings(PageFrameDesign src, PageFrameDesign dst)
        {
            foreach (var p in typeof(PageFrameDesign).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.Name == nameof(PageFrameDesign.Name)) continue;
                if (p.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                p.SetValue(dst, p.GetValue(src));
            }
        }

        // ページフレームの仕様知識(構造・プロパティ定義)は Lib/AI/PageFrame.md を
        // 埋め込みリソースとして読み込む(csproj の EmbeddedResource を参照)。
        static readonly string PageFrameReference = LoadPageFrameReference();

        static string LoadPageFrameReference()
        {
            var sb = new StringBuilder();
            var asm = typeof(PageFrameChat).Assembly;
            foreach (var name in new[] { "AccountingApp.Designer.Lib.AI.PageFrame.md", "AccountingApp.Designer.Lib.AI.SearchConditions.md", "AccountingApp.Designer.Lib.AI.JsonAbstractTypeFullName.md" })
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
                }
            }
            return sb.ToString();
        }

        private class AIResponse
        {
            public PageFrameDesign PageFrame { get; set; } = new();
            public string Explanation { get; set; } = string.Empty;
        }

        const string SystemPrompt = @"
あなたはローコードWebアプリケーションのページフレーム（ナビゲーション構造）のデザイナです。
ユーザーの指示に基づいてページフレーム定義を編集し、結果をJSONで返してください。

## 基本ルール
- 元のページフレーム定義が渡されるので、ユーザーの指示に対して**必要最小限の変更**にしてください。指示がないプロパティは変更しない。
- Name（フレーム識別名）は変更しないでください。
- ページフレームの**全プロパティを編集できます**: アプリルート(IsApplicationRoot)/優先度(Priority)/サイドバー(Left,Right)/ヘッダー(Header)/トップ・追加ページ(TopPageModuleDesign,OtherPageModuleDesigns)/余白・色・フォント/自動ズーム(AutoZoom,BaseWidth)/デバイス出し分け(TargetDevice,WidthFrom)/**アクセス権限(UserReadCondition)**。
- **アクセス権限(UserReadCondition)**: このフレームを誰が開けるかの条件。指示されたら設定する(例「管理者ロールだけ」「特定ユーザーだけ」)。書き方は「## ページフレーム仕様」内の検索条件(ModuleMatchCondition / FieldValueMatchCondition / FieldVariableMatchCondition)に従う。条件は**現在ユーザー(CurrentUser)モジュールのフィールド**に対して評価される(ロールや所属で絞る)。解除する指示なら空(Condition を null)にする。
- モジュール一覧に存在するモジュール名のみをPageLinkのModuleに指定してください。
- 別途渡される「## ページフレーム仕様」の構造・プロパティ定義・権限条件の書き方に必ず従ってください。

## 出力JSON形式

以下の形式でJSONを返してください:
{
  ""PageFrame"": { /* PageFrameDesign - フレーム定義全体 */ },
  ""Explanation"": ""変更内容の説明""
}

- PageFrame: 変更後のページフレーム定義全体
- Explanation: 何を変更したかの簡潔な日本語説明
";
    }
}
