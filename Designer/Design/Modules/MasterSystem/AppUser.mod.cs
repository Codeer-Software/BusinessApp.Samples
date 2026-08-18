// AppUser.mod.cs — ユーザーマスタ（Q4: 退職者アカウントの無効化）
// 有効フラグ(is_active)の実体は ddl/290 参照。ログイン拒否は active_app_users ビュー＋
// appsettings.json の PasswordCheckUserTableInfo.TableName で行う（アプリ層の実装なし）。

void Detail_OnAfterInit()
{
    ShowPasswordPolicy();

    // 新規作成時は有効で始める（無効な新規ユーザーを作る業務はない）
    if (IsNewData && IsActive.Value != true)
    {
        IsActive.Value = true;
    }
    // 全社員機能（経費精算・工数入力）は既定 ON のオプトアウト型（ADR-0043）
    if (IsNewData && CanUseExpense.Value != true)
    {
        CanUseExpense.Value = true;
    }
    if (IsNewData && CanUseTimesheet.Value != true)
    {
        CanUseTimesheet.Value = true;
    }
}

// パスワードの条件を欄の下に出す（ADR-0059）。文言はサーバの GuidanceText() が唯一の出どころで、
// ポリシーを変えると画面の説明文も自動で追随する（マスタの値と説明がズレない）
string passwordGuidance = "";

void ShowPasswordPolicy()
{
    passwordGuidance = "";
    PasswordHintLabel.Text = "";
    PasswordHintLabel.IsVisible = false;
    var result = WebApiService.Get("/api/password/policy");
    if (result.StatusCode != 200) return;
    var guidance = $"{result.JsonObject.guidance}";
    if (guidance == "") return;
    passwordGuidance = guidance;
    ShowPasswordGuidance();
}

void ShowPasswordGuidance()
{
    PasswordHintLabel.Color = "";
    PasswordHintLabel.Text = $"条件: {passwordGuidance}（空欄のままにするとパスワードは変更されません）";
    PasswordHintLabel.IsVisible = true;
}

// 入力の都度（フォーカスが外れたとき）に条件を確かめて、その場で赤く知らせる。
// 判定はサーバの PasswordPolicyService が唯一の実装で、ここはその呼び出し。
// **これは通知であって関門ではない**——保存を止めるのは CustomizedModuleDataIO（サーバ）の仕事。
// 当初はフィールドの OnValidateInput に置いたが、保存が無言で止まる（エラー表示も通信も起きない）
// 挙動を実測したため、この repo で挙動が確立している OnDataChanged に替えた。
// 空欄＝「パスワードは変更しない」なので検証しない（ハッシュ化側も同じ扱い）。
void Password_OnDataChanged()
{
    var pw = パスワード.Value;
    if (string.IsNullOrEmpty(pw))
    {
        ShowPasswordGuidance();
        return;
    }

    var body = new JsonObject();
    body.Password = pw;
    body.UserName = ユーザー識別名.Value;
    var result = WebApiService.Post("/api/password/validate", body);
    if (result.StatusCode != 200) return;

    var data = result.JsonObject;
    var ok = $"{data.ok}";
    if (ok == "True" || ok == "true")
    {
        ShowPasswordGuidance();
        return;
    }
    PasswordHintLabel.Color = "#dc3545";
    PasswordHintLabel.Text = $"{data.message}";
    PasswordHintLabel.IsVisible = true;
}

// 自分自身を無効にできない（BUG-0386）。
// 無効化はアプリではなく認証ビュー `active_app_users` で効くので、**次のログインから本人が入れなくなる**。
// システム管理者が 1 名しかいない環境で自分を無効にすると、システム管理のフレームに入れる人間がゼロになり、
// 画面からは復旧できない（SQL 直叩きしかない）。管理者権限のガードと対で必要だった
void IsActive_OnDataChanged()
{
    if (IsNewData) return;
    if (IsActive.Value == true) return;
    if ($"{Id.Value}" != $"{CurrentUser.Id.Value}") return;
    IsActive.Value = true;
    Toaster.Error("自分自身を無効にはできません（ロックアウト防止）。別の管理者で操作してください。");
}

// 自分自身のシステム管理者権限は外せない（唯一の管理者が自分を降格するとロックアウトするため）
void IsSysAdmin_OnDataChanged()
{
    if (IsNewData) return;
    if (IsSysAdmin.Value == true) return;
    if ($"{Id.Value}" != $"{CurrentUser.Id.Value}") return;
    IsSysAdmin.Value = true;
    Toaster.Error("自分自身のシステム管理者権限は外せません（ロックアウト防止）。別の管理者で操作してください。");
}
