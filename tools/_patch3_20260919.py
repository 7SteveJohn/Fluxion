# -*- coding: utf-8 -*-
"""补丁 3：nvdrsdb 只读 primary（两份是主/备镜像，同读会让条数翻倍）+ 文案与阈值微调 + 探针复算策略对齐。"""
import codecs
import sys

ROOT = r"D:\youhua\GameBoost-DLSSG"


def load(p):
    raw = open(p, "rb").read()
    return raw.decode("utf-8-sig"), raw.startswith(codecs.BOM_UTF8)


def save(p, txt, bom):
    out = txt.encode("utf-8")
    if bom:
        out = codecs.BOM_UTF8 + out
    open(p, "wb").write(out)


def rep(txt, old, new, tag):
    n = txt.count(old)
    if n != 1:
        print("  [FAIL] %-34s 锚点 %d 次" % (tag, n))
        return None
    print("  [ ok ] %-34s" % tag)
    return txt.replace(old, new)


CORE = ROOT + r"\src\Core.cs"
core, bom = load(CORE)

# 1) 主/备镜像：只读 primary
core = rep(core,
    """        public static bool Available()
        {
            try { return Dir != null && Directory.Exists(Dir) && Directory.GetFiles(Dir, "nvdrsdb*.bin").Length > 0; }
            catch { return false; }
        }
""",
    """        public static bool Available() { return PrimaryFile() != null; }

        // 只取 primary 一份。⚠️ nvdrsdb0.bin 与 nvdrsdb1.bin 是**主/备镜像**（实测两份内容完全一致），
        //   把两份都扫一遍会让所有条数**翻倍**（DLSS 覆盖 9→18、rBAR 73→116），
        //   而且两份在写入过程中可能短暂不一致 —— 只读 primary，仅在它缺失时才退回备份。
        public static string PrimaryFile()
        {
            try
            {
                if (Dir == null || !Directory.Exists(Dir)) return null;
                string p0 = Path.Combine(Dir, "nvdrsdb0.bin");
                if (File.Exists(p0)) return p0;
                var fs = Directory.GetFiles(Dir, "nvdrsdb*.bin");
                return fs.Length > 0 ? fs[0] : null;
            }
            catch { return null; }
        }
""",
    "Core/PrimaryFile")
if core is None:
    sys.exit(1)

core = rep(core,
    """                if (!Available()) return 0;
                foreach (var f in Directory.GetFiles(Dir, "nvdrsdb*.bin"))
                {
                    byte[] d = File.ReadAllBytes(f);
                    for (int i = 0; i + 16 <= d.Length; i++)
                    {
                        if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                        if (BitConverter.ToUInt32(d, i + 4) == id) n++;
                        i += 15;
                    }
                }""",
    """                string f = PrimaryFile();
                if (f == null) return 0;
                byte[] d = File.ReadAllBytes(f);
                for (int i = 0; i + 16 <= d.Length; i++)
                {
                    if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                    if (BitConverter.ToUInt32(d, i + 4) == id) n++;
                    i += 15;
                }""",
    "Core/CountById 只读 primary")
if core is None:
    sys.exit(1)

core = rep(core,
    """                foreach (var f in Directory.GetFiles(Dir, "nvdrsdb*.bin"))
                {
                    byte[] d = File.ReadAllBytes(f);
                    for (int i = 0; i + 16 <= d.Length; i++)
                    {
                        if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                        uint id = BitConverter.ToUInt32(d, i + 4);""",
    """                {
                    byte[] d = File.ReadAllBytes(PrimaryFile());
                    for (int i = 0; i + 16 <= d.Length; i++)
                    {
                        if (d[i] != 0xA4 || d[i + 1] != 0x00 || d[i + 2] != 0x10 || d[i + 3] != 0x00) continue;
                        uint id = BitConverter.ToUInt32(d, i + 4);""",
    "Core/Snapshot 只读 primary")
if core is None:
    sys.exit(1)

# 2) 文案：把「上限」说清楚是"档记录"汇总（大小这项没有 per-game 覆盖，重点是"有没有 0"）
core = rep(core,
    """                s.ShaderCache = "开关=" + swTxt + "，上限=" + (szSet.Count == 0 ? "继承驱动默认(16 GB)" : string.Join(" / ", szSet.ToArray()));""",
    """                s.ShaderCache = "开关=" + swTxt + "，上限记录=" + (szSet.Count == 0 ? "无（继承驱动默认 16 GB）"
                              : (string.Join(" / ", szSet.ToArray()) + (sizes.Count > 1 ? "（" + sizes.Count + " 档）" : "")));""",
    "Core/着色器缓存文案")
if core is None:
    sys.exit(1)

core = rep(core,
    """                    Expected = "开关=开，上限 ≥16 GB",""",
    """                    Expected = "开关=开，且无「0=禁用」记录",""",
    "Core/着色器缓存期望值")
if core is None:
    sys.exit(1)

# 3) DPC 阈值放宽到 10%（单次取样本身有噪声，6% 就报警会变成狼来了）+ 标注取样性质
core = rep(core,
    """                    Expected = "DPC 峰值 <5%（持续偏高则查驱动/网卡中断）",
                    Status = maxDpc < 5 ? "✅ 正常" : "🟡 偏高\"""",
    """                    Expected = "单次取样，DPC 峰值 <10% 视为正常（持续偏高则查驱动/网卡中断）",
                    Status = maxDpc < 10 ? "✅ 正常" : "🟡 偏高\"""",
    "Core/DPC 阈值")
if core is None:
    sys.exit(1)

save(CORE, core, bom)

# ---------------- 探针：独立复算改为只扫 nvdrsdb0.bin ----------------
PROBE = ROOT + r"\tools\virt_probe.cs"
pb, pbom = load(PROBE)

pb = rep(pb,
    """                        string dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                                     + @"\\NVIDIA Corporation\\Drs";
                        foreach (string f in System.IO.Directory.GetFiles(dir, "nvdrsdb*.bin"))
                        {
                            byte[] dd = System.IO.File.ReadAllBytes(f);""",
    """                        // 只扫 primary（nvdrsdb*.bin 通配会把主/备镜像算两遍 → 条数翻倍）
                        string dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                                     + @"\\NVIDIA Corporation\\Drs";
                        string pf = System.IO.Path.Combine(dir, "nvdrsdb0.bin");
                        if (!System.IO.File.Exists(pf))
                        {
                            string[] cand = System.IO.Directory.GetFiles(dir, "nvdrsdb*.bin");
                            pf = cand.Length > 0 ? cand[0] : null;
                        }
                        if (pf != null)
                        {
                            byte[] dd = System.IO.File.ReadAllBytes(pf);""",
    "Probe/复算只扫 primary")
if pb is None:
    sys.exit(1)

pb = rep(pb,
    """                    Chk("DLSS 覆盖记录数：独立复算 == NvDrsDb（解析器自洽）\",""",
    """                    Chk("DLSS 覆盖记录数：独立复算(仅 primary) == NvDrsDb（解析器自洽）",""",
    "Probe/断言文案")
if pb is None:
    sys.exit(1)

save(PROBE, pb, pbom)
print("全部完成")
