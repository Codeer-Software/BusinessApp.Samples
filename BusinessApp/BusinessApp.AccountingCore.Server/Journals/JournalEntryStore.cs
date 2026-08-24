namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Partners;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Shared;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 保存済みの仕訳を読み、計上の印を付ける。
/// </summary>
/// <remarks>
/// <para><b>検証は「送られてきた差分」ではなく「保存された姿」に対して行う。</b>
/// CLB が送ってくる <c>ModuleData</c> は<b>変更されたフィールドだけ</b>を持つので
/// （qa/01 F-11・F-12）、差分だけを見て貸借一致や期間を判定すると、既存の下書きを開いて
/// 状態だけ変えた保存で必ず誤判定する。いったん下書きとして書かせてから、
/// ここで完全な姿を読み直す。</para>
/// <para>型付き識別子への変換をこの境界に閉じ込める（ADR-0014）。</para>
/// </remarks>
public sealed class JournalEntryStore(IDbAccessor dbAccessor, string dataSourceName)
{
    /// <summary>伝票と明細を丸ごと読む。</summary>
    public async Task<JournalEntry> LoadAsync(JournalEntryId id)
    {
        var rows = await QueryAsync(
            """
            select id, fiscal_year_id, entry_no, transaction_date, posting_date, status, entry_type,
                   original_entry_id, description, partner_id, source_component, source_document_id,
                   idempotency_key, entered_at, posted_at
            from journal_entries where id = @p1
            """,
            id.Value);

        var row = rows.FirstOrDefault()
            ?? throw new InvalidOperationException($"仕訳 {id.Value} が見つからない。");

        return new JournalEntry
        {
            Id = id,
            FiscalYearId = new FiscalYearId(DbValue.ToLong(row["fiscal_year_id"])),
            EntryNo = DbValue.ToNullableInt(row["entry_no"]),
            TransactionDate = DbValue.ToDate(row["transaction_date"]),
            PostingDate = DbValue.ToDate(row["posting_date"]),
            Status = DbValue.ToEnum<EntryStatus>(row["status"]),
            EntryType = DbValue.ToEnum<EntryType>(row["entry_type"]),
            OriginalEntryId = DbValue.ToNullableLong(row["original_entry_id"]) is { } original
                ? new JournalEntryId(original) : null,
            Description = DbValue.ToNullableText(row["description"]),
            PartnerId = DbValue.ToNullableLong(row["partner_id"]) is { } partner
                ? new PartnerId(partner) : null,
            SourceComponent = DbValue.ToNullableText(row["source_component"]),
            SourceDocumentId = DbValue.ToNullableText(row["source_document_id"]),
            IdempotencyKey = DbValue.ToNullableText(row["idempotency_key"]),
            EnteredAt = DbValue.ToDateTimeOffset(row["entered_at"]),
            PostedAt = DbValue.ToNullableDateTimeOffset(row["posted_at"]),
            Lines = await LoadLinesAsync(id),
        };
    }

    private async Task<IReadOnlyList<JournalLine>> LoadLinesAsync(JournalEntryId id)
    {
        var rows = await QueryAsync(
            """
            select line_no, debit_credit, account_id, sub_account_id, department_id, partner_id,
                   partner_name_snapshot, amount, tax_category_id, tax_treatment, tax_point,
                   applied_rule_version, is_tax_line, parent_line_no, item_description,
                   book_only_deduction, evidence_ref
            from journal_lines where journal_entry_id = @p1 order by line_no
            """,
            id.Value);

        return rows.Select(row => new JournalLine
        {
            LineNo = DbValue.ToInt(row["line_no"]),
            DebitCredit = DbValue.ToEnum<DebitCredit>(row["debit_credit"]),
            AccountId = new AccountId(DbValue.ToLong(row["account_id"])),
            SubAccountId = DbValue.ToNullableLong(row["sub_account_id"]) is { } sub
                ? new SubAccountId(sub) : null,
            DepartmentId = DbValue.ToNullableLong(row["department_id"]) is { } department
                ? new DepartmentId(department) : null,
            PartnerId = DbValue.ToNullableLong(row["partner_id"]) is { } partner
                ? new PartnerId(partner) : null,
            PartnerNameSnapshot = DbValue.ToNullableText(row["partner_name_snapshot"]),
            // **decimal のまま Yen に渡す。** long で受けると小数が黙って丸まり、
            // Yen が持っている「整数円でなければ例外」というガードを迂回する（I-01）。
            Amount = Yen.From(DbValue.ToDecimal(row["amount"])),
            TaxCategoryId = new TaxCategoryId(DbValue.ToLong(row["tax_category_id"])),
            TaxTreatment = DbValue.ToNullableEnum<TaxTreatment>(row["tax_treatment"]),
            TaxPoint = DbValue.ToNullableDate(row["tax_point"]),
            AppliedRuleVersion = DbValue.ToNullableText(row["applied_rule_version"]) is { } version
                ? new RuleVersion(version) : null,
            IsTaxLine = DbValue.ToBool(row["is_tax_line"]),
            ParentLineNo = DbValue.ToNullableInt(row["parent_line_no"]),
            ItemDescription = DbValue.ToNullableText(row["item_description"]),
            BookOnlyDeduction = DbValue.ToNullableText(row["book_only_deduction"]),
            EvidenceRef = DbValue.ToNullableText(row["evidence_ref"]),
        }).ToList();
    }

    /// <summary>
    /// 下書きに計上の印を付ける。<b>下書きにしか当たらない</b> ので、
    /// 二重計上も計上済みの書き換えもこの 1 文が防ぐ。
    /// </summary>
    /// <remarks>
    /// <para><b>版（<c>optimistic_locking</c>）は触らない。</b> 版は CLB が
    /// <c>OptimisticLockingFieldDesign</c> で進めるものなので、ここでも進めると
    /// 1 回の保存で 2 つ進み、画面が握っている版が保存直後から古くなる（qa/01 F-09）。</para>
    /// <para>採番は <see cref="EntryNumber"/> で受ける。生の <c>int</c> で受けると、
    /// 会計年度と組で意味を持つ番号が境界で裸になる（ADR-0014）。</para>
    /// </remarks>
    public async Task MarkPostedAsync(JournalEntryId id, EntryNumber entryNo, DateTimeOffset postedAt)
    {
        var affected = await dbAccessor.ExecuteAsync(
            dataSourceName,
            """
            update journal_entries
               set status = 'posted', entry_no = @p2, posted_at = @p3
             where id = @p1 and status = 'draft'
            """,
            new()
            {
                { "@p1", id.Value },
                { "@p2", entryNo.Value },
                { "@p3", AccountingTimeZone.ToWallClock(postedAt) },
            });

        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"仕訳 {id.Value} は下書きではないので計上できない。");
        }
    }

    /// <summary>この原仕訳を取り消す計上済みの反対仕訳が既にあるか（二重取消の検出）。</summary>
    public async Task<bool> HasReversalAsync(JournalEntryId originalId)
    {
        var rows = await QueryAsync(
            """
            select 1 from journal_entries
             where original_entry_id = @p1 and entry_type = 'reversal' and status = 'posted'
             limit 1
            """,
            originalId.Value);

        return rows.Count > 0;
    }

    /// <summary>
    /// 明細を入れ替える。<b>反対仕訳の明細はシステムが決める</b>ので、
    /// 送られてきた内容が何であれ、原仕訳を反転したものに置き換える。
    /// </summary>
    /// <remarks>
    /// 下書きにしか当てない。計上済みの明細は DDL のトリガが変更も削除も追加も止める。
    /// </remarks>
    public async Task ReplaceLinesAsync(JournalEntryId id, IReadOnlyList<JournalLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        await dbAccessor.ExecuteAsync(
            dataSourceName,
            "delete from journal_lines where journal_entry_id = @p1",
            new() { { "@p1", id.Value } });

        foreach (var line in lines)
        {
            await dbAccessor.ExecuteAsync(
                dataSourceName,
                """
                insert into journal_lines
                    (journal_entry_id, line_no, debit_credit, account_id, sub_account_id,
                     department_id, partner_id, partner_name_snapshot, amount, tax_category_id,
                     tax_treatment, tax_point, applied_rule_version, is_tax_line, parent_line_no,
                     item_description, book_only_deduction, evidence_ref)
                values (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10,
                        @p11, @p12, @p13, @p14, @p15, @p16, @p17, @p18)
                """,
                new()
                {
                    { "@p1", id.Value },
                    { "@p2", line.LineNo },
                    { "@p3", DbValue.ToSnakeCase(line.DebitCredit) },
                    { "@p4", line.AccountId.Value },
                    { "@p5", line.SubAccountId?.Value },
                    { "@p6", line.DepartmentId?.Value },
                    { "@p7", line.PartnerId?.Value },
                    { "@p8", line.PartnerNameSnapshot },
                    { "@p9", (long)line.Amount.Value },
                    { "@p10", line.TaxCategoryId.Value },
                    { "@p11", line.TaxTreatment is { } treatment ? DbValue.ToSnakeCase(treatment) : null },
                    { "@p12", line.TaxPoint?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
                    { "@p13", line.AppliedRuleVersion?.Value },
                    { "@p14", line.IsTaxLine ? 1 : 0 },
                    { "@p15", line.ParentLineNo },
                    { "@p16", line.ItemDescription },
                    { "@p17", line.BookOnlyDeduction },
                    { "@p18", line.EvidenceRef },
                });
        }
    }

    /// <summary>下書きの摘要を差し替える（取消の摘要はシステムが決める）。</summary>
    public async Task UpdateDescriptionAsync(JournalEntryId id, string? description)
        => await dbAccessor.ExecuteAsync(
            dataSourceName,
            "update journal_entries set description = @p2 where id = @p1 and status = 'draft'",
            new() { { "@p1", id.Value }, { "@p2", description } });

    private async Task<IReadOnlyList<IDictionary<string, object>>> QueryAsync(string sql, long parameter)
        => await dbAccessor.QueryAsync(
            dataSourceName, sql,
            new() { { "@p1", new ParamAndRawDbTypeName { Value = parameter } } });
}
