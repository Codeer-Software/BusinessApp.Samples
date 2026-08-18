// 案件マスタ: SES 契約条件の表示制御
// 種別 (ProjectType) が "ses" のときだけ SES 契約条件（月額・精算幅・単価）を表示する。
// 値は消さない（種別を誤って切り替えても入力済みの契約条件が失われないように）。
// 書込は 経理 ∨ 部長（ADR-0046。ポリシーは UserWriteCondition に宣言）。ただし
// SES 精算条件（単価・精算幅）は損益に直結する経理項目のため、部長には読取専用にする。

void UpdateSesVisibility()
{
    var isSes = ProjectType.Value == "ses";
    SesMonthlyRateLabel.IsVisible = isSes;
    SesMonthlyRate.IsVisible = isSes;
    SesRangeLabel.IsVisible = isSes;
    SesLowerHours.IsVisible = isSes;
    SesUpperHours.IsVisible = isSes;
    SesRateLabel.IsVisible = isSes;
    SesDeductRate.IsVisible = isSes;
    SesExcessRate.IsVisible = isSes;
}

void Detail_OnAfterInitialization()
{
    // マスタの有効フラグは DB 既定が 1 だが、**CLB の Boolean は新規作成で常に未チェック**になる
    // （CLB-017・実測）。既定が効いていると思って保存すると、作った直後から無効なマスタができ、
    // 参照側のピッカーに出てこない。新規のときだけ明示的に立てる
    if (IsNewData && IsActive.Value != true) { IsActive.Value = true; }
    UpdateSesVisibility();

    // SES 精算条件は経理のみ編集可（部長は基本情報のみ・ADR-0046）
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        SesMonthlyRate.IsViewOnly = true;
        SesLowerHours.IsViewOnly = true;
        SesUpperHours.IsViewOnly = true;
        SesDeductRate.IsViewOnly = true;
        SesExcessRate.IsViewOnly = true;
    }
}

void ProjectType_OnDataChanged()
{
    UpdateSesVisibility();
}
