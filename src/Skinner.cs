using System;
using System.Collections.Generic;

namespace ObjPoser
{
    public class PosedResult
    {
        public double[] P;
        public Xf[] G;
        public double Dy;
        public double[][] DQ;
    }

    public class Skinner
    {
        public Skeleton Skel;
        public double[] Rest;
        public int NV;
        public int[] BIdx;
        public double[] BW;
        public bool UseDQS = false;

        static double SegDist(double px, double py, double pz, double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
            double l2 = dx * dx + dy * dy + dz * dz;
            double t = l2 < 1e-12 ? 0 : ((px - a[0]) * dx + (py - a[1]) * dy + (pz - a[2]) * dz) / l2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double qx = a[0] + t * dx - px, qy = a[1] + t * dy - py, qz = a[2] + t * dz - pz;
            return Math.Sqrt(qx * qx + qy * qy + qz * qz);
        }

        public Skinner(double[] rest, int nv, int[] tris, Skeleton skel, int smoothIters)
        {
            Rest = rest; NV = nv; Skel = skel;
            BIdx = new int[nv * 4]; BW = new double[nv * 4];
            int NB = Skeleton.N;

            // weld vertices that share a position (UV / normal seams) so they get identical weights
            double q = Math.Max(skel.H, 1e-9) * 1e-5;
            Dictionary<string, int> map = new Dictionary<string, int>();
            int[] canon = new int[nv]; int nu = 0; List<int> uniq = new List<int>();
            for (int i = 0; i < nv; i++)
            {
                string key = Math.Round(rest[i * 3] / q) + "," + Math.Round(rest[i * 3 + 1] / q) + "," + Math.Round(rest[i * 3 + 2] / q);
                int id;
                if (!map.TryGetValue(key, out id)) { id = nu++; map[key] = id; uniq.Add(i); }
                canon[i] = id;
            }

            double[] W = new double[nu * NB];
            for (int u = 0; u < nu; u++)
            {
                int i = uniq[u];
                double x = rest[i * 3], y = rest[i * 3 + 1], z = rest[i * 3 + 2], sum = 0;
                for (int j = 0; j < NB; j++)
                {
                    double s = SegDist(x, y, z, skel.Pos[j], skel.End[j]) / skel.Radius[j];
                    double w = 1.0 / (s * s * s + 0.02);
                    W[u * NB + j] = w; sum += w;
                }
                for (int j = 0; j < NB; j++) W[u * NB + j] /= sum;
            }

            if (smoothIters > 0 && tris != null)
            {
                List<int>[] adj = new List<int>[nu];
                for (int u = 0; u < nu; u++) adj[u] = new List<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = canon[tris[t]], b = canon[tris[t + 1]], c = canon[tris[t + 2]];
                    if (a != b) { adj[a].Add(b); adj[b].Add(a); }
                    if (b != c) { adj[b].Add(c); adj[c].Add(b); }
                    if (a != c) { adj[a].Add(c); adj[c].Add(a); }
                }
                double[] W2 = new double[nu * NB];
                for (int it = 0; it < smoothIters; it++)
                {
                    for (int u = 0; u < nu; u++)
                    {
                        List<int> nb = adj[u];
                        if (nb.Count == 0) { for (int j = 0; j < NB; j++) W2[u * NB + j] = W[u * NB + j]; continue; }
                        for (int j = 0; j < NB; j++)
                        {
                            double s = 0;
                            for (int k = 0; k < nb.Count; k++) s += W[nb[k] * NB + j];
                            W2[u * NB + j] = 0.5 * W[u * NB + j] + 0.5 * s / nb.Count;
                        }
                    }
                    double[] tmp = W; W = W2; W2 = tmp;
                }
            }

            double[] w4 = new double[NB];
            for (int i = 0; i < nv; i++)
            {
                int u = canon[i];
                for (int j = 0; j < NB; j++) w4[j] = W[u * NB + j];
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    int best = 0; double bw = -1;
                    for (int j = 0; j < NB; j++) if (w4[j] > bw) { bw = w4[j]; best = j; }
                    BIdx[i * 4 + k] = best; BW[i * 4 + k] = bw; sum += bw; w4[best] = -2;
                }
                for (int k = 0; k < 4; k++) BW[i * 4 + k] /= sum;
            }
        }

        // local rotation of joint j in parent space (palm frame applied for hand / finger / toe joints)
        public double[] LocalRot(Pose pose, int j)
        {
            double[] r = Xf.Euler(pose.Rot[j][0], pose.Rot[j][1], pose.Rot[j][2]);
            double[] f = Skel.Frame[j];
            if (f == null) return r;
            double[] ft = { f[0], f[3], f[6], f[1], f[4], f[7], f[2], f[5], f[8] };
            return Xf.Mul3(f, Xf.Mul3(r, ft));
        }

        public Xf[] ComputeG(Pose pose)
        {
            Xf[] G = new Xf[Skeleton.N];
            for (int j = 0; j < Skeleton.N; j++)
            {
                Xf loc = new Xf();
                loc.R = LocalRot(pose, j);
                int par = Skeleton.Parent[j];
                for (int k = 0; k < 3; k++)
                    loc.T[k] = (par < 0 ? Skel.Pos[j][k] : Skel.Pos[j][k] - Skel.Pos[par][k]) + pose.Trans[j][k] * Skel.H;
                G[j] = par < 0 ? loc : Xf.Mul(G[par], loc);
            }
            return G;
        }

        static double[] QMul(double[] a, double[] b)
        {
            return new double[] {
                a[0]*b[0]-a[1]*b[1]-a[2]*b[2]-a[3]*b[3],
                a[0]*b[1]+a[1]*b[0]+a[2]*b[3]-a[3]*b[2],
                a[0]*b[2]-a[1]*b[3]+a[2]*b[0]+a[3]*b[1],
                a[0]*b[3]+a[1]*b[2]-a[2]*b[1]+a[3]*b[0] };
        }

        double[][] BuildDQ(Xf[] G)
        {
            double[][] dq = new double[Skeleton.N][];
            for (int j = 0; j < Skeleton.N; j++)
            {
                double[] jp = Skel.Pos[j], t = new double[3];
                G[j].Rotate(-jp[0], -jp[1], -jp[2], t);
                double tx = t[0] + G[j].T[0], ty = t[1] + G[j].T[1], tz = t[2] + G[j].T[2];
                double[] qx = Xf.ToQuat(G[j].R);
                double[] r = { qx[3], qx[0], qx[1], qx[2] };
                double[] d = QMul(new double[] { 0, 0.5 * tx, 0.5 * ty, 0.5 * tz }, r);
                dq[j] = new double[] { r[0], r[1], r[2], r[3], d[0], d[1], d[2], d[3] };
            }
            return dq;
        }

        public PosedResult Apply(Pose pose)
        {
            PosedResult r = new PosedResult();
            r.G = ComputeG(pose);
            r.P = new double[NV * 3];
            double[] o = new double[3];
            double minY = 1e30;
            double[][] dq = UseDQS ? BuildDQ(r.G) : null;
            r.DQ = dq;
            double[] acc = new double[8];
            for (int i = 0; i < NV; i++)
            {
                double ax = 0, ay = 0, az = 0;
                double px = Rest[i * 3], py = Rest[i * 3 + 1], pz = Rest[i * 3 + 2];
                if (!UseDQS)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        int j = BIdx[i * 4 + k]; double w = BW[i * 4 + k]; double[] jp = Skel.Pos[j];
                        r.G[j].Apply(px - jp[0], py - jp[1], pz - jp[2], o);
                        ax += w * o[0]; ay += w * o[1]; az += w * o[2];
                    }
                }
                else
                {
                    Array.Clear(acc, 0, 8);
                    double[] first = dq[BIdx[i * 4]];
                    for (int k = 0; k < 4; k++)
                    {
                        int j = BIdx[i * 4 + k]; double w = BW[i * 4 + k]; double[] d = dq[j];
                        double dot = d[0] * first[0] + d[1] * first[1] + d[2] * first[2] + d[3] * first[3];
                        double sg = dot < 0 ? -w : w;
                        for (int m = 0; m < 8; m++) acc[m] += sg * d[m];
                    }
                    double n = Math.Sqrt(acc[0] * acc[0] + acc[1] * acc[1] + acc[2] * acc[2] + acc[3] * acc[3]); if (n < 1e-12) n = 1;
                    for (int m = 0; m < 8; m++) acc[m] /= n;
                    double[] q0 = { acc[0], acc[1], acc[2], acc[3] }, qe = { acc[4], acc[5], acc[6], acc[7] };
                    double[] tq = QMul(qe, new double[] { q0[0], -q0[1], -q0[2], -q0[3] });
                    double tx = 2 * tq[1], ty = 2 * tq[2], tz = 2 * tq[3];
                    double vx = q0[1], vy = q0[2], vz = q0[3], w0 = q0[0];
                    double cx1 = vy * pz - vz * py, cy1 = vz * px - vx * pz, cz1 = vx * py - vy * px;
                    double cx2 = vy * cz1 - vz * cy1, cy2 = vz * cx1 - vx * cz1, cz2 = vx * cy1 - vy * cx1;
                    ax = px + 2 * (w0 * cx1 + cx2) + tx; ay = py + 2 * (w0 * cy1 + cy2) + ty; az = pz + 2 * (w0 * cz1 + cz2) + tz;
                }
                r.P[i * 3] = ax; r.P[i * 3 + 1] = ay; r.P[i * 3 + 2] = az;
                if (ay < minY) minY = ay;
            }
            double dy = pose.LiftH * Skel.H;
            if (pose.Snap) dy += Skel.MinY - minY;
            for (int i = 0; i < NV; i++) r.P[i * 3 + 1] += dy;
            r.Dy = dy;
            return r;
        }

        public void SkinNormal(PosedResult pr, int vi, double[] n, double[] o)
        {
            double ax = 0, ay = 0, az = 0; double[] t = new double[3];
            if (!UseDQS || pr.DQ == null)
            {
                for (int k = 0; k < 4; k++)
                {
                    int j = BIdx[vi * 4 + k]; double w = BW[vi * 4 + k];
                    pr.G[j].Rotate(n[0], n[1], n[2], t);
                    ax += w * t[0]; ay += w * t[1]; az += w * t[2];
                }
            }
            else
            {
                double[] acc = new double[4]; double[] first = pr.DQ[BIdx[vi * 4]];
                for (int k = 0; k < 4; k++)
                {
                    int j = BIdx[vi * 4 + k]; double w = BW[vi * 4 + k]; double[] d = pr.DQ[j];
                    double dot = d[0] * first[0] + d[1] * first[1] + d[2] * first[2] + d[3] * first[3];
                    double sg = dot < 0 ? -w : w;
                    for (int m = 0; m < 4; m++) acc[m] += sg * d[m];
                }
                double nn = Math.Sqrt(acc[0] * acc[0] + acc[1] * acc[1] + acc[2] * acc[2] + acc[3] * acc[3]); if (nn < 1e-12) nn = 1;
                double w0 = acc[0] / nn, vx = acc[1] / nn, vy = acc[2] / nn, vz = acc[3] / nn, px = n[0], py = n[1], pz = n[2];
                double cx1 = vy * pz - vz * py, cy1 = vz * px - vx * pz, cz1 = vx * py - vy * px;
                double cx2 = vy * cz1 - vz * cy1, cy2 = vz * cx1 - vx * cz1, cz2 = vx * cy1 - vy * cx1;
                ax = px + 2 * (w0 * cx1 + cx2); ay = py + 2 * (w0 * cy1 + cy2); az = pz + 2 * (w0 * cz1 + cz2);
            }
            double l = Math.Sqrt(ax * ax + ay * ay + az * az); if (l < 1e-12) l = 1;
            o[0] = ax / l; o[1] = ay / l; o[2] = az / l;
        }
    }
}
