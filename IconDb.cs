using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VcAdvisor
{
    /// <summary>
    /// 卡牌/角色图标库：读取内嵌的 icons.dat（来自官方 Wiki 托管的游戏素材，16×16 像素图）。
    /// 图标以预乘 alpha 的 BGRA 存储，用 GDI AlphaBlend 绘制；找不到时由调用方回退到矢量图标。
    /// </summary>
    internal static class IconDb
    {
        private sealed class Entry
        {
            public int W, H;
            public byte[] Pixels;
            public IntPtr Bmp = IntPtr.Zero;
            public IntPtr Dc = IntPtr.Zero;
        }

        private static readonly Dictionary<string, Entry> _map =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        private static bool _loaded;

        public static int Count { get { EnsureLoaded(); return _map.Count; } }

        // 界面专用图标键（由 build-icons.ps1 打包进 icons.dat）
        public const string CardBackIcon = "__VC_CARD_BACK__"; // 卡牌行占位/标题图标：紫卡+金币

        public static bool DrawCardBack(IntPtr dc, int x, int y, int size)
        {
            return Draw(dc, CardBackIcon, null, x, y, size);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    string res = null;
                    foreach (var n in asm.GetManifestResourceNames())
                    {
                        if (n.EndsWith("icons.dat", StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                    }
                    if (res == null) return;
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var br = new BinaryReader(s, Encoding.UTF8))
                    {
                        var magic = br.ReadBytes(4);
                        if (magic.Length < 4 ||
                            magic[0] != (byte)'V' || magic[1] != (byte)'C' ||
                            magic[2] != (byte)'I' || magic[3] != (byte)'1') return;

                        int count = br.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            int aliasCount = br.ReadUInt16();
                            var aliases = new List<string>(aliasCount);
                            for (int a = 0; a < aliasCount; a++)
                            {
                                int len = br.ReadUInt16();
                                aliases.Add(Encoding.UTF8.GetString(br.ReadBytes(len)));
                            }
                            int w = br.ReadByte();
                            int h = br.ReadByte();
                            var px = br.ReadBytes(w * h * 4);
                            var e = new Entry { W = w, H = h, Pixels = px };
                            foreach (var alias in aliases)
                            {
                                if (!string.IsNullOrEmpty(alias) && !_map.ContainsKey(alias))
                                    _map[alias] = e;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>按 卡牌名（中文）→ 资源键（如 MAGIC_WAND）→ 去掉阶级号 的顺序匹配图标。</summary>
        private static Entry Resolve(string key, string name)
        {
            Entry e;
            if (!string.IsNullOrEmpty(name) && _map.TryGetValue(name.Trim(), out e)) return e;
            string n = Normalize(key);
            if (n.Length == 0) return null;
            if (_map.TryGetValue(n, out e)) return e;
            string n2 = n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (n2.Length > 0 && _map.TryGetValue(n2, out e)) return e;
            return null;
        }

        /// <summary>把 Card_B_2_EmptyTome 归一化成 EMPTY_TOME（与 CardDb 的规则一致）。</summary>
        private static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            if (s.StartsWith("FCC_", StringComparison.Ordinal))
            {
                s = s.Substring(4);
            }
            else if (s.StartsWith("Card_", StringComparison.Ordinal))
            {
                s = s.Substring(5);
                int us = s.IndexOf('_');
                if (us > 0 && us + 1 < s.Length && s[us + 1] >= '0' && s[us + 1] <= '9')
                {
                    int next = s.IndexOf('_', us + 1);
                    if (next > 0) s = s.Substring(next + 1);
                }
            }
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool upper = c >= 'A' && c <= 'Z';
                bool prevLower = i > 0 && s[i - 1] >= 'a' && s[i - 1] <= 'z';
                if (upper && prevLower) sb.Append('_');
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 在指定 DC 上绘制图标；成功返回 true，没有对应图标返回 false。
        /// size 为目标边长（正方形），源图按比例拉伸。
        /// </summary>
        public static bool Draw(IntPtr dc, string key, string name, int x, int y, int size)
        {
            EnsureLoaded();
            var e = Resolve(key, name);
            if (e == null || size <= 0) return false;
            try
            {
                lock (_lock)
                {
                    if (e.Bmp == IntPtr.Zero) CreateBitmapResources(e);
                }
                if (e.Bmp == IntPtr.Zero) return false;
                var bf = new BLENDFUNCTION
                {
                    BlendOp = 0,               // AC_SRC_OVER
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = 1            // AC_SRC_ALPHA
                };
                return AlphaBlend(dc, x, y, size, size, e.Dc, 0, 0, e.W, e.H, bf);
            }
            catch { return false; }
        }

        private static void CreateBitmapResources(Entry e)
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = e.W;
            bmi.bmiHeader.biHeight = -e.H;      // 负数 = 自上而下
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;    // BI_RGB

            IntPtr bits;
            IntPtr bmp = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero) return;
            Marshal.Copy(e.Pixels, 0, bits, e.Pixels.Length);

            IntPtr mem = CreateCompatibleDC(IntPtr.Zero);
            if (mem == IntPtr.Zero) return;
            SelectObject(mem, bmp);
            e.Bmp = bmp;
            e.Dc = mem;
        }

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RGBQUAD { public byte b, g, r, a; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public RGBQUAD bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("msimg32.dll")]
        private static extern bool AlphaBlend(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, BLENDFUNCTION blend);

        #endregion
    }
}
