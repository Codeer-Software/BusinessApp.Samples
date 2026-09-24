namespace BusinessApp.Schema.Tests;

using System.Text.RegularExpressions;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 制度ルール（税率）の表の守り（<c>Designer/ddl/015</c>・<c>016</c>・ADR-0069・docs/11 §1-1）。
/// </summary>
/// <remarks>
/// <para><b>この表には画面が無い。</b> 書き込む経路は <c>sql</c> CLI・取込・直打ちだけで、
/// <b>DDL が最後の砦になる</b>——<c>designcheck</c> も <c>lint_design.py</c> も、
/// モジュールが指していない表には 1 つも当たらない（<see cref="TransitionRateConstraintTests"/> と同じ）。</para>
/// <para><b>014 と同じ検体を並べるだけでは足りない。</b> この表は
/// <b>①区分が鍵に入る ②終期が NULL を許す ③値が 2 つあって形が違う</b>の 3 点で違い、
/// <b>どれも「守りが弱すぎる／強すぎる」の新しい形を持ち込む</b>——
/// 区分を忘れれば 2 区分が同じ日から始められず、NULL を素直に比べれば重なりを 1 本も見つけず、
/// 分数を取り違えた行は他の守りを全部通る。</para>
/// </remarks>
public class TaxRateConstraintTests
{
    private const string Columns =
        "(rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator, local_denominator,"
        + " version, legal_basis, source_url, confirmed_on)";

    /// <summary>
    /// その区分・その期間の行を 1 本入れる SQL。<b>版の字は区分と期間から導く</b>（表の規則）。
    /// </summary>
    /// <remarks>
    /// <b>既定の組（780・22/78）は合計税率が万分率の整数になる。</b>
    /// 国税を動かす検体は<b>分数も一緒に動かす</b>——22/78 では国税が 39 の倍数でないと
    /// 割り切れの CHECK が先に断り、<b>撃ちたい守りに届かない</b>。
    /// </remarks>
    private static string Insert(
        string kind,
        string from,
        string? to = null,
        string national = "780",
        string numerator = "22",
        string denominator = "78",
        string? version = null,
        string basis = "'検体'")
        => $"INSERT INTO tax_rates {Columns} VALUES"
            + $" ('{kind}', '{from}', {(to is null ? "NULL" : $"'{to}'")}, {national}, {numerator}, {denominator},"
            + $" {version ?? $"'tax_rate:{kind}:{from}'"}, {basis}, '検体', '2026-09-23');";

    /// <summary>改正前の税率の組（旧税率。6.3% ＋ 63 分の 17）。<b>こちらは 630 で割り切れる。</b>
    /// 区分は引数で渡す。<b>日付はテストの字で、制度の日付ではない</b>（いつから 6.3% かは確かめていない——税率リサーチ §3）。</summary>
    private static string InsertOldRate(string kind, string from, string? to = null)
        => Insert(kind, from, to, national: "630", numerator: "17", denominator: "63");

    private static SqliteConnection Empty() => TestDatabase.Create();

    // ------------------------------------------------------------------------------------------
    // 通るべき行が通ること
    //
    // **断りより先に置く。** 締めすぎた守りは、拒む側のテストでは 1 本も赤くならない——
    // 配った 2 行が入らなくなるほうの壊れ方は、ここでしか見えない。
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>2 つの区分が、同じ日から同時に始められる。書いた値がそのまま読み戻せる。</b>
    /// </summary>
    /// <remarks>
    /// <b>014 には無い形である。</b> 重なりの守りと一意索引から<b>区分を落とすと、ここだけが赤くなる</b>
    /// ——配っている 2 行は、どちらも 2019-10-01 から始まる。
    /// <b>意味を持つ列は全部読み戻す</b>（qa/03 の L-04）。
    /// </remarks>
    [Fact]
    public void 区分が違えば同じ日から始められる()
    {
        using var db = Empty();

        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));
        TestDatabase.Execute(db, Insert("reduced", "2019-10-01", national: "624"));

        Assert.Equal(
            [
                "reduced|2019-10-01|(null)|624|22|78|tax_rate:reduced:2019-10-01|検体|検体|2026-09-23",
                "standard|2019-10-01|(null)|780|22|78|tax_rate:standard:2019-10-01|検体|検体|2026-09-23",
            ],
            TestDatabase.Query(
                db,
                "SELECT rate_kind || '|' || valid_from || '|' || COALESCE(valid_to, '(null)')"
                + " || '|' || national_rate_per_10000 || '|' || local_numerator || '|' || local_denominator"
                + " || '|' || version || '|' || legal_basis || '|' || source_url || '|' || confirmed_on"
                + " FROM tax_rates ORDER BY rate_kind"));
    }

    /// <summary>
    /// <b>終期なし（NULL）の行が入る。</b> 014 との最大の違いである。
    /// </summary>
    /// <remarks>
    /// 消税法 29 も地方税法 72 の 83 も期限を書いていないので、<b>配る 2 行はどちらも終期が無い</b>。
    /// <c>valid_to</c> に NOT NULL を付け直したら、ここが赤くなる。
    /// </remarks>
    [Fact]
    public void 終期のない行が入る()
    {
        using var db = Empty();

        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));

        Assert.Equal(["(null)"], TestDatabase.Query(db, "SELECT COALESCE(valid_to, '(null)') FROM tax_rates"));
    }

    /// <summary><b>同じ区分でも、隣り合う期間は通る。</b> 重なりの守りが 1 日ぶん強すぎないこと。</summary>
    [Fact]
    public void 同じ区分でも隣り合う期間は通る()
    {
        using var db = Empty();

        TestDatabase.Execute(db, InsertOldRate("standard", "2014-04-01", "2019-09-30"));
        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));

        Assert.Equal(
            ["2014-04-01|2019-09-30", "2019-10-01|(null)"],
            TestDatabase.Query(
                db,
                "SELECT valid_from || '|' || COALESCE(valid_to, '(null)') FROM tax_rates ORDER BY valid_from"));
    }

    /// <summary>
    /// <b>区分が違えば、期間が重なる更新も通る。</b>
    /// </summary>
    /// <remarks>
    /// <b>更新側の重なりから区分を落とすと、ここだけが赤くなる</b>（2026-09-23 の自己レビュー）。
    /// 落とすと<b>厳しくなる方向に壊れる</b>——稼働 DB で標準税率の版を切ろうとしたとき、
    /// <b>終期なしの軽減税率と「重なる」と言われて、改定の版切りが一切できなくなる</b>。
    /// </remarks>
    [Fact]
    public void 区分が違えば期間が重なる更新も通る()
    {
        using var db = Empty();
        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));
        TestDatabase.Execute(db, Insert("reduced", "2015-04-01", "2016-03-31", national: "624"));

        TestDatabase.Execute(
            db,
            "UPDATE tax_rates SET valid_to = NULL WHERE rate_kind = 'reduced';");

        Assert.Equal(
            ["reduced|(null)", "standard|(null)"],
            TestDatabase.Query(
                db,
                "SELECT rate_kind || '|' || COALESCE(valid_to, '(null)') FROM tax_rates ORDER BY rate_kind"));
    }

    /// <summary>
    /// <b>国税の税率は上限の 10000 まで通る。</b>
    /// </summary>
    /// <remarks>
    /// <b>下限の 1 は入れられない。</b> 割り切れの CHECK が
    /// <c>国税 ×（分母＋分子）÷ 分母</c> が整数になることを求めるので、
    /// <b>国税を 1 にすると、それを満たす真分数（分子 &lt; 分母）が無い</b>
    /// ——分子が分母の倍数でなければならず、真分数ではそうならない。
    /// <b>だから下限を 2 に締めても、どの検体も失敗しない</b>——観測できないことをここに書き残す。
    /// </remarks>
    [Fact]
    public void 国税の税率は上限値まで通る()
    {
        using var db = Empty();

        // 1/2 なら 10000 × 3 ÷ 2 ＝ 15000 で割り切れる。**帯の端そのものを撃つための組である。**
        TestDatabase.Execute(
            db, Insert("standard", "2019-10-01", national: "10000", numerator: "1", denominator: "2"));

        Assert.Equal(["10000"], TestDatabase.Query(db, "SELECT national_rate_per_10000 FROM tax_rates"));
    }

    // ------------------------------------------------------------------------------------------
    // 断るべき行が断られること
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>同じ区分で 1 日でも重なる期間は断る。</b>
    /// </summary>
    /// <remarks>
    /// <b>重なりは一意索引では止まらない</b>（始まりが違えば通る）。
    /// 重なりを許すと「どちらが当たったか」が実行順に依存し、<b>静かに違う税率で計算される</b>。
    /// </remarks>
    [Theory]
    [InlineData("2019-09-30", "2020-03-31", "既にある行の始まりの 1 日前から始める")]
    [InlineData("2015-01-01", "2019-10-01", "始まりに 1 日重なる")]
    [InlineData("2022-01-01", "2022-12-31", "丸ごと内側")]
    [InlineData("2015-01-01", "2030-12-31", "丸ごと外側")]
    public void 同じ区分で期間が重なる版は追加できない(string from, string to, string label)
    {
        using var db = Empty();
        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));

        Rejected.ByTrigger(
            db, Insert("standard", from, to), "同じ税率区分で、有効期間が既にある版と重なっている。", label);
    }

    /// <summary>
    /// <b>終期なしの行は、その日以後のすべてと重なる。</b>
    /// </summary>
    /// <remarks>
    /// <b>ここが NULL の本番である。</b> 重なりの条件から <c>o.valid_to IS NULL</c> の枝を落とすと、
    /// <c>date(NULL)</c> の比較で条件全体が NULL になり、<b>重なりを 1 本も見つけずに通す</b>——
    /// 配っている 2 行はどちらも終期が無いので、<b>実際に配った行の上に二重の税率が積める</b>。
    /// <b>入れる側が終期なしのときも同じ</b>ので、両向きを撃つ。
    /// </remarks>
    [Theory]
    [InlineData("2026-04-01", "2027-03-31", "終期ありを、終期なしの後ろに足す")]
    [InlineData("2026-04-01", null, "終期なしを、終期なしの後ろに足す")]
    public void 終期なしの行の後ろには足せない(string from, string? to, string label)
    {
        using var db = Empty();
        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));

        Rejected.ByTrigger(
            db, Insert("standard", from, to), "同じ税率区分で、有効期間が既にある版と重なっている。", label);
    }

    /// <summary>更新でも重なりを断る。</summary>
    [Fact]
    public void 期間が重なる版へは更新できない()
    {
        using var db = Empty();
        TestDatabase.Execute(db, InsertOldRate("standard", "2014-04-01", "2019-09-30"));
        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));

        // **版の字も一緒に動かす。** 動かさないと版の CHECK が断って、重なりのトリガを測れない。
        Rejected.ByTrigger(
            db,
            "UPDATE tax_rates SET valid_from = '2019-09-30', version = 'tax_rate:standard:2019-09-30'"
            + " WHERE version = 'tax_rate:standard:2019-10-01';",
            "同じ税率区分で、有効期間が既にある版と重なっている。");
    }

    /// <summary>
    /// <b>同じ区分・同じ日から始まる版は 2 つ作れない</b>（時刻が付いていても同じ日である）。
    /// </summary>
    [Fact]
    public void 同じ区分で同じ日から始まる版は2つ作れない()
    {
        using var db = Empty();
        TestDatabase.Execute(db, Insert("standard", "2019-10-01"));

        // **同じ日から始まれば必ず重なる**ので、重なりのトリガが先に断る——
        // **索引そのものを撃つには、トリガを外すしかない**（許可表を通し、終わったら貼り直す）。
        // **時刻付きで書いても同じ日である**（CLB は日付の列へ "2019-10-01 00:00:00" と書く）。
        var exception = Assert.Throws<SqliteException>(() => TestDatabase.WithoutTrigger(
            db,
            "trg_tax_rates_no_overlap_insert",
            Insert("standard", "2019-10-01 00:00:00", version: "'tax_rate:standard:2019-10-01'")));

        Assert.Contains("ux_tax_rates_kind_valid_from", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>終わりが始まりより前の期間は作れない。</summary>
    [Fact]
    public void 終わりが始まりより前の期間は作れない()
    {
        using var db = Empty();

        Rejected.ByCheck(db, Insert("standard", "2019-10-01", "2019-09-30"), "date(valid_from) <= date(valid_to)");
    }

    /// <summary>知らない税率区分は入らない。</summary>
    [Fact]
    public void 知らない税率区分は入らない()
    {
        using var db = Empty();

        Rejected.ByCheck(db, Insert("legacy_8", "2019-10-01"), "rate_kind IN ('standard', 'reduced')");
    }

    /// <summary>
    /// <b>国税の税率は 1 以上 10000 以下の整数だけ。</b>
    /// </summary>
    /// <remarks>
    /// <b>0 を許さない。</b> 0% の税率区分は「課税だが税額 0」と読め、<b>非課税・免税と区別が付かなくなる</b>
    /// ——税率の概念がない区分は、税区分マスタ側で <c>rate_kind</c> を NULL にして表す（docs/11 §1-1）。
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("10001")]
    [InlineData("-1")]
    public void 国税の税率は1から10000の範囲だけ(string national)
    {
        using var db = Empty();

        Rejected.ByCheck(
            db,
            Insert("standard", "2019-10-01", national: national),
            "national_rate_per_10000 BETWEEN 1 AND 10000");
    }

    /// <summary>
    /// <b>数の列に小数は入らない。</b>
    /// </summary>
    /// <remarks>
    /// <b>範囲の CHECK だけでは通ってしまう。</b> INTEGER affinity は<b>非可逆な REAL を変換しない</b>ので、
    /// <c>780.5</c> は REAL のまま残り <c>BETWEEN 1 AND 10000</c> を満たす（014 で 2026-09-23 に実測）。
    /// 入った行は、読み出し側（<c>DbValue.ToInt</c>）が見つけた瞬間に投げる。
    /// </remarks>
    [Theory]
    [InlineData("national_rate_per_10000", "typeof(national_rate_per_10000) = 'integer'")]
    [InlineData("local_numerator", "typeof(local_numerator) = 'integer'")]
    [InlineData("local_denominator", "typeof(local_denominator) = 'integer'")]
    public void 数の列に小数は入らない(string column, string expression)
    {
        using var db = Empty();

        var sql = column switch
        {
            "national_rate_per_10000" => Insert("standard", "2019-10-01", national: "780.5"),
            "local_numerator" => Insert("standard", "2019-10-01", numerator: "22.5"),
            _ => Insert("standard", "2019-10-01", denominator: "78.5"),
        };

        Rejected.ByCheck(db, sql, expression, column);
    }

    /// <summary>
    /// <b>分子も分母も、帯を外れた値は入らない。</b>
    /// </summary>
    /// <remarks>
    /// <b>分母 0 は読み出し側で 0 除算になる</b>（<c>TaxRate.CombinedRatePer10000</c>）。
    /// <b>分子 0 は「地方消費税が無い」という制度像</b>で、地方税法 72 の 83 には無い。
    /// <b>分母の上限は「制度値として在りえない値」を入り口で断るための範囲である</b>（015 の注記）。
    /// <b>桁の溢れではない</b>——SQLite の整数は 64 ビットで、読み出し側も long で掛ける。
    /// </remarks>
    [Theory]
    [InlineData("0", "78", "local_numerator > 0")]
    [InlineData("22", "0", "local_denominator BETWEEN 1 AND 10000")]
    [InlineData("22", "10001", "local_denominator BETWEEN 1 AND 10000")]
    public void 分数が範囲外の行は入らない(string numerator, string denominator, string expression)
    {
        using var db = Empty();

        Rejected.ByCheck(
            db,
            Insert("standard", "2019-10-01", numerator: numerator, denominator: denominator),
            expression);
    }

    /// <summary>
    /// <b>分子が分母以上の行は入らない。</b>
    /// </summary>
    /// <remarks>
    /// <b>22/78 を 78/22 と取り違えた行は、型も正も版の字も日付も通る</b>——
    /// <b>通すと地方税額が国税の 3.5 倍になる</b>（2026-09-23 の自己レビュー）。
    /// 地方消費税は消費税額に対する真分数である。
    /// <b>割り切れの CHECK も (780, 78, 22) を断る</b>（780 × 100 ÷ 22 は割り切れない）が、
    /// <b>そちらは分数の向きを見ていない</b>——割り切れてしまう組（たとえば 50/22）は通してしまう。
    /// </remarks>
    [Theory]
    [InlineData("78", "22", "分子と分母が逆")]
    [InlineData("78", "78", "分子と分母が同じ")]
    public void 分子が分母以上の行は入らない(string numerator, string denominator, string label)
    {
        using var db = Empty();

        Rejected.ByCheck(
            db,
            Insert("standard", "2019-10-01", numerator: numerator, denominator: denominator),
            "local_numerator < local_denominator",
            label);
    }

    /// <summary>
    /// <b>合計税率が万分率の整数にならない行は入らない。</b>
    /// </summary>
    /// <remarks>
    /// <c>(800, 22, 78)</c> は他の CHECK を全部通るが、800 × 100 ÷ 78 ＝ 1025.64… で割り切れない。
    /// <b>入れてしまうと、導いた合計税率が黙って切り捨てられ、税込 → 税抜の換算が分数で書けなくなる</b>
    /// （docs/11 §1-1。<c>TaxRate</c> の不変条件が読み出しの側でも同じことを見る）。
    /// </remarks>
    [Fact]
    public void 合計税率が万分率の整数にならない行は入らない()
    {
        using var db = Empty();

        Rejected.ByCheck(
            db,
            Insert("standard", "2019-10-01", national: "800"),
            "national_rate_per_10000 * (local_denominator + local_numerator) % local_denominator = 0");
    }

    /// <summary>
    /// <b>文字の列に BLOB は入らない。</b>
    /// </summary>
    /// <remarks>
    /// <b>BLOB は TEXT の列にそのまま入り、UNIQUE でもぶつからない</b>（010 が 2026-09-09 に実測）。
    /// 読み出し側（<c>DbValue.ToText</c>）は byte[] を <c>System.Byte[]</c> にするので、
    /// <b>その字が版として仕訳に焼かれ、版で引き直せなくなる</b>。
    /// </remarks>
    [Theory]
    [InlineData("version", "typeof(version) = 'text'")]
    [InlineData("legal_basis", "typeof(legal_basis) = 'text'")]
    [InlineData("source_url", "typeof(source_url) = 'text'")]
    public void 文字の列にBLOBは入らない(string column, string expression)
    {
        using var db = Empty();

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "'tax_rate:standard:2019-10-01'",
            ["legal_basis"] = "'検体'",
            ["source_url"] = "'検体'",
        };
        values[column] = "x'61646D696E'";

        Rejected.ByCheck(
            db,
            $"INSERT INTO tax_rates {Columns} VALUES"
            + $" ('standard', '2019-10-01', NULL, 780, 22, 78, {values["version"]},"
            + $" {values["legal_basis"]}, {values["source_url"]}, '2026-09-23');",
            expression,
            column);
    }

    /// <summary>
    /// <b>版の字は区分と有効期間の開始日から導く。</b>
    /// </summary>
    /// <remarks>
    /// 形を決めただけでは、<b>版が指す日と、その版が実際に効いた期間が食い違ったまま仕訳に焼ける</b>
    /// （版は <c>journal_lines.applied_rule_version</c> に写す。I-16）。
    /// <b>区分の食い違いは 014 には無い形である</b>——2 区分が同じ日から始まるので、
    /// <b>区分を落とした版の字は 2 行で同じになる</b>。
    /// </remarks>
    [Theory]
    [InlineData("'tax_rate:standard:2020-01-01'", "日がずれている")]
    [InlineData("'tax_rate:reduced:2019-10-01'", "区分がずれている")]
    [InlineData("'tax_rate:2019-10-01'", "区分が無い")]
    [InlineData("'2019-10-01'", "種類も区分も無い")]
    public void 版の字が区分や期間と食い違う行は作れない(string version, string label)
    {
        using var db = Empty();

        Rejected.ByCheck(
            db,
            Insert("standard", "2019-10-01", version: version),
            "version = 'tax_rate:' || rate_kind || ':' || substr(valid_from, 1, 10)",
            label);
    }

    /// <summary>
    /// <b>日付が読めない値では、日付の断りが先に鳴る。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>014 で踏んだ事故の再発を見張る。</b> あちらは値の形もトリガで書いたので、
    /// <b>発火順（仕様上 undefined。実測では後に作ったものから鳴る）によって版の断りが先に出て、
    /// 直すべき日付の断りが出なかった</b>。015 は<b>値の形を CHECK に置いた</b>——
    /// <b>SQLite は BEFORE トリガを走らせてから CHECK を見る</b>ので、順が入れ替わらない。</para>
    /// <para><b>版の字は正しいものを渡す。</b> <c>Insert()</c> の既定は版を <c>from</c> から組むので、
    /// <c>'20191001'</c> を渡すと版の字も <c>'20191001'</c> になり、
    /// <b>版の CHECK は真になって 1 度も鳴らない</b>——それでは「トリガが先に走る」を測れない
    /// （2026-09-23 の自己レビューで、測れていないことが分かった）。
    /// <b>版を正しい字に固定すると、版の CHECK は必ず破れる</b>ので、
    /// <b>日付の断りが出ることが、順の証拠になる</b>。</para>
    /// <para><b>配った行がある DB で撃つ。</b> 終期なしの行があるので、
    /// <b>重なりのトリガも同時に鳴る条件に入る</b>——その状態で日付の断りが出ることを見る。</para>
    /// <para><b>015 の重なりのトリガに置いた先行条件</b>
    /// （<c>date(julianday(NEW.valid_from)) IS substr(…)</c>。開始日が年月日として読めるときだけ
    /// 重なりを見る、という条件）<b>は、このテストでは測れない。</b>
    /// 外しても、<b>日付のトリガが後に作られている分だけ先に失敗するので、同じエラーメッセージが返る</b>
    /// （2026-09-23 に実測）。<b>この先行条件は発火順が変わった日のための保険であり、
    /// いまの SQLite では観測できない</b>——観測できないことをここに書き残す。</para>
    /// </remarks>
    [Theory]
    [InlineData("20191001", "区切り無し")]
    [InlineData("2019-13-01", "月が 13")]
    [InlineData("2460000", "ユリウス日")]
    public void 日付が読めないときは日付の断りが出る(string from, string label)
    {
        using var db = TestDatabase.CreateWithSeed();

        Rejected.ByTrigger(
            db,
            Insert("standard", from, version: "'tax_rate:standard:2019-10-01'"),
            "「有効期間の開始日」は実在する年月日を「2026-04-01」の形で入れる。",
            label);
    }

    // ------------------------------------------------------------------------------------------
    // REPLACE の暗黙の DELETE（Designer/ddl/016。qa/03 の L-26）
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b><c>INSERT OR REPLACE</c> で、配った行を上書きできない。</b>
    /// </summary>
    /// <remarks>
    /// <b>2026-09-23 に実測した形である</b>——守りを入れる前は、
    /// <c>id = 1</c> をぶつけた <c>reduced</c> の行が通り、<b><c>standard</c> の行が音もなく消えた</b>
    /// （<c>reduced</c> が 2 本になる）。<b>重なりのトリガは同じ区分しか見ない</b>ので 1 本も鳴らない。
    /// <b>行が消えていないことまで読み戻す</b>——例外が飛んだだけでは足りない。
    /// </remarks>
    [Fact]
    public void REPLACEで配った行を上書きできない()
    {
        using var db = TestDatabase.CreateWithSeed();
        var id = TestDatabase.ScalarOf<long>(db, "SELECT id FROM tax_rates WHERE rate_kind = 'standard'");

        Rejected.ByTrigger(
            db,
            $"INSERT OR REPLACE INTO tax_rates (id, {Columns[1..^1]}) VALUES"
            + $" ({id}, 'reduced', '2015-01-01', '2019-09-30', 630, 17, 63,"
            + " 'tax_rate:reduced:2015-01-01', '検体', '検体', '2026-09-23');",
            "税率の行を、既にある行の識別子へ被せられない。");

        Assert.Equal(
            ["reduced", "standard"],
            TestDatabase.Query(db, "SELECT rate_kind FROM tax_rates ORDER BY rate_kind"));
    }

    /// <summary>
    /// <b>識別子を別の行へ移す更新もできない。</b>
    /// </summary>
    /// <remarks>
    /// <c>id</c> は<b>どのトリガの <c>OF</c> にも入っていない</b>ので、
    /// <c>UPDATE OR REPLACE … SET id = …</c> は<b>守りを 1 本も起こさずに 1 行消していた</b>
    /// （2026-09-23 に実測。3 行が 2 行になった）。
    /// </remarks>
    [Fact]
    public void 識別子を別の行へ移す更新はできない()
    {
        using var db = TestDatabase.CreateWithSeed();
        var standard = TestDatabase.ScalarOf<long>(db, "SELECT id FROM tax_rates WHERE rate_kind = 'standard'");
        var reduced = TestDatabase.ScalarOf<long>(db, "SELECT id FROM tax_rates WHERE rate_kind = 'reduced'");

        Rejected.ByTrigger(
            db,
            $"UPDATE OR REPLACE tax_rates SET id = {standard} WHERE id = {reduced};",
            "税率の行を、既にある行の識別子へ被せられない。");

        Assert.Equal(2, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM tax_rates"));
    }

    // ------------------------------------------------------------------------------------------
    // 税率区分の値
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>税率区分の語彙は、003 と 015 で同じである。</b>
    /// </summary>
    /// <remarks>
    /// <b>2 つの表が同じ 2 つの値を別々に宣言している。</b> 税区分マスタ（003）が「この行はどの区分か」を持ち、
    /// 税率の表（015）が「その区分は何 % か」を持つ——<b>片方にだけ区分を足すと、
    /// 選べるのに税率が引けない区分</b>（003 だけに足した場合）か、
    /// <b>誰も選べない税率の行</b>（015 だけに足した場合）ができる。どちらも実行時まで黙っている。
    /// </remarks>
    [Fact]
    public void 税率区分の値は税区分マスタと同じ2つである()
    {
        using var db = Empty();

        // **字の錨を置く。** 両辺とも DDL から採るので、**2 つの CHECK から同じ区分を同時に消すと
        // 相等だけでは緑のまま通る**（self-review スキル §9 の 4）。
        string[] expected = ["reduced", "standard"];

        Assert.Equal(expected, Vocabulary(db, "tax_categories"));
        Assert.Equal(expected, Vocabulary(db, "tax_rates"));
    }

    // ------------------------------------------------------------------------------------------
    // OR IGNORE（Designer/ddl/015 の「値の形を CHECK に置いた」ことの帰結）
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b><c>INSERT OR IGNORE</c> は、CHECK に反する行を黙って飛ばす。</b>
    /// </summary>
    /// <remarks>
    /// <b>015 が「配り方は `NOT EXISTS` で包む」と決めた根拠そのものである</b>
    /// （`OR IGNORE` は CHECK・UNIQUE・NOT NULL の違反行をエラー無しで飛ばすが、
    /// トリガの <c>RAISE(ABORT)</c> は飛ばせない。2026-09-23 に実測）。
    /// <b>014 は値の形をトリガで書いたので断り、015 は CHECK なので飛ばす</b>——
    /// <b>同じ「制度ルールの表」で強さが非対称である</b>。ここでその差を字にして固定する。
    /// <b>飛ばされた行は行の同値検査が「足りない」と言う</b>（<c>VendorRows</c>）。
    /// </remarks>
    [Fact]
    public void ORIGNOREはCHECKに反する行を黙って飛ばす()
    {
        using var db = Empty();

        TestDatabase.Execute(
            db,
            "INSERT OR IGNORE INTO tax_rates " + Columns + " VALUES"
            + " ('standard', '2019-10-01', NULL, 780.5, 22, 78,"
            + " 'tax_rate:standard:2019-10-01', '検体', '検体', '2026-09-23');");

        Assert.Equal(0, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM tax_rates"));
    }

    /// <summary>
    /// <b><c>INSERT OR IGNORE</c> でも、識別子の衝突は断られる。</b>
    /// </summary>
    /// <remarks>
    /// <b>トリガの <c>RAISE(ABORT)</c> は <c>OR IGNORE</c> で飛ばせない</b>ので、
    /// <c>Designer/ddl/016</c> の守りはこの経路でも効く——
    /// <b>「冪等にしたい」で <c>OR IGNORE</c> を書いた人が、配った行を消してしまう形は塞がっている</b>。
    /// </remarks>
    [Fact]
    public void ORIGNOREでも識別子の衝突は断られる()
    {
        using var db = TestDatabase.CreateWithSeed();
        var id = TestDatabase.ScalarOf<long>(db, "SELECT id FROM tax_rates WHERE rate_kind = 'standard'");

        Rejected.ByTrigger(
            db,
            $"INSERT OR IGNORE INTO tax_rates (id, {Columns[1..^1]}) VALUES"
            + $" ({id}, 'reduced', '2015-01-01', '2019-09-30', 630, 17, 63,"
            + " 'tax_rate:reduced:2015-01-01', '検体', '検体', '2026-09-23');",
            "税率の行を、既にある行の識別子へ被せられない。");

        Assert.Equal(2, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM tax_rates"));
    }

    /// <summary><c>rate_kind IN (...)</c> が許す値の並びを、表の定義から採る。</summary>
    private static IReadOnlyList<string> Vocabulary(SqliteConnection db, string table)
    {
        var definition = TestDatabase.ScalarOf<string>(
            db, $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{table}'");
        var match = Regex.Match(definition, @"rate_kind\s+TEXT[^\n]*?IN\s*\(([^)]*)\)");

        Assert.True(match.Success, $"{table} の rate_kind の CHECK を読めない。定義の書き方を変えたら、ここも直す。");

        return match.Groups[1].Value
            .Split(',')
            .Select(value => value.Trim().Trim('\''))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }
}
