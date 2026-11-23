using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ObjPoser
{
    public class Pose
    {
        public string Name;
        public double[][] Rot = new double[Skeleton.N][];     // degrees, Euler (Rx*Ry*Rz); hand/finger/toe joints use their palm frame (Z = curl)
        public double[][] Trans = new double[Skeleton.N][];   // translation as a fraction of body height
        public bool Snap = true;
        public double LiftH = 0;

        public Pose(string name)
        {
            Name = name;
            for (int i = 0; i < Skeleton.N; i++) { Rot[i] = new double[3]; Trans[i] = new double[3]; }
        }

        public Pose Set(string joint, double x, double y, double z) { double[] r = Rot[Skeleton.Idx(joint)]; r[0] = x; r[1] = y; r[2] = z; return this; }
        public Pose SetT(string joint, double x, double y, double z) { double[] r = Trans[Skeleton.Idx(joint)]; r[0] = x; r[1] = y; r[2] = z; return this; }

        public Pose Clone()
        {
            Pose p = new Pose(Name); p.Snap = Snap; p.LiftH = LiftH;
            for (int i = 0; i < Skeleton.N; i++) { p.Rot[i] = (double[])Rot[i].Clone(); p.Trans[i] = (double[])Trans[i].Clone(); }
            return p;
        }

        public static Pose Lerp(Pose a, Pose b, double t)
        {
            Pose p = a.Clone();
            for (int i = 0; i < Skeleton.N; i++)
                for (int k = 0; k < 3; k++)
                {
                    p.Rot[i][k] = a.Rot[i][k] + (b.Rot[i][k] - a.Rot[i][k]) * t;
                    p.Trans[i][k] = a.Trans[i][k] + (b.Trans[i][k] - a.Trans[i][k]) * t;
                }
            p.LiftH = a.LiftH + (b.LiftH - a.LiftH) * t;
            return p;
        }

        // Mirror left <-> right (x -> -x)
        public Pose Mirrored()
        {
            Pose p = new Pose(Name + " (mirrored)"); p.Snap = Snap; p.LiftH = LiftH;
            for (int i = 0; i < Skeleton.N; i++)
            {
                double[] s = Rot[i]; int m = Skeleton.Mirror[i];
                p.Rot[m] = Skeleton.HasFrame[i] ? new double[] { -s[0], -s[1], s[2] } : new double[] { s[0], -s[1], -s[2] };
                p.Trans[m] = new double[] { -Trans[i][0], Trans[i][1], Trans[i][2] };
            }
            return p;
        }

        // value of a single joint mirrored (used by "symmetric edit")
        public static double[] MirrorRot(int joint, double[] s)
        {
            return Skeleton.HasFrame[joint] ? new double[] { -s[0], -s[1], s[2] } : new double[] { s[0], -s[1], -s[2] };
        }

        // ---------- fingers / hands ----------
        public Pose Curl(string side, int finger, double c)
        {
            double[] f = finger == 0 ? new double[] { 0.5, 0.8, 0.7 } : new double[] { 1.0, 1.1, 0.75 };
            for (int k = 1; k <= 3; k++) { double[] r = Rot[Skeleton.Idx(side + " " + Skeleton.FingerNames[finger] + k)]; r[2] = Math.Min(115, c * f[k - 1]); }
            return this;
        }

        public static readonly string[] HandKinds = { "Open", "Relaxed", "Fist", "Point", "Grip", "Salute / flat", "Spread" };

        public Pose Hand(string side, string kind)
        {
            double[] c;
            switch (kind)
            {
                case "Relaxed": c = new double[] { 12, 22, 28, 34, 40 }; break;
                case "Fist": c = new double[] { 50, 85, 90, 92, 90 }; break;
                case "Point": c = new double[] { 35, 0, 88, 90, 90 }; break;
                case "Grip": c = new double[] { 30, 55, 60, 62, 60 }; break;
                case "Salute / flat": c = new double[] { 0, 0, 0, 0, 0 }; break;
                default: c = new double[] { 0, 0, 0, 0, 0 }; break;
            }
            for (int f = 0; f < 5; f++) Curl(side, f, c[f]);
            double[] spread = kind == "Spread" ? new double[] { 18, 10, 0, -9, -18 } : new double[5];
            for (int f = 0; f < 5; f++) Rot[Skeleton.Idx(side + " " + Skeleton.FingerNames[f] + "1")][1] = spread[f];
            return this;
        }

        // ---------- face ----------
        public static readonly string[] Expressions = { "Neutral", "Mouth open", "Smile", "Surprised", "Angry", "Eyes closed", "Look left", "Look right", "Look down" };

        public Pose Expression(string kind)
        {
            string[] face = { "Jaw", "L Eye", "R Eye", "L Eyelid", "R Eyelid", "L Brow", "R Brow", "Nose", "L Mouth", "R Mouth" };
            foreach (string f in face) { Set(f, 0, 0, 0); SetT(f, 0, 0, 0); }
            switch (kind)
            {
                case "Mouth open": Set("Jaw", 20, 0, 0); break;
                case "Smile":
                    SetT("L Mouth", 0.006, 0.005, 0); SetT("R Mouth", -0.006, 0.005, 0); SetT("L Brow", 0, 0.002, 0); SetT("R Brow", 0, 0.002, 0); Set("Jaw", 4, 0, 0); break;
                case "Surprised":
                    Set("Jaw", 22, 0, 0); SetT("L Brow", 0, 0.01, 0); SetT("R Brow", 0, 0.01, 0); SetT("L Eyelid", 0, 0.004, 0); SetT("R Eyelid", 0, 0.004, 0); break;
                case "Angry":
                    SetT("L Brow", -0.004, -0.007, 0); SetT("R Brow", 0.004, -0.007, 0); Set("Jaw", 6, 0, 0); SetT("L Mouth", 0, -0.004, 0); SetT("R Mouth", 0, -0.004, 0); break;
                case "Eyes closed": SetT("L Eyelid", 0, -0.009, 0); SetT("R Eyelid", 0, -0.009, 0); break;
                case "Look left": Set("L Eye", 0, 25, 0); Set("R Eye", 0, 25, 0); break;
                case "Look right": Set("L Eye", 0, -25, 0); Set("R Eye", 0, -25, 0); break;
                case "Look down": Set("L Eye", 25, 0, 0); Set("R Eye", 25, 0, 0); break;
            }
            return this;
        }

        // ---------- JSON ----------
        static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"name\": \"").Append(Name.Replace("\"", "'")).Append("\",\n");
            sb.Append("  \"snapToGround\": ").Append(Snap ? "true" : "false").Append(",\n");
            sb.Append("  \"liftHeightFraction\": ").Append(F(LiftH)).Append(",\n  \"joints\": {\n");
            bool first = true;
            for (int i = 0; i < Skeleton.N; i++)
            {
                if (Rot[i][0] == 0 && Rot[i][1] == 0 && Rot[i][2] == 0) continue;
                sb.Append(first ? "" : ",\n").Append("    \"").Append(Skeleton.Names[i]).Append("\": [").Append(F(Rot[i][0])).Append(", ").Append(F(Rot[i][1])).Append(", ").Append(F(Rot[i][2])).Append("]");
                first = false;
            }
            sb.Append("\n  },\n  \"translations\": {\n");
            first = true;
            for (int i = 0; i < Skeleton.N; i++)
            {
                if (Trans[i][0] == 0 && Trans[i][1] == 0 && Trans[i][2] == 0) continue;
                sb.Append(first ? "" : ",\n").Append("    \"").Append(Skeleton.Names[i]).Append("\": [").Append(F(Trans[i][0])).Append(", ").Append(F(Trans[i][1])).Append(", ").Append(F(Trans[i][2])).Append("]");
                first = false;
            }
            sb.Append("\n  }\n}\n");
            return sb.ToString();
        }

        public static Pose FromJson(string json)
        {
            Pose p = new Pose("Loaded pose");
            Match m = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success) p.Name = m.Groups[1].Value;
            m = Regex.Match(json, "\"snapToGround\"\\s*:\\s*(true|false)");
            if (m.Success) p.Snap = m.Groups[1].Value == "true";
            m = Regex.Match(json, "\"liftHeightFraction\"\\s*:\\s*(-?[0-9.eE+-]+)");
            if (m.Success) p.LiftH = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int ti = json.IndexOf("\"translations\"", StringComparison.Ordinal);
            string rotPart = ti >= 0 ? json.Substring(0, ti) : json, trPart = ti >= 0 ? json.Substring(ti) : "";
            int found = 0;
            for (int i = 0; i < Skeleton.N; i++)
            {
                string pat = "\"" + Regex.Escape(Skeleton.Names[i]) + "\"\\s*:\\s*\\[\\s*(-?[0-9.eE+-]+)\\s*,\\s*(-?[0-9.eE+-]+)\\s*,\\s*(-?[0-9.eE+-]+)\\s*\\]";
                m = Regex.Match(rotPart, pat);
                if (m.Success) { for (int k = 0; k < 3; k++) p.Rot[i][k] = double.Parse(m.Groups[k + 1].Value, CultureInfo.InvariantCulture); found++; }
                m = Regex.Match(trPart, pat);
                if (m.Success) for (int k = 0; k < 3; k++) p.Trans[i][k] = double.Parse(m.Groups[k + 1].Value, CultureInfo.InvariantCulture);
            }
            if (found == 0 && ti < 0) throw new Exception("This file does not contain a valid pose.");
            return p;
        }

        // Loads every *.json pose file of a folder (extra poses that are not built into the program).
        // Unreadable files are skipped; their names + reasons are added to `errors` (may be null).
        public static List<Pose> LoadFolder(string dir, List<string> errors)
        {
            List<Pose> l = new List<Pose>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return l;
            string[] files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                try { l.Add(FromJson(File.ReadAllText(f))); }
                catch (Exception ex) { if (errors != null) errors.Add(Path.GetFileName(f) + ": " + ex.Message); }
            }
            return l;
        }

        // ---------- presets ----------
        public static List<Pose> BuildPresets()
        {
            List<Pose> l = new List<Pose>();
            l.Add(new Pose("T-Pose (original)"));
            l.Add(new Pose("Relaxed (arms down)").Set("L UpperArm", 0, 0, -72).Set("R UpperArm", 0, 0, 72));

            l.Add(new Pose("Standing (natural)")
                .Set("Hips", 0, 3, 0).Set("Spine", 1, -2, 0).Set("Head", 0, -2, 2)
                .Set("L UpperArm", -6, 0, -78).Set("R UpperArm", -4, 0, 77)
                .Set("L ForeArm", 0, -16, 0).Set("R ForeArm", 0, 12, 0)
                .Set("L Clavicle", 0, 0, -2).Set("R Clavicle", 0, 0, 2)
                .Set("L Thigh", 0, 9, 3.5).Set("R Thigh", 0, -9, -3.5).Set("L Foot", 0, 0, -3.5).Set("R Foot", 0, 0, 3.5)
                .Set("L Shin", 4, 0, 0)
                .Hand("L", "Relaxed").Hand("R", "Relaxed"));

            l.Add(new Pose("Attention (military)")
                .Set("Spine", -2, 0, 0).Set("Chest", -3, 0, 0)
                .Set("L Clavicle", 0, 6, 0).Set("R Clavicle", 0, -6, 0)
                .Set("L UpperArm", 2, 0, -86).Set("R UpperArm", 2, 0, 86)
                .Set("L Thigh", 0, 15, -0.5).Set("R Thigh", 0, -15, 0.5)
                .Set("L Foot", 0, 0, 0.5).Set("R Foot", 0, 0, -0.5)
                .Hand("L", "Grip").Hand("R", "Grip")
                .Curl("L", 0, 5).Curl("R", 0, 5));

            l.Add(new Pose("Sitting (chair)")
                .Set("L Thigh", -90, 0, 0).Set("R Thigh", -90, 0, 0).Set("L Shin", 90, 0, 0).Set("R Shin", 90, 0, 0)
                .Set("L UpperArm", -25, 0, -68).Set("R UpperArm", -25, 0, 68).Set("L ForeArm", 0, 0, -10).Set("R ForeArm", 0, 0, 10)
                .Set("Spine", 4, 0, 0).Hand("L", "Relaxed").Hand("R", "Relaxed"));

            l.Add(new Pose("Sitting (floor)")
                .Set("L Thigh", -88, 0, -3).Set("R Thigh", -88, 0, 3).Set("L Shin", 4, 0, 0).Set("R Shin", 4, 0, 0)
                .Set("L UpperArm", -20, 0, -62).Set("R UpperArm", -20, 0, 62).Set("Spine", 6, 0, 0).Set("Head", 8, 0, 0));

            l.Add(new Pose("Kneeling")
                .Set("L Thigh", -5, 0, 0).Set("R Thigh", -5, 0, 0).Set("L Shin", 95, 0, 0).Set("R Shin", 95, 0, 0)
                .Set("L UpperArm", 0, 0, -75).Set("R UpperArm", 0, 0, 75).Set("Head", 8, 0, 0));

            l.Add(new Pose("Lying on back (dead)")
                .Set("Hips", -90, 0, 0).Set("Head", 0, 25, 10)
                .Set("L UpperArm", 0, 0, -62).Set("R UpperArm", 0, 0, 80).Set("L ForeArm", 0, 0, -8).Set("R ForeArm", 0, 0, 14)
                .Set("L Thigh", 0, 0, 8).Set("R Thigh", 0, 0, -13).Set("R Shin", 10, 0, 0)
                .Hand("L", "Relaxed").Hand("R", "Relaxed").Set("Jaw", 8, 0, 0));

            l.Add(new Pose("Lying face down")
                .Set("Hips", 90, 0, 0).Set("Head", 0, -35, 0)
                .Set("L UpperArm", 0, 0, -55).Set("R UpperArm", 0, 0, 60).Set("L ForeArm", 0, 0, 20).Set("R ForeArm", 0, 0, -20)
                .Set("L Thigh", 0, 0, 6).Set("R Thigh", 0, 0, -9));

            l.Add(new Pose("Tied (hands behind, sitting)")
                .Set("L Thigh", -90, 0, -2).Set("R Thigh", -90, 0, 2).Set("L Shin", 90, 0, 0).Set("R Shin", 90, 0, 0)
                .Set("L UpperArm", 25, 0, -80).Set("R UpperArm", 25, 0, 80).Set("L ForeArm", 0, 0, -55).Set("R ForeArm", 0, 0, 55)
                .Set("Spine", 8, 0, 0).Set("Head", 22, 0, 0).Hand("L", "Fist").Hand("R", "Fist"));

            Pose hang = new Pose("Hanging (arms up)");
            hang.LiftH = 0.12;
            hang.Set("L UpperArm", 0, 0, 80).Set("R UpperArm", 0, 0, -80).Set("L ForeArm", 0, 0, 14).Set("R ForeArm", 0, 0, -14)
                .Set("Head", 30, 0, 0).Set("Spine", 8, 0, 0)
                .Set("L Thigh", 6, 0, 2).Set("R Thigh", 4, 0, -2).Set("L Shin", 14, 0, 0).Set("R Shin", 18, 0, 0)
                .Set("L Foot", 35, 0, 0).Set("R Foot", 35, 0, 0).Hand("L", "Grip").Hand("R", "Grip");
            l.Add(hang);

            l.Add(new Pose("Crouching")
                .Set("L Thigh", -105, 0, -6).Set("R Thigh", -105, 0, 6).Set("L Shin", 115, 0, 0).Set("R Shin", 115, 0, 0)
                .Set("L Foot", -15, 0, 0).Set("R Foot", -15, 0, 0)
                .Set("Spine", 22, 0, 0).Set("Chest", 10, 0, 0).Set("Head", -15, 0, 0)
                .Set("L UpperArm", -40, 0, -55).Set("R UpperArm", -40, 0, 55).Set("L ForeArm", 0, 0, -20).Set("R ForeArm", 0, 0, 20));

            l.Add(new Pose("Lying on side")
                .Set("Hips", 0, 0, 90).Set("Head", 0, 0, -10)
                .Set("L UpperArm", -35, 0, -70).Set("R UpperArm", -50, 0, 65).Set("L ForeArm", 0, 0, -20).Set("R ForeArm", 0, 0, 35)
                .Set("L Thigh", -55, 0, 0).Set("R Thigh", -35, 0, 0).Set("L Shin", 80, 0, 0).Set("R Shin", 70, 0, 0));

            l.Add(new Pose("Crawling (all fours)")
                .Set("Hips", 90, 0, 0).Set("Head", -75, 0, 0).Set("Neck", -10, 0, 0)
                .Set("L UpperArm", 0, -90, 0).Set("R UpperArm", 0, 90, 0)
                .Set("L Thigh", -90, 0, 0).Set("R Thigh", -90, 0, 0).Set("L Shin", 90, 0, 0).Set("R Shin", 90, 0, 0));

            l.Add(new Pose("Hands up (surrender)")
                .Set("L UpperArm", 0, 0, 40).Set("R UpperArm", 0, 0, -40).Set("L ForeArm", 0, 0, 50).Set("R ForeArm", 0, 0, -50)
                .Set("L Thigh", -4, 0, 0).Set("R Thigh", -4, 0, 0).Set("L Shin", 8, 0, 0).Set("R Shin", 8, 0, 0)
                .Hand("L", "Spread").Hand("R", "Spread"));

            l.Add(new Pose("Running")
                .Set("Spine", 10, 0, 0).Set("Chest", 5, 0, 0).Set("Head", -8, 0, 0)
                .Set("L Thigh", -60, 0, 0).Set("L Shin", 75, 0, 0).Set("L Foot", 15, 0, 0)
                .Set("R Thigh", 25, 0, 0).Set("R Shin", 15, 0, 0).Set("R Foot", 30, 0, 0)
                .Set("L UpperArm", 30, 0, -68).Set("R UpperArm", -45, 0, 68).Set("L ForeArm", 0, 0, 10).Set("R ForeArm", 0, 0, 10)
                .Hand("L", "Fist").Hand("R", "Fist"));


            // facepalm: right hand over the face (angles solved numerically on a typical humanoid; fine-tune with the sliders)
            Pose fp = new Pose("Facepalm (one hand)");
            fp.Set("R Clavicle", -33, 23, -6).Set("R UpperArm", -63, -29, 86).Set("R ForeArm", -44, 8, 99).Set("R Hand", -13, -30, -12)
              .Set("L UpperArm", -5, 0, -78).Set("L ForeArm", 0, -12, 0)
              .Set("Head", 18, 0, 3).Set("Spine", 5, 0, 0).Set("Jaw", 3, 0, 0)
              .Hand("L", "Relaxed").Hand("R", "Salute / flat");
            l.Add(fp);

            Pose cv = new Pose("Covering face (both hands)");
            cv.Set("R Clavicle", -39, 27, -25).Set("R UpperArm", -27, -52, 85).Set("R ForeArm", -34, -7, 107).Set("R Hand", -5, -38, 32)
              .Set("Head", 22, 0, 0).Set("Spine", 8, 0, 0).Set("Chest", 4, 0, 0).Hand("R", "Salute / flat");
            Pose cvm = cv.Mirrored();
            foreach (string j in new string[] { "L Clavicle", "L UpperArm", "L ForeArm", "L Hand" })
                cv.Rot[Skeleton.Idx(j)] = (double[])cvm.Rot[Skeleton.Idx(j)].Clone();
            cv.Hand("L", "Salute / flat");
            l.Add(cv);
            return l;
        }
    }
}
