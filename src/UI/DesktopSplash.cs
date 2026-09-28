using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AmongUsAntiCheat.UI
{
    /// <summary>
    /// 桌面右下角启动画面（复刻腾讯 ACE 反作弊启动样式）。
    ///
    /// 视觉参照 ACE 实际启动画面：
    ///   - 蓝色横向渐变底（左深右亮）+ 斜线科技纹理
    ///   - 左侧 ACE 艺术字标志（白色带投影）
    ///   - 右侧主标题「ACE反作弊引擎」
    ///   - 下方白色警示语「感谢使用 ACE，作弊者将被标记」
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

        private const int WindowWidth = 360;
        private const int WindowHeight = 96;
        private const int ScreenMargin = 16;

        private const uint TimerId = 1;
        private const int TimerIntervalMs = 33;   // 30fps

        // ================= 时间轴（秒） =================
        private const float FadeInDuration = 0.28f;
        private const float HoldDuration = 2.90f;
        private const float FadeOutDuration = 0.60f;
        private static readonly float TotalDuration =
            FadeInDuration + HoldDuration + FadeOutDuration;

        // ================= 配色（ACE 蓝） =================
        private const int ColorGradientLeft = 0x00C4550A;    // #0A55C4 深蓝
        private const int ColorGradientRight = 0x00E68B2E;   // #2E8BE6 亮蓝
        private const int ColorTextMain = 0x00FFFFFF;        // 白
        private const int ColorTextShadow = 0x00603208;      // 深蓝投影
        private const int ColorProgressBar = 0x00FFFFFF;     // 白色进度条

        /// <summary>渐变色的色带步长（像素）。越小越平滑，但 FillRect 次数越多。</summary>
        private const int GradientBandStep = 4;

        // ================= 状态 =================
        private static long _startTick;
        private static IntPtr _hwnd = IntPtr.Zero;
        private static bool _started;

        private static float _lastProgress = -1f;
        private static int _lastAlphaByte = -1;

        // ================= GDI 资源（全局复用） =================
        private static IntPtr _brushTextShadow;
        private static IntPtr _brushProgress;
        private static IntPtr _fontLogo;      // ACE 艺术字
        private static IntPtr _fontTitle;     // 主标题
        private static IntPtr _fontWarning;   // 警示语

        // ================= P/Invoke 结构 =================

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            // 尾部 32 字节为系统保留区，用 Size=64 声明总长度即可，
            // 不再用 fixed 数组（避免 Marshal 尺寸推断出错导致 hdc 无效）
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
                    "ACE 反作弊引擎",
                    WS_POPUP,
                    x, y, WindowWidth, WindowHeight,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    AntiCheatRuntime.Log?.LogWarning("[桌面动画] 创建窗口失败，跳过。");
                    CleanupResources();
                    return;
                }

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

                // ---- 蓝色横向渐变底（色带法） ----
                for (var x = 0; x < WindowWidth; x += GradientBandStep)
                {
                    var t = x / (float)WindowWidth;
                    var brush = GetGradientBrush(t);

                    var band = new RECT
                    {
                        Left = x,
                        Top = 0,
                        Right = Math.Min(x + GradientBandStep, WindowWidth),
                        Bottom = WindowHeight,
                    };
                    FillRect(hdc, ref band, brush);
                }

                SetBkMode(hdc, TRANSPARENT);

                // ---- ACE 艺术字标志（白色主体 + 深色投影） ----
                var logoRect = new RECT { Left = 18, Top = 10, Right = 130, Bottom = 50 };

                // 投影：先画深色偏移 2px
                SelectObject(hdc, _fontLogo);
                SetTextColor(hdc, ColorTextShadow);
                var shadowRect = new RECT
                {
                    Left = logoRect.Left + 2,
                    Top = logoRect.Top + 2,
                    Right = logoRect.Right + 2,
                    Bottom = logoRect.Bottom + 2,
                };
                DrawText(hdc, "ACE", -1, ref shadowRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);

                // 主体：白色
                SetTextColor(hdc, ColorTextMain);
                DrawText(hdc, "ACE", -1, ref logoRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);

                // ---- 主标题 ----
                var titleRect = new RECT { Left = 132, Top = 16, Right = WindowWidth - 14, Bottom = 42 };
                SelectObject(hdc, _fontTitle);
                SetTextColor(hdc, ColorTextShadow);
                var titleShadow = new RECT
                {
                    Left = titleRect.Left + 1,
                    Top = titleRect.Top + 1,
                    Right = titleRect.Right + 1,
                    Bottom = titleRect.Bottom + 1,
                };
                DrawText(hdc, "ACE反作弊引擎", -1, ref titleShadow, DT_LEFT | DT_SINGLELINE | DT_VCENTER);

                SetTextColor(hdc, ColorTextMain);
                DrawText(hdc, "ACE反作弊引擎", -1, ref titleRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);

                // ---- 警示语（居中） ----
                var warnRect = new RECT { Left = 16, Top = 58, Right = WindowWidth - 16, Bottom = 80 };
                SelectObject(hdc, _fontWarning);
                SetTextColor(hdc, ColorTextShadow);
                var warnShadow = new RECT
                {
                    Left = warnRect.Left + 1,
                    Top = warnRect.Top + 1,
                    Right = warnRect.Right + 1,
                    Bottom = warnRect.Bottom + 1,
                };
                DrawText(hdc, "感谢使用 ACE，作弊者将被标记", -1, ref warnShadow, DT_CENTER | DT_SINGLELINE | DT_VCENTER);

                SetTextColor(hdc, ColorTextMain);
                DrawText(hdc, "感谢使用 ACE，作弊者将被标记", -1, ref warnRect, DT_CENTER | DT_SINGLELINE | DT_VCENTER);

                // ---- 底部细进度条（加载指示） ----
                var progress = ProgressAt(elapsed);
                if (progress > 0f)
                {
                    var bar = new RECT
                    {
                        Left = 0,
                        Top = WindowHeight - 2,
                        Right = (int)(WindowWidth * progress),
                        Bottom = WindowHeight,
                    };
                    FillRect(hdc, ref bar, _brushProgress);
                }
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

        /// <summary>
        /// 按位置比例取渐变色刷（带缓存）。
        /// 缓存粒度是色带步长，所以整个渐变只需要 WindowWidth/Step 个画刷。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, IntPtr> GradientBrushes
            = new System.Collections.Generic.Dictionary<int, IntPtr>();

        private static IntPtr GetGradientBrush(float t)
        {
            var key = (int)(t * 255f);
            if (GradientBrushes.TryGetValue(key, out var cached)) return cached;

            // 按比例插值 BGR 三个通道
            var l = ColorGradientLeft;
            var r = ColorGradientRight;

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
            _brushTextShadow = CreateSolidBrush(ColorTextShadow);
            _brushProgress = CreateSolidBrush(ColorProgressBar);

            // DEFAULT_CHARSET(1)：让系统按字体自身支持情况挑字符集。
            // 硬编码 GB2312_CHARSET 会让纯英文字体（Arial Black）匹配异常。
            const uint DEFAULT_CHARSET = 1;
            const uint CLEARTYPE_QUALITY = 5;
            const uint OUT_TT_PRECIS = 4;

            // ACE 标志：大号粗斜体，模拟原图的艺术字。
            // 字体名优先 Arial Black，缺失时系统会自动回退到最接近的粗体，仍有字。
            _fontLogo = CreateFont(30, 0, 0, 0, 900, 1, 0, 0, DEFAULT_CHARSET,
                OUT_TT_PRECIS, 0, CLEARTYPE_QUALITY, 0, "Arial Black");

            // 主标题（中文，需要中文字体）
            _fontTitle = CreateFont(17, 0, 0, 0, 700, 0, 0, 0, DEFAULT_CHARSET,
                OUT_TT_PRECIS, 0, CLEARTYPE_QUALITY, 0, "Microsoft YaHei");

            // 警示语（中文）
            _fontWarning = CreateFont(14, 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET,
                OUT_TT_PRECIS, 0, CLEARTYPE_QUALITY, 0, "Microsoft YaHei");

            AntiCheatRuntime.Log?.LogInfo(
                $"[桌面动画] 字体句柄 logo={_fontLogo} title={_fontTitle} warn={_fontWarning}");
        }

        private static void CleanupResources()
        {
            DeleteObject(_brushTextShadow);
            DeleteObject(_brushProgress);
            DeleteObject(_fontLogo);
            DeleteObject(_fontTitle);
            DeleteObject(_fontWarning);

            _brushTextShadow = _brushProgress = IntPtr.Zero;
            _fontLogo = _fontTitle = _fontWarning = IntPtr.Zero;

            foreach (var brush in GradientBrushes.Values) DeleteObject(brush);
            GradientBrushes.Clear();
        }
    }
}
