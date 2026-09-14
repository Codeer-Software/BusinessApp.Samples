namespace BusinessApp.Schema.Tests;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.ServerSupport;
using BusinessApp.TestSupport;

/// <summary>
/// <b>文字の欄の上限を、DDL が最後に止める</b>（マスタと取引先は docs/12 §2-2、伝票は docs/10 §4-2-1）。
/// </summary>
/// <remarks>
/// <para><b>関門が本体で、ここは最後の守り</b>である（コードの 20 文字と同じ 2 段）。
/// <b>アプリを迂回する経路</b>（CLI・取込・手打ちの SQL）にも効かせるために置く。</para>
/// <para><b>3 者の数が一致することは <see cref="FieldLengthConsistencyTests"/> が見る。</b>
/// ここが見るのは<b>振る舞い</b>——同じ値を C# と DDL が同じように扱うか、である。</para>
/// <para><b>20 本のトリガを 1 本残らず撃つ。</b> 掃引（<c>knockout.ps1</c>）は
/// <c>FieldLengthConsistencyTests</c> を殺し手から外している（定義文を読むだけで
/// 振る舞いを見ていないから）ので、<b>ここで撃たないトリガは誰も見張っていない</b>
/// ——自己レビューのラウンド 94 で、8 本が素通りだと 2 人に指摘された。</para>
/// </remarks>
public class TextLengthGuardTests
{
    /// <summary>
    /// 上限を持つ 10 の列と、行を 1 本入れるのに要る他の列。
    /// </summary>
    /// <remarks>
    /// <b>母数は <see cref="FieldLengthConsistencyTests.TextLengthFields"/> と対になる。</b>
    /// 数が食い違ったら <see cref="トリガは母数のぶんだけある"/> が赤くなる。
    /// </remarks>
    public static TheoryData<string, string, string, string, int, bool, string> Columns => new()
    {
        { "accounts", "name", "code, category", "'X1', 'asset'", MasterTextLength.MasterName, false, "" },
        { "sub_accounts", "name", "account_id, code", "1, 'X1'", MasterTextLength.MasterName, false, "" },
        { "departments", "name", "code", "'X1'", MasterTextLength.MasterName, false, "" },
        { "tax_categories", "name", "code, taxation_type", "'X1', 'out_of_scope'", MasterTextLength.MasterName, false, "" },
        { "fiscal_years", "label", "code, start_date, end_date, status", "'X1', '2030-04-01', '2031-03-31', 'open'", MasterTextLength.MasterName, false, "" },
        { "partners", "name", "code", "'X1'", MasterTextLength.PartnerName, false, "" },
        { "partners", "name_kana", "code, name", "'X1', '検証'", MasterTextLength.PartnerName, true, "" },
        { "partners", "address", "code, name", "'X1', '検証'", MasterTextLength.Address, true, "" },
        {
            "journal_entries", "description",
            "fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at",
            "1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00'",
            JournalLineRules.TextMaxLength, true, ""
        },
        {
            "journal_lines", "item_description",
            "journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id",
            "1, 1, 'debit', 1, 100, 1",
            JournalLineRules.TextMaxLength, true, EntryForLines
        },
    };

    /// <summary>明細の親になる下書きを 1 本作る。</summary>
    /// <remarks>
    /// <b><see cref="SchemaSeed.Create"/> はマスタしか入れない</b>ので、
    /// 明細を撃つ検体は親を自分で用意する。
    /// <b><see cref="SchemaSeed.Draft"/> は明細まで入れてしまう</b>ので使えない
    /// ——行番号がぶつかる。
    /// </remarks>
    private const string EntryForLines =
        "INSERT INTO journal_entries"
        + " (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)"
        + " VALUES (1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');";

    /// <summary>検体の土台。マスタと、必要なら親の行。</summary>
    private static Microsoft.Data.Sqlite.SqliteConnection Prepared(string setup)
    {
        var db = SchemaSeed.Create();
        if (setup.Length > 0)
        {
            TestDatabase.Execute(db, setup);
        }

        return db;
    }

    /// <summary>上限ちょうどは通り、1 文字超えると断られる（<b>追加</b>）。</summary>
    /// <remarks>
    /// <b>両端を撃つ。</b> 片側だけだと <c>&gt;</c> と <c>&gt;=</c> の取り違えが見えない。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Columns))]
    public void 追加は上限ちょうどが通り一文字超えると断られる(
        string table, string column, string others, string values, int max, bool nullable, string setup)
    {
        _ = nullable;
        using var db = Prepared(setup);
        var insert = $"INSERT INTO {table} ({others}, {column}) VALUES ({values}, ";

        TestDatabase.Execute(db, $"{insert}'{new string('あ', max)}');");

        // **入れた行を消してから 2 件目を撃つ。** 残したままだと、
        // **一意の制約が先に鳴って、長さのトリガまで届かない**（同じコードや行番号になるため）。
        TestDatabase.Execute(db, $"DELETE FROM {table} WHERE rowid = (SELECT MAX(rowid) FROM {table});");

        Rejected.ByTrigger(
            db, $"{insert}'{new string('あ', max + 1)}');", $"は {max} 文字以内。", $"{table}.{column}");
    }

    /// <summary>直すときも同じ上限で断られる（<b>更新</b>）。</summary>
    /// <remarks>
    /// <b>追加と更新の 2 本 1 組である。</b> 片方だけ置くと、
    /// <b>作るときは断られるのに、直すときは通る</b>——入口が 1 つ開いたままになる。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Columns))]
    public void 更新も上限ちょうどが通り一文字超えると断られる(
        string table, string column, string others, string values, int max, bool nullable, string setup)
    {
        _ = nullable;
        using var db = Prepared(setup);

        // **狙う列も埋めて行を作る。** 名前は `NOT NULL` なので、空のまま行は作れない。
        TestDatabase.Execute(db, $"INSERT INTO {table} ({others}, {column}) VALUES ({values}, 'あ');");

        var where = $"WHERE rowid = (SELECT MAX(rowid) FROM {table})";
        TestDatabase.Execute(db, $"UPDATE {table} SET {column} = '{new string('あ', max)}' {where};");

        Rejected.ByTrigger(
            db,
            $"UPDATE {table} SET {column} = '{new string('あ', max + 1)}' {where};",
            $"は {max} 文字以内。",
            $"{table}.{column}");
    }

    /// <summary><c>NULL</c> と空文字は長さの話ではない（<b>どちらも通る</b>）。</summary>
    /// <remarks>
    /// <b>必須かどうかは別の守りが見る</b>（画面の <c>IsRequired</c> と DB の <c>NOT NULL</c>）。
    /// <b>トリガの <c>WHEN NEW.&lt;列&gt; IS NOT NULL</c> がここを通す形になっている</b>ので、
    /// <b>その条件を消したら赤くなる</b>ようにしておく。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Columns))]
    public void NULLと空文字は長さの守りを通る(
        string table, string column, string others, string values, int max, bool nullable, string setup)
    {
        _ = max;
        using var db = Prepared(setup);
        TestDatabase.Execute(db, $"INSERT INTO {table} ({others}, {column}) VALUES ({values}, 'あ');");

        var where = $"WHERE rowid = (SELECT MAX(rowid) FROM {table})";
        TestDatabase.Execute(db, $"UPDATE {table} SET {column} = '' {where};");
        Assert.Equal(
            string.Empty,
            TestDatabase.ScalarOf<string>(db, $"SELECT {column} FROM {table} {where}"));

        // **NOT NULL の列には NULL を入れられない**ので、入れられる列だけを撃つ。
        // **どちらかは母数が持つ**——ここで `pragma_table_info` を読むと、
        // このテストが「スキーマの字面を読むテスト」になり、掃引の殺し手から外れてしまう。
        if (nullable)
        {
            TestDatabase.Execute(db, $"UPDATE {table} SET {column} = NULL {where};");
            Assert.Null(TestDatabase.ScalarOf<string?>(db, $"SELECT {column} FROM {table} {where}"));
        }
    }

    /// <summary>
    /// <b>BLOB は数えられないので断る。</b>
    /// </summary>
    /// <remarks>
    /// <c>LENGTH()</c> は BLOB では<b>バイト数</b>を返す。
    /// <b>短い BLOB は上限に当たらずに素通りし、TEXT の列にそのまま残る</b>
    /// （010 が自然キーで塞いだのと同じ穴）。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Columns))]
    public void BLOBは断られる(
        string table, string column, string others, string values, int max, bool nullable, string setup)
    {
        _ = max;
        _ = nullable;
        using var db = Prepared(setup);

        Rejected.ByTrigger(
            db,
            $"INSERT INTO {table} ({others}, {column}) VALUES ({values}, x'41');",
            "に使えない字が入っている。",
            $"{table}.{column}");
    }

    /// <summary>
    /// <b>NUL を混ぜて上限をすり抜けられない。</b>
    /// </summary>
    /// <remarks>
    /// <para><c>LENGTH()</c> は<b>途中の U+0000 で止まる</b>ので、
    /// <b>NUL の後ろにいくら続けても短く見える</b>（008 が同じ穴を塞いでいる）。</para>
    /// <para><b>検体は「見える側が上限ちょうど」にする。</b> ここが穴の底である——
    /// <c>バイト数 &gt; 4 × 符号点</c> の見立てだけで守っていたとき、
    /// <b>見える側が長いほど隠せる量が増える</b>ので通ってしまった
    /// （<c>'あ'×30 || NUL || 'い'×40</c> のような<b>短い可視側</b>だけを撃つと、
    /// 穴を避けたまま緑になる。自己レビューのラウンド 94）。</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Columns))]
    public void NULを混ぜて上限をすり抜けられない(
        string table, string column, string others, string values, int max, bool nullable, string setup)
    {
        _ = nullable;
        using var db = Prepared(setup);

        // **見える側は上限ちょうど**（`LENGTH()` からは合法に見える）。隠す側はその 3 倍。
        var hidden = System.Text.Encoding.UTF8.GetBytes(
            new string('a', max) + "\0" + new string('b', max * 3));

        Rejected.ByTrigger(
            db,
            $"INSERT INTO {table} ({others}, {column})"
            + $" VALUES ({values}, CAST(x'{Convert.ToHexString(hidden)}' AS TEXT));",
            "に使えない字が入っている。",
            $"{table}.{column}");
    }

    /// <summary>
    /// <b>基本多言語面の外の字も 1 文字と数える</b>（docs/12 §2-2 の「符号点で数える」）。
    /// </summary>
    /// <remarks>
    /// <para><b>ここが C# と DDL のいちばん危ない食い違いである。</b>
    /// <c>string.Length</c> は UTF-16 の符号単位を数えるので <b>🙂 を 2 と数える</b>が、
    /// SQLite の <c>LENGTH()</c> は <b>1 と数える</b>。
    /// <b>片方だけで実装すると、画面が断った名前を DB が通す</b>（あるいはその逆）。</para>
    /// <para><b>両側を同じ値で撃って、同じ答えになることを見る。</b></para>
    /// </remarks>
    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void 基本多言語面の外の字も一文字と数える(int count, bool accepted)
    {
        using var db = SchemaSeed.Create();
        var name = string.Concat(Enumerable.Repeat("\U0001F642", count));

        // **C# の側**（関門が使う数え方）。
        Assert.Equal(count, MasterTextLength.Count(name));
        Assert.Equal(
            accepted,
            MasterTextLength.DescribeProblem("科目名", name, MasterTextLength.MasterName) is null);

        // **DDL の側**。同じ値で同じ答えになる。
        var insert = $"INSERT INTO accounts (code, category, name) VALUES ('X1', 'asset', '{name}');";
        if (accepted)
        {
            TestDatabase.Execute(db, insert);
            Assert.Equal(
                (long)count,
                TestDatabase.ScalarOf<long>(db, "SELECT LENGTH(name) FROM accounts WHERE code = 'X1'"));
        }
        else
        {
            Rejected.ByTrigger(db, insert, $"は {MasterTextLength.MasterName} 文字以内。");
        }
    }

    /// <summary>
    /// <b>関門が断る値は DDL も断り、関門が通す値は DDL も通す</b>（qa/03 の L-14）。
    /// </summary>
    /// <remarks>
    /// <b>受理する集合がずれると、片方だけが通す穴になる。</b>
    /// 関門が広ければ利用者に定型文が出て（L-28）、DDL が広ければ守りが 1 層に減る。
    /// </remarks>
    [Theory]
    [InlineData("あ", true)]
    [InlineData("  あ  ", true)]            // 前後の空白は落とす
    [InlineData("あ い", true)]             // 字の間の空白は落とさない
    [InlineData("あ　い", true)]            // 全角の空白も同じ
    [InlineData("あ\0い", false)]           // NUL は両方が断る
    public void 関門と_DDL_が同じ値に同じ答えを返す(string value, bool accepted)
    {
        using var db = SchemaSeed.Create();
        var normalized = MasterTextLength.Normalize(value);
        var gate = MasterTextLength.DescribeProblem("科目名", normalized, MasterTextLength.MasterName) is null;

        Assert.Equal(accepted, gate);

        var hex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(normalized!));
        var insert = $"INSERT INTO accounts (code, category, name) VALUES ('X1', 'asset', CAST(x'{hex}' AS TEXT));";
        if (accepted)
        {
            TestDatabase.Execute(db, insert);
        }
        else
        {
            Rejected.ByTrigger(db, insert, "「科目名」", value);
        }
    }

    /// <summary>
    /// <b>伝票の側も、関門と DDL が同じ値に同じ答えを返す</b>（qa/03 の L-14）。
    /// </summary>
    /// <remarks>
    /// <b>上のマスタ側と同じ形を、<c>JournalLineRules</c> と 013 のトリガで通す。</b>
    /// <b>規則が 2 つの型に分かれている</b>（純粋層は <c>ServerSupport</c> を参照できない。docs/22）ので、
    /// <b>片方だけ直したときに気づける相手がここしかない</b>。
    /// </remarks>
    [Theory]
    [InlineData("あ", true)]
    [InlineData("  あ  ", true)]            // 前後の空白は落とす
    [InlineData("あ い", true)]             // 字の間の空白は落とさない
    [InlineData("あ\0い", false)]           // NUL は両方が断る
    public void 伝票でも関門と_DDL_が同じ値に同じ答えを返す(string value, bool accepted)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, EntryForLines);

        var normalized = value.Trim();
        var gate = JournalLineRules.CountCharacters(normalized) <= JournalLineRules.TextMaxLength
                   && !normalized.Contains('\0');

        Assert.Equal(accepted, gate);

        var hex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(normalized));
        var update = "UPDATE journal_entries SET description = CAST(x'" + hex + "' AS TEXT)"
                     + " WHERE rowid = (SELECT MAX(rowid) FROM journal_entries);";
        if (accepted)
        {
            TestDatabase.Execute(db, update);
        }
        else
        {
            Rejected.ByTrigger(db, update, "「摘要」", value);
        }
    }

    /// <summary>
    /// <b>伝票の上限も、符号点で数える</b>（マスタ側と同じ判断）。
    /// </summary>
    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void 伝票の摘要も基本多言語面の外の字を一文字と数える(int count, bool accepted)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, EntryForLines);
        var text = string.Concat(Enumerable.Repeat("\U0001F642", count));

        Assert.Equal(count, JournalLineRules.CountCharacters(text));

        var where = "WHERE rowid = (SELECT MAX(rowid) FROM journal_entries)";
        var update = $"UPDATE journal_entries SET description = '{text}' {where};";
        if (accepted)
        {
            TestDatabase.Execute(db, update);
            Assert.Equal(
                (long)count,
                TestDatabase.ScalarOf<long>(db, $"SELECT LENGTH(description) FROM journal_entries {where}"));
        }
        else
        {
            Rejected.ByTrigger(db, update, $"は {JournalLineRules.TextMaxLength} 文字以内。");
        }
    }

    /// <summary>
    /// <b>トリガは母数のぶんだけある</b>（10 列 × 追加・更新）。
    /// </summary>
    /// <remarks>
    /// <b>母数が 2 つに割れていないことを見る。</b> <see cref="Columns"/> と
    /// <see cref="FieldLengthConsistencyTests.TextLengthFields"/> は別々に書いてあるので、
    /// <b>片方に足してもう片方を忘れると、その列だけ誰も撃たない</b>。
    /// </remarks>
    [Fact]
    public void トリガは母数のぶんだけある()
    {
        using var db = SchemaSeed.Create();

        Assert.Equal(
            FieldLengthConsistencyTests.TextLengthFields.Select(row => $"{row[1]}.{row[3]}")
                .OrderBy(x => x, StringComparer.Ordinal),
            Columns.Select(row => $"{row[0]}.{row[1]}").OrderBy(x => x, StringComparer.Ordinal));

        Assert.Equal(
            (long)(Columns.Count() * 2),
            TestDatabase.ScalarOf<long>(
                db,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger'"
                + " AND name LIKE 'trg_%_length_%'"));
    }
}
