#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PageFrame から指定モジュールの登録（サイドバーLinks / OtherPageModuleDesigns）を落とす。

使い方: python strip_frame.py <frame.frm.json> Module1 Module2 ...
"""
import json, sys, io

path = sys.argv[1]
targets = set(sys.argv[2:])
j = json.load(open(path, encoding='utf-8'))
removed = 0

for side in ('Left', 'Right', 'Header'):
    node = j.get(side)
    if isinstance(node, dict) and isinstance(node.get('Links'), list):
        before = len(node['Links'])
        node['Links'] = [x for x in node['Links'] if x.get('Module') not in targets]
        removed += before - len(node['Links'])

if isinstance(j.get('OtherPageModuleDesigns'), list):
    before = len(j['OtherPageModuleDesigns'])
    j['OtherPageModuleDesigns'] = [
        x for x in j['OtherPageModuleDesigns'] if x.get('Module') not in targets]
    removed += before - len(j['OtherPageModuleDesigns'])

if isinstance(j.get('OtherPages'), list):
    before = len(j['OtherPages'])
    j['OtherPages'] = [x for x in j['OtherPages'] if x.get('Module') not in targets]
    removed += before - len(j['OtherPages'])

if j.get('TopPageModule') in targets:
    print('  警告: 着地モジュール %s を消した。TopPageModule を差し替える必要がある' % j['TopPageModule'])

with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
    json.dump(j, f, ensure_ascii=False, indent=2)
    f.write('\n')
print('%s: 登録 %d 件削除' % (path.split('\\')[-1], removed))
