using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Plugin_test_1.Reliability;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Rhino.Geometry;
using MathNet.Numerics.Distributions;

namespace Plugin_test_1.Reliability.Reliability_TryOuts
{
    /// <summary>
    /// FORM Reliability Analysis Component
    /// Accepts beam elements, loads, and supports to compute reliability index and probability of failure.
    /// Optional: provide section modulus for utilization check. If not provided, optimizes required section.
    /// </summary>
    public class ReliabilityAnalysisComponent : GH_Component
    {
        public ReliabilityAnalysisComponent()
            : base(
                "Reliability Analysis (FORM)",
                "RelAnalysis",
                "First Order Reliability Method (FORM) analysis for structural elements. " +
                "Inputs: beam element, point loads, support conditions. " +
                "Outputs: reliability index (β), probability of failure (Pf), required section modulus, utilization.",
                "Structural",
                "Reliability")
        { }

        public override Guid ComponentGuid =>
            new Guid("F1A2B3C4-D5E6-F7A8-B9C0-D1E2F3A4B5C6");

        protected override System.Drawing.Bitmap Icon => null;

        // =====================================================================
        // INPUTS
        // =====================================================================
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Element1D(), "Elements", "Beam", "Beam element", GH_ParamAccess.item);
            pManager.AddBooleanParameter("T/F", "Element section", "True/False: Use existing section information", GH_ParamAccess.item);
            pManager.AddParameter(new Param_Load(), "Loads", "Loads", "Point and distributed loads", GH_ParamAccess.list);
            pManager.AddParameter(new Param_Support(), "Supports", "Sup", "Support conditions", GH_ParamAccess.list);
            pManager.AddNumberParameter("W_el", "Wel", "Elastic section modulus [mm³] (optional; leave empty to optimize)", GH_ParamAccess.item);
            pManager.AddNumberParameter("fy_k", "fyk", "Characteristic yield strength [MPa] (e.g. 355 for S355)", GH_ParamAccess.item, 355.0);
            pManager.AddNumberParameter("E_mean", "E", "Mean Young's modulus [MPa]", GH_ParamAccess.item, 210000.0);

            pManager[1].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
            pManager[6].Optional = true;
        }

        // =====================================================================
        // OUTPUTS
        // =====================================================================
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("beta", "β", "FORM reliability index", GH_ParamAccess.item);
            pManager.AddNumberParameter("Pf", "Pf", "Probability of failure", GH_ParamAccess.item);
            pManager.AddNumberParameter("W_el_required", "Wel_req", "Required section modulus [mm³] from Eurocode ULS", GH_ParamAccess.item);
            pManager.AddNumberParameter("utilization", "Util", "Utilization ratio M_Ed/M_Rd (calculated for the section used in FORM analysis)", GH_ParamAccess.item);
            pManager.AddBooleanParameter("converged", "Conv", "True if HLRF algorithm converged", GH_ParamAccess.item);
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
            bool useSection = false;
            double fyk = 355.0;
            double Emean = 210000.0;

            if (!DA.GetData(0, ref ghBeam) || ghBeam?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Beam elements required.");
                return;
            }

            if (!DA.GetDataList(2, ghLoads) || ghLoads.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least one load required.");
                return;
            }

            if (!DA.GetDataList(3, ghSupports) || ghSupports.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least one support required.");
                return;
            }

            DA.GetData(1, ref useSection);
            DA.GetData(4, ref welInput);
            DA.GetData(5, ref fyk);
            DA.GetData(6, ref Emean);

            // ── basic validation ─────────────────────────────────────────────
            if (fyk <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "fyk must be > 0"); return; }
            if (Emean <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "E_mean must be > 0"); return; }

            var beam = ghBeam.Value;

            // ── extract section modulus from beam if available ──────────────

            if (useSection && beam?.Sec != null && welInput <= 0)
            {
                welInput = beam.Sec.Wy;  // Use Wy (bending around y-axis) as the elastic section modulus
            }


            // ── extract loads and supports to determine beam geometry ────────
            double Qk = 0.0;
            double Gk = 0.0;
            Point3d loadPoint = Point3d.Unset;

            foreach (var ghLd in ghLoads)
            {
                var ld = ghLd.Value;
                if (ld is TB_Load_Point ptLoad)
                {
                    // Extract point load magnitude 
                    Qk = Math.Abs(ptLoad.Loads[2]);  // Loads[2] = Z-component
                    loadPoint = ptLoad.Pt;
                }
                // TODO: Add TB_Load_Distributed handling for Gk
            }

            // For now, assume Gk = 0 if not explicitly handled
            if (Gk == 0 && Qk > 0)
                Gk = 0.001 * Qk;  // Small default UDL

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
            double L = CalculateBeamLength(supportPts);
            double a = CalculateLoadPosition(loadPoint, supportPts, L);

            if (L <= 0) 
            { 
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Beam length must be > 0"); 
                return; 
            }

            // ── validate load position is within span ────────────────────────
            if (a <= 0 || a >= L)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, 
                    $"Load position {a:F2} m is outside beam span [0, {L:F2}]. Clamping to valid range.");
                a = Math.Max(0.001 * L, Math.Min(0.999 * L, a));
            }

            // if useSection = true, set sectionGiven > 0, if not, skip.
            bool sectionGiven = useSection || welInput > 0;

            // ── Eurocode sizing ───────────────────────────────────────────────
            double W_el_req = BeamMechanics.WelEurocode(L, Gk, Qk, a, fyk);
            double W_el_form = sectionGiven ? welInput : W_el_req;

            // ── build random variables ────────────────────────────────────────
            var rvs = RandomVariable.BuildEurocodeRVs(fyk, Gk, Qk);

            // ── FORM ──────────────────────────────────────────────────────────
            double beta;
            bool converged;
            FORM.Run(W_el_form, L, a, rvs, out beta, out converged);

            double Pf = Normal.CDF(0, 1, -beta);

            // ── utilization ───────────────────────────────────────────────────
            // Calculate for both provided section and Eurocode-optimized section
            //double M_Ed = BeamMechanics.MaxMoment(L, Eurocode.gammaG * Gk, Eurocode.gammaQ * Qk, a);
            double M_Ed = BeamMechanics.MaxMoment(L, Gk, Qk, a);
            double W_el_for_util = sectionGiven ? welInput : W_el_req;
            //double M_Rd = W_el_for_util * fyk / Eurocode.gammaM / 1e6;  // kNm
            double M_Rd = W_el_for_util * fyk / 1e6;  // kNm
            double utilization = M_Ed / M_Rd;

            // ── warn if not converged ─────────────────────────────────────────
            if (!converged)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "HLRF did not fully converge. Beta estimate is still valid but treat with caution.");

            // ── set outputs ───────────────────────────────────────────────────
            DA.SetData(0, beta);
            DA.SetData(1, Pf);
            DA.SetData(2, W_el_req);
            DA.SetData(3, utilization);
            DA.SetData(4, converged);
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
