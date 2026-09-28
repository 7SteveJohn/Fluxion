import os

base = r"D:\youhua\帧生成方案\别人的管理器"
pats = [b"dlssg", b"MFG", b"0.3.", b"0.2.", b"Vulkan", b"vulkan",
        b"sl.dlss_g", b"nvngx_dlssg", b"version.dll", b"dxgi.dll",
        b"RE Engine", b"rengine", b"MaxGenerated", b"MaxInterpolated"]

for root, dirs, files in os.walk(base):
    for f in files:
        if f.lower().endswith(".dll"):
            p = os.path.join(root, f)
            data = open(p, "rb").read()
            print("文件:", f, "大小:", len(data))
            for pat in pats:
                idx = data.find(pat)
                if idx >= 0:
                    ctx = data[max(0, idx - 40):idx + 80]
                    print("  ", pat, "@", idx, ":", ctx)
            print()
