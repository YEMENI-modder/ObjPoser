using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ObjPoser
{
    public static class AppInfo
    {
        public const string Version = "3.2.0";
        public const string Author = "YEMENI-modder";
        public const string Repo = "https://github.com/YEMENI-modder/ObjPoser";
    }

    public class NoRestoreDataException : Exception
    {
        public NoRestoreDataException() : base("This OBJ has no ObjPoser restore data.") { }
    }

    public static class ObjRestore
    {
        // Writes the T-Pose version of a posed OBJ.
        //  1) restore data embedded by ObjPoser (exact), or
        //  2) a reference T-Pose OBJ with the same vertex order (refPath).
        public static string Restore(string posedPath, string outPath, string refPath)
        {
            ObjData p = ObjData.Load(posedPath);
            double[] v, vn = null; string how;
            if (p.RestV != null) { v = p.RestV; vn = p.RestVN; how = "embedded restore data"; }
            else if (refPath != null)
            {
                ObjData r = ObjData.Load(refPath);
                if (r.V.Count != p.V.Count) throw new Exception("The reference has " + r.V.Count + " vertices but the posed file has " + p.V.Count + ". They must be the same model.");
                v = new double[r.V.Count * 3];
                for (int i = 0; i < r.V.Count; i++) { v[i * 3] = r.V[i][0]; v[i * 3 + 1] = r.V[i][1]; v[i * 3 + 2] = r.V[i][2]; }
                if (r.VN.Count == p.VN.Count && r.VN.Count > 0)
                {
                    vn = new double[r.VN.Count * 3];
                    for (int i = 0; i < r.VN.Count; i++) { vn[i * 3] = r.VN[i][0]; vn[i * 3 + 1] = r.VN[i][1]; vn[i * 3 + 2] = r.VN[i][2]; }
                }
                how = "reference T-Pose file";
            }
            else throw new NoRestoreDataException();

            StringBuilder sb = new StringBuilder(); int vc = 0, nc = 0;
            foreach (string line in p.Lines)
            {
                string t = line.Trim();
                if (t.StartsWith(ObjData.MetaPrefix, StringComparison.Ordinal)) continue;
                string[] q = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (q.Length >= 4 && q[0] == "v")
                {
                    sb.Append("v ").Append(F(v[vc * 3])).Append(' ').Append(F(v[vc * 3 + 1])).Append(' ').Append(F(v[vc * 3 + 2]));
                    for (int i = 4; i < q.Length; i++) sb.Append(' ').Append(q[i]);
                    sb.Append('\n'); vc++;
                }
                else if (q.Length >= 4 && q[0] == "vn")
                {
                    if (vn != null) sb.Append("vn ").Append(F(vn[nc * 3])).Append(' ').Append(F(vn[nc * 3 + 1])).Append(' ').Append(F(vn[nc * 3 + 2])).Append('\n');
                    else sb.Append(line).Append('\n');
                    nc++;
                }
                else sb.Append(line).Append('\n');
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            return how;
        }
        static string F(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }
    }

    public static class ObjExporter
    {
        static void WriteChunks(StringBuilder sb, string key, string data)
        {
            for (int i = 0; i < data.Length; i += 3000)
                sb.Append(ObjData.MetaPrefix).Append(key).Append(data.Substring(i, Math.Min(3000, data.Length - i))).Append('\n');
        }

        static string F(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }

        // Writes the posed mesh. Everything except v / vn lines is copied from the source file unchanged.
        public static void Save(string path, ObjData obj, AxisMap map, Skinner sk, PosedResult pr, bool embedRestore, string poseName)
        {
            StringBuilder sb = new StringBuilder();
            if (embedRestore)
            {
                // original T-Pose (file coordinates) so the pose can be undone later with "Restore T-Pose"
                sb.Append(ObjData.MetaPrefix).Append("META ObjPoser ").Append(AppInfo.Version).Append(" pose=").Append((poseName ?? "").Replace('\n', ' ')).Append('\n');
                WriteChunks(sb, "V ", ObjData.EncodeFloats(obj.V));
                if (obj.VN.Count > 0) WriteChunks(sb, "VN ", ObjData.EncodeFloats(obj.VN));
            }
            int vc = 0, nc = 0;
            double[] o = new double[3], n = new double[3], n2 = new double[3];
            foreach (string line in obj.Lines)
            {
                string t = line.Trim();
                if (t.StartsWith(ObjData.MetaPrefix, StringComparison.Ordinal)) continue;   // old restore data
                string[] p = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 && p[0] == "v")
                {
                    map.FromWork(pr.P[vc * 3], pr.P[vc * 3 + 1], pr.P[vc * 3 + 2], o);
                    sb.Append("v ").Append(F(o[0])).Append(' ').Append(F(o[1])).Append(' ').Append(F(o[2]));
                    for (int i = 4; i < p.Length; i++) sb.Append(' ').Append(p[i]);
                    sb.Append('\n');
                    vc++;
                }
                else if (p.Length >= 4 && p[0] == "vn")
                {
                    int src;
                    if (obj.NormalSrc.TryGetValue(nc, out src))
                    {
                        double[] on = obj.VN[nc];
                        map.ToWork(on[0], on[1], on[2], n);
                        sk.SkinNormal(pr, src, n, n2);
                        map.FromWork(n2[0], n2[1], n2[2], o);
                        sb.Append("vn ").Append(F(o[0])).Append(' ').Append(F(o[1])).Append(' ').Append(F(o[2])).Append('\n');
                    }
                    else sb.Append(line).Append('\n');
                    nc++;
                }
                else sb.Append(line).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // Binary glTF 2.0 with skeleton + skin weights. Written in working space (Y up, +Z front).
    // The mesh is the rest (T-Pose) mesh; the chosen pose is stored in the joint rotations.
    public static class GlbExporter
    {
        static string F(double v) { return v.ToString("0.#######", CultureInfo.InvariantCulture); }

        static void Align(MemoryStream ms) { while (ms.Length % 4 != 0) ms.WriteByte(0); }

        public static void Save(string path, ObjData obj, Skinner sk, Pose pose, double groundDy, bool animation)
        {
            int nv = sk.NV;
            // expand to unique (position, uv) pairs
            Dictionary<long, int> map = new Dictionary<long, int>();
            List<int> srcV = new List<int>(); List<int> srcT = new List<int>();
            List<uint> idx = new List<uint>();
            long mult = obj.VT.Count + 1;
            bool hasUV = obj.VT.Count > 0;
            for (int i = 0; i < obj.Tris.Count; i++)
            {
                int v = obj.Tris[i], t = hasUV ? obj.TriVT[i] : -1;
                long key = (long)v * mult + (t + 1);
                int id;
                if (!map.TryGetValue(key, out id)) { id = srcV.Count; map[key] = id; srcV.Add(v); srcT.Add(t); }
                idx.Add((uint)id);
            }
            int n = srcV.Count;

            // smooth normals on rest mesh
            double[] nrm = new double[nv * 3];
            for (int i = 0; i + 2 < obj.Tris.Count; i += 3)
            {
                int a = obj.Tris[i], b = obj.Tris[i + 1], c = obj.Tris[i + 2];
                double ux = sk.Rest[b * 3] - sk.Rest[a * 3], uy = sk.Rest[b * 3 + 1] - sk.Rest[a * 3 + 1], uz = sk.Rest[b * 3 + 2] - sk.Rest[a * 3 + 2];
                double vx = sk.Rest[c * 3] - sk.Rest[a * 3], vy = sk.Rest[c * 3 + 1] - sk.Rest[a * 3 + 1], vz = sk.Rest[c * 3 + 2] - sk.Rest[a * 3 + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                int[] vs = { a, b, c };
                foreach (int q in vs) { nrm[q * 3] += nx; nrm[q * 3 + 1] += ny; nrm[q * 3 + 2] += nz; }
            }

            MemoryStream bin = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(bin);
            StringBuilder views = new StringBuilder(), accs = new StringBuilder();
            int viewCount = 0, accCount = 0;
            Action<long, long, int?> addView = delegate (long off, long len, int? target)
            {
                if (viewCount > 0) views.Append(',');
                views.Append("{\"buffer\":0,\"byteOffset\":").Append(off).Append(",\"byteLength\":").Append(len);
                if (target.HasValue) views.Append(",\"target\":").Append(target.Value);
                views.Append('}'); viewCount++;
            };
            Action<string> addAcc = delegate (string json) { if (accCount > 0) accs.Append(','); accs.Append(json); accCount++; };

            // POSITION
            long off0 = bin.Length;
            double[] mn = { 1e30, 1e30, 1e30 }, mx = { -1e30, -1e30, -1e30 };
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 3; k++)
                {
                    double v = sk.Rest[srcV[i] * 3 + k]; bw.Write((float)v);
                    if (v < mn[k]) mn[k] = v; if (v > mx[k]) mx[k] = v;
                }
            addView(off0, bin.Length - off0, 34962);
            int accPos = accCount;
            addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":" + n + ",\"type\":\"VEC3\",\"min\":[" + F(mn[0]) + "," + F(mn[1]) + "," + F(mn[2]) + "],\"max\":[" + F(mx[0]) + "," + F(mx[1]) + "," + F(mx[2]) + "]}");

            // NORMAL
            off0 = bin.Length;
            for (int i = 0; i < n; i++)
            {
                int v = srcV[i];
                double x = nrm[v * 3], y = nrm[v * 3 + 1], z = nrm[v * 3 + 2];
                double l = Math.Sqrt(x * x + y * y + z * z); if (l < 1e-12) { x = 0; y = 1; z = 0; l = 1; }
                bw.Write((float)(x / l)); bw.Write((float)(y / l)); bw.Write((float)(z / l));
            }
            addView(off0, bin.Length - off0, 34962);
            int accNrm = accCount;
            addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":" + n + ",\"type\":\"VEC3\"}");

            // TEXCOORD_0
            int accUV = -1;
            if (hasUV)
            {
                off0 = bin.Length;
                for (int i = 0; i < n; i++)
                {
                    int t = srcT[i];
                    double u = 0, vv = 0;
                    if (t >= 0) { u = obj.VT[t][0]; vv = 1.0 - obj.VT[t][1]; }
                    bw.Write((float)u); bw.Write((float)vv);
                }
                addView(off0, bin.Length - off0, 34962);
                accUV = accCount;
                addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":" + n + ",\"type\":\"VEC2\"}");
            }

            // JOINTS_0
            off0 = bin.Length;
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 4; k++) bw.Write((ushort)sk.BIdx[srcV[i] * 4 + k]);
            addView(off0, bin.Length - off0, 34962);
            int accJ = accCount;
            addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5123,\"count\":" + n + ",\"type\":\"VEC4\"}");

            // WEIGHTS_0
            off0 = bin.Length;
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 4; k++) bw.Write((float)sk.BW[srcV[i] * 4 + k]);
            addView(off0, bin.Length - off0, 34962);
            int accW = accCount;
            addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":" + n + ",\"type\":\"VEC4\"}");

            // indices
            off0 = bin.Length;
            foreach (uint u in idx) bw.Write(u);
            addView(off0, bin.Length - off0, 34963);
            int accI = accCount;
            addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5125,\"count\":" + idx.Count + ",\"type\":\"SCALAR\"}");

            // inverse bind matrices (column-major)
            off0 = bin.Length;
            for (int j = 0; j < Skeleton.N; j++)
            {
                double[] p = sk.Skel.Pos[j];
                float[] m = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, (float)-p[0], (float)-p[1], (float)-p[2], 1 };
                foreach (float f in m) bw.Write(f);
            }
            addView(off0, bin.Length - off0, null);
            int accIBM = accCount;
            addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":" + Skeleton.N + ",\"type\":\"MAT4\"}");
            bw.Flush(); Align(bin);

            // nodes
            StringBuilder nodes = new StringBuilder();
            for (int j = 0; j < Skeleton.N; j++)
            {
                int par = Skeleton.Parent[j];
                double[] pj = sk.Skel.Pos[j];
                double tx = par < 0 ? pj[0] : pj[0] - sk.Skel.Pos[par][0];
                double ty = (par < 0 ? pj[1] + groundDy : pj[1] - sk.Skel.Pos[par][1]);
                double tz = par < 0 ? pj[2] : pj[2] - sk.Skel.Pos[par][2];
                double[] q = animation ? new double[] { 0, 0, 0, 1 } : Xf.ToQuat(sk.LocalRot(pose, j));
                if (!animation) { tx += pose.Trans[j][0] * sk.Skel.H; ty += pose.Trans[j][1] * sk.Skel.H; tz += pose.Trans[j][2] * sk.Skel.H; }
                List<string> ch = new List<string>();
                for (int c = 0; c < Skeleton.N; c++) if (Skeleton.Parent[c] == j) ch.Add(c.ToString());
                if (j > 0) nodes.Append(',');
                nodes.Append("{\"name\":\"").Append(Skeleton.Names[j].Replace(' ', '_')).Append("\",\"translation\":[").Append(F(tx)).Append(',').Append(F(ty)).Append(',').Append(F(tz))
                     .Append("],\"rotation\":[").Append(F(q[0])).Append(',').Append(F(q[1])).Append(',').Append(F(q[2])).Append(',').Append(F(q[3])).Append(']');
                if (ch.Count > 0) nodes.Append(",\"children\":[").Append(string.Join(",", ch.ToArray())).Append(']');
                nodes.Append('}');
            }
            nodes.Append(",{\"name\":\"Mesh\",\"mesh\":0,\"skin\":0}");
            List<string> jl = new List<string>(); for (int j = 0; j < Skeleton.N; j++) jl.Add(j.ToString());

            // optional animation: rest (T-Pose) -> pose over 1 second
            string animJson = "";
            if (animation)
            {
                StringBuilder samplers = new StringBuilder(), channels = new StringBuilder(); int sc = 0;
                long ofsT = bin.Length;
                bw.Write(0f); bw.Write(1f);
                addView(ofsT, 8, null);
                int accTime = accCount;
                addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":2,\"type\":\"SCALAR\",\"min\":[0],\"max\":[1]}");
                for (int j = 0; j < Skeleton.N; j++)
                {
                    double[] q = Xf.ToQuat(sk.LocalRot(pose, j));
                    bool rot = Math.Abs(q[0]) > 1e-9 || Math.Abs(q[1]) > 1e-9 || Math.Abs(q[2]) > 1e-9 || Math.Abs(q[3] - 1) > 1e-9;
                    bool tr = pose.Trans[j][0] != 0 || pose.Trans[j][1] != 0 || pose.Trans[j][2] != 0 || (j == 0 && groundDy != 0);
                    if (rot)
                    {
                        long o2 = bin.Length; bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(1f);
                        bw.Write((float)q[0]); bw.Write((float)q[1]); bw.Write((float)q[2]); bw.Write((float)q[3]);
                        addView(o2, 32, null);
                        addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":2,\"type\":\"VEC4\"}");
                        if (sc > 0) { samplers.Append(','); channels.Append(','); }
                        samplers.Append("{\"input\":" + accTime + ",\"output\":" + (accCount - 1) + ",\"interpolation\":\"LINEAR\"}");
                        channels.Append("{\"sampler\":" + sc + ",\"target\":{\"node\":" + j + ",\"path\":\"rotation\"}}"); sc++;
                    }
                    if (tr)
                    {
                        int par = Skeleton.Parent[j]; double[] pj = sk.Skel.Pos[j];
                        double bx = par < 0 ? pj[0] : pj[0] - sk.Skel.Pos[par][0], by = par < 0 ? pj[1] : pj[1] - sk.Skel.Pos[par][1], bz = par < 0 ? pj[2] : pj[2] - sk.Skel.Pos[par][2];
                        double ex = bx + pose.Trans[j][0] * sk.Skel.H, ey = by + pose.Trans[j][1] * sk.Skel.H + (j == 0 ? groundDy : 0), ez = bz + pose.Trans[j][2] * sk.Skel.H;
                        long o3 = bin.Length; bw.Write((float)bx); bw.Write((float)by); bw.Write((float)bz); bw.Write((float)ex); bw.Write((float)ey); bw.Write((float)ez);
                        addView(o3, 24, null);
                        addAcc("{\"bufferView\":" + (viewCount - 1) + ",\"componentType\":5126,\"count\":2,\"type\":\"VEC3\"}");
                        if (sc > 0) { samplers.Append(','); channels.Append(','); }
                        samplers.Append("{\"input\":" + accTime + ",\"output\":" + (accCount - 1) + ",\"interpolation\":\"LINEAR\"}");
                        channels.Append("{\"sampler\":" + sc + ",\"target\":{\"node\":" + j + ",\"path\":\"translation\"}}"); sc++;
                    }
                }
                bw.Flush(); Align(bin);
                animJson = "\"animations\":[{\"name\":\"TPose_to_" + pose.Name.Split(' ', '(')[0] + "\",\"samplers\":[" + samplers + "],\"channels\":[" + channels + "]}],";
            }

            StringBuilder attrs = new StringBuilder();
            attrs.Append("\"POSITION\":").Append(accPos).Append(",\"NORMAL\":").Append(accNrm);
            if (accUV >= 0) attrs.Append(",\"TEXCOORD_0\":").Append(accUV);
            attrs.Append(",\"JOINTS_0\":").Append(accJ).Append(",\"WEIGHTS_0\":").Append(accW);

            StringBuilder js = new StringBuilder();
            js.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"ObjPoser\"},\"scene\":0,\"scenes\":[{\"nodes\":[0,").Append(Skeleton.N).Append("]}],");
            js.Append("\"nodes\":[").Append(nodes).Append("],");
            js.Append("\"skins\":[{\"joints\":[").Append(string.Join(",", jl.ToArray())).Append("],\"inverseBindMatrices\":").Append(accIBM).Append(",\"skeleton\":0}],");
            js.Append("\"meshes\":[{\"name\":\"Character\",\"primitives\":[{\"attributes\":{").Append(attrs).Append("},\"indices\":").Append(accI).Append(",\"mode\":4}]}],");
            js.Append(animJson).Append("\"accessors\":[").Append(accs).Append("],\"bufferViews\":[").Append(views).Append("],\"buffers\":[{\"byteLength\":").Append(bin.Length).Append("}]}");

            byte[] jb = Encoding.UTF8.GetBytes(js.ToString());
            int jpad = (4 - jb.Length % 4) % 4;
            byte[] binb = bin.ToArray();
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(0x46546C67); w.Write(2);
                w.Write(12 + 8 + jb.Length + jpad + 8 + binb.Length);
                w.Write(jb.Length + jpad); w.Write(0x4E4F534A); w.Write(jb);
                for (int i = 0; i < jpad; i++) w.Write((byte)0x20);
                w.Write(binb.Length); w.Write(0x004E4942); w.Write(binb);
            }
        }
    }
}
