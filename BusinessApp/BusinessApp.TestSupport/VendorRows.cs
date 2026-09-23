namespace BusinessApp.TestSupport;

using System.Text;

using Microsoft.Data.Sqlite;

/// <summary>
/// <b>ベンダーが配る行（制度ルール）の同値検査</b>（ADR-0020 の宿題・ADR-0069）。
/// </summary>
/// <remarks>
/// <para><b>スキーマの網では足りない。</b> 制度ルールは<b>行が本体</b>である——
/// 表の形が合っていても、<b>稼働 DB の控除割合が正典と違えば、仕訳の税額が静かに狂う</b>。
/// <c>migrate.ps1 -Verify</c> は「未適用のマイグレーションが残っていないか」までしか見ておらず、
/// <b>書き込まれた行の中身は誰も確かめていなかった</b>（ADR-0020 の帰結が「フェーズ 3 で入れる」とした当のもの）。</para>
/// <para><b>見るのはこの一覧に載せた表だけである。</b> 勘定科目・部門のように<b>利用者が編集するマスタ</b>は、
/// 初期値こそベンダーが配るが<b>その後は利用者のもの</b>なので、正典と違って当たり前である——
/// <b>網を広げると、正常な運用のたびに赤くなる網になる</b>。</para>
/// <para><b>税区分は「利用者のもの」だからではなく、編集させるかが未決だから載せない</b>
/// （docs/12 の税区分の行——「経理責任者（当面。<b>利用者に編集させるか自体が未決</b>）」。docs/04 §5）。
/// <b>決まったら、そのときに載せるかを決める。</b></para>
/// <para><b>比べるのは意味の列だけ。</b> 代理キーと監査列は、入れた順や時刻で変わる——
/// <b>新しく作った DB では 1 から、配達で足した DB では続きから</b> <c>id</c> が振られる。
/// そこを比べると、中身が同じでも毎回ずれる。</para>
/// </remarks>
public static class VendorRows
{
    /// <summary>
    /// <b>行までベンダーが配る表。</b> ここに載せた表だけが同値検査の対象になる。
    /// </summary>
    /// <remarks>
    /// <b>載せるのは「利用者が 1 行も足さず、1 文字も直さない表」だけ</b>である。
    /// 迷ったら載せない——<b>載せ忘れは「見ていない」だが、載せ過ぎは「正常な運用で赤くなる」</b>で、
    /// 後者は網そのものを無視させる。
    /// </remarks>
    public static IReadOnlyList<string> Tables { get; } = ["transition_purchase_rates", "tax_rates"];

    /// <summary>比べない列（代理キーと監査列）。</summary>
    public static IReadOnlyCollection<string> IgnoredColumns { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "created_at", "updated_at", "creator", "updater", "optimistic_locking",
        };

    private const string NullText = "(null)";

    /// <summary>
    /// 行を 1 本のテキストへ畳む SQL を組む。<b>稼働 DB へは PowerShell が流す</b>
    /// （接続文字列は <c>sql</c> CLI の中にあり、C# からは開けない）。
    /// </summary>
    /// <remarks>
    /// <b>列の並びはスキーマから採る</b>——書き写すと、列を足した回に写しだけが古くなる。
    /// <b>スキーマのずれとは独立に流す</b>（<c>migrate.ps1 -Verify</c> は、スキーマが違っていても行の検査を流す）
    /// ——<b>列の過不足はスキーマ側が言う</b>ので、ここは「正典の列で読んだ行」を比べるだけでよい。
    /// </remarks>
    public static string DumpSql(SqliteConnection schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        // **空の一覧で「SQL らしきもの」を返さない。** 返すと呼び出し側の
        // 「空なら止める」が死ぬ（改行と ORDER BY だけの字が返り、空文字にはならない）。
        if (Tables.Count == 0)
        {
            throw new InvalidOperationException(
                "ベンダーが配る行を持つ表が 1 つも登録されていない（VendorRows.Tables）。");
        }

        var selects = new List<string>();
        foreach (var table in Tables)
        {
            var columns = MeaningfulColumns(schema, table);
            if (columns.Count == 0)
            {
                // **いちばん起きやすいのは綴り違いである。** pragma_table_info は無い表に 0 行を返すので、
                // 「列が無い」と「表が無い」が同じ顔になる——先に言い分ける。
                var exists = TestDatabase.Query(
                    schema,
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
                    .Contains(table, StringComparer.Ordinal);
                throw new InvalidOperationException(
                    exists
                        ? $"{table}: 比べる列が 1 つも無い。IgnoredColumns が広すぎる。"
                        : $"{table}: この名前の表がスキーマに無い（VendorRows.Tables の綴りを確かめる）。");
            }

            var parts = columns.Select(column =>
                $"'{column}=' || COALESCE(CAST({column} AS TEXT), '{NullText}')");
            selects.Add(
                $"SELECT '{table} | ' || {string.Join(" || ' | ' || ", parts)} AS row FROM {table}");
        }

        return string.Join("\nUNION ALL\n", selects) + "\nORDER BY 1;";
    }

    /// <summary>その DB のベンダー行を、並びを固定したテキストで返す。</summary>
    public static IReadOnlyList<string> Dump(SqliteConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return TestDatabase.Query(db, DumpSql(db));
    }

    /// <summary>
    /// 2 つの取り出しの差を、<b>どちらに何があるか</b>まで書いて返す。差が無ければ空。
    /// </summary>
    public static IReadOnlyList<string> Diff(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string expectedName,
        string actualName)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        // **出現回数まで見る**（多重集合）。含むかどうかだけで比べると、
        // **同じ行が片側に 2 本あっても差 0 件**になる——いまの表は一意制約があるので重複しないが、
        // **次に載せる表がそうとは限らない**。
        var left = Count(expected);
        var right = Count(actual);
        var diff = new List<string>();

        foreach (var row in left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal)
                     .OrderBy(row => row, StringComparer.Ordinal))
        {
            var inLeft = left.GetValueOrDefault(row);
            var inRight = right.GetValueOrDefault(row);
            if (inLeft == inRight)
            {
                continue;
            }

            diff.Add(inRight == 0 ? $"{actualName} に無い: {row}"
                : inLeft == 0 ? $"{expectedName} に無い: {row}"
                : $"数が違う（{expectedName} {inLeft} / {actualName} {inRight}）: {row}");
        }

        return diff;
    }

    /// <summary>
    /// 突き合わせの<b>報告そのもの</b>——印字する行と終了コードを組で返す。
    /// </summary>
    /// <remarks>
    /// <para><b>判定だけを検体に載せても足りない</b>——<b>印字する側がそれを捨てれば誰も気づかない</b>
    /// （<c>self-review</c> スキル §9 の 5）。<b>だから報告をここへ引き出して、検体で字ごと固定する。</b>
    /// 実行ファイル（<c>BusinessApp.SchemaVerifyCli</c>）はこれを印字して終了コードを返すだけにする。</para>
    /// <para><b>名乗りは数ではなく表の名前でする</b>（同 §9 の 6）——
    /// 「1 表・4 行」だけだと、<b>別の表に差し替えても行を差し替えても報告が 1 字も変わらない</b>。</para>
    /// </remarks>
    public static (int ExitCode, IReadOnlyList<string> Lines) Report(
        IReadOnlyList<string> canonical,
        IEnumerable<string> live)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(live);

        // **空回りを「一致」と言わない。** 差は 0 件でも「見ていない」ことがある。
        var vacuous = Vacuous(canonical);
        if (vacuous.Count > 0)
        {
            return (1, vacuous);
        }

        var diff = Diff(canonical, live, CanonicalName, LiveName);
        if (diff.Count == 0)
        {
            return (0, [$"一致: ベンダーが配る行は正典と同値である（{string.Join("・", Tables)}。{canonical.Count} 行）。"]);
        }

        return (1, [$"行のずれが {diff.Count} 件ある。", .. diff]);
    }

    /// <summary>報告で正典を指す名。</summary>
    public const string CanonicalName = "正典(Designer/ddl + Designer/seed)";

    /// <summary>報告で稼働 DB を指す名。</summary>
    public const string LiveName = "稼働 DB";

    private static Dictionary<string, int> Count(IEnumerable<string> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            counts[row] = counts.GetValueOrDefault(row) + 1;
        }

        return counts;
    }

    /// <summary>その表の、比べる列（宣言順）。</summary>
    public static IReadOnlyList<string> MeaningfulColumns(SqliteConnection schema, string table)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return TestDatabase
            .Query(schema, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid")
            .Where(name => !IgnoredColumns.Contains(name))
            .ToList();
    }

    /// <summary>
    /// <b>網が空回りしていないこと。</b> 一覧が空でも、表に 1 行も無くても、
    /// 差は必ず 0 件になる——<b>「問題なし」と「見ていない」が同じ顔をする</b>。
    /// </summary>
    /// <returns>空回りしていれば、その理由。していなければ空。</returns>
    public static IReadOnlyList<string> Vacuous(IEnumerable<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var problems = new List<string>();
        if (Tables.Count == 0)
        {
            problems.Add("ベンダーが配る行を持つ表が 1 つも登録されていない（VendorRows.Tables）。");
        }

        var dumped = rows.ToList();
        var builder = new StringBuilder();
        foreach (var table in Tables)
        {
            if (!dumped.Any(row => row.StartsWith($"{table} | ", StringComparison.Ordinal)))
            {
                builder.Append(table).Append(' ');
            }
        }

        if (builder.Length > 0)
        {
            problems.Add($"行が 1 つも取れていない表がある: {builder.ToString().TrimEnd()}");
        }

        return problems;
    }
}
