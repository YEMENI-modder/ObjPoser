// Console self-test for the ObjPoser core library.
// Build:  csc /out:selftest.exe tests\CoreSelfTest.cs src\Core.cs src\Skeleton.cs src\Pose.cs src\Skinner.cs src\Export.cs
// Run:    selftest.exe samples\sample_tpose.obj      (exit code 0 = all checks passed)
using System;
using System.Collections.Generic;
using System.IO;
using ObjPoser;

static class SelfTest
{
    static int failures = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failures++; }

    static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "samples/sample_tpose.obj";
        string tmp = Path.Combine(Path.GetTempPath(), "objposer_selftest"); Directory.CreateDirectory(tmp);
        ObjData o = ObjData.Load(path);
        string info; AxisMap m = AxisMap.Detect(o.V, out info);
        int nv = o.V.Count; double[] rest = new double[nv * 3]; double[] t = new double[3];
        for (int i = 0; i < nv; i++) { m.ToWork(o.V[i][0], o.V[i][1], o.V[i][2], t); rest[i * 3] = t[0]; rest[i * 3 + 1] = t[1]; rest[i * 3 + 2] = t[2]; }
        Console.WriteLine("model: " + nv + " vertices, " + o.Tris.Count / 3 + " triangles, " + info + ", joints: " + Skeleton.N);

        Check(Analysis.CheckTPose(rest, nv).Count == 0, "T-Pose check accepts the sample");
        Skeleton sk = Skeleton.Build(rest, nv, 0);
        Skinner s = new Skinner(rest, nv, o.Tris.ToArray(), sk, 3);
        List<Pose> poses = Pose.BuildPresets();
        Check(poses.Count >= 18, "at least 18 preset poses (" + poses.Count + ")");

        // every pose: finite coordinates with LBS and DQS
        bool allFinite = true;
        foreach (Pose p in poses)
            foreach (bool dqs in new bool[] { false, true })
            {
                s.UseDQS = dqs; PosedResult r = s.Apply(p);
                foreach (double v in r.P) if (double.IsNaN(v) || double.IsInfinity(v)) allFinite = false;
            }
        s.UseDQS = false;
        Check(allFinite, "all presets give finite coordinates (LBS + DQS)");
        Check(poses[0].Name.StartsWith("T-Pose"), "first preset is the T-Pose");
        PosedResult rt = s.Apply(poses[0]); double dmax = 0;
        for (int i = 0; i < rt.P.Length; i++) dmax = Math.Max(dmax, Math.Abs(rt.P[i] - rest[i]));
        Check(dmax < 1e-6 * Math.Max(1, sk.H), "T-Pose preset leaves the mesh unchanged (max diff " + dmax.ToString("G2") + ")");

        // pose JSON + mirror
        Pose q = poses[2].Clone().Expression("Smile").Hand("L", "Fist");
        Pose q2 = Pose.FromJson(q.ToJson()); double dd = 0;
        for (int i = 0; i < Skeleton.N; i++) for (int c = 0; c < 3; c++) { dd = Math.Max(dd, Math.Abs(q.Rot[i][c] - q2.Rot[i][c])); dd = Math.Max(dd, Math.Abs(q.Trans[i][c] - q2.Trans[i][c])); }
        Check(dd < 1e-3, "pose JSON round trip (diff " + dd.ToString("G2") + ")");
        Pose mm = q.Mirrored().Mirrored(); dd = 0;
        for (int i = 0; i < Skeleton.N; i++) for (int c = 0; c < 3; c++) dd = Math.Max(dd, Math.Max(Math.Abs(q.Rot[i][c] - mm.Rot[i][c]), Math.Abs(q.Trans[i][c] - mm.Trans[i][c])));
        Check(dd < 1e-9, "mirroring twice gives the original pose");

        // OBJ export with restore data, restore, and reference restore
        Pose pose = poses[4]; PosedResult pr = s.Apply(pose);
        string posed = Path.Combine(tmp, "posed.obj"), plain = Path.Combine(tmp, "plain.obj"), back = Path.Combine(tmp, "back.obj"), back2 = Path.Combine(tmp, "back2.obj");
        ObjExporter.Save(posed, o, m, s, pr, true, pose.Name); ObjExporter.Save(plain, o, m, s, pr, false, pose.Name);
        ObjData op = ObjData.Load(posed);
        Check(op.RestV != null, "exported OBJ carries restore data");
        Check(op.V.Count == nv && op.Tris.Count == o.Tris.Count, "exported OBJ keeps vertex / face counts");
        ObjRestore.Restore(posed, back, null);
        ObjData ob = ObjData.Load(back); dd = 0;
        for (int i = 0; i < nv; i++) for (int k = 0; k < 3; k++) dd = Math.Max(dd, Math.Abs(ob.V[i][k] - o.V[i][k]));
        Check(dd < 1e-4 * Math.Max(1, sk.H), "Restore T-Pose brings the original vertices back (diff " + dd.ToString("G2") + ")");
        bool threw = false; try { ObjRestore.Restore(plain, back2, null); } catch (NoRestoreDataException) { threw = true; }
        Check(threw, "file without restore data -> NoRestoreDataException");
        ObjRestore.Restore(plain, back2, path); ob = ObjData.Load(back2); dd = 0;
        for (int i = 0; i < nv; i++) for (int k = 0; k < 3; k++) dd = Math.Max(dd, Math.Abs(ob.V[i][k] - o.V[i][k]));
        Check(dd < 1e-9, "Restore from a reference T-Pose file");

        // GLB export (structure only: header, lengths, chunk types)
        string glb = Path.Combine(tmp, "pose.glb"); GlbExporter.Save(glb, o, s, pose, pr.Dy, false);
        string glbA = Path.Combine(tmp, "anim.glb"); GlbExporter.Save(glbA, o, s, pose, pr.Dy, true);
        foreach (string g in new string[] { glb, glbA })
        {
            byte[] b = File.ReadAllBytes(g);
            Check(b.Length > 100 && BitConverter.ToUInt32(b, 0) == 0x46546C67 && BitConverter.ToUInt32(b, 8) == (uint)b.Length, "valid GLB container: " + Path.GetFileName(g));
        }

        Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : "\n" + failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
