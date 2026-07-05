using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.DesignLogic.Check;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.Repository.Design;
using OpenAI.Chat;
using System.IO;
using System.Text;

namespace AccountingApp.Designer.Lib.AI
{
    public class DetailLayoutChat : IAIChat
    {
        readonly IModuleDetailLayoutEditor _editor;
        readonly ChatClient _chatClient;
        readonly List<ChatMessage> _messages = new();

        // 直近の ApplyResponse で EnsureStandardParts が追加した標準パーツの通知文(結果メッセージ用)。
        string _standardPartsNote = string.Empty;

        public string Explanation =>
            "詳細レイアウトを編集するチャットです。\r\n" +
            "おすすめの並べ方（名前で指定できます）:\r\n" +
            "・全体配置-ラベル左 … 1行に1項目、ラベルを左に（既定）\r\n" +
            "・全体配置-ラベル左-2 … ラベル左のまま1行に2項目ずつ\r\n" +
            "・全体配置-ラベル上 … 1行に1項目、ラベルを入力の上に\r\n" +
            "・全体配置-ラベル上-3 … ラベル上のまま1行に3項目ずつ\r\n" +
            "末尾の数字が1行あたりの項目数（省略すると1）。「カード分け」で枠線カードにまとめる指定も併用できます。\r\n" +
            "これらの名前でも、自由な指示（例: この行を全幅に / 罫線を引いて / Notes を一番下に）でも指定できます。";

        public DetailLayoutChat(AISettings settings, IModuleDetailLayoutEditor editor)
        {
            _editor = editor;
            _chatClient = settings.CreateChatClient();
        }

        public void Clear() => _messages.Clear();

        public async Task<string> ProcessMessage(string message)
        {
            _standardPartsNote = string.Empty;
            var detail = _editor.GetDetailLayoutDesign();
            if (detail.Layout is not GridLayoutDesign)
                return "レイアウトデータが不正です（GridLayoutDesignが必要です）";

            // 仕様プロンプト(SystemPrompt + レイアウト仕様Docs)は会話の最初の1回だけ履歴に入れる。
            if (_messages.Count == 0)
            {
                _messages.Add(new SystemChatMessage(SystemPrompt));
                if (!string.IsNullOrEmpty(LayoutReference))
                    _messages.Add(new SystemChatMessage(
                        "## レイアウト仕様（クラス定義・プロパティ・推奨ルール・IsViewOnly）\n\n" + LayoutReference));
            }

            // フィールド一覧と現在のレイアウトは毎ターン追加する(編集で変わるため常に最新を渡す)。
            var fields = _editor.GetFieldDesigns();
            var fieldInfo = fields.Select(f => $"  {f.Name} ({f.GetType().Name})").ToList();
            var moduleName = _editor.GetModuleName();
            _messages.Add(new UserChatMessage(
                $"モジュール名: {moduleName}\n\n"
                + $"現在のモジュールに定義されているフィールド一覧:\n{string.Join("\n", fieldInfo)}\n\n"
                + $"現在のレイアウト:\n{JsonConverterEx.SerializeObject(detail.Layout)}\n\n指示: {message}"));

            // システムフィールドは原則、詳細レイアウトに配置しない。ただしユーザーが明示的に表示を求めた分は許可する。
            // 「明示要求」= 指示文にそのフィールドの Name か DisplayName が含まれること(例: 「作成日時も表示して」「CreatedAt を出して」)。
            var explicitlyRequestedSystemFields = fields
                .Where(f => SystemLayoutFieldNames.Contains(f.Name))
                .Where(f => message.Contains(f.Name, StringComparison.OrdinalIgnoreCase)
                    || (f is IDisplayName d && !string.IsNullOrEmpty(d.DisplayName) && message.Contains(d.DisplayName)))
                .Select(f => f.Name)
                .ToHashSet();

            // ラベル上(縦置き)を明示要求しているか。要求していなければ既定のラベル左に是正する。
            var labelAboveRequested = message.Contains("ラベル上") || message.Contains("縦")
                || (message.Contains("ラベル") && message.Contains("上"));

            // 標準パーツ(タイトル/戻る/サブミット)を追加するか。
            // 条件: 明示要求 OR 何もない(入力未配置)状態からの全体配置依頼。それ以外では足さない。
            var explicitBack = message.Contains("戻る");
            var explicitTitle = message.Contains("タイトル");
            var explicitSubmit = message.Contains("サブミット")
                || message.Contains("submit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("登録ボタン") || message.Contains("保存ボタン");
            var arrangeWords = new[] { "並べ", "配置", "レイアウト", "いい感じ", "見やす", "整え", "フォーム" };
            var isWholeArrange = arrangeWords.Any(message.Contains);
            var fromScratch = !HasAnyInputPlaced(detail.Layout) && isWholeArrange;
            var wantBack = explicitBack || fromScratch;
            var wantTitle = explicitTitle || fromScratch;
            var wantSubmit = explicitSubmit || fromScratch;

            // 生成 → 適用 → デザインチェック → エラーがあればAIに返して再生成、を上限まで繰り返す自己修正ループ。
            // レイアウトは壊れても表示が崩れる程度なので「適用しつつ」検証し、直らなければ警告する。
            const int maxAttempts = 3;
            var lastErrors = new List<DesignCheckInfo>();
            AIResponse? lastResponse = null;

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
                            "次のエラーを解消し、正しいプロパティ名・構造で全体を再度出力してください。\n" + unmappedError));
                        continue;
                    }
                    return $"生成されたJSONに定義外のプロパティが含まれており解消できなかったため、変更は適用していません。\r\n{unmappedError}";
                }

                // レイアウト変更を伴わない応答(できない依頼の断り等)。Layout が空(行なし)なら適用せず説明だけ返す。
                // 既存レイアウトを保持し(空を適用するとフォームが消える)、断りを1回で完結させる(リトライせず速い)。
                if (response.Layout == null || response.Layout.Rows.Count == 0)
                {
                    // タイトル/戻る/サブミットの追加要求は、AIがレイアウトを返さなくても(空=現状維持)コードが
                    // 現在のレイアウトに標準パーツを足す。これらを「できない」と断らせず確実に追加するため。
                    if ((wantBack || wantTitle || wantSubmit) && detail.Layout is GridLayoutDesign currentRoot)
                    {
                        _standardPartsNote = EnsureStandardParts(currentRoot, _editor.GetFieldDesigns(),
                            wantBack, wantTitle, wantSubmit, _editor.GetModuleName());
                        _editor.Update();
                        // 新規作成があればその通知、無ければ(既存を再配置しただけでも)配置した旨を返す。
                        return !string.IsNullOrEmpty(_standardPartsNote)
                            ? _standardPartsNote
                            : "指定のパーツ(タイトル/戻る/サブミット)を配置しました。";
                    }
                    return string.IsNullOrWhiteSpace(response.Explanation)
                        ? "レイアウトは変更していません。"
                        : response.Explanation;
                }

                // 列(GridColumn)の無い行(GridRow)は何も描画されず不具合になる。デザインチェックでは検出されないため、
                // ここで検出して適用せず再生成ループに回す(ネストした Grid / Tab 内も再帰的に確認)。
                if (HasEmptyRow(response.Layout))
                {
                    if (attempt < maxAttempts)
                    {
                        _messages.Add(new UserChatMessage(
                            "生成されたレイアウトに、列(GridColumn)が1つも無い行(GridRow)が含まれています。" +
                            "列の無い行は画面に何も表示されず不具合になります。" +
                            "すべての行に最低1つの列(GridColumn)を入れ、フィールドを置く行はその列の Layout に FieldLayoutDesign を設定して、" +
                            "レイアウト全体を再度出力してください。"));
                        continue;
                    }
                    return "列の無い行(GridRow)が生成され解消できなかったため、変更は適用していません。もう一度指示してください。";
                }

                // 存在しないフィールドを参照する FieldLayoutDesign(宙ぶらりん参照)は不具合になる(セルが壊れた参照で占有され、
                // 何も表示されず、上にドロップもできない)。このチャットはラベル以外のフィールドを追加できないため、
                // 未追加フィールドを参照させず、適用せず再生成ループに回す。NewLabels で追加するラベル名は既知として扱う。
                // 標準パーツ(タイトル/戻る/サブミット)は EnsureStandardParts がコードで作成・配置するため、ここでは既知として扱う
                // (AIが参照しても宙ぶらりんエラーにしない。AI自身はこれらを作らない)。
                var knownFieldNames = new HashSet<string>(_editor.GetFieldDesigns().Select(f => f.Name));
                foreach (var label in response.NewLabels ?? new())
                    if (!string.IsNullOrEmpty(label.Name)) knownFieldNames.Add(label.Name);

                var unknownRefs = FindUnknownFieldRefs(response.Layout, knownFieldNames);
                if (unknownRefs.Count > 0)
                {
                    if (attempt < maxAttempts)
                    {
                        _messages.Add(new UserChatMessage(
                            $"レイアウトに、存在しないフィールドを参照する FieldLayoutDesign が含まれています: {string.Join(", ", unknownRefs)}。" +
                            "このチャットはラベル以外のフィールドを追加できません。存在しないフィールド名を FieldLayoutDesign.FieldName に指定しないでください。" +
                            "そのフィールドを後で置く場所は、GridColumn の Layout を省略した空のセルにして、レイアウト全体を再度出力してください。"));
                        continue;
                    }
                    return $"存在しないフィールド({string.Join(", ", unknownRefs)})を参照するレイアウトが生成されたため、変更は適用していません。" +
                        "そのフィールドは『全体設定』で追加するか、デザイナで手動追加してから配置してください。";
                }

                // 同じフィールドを複数の場所に配置するのは不具合(1フィールドは1箇所のみ)。
                // ラベル上スタイルで入力フィールドを「ラベル行」と「入力行」に二重に置く誤りなどを検出して再生成へ回す。
                var duplicateRefs = FindDuplicateFieldRefs(response.Layout);
                if (duplicateRefs.Count > 0)
                {
                    if (attempt < maxAttempts)
                    {
                        _messages.Add(new UserChatMessage(
                            $"同じフィールドが複数の場所に配置されています: {string.Join(", ", duplicateRefs)}。" +
                            "1つのフィールドはレイアウト内で1箇所にしか置けません。" +
                            "ラベルを上に置く場合、ラベル行に入力フィールドを置くのではなく、その入力用の LabelFieldDesign(Text は \"\"、RelativeField に入力フィールド名を設定)を NewLabels に用意し、" +
                            "ラベル行ではそのラベルを FieldName で参照してください。入力フィールドは入力行に1回だけ置きます。重複を解消してレイアウト全体を再度出力してください。"));
                        continue;
                    }
                    return $"同じフィールド({string.Join(", ", duplicateRefs)})が複数箇所に配置されたため、変更は適用していません。もう一度指示してください。";
                }

                // システムフィールド(Id/CreatedAt/... )は、明示要求が無い限り詳細レイアウトに置かない。
                // AIが指示を無視して置くことがあるため、検出したら再生成へ回し、最終的には決定的に取り除いて適用する。
                var unwantedSystemFields = FindPlacedFieldRefs(response.Layout, SystemLayoutFieldNames)
                    .Where(n => !explicitlyRequestedSystemFields.Contains(n)).ToList();
                if (unwantedSystemFields.Count > 0)
                {
                    if (attempt < maxAttempts)
                    {
                        _messages.Add(new UserChatMessage(
                            $"システムフィールドが詳細レイアウトに配置されています: {string.Join(", ", unwantedSystemFields)}。" +
                            "Id / CreatedAt / UpdatedAt / Creator / Updater / LogicalDelete / OptimisticLocking / DeletedAt / Deleter は、" +
                            "ユーザーが明示的に『表示して』と求めていない限り詳細レイアウトに置きません。これらの FieldLayoutDesign をレイアウトから取り除き(該当セルは Layout を省略した空セルにするか、行ごと削除する)、レイアウト全体を再度出力してください。"));
                        continue;
                    }
                    // 最終手段: AIが直さないので、該当する FieldLayoutDesign を決定的に空セル化して適用し、除外したことを伝える。
                    StripFieldRefs(response.Layout, unwantedSystemFields);
                    response.Explanation = (string.IsNullOrWhiteSpace(response.Explanation) ? "" : response.Explanation + "\r\n")
                        + $"（システムフィールド {string.Join(", ", unwantedSystemFields)} は詳細レイアウトに配置しない決まりのため除外しました。表示が必要なら『{unwantedSystemFields[0]} も表示して』のように指示してください。）";
                }

                ApplyResponse(detail, response, labelAboveRequested, wantBack, wantTitle, wantSubmit, fromScratch);
                lastResponse = response;

                var errors = ValidateLayout(detail);
                if (errors.Count == 0)
                    return BuildResultMessage(response);

                lastErrors = errors;
                if (attempt < maxAttempts)
                    _messages.Add(new UserChatMessage(
                        "生成されたレイアウトに次のデザインチェックエラーがあります。修正してレイアウト全体を再度出力してください。\n"
                        + FormatErrors(errors)));
            }

            return BuildResultMessage(lastResponse!)
                + $"\r\n（注意: デザインチェックエラーが残っています。内容を確認してください）\r\n{FormatErrors(lastErrors)}";
        }

        // 列(GridColumn)が1つも無い行(GridRow)があるか。ネストした GridLayoutDesign / TabLayoutDesign も再帰確認する。
        static bool HasEmptyRow(LayoutDesignBase? layout)
        {
            switch (layout)
            {
                case GridLayoutDesign grid:
                    foreach (var row in grid.Rows)
                    {
                        if (row.Columns.Count == 0) return true;
                        foreach (var col in row.Columns)
                            if (HasEmptyRow(col.Layout)) return true;
                    }
                    return false;
                case TabLayoutDesign tab:
                    return tab.Layouts.Any(HasEmptyRow);
                default:
                    return false;
            }
        }

        // FieldLayoutDesign が参照する FieldName が、実在フィールドにも新規ラベルにも無い(宙ぶらりん参照)を集める。
        // このチャットはラベル以外のフィールドを追加できないため、AIが存在しないフィールド(例: 未追加の SubmitButton)を
        // 参照する FieldLayoutDesign を作ってしまうのを防ぐ。ネストした Grid / Tab / Canvas も再帰確認する。
        static List<string> FindUnknownFieldRefs(LayoutDesignBase? layout, HashSet<string> knownFieldNames)
        {
            var unknown = new List<string>();

            void Walk(LayoutDesignBase? node)
            {
                switch (node)
                {
                    case FieldLayoutDesign field:
                        if (!string.IsNullOrEmpty(field.FieldName) && !knownFieldNames.Contains(field.FieldName))
                            unknown.Add(field.FieldName);
                        break;
                    case GridLayoutDesign grid:
                        foreach (var row in grid.Rows)
                            foreach (var col in row.Columns)
                                Walk(col.Layout);
                        break;
                    case TabLayoutDesign tab:
                        foreach (var t in tab.Layouts) Walk(t);
                        break;
                    case CanvasLayoutDesign canvas:
                        foreach (var e in canvas.Elements) Walk(e.Layout);
                        break;
                }
            }

            Walk(layout);
            return unknown.Distinct().ToList();
        }

        // 同じ FieldName を持つ FieldLayoutDesign が2回以上現れる(同一フィールドの多重配置)を集める。
        // ネストした Grid / Tab / Canvas も再帰確認する。
        static List<string> FindDuplicateFieldRefs(LayoutDesignBase? layout)
        {
            var counts = new Dictionary<string, int>();

            void Walk(LayoutDesignBase? node)
            {
                switch (node)
                {
                    case FieldLayoutDesign field:
                        if (!string.IsNullOrEmpty(field.FieldName))
                            counts[field.FieldName] = counts.GetValueOrDefault(field.FieldName) + 1;
                        break;
                    case GridLayoutDesign grid:
                        foreach (var row in grid.Rows)
                            foreach (var col in row.Columns)
                                Walk(col.Layout);
                        break;
                    case TabLayoutDesign tab:
                        foreach (var t in tab.Layouts) Walk(t);
                        break;
                    case CanvasLayoutDesign canvas:
                        foreach (var e in canvas.Elements) Walk(e.Layout);
                        break;
                }
            }

            Walk(layout);
            return counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        }

        // 詳細レイアウトに原則置かないシステムフィールド名(CurrentUser はフィールドではないので除外)。
        // コアの public 定数を参照して綴りを源泉と一致させる(SystemFieldNames.All は internal のため自前で列挙)。
        static readonly HashSet<string> SystemLayoutFieldNames = new(StringComparer.Ordinal)
        {
            SystemFieldNames.Id, SystemFieldNames.LogicalDelete, SystemFieldNames.CreatedAt,
            SystemFieldNames.UpdatedAt, SystemFieldNames.DeletedAt, SystemFieldNames.Creator,
            SystemFieldNames.Updater, SystemFieldNames.Deleter, SystemFieldNames.OptimisticLocking,
        };

        // 指定した名前集合のいずれかを FieldName に持つ FieldLayoutDesign がレイアウトに置かれているものを集める。
        static List<string> FindPlacedFieldRefs(LayoutDesignBase? layout, HashSet<string> targetNames)
        {
            var found = new List<string>();

            void Walk(LayoutDesignBase? node)
            {
                switch (node)
                {
                    case FieldLayoutDesign field:
                        if (!string.IsNullOrEmpty(field.FieldName) && targetNames.Contains(field.FieldName))
                            found.Add(field.FieldName);
                        break;
                    case GridLayoutDesign grid:
                        foreach (var row in grid.Rows)
                            foreach (var col in row.Columns)
                                Walk(col.Layout);
                        break;
                    case TabLayoutDesign tab:
                        foreach (var t in tab.Layouts) Walk(t);
                        break;
                    case CanvasLayoutDesign canvas:
                        foreach (var e in canvas.Elements) Walk(e.Layout);
                        break;
                }
            }

            Walk(layout);
            return found.Distinct().ToList();
        }

        // 指定した名前を FieldName に持つ FieldLayoutDesign を置いているセル(GridColumn.Layout / CanvasElement.Layout)を空にする。
        // セルや行の構造は壊さず(空セルは有効)、参照だけ取り除く。
        static void StripFieldRefs(LayoutDesignBase? layout, IEnumerable<string> namesToRemove)
        {
            var names = new HashSet<string>(namesToRemove, StringComparer.Ordinal);

            bool IsTarget(LayoutDesignBase? node)
                => node is FieldLayoutDesign f && !string.IsNullOrEmpty(f.FieldName) && names.Contains(f.FieldName);

            void Walk(LayoutDesignBase? node)
            {
                switch (node)
                {
                    case GridLayoutDesign grid:
                        foreach (var row in grid.Rows)
                            foreach (var col in row.Columns)
                            {
                                if (IsTarget(col.Layout)) col.Layout = null;
                                else Walk(col.Layout);
                            }
                        break;
                    case TabLayoutDesign tab:
                        foreach (var t in tab.Layouts) Walk(t);
                        break;
                    case CanvasLayoutDesign canvas:
                        foreach (var e in canvas.Elements)
                        {
                            if (IsTarget(e.Layout)) e.Layout = null;
                            else Walk(e.Layout);
                        }
                        break;
                }
            }

            Walk(layout);
        }

        List<DesignCheckInfo> ValidateLayout(DetailLayoutDesign detail)
        {
            try
            {
                return _editor.CheckLayout(detail);
            }
            catch
            {
                return new();
            }
        }

        static string FormatErrors(List<DesignCheckInfo> errors)
            => string.Join("\n", errors.Select(e => $"- {e.GetPositionText()}: {e.Message}"));

        void ApplyResponse(DetailLayoutDesign detail, AIResponse response, bool labelAboveRequested,
            bool wantBack, bool wantTitle, bool wantSubmit, bool fromScratch)
        {
            var fields = _editor.GetFieldDesigns();
            var existingNames = new HashSet<string>(fields.Select(f => f.Name));

            if (response.NewLabels?.Count > 0)
            {
                var addedLabels = new List<LabelFieldDesign>();
                foreach (var label in response.NewLabels)
                {
                    if (!string.IsNullOrEmpty(label.Name) && !existingNames.Contains(label.Name))
                    {
                        fields.Add(label);
                        existingNames.Add(label.Name);
                        addedLabels.Add(label);
                    }
                }
                response.NewLabels = addedLabels;
            }

            // 既定はラベル左。ラベル上を明示要求していないのに AI がラベル上(縦置き)を作ったら、ラベル左へ是正する。
            if (!labelAboveRequested)
                ConvertLabelAboveToLabelLeft(response.Layout, _editor.GetFieldDesigns());

            // ラベル左のラベル列幅をコードで確定的に推定設定する(AIが幅を付け忘れる/狭すぎるのを補正)。ラベル上の幅は外す。
            NormalizeLabelLeftWidths(response.Layout, _editor.GetFieldDesigns());

            // 一から全体配置するときは、AIが作りがちな「全セルが空のゴミ行」を除去する(個別編集では消さない)。
            if (fromScratch)
                RemoveEmptyRows(response.Layout);

            // 標準パーツ(タイトル/戻る=上、サブミット=下)は「コードが」作成・配置する。AI には作らせない(型の取り違え=AnchorTag を Label にする等を防ぐ)。
            _standardPartsNote = EnsureStandardParts(response.Layout, _editor.GetFieldDesigns(), wantBack, wantTitle, wantSubmit, _editor.GetModuleName());

            detail.Layout = response.Layout;
            _editor.Update();
        }

        // レイアウトに入力フィールド(DbValueFieldDesignBase)が1つでも配置されているか。フィールド型は名前では判断できないため、
        // ここでは「FieldName が付いた FieldLayoutDesign が1つでもあるか」で“何か配置済みか”を判定する(空/空セルだけなら false)。
        static bool HasAnyInputPlaced(LayoutDesignBase? layout)
        {
            var found = false;
            void Walk(LayoutDesignBase? node)
            {
                if (found) return;
                switch (node)
                {
                    case FieldLayoutDesign f:
                        if (!string.IsNullOrEmpty(f.FieldName)) found = true;
                        break;
                    case GridLayoutDesign grid:
                        foreach (var row in grid.Rows)
                            foreach (var col in row.Columns) Walk(col.Layout);
                        break;
                    case TabLayoutDesign tab:
                        foreach (var t in tab.Layouts) Walk(t);
                        break;
                    case CanvasLayoutDesign canvas:
                        foreach (var e in canvas.Elements) Walk(e.Layout);
                        break;
                }
            }
            Walk(layout);
            return found;
        }

        // 詳細画面の標準パーツ(タイトル/戻るボタン=一番上の行、サブミット=一番下の行)を確定的に用意・配置する。
        // wantX のものだけ、フィールドに無ければ作成し、レイアウト内の既存配置を取り除いて先頭/末尾の行に置き直す。
        internal static string EnsureStandardParts(GridLayoutDesign root, List<FieldDesignBase> fields,
            bool wantBack, bool wantTitle, bool wantSubmit, string moduleName)
        {
            if (!wantBack && !wantTitle && !wantSubmit) return string.Empty;

            string Unique(string baseName)
            {
                var names = new HashSet<string>(fields.Select(f => f.Name));
                if (!names.Contains(baseName)) return baseName;
                var i = 2;
                while (names.Contains(baseName + i)) i++;
                return baseName + i;
            }

            var created = new List<string>();
            // 既存の戻るボタンは「HistoryBack の AnchorTag」または「Name が BackButton の AnchorTag」で拾う
            // (Target を Url のまま放置された/文字が残った既存ボタンも拾って icon-only・HistoryBack に正規化するため)。
            var back = fields.OfType<AnchorTagFieldDesign>()
                .FirstOrDefault(a => a.Target == AnchorTarget.HistoryBack || a.Name == "BackButton");
            var title = fields.OfType<LabelFieldDesign>().FirstOrDefault(l => l.Style == LabelStyle.H4);
            var submit = fields.OfType<SubmitButtonFieldDesign>().FirstOrDefault();

            if (wantBack && back == null)
            {
                back = new AnchorTagFieldDesign { Name = Unique("BackButton"), Target = AnchorTarget.HistoryBack };
                fields.Add(back);
                created.Add("戻るボタン");
            }
            // 戻るボタンは CCFD と同じ慣習に揃える: アイコンのみ(TitleText 空)・Style=Text・塗りつぶしアイコン(未設定時)。
            // 大きさは配置側 FieldLayoutDesign.FontSize=30 で出す。既存ボタンも再実行で慣習に収束させる。
            if (wantBack && back != null)
            {
                back.Target = AnchorTarget.HistoryBack;
                back.Style = AnchorStyle.Text;
                back.TitleText = "";
                if (string.IsNullOrEmpty(back.Icon) || back.Icon == "bi bi-arrow-left-circle") back.Icon = "bi bi-arrow-left-circle-fill";
            }
            if (wantTitle && title == null)
            {
                title = new LabelFieldDesign { Name = Unique("PageTitle"), Text = string.IsNullOrEmpty(moduleName) ? "詳細" : moduleName, Style = LabelStyle.H4 };
                fields.Add(title);
                created.Add("タイトル");
            }
            if (wantSubmit && submit == null)
            {
                submit = new SubmitButtonFieldDesign { Name = Unique("SubmitButton"), Text = "登録" };
                fields.Add(submit);
                created.Add("サブミットボタン");
            }

            // 対象パーツの既存配置を一旦すべて取り除いてから、正しい位置に置き直す(重複・誤配置を防ぐ)。
            var managed = new List<string>();
            if (wantBack && back != null) managed.Add(back.Name);
            if (wantTitle && title != null) managed.Add(title.Name);
            if (wantSubmit && submit != null) managed.Add(submit.Name);
            RemovePlacements(root, managed);

            // 先頭行: 戻る(Width:60・アイコンを FontSize:30 で大きく) | タイトル(中央) | 空セル(Width:60)
            if ((wantBack && back != null) || (wantTitle && title != null))
            {
                var header = new GridRow();
                if (wantBack && back != null)
                    header.Columns.Add(new GridColumn { Layout = new FieldLayoutDesign { FieldName = back.Name, FontSize = 30 }, Width = 60 });
                if (wantTitle && title != null)
                    header.Columns.Add(new GridColumn
                    {
                        Layout = new FieldLayoutDesign { FieldName = title.Name },
                        HorizontalAlignment = (wantBack && back != null) ? HorizontalAlignment.Center : (HorizontalAlignment?)null
                    });
                if (wantBack && back != null && wantTitle && title != null)
                    header.Columns.Add(new GridColumn { Width = 60 });
                root.Rows.Insert(0, header);
            }

            // 末尾行: サブミット(1カラムのみなので左右中央に置く)
            if (wantSubmit && submit != null)
            {
                var footer = new GridRow();
                footer.Columns.Add(new GridColumn { Layout = new FieldLayoutDesign { FieldName = submit.Name }, HorizontalAlignment = HorizontalAlignment.Center });
                root.Rows.Add(footer);
            }

            return created.Count > 0 ? $"{string.Join("・", created)}を追加しました。" : string.Empty;
        }

        // 中身(配置されたフィールド)が1つも無い行を取り除く(全セルが空のゴミ行)。ネストGrid/Tab も再帰。
        // 一から全体配置するとき専用(個別編集では『後で置く空セル行』を消さないよう呼ばない)。
        static void RemoveEmptyRows(GridLayoutDesign root)
        {
            void Walk(GridLayoutDesign g)
            {
                foreach (var row in g.Rows)
                    foreach (var col in row.Columns)
                    {
                        if (col.Layout is GridLayoutDesign ng) Walk(ng);
                        else if (col.Layout is TabLayoutDesign tab)
                            foreach (var t in tab.Layouts)
                                if (t is GridLayoutDesign tg) Walk(tg);
                    }
                g.Rows.RemoveAll(r => !r.Columns.Any(c => HasAnyInputPlaced(c.Layout)));
            }
            Walk(root);
        }

        // 指定名の FieldLayoutDesign を置いている列を取り除き、空になった行も取り除く(ネストGrid/Tab も再帰)。
        static void RemovePlacements(GridLayoutDesign root, List<string> names)
        {
            if (names.Count == 0) return;
            var set = new HashSet<string>(names, StringComparer.Ordinal);

            void Walk(GridLayoutDesign g)
            {
                foreach (var row in g.Rows)
                {
                    row.Columns.RemoveAll(c => c.Layout is FieldLayoutDesign f && set.Contains(f.FieldName));
                    foreach (var col in row.Columns)
                    {
                        if (col.Layout is GridLayoutDesign ng) Walk(ng);
                        else if (col.Layout is TabLayoutDesign tab)
                            foreach (var t in tab.Layouts)
                                if (t is GridLayoutDesign tg) Walk(tg);
                    }
                }
                // 管理対象を取り除いた結果、中身(配置フィールド)が無くなった行は丸ごと除去する
                // (例: AIが作ったヘッダ行から戻る/タイトルを抜いた後に残る空セルだけの行)。
                g.Rows.RemoveAll(r => !r.Columns.Any(c => HasAnyInputPlaced(c.Layout)));
            }
            Walk(root);
        }

        // ラベル上(ネストGrid: ラベル行→入力行)のブロックを、ラベル左(同じ行に[ラベル, 入力])へ変換する。
        // 既定ではラベル左がファーストチョイスなので、ユーザーがラベル上を明示要求していないのに AI がラベル上を作ったら是正する。
        // 厳密に「2行・各行1列・上が対応ラベル・下が入力」のネストGridだけを平坦化し、カード/タブ等は触らない。
        internal static void ConvertLabelAboveToLabelLeft(LayoutDesignBase? layout, List<FieldDesignBase> fields)
        {
            var byName = new Dictionary<string, FieldDesignBase>();
            foreach (var f in fields) byName[f.Name] = f;

            void TryFlatten(GridLayoutDesign g)
            {
                if (g.Rows.Count != 2) return;
                var r0 = g.Rows[0];
                var r1 = g.Rows[1];
                if (r0.Columns.Count != 1 || r1.Columns.Count != 1) return;
                if (r0.Columns[0].Layout is not FieldLayoutDesign lf0 || r1.Columns[0].Layout is not FieldLayoutDesign lf1) return;
                if (!byName.TryGetValue(lf0.FieldName, out var d0) || d0 is not LabelFieldDesign lbl) return;
                if (!byName.TryGetValue(lf1.FieldName, out var d1) || d1 is not DbValueFieldDesignBase) return;
                // ラベルが入力に対応している(RelativeField 一致 or 「<入力>Label」命名)。
                if (lbl.RelativeField != lf1.FieldName && lf0.FieldName != lf1.FieldName + "Label") return;

                var labelCol = r0.Columns[0];
                labelCol.Width = null; // 後段の NormalizeLabelLeftWidths が推定幅を設定する
                var inputCol = r1.Columns[0];
                var newRow = new GridRow();
                newRow.Columns.Add(labelCol);
                newRow.Columns.Add(inputCol);
                g.Rows.Clear();
                g.Rows.Add(newRow);
            }

            void Walk(LayoutDesignBase? node)
            {
                switch (node)
                {
                    case GridLayoutDesign grid:
                        TryFlatten(grid);
                        foreach (var row in grid.Rows)
                            foreach (var col in row.Columns) Walk(col.Layout);
                        break;
                    case TabLayoutDesign tab:
                        foreach (var t in tab.Layouts) Walk(t);
                        break;
                    case CanvasLayoutDesign canvas:
                        foreach (var e in canvas.Elements) Walk(e.Layout);
                        break;
                }
            }
            Walk(layout);
        }

        // ラベル左パターンのラベル列(入力と同じ行に並ぶ見出しラベルの列)の幅を、ラベル文字数から推定して統一する。
        // ラベル上(ラベルが入力と別の行)の列は対象外(幅を付けない)。構造は変えず Width だけ補正するので安全。
        // ユニットテストから直接検証するため internal。
        internal static void NormalizeLabelLeftWidths(LayoutDesignBase? layout, List<FieldDesignBase> fields)
        {
            var byName = new Dictionary<string, FieldDesignBase>();
            foreach (var f in fields) byName[f.Name] = f;

            bool IsLabelCol(GridColumn c) => c.Layout is FieldLayoutDesign f && !string.IsNullOrEmpty(f.FieldName)
                && byName.TryGetValue(f.FieldName, out var d) && d is LabelFieldDesign;
            bool IsInputCol(GridColumn c) => c.Layout is FieldLayoutDesign f && !string.IsNullOrEmpty(f.FieldName)
                && byName.TryGetValue(f.FieldName, out var d) && d is DbValueFieldDesignBase;

            // ラベル列を「ラベル左(入力と横並び)」と「それ以外(ラベル上/見出し)」に分類する。
            var labelLeftCols = new List<GridColumn>();
            var otherLabelCols = new List<GridColumn>();
            void Walk(LayoutDesignBase? node)
            {
                switch (node)
                {
                    case GridLayoutDesign grid:
                        foreach (var row in grid.Rows)
                        {
                            var hasInput = row.Columns.Any(IsInputCol);
                            foreach (var c in row.Columns.Where(IsLabelCol))
                                (hasInput ? labelLeftCols : otherLabelCols).Add(c);
                            foreach (var c in row.Columns) Walk(c.Layout);
                        }
                        break;
                    case TabLayoutDesign tab:
                        foreach (var t in tab.Layouts) Walk(t);
                        break;
                    case CanvasLayoutDesign canvas:
                        foreach (var e in canvas.Elements) Walk(e.Layout);
                        break;
                }
            }
            Walk(layout);

            // ラベル左でないラベル列(ラベル上のラベル行・見出し)に Width が付いていたら外す。
            // ラベル上では幅指定は不要で、AIが付けてしまうと縦置きなのに列幅が固定される不具合になる。
            foreach (var c in otherLabelCols) c.Width = null;

            if (labelLeftCols.Count == 0) return;

            // ラベルの表示文字を求める(Text 優先、無ければ RelativeField の対象フィールドの表示名)。
            string LabelText(GridColumn c)
            {
                var fieldName = ((FieldLayoutDesign)c.Layout!).FieldName;
                if (!byName.TryGetValue(fieldName, out var d) || d is not LabelFieldDesign lbl) return string.Empty;
                if (!string.IsNullOrEmpty(lbl.Text)) return lbl.Text;
                if (!string.IsNullOrEmpty(lbl.RelativeField)
                    && byName.TryGetValue(lbl.RelativeField, out var input) && input is IDisplayName dn
                    && !string.IsNullOrEmpty(dn.DisplayName))
                    return dn.DisplayName;
                return lbl.RelativeField ?? string.Empty;
            }

            var maxChars = labelLeftCols.Max(c => LabelText(c).Length);
            // 全角約18px + 余白40px、下限96px・上限240px。
            var estimate = Math.Clamp(maxChars * 18.0 + 40.0, 96.0, 240.0);
            // AI/ユーザーが付けた妥当(>=96)な幅があればそれも尊重し、大きい方に揃える。
            var existingMax = labelLeftCols.Where(c => c.Width is >= 96).Select(c => c.Width!.Value).DefaultIfEmpty(0).Max();
            var target = Math.Max(estimate, existingMax);
            foreach (var c in labelLeftCols) c.Width = target;
        }

        string BuildResultMessage(AIResponse response)
        {
            var messages = new List<string>
            {
                string.IsNullOrWhiteSpace(response.Explanation) ? "変更しました" : response.Explanation
            };
            if (response.NewLabels?.Count > 0)
            {
                var names = response.NewLabels.Select(l => l.Name);
                messages.Add($"ラベルを追加しました: {string.Join(", ", names)}");
            }
            if (!string.IsNullOrEmpty(_standardPartsNote)) messages.Add(_standardPartsNote);
            return string.Join("\r\n", messages);
        }

        // レイアウト仕様(クラス定義・プロパティ・推奨ルール・IsViewOnly)は Lib/AI の Layouts.md / LayoutGuidelines.md を
        // 埋め込みリソースとして読み込んで連結する。各レイアウトChatで共有(SystemPromptにハードコードしない)。
        static readonly string LayoutReference = LoadLayoutReference();

        static string LoadLayoutReference()
        {
            var sb = new StringBuilder();
            var asm = typeof(DetailLayoutChat).Assembly;
            foreach (var name in new[] { "AccountingApp.Designer.Lib.AI.Layouts.md", "AccountingApp.Designer.Lib.AI.LayoutGuidelines.md", "AccountingApp.Designer.Lib.AI.JsonAbstractTypeFullName.md" })
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
            public GridLayoutDesign Layout { get; set; } = new();
            public List<LabelFieldDesign> NewLabels { get; set; } = new();

            // ユーザーへの説明。できたこと・できなかったこと(と理由・代替)を書く。できないことを成功扱いにしないため。
            public string Explanation { get; set; } = string.Empty;
        }

        // レイアウトのクラス定義・プロパティ・配置パターン・推奨ルール・IsViewOnly・Tab整合性・フィールド移動などは
        // 別途渡す「## レイアウト仕様」(Layouts.md / LayoutGuidelines.md)を参照。
        // ここには Detail レイアウトChat 固有の出力プロトコル(AIResponse の Layout / NewLabels 分離)のみを書く。
        const string SystemPrompt = @"
あなたはローコードでのDetail画面レイアウトのデザイナです。
ユーザーの指示に基づいてレイアウトを編集し、結果をJSONで返してください。
レイアウトのクラス定義・プロパティ・配置パターン・推奨ルール・IsViewOnly・Tab整合性・フィールド移動(重複禁止)などは、
別途渡される「## レイアウト仕様」に必ず従ってください。

## 基本ルール
- 元のレイアウトが渡されるので、ユーザーの指示に対して**必要最小限の変更**にしてください。
- 既存のプロパティ値は指示がない限り変更しないでください。
- **ただし「いい感じに/見やすく並べて」のような“全体を整える”依頼のときは、最小変更にこだわらず、既存レイアウト(空セルだけの初期状態や雑な並びを含む)を破棄して『おすすめ詳細レイアウトの作り方』レシピに沿って組み直してください。** ラベルを付け、関連項目をセクション化する。「最小変更」は具体的な個別編集(この行を移動・この列を広げる等)のときの原則です。
- **既定の並べ方は `全体配置-ラベル左`=1行に1項目(ラベル左＋入力右)をファーストチョイスにする**: 並べ方の指定が無く「いい感じに/見やすく並べて」だけのときは、横に複数項目を詰めず、1項目1行のラベル左で組む。
  - **ラベル左とは: ラベルと入力を「同じ行」に左右で置く**(同じ GridRow の中で、左の GridColumn にラベル、右の GridColumn に入力)。**ラベルを入力の「上の行」に置く縦置き(ラベル上)にしてはいけない。**
  - **ラベル上(縦置き・ネストGridでラベル行→入力行)にするのは、ユーザーが明示的に「ラベル上」「縦置き」「ラベルを上に」等と言ったときだけ。** 「いい感じに」「見やすく」だけのとき、また `全体配置-ラベル左` のときは、必ずラベル左(同じ行に左右)にする。迷ったらラベル左。
- **パターン名の指定に対応する**: パターン名は `やること-詳細1-詳細2` の階層命名(ハイフン区切り、左から解釈)。`全体配置-<ラベル左|ラベル上>[-<1行あたりの項目数>]` を指示されたら「## レイアウト仕様」の『詳細レイアウトのパターン』に従って組む。**末尾の数字＝1行あたりの項目数**(省略時は1。`全体配置-ラベル上-3`なら1行に3項目、ラベル上)。「カード分け」(枠線カードでセクション化)や自由形式の指示(この行を全幅に・罫線を引く・Notesを一番下に 等)にも従い、複数指定は組み合わせる。
- **ラベルを維持する(重要)**: レイアウト方式を切り替える依頼(縦↔横、ラベル左↔ラベル上、1項目1行↔複数列 等)は、配置の組み替えであってラベルの削除ではありません。**既にあるラベル(フィールド一覧の `XxxLabel` 等)は、新しい構造の中で必ず `FieldLayoutDesign.FieldName` で再配置**してください(ラベルを落として素並びに戻さない)。既存ラベルの再利用に NewLabels は不要(NewLabels は新規ラベル追加のときだけ)。ユーザーがラベルの削除を明示的に求めた場合だけ外します。
- **BooleanField には見出しラベルを付けない**: `BooleanField`(チェックボックス/スイッチ/トグル)はフィールド自身が表示名を描画するため、別の見出しラベルは重複です。Boolean は1セルに単独で置き、ラベル左の2カラムにもラベル上のネストGridにもしないでください。組み直しのときも Boolean 用のラベルは作らない/置かない。
- **ラベル左のラベル列幅は文字数から推定する**: ラベル左のとき、ラベル列の `Width` は固定の決め打ちにせず、フォーム内で最も長い見出しラベルが折り返さず収まる幅にする。目安は `最長ラベルの文字数 × 18 + 40`(全角約18px)、**下限 96px(50px のような狭すぎは禁止)**、上限の目安 240px。ラベル列の幅はフォーム内で**全行同じ値に揃える**(左端を揃えるため)。
- **【最重要】システムフィールドの扱い**: `Id` / `CreatedAt` / `UpdatedAt` / `Creator` / `Updater` / `LogicalDelete` / `OptimisticLocking` の扱いは次の2点。判断は必ずこの順:
  1. **ユーザーがそのシステムフィールドの表示を明確に求めた場合は、求められたフィールドを必ず配置する(これが最優先)。** 「明確に求めた」= 指示文にそのフィールドの名前か表示名があり「表示して」「配置して」「並べて」「出して」等と言っているとき。例:「CreatedAt も表示して」「作成日時を出して」→ そのフィールドは必ず置く。求めていないことを理由に外さない。
  2. **上記で求められていないシステムフィールドは、詳細レイアウトに入れない。** 既存レイアウトに置かれていても組み直しのときに外す。「全部のフィールドを並べて」「いい感じに並べて」のような漠然とした指示は、システムフィールド表示の明示要求には**含めない**(=これらは置かない)。勝手に「作成日時・更新日時も並べる」をしない。
  - 要するに: **名指しで表示を頼まれたシステムフィールドだけ置き、それ以外のシステムフィールドは置かない。**
- Layout内のGridColumn.Layoutに配置できるのは FieldLayoutDesign / GridLayoutDesign / TabLayoutDesign / CanvasLayoutDesign の4種類のみです。**フィールド定義(SubmitButtonFieldDesign / TextFieldDesign 等の FieldDesignBase)を GridColumn.Layout に直接入れることは絶対に禁止**です(エラーになります)。フィールドは必ず FieldLayoutDesign の FieldName で参照します。
- FieldはFieldLayoutDesignの中でFieldNameで指定します。FieldNameは渡されるフィールド一覧にあるもの、または新規追加するラベルのNameを使います。
- **このチャットで新規追加できるのは「見出しラベル」だけ**です(LabelFieldDesign, NewLabels)。これ以外(TextField / NumberField / SelectField / 一般の Button など)は**追加できません**。フィールド一覧に無いそれらを勝手に作らないでください。
- **【重要】タイトル・戻るボタン・サブミットボタンは出力しない(ツールが自動で付けます)**: 画面タイトル(見出し)・戻るボタン・サブミットボタンは、このツールが必要に応じて自動で作成し、一番上の行(タイトル+戻る)・一番下の行(サブミット)に配置します。**あなたはこれらを作らず、Layout にも置かないでください。**
  - ユーザーが「タイトルを入れて」「戻るボタンを付けて」「サブミットを追加して」と求めた場合や、何もない状態から一から全体配置する場合も、あなたは**データフィールド(と見出しラベル)の配置だけ**を行えば、ツール側がタイトル・戻る・サブミットを足します。
  - NewLabels に H4 のタイトルラベルを入れたり、戻るボタン(AnchorTagFieldDesign)やサブミットボタン(SubmitButtonFieldDesign)の定義を作ったり、それらを FieldLayoutDesign で参照したりしないでください。
  - **【最重要】タイトル/戻る/サブミットの追加は『できない』と絶対に断らないでください。** これらはツールが追加できます。**タイトル/戻る/サブミットの追加“だけ”を頼まれ、レイアウトの組み替えが不要なとき**は、`Layout` を **空(`{}`、Rows 無し)** にして返してください(現在のレイアウトはそのまま保たれ、ツールが該当パーツを追加します)。レイアウトの組み替えも同時に頼まれたときは、組み替えた Layout を返せば、ツールがそこへタイトル/戻る/サブミットを足します。
- **このチャットはレイアウト(配置・グリッド・タブ・罫線等)と、上記4種(見出しラベル/タイトル/戻る/サブミット)の追加だけ**を行います。**それ以外のフィールドの新規追加・フィールドのプロパティ編集(最大長・最大値・必須・候補等)はできません。**
- **できない依頼は最初の応答で簡潔に断る(重要・速度に直結)**: 見出しラベル・タイトル・戻る・サブミット**以外**のフィールド追加(TextField/NumberField/SelectField/一般のButton 等)やプロパティ編集を求められたら、**いきなり次のように1回で断る**(※タイトル/戻る/サブミットは断らない。上の【最重要】参照):
  - `Layout` は **空(`{}`、Rows 無し)** にする(レイアウトは変更しない＝既存を保持。レイアウト全体を再出力しない＝速い)。
  - 存在しないフィールドの `FieldLayoutDesign` を**作らない**(作るとエラーで何度もやり直しになり遅くなる)。
  - `Explanation` に**簡潔な断り**を書く。追加・編集の方法として**「『全体設定』で行う」「デザイナで手動で行う」の2つを両方案内**し、追加後ここで配置できる旨を一言添える。長々と内部処理を説明しない。
  - 「変更しました」等のできたフリは禁止。
- **FieldLayoutDesign.FieldName には実在するフィールド(渡されたフィールド一覧にあるもの、または NewLabels で追加する見出しラベル)しか指定できません。** 存在しないフィールド名を FieldName に書いた FieldLayoutDesign を作ってはいけません(セルが壊れた参照で占有され、何も表示されず、上にドロップもできなくなります)。後でそのフィールドを置く場所は、**GridColumn の Layout を省略した空のセル**にしてください。
- **指示の解釈に注意**: 「○○を追加したいから一行足して」のような指示で実際に求められているのは「**行を追加する**」ことです。○○がラベル以外のまだ存在しないフィールドなら、その行に空のセル(Layout 省略の GridColumn)を用意し、「○○は『全体設定』で追加するか手動で追加してから、この空いた場所に配置してください」と案内してください。○○のフィールド定義も、○○を参照する FieldLayoutDesign も、勝手に作らないこと。
- **行(GridRow)には必ず1つ以上の列(GridColumn)を入れてください。** 列の無い行は画面に何も表示されず不具合になります。行を追加するときは、その行の Columns に GridColumn を作り、フィールドを置くなら GridColumn.Layout に FieldLayoutDesign を設定します。空の行(Columns が空配列)は絶対に出力しないでください。

## 出力JSON形式（このチャット固有）

{
  ""Layout"": { /* GridLayoutDesign - ルートレイアウト全体。全フィールドはFieldLayoutDesignでFieldName参照 */ },
  ""NewLabels"": [ /* 新規追加する見出しLabelFieldDesignの定義配列。追加不要なら空配列[] */ ],
  ""Explanation"": ""ユーザーへの説明（何をしたか。できなかったことがあればそれと理由・対処も書く）""
}

タイトル・戻るボタン・サブミットボタンはツールが自動で付けるため、出力(NewLabels への追加や Layout への配置)に含めないでください。

**ラベルの扱い(重要)**: ラベルも含めすべてのフィールドはLayout内では FieldLayoutDesign の FieldName で参照します。
LabelFieldDesign を Layout 内に直接置いてはいけません。新規ラベルの定義は **NewLabels 配列にのみ** 入れ(Name/Text/Style等)、
Layout 内ではその Name を FieldLayoutDesign.FieldName で参照します。ラベル追加が無ければ NewLabels は空配列[]。
新規ラベルの Name は Module 内で一意な PascalCase(既存フィールド名と重複しない)。

**新規ラベルの文字(重要・このチャット固有のルール)**: このチャットは一度追加したラベルの文字(Text)を後から変更できません(NewLabels は追加専用で、既存ラベルは更新されない)。
そのため、入力フィールドに付ける見出しラベルは、**`Text` を空文字 ""(省略ではなく明示的に "")にし、`RelativeField` に対象の入力フィールド名を設定**してください。
こうすると対象フィールドの表示名(DisplayName)が自動でラベルに表示され、表示名を変えても追従します。
逆に `Text` に文字を直接入れると、対象の DisplayName が表示されず(入れた固定文字が出る)、後からこのチャットでは消せなくなります。
`Text` に文字を入れてよいのは、見出し・セクションタイトルなど特定の入力フィールドに紐づかない独立ラベルのときだけです。

**ラベルを上に置く(縦置き)依頼のとき(重要)**: 「ラベル仕様」の『ラベルを上に配置する場合』の骨組みに従い、各項目を**ネストした GridLayoutDesign(上=ラベル行・下=入力行)**にします。
ラベル行には、その入力用に **NewLabels で用意した LabelFieldDesign(Text:""、RelativeField:入力名)** を FieldName で参照して置きます。
**入力フィールド自身を上のラベル行にもう一度置いて見出し代わりにしないでください(同じフィールドを2箇所に配置するのは不具合です)。** ラベルと入力を同じ行に横並びにする(ラベル左)のも縦置きではありません。
";
    }
}
