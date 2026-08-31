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

# ボタンの色は 3 値だけ（docs/09 §4・ADR-0030）。CLB は Outline* や Text も持つが使わない。
ALLOWED_VARIANTS = {"Primary", "Danger", "Secondary"}

# 必須の印を出すクラス（app.css）。ラベル側の要素に付ける。
REQUIRED_LABEL_CLASS = "required-label"

# 参照してはならない向き（ADR-0025 §4）。`Modules/` のトップレベルのフォルダ＝アプリ（部品）で
# 判定する（Designer/Project.md のフォルダ規約）——モジュール名を並べると、増えるたびに腐る。
# **認証部品（Platform）は誰が参照してもよい**——権限の条件は AppUser の列でしか書けない（qa/01 F-21）。
FORBIDDEN_REFERENCES = {"Partners": {"Accounting"}}


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

        # 本プロジェクトは論理削除を使わない（docs/08 マスタ台帳・Designer/ddl/README）
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

    # D-18 ボタンの色は 3 値だけ（docs/09 §4・ADR-0030）
    for field in doc.get("Fields", []):
        variant = field.get("Variant")
        if variant and variant not in ALLOWED_VARIANTS:
            findings.append((SEV_ERROR, "D-18", relative(path),
                             f"{module}.{field.get('Name', '')}: Variant「{variant}」は使わない"
                             f"（{' / '.join(sorted(ALLOWED_VARIANTS))} のどれかにする）"))

    # D-19 検索欄を持つレイアウトは既定で開く（docs/09 §3）。
    # **空の検索レイアウトは対象にしない**——開いても空箱が出るだけである。
    search = (doc.get("SearchLayouts") or {}).get("") or {}
    search_layout = search.get("Layout") or {}
    if _field_count(search_layout) > 0 and not search_layout.get("IsExpanderDefaultOpened"):
        findings.append((SEV_ERROR, "D-19", relative(path),
                         f"{module}: 検索条件は既定で開く（IsExpanderDefaultOpened: true）"))

    # D-24 データを持つモジュールに書き込み条件が書かれているか（qa/01 F-18）。
    # **空＝全開放である。** 前回プロジェクトは 104 本になってから全数監査をして穴を 28 本見つけた
    # （ADR-0026 の教訓）。新しいモジュールは条件が空で生まれるので、増えた日に鳴らす。
    if doc.get("DbTable") and not (doc.get("UserWriteCondition") or {}).get("ModuleName"):
        findings.append((SEV_ERROR, "D-24", relative(path),
                         f"{module}: データを持つモジュールに UserWriteCondition が要る"
                         "（空＝全開放。qa/01 F-18）"))

    # D-20 必須の欄には印が要る（docs/09 §1）。
    # **見るのは詳細レイアウトのラベルだけ**——一覧の見出し（<th>）には class が付かないので、
    # そちらは文字列に「*」を入れてある（qa/01 D-16）。
    _check_required_marks(path, doc, findings)

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


def _check_required_marks(path, doc, findings):
    """必須のフィールドのラベルに、印を出すクラスが付いているか（docs/09 §1）。

    **`IsRequired` は「利用者が埋める必須欄」の 1 意味に揃えてある**（Designer/Project.md）。
    画面が自動で入れる欄には立てないので、ここは例外なしの規則でよい。
    """
    module = doc.get("Name", "")
    required = {f.get("Name", "") for f in doc.get("Fields", []) if f.get("IsRequired")}
    if not required:
        return

    marked = set()
    unmarked = {}
    placed = set()

    def walk(node):
        if isinstance(node, dict):
            name = node.get("FieldName", "")
            if node.get("TypeFullName", "").endswith("FieldLayoutDesign"):
                if name in required:
                    placed.add(name)
                if name.endswith("Label"):
                    owner = name[:-len("Label")]
                    if owner in required:
                        # **クラスは分割して見る。** `"required-label ms-2"` のように
                        # 他のクラスと併記できる（app.css はクラスセレクタなので印は出る）。
                        # 完全一致で見ると、正しい書き方を誤検知する。
                        if REQUIRED_LABEL_CLASS in (node.get("ClassName") or "").split():
                            marked.add(owner)
                        else:
                            unmarked[owner] = name
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(doc.get("DetailLayouts", {}))

    for owner, label in sorted(unmarked.items()):
        if owner in marked:
            continue    # 同じ欄が複数のレイアウトにあり、片方には付いている
        findings.append((SEV_ERROR, "D-20", relative(path),
                         f"{module}: 必須の {owner} のラベル {label} に "
                         f'"ClassName": "{REQUIRED_LABEL_CLASS}" が要る（docs/09 §1）'))

    # **ラベル要素そのものが無い場合を見落とさない。** これがいちばん起きやすい書き忘れで、
    # 「`<Field>Label` があるときにしか見ない」実装では素通りしていた（2026-08-31 の自己レビュー）。
    for owner in sorted(placed - marked - set(unmarked)):
        findings.append((SEV_ERROR, "D-20", relative(path),
                         f"{module}: 必須の {owner} に、印を付けるラベル要素"
                         f"（{owner}Label）が詳細レイアウトに無い（docs/09 §1）"))


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


def check_child_parent_keys(modules, findings):
    """ヘッダ＋明細の、**明細側の親 FK が `IdFieldDesign` ＋ `IsManualInput: false` か**（qa/01 D-17）。

    **`LinkFieldDesign` にすると、行を「追加」した保存が静かに消える。**
    ボタンは押せるのに保存要求が 1 本も飛ばず、エラーもトーストも出ない。
    **読み取りは動く**ので気づけない——既存の行は逆引きで正しく出て、
    リロードして初めて消えたと分かる（2026-08-30 実測 1.3.20）。
    `designcheck` は緑のままである。

    **親子の関係はデザインから読む。モジュール名を並べない**（増えるたびに腐る）。
    親の `ListField` が「子の `<X>.Value` ＝ 自分の `Id.Value`」で絞っていれば、
    その `<X>` が明細側の親 FK である——つまり**この検査の対象は、親が自分で名乗っている**。
    """
    fields_by_module = {
        doc["Name"]: {f.get("Name", ""): f for f in doc.get("Fields", [])}
        for _, doc in modules if doc.get("Name")
    }

    def parent_keys(node, child, into):
        """子モジュールを絞る条件から、親 FK の名前を拾う。"""
        if isinstance(node, dict):
            if (node.get("TypeFullName", "").endswith("FieldVariableMatchCondition")
                    and node.get("Variable") == "Id.Value"
                    and node.get("SearchTargetVariable", "").endswith(".Value")):
                into.add(node["SearchTargetVariable"][:-len(".Value")])
            for value in node.values():
                parent_keys(value, child, into)
        elif isinstance(node, list):
            for value in node:
                parent_keys(value, child, into)

    pairs = 0
    for path, doc in modules:
        for field in doc.get("Fields", []):
            if not field.get("TypeFullName", "").endswith("ListFieldDesign"):
                continue
            condition = field.get("SearchCondition") or {}
            child = condition.get("ModuleName", "")
            if child not in fields_by_module:
                continue

            names = set()
            parent_keys(condition.get("Condition"), child, names)
            pairs += len(names)
            for name in sorted(names):
                key = fields_by_module[child].get(name)
                if key is None:
                    findings.append((SEV_ERROR, "D-17", relative(path),
                                     f"{doc.get('Name', '')}.{field.get('Name', '')} が絞る "
                                     f"{child}.{name} が無い（親子の逆引きが効かない）"))
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


def check_cross_frame_links(frames, findings, modules=(), scripts=()):
    """遷移先での登録漏れ（qa/01 F-17）。

    **登録が無いと画面が静かに真っ白になる。** designcheck は検出しない。
    前回プロジェクトは繰り返し踏んで静的検査を自作した（ADR-0026 §6）。

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
    registered = {}
    for path, doc in frames:
        name = doc.get("Name", "")
        # **`modules` という名前を使い回さない。** 引数の `modules`（モジュール定義の一覧）を
        # 上書きしてしまい、後半のループが文字列を展開しようとして落ちた（2026-08-31）。
        placed = set()
        if doc.get("TopPageModule"):
            placed.add(doc["TopPageModule"])
        for side in ("Left", "Right", "Header"):
            for link in (doc.get(side) or {}).get("Links", []):
                if not link.get("PageFrame"):
                    placed.add(link.get("Module", ""))
        for other in doc.get("OtherPageModuleDesigns") or []:
            placed.add(other.get("Module", ""))
        registered[name] = placed

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
    """役割で絞る条件が、階層を **OR-of-Equal** で表しているか（ADR-0026 §1 の追記）。

    **階層方式の唯一の弱点は書き忘れである。** 見るのは 3 つ。

    1. **下位の役割で絞るなら、上位も入っている**（`staff` だけだと責任者が入れない）
    2. **`IsOrMatch` が真である**——AND で書くと **誰も通らない**（1 つの列が
       2 つの値を同時に取ることは無い）。しかも画面はリンクが消えるだけなので気づけない
    3. **`Comparison` が `Equal` で、`IsNot` が偽である**——否定に化けると意図と逆の集合を通す

    **軸ごとに順位を表で持つ。** 名前を書き並べると、役割が増えた日に片方だけ直る。
    見るキーは 4 つ——`AppAccessConditions` は `app.clprj` にしか無いので入れない。
    """
    hierarchy = {
        "AccountingRole.Value": ["viewer", "staff", "manager"],
        "PartnerRole.Value": ["viewer", "editor"],
    }

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
                                     f"上位の {missing} も OR で入れる（ADR-0026 §1 の追記②）"))

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
        # **関門は足したときが完成ではない**（CLAUDE.md §4-2）。
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
    check_module_references(loaded_modules, loaded_scripts, findings)
    check_role_conditions(loaded_modules, loaded_frames, findings)
    app_settings = os.path.join(DESIGN_DIR, "app.clprj")
    if os.path.exists(app_settings):
        check_app_access_condition(
            json.load(io.open(app_settings, encoding="utf-8")), app_settings, findings)

    errors = [f for f in findings if f[0] == SEV_ERROR]
    warns = [f for f in findings if f[0] == SEV_WARN]
    for severity, rule, path, message in sorted(findings):
        print(f"{severity}\t{rule}\t{path}\t{message}")

    print("")
    print(f"検査ファイル数: {len(design_files('*.mod.json')) + len(design_files('*.frm.json')) + len(design_files('*.mod.cs'))}"
          f" / error: {len(errors)} / warn: {len(warns)}")
    return 1 if errors else 0


SELFTEST_CASES = [
    # (何を壊すか, 壊した姿, 期待する (severity, ルール))
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
    ("検索条件が既定で閉じている",
     lambda: _module(SearchLayouts={"": {"Layout": {"IsExpanderDefaultOpened": False, "Rows": [
         {"Columns": [{"Layout": {"FieldName": "A"}}]}]}}}),
     (SEV_ERROR, "D-19")),
    ("必須の欄に印が無い",
     lambda: _required_module(class_name=""), (SEV_ERROR, "D-20")),
    ("必須の欄にラベル要素が無い",
     lambda: _required_module(with_label=False), (SEV_ERROR, "D-20")),
    ("データを持つのに書き込み条件が無い",
     lambda: _module(DbTable="x"), (SEV_ERROR, "D-24")),
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
    "check_module", "check_page_frame", "check_application_root", "check_script",
    "check_cross_frame_links", "check_child_parent_keys", "check_module_references",
    "check_role_conditions", "check_app_access_condition",
]


def _module(**overrides):
    """検査に掛ける最小のモジュール定義。"""
    doc = {"Name": "SelfTest", "DbTable": "", "CanUpdate": False, "Fields": [],
           "DetailLayouts": {}, "SearchLayouts": {}, "ListLayouts": {}}
    doc.update(overrides)
    return doc


def _required_module(class_name=REQUIRED_LABEL_CLASS, with_label=True):
    """必須の欄が 1 つある詳細レイアウト。"""
    columns = [{"Layout": {"FieldName": "Code", "ClassName": "",
                           "TypeFullName": "X.FieldLayoutDesign"}}]
    if with_label:
        # ラベル列は Middle 揃え（D-10）。**実物と同じ形で作る**——
        # 検体が実データと違うと、正しい姿のはずが別の関門を鳴らす（qa/03 L-17）。
        columns.insert(0, {"VerticalAlignment": "Middle",
                           "Layout": {"FieldName": "CodeLabel", "ClassName": class_name,
                                      "TypeFullName": "X.FieldLayoutDesign"}})
    return _module(
        Fields=[{"Name": "Code", "TypeFullName": "X.TextFieldDesign", "IsRequired": True},
                {"Name": "CodeLabel", "TypeFullName": "X.LabelFieldDesign"}],
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
    row = {"Name": "Row", "Fields": [f for f in [parent_key] if f is not None]}
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

    for label, build, expected in SELFTEST_CASES:
        findings = []
        doc = build()
        check_module(_self_path(), doc, findings)
        check_role_conditions([(_self_path(), doc)], [], findings)
        got = [(f[0], f[1]) for f in findings]
        if expected not in got:
            failures.append(f"{label}: {expected} が鳴らない（出たのは {got}）")

    # 正しい姿では鳴らない（鳴りっぱなしの関門は、赤を無視させる）
    for label, doc in [
        ("表を持たないモジュール", _module()),
        ("書き込み条件のあるモジュール",
         _module(DbTable="x", UserWriteCondition={"ModuleName": "AppUser"})),
        ("印の付いた必須の欄", _required_module()),
        ("他のクラスと併記した印", _required_module(class_name="ms-2 required-label")),
        ("階層を OR で書いた条件",
         _module(UserWriteCondition=_role_condition(["staff", "manager"]))),
    ]:
        findings = []
        check_module(_self_path(), doc, findings)
        check_role_conditions([(_self_path(), doc)], [], findings)
        if findings:
            failures.append(f"正しい{label}で鳴った: {[(f[0], f[1]) for f in findings]}")

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
