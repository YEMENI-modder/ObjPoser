// ObjPoser extra-pose generator (developer tool, not part of the program).
// Builds the pose JSON files of the "poses" folder: hand / arm angles are solved numerically on a real T-Pose model
// (coordinate descent on Skinner.ComputeG with position / direction targets), written with Pose.ToJson and rendered to PNG.
//   mcs -sdk:4.5 -main:Gen -r:System.Windows.Forms.dll -r:System.Drawing.dll -out:posegen.exe tools/PoseGen.cs src/Core.cs src/Skeleton.cs src/Pose.cs src/Skinner.cs src/Export.cs src/Textures.cs src/UI.cs src/ThirdParty/*.cs
//   xvfb-run -a mono posegen.exe <tpose.obj> <outdir> cover crossed hang inverted injured peek sleep drive scooter
// Never commit the model you test with (game assets); the repository only keeps the resulting JSON + preview PNGs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using ObjPoser;

// Generator for the extra pose JSON files (solves hand/arm targets numerically on the sample model) + preview renderer.
static class Gen
{
    static ObjData o; static Skinner skin; static Skeleton sk; static Viewport vp; static int[] tris; static double[] rest; static int nv;
    static Random rnd = new Random(7);

    // ---------- helpers ----------
    static int I(string n) { return Skeleton.Idx(n); }
    static double[] J(Xf[] G, string j) { return new double[] { G[I(j)].T[0], G[I(j)].T[1], G[I(j)].T[2] }; }
    static double[] Pt(Xf[] G, string j, double x, double y, double z)   // rest point attached to joint j -> world
    {
        int k = I(j); double[] p = sk.Pos[k]; double[] o2 = new double[3];
        G[k].Rotate(x - p[0], y - p[1], z - p[2], o2); return new double[] { o2[0] + G[k].T[0], o2[1] + G[k].T[1], o2[2] + G[k].T[2] };
    }
    static double[] Dir(double[] a, double[] b) { double x = b[0] - a[0], y = b[1] - a[1], z = b[2] - a[2], l = Math.Sqrt(x * x + y * y + z * z) + 1e-12; return new double[] { x / l, y / l, z / l }; }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static double D2(double[] a, double[] b) { double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return x * x + y * y + z * z; }
    static double[] Axis(Xf[] G, string j, double x, double y, double z) { double[] r = new double[3]; G[I(j)].Rotate(x, y, z, r); return r; }
    static double[] PalmN(Xf[] G, string side)   // direction the palm faces
    {
        int k = I(side + " Hand"); double[] F = sk.Frame[k]; double[] r = new double[3];
        G[k].Rotate(F[1], F[4], F[7], r); return r;
    }
    static double[] FingerDir(Xf[] G, string side) { return Dir(J(G, side + " Middle1"), J(G, side + " Middle3")); }
    static double FrontZ(double yc)
    {
        double m = -1e9;
        for (int i = 0; i < nv; i++) { double x = rest[i * 3], y = rest[i * 3 + 1], z = rest[i * 3 + 2]; if (Math.Abs(x) < 2.0 && Math.Abs(y - yc) < 0.6 && z > m) m = z; }
        return m;
    }

    class Var { public int J, Ax; public double Lo, Hi, Init, W = 1; }
    static List<Var> Vars(string joint, double[] lo, double[] hi) { List<Var> l = new List<Var>(); for (int a = 0; a < 3; a++) if (lo[a] < hi[a]) l.Add(new Var { J = I(joint), Ax = a, Lo = lo[a], Hi = hi[a] }); return l; }
    static double[] A(double x, double y, double z) { return new double[] { x, y, z }; }

    static double Solve(Pose p, List<Var> vars, Func<Xf[], double> cost, double reg, int restarts)
    {
        double[] bestVals = null; double bestC = 1e30;
        double[] init = new double[vars.Count];
        for (int i = 0; i < vars.Count; i++) { vars[i].Init = p.Rot[vars[i].J][vars[i].Ax]; init[i] = vars[i].Init; }
        Func<double> f = delegate { double c = cost(skin.ComputeG(p)); for (int i = 0; i < vars.Count; i++) { double d = p.Rot[vars[i].J][vars[i].Ax] - vars[i].Init; c += reg * vars[i].W * d * d; } return c; };
        for (int r = 0; r < restarts; r++)
        {
            for (int i = 0; i < vars.Count; i++) { Var v = vars[i]; p.Rot[v.J][v.Ax] = r == 0 ? init[i] : v.Lo + rnd.NextDouble() * (v.Hi - v.Lo); }
            double best = f();
            for (double step = 24; step >= 0.2; step /= 2)
                for (int it = 0; it < 80; it++)
                {
                    bool imp = false;
                    foreach (Var v in vars) foreach (int sg in new int[] { 1, -1 })
                    {
                        double old = p.Rot[v.J][v.Ax], nvv = Math.Max(v.Lo, Math.Min(v.Hi, old + sg * step)); if (nvv == old) continue;
                        p.Rot[v.J][v.Ax] = nvv; double c = f(); if (c < best - 1e-10) { best = c; imp = true; } else p.Rot[v.J][v.Ax] = old;
                    }
                    if (!imp) break;
                }
            if (best < bestC) { bestC = best; bestVals = new double[vars.Count]; for (int i = 0; i < vars.Count; i++) bestVals[i] = p.Rot[vars[i].J][vars[i].Ax]; }
        }
        for (int i = 0; i < vars.Count; i++) p.Rot[vars[i].J][vars[i].Ax] = Math.Round(bestVals[i]);
        return bestC;
    }

    static void MirrorArmToLeft(Pose p, string[] joints)
    {
        foreach (string j in joints) { string r = "R " + j, l = "L " + j; p.Rot[I(l)] = Pose.MirrorRot(I(r), p.Rot[I(r)]); }
    }
    static void CopyArmMirror(Pose p, string fromSide, string[] joints)
    {
        string toSide = fromSide == "R" ? "L" : "R";
        foreach (string j in joints) { int a = I(fromSide + " " + j), b = I(toSide + " " + j); p.Rot[b] = Pose.MirrorRot(a, p.Rot[a]); }
    }
    static string[] ARM = { "Clavicle", "UpperArm", "ForeArm", "Hand" };

    static List<Var> ArmVars(string side)
    {
        double s = side == "L" ? 1 : -1; List<Var> v = new List<Var>();
        v.AddRange(Vars(side + " Clavicle", A(-45, -15, -30), A(30, 30, 20)));
        v.AddRange(Vars(side + " UpperArm", A(-150, -110, -40), A(60, 110, 110)));   // generous; bounds fixed per pose through Bounds()
        v.AddRange(Vars(side + " ForeArm", A(-20, -150, -20), A(20, 20, 20)));
        v.AddRange(Vars(side + " Hand", A(-60, -60, -60), A(60, 60, 60)));
        return v;
    }

    // ---------- render ----------
    static Bitmap Shot(Pose p, int w, int h, double yaw, double pitch, double ext)
    {
        PosedResult pr = skin.Apply(p);
        vp.Tris = tris; vp.P = pr.P; vp.G = pr.G; vp.Dy = pr.Dy; vp.RecalcBounds();
        return vp.Capture(w, h, yaw, pitch, false, ext);
    }
    static void Render(Pose p, string dir, string file)
    {
        double[] yaws = { 0, -0.7, 1.5708, 3.14159 };
        int W = 360, H = 460; Bitmap sheet = new Bitmap(4 * W, H);
        using (Graphics g = Graphics.FromImage(sheet))
            for (int k = 0; k < 4; k++) using (Bitmap b = Shot(p, W, H, yaws[k], 0.15, sk.H * 0.95)) g.DrawImage(b, k * W, 0);
        sheet.Save(Path.Combine(dir, file + "_sheet.png"), ImageFormat.Png);
    }


    static Bitmap Final(Pose p, string title, int W, int H)
    {
        PosedResult pr = skin.Apply(p);
        double mnx = 1e9, mxx = -1e9, mny = 1e9, mxy = -1e9, mnz = 1e9, mxz = -1e9;
        for (int i = 0; i < pr.P.Length; i += 3) { mnx = Math.Min(mnx, pr.P[i]); mxx = Math.Max(mxx, pr.P[i]); mny = Math.Min(mny, pr.P[i + 1]); mxy = Math.Max(mxy, pr.P[i + 1]); mnz = Math.Min(mnz, pr.P[i + 2]); mxz = Math.Max(mxz, pr.P[i + 2]); }
        double ext = Math.Max(Math.Max(mxx - mnx, mxy - mny), mxz - mnz) * 1.0;
        Bitmap b = new Bitmap(W, H);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(38, 40, 46));
            double[] yaws = { -0.6, 1.5708 };
            int pw = W / 2;
            for (int k = 0; k < 2; k++) using (Bitmap v = Shot(p, pw, H - 44, yaws[k], 0.15, ext)) g.DrawImage(v, k * pw, 44);
            using (Font f = new Font("DejaVu Sans", 17, FontStyle.Bold)) g.DrawString(title, f, Brushes.White, 14, 8);
        }
        return b;
    }

    static Pose Arms0(Pose p) { return p; }

    // ======================================================================
    // POSES
    // ======================================================================
    static Pose SittingCover()
    {
        Pose p = new Pose("Sitting - covering face (two hands)");
        p.Set("L Thigh", -90, 0, -2).Set("R Thigh", -90, 0, 2).Set("L Shin", 92, 0, 0).Set("R Shin", 92, 0, 0)
         .Set("L Foot", -5, 0, 0).Set("R Foot", -5, 0, 0)
         .Set("Spine", 14, 0, 0).Set("Chest", 8, 0, 0).Set("Neck", 6, 0, 0).Set("Head", 16, 0, 0);
        p.Hand("L", "Salute / flat").Hand("R", "Salute / flat");
        // right arm solved, then mirrored to the left
        List<Var> vars = ArmVars("R");
        foreach (Var v in vars) { if (v.J == I("R UpperArm") && v.Ax == 2) { v.Lo = 30; v.Hi = 110; } if (v.J == I("R ForeArm") && v.Ax == 1) { v.Lo = 0; v.Hi = 150; } }
        // start from the existing "covering face" arm angles
        p.Set("R Clavicle", -39, 27, -25).Set("R UpperArm", -27, -52, 85).Set("R ForeArm", -34, -7, 107).Set("R Hand", -5, -38, 32);
        Func<Xf[], double> cost = delegate (Xf[] G)
        {
            double c = 0;
            double[] face = Pt(G, "Head", -0.55, 17.45, 1.75);
            c += 6 * D2(J(G, "R Middle2"), face);
            double[] headUp = Axis(G, "Head", 0, 1, 0), headFwd = Axis(G, "Head", 0, 0, 1);
            double dUp = Dot(FingerDir(G, "R"), headUp); c += 3 * (1 - dUp) * (1 - dUp);
            double dN = Dot(PalmN(G, "R"), A(-headFwd[0], -headFwd[1], -headFwd[2])); c += 3 * (1 - dN) * (1 - dN);
            // elbow resting forward and low (on a table)
            double[] elbow = J(G, "R ForeArm"), want = Pt(G, "Chest", -3.2, 12.4, 3.6);
            c += 0.5 * D2(elbow, want);
            return c;
        };
        double best = Solve(p, vars, cost, 0.0005, 40); Console.WriteLine("cover cost " + best);
        CopyArmMirror(p, "R", ARM);
        return p;
    }


    static Pose ArmsCrossed()
    {
        Pose p = new Pose("Arms crossed");
        p.Set("Hips", 0, 2, 0).Set("Spine", 1, -2, 0).Set("Head", 0, -2, 2)
         .Set("L Thigh", 0, 6, 3).Set("R Thigh", 0, -6, -3).Set("L Foot", 0, 0, -3).Set("R Foot", 0, 0, 3)
         .Hand("L", "Relaxed").Hand("R", "Relaxed");
        double zf = FrontZ(13.6);
        // right forearm lies on top (closer to the camera), left forearm behind it
        List<Var> vr = ArmVars("R");
        foreach (Var v in vr) { if (v.J == I("R UpperArm") && v.Ax == 2) { v.Lo = 20; v.Hi = 110; } if (v.J == I("R ForeArm") && v.Ax == 1) { v.Lo = 0; v.Hi = 150; } }
        p.Set("R UpperArm", -20, 0, 70).Set("R ForeArm", 0, 110, 0);
        Func<Xf[], double> costR = delegate (Xf[] G)
        {
            double c = 0;
            c += 6 * D2(J(G, "R Middle2"), A(2.4, 13.9, zf + 0.9));
            c += 3 * D2(J(G, "R ForeArm"), A(-1.6, 13.0, zf + 1.9));
            return c;
        };
        Console.WriteLine("crossR " + Solve(p, vr, costR, 0.0003, 40));
        List<Var> vl = ArmVars("L");
        foreach (Var v in vl) { if (v.J == I("L UpperArm") && v.Ax == 2) { v.Lo = -110; v.Hi = -20; } if (v.J == I("L ForeArm") && v.Ax == 1) { v.Lo = -150; v.Hi = 0; } }
        p.Set("L UpperArm", -20, 0, -70).Set("L ForeArm", 0, -110, 0);
        Func<Xf[], double> costL = delegate (Xf[] G)
        {
            double c = 0;
            c += 6 * D2(J(G, "L Middle2"), A(-2.4, 14.0, zf + 0.35));
            c += 3 * D2(J(G, "L ForeArm"), A(1.6, 13.2, zf + 1.3));
            return c;
        };
        Console.WriteLine("crossL " + Solve(p, vl, costL, 0.0003, 40));
        return p;
    }

    static Pose HangedCorpse()
    {
        Pose p = new Pose("Hanged corpse (rope)");
        p.LiftH = 0.14;
        p.Set("Hips", 0, 6, 0).Set("Spine", 6, 0, 0).Set("Chest", 4, 0, 0).Set("Neck", 26, 0, 12).Set("Head", 30, 10, 28).Set("Jaw", 16, 0, 0)
         .Set("L Clavicle", 0, 0, -6).Set("R Clavicle", 0, 0, 6)
         .Set("L UpperArm", -8, 0, -82).Set("R UpperArm", -4, 0, 80).Set("L ForeArm", 0, -10, 0).Set("R ForeArm", 0, 12, 0)
         .Set("L Thigh", 4, 6, 3).Set("R Thigh", 2, -5, -2).Set("L Shin", 6, 0, 0).Set("R Shin", 9, 0, 0)
         .Set("L Foot", 50, 0, 0).Set("R Foot", 44, 0, 0).Set("L Toe1", 0, 0, 0)
         .Hand("L", "Relaxed").Hand("R", "Relaxed");
        return p;
    }

    static Pose HangedInverted()
    {
        Pose p = new Pose("Hanged upside down (by the feet)");
        p.LiftH = 0.16;
        p.Set("Hips", 0, 0, 180)
         .Set("Spine", -4, 0, 0).Set("Chest", -4, 0, 0).Set("Neck", -16, 0, 0).Set("Head", -14, 0, 6).Set("Jaw", 10, 0, 0)
         .Set("L UpperArm", 0, 0, 78).Set("R UpperArm", 0, 0, -78).Set("L ForeArm", 0, -14, 0).Set("R ForeArm", 0, 18, 0)
         .Set("L Thigh", 0, 0, 2).Set("R Thigh", 0, 0, -2).Set("L Shin", 6, 0, 0).Set("R Shin", 8, 0, 0)
         .Set("L Foot", 45, 0, 0).Set("R Foot", 45, 0, 0)
         .Hand("L", "Relaxed").Hand("R", "Relaxed");
        return p;
    }


    static Pose Injured()
    {
        Pose p = new Pose("Injured (holding stomach)");
        p.Set("Hips", 4, 4, -4).Set("Spine", 24, 0, -3).Set("Chest", 12, 0, 0).Set("Neck", 8, 0, 0).Set("Head", 14, 0, 8)
         .Set("Jaw", 10, 0, 0).Set("L Brow", 0, 0, 0)
         .Set("L Thigh", 6, 0, 3).Set("L Shin", 6, 0, 0).Set("R Thigh", -16, -6, -3).Set("R Shin", 28, 0, 0).Set("R Foot", 10, 0, 0)
         .Set("L UpperArm", -14, 0, -66).Set("L ForeArm", 0, -26, 0).Set("L Clavicle", 0, 0, -3).Hand("L", "Relaxed");
        p.SetT("L Brow", 0.003, -0.006, 0).SetT("R Brow", -0.003, -0.006, 0);
        p.Hand("R", "Grip");
        double zb = FrontZ(10.4);
        List<Var> vr = ArmVars("R");
        foreach (Var v in vr) { if (v.J == I("R UpperArm") && v.Ax == 2) { v.Lo = 20; v.Hi = 110; } if (v.J == I("R ForeArm") && v.Ax == 1) { v.Lo = 0; v.Hi = 150; } }
        p.Set("R UpperArm", -20, 0, 70).Set("R ForeArm", 0, 90, 0);
        Func<Xf[], double> cost = delegate (Xf[] G)
        {
            double c = 0;
            c += 6 * D2(J(G, "R Middle2"), Pt(G, "Spine", -0.9, 10.5, zb + 0.55));
            c += 0.8 * D2(J(G, "R ForeArm"), Pt(G, "Chest", -3.0, 12.2, FrontZ(12.5) + 0.9));
            return c;
        };
        Console.WriteLine("injured " + Solve(p, vr, cost, 0.0003, 40));
        return p;
    }

    static Pose Peeking()
    {
        Pose p = new Pose("Stealth peeking (around a wall)");
        // body in profile (back against the wall, facing along it), torso leaning out, head turned to the viewer
        p.Set("Hips", 0, 80, 0).Set("Spine", 16, -30, 6).Set("Chest", 8, -18, 4).Set("Neck", 6, -20, 10).Set("Head", 0, -24, 14)
         .Set("L Thigh", -40, 4, 4).Set("L Shin", 64, 0, 0).Set("L Foot", -12, 0, 0)
         .Set("R Thigh", -12, -4, -3).Set("R Shin", 44, 0, 0).Set("R Foot", -6, 0, 0)
         .Hand("L", "Relaxed").Hand("R", "Salute / flat");
        p.SetT("L Brow", 0.002, -0.004, 0).SetT("R Brow", -0.002, -0.004, 0);
        List<Var> vr = ArmVars("R");
        foreach (Var v in vr) { if (v.J == I("R UpperArm") && v.Ax == 2) { v.Lo = 10; v.Hi = 110; } if (v.J == I("R ForeArm") && v.Ax == 1) { v.Lo = 0; v.Hi = 150; } }
        p.Set("R UpperArm", -50, 0, 60).Set("R ForeArm", 0, 40, 0);
        Func<Xf[], double> costR = delegate (Xf[] G)
        {
            double[] c0 = J(G, "Chest"), f = Axis(G, "Hips", 0, 0, 1);
            double[] edge = A(c0[0] + f[0] * 4.2, c0[1] + 1.9, c0[2] + f[2] * 4.2 + 0.7);
            double c = 6 * D2(J(G, "R Middle2"), edge);
            double[] palm = PalmN(G, "R"); double[] want = A(f[0], 0, f[2]);
            double d = Dot(palm, want); c += 3 * (1 - d) * (1 - d);
            double[] fd = FingerDir(G, "R"); double du = Dot(fd, A(0, 1, 0)); c += 2 * (1 - du) * (1 - du);   // fingers point up along the wall edge
            return c;
        };
        Console.WriteLine("peek R " + Solve(p, vr, costR, 0.0003, 50));
        // left arm relaxed, slightly bent, hand near the hip (holding on to the wall side / ready position)
        p.Set("L UpperArm", 6, 0, -62).Set("L ForeArm", 0, -34, 0).Set("L Clavicle", 0, 0, -3);
        return p;
    }

    static Pose Sleeping()
    {
        Pose p = new Pose("Sleeping (on the side)");
        p.Set("Hips", 0, 0, 90).Set("Spine", 16, 0, 0).Set("Chest", 8, 0, 0).Set("Neck", 6, 0, 0).Set("Head", 6, 0, 0)
         .Set("L Thigh", -62, 0, 4).Set("R Thigh", -80, 0, -2).Set("L Shin", 98, 0, 0).Set("R Shin", 104, 0, 0).Set("L Foot", 10, 0, 0).Set("R Foot", 10, 0, 0)
         .Hand("L", "Relaxed").Hand("R", "Relaxed");
        p.SetT("L Eyelid", 0, -0.009, 0).SetT("R Eyelid", 0, -0.009, 0);
        // bottom (right) arm folded under the head, top (left) arm resting in front of the chest / face
        List<Var> vr = ArmVars("R");
        foreach (Var v in vr) { if (v.J == I("R UpperArm") && v.Ax == 2) { v.Lo = -140; v.Hi = 0; } if (v.J == I("R ForeArm") && v.Ax == 1) { v.Lo = 0; v.Hi = 150; } }
        p.Set("R UpperArm", 0, 0, -60).Set("R ForeArm", 0, 80, 0);
        Func<Xf[], double> costR = delegate (Xf[] G)
        {
            double c = 5 * D2(J(G, "R Middle2"), Pt(G, "Head", -1.4, 16.3, 0.7));
            c += 0.6 * D2(J(G, "R ForeArm"), Pt(G, "Chest", -3.4, 14.6, 1.8));
            return c;
        };
        Console.WriteLine("sleep R " + Solve(p, vr, costR, 0.0003, 40));
        List<Var> vl = ArmVars("L");
        foreach (Var v in vl) { if (v.J == I("L UpperArm") && v.Ax == 2) { v.Lo = -110; v.Hi = -10; } if (v.J == I("L ForeArm") && v.Ax == 1) { v.Lo = -150; v.Hi = 0; } }
        p.Set("L UpperArm", -60, 0, -30).Set("L ForeArm", 0, -100, 0);
        Func<Xf[], double> costL = delegate (Xf[] G) { return 5 * D2(J(G, "L Middle2"), Pt(G, "Head", 0.9, 15.4, 2.6)) + 1.5 * D2(J(G, "L ForeArm"), Pt(G, "Chest", 1.3, 13.6, FrontZ(13.4) + 2.4)); };
        Console.WriteLine("sleep L " + Solve(p, vl, costL, 0.0003, 30));
        return p;
    }

    static Pose Driving()
    {
        Pose p = new Pose("Driving a car");
        p.Set("Spine", -3, 0, 0).Set("Chest", -2, 0, 0).Set("Head", 2, 0, 0)
         .Set("L Thigh", -76, 0, 3).Set("R Thigh", -72, 0, -3).Set("L Shin", 82, 0, 0).Set("R Shin", 70, 0, 0)
         .Set("L Foot", 2, 0, 0).Set("R Foot", 16, 0, 0)
         .Hand("L", "Grip").Hand("R", "Grip");
        double zf = FrontZ(13.6);
        List<Var> vr = ArmVars("R");
        foreach (Var v in vr) { if (v.J == I("R UpperArm") && v.Ax == 2) { v.Lo = 10; v.Hi = 110; } if (v.J == I("R ForeArm") && v.Ax == 1) { v.Lo = 0; v.Hi = 150; } }
        p.Set("R UpperArm", -50, 0, 60).Set("R ForeArm", 0, 50, 0);
        Func<Xf[], double> cost = delegate (Xf[] G)
        {
            double c = 6 * D2(J(G, "R Middle2"), Pt(G, "Chest", -2.1, 14.5, zf + 4.9));
            c += 0.8 * D2(J(G, "R ForeArm"), Pt(G, "Chest", -3.1, 12.3, zf + 1.6));
            double[] palm = PalmN(G, "R"); double d = Dot(palm, A(0.5, -0.5, 0.2)); c += 0.5 * (1 - d) * (1 - d);
            return c;
        };
        Console.WriteLine("drive " + Solve(p, vr, cost, 0.0003, 40));
        CopyArmMirror(p, "R", ARM);
        return p;
    }

    static Pose Scooter()
    {
        Pose p = new Pose("Riding a motorbike / scooter");
        p.Set("Spine", 7, 0, 0).Set("Chest", 4, 0, 0).Set("Head", -4, 0, 0)
         .Set("L Thigh", -58, -4, 13).Set("R Thigh", -58, 4, -13).Set("L Shin", 70, 0, 0).Set("R Shin", 70, 0, 0)
         .Set("L Foot", 8, 0, 0).Set("R Foot", 8, 0, 0)
         .Hand("L", "Grip").Hand("R", "Fist");
        double zf = FrontZ(13.6);
        List<Var> vr = ArmVars("R");
        foreach (Var v in vr) { if (v.J == I("R UpperArm") && v.Ax == 2) { v.Lo = 10; v.Hi = 110; } if (v.J == I("R ForeArm") && v.Ax == 1) { v.Lo = 0; v.Hi = 150; } }
        p.Set("R UpperArm", -50, 0, 60).Set("R ForeArm", 0, 40, 0);
        Func<Xf[], double> cost = delegate (Xf[] G)
        {
            double c = 6 * D2(J(G, "R Middle2"), Pt(G, "Chest", -3.7, 13.3, zf + 4.6));
            c += 0.6 * D2(J(G, "R ForeArm"), Pt(G, "Chest", -3.6, 12.6, zf + 1.4));
            double[] palm = PalmN(G, "R"); double d = Dot(palm, A(0, -1, 0)); c += 0.6 * (1 - d) * (1 - d);
            return c;
        };
        Console.WriteLine("scooter " + Solve(p, vr, cost, 0.0003, 40));
        CopyArmMirror(p, "R", ARM);
        p.Hand("L", "Grip");
        return p;
    }

    static Pose Make(string which)
    {
        switch (which)
        {
            case "cover": return SittingCover();
            case "crossed": return ArmsCrossed();
            case "hang": return HangedCorpse();
            case "inverted": return HangedInverted();
            case "injured": return Injured();
            case "peek": return Peeking();
            case "sleep": return Sleeping();
            case "drive": return Driving();
            case "scooter": return Scooter();
        }
        throw new Exception("unknown pose " + which);
    }

    static int Main(string[] a)
    {
        string objp = a[0], outDir = a[1]; Directory.CreateDirectory(outDir);
        o = ObjData.Load(objp); string info; AxisMap m = AxisMap.Detect(o.V, out info);
        nv = o.V.Count; rest = new double[nv * 3]; double[] t = new double[3];
        for (int i = 0; i < nv; i++) { m.ToWork(o.V[i][0], o.V[i][1], o.V[i][2], t); rest[i * 3] = t[0]; rest[i * 3 + 1] = t[1]; rest[i * 3 + 2] = t[2]; }
        sk = Skeleton.Build(rest, nv, 0); tris = o.Tris.ToArray(); skin = new Skinner(rest, nv, tris, sk, 3);
        vp = new Viewport(); vp.Skel = sk; vp.Obj = o; vp.ShowFloor = true; vp.ShowSkeleton = false; vp.ShowGhost = false; vp.RestP = rest;
        for (int i = 2; i < a.Length; i++)
        {
            Pose p = Make(a[i]);
            string fn = a[i];
            File.WriteAllText(Path.Combine(outDir, fn + ".json"), p.ToJson());
            Render(p, outDir, fn); using (Bitmap fb = Final(p, p.Name, 1000, 640)) fb.Save(Path.Combine(outDir, fn + "_final.png"), ImageFormat.Png); Console.WriteLine("done " + fn);
        }
        return 0;
    }
}
