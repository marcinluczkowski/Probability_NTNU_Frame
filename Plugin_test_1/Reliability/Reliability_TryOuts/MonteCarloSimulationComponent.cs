using Grasshopper.Kernel;
using MathNet.Numerics.Distributions;
using Plugin_test_1.Reliability;
using Plugin_test_1.Reliability.FORM;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Rhino.Geometry;
using System;
using System.Collections.Generic;

namespace Plugin_test_1.Reliability.Reliability_TryOuts
{
    /// <summary>
    /// Monte Carlo Simulation Component
    /// Accepts beam elements, loads, and supports to perform probabilistic sampling analysis.
    /// Supports both Crude Monte Carlo and Importance Sampling methods.
    /// </summary>
    public class MonteCarloSimulationComponent : GH_Component
    {
        public MonteCarloSimulationComponent()
            : base(
                "Monte Carlo Simulation",
                "MonteCarlo",
                "Monte Carlo sampling analysis for structural elements. " +
                "Methods: Crude MC (direct sampling) or Importance Sampling (variance reduction). " +
                "Inputs: beam element, loads, supports. " +
                "Outputs: probability of failure, reliability index, failure count, variance metrics.",
                "Structural",
                "Reliability")
        { }

        public override Guid ComponentGuid =>
            new Guid("E2B3C4D5-E6F7-A8B9-C0D1-E2F3A4B5C6D7");

        protected override System.Drawing.Bitmap Icon => null;

        // =====================================================================
        // INPUTS
        // =====================================================================
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Element1D(), "Elements", "Beam", "Beam element", GH_ParamAccess.item);
            pManager.AddParameter(new Param_Load(), "Loads", "Loads", "Point and distributed loads", GH_ParamAccess.list);
            pManager.AddParameter(new Param_Support(), "Supports", "Sup", "Support conditions", GH_ParamAccess.list);
            pManager.AddNumberParameter("W_el", "Wel", "Elastic section modulus [mm³]", GH_ParamAccess.item);
            pManager.AddNumberParameter("fy_k", "fyk", "Characteristic yield strength [MPa]", GH_ParamAccess.item, 355.0);
            pManager.AddNumberParameter("N", "N", "Number of samples (e.g. 100000)", GH_ParamAccess.item, 100000);
            pManager.AddIntegerParameter("Seed", "Seed", "Random seed for reproducibility", GH_ParamAccess.item, 42);
            pManager.AddBooleanParameter("UseIS", "IS", "Use Importance Sampling (true) or Crude MC (false)", GH_ParamAccess.item, false);

            pManager[4].Optional = true;
            pManager[5].Optional = true;
            pManager[6].Optional = true;
            pManager[7].Optional = true;
        }

        // =====================================================================
        // OUTPUTS
        // =====================================================================
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Pf", "Pf", "Estimated probability of failure", GH_ParamAccess.item);
            pManager.AddNumberParameter("beta", "β", "Reliability index -Φ⁻¹(Pf)", GH_ParamAccess.item);
            pManager.AddNumberParameter("N_Fail", "Nf", "Number of failed samples", GH_ParamAccess.item);
            pManager.AddNumberParameter("Pf_Std", "σ(Pf)", "Standard deviation of Pf estimator", GH_ParamAccess.item);
            pManager.AddNumberParameter("Pf_CoV", "CoV", "Coefficient of variation of Pf", GH_ParamAccess.item);
            pManager.AddNumberParameter("N_samples", "N", "Total number of samples executed", GH_ParamAccess.item);
        }

        // =====================================================================
        // SOLVE
        // =====================================================================
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── read inputs ──────────────────────────────────────────────────
            GH_Element_1D ghBeam = null;
            var ghLoads = new List<GH_Load>();
            var ghSupports = new List<GH_Support>();
            double welInput = 0.0;
            double fyk = 355.0;
            double N = 100000;
            int seed = 42;
            bool useIS = false;

            if (!DA.GetData(0, ref ghBeam) || ghBeam?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Beam element required.");
                return;
            }

            if (!DA.GetDataList(1, ghLoads) || ghLoads.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least one load required.");
                return;
            }

            if (!DA.GetDataList(2, ghSupports) || ghSupports.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least one support required.");
                return;
            }

            if (!DA.GetData(3, ref welInput) || welInput <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Valid section modulus (W_el > 0) required.");
                return;
            }

            DA.GetData(4, ref fyk);
            DA.GetData(5, ref N);
            DA.GetData(6, ref seed);
            DA.GetData(7, ref useIS);

            // ── basic validation ─────────────────────────────────────────────
            if (fyk <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "fyk must be > 0"); return; }
            if (N <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "N must be > 0"); return; }

            var beam = ghBeam.Value;

            // ── extract loads and supports to determine beam geometry ────────
            double Qk = 0.0;
            double Gk = 0.0;
            Point3d loadPoint = Point3d.Unset;

            foreach (var ghLd in ghLoads)
            {
                var ld = ghLd.Value;
                if (ld is TB_Load_Point ptLoad)
                {
                    Qk = Math.Abs(ptLoad.Loads[2]);  // Z-component
                    loadPoint = ptLoad.Pt;
                }
            }

            if (Gk == 0 && Qk > 0)
                Gk = 0.1 * Qk;

            // ── extract support positions to determine true beam span ────────
            var supportPts = new List<Point3d>();
            foreach (var ghSup in ghSupports)
            {
                var sup = ghSup.Value;
                if (sup is TB_Support tbSup && tbSup.IsValid())
                {
                    supportPts.Add(tbSup.Pt);
                }
            }

            if (supportPts.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least two valid supports required to define beam span.");
                return;
            }

            // ── calculate beam length and load position from support geometry ─
            // Sort supports along beam direction to get true span
            double L = CalculateBeamLength(supportPts);
            double a = CalculateLoadPosition(loadPoint, supportPts, L);

            if (L <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Beam length must be > 0"); return; }

            // ── clamp load position strictly inside span ────────────────────
            a = Math.Max(0.001 * L, Math.Min(0.999 * L, a));

            // ── build random variables ────────────────────────────────────────
            //var rvs = RandomVariable.BuildEurocodeRVs(fyk, Gk, Qk);
            // Construct RVs explicitly to match the FEM-FORM setup
            var rvs = new RandomVariable[2];
            rvs[0] = new RandomVariable("fy", fyk, 0.01, 0.05, "lognormal");
            rvs[1] = new RandomVariable("Q", Qk, 0.98, 0.26, "gumbel");

            // ── Monte Carlo Simulation ────────────────────────────────────────
            int N_int = (int)N;
            Dictionary<string, object> results;

            if (useIS)
            {
                // Run FORM first to get design point for importance sampling
                double beta_FORM;
                bool converged;
                FORM.Run(welInput, L, a, rvs, out beta_FORM, out converged);

                // Use mean point as approximate design point
                double[] designPoint = new double[rvs.Length];
                for (int i = 0; i < rvs.Length; i++)
                    designPoint[i] = rvs[i].Mean;

                results = MonteCarloCalcs.RunImportanceSampling(welInput, L, a, rvs, designPoint, stdShift: 1.5, N: N_int, seed: seed);
            }
            else
            {
                // Crude Monte Carlo
                results = MonteCarloCalcs.RunCrudeMonteCarlo(welInput, L, a, rvs, N_int, seed);
            }

            // ── extract results ──────────────────────────────────────────────
            double Pf = (double)results["Pf"];
            double beta = (double)results["Beta"];
            double nFail = (double)results["N_Fail"];
            double Pf_std = (double)results["Pf_Std"];
            double Pf_cov = (double)results["Pf_CoV"];

            // ── set outputs ───────────────────────────────────────────────────
            DA.SetData(0, Pf);
            DA.SetData(1, beta);
            DA.SetData(2, nFail);
            DA.SetData(3, Pf_std);
            DA.SetData(4, Pf_cov);
            DA.SetData(5, N_int);
        }

        /// <summary>
        /// Calculate total beam length from support positions.
        /// The beam spans between the two most distant supports.
        /// </summary>
        private double CalculateBeamLength(List<Point3d> supportPts)
        {
            if (supportPts.Count < 2)
                return 0.0;

            // Find two supports that are farthest apart (defines the main span)
            double maxDist = 0.0;
            int idxA = 0, idxB = 1;

            for (int i = 0; i < supportPts.Count; i++)
            {
                for (int j = i + 1; j < supportPts.Count; j++)
                {
                    double dist = supportPts[i].DistanceTo(supportPts[j]);
                    if (dist > maxDist)
                    {
                        maxDist = dist;
                        idxA = i;
                        idxB = j;
                    }
                }
            }

            return maxDist;
        }

        /// <summary>
        /// Calculate load position along beam measured from first support.
        /// Projects load point onto the line between the two main supports.
        /// </summary>
        private double CalculateLoadPosition(Point3d loadPoint, List<Point3d> supportPts, double beamLength)
        {
            if (supportPts.Count < 2 || !loadPoint.IsValid || beamLength <= 0)
                return beamLength / 2.0;  // Default to midspan

            // Find the two supports that are farthest apart (main span)
            double maxDist = 0.0;
            int idxA = 0, idxB = 1;

            for (int i = 0; i < supportPts.Count; i++)
            {
                for (int j = i + 1; j < supportPts.Count; j++)
                {
                    double dist = supportPts[i].DistanceTo(supportPts[j]);
                    if (dist > maxDist)
                    {
                        maxDist = dist;
                        idxA = i;
                        idxB = j;
                    }
                }
            }

            Point3d supportA = supportPts[idxA];
            Point3d supportB = supportPts[idxB];
            Vector3d spanDir = supportB - supportA;

            if (spanDir.Length < 1e-6)
                return beamLength / 2.0;

            // Project load point onto the span line
            Vector3d toLoad = loadPoint - supportA;
            double t = (toLoad.X * spanDir.X + toLoad.Y * spanDir.Y + toLoad.Z * spanDir.Z) / (beamLength * beamLength);
            t = Math.Max(0.0, Math.Min(1.0, t));  // Clamp to [0, 1]

            return t * beamLength;
        }
    }
}
