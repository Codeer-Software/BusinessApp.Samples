namespace BusinessApp.TestSupport;

using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

/// <summary>変異点 1 つ。<see cref="Spec"/> は <see cref="TestDatabase.Mutate"/> に渡す形。</summary>
/// <param name="Index">掃引が振った通番。報告の見出しになる。</param>
/// <param name="Spec"><c>&lt;モジュール名&gt;:&lt;開始位置&gt;:&lt;長さ&gt;:&lt;置換後&gt;:&lt;原文&gt;</c>。</param>
public sealed record ProbePoint(int Index, string Spec);

/// <summary>掃引が 1 モジュール分まとめて渡してくる変異点。</summary>
/// <param name="Module">対象のクエリモジュール名。</param>
/// <param name="Points">変異点。通番の順。</param>
public sealed record ProbePlan(string Module, IReadOnlyList<ProbePoint> Points);

/// <summary>
/// クエリの SQL を 1 箇所ずつ壊し、<b>行動テストが実際に流した入力で行セットが変わるか</b>を見る
/// （ADR-0058。<c>docs/qa/05_観点網羅の計器.md</c> §3-2 の B 案）。
/// </summary>
/// <remarks>
/// <para><b>A 案（<c>SQL_MUTATION</c>）との違いは殺し方だけである。</b>
/// あちらは<b>テストが赤になったか</b>を見るので「アサーションが違いを見分けたか」を測る。
/// こちらは<b>行セットが変わったか</b>を見るので、
/// <b>「いまのテストデータ・入力で原理的に見分けられるか」という上界</b>を測る
/// （ADR-0056 決定 8 が別の判断として送った）。</para>
/// <para><b>入力コーパスを別に持たない。</b>
/// <b>表を持てば写しが生まれる</b>——そして<b>表にあって誰も流していない入力が 1 つ混ざると、
/// B 案は「見分けられる」と報告するのに実際のテストは見張っていない</b>という、
/// <b>穴を静かに隠す向き</b>の狂いになる（ADR-0058 決定 1。採らなかった形もそちらが持つ）。
/// <b>行動テストが流したその瞬間に、その接続とその引数で当てる</b>ので、
/// コーパスは定義されるのではなく<b>観測される</b>。ずれようがない。</para>
/// <para><b>既定では何も起きない。</b> <see cref="SpecVariable"/> が立っているときだけ働く。
/// 立っていなければ <see cref="ExecuteReader"/> はそのまま実行するだけである。</para>
/// </remarks>
public static class SqlMutationProbe
{
    /// <summary>変異点を渡す環境変数。1 行 1 点で、<c>sql_mutate.py spec</c> の出力をそのまま入れる。</summary>
    /// <remarks>
    /// <b>ファイルで渡さない。</b> 掃引の途中で止めたときに、
    /// <b>次の掃引が古い表を読む</b>のを避ける（ADR-0056 決定 3 と同じ理由）。
    /// </remarks>
    public const string SpecVariable = "SQL_MUTATION_PROBE";

    /// <summary>報告の書き出し先を渡す環境変数。</summary>
    /// <remarks>
    /// <b>標準出力では返せない。</b> テストランナーの外に出るまでに握り潰される経路があり、
    /// <b>報告が届かなかったのか、報告する中身が無かったのかを駆動役が区別できない</b>。
    /// ファイルなら「無い」がそのまま「届かなかった」である。
    /// </remarks>
    public const string ReportVariable = "SQL_MUTATION_PROBE_REPORT";

    private static readonly object Gate = new();

    private static Session? current;

    /// <summary>
    /// クエリの SQL を流す。<b>行動テストはこれを通す。</b>
    /// </summary>
    /// <param name="command">
    /// クエリの SQL と引数を束縛済みのコマンド。<b>このコマンド自身は書き換えない。</b>
    /// </param>
    /// <param name="moduleName">クエリモジュールの名前。</param>
    /// <remarks>
    /// <b>観測は実行の前に済ませる。</b> 呼んだ側は返ってきたリーダを普通に読むだけでよい。
    /// </remarks>
    public static SqliteDataReader ExecuteReader(SqliteCommand command, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(moduleName);

        Observe(command, moduleName);

        return command.ExecuteReader();
    }

    /// <summary>
    /// 掃引が渡してきた変異点を読む。<b>形が壊れていたら投げる。</b>
    /// </summary>
    /// <remarks>
    /// <b>黙って素通ししない。</b> 1 行でも読み落とすと、
    /// その点は<b>誰にも当てられないまま「見分けられない」に数えられる</b>
    /// ——**穴を増やす向きに狂う**ので気づけはするが、理由が分からなくなる。
    /// </remarks>
    public static ProbePlan Read(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var points = new List<ProbePoint>();
        var seen = new HashSet<int>();
        string? module = null;

        foreach (var line in spec.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0)
            {
                continue;
            }

            // **欄の数を数える。** 原文に TAB が混ざれば 1 行が割れ、
            // **位置の欄に別の字が入ったまま進む**（`int.TryParse` が拾うまで気づけない）。
            var columns = text.Split('\t');
            if (columns.Length != 6)
            {
                throw new ArgumentException(
                    $"{SpecVariable} の欄が 6 つでない（{columns.Length} 欄）: '{text}'", nameof(spec));
            }

            if (!int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                throw new ArgumentException($"{SpecVariable} の通番が数でない: '{text}'", nameof(spec));
            }

            if (!seen.Add(index))
            {
                throw new ArgumentException($"{SpecVariable} に同じ通番が 2 度ある: {index}", nameof(spec));
            }

            // **モジュール名は変異点の側から取る。** 別に渡すと、
            // **渡し忘れた回に「1 点も当たらないまま緑」**という最良の報告が返る。
            // **5 欄に割れることまでここで見る。** 割れない形は当てる瞬間まで分からず、
            // **形の検査は入口に集めてある**（`形が壊れていたら投げる` の検体が守る範囲と揃える）。
            var named = columns[5].Split(':', 5);
            if (named.Length != 5 || named[0].Length == 0)
            {
                throw new ArgumentException(
                    $"{SpecVariable} の変異点が <モジュール名>:<開始位置>:<長さ>:<置換後>:<原文> の形でない: '{text}'",
                    nameof(spec));
            }

            module ??= named[0];
            if (!string.Equals(module, named[0], StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{SpecVariable} に 2 つのモジュールが混ざっている: {module} と {named[0]}", nameof(spec));
            }

            points.Add(new ProbePoint(index, columns[5]));
        }

        if (module is null)
        {
            throw new ArgumentException($"{SpecVariable} に変異点が 1 つも無い。", nameof(spec));
        }

        return new ProbePlan(module, points);
    }

    /// <summary>報告の姿。<b>駆動役（<c>sql_sweep.ps1</c>）が読む。</b></summary>
    /// <param name="plan">変異点。</param>
    /// <param name="observations">当てた入力の数（＝行動テストがクエリを流した回数）。</param>
    /// <param name="killed">どれかの入力で行セットが変わった点。</param>
    /// <param name="broken">SQL として成り立たなくなった点。<b>集計から除く。</b></param>
    /// <remarks>
    /// <b>ディスクを見ない形に切り出してある。</b> そうしないと
    /// <b>中身を空にしても緑のまま</b>になる（<c>QueryBehaviorCoverageTests</c> と同じ作法）。
    /// </remarks>
    public static string Render(
        ProbePlan plan, int observations, IReadOnlySet<int> killed, IReadOnlySet<int> broken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(killed);
        ArgumentNullException.ThrowIfNull(broken);

        var report = new StringBuilder();
        Line(report, "module", plan.Module);
        Line(report, "observations", observations.ToString(CultureInfo.InvariantCulture));
        Line(report, "points", plan.Points.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var point in plan.Points)
        {
            // **壊れた SQL は「殺した」にも「見分けられない」にも数えない**（qa/05 §3-2）。
            // 実物の SQL がそう壊れれば `QueryModuleTests` が全 SQL を流して捕まえる——
            // **テストデータの豊かさを測るこの計器が言えることは、そこには何も無い。**
            var outcome = broken.Contains(point.Index) ? "broken"
                : killed.Contains(point.Index) ? "killed"
                : "survived";
            Line(report, point.Index.ToString(CultureInfo.InvariantCulture), outcome);
        }

        return report.ToString();
    }

    private static void Line(StringBuilder report, string key, string value)
        => report.Append(key).Append('\t').Append(value).Append('\n');

    private static void Observe(SqliteCommand command, string moduleName)
    {
        var spec = Environment.GetEnvironmentVariable(SpecVariable);

        // **空白だけの値も「立っていない」と読む**（`SchemaKnockout.Requested` と同じ作法）。
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }

        // **A 案と同時には立てない。** あちらは読み込んだ SQL そのものを壊すので、
        // **こちらの「原本」が既に壊れたものになり、差が出ないほうを「見分けられない」と報告する**。
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestDatabase.SqlMutationVariable)))
        {
            throw new InvalidOperationException(
                $"{SpecVariable} と {TestDatabase.SqlMutationVariable} が同時に立っている。"
                + "A 案（テストの赤で殺す）と B 案（行セットの差分で殺す）は同時に回せない。");
        }

        // **報告の行き先は毎回読み直す。** セッションを開いたときに 1 度だけ解決すると、
        // **同じ変異点のまま行き先だけ差し替えた回に、古い宛先へ書き続ける**。
        var report = ReportPath();

        lock (Gate)
        {
            var session = current;
            if (session is null
                || !string.Equals(session.Spec, spec, StringComparison.Ordinal)
                || !string.Equals(session.ReportPath, report, StringComparison.Ordinal))
            {
                session = new Session(spec, Read(spec), report);
                current = session;

                // **開いた時点で 1 度書く。** 1 度も観測されないまま終わったときに、
                // **報告が「無い」ではなく「入力 0 通り」として残る**——
                // 駆動役はどちらでも止まるが、**理由が読める側**にしておく。
                Write(session);
            }

            // **名指されたモジュール以外は素通しする。** 1 回の掃引で 2 つ以上のモジュールに
            // 触るテストがあっても、当てるのは 1 モジュールだけである。
            if (string.Equals(session.Plan.Module, moduleName, StringComparison.Ordinal))
            {
                Measure(session, command);
            }
        }
    }

    private static string ReportPath()
    {
        var path = Environment.GetEnvironmentVariable(ReportVariable);

        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException(
                $"{SpecVariable} が立っているのに {ReportVariable} が無い。報告の行き先が要る。")
            : path;
    }

    private static void Measure(Session session, SqliteCommand command)
    {
        // **原本は、いまテストが流そうとしている字面そのものを使う。**
        // ここで読み直すと、**読み直した側と掃引が数えた側がずれても気づけない**。
        var original = command.CommandText;
        var baseline = Signature(command, original);

        // **決定性のカナリア。** 同じ SQL を 2 度流して行セットが違えば、
        // **以降の「変わった」は変異のせいだと言えない**——`ORDER BY` が全順序でない SQL で起きる。
        // **見分けられた点が増える向き**に狂うので、上限だけの関門では気づけない。
        if (!string.Equals(baseline, Signature(command, original), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{session.Plan.Module} は同じ SQL を 2 度流して行セットが違う。"
                + "並びが決まっていないので、行セットの差分で殺す判定が成り立たない。");
        }

        session.Observations++;

        foreach (var point in session.Plan.Points)
        {
            // **一度でも見分けられた点は、もう当てない。** 「見分けられた」は増える一方なので
            // 結果は変わらず、**残るのは見分けられていない点だけ**になって掃引が速くなる。
            if (session.Killed.Contains(point.Index) || session.Broken.Contains(point.Index))
            {
                continue;
            }

            // **位置がずれていれば `Mutate` が投げる**（ADR-0056 決定 3）。
            var mutated = TestDatabase.Mutate(original, session.Plan.Module, point.Spec);

            // **当てても 1 文字も変わらない点は、計器の故障である。**
            // 放っておくと**必ず「見分けられない」に数えられ、穴の報告に化ける**。
            if (string.Equals(mutated, original, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"変異点 {point.Index} は {session.Plan.Module} の SQL を 1 文字も変えていない: {point.Spec}");
            }

            string signature;
            try
            {
                signature = Signature(command, mutated);
            }
            catch (SqliteException)
            {
                // **SQL として成り立たなくなった点。** 実物がそう壊れれば `QueryModuleTests` が捕まえる。
                // **捕まえるのは流す段だけにする**——読み出しまで包むと、
                // **本物の不具合まで「壊れた点」に落ちて数から消える**。
                session.Broken.Add(point.Index);
                continue;
            }

            if (string.Equals(signature, baseline, StringComparison.Ordinal))
            {
                continue;
            }

            // **見分けたと数える前に、その変異体をもう一度流す。**
            // **変異体が流すたびに違う行を返すなら、変わった理由を変異に帰せない**
            // ——狂いは「見分けた点が増える＝見えない点が減る」向きで、上限では気づけない。
            if (!string.Equals(Signature(command, mutated), signature, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"変異点 {point.Index}（{session.Plan.Module}）は流すたびに違う行を返す。"
                    + "変わった理由を変異に帰せないので、行セットの差分で殺す判定が成り立たない。");
            }

            session.Killed.Add(point.Index);
        }

        // **観測のたびに書き出す。** 途中で落ちても、そこまでの姿が残る——
        // **観測の回数が減る＝見分けられない点が増える**ので、狂いは安全側に出る。
        Write(session);
    }

    /// <summary>
    /// 返る行を 1 本の文字列にする。<b>列の名前・並び・行の並び・値の型・NULL の別を全部畳み込む。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>コマンドを複製しない。呼んだ側のコマンドをそのまま使い、字面だけ戻す。</b>
    /// 複製すると<b>引数の型・トランザクション・待ち時間を写し損ねる経路が開く</b>——
    /// しかも<b>写し損ねた日に出るのは「行動テストが赤い」という、理由の分からない赤</b>である。
    /// 呼んだ側はまだ実行していないので、<c>finally</c> で戻せば何も起きない。</para>
    /// <para><b>列の名前も入れる。</b> 別名が変われば、行動テストは
    /// <c>GetOrdinal</c> で落ちて赤くなる——A 案が「殺した」と数える形なので、
    /// <b>こちらも見分けられたことにしないと、上界という関係が崩れる</b>。</para>
    /// <para><b>NULL と空文字を同じにしない。</b> どちらも <c>""</c> に畳むと、
    /// <c>COALESCE</c> の段の取り違え（qa/03 の L-19）が見分けられなくなる。</para>
    /// <para><b>値の型も入れる。</b> 整数の <c>1</c> と文字列の <c>"1"</c> を同じ字に畳むと、
    /// <b>同じ字面になる別の列へ取り違える変異が「変わらなかった」に落ちる</b>。
    /// <c>byte[]</c> は <c>Convert.ToString</c> が型名しか返さないので Base64 にする。</para>
    /// </remarks>
    private static string Signature(SqliteCommand command, string sql)
    {
        var restore = command.CommandText;
        try
        {
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            var text = new StringBuilder();

            for (var column = 0; column < reader.FieldCount; column++)
            {
                text.Append(reader.GetName(column)).Append('\u001f');
            }

            text.Append('\u001e');

            while (reader.Read())
            {
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    Append(text, reader, column);
                }

                text.Append('\u001e');
            }

            return text.ToString();
        }
        finally
        {
            command.CommandText = restore;
        }
    }

    private static void Append(StringBuilder text, SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column))
        {
            // **NULL は、どの値とも重ならない印にする。** 文字列の "null" は型名が前に付くので当たらない。
            text.Append("null").Append('\u001f');
            return;
        }

        var value = reader.GetValue(column);
        text.Append(value.GetType().Name).Append(':');
        text.Append(
            value is byte[] bytes
                ? Convert.ToBase64String(bytes)
                : Convert.ToString(value, CultureInfo.InvariantCulture));
        text.Append('\u001f');
    }

    private static void Write(Session session)
    {
        var directory = Path.GetDirectoryName(session.ReportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            session.ReportPath,
            Render(session.Plan, session.Observations, session.Killed, session.Broken));
    }

    private sealed class Session(string spec, ProbePlan plan, string reportPath)
    {
        public string Spec { get; } = spec;

        public ProbePlan Plan { get; } = plan;

        public string ReportPath { get; } = reportPath;

        public HashSet<int> Killed { get; } = [];

        public HashSet<int> Broken { get; } = [];

        public int Observations { get; set; }
    }
}
