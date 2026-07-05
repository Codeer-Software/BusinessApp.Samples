using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.SystemSettings;
using OpenAI.Chat;
using System.Text;
using System.Text.RegularExpressions;

namespace AccountingApp.Designer.Lib.AI
{
    /// <summary>
    /// モジュール設定(フィールド定義)と現在のDBスキーマの差分から、DBを設計に合わせるDDLを
    /// AIで生成する。機械的な <see cref="DbMapping.CreateDDL"/> を「安全な型のデフォルト」として渡し、
    /// それを超える内容(フィールド制約に基づく型最適化・型変更ALTER・インデックス等)をAIに作らせる。
    /// 生成したDDLは実行せず、呼び出し側がモードレスの DDLWindow に表示してユーザーにRunさせる。
    /// </summary>
    public class ModuleDdlGenerator
    {
        readonly ChatClient _chatClient;

        public ModuleDdlGenerator(AISettings settings) => _chatClient = settings.CreateChatClient();

        public async Task<string> GenerateAsync(ModuleDesign module, DataSourceType dataSourceType,
            List<DbTableDefinition> existingTables, List<string> mechanicalBaseline, string userInstruction)
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(BuildContext(module, dataSourceType, existingTables, mechanicalBaseline, userInstruction)),
            };

            var result = await _chatClient.CompleteChatAsync(messages);
            var text = result.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
            return ExtractSql(text);
        }

        static string BuildContext(ModuleDesign module, DataSourceType dataSourceType,
            List<DbTableDefinition> existingTables, List<string> mechanicalBaseline, string userInstruction)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"DB種別: {dataSourceType}");
            sb.AppendLine($"対象テーブル: {module.DbTable}");
            sb.AppendLine();

            sb.AppendLine("## ★このDB({0})での列の型変更方法（最優先・他の方言の構文は絶対に使わない）".Replace("{0}", dataSourceType.ToString()));
            sb.AppendLine(TypeChangeGuidance(dataSourceType, module.DbTable));
            sb.AppendLine();

            sb.AppendLine("## 現在のモジュール定義(フィールド)");
            sb.AppendLine("各フィールドの型(TypeFullName)と DbColumn、MaxLength / Max / MaxFractionDigits / IsRequired 等の制約から、最適な列型を判断してください。");
            sb.AppendLine(JsonConverterEx.SerializeObject(module.Fields));
            sb.AppendLine();

            sb.AppendLine("## 現在のDBスキーマ(対象テーブルの実際の列)");
            var existing = existingTables.FirstOrDefault(
                t => string.Equals(t.Name, module.DbTable, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                sb.AppendLine("テーブルは存在しません(新規作成が必要)。");
            }
            else
            {
                foreach (var c in existing.Columns)
                    sb.AppendLine($"- {c.Name}: {c.RawDbTypeName} (.NET型 {c.NetTypeFullName}, {(c.IsNullable ? "NULL可" : "NOT NULL")})");
            }
            sb.AppendLine();

            sb.AppendLine("## 機械生成の基準DDL(型の安全なデフォルト。列名と対応は尊重しつつ、型はここから改善してよい)");
            sb.AppendLine(string.Join(Environment.NewLine, mechanicalBaseline));
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(userInstruction))
            {
                sb.AppendLine("## ユーザーの指示(インデックス等の意図の参考)");
                sb.AppendLine(userInstruction);
            }

            return sb.ToString();
        }

        // DB種別はコード側で確定しているので、その方言の型変更方法だけを渡す(5方言を並べて選ばせると誤った方言を引く)。
        static string TypeChangeGuidance(DataSourceType dataSourceType, string table) => dataSourceType switch
        {
            DataSourceType.PostgreSQL =>
                $"ALTER TABLE {table} ALTER COLUMN <列> TYPE <新型> USING <列>::<新型>; の形で変更します。",
            DataSourceType.MySQL =>
                $"ALTER TABLE {table} MODIFY COLUMN <列> <新型>; の形で変更します。",
            DataSourceType.SQLServer =>
                $"ALTER TABLE {table} ALTER COLUMN <列> <新型>; の形で変更します(USING は付けない)。",
            DataSourceType.Oracle =>
                $"ALTER TABLE {table} MODIFY (<列> <新型>); の形で変更します。",
            DataSourceType.SQLite =>
                $@"SQLite には列の型を変更する構文がありません。`ALTER TABLE ... ALTER COLUMN ... TYPE ...` は絶対に使わないでください(構文エラーになります)。
型を変えるにはテーブルを作り直してデータを移します。手順:
  1. 新スキーマで一時テーブルを作る(例 {table}__new)。型は最適化後の型にする。CREATE文は基準DDLの CREATE TABLE をベースに型だけ直して作る。
  2. INSERT INTO {table}__new (列...) SELECT 列... FROM {table};  -- 型が変わる列は CAST(<列> AS <新型>) で移す
  3. DROP TABLE {table};
  4. ALTER TABLE {table}__new RENAME TO {table};",
            _ => "DBの標準的な方法で列の型を変更してください。"
        };

        static string ExtractSql(string text)
        {
            var sql = Regex.Match(text, @"```sql\s(.*?)\s```", RegexOptions.Singleline);
            if (sql.Success) return sql.Groups[1].Value.Trim();

            var generic = Regex.Match(text, @"```[a-zA-Z]*\s(.*?)\s```", RegexOptions.Singleline);
            if (generic.Success) return generic.Groups[1].Value.Trim();

            return text.Trim();
        }

        const string SystemPrompt = @"
あなたはデータベースのスキーマ移行(DDL)の専門家です。
ローコードフレームワークのモジュール定義(フィールド)と現在のDBスキーマの差分から、
DBを設計に合わせるための実行可能なDDLを生成します。

## 出力形式
- 出力は単一の ```sql ブロックの中に、実行可能なDDLだけを入れてください。C#やDDL以外の解説は不要です。
- このSQLはC#から DbConnection で実行されます。DECLARE 等そのままでは実行できない構文は入れないでください。
- 変更が一切不要な場合は ```sql の中に `-- 変更は不要です` だけを出力してください。

## 型の決定(ここが機械生成を超える肝)
- フレームワークはDB列の.NET型から値を変換して読み書きします。値を保持でき、変換可能で、サイズが足りる型であれば自由に選べます。
- 文字列(TextField): MaxLength があれば VARCHAR/NVARCHAR(MaxLength) 等の上限付き、無ければ可変長最大(TEXT 等)。常に最大長にしないこと。
- 数値(NumberField): MaxFractionDigits があれば DECIMAL(p, s)、無ければ INTEGER 系。Max から桁を見積もってよい。
- 基準DDLの列名・対応関係は尊重し、型だけを制約から最適化してください。
- システムフィールド(Id 等)の主キー・自動採番、他テーブルを参照する Id 列の整数幅は基準DDLに従ってください(食い違うとFKや桁で破綻します)。

## 差分の作り方
- テーブルが存在しなければ CREATE TABLE。存在すれば不足している列だけ ALTER TABLE ADD。
- 既存列の型とフィールドの型が食い違う場合は、その列を新しい型に変更してください。**変更構文はメッセージ冒頭の「★このDBでの列の型変更方法」に必ず従い、他の方言の構文は絶対に使わないこと**(構文エラーになります)。
- 型変更でデータ変換が失敗しうる場合(文字列→数値 等)も、最終的にユーザーが内容を確認して実行するので、移行DDLとして出力してください。

## インデックス
- 検索やFKに使う列にはインデックスを提案してください。具体的には、他テーブルを参照する Id 列・LinkField の列、ユーザーが「検索する」と言った列、コード等の一意な列(UNIQUE)。
- 既存インデックスの情報は渡されません。CREATE INDEX IF NOT EXISTS が使えるDB(PostgreSQL/SQLite/MySQL)では IF NOT EXISTS を付けてください。SQL Server / Oracle は重複作成しないよう、ユーザーが確認して実行する前提でそのまま出してください。

## 安全規則(重要)
- データを失う操作 — DROP TABLE / DROP COLUMN / 桁やサイズを縮小する型変更 — は、原則として誤実行防止のためコメント(/* */ または行頭 --)で囲んで出力します(追加・拡張は実行される形、破壊・縮小はコメントで、が既定)。
- **ただし、ユーザーの指示がその削除/縮小を明示的に求めている場合は、その操作は実行可能な形(コメントしない)で出力してください。** 例: 「test カラムを削除して」「このテーブルを削除して」と言われたら、その `DROP COLUMN test;` / `DROP TABLE ...;` は**コメントせず実行可能**に出す(ユーザーが意図した操作なので)。基準DDLでコメント化されていても、明示要求された分はコメントを外す。
- コメントで囲むのは「ユーザーが頼んでいないのに念のため提示する破壊操作(孤立した既存列の削除候補など)」だけにとどめてください。
";
    }
}
