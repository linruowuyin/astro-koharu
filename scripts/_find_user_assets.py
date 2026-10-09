import subprocess, sys

sys.stdout.reconfigure(encoding='utf-8')

OLD = 'backup-main-v253-20261010'   # 你的 v2.5.3 版本
BASE = 'v2.5.3'                    # 上游 2.5.3 基线
NEW = 'HEAD'                       # 当前 v7.7.4


def diff_names(a, b, path):
    r = subprocess.run(
        ['git', '-c', 'core.quotePath=false', 'diff', '--name-only', a, b, '--', path],
        capture_output=True)
    return [x for x in r.stdout.decode('utf-8').split('\n') if x.strip()]


PUBLIC = 'public'

overwritten = set(diff_names(OLD, NEW, PUBLIC))          # 被上游新版覆盖的
user_touched = set(diff_names(BASE, OLD, PUBLIC))        # 你相对 2.5.3 改动过的

restore = sorted(overwritten & user_touched)
keep_upstream = sorted(overwritten - user_touched)

print(f'被上游覆盖的 public 文件      : {len(overwritten)}')
print(f'其中你真正改动过的(需恢复)   : {len(restore)}')
print(f'其中你没碰过的(留上游新版)   : {len(keep_upstream)}')

print('\n=== 需要恢复(你的资产) ===')
for f in restore:
    print('  ' + f)

# 第三方库目录直接全部留上游
third_party = [f for f in keep_upstream if '/fonts/' in f or '/katex/' in f]
other = [f for f in keep_upstream if f not in third_party]
print(f'\n=== 留上游新版(字体/katex 等第三方, {len(third_party)} 个) ===')
if other:
    print('=== 其他留上游新版的文件(你从未改动) ===')
    for f in other:
        print('  ' + f)

with open('scripts/_restore_list.txt', 'w', encoding='utf-8') as fh:
    fh.write('\n'.join(restore))
print(f'\n[恢复清单已写入 scripts/_restore_list.txt: {len(restore)} 个]')