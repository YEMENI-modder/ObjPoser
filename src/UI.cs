using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ObjPoser
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ---------- 3D viewport (software renderer, GDI+) ----------
    class Viewport : Control
    {
        public double[] P;          // posed vertices
        public double[] RestP;      // T-Pose vertices (ghost)
        public int[] Tris;
        public ObjData Obj;
        public Bitmap[] Tex;
        public Skeleton Skel;
        public Xf[] G;
        public double Dy;
        public bool ShowSkeleton, Wire, ShowGhost, ShowFloor = true, UseTextures = true;
        public double Yaw = 0, Pitch = 0.15, Zoom = 1.0;

        double cxv, cyv, czv, extent = 1;
        int pw = 100, ph = 100;
        public double FixedExtent = 0;
        public int GridN = 10;
        bool dragging; Point last;
        Dictionary<int, Brush> brushes = new Dictionary<int, Brush>();

        public Viewport()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(38, 40, 46);
        }

        public void SetMesh(double[] p, int[] tris)
        {
            P = p; Tris = tris; RecalcBounds(); Invalidate();
        }

        public void RecalcBounds()
        {
            if (P == null || P.Length == 0) return;
            double mnx = 1e30, mny = 1e30, mnz = 1e30, mxx = -1e30, mxy = -1e30, mxz = -1e30;
            for (int i = 0; i < P.Length; i += 3)
            {
                if (P[i] < mnx) mnx = P[i]; if (P[i] > mxx) mxx = P[i];
                if (P[i + 1] < mny) mny = P[i + 1]; if (P[i + 1] > mxy) mxy = P[i + 1];
                if (P[i + 2] < mnz) mnz = P[i + 2]; if (P[i + 2] > mxz) mxz = P[i + 2];
            }
            cxv = (mnx + mxx) / 2; cyv = (mny + mxy) / 2; czv = (mnz + mxz) / 2;
            extent = Math.Max(mxx - mnx, Math.Max(mxy - mny, mxz - mnz));
            if (extent < 1e-9) extent = 1;
        }

        void Proj(double x, double y, double z, double sc, out float sx, out float sy, out double d)
        {
            x -= cxv; y -= cyv; z -= czv;
            double cY = Math.Cos(Yaw), sY = Math.Sin(Yaw), cP = Math.Cos(Pitch), sP = Math.Sin(Pitch);
            double x1 = x * cY + z * sY, z1 = -x * sY + z * cY;
            double y2 = y * cP - z1 * sP, z2 = y * sP + z1 * cP;
            sx = (float)(pw / 2.0 + x1 * sc);
            sy = (float)(ph / 2.0 - y2 * sc);
            d = z2;
        }

        Brush ShadeBrush(int mat, double inten)
        {
            int b = (int)(inten * 15); if (b > 15) b = 15; if (b < 0) b = 0;
            int key = (mat + 1) * 16 + b;
            Brush br;
            if (brushes.TryGetValue(key, out br)) return br;
            double[] kd = { 0.72, 0.76, 0.84 };
            if (mat >= 0 && Obj != null && mat < Obj.Materials.Count && UseTextures) kd = Obj.Materials[mat].Kd;
            double k = 0.30 + 0.70 * b / 15.0;
            Color c = Color.FromArgb((int)Math.Min(255, kd[0] * 255 * k), (int)Math.Min(255, kd[1] * 255 * k), (int)Math.Min(255, kd[2] * 255 * k));
            br = new SolidBrush(c); brushes[key] = br; return br;
        }

        public void ClearBrushCache() { brushes.Clear(); }

        bool DrawTextured(Graphics g, int t, PointF[] pts)
        {
            if (Obj == null || Tex == null) return false;
            int mat = Obj.TriMat[t];
            if (mat < 0 || mat >= Tex.Length || Tex[mat] == null) return false;
            int u0 = Obj.TriVT[t * 3], u1 = Obj.TriVT[t * 3 + 1], u2 = Obj.TriVT[t * 3 + 2];
            if (u0 < 0 || u1 < 0 || u2 < 0) return false;
            Bitmap bmp = Tex[mat]; int W = bmp.Width, H = bmp.Height;
            double[] a = Obj.VT[u0], b = Obj.VT[u1], c = Obj.VT[u2];
            // wrap repeating UVs: shift the triangle into the 0..1 tile it mostly sits in
            double shU = Math.Floor(Math.Min(a[0], Math.Min(b[0], c[0])) + 1e-6), shV = Math.Floor(Math.Min(a[1], Math.Min(b[1], c[1])) + 1e-6);
            double s0x = (a[0] - shU) * W, s0y = (1 - (a[1] - shV)) * H, s1x = (b[0] - shU) * W, s1y = (1 - (b[1] - shV)) * H, s2x = (c[0] - shU) * W, s2y = (1 - (c[1] - shV)) * H;
            double minx = Math.Min(s0x, Math.Min(s1x, s2x)), maxx = Math.Max(s0x, Math.Max(s1x, s2x));
            double miny = Math.Min(s0y, Math.Min(s1y, s2y)), maxy = Math.Max(s0y, Math.Max(s1y, s2y));
            if (minx < -0.5 || miny < -0.5 || maxx > W + 0.5 || maxy > H + 0.5) return false;
            double m00 = s1x - s0x, m01 = s2x - s0x, m10 = s1y - s0y, m11 = s2y - s0y;
            double det = m00 * m11 - m01 * m10;
            if (Math.Abs(det) < 1e-9) return false;
            double d00 = pts[1].X - pts[0].X, d01 = pts[2].X - pts[0].X, d10 = pts[1].Y - pts[0].Y, d11 = pts[2].Y - pts[0].Y;
            // A = D * S^-1
            double i00 = m11 / det, i01 = -m01 / det, i10 = -m10 / det, i11 = m00 / det;
            double A00 = d00 * i00 + d01 * i10, A01 = d00 * i01 + d01 * i11;
            double A10 = d10 * i00 + d11 * i10, A11 = d10 * i01 + d11 * i11;
            double x0 = Math.Floor(minx), y0 = Math.Floor(miny), x1 = Math.Ceiling(maxx), y1 = Math.Ceiling(maxy);
            if (x1 <= x0) x1 = x0 + 1; if (y1 <= y0) y1 = y0 + 1;
            Func<double, double, PointF> map = delegate (double sx, double sy)
            {
                double dx = sx - s0x, dy = sy - s0y;
                return new PointF((float)(pts[0].X + A00 * dx + A01 * dy), (float)(pts[0].Y + A10 * dx + A11 * dy));
            };
            PointF[] dest = { map(x0, y0), map(x1, y0), map(x0, y1) };
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddPolygon(pts);
                g.SetClip(gp);
                g.DrawImage(bmp, dest, new RectangleF((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0)), GraphicsUnit.Pixel);
                g.ResetClip();
            }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e) { PaintScene(e.Graphics, Width, Height, dragging); }

        // Renders the current mesh into a bitmap (used for thumbnails and the "all poses" screenshots).
        public Bitmap Capture(int w, int h, double yaw, double pitch, bool tex, double fixedExtent)
        {
            double oy = Yaw, op = Pitch, oz = Zoom, ofe = FixedExtent; bool ot = UseTextures;
            Yaw = yaw; Pitch = pitch; Zoom = 1; FixedExtent = fixedExtent; UseTextures = tex;
            Bitmap b = new Bitmap(w, h);
            try { using (Graphics g = Graphics.FromImage(b)) PaintScene(g, w, h, false); }
            finally { Yaw = oy; Pitch = op; Zoom = oz; FixedExtent = ofe; UseTextures = ot; }
            return b;
        }

        void PaintScene(Graphics g, int W, int H, bool drag)
        {
            pw = W; ph = H;
            g.Clear(BackColor);
            if (P == null || Tris == null) { DrawHint(g); return; }
            double sc = Math.Min(W, H) / ((FixedExtent > 0 ? FixedExtent : extent) * 1.15) * Zoom;
            int nv = P.Length / 3;
            float[] sx = new float[nv], sy = new float[nv]; double[] dz = new double[nv];
            for (int i = 0; i < nv; i++) Proj(P[i * 3], P[i * 3 + 1], P[i * 3 + 2], sc, out sx[i], out sy[i], out dz[i]);
            g.SmoothingMode = SmoothingMode.None;

            if (ShowFloor && Skel != null)
            {
                using (Pen fp = new Pen(Color.FromArgb(70, 75, 85), 1f))
                {
                    double step = Skel.H / 8.0; int cnt0 = GridN;
                    double gx0 = Math.Round(cxv / step) * step, gz0 = Math.Round(czv / step) * step;
                    for (int i = -cnt0; i <= cnt0; i++)
                    {
                        float ax, ay, bx, by, ax2, ay2, bx2, by2; double d;
                        Proj(gx0 + i * step, Skel.MinY, gz0 - cnt0 * step, sc, out ax, out ay, out d);
                        Proj(gx0 + i * step, Skel.MinY, gz0 + cnt0 * step, sc, out bx, out by, out d);
                        Proj(gx0 - cnt0 * step, Skel.MinY, gz0 + i * step, sc, out ax2, out ay2, out d);
                        Proj(gx0 + cnt0 * step, Skel.MinY, gz0 + i * step, sc, out bx2, out by2, out d);
                        g.DrawLine(fp, ax, ay, bx, by); g.DrawLine(fp, ax2, ay2, bx2, by2);
                    }
                }
            }

            if (ShowGhost && RestP != null)
            {
                int gn = RestP.Length / 3;
                float[] gx = new float[gn], gy = new float[gn];
                for (int i = 0; i < gn; i++) { double dd; Proj(RestP[i * 3], RestP[i * 3 + 1], RestP[i * 3 + 2], sc, out gx[i], out gy[i], out dd); }
                int gt = Tris.Length / 3; int gs = Math.Max(1, gt / 5000);
                using (Pen gp = new Pen(Color.FromArgb(70, 255, 200, 90), 1f))
                    for (int t = 0; t < gt; t += gs)
                    {
                        int a = Tris[t * 3], b = Tris[t * 3 + 1], c = Tris[t * 3 + 2];
                        g.DrawLine(gp, gx[a], gy[a], gx[b], gy[b]); g.DrawLine(gp, gx[b], gy[b], gx[c], gy[c]);
                    }
            }

            int nt = Tris.Length / 3;
            int maxT = drag ? 12000 : 90000;
            int stride = nt > maxT ? (nt + maxT - 1) / maxT : 1;
            int cnt = (nt + stride - 1) / stride;
            double[] keys = new double[cnt]; int[] ids = new int[cnt];
            int c2 = 0;
            for (int t = 0; t < nt; t += stride)
            {
                keys[c2] = (dz[Tris[t * 3]] + dz[Tris[t * 3 + 1]] + dz[Tris[t * 3 + 2]]); ids[c2] = t; c2++;
            }
            Array.Sort(keys, ids);

            PointF[] pts = new PointF[3];
            double lx = 0.35, ly = 0.55, lz = 0.75, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            lx /= ll; ly /= ll; lz /= ll;
            bool tex = UseTextures && Tex != null && !drag && cnt <= 25000 && !Wire;
            if (tex) g.InterpolationMode = InterpolationMode.NearestNeighbor;
            using (Pen wp = new Pen(Color.FromArgb(170, 190, 210), 1f))
            {
                for (int k = 0; k < cnt; k++)
                {
                    int t = ids[k];
                    int a = Tris[t * 3], b = Tris[t * 3 + 1], cc = Tris[t * 3 + 2];
                    pts[0] = new PointF(sx[a], sy[a]); pts[1] = new PointF(sx[b], sy[b]); pts[2] = new PointF(sx[cc], sy[cc]);
                    if (Wire) { g.DrawPolygon(wp, pts); continue; }
                    if (tex && DrawTextured(g, t, pts)) continue;
                    double ux = sx[b] - sx[a], uy = -(sy[b] - sy[a]), uz = (dz[b] - dz[a]) * sc;
                    double vx = sx[cc] - sx[a], vy = -(sy[cc] - sy[a]), vz = (dz[cc] - dz[a]) * sc;
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    double inten = nl < 1e-9 ? 0.5 : Math.Abs(nx * lx + ny * ly + nz * lz) / nl;
                    int mat = Obj != null && t < Obj.TriMat.Count ? Obj.TriMat[t] : -1;
                    g.FillPolygon(ShadeBrush(mat, inten), pts);
                }
            }

            if (ShowSkeleton && Skel != null && G != null)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                double[] o = new double[3]; double[] e2 = new double[3];
                using (Pen sp = new Pen(Color.FromArgb(255, 80, 60), 2f))
                using (Brush jb = new SolidBrush(Color.Yellow))
                {
                    for (int j = 0; j < Skeleton.N; j++)
                    {
                        G[j].Apply(0, 0, 0, o);
                        double[] en = Skel.End[j];
                        G[j].Apply(en[0] - Skel.Pos[j][0], en[1] - Skel.Pos[j][1], en[2] - Skel.Pos[j][2], e2);
                        float x1, y1, x2, y2; double d;
                        Proj(o[0], o[1] + Dy, o[2], sc, out x1, out y1, out d);
                        Proj(e2[0], e2[1] + Dy, e2[2], sc, out x2, out y2, out d);
                        g.DrawLine(sp, x1, y1, x2, y2);
                        g.FillEllipse(jb, x1 - 3, y1 - 3, 6, 6);
                    }
                }
            }
        }

        void DrawHint(Graphics g)
        {
            using (Font f = new Font("Segoe UI", 12f))
                g.DrawString("Open an OBJ file (T-Pose character) to start", f, Brushes.Gray, 20, 20);
        }

        protected override void OnMouseDown(MouseEventArgs e) { dragging = true; last = e.Location; Focus(); }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            Yaw += (e.X - last.X) * 0.01;
            Pitch += (e.Y - last.Y) * 0.01;
            if (Pitch > 1.5) Pitch = 1.5; if (Pitch < -1.5) Pitch = -1.5;
            last = e.Location; Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Zoom *= e.Delta > 0 ? 1.1 : 1 / 1.1;
            if (Zoom < 0.2) Zoom = 0.2; if (Zoom > 8) Zoom = 8;
            Invalidate();
        }
    }

    // ---------- batch dialog ----------
    class BatchDialog : Form
    {
        public CheckedListBox List = new CheckedListBox();
        public RadioButton RObj = new RadioButton(), RGlb = new RadioButton(), RBoth = new RadioButton();
        public CheckBox ChkAnim = new CheckBox();
        public BatchDialog(List<Pose> poses)
        {
            Text = "Batch export"; Width = 340; Height = 500; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            Label l = new Label(); l.Text = "Poses to export for every file:"; l.Left = 10; l.Top = 10; l.Width = 300; Controls.Add(l);
            List.Left = 10; List.Top = 34; List.Width = 305; List.Height = 270; List.CheckOnClick = true;
            foreach (Pose p in poses) List.Items.Add(p.Name, false);
            Controls.Add(List);
            RObj.Text = "OBJ"; RObj.Left = 10; RObj.Top = 312; RObj.Checked = true; RObj.Width = 70;
            RGlb.Text = "GLB (with bones)"; RGlb.Left = 85; RGlb.Top = 312; RGlb.Width = 130;
            RBoth.Text = "Both"; RBoth.Left = 220; RBoth.Top = 312; RBoth.Width = 80;
            Controls.Add(RObj); Controls.Add(RGlb); Controls.Add(RBoth);
            ChkAnim.Text = "GLB: include animation (T-Pose -> pose)"; ChkAnim.Left = 10; ChkAnim.Top = 340; ChkAnim.Width = 300; Controls.Add(ChkAnim);
            Button ok = new Button(); ok.Text = "Choose files..."; ok.Left = 10; ok.Top = 380; ok.Width = 150; ok.Height = 32; ok.DialogResult = DialogResult.OK;
            Button cancel = new Button(); cancel.Text = "Cancel"; cancel.Left = 170; cancel.Top = 380; cancel.Width = 100; cancel.Height = 32; cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
        }
    }

    // ---------- group + joint picker ----------
    class JointPicker
    {
        public ComboBox Group = new ComboBox(), Joint = new ComboBox();
        List<int> items = new List<int>();
        public event EventHandler Changed;
        public int Current { get { return Joint.SelectedIndex >= 0 ? items[Joint.SelectedIndex] : 0; } }

        public JointPicker(Control parent, ref int y)
        {
            Group.DropDownStyle = ComboBoxStyle.DropDownList; Group.Left = 8; Group.Top = y; Group.Width = 285;
            foreach (string g in Skeleton.GroupNames) Group.Items.Add(g);
            parent.Controls.Add(Group); y += 28;
            Joint.DropDownStyle = ComboBoxStyle.DropDownList; Joint.Left = 8; Joint.Top = y; Joint.Width = 285;
            parent.Controls.Add(Joint); y += 30;
            Group.SelectedIndexChanged += delegate { Fill(); };
            Joint.SelectedIndexChanged += delegate { if (Changed != null) Changed(this, EventArgs.Empty); };
            Group.SelectedIndex = 0;
        }

        void Fill()
        {
            items.Clear(); Joint.Items.Clear();
            for (int i = 0; i < Skeleton.N; i++) if (Skeleton.Group[i] == Group.SelectedItem.ToString()) { items.Add(i); Joint.Items.Add(Skeleton.Names[i]); }
            Joint.SelectedIndex = 0;
        }

        public void Select(int joint)
        {
            Group.SelectedItem = Skeleton.Group[joint];
            Joint.SelectedItem = Skeleton.Names[joint];
        }
    }

    // ---------- About dialog ----------
    class AboutDialog : Form
    {
        public AboutDialog()
        {
            Text = "About ObjPoser"; Width = 520; Height = 470; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; Font = new Font("Segoe UI", 9f);
            Label t = new Label(); t.Text = "ObjPoser  v" + AppInfo.Version; t.Font = new Font("Segoe UI", 16f, FontStyle.Bold); t.Left = 16; t.Top = 14; t.Width = 480; t.Height = 34; Controls.Add(t);
            Label d = new Label(); d.Left = 16; d.Top = 54; d.Width = 480; d.Height = 330;
            d.Text = "Turn a T-Pose character OBJ into poses (standing, attention, sitting, lying, facepalm...), edit fingers, toes and face, preview and export OBJ / GLB.\n\n" +
                     "Created by " + AppInfo.Author + "  -  Copyright (c) 2026 YEMENI.  MIT License.\n\n" +
                     "AI DISCLOSURE\nThis tool's code was written entirely by AI: Claude Sonnet 5.5 (Anthropic) wrote all of it. " + AppInfo.Author + " directed the project and did the testing.\n\n" +
                     "THIRD-PARTY LIBRARIES (MIT)\n  TGASharpLib - Alex Green (Zelenskyi Alexandr)\n  DDSReaderSharp - Alex Green (Zelenskyi Alexandr), based on DDSReader.java by Kenji Sasaki\n\n" +
                     "SHORTCUTS\n  Ctrl+O open   Ctrl+E export OBJ   Ctrl+G export GLB   Ctrl+Z / Ctrl+Y undo / redo\n  Ctrl+M mirror pose   Ctrl+T restore T-Pose   F5 screenshots of all poses   F12 screenshot";
            Controls.Add(d);
            LinkLabel l = new LinkLabel(); l.Text = AppInfo.Repo; l.Left = 16; l.Top = 392; l.Width = 480;
            l.LinkClicked += delegate { try { System.Diagnostics.Process.Start(AppInfo.Repo); } catch { } };
            Controls.Add(l);
        }
    }

    // ---------- Main window ----------
    class MainForm : Form
    {
        static readonly Color Accent = Color.FromArgb(0, 120, 212);
        Viewport view = new Viewport();
        MenuStrip menu = new MenuStrip(); ToolStrip toolbar = new ToolStrip(); StatusStrip statusBar = new StatusStrip();
        ToolStripStatusLabel statusLbl = new ToolStripStatusLabel(); ToolStripProgressBar progress = new ToolStripProgressBar();
        ToolTip tips = new ToolTip();
        List<ToolStripItem> needsModel = new List<ToolStripItem>();
        Button btnOpen, btnExport, btnGlb, btnGlbAnim, btnBatch, btnResetView, btnShot, btnShotAll, btnRestore, btnMirror, btnSavePose, btnLoadPose, btnRecalc, btnRigReset, btnUndo, btnRedo, btnResetJoint, btnHand, btnExpr;
        CheckBox chkAuto, chkZUp, chkFlip, chkSkel, chkWire, chkGhost, chkFloor, chkTex, chkDqs, chkSym, chkEmbed, chkThumbs;
        NumericUpDown numSmooth;
        ListView lstPoses; ImageList thumbs = new ImageList();
        ComboBox cmbBlend, cmbHandKind, cmbHandSide, cmbExpr, cmbHandMode;
        JointPicker pick, rigPick;
        TrackBar[] tb = new TrackBar[3], ttb = new TrackBar[3], rtb = new TrackBar[3];
        TrackBar blendTb;
        Label[] tbl = new Label[3], ttbl = new Label[3], rtbl = new Label[3];
        Label modelInfo;
        Timer rigTimer = new Timer();

        ObjData obj; string objPath; int[] trisArr;
        AxisMap map = new AxisMap();
        Skinner skin; double[] rest;
        List<Pose> presets = Pose.BuildPresets();
        int builtInPoses, extraPoses; string extraPoseDir; List<string> extraPoseErrors = new List<string>();

        // Extra poses live in a "poses" folder next to ObjPoser.exe (or in the current folder): every *.json file in it is added to the gallery at start-up.
        static string FindPoseFolder()
        {
            string a = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "poses"), b = Path.Combine(Directory.GetCurrentDirectory(), "poses");
            if (Directory.Exists(a) && Directory.GetFiles(a, "*.json").Length > 0) return a;
            if (Directory.Exists(b) && Directory.GetFiles(b, "*.json").Length > 0) return b;
            return a;
        }
        Pose cur, basePose;
        PosedResult posed;
        bool updating;
        double[][] rigOff = new double[Skeleton.N][];
        Bitmap[] textures;
        Stack<Pose> undo = new Stack<Pose>(), redo = new Stack<Pose>();
        string axisInfo = "";

        int PoseIndex
        {
            get { return lstPoses.SelectedIndices.Count > 0 ? lstPoses.SelectedIndices[0] : -1; }
            set { lstPoses.SelectedIndices.Clear(); if (value >= 0 && value < lstPoses.Items.Count) { lstPoses.Items[value].Selected = true; lstPoses.Items[value].EnsureVisible(); } }
        }

        public MainForm()
        {
            Text = "ObjPoser v" + AppInfo.Version + "  -  T-Pose character poser";
            Font = new Font("Segoe UI", 9f);
            Width = 1280; Height = 840; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(1000, 680);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            for (int i = 0; i < Skeleton.N; i++) rigOff[i] = new double[3];
            builtInPoses = presets.Count; extraPoseDir = FindPoseFolder();
            presets.AddRange(Pose.LoadFolder(extraPoseDir, extraPoseErrors)); extraPoses = presets.Count - builtInPoses;
            thumbs.ImageSize = new Size(88, 108); thumbs.ColorDepth = ColorDepth.Depth32Bit;

            TabControl tabs = new TabControl(); tabs.Dock = DockStyle.Left; tabs.Width = 372; tabs.Padding = new Point(10, 4);
            TabPage t1 = new TabPage("Poses"), t2 = new TabPage("Edit joints"), t3 = new TabPage("Rig"), t4 = new TabPage("Export && View");
            foreach (TabPage tp in new TabPage[] { t1, t2, t3, t4 }) { tp.AutoScroll = true; tp.BackColor = Color.White; tabs.TabPages.Add(tp); }
            view.Dock = DockStyle.Fill;
            Splitter split = new Splitter(); split.Dock = DockStyle.Left; split.Width = 5; split.BackColor = Color.FromArgb(220, 220, 224);
            Controls.Add(view); Controls.Add(split); Controls.Add(tabs);

            // ---- tab 1: poses ----
            int y = 10;
            btnOpen = MkBtn("Open OBJ (T-Pose character)...", t1, ref y, true); btnOpen.Click += delegate { OnOpen(); }; Tip(btnOpen, "Load a T-Pose OBJ. Files posed by ObjPoser can be returned to their T-Pose.");
            chkAuto = MkChk("Auto-detect up / front axes", t1, ref y); chkAuto.Checked = true; chkAuto.CheckedChanged += OnAxisChanged;
            chkZUp = MkChk("      manual: file is Z-up", t1, ref y); chkZUp.CheckedChanged += OnAxisChanged;
            chkFlip = MkChk("      manual: character faces -Z", t1, ref y); chkFlip.CheckedChanged += OnAxisChanged;
            chkZUp.Enabled = chkFlip.Enabled = false;
            MkHeader("Pose gallery", t1, ref y);
            lstPoses = new ListView(); lstPoses.View = View.LargeIcon; lstPoses.LargeImageList = thumbs; lstPoses.MultiSelect = false; lstPoses.HideSelection = false;
            lstPoses.Left = 8; lstPoses.Top = y; lstPoses.Width = 336; lstPoses.Height = 300; lstPoses.BackColor = Color.White;
            foreach (Pose p in presets) lstPoses.Items.Add(p.Name, -1);
            lstPoses.SelectedIndexChanged += OnPoseSelected; t1.Controls.Add(lstPoses); y += 308;
            MkHeader("Blend with another pose", t1, ref y);
            cmbBlend = new ComboBox(); cmbBlend.DropDownStyle = ComboBoxStyle.DropDownList; cmbBlend.Left = 8; cmbBlend.Top = y; cmbBlend.Width = 336;
            foreach (Pose p in presets) cmbBlend.Items.Add(p.Name);
            cmbBlend.SelectedIndex = 0; cmbBlend.SelectedIndexChanged += OnBlend; t1.Controls.Add(cmbBlend); y += 28;
            blendTb = new TrackBar(); blendTb.Minimum = 0; blendTb.Maximum = 100; blendTb.TickFrequency = 25; blendTb.Left = 8; blendTb.Top = y; blendTb.Width = 336;
            blendTb.MouseDown += delegate { PushUndo(); }; blendTb.Scroll += OnBlend; t1.Controls.Add(blendTb); y += 46;
            btnMirror = MkBtn("Mirror pose (left <-> right)", t1, ref y, false); btnMirror.Click += delegate { OnMirror(); };
            btnUndo = new Button(); btnUndo.Text = "Undo"; btnUndo.Left = 8; btnUndo.Top = y; btnUndo.Width = 164; btnUndo.Height = 30; btnUndo.Click += delegate { DoUndo(); }; t1.Controls.Add(btnUndo);
            btnRedo = new Button(); btnRedo.Text = "Redo"; btnRedo.Left = 180; btnRedo.Top = y; btnRedo.Width = 164; btnRedo.Height = 30; btnRedo.Click += delegate { DoRedo(); }; t1.Controls.Add(btnRedo); y += 38;
            btnSavePose = MkBtn("Save pose (.json)...", t1, ref y, false); btnSavePose.Click += delegate { OnSavePose(); };
            btnLoadPose = MkBtn("Load pose (.json)...", t1, ref y, false); btnLoadPose.Click += delegate { OnLoadPose(); };

            // ---- tab 2: edit joints ----
            y = 10; string[] ax = { "X", "Y", "Z" };
            MkHeader("Joint (body / face / fingers / toes)", t2, ref y);
            pick = new JointPicker(t2, ref y); pick.Changed += delegate { SyncSliders(); };
            MkHeader("Rotation (degrees)   fingers, toes and hands: Z = curl", t2, ref y);
            for (int i = 0; i < 3; i++)
            {
                tbl[i] = new Label(); tbl[i].Text = ax[i] + ": 0"; tbl[i].Left = 8; tbl[i].Top = y + 6; tbl[i].Width = 55;
                tb[i] = new TrackBar(); tb[i].Minimum = -180; tb[i].Maximum = 180; tb[i].TickFrequency = 45; tb[i].Left = 63; tb[i].Top = y; tb[i].Width = 280;
                tb[i].MouseDown += delegate { PushUndo(); }; tb[i].Scroll += OnSlider; t2.Controls.Add(tbl[i]); t2.Controls.Add(tb[i]); y += 40;
            }
            MkHeader("Move (tenths of % of body height)", t2, ref y);
            for (int i = 0; i < 3; i++)
            {
                ttbl[i] = new Label(); ttbl[i].Text = ax[i] + ": 0"; ttbl[i].Left = 8; ttbl[i].Top = y + 6; ttbl[i].Width = 55;
                ttb[i] = new TrackBar(); ttb[i].Minimum = -100; ttb[i].Maximum = 100; ttb[i].TickFrequency = 25; ttb[i].Left = 63; ttb[i].Top = y; ttb[i].Width = 280;
                ttb[i].MouseDown += delegate { PushUndo(); }; ttb[i].Scroll += OnSlider; t2.Controls.Add(ttbl[i]); t2.Controls.Add(ttb[i]); y += 40;
            }
            chkSym = MkChk("Symmetric edit (also change the opposite side)", t2, ref y);
            btnResetJoint = MkBtn("Reset this joint", t2, ref y, false); btnResetJoint.Click += delegate { if (cur == null) return; PushUndo(); int j = pick.Current; cur.Rot[j] = new double[3]; cur.Trans[j] = new double[3]; AfterEdit(); };
            MkHeader("Hand presets", t2, ref y);
            cmbHandKind = new ComboBox(); cmbHandKind.DropDownStyle = ComboBoxStyle.DropDownList; cmbHandKind.Left = 8; cmbHandKind.Top = y; cmbHandKind.Width = 160;
            foreach (string k in Pose.HandKinds) cmbHandKind.Items.Add(k); cmbHandKind.SelectedIndex = 1; t2.Controls.Add(cmbHandKind);
            cmbHandSide = new ComboBox(); cmbHandSide.DropDownStyle = ComboBoxStyle.DropDownList; cmbHandSide.Left = 176; cmbHandSide.Top = y; cmbHandSide.Width = 90;
            cmbHandSide.Items.AddRange(new object[] { "Both", "Left", "Right" }); cmbHandSide.SelectedIndex = 0; t2.Controls.Add(cmbHandSide);
            btnHand = new Button(); btnHand.Text = "Apply"; btnHand.Left = 274; btnHand.Top = y - 1; btnHand.Width = 70; btnHand.Click += delegate { OnHand(); }; t2.Controls.Add(btnHand); y += 36;
            MkHeader("Face expression", t2, ref y);
            cmbExpr = new ComboBox(); cmbExpr.DropDownStyle = ComboBoxStyle.DropDownList; cmbExpr.Left = 8; cmbExpr.Top = y; cmbExpr.Width = 258;
            foreach (string k in Pose.Expressions) cmbExpr.Items.Add(k); cmbExpr.SelectedIndex = 1; t2.Controls.Add(cmbExpr);
            btnExpr = new Button(); btnExpr.Text = "Apply"; btnExpr.Left = 274; btnExpr.Top = y - 1; btnExpr.Width = 70; btnExpr.Click += delegate { OnExpression(); }; t2.Controls.Add(btnExpr);

            // ---- tab 3: rig ----
            y = 10;
            MkHeader("Skinning", t3, ref y);
            chkDqs = MkChk("Dual Quaternion skinning (keeps volume at joints)", t3, ref y); chkDqs.CheckedChanged += delegate { if (skin != null) { skin.UseDQS = chkDqs.Checked; ApplyPose(); } };
            MkLbl("Weight smoothing passes:", t3, ref y);
            numSmooth = new NumericUpDown(); numSmooth.Minimum = 0; numSmooth.Maximum = 12; numSmooth.Value = 3; numSmooth.Left = 8; numSmooth.Top = y; numSmooth.Width = 60; t3.Controls.Add(numSmooth);
            btnRecalc = new Button(); btnRecalc.Text = "Recalculate weights"; btnRecalc.Left = 80; btnRecalc.Top = y - 2; btnRecalc.Width = 264; btnRecalc.Height = 28;
            btnRecalc.Click += delegate { if (obj != null) { RebuildSkinner(); ApplyPose(); } }; t3.Controls.Add(btnRecalc); y += 38;
            MkHeader("Hand orientation in the T-Pose", t3, ref y);
            cmbHandMode = new ComboBox(); cmbHandMode.DropDownStyle = ComboBoxStyle.DropDownList; cmbHandMode.Left = 8; cmbHandMode.Top = y; cmbHandMode.Width = 336;
            cmbHandMode.Items.AddRange(new object[] { "Auto-detect", "Palms down (thumbs forward)", "Palms forward (thumbs up)" }); cmbHandMode.SelectedIndex = 0;
            cmbHandMode.SelectedIndexChanged += delegate { if (obj != null) { RebuildSkinner(); ApplyPose(); } };
            t3.Controls.Add(cmbHandMode); y += 36;
            MkHeader("Move a joint (when the auto-rig is wrong)", t3, ref y);
            MkLbl("Tenths of % of body height. Turn on 'Show skeleton' in the Export && View tab.", t3, ref y);
            rigPick = new JointPicker(t3, ref y); rigPick.Changed += delegate { SyncRigSliders(); };
            for (int i = 0; i < 3; i++)
            {
                rtbl[i] = new Label(); rtbl[i].Text = ax[i] + ": 0"; rtbl[i].Left = 8; rtbl[i].Top = y + 6; rtbl[i].Width = 55;
                rtb[i] = new TrackBar(); rtb[i].Minimum = -100; rtb[i].Maximum = 100; rtb[i].TickFrequency = 25; rtb[i].Left = 63; rtb[i].Top = y; rtb[i].Width = 280;
                rtb[i].Scroll += OnRigSlider; t3.Controls.Add(rtbl[i]); t3.Controls.Add(rtb[i]); y += 40;
            }
            btnRigReset = MkBtn("Reset all joint positions", t3, ref y, false);
            btnRigReset.Click += delegate { for (int i = 0; i < Skeleton.N; i++) rigOff[i] = new double[3]; SyncRigSliders(); if (obj != null) { RebuildSkinner(); ApplyPose(); } };
            rigTimer.Interval = 350; rigTimer.Tick += delegate { rigTimer.Stop(); if (obj != null) { RebuildSkinner(); ApplyPose(); } };

            // ---- tab 4: export & view ----
            y = 10;
            MkHeader("Export", t4, ref y);
            btnExport = MkBtn("Export posed OBJ...", t4, ref y, true); btnExport.Click += delegate { OnExport(); };
            chkEmbed = MkChk("Embed T-Pose restore data in the OBJ (+10% size)", t4, ref y); chkEmbed.Checked = true;
            Tip(chkEmbed, "Lets 'Restore T-Pose' bring the exact original T-Pose back from the posed file. Normal OBJ viewers ignore it (comment lines).");
            btnGlb = MkBtn("Export GLB (skeleton + weights, pose applied)...", t4, ref y, false); btnGlb.Click += delegate { OnExportGlb(false); };
            btnGlbAnim = MkBtn("Export GLB with animation (T-Pose -> pose)...", t4, ref y, false); btnGlbAnim.Click += delegate { OnExportGlb(true); };
            btnBatch = MkBtn("Batch export (many files / poses)...", t4, ref y, false); btnBatch.Click += delegate { OnBatch(); };
            MkHeader("Restore", t4, ref y);
            btnRestore = MkBtn("Restore T-Pose from a posed OBJ...", t4, ref y, false); btnRestore.Click += delegate { OnRestore(); };
            Tip(btnRestore, "Turns an OBJ posed by ObjPoser back into its T-Pose. For other files you can pick the original T-Pose OBJ (same vertex order).");
            MkHeader("Screenshots", t4, ref y);
            btnShot = MkBtn("Save screenshot of the view (.png)...", t4, ref y, false); btnShot.Click += delegate { OnShot(); };
            btnShotAll = MkBtn("Screenshots of ALL poses (sheet + files)...", t4, ref y, false); btnShotAll.Click += delegate { OnShotAll(); };
            MkHeader("View", t4, ref y);
            chkSkel = MkChk("Show skeleton", t4, ref y); chkSkel.CheckedChanged += delegate { view.ShowSkeleton = chkSkel.Checked; view.Invalidate(); };
            chkWire = MkChk("Wireframe", t4, ref y); chkWire.CheckedChanged += delegate { view.Wire = chkWire.Checked; view.Invalidate(); };
            chkGhost = MkChk("Show original T-Pose (ghost)", t4, ref y); chkGhost.CheckedChanged += delegate { view.ShowGhost = chkGhost.Checked; view.Invalidate(); };
            chkFloor = MkChk("Floor grid", t4, ref y); chkFloor.Checked = true; chkFloor.CheckedChanged += delegate { view.ShowFloor = chkFloor.Checked; view.Invalidate(); };
            chkTex = MkChk("Materials / textures", t4, ref y); chkTex.Checked = true; chkTex.CheckedChanged += delegate { view.UseTextures = chkTex.Checked; view.ClearBrushCache(); view.Invalidate(); };
            chkThumbs = MkChk("Pose thumbnails (turn off for very heavy models)", t4, ref y); chkThumbs.Checked = true;
            btnResetView = MkBtn("Reset view", t4, ref y, false); btnResetView.Click += delegate { ResetView(); };
            MkLbl("Textures: PNG, JPG, BMP, GIF, TIFF, TGA (TGASharpLib), DDS (DDSReaderSharp).", t4, ref y);

            BuildMenus();
            modelInfo = new Label(); modelInfo.Dock = DockStyle.None;

            statusBar.Items.Add(statusLbl); statusLbl.Spring = true; statusLbl.TextAlign = ContentAlignment.MiddleLeft;
            progress.Visible = false; progress.Width = 160; statusBar.Items.Add(progress);
            statusLbl.Text = "Open a T-Pose OBJ to start.   Mouse: drag = rotate, wheel = zoom.   Left side of the character = +X, front = +Z." +
                (extraPoses > 0 ? "   |   " + extraPoses + " extra poses loaded from the \"poses\" folder." : "") +
                (extraPoseErrors.Count > 0 ? "   |   " + extraPoseErrors.Count + " pose file(s) skipped: " + extraPoseErrors[0] : "");
            Controls.Add(statusBar); Controls.Add(toolbar); Controls.Add(menu); MainMenuStrip = menu;
            UpdateEnabled();
        }

        // ---------- UI helpers ----------
        void Tip(Control c, string text) { tips.SetToolTip(c, text); }
        Button MkBtn(string t, Control p, ref int y, bool primary)
        {
            Button b = new Button(); b.Text = t; b.Left = 8; b.Top = y; b.Width = 336; b.Height = 32;
            if (primary) { b.FlatStyle = FlatStyle.Flat; b.BackColor = Accent; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; b.Font = new Font(Font, FontStyle.Bold); }
            p.Controls.Add(b); y += 38; return b;
        }
        CheckBox MkChk(string t, Control p, ref int y) { CheckBox c = new CheckBox(); c.Text = t; c.Left = 8; c.Top = y; c.Width = 340; p.Controls.Add(c); y += 26; return c; }
        Label MkLbl(string t, Control p, ref int y) { Label l = new Label(); l.Text = t; l.Left = 8; l.Top = y + 2; l.Width = 336; l.Height = 34; l.ForeColor = Color.DimGray; p.Controls.Add(l); y += 36; return l; }
        void MkHeader(string t, Control p, ref int y)
        {
            y += 4;
            Label l = new Label(); l.Text = t; l.Left = 8; l.Top = y; l.Width = 336; l.Height = 20; l.Font = new Font(Font, FontStyle.Bold); l.ForeColor = Accent; p.Controls.Add(l);
            Label line = new Label(); line.Left = 8; line.Top = y + 21; line.Width = 336; line.Height = 1; line.BackColor = Color.FromArgb(215, 215, 220); p.Controls.Add(line);
            y += 28;
        }

        ToolStripMenuItem Mi(ToolStripMenuItem parent, string text, Keys keys, EventHandler h, bool model)
        {
            ToolStripMenuItem m = new ToolStripMenuItem(text); if (keys != Keys.None) m.ShortcutKeys = keys; m.Click += h; parent.DropDownItems.Add(m);
            if (model) needsModel.Add(m); return m;
        }

        void BuildMenus()
        {
            ToolStripMenuItem file = new ToolStripMenuItem("&File"), edit = new ToolStripMenuItem("&Edit"), vw = new ToolStripMenuItem("&View"), help = new ToolStripMenuItem("&Help");
            Mi(file, "Open OBJ...", Keys.Control | Keys.O, delegate { OnOpen(); }, false);
            file.DropDownItems.Add(new ToolStripSeparator());
            Mi(file, "Export posed OBJ...", Keys.Control | Keys.E, delegate { OnExport(); }, true);
            Mi(file, "Export GLB...", Keys.Control | Keys.G, delegate { OnExportGlb(false); }, true);
            Mi(file, "Export GLB with animation...", Keys.None, delegate { OnExportGlb(true); }, true);
            Mi(file, "Batch export...", Keys.None, delegate { OnBatch(); }, false);
            file.DropDownItems.Add(new ToolStripSeparator());
            Mi(file, "Restore T-Pose from a posed OBJ...", Keys.Control | Keys.T, delegate { OnRestore(); }, false);
            file.DropDownItems.Add(new ToolStripSeparator());
            Mi(file, "Screenshot of the view...", Keys.F12, delegate { OnShot(); }, true);
            Mi(file, "Screenshots of all poses...", Keys.F5, delegate { OnShotAll(); }, true);
            file.DropDownItems.Add(new ToolStripSeparator());
            Mi(file, "Exit", Keys.Alt | Keys.F4, delegate { Close(); }, false);
            Mi(edit, "Undo", Keys.Control | Keys.Z, delegate { DoUndo(); }, true);
            Mi(edit, "Redo", Keys.Control | Keys.Y, delegate { DoRedo(); }, true);
            edit.DropDownItems.Add(new ToolStripSeparator());
            Mi(edit, "Mirror pose", Keys.Control | Keys.M, delegate { OnMirror(); }, true);
            Mi(edit, "Save pose...", Keys.None, delegate { OnSavePose(); }, true);
            Mi(edit, "Load pose...", Keys.None, delegate { OnLoadPose(); }, true);
            Mi(edit, "Open extra poses folder...", Keys.None, delegate { OnOpenPoseFolder(); }, false);
            Mi(vw, "Reset view", Keys.Control | Keys.D0, delegate { ResetView(); }, false);
            Mi(vw, "Toggle skeleton", Keys.Control | Keys.K, delegate { chkSkel.Checked = !chkSkel.Checked; }, false);
            Mi(vw, "Toggle wireframe", Keys.Control | Keys.W, delegate { chkWire.Checked = !chkWire.Checked; }, false);
            Mi(vw, "Toggle ghost T-Pose", Keys.Control | Keys.H, delegate { chkGhost.Checked = !chkGhost.Checked; }, false);
            Mi(help, "GitHub page", Keys.None, delegate { try { System.Diagnostics.Process.Start(AppInfo.Repo); } catch { } }, false);
            Mi(help, "About ObjPoser", Keys.F1, delegate { new AboutDialog().ShowDialog(this); }, false);
            menu.Items.AddRange(new ToolStripItem[] { file, edit, vw, help });

            toolbar.GripStyle = ToolStripGripStyle.Hidden; toolbar.RenderMode = ToolStripRenderMode.System;
            Action<string, string, EventHandler, bool> tbtn = delegate (string text, string tip, EventHandler h, bool model)
            {
                ToolStripButton b = new ToolStripButton(text); b.ToolTipText = tip; b.Click += h; b.DisplayStyle = ToolStripItemDisplayStyle.Text; toolbar.Items.Add(b); if (model) needsModel.Add(b);
            };
            tbtn("Open", "Open OBJ (Ctrl+O)", delegate { OnOpen(); }, false);
            tbtn("Export OBJ", "Export posed OBJ (Ctrl+E)", delegate { OnExport(); }, true);
            tbtn("Export GLB", "Export GLB with skeleton (Ctrl+G)", delegate { OnExportGlb(false); }, true);
            toolbar.Items.Add(new ToolStripSeparator());
            tbtn("Restore T-Pose", "Restore a posed OBJ to its T-Pose (Ctrl+T)", delegate { OnRestore(); }, false);
            tbtn("Screenshots", "Screenshots of all poses (F5)", delegate { OnShotAll(); }, true);
            toolbar.Items.Add(new ToolStripSeparator());
            tbtn("Undo", "Undo (Ctrl+Z)", delegate { DoUndo(); }, true);
            tbtn("Redo", "Redo (Ctrl+Y)", delegate { DoRedo(); }, true);
            tbtn("Mirror", "Mirror pose left/right (Ctrl+M)", delegate { OnMirror(); }, true);
            toolbar.Items.Add(new ToolStripSeparator());
            tbtn("About", "About ObjPoser (F1)", delegate { new AboutDialog().ShowDialog(this); }, false);
        }

        void UpdateEnabled()
        {
            bool has = skin != null;
            foreach (ToolStripItem i in needsModel) i.Enabled = has;
            foreach (Control c in new Control[] { btnExport, btnGlb, btnGlbAnim, btnShot, btnShotAll, btnMirror, btnUndo, btnRedo, btnSavePose, btnLoadPose, btnHand, btnExpr, btnResetJoint })
                if (c != null) c.Enabled = has;
        }

        void SetStatus(string s) { statusLbl.Text = s; statusBar.Refresh(); }
        void ResetView() { view.Yaw = 0; view.Pitch = 0.15; view.Zoom = 1; view.Invalidate(); }

        // ---------- undo / redo ----------
        void PushUndo() { if (cur == null) return; undo.Push(cur.Clone()); if (undo.Count > 100) { Pose[] a = undo.ToArray(); undo.Clear(); for (int i = 99; i >= 0; i--) undo.Push(a[i]); } redo.Clear(); }
        void DoUndo() { if (undo.Count == 0 || cur == null) return; redo.Push(cur.Clone()); cur = undo.Pop(); basePose = cur.Clone(); blendTb.Value = 0; ApplyPose(); SyncSliders(); }
        void DoRedo() { if (redo.Count == 0 || cur == null) return; undo.Push(cur.Clone()); cur = redo.Pop(); basePose = cur.Clone(); blendTb.Value = 0; ApplyPose(); SyncSliders(); }
        void AfterEdit() { basePose = cur.Clone(); blendTb.Value = 0; ApplyPose(); SyncSliders(); }

        // ---------- loading ----------
        void OnOpen()
        {
            OpenFileDialog d = new OpenFileDialog(); d.Filter = "OBJ files (*.obj)|*.obj";
            if (d.ShowDialog() != DialogResult.OK) return;
            LoadFile(d.FileName, true);
        }

        public bool LoadFile(string path, bool interactive)
        {
            try
            {
                ObjData o = ObjData.Load(path);
                if (o.V.Count == 0 || o.Tris.Count == 0) throw new Exception("No vertices/faces found in the file.");
                bool restored = false;
                if (o.RestV != null)
                {
                    DialogResult rr = interactive ? MessageBox.Show("This OBJ was posed with ObjPoser (" + o.MetaInfo + ").\n\nLoad its original T-Pose?\n\nYes = original T-Pose,  No = load it as it is.", "Posed file detected", MessageBoxButtons.YesNo, MessageBoxIcon.Question) : DialogResult.Yes;
                    if (rr == DialogResult.Yes) restored = o.ApplyRestMeta();
                }
                AxisMap m = BuildMap(o);
                double[] r = ToWork(o, m);
                List<string> issues = Analysis.CheckTPose(r, o.V.Count);
                if (issues.Count > 0 && interactive)
                {
                    DialogResult dr = MessageBox.Show("This file does not look like a T-Pose:\n\n - " + string.Join("\n - ", issues.ToArray()) +
                        "\n\nThe result may be distorted. Continue anyway?\n(Tip: File > Restore T-Pose can bring back a pose made by ObjPoser.)", "T-Pose check", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (dr != DialogResult.Yes) return false;
                }
                obj = o; objPath = path; map = m; rest = r; trisArr = o.Tris.ToArray();
                for (int i = 0; i < Skeleton.N; i++) rigOff[i] = new double[3];
                SyncRigSliders();
                LoadTextures();
                RebuildSkinner();
                view.Obj = obj; view.Tex = textures; view.RestP = rest; view.ClearBrushCache();
                undo.Clear(); redo.Clear(); cur = null;
                UpdateEnabled();
                MakeThumbnails();
                PoseIndex = -1; PoseIndex = 0;
                string tp = issues.Count == 0 ? "T-Pose check: OK" : "T-Pose check: WARNING (" + issues.Count + ")";
                int tx = 0; foreach (Bitmap b in textures) if (b != null) tx++;
                SetStatus(Path.GetFileName(path) + "  |  " + obj.V.Count + " vertices, " + obj.Tris.Count / 3 + " triangles  |  " + tp + (chkAuto.Checked ? "  |  axes " + axisInfo : "") +
                    "  |  hands: " + (skin.Skel.PalmsForward ? "palms forward" : "palms down") + "  |  textures " + tx + "/" + textures.Length + (restored ? "  |  original T-Pose restored from file" : ""));
                Text = "ObjPoser v" + AppInfo.Version + "  -  " + Path.GetFileName(path);
                return true;
            }
            catch (Exception ex) { if (interactive) MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); else throw; return false; }
        }

        AxisMap BuildMap(ObjData o)
        {
            if (chkAuto.Checked) return AxisMap.Detect(o.V, out axisInfo);
            return AxisMap.FromOptions(chkZUp.Checked, chkFlip.Checked);
        }

        static double[] ToWork(ObjData o, AxisMap m)
        {
            int nv = o.V.Count; double[] r = new double[nv * 3]; double[] t = new double[3];
            for (int i = 0; i < nv; i++) { m.ToWork(o.V[i][0], o.V[i][1], o.V[i][2], t); r[i * 3] = t[0]; r[i * 3 + 1] = t[1]; r[i * 3 + 2] = t[2]; }
            return r;
        }

        void LoadTextures()
        {
            if (textures != null) foreach (Bitmap b in textures) if (b != null) b.Dispose();
            textures = new Bitmap[obj.Materials.Count];
            for (int i = 0; i < obj.Materials.Count; i++)
            {
                string f = obj.Materials[i].MapKd; if (string.IsNullOrEmpty(f)) continue;
                try { string p = TextureLoader.Find(obj.Dir, f); if (p != null) textures[i] = TextureLoader.Load(p); } catch { }
            }
        }

        void OnAxisChanged(object s, EventArgs e)
        {
            chkZUp.Enabled = chkFlip.Enabled = !chkAuto.Checked;
            if (obj == null) return;
            map = BuildMap(obj); rest = ToWork(obj, map); view.RestP = rest;
            RebuildSkinner(); ApplyPose(); MakeThumbnails();
        }

        void RebuildSkinner()
        {
            if (obj == null) return;
            Cursor = Cursors.WaitCursor; SetStatus("Calculating skeleton and skin weights..."); Application.DoEvents();
            Skeleton sk = Skeleton.Build(rest, obj.V.Count, cmbHandMode.SelectedIndex);
            sk.ApplyOffsets(rigOff);
            bool dqs = chkDqs.Checked;
            skin = new Skinner(rest, obj.V.Count, trisArr, sk, (int)numSmooth.Value);
            skin.UseDQS = dqs;
            view.Skel = skin.Skel;
            Cursor = Cursors.Default;
            SetStatus("Ready.  " + Path.GetFileName(objPath));
        }

        // ---------- rendering poses off-screen (thumbnails, screenshots) ----------
        Bitmap CapturePose(Pose p, int w, int h, bool tex, bool floor, double yaw, double pitch)
        {
            PosedResult pr = skin.Apply(p);
            int[] oT = view.Tris; double[] oP = view.P; Xf[] oG = view.G; double oDy = view.Dy; bool oS = view.ShowSkeleton, oGh = view.ShowGhost, oF = view.ShowFloor, oW = view.Wire;
            int oGN = view.GridN; view.GridN = 5; view.Tris = trisArr; view.P = pr.P; view.G = pr.G; view.Dy = pr.Dy; view.ShowSkeleton = false; view.ShowGhost = false; view.ShowFloor = floor; view.Wire = false;
            view.RecalcBounds();
            Bitmap b = view.Capture(w, h, yaw, pitch, tex, skin.Skel.H * 1.12);
            view.GridN = oGN; view.Tris = oT; view.P = oP; view.G = oG; view.Dy = oDy; view.ShowSkeleton = oS; view.ShowGhost = oGh; view.ShowFloor = oF; view.Wire = oW;
            if (view.P != null) view.RecalcBounds();
            return b;
        }

        void MakeThumbnails()
        {
            thumbs.Images.Clear();
            for (int i = 0; i < lstPoses.Items.Count; i++) lstPoses.Items[i].ImageIndex = -1;
            if (skin == null || !chkThumbs.Checked || trisArr.Length / 3 > 40000) return;
            Cursor = Cursors.WaitCursor; progress.Visible = true; progress.Minimum = 0; progress.Maximum = presets.Count; progress.Value = 0;
            for (int i = 0; i < presets.Count; i++)
            {
                SetStatus("Creating pose thumbnails " + (i + 1) + "/" + presets.Count + " ...");
                using (Bitmap b = CapturePose(presets[i], 88, 108, false, false, -0.5, 0.15)) thumbs.Images.Add((Bitmap)b.Clone());
                lstPoses.Items[i].ImageIndex = i; progress.Value = i + 1; Application.DoEvents();
            }
            progress.Visible = false; Cursor = Cursors.Default;
            view.Invalidate();
        }

        static string SafeName(string s) { foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Replace(' ', '_').Replace('(', '_').Replace(')', '_'); }

        void OnShotAll()
        {
            if (skin == null) return;
            FolderBrowserDialog fd = new FolderBrowserDialog(); fd.Description = "Folder for the pose screenshots (a sheet + one PNG per pose)";
            if (objPath != null) fd.SelectedPath = Path.GetDirectoryName(objPath);
            if (fd.ShowDialog() != DialogResult.OK) return;
            ShotAll(fd.SelectedPath, true);
        }

        public string ShotAll(string dir, bool interactive)
        {
            int cw = 380, ch = 470, cols = 4, titleH = 34, n = presets.Count, rows = (n + cols - 1) / cols;
            Cursor = Cursors.WaitCursor; progress.Visible = true; progress.Minimum = 0; progress.Maximum = n; progress.Value = 0;
            using (Bitmap sheet = new Bitmap(cols * cw, rows * (ch + titleH)))
            using (Graphics g = Graphics.FromImage(sheet))
            using (Font f = new Font("Segoe UI", 13f, FontStyle.Bold))
            {
                g.Clear(view.BackColor);
                for (int i = 0; i < n; i++)
                {
                    SetStatus("Rendering pose " + (i + 1) + "/" + n + ": " + presets[i].Name);
                    using (Bitmap b = CapturePose(presets[i], cw, ch, chkTex.Checked, true, -0.55, 0.2))
                    {
                        b.Save(Path.Combine(dir, "pose_" + (i + 1).ToString("00") + "_" + SafeName(presets[i].Name) + ".png"), ImageFormat.Png);
                        int x = (i % cols) * cw, yy = (i / cols) * (ch + titleH);
                        g.DrawImage(b, x, yy + titleH);
                        g.DrawString(presets[i].Name, f, Brushes.White, x + 10, yy + 6);
                    }
                    progress.Value = i + 1; Application.DoEvents();
                }
                string sp = Path.Combine(dir, "poses_sheet.png"); sheet.Save(sp, ImageFormat.Png);
                progress.Visible = false; Cursor = Cursors.Default; SetStatus("Saved " + n + " pose screenshots in " + dir);
                ApplyPose();
                if (interactive) MessageBox.Show("Saved the sheet (poses_sheet.png) and " + n + " separate PNG files in:\n" + dir, "Screenshots");
                return sp;
            }
        }

        // ---------- pose handling ----------
        void OnPoseSelected(object s, EventArgs e)
        {
            if (PoseIndex < 0 || skin == null) return;
            PushUndo();
            basePose = presets[PoseIndex].Clone();
            cur = basePose.Clone();
            blendTb.Value = 0;
            ApplyPose(); SyncSliders();
        }

        void OnBlend(object s, EventArgs e)
        {
            if (basePose == null) return;
            cur = Pose.Lerp(basePose, presets[cmbBlend.SelectedIndex], blendTb.Value / 100.0);
            ApplyPose(); SyncSliders();
        }

        void SyncSliders()
        {
            if (cur == null) return;
            updating = true;
            int j = pick.Current; string[] ax = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                tb[i].Value = (int)Math.Max(-180, Math.Min(180, Math.Round(cur.Rot[j][i]))); tbl[i].Text = ax[i] + ": " + tb[i].Value;
                ttb[i].Value = (int)Math.Max(-100, Math.Min(100, Math.Round(cur.Trans[j][i] * 1000))); ttbl[i].Text = ax[i] + ": " + ttb[i].Value;
            }
            updating = false;
        }

        void OnSlider(object s, EventArgs e)
        {
            if (updating || cur == null) return;
            int j = pick.Current; string[] ax = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                cur.Rot[j][i] = tb[i].Value; tbl[i].Text = ax[i] + ": " + tb[i].Value;
                cur.Trans[j][i] = ttb[i].Value / 1000.0; ttbl[i].Text = ax[i] + ": " + ttb[i].Value;
            }
            int mj = Skeleton.Mirror[j];
            if (chkSym.Checked && mj != j)
            {
                cur.Rot[mj] = Pose.MirrorRot(j, cur.Rot[j]);
                cur.Trans[mj] = new double[] { -cur.Trans[j][0], cur.Trans[j][1], cur.Trans[j][2] };
            }
            basePose = cur.Clone(); blendTb.Value = 0;
            ApplyPose();
        }

        void OnMirror() { if (cur == null) return; PushUndo(); cur = cur.Mirrored(); AfterEdit(); }

        void OnHand()
        {
            if (cur == null) return;
            PushUndo();
            string kind = cmbHandKind.SelectedItem.ToString(); int side = cmbHandSide.SelectedIndex;
            if (side != 2) cur.Hand("L", kind);
            if (side != 1) cur.Hand("R", kind);
            AfterEdit();
        }

        void OnExpression() { if (cur == null) return; PushUndo(); cur.Expression(cmbExpr.SelectedItem.ToString()); AfterEdit(); }

        void OnSavePose()
        {
            if (cur == null) return;
            SaveFileDialog d = new SaveFileDialog(); d.Filter = "Pose JSON (*.json)|*.json";
            d.FileName = cur.Name.Split(' ', '(')[0] + ".pose.json";
            if (d.ShowDialog() != DialogResult.OK) return;
            try { File.WriteAllText(d.FileName, cur.ToJson()); SetStatus("Pose saved: " + Path.GetFileName(d.FileName)); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
        }

        // The "poses" folder holds extra pose JSON files that are loaded when the program starts.
        void OnOpenPoseFolder()
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "poses");
                if (Directory.Exists(extraPoseDir)) dir = extraPoseDir;
                Directory.CreateDirectory(dir);
                Process.Start(dir);
                SetStatus("Extra poses: put *.json pose files in " + dir + " and restart ObjPoser (currently loaded: " + extraPoses + ").");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
        }

        void OnLoadPose()
        {
            OpenFileDialog d = new OpenFileDialog(); d.Filter = "Pose JSON (*.json)|*.json";
            if (d.ShowDialog() != DialogResult.OK) return;
            try
            {
                Pose p = Pose.FromJson(File.ReadAllText(d.FileName));
                presets.Add(p); cmbBlend.Items.Add(p.Name);
                lstPoses.Items.Add(p.Name, -1);
                if (skin != null) { using (Bitmap b = CapturePose(p, 88, 108, false, false, -0.5, 0.15)) thumbs.Images.Add((Bitmap)b.Clone()); lstPoses.Items[lstPoses.Items.Count - 1].ImageIndex = thumbs.Images.Count - 1; PoseIndex = lstPoses.Items.Count - 1; }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
        }

        void ApplyPose()
        {
            if (skin == null || cur == null) return;
            posed = skin.Apply(cur);
            view.G = posed.G; view.Dy = posed.Dy;
            view.SetMesh(posed.P, trisArr);
        }

        // ---------- rig editing ----------
        void SyncRigSliders()
        {
            if (rigPick == null) return;
            updating = true;
            double[] r = rigOff[rigPick.Current]; string[] ax = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++) { rtb[i].Value = (int)Math.Max(-100, Math.Min(100, Math.Round(r[i] * 1000))); rtbl[i].Text = ax[i] + ": " + rtb[i].Value; }
            updating = false;
        }

        void OnRigSlider(object s, EventArgs e)
        {
            if (updating) return;
            double[] r = rigOff[rigPick.Current]; string[] ax = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++) { r[i] = rtb[i].Value / 1000.0; rtbl[i].Text = ax[i] + ": " + rtb[i].Value; }
            rigTimer.Stop(); rigTimer.Start();
        }

        // ---------- export / restore ----------
        string DefaultName() { string nm = cur.Name.Split(' ', '(')[0]; return Path.GetFileNameWithoutExtension(objPath) + "_" + nm; }

        void OnExport()
        {
            if (posed == null) return;
            SaveFileDialog d = new SaveFileDialog(); d.Filter = "OBJ files (*.obj)|*.obj";
            d.FileName = DefaultName() + ".obj"; d.InitialDirectory = Path.GetDirectoryName(objPath);
            if (d.ShowDialog() != DialogResult.OK) return;
            try { ObjExporter.Save(d.FileName, obj, map, skin, posed, chkEmbed.Checked, cur.Name); MessageBox.Show("Saved:\n" + d.FileName + (chkEmbed.Checked ? "\n\nThis file can be returned to its T-Pose with File > Restore T-Pose." : ""), "Done"); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void OnExportGlb(bool anim)
        {
            if (posed == null) return;
            SaveFileDialog d = new SaveFileDialog(); d.Filter = "glTF binary (*.glb)|*.glb";
            d.FileName = DefaultName() + (anim ? "_anim" : "") + ".glb"; d.InitialDirectory = Path.GetDirectoryName(objPath);
            if (d.ShowDialog() != DialogResult.OK) return;
            try { GlbExporter.Save(d.FileName, obj, skin, cur, posed.Dy, anim); MessageBox.Show("Saved:\n" + d.FileName + "\n\nOpen it in Blender (File > Import > glTF) to keep animating.", "Done"); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void OnRestore()
        {
            OpenFileDialog od = new OpenFileDialog(); od.Filter = "OBJ files (*.obj)|*.obj"; od.Title = "Select the POSED OBJ you want to return to T-Pose";
            if (od.ShowDialog() != DialogResult.OK) return;
            SaveFileDialog sd = new SaveFileDialog(); sd.Filter = "OBJ files (*.obj)|*.obj";
            sd.FileName = Path.GetFileNameWithoutExtension(od.FileName) + "_TPose.obj"; sd.InitialDirectory = Path.GetDirectoryName(od.FileName); sd.Title = "Save the T-Pose OBJ as";
            if (sd.ShowDialog() != DialogResult.OK) return;
            try
            {
                string how;
                try { how = ObjRestore.Restore(od.FileName, sd.FileName, null); }
                catch (NoRestoreDataException)
                {
                    DialogResult dr = MessageBox.Show("This file has no ObjPoser restore data (it was not exported by ObjPoser with 'Embed restore data').\n\n" +
                        "If you still have the ORIGINAL T-Pose OBJ of the same model (same vertex order), you can pick it and the tool will restore from it.\n\nPick the original T-Pose file now?",
                        "No restore data", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes) return;
                    OpenFileDialog rd = new OpenFileDialog(); rd.Filter = "OBJ files (*.obj)|*.obj"; rd.Title = "Select the ORIGINAL T-Pose OBJ of this model";
                    if (rd.ShowDialog() != DialogResult.OK) return;
                    how = ObjRestore.Restore(od.FileName, sd.FileName, rd.FileName);
                }
                MessageBox.Show("T-Pose restored (" + how + ").\n\n" + sd.FileName, "Done");
                SetStatus("Restored T-Pose: " + Path.GetFileName(sd.FileName));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void OnShot()
        {
            SaveFileDialog d = new SaveFileDialog(); d.Filter = "PNG (*.png)|*.png"; d.FileName = "pose.png";
            if (d.ShowDialog() != DialogResult.OK) return;
            using (Bitmap bmp = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(bmp, new Rectangle(0, 0, view.Width, view.Height));
                bmp.Save(d.FileName, ImageFormat.Png);
            }
        }

        void OnBatch()
        {
            BatchDialog bd = new BatchDialog(presets);
            if (bd.ShowDialog() != DialogResult.OK) return;
            List<Pose> chosen = new List<Pose>();
            foreach (int i in bd.List.CheckedIndices) chosen.Add(presets[i]);
            if (chosen.Count == 0) { MessageBox.Show("Select at least one pose."); return; }
            OpenFileDialog od = new OpenFileDialog(); od.Filter = "OBJ files (*.obj)|*.obj"; od.Multiselect = true; od.Title = "Select the T-Pose OBJ files";
            if (od.ShowDialog() != DialogResult.OK) return;
            FolderBrowserDialog fd = new FolderBrowserDialog(); fd.Description = "Output folder";
            if (fd.ShowDialog() != DialogResult.OK) return;
            bool doObj = bd.RObj.Checked || bd.RBoth.Checked, doGlb = bd.RGlb.Checked || bd.RBoth.Checked;
            int ok = 0, fail = 0; List<string> log = new List<string>();
            Cursor = Cursors.WaitCursor; progress.Visible = true; progress.Minimum = 0; progress.Maximum = od.FileNames.Length; progress.Value = 0;
            foreach (string f in od.FileNames)
            {
                try
                {
                    SetStatus("Batch: " + Path.GetFileName(f) + " ..."); Application.DoEvents();
                    ObjData o = ObjData.Load(f); if (o.RestV != null) o.ApplyRestMeta();
                    string info; AxisMap m = chkAuto.Checked ? AxisMap.Detect(o.V, out info) : AxisMap.FromOptions(chkZUp.Checked, chkFlip.Checked);
                    double[] r = ToWork(o, m);
                    if (Analysis.CheckTPose(r, o.V.Count).Count > 0) log.Add(Path.GetFileName(f) + ": does not look like a T-Pose");
                    Skeleton sk = Skeleton.Build(r, o.V.Count, cmbHandMode.SelectedIndex);
                    Skinner sn = new Skinner(r, o.V.Count, o.Tris.ToArray(), sk, (int)numSmooth.Value); sn.UseDQS = chkDqs.Checked;
                    foreach (Pose p in chosen)
                    {
                        PosedResult pr = sn.Apply(p);
                        string nm = Path.GetFileNameWithoutExtension(f) + "_" + p.Name.Split(' ', '(')[0];
                        if (doObj) ObjExporter.Save(Path.Combine(fd.SelectedPath, nm + ".obj"), o, m, sn, pr, chkEmbed.Checked, p.Name);
                        if (doGlb) GlbExporter.Save(Path.Combine(fd.SelectedPath, nm + ".glb"), o, sn, p, pr.Dy, bd.ChkAnim.Checked);
                    }
                    ok++;
                }
                catch (Exception ex) { fail++; log.Add(Path.GetFileName(f) + ": " + ex.Message); }
                progress.Value = Math.Min(progress.Maximum, progress.Value + 1);
            }
            Cursor = Cursors.Default; progress.Visible = false;
            SetStatus("Batch finished: " + ok + " ok, " + fail + " failed.");
            MessageBox.Show("Done: " + ok + " file(s) exported, " + fail + " failed.\n" + string.Join("\n", log.ToArray()), "Batch export");
        }

        // used by automated tests
        internal Viewport TestView { get { return view; } }
        internal void TestSelectPose(int i) { PoseIndex = -1; PoseIndex = i; }
        internal void TestPickJoint(string name) { pick.Select(Skeleton.Idx(name)); }
        internal void TestSelectTab(int i) { foreach (Control c in Controls) { TabControl t = c as TabControl; if (t != null) t.SelectedIndex = i; } }
    }
}
