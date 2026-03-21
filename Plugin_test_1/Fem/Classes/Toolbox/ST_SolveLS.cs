using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using CSparse;
using CSparse.Storage;
using CSparse.Double;
using CSparse.Double.Factorization;


namespace Propability_NTNU_v1.Classes.Toolbox
{
    public class SolveLS
    {
        // --- field ---
        public TB_Model Mdl { get; private set; }
        public int N_DOF { get; private set; }

        // --- constructors --- 
        public SolveLS() { }
        public SolveLS(ref TB_Model _mdl)
        {
            // Use the supplied, fully initialized model instead of JSON deep copy
            Mdl = _mdl;
            N_DOF = 6;

            Solve();
        }


        // --- methods ---

        private void Solve()
        {
            // global stiffness matrix
            DenseMatrix kG = CreateGlobalStiffMX();
            Mdl.KG = (DenseMatrix)kG.Clone();

            // CoordinateStorage<double> ccs_kG = CreateGlobalStiffMX();

            // load vector
            DenseMatrix lV = CreateLoadMX();
            Mdl.LM = (DenseMatrix)lV.Clone();

            // Reaction initialization
            foreach (TB_Support s in Mdl.Sups)
            {
                s.InitializeReact(lV.ColumnCount);
            }


            // overwrite boundary condition & load
            foreach (Node n in Mdl.Nodes.Where(n => n.Sup != null))
            {
                // i: i-th degree of freedom in a node
                for (int i = 0; i < N_DOF; i++)
                {
                    if (n.Sup.Conditions[i] == false)
                    {
                        continue;
                    }

                    int ind = N_DOF * n.Id.Value + i;

                    for (int j = 0; j < lV.ColumnCount; j++)
                    {

                        n.Sup.React[j][i] = -1 * lV[ind, j];

                        lV[ind, j] = 0;
                    }

                    // global stiffness matrix
                    for (int j = 0; j < N_DOF * Mdl.Nodes.Count; j++)
                    {
                        if (ind == j)
                        {
                            kG[ind, j] = 1;

                        }
                        else
                        {
                            kG[ind, j] = 0;
                            kG[j, ind] = 0;
                        }
                    }
                }
            }

            // Solve system
            CompressedColumnStorage<double> kGs = SparseMatrix.OfMatrix(kG);
            CompressedColumnStorage<double> lVs = SparseMatrix.OfMatrix(lV);

            var order = ColumnOrdering.MinimumDegreeAtPlusA;
            int nCols = Math.Max(1, lVs.ColumnCount); // solve at least once for zero displacements

            for (int i = 0; i < nCols; i++)
            {
                double[] b = (i < lVs.ColumnCount) ? lVs.Column(i) : Vector.Create(N_DOF * Mdl.Nodes.Count, 0.0);
                SparseLU lu = SparseLU.Create(kGs, order, Common.PRES);
                double[] disp = Vector.Create(N_DOF * Mdl.Nodes.Count, 0.0);

                lu.Solve(b, disp);

                Mdl.Disps.Add(disp);

                for (int j = 0; j < Mdl.Nodes.Count; j++)
                {
                    double[] vals = new double[6];
                    Array.Copy(disp, N_DOF * j, vals, 0, 6);
                    Mdl.Nodes[j].Disps.Add(vals);
                }
            }

            // reaction forces
            // i: load case
            for (int i = 0; i < lVs.ColumnCount; i++)
            {
                var Disp = new DenseMatrix(Mdl.Nodes.Count * N_DOF, 1, Mdl.Disps[i]);
                var FM = (DenseMatrix)Mdl.KG.Multiply(Disp);
                foreach (TB_Support s in Mdl.Sups)
                {
                    int sid = s.Node.Id.Value * N_DOF;
                    double[] fs = new double[N_DOF];
                    Array.Copy(FM.Column(0), sid, fs, 0, N_DOF);

                    // s.React.Add(fs);

                    // j: degree of freedom
                    for (int j = 0; j < N_DOF; j++)
                    {
                        s.React[i][j] += fs[j];
                    }

                }
            }

            return;
        }

        public DenseMatrix CreateGlobalStiffMX()
        {
            int lenMX = Mdl.Nodes.Count * N_DOF;
            DenseMatrix kG = new DenseMatrix(lenMX, lenMX);

            foreach (TB_Element_1D e in Mdl.Elem1Ds)
            {
                int snid = N_DOF * e.Nodes[0].Id.Value;
                int enid = N_DOF * e.Nodes[1].Id.Value;

                for (int i = 0; i < N_DOF; i++)
                {
                    for (int j = 0; j < N_DOF; j++)
                    {
                        kG[snid + i, snid + j] += e.EKG[i, j];
                        kG[snid + i, enid + j] += e.EKG[i, N_DOF + j];
                        kG[enid + i, snid + j] += e.EKG[N_DOF + i, j];
                        kG[enid + i, enid + j] += e.EKG[N_DOF + i, N_DOF + j];
                    }
                }
            }
            return kG;
        }


        private DenseMatrix CreateLoadMX()
        {
            int[] LCs = Mdl.LCs;
            int nLc = Math.Max(1, LCs.Length); // at least one column for zero-load case
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
