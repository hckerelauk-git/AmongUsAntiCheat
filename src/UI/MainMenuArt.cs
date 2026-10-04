using System;
using System.IO;
using System.Reflection;
using ApexCheatEnder.Core;
using UnityEngine;

namespace ApexCheatEnder.UI
{
    /// <summary>
    /// 主菜单背景：每次启动从三张内嵌图里随机选一张铺在主菜单后面。
    ///
    /// ────────────── 为什么改成「自建后景」而不是「替换原背景」 ──────────────
    ///
    /// 旧实现遍历 <c>Scene.GetRootGameObjects()</c> 再逐个
    /// <c>GetComponent/GetComponentsInChildren&lt;SpriteRenderer&gt;</c> 找名为 Background 的对象。
    /// 好友房主的现场日志证明这条路在真实环境里走不通：
    ///
    ///     [主菜单] Start 入口，场景=MainMenu，内置图片=...
    ///     [主菜单] 背景应用失败，已恢复：Method unstripping failed   ← 反复 133 次
    ///     [主菜单] 命中原 Background                                  ← 一次都没有
    ///
    /// 也就是说：补丁挂上了、图片也读到了，卡在「枚举场景根对象 + 泛型组件查询」这一步。
    /// IL2CPP 的 interop 会为这些泛型方法动态补桩，好友那版游戏里补桩失败。
    ///
    /// Amethyst 的做法不同（见反编译 AmethystMainMenuArt）：
    /// 它**不去找原背景**，而是自己新建一个 SpriteRenderer 摆在相机前面。
    /// 这条路不碰场景枚举，也就绕开了补桩失败的调用。
    ///
    /// 本实现沿用这个思路，但两点与 Amethyst 不同：
    ///   1. 不隐藏游戏的 Ambience —— 那会改变原版菜单观感，超出「换背景图」的范围
    ///   2. 位置按相机实时算，而不是写死世界坐标 (0,0,600) ——
    ///      写死坐标只在 Amethyst 自己验证过的那版游戏里成立，换版本就会飘走
    ///
    /// 分层策略：排序层保持 Default、sortingOrder 设成极小值，
    /// 于是它一定落在所有游戏精灵（含菜单里飘来飘去的角色）之后。
    /// 主菜单 UI 是 Screen Space Overlay 画布，永远画在 SpriteRenderer 之上，
    /// 所以不存在盖住按钮和 Logo 的问题。
    /// </summary>
    internal static class MainMenuArt
    {
        private static readonly string[] ResourceNames =
            { MenuArtSource.FirstResource, MenuArtSource.SecondResource, MenuArtSource.ThirdResource };

        /// <summary>进程内只选一次；离开菜单再回来、卸载清理都不重新抽图。</summary>
        private static readonly int SelectedBackground = new System.Random().Next(MenuArtSource.BackgroundCount);

        /// <summary>后景排序值。取负值保证落在所有游戏精灵之后。</summary>
        private const short BackdropSortingOrder = -30000;

        /// <summary>连续失败多少次后彻底放弃。避免像现场那样刷 133 条同样的错误。</summary>
        private const int MaxFailures = 3;

        private static Texture2D _texture;
        private static Transform _menuRoot;
        private static Camera _camera;
        private static GameObject _backdrop;
        private static SpriteRenderer _renderer;
        private static Sprite _appliedSprite;
        private static float _lastAspect;
        private static float _nextCheck;
        private static int _failures;
        private static bool _gaveUp;
        private static bool _cameraMissingLogged;

        public static void Apply(Transform menuRoot)
        {
            if (menuRoot == null) return;
            if (_menuRoot != menuRoot)
            {
                Restore();
                _menuRoot = menuRoot;
                _nextCheck = 0f;
                _cameraMissingLogged = false;
                AntiCheatRuntime.Log?.LogInfo("[主菜单] Start 入口，场景=" + menuRoot.gameObject.scene.name +
                    "，内置图片=" + ResourceNames[SelectedBackground]);
            }
            Tick();
        }

        /// <summary>由已有 Canvas 主线程帧入口驱动，不依赖游戏存在 Update。</summary>
        public static void Tick()
        {
            if (_gaveUp) return;

            // 菜单对象随场景销毁后 Unity 的伪空判断会成立，据此收尾，不需要再查活动场景。
            if (_menuRoot == null || !_menuRoot.gameObject.activeInHierarchy)
            {
                Restore();
                _menuRoot = null;
                return;
            }

            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;

            try
            {
                var camera = Camera.main;
                if (camera == null || !camera.isActiveAndEnabled)
                {
                    if (!_cameraMissingLogged)
                    {
                        _cameraMissingLogged = true;
                        AntiCheatRuntime.Log?.LogWarning("[主菜单] 尚未取到主相机，稍后重试。");
                    }
                    return;
                }

                EnsureBackdrop(camera);

                // 摆到相机正前方：正交相机下距离不影响成像大小，只要落在裁剪面之间即可。
                var camTransform = camera.transform;
                var distance = camera.orthographic
                    ? camera.nearClipPlane + (camera.farClipPlane - camera.nearClipPlane) * 0.5f
                    : Mathf.Max(camera.nearClipPlane + 1f, 60f);

                var transform = _backdrop.transform;
                transform.position = camTransform.position + camTransform.forward * distance;
                transform.rotation = camTransform.rotation;
                transform.localScale = Vector3.one;

                // 按相机可视范围算世界尺寸，再交给 SetSprite 做等比居中裁切（cover）。
                var height = camera.orthographic
                    ? camera.orthographicSize * 2f
                    : 2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var width = height * Mathf.Max(0.01f, camera.aspect);
                SetSprite(new Vector2(width, height));
            }
            catch (Exception ex)
            {
                Restore();
                _failures++;

                // 只在首次和判定放弃时落盘，并带上类型与调用栈 ——
                // 现场只有 ex.Message 一行，根本看不出是哪一步炸的。
                if (_failures == 1 || _failures >= MaxFailures)
                    AntiCheatRuntime.Log?.LogWarning("[主菜单] 背景应用失败（第 " + _failures + " 次）：" +
                        ex.GetType().Name + "：" + ex.Message + "\n" + ex.StackTrace);

                if (_failures >= MaxFailures)
                {
                    _gaveUp = true;
                    AntiCheatRuntime.Log?.LogWarning("[主菜单] 连续失败已达上限，停止重试，菜单保持原样。");
                }
            }
        }

        private static void EnsureBackdrop(Camera camera)
        {
            if (_backdrop != null && _camera == camera) return;

            Restore();
            _camera = camera;

            _backdrop = new GameObject("AceMainMenuBackdrop");

            // 相机不渲染 Default 层时，挑一个它确实渲染的层，否则后景永远不可见。
            var layer = 0;
            if ((camera.cullingMask & 1) == 0)
            {
                for (var i = 1; i < 32; i++)
                {
                    if ((camera.cullingMask & (1 << i)) == 0) continue;
                    layer = i;
                    break;
                }
            }
            _backdrop.layer = layer;

            _renderer = _backdrop.AddComponent<SpriteRenderer>();
            _renderer.sortingOrder = BackdropSortingOrder;

            AntiCheatRuntime.Log?.LogInfo("[主菜单] 已创建独立后景（层 " + layer + "，排序 " + BackdropSortingOrder +
                "），不替换任何原对象。");
        }

        private static void SetSprite(Vector2 size)
        {
            if (size.x <= 0f || size.y <= 0f) return;

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
            if (_appliedSprite == null || Mathf.Abs(aspect - _lastAspect) > 0.0001f)
            {
                var crop = MenuArtSource.Crop(_texture.width, _texture.height, aspect);
                var previous = _appliedSprite;
                _appliedSprite = Sprite.Create(_texture, new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                    new Vector2(0.5f, 0.5f), crop.Width / size.x, 0, SpriteMeshType.FullRect);
                _lastAspect = aspect;
                if (previous != null) UnityEngine.Object.Destroy(previous);
            }

            if (_renderer != null) _renderer.sprite = _appliedSprite;
        }

        /// <summary>销毁自建后景并释放 Sprite；不触碰任何游戏原有对象。</summary>
        private static void Restore()
        {
            if (_backdrop != null) UnityEngine.Object.Destroy(_backdrop);
            _backdrop = null;
            _renderer = null;
            _camera = null;

            if (_appliedSprite != null) UnityEngine.Object.Destroy(_appliedSprite);
            _appliedSprite = null;
            _lastAspect = 0f;
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
