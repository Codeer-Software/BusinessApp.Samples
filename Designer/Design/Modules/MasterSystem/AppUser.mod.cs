// AppUser.mod.cs — ユーザーマスタ（Q4: 退職者アカウントの無効化）
// 有効フラグ(is_active)の実体は ddl/290 参照。ログイン拒否は active_app_users ビュー＋
// appsettings.json の PasswordCheckUserTableInfo.TableName で行う（アプリ層の実装なし）。

void Detail_OnAfterInit()
{
    // 新規作成時は有効で始める（無効な新規ユーザーを作る業務はない）
    if (IsNewData && IsActive.Value != true)
    {
        IsActive.Value = true;
    }
}
