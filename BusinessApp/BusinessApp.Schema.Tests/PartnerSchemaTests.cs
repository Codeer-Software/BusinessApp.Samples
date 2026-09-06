namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 取引先の素性の列と、適格請求書発行事業者の登録テーブル（docs/07）。
/// CHECK が実際に書き込みを拒むことを検査する（宣言してあるだけでは守りにならない）。
/// </summary>
public class PartnerSchemaTests
{
    private static SqliteConnection Seeded()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        return db;
    }

    [Fact]
    public void 取引先の素性は全項目がそのまま読み戻せる()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partners (code, name, address, entity_type, corporate_number)
                VALUES ('P100', '株式会社アルデバラン', '東京都台東区雷門 2-3-4', 'corporation', '1234567890123');
            """);

        Assert.Equal(
            "東京都台東区雷門 2-3-4|corporation|1234567890123",
            TestDatabase.ScalarOf<string>(db,
                "SELECT address || '|' || entity_type || '|' || corporate_number FROM partners WHERE code = 'P100'"));
    }

    [Fact]
    public void 取引先の種別に規定外の値は書けない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            "INSERT INTO partners (code, name, entity_type) VALUES ('P100', 'X', 'company');"));
    }

    /// <summary>種別は NULL 可（未分類）。既存の行を偽の値で埋めない（docs/07 §1-2）。</summary>
    [Fact]
    public void 取引先の種別は未分類のままでもよい()
    {
        using var db = Seeded();

        TestDatabase.Execute(db, "INSERT INTO partners (code, name) VALUES ('P100', '素性のまだ無い取引先');");

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM partners WHERE code = 'P100' AND entity_type IS NULL"));
    }

    [Theory]
    [InlineData("123456789012")]     // 12 桁
    [InlineData("12345678901234")]   // 14 桁
    [InlineData("123456789012a")]    // 数字でない
    [InlineData("")]                 // **空文字も拒む。** 「無い」は NULL で表す（docs/10 §4-4）
    public void 法人番号は13桁の数字でなければ書けない(string invalid)
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            $"INSERT INTO partners (code, name, corporate_number) VALUES ('P100', 'X', '{invalid}');"));
    }

    /// <summary>個人事業者に法人番号は指定されない（制度事実）。矛盾行は誤束ねの温床になる。</summary>
    [Fact]
    public void 個人事業者に法人番号は書けない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partners (code, name, entity_type, corporate_number)
            VALUES ('P100', 'X', 'sole_proprietor', '1234567890123');
            """));
    }

    /// <summary>人格のない社団等は法人番号を持ちうる（docs/07 §1-2）。法人以外を一律に拒まない。</summary>
    [Fact]
    public void 人格のない社団等には法人番号を書ける()
    {
        using var db = Seeded();

        TestDatabase.Execute(db, """
            INSERT INTO partners (code, name, entity_type, corporate_number)
            VALUES ('P100', '町内会', 'unincorporated_association', '1234567890123');
            """);

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM partners WHERE code = 'P100'"));
    }

    [Fact]
    public void 取引先は自分自身を名寄せの親にできない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, "INSERT INTO partners (code, name) VALUES ('P100', 'X');");
        var id = TestDatabase.ScalarOf<long>(db, "SELECT id FROM partners WHERE code = 'P100'");

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            $"UPDATE partners SET parent_partner_id = {id} WHERE id = {id};"));
    }

    /// <summary>親を指す正常系の往復。拒む側のテストだけだと、列を取り違えても全テストが緑になる。</summary>
    [Fact]
    public void 名寄せの親を書いて読み戻せる()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, "INSERT INTO partners (code, name) VALUES ('P100', '親の商店');");
        var parent = TestDatabase.ScalarOf<long>(db, "SELECT id FROM partners WHERE code = 'P100'");
        TestDatabase.Execute(db,
            $"INSERT INTO partners (code, name, parent_partner_id) VALUES ('P200', '子の屋台', {parent});");

        Assert.Equal(parent, TestDatabase.ScalarOf<long>(db,
            "SELECT parent_partner_id FROM partners WHERE code = 'P200'"));
    }

    [Fact]
    public void 名寄せの親は実在する取引先でなければならない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            "INSERT INTO partners (code, name, parent_partner_id) VALUES ('P100', 'X', 999);"));
    }

    // ---- 適格請求書発行事業者の登録 ----

    [Fact]
    public void 登録は全項目がそのまま読み戻せる()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason,
                 source, confirmed_on, nta_updated_on, published_name)
            VALUES (1, 'T1234567890123', '2023-10-01', '2026-03-31', 'revoked',
                    'nta_api', '2026-08-25', '2026-04-01', '株式会社トリヒキサキ');
            """);

        Assert.Equal(
            "T1234567890123|2023-10-01|2026-03-31|revoked|nta_api|2026-08-25|2026-04-01|株式会社トリヒキサキ",
            TestDatabase.ScalarOf<string>(db, """
                SELECT registration_no || '|' || valid_from || '|' || ended_on || '|' || end_reason
                       || '|' || source || '|' || confirmed_on || '|' || nta_updated_on || '|' || published_name
                FROM partner_invoice_registrations
                """));
    }

    [Fact]
    public void 登録の終わりには必ず理由が要る()
    {
        using var db = Seeded();

        // 終わりだけ（理由なし）
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from, ended_on)
            VALUES (1, 'T1234567890123', '2023-10-01', '2026-03-31');
            """));

        // 理由だけ（終わりなし）
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', 'revoked');
            """));
    }

    /// <summary>登録より前に終わる行は転記ミス。同日は（あり得るか未確認のため）許す。</summary>
    [Fact]
    public void 登録より前には終われない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2023-09-30', 'revoked');
            """));

        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2023-10-01', 'revoked');
            """);
    }

    // --- 期間の重なり（docs/07 §3-5 R-I4・R-I5。トリガ。2026-09-02）---
    // 本線の関門は PartnerRegistrationSubmitGate。ここは API を迂回した経路への最後の守りが
    // 実際に書き込みを拒むことを検査する。

    [Fact]
    public void 期間が重なる登録は書けない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2026-03-31', 'revoked');
            """);

        // 先の登録の終わり（2026-03-31）より前に始まる
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T9999999999999', '2026-03-30');
            """));

        // 逆向き（新しい行が先に始まり、先の登録に食い込んで終わる）
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T9999999999999', '2020-01-01', '2023-10-02', 'expired');
            """));
    }

    /// <summary>隣接（前の行の終わりの日＝次の行の登録年月日）は書ける（docs/07 §3-5。正常形かは未確認）。</summary>
    [Fact]
    public void 前の登録が終わった日に始まる再登録は書ける()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2026-03-31', 'revoked');
            """);

        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2026-03-31');
            """);

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM partner_invoice_registrations"));
    }

    [Fact]
    public void 終わりのない登録のあとには書けない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2023-10-01');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T9999999999999', '2024-01-01');
            """));
    }

    [Fact]
    public void 更新でも期間を重ねられない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2024-03-31', 'revoked');
            """);
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T9999999999999', '2024-04-01');
            """);

        // 先の行の終わりを伸ばして、後続の行に食い込ませる
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            UPDATE partner_invoice_registrations SET ended_on = '2024-06-30'
             WHERE registration_no = 'T1234567890123';
            """));
    }

    /// <summary>軸は取引先ごと。別の取引先の期間とは重ねて数えない。</summary>
    [Fact]
    public void 別の取引先の期間とは重ならない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db,
            "INSERT INTO partners (code, name) VALUES ('P200', '二社目');");
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2023-10-01');
            """);

        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            SELECT id, 'T9999999999999', '2024-01-01' FROM partners WHERE code = 'P200';
            """);

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM partner_invoice_registrations"));
    }

    /// <summary>
    /// <b>時刻付きの書き方と素の日付が混ざっても、判定が揺れない。</b>
    /// CLB は日付列に '2023-10-01 00:00:00' と時刻付きで書く。トリガから date() を外すと、
    /// 書式の揃ったテストデータでは文字列比較が偶然正しく動いて緑のまま抜ける（qa/03 L-12 と同型）。
    /// </summary>
    [Fact]
    public void 書式が混ざっても隣接は書けて重なりは書けない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01 00:00:00', '2026-03-31 00:00:00', 'revoked');
            """);

        // 隣接（素の日付で書く）——date() が無いと '2026-03-31' < '2026-03-31 00:00:00' で誤拒否になる
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2026-03-31');
            """);

        // 重なり（素の日付で書く）
        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T9999999999999', '2020-01-01', '2023-10-02', 'expired');
            """));
        Assert.Contains("登録の期間が重なっている", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>正当な単行の更新は通る。</b> UPDATE トリガの「自分の旧値」の除外（o.id &lt;&gt; NEW.id）を
    /// 消すと、終わりのない行の登録年月日を後ろへ直す正当な更新が旧値の自分に当たって全滅する。
    /// 拒む側のテストだけだとこの 1 語を消しても緑のまま。
    /// </summary>
    [Fact]
    public void 終わりのない行の登録年月日を動かす更新は通る()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2023-10-01');
            """);

        TestDatabase.Execute(db,
            "UPDATE partner_invoice_registrations SET valid_from = '2023-12-01' WHERE registration_no = 'T1234567890123';");

        Assert.Equal("2023-12-01", TestDatabase.ScalarOf<string>(db,
            "SELECT date(valid_from) FROM partner_invoice_registrations WHERE registration_no = 'T1234567890123'"));
    }

    /// <summary>取引先の付け替え（UPDATE OF partner_id）も、移り先で重なるならトリガが拒む。</summary>
    [Fact]
    public void 付け替えで移り先の期間と重なる更新は書けない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db,
            "INSERT INTO partners (code, name) VALUES ('P200', '二社目');");
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2023-10-01');
            """);
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            SELECT id, 'T9999999999999', '2024-01-01' FROM partners WHERE code = 'P200';
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            UPDATE partner_invoice_registrations
               SET partner_id = (SELECT id FROM partners WHERE code = 'P200')
             WHERE registration_no = 'T1234567890123';
            """));
    }

    [Fact]
    public void 同じ取引先に同じ登録は二重に取り込めない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2023-10-01');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2023-10-01');
            """));
    }

    /// <summary>再登録（同じ番号・別の登録年月日）は別の行として持てる。</summary>
    [Fact]
    public void 再登録は別の行として持てる()
    {
        using var db = Seeded();

        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2025-03-31', 'revoked');
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (1, 'T1234567890123', '2026-04-01');
            """);

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM partner_invoice_registrations"));
    }

    [Fact]
    public void 登録は実在する取引先にしか付けられない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)
            VALUES (999, 'T1234567890123', '2023-10-01');
            """));
    }

    [Fact]
    public void 出所に規定外の値は書けない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from, source)
            VALUES (1, 'T1234567890123', '2023-10-01', 'excel');
            """));
    }

    [Fact]
    public void 終わりの理由に規定外の値は書けない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2026-03-31', 'cancelled');
            """));
    }

    /// <summary>失効（expired）も取消と同じに通る。取消だけで検査すると片方の値が一度も書かれない。</summary>
    [Fact]
    public void 失効も終わりの理由として書ける()
    {
        using var db = Seeded();

        TestDatabase.Execute(db, """
            INSERT INTO partner_invoice_registrations
                (partner_id, registration_no, valid_from, ended_on, end_reason)
            VALUES (1, 'T1234567890123', '2023-10-01', '2026-03-31', 'expired');
            """);

        Assert.Equal("expired", TestDatabase.ScalarOf<string>(db,
            "SELECT end_reason FROM partner_invoice_registrations"));
    }
}
