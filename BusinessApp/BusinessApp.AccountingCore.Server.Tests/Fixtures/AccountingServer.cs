namespace BusinessApp.AccountingCore.Server.Tests.Fixtures;

using System.Globalization;
using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.Partners.Server;
using BusinessApp.TestSupport;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;
using Microsoft.Data.Sqlite;
using BusinessApp.AccountingCore.Server.Journals.Application;
using BusinessApp.AccountingCore.Server.Journals.Infrastructure;
using BusinessApp.AccountingCore.Server.Journals.Presentation;
using BusinessApp.AccountingCore.Server.Shared.Infrastructure;
using BusinessApp.AccountingCore.Server.Shared.Presentation;

/// <summary>
/// 本物の DDL と初期データを載せた SQLite の上に、サーバ側部品を組み立てて渡す。
/// </summary>
/// <remarks>
/// <b>組み立ては本番と同じ <see cref="JournalSubmitGate.Create"/> を通す。</b>
/// テストが部品を手で繋ぐと、本番の配線とずれても誰も気づけない。
/// </remarks>
internal sealed class AccountingServer : IDisposable
{
    /// <summary>計上の時刻。日付をまたぐ話をしないので固定する。</summary>
    public static readonly DateTimeOffset Now =
        new(2026, 8, 24, 13, 6, 46, TimeSpan.FromHours(9));

    /// <summary>初期データの第 18 期（2026-04-01 〜 2027-03-31）。</summary>
    public static readonly FiscalYearId FiscalYear = new(1);

    private readonly SqliteConnection connection;

    /// <summary>
    /// 操作している人の識別子。<b>他の id（会計年度 1・伝票 1・伝票番号 1…）と衝突しない値にする</b>
    /// （縮退させると、取り違えても全テストが緑のままになる。qa/03 L-02）。
    /// </summary>
    /// <remarks>
    /// <b>この利用者は <c>app_users</c> にも入れる</b>（2026-09-02）。
    /// 取消・訂正の入口が会計の役割を読むようになったので（qa/03 L-22）、
    /// 行が無いと「役割なし」に倒れて全部差し戻される。既定は<b>経理担当</b>——
    /// 画面から取消・訂正を押せる最小の役割である。
    /// </remarks>
    public const long CurrentUser = 91;

    public AccountingServer()
    {
        connection = TestDatabase.CreateWithSeed();
        Accessor = new SqliteDbAccessor(connection);
        SetAccountingRole("staff");

        var authentication = new TestAuthenticationContext(() => CurrentUserId);
        Authentication = authentication;
        MasterLoader = new AccountingMasterLoader(Accessor, SqliteDbAccessor.DataSourceName);
        EntryStore = new JournalEntryStore(Accessor, SqliteDbAccessor.DataSourceName);
        SequenceStore = new EntryNumberSequenceStore(Accessor, SqliteDbAccessor.DataSourceName);
        Registrations = new PartnerRegistrationStore(Accessor, SqliteDbAccessor.DataSourceName);
        Partners = new PartnerStore(Accessor, SqliteDbAccessor.DataSourceName);
        SnapshotWriter = new LedgerSnapshotWriter(Accessor, SqliteDbAccessor.DataSourceName, Registrations);
        Poster = JournalPoster.Create(
            Accessor, SqliteDbAccessor.DataSourceName, EntryStore, new FixedTimeProvider(Now), authentication);
        Gate = JournalSubmitGate.Create(
            Accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication);
        Pipeline = AccountingSubmitPipeline.Create(
            Accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication,
            SaveFailureLog.Add);
        PipelineWithoutLog = AccountingSubmitPipeline.Create(
            Accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication);
        AmendmentService = JournalAmendmentService.Create(
            Accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication);
        Amendment = JournalAmendmentEndpoint.Create(
            Accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication);
    }

    /// <summary>
    /// 認証コンテキストが返す識別子。<b>テストが差し替えられる</b>
    /// （数値でない・空のときに posted_by が null になることの検査に使う）。
    /// </summary>
    public string CurrentUserId { get; set; } = CurrentUser.ToString(CultureInfo.InvariantCulture);

    /// <summary>認証の代わり（<see cref="CurrentUserId"/> を返す）。部品を手で組むテストが使う。</summary>
    public IAuthenticationContext Authentication { get; }

    /// <summary>DB への口。<b>部品を手で組むテスト</b>（読み口 1 つだけを試すもの）が使う。</summary>
    public SqliteDbAccessor Accessor { get; }

    /// <summary>
    /// 操作している人の会計の役割を差し替える。<c>null</c> で<b>役割なし</b>にする。
    /// </summary>
    /// <remarks>
    /// 行ごと入れ直すのは、<b>「利用者が居ない」と「居るが役割が無い」を撃ち分ける</b>ため——
    /// 前者は <see cref="CurrentUserId"/> を別の値にすれば作れる。
    /// </remarks>
    public void SetAccountingRole(string? role)
    {
        var value = role is null ? "null" : $"'{role}'";
        Execute($"delete from app_users where id = {CurrentUser}");
        Execute($"""
            insert into app_users (id, user_name, name, hash, salt, accounting_role)
            values ({CurrentUser}, 'test_user', 'テスト利用者', 'h', 's', {value})
            """);
    }

    public AccountingMasterLoader MasterLoader { get; }

    public JournalEntryStore EntryStore { get; }

    public EntryNumberSequenceStore SequenceStore { get; }

    /// <summary>取引先の名称と登録を読む口。</summary>
    public PartnerRegistrationStore Registrations { get; }

    /// <summary>取引先の素性（種別・法人番号）を読む口。</summary>
    public PartnerStore Partners { get; }

    /// <summary>計上のときに帳簿の記載事項を写す部品（ADR-0018）。</summary>
    public LedgerSnapshotWriter SnapshotWriter { get; }

    /// <summary>計上そのもの。<b>本番と同じ組み立て</b>（<see cref="JournalPoster.Create"/>）。</summary>
    public JournalPoster Poster { get; }

    public JournalSubmitGate Gate { get; }

    /// <summary>保存の関門を本番と同じ順につないだもの（<see cref="AccountingSubmitPipeline.Create"/>）。</summary>
    public AccountingSubmitPipeline Pipeline { get; }

    /// <summary>
    /// 利用者の語へ差し替えた<b>原文</b>（本番ではホストがログへ出すもの）。
    /// </summary>
    /// <remarks>
    /// <b>本番の配線と同じ口を繋いでおく。</b> 繋がないと、
    /// 「定型文を見せて中身はログへ」という分担が<b>片側しか検査できない</b>——
    /// 利用者に出ないことは見えても、直すべき側に届いていることが見えない（2026-09-09 の自己レビュー）。
    /// </remarks>
    public List<string> SaveFailureLog { get; } = [];

    /// <summary>
    /// 原文の受け皿を繋がない配線。
    /// </summary>
    /// <remarks>
    /// <b>受け皿は任意である</b>（<c>AccountingSubmitPipeline.Create</c> の既定は「何もしない」）。
    /// 繋がない配線でも<b>利用者への文言は同じ</b>であることを、この器で確かめる。
    /// </remarks>
    public AccountingSubmitPipeline PipelineWithoutLog { get; }

    /// <summary>「訂正する」「取り消す」の会計側（識別子は型、トランザクションは呼び出し側）。</summary>
    public JournalAmendmentService AmendmentService { get; }

    /// <summary>
    /// <b>別の日の</b>「訂正する」「取り消す」。<see cref="Now"/> の翌日にやり直す、といった検体のために、
    /// 時計だけを差し替えた同じ配線を作る（DB・認証は共有）。
    /// </summary>
    public JournalAmendmentService AmendmentServiceAt(DateTimeOffset now)
        => JournalAmendmentService.Create(Accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(now), Authentication);

    /// <summary>
    /// 「訂正する」「取り消す」の入口。<b>コントローラが呼ぶのと同じもの</b>で、
    /// 識別子の解釈・トランザクション・結果への写像まで含む（ADR-0016）。
    /// </summary>
    public JournalAmendmentEndpoint Amendment { get; }

    /// <summary>
    /// SQL を流す直前に呼ばれる。<b>例外を返すとその 1 文が失敗する</b>（途中で落とす仕掛け）。
    /// </summary>
    public Func<string, Exception?>? FailBeforeStatement
    {
        get => Accessor.FailBeforeStatement;
        set => Accessor.FailBeforeStatement = value;
    }

    /// <summary>
    /// 本番（<c>CustomizedModuleDataIO.SubmitAsync</c>）を<b>模した</b>形で 1 回の保存を通す。
    /// <b>トランザクションで包む。</b> 例外で巻き戻ることまで含めないと、
    /// 会計コアが最も頼っている性質だけが検査されない。
    /// <b>ただし本番でこれを張るのは CLB 側</b>で、<c>DbTransactionScope</c> は通らない。
    /// ここは「同じ形」ではなく「同じ結果になるように模したもの」である。
    /// </summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        return await DbTransactionScope.RunAsync(Accessor, () => Pipeline.SubmitAsync(transactionData, save));
    }

    /// <summary>
    /// 「訂正する」「取り消す」を本番（コントローラ）と同じ形で呼ぶ。
    /// </summary>
    /// <remarks>
    /// <b>トランザクションで包む。</b> 訂正は「取消を計上する」「再計上の下書きを作る」の
    /// 2 つを 1 操作として行うので、途中で失敗したときに<b>取消だけが残らない</b>ことが
    /// この機能の要件そのものである。オートコミットで走らせるとそこだけ検査されない。
    /// </remarks>
    public async Task<T> AmendAsync<T>(Func<JournalAmendmentService, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await DbTransactionScope.RunAsync(Accessor, () => operation(AmendmentService));
    }

    /// <summary>
    /// CLB の保存の代わり。<b>関門が書き換えた <see cref="ModuleData"/> のとおりに書く。</b>
    /// 状態も入力年月日も送られてきた値をそのまま使うので、関門が下書きに戻し忘れれば
    /// DDL のトリガに弾かれ、入力年月日を打ち忘れれば NOT NULL に弾かれる。
    /// </summary>
    public Func<Task<List<ModuleSubmitResult>>> Saving(
        ModuleData entry, params (string DebitCredit, string AccountCode, long Amount)[] lines)
        => () =>
        {
            var status = (entry.Fields["Status"] as SelectFieldData)?.Value ?? "draft";
            var enteredAt = (entry.Fields["EnteredAt"] as DateTimeFieldData)?.Value;

            // **摘要も送られてきた値だけを書く。** 既定を差し込むと、
            // **本番なら摘要の関門が拒む保存を、検体だけが通してしまう**（2026-09-08 の自己レビュー）。
            var description = (entry.Fields.GetValueOrDefault("Description") as TextFieldData)?.Value;
            var id = InsertEntry(status, enteredAt, description);

            var lineNo = 0;
            foreach (var (debitCredit, accountCode, amount) in lines)
            {
                InsertLine(id, ++lineNo, debitCredit, accountCode, amount);
            }

            var submittedId = (entry.Fields["Id"] as IdFieldData)?.Value
                ?? throw new InvalidOperationException("保存する伝票に Id が無い。");

            // **本物の CLB は SourceId / DestinationId で返す。** TemporaryIdMap には入らない。
            // ここを取り違えていたせいで、新規作成の画面から計上すると必ず落ちる不具合を
            // テストが 1 度も捕まえられなかった（qa/03 L-10）。
            return Task.FromResult(new List<ModuleSubmitResult>
            {
                new() { SourceId = submittedId, DestinationId = Text(id.Value) },
            });
        };

    /// <summary>
    /// 伝票番号の採番を、<b>行の識別子とずれた値から始める</b>。
    /// </summary>
    /// <remarks>
    /// <b>既定では伝票 1 件目の <c>id</c> も <c>entry_no</c> も 1 になる。</b> そのままだと
    /// 「番号を出しているつもりで識別子を出している」実装と区別が付かず、
    /// 文言や API の表明が全部素通りする（qa/03 L-02 の縮退）。
    /// 実機では <c>id = 33</c> が「伝票 27」のようにずれているので、
    /// <b>ずれている状態こそが本番の姿</b>である。
    /// </remarks>
    public void StartEntryNumbersAt(int first)
        => Execute($"update journal_entry_sequences set next_entry_no = {first}");

    /// <summary>
    /// CLB の削除の代わり。<b>伝票と明細を実際に消す</b>（親の <c>DeleteTogether</c> と同じ形）。
    /// </summary>
    /// <remarks>
    /// <b>「削除が通る」を、何も消さない保存で表明しない。</b> 空リストを返すだけの偽物を渡すと、
    /// 関門が止めなかったことしか言えず、<b>行が消えることは一度も検査されない</b>
    /// （2026-08-31 の自己レビューで、まさにその状態だった）。
    /// </remarks>
    public Func<Task<List<ModuleSubmitResult>>> Deleting(JournalEntryId id)
        => () =>
        {
            Execute($"delete from journal_lines where journal_entry_id = {id.Value}");
            Execute($"delete from journal_entries where id = {id.Value}");
            return Task.FromResult(new List<ModuleSubmitResult>());
        };

    /// <summary>
    /// 日付の列に、<b>CLB と同じ形</b>で書く。
    /// </summary>
    /// <remarks>
    /// CLB は日付の列に <c>"2023-10-01 00:00:00"</c> と<b>時刻付き</b>で書く（2026-08-26 実測）。
    /// テストが素の <c>"2023-10-01"</c> を入れると、
    /// <b>文字列で比べている検査が本番では外れるのにテストでは通る</b>（qa/03 L-12）。
    /// </remarks>
    public static string DateLiteral(string date) => $"'{date} 00:00:00'";

    /// <summary>摘要の既定値。<b>計上には要る</b>ので、下書きにも既定で入れる（docs/10 §4-2-1）。</summary>
    /// <remarks>
    /// <para><b>空の摘要を試すテストは <c>description: null</c> と書く。</b> 既定を空のままにすると、
    /// 摘要を要求する関門を入れた日に<b>計上を通すテストが全部赤になる</b>ので、
    /// 「摘要が要る」ことを検査しているテストと、そうでないテストの区別が付かなくなる。</para>
    /// <para><b>ドメイン側の <c>AccountingFixture.DefaultDescription</c> とは別の字にしてある</b>——
    /// 同じにすると、層をまたいで値が漏れていても気づけない（qa/03 L-02 の縮退）。</para>
    /// </remarks>
    public const string DefaultDescription = "9 月分の通信費";

    /// <summary>下書きの伝票を 1 件入れて、その識別子を返す。</summary>
    public JournalEntryId InsertDraft(
        string transactionDate = "2026-08-24",
        string postingDate = "2026-08-24",
        string entryType = "normal",
        JournalEntryId? originalEntryId = null,
        FiscalYearId? fiscalYearId = null,
        string? description = DefaultDescription,
        long? partnerId = null)
    {
        // 訂正・取消は原仕訳が要る（I-06）。DDL の CHECK は INSERT の時点で効くので、
        // 後から UPDATE で足すことはできない。
        var original = originalEntryId is JournalEntryId value ? Text(value.Value) : "null";
        var year = (fiscalYearId ?? FiscalYear).Value;

        Execute($"""
            insert into journal_entries
                (fiscal_year_id, transaction_date, posting_date, status, entry_type,
                 original_entry_id, description, entered_at, partner_id)
            values ({year}, {DateLiteral(transactionDate)}, {DateLiteral(postingDate)}, 'draft', '{entryType}',
                    {original}, {TextLiteral(description)}, '2026-08-24 13:00:00',
                    {(partnerId is long partner ? Text(partner) : "null")})
            """);

        return new JournalEntryId(Scalar<long>("select last_insert_rowid()"));
    }

    /// <summary>SQL の文字列リテラル。<b>null は NULL に落とす</b>（空文字にしない。docs/10 §4-4）。</summary>
    private static string TextLiteral(string? value)
        => value is null ? "null" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private JournalEntryId InsertEntry(string status, DateTime? enteredAt, string? description)
    {
        var entered = enteredAt is DateTime value
            ? $"'{value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)}'"
            : "null";

        Execute($"""
            insert into journal_entries
                (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            values ({FiscalYear.Value}, {DateLiteral("2026-08-24")}, {DateLiteral("2026-08-24")}, '{status}', 'normal',
                    {TextLiteral(description)}, {entered})
            """);

        return new JournalEntryId(Scalar<long>("select last_insert_rowid()"));
    }

    /// <summary>明細を 1 行足す。勘定科目と税区分はコードで引く（初期データの id を書き写さない）。</summary>
    public void InsertLine(
        JournalEntryId entryId,
        int lineNo,
        string debitCredit,
        string accountCode,
        long amount,
        string taxCategoryCode = "OUT",
        string? departmentCode = null)
    {
        var department = departmentCode is null
            ? "null"
            : $"(select id from departments where code = '{departmentCode}')";

        Execute($"""
            insert into journal_lines
                (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, department_id)
            values ({entryId.Value}, {lineNo}, '{debitCredit}',
                    (select id from accounts where code = '{accountCode}'),
                    {amount},
                    (select id from tax_categories where code = '{taxCategoryCode}'),
                    {department})
            """);
    }

    /// <summary>行番号から明細の識別子を引く。</summary>
    public long LineIdOf(JournalEntryId entryId, int lineNo)
        => Scalar<long>($"select id from journal_lines where journal_entry_id = {entryId.Value} and line_no = {lineNo}");

    /// <summary>
    /// 明細を、<b>保存の関門が通したとおりの値で</b> 1 行足す。
    /// </summary>
    /// <remarks>
    /// <b>「関門の受理集合が DB の受理集合に収まっているか」を、実際に書いて確かめるための口</b>
    /// （qa/03 L-14 の処方）。値を作り直さずに <see cref="ModuleData"/> から読むので、
    /// 関門が通した値を DDL が拒めば、ここで落ちる。
    /// </remarks>
    public void InsertLine(JournalEntryId entryId, ModuleData line)
        => Execute($"""
            insert into journal_lines
                (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            values ({entryId.Value}, {ValuesOf(line)})
            """);

    /// <summary>
    /// 明細の値を <c>line_no/debit_credit/account_id/amount/tax_category_id</c> の順に並べた文字列。
    /// </summary>
    /// <remarks>
    /// <b>書いた値と読み戻した値を、1 つの表明で record ごと比べるために使う</b>（qa/03 L-04）。
    /// 列ごとに数えると、取り違えた 2 列が打ち消し合っても気づけない。
    /// </remarks>
    public static string ValuesOf(ModuleData line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return string.Join(", ", [
            $"{(line.Fields["LineNo"] as NumberFieldData)?.Value}",
            $"'{(line.Fields["DebitCredit"] as SelectFieldData)?.Value}'",
            $"{(line.Fields["Account"] as LinkFieldData)?.Value}",
            $"{(line.Fields["Amount"] as NumberFieldData)?.Value}",
            $"{(line.Fields["TaxCategory"] as LinkFieldData)?.Value}",
        ]);
    }

    /// <summary>DB に書かれた明細を、<see cref="ValuesOf"/> と同じ並びで読み戻す。</summary>
    public string StoredLine(JournalEntryId entryId, int lineNo)
        => Scalar<string>($"""
            select line_no || ', ''' || debit_credit || ''', ' || account_id || ', ' || amount
                   || ', ' || tax_category_id
              from journal_lines
             where journal_entry_id = {entryId.Value} and line_no = {lineNo}
            """);

    /// <summary>
    /// 会計年度をもう 1 本足す。伝票番号が年度ごとの連番であること（I-17）の検査に使う。
    /// <b>月次の会計期間も一緒に作る。</b> 期間が無い年度には計上できないので、
    /// 年度だけ足しても検証を通らない（I-03）。
    /// </summary>
    public FiscalYearId InsertFiscalYear(string code, string startDate, string endDate)
    {
        Execute($"""
            insert into fiscal_years (code, label, start_date, end_date, status)
            values ('{code}', '{code} 期', '{startDate}', '{endDate}', 'open')
            """);

        var id = new FiscalYearId(Scalar<long>("select last_insert_rowid()"));
        var start = DateOnly.Parse(startDate, CultureInfo.InvariantCulture);

        for (var month = 0; month < 12; month++)
        {
            var from = start.AddMonths(month);
            var to = from.AddMonths(1).AddDays(-1);
            Execute($"""
                insert into accounting_periods (fiscal_year_id, start_date, end_date, status)
                values ({id.Value}, '{from:yyyy-MM-dd}', '{to:yyyy-MM-dd}', 'open')
                """);
        }

        return id;
    }

    /// <summary>計上済みの仕訳を 1 件作る（取消の相手として使う）。</summary>
    public JournalEntryId InsertPosted(
        int entryNo,
        string? description,
        string transactionDate,
        params (string DebitCredit, string AccountCode, long Amount)[] lines)
        => InsertPosted(entryNo, description, transactionDate, null, lines);

    /// <summary>取引先つきの計上済み仕訳（取消・訂正で取引先が写ることの検査に使う）。</summary>
    /// <remarks>
    /// <b><paramref name="description"/> に null を渡すと「摘要の無い計上済み」を作る。</b>
    /// いまの製品では作れない形だが、<b>規則より前に計上された行が稼働 DB に 2 件あり</b>
    /// （伝票番号 1・12。docs/10 §4-2-1）、その伝票も取り消せなければならない。
    /// そのときだけ計上のトリガを外す（<see cref="TestDatabase.WithoutTrigger"/>）。
    /// </remarks>
    public JournalEntryId InsertPosted(
        int entryNo,
        string? description,
        string transactionDate,
        long? partnerId,
        params (string DebitCredit, string AccountCode, long Amount)[] lines)
    {
        // **摘要は下書きを作るときに渡す。** 後から UPDATE で足す形にすると、
        // null（＝摘要なし）を渡したときに InsertDraft の既定が残り、
        // **「摘要の無い原仕訳」のつもりのテストが摘要つきの伝票を検査する**（2026-09-08 の自己レビュー）。
        var id = InsertDraft(
            transactionDate: transactionDate, postingDate: transactionDate, description: description);

        if (partnerId is long partner)
        {
            Execute($"update journal_entries set partner_id = {partner} where id = {id.Value}");
        }

        var lineNo = 0;
        foreach (var (debitCredit, accountCode, amount) in lines)
        {
            InsertLine(id, ++lineNo, debitCredit, accountCode, amount);
        }

        // 下書きとして書いてから状態を進める（DDL のトリガが唯一許す順序）。
        var post = $"""
            update journal_entries
               set status = 'posted', entry_no = {entryNo}, posted_at = '2026-08-24 13:00:00'
             where id = {id.Value}
            """;

        if (description is null)
        {
            TestDatabase.WithoutTrigger(
                connection, "trg_journal_entries_description_required_when_posted", post);
        }
        else
        {
            Execute(post);
        }

        // 採番も一緒に進める。進めないと、次の計上が同じ番号を取って一意制約に当たる。
        Execute($"""
            update journal_entry_sequences set next_entry_no = {entryNo + 1}
             where fiscal_year_id = {FiscalYear.Value} and next_entry_no <= {entryNo}
            """);

        return id;
    }

    /// <summary>
    /// <b>補助科目を使わない科目に補助科目が付いた計上済みの伝票</b>を 1 件作る（ADR-0038 §3）。
    /// </summary>
    /// <remarks>
    /// <b>いまの製品では作れない形である。</b> 規則より前に計上された行が稼働 DB に 1 行あり
    /// （伝票 36。qa/04 の 2026-09-08）、<b>その伝票を取り消せることがこの規則の免除の根拠</b>なので、
    /// 検体が要る。そのときだけ計上のトリガを外す（<see cref="TestDatabase.WithoutTrigger"/>）。
    /// </remarks>
    public JournalEntryId InsertPostedWithSubAccountOnUnusedAccount(int entryNo, string transactionDate)
    {
        // 1100（現金）は「補助科目を使う」がオフのまま。そこに補助科目を作る（マスタ側の関門はまだ無い）。
        Execute("""
            insert into sub_accounts (account_id, code, name)
            values ((select id from accounts where code = '1100'), 'X001', '規則より前の補助科目')
            """);

        var id = InsertDraft(transactionDate: transactionDate, postingDate: transactionDate, description: "規則より前の伝票");
        Execute($"""
            insert into journal_lines
                (journal_entry_id, line_no, debit_credit, account_id, sub_account_id, amount, tax_category_id)
            values ({id.Value}, 1, 'debit',
                    (select id from accounts where code = '1100'),
                    (select id from sub_accounts where code = 'X001'),
                    1000,
                    (select id from tax_categories where code = 'OUT'))
            """);
        InsertLine(id, 2, "credit", "2200", 1000);

        TestDatabase.WithoutTrigger(
            connection,
            "trg_journal_entries_sub_account_presence_when_posted",
            $"""
            update journal_entries
               set status = 'posted', entry_no = {entryNo}, posted_at = '2026-08-24 13:00:00'
             where id = {id.Value}
            """);

        Execute($"""
            update journal_entry_sequences set next_entry_no = {entryNo + 1}
             where fiscal_year_id = {FiscalYear.Value} and next_entry_no <= {entryNo}
            """);

        return id;
    }

    /// <summary>
    /// <b>取引先を要する科目に取引先の無い計上済みの伝票</b>を 1 件作る（docs/10 §6-2）。
    /// </summary>
    /// <remarks>
    /// <b>いまの製品では作れない形である。</b> 規則より前に計上された行が稼働 DB に実在し
    /// （件数と数え方は qa/04）、
    /// <b>それらを取り消せることがこの規則の免除の根拠</b>なので、検体が要る。
    /// そのときだけ計上のトリガを外す（<see cref="TestDatabase.WithoutTrigger"/>）。
    /// </remarks>
    public JournalEntryId InsertPostedWithoutPartnerOnRequiringAccount(int entryNo, string transactionDate)
    {
        // 2100（買掛金）は初期データで「取引先を要する」が立っている（Designer/seed/004_accounts.sql）。
        // **フィクスチャで立て直さない**——初期データと規則が食い違ったらここで落ちてほしい。
        // **その「落ちてほしい」を機械にする**——下でトリガを外して計上するので、
        // 立っていなくても計上は成功してしまう（自己レビューで指摘された。2026-09-08）。
        if (Scalar<long>("select requires_partner from accounts where code = '2100'") != 1)
        {
            throw new InvalidOperationException(
                "初期データの 2100（買掛金）に requires_partner が立っていない。"
                + "この検体は「規則より前に計上された伝票」を作るためのものなので、立っていないと意味が無い。");
        }

        var id = InsertDraft(transactionDate: transactionDate, postingDate: transactionDate, description: "規則より前の伝票");
        InsertLine(id, 1, "debit", "1100", 1000);
        InsertLine(id, 2, "credit", "2100", 1000);

        TestDatabase.WithoutTrigger(
            connection,
            "trg_journal_entries_partner_presence_when_posted",
            $"""
            update journal_entries
               set status = 'posted', entry_no = {entryNo}, posted_at = '2026-08-24 13:00:00'
             where id = {id.Value}
            """);

        Execute($"""
            update journal_entry_sequences set next_entry_no = {entryNo + 1}
             where fiscal_year_id = {FiscalYear.Value} and next_entry_no <= {entryNo}
            """);

        return id;
    }

    /// <summary>コードから補助科目の識別子を引く。</summary>
    public SubAccountId SubAccountOf(string accountCode, string code)
        => new(Scalar<long>($"""
            select s.id from sub_accounts s join accounts a on a.id = s.account_id
             where a.code = '{accountCode}' and s.code = '{code}'
            """));

    /// <summary>取消の下書きを 1 件作る（明細は入れない。中身はサーバが決める）。</summary>
    public JournalEntryId InsertReversalDraft(JournalEntryId originalId, string postingDate = "2026-08-25")
        => InsertDraft(postingDate: postingDate, entryType: "reversal", originalEntryId: originalId);

    /// <summary>
    /// 再計上（訂正）の下書きを 1 件作る。
    /// <b>中身は利用者が決める</b>ので、明細は呼び出し側が入れる。
    /// </summary>
    public JournalEntryId InsertCorrectionDraft(
        JournalEntryId originalId, string postingDate = "2026-08-25", string transactionDate = "2026-08-24")
        => InsertDraft(
            transactionDate: transactionDate,
            postingDate: postingDate,
            entryType: "correction",
            originalEntryId: originalId);

    /// <summary>伝票の現在の状態（計上されたか・巻き戻ったかの確認に使う）。</summary>
    public string StatusOf(JournalEntryId id)
        => Scalar<string>($"select status from journal_entries where id = {id.Value}");

    /// <summary>この原仕訳を指す伝票の件数（種別・状態ごと）。</summary>
    public long CountAmendments(JournalEntryId originalId, string entryType, string status = "posted")
        => Scalar<long>($"""
            select count(*) from journal_entries
            where original_entry_id = {originalId.Value} and entry_type = '{entryType}' and status = '{status}'
            """);

    /// <summary>取引先を 1 件足す（初期データには 0 件しか無い）。</summary>
    public long InsertPartner(string code = "P001", string name = "株式会社れい")
    {
        Execute($"insert into partners (code, name) values ('{code}', '{name}')");
        return Scalar<long>("select last_insert_rowid()");
    }

    /// <summary>補助科目を 1 件足す。勘定科目はコードで引く。</summary>
    public long InsertSubAccount(string accountCode, string code = "S001", string name = "本店")
    {
        Execute($"""
            insert into sub_accounts (account_id, code, name)
            values ((select id from accounts where code = '{accountCode}'), '{code}', '{name}')
            """);
        return Scalar<long>("select last_insert_rowid()");
    }

    /// <summary>コードから識別子を引く（初期データの id をテストに書き写さない）。</summary>
    public AccountId AccountOf(string code)
        => new(Scalar<long>($"select id from accounts where code = '{code}'"));

    public DepartmentId DepartmentOf(string code)
        => new(Scalar<long>($"select id from departments where code = '{code}'"));

    public TaxCategoryId TaxCategoryOf(string code)
        => new(Scalar<long>($"select id from tax_categories where code = '{code}'"));

    public void Execute(string sql) => TestDatabase.Execute(connection, sql);

    public T Scalar<T>(string sql) => TestDatabase.ScalarOf<T>(connection, sql);

    public string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    public void Dispose() => connection.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// 認証の代わり。<b>呼ばれるたびに読み直す</b>ので、テストが途中で
    /// <see cref="CurrentUserId"/> を差し替えると、その後の計上に反映される
    /// （別の人が取り消す・識別子が壊れている、の検査に使う）。
    /// </summary>
    private sealed class TestAuthenticationContext(Func<string> currentUserId) : IAuthenticationContext
    {
        public Task<string> GetCurrentUserIdAsync() => Task.FromResult(currentUserId());
    }
}
