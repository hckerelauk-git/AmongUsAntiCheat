using System;

namespace ApexCheatEnder.Core
{
    /// <summary>
    /// 纯逻辑二维向量。刻意不依赖 UnityEngine.Vector2，
    /// 这样核心算法可以脱离游戏环境单独推理与单元测试。
    /// </summary>
    public readonly struct GameVec2 : IEquatable<GameVec2>
    {
        public readonly float X;
        public readonly float Y;

        public GameVec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static readonly GameVec2 Zero = new GameVec2(0f, 0f);

        public float SqrMagnitude => X * X + Y * Y;
        public float Magnitude => MathF.Sqrt(SqrMagnitude);

        public static GameVec2 operator +(GameVec2 a, GameVec2 b) => new GameVec2(a.X + b.X, a.Y + b.Y);
        public static GameVec2 operator -(GameVec2 a, GameVec2 b) => new GameVec2(a.X - b.X, a.Y - b.Y);

        public static float Distance(GameVec2 a, GameVec2 b) => (a - b).Magnitude;
        public static float SqrDistance(GameVec2 a, GameVec2 b) => (a - b).SqrMagnitude;

        public static GameVec2 Lerp(GameVec2 a, GameVec2 b, float t) =>
            new GameVec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        public bool Equals(GameVec2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is GameVec2 v && Equals(v);
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ Y.GetHashCode());
        public override string ToString() => $"({X:F2}, {Y:F2})";
    }
}
