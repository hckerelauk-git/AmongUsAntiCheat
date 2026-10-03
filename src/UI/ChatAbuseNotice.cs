using System;
using System.IO;
using System.Reflection;
using System.Collections.Concurrent;
using ApexCheatEnder.Core;
using UnityEngine;

namespace ApexCheatEnder.UI
{
    // 聊天观察只交接文本；解码、创建、显示和销毁均由主线程帧入口执行。
    internal static class ChatAbuseNotice
    {
        internal const string ResourceName = "ApexCheatEnder.ChatAbuseNotice.jpg";
        private static ChatAbuseNoticePolicy _policy = new ChatAbuseNoticePolicy();
        private static readonly ConcurrentQueue<(int PlayerId, string Text)> PendingText = new ConcurrentQueue<(int, string)>();
        private static readonly ChatRepeatNoticePolicy RepeatPolicy = new ChatRepeatNoticePolicy();
        private static GameObject _root;
        private static Texture2D _texture;
        private static Sprite _sprite;
        private static double _hideAt;
        private static double _nextBuildAttempt;
        private static int _buildFailureCount;
        private static bool _retryPending;

        public static void Observe(int playerId, string text)
        {
            if (text != null && text.Length <= 2048 && PendingText.Count < 128) PendingText.Enqueue((playerId, text));
        }

        public static void Tick(Transform canvasRoot)
        {
            var cfg = AntiCheatRuntime.Config;
            var enabled = cfg?.ShowChatAbuseNotice.Value ?? false;
            var inSession = GameBridge.GetLocalPlayer() != null;
            var now = (double)Time.unscaledTime;
            var escape = Input.GetKeyDown(KeyCode.Escape);
            if (!enabled || !inSession || escape)
                _retryPending = false;
            if (!enabled || !inSession || escape || now >= _hideAt)
                Hide();
            var trigger = false;
            if (!inSession || !(cfg?.ShowRepeatedChatNotice.Value ?? false)) RepeatPolicy.Reset();
            while (PendingText.TryDequeue(out var message))
            {
                if (inSession && !escape && canvasRoot != null && cfg != null &&
                    RepeatPolicy.TryNotice(message.PlayerId, message.Text, cfg.ShowRepeatedChatNotice.Value, now, cfg.RepeatedChatThreshold.Value))
                    NotificationPanel.Show("重复聊天提示", "同一玩家短时间重复发送相同消息，仅供本地参考，不计作弊。", AceTheme.Warning, 2f);
                if (enabled && inSession && !escape && canvasRoot != null &&
                    _policy.TryShow(message.Text, cfg.ChatAbuseKeywords.Value, enabled, now))
                    trigger = true;
            }
            if (!trigger && _retryPending && _root == null && now >= _nextBuildAttempt &&
                enabled && inSession && !escape && canvasRoot != null)
                trigger = true;
            if (!trigger) return;
            if (_root == null && now < _nextBuildAttempt)
            {
                _retryPending = true;
                return;
            }
            try
            {
                EnsureBuilt(canvasRoot);
                _root.transform.SetAsLastSibling();
                _root.SetActive(true);
                _hideAt = now + ChatAbuseNoticePolicy.DurationSeconds;
                _buildFailureCount = 0;
                _nextBuildAttempt = 0;
                _retryPending = false;
            }
            catch (Exception ex)
            {
                DestroyBuilt();
                _buildFailureCount = Math.Min(_buildFailureCount + 1, 6);
                var delay = Math.Min(30d, Math.Pow(2d, _buildFailureCount));
                _nextBuildAttempt = now + delay;
                _retryPending = true;
                AntiCheatRuntime.Log?.LogWarning("[疑似骂人提示] 展示失败，将在 " + delay + " 秒后重试，不影响聊天：" + ex.Message);
            }
        }

        private static void EnsureBuilt(Transform parent)
        {
            if (_root != null) return;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null) throw new InvalidDataException("缺少疑似骂人提示图片。");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            _texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!_texture.LoadImage(bytes.ToArray())) throw new InvalidDataException("提示图片解码失败。");
            _texture.wrapMode = TextureWrapMode.Clamp;
            var background = UiBuilder.CreateImage("ChatAbuseNotice", parent, AceTheme.WindowBg);
            _root = background.gameObject;
            _root.SetActive(false);
            UiBuilder.Stretch(background.rectTransform, 0f, 0f, 0f, 0f);
            var image = UiBuilder.CreateImage("NoticeArt", _root.transform, Color.white);
            _sprite = Sprite.Create(_texture, new Rect(0, 0, _texture.width, _texture.height), new Vector2(0.5f, 0.5f));
            image.sprite = _sprite;
            image.preserveAspect = true;
            UiBuilder.Stretch(image.rectTransform, 0f, 0f, 0f, 0f);
            var label = UiBuilder.CreateText("NoticeHint", _root.transform,
                "疑似骂人 · 关键词可能误报 · Esc 关闭 · 可在设置中禁用", UiBuilder.LoadFont(18), 18,
                AceTheme.TextMain, TextAnchor.MiddleCenter);
            label.supportRichText = false;
            UiBuilder.Place(label.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(660f, 40f));
            // 所有 Graphic 均 raycastTarget=false，不添加输入拦截组件，不吞点击。
        }

        private static void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        private static void DestroyBuilt()
        {
            Hide();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _root = null;
            _sprite = null;
            _texture = null;
        }

        public static void Shutdown()
        {
            while (PendingText.TryDequeue(out _)) { }
            DestroyBuilt();
            _hideAt = 0;
            _nextBuildAttempt = 0;
            _buildFailureCount = 0;
            _retryPending = false;
            _policy = new ChatAbuseNoticePolicy();
            RepeatPolicy.Reset();
        }
    }
}
