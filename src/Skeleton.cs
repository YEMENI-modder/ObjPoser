using System;
using System.Collections.Generic;

namespace ObjPoser
{
    // Humanoid skeleton: body + face + 5 fingers (3 bones) per hand + 5 toes per foot.
    public class Skeleton
    {
        public static readonly int N;
        public static readonly string[] Names;
        public static readonly int[] Parent;
        public static readonly int[] Mirror;       // index of the opposite-side joint (itself for centre joints)
        public static readonly bool[] HasFrame;    // hand / finger / toe joints use a "palm" frame (see Frame)
        public static readonly string[] Group;
        public static readonly string[] GroupNames = { "Body", "Face", "L Hand & Fingers", "R Hand & Fingers", "L Toes", "R Toes" };
        public static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
        static readonly Dictionary<string, int> idx = new Dictionary<string, int>();

        public static int Idx(string name) { return idx[name]; }

        static Skeleton()
        {
            List<string> n = new List<string>(), p = new List<string>();
            Action<string, string> add = delegate (string name, string par) { n.Add(name); p.Add(par); };
            add("Hips", null); add("Spine", "Hips"); add("Chest", "Spine"); add("Neck", "Chest"); add("Head", "Neck");
            add("Jaw", "Head");
            add("L Eye", "Head"); add("R Eye", "Head"); add("L Eyelid", "Head"); add("R Eyelid", "Head");
            add("L Brow", "Head"); add("R Brow", "Head"); add("Nose", "Head");
            add("L Mouth", "Jaw"); add("R Mouth", "Jaw");
            foreach (string s in new string[] { "L", "R" })
            {
                add(s + " Clavicle", "Chest"); add(s + " UpperArm", s + " Clavicle"); add(s + " ForeArm", s + " UpperArm"); add(s + " Hand", s + " ForeArm");
                foreach (string f in FingerNames)
                    for (int k = 1; k <= 3; k++) add(s + " " + f + k, k == 1 ? s + " Hand" : s + " " + f + (k - 1));
            }
            foreach (string s in new string[] { "L", "R" })
            {
                add(s + " Thigh", "Hips"); add(s + " Shin", s + " Thigh"); add(s + " Foot", s + " Shin");
                for (int k = 1; k <= 5; k++) add(s + " Toe" + k, s + " Foot");
            }
            N = n.Count; Names = n.ToArray(); Parent = new int[N]; Mirror = new int[N]; HasFrame = new bool[N]; Group = new string[N];
            for (int i = 0; i < N; i++) idx[Names[i]] = i;
            for (int i = 0; i < N; i++)
            {
                Parent[i] = p[i] == null ? -1 : idx[p[i]];
                string nm = Names[i], mir = nm;
                if (nm.StartsWith("L ")) mir = "R " + nm.Substring(2); else if (nm.StartsWith("R ")) mir = "L " + nm.Substring(2);
                Mirror[i] = idx[mir];
                bool toe = nm.Contains("Toe"), hand = nm.Contains("Hand"), finger = false;
                foreach (string f in FingerNames) if (nm.Contains(f)) finger = true;
                HasFrame[i] = toe || hand || finger;
                string g = "Body";
                if (nm == "Jaw" || nm.Contains("Eye") || nm.Contains("Brow") || nm == "Nose" || nm.Contains("Mouth")) g = "Face";
                else if (toe) g = nm.StartsWith("L") ? "L Toes" : "R Toes";
                else if (hand || finger) g = nm.StartsWith("L") ? "L Hand & Fingers" : "R Hand & Fingers";
                Group[i] = g;
            }
        }

        public double[][] Pos = new double[N][];
        public double[][] End = new double[N][];
        public bool[] OwnEnd = new bool[N];
        public double[] Radius = new double[N];
        public double[][] Frame = new double[N][];   // 3x3 row-major, columns = canonical axes in world coords (null = identity)
        public double H, MinY;
        public bool PalmsForward;

        static double[] P(double x, double y, double z) { return new double[] { x, y, z }; }
        static double Clamp(double v, double a, double b) { return v < a ? a : (v > b ? b : v); }

        // Canonical joint frame: X = along the bone, Z = curl axis (positive curls toward n), Y = Z x X.
        static double[] MakeFrame(double[] b, double[] n)
        {
            double[] a = { b[1] * n[2] - b[2] * n[1], b[2] * n[0] - b[0] * n[2], b[0] * n[1] - b[1] * n[0] };
            double l = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); a[0] /= l; a[1] /= l; a[2] /= l;
            double[] y = { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
            return new double[] { b[0], y[0], a[0], b[1], y[1], a[1], b[2], y[2], a[2] };
        }

        // p: working-space vertices (T-pose, Y up, facing +Z, left = +X).  handMode: 0 auto, 1 palms down, 2 palms forward
        public static Skeleton Build(double[] p, int nv, int handMode)
        {
            double minX = 1e30, maxX = -1e30, minY = 1e30, maxY = -1e30, minZ = 1e30, maxZ = -1e30;
            for (int i = 0; i < nv; i++)
            {
                double x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            double H = maxY - minY, cx = (minX + maxX) / 2;
            double sz = 0; int nz = 0; double sumY = 0; int na = 0; double ext = 0;
            for (int i = 0; i < nv; i++)
            {
                double x = p[i * 3] - cx, y = p[i * 3 + 1], z = p[i * 3 + 2];
                double fy = (y - minY) / H;
                if (fy > 0.3 && fy < 0.7 && Math.Abs(x) < 0.15 * H) { sz += z; nz++; }
                if (Math.Abs(x) > 0.28 * H) { sumY += y; na++; if (Math.Abs(x) > ext) ext = Math.Abs(x); }
            }
            double cz = nz > 0 ? sz / nz : (minZ + maxZ) / 2;
            double shoulderY = minY + 0.82 * H;
            if (na > 10) { double ay = sumY / na; if (ay > minY + 0.65 * H && ay < minY + 0.95 * H) shoulderY = ay; }
            if (na <= 10) ext = 0.45 * H;

            Skeleton s = new Skeleton(); s.H = H; s.MinY = minY;
            double hipY = minY + 0.50 * H, chestY = shoulderY - 0.10 * H, spineY = (hipY + chestY) / 2;
            double neckY = shoulderY + 0.03 * H, headY = neckY + 0.05 * H;
            if (headY > maxY - 0.03 * H) headY = maxY - 0.03 * H;
            double shX = 0.12 * H, wristX = Math.Max(shX + 0.10 * H, ext - 0.105 * H), elbowX = (shX + wristX) / 2, legX = 0.06 * H;
            double kneeY = minY + 0.28 * H, ankleY = minY + 0.05 * H, toeY = minY + 0.015 * H;
            double hl = Math.Max(0.05 * H, ext - wristX);

            Dictionary<string, double[]> pos = new Dictionary<string, double[]>();
            pos["Hips"] = P(cx, hipY, cz); pos["Spine"] = P(cx, spineY, cz); pos["Chest"] = P(cx, chestY, cz);
            pos["Neck"] = P(cx, neckY, cz); pos["Head"] = P(cx, headY, cz);

            // ---- head / face landmarks from the head vertices ----
            double hx0 = 1e30, hx1 = -1e30, hz0 = 1e30, hz1 = -1e30; int hc = 0;
            for (int i = 0; i < nv; i++)
            {
                double x = p[i * 3] - cx, y = p[i * 3 + 1], z = p[i * 3 + 2];
                if (y > neckY && Math.Abs(x) < 0.12 * H)
                {
                    hc++; if (x + cx < hx0) hx0 = x + cx; if (x + cx > hx1) hx1 = x + cx; if (z < hz0) hz0 = z; if (z > hz1) hz1 = z;
                }
            }
            if (hc < 10) { hx0 = cx - 0.05 * H; hx1 = cx + 0.05 * H; hz0 = cz - 0.06 * H; hz1 = cz + 0.06 * H; }
            double headW = hx1 - hx0, headD = hz1 - hz0, headH = Math.Min(0.13 * H, maxY - neckY), headCz = (hz0 + hz1) / 2;
            Func<double, double> frontZ = delegate (double yy)
            {
                double best = -1e30;
                for (int i = 0; i < nv; i++)
                {
                    double x = p[i * 3] - cx, y = p[i * 3 + 1];
                    if (y > neckY && Math.Abs(y - yy) < 0.02 * H && Math.Abs(x) < 0.12 * headW && p[i * 3 + 2] > best) best = p[i * 3 + 2];
                }
                return best < -1e29 ? hz1 : best;
            };
            double eyeY = maxY - 0.46 * headH, browY = maxY - 0.36 * headH, noseY = maxY - 0.62 * headH, mouthY = maxY - 0.78 * headH, chinY = maxY - headH;
            double eyeZ = frontZ(eyeY) - 0.025 * H, browZ = frontZ(browY) - 0.004 * H, noseZ = frontZ(noseY) - 0.002 * H, mouthZ = frontZ(mouthY) - 0.004 * H;
            pos["Jaw"] = P(cx, maxY - 0.60 * headH, headCz - 0.05 * headD);
            pos["Nose"] = P(cx, noseY, noseZ);
            foreach (string sd in new string[] { "L", "R" })
            {
                double sg = sd == "L" ? 1 : -1;
                pos[sd + " Eye"] = P(cx + sg * 0.18 * headW, eyeY, eyeZ);
                pos[sd + " Eyelid"] = P(cx + sg * 0.18 * headW, eyeY + 0.006 * H, eyeZ + 0.012 * H);
                pos[sd + " Brow"] = P(cx + sg * 0.15 * headW, browY, browZ);
                pos[sd + " Mouth"] = P(cx + sg * 0.14 * headW, mouthY, mouthZ);
            }

            // ---- hands: palm orientation + finger layout ----
            double[] hb = new double[6]; int hcnt = 0; double ymn = 1e30, ymx = -1e30, zmn = 1e30, zmx = -1e30;
            for (int i = 0; i < nv; i++)
            {
                double x = p[i * 3] - cx, y = p[i * 3 + 1], z = p[i * 3 + 2];
                if (x > wristX && Math.Abs(y - shoulderY) < 0.12 * H) { hcnt++; if (y < ymn) ymn = y; if (y > ymx) ymx = y; if (z < zmn) zmn = z; if (z > zmx) zmx = z; }
            }
            double hcy = shoulderY, hcz = cz, ey = 0.03 * H, ez = 0.05 * H;
            if (hcnt >= 8) { hcy = (ymn + ymx) / 2; hcz = (zmn + zmx) / 2; ey = ymx - ymn; ez = zmx - zmn; }
            bool forward = handMode == 2 || (handMode == 0 && hcnt >= 20 && ey > ez * 1.3);
            s.PalmsForward = forward;
            double palmW = Clamp(forward ? ey : ez, 0.03 * H, 0.07 * H);
            double[] rootFrac = { 0.20, 0.56, 0.51, 0.55, 0.64 }, lenFrac = { 0.45, 0.42, 0.47, 0.43, 0.34 };
            double[] lat = { 0.45, 0.22, 0.0, -0.20, -0.40 }, segF = { 0.45, 0.30, 0.25 };
            Dictionary<string, double[]> ends = new Dictionary<string, double[]>();
            foreach (string sd in new string[] { "L", "R" })
            {
                double sg = sd == "L" ? 1 : -1;
                pos[sd + " Clavicle"] = P(cx + sg * 0.03 * H, shoulderY + 0.015 * H, cz);
                pos[sd + " UpperArm"] = P(cx + sg * shX, shoulderY, cz);
                pos[sd + " ForeArm"] = P(cx + sg * elbowX, shoulderY, cz);
                pos[sd + " Hand"] = P(cx + sg * wristX, hcy, hcz);
                for (int f = 0; f < 5; f++)
                {
                    double lo = lat[f] * palmW, cum = rootFrac[f] * hl, fl = lenFrac[f] * hl;
                    for (int k = 1; k <= 3; k++)
                    {
                        double ly = forward ? hcy + lo : hcy, lz = forward ? hcz : hcz + lo;
                        pos[sd + " " + Skeleton.FingerNames[f] + k] = P(cx + sg * (wristX + cum), ly, lz);
                        cum += segF[k - 1] * fl;
                    }
                    double ty = forward ? hcy + lo : hcy, tz = forward ? hcz : hcz + lo;
                    ends[sd + " " + Skeleton.FingerNames[f] + "3"] = P(cx + sg * (wristX + cum), ty, tz);
                }
            }

            // ---- legs, feet, toes ----
            double[] toeLen = { 0.26, 0.24, 0.22, 0.20, 0.17 };
            foreach (string sd in new string[] { "L", "R" })
            {
                double sg = sd == "L" ? 1 : -1;
                double fx0 = 1e30, fx1 = -1e30, fz0 = 1e30, fz1 = -1e30; int fc = 0;
                for (int i = 0; i < nv; i++)
                {
                    double x = p[i * 3] - cx, y = p[i * 3 + 1], z = p[i * 3 + 2];
                    if (sg * x > 0.005 * H && y < minY + 0.09 * H) { fc++; if (p[i * 3] < fx0) fx0 = p[i * 3]; if (p[i * 3] > fx1) fx1 = p[i * 3]; if (z < fz0) fz0 = z; if (z > fz1) fz1 = z; }
                }
                if (fc < 8) { fx0 = cx + sg * 0.03 * H; fx1 = cx + sg * 0.09 * H; if (fx0 > fx1) { double t = fx0; fx0 = fx1; fx1 = t; } fz0 = cz - 0.04 * H; fz1 = cz + 0.10 * H; }
                double footL = Math.Max(0.06 * H, fz1 - fz0), fw = Math.Max(0.03 * H, fx1 - fx0);
                double inner = sg > 0 ? fx0 : fx1, ankleX = (fx0 + fx1) / 2, ankleZ = fz0 + 0.27 * footL, tipZ = fz1 - 0.01 * H;
                pos[sd + " Thigh"] = P(cx + sg * legX, hipY, cz);
                pos[sd + " Shin"] = P(cx + sg * legX, kneeY, cz);
                pos[sd + " Foot"] = P(ankleX, ankleY, ankleZ);
                double ballSum = 0;
                for (int k = 0; k < 5; k++)
                {
                    double tx = inner + sg * (k + 0.5) / 5.0 * fw, tl = toeLen[k] * footL;
                    pos[sd + " Toe" + (k + 1)] = P(tx, toeY, tipZ - tl);
                    ends[sd + " Toe" + (k + 1)] = P(tx, toeY, tipZ);
                    ballSum += tipZ - tl;
                }
                ends[sd + " Foot"] = P(ankleX, toeY, ballSum / 5.0);
            }

            // ---- explicit segment ends for leaf / special joints ----
            ends["Head"] = P(cx, maxY, headCz);
            ends["Jaw"] = P(cx, chinY, frontZ(chinY + 0.01 * H) - 0.01 * H);
            ends["Nose"] = P(cx, noseY - 0.006 * H, noseZ + 0.015 * H);
            foreach (string sd in new string[] { "L", "R" })
            {
                double sg = sd == "L" ? 1 : -1;
                double[] e = pos[sd + " Eye"]; ends[sd + " Eye"] = P(e[0], e[1], e[2] + 0.012 * H);
                double[] l2 = pos[sd + " Eyelid"]; ends[sd + " Eyelid"] = P(l2[0], l2[1] - 0.004 * H, l2[2] + 0.004 * H);
                double[] b = pos[sd + " Brow"]; ends[sd + " Brow"] = P(b[0] + sg * 0.012 * H, b[1] + 0.002 * H, b[2] + 0.004 * H);
                double[] m = pos[sd + " Mouth"]; ends[sd + " Mouth"] = P(m[0] + sg * 0.006 * H, m[1], m[2] + 0.004 * H);
            }

            // ---- fill arrays ----
            double[] palm = forward ? P(0, 0, 1) : P(0, -1, 0);
            for (int j = 0; j < N; j++) s.Pos[j] = pos[Names[j]];
            for (int j = 0; j < N; j++)
            {
                string nm = Names[j];
                double[] en;
                if (ends.TryGetValue(nm, out en)) { s.End[j] = en; s.OwnEnd[j] = true; }
                else
                {
                    int child = -1;
                    if (nm.EndsWith(" Hand")) child = idx[nm.Substring(0, 1) + " Middle1"];
                    else for (int c = 0; c < N; c++) if (Parent[c] == j) { child = c; break; }
                    s.End[j] = child >= 0 ? s.Pos[child] : P(pos[nm][0], pos[nm][1] + 0.02 * H, pos[nm][2]);   // alias: follows the child joint
                }
                double r = 0.03;
                if (nm == "Hips") r = 0.13; else if (nm == "Spine") r = 0.12; else if (nm == "Chest") r = 0.13; else if (nm == "Neck") r = 0.04; else if (nm == "Head") r = 0.08;
                else if (nm == "Jaw") r = 0.05; else if (nm.Contains("Eyelid")) r = 0.009; else if (nm.Contains("Eye")) r = 0.012; else if (nm.Contains("Brow")) r = 0.012;
                else if (nm == "Nose") r = 0.014; else if (nm.Contains("Mouth")) r = 0.012; else if (nm.Contains("Clavicle")) r = 0.05;
                else if (nm.Contains("UpperArm")) r = 0.05; else if (nm.Contains("ForeArm")) r = 0.04; else if (nm.EndsWith("Hand")) r = 0.035;
                else if (nm.Contains("Thumb")) r = 0.013; else if (nm.Contains("Index") || nm.Contains("Middle") || nm.Contains("Ring") || nm.Contains("Pinky")) r = 0.011;
                else if (nm.Contains("Thigh")) r = 0.07; else if (nm.Contains("Shin")) r = 0.05; else if (nm.EndsWith("Foot")) r = 0.04; else if (nm.Contains("Toe")) r = 0.008;
                s.Radius[j] = r * H;
                if (HasFrame[j])
                {
                    double sg = nm.StartsWith("R") ? -1 : 1;
                    s.Frame[j] = nm.Contains("Toe") ? MakeFrame(P(0, 0, 1), P(0, -1, 0)) : MakeFrame(P(sg, 0, 0), palm);
                }
            }
            return s;
        }

        // Move joints by offsets (fractions of body height). Aliased segment ends follow their child joint.
        public void ApplyOffsets(double[][] off)
        {
            if (off == null) return;
            for (int j = 0; j < N; j++)
            {
                if (off[j] == null) continue;
                for (int k = 0; k < 3; k++) { Pos[j][k] += off[j][k] * H; if (OwnEnd[j]) End[j][k] += off[j][k] * H; }
            }
        }
    }
}
