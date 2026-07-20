// 案件マスタ: SES 契約条件の表示制御
// 種別 (ProjectType) が "ses" のときだけ SES 契約条件（月額・精算幅・単価）を表示する。
// 値は消さない（種別を誤って切り替えても入力済みの契約条件が失われないように）。

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
    UpdateSesVisibility();
}

void ProjectType_OnDataChanged()
{
    UpdateSesVisibility();
}
