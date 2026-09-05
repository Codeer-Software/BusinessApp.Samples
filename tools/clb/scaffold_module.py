#!/usr/bin/env python
"""CLB のモジュール定義（*.mod.json）を生成する。

なぜスクリプトで生成するか
--------------------------
モジュール JSON は入れ子が深く、単純なマスタでも 1000 行を超える。手書きすると
「designcheck は通るが挙動が違う」種類の間違いが入りやすい。`_defaults/` にある
デザイナ本体が書き出す既定 JSON を土台にし、必要なプロパティだけ上書きする。

**生成後は Design/Modules/*.mod.json が正典である。** デザイナ GUI からも編集されるので、
このスクリプトを再実行して上書きしない。あくまで新規モジュールの足場作りに使う。

使い方
------
仕様を JSON で標準入力から渡す（一時ファイルを作らないため。docs/15_実装の原則.md §5）。

    python tools/clb/scaffold_module.py <<'JSON'
    {
      "module": "Account",
      "folder": "Masters",
      "table": "accounts",
      "pageTitle": "勘定科目",
      "fields": [
        {"name": "Id",   "column": "id",   "type": "Id"},
        {"name": "Code", "column": "code", "type": "Text", "label": "科目コード", "required": true}
      ],
      "detail": [["Code"]],
      "list": ["Code"],
      "search": ["Code"]
    }
    JSON

仕様のキー
----------
module     モジュール名（PascalCase 英語）
folder     Modules/ 配下のサブフォルダ（省略可）
table      DB テーブル名
pageTitle  画面のタイトル（日本語）
fields     フィールド定義の配列
             name      フィールド名（PascalCase 英語。CLB の予約名はその綴りのまま）
             column    DB 列名（snake_case）
             type      Id | OptimisticLocking | Text | Number | Boolean | Select | Link | Date | DateTime
             label     表示名（日本語）
             required  必須か
             readonly  更新時に変更させないか
             enum      Select のとき参照する enum 名
             module    Link のとき参照するモジュール名
             valueVariable / displayText  Link が保持する値と表示（既定 Id.Value / Name.Value）
             format    Number の書式（金額なら "#,0"）
             trueText / falseText  Boolean の一覧表示（既定は ○ / —）
detail     詳細画面の行。各行はフィールド名の配列（ラベル列＋入力列で組む）
list       一覧に出すフィールド名の配列
search     検索条件に出すフィールド名の配列
labelWidth ラベル列の幅（px。省略時 140）
canCreate / canUpdate / canDelete  省略時 true。false にすると参照のみの画面になる

クエリモジュール（帳簿・集計）
------------------------------
`query` を書くと、実テーブルではなく **SELECT の結果**を見せる読み取り専用モジュールになる
（`_specs/QueryAndSql.md`）。`table` は要らず、CRUD は自動的に false、詳細画面も登録ボタンも作らない。

    "query": {"sortType": "None", "pagingType": "System"}

- SQL は `Design/Modules/{folder}/{module}.Query.sql` に**別ファイルで**置く（JSON に書かない）
- **全フィールドに `column` が要る。** 出力列は SELECT の別名、入力パラメータは SQL の `@名前`
- 入力パラメータにするフィールドには `"searchParameter": true` を付ける
  （`IsSimpleSearchParameter` が立ち、SQL の `@{column}` に束縛される）
- 範囲で絞りたいなら**下限と上限で 2 つのフィールド**を作る。
  クエリモジュールの検索は 1 フィールド＝1 パラメータで、`SearchMin` / `SearchMax` は使えない
- `dbType` で DB 型を明示できる（省略時は `type` から決める）
- Select の候補はその画面限りなら `candidates`（`"表示,値"` の配列）で足りる
"""
from __future__ import annotations

import json
import os
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULTS_DIR = os.path.join(REPO_ROOT, "Designer", "ClaudeCodeForDesigner", "_defaults")
MODULES_DIR = os.path.join(REPO_ROOT, "Designer", "Design", "Modules")

# CLB の予約名は専用のデザイン型でなければ自動動作が効かない（CommonMistakes #42-A）。
# 名前だけ合わせて型が違うと designcheck は緑のまま、実機の更新が黙って失敗する。
TYPE_TO_DESIGN = {
    "Id": "IdFieldDesign",
    "OptimisticLocking": "OptimisticLockingFieldDesign",
    "Text": "TextFieldDesign",
    "Number": "NumberFieldDesign",
    "Boolean": "BooleanFieldDesign",
    "Select": "SelectFieldDesign",
    "Link": "LinkFieldDesign",
    "Date": "DateFieldDesign",
    "DateTime": "DateTimeFieldDesign",
}

# クエリモジュールの列は DB 型を宣言する必要がある（_specs/QueryAndSql.md）。
# 日付を TEXT で宣言しないこと（qa/01 A-02 と同じ理由で、比較が壊れる）。
TYPE_TO_DB_TYPE = {
    "Id": "integer",
    "Text": "text",
    "Number": "integer",
    "Boolean": "integer",
    "Select": "text",
    "Link": "integer",
    "Date": "DATE",
    "DateTime": "DATETIME",
}


def load_default(type_name: str) -> dict:
    path = os.path.join(DEFAULTS_DIR, type_name + ".json")
    if not os.path.exists(path):
        raise SystemExit(f"既定 JSON が見つからない: {path}")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def build_field(spec: dict) -> dict:
    design_type = TYPE_TO_DESIGN.get(spec["type"])
    if design_type is None:
        raise SystemExit(f"未知のフィールド型: {spec['type']}")

    field = load_default(design_type)
    field["Name"] = spec["name"]
    if "column" in spec:
        field["DbColumn"] = spec["column"]
    if spec.get("label"):
        field["DisplayName"] = spec["label"]
    if spec.get("required"):
        field["IsRequired"] = True
    if spec.get("readonly"):
        field["IsUpdateProtected"] = True

    if spec["type"] == "OptimisticLocking":
        # 既定は false（PostgreSQL の xmin 前提）。SQLite ではアプリ側で版を進める必要がある。
        field["IncrementVersion"] = True
    if spec["type"] == "Select" and spec.get("enum"):
        field["EnumName"] = spec["enum"]
    if spec["type"] == "Select" and spec.get("module"):
        # SelectField でマスタを引く書き方（_field_catalog.md）。**クエリモジュールの
        # 検索条件では LinkField ではなくこちらを使う。** LinkField は親テーブルとの
        # 結合を前提にしており、実テーブルを持たないモジュールでは噛み合わない。
        field["SearchCondition"]["ModuleName"] = spec["module"]
        field["SearchCondition"]["LimitCount"] = spec.get("limitCount", 200)
        field["ValueVariable"] = spec.get("valueVariable", "Id.Value")
        field["DisplayTextVariable"] = spec.get("displayText", "Name.Value")
    if spec["type"] == "Link":
        # DB 上の外部キーは相手の id（INTEGER）なので、保持する値は Id、
        # 画面に見せるのは名称にする（DatabaseGuidelines の主キー規約）。
        field["SearchCondition"]["ModuleName"] = spec["module"]
        field["ValueVariable"] = spec.get("valueVariable", "Id.Value")
        field["DisplayTextVariable"] = spec.get("displayText", "Name.Value")
    if spec["type"] == "Number" and spec.get("format"):
        field["Format"] = spec["format"]
    if spec["type"] == "Boolean":
        # 一覧では ○ / — で見せる（Designer/Project.md のレイアウト規約）
        field["Text"] = ""
        field["TrueText"] = spec.get("trueText", "○")
        field["FalseText"] = spec.get("falseText", "—")
    if spec.get("candidates"):
        # その画面限りの固定候補。複数モジュールで使う値ならデザイン enum にする
        # （_field_catalog.md「Candidates と enum の使い分け」）。
        field["Candidates"] = spec["candidates"]
    if spec.get("searchParameter"):
        # クエリモジュールでは、この印が付いたフィールドが SQL の @{column} に束縛される。
        field["IsSimpleSearchParameter"] = True

    return field


def query_field(spec: dict) -> dict:
    """
    SELECT の結果をモジュールに見せる QueryField を作る。

    **出力列も入力パラメータも、ここで全部宣言する。** 宣言し忘れた列は
    SQL が返していても画面に出てこない（`_specs/QueryAndSql.md`）。
    """
    field = load_default("QueryFieldDesign")
    field["Name"] = "Query"

    query = spec["query"]
    field["QuerySetting"]["QuerySortType"] = query.get("sortType", "None")
    field["QuerySetting"]["QueryPagingType"] = query.get("pagingType", "System")
    field["QuerySetting"]["Parameters"] = [
        {
            "IsParameter": bool(f.get("searchParameter")),
            "Name": f["column"],
            "DbType": f.get("dbType", TYPE_TO_DB_TYPE[f["type"]]),
            "DbParameterDirection": "Input",
        }
        for f in spec["fields"]
    ]
    return field


def label_field(name: str, text: str) -> dict:
    field = load_default("LabelFieldDesign")
    field["Name"] = name
    field["Text"] = text
    return field


def submit_button(text: str) -> dict:
    field = load_default("SubmitButtonFieldDesign")
    field["Name"] = "SubmitButton"
    field["Text"] = text
    field["IsBlock"] = False
    return field


def grid_column(layout: dict | None, width: int | None = None, vertical: str | None = None,
                horizontal: str | None = None) -> dict:
    # 揃えの有効値は Start / Center / End / Stretch。旧値 Left / Right は静かに Start に化ける
    # （qa/01 A-01）。lint_design.py が検査する。
    column = {
        "IgnoreContentWidth": False,
        "Padding": {},
        "BackgroundColor": "",
        "BorderStyle": {"LeftColor": "", "TopColor": "", "RightColor": "", "BottomColor": ""},
        "CanResize": False,
        "AllowOverflow": False,
        "Border": "None",
    }
    if layout is not None:
        column["Layout"] = layout
    if width is not None:
        column["Width"] = width
    if vertical is not None:
        column["VerticalAlignment"] = vertical
    if horizontal is not None:
        column["HorizontalAlignment"] = horizontal
    return column


def field_layout(field_name: str) -> dict:
    return {
        "FieldName": field_name,
        "ContextMenu": "",
        "ClassName": "",
        "FontFamily": "",
        "Color": "",
        "Name": "",
        "BackgroundColor": "",
        "TypeFullName": "Codeer.LowCode.Blazor.Repository.Design.FieldLayoutDesign",
    }


def grid_row(columns: list[dict], wrap: bool = False) -> dict:
    return {
        "IsWrap": wrap,
        "IsAutoFillWrap": False,
        "Margin": {},
        "GridRowType": "Normal",
        "CanResize": False,
        "BackgroundColor": "",
        "KeepInFillAvailableGrid": False,
        "IsProportionalScale": False,
        "IsRowMarginRemoved": False,
        "Columns": columns,
    }


def build_detail_layout(spec: dict, labels: dict[str, str]) -> dict:
    layout = load_default("ModuleDesign")["DetailLayouts"][""]
    label_width = spec.get("labelWidth", 140)

    rows = []
    for row_fields in spec.get("detail", []):
        columns = []
        for field_name in row_fields:
            # ラベル列は Middle 揃え（Designer/Project.md のレイアウト規約）
            columns.append(grid_column(field_layout(field_name + "Label"),
                                       width=label_width, vertical="Middle"))
            columns.append(grid_column(field_layout(field_name)))
        rows.append(grid_row(columns))

    writable = not ("query" in spec)
    if spec.get("canUpdate", writable) or spec.get("canCreate", writable):
        rows.append(grid_row([grid_column(field_layout("SubmitButton"), horizontal="End")]))
    layout["Layout"]["Rows"] = rows
    return layout


def build_list_layout(spec: dict, labels: dict[str, str]) -> dict:
    layout = load_default("ModuleDesign")["ListLayouts"][""]
    layout["HeaderTitle"] = spec.get("pageTitle", "")
    layout["Elements"] = [[
        {
            "FieldName": name,
            "ContextMenu": "",
            "Label": labels.get(name, name),
            "IsMaxWidthFixed": False,
            "ColumnSpan": 1,
            "RowSpan": 1,
            "TextWrap": "Unset",
            "CanResize": True,
            "CanUserSort": True,
            "ClassName": "",
            "FontFamily": "",
            "Color": "",
            "BackgroundColor": "",
            "DetailLayoutName": "",
            "ListElementComponent": "",
        }
        for name in spec.get("list", [])
    ]]
    return layout


def build_search_layout(spec: dict) -> dict:
    layout = load_default("ModuleDesign")["SearchLayouts"][""]
    label_width = spec.get("labelWidth", 140)

    columns = []
    for field_name in spec.get("search", []):
        columns.append(grid_column(field_layout(field_name + "SearchLabel"),
                                   width=label_width, vertical="Middle"))
        columns.append(grid_column(field_layout(field_name)))

    # 検索行は 1 行 3 組（ラベル＋入力）までで折り返す（Designer/Project.md のレイアウト規約）。
    # 文書に書くだけでなく生成器にも実装する。lint_design.py が同じ規約を検査する。
    rows = [grid_row(columns[i:i + 6], wrap=True) for i in range(0, len(columns), 6)]
    layout["Layout"]["Rows"] = rows
    return layout


def validate_spec(spec: dict) -> None:
    """生成器自身が「designcheck は通るが挙動が違う」ものを作らないようにする。"""
    is_query = "query" in spec
    required = ("module", "fields") if is_query else ("module", "table", "fields")
    for key in required:
        if key not in spec:
            raise SystemExit(f"仕様に {key} がない")

    if is_query and spec.get("detail"):
        raise SystemExit("クエリモジュールに detail は作れない（読み取り専用で登録ボタンも無い）")

    names = []
    for field in spec["fields"]:
        for key in ("name", "type"):
            if key not in field:
                raise SystemExit(f"フィールドに {key} がない: {field}")
        # 列名の無いフィールドは QueryField の宣言に載らず、画面に値が出ない。
        if is_query and not field.get("column"):
            raise SystemExit(f"クエリモジュールのフィールドには column が要る: {field['name']}")
        if is_query and field["type"] not in TYPE_TO_DB_TYPE and not field.get("dbType"):
            raise SystemExit(f"{field['name']} の DB 型が決まらない。dbType を書く")
        names.append(field["name"])

    columns = [f["column"] for f in spec["fields"] if f.get("column")]
    if is_query:
        # 同じ列名を 2 つのフィールドで宣言すると、SQL のパラメータが二重になって
        # 実行時に落ちる。designcheck は列名の重複を知らない。
        duplicated_columns = {c for c in columns if columns.count(c) > 1}
        if duplicated_columns:
            raise SystemExit(f"列名が重複している: {', '.join(sorted(duplicated_columns))}")

    duplicated = {n for n in names if names.count(n) > 1}
    if duplicated:
        raise SystemExit(f"フィールド名が重複している: {', '.join(sorted(duplicated))}")

    known = set(names)
    for section in ("list", "search"):
        for name in spec.get(section, []):
            if name not in known:
                raise SystemExit(f"{section} の {name} は fields にない")

    placed = [name for row in spec.get("detail", []) for name in row]
    for name in placed:
        if name not in known:
            raise SystemExit(f"detail の {name} は fields にない")
    twice = {n for n in placed if placed.count(n) > 1}
    if twice:
        raise SystemExit(f"detail に同じフィールドが 2 回ある: {', '.join(sorted(twice))}")


def main() -> None:
    # **標準入力は UTF-8 として読む。** 既定のままだと Windows のコンソール既定（cp932）で
    # 復号され、日本語のラベルが壊れたまま JSON になる。壊れ方が「サロゲートが混じる」なので
    # 読み込みでは落ちず、**書き出しの直前まで気づけない**。
    spec = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    validate_spec(spec)

    labels = {f["name"]: f.get("label", f["name"]) for f in spec["fields"]}

    is_query = "query" in spec

    module = load_default("ModuleDesign")
    module["Name"] = spec["module"]
    module["DataSourceName"] = spec.get("dataSource", "BusinessAppSQLite")
    # クエリモジュールは実テーブルを持たない。DbTable を空にすることが
    # 「書き込み経路が無い」ことの表明でもある（_specs/QueryAndSql.md）。
    module["DbTable"] = "" if is_query else spec["table"]
    module["PageTitle"] = spec.get("pageTitle", "")
    # 既定はすべて true。false にすると入力フィールドが ViewOnly になる（CommonMistakes #40）ので、
    # 「参照のみの画面」を作るのに使える。
    writable = not is_query
    module["CanCreate"] = spec.get("canCreate", writable)
    module["CanUpdate"] = spec.get("canUpdate", writable)
    module["CanDelete"] = spec.get("canDelete", writable)

    fields = [query_field(spec)] if is_query else []
    fields += [build_field(f) for f in spec["fields"]]
    for row_fields in spec.get("detail", []):
        for name in row_fields:
            fields.append(label_field(name + "Label", labels.get(name, name)))
    for name in spec.get("search", []):
        fields.append(label_field(name + "SearchLabel", labels.get(name, name)))
    if module["CanUpdate"] or module["CanCreate"]:
        fields.append(submit_button(spec.get("submitText", "登録")))
    module["Fields"] = fields

    module["DetailLayouts"][""] = build_detail_layout(spec, labels)
    module["ListLayouts"][""] = build_list_layout(spec, labels)
    module["SearchLayouts"][""] = build_search_layout(spec)
    module["LinkFieldNames"] = [f["name"] for f in spec["fields"] if f["type"] == "Link"]

    folder = spec.get("folder", "")
    out_dir = os.path.join(MODULES_DIR, folder) if folder else MODULES_DIR
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, spec["module"] + ".mod.json")

    # 生成後は Design/Modules/*.mod.json が正典であり、デザイナ GUI からも編集される。
    # 黙って潰すと、再現に最も時間がかかる種類の作業（GUI での手作業）が失われる。
    if os.path.exists(out_path):
        raise SystemExit(
            f"既にある: {os.path.relpath(out_path, REPO_ROOT)} / "
            "生成後の .mod.json が正典なので上書きしない。作り直すなら先に消すこと。")

    # **先に文字列にしてから書く。** 直接ストリームへ書くと、途中で失敗したときに
    # 壊れたファイルが残り、消さないと作り直せない（削除は確認を挟む運用なので、
    # そこで作業が止まる）。生成器の失敗が後片付けを要求しない形にしておく。
    body = json.dumps(module, ensure_ascii=False, indent=2) + "\n"
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(body)

    print(os.path.relpath(out_path, REPO_ROOT))


if __name__ == "__main__":
    main()
