// 仕訳明細の画面。
//
// **会計の判断はここに書かない**（ADR-0008）。ここにあるのは打鍵を減らすための補助だけで、
// 税区分・部門・貸借の正しさはサーバ側の関門が AccountingCore を呼んで判定する。

// 勘定科目を選んだら、その科目に属さない補助科目を落とし、既定税区分を入れる。
void Account_OnDataChanged()
{
    DropForeignSubAccount();
    FillDefaultTaxCategory();
}

// 選んである補助科目が、いまの勘定科目のものでなくなったら空にする。
//
// **補助科目の候補は勘定科目で絞ってある**（`SubAccount` の検索条件の
// `FieldVariableMatchCondition`）。絞りは候補を出すときにしか効かないので、
// **科目を選び直したあとの古い値は自分で落とす**。落とさないと、
// 候補に出ていない補助科目が欄に残ったまま保存でき、計上の関門
// （`E-SUBACCOUNT-MISMATCH`）に当たるまで気づけない。
//
// **科目を空にしたときも落とす。** 補助科目は科目にぶら下がるものなので、
// 親が無いのに子だけ残っている状態を作らない。
void DropForeignSubAccount()
{
    if (string.IsNullOrEmpty(SubAccount.Value)) return;

    if (!string.IsNullOrEmpty(Account.Value))
    {
        var searcher = new ModuleSearcher<SubAccount>();
        searcher.AddEquals(e => e.Id.Value, SubAccount.Value);

        foreach (var subAccount in searcher.Execute())
        {
            if (subAccount.Account.Value == Account.Value) return;
        }
    }

    SubAccount.Value = "";
    SubAccount.DisplayText = "";
}

// その科目の既定税区分を入れる。
//
// **入っている税区分は上書きしない。** 同じ科目でも取引ごとに税区分は変わりうるので
// （docs/06 §1）、既定はあくまで初回の入力を省くためのものである。
// 利用者が選び直したものを、科目を触るたびに戻してはいけない。
void FillDefaultTaxCategory()
{
    if (string.IsNullOrEmpty(Account.Value)) return;
    if (!string.IsNullOrEmpty(TaxCategory.Value)) return;

    var searcher = new ModuleSearcher<Account>();
    searcher.AddEquals(e => e.Id.Value, Account.Value);

    foreach (var account in searcher.Execute())
    {
        if (string.IsNullOrEmpty(account.DefaultTaxCategory.Value)) return;

        TaxCategory.Value = account.DefaultTaxCategory.Value;
        TaxCategory.DisplayText = account.DefaultTaxCategory.DisplayText;
        return;
    }
}
