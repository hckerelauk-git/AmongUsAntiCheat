using System;
using System.IO;
using System.Reflection;
using ApexCheatEnder.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ApexCheatEnder.UI
{
    internal static class MainMenuArt
    {
        private static readonly string[] ResourceNames =
            { MenuArtSource.FirstResource, MenuArtSource.SecondResource, MenuArtSource.ThirdResource };
        // 进程内只选一次；离开菜单、重新进入和卸载清理不重新抽图。
        private static readonly int SelectedBackground = new System.Random().Next(MenuArtSource.BackgroundCount);
        private static Texture2D _texture;
        private static Transform _menuRoot;
        private static SpriteRenderer _renderer;
        private static Sprite _originalSprite;
        private static SpriteDrawMode _originalDrawMode;
        private static Vector2 _originalSize;
        private static Sprite _appliedSprite;
        private static float _lastAspect;
        private static float _nextCheck;
        private static bool _missingLogged;

        public static void Apply(Transform menuRoot)
        {
            if (menuRoot == null) return;
            if (_menuRoot != menuRoot)
            {
                Restore();
                _menuRoot = menuRoot;
                _nextCheck = 0f;
                _missingLogged = false;
                AntiCheatRuntime.Log?.LogInfo("[主菜单] Start 入口，场景=" + menuRoot.gameObject.scene.name +
                    "，内置图片=" + ResourceNames[SelectedBackground]);
            }
            Tick();
        }

        // 由已有 Canvas 主线程帧入口驱动，不依赖游戏存在 Update。
        public static void Tick()
        {
            if (_menuRoot == null || !_menuRoot.gameObject.activeInHierarchy ||
                _menuRoot.gameObject.scene.handle != SceneManager.GetActiveScene().handle)
            {
                Restore();
                _menuRoot = null;
                return;
            }
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            try
            {
                var background = FindBackgroundRenderer(_menuRoot.gameObject.scene);
                if (background != null && background.sprite != null)
                {
                    if (_renderer != background)
                    {
                        Restore();
                        _renderer = background;
                        _originalSprite = background.sprite;
                        _originalDrawMode = background.drawMode;
                        _originalSize = background.size;
                        AntiCheatRuntime.Log?.LogInfo("[主菜单] 命中原 Background：" + background.name);
                    }
                    var size = _originalDrawMode == SpriteDrawMode.Simple
                        ? new Vector2(_originalSprite.bounds.size.x, _originalSprite.bounds.size.y) : _originalSize;
                    if (size.x <= 0f || size.y <= 0f) return;
                    SetSprite(size, new Vector2(_originalSprite.pivot.x / _originalSprite.rect.width,
                        _originalSprite.pivot.y / _originalSprite.rect.height));
                    _renderer.drawMode = SpriteDrawMode.Simple;
                    return;
                }

                // 日志与参考只证明菜单入口及旧版世界坐标，不能证明当前场景安全的后景排序。
                // 未命中明确 Background 时不创建后景，避免局部坐标、排序层和其他相机覆盖风险。
                Restore();
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    AntiCheatRuntime.Log?.LogWarning("[主菜单] 未找到明确 Background，无法确认安全后景位置，保留原菜单并等待后续检查。");
                }
                return;
            }
            catch (Exception ex)
            {
                Restore();
                AntiCheatRuntime.Log?.LogWarning("[主菜单] 背景应用失败，已恢复：" + ex.Message);
            }
        }

        private static void SetSprite(Vector2 size, Vector2 pivot)
        {
            if (_texture == null)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceNames[SelectedBackground]);
                if (stream == null) throw new InvalidDataException("缺少内嵌背景资源。");
                using var output = new MemoryStream();
                stream.CopyTo(output);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(output.ToArray()))
                {
                    UnityEngine.Object.Destroy(texture);
                    throw new InvalidDataException("内嵌图片解码失败。");
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                _texture = texture;
            }
            var aspect = size.x / size.y;
            if (_appliedSprite == null || Mathf.Abs(aspect - _lastAspect) > 0.0001f ||
                Mathf.Abs(_appliedSprite.bounds.size.x - size.x) > 0.0001f)
            {
                var crop = MenuArtSource.Crop(_texture.width, _texture.height, aspect);
                var previous = _appliedSprite;
                _appliedSprite = Sprite.Create(_texture, new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                    pivot, crop.Width / size.x, 0, SpriteMeshType.FullRect);
                _lastAspect = aspect;
                _renderer.sprite = _appliedSprite;
                if (previous != null) UnityEngine.Object.Destroy(previous);
            }
            _renderer.sprite = _appliedSprite;
        }

        private static SpriteRenderer FindBackgroundRenderer(Scene scene)
        {
            // Background 可以是场景根或 Camera 的子节点，不限定菜单直属路径。
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!root.activeInHierarchy) continue;
                if (root.name == "Background")
                {
                    var renderer = root.GetComponent<SpriteRenderer>();
                    if (renderer != null && renderer.enabled) return renderer;
                }
                if (root.GetComponent<Camera>() == null && root.transform != _menuRoot.root) continue;
                foreach (var renderer in root.GetComponentsInChildren<SpriteRenderer>(true))
                    if (renderer.name == "Background" && renderer.enabled && renderer.gameObject.activeInHierarchy)
                        return renderer;
            }
            return null;
        }

        private static void Restore()
        {
            if (_renderer != null)
            {
                _renderer.sprite = _originalSprite;
                _renderer.drawMode = _originalDrawMode;
                _renderer.size = _originalSize;
            }
            _renderer = null;
            _originalSprite = null;
            if (_appliedSprite != null) UnityEngine.Object.Destroy(_appliedSprite);
            _appliedSprite = null;
        }

        public static void Shutdown()
        {
            Restore();
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _texture = null;
            _menuRoot = null;
            _nextCheck = 0f;
        }
    }
}
