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
仕様を JSON で標準入力から渡す（一時ファイルを作らないため。CLAUDE.md §3-2-1）。

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

    rows.append(grid_row([grid_column(field_layout("SubmitButton"), horizontal="Right")]))
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

    # 検索行は 1 行 3 組までで折り返す（Designer/Project.md のレイアウト規約）
    layout["Layout"]["Rows"] = [grid_row(columns, wrap=True)] if columns else []
    return layout


def main() -> None:
    spec = json.load(sys.stdin)

    labels = {f["name"]: f.get("label", f["name"]) for f in spec["fields"]}

    module = load_default("ModuleDesign")
    module["Name"] = spec["module"]
    module["DataSourceName"] = spec.get("dataSource", "BusinessAppSQLite")
    module["DbTable"] = spec["table"]
    module["PageTitle"] = spec.get("pageTitle", "")

    fields = [build_field(f) for f in spec["fields"]]
    for row_fields in spec.get("detail", []):
        for name in row_fields:
            fields.append(label_field(name + "Label", labels.get(name, name)))
    for name in spec.get("search", []):
        fields.append(label_field(name + "SearchLabel", labels.get(name, name)))
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
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(module, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print(os.path.relpath(out_path, REPO_ROOT))


if __name__ == "__main__":
    main()
