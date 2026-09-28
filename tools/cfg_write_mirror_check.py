# -*- coding: utf-8 -*-
"""config.json 节定位 / 补键逻辑的镜像自测（与 Core.cs 的 FindSectionBlock/InsertKeyBlock/AppendSectionBlock 同构）。
用法: python tools/_cfg_mirror_test.py [config.json 路径]
改 Core.cs 的写回逻辑后必须跑一遍：编译通过 != 写回正确。"""
import json, re, sys

BS = chr(92)   # 反斜杠，避免被 shell/编辑器转义


def depth_at(text, idx):
    d = 0; ins = False; i = 0
    while i < idx and i < len(text):
        c = text[i]
        if ins:
            if c == BS:
                i += 2; continue
            if c == '"':
                ins = False
        elif c == '"':
            ins = True
        elif c == '{':
            d += 1
        elif c == '}':
            d -= 1
        i += 1
    return d


def match_brace(text, b):
    d = 0; ins = False; i = b
    while i < len(text):
        c = text[i]
        if ins:
            if c == BS:
                i += 2; continue
            if c == '"':
                ins = False
        elif c == '"':
            ins = True
        elif c == '{':
            d += 1
        elif c == '}':
            d -= 1
            if d == 0:
                return i
        i += 1
    return -1


def find_section(text, sec):
    needle = '"%s"' % sec
    frm = 0
    while True:
        si = text.find(needle, frm)
        if si < 0:
            return None
        frm = si + len(needle)
        if depth_at(text, si) != 1:
            continue
        b = text.find('{', si)
        if b < 0:
            return None
        if any(ch not in ': \t\r\n' for ch in text[si + len(needle):b]):
            continue
        e = match_brace(text, b)
        if e < 0:
            return None
        return (b, e)


def write_key(text, sec, key, pat, rawval):
    f = find_section(text, sec)
    if f is None:
        b = text.find('{'); e = match_brace(text, b)
        inner = text[b + 1:e].rstrip()
        sep = '' if (inner == '' or inner.endswith(',')) else ','
        nb = (text[:b + 1] + inner + sep
              + '\r\n\r\n  "%s": {\r\n    "%s": %s\r\n  }\r\n' % (sec, key, rawval)
              + text[e:])
        json.loads(nb)
        return nb, 'appended-section'
    b, e = f
    block = text[b:e]
    nb = re.sub(pat, lambda m: m.group(1) + rawval, block)
    if nb == block:
        inner = block[1:].rstrip()
        sep = '' if (inner == '' or inner.endswith(',')) else ','
        nb = '{' + inner + sep + '\r\n    "%s": %s\r\n  ' % (key, rawval)
        # 本函数返回的块不含结尾 '}'（调用方接回去），校验时自己补上
        json.loads(nb + '}')
        mode = 'inserted-key'
    else:
        mode = 'replaced'
    out = text[:b] + nb + text[e:]
    json.loads(out)
    return out, mode


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else r'D:\youhua\Fluxion\config.json'
    raw = open(path, encoding='utf-8-sig').read()
    ok = True

    b, e = find_section(raw, 'services')
    fb, fe = find_section(raw, 'gameAware')
    print('[1] services 节定位 =', repr(raw[b:e][:70]), '...')
    inside = (fb < b < fe)
    print('    是否误落在 gameAware 内部:', inside, '(必须 False)')
    ok &= not inside
    ok &= '"disable"' in raw[b:e]

    t2, mode = write_key(raw, 'services', 'enable', r'("enable"\s*:\s*)(true|false)', 'false')
    d2 = json.loads(t2)
    print('[2] 改 services.enable ->', mode, '| services.enable =', d2['services']['enable'],
          '| scheduler.enable =', d2['scheduler']['enable'], '(必须仍为 True)')
    ok &= (d2['services']['enable'] is False and d2['scheduler']['enable'] is True)

    # 老安装的 config.json 没有 ui 节（安装包对该文件是 onlyifdoesntexist）→ 必须能整节追加
    t3src = re.sub(r',\s*"ui":\s*\{[^}]*\}', '', raw)
    json.loads(t3src)
    t3, mode = write_key(t3src, 'ui', 'closeAsk', r'("closeAsk"\s*:\s*)(true|false)', 'false')
    d3 = json.loads(t3)
    print('[3] 无 ui 节时写入 ->', mode, '| ui =', d3.get('ui'))
    ok &= (mode == 'appended-section' and d3.get('ui', {}).get('closeAsk') is False)

    t4, mode = write_key(raw, 'services', 'zzNew', r'("zzNew"\s*:\s*)(true|false)', 'true')
    d4 = json.loads(t4)
    print('[4] 节在键缺 ->', mode, '| services =', d4['services'])
    ok &= (mode == 'inserted-key' and d4['services']['zzNew'] is True)

    t5 = re.sub(r',\s*"ui":\s*\{[^}]*\}', '', raw)
    json.loads(t5)
    t5b, m5 = write_key(t5, 'ui', 'closeAsk', r'("closeAsk"\s*:\s*)(true|false)', 'false')
    t5c, m5b = write_key(t5b, 'ui', 'closeToTray', r'("closeToTray"\s*:\s*)(true|false)', 'true')
    print('[5] 旧配置(无 ui) 逐键补写:', m5, '/', m5b, '->', json.loads(t5c)['ui'])
    # 第一次整节追加，第二次是往新节里补第二个键
    ok &= (m5 == 'appended-section' and m5b == 'inserted-key'
           and json.loads(t5c)['ui'] == {'closeAsk': False, 'closeToTray': True})

    # 键存在时的替换不能误伤同名键：power.enable 与 power.gameAwareSwitch
    t6, mode = write_key(raw, 'power', 'enable', r'("enable"\s*:\s*)(true|false)', 'false')
    d6 = json.loads(t6)
    print('[6] power.enable ->', mode, '| gameAwareSwitch =', d6['power']['gameAwareSwitch'], '(必须仍为 True)')
    ok &= (d6['power']['enable'] is False and d6['power']['gameAwareSwitch'] is True)

    print('RESULT:', 'PASS' if ok else 'FAIL')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
