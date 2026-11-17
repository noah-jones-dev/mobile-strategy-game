using System;
using System.Collections.Generic;

namespace Hexfront.Core
{
    /// <summary>
    /// A hex cell in axial coordinates (Q, R). The board is a rectangle stored in
    /// "odd-r" offset layout (pointy-top hexes, odd rows shifted half a hex right).
    /// All maths here is integer-only so every device computes identical results.
    /// </summary>
    public readonly struct Hex : IEquatable<Hex>
    {
        public readonly int Q;
        public readonly int R;

        public Hex(int q, int r) { Q = q; R = r; }

        /// <summary>The six neighbour directions, in a fixed order used for all tie-breaking.</summary>
        public static readonly Hex[] Directions =
        {
            new Hex(1, 0), new Hex(1, -1), new Hex(0, -1),
            new Hex(-1, 0), new Hex(-1, 1), new Hex(0, 1),
        };

        public Hex Neighbor(int direction) => new Hex(Q + Directions[direction].Q, R + Directions[direction].R);

        public static int Distance(Hex a, Hex b)
        {
            int dq = a.Q - b.Q, dr = a.R - b.R;
            return (Math.Abs(dq) + Math.Abs(dq + dr) + Math.Abs(dr)) / 2;
        }

        public int Col => Q + (R - (R & 1)) / 2;
        public int Row => R;

        public static Hex FromOffset(int col, int row) => new Hex(col - (row - (row & 1)) / 2, row);

        /// <summary>Hexes on a straight line from a to b (inclusive), using integer cube lerp.</summary>
        public static List<Hex> Line(Hex a, Hex b)
        {
            int n = Distance(a, b);
            var result = new List<Hex>(n + 1);
            if (n == 0) { result.Add(a); return result; }
            // Work in cube coords scaled by 2n so rounding is exact integer maths.
            int ax = a.Q, az = a.R, ay = -ax - az;
            int bx = b.Q, bz = b.R, by = -bx - bz;
            for (int i = 0; i <= n; i++)
            {
                // scaled coordinate = a*(n-i) + b*i, true value = scaled / n
                long sx = (long)ax * (n - i) + (long)bx * i;
                long sy = (long)ay * (n - i) + (long)by * i;
                long sz = (long)az * (n - i) + (long)bz * i;
                long rx = RoundDiv(sx, n), ry = RoundDiv(sy, n), rz = RoundDiv(sz, n);
                long dx = Math.Abs(rx * n - sx), dy = Math.Abs(ry * n - sy), dz = Math.Abs(rz * n - sz);
                if (dx > dy && dx > dz) rx = -ry - rz;
                else if (dy > dz) ry = -rx - rz;
                else rz = -rx - ry;
                result.Add(new Hex((int)rx, (int)rz));
            }
            return result;
        }

        /// <summary>Integer round-to-nearest of a/n (n &gt; 0), halves round up. No floating point.</summary>
        static long RoundDiv(long a, long n)
        {
            long num = 2 * a + n, den = 2 * n;
            long q = num / den;
            if (num % den != 0 && num < 0) q--; // floor for negatives
            return q;
        }

        public bool Equals(Hex other) => Q == other.Q && R == other.R;
        public override bool Equals(object obj) => obj is Hex h && Equals(h);
        public override int GetHashCode() => (Q * 73856093) ^ (R * 19349663);
        public static bool operator ==(Hex a, Hex b) => a.Q == b.Q && a.R == b.R;
        public static bool operator !=(Hex a, Hex b) => !(a == b);
        public override string ToString() => "(" + Q + "," + R + ")";
    }

    /// <summary>Small deterministic random generator (xorshift32) plus stateless hashing.</summary>
    public struct Rng
    {
        uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }

        public uint NextUInt()
        {
            uint x = _s;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _s = x;
            return x;
        }

        /// <summary>Uniform integer in [0, maxExclusive).</summary>
        public int Next(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);
        public int Range(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

        /// <summary>
        /// Stateless hash of up to four ints. Used for combat variance so a result depends only on
        /// WHAT is being rolled (seed, round, attacker, target), never on the order rolls happen in.
        /// </summary>
        public static uint Hash(uint a, int b, int c = 0, int d = 0)
        {
            uint h = a * 0x9E3779B1u;
            h = Mix(h ^ (uint)b); h = Mix(h ^ (uint)c); h = Mix(h ^ (uint)d);
            return h;
        }

        static uint Mix(uint h)
        {
            h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
            return h;
        }
    }
}
