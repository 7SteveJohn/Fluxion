#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
pe_entry_probe.py - 探测游戏进程真正会静态加载的入口 DLL

用途：
  代理类 MOD（dlssg_for_sm86 / ReShade / UE4SS）必须挑一个"游戏启动时一定会被
  加载"的系统 DLL 名做替身。version.dll 不在 Windows KnownDLLs 名单里，UE 系游戏
  基本不 import 它 —— 于是代理文件躺在目录里从没被执行。

  本脚本解析 PE 的 Import Table（静态导入），列出候选入口名谁真的被 import。
  对 exe 及其主宿主 DLL（如 UnityPlayer.dll / *-Win64-ShippingBase.dll）都跑一遍。

用法：
  python pe_entry_probe.py <exe或dll> [<更多文件>...]
"""
import struct
import sys
import os

ENTRIES = ["version.dll", "winmm.dll", "dinput8.dll", "winhttp.dll", "dxgi.dll",
           "dwmapi.dll", "xinput1_3.dll", "xinput9_1_0.dll", "d3d11.dll", "d3d12.dll",
           "dbghelp.dll", "wininet.dll", "msimg32.dll", "opengl32.dll", "wtsapi32.dll"]


def _rva2off(secs, rva):
    for _name, va, vsz, raw, rsz in secs:
        if va <= rva < va + max(vsz, rsz):
            return raw + (rva - va)
    return None


def read_imports(path):
    """返回 (machine, 导入的DLL名列表)。解析失败抛异常。"""
    with open(path, "rb") as f:
        try:
            import mmap
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        except Exception:
            mm = f.read()

        if mm[0:2] != b"MZ":
            raise ValueError("not a PE (no MZ)")
        e_lfanew = struct.unpack_from("<I", mm, 0x3C)[0]
        if mm[e_lfanew:e_lfanew + 4] != b"PE\0\0":
            raise ValueError("not a PE (no PE sig)")
        coff = e_lfanew + 4
        machine = struct.unpack_from("<H", mm, coff)[0]
        nsec = struct.unpack_from("<H", mm, coff + 2)[0]
        optsz = struct.unpack_from("<H", mm, coff + 16)[0]
        opt = coff + 20
        magic = struct.unpack_from("<H", mm, opt)[0]
        pe32p = (magic == 0x20B)
        ddir = opt + (112 if pe32p else 96)
        imp_rva = struct.unpack_from("<I", mm, ddir + 8)[0]   # DataDirectory[1] = Import

        secs = []
        so = opt + optsz
        for i in range(nsec):
            o = so + i * 40
            name = bytes(mm[o:o + 8]).rstrip(b"\0").decode("latin1")
            vsz, va, rsz, raw = struct.unpack_from("<IIII", mm, o + 8)
            secs.append((name, va, vsz, raw, rsz))

        def _read_name(rva):
            no = _rva2off(secs, rva)
            if no is None:
                return None
            nm = b""
            while mm[no] != 0 and len(nm) < 128:
                nm += mm[no:no + 1]
                no += 1
            return nm.decode("latin1")

        names = []
        if imp_rva:
            off = _rva2off(secs, imp_rva)
            if off is None:
                raise ValueError("import dir rva unmapped (file truncated?)")
            while True:
                ent = bytes(mm[off:off + 20])
                if len(ent) < 20 or ent == b"\0" * 20:
                    break
                name_rva = struct.unpack_from("<I", ent, 12)[0]
                if name_rva == 0:
                    break
                nm = _read_name(name_rva)
                if nm:
                    names.append(nm)
                off += 20

        # Delay-Load Import (DataDirectory[13])：很多 UE / 加壳游戏把真依赖放这里，
        # 只看静态导入表会漏判。
        delay = []
        dly_rva = struct.unpack_from("<I", mm, ddir + 13 * 8)[0]
        if dly_rva:
            off = _rva2off(secs, dly_rva)
            if off is not None:
                while True:
                    ent = bytes(mm[off:off + 32])
                    if len(ent) < 32 or ent == b"\0" * 32:
                        break
                    name_rva = struct.unpack_from("<I", ent, 4)[0]
                    if name_rva == 0:
                        break
                    nm = _read_name(name_rva)
                    if nm:
                        delay.append(nm)
                    off += 32

        try:
            mm.close()
        except Exception:
            pass
        return machine, names, delay


MACHINE = {0x8664: "x64", 0x14C: "x86", 0xAA64: "arm64"}


def report(path):
    print("=" * 74)
    if not os.path.isfile(path):
        print("MISSING:", path)
        return None
    try:
        machine, names, delay = read_imports(path)
    except Exception as e:
        print("FAIL   :", path, "->", e)
        return None
    low = [n.lower() for n in names]
    dlow = [n.lower() for n in delay]
    print("FILE   :", path, " (%s, %.1f MB)" % (MACHINE.get(machine, hex(machine)),
                                                os.path.getsize(path) / 1048576.0))
    print("IMPORTS: %d ->" % len(low), ", ".join(low[:24]) + (" ..." if len(low) > 24 else ""))
    if dlow:
        print("DELAYED: %d ->" % len(dlow), ", ".join(dlow))
    hits = [e for e in ENTRIES if e in low]
    dhits = [e for e in ENTRIES if e in dlow]
    print("静态导入命中  :", ", ".join(hits) if hits else "(无)")
    print("延迟导入命中  :", ", ".join(dhits) if dhits else "(无)")
    ok = sorted(set(hits) | set(dhits))
    print("=> 可劫持入口 :", ", ".join(ok) if ok else "无（本文件不会按名加载任何入口 DLL）")
    return ok


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(1)
    for a in args:
        report(a)
