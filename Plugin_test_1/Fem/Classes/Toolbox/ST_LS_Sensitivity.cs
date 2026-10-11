// =============================================================================
//  ST_LS_Sensitivity.cs
//
//  WHAT IT IS
//  The linear-elastic counterpart of ST_NL_Sensitivity.cs: member axial forces of a
//  model and their derivatives with respect to the parameters that enter the FE
//  equations, by the Direct Differentiation Method (DDM).
//
//  HOW IT WORKS
//  Linear equilibrium is  K(p) u = F(p).  Differentiating with respect to a
//  parameter p_i gives
//
//      K du/dp_i = dF/dp_i - (dK/dp_i) u
//
//  which has the SAME matrix as the analysis itself. K is therefore factorised
//  once and every parameter costs one extra back-substitution. The axial force of
//  element e is N_e = s_e q_e . u_e, so
//
//      dN_e/dp_i = (ds_e/dp_i) q_e . u_e  +  s_e q_e . du_e/dp_i
//
//  The same holds for every other end force of the element (shear forces and the
//  bending moments from the rigid joints): f_r = s_e q_e,r . u_e, see
//  LocalEndForceGradient. The moments are what the N+M interaction check needs.
//
//  This is the DIRECT form: one solve per parameter gives the derivatives of ALL
//  member forces. (NL_DDM uses the adjoint form, one solve per response, which is
//  the better choice for a single displacement response but not for many members.)
//
//  PARAMETERS
//    LoadCase  the variable scales every load of one load case:
//                  loads = (p / Reference) * loads as modelled
//              so the model can hold e.g. the characteristic loads and Reference
//              is the characteristic value. dK/dp = 0, dF/dp = F_lc / Reference.
//    Modulus   Young's modulus of every element with a given ELEMENT tag.
//              dF/dp = 0, dK/dp = sum over the group of K_e / E.
//
//  Load cases that are not bound to a parameter are applied as modelled.
//  When no Modulus parameter is present K never changes: it is factorised once in
//  the constructor and an evaluation costs no solve at all.
//
//  UNITS (as SolveLS): loads [kN] -> [N], lengths [m], E [N/mm2], A [mm2].
//  Axial force N in [N], tension positive.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    public enum LS_ParamType
    {
        /// <summary>Multiplier on all loads of one load case.</summary>
        LoadCase,
        /// <summary>Young's modulus of every element whose element Tag matches.</summary>
        Modulus
    }

    /// <summary>Describes one parameter of the linear FE model.</summary>
    public class LS_Parameter
    {
        public string Name = "p";
        public LS_ParamType Type = LS_ParamType.LoadCase;

        /// <summary>LoadCase: the load case number, as given to the Loads component.</summary>
        public int LoadCase = 0;

        /// <summary>LoadCase: the parameter value that reproduces the loads as modelled.</summary>
        public double Reference = 1.0;

        /// <summary>Modulus: element tag to target. Null/empty = all elements.</summary>
        public string ElementTag = null;

        public static LS_Parameter ForLoadCase(string name, int loadCase, double reference = 1.0)
            => new LS_Parameter { Name = name, Type = LS_ParamType.LoadCase, LoadCase = loadCase, Reference = reference };

        public static LS_Parameter ForModulus(string name, string elementTag)
            => new LS_Parameter { Name = name, Type = LS_ParamType.Modulus, ElementTag = elementTag };

        public override string ToString() => $"{Name} ({Type})";
    }

    /// <summary>
    /// Linear FE response (member axial forces) and its DDM gradient.
    /// The model is only read, never modified, so no deep copy is needed.
    /// </summary>
    public class LS_DDM
    {
        // --- field ---
        public const int N_DOF = 6;
        /// <summary>Row of the local end-force vector holding the axial force at the end node (tension positive).</summary>
        private const int AXIAL_ROW = 6;

        public TB_Model Mdl { get; private set; }
        public int NElem { get; private set; }
        public int NPar { get; private set; }

        /// <summary>True when no parameter changes the stiffness (K is factorised once).</summary>
        public bool StiffnessIsConstant { get; private set; }
        /// <summary>Number of factorisations of K so far.</summary>
        public int Factorisations { get; private set; }
        /// <summary>Parameter values that reproduce the model as built.</summary>
        public double[] Baseline { get; private set; }

        /// <summary>Displacements at the last evaluated point [m, rad].</summary>
        public double[] U { get; private set; }
        /// <summary>du/dp at the last evaluated point, [parameter][dof].</summary>
        public double[][] dU { get; private set; }
        /// <summary>Axial force per element at the last evaluated point [N], tension positive.</summary>
        public double[] N { get; private set; }
        /// <summary>dN/dp at the last evaluated point, [element][parameter].</summary>
        public double[][] dN { get; private set; }

        private readonly List<LS_Parameter> _pars;
        private readonly int _nDofTotal;
        private readonly bool[] _isFixed;
        private readonly int[] _si, _ei;        // first global DOF of the start / end node
        private readonly double[] _e0;          // modulus of each element in the model
        private readonly int[] _modPar;         // Modulus parameter of each element, -1 if none
        private readonly double[][,] _kE;       // element stiffness, global axes, part proportional to E
        private readonly double[][,] _kT;       // element stiffness, global axes, torsion (proportional to G)
        private readonly double[][,] _q;        // local end forces = s * _q * u_elem (part proportional to E)
        private readonly double[] _fFixed;      // loads of the load cases without a parameter [N]
        private readonly double[][] _fPar;      // dF/dp per LoadCase parameter [N]; null for Modulus

        // cache for constant stiffness
        private SparseLU _lu;
        private double[] _uFixed0;
        private double[][] _uLoad0;

        private double[] _lastP;

        // --- constructors ---
        public LS_DDM(TB_Model mdl, IList<LS_Parameter> pars)
        {
            if (mdl == null) throw new ArgumentNullException(nameof(mdl));
            if (mdl.Nodes == null || mdl.Elem1Ds == null || mdl.Elem1Ds.Count == 0)
                throw new ArgumentException("The model has no elements. Use the model from the Assembly component.");

            Mdl = mdl;
            _pars = (pars == null) ? new List<LS_Parameter>() : pars.ToList();
            NPar = _pars.Count;
            NElem = mdl.Elem1Ds.Count;
            _nDofTotal = mdl.Nodes.Count * N_DOF;
            Baseline = new double[NPar];

            // --- supports ---
            _isFixed = new bool[_nDofTotal];
            foreach (Node nd in mdl.Nodes)
            {
                if (nd.Sup == null || nd.Sup.Conditions == null) continue;
                for (int i = 0; i < N_DOF; i++)
                    if (nd.Sup.Conditions[i]) _isFixed[N_DOF * nd.Id.Value + i] = true;
            }

            // --- element matrices, split into the part that scales with E and the torsion part ---
            _si = new int[NElem];
            _ei = new int[NElem];
            _e0 = new double[NElem];
            _modPar = new int[NElem];
            _kE = new double[NElem][,];
            _kT = new double[NElem][,];
            _q = new double[NElem][,];

            for (int k = 0; k < NElem; k++)
            {
                TB_Element_1D e = mdl.Elem1Ds[k];
                if (e == null || e.Nodes == null || e.Nodes.Count < 2 || e.Sec == null || e.Sec.Mat == null)
                    throw new ArgumentException($"Element {k} has no nodes, section or material.");
                if (!(e.Sec.Mat.E > 0.0))
                    throw new ArgumentException($"Element {k} ('{e.Tag}') has a non-positive Young's modulus.");

                _si[k] = N_DOF * e.Nodes[0].Id.Value;
                _ei[k] = N_DOF * e.Nodes[1].Id.Value;
                _e0[k] = e.Sec.Mat.E;
                _modPar[k] = -1;

                double[,] ekE = new double[12, 12];
                double[,] ekT = new double[12, 12];
                double[,] tm = new double[12, 12];
                for (int i = 0; i < 12; i++)
                    for (int j = 0; j < 12; j++)
                    {
                        tm[i, j] = e.TM[i, j];
                        bool torsion = (i == 3 || i == 9) && (j == 3 || j == 9);
                        if (torsion) ekT[i, j] = e.EK[i, j];
                        else ekE[i, j] = e.EK[i, j];
                    }

                double[,] tmT = Transpose(tm);
                _q[k] = Multiply(ekE, tm);
                _kE[k] = Multiply(tmT, _q[k]);
                _kT[k] = Multiply(tmT, Multiply(ekT, tm));
            }

            // --- parameters ---
            int[] lcs = mdl.LCs ?? new int[0];
            var lcToPar = new Dictionary<int, int>();   // load-case column -> parameter index

            for (int ip = 0; ip < NPar; ip++)
            {
                LS_Parameter par = _pars[ip];

                if (par.Type == LS_ParamType.LoadCase)
                {
                    int col = Array.IndexOf(lcs, par.LoadCase);
                    if (col < 0)
                        throw new ArgumentException(
                            $"Parameter '{par.Name}': the model has no loads in load case {par.LoadCase}.");
                    if (lcToPar.ContainsKey(col))
                        throw new ArgumentException(
                            $"Parameter '{par.Name}': load case {par.LoadCase} is already bound to '{_pars[lcToPar[col]].Name}'.");
                    if (Math.Abs(par.Reference) < 1e-300)
                        throw new ArgumentException($"Parameter '{par.Name}': the reference value cannot be zero.");

                    lcToPar[col] = ip;
                    Baseline[ip] = par.Reference;
                }
                else
                {
                    int count = 0;
                    for (int k = 0; k < NElem; k++)
                    {
                        if (!TagMatches(mdl.Elem1Ds[k].Tag, par.ElementTag)) continue;
                        if (_modPar[k] >= 0)
                            throw new ArgumentException(
                                $"Parameter '{par.Name}': element {k} ('{mdl.Elem1Ds[k].Tag}') already takes its modulus " +
                                $"from '{_pars[_modPar[k]].Name}'.");
                        _modPar[k] = ip;
                        if (count == 0) Baseline[ip] = _e0[k];
                        count++;
                    }
                    if (count == 0)
                        throw new ArgumentException(
                            $"Parameter '{par.Name}': no element has the tag '{par.ElementTag}'.");
                }
            }

            // --- load vectors (same convention as SolveLS.CreateLoadMX) ---
            _fFixed = new double[_nDofTotal];
            _fPar = new double[NPar][];
            foreach (int ip in lcToPar.Values) _fPar[ip] = new double[_nDofTotal];

            if (mdl.Loads != null)
            {
                foreach (TB_Load l in mdl.Loads)
                {
                    TB_Load_Point pl = l as TB_Load_Point;
                    if (pl == null || pl.Node == null || !pl.Node.Id.HasValue || !pl.Lc.HasValue) continue;

                    int col = Array.IndexOf(lcs, pl.Lc.Value);
                    if (col < 0) continue;

                    double[] target = _fFixed;
                    double factor = 1e3;                               // [kN] -> [N]
                    int ipLoad;
                    if (lcToPar.TryGetValue(col, out ipLoad))
                    {
                        target = _fPar[ipLoad];
                        factor = 1e3 / _pars[ipLoad].Reference;        // per unit of the parameter
                    }

                    int first = N_DOF * pl.Node.Id.Value;
                    for (int i = 0; i < N_DOF && i < pl.Loads.Count; i++)
                        target[first + i] += factor * pl.Loads[i];
                }
            }

            // boundary conditions on the right-hand sides
            for (int i = 0; i < _nDofTotal; i++)
            {
                if (!_isFixed[i]) continue;
                _fFixed[i] = 0.0;
                for (int ip = 0; ip < NPar; ip++)
                    if (_fPar[ip] != null) _fPar[ip][i] = 0.0;
            }

            // --- constant stiffness: factorise and solve the load basis once ---
            StiffnessIsConstant = !_pars.Any(prm => prm.Type == LS_ParamType.Modulus);
            if (StiffnessIsConstant)
            {
                _lu = Factorise(null);
                SolveLoadBasis(_lu, out _uFixed0, out _uLoad0);
            }
        }

        // --- methods ---

        /// <summary>
        /// Solves the model for the parameter vector p and stores U, N, dU and dN.
        /// Repeating the call with the same p costs nothing.
        /// </summary>
        public void Evaluate(double[] p)
        {
            if (p == null || p.Length != NPar)
                throw new ArgumentException($"Expected {NPar} parameter values.");
            if (_lastP != null && SameVector(_lastP, p)) return;

            double[] u, n;
            double[][] dn, du;
            Compute(p, true, out u, out n, out dn, out du);

            U = u; N = n; dN = dn; dU = du;
            _lastP = (double[])p.Clone();
        }

        /// <summary>
        /// dN/dp by central finite differences, [element][parameter]. For checking the DDM only.
        /// </summary>
        public double[][] FiniteDifference_dN(double[] p, double relStep = 1e-6)
        {
            var fd = new double[NElem][];
            for (int k = 0; k < NElem; k++) fd[k] = new double[NPar];

            for (int ip = 0; ip < NPar; ip++)
            {
                double h = relStep * Math.Max(Math.Max(Math.Abs(p[ip]), Math.Abs(Baseline[ip])), 1e-12);

                double[] pPlus = (double[])p.Clone(); pPlus[ip] += h;
                double[] pMinus = (double[])p.Clone(); pMinus[ip] -= h;

                double[] uTmp, nPlus, nMinus;
                double[][] a, b;
                Compute(pPlus, false, out uTmp, out nPlus, out a, out b);
                Compute(pMinus, false, out uTmp, out nMinus, out a, out b);

                for (int k = 0; k < NElem; k++) fd[k][ip] = (nPlus[k] - nMinus[k]) / (2.0 * h);
            }
            return fd;
        }

        /// <summary>
        /// Local end forces of element k at the last evaluated point, in the layout of
        /// TB_Element_1D.Calc_Forces: [N, Vy, Vz, Mx, My, Mz] at the start node, then at the end
        /// node, in [N] and [N.m]. The torsion entries (3 and 9) are not computed and are zero.
        /// </summary>
        public double[] LocalEndForces(int k)
        {
            if (U == null) throw new InvalidOperationException("Call Evaluate first.");

            double s = ModulusScale(k, _lastP);
            double[] f = new double[12];
            for (int r = 0; r < 12; r++) f[r] = s * RowDot(_q[k], r, U, k);
            return f;
        }

        /// <summary>
        /// Derivatives of the local end forces of element k at the last evaluated point,
        /// [row of the end-force vector][parameter], same layout and units as LocalEndForces:
        ///     d f_r / d p_i = (ds/dp_i) q_r . u  +  s q_r . du/dp_i
        /// from the same direct-differentiation solves as dN (no extra solve). Only the rows in
        /// 'rows' are filled; the others are null.
        /// </summary>
        public double[][] LocalEndForceGradient(int k, IList<int> rows)
        {
            if (U == null || dU == null) throw new InvalidOperationException("Call Evaluate first.");

            double s = ModulusScale(k, _lastP);
            double[][] g = new double[12][];
            foreach (int r in rows)
            {
                g[r] = new double[NPar];
                for (int ip = 0; ip < NPar; ip++)
                {
                    double v = s * RowDot(_q[k], r, dU[ip], k);
                    if (_modPar[k] == ip) v += RowDot(_q[k], r, U, k) / _e0[k];
                    g[r][ip] = v;
                }
            }
            return g;
        }

        /// <summary>
        /// Local end forces of every element by central finite differences, [element][row][parameter].
        /// For checking LocalEndForceGradient only. The stored state (U, N, dU, dN) is not changed.
        /// </summary>
        public double[][][] FiniteDifference_dF(double[] p, double relStep = 1e-6)
        {
            var fd = new double[NElem][][];
            for (int k = 0; k < NElem; k++)
            {
                fd[k] = new double[12][];
                for (int r = 0; r < 12; r++) fd[k][r] = new double[NPar];
            }

            for (int ip = 0; ip < NPar; ip++)
            {
                double h = relStep * Math.Max(Math.Max(Math.Abs(p[ip]), Math.Abs(Baseline[ip])), 1e-12);
                double[] pPlus = (double[])p.Clone(); pPlus[ip] += h;
                double[] pMinus = (double[])p.Clone(); pMinus[ip] -= h;

                double[] uPlus, uMinus, nTmp;
                double[][] a, b;
                Compute(pPlus, false, out uPlus, out nTmp, out a, out b);
                Compute(pMinus, false, out uMinus, out nTmp, out a, out b);

                for (int k = 0; k < NElem; k++)
                {
                    double sp = ModulusScale(k, pPlus), sm = ModulusScale(k, pMinus);
                    for (int r = 0; r < 12; r++)
                        fd[k][r][ip] = (sp * RowDot(_q[k], r, uPlus, k) - sm * RowDot(_q[k], r, uMinus, k)) / (2.0 * h);
                }
            }
            return fd;
        }

        /// <summary>The solve itself. With withSensitivity = false only u and n are computed.</summary>
        private void Compute(double[] p, bool withSensitivity,
                             out double[] u, out double[] n, out double[][] dn, out double[][] du)
        {
            SparseLU lu;
            double[] uFixed;
            double[][] uLoad;

            if (StiffnessIsConstant)
            {
                lu = _lu; uFixed = _uFixed0; uLoad = _uLoad0;
            }
            else
            {
                lu = Factorise(p);
                SolveLoadBasis(lu, out uFixed, out uLoad);
            }

            // u = u(fixed load cases) + sum over LoadCase parameters of p_i * du/dp_i
            u = (double[])uFixed.Clone();
            for (int ip = 0; ip < NPar; ip++)
            {
                if (uLoad[ip] == null) continue;
                double w = p[ip];
                double[] ul = uLoad[ip];
                for (int i = 0; i < _nDofTotal; i++) u[i] += w * ul[i];
            }

            n = new double[NElem];
            for (int k = 0; k < NElem; k++)
                n[k] = ModulusScale(k, p) * RowDot(_q[k], AXIAL_ROW, u, k);

            dn = null;
            du = null;
            if (!withSensitivity) return;

            // --- du/dp, one back-substitution per parameter ---
            du = new double[NPar][];
            for (int ip = 0; ip < NPar; ip++)
            {
                if (uLoad[ip] != null)
                {
                    du[ip] = uLoad[ip];            // K du/dp = dF/dp, already solved
                    continue;
                }

                // Modulus: K du/dp = -(dK/dp) u, with dK/dp = sum over the group of K_e / E
                double[] rhs = new double[_nDofTotal];
                for (int k = 0; k < NElem; k++)
                {
                    if (_modPar[k] != ip) continue;
                    AddElementProduct(rhs, _kE[k], u, k, -1.0 / _e0[k]);
                }
                for (int i = 0; i < _nDofTotal; i++)
                    if (_isFixed[i]) rhs[i] = 0.0;

                du[ip] = Solve(lu, rhs);
            }

            // --- dN/dp = (ds/dp) q.u + s q.du/dp ---
            dn = new double[NElem][];
            for (int k = 0; k < NElem; k++)
            {
                dn[k] = new double[NPar];
                double s = ModulusScale(k, p);
                for (int ip = 0; ip < NPar; ip++)
                {
                    double v = s * RowDot(_q[k], AXIAL_ROW, du[ip], k);
                    if (_modPar[k] == ip) v += RowDot(_q[k], AXIAL_ROW, u, k) / _e0[k];
                    dn[k][ip] = v;
                }
            }
        }

        /// <summary>s_e = E / E_model for an element with a Modulus parameter, else 1.</summary>
        private double ModulusScale(int k, double[] p)
        {
            return (p != null && _modPar[k] >= 0) ? p[_modPar[k]] / _e0[k] : 1.0;
        }

        /// <summary>Assembles K(p), applies the supports and factorises.</summary>
        private SparseLU Factorise(double[] p)
        {
            DenseMatrix kG = new DenseMatrix(_nDofTotal, _nDofTotal);

            for (int k = 0; k < NElem; k++)
            {
                double s = ModulusScale(k, p);
                double[,] kE = _kE[k];
                double[,] kT = _kT[k];
                int si = _si[k], ei = _ei[k];

                for (int i = 0; i < N_DOF; i++)
                    for (int j = 0; j < N_DOF; j++)
                    {
                        kG[si + i, si + j] += s * kE[i, j] + kT[i, j];
                        kG[si + i, ei + j] += s * kE[i, N_DOF + j] + kT[i, N_DOF + j];
                        kG[ei + i, si + j] += s * kE[N_DOF + i, j] + kT[N_DOF + i, j];
                        kG[ei + i, ei + j] += s * kE[N_DOF + i, N_DOF + j] + kT[N_DOF + i, N_DOF + j];
                    }
            }

            // supports: zero the row and column, 1 on the diagonal (as SolveLS)
            for (int i = 0; i < _nDofTotal; i++)
            {
                if (!_isFixed[i]) continue;
                for (int j = 0; j < _nDofTotal; j++)
                {
                    kG[i, j] = 0.0;
                    kG[j, i] = 0.0;
                }
                kG[i, i] = 1.0;
            }

            Factorisations++;
            try
            {
                var kSparse = SparseMatrix.OfMatrix(kG);
                return SparseLU.Create(kSparse, ColumnOrdering.MinimumDegreeAtPlusA, Common.PRES);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The stiffness matrix could not be factorised. Check that the supports restrain the structure. " + ex.Message);
            }
        }

        /// <summary>Displacements for the fixed load cases and for a unit of each LoadCase parameter.</summary>
        private void SolveLoadBasis(SparseLU lu, out double[] uFixed, out double[][] uLoad)
        {
            uFixed = Solve(lu, _fFixed);
            uLoad = new double[NPar][];
            for (int ip = 0; ip < NPar; ip++)
                if (_fPar[ip] != null) uLoad[ip] = Solve(lu, _fPar[ip]);
        }

        private double[] Solve(SparseLU lu, double[] rhs)
        {
            double[] b = (double[])rhs.Clone();
            double[] x = new double[_nDofTotal];
            lu.Solve(b, x);

            for (int i = 0; i < x.Length; i++)
                if (double.IsNaN(x[i]) || double.IsInfinity(x[i]))
                    throw new InvalidOperationException(
                        "The stiffness matrix is singular. Check that the supports restrain the structure.");
            return x;
        }

        /// <summary>Row r of a 12x12 element matrix times the 12 displacements of element k.</summary>
        private double RowDot(double[,] m, int r, double[] uGlobal, int k)
        {
            int si = _si[k], ei = _ei[k];
            double s = 0.0;
            for (int j = 0; j < N_DOF; j++)
                s += m[r, j] * uGlobal[si + j] + m[r, N_DOF + j] * uGlobal[ei + j];
            return s;
        }

        /// <summary>target += factor * (12x12 element matrix) * u_elem, scattered to the global DOF.</summary>
        private void AddElementProduct(double[] target, double[,] m, double[] uGlobal, int k, double factor)
        {
            int si = _si[k], ei = _ei[k];
            for (int i = 0; i < N_DOF; i++)
            {
                target[si + i] += factor * RowDot(m, i, uGlobal, k);
                target[ei + i] += factor * RowDot(m, N_DOF + i, uGlobal, k);
            }
        }

        private static bool TagMatches(string elementTag, string wanted)
        {
            return string.IsNullOrEmpty(wanted) ||
                   string.Equals(elementTag, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameVector(double[] a, double[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static double[,] Transpose(double[,] a)
        {
            int r = a.GetLength(0), c = a.GetLength(1);
            double[,] t = new double[c, r];
            for (int i = 0; i < r; i++)
                for (int j = 0; j < c; j++)
                    t[j, i] = a[i, j];
            return t;
        }

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            int r = a.GetLength(0), m = a.GetLength(1), c = b.GetLength(1);
            double[,] p = new double[r, c];
            for (int i = 0; i < r; i++)
                for (int k = 0; k < m; k++)
                {
                    double aik = a[i, k];
                    if (aik == 0.0) continue;
                    for (int j = 0; j < c; j++) p[i, j] += aik * b[k, j];
                }
            return p;
        }
    }
}
