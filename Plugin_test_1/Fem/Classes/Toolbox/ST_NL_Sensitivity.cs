using System;
using System.Collections.Generic;
using System.Linq;

using Rhino.Geometry;

using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    // =======================================================================
    //  Parameter / response description
    // =======================================================================

    public enum NL_ParamType
    {
        /// <summary>Young's modulus of every element whose Section Tag matches.</summary>
        Modulus,
        /// <summary>One component of a nodal point load.</summary>
        PointLoad,
        /// <summary>One component of a node's ORIGINAL coordinate (imperfection).</summary>
        NodeCoordinate
    }

    /// <summary>Describes one differentiation parameter.</summary>
    public class NL_Parameter
    {
        public string Name = "p";
        public NL_ParamType Type = NL_ParamType.Modulus;

        /// <summary>Modulus: section tag to target. Null/empty = all elements.</summary>
        public string SectionTag = null;

        /// <summary>PointLoad / NodeCoordinate: node Id.</summary>
        public int NodeId = 0;

        /// <summary>PointLoad: DOF index 0..5 (fx,fy,fz,mx,my,mz).</summary>
        public int Dof = 0;

        /// <summary>
        /// PointLoad: +1 if the variable IS the applied component, -1 if the
        /// variable is a magnitude applied in the negative direction
        /// (e.g. F2 downward => Dof=2, Scale=-1).
        /// </summary>
        public double Scale = 1.0;

        /// <summary>NodeCoordinate: 0=X, 1=Y, 2=Z.</summary>
        public int Component = 0;

        public static NL_Parameter ForModulus(string name, string sectionTag)
            => new NL_Parameter { Name = name, Type = NL_ParamType.Modulus, SectionTag = sectionTag };

        public static NL_Parameter ForLoad(string name, int nodeId, int dof, double scale = 1.0)
            => new NL_Parameter { Name = name, Type = NL_ParamType.PointLoad, NodeId = nodeId, Dof = dof, Scale = scale };

        public static NL_Parameter ForCoordinate(string name, int nodeId, int component)
            => new NL_Parameter { Name = name, Type = NL_ParamType.NodeCoordinate, NodeId = nodeId, Component = component };

        public override string ToString() => $"{Name} ({Type})";
    }

    /// <summary>The scalar response being differentiated: one nodal DOF.</summary>
    public class NL_Response
    {
        public int NodeId = 0;
        /// <summary>0..5 (ux,uy,uz,rx,ry,rz).</summary>
        public int Dof = 0;

        public NL_Response() { }
        public NL_Response(int nodeId, int dof) { NodeId = nodeId; Dof = dof; }
    }


    // =======================================================================
    //  Element-level derivative kernels
    // =======================================================================

    /// <summary>
    /// Analytical derivatives of the element internal force for the
    /// Total-Lagrangian truss formulation used by NL_Element.
    ///
    ///   D0 = Xj - Xi,  L2 = D0.D0,  d = D0 + (uj - ui),  l2 = d.d
    ///   Egl = (l2 - L2)/(2 L2),  S = E*Egl,  B = [-d, d]/L2,  f = A*L*S*B
    ///
    /// w.r.t. modulus E:
    ///   df/dE = A * L * Egl * B
    ///
    /// w.r.t. an ORIGINAL nodal coordinate, with a = dD0/dp
    /// (a = -e_c for node i, +e_c for node j):
    ///   dL   = (D0.a)/L
    ///   dEgl = ((d - D0).a)/L2 - 2*Egl*(D0.a)/L2
    ///   dB   = [-a, a]/L2 - B*(2*(D0.a)/L2)
    ///   df   = A*( dL*S*B + L*(E*dEgl)*B + L*S*dB )
    ///
    /// NOTE: the parasitic linear bending stiffness also depends on the nodal
    /// coordinates. That term is neglected here; with the small Iy/Iz used for
    /// truss-equivalent modelling its contribution is well below 0.1%.
    /// </summary>
    public static class NL_Sensitivity
    {
        private static readonly int[] TRANS = { 0, 1, 2, 6, 7, 8 };

        /// <summary>d(f_elem)/d(modulus) as a 12-vector [N/(N/mm2)].</summary>
        public static double[] dFint_dModulus(TB_Element_1D e, double[] uElem)
        {
            Geom(e, uElem, out double L, out double L2, out double[] D0,
                 out double[] d, out double l2);

            double A = e.Sec.Area;
            double Egl = (l2 - L2) / (2.0 * L2);
            double[] B = BVector(d, L2);

            double[] outv = new double[12];
            double f = A * L * Egl;
            for (int a = 0; a < 6; a++) outv[TRANS[a]] = f * B[a];
            return outv;
        }

        /// <summary>d(f_elem)/d(original coordinate) as a 12-vector.</summary>
        public static double[] dFint_dCoord(TB_Element_1D e, double[] uElem,
                                            int whichNode, int comp)
        {
            Geom(e, uElem, out double L, out double L2, out double[] D0,
                 out double[] d, out double l2);

            double A = e.Sec.Area;
            double E = e.Sec.Mat.E;
            double Egl = (l2 - L2) / (2.0 * L2);
            double S = E * Egl;
            double[] B = BVector(d, L2);

            double[] a = new double[3];
            a[comp] = (whichNode == 0) ? -1.0 : 1.0;

            double D0a = D0[0] * a[0] + D0[1] * a[1] + D0[2] * a[2];
            double da = d[0] * a[0] + d[1] * a[1] + d[2] * a[2];

            double dL = D0a / L;
            double dEgl = (da - D0a) / L2 - 2.0 * Egl * D0a / L2;
            double dS = E * dEgl;

            // dB = [-a, a]/L2 - B * (2*D0a/L2)
            double k = 2.0 * D0a / L2;
            double[] dB = new double[6];
            for (int i = 0; i < 3; i++)
            {
                dB[i] = -a[i] / L2 - B[i] * k;
                dB[i + 3] = a[i] / L2 - B[i + 3] * k;
            }

            double[] outv = new double[12];
            for (int i = 0; i < 6; i++)
            {
                double val = A * (dL * S * B[i] + L * dS * B[i] + L * S * dB[i]);
                outv[TRANS[i]] = val;
            }
            return outv;
        }

        // --- helpers -------------------------------------------------------

        private static void Geom(TB_Element_1D e, double[] uElem,
                                 out double L, out double L2, out double[] D0,
                                 out double[] d, out double l2)
        {
            Point3d Pi = e.Nodes[0].Pt;
            Point3d Pj = e.Nodes[1].Pt;

            D0 = new[] { Pj.X - Pi.X, Pj.Y - Pi.Y, Pj.Z - Pi.Z };
            L2 = D0[0] * D0[0] + D0[1] * D0[1] + D0[2] * D0[2];
            L = Math.Sqrt(L2);

            d = new[]
            {
                D0[0] + (uElem[6] - uElem[0]),
                D0[1] + (uElem[7] - uElem[1]),
                D0[2] + (uElem[8] - uElem[2])
            };
            l2 = d[0] * d[0] + d[1] * d[1] + d[2] * d[2];
        }

        private static double[] BVector(double[] d, double L2)
        {
            double i2 = 1.0 / L2;
            return new[] { -d[0] * i2, -d[1] * i2, -d[2] * i2,
                            d[0] * i2,  d[1] * i2,  d[2] * i2 };
        }
    }


    // =======================================================================
    //  Adjoint DDM gradient solver
    // =======================================================================

    /// <summary>
    /// Computes d(response)/d(parameter) at a converged nonlinear state.
    ///
    /// Differentiating equilibrium  Fext(x) - Fint(u,x) = 0  gives
    ///     K_T du/dx = dFext/dx - dFint/dx
    /// and because the response is a single DOF, the ADJOINT form needs only
    /// ONE linear solve for ALL parameters:
    ///     K_T lam = e_resp        then   dresp/dx_i = lam . RHS_i
    /// </summary>
    public class NL_DDM
    {
        public TB_Model Mdl { get; private set; }
        public int N_DOF { get; private set; } = 6;

        /// <summary>Analytical gradient, one entry per parameter.</summary>
        public double[] Gradient { get; private set; }
        /// <summary>Response value at the converged state.</summary>
        public double ResponseValue { get; private set; }
        public bool Ok { get; private set; }
        public string Message { get; private set; } = "ok";

        private readonly double[] _u;
        private readonly bool[] _isFixed;

        public NL_DDM(TB_Model mdl, double[] u, NL_Response resp,
                      IList<NL_Parameter> pars, int nDof = 6)
        {
            Mdl = mdl;
            N_DOF = nDof;
            _u = u;

            int nDofTotal = Mdl.Nodes.Count * N_DOF;
            _isFixed = new bool[nDofTotal];
            foreach (Node n in Mdl.Nodes.Where(x => x.Sup != null))
                for (int i = 0; i < N_DOF; i++)
                    if (n.Sup.Conditions[i]) _isFixed[N_DOF * n.Id.Value + i] = true;

            int respDof = N_DOF * resp.NodeId + resp.Dof;
            ResponseValue = u[respDof];

            try
            {
                Gradient = Compute(respDof, pars);
                Ok = true;
            }
            catch (Exception ex)
            {
                Gradient = new double[pars.Count];
                Ok = false;
                Message = ex.Message;
            }
        }

        private double[] Compute(int respDof, IList<NL_Parameter> pars)
        {
            int nDofTotal = Mdl.Nodes.Count * N_DOF;

            // --- tangent stiffness at the converged state ---------------------
            var cache = new Dictionary<TB_Element_1D, DenseMatrix>();
            DenseMatrix kT = new DenseMatrix(nDofTotal, nDofTotal);

            foreach (TB_Element_1D e in Mdl.Elem1Ds)
            {
                double[] ue = NL_Element.GatherElementDisp(e, _u, N_DOF);
                NL_Element.Compute(e, ue, out _, out DenseMatrix ke, out _, cache);

                int si = N_DOF * e.Nodes[0].Id.Value;
                int ei = N_DOF * e.Nodes[1].Id.Value;
                for (int i = 0; i < N_DOF; i++)
                    for (int j = 0; j < N_DOF; j++)
                    {
                        kT[si + i, si + j] += ke[i, j];
                        kT[si + i, ei + j] += ke[i, N_DOF + j];
                        kT[ei + i, si + j] += ke[N_DOF + i, j];
                        kT[ei + i, ei + j] += ke[N_DOF + i, N_DOF + j];
                    }
            }

            for (int i = 0; i < nDofTotal; i++)
            {
                if (!_isFixed[i]) continue;
                for (int j = 0; j < nDofTotal; j++) { kT[i, j] = 0.0; kT[j, i] = 0.0; }
                kT[i, i] = 1.0;
            }

            // --- adjoint solve: K_T lam = e_resp -----------------------------
            double[] eResp = Vector.Create(nDofTotal, 0.0);
            if (!_isFixed[respDof]) eResp[respDof] = 1.0;

            var kSparse = SparseMatrix.OfMatrix(kT);
            var lu = SparseLU.Create(kSparse, ColumnOrdering.MinimumDegreeAtPlusA, Common.PRES);
            double[] lam = Vector.Create(nDofTotal, 0.0);
            lu.Solve(eResp, lam);

            // --- right-hand sides, one per parameter --------------------------
            double[] grad = new double[pars.Count];
            for (int p = 0; p < pars.Count; p++)
            {
                double[] rhs = BuildRhs(pars[p], nDofTotal);
                double s = 0.0;
                for (int i = 0; i < nDofTotal; i++)
                    if (!_isFixed[i]) s += lam[i] * rhs[i];
                grad[p] = s;
            }
            return grad;
        }

        /// <summary>RHS_i = dFext/dx_i - dFint/dx_i (evaluated at fixed u).</summary>
        private double[] BuildRhs(NL_Parameter p, int nDofTotal)
        {
            double[] rhs = Vector.Create(nDofTotal, 0.0);

            switch (p.Type)
            {
                case NL_ParamType.Modulus:
                    foreach (TB_Element_1D e in Mdl.Elem1Ds)
                    {
                        if (!string.IsNullOrEmpty(p.SectionTag) &&
                            !string.Equals(e.Sec.Tag, p.SectionTag, StringComparison.OrdinalIgnoreCase))
                            continue;

                        double[] ue = NL_Element.GatherElementDisp(e, _u, N_DOF);
                        double[] dfe = NL_Sensitivity.dFint_dModulus(e, ue);
                        Scatter(rhs, dfe, e, -1.0);
                    }
                    break;

                case NL_ParamType.NodeCoordinate:
                    foreach (TB_Element_1D e in Mdl.Elem1Ds)
                    {
                        int which = -1;
                        if (e.Nodes[0].Id.Value == p.NodeId) which = 0;
                        else if (e.Nodes[1].Id.Value == p.NodeId) which = 1;
                        if (which < 0) continue;

                        double[] ue = NL_Element.GatherElementDisp(e, _u, N_DOF);
                        double[] dfe = NL_Sensitivity.dFint_dCoord(e, ue, which, p.Component);
                        Scatter(rhs, dfe, e, -1.0);
                    }
                    break;

                case NL_ParamType.PointLoad:
                    // Fint does not depend on the load; dFext/dp is a unit vector.
                    // Loads enter the solver in [kN] and are scaled by 1e3 -> [N].
                    rhs[N_DOF * p.NodeId + p.Dof] = p.Scale * Math.Pow(10, 3);
                    break;
            }
            return rhs;
        }

        private void Scatter(double[] target, double[] fe, TB_Element_1D e, double sign)
        {
            int si = N_DOF * e.Nodes[0].Id.Value;
            int ei = N_DOF * e.Nodes[1].Id.Value;
            for (int i = 0; i < N_DOF; i++)
            {
                target[si + i] += sign * fe[i];
                target[ei + i] += sign * fe[N_DOF + i];
            }
        }
    }


    // =======================================================================
    //  Finite-difference reference (for validating DDM)
    // =======================================================================

    /// <summary>
    /// Central finite differences by re-solving the nonlinear problem on a
    /// perturbed DEEP COPY of the model. Slow - intended for validation and
    /// for reporting the DDM-vs-FD comparison, not for production FORM runs.
    /// </summary>
    public static class NL_FiniteDiff
    {
        public static double[] Gradient(TB_Model mdl, NL_Response resp,
                                        IList<NL_Parameter> pars,
                                        double relStep = 1e-6,
                                        int loadSteps = 10, int maxIter = 50,
                                        double tol = 1e-9, int nDof = 6)
        {
            double[] grad = new double[pars.Count];

            for (int p = 0; p < pars.Count; p++)
            {
                double baseVal = BaseValue(mdl, pars[p]);
                double h = relStep * Math.Max(Math.Abs(baseVal), 1.0);

                double rp = Evaluate(mdl, pars[p], +h, resp, loadSteps, maxIter, tol, nDof);
                double rm = Evaluate(mdl, pars[p], -h, resp, loadSteps, maxIter, tol, nDof);

                grad[p] = (rp - rm) / (2.0 * h);
            }
            return grad;
        }

        private static double BaseValue(TB_Model mdl, NL_Parameter p)
        {
            switch (p.Type)
            {
                case NL_ParamType.Modulus:
                    foreach (var e in mdl.Elem1Ds)
                        if (string.IsNullOrEmpty(p.SectionTag) ||
                            string.Equals(e.Sec.Tag, p.SectionTag, StringComparison.OrdinalIgnoreCase))
                            return e.Sec.Mat.E;
                    return 1.0;

                case NL_ParamType.NodeCoordinate:
                    {
                        Node n = mdl.Nodes.FirstOrDefault(x => x.Id.Value == p.NodeId);
                        if (n == null) return 1.0;
                        return p.Component == 0 ? n.Pt.X : (p.Component == 1 ? n.Pt.Y : n.Pt.Z);
                    }

                case NL_ParamType.PointLoad:
                    foreach (var l in mdl.Loads.OfType<TB_Load_Point>())
                        if (l.Node != null && l.Node.Id.Value == p.NodeId)
                            return l.Loads[p.Dof];
                    return 1.0;
            }
            return 1.0;
        }

        private static double Evaluate(TB_Model mdl, NL_Parameter p, double delta,
                                       NL_Response resp, int loadSteps, int maxIter,
                                       double tol, int nDof)
        {
            TB_Model m = mdl.DeepCopy();
            m.Disps.Clear();
            foreach (var n in m.Nodes) n.Disps.Clear();

            Perturb(m, mdl, p, delta);

            SolveNL slv = new SolveNL(ref m, loadSteps, maxIter, tol);
            if (!slv.Converged)
                throw new InvalidOperationException("FD probe failed to converge: " + slv.Message);

            return m.Disps[0][nDof * resp.NodeId + resp.Dof];
        }

        private static void Perturb(TB_Model m, TB_Model original, NL_Parameter p, double delta)
        {
            switch (p.Type)
            {
                case NL_ParamType.Modulus:
                    {
                        var touched = new HashSet<TB_Material>();
                        foreach (var e in m.Elem1Ds)
                        {
                            if (!string.IsNullOrEmpty(p.SectionTag) &&
                                !string.Equals(e.Sec.Tag, p.SectionTag, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (touched.Contains(e.Sec.Mat)) continue;
                            touched.Add(e.Sec.Mat);
                            SetMaterialE(e.Sec.Mat, e.Sec.Mat.E + delta);
                        }
                        RebuildAll(m);
                        break;
                    }

                case NL_ParamType.NodeCoordinate:
                    {
                        Node n = m.Nodes.FirstOrDefault(x => x.Id.Value == p.NodeId);
                        if (n == null) return;

                        Point3d q = n.Pt;
                        if (p.Component == 0) q.X += delta;
                        else if (p.Component == 1) q.Y += delta;
                        else q.Z += delta;
                        n.Pt = q;

                        foreach (var e in m.Elem1Ds)
                        {
                            bool a = e.Nodes[0].Id.Value == p.NodeId;
                            bool b = e.Nodes[1].Id.Value == p.NodeId;
                            if (!a && !b) continue;
                            e.Line = new Line(e.Nodes[0].Pt, e.Nodes[1].Pt);
                        }
                        RebuildAll(m);
                        break;
                    }

                case NL_ParamType.PointLoad:
                    {
                        double base0 = BaseValue(original, p);   // absolute, never incremental
                        foreach (var l in m.Loads.OfType<TB_Load_Point>())
                            if (l.Node != null && l.Node.Id.Value == p.NodeId)
                                l.Loads[p.Dof] = base0 + delta;
                        break;
                    }
            }
        }

        /// <summary>
        /// TB_Material.E has a private setter; reflection keeps this validation-only
        /// helper from forcing a change to the production class.
        /// </summary>
        private static void SetMaterialE(TB_Material mat, double value)
        {
            var prop = typeof(TB_Material).GetProperty("E");
            prop.SetValue(mat, value, null);
        }

        private static void RebuildAll(TB_Model m)
        {
            foreach (var e in m.Elem1Ds)
            {
                e.EK = e.Calc_ElemStiffMX();
                e.TM = e.Calc_TransMX();
                e.EKG = e.TM.Transpose().Multiply(e.EK).Multiply(e.TM) as DenseMatrix;
            }
        }
    }
}
