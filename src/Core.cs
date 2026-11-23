using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ObjPoser
{
    // ---------- Rigid transform (3x3 rotation + translation) ----------
    public class Xf
    {
        public double[] R = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        public double[] T = new double[3];

        public static Xf Mul(Xf a, Xf b)
        {
            Xf r = new Xf();
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += a.R[i * 3 + k] * b.R[k * 3 + j];
                    r.R[i * 3 + j] = s;
                }
                r.T[i] = a.R[i * 3] * b.T[0] + a.R[i * 3 + 1] * b.T[1] + a.R[i * 3 + 2] * b.T[2] + a.T[i];
            }
            return r;
        }

        public void Apply(double x, double y, double z, double[] o)
        {
            o[0] = R[0] * x + R[1] * y + R[2] * z + T[0];
            o[1] = R[3] * x + R[4] * y + R[5] * z + T[1];
            o[2] = R[6] * x + R[7] * y + R[8] * z + T[2];
        }

        public void Rotate(double x, double y, double z, double[] o)
        {
            o[0] = R[0] * x + R[1] * y + R[2] * z;
            o[1] = R[3] * x + R[4] * y + R[5] * z;
            o[2] = R[6] * x + R[7] * y + R[8] * z;
        }

        public static double[] Mul3(double[] a, double[] b)
        {
            double[] r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += a[i * 3 + k] * b[k * 3 + j];
                    r[i * 3 + j] = s;
                }
            return r;
        }

        // R = Rx * Ry * Rz (Z applied first). Angles in degrees.
        public static double[] Euler(double xd, double yd, double zd)
        {
            double x = xd * Math.PI / 180, y = yd * Math.PI / 180, z = zd * Math.PI / 180;
            double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
            double[] rx = { 1, 0, 0, 0, cx, -sx, 0, sx, cx };
            double[] ry = { cy, 0, sy, 0, 1, 0, -sy, 0, cy };
            double[] rz = { cz, -sz, 0, sz, cz, 0, 0, 0, 1 };
            return Mul3(rx, Mul3(ry, rz));
        }

        // rotation matrix -> quaternion (x,y,z,w)
        public static double[] ToQuat(double[] m)
        {
            double tr = m[0] + m[4] + m[8];
            double x, y, z, w;
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1.0) * 2;
                w = 0.25 * s; x = (m[7] - m[5]) / s; y = (m[2] - m[6]) / s; z = (m[3] - m[1]) / s;
            }
            else if (m[0] > m[4] && m[0] > m[8])
            {
                double s = Math.Sqrt(1.0 + m[0] - m[4] - m[8]) * 2;
                w = (m[7] - m[5]) / s; x = 0.25 * s; y = (m[1] + m[3]) / s; z = (m[2] + m[6]) / s;
            }
            else if (m[4] > m[8])
            {
                double s = Math.Sqrt(1.0 + m[4] - m[0] - m[8]) * 2;
                w = (m[2] - m[6]) / s; x = (m[1] + m[3]) / s; y = 0.25 * s; z = (m[5] + m[7]) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m[8] - m[0] - m[4]) * 2;
                w = (m[3] - m[1]) / s; x = (m[2] + m[6]) / s; y = (m[5] + m[7]) / s; z = 0.25 * s;
            }
            return new double[] { x, y, z, w };
        }
    }

    // ---------- Materials ----------
    public class Material
    {
        public string Name;
        public double[] Kd = new double[] { 0.72, 0.74, 0.80 };
        public string MapKd;
    }

    // ---------- OBJ file ----------
    public class ObjData
    {
        public List<string> Lines = new List<string>();
        public List<double[]> V = new List<double[]>();
        public List<double[]> VN = new List<double[]>();
        public List<double[]> VT = new List<double[]>();
        public List<int> Tris = new List<int>();     // 3 per triangle (vertex index)
        public List<int> TriVT = new List<int>();    // 3 per triangle (uv index or -1)
        public List<int> TriMat = new List<int>();   // 1 per triangle (material index or -1)
        public Dictionary<int, int> NormalSrc = new Dictionary<int, int>();
        public List<Material> Materials = new List<Material>();
        public string MtlLib;
        public string Dir = "";

        // ---- restore data written by ObjPoser (original T-Pose vertices / normals, file coordinates) ----
        public const string MetaPrefix = "# OBJPOSER-";
        public double[] RestV, RestVN;     // flat xyz arrays, null when the file has no restore data
        public string MetaInfo;

        public static string EncodeFloats(List<double[]> list)
        {
            byte[] raw = new byte[list.Count * 12];
            using (MemoryStream ms = new MemoryStream(raw))
            using (BinaryWriter bw = new BinaryWriter(ms))
                foreach (double[] v in list) { bw.Write((float)v[0]); bw.Write((float)v[1]); bw.Write((float)v[2]); }
            using (MemoryStream o = new MemoryStream())
            {
                using (DeflateStream z = new DeflateStream(o, CompressionMode.Compress, true)) z.Write(raw, 0, raw.Length);
                return Convert.ToBase64String(o.ToArray());
            }
        }

        static double[] DecodeFloats(string b64, int expectedCount)
        {
            if (b64 == null || b64.Length == 0) return null;
            try
            {
                byte[] z = Convert.FromBase64String(b64);
                using (MemoryStream ms = new MemoryStream(z))
                using (DeflateStream d = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream o = new MemoryStream())
                {
                    d.CopyTo(o); byte[] raw = o.ToArray();
                    if (raw.Length != expectedCount * 12) return null;
                    double[] r = new double[expectedCount * 3];
                    for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToSingle(raw, i * 4);
                    return r;
                }
            }
            catch { return null; }
        }

        // Replace the vertices / normals by the stored original T-Pose. Returns false when the file has no restore data.
        public bool ApplyRestMeta()
        {
            if (RestV == null) return false;
            for (int i = 0; i < V.Count; i++) V[i] = new double[] { RestV[i * 3], RestV[i * 3 + 1], RestV[i * 3 + 2] };
            if (RestVN != null) for (int i = 0; i < VN.Count; i++) VN[i] = new double[] { RestVN[i * 3], RestVN[i * 3 + 1], RestVN[i * 3 + 2] };
            return true;
        }

        static string[] Tok(string line)
        {
            return line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }
        static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }

        public static ObjData Load(string path)
        {
            ObjData o = new ObjData();
            o.Dir = Path.GetDirectoryName(path);
            Dictionary<string, int> matIdx = new Dictionary<string, int>();
            int curMat = -1;
            StringBuilder vMeta = new StringBuilder(), vnMeta = new StringBuilder();
            foreach (string line in File.ReadAllLines(path))
            {
                o.Lines.Add(line);
                string t = line.Trim();
                if (t.Length < 2) continue;
                if (t.StartsWith(MetaPrefix, StringComparison.Ordinal))
                {
                    if (t.StartsWith(MetaPrefix + "VN ", StringComparison.Ordinal)) vnMeta.Append(t.Substring(MetaPrefix.Length + 3).Trim());
                    else if (t.StartsWith(MetaPrefix + "V ", StringComparison.Ordinal)) vMeta.Append(t.Substring(MetaPrefix.Length + 2).Trim());
                    else if (t.StartsWith(MetaPrefix + "META ", StringComparison.Ordinal)) o.MetaInfo = t.Substring(MetaPrefix.Length + 5).Trim();
                    continue;
                }
                string[] p = Tok(t);
                if (p[0] == "v" && p.Length >= 4) o.V.Add(new double[] { D(p[1]), D(p[2]), D(p[3]) });
                else if (p[0] == "vn" && p.Length >= 4) o.VN.Add(new double[] { D(p[1]), D(p[2]), D(p[3]) });
                else if (p[0] == "vt" && p.Length >= 3) o.VT.Add(new double[] { D(p[1]), D(p[2]) });
                else if (p[0] == "mtllib" && p.Length >= 2) o.MtlLib = t.Substring(6).Trim();
                else if (p[0] == "usemtl" && p.Length >= 2)
                {
                    string nm = t.Substring(6).Trim();
                    if (!matIdx.TryGetValue(nm, out curMat))
                    {
                        curMat = o.Materials.Count; matIdx[nm] = curMat;
                        Material m = new Material(); m.Name = nm; o.Materials.Add(m);
                    }
                }
                else if (p[0] == "f" && p.Length >= 4)
                {
                    int n = p.Length - 1;
                    int[] vi = new int[n]; int[] ti = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        string[] c = p[i + 1].Split('/');
                        int v = int.Parse(c[0]);
                        v = v < 0 ? o.V.Count + v : v - 1;
                        vi[i] = v; ti[i] = -1;
                        if (c.Length >= 2 && c[1].Length > 0)
                        {
                            int tx = int.Parse(c[1]);
                            ti[i] = tx < 0 ? o.VT.Count + tx : tx - 1;
                        }
                        if (c.Length >= 3 && c[2].Length > 0)
                        {
                            int ni = int.Parse(c[2]);
                            ni = ni < 0 ? o.VN.Count + ni : ni - 1;
                            if (!o.NormalSrc.ContainsKey(ni)) o.NormalSrc[ni] = v;
                        }
                    }
                    for (int i = 1; i + 1 < n; i++)
                    {
                        o.Tris.Add(vi[0]); o.Tris.Add(vi[i]); o.Tris.Add(vi[i + 1]);
                        o.TriVT.Add(ti[0]); o.TriVT.Add(ti[i]); o.TriVT.Add(ti[i + 1]);
                        o.TriMat.Add(curMat);
                    }
                }
            }
            o.LoadMtl();
            o.RestV = DecodeFloats(vMeta.ToString(), o.V.Count);
            if (o.RestV != null && o.VN.Count > 0) o.RestVN = DecodeFloats(vnMeta.ToString(), o.VN.Count);
            if (o.RestV == null) o.MetaInfo = null;
            return o;
        }

        void LoadMtl()
        {
            if (string.IsNullOrEmpty(MtlLib) || Materials.Count == 0) return;
            try
            {
                string mp = Path.Combine(Dir, MtlLib);
                if (!File.Exists(mp)) return;
                Material cur = null;
                foreach (string line in File.ReadAllLines(mp))
                {
                    string t = line.Trim();
                    if (t.Length < 3) continue;
                    string[] p = Tok(t);
                    if (p[0] == "newmtl") { string nm = t.Substring(6).Trim(); cur = Materials.Find(delegate (Material m) { return m.Name == nm; }); }
                    else if (cur != null && p[0] == "Kd" && p.Length >= 4) cur.Kd = new double[] { D(p[1]), D(p[2]), D(p[3]) };
                    else if (cur != null && p[0] == "map_Kd" && p.Length >= 2) cur.MapKd = t.Substring(6).Trim();
                }
            }
            catch { }
        }
    }

    // ---------- Axis conversion: file space <-> working space (Y up, front +Z, left +X) ----------
    public class AxisMap
    {
        public double[] M = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };   // rows: left, up, front (in file coords)

        public static AxisMap FromOptions(bool zUp, bool flipFront)
        {
            AxisMap a = new AxisMap();
            double[] m = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            if (zUp) m = new double[] { 1, 0, 0, 0, 0, 1, 0, -1, 0 };
            if (flipFront) m = Xf.Mul3(new double[] { -1, 0, 0, 0, 1, 0, 0, 0, -1 }, m);
            a.M = m; return a;
        }

        public void ToWork(double x, double y, double z, double[] o)
        {
            o[0] = M[0] * x + M[1] * y + M[2] * z;
            o[1] = M[3] * x + M[4] * y + M[5] * z;
            o[2] = M[6] * x + M[7] * y + M[8] * z;
        }

        public void FromWork(double x, double y, double z, double[] o)
        {
            o[0] = M[0] * x + M[3] * y + M[6] * z;
            o[1] = M[1] * x + M[4] * y + M[7] * z;
            o[2] = M[2] * x + M[5] * y + M[8] * z;
        }

        static double Asym(List<double[]> V, int axis, double mn, double mx)
        {
            int n = V.Count; double[] q = new double[n];
            for (int i = 0; i < n; i++) q[i] = V[i][axis];
            Array.Sort(q);
            double c = (mn + mx) / 2, s = 0; int h = n / 2;
            for (int i = 0; i < h; i++) s += Math.Abs((q[i] - c) + (q[n - 1 - i] - c));
            double ext = mx - mn; if (ext < 1e-12) ext = 1;
            return s / Math.Max(1, h) / ext;
        }

        // Guess up / front axes of a standing T-pose character.
        public static AxisMap Detect(List<double[]> V, out string info)
        {
            double[] mn = { 1e30, 1e30, 1e30 }, mx = { -1e30, -1e30, -1e30 };
            foreach (double[] v in V) for (int k = 0; k < 3; k++) { if (v[k] < mn[k]) mn[k] = v[k]; if (v[k] > mx[k]) mx[k] = v[k]; }
            double[] e = { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] };
            int depth = 0; for (int k = 1; k < 3; k++) if (e[k] < e[depth]) depth = k;
            int a = (depth + 1) % 3, b = (depth + 2) % 3;
            double sa = Asym(V, a, mn[a], mx[a]), sb = Asym(V, b, mn[b], mx[b]);
            int up, lat;
            if (Math.Abs(sa - sb) < 0.01) { up = a == 1 ? a : (b == 1 ? b : Math.Max(a, b)); lat = up == a ? b : a; }
            else if (sa < sb) { lat = a; up = b; } else { lat = b; up = a; }

            double U = e[up], cl = (mn[lat] + mx[lat]) / 2, cu = (mn[up] + mx[up]) / 2;
            double sum = 0; int cnt = 0;
            foreach (double[] v in V) if (Math.Abs(v[lat] - cl) > 0.28 * U) { sum += v[up]; cnt++; }
            double upSign = (cnt > 5 && sum / cnt < cu) ? -1 : 1;

            double fs = 0, ts = 0; int fc = 0, tc = 0;
            foreach (double[] v in V)
            {
                double t = upSign > 0 ? v[up] - mn[up] : mx[up] - v[up];
                if (t < 0.06 * U) { fs += v[depth]; fc++; }
                if (t > 0.3 * U && t < 0.7 * U && Math.Abs(v[lat] - cl) < 0.15 * U) { ts += v[depth]; tc++; }
            }
            double frontSign = 1;
            if (fc > 0 && tc > 0 && Math.Abs(fs / fc - ts / tc) > 0.005 * U) frontSign = (fs / fc > ts / tc) ? 1 : -1;

            double[] u = new double[3], f = new double[3];
            u[up] = upSign; f[depth] = frontSign;
            double[] l = { u[1] * f[2] - u[2] * f[1], u[2] * f[0] - u[0] * f[2], u[0] * f[1] - u[1] * f[0] };
            AxisMap m = new AxisMap();
            m.M = new double[] { l[0], l[1], l[2], u[0], u[1], u[2], f[0], f[1], f[2] };
            string[] nm = { "X", "Y", "Z" };
            info = "up=" + (upSign > 0 ? "+" : "-") + nm[up] + ", front=" + (frontSign > 0 ? "+" : "-") + nm[depth];
            return m;
        }
    }

    // ---------- Analysis ----------
    public static class Analysis
    {
        // returns list of problems (empty = looks like a T-pose). p = working-space vertices.
        public static List<string> CheckTPose(double[] p, int nv)
        {
            List<string> issues = new List<string>();
            double minX = 1e30, maxX = -1e30, minY = 1e30, maxY = -1e30;
            for (int i = 0; i < nv; i++)
            {
                double x = p[i * 3], y = p[i * 3 + 1];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            double H = maxY - minY, cx = (minX + maxX) / 2;
            if (H < 1e-9) { issues.Add("Mesh has no height."); return issues; }
            double span = (maxX - minX) / H;
            if (span < 0.70) issues.Add("Arm span is only " + (int)(span * 100) + "% of the height (a T-Pose is about 80-110%).");
            double sumY = 0, sumY2 = 0; int n = 0; double sx = 0;
            for (int i = 0; i < nv; i++)
            {
                double x = p[i * 3] - cx; sx += x;
                if (Math.Abs(x) > 0.28 * H) { double fy = (p[i * 3 + 1] - minY) / H; sumY += fy; sumY2 += fy * fy; n++; }
            }
            if (n < Math.Max(8, nv / 400)) issues.Add("No arms were found sticking out sideways.");
            else
            {
                double mean = sumY / n, sd = Math.Sqrt(Math.Max(0, sumY2 / n - mean * mean));
                if (mean < 0.62 || mean > 0.97) issues.Add("The arms are at " + (int)(mean * 100) + "% of the height (expected about 75-90%).");
                if (sd > 0.07) issues.Add("The arms are not horizontal (spread " + (int)(sd * 100) + "% of the height).");
            }
            if (Math.Abs(sx / nv) > 0.06 * H) issues.Add("The mesh is not left/right symmetric.");
            return issues;
        }
    }

}
