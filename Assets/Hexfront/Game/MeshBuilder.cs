using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hexfront
{
    /// <summary>
    /// Builds flat-shaded, vertex-coloured meshes out of simple shapes. Every face gets its own
    /// vertices and a single normal, which is what produces the faceted low-poly look.
    /// </summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Vector3> _n = new List<Vector3>();
        readonly List<Color32> _c = new List<Color32>();
        readonly List<int> _t = new List<int>();

        /// <summary>Applied to every shape added (lets us place and rotate parts).</summary>
        public Matrix4x4 Transform = Matrix4x4.identity;

        public int VertexCount => _v.Count;

        public void SetTransform(Vector3 position, float yawDegrees = 0f, float pitchDegrees = 0f, float scale = 1f)
        {
            Transform = Matrix4x4.TRS(position, Quaternion.Euler(pitchDegrees, yawDegrees, 0f), Vector3.one * scale);
        }

        public void ResetTransform() => Transform = Matrix4x4.identity;

        /// <summary>Adds a triangle whose front faces toward <paramref name="outward"/> (so winding can never be wrong).</summary>
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color32 color, Vector3 outward)
        {
            a = Transform.MultiplyPoint3x4(a); b = Transform.MultiplyPoint3x4(b); c = Transform.MultiplyPoint3x4(c);
            outward = Transform.MultiplyVector(outward);
            var normal = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(normal, outward) < 0f)
            {
                var tmp = b; b = c; c = tmp;
                normal = -normal;
            }
            normal.Normalize();
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            _n.Add(normal); _n.Add(normal); _n.Add(normal);
            _c.Add(color); _c.Add(color); _c.Add(color);
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 color, Vector3 outward)
        {
            Tri(a, b, c, color, outward);
            Tri(a, c, d, color, outward);
        }

        /// <summary>Axis-aligned box (before Transform) centred at <paramref name="center"/>.</summary>
        public void Box(Vector3 center, Vector3 size, Color32 color)
        {
            Vector3 h = size * 0.5f;
            Vector3 p(float x, float y, float z) => center + new Vector3(x * h.x, y * h.y, z * h.z);
            Quad(p(-1, 1, -1), p(1, 1, -1), p(1, 1, 1), p(-1, 1, 1), color, Vector3.up);
            Quad(p(-1, -1, -1), p(1, -1, -1), p(1, -1, 1), p(-1, -1, 1), color, Vector3.down);
            Quad(p(-1, -1, 1), p(1, -1, 1), p(1, 1, 1), p(-1, 1, 1), color, Vector3.forward);
            Quad(p(-1, -1, -1), p(1, -1, -1), p(1, 1, -1), p(-1, 1, -1), color, Vector3.back);
            Quad(p(1, -1, -1), p(1, -1, 1), p(1, 1, 1), p(1, 1, -1), color, Vector3.right);
            Quad(p(-1, -1, -1), p(-1, -1, 1), p(-1, 1, 1), p(-1, 1, -1), color, Vector3.left);
        }

        /// <summary>
        /// Upright prism / frustum / cone: an n-sided ring of radius <paramref name="bottomRadius"/> at the
        /// base and <paramref name="topRadius"/> at the top (0 makes a cone).
        /// </summary>
        public void Prism(Vector3 baseCenter, float bottomRadius, float topRadius, float height, int sides, Color32 color,
            float angleOffsetDegrees = 0f, Color32? topColor = null)
        {
            var top = baseCenter + Vector3.up * height;
            Color32 tc = topColor ?? color;
            for (int i = 0; i < sides; i++)
            {
                float a0 = Mathf.Deg2Rad * (360f * i / sides + angleOffsetDegrees);
                float a1 = Mathf.Deg2Rad * (360f * (i + 1) / sides + angleOffsetDegrees);
                var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                var outward = (d0 + d1) * 0.5f + Vector3.up * 0.001f;
                Vector3 b0 = baseCenter + d0 * bottomRadius, b1 = baseCenter + d1 * bottomRadius;
                if (topRadius <= 0.0001f)
                {
                    Tri(b0, b1, top, color, outward);
                }
                else
                {
                    Vector3 t0 = top + d0 * topRadius, t1 = top + d1 * topRadius;
                    Quad(b0, b1, t1, t0, color, outward);
                    Tri(top, t0, t1, tc, Vector3.up);
                }
                if (bottomRadius > 0.0001f) Tri(baseCenter, b0, b1, color, Vector3.down);
            }
        }

        /// <summary>Flat hexagon facing up (used for tile tops and highlights).</summary>
        public void HexTop(Vector3 center, float radius, Color32 color)
        {
            for (int i = 0; i < 6; i++)
                Tri(center, center + HexLayout.Corner(i, radius), center + HexLayout.Corner((i + 1) % 6, radius), color, Vector3.up);
        }

        /// <summary>Hexagonal ring (outline) facing up.</summary>
        public void HexRing(Vector3 center, float outer, float inner, Color32 color)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3 a = HexLayout.Corner(i, 1f), b = HexLayout.Corner((i + 1) % 6, 1f);
                Quad(center + a * inner, center + b * inner, center + b * outer, center + a * outer, color, Vector3.up);
            }
        }

        /// <summary>A flat ribbon on the ground from a to b.</summary>
        public void Ribbon(Vector3 a, Vector3 b, float width, Color32 color)
        {
            var dir = b - a; dir.y = 0;
            if (dir.sqrMagnitude < 0.0001f) return;
            var side = Vector3.Cross(Vector3.up, dir.normalized) * (width * 0.5f);
            Quad(a - side, a + side, b + side, b - side, color, Vector3.up);
        }

        public Mesh ToMesh(string name, bool dynamic = false)
        {
            var mesh = new Mesh { name = name };
            if (_v.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            if (dynamic) mesh.MarkDynamic();
            mesh.SetVertices(_v);
            mesh.SetNormals(_n);
            mesh.SetColors(_c);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Refill an existing mesh (for overlays that change often).</summary>
        public void Apply(Mesh mesh)
        {
            mesh.Clear();
            mesh.indexFormat = _v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_v);
            mesh.SetNormals(_n);
            mesh.SetColors(_c);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateBounds();
        }

        public void Clear()
        {
            _v.Clear(); _n.Clear(); _c.Clear(); _t.Clear();
            Transform = Matrix4x4.identity;
        }
    }
}
