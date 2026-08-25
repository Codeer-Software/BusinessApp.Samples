namespace BusinessApp.AccountingCore.Server.Tests.Fixtures;

using System.Globalization;
using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Shared;
using BusinessApp.TestSupport;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;
using Microsoft.Data.Sqlite;

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
    private readonly SqliteDbAccessor accessor;

    /// <summary>
    /// 操作している人の識別子。ユーザーは認証部品のもので、このテスト DB にテーブルも FK も無いので
    /// 値は自由に選べる。<b>他の id（会計年度 1・伝票 1・伝票番号 1…）と衝突しない値にする</b>
    /// （縮退させると、取り違えても全テストが緑のままになる。qa/03 L-02）。
    /// </summary>
    public const long CurrentUser = 91;

    public AccountingServer()
    {
        connection = TestDatabase.CreateWithSeed();
        accessor = new SqliteDbAccessor(connection);

        var authentication = new TestAuthenticationContext(() => CurrentUserId);
        Authentication = authentication;
        MasterLoader = new AccountingMasterLoader(accessor, SqliteDbAccessor.DataSourceName);
        EntryStore = new JournalEntryStore(accessor, SqliteDbAccessor.DataSourceName);
        SequenceStore = new EntryNumberSequenceStore(accessor, SqliteDbAccessor.DataSourceName);
        Gate = JournalSubmitGate.Create(
            accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication);
        AmendmentService = JournalAmendmentService.Create(
            accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication);
        Amendment = JournalAmendmentEndpoint.Create(
            accessor, SqliteDbAccessor.DataSourceName, new FixedTimeProvider(Now), authentication);
    }

    /// <summary>
    /// 認証コンテキストが返す識別子。<b>テストが差し替えられる</b>
    /// （数値でない・空のときに posted_by が null になることの検査に使う）。
    /// </summary>
    public string CurrentUserId { get; set; } = CurrentUser.ToString(CultureInfo.InvariantCulture);

    /// <summary>認証の代わり（<see cref="CurrentUserId"/> を返す）。部品を手で組むテストが使う。</summary>
    public IAuthenticationContext Authentication { get; }

    public AccountingMasterLoader MasterLoader { get; }

    public JournalEntryStore EntryStore { get; }

    public EntryNumberSequenceStore SequenceStore { get; }

    public JournalSubmitGate Gate { get; }

    /// <summary>「訂正する」「取り消す」の会計側（識別子は型、トランザクションは呼び出し側）。</summary>
    public JournalAmendmentService AmendmentService { get; }

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
        get => accessor.FailBeforeStatement;
        set => accessor.FailBeforeStatement = value;
    }

    /// <summary>
    /// 本番（<c>CustomizedModuleDataIO.SubmitAsync</c>）と同じ形で 1 回の保存を通す。
    /// <b>トランザクションで包む。</b> 例外で巻き戻ることまで含めて本番と同じにしないと、
    /// 会計コアが最も頼っている性質だけが検査されない。
    /// </summary>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        return await DbTransactionScope.RunAsync(accessor, () => Gate.SubmitAsync(transactionData, save));
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
        return await DbTransactionScope.RunAsync(accessor, () => operation(AmendmentService));
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
            var id = InsertEntry(status, enteredAt);

            var lineNo = 0;
            foreach (var (debitCredit, accountCode, amount) in lines)
            {
                InsertLine(id, ++lineNo, debitCredit, accountCode, amount);
            }

            var submittedId = (entry.Fields["Id"] as IdFieldData)?.Value
                ?? throw new InvalidOperationException("保存する伝票に Id が無い。");

            var result = new ModuleSubmitResult();
            result.TemporaryIdMap[submittedId] = Text(id.Value);
            return Task.FromResult(new List<ModuleSubmitResult> { result });
        };

    /// <summary>下書きの伝票を 1 件入れて、その識別子を返す。</summary>
    public JournalEntryId InsertDraft(
        string transactionDate = "2026-08-24",
        string postingDate = "2026-08-24",
        string entryType = "normal",
        JournalEntryId? originalEntryId = null,
        FiscalYearId? fiscalYearId = null)
    {
        // 訂正・取消は原仕訳が要る（I-06）。DDL の CHECK は INSERT の時点で効くので、
        // 後から UPDATE で足すことはできない。
        var original = originalEntryId is JournalEntryId value ? Text(value.Value) : "null";
        var year = (fiscalYearId ?? FiscalYear).Value;

        Execute($"""
            insert into journal_entries
                (fiscal_year_id, transaction_date, posting_date, status, entry_type,
                 original_entry_id, entered_at)
            values ({year}, '{transactionDate}', '{postingDate}', 'draft', '{entryType}',
                    {original}, '2026-08-24 13:00:00')
            """);

        return new JournalEntryId(Scalar<long>("select last_insert_rowid()"));
    }

    private JournalEntryId InsertEntry(string status, DateTime? enteredAt)
    {
        var entered = enteredAt is DateTime value
            ? $"'{value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)}'"
            : "null";

        Execute($"""
            insert into journal_entries
                (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            values ({FiscalYear.Value}, '2026-08-24', '2026-08-24', '{status}', 'normal', {entered})
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
    public JournalEntryId InsertPosted(
        int entryNo,
        string? description,
        string transactionDate,
        long? partnerId,
        params (string DebitCredit, string AccountCode, long Amount)[] lines)
    {
        var id = InsertDraft(transactionDate: transactionDate, postingDate: transactionDate);

        // 摘要は下書きのうちに入れる。計上済みの変更はトリガが止める。
        if (description is not null)
        {
            Execute($"update journal_entries set description = '{description}' where id = {id.Value}");
        }

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
        Execute($"""
            update journal_entries
               set status = 'posted', entry_no = {entryNo}, posted_at = '2026-08-24 13:00:00'
             where id = {id.Value}
            """);

        // 採番も一緒に進める。進めないと、次の計上が同じ番号を取って一意制約に当たる。
        Execute($"""
            update journal_entry_sequences set next_entry_no = {entryNo + 1}
             where fiscal_year_id = {FiscalYear.Value} and next_entry_no <= {entryNo}
            """);

        return id;
    }

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
