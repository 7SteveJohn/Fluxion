# -*- coding: utf-8 -*-
"""Plan.Current 漏判修复：主入口不在时也要扫描其它入口名（旧版代理 / 备选入口）。"""
import io, os, sys

P = r'D:\youhua\Fluxion\src\Dlssg.cs'
raw = io.open(P, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in raw else '\n'
t = raw.replace('\r\n', '\n')

old = '''        public static string Current(string gameDir)
        {
            string kind = XeMfg.Detect(gameDir);
            if (kind.Length == 0) return "";
            string p = Path.Combine(XeMfg.TargetDir(gameDir, kind), EntryName(kind));
            if (!File.Exists(p)) return None;
            string o = OwnerOf(p);
            return o.Length > 0 ? o : None;
        }'''
new = '''        public static string Current(string gameDir)
        {
            string kind = XeMfg.Detect(gameDir);
            if (kind.Length == 0) return "";
            string dir = XeMfg.TargetDir(gameDir, kind);
            // 先看主入口，主入口不在（被停放）就扫一遍其它入口名 ——
            //  早期 dlssg_for_sm86 用的是 version.dll / dinput8.dll，只认主入口会误报"原生"。
            string entry = EntryName(kind);
            string p = Path.Combine(dir, entry);
            if (File.Exists(p))
            {
                string o = OwnerOf(p);
                if (o.Length > 0) return o;
            }
            foreach (string nm in EntryNames)
            {
                if (nm.Equals(entry, StringComparison.OrdinalIgnoreCase)) continue;
                string f = Path.Combine(dir, nm);
                if (!File.Exists(f)) continue;
                string o = OwnerOf(f);
                if (o.Length > 0) return o;
            }
            return None;
        }'''
if t.count(old) != 1:
    print('!! 锚点命中 %d 次' % t.count(old)); sys.exit(1)
t = t.replace(old, new, 1)
io.open(P, 'w', encoding='utf-8', newline='').write(t if nl == '\n' else t.replace('\n', nl))
print('OK Plan.Current 已补：主入口不在时扫描其它入口名')
