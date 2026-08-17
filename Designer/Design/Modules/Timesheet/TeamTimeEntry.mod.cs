// TeamTimeEntry.mod.cs — チームの工数（課長・部長の閲覧専用ビュー / BUG-0217）
//
// DbTable は実テーブルではなく `team_time_entry_view`（DDL: Designer/ddl/620_team_time_entry_view.sql）。
// 「1 明細 × 閲覧可能者」のファンアウト・ビューで、viewer_user_id 列に閲覧者が並ぶ。
// CLB の DataReadCondition は自モジュールの列しか見られず「自分の行 ∨ 部下の行」の OR を書けないため、
// ビュー側で OR をほどいて `ViewerUser.Value == CurrentUser.Id.Value` の単一条件に落としている
// （approval_inbox_view / my_application_view と同じ形）。
//
// 見える範囲（正は department_members.role。departments.manager_user/director_user は全行 NULL の死列）:
//   本人   … 自分の工数
//   課長   … 自分が manager 行を持つノードのメンバーの工数
//   部長   … 自分が director 行を持つ部と、その配下の全課のメンバーの工数（課長本人ぶんを含む）
//   経理   … このモジュールではなく TimeEntryAdmin（全件・編集可）
// 判定は常に「現在の所属」。異動すれば当時の上司には見えなくなり、今の上司に見えるようになる。
//
// **書き込みは持たない**（CanCreate/CanUpdate/CanDelete = false。ビューに INSTEAD OF トリガも無い）。
// 上司は部下の工数を「見る」だけで、直すのは本人か経理——という職務分掌。
// UserReadCondition = AppUser.IsApprover で、課長/部長でない人にはサイドバーのリンクごと出さない
// （仮に開けても DataReadCondition により自分の行しか返らない＝二重の歯止め）。
//
// 一括ダウンロードは意図的に無効（CanBulkDataDownload = false）。可視範囲の判定を
// 一覧の検索経路 1 本に閉じ、エクスポート経路で条件が抜ける事故を作らないため。
