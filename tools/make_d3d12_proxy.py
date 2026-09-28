#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
make_d3d12_proxy.py — 生成"转发型" d3d12.dll 代理（纯 PE 构造，零编译器）

背景
----
dlssg_for_sm86 只提供 5 个入口名（version/winmm/winhttp/dinput8/dxgi），而
绝区零 + 鸣潮的反作弊按"入口 DLL 文件名"拦截第三方代理：上游 issue #102
明确写 "Zenless zone zero doesn't work with any alternatives except d3d12.dll"，
#111 补充 "绝区零的反作弊会识别到 version.dll 等的名词，从而使其失效"。

d3d12.dll 是唯一无法被反作弊拉黑的名字 —— 因为游戏在 DX12 模式下**必须**
加载它（绝区零 Player.log 实证：`d3d12: loaded!`）。但上游没有这个入口。

本脚本构造一个不含机器码的 d3d12.dll：
  1. 导出系统 d3d12.dll 的全部 18 个函数，逐一"导出转发"到 d3d12_orig.dll
     （系统 d3d12.dll 的副本，保持微软签名）
  2. 通过导入表静态依赖 dinput8.dll —— Windows 加载本代理时会自动加载同目录的
     dinput8.dll，也就是 dlssg_for_sm86 的 dinput8 入口代理，从而装上它的
     LoadLibrary 钩子，去截获游戏对 nvngx_dlssg.dll 的请求

  载体为什么不用 dxgi：UnityPlayer.dll 的导入表里没有 dinput8，用它当载体不会
  劫持游戏已有功能；dxgi 是 DX12 必经路径，实测以其为载体时 D3D12CreateDevice
  会返回 0x887E0003（DXGI_ERROR_NOT_CURRENTLY_AVAILABLE）而失败。

部署四件套（放游戏 EXE 同目录）：
  d3d12.dll        <- 本脚本产出（约 3.5 KB）
  d3d12_orig.dll   <- 系统 C:\\Windows\\System32\\d3d12.dll 的副本
  dinput8.dll      <- dlssg_for_sm86 的 altnative/dinput8.dll（被上面静态导入）
  dlssg_sm86.ini   <- 插件配置

用法
----
  python make_d3d12_proxy.py --selftest        # 只跑转发能力自检
  python make_d3d12_proxy.py --out d3d12.dll   # 生成正式代理
"""

import argparse
import ctypes
import os
import struct
import sys
import tempfile

SA = 0x1000          # SectionAlignment
FA = 0x200           # FileAlignment
IMAGE_BASE = 0x180000000
DLLCHAR = 0x0140     # DYNAMIC_BASE | NX_COMPAT

# 系统 d3d12.dll 的完整导出表（本机 Win11 实测枚举所得，18 项）
D3D12_EXPORTS = [
    "D3D12CoreCreateLayeredDevice",
    "D3D12CoreGetLayeredDeviceSize",
    "D3D12CoreRegisterLayers",
    "D3D12CreateDevice",
    "D3D12CreateRootSignatureDeserializer",
    "D3D12CreateVersionedRootSignatureDeserializer",
    "D3D12DeviceRemovedExtendedData",
    "D3D12EnableExperimentalFeatures",
    "D3D12GetDebugInterface",
    "D3D12GetInterface",
    "D3D12PIXEventsReplaceBlock",
    "D3D12PIXGetThreadInfo",
    "D3D12PIXNotifyWakeFromFenceSignal",
    "D3D12PIXReportCounter",
    "D3D12SerializeRootSignature",
    "D3D12SerializeVersionedRootSignature",
    "GetBehaviorValue",
    "SetAppCompatStringPointer",
]


def al(v, a):
    return (v + a - 1) & ~(a - 1)


def parse_d3d12_exports(path):
    """从真实 d3d12.dll 动态读取导出表，确保逐版本对齐。"""
    d = open(path, "rb").read()
    e = struct.unpack_from("<I", d, 0x3C)[0]
    if d[e:e + 4] != b"PE\x00\x00":
        raise ValueError("not a PE: %s" % path)
    nsec = struct.unpack_from("<H", d, e + 6)[0]
    optsz = struct.unpack_from("<H", d, e + 20)[0]
    opt = e + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    ddoff = opt + (112 if magic == 0x20B else 96)
    exp_rva = struct.unpack_from("<I", d, ddoff)[0]
    secs = []
    so = opt + optsz
    for i in range(nsec):
        s = so + 40 * i
        vsz, va = struct.unpack_from("<II", d, s + 8)
        rsz, ra = struct.unpack_from("<II", d, s + 16)
        secs.append((va, vsz, ra, rsz))

    def r2o(rva):
        for va, vsz, ra, rsz in secs:
            if va <= rva < va + max(vsz, rsz):
                return ra + (rva - va)
        return None

    o = r2o(exp_rva)
    nnames = struct.unpack_from("<I", d, o + 24)[0]
    anames = struct.unpack_from("<I", d, o + 32)[0]
    no = r2o(anames)
    out = []
    for i in range(nnames):
        nr = struct.unpack_from("<I", d, no + 4 * i)[0]
        p = r2o(nr)
        end = d.find(b"\x00", p)
        out.append(d[p:end].decode("latin1"))
    return sorted(out)


def build(out_path, self_name, forwards, imports):
    """构造一个只含导出转发 / 导入依赖的最小 PE32+ DLL。

    forwards : [(导出函数名, "目标DLL.目标函数")]   导出转发表
    imports  : [(DLL名, 函数名)]                    静态导入（会触发目标 DLL 加载）
    """
    rd = bytearray()   # .rdata  只读：导出目录 / 字符串 / 导入描述符 / ILT
    dt = bytearray()   # .data   可写：IAT
    TARGET = "d3d12_orig.dll"

    def rd_add(b):
        o = len(rd); rd.extend(b); return 0x1000 + o

    def rd_res(n):
        o = len(rd); rd.extend(b"\x00" * n); return 0x1000 + o

    def rd_patch(rva, b):
        o = rva - 0x1000; rd[o:o + len(b)] = b

    def dt_res(n):
        o = len(dt); dt.extend(b"\x00" * n); return 0x2000 + o

    def dt_patch(rva, b):
        o = rva - 0x2000; dt[o:o + len(b)] = b

    # ---------- 导出目录 ----------
    n = len(forwards)
    edir = rd_res(40)                 # IMAGE_EXPORT_DIRECTORY
    aof = rd_res(4 * n)               # AddressOfFunctions（转发字符串 RVA）
    aon = rd_res(4 * n)               # AddressOfNames
    aoo = rd_res(2 * n)               # AddressOfNameOrdinals
    dllname_rva = rd_add(self_name.encode() + b"\x00")

    ordered = sorted(forwards, key=lambda x: x[0])   # 导出名必须按字母序
    name_rva = {}
    for fn, _ in ordered:
        name_rva[fn] = rd_add(fn.encode() + b"\x00")
    # 转发字符串必须落在「导出目录 RVA 区间」内，loader 才判定为 forwarder
    for i, (fn, tgt) in enumerate(ordered):
        rd_patch(aof + 4 * i,
                 struct.pack("<I", rd_add(tgt.encode() + b"\x00")))
    for i, (fn, _) in enumerate(ordered):
        rd_patch(aon + 4 * i, struct.pack("<I", name_rva[fn]))
        rd_patch(aoo + 2 * i, struct.pack("<H", i))

    edir_size = len(rd)               # 覆盖到最后一个转发字符串末尾

    struct.pack_into("<IIHHIIIIIII", rd, edir - 0x1000,
                     0,            # Characteristics
                     0,            # TimeDateStamp
                     0, 0,         # Major/MinorVersion
                     dllname_rva,  # Name
                     1,            # Base
                     n,            # NumberOfFunctions
                     n,            # NumberOfNames
                     aof, aon, aoo)

    # ---------- 导入表（触发 dxgi.dll 加载） ----------
    imp_rva = imp_size = iat_rva = iat_size = 0
    if imports:
        descs = rd_res(20 * (len(imports) + 1))
        first_iat = None
        for i, (dll, fn) in enumerate(imports):
            if len(rd) % 2:
                rd.extend(b"\x00")
            hn = rd_add(struct.pack("<H", 0) + fn.encode() + b"\x00")
            ilt = rd_add(struct.pack("<Q", hn) + struct.pack("<Q", 0))
            iat = dt_res(16)
            dt_patch(iat, struct.pack("<Q", hn) + struct.pack("<Q", 0))
            if first_iat is None:
                first_iat = iat
            dn = rd_add(dll.encode() + b"\x00")
            rd_patch(descs + 20 * i,
                     struct.pack("<IIIII", ilt, 0, 0, dn, iat))
        rd_patch(descs + 20 * len(imports), b"\x00" * 20)
        imp_rva, imp_size = descs, len(rd) - (descs - 0x1000)
        iat_rva = first_iat
        iat_size = 16 * len(imports)

    # ---------- .reloc（合法但无操作，保证可 ASLR） ----------
    reloc = struct.pack("<IIHH", 0x1000, 12, 0x0000, 0x0000)

    # ---------- 组装节 ----------
    rd_raw = al(len(rd), FA)
    dt_raw = al(max(len(dt), 1), FA)
    rl_raw = al(len(reloc), FA)
    rd_vs, dt_vs, rl_vs = len(rd), max(len(dt), 1), len(reloc)

    hdr_size = 0x80 + 4 + 20 + 240 + 40 * 3
    hdr_al = al(hdr_size, FA)
    rd_rva, dt_rva, rl_rva = 0x1000, 0x2000, 0x3000
    rd_ptr = hdr_al
    dt_ptr = rd_ptr + rd_raw
    rl_ptr = dt_ptr + dt_raw
    size_of_image = al(rl_rva + rl_vs, SA)

    # ---------- DOS + PE 头 ----------
    out = bytearray(hdr_al)
    dos = bytearray(0x40)
    dos[0:2] = b"MZ"
    struct.pack_into("<H", dos, 0x02, 0x90)
    struct.pack_into("<H", dos, 0x04, 0x03)
    struct.pack_into("<H", dos, 0x08, 0x04)
    struct.pack_into("<H", dos, 0x0C, 0xFFFF)
    struct.pack_into("<H", dos, 0x10, 0xB8)
    struct.pack_into("<H", dos, 0x18, 0x40)
    struct.pack_into("<I", dos, 0x3C, 0x80)
    out[0:0x40] = dos
    stub = b"This program cannot be run in DOS mode.\r\n$"
    out[0x40:0x40 + len(stub)] = stub
    out[0x80:0x84] = b"PE\x00\x00"

    struct.pack_into("<HHIIIHH", out, 0x84,
                     0x8664,                       # Machine = AMD64
                     3,                            # NumberOfSections
                     0, 0, 0,
                     240,                          # SizeOfOptionalHeader
                     0x2022)                       # DLL | EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

    o = 0x98
    struct.pack_into("<H", out, o + 0, 0x20B)
    out[o + 2] = 14
    struct.pack_into("<I", out, o + 4, 0)                         # SizeOfCode
    struct.pack_into("<I", out, o + 8, rd_raw + dt_raw + rl_raw)  # SizeOfInitializedData
    struct.pack_into("<I", out, o + 12, 0)
    struct.pack_into("<I", out, o + 16, 0)                        # AddressOfEntryPoint
    struct.pack_into("<I", out, o + 20, 0)                        # BaseOfCode
    struct.pack_into("<Q", out, o + 24, IMAGE_BASE)
    struct.pack_into("<I", out, o + 32, SA)
    struct.pack_into("<I", out, o + 36, FA)
    struct.pack_into("<HHHH", out, o + 40, 6, 0, 0, 0)
    struct.pack_into("<HH", out, o + 48, 6, 0)
    struct.pack_into("<I", out, o + 52, 0)
    struct.pack_into("<I", out, o + 56, size_of_image)
    struct.pack_into("<I", out, o + 60, hdr_al)
    struct.pack_into("<I", out, o + 64, 0)
    struct.pack_into("<H", out, o + 68, 2)          # Subsystem = GUI
    struct.pack_into("<H", out, o + 70, DLLCHAR)
    struct.pack_into("<QQ", out, o + 72, 0x100000, 0x1000)
    struct.pack_into("<QQ", out, o + 88, 0x100000, 0x1000)
    struct.pack_into("<I", out, o + 104, 0)
    struct.pack_into("<I", out, o + 108, 16)
    dd = o + 112
    struct.pack_into("<II", out, dd + 8 * 0, edir, edir_size)      # EXPORT
    struct.pack_into("<II", out, dd + 8 * 1, imp_rva, imp_size)    # IMPORT
    struct.pack_into("<II", out, dd + 8 * 12, iat_rva, iat_size)   # IAT
    struct.pack_into("<II", out, dd + 8 * 5, rl_rva, rl_vs)        # BASERELOC

    sh = 0x98 + 240
    for i, (nm, vs, va, rs, pr, ch) in enumerate([
        (b".rdata", rd_vs, rd_rva, rd_raw, rd_ptr, 0x40000040),
        (b".data",  dt_vs, dt_rva, dt_raw, dt_ptr, 0xC0000040),
        (b".reloc", rl_vs, rl_rva, rl_raw, rl_ptr, 0x42000040),
    ]):
        s = sh + 40 * i
        out[s:s + 8] = nm.ljust(8, b"\x00")
        struct.pack_into("<IIII", out, s + 8, vs, va, rs, pr)
        struct.pack_into("<II", out, s + 24, 0, 0)
        struct.pack_into("<HH", out, s + 32, 0, 0)
        struct.pack_into("<I", out, s + 36, ch)

    body = bytearray()
    body += rd + b"\x00" * (rd_raw - len(rd))
    body += (dt if dt else b"\x00") + b"\x00" * (dt_raw - len(dt) if dt else dt_raw - 1)
    body += reloc + b"\x00" * (rl_raw - len(reloc))
    out[rd_ptr:rd_ptr + len(body)] = body

    with open(out_path, "wb") as f:
        f.write(out)
    return len(out)


def selftest():
    """造一个转发到 kernel32.GetCurrentProcessId 的纯测试 DLL，并真的加载调用。"""
    tmp = os.path.join(tempfile.gettempdir(), "d3d12proxy_selftest")
    os.makedirs(tmp, exist_ok=True)
    p = os.path.join(tmp, "fwdtest.dll")
    size = build(p, "fwdtest.dll",
                 [("FwdTestFunc", "kernel32.GetCurrentProcessId")],
                 [("kernel32.dll", "GetCurrentProcessId")])
    print("[1] 生成 %s (%d 字节)" % (p, size))

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    expect = k32.GetCurrentProcessId()
    print("[2] 期望 PID = %d" % expect)

    h = ctypes.WinDLL(p, use_last_error=True)
    print("[3] LoadLibrary 成功，模块句柄 = %#x" % (h._handle & 0xFFFFFFFF))

    fn = getattr(h, "FwdTestFunc")
    fn.restype = ctypes.c_uint
    got = fn()
    print("[4] 经导出转发调用得到 = %d" % got)
    if got == expect:
        print("[OK] 导出转发链路完全可用 —— 同一套构造可以拿来做 d3d12.dll")
        return True
    print("[FAIL] 返回值不匹配")
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--out", default="")
    ap.add_argument("--orig", default=r"C:\Windows\System32\d3d12.dll")
    ap.add_argument("--carrier", "--dxgi-dep", dest="carrier", default="dinput8.dll",
                    help="静态依赖的载体名（同目录的 dlssg 入口代理）")
    ap.add_argument("--carrier-func", dest="carrier_func", default="DirectInput8Create",
                    help="载体必须导出的函数名（仅用于触发加载）")
    a = ap.parse_args()

    if a.selftest:
        sys.exit(0 if selftest() else 1)

    if not a.out:
        ap.error("需要 --out 或 --selftest")

    exports = parse_d3d12_exports(a.orig)
    print("从 %s 读到 %d 个导出" % (a.orig, len(exports)))
    forwards = [(e, "d3d12_orig.dll." + e) for e in exports]
    imports = [(a.carrier, a.carrier_func)]
    size = build(a.out, "d3d12.dll", forwards, imports)
    print("已生成 %s (%d 字节)，转发 %d 个导出，静态依赖 %s!%s"
          % (a.out, size, len(forwards), a.carrier, a.carrier_func))


if __name__ == "__main__":
    main()
