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
