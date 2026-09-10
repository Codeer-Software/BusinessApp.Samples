#!/usr/bin/env python
"""CLB デザインの静的検査。

`designcheck` が緑でも壊れるもの（docs/qa/01_CLB静かな失敗.md）のうち、
デザイン JSON とスクリプトを読めば判るものを機械的に検出する。

    python tools/clb/lint_design.py

終了コード: 0 = error なし / 1 = error あり

**designcheck の代わりではない。** 順序は
`designcheck` → `lint_design.py` → `sql` で DB 確認 → 実機（ブラウザ）である。
"""
from __future__ import annotations

import glob
import io
import json
import os
import re
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DESIGN_DIR = os.path.join(REPO_ROOT, "Designer", "Design")
DDL_DIR = os.path.join(REPO_ROOT, "Designer", "ddl")

SEV_ERROR = "error"
SEV_WARN = "warn"

# 予約名 → 規定のデザイン型（Docs/CommonMistakes.md #42-A / qa/01 F-09）。
# 名前だけ合わせて型が違うと、designcheck 緑・HTTP 200 のまま更新だけが黙って失敗する。
RESERVED_FIELD_TYPES = {
    "Id": "IdFieldDesign",
    "LogicalDelete": "BooleanFieldDesign",
    "OptimisticLocking": "OptimisticLockingFieldDesign",
    "CreatedAt": "DateTimeFieldDesign",
    "UpdatedAt": "DateTimeFieldDesign",
    # **`LinkFieldDesign` である**（`Docs/AppPatterns/system_fields.md` の表。DB 列は
    # `creator` / `updater` の INTEGER で、参照先は認証部品の利用者）。
    # `Docs/CommonMistakes.md` #42-A の表だけが `TextFieldDesign` を「推奨」と書いているが、
    # **同じファイルの本文（予約名の一覧）は `LinkFieldDesign` と書いており、食い違っている**。
    # docs/30_作業のルール.md §2 が `Docs/AppPatterns/` を正典と定めているので、そちらに従う。
    # **型を間違えると自動セットそのものが効かない**（F-09 の機序）。
    # 「文字列だと利用者表と突き合わせられない」ではない——SQLite の INTEGER 親和性は
    # `'3'` を格納時に整数へ直すので、比較も結合も当たる（2026-09-03 実測）。
    "Creator": "LinkFieldDesign",
    "Updater": "LinkFieldDesign",
}

LEGACY_ALIGNMENTS = {"Left", "Right"}

# ボタンの色は 3 値だけ（docs/21 §4・ADR-0030）。CLB は Outline* や Text も持つが使わない。
ALLOWED_VARIANTS = {"Primary", "Danger", "Secondary"}

# 必須の印を出すクラス（app.css）。ラベル側の要素に付ける。
REQUIRED_LABEL_CLASS = "required-label"

# 利用者に見せる日時の書式（docs/21 §2-5。D-30）。
#
# **`Format` が空だと CLB の既定が出る**——実機では `2026/08/24 18:56:09` と**秒まで**並んだ
# （2026-09-02 実測 1.3.20。仕訳帳の「入力年月日」）。21 §2-5 が決めたのは
# `yyyy/MM/dd HH:mm` なので、**画面に出す日時のフィールドには書式を書く**。
# 日付だけの `DateFieldDesign` はブラウザ標準の `<input type="date">` で、
# 日本語環境では `yyyy/MM/dd` に見える——**こちらは書式を書かなくてよい**（21 §2-5）。
DATETIME_DISPLAY_FORMAT = "yyyy/MM/dd HH:mm"

# 洗い替え（`ListFieldDesignBase.ReplaceMode`）の既定。
# **`None` 以外にすると、保存が `SearchDelete`（検索条件に一致する行の一括削除）を起こす。**
# この経路はサーバ側の関門（`AccountingSubmitPipeline`）を通らないので、
# 計上済みの明細を守っている DDL のトリガが**生の例外**として出る（qa/02 R29-08）。
# いま発火しないのは設計側が全部 `None` だからであり、**入れた日に鳴らす**。
ALLOWED_REPLACE_MODE = "None"

# 読み取り条件を空のままにしてよいモジュールと、その理由（D-29。ADR-0033）。
#
# **既定は「そのアプリの役割を 1 つも持たない利用者は、そのアプリのデータを読めない」**である。
# 空＝全開放なので、**例外は理由を書いて 1 件ずつ置く**（qa/01 F-18 と同じ性質）。
# **期限のある例外には期限を書く**——書かないと、例外が既定に育つ。
READ_CONDITION_EXEMPTIONS = {
    "AppUser": "認証部品のユーザー表。読み取り条件を付けられない（qa/01 F-20）。"
               "役割の列が全利用者から読めることは ADR-0032 の帰結",
    "Account": "マスタ。フェーズ 4——viewer を効かせる回でないと「読ませるが編集させない」を表せない",
    "Department": "同上",
    "SubAccount": "同上",
    "TaxCategory": "同上",
    "CompanyProfile": "設定。フェーズ 4（同上）",
    "FiscalYear": "設定。フェーズ 4（同上）",
    "AccountingPeriod": "設定。フェーズ 4（同上）",
    "Partner": "取引先マスタ。フェーズ 4——閉じると会計側の 5 か所を出し分ける必要がある"
               "（ADR-0033 の帰結）",
    "PortalHome": "玄関。どの役割の人も着地する画面で、閉じるとルート URL の着地先が無くなる"
                  "（ADR-0033 決定④の例外）",
}

# 関門が名指ししている語（qa/02 R26-22）。
# **デザイン側で名前が変わると、関門は何も言わずに外れて全テストが緑になる。**
# `EnumConsistencyTests` の「3 者一致」と同じ作法を、関門の名指しにも当てる。
VOCABULARY_MODULE = "AppUser"

# 役割の軸 → (デザイン enum の名前, 下位から上位への順)。
# **順序はここでしか表せない**（enum は集合しか持たない）ので、値の集合が一致することを検査する。
ROLE_HIERARCHY = {
    "AccountingRole.Value": ("AccountingRoles", ["viewer", "staff", "manager"]),
    "PartnerRole.Value": ("PartnerRoles", ["viewer", "editor"]),
}

# アプリ全体のアクセス条件が見ている変数（D-23）。
ACCESS_FLAG_VARIABLE = "CanAccessApp.Value"

# 参照してはならない向き（ADR-0025 §4）。`Modules/` のトップレベルのフォルダ＝アプリ（部品）で
# 判定する（Designer/Project.md のフォルダ規約）——モジュール名を並べると、増えるたびに腐る。
# **認証部品（Platform）は誰が参照してもよい**——権限の条件は AppUser の列でしか書けない（qa/01 F-21）。
FORBIDDEN_REFERENCES = {"Partners": {"Accounting"}}


def ddl_tables():
    """DDL のテーブル → 列定義の行（列名 → その行の本文）。

    **列定義の行だけを拾う。** `UNIQUE (...)` のような表制約は列ではないので落とす。
    """
    tables = {}
    for path in sorted(glob.glob(os.path.join(DDL_DIR, "*.sql"))):
        text = io.open(path, encoding="utf-8").read()
        # **`IF NOT EXISTS` も空白の詰め方も受ける。** 外れると、その表が丸ごと
        # `DDL_TABLES` から落ちて D-25 が無音で消える（2026-09-02 の自己レビュー）。
        for match in re.finditer(
                r"CREATE TABLE (?:IF NOT EXISTS )?(\w+)\s*\((.*?)\n\s*\);", text, re.DOTALL):
            columns = {}
            for line in match.group(2).split("\n"):
                line = line.split("--")[0].strip().rstrip(",")
                head = re.match(r"^(\w+)\s+\w", line)
                if head and head.group(1).upper() not in (
                        "UNIQUE", "CHECK", "FOREIGN", "PRIMARY", "CONSTRAINT"):
                    columns[head.group(1)] = line
            tables[match.group(1)] = columns
    return tables


DDL_TABLES = ddl_tables()


def tables_with_optimistic_locking():
    """DDL で optimistic_locking 列を持つテーブル。

    認証部品の app_users のように、こちらが定義していないテーブルまで規約の対象にしない。
    """
    return {t for t, columns in DDL_TABLES.items() if "optimistic_locking" in columns}


OPTIMISTIC_LOCKING_TABLES = tables_with_optimistic_locking()


def columns_the_user_must_fill(table):
    """その表で「誰かが値を入れないと INSERT が落ちる」列（D-25）。

    `NOT NULL` かつ `DEFAULT` が無く、主キーでもない列。
    **`DEFAULT` のある列を入れない**——DB が埋めるので、画面が空でも落ちない。
    """
    required = set()
    for column, line in DDL_TABLES.get(table, {}).items():
        # **`DEFAULT` は語として見る。** 部分一致だと `default_tax_treatment` のような
        # 列名を「既定値がある」と読み違える（2026-09-02 の自己レビュー）。
        upper = line.upper()
        if ("NOT NULL" in upper
                and not re.search(r"\bDEFAULT\b", upper)
                and "PRIMARY KEY" not in upper):
            required.add(column)
    return required


# `NOT NULL` なのに `IsRequired` を立てない欄と、その理由（D-25）。
# **理由の無い免除を置かない**——外してよいかを、次に見る人が判断できる形で書く。
# `IsRequired` は「**利用者が埋める**必須欄」の 1 意味に揃えてある（Designer/Project.md）ので、
# **画面やサーバが自動で入れる列はここに載る**。印（D-20）が付いてしまうのを避ける意味もある。
REQUIRED_EXEMPTIONS = {
    ("JournalEntry", "fiscal_year_id"): "計上日から画面が入れる（表示専用。利用者は選ばない）",
    ("JournalEntry", "entered_at"): "入力年月日はサーバが入れる",
    ("JournalLine", "journal_entry_id"): "親 FK。親が識別子を差し込む（qa/01 D-17）。"
                                          "qa/01 H-02 は「親子の FK に NOT NULL を付けない」と書いているが、"
                                          "1.3.20 では NOT NULL のまま明細の追加も計上も通っている（実測。"
                                          "H-02 は前回プロジェクト由来の未確認）",
    ("JournalLine", "line_no"): "行番号は親の画面が自動で採る",
    ("PartnerInvoiceRegistration", "partner_id"): "URL の ?partner で決まり、画面は表示だけ（13 §3-4）",
}


# **印を出すが `IsRequired` は立てない欄**（保存では要らず、計上でだけ要る）。
# **キーは (モジュール名, フィールド名)**——`REQUIRED_EXEMPTIONS` は DB の列名なので取り違えないこと。
# `IsRequired` を立てると CLB の入力検査が**下書き保存まで止める**ので立てられないが、
# **印は最初から出す**（docs/10 §4-2-1・docs/21 §1）。
# **理由を書かないと載せられない**（空の理由は `check_exemptions` が赤くする）。
MARK_WITHOUT_REQUIRED = {
    ("JournalEntry", "Description"):
        "摘要は計上でだけ必須。IsRequired を立てると下書き保存も止まる（docs/10 §4-2-1）",
    # 条件つき必須（要否が他の欄の値で変わる）。IsRequired では表せないので、印と凡例で先出しする（docs/21 §1）。
    ("TaxCategory", "RateKind"):
        "税率区分は課税区分が課税売上・課税仕入のときだけ必須（docs/11 §1。関門 MasterSubmitGate）",
    ("JournalEntry", "Partner"):
        "伝票の取引先は「取引先を要する」科目の明細があるときだけ、計上に必須（docs/10 §6-2。計上の関門）",
}


def design_files(pattern):
    return sorted(glob.glob(os.path.join(DESIGN_DIR, "**", pattern), recursive=True))


def relative(path):
    return os.path.relpath(path, REPO_ROOT).replace(os.sep, "/")


def check_module(path, doc, findings):
    module = doc.get("Name", "")

    for field in doc.get("Fields", []):
        name = field.get("Name", "")
        actual = field.get("TypeFullName", "").rsplit(".", 1)[-1]

        # F-09 予約名フィールドは規定のデザイン型でなければ自動動作が効かない
        expected = RESERVED_FIELD_TYPES.get(name)
        if expected and actual != expected:
            findings.append((SEV_ERROR, "F-09", relative(path),
                             f"予約名 {name} は {expected} でなければならない（今は {actual}）"))

        # F-09 SQLite では版を自前で進めないと楽観ロックが働かない
        if name == "OptimisticLocking" and actual == "OptimisticLockingFieldDesign" \
                and not field.get("IncrementVersion"):
            findings.append((SEV_ERROR, "F-09", relative(path),
                             "OptimisticLocking は IncrementVersion: true が要る（既定は PostgreSQL の xmin 前提）"))

        # 本プロジェクトは論理削除を使わない（docs/12 マスタ台帳・Designer/ddl/README）
        if name == "LogicalDelete":
            findings.append((SEV_ERROR, "PRJ-01", relative(path),
                             "論理削除は使わない。仕訳は消せず、マスタは is_active で無効化する"))

        # F-01 OnValidateInput は false を返すと無言で保存を止める
        if field.get("OnValidateInput"):
            findings.append((SEV_ERROR, "F-01", relative(path),
                             f"{name}.OnValidateInput は使わない。関門はサーバ側に置く"))

    # 更新できるモジュールに楽観ロックが無いと、ロスト・アップデートが黙って起きる。
    # 「フィールドがあるとき型を見る」だけでは、最も危ない側（欠落）を見逃す。
    if (doc.get("CanUpdate", True)
            and doc.get("DbTable") in OPTIMISTIC_LOCKING_TABLES
            and not any(f.get("Name") == "OptimisticLocking" for f in doc.get("Fields", []))):
        findings.append((SEV_ERROR, "F-09", relative(path),
                         f"{module}: 更新できるモジュールには OptimisticLocking フィールドが要る"))

    # D-18 ボタンの色は 3 値だけ（docs/21 §4・ADR-0030）。
    # **`check_module` の中から呼ぶ。** `main()` から別に呼ぶ形にすると、
    # モジュール側の呼び出しを消してもフレーム側が残るので `WIRED_CHECKS` が緑になった
    # （2026-09-02 に壊して確かめた）。**呼ぶ人を 1 か所にすると、消えたことが検体で分かる。**
    check_variants(path, doc, findings)

    # D-19 検索欄を持つレイアウトは既定で開く（docs/21 §3）。
    #
    # **名前つきの検索レイアウトも見る。** 既定（`""`）だけを見ていたので、
    # レイアウトを名前つきで足した日に素通りしていた（2026-08-31 の自己レビュー R28-17）。
    # **空の検索レイアウトは対象にしない**——開いても空箱が出るだけである。
    # **折りたためないレイアウトも対象にしない**——`IsExpandable: false` は常に開いた姿で、
    # `IsExpanderDefaultOpened` はそもそも読まれない（同 R28-17 の誤検知）。
    for layout_name, search in (doc.get("SearchLayouts") or {}).items():
        search_layout = search.get("Layout") or {}
        if (_field_count(search_layout) > 0
                and search_layout.get("IsExpandable")
                and not search_layout.get("IsExpanderDefaultOpened")):
            where = f"{module}{'/' + layout_name if layout_name else ''}"
            findings.append((SEV_ERROR, "D-19", relative(path),
                             f"{where}: 検索条件は既定で開く（IsExpanderDefaultOpened: true）"))

    # D-26 洗い替えは使わない（関門を通らない削除が起きる。qa/02 R29-08）
    _check_replace_mode(path, doc, findings)

    # D-25 DDL が必須にしている欄は、画面でも必須にする
    _check_required_columns(path, doc, findings)

    # D-24 データを持つモジュールに書き込み条件が書かれているか（qa/01 F-18）。
    # **空＝全開放である。** 前回プロジェクトは 104 本になってから全数監査をして穴を 28 本見つけた
    # （ADR-0026 の教訓。lint-docs:ignore 経緯）。新しいモジュールは条件が空で生まれるので、増えた日に鳴らす。
    if doc.get("DbTable") and not (doc.get("UserWriteCondition") or {}).get("ModuleName"):
        findings.append((SEV_ERROR, "D-24", relative(path),
                         f"{module}: データを持つモジュールに UserWriteCondition が要る"
                         "（空＝全開放。qa/01 F-18）"))

    # D-29 データを持つモジュールに読み取り条件が書かれているか（ADR-0033）。
    #
    # **D-24 の読み取り版である。** 既定は「そのアプリの役割を 1 つも持たない利用者は、
    # そのアプリのデータを読めない」で、空＝全開放だと **`is_sysadmin` だけの利用者や
    # 取引先だけの利用者が、API から下書きを含む全伝票を読める**（qa/01 F-18）。
    #
    # **機械で守れるのは「空でないこと」までである**（ADR-0033 の帰結）。
    # 「軸の全ての値が OR で入っている」という形にはできない——**下位は上位専用のものを読めない**と
    # 決めた（決定②）ので、どの役割まで開くかはモジュールごとの判断になる。
    #
    # **表を持たないモジュールも見る**（2026-09-02 の自己レビューで 2 人が独立に指摘）。
    # `DbTable` を入口にしていたので、**帳簿と入力の一覧（クエリモジュール）が丸ごと外**にあった——
    # ADR-0033 の状況節が名指ししたのはまさにその 3 本（下書きを含む全伝票が読める）で、
    # qa/01 F-23 も「`UserReadCondition` はクエリモジュールに効く」と書いている。
    # **いちばん広い読み取り面が網の外にあった。**
    if (_shows_data(doc)
            and module not in READ_CONDITION_EXEMPTIONS
            and not (doc.get("UserReadCondition") or {}).get("ModuleName")):
        findings.append((SEV_ERROR, "D-29", relative(path),
                         f"{module}: データを持つモジュールに UserReadCondition が要る"
                         "（空＝全開放。ADR-0033。開けたままにするなら "
                         "READ_CONDITION_EXEMPTIONS に理由つきで載せる）"))

    # D-20 必須の欄には印が要る（docs/21 §1）。
    # **見るのは詳細レイアウトのラベルだけ**——一覧の見出し（<th>）には class が付かないので、
    # そちらは文字列に「*」を入れてある（qa/01 D-16）。
    _check_required_marks(path, doc, findings)
    _check_marks_without_required(path, doc, findings)

    # D-30 画面に出す日時は書式を書く（docs/21 §2-5）。
    _check_datetime_formats(path, doc, findings)

    field_names = {f.get("Name", "") for f in doc.get("Fields", [])}
    for kind, layouts in (("Detail", doc.get("DetailLayouts", {})),
                          ("Search", doc.get("SearchLayouts", {}))):
        for layout_name, layout in layouts.items():
            check_layout(path, f"{module}/{kind}{'/' + layout_name if layout_name else ''}",
                         layout.get("Layout", {}), kind, field_names, findings)

    # 一覧レイアウトにも揃えの旧値が入りうる（A-01 は Detail だけの話ではない）。
    for layout_name, layout in doc.get("ListLayouts", {}).items():
        where = f"{module}/List{'/' + layout_name if layout_name else ''}"
        for row in layout.get("Elements", []):
            for element in row:
                for key in ("HorizontalAlignment", "VerticalAlignment"):
                    if element.get(key) in LEGACY_ALIGNMENTS:
                        findings.append((SEV_ERROR, "A-01", relative(path),
                                         f"{where}: {key} の旧値 {element[key]} は Start / End に化ける"))


def _shows_data(doc):
    """そのモジュールが**利用者にデータを見せる**か（D-29 の対象）。

    表を持つモジュール（`DbTable`）と、SQL でデータを出すクエリモジュール（`QueryFieldDesign`）。
    **ラベルと遷移だけの箱は含めない**——玄関の見出しに読み取り条件を求めても意味が無い。
    """
    if doc.get("DbTable"):
        return True
    return any(f.get("TypeFullName", "").endswith("QueryFieldDesign")
               for f in doc.get("Fields", []))


def _walk(node, visit):
    """JSON の木を全部たどり、辞書のノードごとに `visit` を呼ぶ。"""
    if isinstance(node, dict):
        visit(node)
        for value in node.values():
            _walk(value, visit)
    elif isinstance(node, list):
        for value in node:
            _walk(value, visit)


def check_variants(path, doc, findings):
    """ボタンの色は 3 値だけ（docs/21 §4・ADR-0030）。

    **`Fields` だけを見ない。** `Variant` はレイアウトの入れ子にも `.frm.json` にも
    現れうるキーで、`Fields` の直下しか見ていなかった（2026-08-31 の自己レビュー R28-17）。
    **どこに書かれても同じ色が出る**以上、検査も書かれる場所を選ばない。
    """
    def visit(node):
        variant = node.get("Variant")
        if variant and variant not in ALLOWED_VARIANTS:
            findings.append((SEV_ERROR, "D-18", relative(path),
                             f"{doc.get('Name', '')}.{node.get('Name') or node.get('FieldName') or '?'}: "
                             f"Variant「{variant}」は使わない"
                             f"（{' / '.join(sorted(ALLOWED_VARIANTS))} のどれかにする）"))

    _walk(doc, visit)


def _check_replace_mode(path, doc, findings):
    """洗い替え（`ReplaceMode`）を使っていないか（qa/02 R29-08）。

    **`None` 以外は `SearchDelete` を起こす。** 保存が「条件に一致する行の一括削除」に化け、
    サーバ側の関門（`AccountingSubmitPipeline`）を通らない。計上済みの明細を守っているのは
    DDL のトリガなので、**利用者には生の例外が出る**（qa/01 F-16）。
    """
    def visit(node):
        mode = node.get("ReplaceMode")
        if mode and mode != ALLOWED_REPLACE_MODE:
            findings.append((SEV_ERROR, "D-26", relative(path),
                             f"{doc.get('Name', '')}.{node.get('Name', '?')}: "
                             f"ReplaceMode「{mode}」は関門を通らない削除（SearchDelete）を起こす。"
                             "入れるなら、その削除を止める関門を先に作る（qa/02 R29-08）"))

    _walk(doc, visit)


def _check_required_columns(path, doc, findings):
    """DDL が `NOT NULL` にした列が、画面でも必須になっているか（qa/02 R28-11）。

    **見るのは「誰かが入れないと INSERT が落ちる列」だけ**である
    （`DEFAULT` のある列は DB が埋める）。空のまま保存へ進むと、
    **生の `SQLite Error 19` がトーストに出る**（qa/01 F-16・qa/03 L-16 で実際に出した）。

    **書き込み経路の無いモジュールは対象外**（`CanCreate` も `CanUpdate` も偽）——
    画面から値が入ることが無いので、必須の印は意味を持たない。

    列は `DbColumn` 以外のキーでも指せる（`PasswordHashFieldDesign` の
    `DbColumnHash` / `DbColumnSalt`）。**そちらは「誰かが書く」までしか言わない**ので、
    `IsRequired` は見ない——書く側の部品が自分で必須を判定する
    （`AppUser` のパスワードは空のまま登録すると CLB が「不正な入力があります」で止める。
    2026-09-02 実測 1.3.20）。**どのフィールドからも指されていない列は鳴らす**——
    値を入れる者が居ないということである。
    """
    module = doc.get("Name", "")
    table = doc.get("DbTable")
    if not table or table not in DDL_TABLES:
        return
    # **既定は「書ける」に倒す**（F-09 の `doc.get("CanUpdate", True)` と揃える）。
    # 書かなかったモジュールで検査だけが静かに外れる形を作らない。
    if not (doc.get("CanCreate", True) or doc.get("CanUpdate", True)):
        return

    by_column = {}
    mentioned = set()
    for field in doc.get("Fields", []):
        for key, value in field.items():
            if key.startswith("DbColumn") and value:
                mentioned.add(value)
                if key == "DbColumn":
                    by_column[value] = field

    # **逆向きも見る**（2026-09-08 に足した。ADR-0038 §3 の列の改名で気づいた）。
    # 上の検査は DDL から引くだけなので、**デザインが実在しない列を指していても鳴らない**——
    # 列を改名して JSON の `DbColumn` を直し忘れると、リポジトリの中では何も鳴らず、
    # 網は稼働 DB に繋がる `designcheck` だけになる（qa/01 X-06）。
    for column in sorted(mentioned):
        if column not in DDL_TABLES[table]:
            findings.append((SEV_ERROR, "D-25", relative(path),
                             f"{module}: {table}.{column} を指すフィールドがあるが、"
                             "その列は DDL に無い（改名したら DbColumn も直す）"))

    for column in sorted(columns_the_user_must_fill(table)):
        if (module, column) in REQUIRED_EXEMPTIONS:
            continue
        if column not in mentioned:
            findings.append((SEV_ERROR, "D-25", relative(path),
                             f"{module}: {table}.{column} は NOT NULL なのに、"
                             "値を書くフィールドがどこにも無い"))
            continue
        field = by_column.get(column)
        if field is not None and not field.get("IsRequired"):
            findings.append((SEV_ERROR, "D-25", relative(path),
                             f"{module}.{field.get('Name', '')}: {table}.{column} は NOT NULL なので "
                             "IsRequired: true にする（空のまま保存すると生の SQLite の文言が出る。"
                             "画面が自動で入れる欄なら REQUIRED_EXEMPTIONS に理由つきで載せる）"))


def _field_count(layout):
    """レイアウトに置かれているフィールドの数（雛形の空行を数えない）。"""
    count = 0

    def walk(node):
        nonlocal count
        if isinstance(node, dict):
            if node.get("FieldName"):
                count += 1
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(layout)
    return count


def _has_class(doc, field_name, class_name):
    """詳細レイアウトのその要素に、そのクラスが付いているか。"""
    found = []

    def visit(node):
        if node.get("FieldName") == field_name:
            found.append(class_name in (node.get("ClassName") or "").split())

    _walk(doc.get("DetailLayouts", {}), visit)
    return any(found)


def _check_datetime_formats(path, doc, findings):
    """**画面に出す日時に書式が書いてあるか**（docs/21 §2-5）。

    `DateTimeFieldDesign` の `Format` が空だと CLB の既定が出て、**秒まで並ぶ**
    （2026-09-02 実測 1.3.20。仕訳帳の「入力年月日」が `2026/08/24 18:56:09` だった）。
    21 §2-5 が決めた書式は `yyyy/MM/dd HH:mm` である。

    **見るのはレイアウトに置いた欄だけ。** `CreatedAt` / `UpdatedAt` のように
    どの画面にも出していない監査用の列まで縛ると、**書式が要らない欄に書式が増える**——
    出していないものの見た目を決めても、次に出す人がそれを読むとは限らない。
    """
    module = doc.get("Name", "")
    wrong = {f.get("Name", ""): f.get("Format", "") for f in doc.get("Fields", [])
             if f.get("TypeFullName", "").endswith("DateTimeFieldDesign")
             and f.get("Format", "") != DATETIME_DISPLAY_FORMAT}
    if not wrong:
        return

    placed = set()

    def visit(node):
        name = node.get("FieldName", "")
        if name in wrong:
            placed.add(name)

    for kind in ("DetailLayouts", "ListLayouts", "SearchLayouts"):
        _walk(doc.get(kind) or {}, visit)

    for name in sorted(placed):
        findings.append((SEV_ERROR, "D-30", relative(path),
                         f"{module}: {name} は画面に出す日時なので "
                         f'"Format": "{DATETIME_DISPLAY_FORMAT}" を書く'
                         f"（空だと秒まで出る。docs/21 §2-5）"))


def _check_required_marks(path, doc, findings):
    """必須のフィールドのラベルに、印が出るか（docs/21 §1）。

    **`IsRequired` は「利用者が埋める必須欄」の 1 意味に揃えてある**（Designer/Project.md）。
    画面が自動で入れる欄には立てないので、ここは例外なしの規則でよい。

    **印の出し方は 2 通りある**（2026-09-02 に実機で分かった）。

    1. ラベルのレイアウト要素に `"ClassName": "required-label"`（app.css の `::after`）
    2. **`LabelFieldDesign` の `RelativeField` を必須の欄に向ける**——
       CLB が `<span class="text-danger">*</span>` を自分で足す

    **見るのは「印が出るか」であって、どちらの書き方かではない。** 2 の形に 1 を重ねると
    **`*` が 2 つ並ぶ**（認証部品の `AppUser` が 2 の形で、実際に重ねて出した）。
    """
    module = doc.get("Name", "")
    required = {f.get("Name", "") for f in doc.get("Fields", []) if f.get("IsRequired")}
    if not required:
        return

    # CLB が自分で印を足すラベル → その持ち主（`RelativeField` が必須の欄を向いている）。
    # **持ち主は `RelativeField` から取る。** ラベル名から `Label` を削る形にすると、
    # `CodeCaption` のような命名で二重印の検査が黙る（2026-09-02 の自己レビュー）。
    drawn_by_clb = {f.get("Name", ""): f["RelativeField"] for f in doc.get("Fields", [])
                    if f.get("RelativeField") in required}

    marked = set()
    unmarked = {}
    placed = set()

    def walk(node):
        if isinstance(node, dict):
            name = node.get("FieldName", "")
            if node.get("TypeFullName", "").endswith("FieldLayoutDesign"):
                if name in required:
                    placed.add(name)
                owner = drawn_by_clb.get(name) or (
                    name[:-len("Label")] if name.endswith("Label") else "")
                if owner in required:
                    # **クラスは分割して見る。** `"required-label ms-2"` のように
                    # 他のクラスと併記できる（app.css はクラスセレクタなので印は出る）。
                    # 完全一致で見ると、正しい書き方を誤検知する。
                    if (REQUIRED_LABEL_CLASS in (node.get("ClassName") or "").split()
                            or name in drawn_by_clb):
                        marked.add(owner)
                    else:
                        unmarked[owner] = name
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(doc.get("DetailLayouts", {}))

    # **両方を書いたら `*` が 2 つ並ぶ。** 実機で出した（2026-09-02。`AppUser`）。
    for name in sorted(drawn_by_clb):
        if _has_class(doc, name, REQUIRED_LABEL_CLASS):
            findings.append((SEV_ERROR, "D-20", relative(path),
                             f"{module}: {name} は RelativeField で CLB が印を出すので、"
                             f'"ClassName": "{REQUIRED_LABEL_CLASS}" を重ねない（* が 2 つ並ぶ）'))

    for owner, label in sorted(unmarked.items()):
        if owner in marked:
            continue    # 同じ欄が複数のレイアウトにあり、片方には付いている
        findings.append((SEV_ERROR, "D-20", relative(path),
                         f"{module}: 必須の {owner} のラベル {label} に印が出ない。"
                         f'"ClassName": "{REQUIRED_LABEL_CLASS}" を付けるか、'
                         "ラベルの RelativeField をその欄に向ける（docs/21 §1）"))

    # **ラベル要素そのものが無い場合を見落とさない。** これがいちばん起きやすい書き忘れで、
    # 「`<Field>Label` があるときにしか見ない」実装では素通りしていた（2026-08-31 の自己レビュー）。
    for owner in sorted(placed - marked - set(unmarked)):
        findings.append((SEV_ERROR, "D-20", relative(path),
                         f"{module}: 必須の {owner} に、印を付けるラベル要素"
                         f"（{owner}Label）が詳細レイアウトに無い（docs/21 §1）"))


def _check_marks_without_required(path, doc, findings):
    """**逆向き**——印が出ているのに `IsRequired` でない欄（docs/21 §1）。

    `IsRequired` を「利用者が埋める必須欄」の 1 意味に揃えてある以上、
    **印だけが独り歩きすると `*` の意味が画面の中で 2 通りになる**。
    計上でだけ必須にしたい欄は実在するので（摘要）、**理由つきの許可表**で受ける。
    表に無い印は、`IsRequired` の付け忘れか、印の付け間違いのどちらかである。
    """
    module = doc.get("Name", "")
    required = {f.get("Name", "") for f in doc.get("Fields", []) if f.get("IsRequired")}
    field_names = {f.get("Name", "") for f in doc.get("Fields", [])}
    # **持ち主の求め方は順方向（`_check_required_marks`）と同じにする。**
    # `RelativeField` を優先し、無ければ `<Field>Label` の形だけを認める——
    # ラベル名そのものを持ち主にすると、`CodeCaption` のような命名で
    # **存在しない欄の名前を名指しして鳴る**（直す手立てが無い。2026-09-08 の自己レビュー）。
    owner_of = {f.get("Name", ""): f.get("RelativeField") or ""
                for f in doc.get("Fields", [])
                if f.get("TypeFullName", "").endswith("LabelFieldDesign")}

    def walk(node):
        if isinstance(node, dict):
            name = node.get("FieldName", "")
            if (node.get("TypeFullName", "").endswith("FieldLayoutDesign")
                    and REQUIRED_LABEL_CLASS in (node.get("ClassName") or "").split()):
                owner = owner_of.get(name) or (
                    name[:-len("Label")] if name.endswith("Label") else "")
                if (owner in field_names
                        and owner not in required
                        and (module, owner) not in MARK_WITHOUT_REQUIRED):
                    findings.append((SEV_ERROR, "D-20", relative(path),
                                     f"{module}: {owner} は IsRequired でないのにラベル {name} に印が出る。"
                                     f"IsRequired を立てるか、理由つきで MARK_WITHOUT_REQUIRED に載せる"
                                     "（docs/21 §1）"))
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    # **詳細だけでなく、一覧・検索も歩く。** 行ごと複製したときに印が付いて回る。
    for layouts in ("DetailLayouts", "ListLayouts", "SearchLayouts"):
        walk(doc.get(layouts, {}))


def app_of(path):
    """そのファイルが属するアプリ（`Modules/` のトップレベルのフォルダ）。"""
    modules_dir = os.path.join(DESIGN_DIR, "Modules")
    parts = os.path.relpath(path, modules_dir).replace(os.sep, "/").split("/")
    return parts[0] if len(parts) > 1 else ""


def check_module_references(modules, scripts, findings):
    """部品をまたぐ参照が、決めた向きに反していないか（ADR-0025 §4・ADR-0029 §2）。

    **依存の向きは会計コア → 取引先の一方通行である。** 逆流すると、
    取引先を取り出したときに会計コアごと付いてくる。
    designcheck は参照が解決できるかしか見ないので、**向きは誰も見ていなかった**。

    **認証部品（Platform）への参照は禁じない。** CLB の権限条件は
    ログインユーザーのレコードの列しか参照できず（qa/01 F-21）、
    どの部品の画面も `AppUser` を名指しするしかない。
    **アプリはフォルダで判定する**（Designer/Project.md のフォルダ規約）——
    モジュール名を並べると、増えるたびに腐る。
    """
    # **名前の無いモジュールを入れない。** 入れると `"ModuleName": ""` が
    # 全 JSON に当たって誤検知する（条件を書いていない欄が実データに多数ある）。
    app_by_module = {doc["Name"]: app_of(path) for path, doc in modules if doc.get("Name")}

    def report(path, owner, target, how):
        findings.append((SEV_ERROR, "D-21", relative(path),
                         f"{owner}（{app_of(path)}）が {app_by_module[target]} の {target} を{how}。"
                         "部品をまたぐ依存は一方通行（ADR-0025 §4）"))

    for path, doc in modules:
        forbidden = FORBIDDEN_REFERENCES.get(app_of(path))
        if not forbidden:
            continue
        text = json.dumps(doc, ensure_ascii=False)
        for target, app in sorted(app_by_module.items()):
            # **`"ModuleName"` だけでなく `"Module"` も見る。** 遷移リンク
            # （`AnchorTagFieldDesign`）は後者で相手を指す（2026-08-31 の自己レビュー）。
            if app in forbidden and any(
                    f'"{key}": "{target}"' in text for key in ("ModuleName", "Module")):
                report(path, doc.get("Name", ""), target, "参照している")

    for path, text in scripts:
        forbidden = FORBIDDEN_REFERENCES.get(app_of(path))
        if not forbidden:
            continue
        owner = os.path.basename(path).split(".", 1)[0]
        for target, app in sorted(app_by_module.items()):
            # 型引数（`ModuleSearcher<X>`）と、**文字列で相手を指す形**
            # （`NavigationService.GetModuleDataUrl("X", ...)`）の両方を見る。
            if app in forbidden and (
                    re.search(r"ModuleSearcher<" + re.escape(target) + ">", text)
                    or f'"{target}"' in text):
                report(path, owner, target, "読んでいる")


def child_parent_keys(modules):
    """親の `ListField` が絞っている「子モジュール → 親 FK の名前」。

    **親子の関係はデザインから読む。モジュール名を並べない**（増えるたびに腐る）。
    親の `ListField` が「子の `<X>.Value` ＝ 自分の `Id.Value`」で絞っていれば、
    その `<X>` が明細側の親 FK である——つまり**親が自分で名乗っている**。
    """
    def collect(node, into):
        if isinstance(node, dict):
            if (node.get("TypeFullName", "").endswith("FieldVariableMatchCondition")
                    and node.get("Variable") == "Id.Value"
                    and node.get("SearchTargetVariable", "").endswith(".Value")):
                into.add(node["SearchTargetVariable"][:-len(".Value")])
            for value in node.values():
                collect(value, into)
        elif isinstance(node, list):
            for value in node:
                collect(value, into)

    for path, doc in modules:
        for field in doc.get("Fields", []):
            if not field.get("TypeFullName", "").endswith("ListFieldDesign"):
                continue
            condition = field.get("SearchCondition") or {}
            names = set()
            collect(condition.get("Condition"), names)
            for name in sorted(names):
                yield path, doc, field, condition.get("ModuleName", ""), name


def check_child_detail_screens(modules, frames, scripts, findings):
    """明細モジュールをフレームに登録するなら、親 FK を埋める経路が要る（qa/02 R29-13）。

    **明細モジュールは詳細レイアウトを持っている**（必須の印を置く場所として。qa/02 R26-31）。
    そこにどのフレームからも到達できないうちは無害だが、**登録した日に
    「親の無い明細を新規作成できる画面」が生まれる**——親 FK は `NOT NULL` なので、
    保存は DB に拒まれ、**生の SQLite の文言がトーストに出る**（qa/01 F-16）。

    **登録そのものは禁じない。** `PartnerInvoiceRegistration` は 2026-09-02 に
    正面の到達先へ昇格し、URL の `?partner=` から親 FK を入れている（13 §3-4）。
    **要求するのは「親 FK に値が入る経路があること」**だけである。

    **新規作成できる子だけを見る。** 親の詳細に埋め込んだクエリモジュール
    （`PartnerRegistrationList`）は `CanCreate` が偽で、作る経路がそもそも無い。
    """
    registered = set()
    for _, frame in frames:
        registered |= modules_on_frame(frame)

    creatable = {doc.get("Name") for _, doc in modules if doc.get("CanCreate")}

    # **コメントと文字列リテラルを潰してから読む**（2026-09-02 の自己レビュー）。
    # 潰さないと「`// Parent.Value = q` と説明に書いただけ」で緑になる。
    assigns = {os.path.basename(path).split(".", 1)[0]: _blank(_blank(text, _COMMENTS), _STRINGS)
               for path, text in scripts}

    for path, doc, field, child, key in child_parent_keys(modules):
        if child not in registered or child not in creatable:
            continue
        script = assigns.get(child, "")
        # **代入だけを見る。** `\s*=` だと `if (Parent.Value == q)` という**比較**にも当たり、
        # 値を入れていないのに緑になる（同じ自己レビュー）。
        if re.search(rf"(?<![\w.]){re.escape(key)}\.Value\s*=(?!=)", script):
            continue
        findings.append((SEV_ERROR, "D-28", relative(path),
                         f"{child} はフレームに登録されているのに、親 FK {child}.{key} に "
                         f"値を入れる経路がスクリプトに無い（{doc.get('Name', '')}."
                         f"{field.get('Name', '')} の明細である）。"
                         "親の無い行を新規作成でき、保存は DB に拒まれる（qa/02 R29-13）"))


def check_child_parent_keys(modules, findings):
    """ヘッダ＋明細の、**明細側の親 FK が `IdFieldDesign` ＋ `IsManualInput: false` か**（qa/01 D-17）。

    **`LinkFieldDesign` にすると、行を「追加」した保存が静かに消える。**
    ボタンは押せるのに保存要求が 1 本も飛ばず、エラーもトーストも出ない。
    **読み取りは動く**ので気づけない——既存の行は逆引きで正しく出て、
    リロードして初めて消えたと分かる（2026-08-30 実測 1.3.20）。
    `designcheck` は緑のままである。

    **親子の関係は `child_parent_keys` がデザインから読む**（モジュール名を並べない）。
    """
    fields_by_module = {
        doc["Name"]: {f.get("Name", ""): f for f in doc.get("Fields", [])}
        for _, doc in modules if doc.get("Name")
    }

    pairs = 0
    for path, doc, field, child, name in child_parent_keys(modules):
        if child not in fields_by_module:
            continue
        # **閲覧専用の一覧（クエリモジュールの埋め込み等）は、型の検査だけ免除する。**
        # D-17 の型は「行の追加が静かに消える」で、追加・更新の経路が無ければ起きない。
        # 名前を出すためにクエリモジュールを選ぶのは正しい形（qa/01 D-17 の処方）。
        # ただし**指し先の存在は閲覧専用でも見る**——絞り込みのフィールドが消えると、
        # クエリの「未指定なら効かない」規約に落ちて**全件が出る**（2026-09-02 のレビュー指摘）。
        # 注意: `ListField.CanCreate: false` は UI しか塞がない。スクリプトで行を足す設計に
        # 変えた日は、この免除が効きすぎる。
        writable = bool(field.get("CanCreate") or field.get("CanUpdate"))
        pairs += 1

        key = fields_by_module[child].get(name)
        if key is None:
            findings.append((SEV_ERROR, "D-17", relative(path),
                             f"{doc.get('Name', '')}.{field.get('Name', '')} が絞る "
                             f"{child}.{name} が無い（親子の逆引きが効かない）"))
            continue
        if not writable:
            continue
        if not key.get("TypeFullName", "").endswith("IdFieldDesign"):
            findings.append((SEV_ERROR, "D-17", relative(path),
                             f"{child}.{name} は親 FK なので IdFieldDesign にする"
                             f"（今は {key.get('TypeFullName', '').rsplit('.', 1)[-1]}）。"
                             "参照フィールドだと行の追加が静かに消える（qa/01 D-17）"))
        elif key.get("IsManualInput"):
            findings.append((SEV_ERROR, "D-17", relative(path),
                             f"{child}.{name} は親 FK なので IsManualInput: false にする"
                             "（親が識別子を差し込む欄である。qa/01 D-17）"))

    # **0 は「違反が無い」ではなく「配線が死んだ」を疑う数字**（qa/03 L-15）。
    # 親子の関係はデザインから読んでいるので、CLB が条件の型名を変えた日に
    # **1 組も見つからないまま error: 0 で緑になる**。実物には必ず 1 組以上ある。
    if pairs == 0:
        findings.append((SEV_ERROR, "D-17", relative(DESIGN_DIR),
                         "ヘッダ＋明細の組が 1 つも見つからない（検査が空回りしている）"))

    return pairs


def modules_on_frame(doc):
    """そのフレームが着地先として登録しているモジュールの名前。

    **`modules` という名前を使い回さない。** かつて呼び出し側の引数
    （モジュール定義の一覧）を上書きしてしまい、後半のループが文字列を展開しようとして
    落ちた（2026-08-31）。
    """
    placed = set()
    if doc.get("TopPageModule"):
        placed.add(doc["TopPageModule"])
    for side in ("Left", "Right", "Header"):
        for link in (doc.get(side) or {}).get("Links", []):
            if not link.get("PageFrame"):
                placed.add(link.get("Module", ""))
    for other in doc.get("OtherPageModuleDesigns") or []:
        placed.add(other.get("Module", ""))
    return placed


def check_cross_frame_links(frames, findings, modules=(), scripts=()):
    """遷移先での登録漏れ（qa/01 F-17）。

    **登録が無いと画面が静かに真っ白になる。** designcheck は検出しない。
    前回プロジェクトは繰り返し踏んで静的検査を自作した（ADR-0035 §3）。

    **見るのはフレームのサイドバーだけではない。** 遷移は 3 通りの形で書かれる。

    1. フレームの `Links[].PageFrame`（サイドバーから別のフレームへ）
    2. モジュールの `AnchorTagFieldDesign`（一覧の行から詳細へ。`PageFrame` ＋ `Module`）
    3. スクリプトの `GetModuleUrl` / `GetModuleDataUrl`（ボタンから遷移する）

    **2 と 3 を見ていないと、いちばん増えた形が素通りする**——`JournalEntryList` の「開く」も
    `JournalEntryBoard` の「新規作成」もこの形で、`Main.frm.json` の
    `OtherPageModuleDesigns` から 1 行消すだけで静かに真っ白になる（2026-08-31 の自己レビュー）。

    **引数が省ける形は「現在のフレームで解決される」**（ADR-0027）。だから
    3 は**そのモジュールが登録されているフレームすべて**で遷移先を要求する。
    フレーム名を明示した形は、そのフレームだけを見る——
    **フレーム名とモジュール名が衝突しないことを前提にしている**（本プロジェクトでは衝突しない）。
    """
    registered = {doc.get("Name", ""): modules_on_frame(doc) for _, doc in frames}

    for path, doc in frames:
        for side in ("Left", "Right", "Header"):
            for link in (doc.get(side) or {}).get("Links", []):
                target_frame = link.get("PageFrame")
                if not target_frame:
                    continue
                if target_frame not in registered:
                    findings.append((SEV_ERROR, "F-17", relative(path),
                                     f"リンク {link.get('Module', '')} の遷移先フレーム"
                                     f"「{target_frame}」が無い"))
                elif link.get("Module") not in registered[target_frame]:
                    findings.append((SEV_ERROR, "F-17", relative(path),
                                     f"リンク {link.get('Module', '')} が遷移先フレーム"
                                     f"「{target_frame}」に登録されていない（開くと真っ白になる）"))

    def require(path, where, frame, module):
        if module and frame in registered and module not in registered[frame]:
            findings.append((SEV_ERROR, "F-17", relative(path),
                             f"{where} の遷移先 {module} がフレーム「{frame}」に"
                             "登録されていない（押すと真っ白になる）"))

    # ② モジュールの遷移リンク（一覧の行から詳細へ）
    for path, doc in modules:
        for field in doc.get("Fields", []):
            if not field.get("TypeFullName", "").endswith("AnchorTagFieldDesign"):
                continue
            # 相手をスクリプトや変数で決める形は、静的には追えない。
            if field.get("ModuleVariable"):
                continue
            require(path, f"{doc.get('Name', '')}.{field.get('Name', '')}",
                    field.get("PageFrame", ""), field.get("Module", ""))

    # ③ スクリプトからの遷移
    frames_of = {}
    for frame, names in registered.items():
        for name in names:
            frames_of.setdefault(name, set()).add(frame)

    for path, text in scripts:
        owner = os.path.basename(path).split(".", 1)[0]
        for name, args in re.findall(r"(GetModuleDataUrl|GetModuleUrl)\(([^)]*)\)", text):
            literals = re.findall(r'"([^"]*)"', args)
            if not literals:
                continue    # 相手を変数で決める形は静的には追えない
            if len(literals) > 1 and literals[0] in registered:
                require(path, f"{owner} の {name}", literals[0], literals[1])
                continue
            # 引数を省いた形は、そのモジュールが載っているフレームで解決される。
            for frame in sorted(frames_of.get(owner, ())):
                require(path, f"{owner} の {name}", frame, literals[0])


def check_role_conditions(modules, frames, findings):
    """役割で絞る条件が、階層を **OR-of-Equal** で表しているか（ADR-0034）。

    **階層方式の唯一の弱点は書き忘れである。** 見るのは 3 つ。

    1. **下位の役割で絞るなら、上位も入っている**（`staff` だけだと責任者が入れない）
    2. **`IsOrMatch` が真である**——AND で書くと **誰も通らない**（1 つの列が
       2 つの値を同時に取ることは無い）。しかも画面はリンクが消えるだけなので気づけない
    3. **`Comparison` が `Equal` で、`IsNot` が偽である**——否定に化けると意図と逆の集合を通す

    **軸ごとに順位を表で持つ。** 名前を書き並べると、役割が増えた日に片方だけ直る。
    見るキーは 4 つ——`AppAccessConditions` は `app.clprj` にしか無いので入れない。
    """
    hierarchy = {variable: order for variable, (_, order) in ROLE_HIERARCHY.items()}

    def collect(node, into):
        if isinstance(node, dict):
            if node.get("SearchTargetVariable") in hierarchy:
                into.append({
                    "variable": node["SearchTargetVariable"],
                    "value": (node.get("Value") or {}).get("Value"),
                    "comparison": node.get("Comparison"),
                })
            for value in node.values():
                collect(value, into)
        elif isinstance(node, list):
            for value in node:
                collect(value, into)

    def groups_of(node, found):
        if isinstance(node, dict):
            if node.get("TypeFullName", "").endswith("MultiMatchCondition"):
                inner = []
                collect(node.get("Children"), inner)
                if inner:
                    found.append((node.get("IsOrMatch"), node.get("IsNot"), len(inner)))
            for value in node.values():
                groups_of(value, found)
        elif isinstance(node, list):
            for value in node:
                groups_of(value, found)

    keys = ("UserReadCondition", "UserWriteCondition",
            "DataReadCondition", "DataWriteCondition")
    for path, doc in modules + frames:
        for key in keys:
            condition = doc.get(key)
            if not condition:
                continue

            terms = []
            collect(condition, terms)
            if not terms:
                continue

            where = f"{doc.get('Name', '')}.{key}"

            for term in terms:
                if term["comparison"] != "Equal":
                    findings.append((SEV_ERROR, "D-22", relative(path),
                                     f"{where}: 役割の比較は Equal にする"
                                     f"（今は {term['comparison']}）"))

            for variable, order in hierarchy.items():
                values = {t["value"] for t in terms if t["variable"] == variable}
                lowest = min((order.index(v) for v in values if v in order), default=None)
                if lowest is None:
                    continue
                missing = [v for v in order[lowest + 1:] if v not in values]
                if missing:
                    findings.append((SEV_ERROR, "D-22", relative(path),
                                     f"{where}: {variable} を {sorted(values)} で絞るなら、"
                                     f"上位の {missing} も OR で入れる（ADR-0034）"))

            groups = []
            groups_of(condition, groups)
            for is_or, is_not, count in groups:
                if count > 1 and not is_or:
                    findings.append((SEV_ERROR, "D-22", relative(path),
                                     f"{where}: 役割を {count} 件並べているのに IsOrMatch が偽。"
                                     "AND では誰も通らない（1 つの列が 2 つの値を同時に取らない）"))
                if is_not:
                    findings.append((SEV_ERROR, "D-22", relative(path),
                                     f"{where}: 役割の条件を IsNot で否定している"))


def check_app_access_condition(doc, path, findings):
    """アプリ全体のアクセス条件が、`can_access_app` を見ているか。

    **空＝全開放である**（qa/01 F-18 と同じ性質）。ここが空だと、
    **`can_access_app` を偽にしても誰も締め出せない**——退職者がそのまま入れる。
    **「条件が 1 つある」だけでは足りない**——常に真の条件でも通ってしまうので、
    見ている変数まで確かめる（2026-08-31 の自己レビュー）。

    **引数で受け取る。** 実ファイルを自分で読むと、壊した入力を食わせられず
    `--selftest` の対象にできない（qa/03 L-15 の「判定の純粋部分を分ける」）。
    """
    condition = doc.get("AppAccessConditions") or {}
    children = (condition.get("Condition") or {}).get("Children") or []
    variables = {c.get("SearchTargetVariable") for c in children}

    if not condition.get("ModuleName") or "CanAccessApp.Value" not in variables:
        findings.append((SEV_ERROR, "D-23", relative(path),
                         "AppAccessConditions が CanAccessApp を見ていない。"
                         "空＝全開放で、退職者を締め出せない（ADR-0032）"))



def _has_required_mark(doc, field):
    """その欄のラベルに印が出るか（`required-label` か、`RelativeField` を欄に向けた LabelField）。"""
    for f in doc.get("Fields", []):
        if (f.get("TypeFullName", "").endswith("LabelFieldDesign") and f.get("RelativeField") == field):
            return True
    found = False

    def walk(node):
        nonlocal found
        if isinstance(node, dict):
            if (node.get("TypeFullName", "").endswith("FieldLayoutDesign")
                    and node.get("FieldName") == f"{field}Label"
                    and REQUIRED_LABEL_CLASS in (node.get("ClassName") or "").split()):
                found = True
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(doc.get("DetailLayouts", {}))
    return found


def check_exemptions(modules, findings):
    """**免除表が腐っていないか**（2026-09-02 の自己レビュー）。

    `REQUIRED_EXEMPTIONS` も `READ_CONDITION_EXEMPTIONS` も、
    **キーが実在するか・まだ免除が要るかを誰も見ていなかった**。
    危ないのは「腐った行が再武装する」形である——フェーズ 4 で `Partner` に読み取り条件を
    付けても行が残っていれば、**あとで条件が空に戻った日に D-29 は永久に鳴らない**。

    C# 側の `ModuleDependencyTests.許可した相手はすべて実際に取引先部品を名指ししている` は
    同じことを機械化してある。**作法を揃える。**
    """
    by_name = {doc.get("Name"): doc for _, doc in modules}
    where = relative(__file__)

    for name in sorted(READ_CONDITION_EXEMPTIONS):
        doc = by_name.get(name)
        if doc is None:
            findings.append((SEV_ERROR, "D-29", where,
                             f"READ_CONDITION_EXEMPTIONS の {name} がデザインに無い（行を消す）"))
        elif (doc.get("UserReadCondition") or {}).get("ModuleName"):
            findings.append((SEV_ERROR, "D-29", where,
                             f"{name} は読み取り条件を持っているので、免除の行は要らない（消す）"))

    for (module, field), why in sorted(MARK_WITHOUT_REQUIRED.items()):
        doc = by_name.get(module)
        if not (why or "").strip():
            findings.append((SEV_ERROR, "D-20", where,
                             f"MARK_WITHOUT_REQUIRED の {module}.{field} に理由が無い"
                             "（**理由を書かないと載せられない**）"))
        if doc is None:
            findings.append((SEV_ERROR, "D-20", where,
                             f"MARK_WITHOUT_REQUIRED の {module} がデザインに無い（行を消す）"))
            continue
        fields = {f.get("Name", ""): f for f in doc.get("Fields", [])}
        if field not in fields:
            findings.append((SEV_ERROR, "D-20", where,
                             f"MARK_WITHOUT_REQUIRED の {module}.{field} がデザインに無い（行を消す）"))
        elif fields[field].get("IsRequired"):
            findings.append((SEV_ERROR, "D-20", where,
                             f"{module}.{field} は IsRequired になったので、免除の行は要らない（消す）"))
        elif not _has_required_mark(doc, field):
            # 印を消しても行が残ると、免除表が死んだ条件になる（qa/03 L-35 の型。2026-09-10 の自己レビュー）。
            findings.append((SEV_ERROR, "D-20", where,
                             f"{module}.{field} のラベルに印が無いのに MARK_WITHOUT_REQUIRED に載っている（印を戻すか、行を消す）"))

    for module, column in sorted(REQUIRED_EXEMPTIONS):
        doc = by_name.get(module)
        if doc is None:
            findings.append((SEV_ERROR, "D-25", where,
                             f"REQUIRED_EXEMPTIONS の {module} がデザインに無い（行を消す）"))
        elif column not in columns_the_user_must_fill(doc.get("DbTable") or ""):
            findings.append((SEV_ERROR, "D-25", where,
                             f"{module}.{column} は DDL が必須にしていないので、"
                             "免除の行は要らない（消す）"))


def check_vocabulary(modules, enums, css, findings):
    """**関門が名指ししている語が、デザインに実在するか**（qa/02 R26-22）。

    D-22（役割の階層）も D-23（アプリ全体のアクセス条件）も D-20（必須の印）も、
    **文字列でしかデザインと結ばれていない**。`AppUser.AccountingRole` を改名した瞬間、
    D-22 はどの条件にも当たらなくなり、**全テストが緑のまま関門だけが消える**。
    `EnumConsistencyTests` が「デザイン enum・DDL の CHECK・C# の 3 者一致」を見ているのと
    同じ作法を、こちらにも当てる。

    **役割の値まで見る。** 順序は関門の表にしか無い（enum は集合しか持たない）ので、
    **集合が一致すること**を確かめる——enum に値を足して表に足し忘れると、
    「上位が欠けていないか」の検査がその値を知らないまま緑になる。
    """
    fields_of = {doc.get("Name"): {f.get("Name", "") for f in doc.get("Fields", [])}
                 for _, doc in modules}
    where = relative(__file__)

    if VOCABULARY_MODULE not in fields_of:
        findings.append((SEV_ERROR, "D-27", where,
                         f"関門が名指しする {VOCABULARY_MODULE} モジュールがデザインに無い"))
        return

    names = fields_of[VOCABULARY_MODULE]
    for variable in list(ROLE_HIERARCHY) + [ACCESS_FLAG_VARIABLE]:
        field = variable.split(".", 1)[0]
        if field not in names:
            findings.append((SEV_ERROR, "D-27", where,
                             f"関門が名指しする {VOCABULARY_MODULE}.{field} がデザインに無い"
                             "（改名すると、その条件を見る関門が静かに外れる）"))

    values_of = {doc.get("Name"): {m.get("Value") for m in doc.get("Members", [])}
                 for doc in enums}
    for variable, (enum_name, order) in ROLE_HIERARCHY.items():
        if enum_name not in values_of:
            findings.append((SEV_ERROR, "D-27", where,
                             f"関門が名指しする enum {enum_name} がデザインに無い"))
        elif values_of[enum_name] != set(order):
            findings.append((SEV_ERROR, "D-27", where,
                             f"{variable} の順位表 {order} が enum {enum_name} "
                             f"{sorted(values_of[enum_name])} と食い違っている（ADR-0034）"))

    if REQUIRED_LABEL_CLASS not in css:
        findings.append((SEV_ERROR, "D-27", where,
                         f"必須の印のクラス {REQUIRED_LABEL_CLASS} が app.css に無い"
                         "（印を要求する D-20 が、印の出ないクラスを求めることになる）"))


def check_layout(path, where, layout, kind, field_names, findings):
    for row in layout.get("Rows", []):
        columns = row.get("Columns", [])

        # D-09 検索レイアウトの行は折り返す（1 行 3 組まで）。
        # フィールドを 1 つも置いていない雛形の行は対象にしない。
        placed = [c for c in columns if (c.get("Layout") or {}).get("FieldName")]
        if kind == "Search" and placed and not row.get("IsWrap"):
            findings.append((SEV_WARN, "D-09", relative(path),
                             f"{where}: 検索レイアウトの行は IsWrap: true を標準にする"))

        # 1 行は 3 組（ラベル＋入力）まで。それ以上は右に見切れる。
        if kind == "Search" and len(placed) > 6:
            findings.append((SEV_WARN, "D-09", relative(path),
                             f"{where}: 検索の 1 行は 3 組（ラベル＋入力）までにする（今は {len(placed) // 2} 組）"))

        for column in columns:
            # A-01 旧値は静かに Start へ化ける
            for key in ("HorizontalAlignment", "VerticalAlignment"):
                if column.get(key) in LEGACY_ALIGNMENTS:
                    findings.append((SEV_ERROR, "A-01", relative(path),
                                     f"{where}: {key} の旧値 {column[key]} は Start / End に化ける"))

            # D-10 ラベル列は Middle 揃えにしないと上端に張り付く。
            # 「Xxx」と「XxxLabel」が対で存在するときだけラベル列とみなす
            # （年度名のように名前が Label で終わるだけのフィールドを誤検知しない）。
            #
            # **2026-08-30 に「1 列しかない行は対象外」を足し、2026-08-31 に戻した。**
            # 足した理由（節の見出しとして単独で置くラベルを叩く）は、その見出しを置いた
            # 画面ごと差し戻したので**当たる行が 1 つも無くなった**（自己レビューで 2 人が独立に指摘）。
            # **理由の消えた緩和を残さない**——次の当て漏らしを静かに通す。
            # 同じ形が本当に要るようになったら、そのとき鳴らして足す。
            field_name = (column.get("Layout") or {}).get("FieldName", "")
            is_label_column = (field_name.endswith("Label")
                               and field_name[:-len("Label")] in field_names)
            if is_label_column and column.get("VerticalAlignment") != "Middle":
                findings.append((SEV_WARN, "D-10", relative(path),
                                 f"{where}: ラベル列 {field_name} に VerticalAlignment: Middle が要る"))

            nested = column.get("Layout") or {}
            if "Rows" in nested:
                check_layout(path, where, nested, kind, field_names, findings)


def check_page_frame(path, doc, findings, module_tables=None):
    """ページフレーム 1 枚を見る。

    **リンクだけでなく着地（TopPageModuleDesign / OtherPageModuleDesigns）も見る。**
    フレームが 2 枚になってから、着地の指定が新しく荷重を負った（ADR-0025 §3）のに、
    D-05・D-07 はリンクにしか掛かっていなかった。

    `module_tables` はモジュール名 → `DbTable`。D-05 が「表を持つモジュールか」を見るために要る。
    """
    module_tables = module_tables or {}
    check_variants(path, doc, findings)
    targets = []
    for side in ("Left", "Right", "Header"):
        for link in (doc.get(side) or {}).get("Links", []):
            targets.append(("リンク", link))
    if doc.get("TopPageModuleDesign"):
        targets.append(("着地", doc["TopPageModuleDesign"]))
    for other in doc.get("OtherPageModuleDesigns") or []:
        targets.append(("その他のページ", other))

    for where, link in targets:
        module = link.get("Module", "")

        # D-05 実測してあるのは **"List" が詳細を真っ白にする**ことだけである（qa/01 D-05）。
        #
        # **もとは「Auto 以外は全部エラー」と書いてあった**（2026-08-30 に絞った）。
        # そのせいで、CLB マニュアルが正規に示す `Detail`——1 行しか持たないモジュールを
        # 一覧を挟まずに開く形（自社情報）と、表を持たない表示専用モジュールを載せる形
        # （ADR-0027 の `JournalEntryBoard`）——まで叩いていた。
        # **関門は足したときが完成ではない**（docs/31_検証のルール.md §4）。
        # **白リストで受ける。** 黒リスト（List だけ禁じる）にすると、`"list"` のような
        # 綴り違いや、CLB が将来増やす値が無言で通る——JSON の enum は大小を無視して読むので、
        # `"list"` は D-05 が防いでいる「詳細が真っ白」を再現しつつ関門は緑になりうる
        # （2026-08-31 の自己レビュー）。
        page_type = link.get("ModulePageType") or "Auto"
        if page_type not in ("Auto", "ListToDetail", "List", "Detail"):
            findings.append((SEV_ERROR, "D-05", relative(path),
                             f"{where} {module}: 知らない ModulePageType「{page_type}」"
                             "（Auto / ListToDetail / List / Detail のどれかにする）"))
        elif page_type == "List":
            findings.append((SEV_ERROR, "D-05", relative(path),
                             f"{where} {module}: ModulePageType が List だと /{module}/{{id}} の"
                             "ルートが登録されず詳細が真っ白になる（Auto にする）"))
        elif page_type == "Detail" and module_tables.get(module) and not link.get("Id"):
            # 表を持つモジュールを Detail で開くなら、どの行かが決まっていなければならない。
            # Id が空だと「どの行でもない詳細」になり、designcheck は何も言わない。
            findings.append((SEV_ERROR, "D-05", relative(path),
                             f"{where} {module}: ModulePageType が Detail なのに Id が空。"
                             "表を持つモジュールは開く行を決める（例: 自社情報の Id=\"1\"）"))

        # D-07 リンクを複製したときの直し忘れ
        condition_module = (((link.get("ListPageDesign") or {})
                             .get("ListFieldDesign") or {})
                            .get("SearchCondition") or {}).get("ModuleName", "")
        if condition_module and condition_module != module:
            findings.append((SEV_ERROR, "D-07", relative(path),
                             f"{where} {module}: SearchCondition.ModuleName が {condition_module} を指している"))


def check_application_root(frames, findings):
    """ルート URL の着地フレームがあるか。

    **これが 0 件だと、CLB は非 application-root のフレームへ黙ってフォールバックする**
    （CLB 仕様リファレンス CommonMistakes #54）。権限で絞った補助フレームが既定の着地に
    選ばれる事故につながり、designcheck は鳴らない。
    **新しい PageFrame は既定が false なので、フレームを足すたびに踏みうる。**
    """
    roots = []
    for path in frames:
        try:
            with open(path, encoding="utf-8") as handle:
                doc = json.load(handle)
        except (OSError, ValueError):
            continue    # 読めないことは呼び出し側が別に報告する
        if doc.get("IsApplicationRoot"):
            roots.append(relative(path))

    if not roots:
        findings.append((SEV_ERROR, "D-13", str(DESIGN_DIR),
                         "IsApplicationRoot が true のページフレームが 1 枚も無い。"
                         "ルート URL を開くと非 root のフレームへ黙って落ちる（CommonMistakes #54）"))


def _blank(text, pattern):
    """`pattern` に当たる部分を、**同じ長さの空白**に置き換える。

    消さずに空白にするのは、**位置がずれると「どちらが先か」の判定が狂う**からである
    （F-15 は `ValidateInput()` が `Submit()` より前にあるかを見る）。改行は残す。
    """
    def blank(match):
        return "".join(c if c == "\n" else " " for c in match.group(0))

    return re.sub(pattern, blank, text, flags=re.DOTALL)


# コメントは検査の対象にしない。**この規則の説明そのものがコメントに書いてある**ので、
# 素で走らせると自分の解説文を叩く（2026-09-02 に F-15 で実際に起きた）。
_COMMENTS = r"//[^\n]*|/\*.*?\*/"

# 文字列リテラルも落とす。利用者に見せる文言に `Submit()` と書けば当たってしまう。
_STRINGS = r'"(?:\\.|[^"\\\n])*"'

# **文字列とコメントは 1 回で、左から順に消す。** 先にコメントだけ消すと、`"https://x"` の `//` 以降が
# 行末まで消えて閉じ引用符が無くなり、以降の文字列の空白化がずれる（2026-09-10 の自己レビュー）。
_STRINGS_AND_COMMENTS = _STRINGS + "|" + _COMMENTS


def _blank_comments(text):
    """コメントだけを空白にする。文字列の中の `//` は残す（A-06 は文字列の中を見る）。"""

    def blank(match):
        if match.group(1) is None:
            return match.group(0)
        return "".join(c if c == "\n" else " " for c in match.group(0))

    return re.sub(_STRINGS + "|(" + _COMMENTS + ")", blank, text, flags=re.DOTALL)


def check_script(path, text, findings):
    name = relative(path)
    text = _blank_comments(text)
    code = _blank(text, _STRINGS_AND_COMMENTS)

    # B-01 try / catch / finally はロードできない
    for match in re.finditer(r"^\s*(try|catch|finally)\b", text, re.MULTILINE):
        findings.append((SEV_ERROR, "B-01", name,
                         f"{match.group(1)} は CLB スクリプトで使えない"))

    # B-10 既定値つきの引数は、省略した呼び出しが実行時に「操作が存在しません」で落ちる（designcheck は緑）。
    # 2026-09-09 に足した `string done = ""` で、訂正・取消が翌日まで壊れていた（qa/03 L-41）。
    for match in _METHOD_HEAD.finditer(code):
        if _ASSIGNMENT.search(match.group(2)):
            findings.append((SEV_ERROR, "B-10", name,
                             f"{match.group(1)}(): 引数に既定値を書かない（CLB は省略した呼び出しを解決しない。"
                             "全部の呼び出しで全部の引数を渡す。qa/01 B-10）"))

    # B-11 bool を返すメソッドの foreach の中で値を返すと、呼び出し元が黙って false 側に落ちる
    # （2026-09-10 実測。OnLocationChanging から呼んだ HasUserChanges で、確認が出ずに遷移だけが止まった。qa/01 B-11）。
    # void の `return;`（SelectFiscalYear）は動いているので、値を返す return だけを見る。
    for method, body in _methods(code):
        if not re.search(r"^bool\s+" + re.escape(method) + r"\s*\(", code, re.MULTILINE):
            continue
        for block in _foreach_blocks(body):
            if re.search(r"\breturn\s+[^;\s][^;]*;", block):
                findings.append((SEV_ERROR, "B-11", name,
                                 f"{method}(): foreach の中で値を返さない（フラグで受けて外で返す。qa/01 B-11）"))
                break

    # F-43 新規作成の画面で IsModified が真になる初期値の欄は、HasUserChanges の除外と揃える
    # （揃っていないと、新規の画面を開いて戻るだけで「保存していない変更があります」が出る。qa/01 F-43）。
    bodies = dict(_methods(code))
    # 除外の欄名は文字列の中にある——`code` は文字列を空白にしてあるので、コメントだけ消した `text` から読む。
    bodies_with_strings = dict(_methods(text))
    if "HasUserChanges" in bodies and "Detail_OnAfterInitialization" in bodies:
        excluded = set(re.findall(r'name\s*!=\s*"(\w+)"', bodies_with_strings.get("HasUserChanges", "")))
        for field in sorted(set(re.findall(r"^\s*(\w+)\.Value\s*=[^=]", bodies["Detail_OnAfterInitialization"], re.MULTILINE))):
            if field not in excluded:
                findings.append((SEV_ERROR, "F-43", name,
                                 f"Detail_OnAfterInitialization が {field} に初期値を入れるのに、HasUserChanges が除いていない"
                                 "（新規作成の画面で離脱の確認が毎回出る。qa/01 F-43）"))

    # A-06 数値は decimal に統一されるので整数専用書式は実行時に落ちる
    for match in re.finditer(r'ToString\("[DdXx]\d*"\)', text):
        findings.append((SEV_ERROR, "A-06", name,
                         f'{match.group(0)} は実行時に落ちる。文字列補間 $"{{n:000}}" を使う'))

    # C-01 .Value を書かないとソートが黙って無効になる
    for match in re.finditer(r"\.(OrderBy|OrderByDescending|ThenBy|ThenByDescending)\(([^)]*)\)", text):
        if ".Value" not in match.group(2):
            findings.append((SEV_ERROR, "C-01", name,
                             f"{match.group(1)} のラムダは .Value まで書く（今は {match.group(2).strip()}）"))

    # F-15 スクリプトからの Submit() は CLB 本来の入力検証を走らせない
    for method, body in _methods(code):
        submit = _SUBMIT.search(body)
        if not submit:
            continue
        validate = _VALIDATE_INPUT.search(body)
        if validate is None or validate.start() > submit.start():
            findings.append((SEV_ERROR, "F-15", name,
                             f"{method}(): Submit() の前に ValidateInput() を呼ぶ。"
                             "呼ばないと IsRequired が効かず、必須の空欄がそのまま保存へ進んで "
                             "生の SQLite の文言がトーストに出る（qa/01 F-15・qa/03 L-16）"))


# 列 0 から始まるメソッドの見出し（`void Foo()` / `string Bar(DateOnly d)`）。
# CLB スクリプトはメソッドを平らに並べるので、これで区切れる。
# **引数の中の括弧を 2 段まで許す**（`int n = Foo(Bar())`・タプル型）。許さないと既定値が `)` を含むとき
# 見出しに見えず、B-10 だけでなく F-15 の区切りからも落ちる。3 段は自己テストで「見えない」と分かるようにしてある。
_METHOD_HEAD = re.compile(
    r"^[A-Za-z_][\w<>\[\],?\s]*\s+(\w+)\s*\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)\s*$", re.MULTILINE)

# 引数の並びの中の代入（既定値）。`==`・`!=`・`<=`・`>=`・`=>` は外す。文字列は先に空にしてから当てる。
_ASSIGNMENT = re.compile(r"(?<![=!<>])=(?![=>])")

# **`row.Submit()` は別のインスタンスの保存**なので対象にしない（qa/01 F-03）。
# `(?<![\w.])` が、直前がドットの形（＝他のオブジェクトのメソッド）を落とす。
# **引数のある形も見る。** CLB は `Submit()` と `Submit(List<Module>)` の 2 つを公開している。
_SUBMIT = re.compile(r"(?<![\w.])(?:this\.)?Submit\s*\([^)]*\)")
_VALIDATE_INPUT = re.compile(r"(?<![\w.])(?:this\.)?ValidateInput\s*\([^)]*\)")


def _foreach_blocks(body):
    """本文の中の foreach ブロック（`{`〜対応する `}`）の中身を順に返す。入れ子は外側ごと 1 つに数える。"""
    for match in re.finditer(r"\bforeach\s*\(", body):
        start = body.find("{", match.end())
        if start < 0:
            continue
        depth = 0
        for i in range(start, len(body)):
            if body[i] == "{":
                depth += 1
            elif body[i] == "}":
                depth -= 1
                if depth == 0:
                    yield body[start + 1:i]
                    break


def _methods(text):
    """スクリプトを (メソッド名, 本文) に切り分ける。"""
    heads = list(_METHOD_HEAD.finditer(text))
    for index, head in enumerate(heads):
        end = heads[index + 1].start() if index + 1 < len(heads) else len(text)
        yield head.group(1), text[head.end():end]


def load_json(path, findings):
    """壊れた JSON があっても、そこで検査全体を終わらせない。"""
    try:
        return json.load(io.open(path, encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError) as e:
        findings.append((SEV_ERROR, "JSON", relative(path), f"読み込めない: {e}"))
        return None


def main() -> int:
    findings: list[tuple[str, str, str, str]] = []

    modules = design_files("*.mod.json")
    frames = design_files("*.frm.json")
    scripts = design_files("*.mod.cs")

    # 検査対象が 1 つも無いのに「error: 0」を出すと、緑を見て「見たはず」と誤解する。
    if not modules or not frames:
        print(f"error	SETUP	{DESIGN_DIR}	検査対象が見つからない（モジュール {len(modules)} / ページフレーム {len(frames)}）")
        print("")
        print("検査ファイル数: 0 / error: 1 / warn: 0")
        return 1

    module_tables = {}
    loaded_modules = []
    for path in modules:
        doc = load_json(path, findings)
        if doc is not None:
            module_tables[doc.get("Name", "")] = doc.get("DbTable", "")
            loaded_modules.append((path, doc))
            check_module(path, doc, findings)

    loaded_frames = []
    for path in frames:
        doc = load_json(path, findings)
        if doc is not None:
            loaded_frames.append((path, doc))
            check_page_frame(path, doc, findings, module_tables)
    check_application_root(frames, findings)

    loaded_scripts = [(path, io.open(path, encoding="utf-8").read()) for path in scripts]
    for path, text in loaded_scripts:
        check_script(path, text, findings)

    check_cross_frame_links(loaded_frames, findings, loaded_modules, loaded_scripts)
    check_child_parent_keys(loaded_modules, findings)
    check_child_detail_screens(loaded_modules, loaded_frames, loaded_scripts, findings)
    check_module_references(loaded_modules, loaded_scripts, findings)
    check_role_conditions(loaded_modules, loaded_frames, findings)
    app_settings = os.path.join(DESIGN_DIR, "app.clprj")
    if os.path.exists(app_settings):
        check_app_access_condition(
            json.load(io.open(app_settings, encoding="utf-8")), app_settings, findings)

    enums = [d for d in (load_json(p, findings) for p in design_files("*.enum.json"))
             if d is not None]
    app_css = os.path.join(DESIGN_DIR, "app.css")
    css = io.open(app_css, encoding="utf-8").read() if os.path.exists(app_css) else ""
    check_vocabulary(loaded_modules, enums, css, findings)
    check_exemptions(loaded_modules, findings)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]
    for severity, rule, path, message in sorted(findings):
        print(f"{severity}\t{rule}\t{path}\t{message}")

    print("")
    print(f"検査ファイル数: {len(design_files('*.mod.json')) + len(design_files('*.frm.json')) + len(design_files('*.mod.cs'))}"
          f" / error: {len(errors)} / warn: {len(warns)}")
    return 1 if errors else 0


SELFTEST_CASES = [
    # (何を壊すか, 壊した姿, 期待する (severity, ルール)[, 指摘文に必ず入る語])
    #
    # **4 つ目は、同じルールに壊れ方が複数あるときに書く**（qa/03 L-17）。
    # 鳴ったことだけを見ると、**直し方が入れ替わっても・薄まっても気づけない**。
    ("予約名の型",
     lambda: _module(Fields=[{"Name": "Id", "TypeFullName": "X.NumberFieldDesign"}]),
     (SEV_ERROR, "F-09")),
    ("論理削除の列",
     lambda: _module(Fields=[{"Name": "LogicalDelete", "TypeFullName": "X.BooleanFieldDesign"}]),
     (SEV_ERROR, "PRJ-01")),
    ("画面側の入力検証",
     lambda: _module(Fields=[{"Name": "A", "TypeFullName": "X.TextFieldDesign",
                              "OnValidateInput": "Check"}]),
     (SEV_ERROR, "F-01")),
    ("3 値以外のボタンの色",
     lambda: _module(Fields=[{"Name": "B", "TypeFullName": "X.ButtonFieldDesign",
                              "Variant": "Warning"}]),
     (SEV_ERROR, "D-18")),
    # **入れ子の中に書いても同じ色が出る。** `Fields` の直下しか見ていなかった（R28-17）。
    ("レイアウトの入れ子に書いたボタンの色",
     lambda: _module(DetailLayouts={"": {"Layout": {"Rows": [
         {"Columns": [{"Layout": {"FieldName": "B", "Variant": "Info"}}]}]}}}),
     (SEV_ERROR, "D-18")),
    ("検索条件が既定で閉じている",
     lambda: _module(SearchLayouts={"": {"Layout": {
         "IsExpandable": True, "IsExpanderDefaultOpened": False,
         "Rows": [{"Columns": [{"Layout": {"FieldName": "A"}}]}]}}}),
     (SEV_ERROR, "D-19")),
    # **名前つきの検索レイアウトも見る**（R28-17）。既定（`""`）だけを見ていた。
    ("名前つきの検索条件が既定で閉じている",
     lambda: _module(SearchLayouts={"絞り込み": {"Layout": {
         "IsExpandable": True, "IsExpanderDefaultOpened": False,
         "Rows": [{"Columns": [{"Layout": {"FieldName": "A"}}]}]}}}),
     (SEV_ERROR, "D-19")),
    ("洗い替えを使っている",
     lambda: _module(Fields=[{"Name": "Lines", "TypeFullName": "X.ListFieldDesign",
                              "ReplaceMode": "All"}]),
     (SEV_ERROR, "D-26")),
    ("DDL が必須にした欄が任意になっている",
     lambda: _module(DbTable=SELFTEST_TABLE, CanCreate=True,
                     UserWriteCondition={"ModuleName": "AppUser"},
                     Fields=[{"Name": "Code", "DbColumn": SELFTEST_REQUIRED_COLUMN,
                              "TypeFullName": "X.TextFieldDesign", "IsRequired": False}]),
     (SEV_ERROR, "D-25"), "IsRequired: true にする"),
    ("必須の列を書くフィールドがどこにも無い",
     lambda: _module(DbTable=SELFTEST_TABLE, CanCreate=True,
                     UserWriteCondition={"ModuleName": "AppUser"}, Fields=[]),
     (SEV_ERROR, "D-25"), "値を書くフィールドがどこにも無い"),
    ("必須の欄に印が無い",
     lambda: _required_module(class_name=""), (SEV_ERROR, "D-20"), "印が出ない"),
    ("必須の欄にラベル要素が無い",
     lambda: _required_module(with_label=False), (SEV_ERROR, "D-20"), "ラベル要素"),
    # **両方を書くと `*` が 2 つ並ぶ**（2026-09-02 に AppUser で実際に出した）。
    ("CLB が出す印にクラスを重ねている",
     lambda: _required_module(relative=True), (SEV_ERROR, "D-20"), "重ねない"),
    ("データを持つのに書き込み条件が無い",
     lambda: _module(DbTable="x"), (SEV_ERROR, "D-24")),
    ("データを持つのに読み取り条件が無い",
     lambda: _module(DbTable="x", UserWriteCondition={"ModuleName": "AppUser"}),
     (SEV_ERROR, "D-29")),
    # **画面に出す日時に書式が無いと、秒まで並ぶ**（2026-09-02 に仕訳帳で実際に出した）。
    ("画面に出す日時に書式が無い",
     lambda: _module(Fields=[{"Name": "EnteredAt", "TypeFullName": "X.DateTimeFieldDesign",
                              "Format": ""}],
                     ListLayouts={"": {"Layout": {"Rows": [
                         {"Columns": [{"Layout": {"FieldName": "EnteredAt"}}]}]}}}),
     (SEV_ERROR, "D-30"), DATETIME_DISPLAY_FORMAT),
    # **別の書式でも鳴る。** 「空でない」だけを見ると、秒つきを書いた日に黙る。
    ("画面に出す日時に別の書式を書いている",
     lambda: _module(Fields=[{"Name": "EnteredAt", "TypeFullName": "X.DateTimeFieldDesign",
                              "Format": "yyyy-MM-dd HH:mm:ss"}],
                     ListLayouts={"": {"Layout": {"Rows": [
                         {"Columns": [{"Layout": {"FieldName": "EnteredAt"}}]}]}}}),
     (SEV_ERROR, "D-30"), DATETIME_DISPLAY_FORMAT),
    ("担当の条件に責任者が入っていない",
     lambda: _module(UserWriteCondition=_role_condition(["staff"])), (SEV_ERROR, "D-22")),
    ("役割を AND で並べている",
     lambda: _module(UserWriteCondition=_role_condition(["staff", "manager"], is_or=False)),
     (SEV_ERROR, "D-22")),
    ("役割の条件を否定している",
     lambda: _module(UserWriteCondition=_role_condition(["staff", "manager"], is_not=True)),
     (SEV_ERROR, "D-22")),
    ("役割の比較が Equal でない",
     lambda: _module(UserWriteCondition=_role_condition(["staff", "manager"],
                                                        comparison="NotEqual")),
     (SEV_ERROR, "D-22")),
    ("行の条件に書いた役割も見る",
     lambda: _module(DataReadCondition=_role_condition(["staff"])), (SEV_ERROR, "D-22")),
]

# `main()` が呼ぶべき検査。**ここに載っているものが全部呼ばれているか**を selftest が見る。
# 呼び出しを 1 行消しても緑になる作りだと、検査は在っても効かない（qa/03 L-15）。
WIRED_CHECKS = [
    "check_module", "check_page_frame", "check_application_root",
    "check_script", "check_cross_frame_links", "check_child_parent_keys",
    "check_child_detail_screens", "check_module_references", "check_role_conditions",
    "check_app_access_condition", "check_vocabulary", "check_exemptions",
]


# D-25 の検体に使う実在の表。**実物の DDL を使う**——架空の表を置くと、
# 「NOT NULL の読み方」そのものが壊れたときに検体も一緒に壊れて気づけない。
SELFTEST_TABLE = "departments"
SELFTEST_REQUIRED_COLUMN = "code"


def _module(**overrides):
    """検査に掛ける最小のモジュール定義。"""
    doc = {"Name": "SelfTest", "DbTable": "", "CanUpdate": False, "Fields": [],
           "DetailLayouts": {}, "SearchLayouts": {}, "ListLayouts": {}}
    doc.update(overrides)
    return doc


def _required_module(class_name=REQUIRED_LABEL_CLASS, with_label=True, relative=False):
    """必須の欄が 1 つある詳細レイアウト。

    `relative` が真なら、ラベルの `RelativeField` を必須の欄に向ける
    （CLB が自分で印を出す形。認証部品の `AppUser` がこれである）。
    """
    columns = [{"Layout": {"FieldName": "Code", "ClassName": "",
                           "TypeFullName": "X.FieldLayoutDesign"}}]
    if with_label:
        # ラベル列は Middle 揃え（D-10）。**実物と同じ形で作る**——
        # 検体が実データと違うと、正しい姿のはずが別の関門を鳴らす（qa/03 L-17）。
        columns.insert(0, {"VerticalAlignment": "Middle",
                           "Layout": {"FieldName": "CodeLabel", "ClassName": class_name,
                                      "TypeFullName": "X.FieldLayoutDesign"}})
    label = {"Name": "CodeLabel", "TypeFullName": "X.LabelFieldDesign"}
    if relative:
        label["RelativeField"] = "Code"
    return _module(
        Fields=[{"Name": "Code", "TypeFullName": "X.TextFieldDesign", "IsRequired": True}, label],
        DetailLayouts={"": {"Layout": {"Rows": [{"Columns": columns}]}}})


def _role_condition(values, is_or=True, is_not=False, comparison="Equal"):
    return {
        "ModuleName": "AppUser",
        "Condition": {
            "IsOrMatch": is_or, "IsNot": is_not, "Name": "",
            "TypeFullName": "Codeer.LowCode.Blazor.Repository.Match.MultiMatchCondition",
            "Children": [{
                "SearchTargetVariable": "AccountingRole.Value", "Comparison": comparison,
                "Value": {"Value": v, "TypeFullName": "Codeer.LowCode.Blazor.Repository.StringValue"},
                "TypeFullName": "Codeer.LowCode.Blazor.Repository.Match.FieldValueMatchConditionNonNull",
            } for v in values],
        },
    }


def _header_detail(parent_key):
    """親（`ListField` で子を絞る）と子（親 FK を持つ）の 2 モジュール。

    **実物と同じ形で作る**——親の絞り込みは `SearchCondition.Condition` の中に
    入れ子で入っており、そこから親 FK の名前を拾えることがこの検査の要である。
    `parent_key` が `None` なら、子に親 FK が無い姿になる。
    """
    header = {
        "Name": "Header",
        "Fields": [{
            "Name": "Rows",
            "TypeFullName": "X.ListFieldDesign",
            "CanCreate": True,
            "SearchCondition": {
                "ModuleName": "Row",
                "Condition": {
                    "TypeFullName": "X.MultiMatchCondition",
                    "Children": [{
                        "TypeFullName": "X.FieldMatchCondition",
                        "Children": [{
                            "SearchTargetVariable": "Parent.Value",
                            "Comparison": "Equal",
                            "Variable": "Id.Value",
                            "TypeFullName": "X.FieldVariableMatchCondition",
                        }],
                    }],
                },
            },
        }],
    }
    row = {"Name": "Row", "CanCreate": True,
           "Fields": [f for f in [parent_key] if f is not None]}
    return [(_self_path(name="Header.mod.json"), header),
            (_self_path(name="Row.mod.json"), row)]


def _self_path(app="Accounting", name="SelfTest.mod.json"):
    return os.path.join(DESIGN_DIR, "Modules", app, name)


def selftest():
    """**関門が本当に鳴るかを、関門自身が確かめる。**

    足したときに手で壊して確かめても、**次に緩めたときには誰も確かめない**
    （2026-08-31 の自己レビューで、D-05・D-10 を緩めた変更にテストが 1 本も無かった。qa/02 R26-21）。
    ここが赤くなったら、検査が空回りしている。

    **severity まで表明する。** ルールだけを見ると、`error` を `warn` に書き換えるだけで
    **selftest も本検査も緑のまま、関門だけが消える**（同 R27-18）。
    """
    failures = []

    if not SELFTEST_CASES:
        failures.append("検体が 0 件である（0 は「違反が無い」ではなく「配線が死んだ」を疑う数字）")

    def run_module_checks(doc, findings):
        check_module(_self_path(), doc, findings)
        check_role_conditions([(_self_path(), doc)], [], findings)

    for case in SELFTEST_CASES:
        label, build, expected = case[:3]
        says = case[3] if len(case) > 3 else ""
        findings = []
        run_module_checks(build(), findings)
        if not [f for f in findings if (f[0], f[1]) == expected and says in f[3]]:
            failures.append(f"{label}: {expected}{f'（「{says}」と言う）' if says else ''} が鳴らない"
                            f"（出たのは {[(f[0], f[1], f[3]) for f in findings]}）")

    # 正しい姿では鳴らない（鳴りっぱなしの関門は、赤を無視させる）
    for label, doc in [
        ("表を持たないモジュール", _module()),
        ("書き込み条件と読み取り条件のあるモジュール",
         _module(DbTable="x", UserWriteCondition={"ModuleName": "AppUser"},
                 UserReadCondition={"ModuleName": "AppUser"})),
        # **免除表に載っているモジュールは読み取り条件が無くてよい**（ADR-0033 の例外）。
        ("免除表に載っているモジュール",
         _module(Name=next(iter(READ_CONDITION_EXEMPTIONS)), DbTable="x",
                 UserWriteCondition={"ModuleName": "AppUser"})),
        ("印の付いた必須の欄", _required_module()),
        ("他のクラスと併記した印", _required_module(class_name="ms-2 required-label")),
        # **CLB が RelativeField で出す印だけでよい**（クラスは付けない）。
        ("CLB が出す印だけの必須の欄", _required_module(class_name="", relative=True)),
        ("階層を OR で書いた条件",
         _module(UserWriteCondition=_role_condition(["staff", "manager"]))),
        # **折りたためない検索レイアウトは常に開いている**（R28-17 の誤検知）。
        ("折りたためない検索レイアウト",
         _module(SearchLayouts={"": {"Layout": {
             "IsExpandable": False, "IsExpanderDefaultOpened": False,
             "Rows": [{"IsWrap": True, "Columns": [{"Layout": {"FieldName": "A"}}]}]}}})),
        # **書き込み経路の無いモジュールは D-25 の対象外**（会計年度・会計期間がこれである）。
        # **両方を明示する**——実物がそう書いてあり、既定は「書ける」に倒してある。
        ("参照だけのモジュール",
         _module(DbTable=SELFTEST_TABLE, CanCreate=False, CanUpdate=False,
                 UserWriteCondition={"ModuleName": "AppUser"},
                 UserReadCondition={"ModuleName": "AppUser"})),
        ("洗い替えを使わない一覧",
         _module(Fields=[{"Name": "Lines", "TypeFullName": "X.ListFieldDesign",
                          "ReplaceMode": "None"}])),
        ("書式を書いた日時の欄",
         _module(Fields=[{"Name": "EnteredAt", "TypeFullName": "X.DateTimeFieldDesign",
                          "Format": DATETIME_DISPLAY_FORMAT}],
                 ListLayouts={"": {"Layout": {"Rows": [
                     {"Columns": [{"Layout": {"FieldName": "EnteredAt"}}]}]}}})),
        # **どの画面にも置いていない日時は対象外**（CreatedAt / UpdatedAt がこれである）。
        ("画面に出していない日時の欄",
         _module(Fields=[{"Name": "CreatedAt", "TypeFullName": "X.DateTimeFieldDesign",
                          "Format": ""}])),
    ]:
        findings = []
        run_module_checks(doc, findings)
        if findings:
            failures.append(f"正しい{label}で鳴った: {[(f[0], f[1]) for f in findings]}")

    # **フレームに書いたボタンの色も見る**（`check_variants` はモジュール専用ではない）。
    # **`check_page_frame` 経由で確かめる**——直接呼ぶと、フレーム側の配線を消しても緑になる。
    findings = []
    check_page_frame("a.frm.json", {"Name": "A", "Left": {"Links": [
        {"Module": "X", "Variant": "Link"}]}}, findings)
    if (SEV_ERROR, "D-18") not in [(f[0], f[1]) for f in findings]:
        failures.append("フレームに書いたボタンの色: D-18 が鳴らない")

    # スクリプトの検査（R28-17。`check_script` は selftest から一度も呼ばれていなかった）。
    for label, script, expected, says in [
        ("try は使えない", "void A()\n{\n    try\n    {\n    }\n}\n", (SEV_ERROR, "B-01"), ""),
        ("既定値つきの引数", 'void Amend(string a, string done = "")\n{\n}\n', (SEV_ERROR, "B-10"), "Amend(): "),
        ("数値の既定値つきの引数", "void A(int n = 0)\n{\n}\n", (SEV_ERROR, "B-10"), "A(): "),
        ("括弧を含む既定値", "void A(List<int> a = new List<int>())\n{\n}\n", (SEV_ERROR, "B-10"), "A(): "),
        ("括弧 2 段の既定値", "void A(int n = Foo(Bar()))\n{\n}\n", (SEV_ERROR, "B-10"), "A(): "),
        ("文字列の中に = がある既定値", 'void A(string a = "=")\n{\n}\n', (SEV_ERROR, "B-10"), "A(): "),
        ("null の既定値", "void A(string a = null, int b = -1)\n{\n}\n", (SEV_ERROR, "B-10"), "A(): "),
        # **文字列の中の `//` をコメントと取り違えない。** 取り違えると閉じ引用符が消え、以降の判定がずれる。
        ("URL の文字列の後の Submit", 'void A()\n{\n    var u = "https://x";\n    Submit();\n}\n',
         (SEV_ERROR, "F-15"), "Submit() の前に ValidateInput() を呼ぶ"),
        ("整数専用の書式", 'void A()\n{\n    var s = n.ToString("D2");\n}\n',
         (SEV_ERROR, "A-06"), ""),
        ("bool メソッドの foreach の中で値を返す",
         'bool A()\n{\n    foreach (var n in Xs())\n    {\n        if (n != "a") return true;\n    }\n    return false;\n}\n',
         (SEV_ERROR, "B-11"), "A(): "),
        ("初期値の欄を HasUserChanges が除いていない",
         'void Detail_OnAfterInitialization()\n{\n    if (!IsNewData) return;\n    IsActive.Value = true;\n}\n'
         'bool HasUserChanges()\n{\n    var changed = false;\n    foreach (var name in this.GetModifiedFieldNames())\n    {\n'
         '        if (name != "Id") changed = true;\n    }\n    return changed;\n}\n',
         (SEV_ERROR, "F-43"), "IsActive"),
        ("並べ替えの .Value 落ち", "void A()\n{\n    rows.OrderBy(r => r.Code);\n}\n",
         (SEV_ERROR, "C-01"), ""),
        ("検証を呼ばない Submit", "void A()\n{\n    Submit();\n}\n",
         (SEV_ERROR, "F-15"), "Submit() の前に ValidateInput() を呼ぶ"),
        # **引数のある形も CLB の仕様にある**（`Submit(List<Module>)`）。
        ("引数つきの Submit", "void A()\n{\n    this.Submit(rows);\n}\n",
         (SEV_ERROR, "F-15"), "Submit() の前に ValidateInput() を呼ぶ"),
        ("検証を Submit の後で呼んでいる",
         "void A()\n{\n    Submit();\n    ValidateInput();\n}\n",
         (SEV_ERROR, "F-15"), "Submit() の前に ValidateInput() を呼ぶ"),
        # **別のメソッドの ValidateInput() は、こちらの Submit() を守らない。**
        # **どのメソッドかまで見る**——名前が入れ替わると、直しに行く先が変わる。
        ("隣のメソッドの検証で済ませている",
         "void A()\n{\n    ValidateInput();\n}\n\nvoid B()\n{\n    this.Submit();\n}\n",
         (SEV_ERROR, "F-15"), "B(): "),
    ]:
        findings = []
        check_script(_self_path(name="Script.mod.cs"), script, findings)
        if not [f for f in findings if (f[0], f[1]) == expected and says in f[3]]:
            failures.append(f"スクリプト（{label}）: {expected}"
                            f"{f'（「{says}」と言う）' if says else ''} が鳴らない"
                            f"（出たのは {[(f[0], f[1], f[3]) for f in findings]}）")

    for label, script in [
        ("既定値の無い引数と、本文の代入", 'void A(string a, int b)\n{\n    var x = "=";\n    if (a == b) return;\n}\n'),
        # **見出しの直後の本文の代入と、列 0 のモジュール変数は引数ではない。**
        ("見出しの直後の代入", "void A(int a)\n{\n    a = 1;\n    var f = (int x) => x >= a;\n}\n"),
        ("列 0 のモジュール変数", "bool _busy = false;\n\nvoid A(int a)\n{\n}\n"),
        ("比較を含む引数の無いメソッド", "bool A(int a, int b)\n{\n    return a != b && a <= b;\n}\n"),
        ("検証してから Submit",
         "void A()\n{\n    if (!ValidateInput()) return;\n    var ok = this.Submit();\n}\n"),
        # **コメントの中の Submit() を叩かない。** この規則の解説そのものがコメントに書いてある。
        ("コメントで Submit() に触れているだけ",
         "// this.Submit() は検証を走らせない。\nvoid A()\n{\n}\n"),
        # **`row.Submit()` は別インスタンスの保存**（qa/01 F-03）。
        ("行ごとの Submit", "void A()\n{\n    foreach (var row in Rows) row.Submit();\n}\n"),
    ]:
        findings = []
        check_script(_self_path(name="Script.mod.cs"), script, findings)
        if findings:
            failures.append(f"正しいスクリプト（{label}）で鳴った: {[(f[1], f[3]) for f in findings]}")

    # 関門が名指しする語の実在（D-27）
    good_modules = [(_self_path("Platform", "AppUser.mod.json"),
                     {"Name": VOCABULARY_MODULE, "Fields": [
                         {"Name": "AccountingRole"}, {"Name": "PartnerRole"},
                         {"Name": "CanAccessApp"}]})]
    # **リテラルで書く。** `ROLE_HIERARCHY` から作ると同語反復になり、
    # 順位表を書き換えても selftest は常に一致する（2026-09-02 の自己レビュー）。
    good_enums = [
        {"Name": "AccountingRoles",
         "Members": [{"Value": v} for v in ["viewer", "staff", "manager"]]},
        {"Name": "PartnerRoles", "Members": [{"Value": v} for v in ["viewer", "editor"]]},
    ]
    for label, modules, enums, css in [
        ("モジュールが無い", [], good_enums, REQUIRED_LABEL_CLASS),
        ("フィールドが改名されている",
         [(_self_path("Platform", "AppUser.mod.json"),
           {"Name": VOCABULARY_MODULE, "Fields": [{"Name": "Role"}]})],
         good_enums, REQUIRED_LABEL_CLASS),
        ("enum が無い", good_modules, [], REQUIRED_LABEL_CLASS),
        ("enum に値が増えている",
         good_modules,
         [{"Name": name, "Members": [{"Value": v} for v in list(order) + ["auditor"]]}
          for name, order in ROLE_HIERARCHY.values()],
         REQUIRED_LABEL_CLASS),
        ("印のクラスが app.css に無い", good_modules, good_enums, ""),
    ]:
        findings = []
        check_vocabulary(modules, enums, css, findings)
        if (SEV_ERROR, "D-27") not in [(f[0], f[1]) for f in findings]:
            failures.append(f"関門が名指しする語（{label}）: D-27 が鳴らない")

    findings = []
    check_vocabulary(good_modules, good_enums, REQUIRED_LABEL_CLASS, findings)
    if findings:
        failures.append(f"正しい名指しで鳴った: {[(f[0], f[3]) for f in findings]}")

    # 免除表の腐り（D-25 / D-29）。**実在しない行**と**もう要らない行**の 2 通り。
    exempt_module = next(iter(READ_CONDITION_EXEMPTIONS))
    for label, modules, expected in [
        ("実在しないモジュールの行", [], (SEV_ERROR, "D-29")),
        ("読み取り条件を持ったのに残っている行",
         [(_self_path(), {"Name": exempt_module,
                          "UserReadCondition": {"ModuleName": "AppUser"}})],
         (SEV_ERROR, "D-29")),
    ]:
        findings = []
        check_exemptions(modules, findings)
        if expected not in [(f[0], f[1]) for f in findings]:
            failures.append(f"免除表の腐り（{label}）: {expected} が鳴らない")

    # **免除の行が要らないと言われない**（読み取り条件が空のままなら、免除はまだ要る）。
    findings = []
    check_exemptions(
        [(_self_path(name=f"{name}.mod.json"),
          {"Name": name, "UserReadCondition": {"ModuleName": ""}})
         for name in READ_CONDITION_EXEMPTIONS], findings)
    if [f for f in findings if "免除の行は要らない" in f[3]]:
        failures.append(f"条件が空のままの免除で鳴った: {[(f[1], f[3]) for f in findings]}")

    # フレーム跨ぎのリンクの登録漏れ（F-17）
    findings = []
    check_cross_frame_links([
        ("a.frm.json", {"Name": "A", "TopPageModule": "AHome",
                        "Left": {"Links": [{"Module": "Missing", "PageFrame": "B"}]}}),
        ("b.frm.json", {"Name": "B", "TopPageModule": "BHome", "Left": {"Links": []}}),
    ], findings)
    if (SEV_ERROR, "F-17") not in [(f[0], f[1]) for f in findings]:
        failures.append("フレーム跨ぎの登録漏れ: F-17 が鳴らない")

    # **遷移は 3 通りの形で書かれる。** サイドバー以外の 2 つも鳴ることを確かめる。
    frames = [("a.frm.json", {"Name": "Main", "TopPageModule": "Board", "Left": {"Links": []}})]
    for label, modules, scripts in [
        ("モジュールの遷移リンク",
         [(_self_path(name="List.mod.json"),
           {"Name": "List", "Fields": [{"TypeFullName": "X.AnchorTagFieldDesign",
                                        "Name": "OpenLink", "PageFrame": "Main",
                                        "Module": "Missing", "ModuleVariable": ""}]})], []),
        ("スクリプトの遷移（フレームを省いた形）", [],
         [(_self_path(name="Board.mod.cs"), 'GetModuleDataUrl("Missing", "-")')]),
        ("スクリプトの遷移（フレームを明示した形）", [],
         [(_self_path(name="Board.mod.cs"), 'GetModuleUrl("Main", "Missing")')]),
    ]:
        findings = []
        check_cross_frame_links(frames, findings, modules, scripts)
        if (SEV_ERROR, "F-17") not in [(f[0], f[1]) for f in findings]:
            failures.append(f"{label}: F-17 が鳴らない")

    # 登録されている相手なら鳴らない。
    findings = []
    check_cross_frame_links(
        [("a.frm.json", {"Name": "Main", "TopPageModule": "Board",
                         "Left": {"Links": []},
                         "OtherPageModuleDesigns": [{"Module": "Detail"}]})],
        findings, [], [(_self_path(name="Board.mod.cs"), 'GetModuleDataUrl("Detail", "-")')])
    if findings:
        failures.append(f"登録されている遷移先で鳴った: {[(f[0], f[1]) for f in findings]}")

    # ヘッダ＋明細の親 FK（D-17）。**壊れ方は 3 通りあり、言うべきことがそれぞれ違う。**
    # **文言まで見る**——3 つとも `(error, D-17)` なので、鳴ったことだけでは
    # 直し方が入れ替わっても気づけない（qa/03 L-17）。
    for label, key, says in [
        ("参照フィールド",
         {"Name": "Parent", "TypeFullName": "X.LinkFieldDesign"}, "IdFieldDesign にする"),
        ("手入力できる識別子",
         {"Name": "Parent", "TypeFullName": "X.IdFieldDesign", "IsManualInput": True},
         "IsManualInput: false にする"),
        ("親 FK が無い", None, "が無い"),
    ]:
        findings = []
        check_child_parent_keys(_header_detail(key), findings)
        hit = [f for f in findings if (f[0], f[1]) == (SEV_ERROR, "D-17") and says in f[3]]
        if not hit:
            failures.append(f"親 FK（{label}）: 「{says}」と言う D-17 が鳴らない"
                            f"（出たのは {[(f[1], f[3]) for f in findings]}）")

    # **閲覧専用でも「指し先が無い」は鳴る**（絞り込みが外れると全件が出る）。
    import copy as _c2
    ro_missing = _c2.deepcopy(_header_detail(None))
    for _, doc in ro_missing:
        doc["Name"] = "Rm" + doc["Name"]
    ro_missing[0][1]["Fields"][0]["CanCreate"] = False
    ro_missing[0][1]["Fields"][0]["SearchCondition"]["ModuleName"] = "RmRow"
    findings = []
    check_child_parent_keys(
        _header_detail({"Name": "Parent", "TypeFullName": "X.IdFieldDesign",
                        "IsManualInput": False}) + ro_missing, findings)
    if not [f for f in findings if f[1] == "D-17" and "が無い" in f[3]]:
        failures.append("閲覧専用の一覧の消えた指し先: D-17 が鳴らない")

    # **閲覧専用の一覧では鳴らない**（クエリモジュールの埋め込み。追加の経路が無い）。
    import copy as _copy
    ro = _copy.deepcopy(_header_detail({"Name": "Parent", "TypeFullName": "X.SelectFieldDesign"}))
    for _, doc in ro:
        doc["Name"] = "Ro" + doc["Name"]
    ro[0][1]["Fields"][0]["CanCreate"] = False
    ro[0][1]["Fields"][0]["SearchCondition"]["ModuleName"] = "RoRow"
    ok_pair = _header_detail({"Name": "Parent", "TypeFullName": "X.IdFieldDesign",
                              "IsManualInput": False})
    findings = []
    check_child_parent_keys(ok_pair + ro, findings)
    if findings:
        failures.append(f"閲覧専用の一覧で鳴った: {[(f[1], f[3]) for f in findings]}")

    findings = []
    check_child_parent_keys(
        _header_detail({"Name": "Parent", "TypeFullName": "X.IdFieldDesign",
                        "IsManualInput": False}), findings)
    if findings:
        failures.append(f"正しい親 FK で鳴った: {[(f[0], f[1]) for f in findings]}")

    # **組が 1 つも無ければ鳴る**（検査が空回りしていることを見逃さない）。
    findings = []
    check_child_parent_keys([(_self_path(), {"Name": "Alone", "Fields": []})], findings)
    if (SEV_ERROR, "D-17") not in [(f[0], f[1]) for f in findings]:
        failures.append("親子の組が 0 でも D-17 が鳴らない")

    # 明細モジュールをフレームに登録したときの親 FK（D-28。qa/02 R29-13）。
    pair = _header_detail({"Name": "Parent", "TypeFullName": "X.IdFieldDesign",
                           "IsManualInput": False})
    on_frame = [("a.frm.json", {"Name": "Main", "Left": {"Links": [{"Module": "Row"}]}})]
    off_frame = [("a.frm.json", {"Name": "Main", "Left": {"Links": [{"Module": "Header"}]}})]
    for label, frames_, scripts_, expected in [
        ("登録したのに親 FK を入れていない", on_frame, [], True),
        ("登録しているが親 FK を入れている", on_frame,
         [(_self_path(name="Row.mod.cs"), "void A()\n{\n    Parent.Value = q;\n}\n")], False),
        # **登録されていなければ無害**（`JournalLine` がこれ。qa/02 R26-31 で残すと決めた）。
        ("フレームに登録していない明細", off_frame, [], False),
        # **他人の FK を入れても、この明細は守られない。**
        ("別のフィールドに入れている", on_frame,
         [(_self_path(name="Row.mod.cs"), "void A()\n{\n    Other.Value = q;\n}\n")], True),
        # **比較は代入ではない。** 代入を `\s*=` だけで見ると `==` に当たって黙る。
        ("比較しているだけ", on_frame,
         [(_self_path(name="Row.mod.cs"),
           "void A()\n{\n    if (Parent.Value == q) return;\n}\n")], True),
        # **コメントと文字列は代入ではない。**
        ("コメントに代入を書いただけ", on_frame,
         [(_self_path(name="Row.mod.cs"), "// Parent.Value = q; と書けばよい\nvoid A()\n{\n}\n")], True),
        ("文字列に代入を書いただけ", on_frame,
         [(_self_path(name="Row.mod.cs"),
           "void A()\n{\n    Log(\"Parent.Value = q\");\n}\n")], True),
    ]:
        findings = []
        check_child_detail_screens(pair, frames_, scripts_, findings)
        rang = (SEV_ERROR, "D-28") in [(f[0], f[1]) for f in findings]
        if rang != expected:
            failures.append(f"明細の詳細画面（{label}）: D-28 が"
                            f"{'鳴らない' if expected else '鳴った'}")

    # **作れない子は対象外**（親の詳細に埋め込んだクエリモジュール）。
    read_only = [(p, dict(doc, CanCreate=False) if doc["Name"] == "Row" else doc)
                 for p, doc in pair]
    for label, modules_, expected in [("作れない子", read_only, False)]:
        findings = []
        check_child_detail_screens(modules_, on_frame, [], findings)
        rang = (SEV_ERROR, "D-28") in [(f[0], f[1]) for f in findings]
        if rang != expected:
            failures.append(f"明細の詳細画面（{label}）: D-28 が"
                            f"{'鳴らない' if expected else '鳴った'}")

    # 部品をまたぐ参照の向き（D-21）。JSON の 2 つのキーとスクリプトの 2 つの形を見る。
    for label, modules, scripts in [
        ("条件の ModuleName",
         [(_self_path("Partners", "P.mod.json"),
           {"Name": "P", "UserReadCondition": {"ModuleName": "JournalEntry"}}),
          (_self_path("Accounting", "JournalEntry.mod.json"), {"Name": "JournalEntry"})], []),
        ("遷移リンクの Module",
         [(_self_path("Partners", "P.mod.json"),
           {"Name": "P", "Fields": [{"PageFrame": "Main", "Module": "JournalEntry"}]}),
          (_self_path("Accounting", "JournalEntry.mod.json"), {"Name": "JournalEntry"})], []),
        ("スクリプトの型引数",
         [(_self_path("Accounting", "JournalEntry.mod.json"), {"Name": "JournalEntry"})],
         [(_self_path("Partners", "P.mod.cs"), "new ModuleSearcher<JournalEntry>();")]),
        ("スクリプトの文字列",
         [(_self_path("Accounting", "JournalEntry.mod.json"), {"Name": "JournalEntry"})],
         [(_self_path("Partners", "P.mod.cs"), 'GetModuleDataUrl("JournalEntry", "-")')]),
    ]:
        findings = []
        check_module_references(modules, scripts, findings)
        if (SEV_ERROR, "D-21") not in [(f[0], f[1]) for f in findings]:
            failures.append(f"部品をまたぐ参照（{label}）: D-21 が鳴らない")

    # 認証部品への参照は鳴らない（権限の条件は AppUser の列でしか書けない。F-21）
    findings = []
    check_module_references(
        [(_self_path("Partners", "P.mod.json"),
          {"Name": "P", "UserReadCondition": {"ModuleName": "AppUser"}}),
         (_self_path("Platform", "AppUser.mod.json"), {"Name": "AppUser"})],
        [], findings)
    if findings:
        failures.append(f"認証部品への参照で鳴った: {[(f[0], f[1]) for f in findings]}")

    # アプリ全体のアクセス条件（D-23）
    for label, doc in [
        ("条件が空", {"AppAccessConditions": {"ModuleName": ""}}),
        ("CanAccessApp を見ていない",
         {"AppAccessConditions": {"ModuleName": "AppUser", "Condition": {"Children": [
             {"SearchTargetVariable": "IsSysadmin.Value"}]}}}),
    ]:
        findings = []
        check_app_access_condition(doc, "app.clprj", findings)
        if (SEV_ERROR, "D-23") not in [(f[0], f[1]) for f in findings]:
            failures.append(f"アプリ全体の条件（{label}）: D-23 が鳴らない")

    findings = []
    check_app_access_condition(
        {"AppAccessConditions": {"ModuleName": "AppUser", "Condition": {"Children": [
            {"SearchTargetVariable": "CanAccessApp.Value"}]}}}, "app.clprj", findings)
    if findings:
        failures.append("正しいアプリ全体の条件で鳴った")

    # **配線**。検査を書いても main() から呼ばれていなければ効かない（qa/03 L-15）。
    source = io.open(__file__, encoding="utf-8").read()
    # **`main()` の中だけを見る。** ファイル末尾までを見ると、
    # **selftest 自身の呼び出しを数えてしまい、配線を消しても緑になる**
    # （2026-08-31 に実際にそうなった。検査を検査すると、こういう自己参照が出る）。
    after = source[source.index("def main("):]
    marker = chr(10) + "def "
    end = after.index(marker, 1) if marker in after[1:] else len(after)
    body = after[:end]
    for name in WIRED_CHECKS:
        if f"{name}(" not in body:
            failures.append(f"{name} が main() から呼ばれていない")

    for failure in failures:
        print(f"error\tSELFTEST\t{relative(__file__)}\t{failure}")

    print(f"lint_design: すべて期待どおり（検体 {len(SELFTEST_CASES)} 件）" if not failures
          else f"lint_design: {len(failures)} 件が期待と違う")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(selftest() if "--selftest" in sys.argv else main())
