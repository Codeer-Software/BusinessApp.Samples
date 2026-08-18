#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""モジュール JSON から指定フィールドを消し、レイアウトの空になった列・行も畳む。

使い方: python strip_fields.py <module.mod.json> Field1 Field2 ...
"""
import json, sys, io


def prune_layout(node, targets):
    """Layout ノードを再帰的に走査し、targets を指す FieldLayout を除く。
    戻り値: このノードが空（中身なし）になったら True"""
    if not isinstance(node, dict):
        return False
    rows = node.get('Rows')
    if rows is None:
        return False
    newrows = []
    for row in rows:
        cols = row.get('Columns', [])
        newcols = []
        for col in cols:
            lay = col.get('Layout') or {}
            fn = lay.get('FieldName')
            if fn in targets:
                continue
            if 'Rows' in lay:
                if prune_layout(lay, targets):
                    continue
            newcols.append(col)
        if newcols:
            row['Columns'] = newcols
            newrows.append(row)
    node['Rows'] = newrows
    return len(newrows) == 0


def main():
    path = sys.argv[1]
    targets = set(sys.argv[2:])
    j = json.load(open(path, encoding='utf-8'))

    before = len(j.get('Fields', []))
    j['Fields'] = [f for f in j.get('Fields', []) if f.get('Name') not in targets]
    removed = before - len(j['Fields'])

    for key in ('DetailLayouts', 'ListLayouts', 'SearchLayouts'):
        for name, lay in (j.get(key) or {}).items():
            if isinstance(lay, dict) and 'Layout' in lay:
                prune_layout(lay['Layout'], targets)
            # DataOnlyFields からも落とす
            if isinstance(lay, dict) and isinstance(lay.get('DataOnlyFields'), list):
                lay['DataOnlyFields'] = [x for x in lay['DataOnlyFields'] if x not in targets]

    if isinstance(j.get('LinkFieldNames'), list):
        j['LinkFieldNames'] = [x for x in j['LinkFieldNames'] if x not in targets]

    with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(j, f, ensure_ascii=False, indent=2)
        f.write('\n')
    print('%s: フィールド %d 件削除' % (path.split('\\')[-1], removed))


main()
