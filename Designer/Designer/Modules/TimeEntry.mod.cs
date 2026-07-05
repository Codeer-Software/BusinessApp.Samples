// TimeEntry.mod.cs — 工数入力
// 新規時の既定: 担当者=自分・日付=今日・時間=480分(8時間)。一覧の検索初期値も自分。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        if (UserRef.Value == null) { UserRef.Value = CurrentUser.Id.Value; }
        if (WorkDate.Value == null) { WorkDate.Value = DateOnly.FromDateTime(DateTime.Today); }
        if (Minutes.Value == null) { Minutes.Value = 480; }
    }
}

// 一覧の検索初期値: 担当者=自分 (サイドバーリンク経由で発火。権限フィルタではない)
void OnSearchInitialization()
{
    UserRef.SearchValue = CurrentUser.Id.Value;
}
