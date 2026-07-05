using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.Repository.Design;
using OpenAI.Chat;
using System.IO;
using System.Text.RegularExpressions;

namespace AccountingApp.Designer.Lib.AI
{
    public class QueryChat : IAIChat
    {
        readonly IDesignerChatHost _host;
        readonly string _dataSourceName;
        readonly List<ChatMessage> _chatHistory = new();
        readonly ChatClient _chatClient;
        readonly IQueryEditor _editor;
        bool _tableInfoSet = false;

        public string Explanation => "SQLを編集するためのチャットです";

        public string Module { get; set; } = string.Empty;

        public QueryChat(IDesignerChatHost host, AISettings settings, IQueryEditor editor)
        {
            _host = host;
            _editor = editor;
            _dataSourceName = _editor.GetDataSourceName();
            _chatClient = settings.CreateChatClient();
        }

        public void Clear()
        {
            _chatHistory.Clear();
            if (string.IsNullOrEmpty(_dataSourceName)) return;

            var dataSourceInfo = string.Empty;
            var info = _host.GetDbInfo(_dataSourceName);
            var dataSourceInfoPath = Path.Combine(_host.CurrentFileDirectory, $"{_dataSourceName}.txt");
            if (File.Exists(dataSourceInfoPath))
            {
                dataSourceInfo = File.ReadAllText(dataSourceInfoPath);
            }
            else
            {
                foreach (var e in info)
                {
                    dataSourceInfo += $"{e.Name}\n";
                }
            }
            _tableInfoSet = false;
            var dataSource = _host.GetDataSources().FirstOrDefault(x => x.Name == _dataSourceName);

            var currentSqlMessage = string.Empty;
            var currentSql = _editor.GetCurrentSql();
            if (!string.IsNullOrEmpty(currentSql))
            {
                currentSqlMessage = $@"現在のSQLです。これの改善を求められることもあります。
{currentSql}";
            }
            var dbType = dataSource?.DataSourceType.ToString() ?? string.Empty;
            _chatHistory.Add(new SystemChatMessage($@"
あなたはSQLの専門家です。
ユーザーが求めるSQLを書くことが目的です。
ユーザーの使っているDBの種類は 
{dbType} です。

{currentSqlMessage}

まずはユーザーの求めるSQLで使うテーブルを選択します。

ユーザーと会話しながら決めてください。
ただしユーザーはDB設計の詳細を知りません。
あなたが全責任をもって適切なテーブルを選択/提案してください。
ユーザーがその提案で良いといった場合は使うテーブルが決定されます。
テーブルが確定したらそれぞれのテーブルのカラムを別途提供します。
テーブルが確定するまでSQLは絶対に書かないでください。
勝手な推測、提案は厳禁です。

ユーザーの承認を得た後に
あなたは以下の囲いの中にテーブル名をカンマ区切りで出力してください。
```tbl

```
この出力があると次のフェーズに進みます。
必ずユーザーの承認を得てください。

"));

            _chatHistory.Add(new SystemChatMessage($@"
テーブル情報です。
{dataSourceInfo}
"));
        }

        void SetTableInfo(List<string> selectedTables)
        {
            var info = _host.GetDbInfo(_dataSourceName);

            if (_tableInfoSet) return;
            _tableInfoSet = true;

            _chatHistory.RemoveAt(1);
            var dataSource = _host.GetDataSources().FirstOrDefault(x => x.Name == _dataSourceName);

            var dbType = dataSource?.DataSourceType.ToString() ?? string.Empty;
            _chatHistory.Add(new SystemChatMessage($@"
今からはSQLを書くフェーズです。
引き続き、ユーザーと話しつつクエリの設計を進めてください。
クエリを作る以外にも質問があれば答えてください。
SQLの部分は以下のように囲ってください。

```sql

```
このSQLはC#からDbConnectionを使って実行します。DECLAREなどその方法で使えないSQLは入れないでください。パラメータをユーザーに求められた場合はその前提で書いてください。
またパラメータは指定しない場合はその条件を無視するようなSQLにしてください。
例えば以下のようなものです。
saledate >= @p1 OR @p1 IS NULL

最終的にSelectされる項目とパラメータは以下の形でも出力してください。
カラムはIsParameterを出力しないでください。
パラメータはIsParameterをtrueにしてNameは@や:などを含めた形で書いてください。
```schema
[
  {{
    ""Name"": ""name"",
    ""DbType"": ""db raw type""
  }},
  {{
    ""IsParameter"": true,
    ""Name"": ""@name"",
    ""DbType"": ""db raw type""
  }}
]
```

C#の解説は必要ありません。

たまにあなたが作ったSQLを実行すると失敗するときがあります。
その場合はユーザーはあなたにその旨を伝え作り直しを要求します。
その場合でも言い訳は書かなくていいので簡潔にシンプルに迅速にSQLとスキーマを返してください。

そしてこれが重要なのですが、与えられたテーブル情報では実現不可能な場合はテーブル情報の取得からやり直します。
やり直す場合は $$$reset$$$ という文言を出力してください。
決して知らないテーブルに対してSQLを書こうとしないでください。

ユーザーから「テーブル選択からやり直したい」旨のメッセージがあった場合も
$$$reset$$$ という文言を出力してください。

プログラムで  $$$reset$$$ を目印にテーブル選択からやり直すようにするのでこの指令は絶対です。
"));

            _chatHistory.Add(new SystemChatMessage($@"
現在のテーブル情報です。これ以外のテーブル名を勝手に提案することも禁じます。勝手に現在のテーブル情報以上にDBの定義を取得する質問も禁じます。そうではなく $$$reset$$$ を出力してください。そうすればテーブル情報が取得されます。
{CreateDbInfo(info, selectedTables)}
"));
        }

        static string CreateDbInfo(List<DbTableDefinition> info, List<string> selectedTables)
        {
            var tables = new List<string>();
            foreach (var table in info.Where(e => selectedTables.Contains(e.Name)))
            {
                var columns = new List<string>();
                foreach (var column in table.Columns)
                {
                    columns.Add($"{column.Name}:{column.RawDbTypeName}");
                }

                tables.Add($"{table.Name}:{{{string.Join(",", columns)}}}");
            }
            return string.Join("\n", tables);
        }

        public async Task<string> ProcessMessage(string userMessage)
        {
            if (!_chatHistory.Any()) Clear();

            if (string.IsNullOrEmpty(_dataSourceName)) return "データソースが指定されていません。";

            _chatHistory.Add(new UserChatMessage(userMessage));
            var result = await _chatClient.CompleteChatAsync(_chatHistory);
            var resultText = result.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
            _chatHistory.Add(new AssistantChatMessage(resultText));

            if (resultText.Contains("$$$reset$$$"))
            {
                Clear();
                return "現在選択中のテーブル情報では実現できませんでした。もう一度最初からやり直してください。";
            }

            var matchTable = Regex.Match(resultText, @"```tbl\s(.*?)\s```", RegexOptions.Singleline);
            if (matchTable.Success)
            {
                var tables = matchTable.Groups[1].Value.Split(',').Select(e => e.Trim()).ToList();
                SetTableInfo(tables);
                return resultText + "\r\n" + await ProcessMessage(userMessage);
            }

            var matchSql = Regex.Match(resultText, @"```sql\s(.*?)\s```", RegexOptions.Singleline);
            var sql = string.Empty;
            if (matchSql.Success)
            {
                sql = matchSql.Groups[1].Value;
                resultText = Regex.Replace(resultText, @"(?s)```sql.*?```", string.Empty, RegexOptions.IgnoreCase);
            }

            var dbParams = new List<DbParameterSetting>();
            var matchParams = Regex.Match(resultText, @"```schema\s*(\[.*?\])\s*```", RegexOptions.Singleline);
            if (matchParams.Success)
            {
                try
                {
                    dbParams = JsonConverterEx.DeserializeObject<List<DbParameterSetting>>(matchParams.Groups[1].Value) ?? new();
                }
                catch
                {
                    return "パラメータ定義(schema)の解析に失敗しました。もう一度作成を依頼してください。";
                }
                resultText = Regex.Replace(resultText, @"(?s)```schema.*?```", string.Empty, RegexOptions.IgnoreCase);
            }

            if (!string.IsNullOrEmpty(sql))
            {
                _editor.ApplySqlAndParameters(sql, dbParams);
                return string.Join(Environment.NewLine, resultText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
                    + "\r\n作成しました、ご確認お願いします。";
            }

            return resultText;
        }
    }
}
