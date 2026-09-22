namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

/// <summary>
/// 開発・デモ専用の初期データ（<c>Designer/seed/dev/</c>）の検査（ADR-0039）。
/// </summary>
/// <remarks>
/// <para><b>これを実行するものが何も無かった</b>（2026-08-31 の自己レビュー R28-13）。
/// 構文も列名も役割の値も誰も見ておらず、<c>'staf'</c> と書いても実機まで気づかない。
/// しかも <c>UPDATE</c> だけなので、<b>対象の利用者が居なければ 0 行で成功する</b>——
/// 流したかどうかすら分からない。</para>
/// <para><b>だから往復で見る。</b> 3 人を入れる → 流す → 読み戻す。
/// 「流れた」ではなく「意図した役割が付いた」ところまで表明する。</para>
/// <para><b>役割の値は DDL の <c>CHECK</c> が拒む。</b> 綴り違いはここで例外になる。</para>
/// </remarks>
public class DevSeedDataTests
{
    /// <summary>全利用者の役割を 1 列に畳んで並べる（<c>TestDatabase.Query</c> は 1 列目しか読まない）。</summary>
    private const string RolesQuery = """
        select user_name || '|' || coalesce(accounting_role, '') || '|' || coalesce(partner_role, '')
          from app_users order by user_name
        """;

    /// <summary>seed が名指ししている利用者と、付くはずの役割。</summary>
    /// <remarks>
    /// <b>期待値をここに書き写している。</b> seed から読むと、seed が壊れたときに期待値も一緒に壊れる。
    /// </remarks>
    public static TheoryData<string, long, long, string?, string?> ExpectedRoles() => new()
    {
        //  利用者識別名     can_access_app  is_sysadmin  accounting_role  partner_role
        { "soumu_ippan", 1, 0, "staff", "editor" },
        { "soumu_bucho", 1, 0, "manager", "editor" },
        { "admin", 1, 1, null, null },
    };

    [Theory]
    [MemberData(nameof(ExpectedRoles))]
    public void 開発用の役割が意図どおりに付く(
        string userName, long canAccessApp, long isSysadmin, string? accountingRole, string? partnerRole)
    {
        using var db = WithDevSeedApplied();

        // `TestDatabase.Query` は 1 列目しか読まないので、SQL で 1 列に畳む。
        var row = TestDatabase.Query(db,
            $"""
            select can_access_app || '|' || is_sysadmin
                   || '|' || coalesce(accounting_role, '(なし)')
                   || '|' || coalesce(partner_role, '(なし)')
              from app_users where user_name = '{userName}'
            """);

        Assert.Equal(
            [$"{canAccessApp}|{isSysadmin}|{accountingRole ?? "(なし)"}|{partnerRole ?? "(なし)"}"],
            row);
    }

    /// <summary>
    /// <b>何度流してもよい</b>（<c>UPDATE</c> だけなので冪等）。
    /// </summary>
    /// <remarks>
    /// seed 自身がそう書いている。開発機では手順を繰り返すことがあるので、書いたとおりを固定する。
    /// </remarks>
    [Fact]
    public void 二度流しても結果が変わらない()
    {
        using var db = WithDevSeedApplied();
        var once = TestDatabase.Query(db, RolesQuery);

        ApplyDevSeed(db);

        Assert.Equal(once, TestDatabase.Query(db, RolesQuery));
    }

    /// <summary>
    /// <b>アカウントは作らない。</b> ハッシュとソルトは CLB の <c>PasswordHashHelper</c> が作るもので、
    /// SQL では作れない（<c>_specs/Authentication.md</c>）。
    /// </summary>
    /// <remarks>
    /// ここが崩れると「seed を流せば利用者ができる」と読める運用になり、
    /// <b>ログインできない利用者</b>が生まれる。
    /// </remarks>
    [Fact]
    public void 利用者を新しく作らない()
    {
        using var db = TestDatabase.CreateWithSeed();
        var before = TestDatabase.ScalarOf<long>(db, "select count(*) from app_users");

        ApplyDevSeed(db);

        Assert.Equal(before, TestDatabase.ScalarOf<long>(db, "select count(*) from app_users"));
    }

    /// <summary>この検査が「1 本も流さず素通り」で緑にならないための土台（qa/03 L-15）。</summary>
    [Fact]
    public void 開発用の初期データを実際に見つけられている()
        => Assert.NotEmpty(TestDatabase.DevSeedFiles());

    // --- 前の年度（002_prior_fiscal_year.sql） ---

    /// <summary>
    /// <b>前の年度（第 17 期）が、月次の会計期間と採番ごと入る。</b>
    /// </summary>
    /// <remarks>
    /// <para>年度だけ足しても使えない——<b>期間が無ければ計上できず</b>（I-03）、
    /// <b>採番の行が無ければ伝票番号が採れない</b>（I-17）。3 つでひと組である。</para>
    /// <para><b>期待値はここに書き写す</b>（<see cref="ExpectedRoles"/> と同じ理由——
    /// seed から読むと、seed が壊れたときに期待値も一緒に壊れる）。</para>
    /// </remarks>
    [Fact]
    public void 前の年度が期間と採番ごと入る()
    {
        using var db = WithDevSeedApplied();

        Assert.Equal(
            ["FY17|第 17 期（2025 年度）|2025-04-01|2026-03-31|open|"],
            TestDatabase.Query(db,
                """
                select code || '|' || label || '|' || start_date || '|' || end_date
                       || '|' || status || '|' || coalesce(premium_ledger_from, '')
                  from fiscal_years where code = 'FY17'
                """));

        // **件数と両端では足りない。** 1 か月が重複して別の 1 か月が欠けても、
        // 件数も最小も最大も変わらない（2026-09-22 の自己レビュー。`self-review` スキル §9 の 6）。
        // **12 組の境界をそのまま並べて突き合わせる。**
        Assert.Equal(
            // **`status` も読む。** 閉じた期間には計上できない（`FiscalCalendar.IsPostable`）ので、
            // **`closed` になった瞬間に、この seed が用意している 3 つがどれも踏めなくなる**
            // ——SQL の冒頭がそう書いているのに、機械が 1 つも守っていなかった（2026-09-22 の自己レビュー）。
            [
                "2025-04-01|2025-04-30|open", "2025-05-01|2025-05-31|open", "2025-06-01|2025-06-30|open",
                "2025-07-01|2025-07-31|open", "2025-08-01|2025-08-31|open", "2025-09-01|2025-09-30|open",
                "2025-10-01|2025-10-31|open", "2025-11-01|2025-11-30|open", "2025-12-01|2025-12-31|open",
                "2026-01-01|2026-01-31|open", "2026-02-01|2026-02-28|open", "2026-03-01|2026-03-31|open",
            ],
            TestDatabase.Query(db,
                """
                select p.start_date || '|' || p.end_date || '|' || p.status
                  from accounting_periods p join fiscal_years y on y.id = p.fiscal_year_id
                 where y.code = 'FY17' order by p.start_date
                """));

        Assert.Equal(
            ["1"],
            TestDatabase.Query(db,
                """
                select s.next_entry_no from journal_entry_sequences s
                  join fiscal_years y on y.id = s.fiscal_year_id where y.code = 'FY17'
                """));
    }

    /// <summary>
    /// <b>年度は重ならない</b>——重なると、帳簿の並びの鍵（<c>date(fy.start_date)</c>）が
    /// 同じ日を 2 通りに読んで並びが不定になる（docs/04 §5 の未決事項）。
    /// </summary>
    /// <remarks>
    /// <b>機械で止めてはいない</b>（<c>fiscal_years</c> は <c>code</c> が一意なだけ）ので、
    /// <b>開発機に足す年度は、足す側が重ならないことを見る</b>。
    /// </remarks>
    [Fact]
    public void 足した年度は初期データの年度と重ならない()
    {
        using var db = WithDevSeedApplied();

        // **突き合わせた対の数も表明する。** 「重なりが 0 件」は、
        // **年度が 1 本しか無くても成り立つ**——`dev/002` を丸ごと消しても緑になってしまう
        // （2026-09-22 の自己レビュー。qa/03 L-46 の型）。
        Assert.Equal(
            ["1|0"],
            TestDatabase.Query(db,
                """
                select count(*) || '|' || sum(case
                         when date(a.start_date) <= date(b.end_date)
                          and date(b.start_date) <= date(a.end_date) then 1 else 0 end)
                  from fiscal_years a join fiscal_years b on a.id < b.id
                """));

        // **例外を投げるのは会計期間の重なりのほうである**（`FiscalCalendar` の ctor）。
        // 年度だけ見ていると、**重なった期間で計上も取消も保存も落ちる状態**を素通りさせる。
        Assert.Equal(
            ["276|0"],
            TestDatabase.Query(db,
                """
                select count(*) || '|' || sum(case
                         when date(a.start_date) <= date(b.end_date)
                          and date(b.start_date) <= date(a.end_date) then 1 else 0 end)
                  from accounting_periods a join accounting_periods b on a.id < b.id
                """));
    }

    /// <summary>
    /// <b>日付は <c>date()</c> で突き合わせる。</b> 時刻が付いていても二重に入れない。
    /// </summary>
    /// <remarks>
    /// <para><b>CLB は日付の列に時刻を付けて書く</b>（<c>2025-08-01 00:00:00</c>。qa/03 L-12）。
    /// <b>文字列一致で守ると、そこだけ守りが外れて 13 本になる</b>——
    /// 重複した瞬間に <c>FiscalCalendar</c> が「会計期間が重複している」で落ち、
    /// <b>計上も取消も保存もできなくなる</b>。</para>
    /// <para><b>UNIQUE (fiscal_year_id, start_date) は助けにならない</b>——
    /// あちらは<b>生の文字列</b>で見るので、形が違えば別の値として通る。</para>
    /// <para><b>seed 自身がこの理由を書いている</b>のに、2026-09-22 まで撃つものが無く、
    /// <c>date()</c> を外す変異でも全部緑だった（同日の自己レビュー）。</para>
    /// </remarks>
    [Fact]
    public void 時刻の付いた期間があっても二重に入れない()
    {
        using var db = WithDevSeedApplied();

        // 8 月の期間だけを、CLB が書く形（時刻つき）に書き換える。
        TestDatabase.Execute(db,
            """
            update accounting_periods
               set start_date = '2025-08-01 00:00:00', end_date = '2025-08-31 00:00:00'
             where fiscal_year_id = (select id from fiscal_years where code = 'FY17')
               and date(start_date) = date('2025-08-01')
            """);

        ApplyDevSeed(db);

        Assert.Equal(
            ["12"],
            TestDatabase.Query(db,
                """
                select count(*) from accounting_periods p
                  join fiscal_years y on y.id = p.fiscal_year_id where y.code = 'FY17'
                """));
    }

    /// <summary>
    /// <b>第 17 期の識別子は、第 18 期より大きい。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>この seed の値の 1 つは、「識別子の順と期間の順が逆」の形を開発機に作ることである</b>
    /// （qa/03 L-19 の型）。<c>FiscalCalendar.IsBeforeFiscalYear</c> の注記も、
    /// <b>開発機がその形であること</b>を根拠に「識別子の大小では決めない」と書いている。</para>
    /// <para><b>ファイル名を <c>000_</c> に変えるか、流す順を入れ替えると、開発機は静かにその形を失う。</b>
    /// 誰も気づかないまま、<b>識別子で比べる実装が実機でも通ってしまう</b>。</para>
    /// </remarks>
    [Fact]
    public void 前の年度の識別子は初期データの年度より大きい()
    {
        using var db = WithDevSeedApplied();

        Assert.Equal(
            ["FY18 < FY17"],
            TestDatabase.Query(db,
                """
                select case when
                         (select id from fiscal_years where code = 'FY18')
                         < (select id from fiscal_years where code = 'FY17')
                       then 'FY18 < FY17' else 'FY17 <= FY18' end
                """));
    }

    /// <summary>
    /// <b>途中まで入って落ちた状態から流し直すと、正しい姿に収束する。</b>
    /// </summary>
    /// <remarks>
    /// <para><c>sql</c> CLI はファイル全体をトランザクションで包まないので、
    /// <b>年度だけ入って落ちる・期間が途中まで入って落ちる</b>が起こりうる。
    /// <b>守りが 3 つとも独立していることを、実際に欠けた状態を作って確かめる</b>
    /// （2026-09-22 の自己レビュー。「二度流しても増えない」は満杯 → 満杯しか見ていない）。</para>
    /// <para><b>期間は 1 本だけ消す。</b> 全部消すと「1 本の INSERT が丸ごと空振りしていない」
    /// ことしか言えず、<b>欠けた 1 本だけを足せるか</b>が見えない。</para>
    /// </remarks>
    [Fact]
    public void 途中まで入って落ちた状態から流し直すと揃う()
    {
        const string Counts = """
            select (select count(*) from accounting_periods p
                      join fiscal_years y on y.id = p.fiscal_year_id where y.code = 'FY17')
                   || '|' || (select count(*) from journal_entry_sequences s
                      join fiscal_years y on y.id = s.fiscal_year_id where y.code = 'FY17')
            """;

        using var db = WithDevSeedApplied();
        Assert.Equal(["12|1"], TestDatabase.Query(db, Counts));

        // 8 月の期間と採番の行を落とす（＝そこまで入って落ちた状態）。
        TestDatabase.Execute(db,
            """
            delete from accounting_periods
             where fiscal_year_id = (select id from fiscal_years where code = 'FY17')
               and date(start_date) = date('2025-08-01');
            delete from journal_entry_sequences
             where fiscal_year_id = (select id from fiscal_years where code = 'FY17');
            """);
        Assert.Equal(["11|0"], TestDatabase.Query(db, Counts));

        ApplyDevSeed(db);

        Assert.Equal(["12|1"], TestDatabase.Query(db, Counts));
    }

    /// <summary>
    /// <b>二度流しても増えない</b>（年度・期間・採番のどれも）。
    /// </summary>
    /// <remarks>
    /// <see cref="二度流しても結果が変わらない"/> が見ているのは役割だけで、
    /// <b>行を足す seed はそちらでは捕まらない</b>——一意制約に当たらない行は黙って二重に入る。
    /// </remarks>
    [Fact]
    public void 二度流しても年度と期間と採番が増えない()
    {
        const string Counts = """
            select (select count(*) from fiscal_years)
                   || '|' || (select count(*) from accounting_periods)
                   || '|' || (select count(*) from journal_entry_sequences)
            """;

        using var db = WithDevSeedApplied();
        var once = TestDatabase.Query(db, Counts);

        ApplyDevSeed(db);

        Assert.Equal(once, TestDatabase.Query(db, Counts));
        Assert.Equal(["2|24|2"], once);
    }

    /// <summary>
    /// seed が名指ししている 3 人を入れてから流す。
    /// </summary>
    /// <remarks>
    /// <b>4 列すべてを「期待の逆」で入れる</b>（2026-09-02 の自己レビュー）。
    /// DDL の既定は <c>can_access_app = 1</c> / <c>is_sysadmin = 0</c> / 役割は NULL で、
    /// これは <c>soumu_ippan</c> と <c>soumu_bucho</c> の期待と<b>ほぼ同じ</b>である——
    /// 既定のまま入れると、seed の <c>SET</c> 句を消しても全部緑になる（qa/03 L-02 の縮退）。
    /// </remarks>
    private static Microsoft.Data.Sqlite.SqliteConnection WithDevSeedApplied()
    {
        var db = TestDatabase.CreateWithSeed();

        foreach (var row in ExpectedRoles())
        {
            // ハッシュとソルトは NOT NULL。**値は使わない**（ログインはしない）。
            TestDatabase.Execute(db,
                $"""
                insert into app_users
                    (user_name, name, hash, salt, can_access_app, is_sysadmin, accounting_role, partner_role)
                values ('{(string)row[0]!}', 'X', 'h', 's', 0, 1, 'viewer', 'viewer')
                """);
        }

        ApplyDevSeed(db);
        return db;
    }

    private static void ApplyDevSeed(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        foreach (var file in TestDatabase.DevSeedFiles())
        {
            TestDatabase.Execute(db, File.ReadAllText(file));
        }
    }
}
