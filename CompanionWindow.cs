using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VcAdvisor
{
    /// <summary>
    /// 独立的置顶伴侣窗口（纯 Win32 + GDI 绘制）。
    /// 游戏运行时没有 WinForms/WPF，所以这里只使用 user32 / gdi32 的 P/Invoke。
    /// 特性：默认贴靠游戏窗口右侧、可拖动、右下角可缩放、可收起、F8 显示/隐藏。
    /// 只读：窗口只显示数据，不向游戏写入任何内容。
    /// </summary>
    internal static class CompanionWindow
    {
        internal sealed class Row
        {
            public string Text = "";
            public int Kind;          // 0 标题 1 副标题 2 普通 3 高亮 4 提示 6 首推大字
            public int Indent;        // 文本左缩进（像素）
            public int Icon = -1;     // 左侧图标（角色）；-1 = 不画
            public int IconColor = -1;// 图标颜色；-1 = 跟随文本颜色
            public int Rank;          // >0 时在图标位置画序号徽章
            public bool Hero;         // 首推：大字号 + 整行高亮底
            public bool Card;         // 圆角卡片底（普通出牌条目）
            public int Tab = -1;      // -1 = 所有标签页可见；0 出牌 1 敌情 2 牌库 3 资料
        }

        /// <summary>把 RGB 组装成 GDI 的 COLORREF（0x00BBGGRR）。</summary>
        public static int Rgb(int r, int g, int b)
        {
            if (r < 0) r = 0; else if (r > 255) r = 255;
            if (g < 0) g = 0; else if (g > 255) g = 255;
            if (b < 0) b = 0; else if (b > 255) b = 255;
            return (b << 16) | (g << 8) | r;
        }

        // —— 供 Unity 线程读取的状态 ——
        private static volatile bool _advanced = true;
        private static volatile bool _alive;

        public static bool AdvancedMode { get { return _advanced; } }
        public static bool IsAlive { get { return _alive; } }

        // —— 内容（Unity 线程写入，窗口线程读取）——
        private static readonly object _lock = new object();
        private static Row[] _pendingRows = new Row[0];
        private static string _pendingTitle = "打牌助手";
        private static volatile bool _hasPending;

        // —— 窗口线程内部状态 ——
        private static Thread _thread;
        private static IntPtr _hwnd = IntPtr.Zero;
        private static bool _visible = true;
        private static bool _collapsed;
        private static bool _userMoved;
        private static bool _inLayout;
        private static bool _inMove;                 // 正在被系统移动/缩放循环接管（拖动中）
        private static bool _hiddenByMinimize;       // 因游戏窗口最小化而临时隐藏
        private static int _width = 372;
        private static int _height = 200;

        // —— GDI 资源缓存（避免每次重绘都新建/销毁画刷，拖动时明显更顺）——
        private static readonly Dictionary<int, IntPtr> _brushes = new Dictionary<int, IntPtr>();
        private static readonly Dictionary<int, IntPtr> _pens = new Dictionary<int, IntPtr>();
        private static IntPtr _memDC = IntPtr.Zero;
        private static IntPtr _memBmp = IntPtr.Zero;
        private static int _memW, _memH;

        // —— 鼠标悬停 / 滚轮 ——
        private static int _hoverRow = -1;
        private static int _scrollY;
        private static int _maxScroll;
        private static IntPtr _hArrow, _hSize;

        // —— 标签页 ——
        private static int _tab;                                  // 0 出牌 1 敌情 2 牌库 3 资料
        private static readonly string[] TAB_NAMES = { "出牌", "敌情", "牌库", "资料" };
        private static readonly RECT[] _tabRects = new RECT[4];
        private static readonly List<Row> _shown = new List<Row>();

        private static readonly List<Row> _paint = new List<Row>();
        private static readonly List<int> _rowTop = new List<int>();
        private static readonly List<int> _rowH = new List<int>();
        private static string _paintTitle = "打牌助手";

        private static RECT _btnAdv, _btnCollapse, _btnClose;
        private static string _lastLayoutSig;
        private static int _timerTicks;
        private static int _paintCount;
        private static string _lastErrStage;

        // —— 鼠标悬停（按钮高亮）——
        private static int _hotBtn;              // 0 无 / 1 进阶 / 2 收起 / 3 关闭
        private static bool _trackingMouse;
        private const uint TME_LEAVE = 0x00000002;

        private const string CLASS_NAME = "VcAdvisorCompanionWnd";

        #region Win32 常量

        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_SETCURSOR = 0x0020;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_DESTROY = 0x0002;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_TIMER = 0x0113;
        private const int WM_PRINTCLIENT = 0x0318;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_APP = 0x8000;
        private const int WM_CONTENT = WM_APP + 1;

        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTBOTTOMRIGHT = 17;
        private const int MA_NOACTIVATE = 3;

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const int DT_LEFT = 0x0000;
        private const int DT_TOP = 0x0000;
        private const int DT_CENTER = 0x0001;
        private const int DT_VCENTER = 0x0004;
        private const int DT_WORDBREAK = 0x0010;
        private const int DT_SINGLELINE = 0x0020;
        private const int DT_NOPREFIX = 0x0800;
        private const int DT_CALCRECT = 0x0400;

        private const int TRANSPARENT = 1;
        private const int SRCCOPY = 0x00CC0020;

        private const int FW_NORMAL = 400;
        private const int FW_BOLD = 700;
        private const uint DEFAULT_CHARSET = 1;
        private const uint OUT_TT_PRECIS = 4;
        private const uint CLIP_DEFAULT_PRECIS = 0;
        private const uint CLEARTYPE_QUALITY = 5;
        private const uint DEFAULT_PITCH = 0;
        private const uint FF_DONTCARE = 0;

        private const int HK_TOGGLE = 1;
        private const int HK_RESCUE = 2;
        private const int VK_F8 = 0x77;
        private const int VK_F9 = 0x78;
        private const int MOD_NOREPEAT = 0x4000;

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        // —— 配色（GDI COLORREF = 0x00BBGGRR）：暗色哥特风，暖金点缀 ——
        // 注意：不使用进度条 / 色条表达任何数值，只用底色、字体与图标做层次。
        private const int C_BORDER = 0x2E4A5C;     // RGB(92,74,46)   暗金边框
        private const int C_BG = 0x180E12;         // RGB(18,14,24)   深夜紫黑
        private const int C_TITLEBAR = 0x281826;   // RGB(38,24,40)   紫黑标题栏
        private const int C_CARD = 0x2A1A22;       // RGB(34,26,42)   普通条目底
        private const int C_CARD_HOVER = 0x3A2630; // RGB(48,38,58)   悬停条目底
        private const int C_CARD_HERO = 0x241E38;  // RGB(56,30,36)   首推卡片底（暗红棕）
        private const int C_SCROLL_TRACK = 0x22181E; // RGB(30,24,34)
        private const int C_SCROLL = 0x364A5A;     // RGB(90,74,54)   滚动条
        private const int C_GOLD = 0x5CB8E0;       // RGB(224,184,92) 金色
        private const int C_BTN = 0x2A1A22;        // RGB(34,26,42)
        private const int C_BTN_HOT = 0x3A2C4E;    // RGB(78,44,58)
        private const int C_TEXT_TITLE = 0xCCE8F4; // RGB(244,232,204) 暖白
        private const int C_TEXT_SUB = 0x96B4C4;   // RGB(196,180,150)
        private const int C_TEXT = 0xD4E0E6;       // RGB(230,224,212)
        private const int C_TEXT_HI = 0x78D6FF;    // RGB(255,214,120) 亮金
        private const int C_TEXT_HINT = 0x828C96;  // RGB(150,140,130)

        #endregion

        #region P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
            IntPtr hdcSrc, int sx, int sy, int rop);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int SetTextColor(IntPtr hdc, int crColor);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int iBkMode);

        [DllImport("gdi32.dll")]
        private static extern int SetBkColor(IntPtr hdc, int crColor);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int crColor);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, int crColor);

        [DllImport("gdi32.dll")]
        private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern bool Polygon(IntPtr hdc, POINT[] points, int count);

        [DllImport("gdi32.dll")]
        private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern int SaveDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool RestoreDC(IntPtr hdc, int savedDC);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int nHeight, int nWidth, int nEscapement, int nOrientation,
            int fnWeight, uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet,
            uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        #endregion

        // 字体
        private static IntPtr _fTitle, _fSub, _fRow, _fHi, _fHint, _fBtn, _fHero, _fNum;

        private static WndProcDelegate _wndProc;   // 必须保持引用，防止被 GC
        private static EnumWindowsProc _enumProc;

        private const int PAD = 10;
        private const int TITLE_H = 36;   // 标题栏留足高度，让标题/按钮/徽记在同一水平中心
        private const int TAB_H = 26;     // 标签页高度
        private const int BTN_W = 42;
        private const int BTN_H = 20;
        private const int CLOSE_W = 20;

        #region 对外接口

        /// <summary>由 Unity 线程调用：更新窗口内容（只在内容变化时调用）。</summary>
        public static void SetContent(List<Row> rows, string title)
        {
            try
            {
                lock (_lock)
                {
                    _pendingRows = rows != null ? rows.ToArray() : new Row[0];
                    _pendingTitle = title ?? "";
                    _hasPending = true;
                }
                if (_hwnd != IntPtr.Zero)
                    PostMessageW(_hwnd, WM_CONTENT, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>启动窗口线程（幂等）。</summary>
        public static void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(ThreadMain);
            _thread.IsBackground = true;
            _thread.Name = "VcAdvisorCompanion";
            _thread.Start();
        }

        public static void Stop()
        {
            try
            {
                if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        #endregion

        private static void ThreadMain()
        {
            try
            {
                _wndProc = WndProc;
                _enumProc = EnumProc;
                // 显式指定光标：否则窗口类没有光标，WM_SETCURSOR 默认不处理，
                // 会一直沿用游戏设置的“忙碌/等待”光标，表现为鼠标移到面板上一直转圈。
                _hArrow = LoadCursorW(IntPtr.Zero, new IntPtr(32512));   // IDC_ARROW
                _hSize = LoadCursorW(IntPtr.Zero, new IntPtr(32642));    // IDC_SIZENWSE

                WNDCLASSEXW wc = new WNDCLASSEXW();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
                wc.style = 0;
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = Process.GetCurrentProcess().Handle;
                wc.hbrBackground = IntPtr.Zero;
                wc.hCursor = _hArrow;
                wc.lpszClassName = CLASS_NAME;
                ushort atom = RegisterClassExW(ref wc);
                if (atom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1410) // 1410 = 类已存在
                    {
                        Log("伴侣窗口注册失败 错误码=" + err);
                        return;
                    }
                }

                // 样式：无边框弹出窗 + 置顶 + 不在任务栏 + 不抢焦点
                int exStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                _hwnd = CreateWindowExW(exStyle, CLASS_NAME, "打牌助手",
                    WS_POPUP, 100, 100, _width, _height,
                    IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    Log("伴侣窗口创建失败 错误码=" + Marshal.GetLastWin32Error());
                    return;
                }

                CreateFonts();

                if (!RegisterHotKey(_hwnd, HK_TOGGLE, MOD_NOREPEAT, VK_F8))
                    Log("全局热键 F8 注册失败（可能被占用），仍可用窗口按钮");
                else
                    Log("伴侣窗口已创建，F8 可显示/隐藏");

                if (!RegisterHotKey(_hwnd, HK_RESCUE, MOD_NOREPEAT, VK_F9))
                    Log("全局热键 F9 注册失败（可能被占用）");
                else
                    Log("F9 可把窗口强制拉到屏幕右下角并置顶");

                Log(string.Format("窗口样式 style=0x{0:X8} exstyle=0x{1:X8}",
                    GetWindowLongW(_hwnd, GWL_STYLE), GetWindowLongW(_hwnd, GWL_EXSTYLE)));

                _alive = true;
                _userMoved = false;
                FollowGame();
                // 启动时游戏若已是最小化状态，FollowGame 已判定为隐藏，不要在这里又显示出来
                if (!_hiddenByMinimize) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                Layout(_hwnd);   // 若内容在窗口创建前就已推送，这里补一次布局
                SetTimer(_hwnd, new IntPtr(1), 1000, IntPtr.Zero);
                Probe("启动后");

                MSG msg;
                while (GetMessageW(out msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            catch (Exception e)
            {
                Log("伴侣窗口线程异常: " + e.Message);
            }
            finally
            {
                _alive = false;
                _hwnd = IntPtr.Zero;
            }
        }

        private static void Log(string msg)
        {
            try { if (AdvisorPlugin.Logger != null) AdvisorPlugin.Logger.LogInfo("[窗口] " + msg); } catch { }
        }

        private static void CreateFonts()
        {
            _fTitle = MakeFont(-15, FW_BOLD);
            _fSub = MakeFont(-12, FW_NORMAL);
            _fRow = MakeFont(-13, FW_NORMAL);
            _fHi = MakeFont(-14, FW_BOLD);
            _fHint = MakeFont(-12, FW_NORMAL);
            _fBtn = MakeFont(-12, FW_NORMAL);
            _fHero = MakeFont(-21, FW_BOLD);   // 首推大字
            _fNum = MakeFont(-12, FW_BOLD);    // 序号徽章
            Log(string.Format("字体句柄 标题={0} 副={1} 正文={2} 高亮={3} 首推={4}",
                _fTitle, _fSub, _fRow, _fHi, _fHero));
        }

        private static IntPtr MakeFont(int h, int weight)
        {
            try
            {
                return CreateFontW(h, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET,
                    OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                    DEFAULT_PITCH | FF_DONTCARE, "Microsoft YaHei");
            }
            catch { return IntPtr.Zero; }
        }

        #region 定位

        private static bool EnumProc(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != (uint)Process.GetCurrentProcess().Id) return true;
                if (hWnd == _hwnd) return true;      // 不要把伴侣窗口自己当成游戏窗口
                if (!IsWindowVisible(hWnd)) return true;

                bool iconic = IsIconic(hWnd);
                RECT r;
                if (!GetWindowRect(hWnd, out r)) return true;
                int w = r.right - r.left, h = r.bottom - r.top;

                // 最小化时系统会把窗口矩形压成 159x27 这类小块（坐标 -25600,-25600），
                // 若仍按尺寸过滤就「找不到游戏窗口」，伴侣窗口既不跟随隐藏也会乱跳。
                // 因此最小化的窗口跳过尺寸过滤，并给足权重确保仍能选中游戏主窗口。
                long area;
                if (iconic)
                {
                    area = (long)w * h + 100000000L;
                }
                else
                {
                    if (w < 320 || h < 240) return true;
                    area = (long)w * h;
                }
                if (area > _bestArea) { _bestArea = area; _bestWnd = hWnd; }
            }
            catch { }
            return true;
        }

        private static long _bestArea;
        private static IntPtr _bestWnd;

        private static IntPtr FindGameWindow()
        {
            _bestArea = 0;
            _bestWnd = IntPtr.Zero;
            try { EnumWindows(_enumProc, IntPtr.Zero); } catch { }
            return _bestWnd;
        }

        private static RECT _lastGameRect;
        private static bool _lastGameValid;

        private static void FollowGame()
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                IntPtr g = FindGameWindow();
                if (g == IntPtr.Zero) return;

                RECT gr;
                bool haveRect = GetWindowRect(g, out gr);

                // 游戏窗口最小化时，GetWindowRect 会返回 (-25600,-25600) 这种假坐标，
                // 照跟会让伴侣窗口在屏幕上乱跳。这里改为「跟随最小化」：一起藏起来，恢复时再出来。
                // 双重判定：IsIconic 之外再兜一层假坐标，避免个别窗口样式下 IsIconic 不可靠。
                // 注意：最小化同步必须无视 _userMoved，否则用户一旦手动拖过窗口就再也不生效了。
                bool fakeRect = haveRect && (gr.left <= -20000 || gr.top <= -20000);
                if (IsIconic(g) || fakeRect)
                {
                    SyncMinimize(true);
                    _lastGameValid = false;
                    return;
                }
                SyncMinimize(false);

                // 用户手动摆过位置：只同步最小化状态，不再自动贴靠
                if (_userMoved) return;
                if (!haveRect) return;

                if (_lastGameValid &&
                    gr.left == _lastGameRect.left && gr.top == _lastGameRect.top &&
                    gr.right == _lastGameRect.right && gr.bottom == _lastGameRect.bottom)
                    return;
                _lastGameRect = gr;
                _lastGameValid = true;

                RECT work = new RECT();
                SystemParametersInfoW(0x0030 /*SPI_GETWORKAREA*/, 0, ref work, 0);
                if (work.right <= work.left) { work.left = 0; work.top = 0; work.right = 1920; work.bottom = 1080; }

                int w = _width, h = _height;
                int x = gr.right + 8;
                if (x + w > work.right) x = gr.left - w - 8;
                if (x < work.left) x = work.right - w - 8;
                if (x < work.left) x = work.left;
                int y = gr.top + 40;
                if (y + h > work.bottom) y = work.bottom - h;
                if (y < work.top) y = work.top;

                SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>让伴侣窗口跟随游戏窗口的最小化状态一起隐藏 / 恢复。</summary>
        private static void SyncMinimize(bool gameMinimized)
        {
            try
            {
                if (gameMinimized)
                {
                    if (_hiddenByMinimize) return;
                    _hiddenByMinimize = true;
                    if (_visible)
                    {
                        ShowWindow(_hwnd, SW_HIDE);
                        Log("游戏窗口已最小化：伴侣窗口同步隐藏");
                    }
                }
                else if (_hiddenByMinimize)
                {
                    _hiddenByMinimize = false;
                    _lastGameValid = false;
                    if (_visible)
                    {
                        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                        InvalidateRect(_hwnd, IntPtr.Zero, false);
                        Log("游戏窗口已还原：伴侣窗口同步显示");
                    }
                }
            }
            catch { }
        }

        #endregion

        #region 诊断 & 应急

        /// <summary>把窗口强制移到屏幕右下角并置顶显示（F9）。</summary>
        private static void Rescue(IntPtr hWnd)
        {
            try
            {
                RECT work = new RECT();
                SystemParametersInfoW(0x0030 /*SPI_GETWORKAREA*/, 0, ref work, 0);
                if (work.right <= work.left) { work.left = 0; work.top = 0; work.right = 1920; work.bottom = 1080; }
                int x = work.right - _width - 8;
                int y = work.bottom - _height - 8;
                if (x < work.left) x = work.left;
                if (y < work.top) y = work.top;

                _userMoved = true;      // 用户已"接管"，停止自动跟随
                _visible = true;
                SetWindowPos(hWnd, HWND_TOPMOST, x, y, _width, _height, SWP_NOACTIVATE);
                ShowWindow(hWnd, SW_SHOWNOACTIVATE);
                InvalidateRect(hWnd, IntPtr.Zero, false);
                Log(string.Format("F9：已强制移动到屏幕右下角 ({0},{1}) 尺寸={2}x{3}", x, y, _width, _height));
                Probe("F9后");
            }
            catch { }
        }

        /// <summary>从屏幕 DC 采样窗口矩形区域，判断窗口是否真的被显示在最上层。</summary>
        private static void Probe(string tag)
        {
            try
            {
                if (_hwnd == IntPtr.Zero) return;
                RECT wr;
                if (!GetWindowRect(_hwnd, out wr)) return;

                var sb = new StringBuilder();
                sb.Append("探针[").Append(tag).Append("] 矩形=(")
                  .Append(wr.left).Append(",").Append(wr.top).Append(")-(")
                  .Append(wr.right).Append(",").Append(wr.bottom).Append(") ")
                  .Append("尺寸=").Append(wr.right - wr.left).Append("x").Append(wr.bottom - wr.top)
                  .Append(" 可见=").Append(IsWindowVisible(_hwnd))
                  .Append(" 收起=").Append(_collapsed);

                // 1) 屏幕 DC 采样：值应等于我们窗口的配色（BG=0C0E16 / 标题栏=181C2C）
                IntPtr sdc = GetDC(IntPtr.Zero);
                sb.Append(" | 屏幕采样: ");
                int stepX = Math.Max(1, (wr.right - wr.left) / 6);
                int stepY = Math.Max(1, (wr.bottom - wr.top) / 5);
                for (int yy = wr.top + 2; yy < wr.bottom - 1; yy += stepY)
                {
                    for (int xx = wr.left + 2; xx < wr.right - 1; xx += stepX)
                    {
                        uint c = GetPixel(sdc, xx, yy);
                        sb.Append(c == 0xFFFFFFFF ? "??????" : (c & 0xFFFFFF).ToString("X6")).Append(" ");
                    }
                    sb.Append("/ ");
                }
                ReleaseDC(IntPtr.Zero, sdc);

                // 2) 窗口自身 DC 采样：验证绘制路径确实产出了像素
                IntPtr wdc = GetDC(_hwnd);
                int mx = Math.Max(1, (wr.right - wr.left) / 2);
                int my = Math.Max(1, (wr.bottom - wr.top) / 2);
                uint ct = GetPixel(wdc, mx, 6);      // 标题栏中部
                uint cb = GetPixel(wdc, mx, my);     // 内容区中部
                ReleaseDC(_hwnd, wdc);
                sb.Append(" | 自绘采样: 标题=").Append(ct == 0xFFFFFFFF ? "??????" : (ct & 0xFFFFFF).ToString("X6"))
                  .Append(" 内容=").Append(cb == 0xFFFFFFFF ? "??????" : (cb & 0xFFFFFF).ToString("X6"));

                Log(sb.ToString());
            }
            catch (Exception e) { Log("探针异常: " + e.Message); }
        }

        #endregion

        #region 布局 & 绘制

        private static void Layout(IntPtr hWnd)
        {
            if (_inLayout || hWnd == IntPtr.Zero) return;
            // 拖动/缩放过程中不要和系统的移动循环抢窗口位置和尺寸，否则会明显发卡、掉帧；
            // 等 WM_EXITSIZEMOVE 再一次性把最新内容排好。
            if (_inMove) return;
            _inLayout = true;
            try
            {
                RECT cr;
                GetClientRect(hWnd, out cr);
                int cw = cr.right - cr.left;
                if (cw < 200) cw = 200;
                _width = cw;

                lock (_lock)
                {
                    if (_hasPending)
                    {
                        _paint.Clear();
                        _paint.AddRange(_pendingRows);
                        _paintTitle = _pendingTitle;
                        _hasPending = false;
                    }
                }

                // 标题栏按钮（与标题文字同一垂直中心）
                int by = 1 + (TITLE_H - BTN_H) / 2;
                _btnClose = new RECT { left = cw - PAD - CLOSE_W, top = by, right = cw - PAD, bottom = by + BTN_H };
                _btnCollapse = new RECT { left = _btnClose.left - 6 - BTN_W, top = by, right = _btnClose.left - 6, bottom = by + BTN_H };
                _btnAdv = new RECT { left = _btnCollapse.left - 6 - BTN_W, top = by, right = _btnCollapse.left - 6, bottom = by + BTN_H };

                // 标签页
                int tabTop = 1 + TITLE_H;
                int tabW = (cw - PAD * 2) / 4;
                for (int i = 0; i < 4; i++)
                {
                    _tabRects[i] = new RECT
                    {
                        left = PAD + i * tabW,
                        top = tabTop + 2,
                        right = PAD + (i + 1) * tabW - 4,
                        bottom = tabTop + TAB_H - 2
                    };
                }

                // 只排版当前标签页的行（Tab=-1 的行在所有页都显示）
                _shown.Clear();
                for (int i = 0; i < _paint.Count; i++)
                {
                    var r = _paint[i];
                    if (r.Tab < 0 || r.Tab == _tab) _shown.Add(r);
                }

                IntPtr dc = GetDC(hWnd);
                int innerW = cw - PAD * 2;
                _rowTop.Clear();
                _rowH.Clear();

                int y = 1 + TITLE_H + TAB_H + PAD;
                if (!_collapsed)
                {
                    for (int i = 0; i < _shown.Count; i++)
                    {
                        var row = _shown[i];
                        _rowTop.Add(y);
                        if (string.IsNullOrEmpty(row.Text))
                        {
                            _rowH.Add(8);
                            y += 8;
                            continue;
                        }
                        int w = innerW - row.Indent;
                        if (row.Rank > 0 || row.Icon >= 0) w -= 38;
                        if (row.Hero) w -= 20;
                        if (w < 40) w = 40;
                        int h = Measure(dc, row.Text, w, FontOf(row.Kind));
                        if (row.Hero) h += 18;      // 首推卡片：上下留白
                        if (row.Card) h += 8;
                        _rowH.Add(h);
                        y += h + 3;
                    }
                }
                ReleaseDC(hWnd, dc);

                int contentTop = 1 + TITLE_H + TAB_H + PAD;
                int contentBottom = y;
                int minH = 1 + TITLE_H + TAB_H + PAD * 2;
                int totalH = contentBottom + PAD;
                if (totalH < minH) totalH = minH;

                // 高度上限：不允许超出屏幕底部（否则出牌计划会被截掉）
                RECT wr0;
                GetWindowRect(hWnd, out wr0);
                int maxH = GetSystemMetrics(1) - wr0.top - 24;   // SM_CYSCREEN
                if (maxH < 240) maxH = 240;
                if (totalH > maxH) totalH = maxH;

                _height = totalH;

                // 内容超出可视区时启用滚轮滚动
                int viewH = _height - contentTop - PAD;
                int contentH = contentBottom - contentTop;
                if (viewH < 40) viewH = 40;
                _maxScroll = contentH - viewH;
                if (_maxScroll < 0) _maxScroll = 0;
                if (_scrollY > _maxScroll) _scrollY = _maxScroll;
                if (_scrollY < 0) _scrollY = 0;
                if (_scrollY > 0)
                {
                    for (int i = 0; i < _rowTop.Count; i++) _rowTop[i] -= _scrollY;
                }

                RECT wr;
                GetWindowRect(hWnd, out wr);
                // 尺寸没变就不要重复 SetWindowPos：每次内容刷新都重设窗口会增加无谓的重绘与抖动
                if (wr.right - wr.left != _width || wr.bottom - wr.top != _height)
                    SetWindowPos(hWnd, IntPtr.Zero, 0, 0, _width, _height,
                        SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOZORDER);

                string sig = _paint.Count + "|" + _width + "|" + _height + "|" + wr.left + "," + wr.top;
                if (sig != _lastLayoutSig)
                {
                    _lastLayoutSig = sig;
                    Log(string.Format("布局 行数={0} 位置=({1},{2}) 尺寸={3}x{4} 收起={5}",
                        _paint.Count, wr.left, wr.top, _width, _height, _collapsed));
                }
            }
            catch { }
            finally { _inLayout = false; }
        }

        private static IntPtr FontOf(int kind)
        {
            switch (kind)
            {
                case 0: return _fTitle;
                case 1: return _fSub;
                case 3: return _fHi;
                case 4: return _fHint;
                case 6: return _fHero;
                default: return _fRow;
            }
        }

        private static int ColorOf(int kind)
        {
            switch (kind)
            {
                case 0: return C_TEXT_TITLE;
                case 1: return C_TEXT_SUB;
                case 3: return C_TEXT_HI;
                case 4: return C_TEXT_HINT;
                case 6: return C_TEXT_HI;
                default: return C_TEXT;
            }
        }

        private static int Measure(IntPtr dc, string text, int width, IntPtr font)
        {
            try
            {
                IntPtr old = SelectObject(dc, font);
                RECT r = new RECT { left = 0, top = 0, right = width, bottom = 0 };
                DrawTextW(dc, text, -1, ref r, DT_LEFT | DT_TOP | DT_WORDBREAK | DT_NOPREFIX | DT_CALCRECT);
                SelectObject(dc, old);
                int h = r.bottom - r.top;
                return h < 16 ? 16 : h;
            }
            catch { return 18; }
        }

        private static void Paint(IntPtr hWnd)
        {
            PAINTSTRUCT ps;
            IntPtr dc = BeginPaint(hWnd, out ps);
            try
            {
                // 离屏缓冲：整帧先画到内存 DC，再一次性 BitBlt 上屏，
                // 避免逐条 DrawText/填色直接刷到屏幕造成的闪烁与卡顿感。
                RECT cr;
                GetClientRect(hWnd, out cr);
                int w = cr.right - cr.left, h = cr.bottom - cr.top;
                IntPtr mem = GetBufferDC(dc, w, h);
                if (mem != IntPtr.Zero)
                {
                    RenderTo(mem, hWnd);
                    BitBlt(dc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
                }
                else
                {
                    RenderTo(dc, hWnd);
                }
            }
            catch { }
            finally { EndPaint(hWnd, ref ps); }
        }

        /// <summary>取得（必要时创建/扩容）离屏绘制用的内存 DC。</summary>
        private static IntPtr GetBufferDC(IntPtr refDc, int w, int h)
        {
            try
            {
                if (w <= 0 || h <= 0) return IntPtr.Zero;
                if (_memDC != IntPtr.Zero && _memBmp != IntPtr.Zero && _memW >= w && _memH >= h)
                    return _memDC;

                ReleaseGdiBuffer();
                _memDC = CreateCompatibleDC(refDc);
                if (_memDC == IntPtr.Zero) return IntPtr.Zero;
                _memBmp = CreateCompatibleBitmap(refDc, w, h);
                if (_memBmp == IntPtr.Zero) { DeleteDC(_memDC); _memDC = IntPtr.Zero; return IntPtr.Zero; }
                SelectObject(_memDC, _memBmp);
                _memW = w;
                _memH = h;
                return _memDC;
            }
            catch { return IntPtr.Zero; }
        }

        private static void ReleaseGdiBuffer()
        {
            // 先删 DC 再删位图：位图还选中在 DC 里时无法被释放
            try { if (_memDC != IntPtr.Zero) DeleteDC(_memDC); } catch { }
            try { if (_memBmp != IntPtr.Zero) DeleteObject(_memBmp); } catch { }
            _memBmp = IntPtr.Zero;
            _memDC = IntPtr.Zero;
            _memW = 0;
            _memH = 0;
        }

        private static void ReleaseGdiResources()
        {
            ReleaseGdiBuffer();
            try
            {
                foreach (var kv in _brushes) { if (kv.Value != IntPtr.Zero) DeleteObject(kv.Value); }
                _brushes.Clear();
                foreach (var kv in _pens) { if (kv.Value != IntPtr.Zero) DeleteObject(kv.Value); }
                _pens.Clear();
            }
            catch { }
        }

        /// <summary>把窗口内容画到指定 DC（WM_PAINT 与 WM_PRINTCLIENT 共用）。</summary>
        private static void RenderTo(IntPtr dc, IntPtr hWnd)
        {
            RECT cr;
            GetClientRect(hWnd, out cr);
            int cw = cr.right, ch = cr.bottom;
            if (cw <= 0 || ch <= 0) return;

            try { SetBkMode(dc, TRANSPARENT); } catch { }

            RECT full = new RECT { left = 0, top = 0, right = cw, bottom = ch };
            FillR(dc, full, C_BORDER);
            RECT body = new RECT { left = 1, top = 1, right = cw - 1, bottom = ch - 1 };
            FillR(dc, body, C_BG);
            RECT tb = new RECT { left = 1, top = 1, right = cw - 1, bottom = 1 + TITLE_H };
            FillR(dc, tb, C_TITLEBAR);

            // 标题（前置金色菱形徽记），与右侧按钮在同一垂直中心
            DrawRoleIcon(dc, 4, PAD, 1 + (TITLE_H - 10) / 2, 10, C_GOLD);
            DrawTVC(dc, _paintTitle + (_collapsed ? "  （已收起）" : ""),
                PAD + 16, 1, cw - PAD * 3 - BTN_W * 2 - CLOSE_W - 28, TITLE_H,
                _fTitle, C_TEXT_TITLE);

            // 标题栏按钮（带悬停反馈）
            DrawBtn(dc, _btnAdv, _advanced ? "简化" : "进阶", 1);
            DrawBtn(dc, _btnCollapse, _collapsed ? "展开" : "收起", 2);
            DrawBtn(dc, _btnClose, "×", 3);

            // 标签页
            for (int i = 0; i < 4; i++)
            {
                bool active = _tab == i;
                FillRound(dc, _tabRects[i], active ? C_BTN_HOT : C_BG, 6);
                DrawTextCentered(dc, TAB_NAMES[i], _tabRects[i], _fBtn, active ? C_GOLD : C_TEXT_SUB);
            }

            if (!_collapsed)
            {
                int innerW = cw - PAD * 2;
                int saved = SaveDC(dc);
                try { IntersectClipRect(dc, 1, 1 + TITLE_H + TAB_H, cw - 1, ch - 1); } catch { }

                for (int i = 0; i < _shown.Count && i < _rowTop.Count; i++)
                {
                    var row = _shown[i];
                    int ry = _rowTop[i];
                    int rh = _rowH[i];

                    if (ry + rh < 1 + TITLE_H + TAB_H || ry > ch) continue;   // 不在可视区
                    if (string.IsNullOrEmpty(row.Text)) continue;

                    bool hover = i == _hoverRow;
                    int baseX = PAD + row.Indent;
                    int textX = baseX;
                    int textY = ry;
                    int textH = rh;

                    RECT band = new RECT { left = PAD - 4, top = ry - 2, right = cw - PAD + 2, bottom = ry + rh };

                    if (row.Hero)
                    {
                        // 首推：圆角高亮卡片 + 金色大箭头 + 21px 大字
                        FillRound(dc, band, C_CARD_HERO, 12);
                        DrawArrow(dc, baseX, ry + rh / 2 - 6, 12, C_GOLD);
                        textX += 20;
                        textY += 8;
                        textH -= 8;
                    }
                    else if (row.Card)
                    {
                        // 普通出牌条目：圆角卡片，悬停变亮
                        FillRound(dc, band, hover ? C_CARD_HOVER : C_CARD, 9);
                        textY += 4;
                        textH -= 4;
                    }
                    else if (hover)
                    {
                        FillRound(dc, band, C_CARD, 8);
                    }

                    if (row.Rank > 0)
                    {
                        DrawBadge(dc, row.Rank, baseX, ry + 2, 18,
                            row.Rank == 1 ? C_GOLD : C_TEXT_SUB);
                        textX += 22;
                    }
                    if (row.Icon >= 0)
                    {
                        DrawRoleIcon(dc, row.Icon, textX, ry + 4, 14,
                            row.IconColor >= 0 ? row.IconColor : C_TEXT_SUB);
                        textX += 18;
                    }

                    DrawT(dc, row.Text, textX, textY, innerW - (textX - PAD), textH,
                        FontOf(row.Kind), ColorOf(row.Kind));
                }

                // 滚动条：内容超出可视区时出现（只做导航，不表达任何数值）
                if (_maxScroll > 0)
                {
                    RECT track = new RECT { left = cw - 7, top = 1 + TITLE_H + TAB_H + 4, right = cw - 3, bottom = ch - 6 };
                    FillRound(dc, track, C_SCROLL_TRACK, 4);
                    int viewH = ch - (1 + TITLE_H + TAB_H) - PAD;
                    int total = viewH + _maxScroll;
                    int thumbH = total > 0 ? Math.Max(24, viewH * viewH / total) : 24;
                    int travel = (track.bottom - track.top) - thumbH;
                    int pos = _maxScroll > 0 ? travel * _scrollY / _maxScroll : 0;
                    RECT thumb = new RECT { left = track.left, top = track.top + pos, right = track.right, bottom = track.top + pos + thumbH };
                    FillRound(dc, thumb, C_SCROLL, 4);
                }

                try { RestoreDC(dc, saved); } catch { }
            }

            // 右下角缩放提示
            DrawT(dc, "◢", cw - 16, ch - 18, 14, 14, _fHint, C_TEXT_HINT);

            _paintCount++;
            if (_paintCount == 1 || _paintCount == 5)
            {
                IntPtr sdc = GetDC(hWnd);
                uint a = GetPixel(sdc, 4, 4);
                uint b = GetPixel(sdc, PAD + 2, PAD + 6);
                uint c = GetPixel(sdc, cw / 2, ch / 2);
                ReleaseDC(hWnd, sdc);
                Log(string.Format("绘制#{0} {1}x{2} 边框={3:X6} 标题字={4:X6} 中部={5:X6} 行数={6}",
                    _paintCount, cw, ch, a & 0xFFFFFF, b & 0xFFFFFF, c & 0xFFFFFF, _paint.Count));
            }
        }

        private static void FillR(IntPtr dc, RECT r, int color)
        {
            try
            {
                // 画刷缓存复用：以前每次填色都 CreateSolidBrush+DeleteObject，
                // 一帧十几次 GDI 分配，拖动时是主要的卡顿来源。
                IntPtr br;
                if (!_brushes.TryGetValue(color, out br) || br == IntPtr.Zero)
                {
                    br = CreateSolidBrush(color);
                    if (br == IntPtr.Zero) { Err("CreateSolidBrush", null); return; }
                    _brushes[color] = br;
                }
                RECT rr = r;
                FillRect(dc, ref rr, br);
            }
            catch (Exception e) { Err("FillRect", e); }
        }

        private static IntPtr Brush(int color)
        {
            IntPtr br;
            if (_brushes.TryGetValue(color, out br) && br != IntPtr.Zero) return br;
            br = CreateSolidBrush(color);
            if (br != IntPtr.Zero) _brushes[color] = br;
            return br;
        }

        private static IntPtr Pen(int color)
        {
            IntPtr pen;
            if (_pens.TryGetValue(color, out pen) && pen != IntPtr.Zero) return pen;
            pen = CreatePen(0, 1, color);
            if (pen != IntPtr.Zero) _pens[color] = pen;
            return pen;
        }

        /// <summary>圆角填充（笔与刷同色，等价于无边框圆角色块）。</summary>
        private static void FillRound(IntPtr dc, RECT r, int color, int radius)
        {
            IntPtr br = Brush(color);
            if (br == IntPtr.Zero) return;
            IntPtr pen = Pen(color);
            IntPtr ob = SelectObject(dc, br);
            IntPtr op = pen != IntPtr.Zero ? SelectObject(dc, pen) : IntPtr.Zero;
            try { RoundRect(dc, r.left, r.top, r.right, r.bottom, radius, radius); } catch { }
            if (op != IntPtr.Zero) SelectObject(dc, op);
            SelectObject(dc, ob);
        }

        /// <summary>用同一颜色填充椭圆（笔与刷同色，等价于无边框色块）。</summary>
        private static void FillEllipse(IntPtr dc, int x, int y, int w, int h, int color)
        {
            IntPtr br = Brush(color);
            if (br == IntPtr.Zero) return;
            IntPtr pen = Pen(color);
            IntPtr ob = SelectObject(dc, br);
            IntPtr op = pen != IntPtr.Zero ? SelectObject(dc, pen) : IntPtr.Zero;
            try { Ellipse(dc, x, y, x + w, y + h); } catch { }
            if (op != IntPtr.Zero) SelectObject(dc, op);
            SelectObject(dc, ob);
        }

        private static void FillPoly(IntPtr dc, POINT[] pts, int color)
        {
            if (pts == null || pts.Length < 3) return;
            IntPtr br = Brush(color);
            if (br == IntPtr.Zero) return;
            IntPtr pen = Pen(color);
            IntPtr ob = SelectObject(dc, br);
            IntPtr op = pen != IntPtr.Zero ? SelectObject(dc, pen) : IntPtr.Zero;
            try { Polygon(dc, pts, pts.Length); } catch { }
            if (op != IntPtr.Zero) SelectObject(dc, op);
            SelectObject(dc, ob);
        }

        /// <summary>首推行前的金色三角箭头。</summary>
        private static void DrawArrow(IntPtr dc, int x, int y, int size, int color)
        {
            FillPoly(dc, new POINT[]
            {
                new POINT { x = x, y = y },
                new POINT { x = x + size, y = y + size / 2 },
                new POINT { x = x, y = y + size },
            }, color);
        }

        /// <summary>出牌序号徽章：实心圆 + 深色数字。</summary>
        private static void DrawBadge(IntPtr dc, int rank, int x, int y, int d, int color)
        {
            FillEllipse(dc, x, y, d, d, color);
            try
            {
                if (_fNum != IntPtr.Zero) SelectObject(dc, _fNum);
                SetTextColor(dc, C_BG);
                RECT r = new RECT { left = x, top = y + 1, right = x + d, bottom = y + d };
                DrawTextW(dc, rank.ToString(), -1, ref r, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            }
            catch { }
        }

        /// <summary>角色图标：全部用 GDI 基本图形绘制，不依赖任何外部图片资源。</summary>
        private static void DrawRoleIcon(IntPtr dc, int icon, int x, int y, int s, int color)
        {
            try
            {
                switch (icon)
                {
                    case 0: // 法力：水滴
                        FillPoly(dc, new POINT[] {
                            new POINT{x=x+s/2,y=y}, new POINT{x=x+s-2,y=y+s/2},
                            new POINT{x=x+s-3,y=y+s-2}, new POINT{x=x+3,y=y+s-2},
                            new POINT{x=x+2,y=y+s/2}}, color);
                        break;
                    case 1: // 武器：剑
                        FillPoly(dc, new POINT[] {
                            new POINT{x=x+s/2,y=y}, new POINT{x=x+s/2+2,y=y+s/2},
                            new POINT{x=x+s/2,y=y+s-4}, new POINT{x=x+s/2-2,y=y+s/2}}, color);
                        FillR(dc, new RECT { left = x + 2, top = y + s / 2, right = x + s - 2, bottom = y + s / 2 + 2 }, color);
                        break;
                    case 2: // 防御：盾
                        FillPoly(dc, new POINT[] {
                            new POINT{x=x+s/2,y=y}, new POINT{x=x+s-1,y=y+2},
                            new POINT{x=x+s-2,y=y+s/2}, new POINT{x=x+s/2,y=y+s},
                            new POINT{x=x+2,y=y+s/2}, new POINT{x=x+1,y=y+2}}, color);
                        break;
                    case 3: // 经济：金币
                        FillEllipse(dc, x + 1, y + 2, s - 2, s - 4, color);
                        FillEllipse(dc, x + s / 2 - 1, y + s / 2 - 1, 2, 2, C_BG);
                        break;
                    case 4: // 被动：菱形
                        FillPoly(dc, new POINT[] {
                            new POINT{x=x+s/2,y=y}, new POINT{x=x+s-1,y=y+s/2},
                            new POINT{x=x+s/2,y=y+s}, new POINT{x=x+1,y=y+s/2}}, color);
                        break;
                    case 5: // 功能：四角星
                        FillPoly(dc, new POINT[] {
                            new POINT{x=x+s/2,y=y}, new POINT{x=x+s/2+2,y=y+s/2-2},
                            new POINT{x=x+s,y=y+s/2}, new POINT{x=x+s/2+2,y=y+s/2+2},
                            new POINT{x=x+s/2,y=y+s}, new POINT{x=x+s/2-2,y=y+s/2+2},
                            new POINT{x=x,y=y+s/2}, new POINT{x=x+s/2-2,y=y+s/2-2}}, color);
                        break;
                    case 6: // 角色：头 + 肩
                        FillEllipse(dc, x + s / 2 - 3, y + 1, 6, 6, color);
                        FillPoly(dc, new POINT[] {
                            new POINT{x=x+2,y=y+s-1}, new POINT{x=x+2,y=y+s-4},
                            new POINT{x=x+s/2,y=y+s-6}, new POINT{x=x+s-2,y=y+s-4},
                            new POINT{x=x+s-2,y=y+s-1}}, color);
                        break;
                    case 7: // 负面：骷髅
                        FillEllipse(dc, x + 1, y + 1, s - 2, s - 4, color);
                        FillR(dc, new RECT { left = x + 4, top = y + 4, right = x + 6, bottom = y + 6 }, C_BG);
                        FillR(dc, new RECT { left = x + s - 6, top = y + 4, right = x + s - 4, bottom = y + 6 }, C_BG);
                        FillR(dc, new RECT { left = x + s / 2 - 3, top = y + s - 4, right = x + s / 2 + 3, bottom = y + s - 1 }, color);
                        break;
                    default: // 未知：方块
                        FillR(dc, new RECT { left = x + 2, top = y + 3, right = x + s - 2, bottom = y + s - 3 }, color);
                        break;
                }
            }
            catch (Exception e) { Err("RoleIcon", e); }
        }

        /// <summary>单行垂直居中绘制（用于标题栏，保证与按钮文字同一基线）。</summary>
        private static void DrawTVC(IntPtr dc, string text, int x, int y, int w, int h, IntPtr font, int color)
        {
            if (string.IsNullOrEmpty(text) || w <= 0) return;
            try
            {
                if (font != IntPtr.Zero) SelectObject(dc, font);
                SetTextColor(dc, color);
                RECT r = new RECT { left = x, top = y, right = x + w, bottom = y + h };
                DrawTextW(dc, text, -1, ref r, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            }
            catch (Exception e) { Err("DrawTextVC", e); }
        }

        private static void DrawT(IntPtr dc, string text, int x, int y, int w, int h, IntPtr font, int color)
        {
            if (string.IsNullOrEmpty(text) || w <= 0) return;
            try
            {
                if (font != IntPtr.Zero) SelectObject(dc, font);
                SetTextColor(dc, color);
                RECT r = new RECT { left = x, top = y, right = x + w, bottom = y + h };
                DrawTextW(dc, text, -1, ref r, DT_LEFT | DT_TOP | DT_WORDBREAK | DT_NOPREFIX);
            }
            catch (Exception e) { Err("DrawText", e); }
        }

        private static void Err(string stage, Exception e)
        {
            try
            {
                if (_lastErrStage == stage) return;
                _lastErrStage = stage;
                Log("绘制失败于 " + stage + "：" + (e == null ? "句柄为空" : e.GetType().Name + " " + e.Message));
            }
            catch { }
        }

        private static void DrawBtn(IntPtr dc, RECT r, string label, int id)
        {
            bool hot = _hotBtn == id;
            FillRound(dc, r, hot ? C_BTN_HOT : C_BTN, 6);
            DrawTextCentered(dc, label, r, _fBtn, hot ? C_GOLD : C_TEXT_TITLE);
        }

        /// <summary>在给定矩形内水平垂直居中绘制文本（按钮文字居中比左对齐更整齐）。</summary>
        private static void DrawTextCentered(IntPtr dc, string text, RECT r, IntPtr font, int color)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (font != IntPtr.Zero) SelectObject(dc, font);
                SetTextColor(dc, color);
                RECT rr = r;
                DrawTextW(dc, text, -1, ref rr, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            }
            catch (Exception e) { Err("DrawTextC", e); }
        }

        #endregion

        #region 消息处理

        private static bool PtIn(RECT r, POINT p)
        {
            return p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom;
        }

        private static POINT ClientPoint(IntPtr hWnd, IntPtr lParam)
        {
            long v = lParam.ToInt64();
            POINT p = new POINT { x = (short)(v & 0xFFFF), y = (short)((v >> 16) & 0xFFFF) };
            ScreenToClient(hWnd, ref p);
            return p;
        }

        /// <summary>
        /// 鼠标消息（WM_LBUTTONDOWN 等）的 lParam 已经是客户区坐标，不能再做一次屏幕→客户区转换，
        /// 否则命中判定会整体偏移，导致“简化/收起/关闭”按钮点了没反应。
        /// </summary>
        private static POINT MouseClientPoint(IntPtr lParam)
        {
            long v = lParam.ToInt64();
            return new POINT { x = (short)(v & 0xFFFF), y = (short)((v >> 16) & 0xFFFF) };
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_PAINT:
                        Paint(hWnd);
                        return IntPtr.Zero;

                    case WM_ERASEBKGND:
                        return new IntPtr(1);

                    case WM_PRINTCLIENT:
                        RenderTo(wParam, hWnd);
                        return IntPtr.Zero;

                    case WM_CONTENT:
                        // 拖动中先攒着，等 WM_EXITSIZEMOVE 再排版重绘
                        if (_inMove) return IntPtr.Zero;
                        Layout(hWnd);
                        if (_visible) InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;

                    case WM_NCHITTEST:
                        {
                            POINT p = ClientPoint(hWnd, lParam);
                            if (PtIn(_btnAdv, p) || PtIn(_btnCollapse, p) || PtIn(_btnClose, p))
                                return new IntPtr(HTCLIENT);
                            if (!_collapsed)
                            {
                                RECT cr;
                                GetClientRect(hWnd, out cr);
                                if (p.x >= cr.right - 16 && p.y >= cr.bottom - 16)
                                    return new IntPtr(HTBOTTOMRIGHT);
                            }
                            if (p.y < 1 + TITLE_H) return new IntPtr(HTCAPTION);
                            return new IntPtr(HTCLIENT);
                        }

                    case WM_SETCURSOR:
                        {
                            int ht = (int)(lParam.ToInt64() & 0xFFFF);
                            try { SetCursor(ht == HTBOTTOMRIGHT ? _hSize : _hArrow); } catch { }
                            return new IntPtr(1);   // TRUE：阻止默认处理，避免继承游戏的等待光标
                        }

                    case WM_LBUTTONDOWN:
                        {
                            POINT p = MouseClientPoint(lParam);

                            // 标签页切换
                            for (int i = 0; i < 4; i++)
                            {
                                if (PtIn(_tabRects[i], p))
                                {
                                    if (_tab != i)
                                    {
                                        _tab = i;
                                        _scrollY = 0;
                                        _hoverRow = -1;
                                        Layout(hWnd);
                                        InvalidateRect(hWnd, IntPtr.Zero, false);
                                        Log("切换标签页 → " + TAB_NAMES[i]);
                                    }
                                    return IntPtr.Zero;
                                }
                            }

                            if (PtIn(_btnClose, p))
                            {
                                _visible = false;
                                ShowWindow(hWnd, SW_HIDE);
                                Log("点击 × ：已隐藏窗口（F8 可再次显示）");
                                return IntPtr.Zero;
                            }
                            if (PtIn(_btnCollapse, p))
                            {
                                _collapsed = !_collapsed;
                                Layout(hWnd);
                                InvalidateRect(hWnd, IntPtr.Zero, false);
                                Log(_collapsed ? "点击 收起：面板已收起" : "点击 展开：面板已展开");
                                return IntPtr.Zero;
                            }
                            if (PtIn(_btnAdv, p))
                            {
                                _advanced = !_advanced;
                                InvalidateRect(hWnd, IntPtr.Zero, false);
                                Log(_advanced ? "点击 简化/进阶：已切换为进阶" : "点击 简化/进阶：已切换为简化");
                                return IntPtr.Zero;
                            }
                            return IntPtr.Zero;
                        }

                    case WM_MOUSEMOVE:
                        {
                            POINT p = MouseClientPoint(lParam);
                            int hot = PtIn(_btnClose, p) ? 3 : PtIn(_btnCollapse, p) ? 2 : PtIn(_btnAdv, p) ? 1 : 0;
                            if (hot != _hotBtn)
                            {
                                _hotBtn = hot;
                                InvalidateRect(hWnd, IntPtr.Zero, false);
                            }
                            if (!_trackingMouse)
                            {
                                _trackingMouse = true;
                                var tme = new TRACKMOUSEEVENT
                                {
                                    cbSize = (uint)Marshal.SizeOf(typeof(TRACKMOUSEEVENT)),
                                    dwFlags = TME_LEAVE,
                                    hwndTrack = hWnd,
                                    dwHoverTime = 0
                                };
                                TrackMouseEvent(ref tme);
                            }

                            // 行悬停高亮：让鼠标经过的条目有轻微反馈
                            int hover = -1;
                            if (!_collapsed)
                            {
                                for (int i = 0; i < _rowTop.Count; i++)
                                {
                                    if (p.y >= _rowTop[i] && p.y < _rowTop[i] + _rowH[i]) { hover = i; break; }
                                }
                            }
                            if (hover != _hoverRow)
                            {
                                _hoverRow = hover;
                                InvalidateRect(hWnd, IntPtr.Zero, false);
                            }
                            return IntPtr.Zero;
                        }

                    case WM_MOUSEWHEEL:
                        {
                            int delta = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                            if (delta > 32767) delta -= 65536;
                            if (delta != 0 && _maxScroll > 0)
                            {
                                _scrollY -= (delta / 120) * 48;
                                if (_scrollY < 0) _scrollY = 0;
                                if (_scrollY > _maxScroll) _scrollY = _maxScroll;
                                Layout(hWnd);
                                InvalidateRect(hWnd, IntPtr.Zero, false);
                            }
                            return IntPtr.Zero;
                        }

                    case WM_MOUSELEAVE:
                        _trackingMouse = false;
                        if (_hotBtn != 0 || _hoverRow != -1)
                        {
                            _hotBtn = 0;
                            _hoverRow = -1;
                            InvalidateRect(hWnd, IntPtr.Zero, false);
                        }
                        return IntPtr.Zero;

                    case WM_ENTERSIZEMOVE:
                        _userMoved = true;
                        _inMove = true;
                        return IntPtr.Zero;

                    case WM_EXITSIZEMOVE:
                        _inMove = false;
                        Layout(hWnd);
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;

                    case WM_HOTKEY:
                        if (wParam.ToInt32() == HK_TOGGLE)
                        {
                            if (_visible)
                            {
                                _visible = false;
                                ShowWindow(hWnd, SW_HIDE);
                                Log("F8：已隐藏窗口");
                            }
                            else
                            {
                                _visible = true;
                                _userMoved = false;
                                _lastGameValid = false;
                                FollowGame();
                                // 游戏窗口当时若处于最小化，保持隐藏，等游戏还原后自动显示
                                if (!_hiddenByMinimize) ShowWindow(hWnd, SW_SHOWNOACTIVATE);
                                InvalidateRect(hWnd, IntPtr.Zero, false);
                                Probe("F8显示后");
                            }
                        }
                        else if (wParam.ToInt32() == HK_RESCUE)
                        {
                            Rescue(hWnd);
                        }
                        return IntPtr.Zero;

                    case WM_TIMER:
                        _timerTicks++;
                        FollowGame();
                        // 每秒重申一次置顶，防止被其它置顶窗口压住（拖动中跳过，避免打断移动循环）
                        if (!_inMove)
                            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                        if (_hasPending && !_inMove) { Layout(hWnd); InvalidateRect(hWnd, IntPtr.Zero, false); }
                        if (_timerTicks == 4 || _timerTicks == 15 || (_timerTicks % 60) == 0)
                            Probe("定时" + _timerTicks + "s");
                        return IntPtr.Zero;

                    case WM_MOUSEACTIVATE:
                        return new IntPtr(MA_NOACTIVATE);

                    case WM_DESTROY:
                        KillTimer(hWnd, new IntPtr(1));
                        UnregisterHotKey(hWnd, HK_TOGGLE);
                        UnregisterHotKey(hWnd, HK_RESCUE);
                        ReleaseGdiResources();
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch { }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        #endregion
    }
}
