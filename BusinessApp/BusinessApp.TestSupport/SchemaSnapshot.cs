namespace BusinessApp.TestSupport;

using System.Text;

using Microsoft.Data.Sqlite;

/// <summary>スキーマオブジェクト 1 つ。<see cref="Sql"/> は正規化済みの定義文。</summary>
public sealed record SchemaObject(string Type, string Name, string Sql);

/// <summary>
/// <c>sqlite_master</c> を正規化して突き合わせる。マイグレーションの同値検査
/// （<c>Schema.Tests</c>）と稼働 DB の検証（<c>SchemaVerifyCli</c> ＝ <c>migrate.ps1 -Verify</c>）が
/// 同じ比較を使う（ADR-0020）。
/// </summary>
/// <remarks>
/// <para><b>比較は「コメント除去＋空白正規化したテキストの完全一致」である。</b>
/// SQLite は全オブジェクトの定義文を <c>sqlite_master.sql</c> にテキストのまま持ち、
/// 起動のたびにそれを読み直すので、このテキストがスキーマの実体そのものである。
/// 構造（pragma）比較にしないのは、pragma には CHECK 制約が載らず、
/// 一番守りたいもの（[ddl/README](../../Designer/ddl/README.md) の二重防御）が穴になるため。</para>
/// <para>テキスト一致が成り立つのは、マイグレーションに次の規約があるからである
/// （[migrations/README](../../Designer/migrations/README.md)）。
/// ①列の追加は正典でも列リストの末尾に置く（SQLite の <c>ADD COLUMN</c> は
/// 新しい列を「最後の列の後・テーブル制約の前」に挿入する。2026-08-25 実測）
/// ②それ以外の形の変更は、正典の定義文をそのまま写した作り直しで行う。
/// 規約から外れると同値テストが落ちるので、規約は機械が守る。</para>
/// </remarks>
public static class SchemaSnapshot
{
    /// <summary>開いている接続のスキーマを正規化して返す。</summary>
    public static IReadOnlyList<SchemaObject> Dump(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master";
        using var reader = command.ExecuteReader();

        var rows = new List<(string, string, string?)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return FromRows(rows);
    }

    /// <summary>
    /// <c>sqlite_master</c> の行から比較対象を作る。SQLite の内部オブジェクト
    /// （<c>sqlite_autoindex_*</c> 等。定義文を持たない）と、ランナーが管理する
    /// <c>schema_migrations</c> テーブル（正典の外にあるのが正しい）は比較しない。
    /// 除外は<b>テーブルに限る</b>。名前だけで除くと、同名を付けたトリガ等が検査の死角になる。
    /// </summary>
    public static IReadOnlyList<SchemaObject> FromRows(IEnumerable<(string Type, string Name, string? Sql)> rows)
        => rows
            .Where(r => r.Sql is not null
                && !r.Name.StartsWith("sqlite_", StringComparison.Ordinal)
                && !(r.Type == "table" && r.Name == "schema_migrations"))
            .Select(r => new SchemaObject(r.Type, r.Name, NormalizeSql(r.Sql!)))
            .OrderBy(o => o.Type, StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// コメントを除き、引用の外の連続する空白を 1 つに潰す。<c>,</c> <c>)</c> <c>;</c> の前と
    /// <c>(</c> の後の空白は 0 個にする（SQLite の ALTER TABLE ADD COLUMN は
    /// 「<c>DEFAULT 0\n)</c>」の改行位置に「<c>, 列</c>」を挿し込むので、
    /// 正典の「<c>DEFAULT 0,</c>」と<b>カンマの前の空白だけが違う</b>テキストになる。2026-08-25 実測）。
    /// 引用の中（文字列・引用付き識別子）は変えない。
    /// 正典の整形だけを変える編集（コメント・改行・字下げ）を、スキーマの差と誤認しないため。
    /// </summary>
    public static string NormalizeSql(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var pendingSpace = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                pendingSpace = true;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, sql.Length);
                pendingSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                i++;
                continue;
            }

            if (pendingSpace && result.Length > 0 && c is not (',' or ')' or ';') && result[^1] != '(')
            {
                result.Append(' ');
            }

            pendingSpace = false;

            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                result.Append(c);
                i++;
                while (i < sql.Length)
                {
                    result.Append(sql[i]);
                    if (sql[i] == close)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// 稼働 DB の行を「会計コアの管轄」と「他部品（管轄外）」に分ける。
    /// 管轄は、正典が持つオブジェクトと、正典のテーブルに付いているオブジェクト
    /// （余分なトリガ・インデックスを検出するため）。他部品のテーブルとその付属物には口を出さない
    /// （部品の境界。ADR-0002）。
    /// </summary>
    public static (IReadOnlyList<SchemaObject> InScope, IReadOnlyList<string> Ignored) SplitByJurisdiction(
        IReadOnlyList<SchemaObject> expected,
        IReadOnlyList<(string Type, string Name, string TblName, string? Sql)> rows)
    {
        // 付属物（tbl_name）の親になれるのはテーブルとビュー（ビューは INSTEAD OF トリガの親）。
        var canonParents = expected.Where(o => o.Type is "table" or "view").Select(o => o.Name).ToHashSet();
        var expectedKeys = expected.Select(o => (o.Type, o.Name)).ToHashSet();

        var inScopeRows = rows
            .Where(r => canonParents.Contains(r.TblName) || expectedKeys.Contains((r.Type, r.Name)))
            .ToList();
        var ignored = rows.Except(inScopeRows)
            .Where(r => r.Sql is not null
                && !r.Name.StartsWith("sqlite_", StringComparison.Ordinal)
                && !(r.Type == "table" && r.Name == "schema_migrations"))
            .Select(r => $"{r.Type} {r.Name}")
            .ToList();

        return (FromRows(inScopeRows.Select(r => (r.Type, r.Name, r.Sql))), ignored);
    }

    /// <summary>2 つのスキーマの差を人が読める行で返す。空なら同値。</summary>
    public static IReadOnlyList<string> Diff(
        IReadOnlyList<SchemaObject> expected,
        IReadOnlyList<SchemaObject> actual,
        string expectedLabel,
        string actualLabel)
    {
        var differences = new List<string>();
        var actualByKey = actual.ToDictionary(o => (o.Type, o.Name));

        foreach (var exp in expected)
        {
            if (!actualByKey.TryGetValue((exp.Type, exp.Name), out var act))
            {
                differences.Add($"{actualLabel} に無い: {exp.Type} {exp.Name}");
            }
            else if (exp.Sql != act.Sql)
            {
                differences.Add(
                    $"定義が違う: {exp.Type} {exp.Name}\n  {expectedLabel}: {exp.Sql}\n  {actualLabel}: {act.Sql}");
            }
        }

        var expectedKeys = expected.Select(o => (o.Type, o.Name)).ToHashSet();
        foreach (var act in actual)
        {
            if (!expectedKeys.Contains((act.Type, act.Name)))
            {
                differences.Add($"{expectedLabel} に無い: {act.Type} {act.Name}");
            }
        }

        return differences;
    }
}
