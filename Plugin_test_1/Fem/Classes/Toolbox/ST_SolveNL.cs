using System;
using System.Collections.Generic;
using System.Linq;

using CSparse;
using CSparse.Storage;
using CSparse.Double;
using CSparse.Double.Factorization;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    /// <summary>
    /// Geometrically nonlinear solver (Newton-Raphson with load stepping).
    ///
    /// Mirrors SolveLS: on completion it fills Mdl.Disps and Node.Disps with one
    /// entry per load case, so all downstream components (deformed shape, forces,
    /// reactions) work unchanged.
    ///
    /// Nonlinear analysis does NOT superpose - each  load case is solved
    /// independently from the undeformed state.
    /// </summary>
    public class SolveNL
    {
        // --- field ---
        public TB_Model Mdl { get; private set; }
        public int N_DOF { get; private set; }

        /// <summary>True only if every load case converged.</summary>
        public bool Converged { get; private set; }
        public string Message { get; private set; }
        /// <summary>Newton iterations used, per load case.</summary>
        public List<int> Iterations { get; private set; } = new List<int>();
        /// <summary>Final relative residual, per load case.</summary>
        public List<double> FinalResidual { get; private set; } = new List<double>();
        /// <summary>Axial force per element, per load case [N].</summary>
        public List<double[]> AxialForces { get; private set; } = new List<double[]>();

        // --- settings ---
        public int LoadSteps { get; set; } = 10;
        public int MaxIter { get; set; } = 50;
        public double Tolerance { get; set; } = 1e-9;

        // --- constructors ---
        public SolveNL() { }

        public SolveNL(ref TB_Model _mdl, int _loadSteps = 10, int _maxIter = 50, double _tol = 1e-9)
        {
            Mdl = _mdl;
            N_DOF = 6;
            LoadSteps = Math.Max(1, _loadSteps);
            MaxIter = Math.Max(1, _maxIter);
            Tolerance = _tol;

            Solve();
        }

        // --- methods ---

        private void Solve()
        {
            int nNode = Mdl.Nodes.Count;
            int nDofTotal = nNode * N_DOF;

            DenseMatrix loadMX = CreateLoadMX();
            int nLc = Math.Max(1, loadMX.ColumnCount);

            // reaction initialisation (same contract as SolveLS)
            foreach (TB_Support s in Mdl.Sups) s.InitializeReact(nLc);

            // constrained DOF list
            bool[] isFixed = new bool[nDofTotal];
            foreach (Node n in Mdl.Nodes.Where(n => n.Sup != null))
            {
                for (int i = 0; i < N_DOF; i++)
                {
                    if (n.Sup.Conditions[i] == false) continue;
                    isFixed[N_DOF * n.Id.Value + i] = true;
                }
            }

            var bendCache = new Dictionary<TB_Element_1D, DenseMatrix>();

            Converged = true;
            Message = "ok";

            for (int lc = 0; lc < nLc; lc++)
            {
                double[] Fext = (lc < loadMX.ColumnCount)
                    ? loadMX.Column(lc)
                    : Vector.Create(nDofTotal, 0.0);

                double refNorm = Math.Max(Norm(Fext, isFixed), 1.0);

                double[] u = Vector.Create(nDofTotal, 0.0);
                double[] fInt = Vector.Create(nDofTotal, 0.0);
                double[] axial = new double[Mdl.Elem1Ds.Count];

                int iterUsed = 0;
                double lastRes = 0.0;
                bool lcOk = true;

                for (int step = 1; step <= LoadSteps; step++)
                {
                    double lambda = (double)step / LoadSteps;

                    bool stepConverged = false;
                    for (int it = 0; it < MaxIter; it++)
                    {
                        DenseMatrix kT = AssembleTangentAndForce(u, bendCache, out fInt, out axial);

                        // residual R = lambda*Fext - Fint
                        double[] R = new double[nDofTotal];
                        for (int i = 0; i < nDofTotal; i++)
                            R[i] = isFixed[i] ? 0.0 : (lambda * Fext[i] - fInt[i]);

                        lastRes = Norm(R, isFixed) / refNorm;
                        if (lastRes < Tolerance) { stepConverged = true; break; }

                        ApplyBoundaryConditions(kT, isFixed, nDofTotal);

                        double[] du;
                        try
                        {
                            var kSparse = SparseMatrix.OfMatrix(kT);
                            var lu = SparseLU.Create(kSparse, ColumnOrdering.MinimumDegreeAtPlusA, Common.PRES);
                            du = Vector.Create(nDofTotal, 0.0);
                            lu.Solve(R, du);
                        }
                        catch (Exception ex)
                        {
                            lcOk = false;
                            Message = $"LC {lc}: singular tangent at step {step} " +
                                      $"(load may exceed the limit point). {ex.Message}";
                            break;
                        }

                        for (int i = 0; i < nDofTotal; i++)
                            if (!isFixed[i]) u[i] += du[i];

                        iterUsed++;
                    }

                    if (!lcOk) break;

                    if (!stepConverged)
                    {
                        lcOk = false;
                        Message = $"LC {lc}: Newton did not converge at load step {step} " +
                                  $"(residual {lastRes:E3}). Try more load steps.";
                        break;
                    }
                }

                if (!lcOk) Converged = false;

                Iterations.Add(iterUsed);
                FinalResidual.Add(lastRes);
                AxialForces.Add(axial);

                // --- store results in the same contract as SolveLS -----------------
                Mdl.Disps.Add(u);
                for (int j = 0; j < nNode; j++)
                {
                    double[] vals = new double[N_DOF];
                    Array.Copy(u, N_DOF * j, vals, 0, N_DOF);
                    Mdl.Nodes[j].Disps.Add(vals);
                }

                // --- reactions: R = Fint - Fext at constrained DOF ------------------
                AssembleTangentAndForce(u, bendCache, out fInt, out axial);
                foreach (TB_Support s in Mdl.Sups)
                {
                    int sid = s.Node.Id.Value * N_DOF;
                    for (int j = 0; j < N_DOF; j++)
                        s.React[lc][j] = fInt[sid + j] - Fext[sid + j];
                }
            }

            if (Converged) Message = "ok";
        }

        /// <summary>Assembles global tangent stiffness and internal force vector.</summary>
        private DenseMatrix AssembleTangentAndForce(double[] u,
                                                    Dictionary<TB_Element_1D, DenseMatrix> cache,
                                                    out double[] fInt,
                                                    out double[] axial)
        {
            int nDofTotal = Mdl.Nodes.Count * N_DOF;
            DenseMatrix kG = new DenseMatrix(nDofTotal, nDofTotal);
            fInt = Vector.Create(nDofTotal, 0.0);
            axial = new double[Mdl.Elem1Ds.Count];

            for (int k = 0; k < Mdl.Elem1Ds.Count; k++)
            {
                TB_Element_1D e = Mdl.Elem1Ds[k];

                double[] ue = NL_Element.GatherElementDisp(e, u, N_DOF);

                NL_Element.Compute(e, ue, out double[] fe, out DenseMatrix ke,
                                   out double N, cache);
                axial[k] = N;

                int snid = N_DOF * e.Nodes[0].Id.Value;
                int enid = N_DOF * e.Nodes[1].Id.Value;

                for (int i = 0; i < N_DOF; i++)
                {
                    fInt[snid + i] += fe[i];
                    fInt[enid + i] += fe[N_DOF + i];

                    for (int j = 0; j < N_DOF; j++)
                    {
                        kG[snid + i, snid + j] += ke[i, j];
                        kG[snid + i, enid + j] += ke[i, N_DOF + j];
                        kG[enid + i, snid + j] += ke[N_DOF + i, j];
                        kG[enid + i, enid + j] += ke[N_DOF + i, N_DOF + j];
                    }
                }
            }
            return kG;
        }

        /// <summary>Zeroes rows/columns of constrained DOF and puts 1 on the diagonal.</summary>
        private void ApplyBoundaryConditions(DenseMatrix kG, bool[] isFixed, int nDofTotal)
        {
            for (int ind = 0; ind < nDofTotal; ind++)
            {
                if (!isFixed[ind]) continue;
                for (int j = 0; j < nDofTotal; j++)
                {
                    kG[ind, j] = 0.0;
                    kG[j, ind] = 0.0;
                }
                kG[ind, ind] = 1.0;
            }
        }

        /// <summary>Euclidean norm over free DOF only.</summary>
        private static double Norm(double[] v, bool[] isFixed)
        {
            double s = 0.0;
            for (int i = 0; i < v.Length; i++)
                if (!isFixed[i]) s += v[i] * v[i];
            return Math.Sqrt(s);
        }

        /// <summary>
        /// Identical convention to SolveLS.CreateLoadMX so that linear and nonlinear
        /// runs see exactly the same applied loads.
        /// </summary>
        private DenseMatrix CreateLoadMX()
        {
            int[] LCs = Mdl.LCs;
            int nLc = Math.Max(1, LCs.Length);
            DenseMatrix loadMX = new DenseMatrix(N_DOF * Mdl.Nodes.Count, nLc);

            foreach (TB_Load l in Mdl.Loads)
            {
                if (!(l is TB_Load_Point pl) || pl.Node == null) continue;
                if (!pl.Node.Id.HasValue) continue;

                int lc = Array.IndexOf(LCs, l.Lc.Value);
                if (lc < 0) continue;

                var lds = pl.Loads;
                for (int i = 0; i < N_DOF && i < lds.Count; i++)
                {
                    double val = lds[i] * Math.Pow(10, 3); // [kN]-->[N]
                    loadMX[N_DOF * pl.Node.Id.Value + i, lc] += val;
                }
            }
            return loadMX;
        }
    }
}
