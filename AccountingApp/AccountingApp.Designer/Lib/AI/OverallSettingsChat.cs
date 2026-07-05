using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.DesignLogic.Check;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using OpenAI.Chat;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountingApp.Designer.Lib.AI
{
    public class OverallSettingsChat : IAIChat
    {
        readonly IModuleOverallSettingsEditor _editor;
        readonly ChatClient _chatClient;
        readonly IDesignerChatHost _host;
        readonly ModuleDdlGenerator _ddlGenerator;
        readonly List<ChatMessage> _messages = new();

        // 直近のユーザー指示。DDL生成時にインデックス等の意図の参考として渡す。
        string _lastInstruction = string.Empty;

        public string Explanation => "モジュールとフィールドの設定を編集するためのチャットです";

        public OverallSettingsChat(IDesignerChatHost host, AISettings settings, IModuleOverallSettingsEditor editor)
        {
            _host = host;
            _editor = editor;
            _chatClient = settings.CreateChatClient();
            _ddlGenerator = new ModuleDdlGenerator(settings);
        }

        public void Clear() => _messages.Clear();

        // 現在のモジュール設定を AI に見せる用のシリアライズ。null も省略せず出力し、未設定プロパティの存在も伝える。
        static readonly JsonSerializerOptions ViewOptions = CreateViewOptions();

        static JsonSerializerOptions CreateViewOptions()
        {
            var o = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
            };
            o.Converters.Add(new JsonStringEnumConverter());
            o.Converters.AddJsonConverters();
            return o;
        }

        static string SerializeForView(object value) => JsonSerializer.Serialize(value, ViewOptions);

        public class ModuleDesignEditing
        {
            public string DataSourceName { get; set; } = string.Empty;
            public string DbTable { get; set; } = string.Empty;

            public bool CanCreate { get; set; } = true;
            public bool CanUpdate { get; set; } = true;
            public bool CanDelete { get; set; } = true;

            public ModuleMatchCondition UserWriteCondition { get; set; } = new();
            public ModuleMatchCondition UserReadCondition { get; set; } = new();
            public ModuleMatchCondition DataWriteCondition { get; set; } = new();
            public ModuleMatchCondition DataReadCondition { get; set; } = new();

            public List<FieldDesignBase> Fields { get; set; } = new();
        }

        public class IO
        {
            public string ModuleName { get; set; } = string.Empty;
            public ModuleDesignEditing ModuleDesign { get; set; } = new();
            public List<string> NeedModuleInfo { get; set; } = new();

            // 物理DBスキーマの変更(列の型変更・インデックス・制約の追加等)が必要な指示のときtrue。
            // 列の新規追加はプログラム側でも自動検出するが、型変更/インデックス等は検出できないためAIに判断させる。
            public bool NeedsDatabaseUpdate { get; set; }

            public string Explanation { get; set; } = string.Empty;
        }

        public class ModuleInfo
        {
            public string Name { get; set; } = string.Empty;
            public Dictionary<string, string> FieldNameAndTypes { get; set; } = new();
        }

        public async Task<string> ProcessMessage(string message)
        {
            _lastInstruction = message;
            var designData = _host.GetDesignData();

            // 仕様プロンプト(SystemPrompt + モジュール設定仕様Docs + デザイン情報)は会話の最初の1回だけ履歴に入れる。
            if (_messages.Count == 0)
            {
                _messages.Add(new SystemChatMessage(SystemPrompt));
                if (!string.IsNullOrEmpty(ModuleReference))
                    _messages.Add(new SystemChatMessage(
                        "## モジュール設定仕様（ModuleDesign構造・権限条件・フィールド共通基底・検索条件・システムフィールド・フィールド型カタログ・TypeFullName一覧）\n\n" + ModuleReference));
                _messages.Add(new SystemChatMessage(BuildDesignContextInfo(designData)));
            }

            // 現在のモジュール設定と指示は毎ターン追加する(設定は編集で変わるため常に最新を渡す)。
            // null も含め全プロパティを見せる(SerializeForView)。既定の SerializeObject は null を省略するため、
            // 現在 null のプロパティ(例: 未設定の TextField.MaxLength)が AI から見えず編集できない問題を防ぐ。
            var editingModule = _editor.GetModuleDesign();
            _messages.Add(new UserChatMessage(
                $"現在のモジュール設定(null は未設定。どのプロパティも編集してよい):\n{SerializeForView(BuildInput(editingModule))}\n\n指示: {message}"));

            return await GenerateAndApplyAsync(1, new());
        }

        // 生成 → (他モジュール情報が必要なら提供して再生成) → デザインチェック → エラーなら適用し直さず再生成、を統合したループ。
        // モジュール設定/フィールド定義が壊れると読み込み不能で致命的なので、検証を通らなかった定義は適用しない。
        async Task<string> GenerateAndApplyAsync(int attempt, List<string> providedModuleInfo)
        {
            IO? output;
            string resultText;
            try
            {
                var result = await _chatClient.CompleteChatAsync(_messages,
                    new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() });
                resultText = result.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
                output = JsonConverterEx.DeserializeObject<IO>(resultText);
            }
            catch (Exception ex)
            {
                if (_messages.Count > 0 && _messages[^1] is UserChatMessage)
                    _messages.RemoveAt(_messages.Count - 1);
                return $"エラーリトライしてください\r\n{ex.Message}";
            }

            _messages.Add(new AssistantChatMessage(resultText));

            // 有効な JSON が得られなかった(null)場合は再生成。実際のパースエラー(途中切れ/構文/型不一致)を添える。
            if (output == null)
            {
                var parseError = AiJsonValidation.GetUnmappedMemberError<IO>(resultText) ?? "(JSONとして解釈できませんでした。出力が途中で切れていないか確認してください)";
                if (attempt < 3)
                {
                    _messages.Add(new UserChatMessage(
                        "有効なJSONが返りませんでした。次のエラーを解消し、出力JSON形式で設定全体を最後まで出力してください。\n" + parseError));
                    return await GenerateAndApplyAsync(attempt + 1, providedModuleInfo);
                }
                return $"有効な応答が得られなかったため、変更は適用していません。\r\n{parseError}";
            }

            // AI は省略可能なプロパティを null で返すことがある(例: NeedModuleInfo: null)。
            // 以降の処理で NullReference にならないよう、null を既定値に正規化する。
            output.NeedModuleInfo ??= new();
            output.ModuleDesign ??= new();
            output.ModuleDesign.Fields ??= new();
            output.ModuleDesign.UserWriteCondition ??= new();
            output.ModuleDesign.UserReadCondition ??= new();
            output.ModuleDesign.DataWriteCondition ??= new();
            output.ModuleDesign.DataReadCondition ??= new();

            // 他モジュールの情報が必要なら、提供してから再生成する
            if (output.NeedModuleInfo.Any())
            {
                var newInfo = output.NeedModuleInfo.Where(n => !providedModuleInfo.Contains(n)).Distinct().ToList();
                if (newInfo.Any())
                {
                    var designData = _host.GetDesignData();
                    var mods = new List<ModuleInfo>();
                    foreach (var modName in newInfo)
                    {
                        var mod = designData.Modules.Find(modName);
                        if (mod == null) continue;
                        var fieldNameAndTypes = new Dictionary<string, string>();
                        foreach (var field in mod.Fields)
                            fieldNameAndTypes[field.Name] = field.GetType().Name;
                        mods.Add(new ModuleInfo { Name = mod.Name, FieldNameAndTypes = fieldNameAndTypes });
                    }
                    _messages.Add(new UserChatMessage(
                        $"要求された他モジュールの情報です。これを踏まえて設定を出力してください。\n{JsonConverterEx.SerializeObject(mods)}"));
                    providedModuleInfo.AddRange(newInfo);
                    return await GenerateAndApplyAsync(attempt, providedModuleInfo);
                }
            }

            // 未知プロパティ検証: 定義に存在しないプロパティをAIが書くと、通常のデシリアライズでは黙って捨てられ、
            // 値が反映されないのに成功扱いになる(例: FieldMatchCondition に存在しない "Condition" を書く)。
            // strict 検証で検出し、まずは再生成ループでAIに直させる(意味のある値の取りこぼしを救う)。
            var unmappedError = AiJsonValidation.GetUnmappedMemberError<IO>(resultText);
            if (unmappedError != null && attempt < 3)
            {
                _messages.Add(new UserChatMessage(
                    "生成されたJSONに、定義に存在しないプロパティが含まれています。" +
                    "存在しないプロパティに書いた値は無視され、設定に反映されません。" +
                    "次のエラーを解消し、正しいプロパティ名・構造で設定全体を再度出力してください。\n" + unmappedError));
                return await GenerateAndApplyAsync(attempt + 1, providedModuleInfo);
            }
            // 上限まで再生成しても定義外プロパティが残る場合: それらは黙って捨てられるだけで、認識できた
            // プロパティ(フィールド定義等)は正しく反映される。AIは習慣的に余分なプロパティ(DisplayName /
            // LinkFieldNames 等)を付けがちで、それ1つで全体を捨てると「何も起きない」になり実害が大きい。
            // よって適用しつつ、無視したプロパティを警告する(unmappedError は後で説明に付記)。

            // デザインチェック(CheckFieldName 等)は DesignData 原本のモジュールを参照するため、検証用コピーを
            // 別途検証しても新規フィールドが原本に無く誤検出する。editingModule(=DesignData原本)に直接適用して
            // から検証し、エラー時は backup から元に戻す(壊れた定義は残さない)。
            var editingModule = _editor.GetModuleDesign();
            var backup = JsonConverterEx.DeserializeObject<ModuleDesign>(JsonConverterEx.SerializeObject(editingModule))!;
            ApplyOutput(editingModule, output);
            var errors = ValidateModule(editingModule);

            if (errors.Count == 0)
            {
                _editor.Update();
                var explanation = string.IsNullOrEmpty(output.Explanation) ? "変更しました" : output.Explanation;
                if (unmappedError != null)
                    explanation += $"\r\n(注意: 認識できないプロパティがあり無視しました: {unmappedError})";
                return await MaybeOpenDdlWindowAsync(explanation, editingModule, output.NeedsDatabaseUpdate);
            }

            // 検証NG: 適用を取り消す(DesignData原本を backup に戻す)
            RestoreModule(editingModule, backup);

            // 検証エラーがある: 上限まではエラーをAIに返して再生成、上限を超えたら適用しない。
            if (attempt < 3)
            {
                _messages.Add(new UserChatMessage(
                    "生成された設定に次のデザインチェックエラーがあります。修正して設定全体を再度出力してください。\n" + FormatErrors(errors)));
                return await GenerateAndApplyAsync(attempt + 1, providedModuleInfo);
            }

            return $"デザインチェックエラーを解消できなかったため、変更は適用していません(現在の設定は保持されます)。\r\n{FormatErrors(errors)}";
        }

        // 設計適用後、ユーザーが明示的にDB定義の変更を求めたときだけ、AI で「Create DDL を超えた」DDL を
        // 生成して既存の DDLWindow をモードレスで開く。実行はユーザーが実行ボタンで行う(AIは実行しない)。
        async Task<string> MaybeOpenDdlWindowAsync(string explanation, ModuleDesign module, bool userRequestedDbUpdate)
        {
            try
            {
                // ユーザーがDB定義の変更を明示的に求めたときだけ開く(項目追加のたびに開くと邪魔)。
                if (!userRequestedDbUpdate) return explanation;

                if (string.IsNullOrEmpty(module.DataSourceName) || string.IsNullOrEmpty(module.DbTable))
                    return explanation;

                var dataSource = _host.GetDataSources()
                    .FirstOrDefault(d => d.Name == module.DataSourceName);
                if (dataSource == null) return explanation;

                var existing = _host.GetDbInfo(dataSource.Name);
                var baseline = module.CreateDDL(dataSource.DataSourceType, existing);
                var ddl = await _ddlGenerator.GenerateAsync(
                    module, dataSource.DataSourceType, existing, baseline, _lastInstruction);

                if (string.IsNullOrWhiteSpace(ddl) || ddl.Contains("変更は不要"))
                    return explanation;

                _host.ShowDdl(dataSource, ddl);
                return explanation +
                    "\r\nDBスキーマの更新が必要です。DDLウィンドウを開いたので、内容を確認して実行ボタンで実行してください(自動実行はしません)。";
            }
            catch (Exception ex)
            {
                return explanation + $"\r\n(DDLの生成中にエラーが発生しました: {ex.Message})";
            }
        }

        IO BuildInput(ModuleDesign editingModule)
            => new IO
            {
                ModuleName = editingModule.Name,
                ModuleDesign = new ModuleDesignEditing
                {
                    DataSourceName = editingModule.DataSourceName,
                    DbTable = editingModule.DbTable,
                    CanCreate = editingModule.CanCreate,
                    CanUpdate = editingModule.CanUpdate,
                    CanDelete = editingModule.CanDelete,
                    UserWriteCondition = editingModule.UserWriteCondition,
                    UserReadCondition = editingModule.UserReadCondition,
                    DataWriteCondition = editingModule.DataWriteCondition,
                    DataReadCondition = editingModule.DataReadCondition,
                    Fields = editingModule.Fields,
                },
                NeedModuleInfo = new()
            };

        static void ApplyOutput(ModuleDesign module, IO output)
        {
            module.DataSourceName = output.ModuleDesign.DataSourceName;
            module.DbTable = output.ModuleDesign.DbTable;
            module.CanCreate = output.ModuleDesign.CanCreate;
            module.CanUpdate = output.ModuleDesign.CanUpdate;
            module.CanDelete = output.ModuleDesign.CanDelete;
            module.UserWriteCondition = output.ModuleDesign.UserWriteCondition;
            module.UserReadCondition = output.ModuleDesign.UserReadCondition;
            module.DataWriteCondition = output.ModuleDesign.DataWriteCondition;
            module.DataReadCondition = output.ModuleDesign.DataReadCondition;
            module.Fields = output.ModuleDesign.Fields;
        }

        // ApplyOutput で書き換えたプロパティを backup の内容に戻す(検証NG時の取り消し用)。
        static void RestoreModule(ModuleDesign target, ModuleDesign backup)
        {
            target.DataSourceName = backup.DataSourceName;
            target.DbTable = backup.DbTable;
            target.CanCreate = backup.CanCreate;
            target.CanUpdate = backup.CanUpdate;
            target.CanDelete = backup.CanDelete;
            target.UserWriteCondition = backup.UserWriteCondition;
            target.UserReadCondition = backup.UserReadCondition;
            target.DataWriteCondition = backup.DataWriteCondition;
            target.DataReadCondition = backup.DataReadCondition;
            target.Fields = backup.Fields;
        }

        List<DesignCheckInfo> ValidateModule(ModuleDesign module)
        {
            try
            {
                // DB テーブル/データソースの「存在しない」チェックは適用ゲートから除外する。
                // テーブル一覧は AI に渡しておらず、ユーザーが新規作成予定の(まだ DB に無い)テーブル名や
                // データソース名を指定するのは正当な操作。これを致命的エラー扱いにすると、再生成ループで
                // AI が「デザインチェックエラーに合わせて」DbTable/DataSourceName を不当に空へ戻してしまう。
                // (これらは設計の読み込み不能を招く致命的エラーではなく、デザイナのチェックパネルには
                //  引き続き警告として表示されるため、ユーザーには気付ける)
                return _editor.CheckModule(module)
                    .Where(e => !IsExternalResourceExistenceError(e, module))
                    .ToList();
            }
            catch
            {
                return new();
            }
        }

        static bool IsExternalResourceExistenceError(DesignCheckInfo info, ModuleDesign module)
        {
            // モジュールの DbTable / DataSourceName が「存在しない」: ユーザーが新規作成予定の名前を指定するのは正当。
            if (info is ModuleDesignCheckInfo m
                && m.Location.Module == module.Name
                && (m.Location.Member == nameof(ModuleDesign.DbTable)
                    || m.Location.Member == nameof(ModuleDesign.DataSourceName)))
                return true;

            // フィールドの DbColumn(File/楽観ロック等の DbColumnXxx 含む)が「DB に存在しない」: これから DDL で追加する
            // 新規列を指定するのは正当な操作。これをゲートで弾くと、再生成ループで AI が DbColumn を不当に空へ戻して
            // しまう(例: 楽観ロックにカラム名を入れてと言っても空に戻され続ける)。チェックパネルには警告として残る。
            if (info is FieldDesignCheckInfo f
                && f.Location.Module == module.Name
                && f.Location.Member?.StartsWith("DbColumn", StringComparison.Ordinal) == true)
                return true;

            return false;
        }

        static string FormatErrors(List<DesignCheckInfo> errors)
            => string.Join("\n", errors.Select(e => $"- {e.GetPositionText()}: {e.Message}"));

        string BuildDesignContextInfo(DesignData designData)
        {
            var lines = new List<string> { "## 現在のアプリケーション情報" };

            try
            {
                var moduleNames = designData.Modules.GetModuleNames();
                if (moduleNames.Any())
                {
                    lines.Add("\n### モジュール一覧（LinkField等の参照先として使用可能）");
                    foreach (var name in moduleNames)
                    {
                        var mod = designData.Modules.Find(name);
                        if (mod == null) continue;
                        var fieldSummary = mod.Fields.Select(f => $"{f.Name}({f.GetType().Name})");
                        lines.Add($"- {name}: {string.Join(", ", fieldSummary)}");
                    }
                }

                var dataSources = _host.GetDataSources();
                if (dataSources.Any())
                {
                    lines.Add("\n### データソース一覧（DataSourceNameに指定可能）");
                    foreach (var ds in dataSources)
                    {
                        lines.Add($"- {ds.Name} ({ds.DataSourceType})");
                    }
                }
            }
            catch
            {
                lines.Add("（デザインデータの取得に失敗しました）");
            }

            return string.Join("\n", lines);
        }

        // モジュール設定の詳細仕様(ModuleDesign構造・権限条件・フィールド共通基底・検索条件・システムフィールド・
        // フィールド型カタログ・TypeFullName一覧)は Lib/AI の各 .md を埋め込みリソースとして読み込んで連結する。
        static readonly string ModuleReference = LoadModuleReference();

        static string LoadModuleReference()
        {
            var sb = new StringBuilder();
            var asm = typeof(OverallSettingsChat).Assembly;
            foreach (var name in new[]
            {
                "AccountingApp.Designer.Lib.AI.ModuleDesign.md",
                "AccountingApp.Designer.Lib.AI._FieldCommon.md",
                "AccountingApp.Designer.Lib.AI.SearchConditions.md",
                "AccountingApp.Designer.Lib.AI.JsonAbstractTypeFullName.md",
            })
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

        // ModuleDesignの構造・権限条件・フィールド共通基底・検索条件・システムフィールド・フィールド型カタログ・TypeFullName一覧は
        // 別途渡す「## モジュール設定仕様」を参照。ここには OverallSettingsChat 固有の出力プロトコル(IO形式・NeedModuleInfo)のみを書く。
        const string SystemPrompt = @"
あなたはローコードWebアプリケーションのモジュール設定（フィールド定義・CRUD権限等）のデザイナです。
ユーザーの指示に基づいてモジュールのフィールドや設定を編集し、結果をJSONで返してください。
ModuleDesignの構造・権限条件・フィールド共通基底プロパティ・検索条件(SearchCondition/MatchCondition)・
システムフィールド(予約名と型)・各フィールド型の用途・各クラスのTypeFullNameは、別途渡される「## モジュール設定仕様」に必ず従ってください。

## 基本ルール
- 元のModuleDesignが渡されるので、ユーザーの指示に対して**必要最小限の変更**にしてください。
- 既存のフィールドや設定は指示がない限り変更・削除しないでください。
- ModuleNameは変更しないでください。
- **DbTable/DataSourceName について**: あなたにはデータソース内のテーブル一覧は渡されません。したがって指定されたテーブルが実在するかどうかをあなたは判断できません。ユーザーが指定したテーブル名（これから新規に作成する未存在のテーブル名を含む）は**そのまま採用**し、「存在しないから」という理由で勝手に空に戻したり変更したりしないでください。空にするのは、ユーザーが明示的に「DB連携をやめる」「テーブル名を消す」と指示したときだけです。
- フィールド名はModule内で一意な PascalCase、DBカラム名は snake_case にしてください。
- **JSON数値型に注意**: int型プロパティに14.0のような小数点付き数値を書くとエラーになります。整数は必ず整数で書いてください。
- 各フィールド定義・検索条件・値オブジェクトには完全修飾名 TypeFullName を必ず設定してください（一覧は「## モジュール設定仕様」のTypeFullNameルールを参照）。

## 出力JSON形式（このチャット固有）

{
  ""ModuleName"": ""モジュール名（変更しない、そのまま返す）"",
  ""ModuleDesign"": { /* ModuleDesignEditing - 設定全体。DataSourceName/DbTable/CanCreate/CanUpdate/CanDelete/各権限条件/Fields */ },
  ""NeedModuleInfo"": [ /* 他モジュールのフィールド構成が必要なときだけモジュール名を列挙(情報を入れて再リクエストされる)。不要なら[] */ ],
  ""NeedsDatabaseUpdate"": false, /* 下記参照。ユーザーがDB定義の変更を明示的に求めたときだけtrue */
  ""Explanation"": ""変更内容の簡潔な日本語説明""
}

## NeedsDatabaseUpdate について
**ユーザーが物理DBの定義変更を明示的に求めたときだけ** true にしてください。true にすると、設定の変更が適用された後に別途DDL(CREATE/ALTER/INDEX等)を生成してDDLウィンドウで提示します(あなたがDDLを書く必要はありません)。
- true にする例: 「DBを更新して」「テーブル定義を変えて」「この列の型を変えて」「インデックスを貼って」「外部キー(制約)を追加して」など、DB側の定義変更を直接依頼されたとき。
- false にする例: 単にフィールドや設定を追加・編集しただけ(「○○項目を追加して」等)。DB反映を求められていないのに勝手に true にしないでください。項目を追加してもDB更新が必要かはユーザーが別途判断します。
";
    }
}
