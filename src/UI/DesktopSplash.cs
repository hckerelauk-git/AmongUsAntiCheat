using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// 桌面右下角启动画面。
    ///
    /// 当前视觉（2026-10 改版，不再是最初的蓝色渐变方案）：
    ///   - 14px 圆角卡片（SetWindowRgn 真裁窗口，不是画个假的）
    ///   - 深空竖向渐变底 #0A1018 → #141F33 + 1px 描边
    ///   - 左侧青色盾牌徽标（圆角方块底衬 + Polygon 画的盾牌）
    ///   - 右侧主标题「Apex Cheat Ender」+ 副标题
    ///   - 底部内缩圆角进度条（青色填充 + 暗色轨道）
    ///
    /// 实现方式：纯 P/Invoke 调 Win32（user32 + gdi32）自建窗口，
    /// 不依赖 WinForms / WPF —— 游戏进程跑在 CoreCLR 上，
    /// 机器上通常没装 .NET Desktop Runtime，引用 WinForms 会抛 FileNotFoundException。
    ///
    /// ────────────── 性能与体积优化 ──────────────
    /// 1. 窗口 360×96，紧凑尺寸
    /// 2. 刷新率 30fps（33ms）
    /// 3. **按需重绘**：只有进度变化 ≥0.5% 或阶段切换才 InvalidateRect
    /// 4. **按需调 API**：透明度变化 <2/255 时不调 SetLayeredWindowAttributes
    /// 5. GDI 画笔/字体句柄全局复用
    /// 6. 渐变底用色带法（每 4px 一条），避免引入 msimg32 依赖
    ///
    /// ────────────── P/Invoke 回调三条铁律 ──────────────
    /// 1. 委托必须静态字段保活，否则被 GC 后回调 = 访问违规
    /// 2. 必须显式声明调用约定（WNDPROC 是 __stdcall）
    /// 3. 回调体内必须包 try-catch，托管异常绝不能穿出 P/Invoke 边界
    /// </summary>
    internal static class DesktopSplash
    {
        // ================= 窗口样式 =================
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;

        // ================= 消息 =================
        private const int WM_PAINT = 0x000F;
        private const int WM_TIMER = 0x0113;
        private const int WM_DESTROY = 0x0002;
        private const int WM_ERASEBKGND = 0x0014;

        // ================= 其他 =================
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint LWA_ALPHA = 0x00000002;
        private const int SPI_GETWORKAREA = 0x0030;
        private const int TRANSPARENT = 1;
        private const int DT_LEFT = 0x00000000;
        private const int DT_CENTER = 0x00000001;
        private const int DT_SINGLELINE = 0x00000020;
        private const int DT_VCENTER = 0x00000004;

        private const int WindowWidth = 384;
        private const int WindowHeight = 104;
        private const int ScreenMargin = 16;

        /// <summary>窗口圆角半径（像素）。配合 SetWindowRgn 把窗口切成圆角卡片。</summary>
        private const int CornerRadius = 14;

        private const uint TimerId = 1;
        private const int TimerIntervalMs = 33;   // 30fps

        // ================= 时间轴（秒） =================
        private const float FadeInDuration = 0.28f;
        private const float HoldDuration = 2.90f;
        private const float FadeOutDuration = 0.60f;
        private static readonly float TotalDuration =
            FadeInDuration + HoldDuration + FadeOutDuration;

        // ================= 配色 =================
        // 说明：GDI 的 COLORREF 是 0x00BBGGRR（注意是 BGR，不是 RGB），
        // 注释中标注的是供人阅读的 RGB 值，不可直接照抄。

        /// <summary>卡片底色渐变：上 #0A1018 → 下 #141F33（深空蓝黑）。</summary>
        private const int ColorBgTop = 0x0018100A;
        private const int ColorBgBottom = 0x00331F14;

        /// <summary>青色强调（#2EE6D6），与官网 / 游戏内面板同一套视觉。</summary>
        private const int ColorAccent = 0x00D6E62E;
        /// <summary>强调色的暗调版本，用于描边与轨道（#1B7C86）。</summary>
        private const int ColorAccentDim = 0x00867C1B;

        /// <summary>卡片描边（#1E3A5F 深蓝）。</summary>
        private const int ColorBorder = 0x005F3A1E;

        private const int ColorTextMain = 0x00FFF2E8;        // #E8F2FF
        private const int ColorTextDim = 0x00A88F7A;         // #7A8FA8
        private const int ColorTrackBg = 0x004A2E14;         // 进度条轨道 #142E4A

        /// <summary>盾牌徽标底衬（比卡片底色略亮）。</summary>
        private const int ColorBadgeBg = 0x00281A10;

        /// <summary>渐变色的色带步长（像素）。越小越平滑，但 FillRect 次数越多。</summary>
        private const int GradientBandStep = 4;

        // ================= 布局（全部相对卡片左上角） =================
        private const int PadX = 18;
        private const int BadgeSize = 38;
        private const int BadgeTop = 18;
        private const int TextLeft = PadX + BadgeSize + 14;
        private const int ProgressTop = 78;
        private const int ProgressHeight = 6;

        // ================= 状态 =================
        private static long _startTick;
        private static IntPtr _hwnd = IntPtr.Zero;
        private static bool _started;

        private static float _lastProgress = -1f;
        private static int _lastAlphaByte = -1;

        // ================= GDI 资源（全局复用） =================
        /// <summary>GDI+ 令牌与图标位图。为 0 表示不可用，此时回退到手绘徽标。</summary>
        private static IntPtr _gdiplusToken = IntPtr.Zero;
        private static IntPtr _iconBitmap = IntPtr.Zero;

        private static IntPtr _brushBadgeBg;   // 徽标底衬
        private static IntPtr _brushAccent;    // 青色强调（盾牌 / 进度填充）
        private static IntPtr _brushTrack;     // 进度条轨道
        private static IntPtr _penBorder;      // 卡片描边
        private static IntPtr _penAccentDim;   // 徽标描边
        private static IntPtr _fontTitle;      // 主标题
        private static IntPtr _fontSub;        // 副标题

        // ================= P/Invoke 结构 =================

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // ⚠️ 不要写死 Size。Win32 的 PAINTSTRUCT 尺寸随架构变化：
        //      x86 = 64 字节，x64 = 72 字节（hdc 由 4 变 8 字节，再加 4 字节对齐填充）。
        //    早期版本硬编码 Size=64，在 64 位进程里 BeginPaint 会越界写 8 字节，
        //    踩坏栈上相邻数据 → 随机崩溃、绘制错乱。
        //    现在按字段自然排布：尾部 8 个 int 正好 32 字节，等价于 rgbReserved[32]，
        //    由 Marshal 在两种架构下各自推导出正确尺寸（x86=64 / x64=72）。
        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            // rgbReserved[32]：拆成 8 个 int，保证 blittable 且与架构无关
            private int _reserved0, _reserved1, _reserved2, _reserved3;
            private int _reserved4, _reserved5, _reserved6, _reserved7;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>窗口过程委托必须静态保活，否则被 GC 回收后回调 = 访问违规。</summary>
        private static WndProcDelegate _wndProcKeepAlive;

        // ================= P/Invoke 声明 =================

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr hWnd, uint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hWnd, uint uIDEvent);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, int crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawText(IntPtr hDC, string lpchText, int cchText, ref RECT lprc, uint format);

        // ⚠️ 下面这几个是 GDI 函数，必须在 gdi32.dll 里找。
        // 之前误声明成 user32.dll，导致 SetBkMode 一调用就抛 EntryPointNotFoundException，
        // 异常被 WindowProc 的 catch 吞掉 → 整个绘制流程中断 → 背景有、文字全无。
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern uint SetTextColor(IntPtr hDC, int crColor);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hDC, int mode);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int crColor);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFont(
            int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
            uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
            uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
            string pszFaceName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // ================= 圆角与图形（本次视觉改版新增） =================

        /// <summary>创建一个圆角矩形区域，用于把窗口裁成圆角卡片。</summary>
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse);

        /// <summary>把窗口区域设成指定形状。成功后系统接管该区域，不要再 DeleteObject。</summary>
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        /// <summary>画圆角矩形（用当前画笔画边、当前画刷填充）。</summary>
        [DllImport("gdi32.dll")]
        private static extern bool RoundRect(
            IntPtr hdc, int left, int top, int right, int bottom,
            int width, int height);

        /// <summary>创建画笔（用于描边）。</summary>
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, int crColor);

        /// <summary>画椭圆/圆。</summary>
        [DllImport("gdi32.dll")]
        private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

        // ================= GDI+（只用来显示图标） =================
        //
        // 启动动画窗口是纯 GDI 的（不依赖 WinForms/WPF —— 游戏进程跑在 CoreCLR 上，
        // 机器上通常没装 .NET Desktop Runtime）。但 GDI 本身画不了 PNG，
        // 要显示真实的插件图标只能借 GDI+ 解一次 PNG，再画到同一个 HDC 上。
        // 任何一步失败都回退到手绘盾牌，不影响动画本身。

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        [DllImport("gdiplus.dll")]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

        [DllImport("gdiplus.dll")]
        private static extern void GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);

        [DllImport("gdiplus.dll")]
        private static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDeleteGraphics(IntPtr graphics);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll")]
        private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

        /// <summary>填充多边形（用于盾牌徽标）。</summary>
        [DllImport("gdi32.dll")]
        private static extern bool Polygon(IntPtr hdc, POINT[] lpPoints, int nCount);

        /// <summary>取空画刷（NULL_BRUSH），用于「只描边不填充」。</summary>
        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int i);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const int NULL_BRUSH = 5;
        private const int PS_SOLID = 0;

        // ================= 对外接口 =================

        public static void Show()
        {
            if (_started) return;
            _started = true;

            var thread = new Thread(RunWindow)
            {
                IsBackground = true,
                Name = "ApexCheatEnder-DesktopSplash",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        // ================= 窗口线程 =================

        private static void RunWindow()
        {
            try
            {
                BuildResources();

                var hInstance = GetModuleHandle(null);
                _wndProcKeepAlive = new WndProcDelegate(WindowProc);

                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                    hInstance = hInstance,
                    lpszClassName = "ApexCheatEnderSplash",
                };

                if (RegisterClassEx(ref wc) == 0)
                {
                    AntiCheatRuntime.Log?.LogWarning("[桌面动画] 注册窗口类失败，跳过。");
                    CleanupResources();
                    return;
                }

                var work = new RECT();
                SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0);
                var x = work.Right - WindowWidth - ScreenMargin;
                var y = work.Bottom - WindowHeight - ScreenMargin;

                _hwnd = CreateWindowEx(
                    WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
                    "ApexCheatEnderSplash",
                    "Apex Cheat Ender",
                    WS_POPUP,
                    x, y, WindowWidth, WindowHeight,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    AntiCheatRuntime.Log?.LogWarning("[桌面动画] 创建窗口失败，跳过。");
                    CleanupResources();
                    return;
                }

                // 把窗口裁成圆角卡片。
                // 直角矩形贴在桌面上观感廉价（类似便签），圆角用于表达成品 UI 质感
                // 与「这是个测试窗口」最直观的分界线。
                // 注意：SetWindowRgn 成功后由系统接管该 HRGN，不要再 DeleteObject。
                try
                {
                    var rgn = CreateRoundRectRgn(0, 0, WindowWidth + 1, WindowHeight + 1,
                                                 CornerRadius, CornerRadius);
                    if (rgn != IntPtr.Zero) SetWindowRgn(_hwnd, rgn, true);
                }
                catch { /* 圆角失败不影响功能，退化成直角 */ }

                _startTick = Environment.TickCount64;

                SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                UpdateWindow(_hwnd);

                SetTimer(_hwnd, TimerId, TimerIntervalMs, IntPtr.Zero);

                AntiCheatRuntime.Log?.LogInfo("[桌面动画] 窗口已显示，开始播放。");

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                AntiCheatRuntime.Log?.LogInfo("[桌面动画] 播放结束。");
            }
            catch (Exception ex)
            {
                AntiCheatRuntime.Log?.LogWarning($"[桌面动画] 异常，已跳过：{ex.Message}");
            }
            finally
            {
                CleanupResources();
            }
        }

        /// <summary>
        /// 窗口过程。
        /// ⚠️ 整体包 try-catch：原生回调里抛出的托管异常无法抛回原生栈，
        /// .NET 只能终止进程（游戏闪退且无日志）。
        /// </summary>
        private static IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_ERASEBKGND:
                        return new IntPtr(1);

                    case WM_TIMER:
                        Tick();
                        return IntPtr.Zero;

                    case WM_PAINT:
                        Paint(hWnd);
                        return IntPtr.Zero;

                    case WM_DESTROY:
                        KillTimer(hWnd, TimerId);
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch
            {
                // 桌面动画出任何问题都不允许影响游戏进程
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // ================= 动画推进（按需重绘） =================

        private static void Tick()
        {
            var elapsed = (Environment.TickCount64 - _startTick) / 1000f;

            if (elapsed >= TotalDuration)
            {
                DestroyWindow(_hwnd);
                return;
            }

            // 透明度：变化 ≥2/255 才调 API
            float alpha;
            if (elapsed < FadeInDuration) alpha = elapsed / FadeInDuration;
            else if (elapsed < FadeInDuration + HoldDuration) alpha = 1f;
            else alpha = 1f - (elapsed - FadeInDuration - HoldDuration) / FadeOutDuration;

            alpha = Math.Max(0f, Math.Min(1f, alpha));
            var alphaByte = (byte)(alpha * 255f);

            if (Math.Abs(alphaByte - _lastAlphaByte) >= 2)
            {
                _lastAlphaByte = alphaByte;
                SetLayeredWindowAttributes(_hwnd, 0, alphaByte, LWA_ALPHA);
            }

            // 底部进度条：变化 ≥0.5% 才重绘
            var progress = ProgressAt(elapsed);
            if (Math.Abs(progress - _lastProgress) >= 0.005f)
            {
                _lastProgress = progress;
                InvalidateRect(_hwnd, IntPtr.Zero, false);
            }
        }

        /// <summary>底部细进度条用的推进曲线（纯视觉，无阶段语义）。</summary>
        private static float ProgressAt(float elapsed)
        {
            if (elapsed <= FadeInDuration) return 0f;
            var t = (elapsed - FadeInDuration) / (HoldDuration + FadeOutDuration);
            return Math.Max(0f, Math.Min(1f, t));
        }

        // ================= 绘制 =================

        private static void Paint(IntPtr hWnd)
        {
            var hdc = BeginPaint(hWnd, out var ps);
            try
            {
                if (hdc == IntPtr.Zero) return;

                var elapsed = (Environment.TickCount64 - _startTick) / 1000f;

                // ---- 深空底：竖向渐变（色带法） ----
                for (var y = 0; y < WindowHeight; y += GradientBandStep)
                {
                    var t = y / (float)WindowHeight;
                    var brush = GetGradientBrush(t);

                    var band = new RECT
                    {
                        Left = 0,
                        Top = y,
                        Right = WindowWidth,
                        Bottom = Math.Min(y + GradientBandStep, WindowHeight),
                    };
                    FillRect(hdc, ref band, brush);
                }

                DrawBorder(hdc);
                DrawBadge(hdc);
                DrawTexts(hdc);
                DrawProgress(hdc, ProgressAt(elapsed));
            }
            catch (Exception ex)
            {
                // 单独记录绘制异常：WindowProc 的 catch 会吞掉它，
                // 不写日志的话只能看到「窗口有背景但没文字」这种无头绪的现象。
                AntiCheatRuntime.Log?.LogWarning($"[桌面动画] 绘制异常：{ex.Message}");
            }
            finally
            {
                EndPaint(hWnd, ref ps);
            }
        }

        // ================= 绘制分块 =================

        /// <summary>1px 圆角描边，给卡片一个清晰的边界。</summary>
        private static void DrawBorder(IntPtr hdc)
        {
            var oldPen = SelectObject(hdc, _penBorder);
            var oldBrush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
            RoundRect(hdc, 0, 0, WindowWidth - 1, WindowHeight - 1, CornerRadius, CornerRadius);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);
        }

        /// <summary>
        /// 左侧盾牌徽标。
        /// 用「圆角方块底衬 + 青色盾牌多边形」而不是纯文字，是因为纯文字标志
        /// 缩略尺寸下缺乏辨识度，观感类似错误弹窗。
        /// </summary>
        private static void DrawBadge(IntPtr hdc)
        {
            var bx = PadX;
            var by = BadgeTop;
            var b2 = bx + BadgeSize;
            var b3 = by + BadgeSize;

            // 底衬圆角方块
            var oldBrush = SelectObject(hdc, _brushBadgeBg);
            var oldPen = SelectObject(hdc, _penAccentDim);
            RoundRect(hdc, bx, by, b2, b3, 10, 10);
            SelectObject(hdc, oldPen);
            SelectObject(hdc, oldBrush);

            // 优先显示插件真实图标；取不到再回退到手绘盾牌。
            if (DrawIconInto(hdc, bx + 3, by + 3, BadgeSize - 6)) return;

            // 盾牌多边形：平顶 + 两侧下收 + 底部尖角。
            // 注意别把顶部也做成尖的 —— 那样画出来是颗宝石，不是盾牌。
            var cx = bx + BadgeSize / 2;
            var top = by + 8;
            var bottom = by + BadgeSize - 9;
            const int HalfW = 9;

            var pts = new[]
            {
                new POINT { X = cx - HalfW, Y = top },
                new POINT { X = cx + HalfW, Y = top },
                new POINT { X = cx + HalfW, Y = top + 13 },
                new POINT { X = cx,         Y = bottom },
                new POINT { X = cx - HalfW, Y = top + 13 },
            };

            var ob = SelectObject(hdc, _brushAccent);
            var op = SelectObject(hdc, GetStockObject(NULL_BRUSH));
            Polygon(hdc, pts, pts.Length);
            SelectObject(hdc, op);
            SelectObject(hdc, ob);
        }

        /// <summary>把已加载的图标缩放绘制到指定矩形。成功返回 true。</summary>
        private static bool DrawIconInto(IntPtr hdc, int x, int y, int size)
        {
            if (_iconBitmap == IntPtr.Zero || _gdiplusToken == IntPtr.Zero) return false;

            var g = IntPtr.Zero;
            try
            {
                if (GdipCreateFromHDC(hdc, out g) != 0 || g == IntPtr.Zero) return false;
                GdipSetInterpolationMode(g, 7);   // HighQualityBicubic
                return GdipDrawImageRectI(g, _iconBitmap, x, y, size, size) == 0;
            }
            catch { return false; }
            finally { if (g != IntPtr.Zero) GdipDeleteGraphics(g); }
        }

        /// <summary>
        /// 初始化 GDI+ 并加载内嵌的 Icon.png。
        /// 任何一步失败都静默降级（图标为 0 时 DrawBadge 会走手绘分支）。
        /// </summary>
        private static void LoadIcon()
        {
            try
            {
                var input = new GdiplusStartupInput { GdiplusVersion = 1 };
                if (GdiplusStartup(out _gdiplusToken, ref input, IntPtr.Zero) != 0)
                {
                    _gdiplusToken = IntPtr.Zero;
                    return;
                }

                var path = ExtractIconToTemp();
                if (path == null) return;

                if (GdipCreateBitmapFromFile(path, out var bmp) == 0 && bmp != IntPtr.Zero)
                    _iconBitmap = bmp;
            }
            catch (Exception ex)
            {
                AntiCheatRuntime.Log?.LogWarning("[桌面动画] 图标加载失败，回退到手绘徽标：" + ex.Message);
            }
        }

        /// <summary>
        /// 把内嵌的 Icon.png 落到临时文件。
        /// GDI+ 有从流加载的接口，但那需要自己封 IStream，出错面比「先落盘再读文件」大得多。
        /// </summary>
        private static string ExtractIconToTemp()
        {
            try
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("ApexCheatEnder.Icon.png");
                if (stream == null) return null;

                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ace-splash-icon.png");
                using var file = System.IO.File.Create(path);
                stream.CopyTo(file);
                return path;
            }
            catch { return null; }
        }

        /// <summary>标题与副标题。用字号与颜色拉出层级，不再靠假投影。</summary>
        private static void DrawTexts(IntPtr hdc)
        {
            SetBkMode(hdc, TRANSPARENT);

            SelectObject(hdc, _fontTitle);
            SetTextColor(hdc, ColorTextMain);
            var titleRect = new RECT
            {
                Left = TextLeft,
                Top = BadgeTop + 1,
                Right = WindowWidth - PadX,
                Bottom = BadgeTop + 25,
            };
            DrawText(hdc, "Apex Cheat Ender", -1, ref titleRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);

            SelectObject(hdc, _fontSub);
            SetTextColor(hdc, ColorTextDim);
            var subRect = new RECT
            {
                Left = TextLeft,
                Top = BadgeTop + 24,
                Right = WindowWidth - PadX,
                Bottom = BadgeTop + 44,
            };
            DrawText(hdc, "Among Us 客户端反作弊 · 正在启动防护", -1, ref subRect,
                     DT_LEFT | DT_SINGLELINE | DT_VCENTER);
        }

        /// <summary>内缩的圆角进度条（不再是焊死在底边的 2px 白条）。</summary>
        private static void DrawProgress(IntPtr hdc, float progress)
        {
            var left = PadX;
            var right = WindowWidth - PadX;
            var top = ProgressTop;
            var bottom = ProgressTop + ProgressHeight;

            var ob = SelectObject(hdc, _brushTrack);
            var op = SelectObject(hdc, GetStockObject(NULL_BRUSH));
            RoundRect(hdc, left, top, right, bottom, ProgressHeight, ProgressHeight);

            var w = (int)((right - left) * progress);
            if (w > ProgressHeight)
            {
                SelectObject(hdc, _brushAccent);
                RoundRect(hdc, left, top, left + w, bottom, ProgressHeight, ProgressHeight);
            }

            SelectObject(hdc, op);
            SelectObject(hdc, ob);
        }

        /// <summary>
        /// 按纵向比例取渐变色刷（带缓存）。
        /// 缓存粒度是色带步长，所以整个渐变只需要 WindowHeight/Step 个画刷。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, IntPtr> GradientBrushes
            = new System.Collections.Generic.Dictionary<int, IntPtr>();

        private static IntPtr GetGradientBrush(float t)
        {
            var key = (int)(t * 255f);
            if (GradientBrushes.TryGetValue(key, out var cached)) return cached;

            // 按比例插值 BGR 三个通道（COLORREF 是 0x00BBGGRR）
            var l = ColorBgTop;
            var r = ColorBgBottom;

            var b = (int)((l & 0xFF) + (((r & 0xFF) - (l & 0xFF)) * t));
            var g = (int)(((l >> 8) & 0xFF) + ((((r >> 8) & 0xFF) - ((l >> 8) & 0xFF)) * t));
            var rr = (int)(((l >> 16) & 0xFF) + ((((r >> 16) & 0xFF) - ((l >> 16) & 0xFF)) * t));

            var color = (b & 0xFF) | ((g & 0xFF) << 8) | ((rr & 0xFF) << 16);
            var brush = CreateSolidBrush(color);

            GradientBrushes[key] = brush;
            return brush;
        }

        // ================= GDI 资源 =================

        private static void BuildResources()
        {
            _brushBadgeBg = CreateSolidBrush(ColorBadgeBg);
            _brushAccent = CreateSolidBrush(ColorAccent);
            _brushTrack = CreateSolidBrush(ColorTrackBg);
            _penBorder = CreatePen(PS_SOLID, 1, ColorBorder);
            _penAccentDim = CreatePen(PS_SOLID, 1, ColorAccentDim);

            // DEFAULT_CHARSET(1)：让系统按字体自身支持情况挑字符集。
            // 硬编码 GB2312_CHARSET 会让纯英文字体（Arial Black）匹配异常。
            const uint DEFAULT_CHARSET = 1;
            const uint CLEARTYPE_QUALITY = 5;
            const uint OUT_TT_PRECIS = 4;

            // 主标题：粗体
            _fontTitle = CreateFont(17, 0, 0, 0, 700, 0, 0, 0, DEFAULT_CHARSET,
                OUT_TT_PRECIS, 0, CLEARTYPE_QUALITY, 0, "Microsoft YaHei");

            // 副标题：常规、略小，靠字号和灰度拉层级
            _fontSub = CreateFont(12, 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET,
                OUT_TT_PRECIS, 0, CLEARTYPE_QUALITY, 0, "Microsoft YaHei");

            LoadIcon();

            AntiCheatRuntime.Log?.LogInfo(
                $"[桌面动画] 字体句柄 title={_fontTitle} sub={_fontSub}，图标={(_iconBitmap != IntPtr.Zero ? "已加载" : "未加载")}");
        }

        private static void CleanupResources()
        {
            DeleteObject(_brushBadgeBg);
            DeleteObject(_brushAccent);
            DeleteObject(_brushTrack);
            DeleteObject(_penBorder);
            DeleteObject(_penAccentDim);
            DeleteObject(_fontTitle);
            DeleteObject(_fontSub);

            _brushBadgeBg = _brushAccent = _brushTrack = IntPtr.Zero;
            _penBorder = _penAccentDim = IntPtr.Zero;
            _fontTitle = _fontSub = IntPtr.Zero;

            if (_iconBitmap != IntPtr.Zero) { GdipDisposeImage(_iconBitmap); _iconBitmap = IntPtr.Zero; }
            if (_gdiplusToken != IntPtr.Zero) { GdiplusShutdown(_gdiplusToken); _gdiplusToken = IntPtr.Zero; }

            // 渐变画刷是按需创建的，必须回收 —— GDI 句柄是进程级限额。
            foreach (var brush in GradientBrushes.Values) DeleteObject(brush);
            GradientBrushes.Clear();
        }
    }
}
