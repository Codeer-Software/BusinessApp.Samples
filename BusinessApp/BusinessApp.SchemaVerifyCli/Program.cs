// 稼働 DB のスキーマ検証（ADR-0020 の -Verify）。
//
// 標準入力: 稼働 DB の sqlite_master（type / name / tbl_name / sql を持つ JSON 配列）。
//           migrate.ps1 が sql CLI で取り出して流し込む。
// 標準出力: 差分の一覧（無ければ一致した旨）。終了コード 0 = 一致 / 1 = ずれあり / 2 = 入力不正。
//
// 正解は Designer/ddl/ をインメモリ SQLite に再生して作る（Schema.Tests と同じ土台）。
// 比較は SchemaSnapshot（正規化テキストの完全一致）で、同値テストと同じ物差しを使う。
//
// 稼働 DB は他部品（認証の app_users など）と同居しているので、検証の管轄は
// 「正典が持つオブジェクト」と「正典のテーブルに付いているオブジェクト」に限る。
// 前者で欠け・定義違いを、後者で会計テーブルへの余分なトリガ・インデックスを捕まえ、
// 他部品のテーブルとその付属物には口を出さない（部品の境界。ADR-0029）。
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using BusinessApp.TestSupport;

Console.OutputEncoding = Encoding.UTF8;

string input;
using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
{
    input = reader.ReadToEnd();
}

List<LiveRow>? rows;
try
{
    rows = JsonSerializer.Deserialize<List<LiveRow>>(
        input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}
catch (JsonException e)
{
    Console.Error.WriteLine($"標準入力を sqlite_master の JSON 配列として読めない: {e.Message}");
    return 2;
}

if (rows is null)
{
    Console.Error.WriteLine("標準入力が空である。sqlite_master の JSON 配列を流し込むこと。");
    return 2;
}

using var canonical = TestDatabase.Create();
var expected = SchemaSnapshot.Dump(canonical);

var (actual, ignored) = SchemaSnapshot.SplitByJurisdiction(
    expected, rows.Select(r => (r.Type, r.Name, r.TblName, r.Sql)).ToList());

if (ignored.Count > 0)
{
    Console.WriteLine($"他部品のオブジェクト {ignored.Count} 件は管轄外として比較しない: {string.Join(", ", ignored)}");
}

var diff = SchemaSnapshot.Diff(expected, actual, "正典(Designer/ddl)", "稼働 DB").ToList();

// **表ごとのトリガの作られた順も見る**（発火順は仕様上 undefined。同値テストと同じ物差し。
// 行は migrate.ps1 が rowid 順で流す）。管轄は正典のテーブルに限る。
var canonTables = expected.Where(o => o.Type == "table").Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
var expectedOrder = SchemaSnapshot.TriggerOrder(canonical);
var actualOrder = SchemaSnapshot.TriggerOrderFromRows(rows.Select(r => (r.Type, r.Name, r.TblName)), canonTables);
foreach (var (e, a) in expectedOrder.Zip(actualOrder))
{
    if (e != a)
    {
        diff.Add($"トリガの作られた順が違う: 正典 [{e}] / 稼働 DB [{a}]");
    }
}

if (expectedOrder.Count != actualOrder.Count)
{
    diff.Add($"トリガを持つ表の数が違う: 正典 {expectedOrder.Count} / 稼働 DB {actualOrder.Count}");
}

if (diff.Count == 0)
{
    Console.WriteLine($"一致: 稼働 DB のスキーマは Designer/ddl と同値である（{expected.Count} オブジェクト。トリガの順も同じ）。");
    return 0;
}

Console.WriteLine($"ずれが {diff.Count} 件ある。");
foreach (var line in diff)
{
    Console.WriteLine(line);
}

return 1;

internal sealed record LiveRow(
    string Type,
    string Name,
    [property: JsonPropertyName("tbl_name")] string TblName,
    string? Sql);
