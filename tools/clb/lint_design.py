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
    "Creator": "TextFieldDesign",
    "Updater": "TextFieldDesign",
}

LEGACY_ALIGNMENTS = {"Left", "Right"}


def tables_with_optimistic_locking():
    """DDL で optimistic_locking 列を持つテーブル。

    認証部品の app_users のように、こちらが定義していないテーブルまで規約の対象にしない。
    """
    tables = set()
    for path in sorted(glob.glob(os.path.join(DDL_DIR, "*.sql"))):
        text = io.open(path, encoding="utf-8").read()
        for match in re.finditer(r"CREATE TABLE (\w+) \((.*?)\);", text, re.DOTALL):
            if "optimistic_locking" in match.group(2):
                tables.add(match.group(1))
    return tables


OPTIMISTIC_LOCKING_TABLES = tables_with_optimistic_locking()


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

        # 本プロジェクトは論理削除を使わない（ADR-0006・Designer/ddl/README）
        if name == "LogicalDelete":
            findings.append((SEV_ERROR, "PRJ-01", relative(path),
                             "論理削除は使わない。仕訳は消せず、マスタは is_active で無効化する"))

        # F-01 OnValidateInput は false を返すと無言で保存を止める
        if field.get("OnValidateInput"):
            findings.append((SEV_ERROR, "F-01", relative(path),
                             f"{name}.OnValidateInput は使わない。関門はサーバ側に置く"))

    # 更新できるモジュールに楽観ロックが無いと、ロスト・アップデートが黙って起きる。
    # 「フィールドがあるとき型を見る」だけでは、最も危ない側（欠落）を見逃す。
    if doc.get("CanUpdate", True) and doc.get("DbTable") in OPTIMISTIC_LOCKING_TABLES             and not any(f.get("Name") == "OptimisticLocking" for f in doc.get("Fields", [])):
        findings.append((SEV_ERROR, "F-09", relative(path),
                         f"{module}: 更新できるモジュールには OptimisticLocking フィールドが要る"))

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
            field_name = (column.get("Layout") or {}).get("FieldName", "")
            is_label_column = (field_name.endswith("Label")
                               and field_name[:-len("Label")] in field_names)
            if is_label_column and column.get("VerticalAlignment") != "Middle":
                findings.append((SEV_WARN, "D-10", relative(path),
                                 f"{where}: ラベル列 {field_name} に VerticalAlignment: Middle が要る"))

            nested = column.get("Layout") or {}
            if "Rows" in nested:
                check_layout(path, where, nested, kind, field_names, findings)


def check_page_frame(path, doc, findings):
    for side in ("Left", "Right"):
        for link in (doc.get(side) or {}).get("Links", []):
            module = link.get("Module", "")

            # D-05 "List" だと /Module/{id} のルートが登録されず詳細が真っ白になる
            if link.get("ModulePageType") not in ("Auto", "", None):
                findings.append((SEV_ERROR, "D-05", relative(path),
                                 f"{module}: ModulePageType は Auto にする（今は {link['ModulePageType']}）"))

            # D-07 リンクを複製したときの直し忘れ
            condition_module = (((link.get("ListPageDesign") or {})
                                 .get("ListFieldDesign") or {})
                                .get("SearchCondition") or {}).get("ModuleName", "")
            if condition_module and condition_module != module:
                findings.append((SEV_ERROR, "D-07", relative(path),
                                 f"{module}: SearchCondition.ModuleName が {condition_module} を指している"))


def check_script(path, text, findings):
    name = relative(path)

    # B-01 try / catch / finally はロードできない
    for match in re.finditer(r"^\s*(try|catch|finally)\b", text, re.MULTILINE):
        findings.append((SEV_ERROR, "B-01", name,
                         f"{match.group(1)} は CLB スクリプトで使えない"))

    # A-06 数値は decimal に統一されるので整数専用書式は実行時に落ちる
    for match in re.finditer(r'ToString\("[DdXx]\d*"\)', text):
        findings.append((SEV_ERROR, "A-06", name,
                         f'{match.group(0)} は実行時に落ちる。文字列補間 $"{{n:000}}" を使う'))

    # C-01 .Value を書かないとソートが黙って無効になる
    for match in re.finditer(r"\.(OrderBy|OrderByDescending|ThenBy|ThenByDescending)\(([^)]*)\)", text):
        if ".Value" not in match.group(2):
            findings.append((SEV_ERROR, "C-01", name,
                             f"{match.group(1)} のラムダは .Value まで書く（今は {match.group(2).strip()}）"))


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

    for path in modules:
        doc = load_json(path, findings)
        if doc is not None:
            check_module(path, doc, findings)
    for path in frames:
        doc = load_json(path, findings)
        if doc is not None:
            check_page_frame(path, doc, findings)
    for path in scripts:
        check_script(path, io.open(path, encoding="utf-8").read(), findings)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]
    for severity, rule, path, message in sorted(findings):
        print(f"{severity}\t{rule}\t{path}\t{message}")

    print("")
    print(f"検査ファイル数: {len(design_files('*.mod.json')) + len(design_files('*.frm.json')) + len(design_files('*.mod.cs'))}"
          f" / error: {len(errors)} / warn: {len(warns)}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
