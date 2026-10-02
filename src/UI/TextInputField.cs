using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// 游戏内文本输入框。
    ///
    /// 为什么要自己造：
    /// uGUI 自带的 InputField 需要 EventSystem 配合才能接收输入，
    /// 而本插件是运行在 Among Us 进程里的动态注册类型，
    /// 往它的 onValueChanged 事件上挂托管委托在 IL2CPP 下不稳定，
    /// 轻则不响应，重则把整个插件的类型注册搞崩。
    ///
    /// 所以改成「自己画键盘、自己做命中测试」：
    ///   - 密钥这类固定格式的东西，用候选字符网格点选其实比打字更快
    ///   - 100% 纯代码，不依赖任何预制体
    ///   - 支持从系统剪贴板粘贴，弥补手打的痛苦
    ///
    /// 输入法的替代方案：按 V 直接把剪贴板内容贴进来，
    /// 绝大多数情况下一键就够了。
    /// </summary>
    internal sealed class TextInputField
    {
        // ---- 外观参数 ----
        private const float FieldHeight = 34f;
        private const float KeySize = 28f;
        private const float KeyGap = 3f;
        private const int KeyColumns = 13;
        private const int MaxVisibleKeys = 64;
        private const int MaxLength = 256;

        // ---- 状态 ----
        private readonly Func<string> _getter;
        private readonly Action<string> _setter;
        private readonly RectTransform _host;

        private RectTransform _fieldRect;
        private Image _fieldBg;
        private Text _display;
        private Text _placeholder;
        private GameObject _panel;
        private RectTransform _panelRect;
        private readonly List<RectTransform> _keys = new List<RectTransform>();
        private readonly List<string> _charset = new List<string>();

        private bool _expanded;
        private bool _hoverField;
        private bool _dirty;

        /// <summary>底部工具条按钮：退格 / 粘贴 / 清空 / 收起。</summary>
        private readonly List<RectTransform> _toolbarButtons = new List<RectTransform>();

        /// <summary>是否用圆点遮住内容（密码用）。</summary>
        private readonly bool _masked;

        public string Placeholder { get; set; } = "点一下开始输入";

        /// <summary>本次会话里改过内容（用于在界面上提示「已保存」）。</summary>
        public bool Dirty => _dirty;

        public TextInputField(
            RectTransform host,
            Func<string> getter,
            Action<string> setter,
            bool masked = true,
            string placeholder = null)
        {
            _host = host;
            _getter = getter;
            _setter = setter;
            _masked = masked;
            if (placeholder != null) Placeholder = placeholder;

            BuildField();
            BuildPanel();
            BuildCharset();
        }

        // ================= 构建 =================

        private void BuildField()
        {
            var node = UiBuilder.CreateNode("TextField", _host);
            _fieldRect = node.GetComponent<RectTransform>();
            UiBuilder.Place(_fieldRect,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(360f, FieldHeight));

            _fieldBg = UiBuilder.CreateImage("FieldBg", node.transform,
                new Color(1f, 1f, 1f, 0.06f));
            UiBuilder.Stretch(_fieldBg.rectTransform, 0f, 0f, 0f, 0f);

            _display = UiBuilder.CreateText("Display", node.transform,
                "", UiBuilder.LoadFont(12), 12, AceTheme.TextMain, TextAnchor.MiddleLeft);
            UiBuilder.Place(_display.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(10f, 0f), new Vector2(318f, FieldHeight));

            _placeholder = UiBuilder.CreateText("Placeholder", node.transform,
                Placeholder, UiBuilder.LoadFont(12), 12, AceTheme.TextDim, TextAnchor.MiddleLeft);
            UiBuilder.Place(_placeholder.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(10f, 0f), new Vector2(318f, FieldHeight));

            Refresh();
        }

        private void BuildPanel()
        {
            _panel = UiBuilder.CreateNode("KeyPanel", _host);
            _panelRect = _panel.GetComponent<RectTransform>();
            UiBuilder.Place(_panelRect,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -(FieldHeight + 8f)),
                new Vector2(430f, 230f));

            var bg = UiBuilder.CreateImage("PanelBg", _panel.transform, AceTheme.PanelBg);
            UiBuilder.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);

            // 底部工具条：退格 / 粘贴 / 清空 / 收起
            var toolbar = UiBuilder.CreateImage("Toolbar", _panel.transform, AceTheme.Track);
            UiBuilder.Place(toolbar.rectTransform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(430f, 28f));
        }

        /// <summary>
        /// 候选字符集。
        ///
        /// 只放 API 密钥真正会出现的字符：
        /// 小写字母、数字、以及 sk- 前缀需要的连字符。
        /// 故意不含大写字母和特殊符号——真实密钥几乎不用，
        /// 放进去只会让网格变大、找字符更费劲。
        /// </summary>
        private void BuildCharset()
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_:/.";

            for (var i = 0; i < alphabet.Length && _charset.Count < MaxVisibleKeys; i++)
                _charset.Add(alphabet[i].ToString());
        }

        private void RebuildKeyGrid()
        {
            // 清掉旧按键
            foreach (var k in _keys)
                if (k != null) UnityEngine.Object.Destroy(k.gameObject);
            _keys.Clear();

            var font = UiBuilder.LoadFont(12);
            var startY = -26f;

            for (var i = 0; i < _charset.Count; i++)
            {
                var ch = _charset[i];
                var col = i % KeyColumns;
                var row = i / KeyColumns;

                var node = UiBuilder.CreateNode($"Key{i}", _panel.transform);
                var rect = node.GetComponent<RectTransform>();
                UiBuilder.Place(rect,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(4f + col * (KeySize + KeyGap), startY - row * (KeySize + KeyGap)),
                    new Vector2(KeySize, KeySize));

                var bg = node.AddComponent<Image>();
                bg.color = new Color(1f, 1f, 1f, 0.07f);
                bg.raycastTarget = false;

                var label = UiBuilder.CreateText($"KeyLabel{i}", node.transform,
                    ch, font, 13, AceTheme.TextMain, TextAnchor.MiddleCenter);
                UiBuilder.Stretch(label.rectTransform, 0f, 0f, 0f, 0f);

                _keys.Add(rect);
            }
        }

        // ================= 文本操作 =================

        private string Current
        {
            get
            {
                try { return _getter?.Invoke() ?? string.Empty; }
                catch { return string.Empty; }
            }
            set
            {
                try
                {
                    _setter?.Invoke(value);
                    _dirty = true;
                }
                catch { }
                Refresh();
            }
        }

        private void Refresh()
        {
            var raw = Current ?? string.Empty;
            var text = _masked ? MaskOf(raw) : raw;

            if (_display != null) _display.text = text;
            if (_placeholder != null)
            {
                var showHint = string.IsNullOrEmpty(raw);
                _placeholder.gameObject.SetActive(showHint);
                if (showHint) _placeholder.text = Placeholder;
            }
        }

        /// <summary>把密钥打码成圆点，只显示前后各几位方便确认。</summary>
        private static string MaskOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (key.Length <= 8) return new string('●', key.Length);

            return key.Substring(0, 4)
                 + new string('●', Mathf.Min(20, key.Length - 8))
                 + key.Substring(key.Length - 4);
        }

        private void Insert(string s)
        {
            if (string.IsNullOrEmpty(s)) return;

            var raw = Current ?? string.Empty;

            // 过滤换行和空白；保留常见供应商密钥使用的 ASCII 字符。
            var sb = new System.Text.StringBuilder(raw.Length + s.Length);
            foreach (var c in s)
            {
                if (c <= 32 || c > 126) continue;
                if (char.IsLetterOrDigit(c) || "-_:/.$".IndexOf(c) >= 0) sb.Append(c);
            }

            var next = sb.ToString();
            if (next.Length > MaxLength) next = next.Substring(0, MaxLength);

            Current = next;
        }

        private void Backspace()
        {
            var raw = Current ?? string.Empty;
            if (raw.Length == 0) return;
            Current = raw.Substring(0, raw.Length - 1);
        }

        private void Clear() => Current = string.Empty;

        private void PasteFromClipboard()
        {
            try
            {
                var clip = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrEmpty(clip)) Insert(clip);
            }
            catch
            {
                // 某些环境下剪贴板不可访问，忽略即可，用户可以手打
            }
        }

        // ================= 每帧 =================

        /// <summary>
        /// 鼠标此刻是否落在输入框或展开的键盘面板上。
        /// 父窗口靠它判断这次点击该不该由输入控件独占，
        /// 否则点键盘上的字母会顺带把底下那行设置也切了。
        /// </summary>
        public bool CapturesMouse
        {
            get
            {
                try
                {
                    var mouse = Input.mousePosition;

                    if (_fieldRect != null &&
                        RectTransformUtility.RectangleContainsScreenPoint(_fieldRect, mouse, null))
                        return true;

                    if (_panel != null && _panel.activeSelf && _panelRect != null &&
                        RectTransformUtility.RectangleContainsScreenPoint(_panelRect, mouse, null))
                        return true;
                }
                catch { }

                return false;
            }
        }

        /// <summary>处理鼠标。必须在父窗口的点击分发之前调用。</summary>
        public void Tick()
        {
            if (_fieldRect == null) return;

            var mouse = Input.mousePosition;
            _hoverField = RectTransformUtility.RectangleContainsScreenPoint(_fieldRect, mouse, null);

            if (_fieldBg != null)
                _fieldBg.color = _hoverField
                    ? new Color(0.18f, 0.61f, 1f, 0.22f)
                    : new Color(1f, 1f, 1f, 0.06f);

            // 面板只在悬停或已展开时显示
            var showPanel = _expanded || _hoverField || CapturesMouse;
            if (_panel != null && _panel.activeSelf != showPanel)
            {
                _panel.SetActive(showPanel);
                if (showPanel && _keys.Count == 0) RebuildKeyGrid();
            }
            if (!showPanel) return;

            // 键盘快捷键：退格 / 粘贴 / 全选清空 / 直接捕获可输入 ASCII 字符。
            if (Input.GetKeyDown(KeyCode.Backspace)) Backspace();
            if (Input.GetKeyDown(KeyCode.V) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                PasteFromClipboard();
            if (Input.GetKeyDown(KeyCode.A) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                Clear();
            if (Input.inputString != null && Input.inputString.Length > 0)
                Insert(Input.inputString);
            if (Input.GetKeyDown(KeyCode.Escape)) _expanded = false;

            if (!Input.GetMouseButtonDown(0)) return;

            // 收起按钮（面板右上角）
            if (_panelRect != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_panelRect, mouse, null) == false)
            {
                // 点到面板外 → 收起
                if (_hoverField == false) _expanded = false;
                return;
            }

            // 点到输入框本身：把面板钉住，鼠标移开也不收，方便连续输入
            if (_hoverField)
            {
                _expanded = true;
                return;
            }

            // 命中字符键
            foreach (var k in _keys)
            {
                if (k == null) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(k, mouse, null)) continue;

                var label = k.GetComponentInChildren<Text>();
                if (label != null) Insert(label.text);
                return;
            }

            // 命中底部工具条
            HandleToolbarClick(mouse);
        }

        private void HandleToolbarClick(Vector3 mouse)
        {
            if (_panelRect == null) return;

            // 屏幕坐标转面板本地坐标：本地原点在面板中心，
            // 所以底部工具条对应的 y 是从 -height/2 起、往上 barH 那一条。
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _panelRect, mouse, null, out var local))
                return;

            var panelW = _panelRect.rect.width;
            var panelH = _panelRect.rect.height;
            const float barH = 26f;

            // local.y 落在面板底部一条里（本地 y 的最小值是 -panelH/2）
            var yFromBottom = local.y + panelH / 2f;
            if (yFromBottom < 0f || yFromBottom > barH) return;

            // local.x 从 -panelW/2 起
            var xFromLeft = local.x + panelW / 2f;
            var idx = Mathf.FloorToInt(xFromLeft / (panelW / 4f));

            switch (idx)
            {
                case 0: Backspace(); break;
                case 1: PasteFromClipboard(); break;
                case 2: Clear(); break;
                case 3: _expanded = false; break;
            }
        }

        /// <summary>当前正在输入的目标串（用于调试）。</summary>
        public string Peek() => Current;
    }
}
