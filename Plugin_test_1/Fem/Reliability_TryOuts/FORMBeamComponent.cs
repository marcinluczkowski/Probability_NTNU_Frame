using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Plugin_test_1.Reliability;
using Rhino.Geometry;
using MathNet.Numerics.Distributions;

namespace FORMBeam
{
    public class FORMBeamComponent : GH_Component
    {
        public FORMBeamComponent()
            : base(
                "FORM Beam",                              // display name
                "FORM",                                   // abbreviation (on component)
                "First Order Reliability Method analysis for a simply-supported beam " +
                "with a UDL and an off-centre point load. " +
                "Outputs deflection geometry, utilization, reliability index and Pf.",
                "Structural",                             // category tab
                "Reliability")                            // sub-category
        { }

        // ── unique ID – generate once, never change ───────────────────────────
        public override Guid ComponentGuid =>
            new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        // ── icon (16x16 default) ──────────────────────────────────────────────
        protected override System.Drawing.Bitmap Icon => null;

        // =====================================================================
        // INPUTS
        // =====================================================================
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("L",       "L",       "Beam span [m]",                                          GH_ParamAccess.item, 6.0);
            pManager.AddNumberParameter("x_load",  "x",       "Point load position from left support [m]",              GH_ParamAccess.item, 3.0);
            pManager.AddNumberParameter("Q_k",     "Qk",      "Characteristic point load [kN]",                         GH_ParamAccess.item, 10.0);
            pManager.AddNumberParameter("G_k",     "Gk",      "Characteristic UDL [kN/m]",                              GH_ParamAccess.item, 20.0);
            pManager.AddNumberParameter("fy_k",    "fyk",     "Characteristic yield strength [MPa] (e.g. 355 for S355)",GH_ParamAccess.item, 355.0);
            pManager.AddNumberParameter("E_mean",  "E",       "Mean Young's modulus [MPa] (e.g. 210000 for steel)",      GH_ParamAccess.item, 210000.0);
            pManager.AddNumberParameter("b",       "b",       "Section width [mm]  (set 0 or leave unconnected for sizing only)", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("h",       "h",       "Section height [mm] (set 0 or leave unconnected for sizing only)", GH_ParamAccess.item, 0.0);

            // make b and h optional
            pManager[6].Optional = true;
            pManager[7].Optional = true;
        }

        // =====================================================================
        // OUTPUTS
        // =====================================================================
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter ("deflection_pts", "Def",   "50 points forming the deformed beam shape (coords in mm, deflection along -Y)", GH_ParamAccess.list);
            pManager.AddNumberParameter("max_deflection", "dmax",  "Maximum deflection [mm]",                                                         GH_ParamAccess.item);
            pManager.AddNumberParameter("utilization",    "Util",  "Elastic bending utilization M_Ed/M_Rd  (NaN if no section given)",                GH_ParamAccess.item);
            pManager.AddNumberParameter("beta",           "β",     "FORM reliability index",                                                           GH_ParamAccess.item);
            pManager.AddNumberParameter("Pf",             "Pf",    "Probability of failure",                                                           GH_ParamAccess.item);
            pManager.AddNumberParameter("W_el_required",  "Wel",   "Required elastic section modulus [mm³] from Eurocode sizing",                      GH_ParamAccess.item);
            pManager.AddBooleanParameter("converged",     "Conv",  "True if HLRF algorithm converged",                                                 GH_ParamAccess.item);
        }

        // =====================================================================
        // SOLVE
        // =====================================================================
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── read inputs ──────────────────────────────────────────────────
            double L      = 6.0, xLoad = 3.0, Qk = 10.0, Gk = 20.0;
            double fyk    = 355.0, Emean = 210000.0, b = 0.0, h = 0.0;

            if (!DA.GetData(0, ref L))      return;
            if (!DA.GetData(1, ref xLoad))  return;
            if (!DA.GetData(2, ref Qk))     return;
            if (!DA.GetData(3, ref Gk))     return;
            if (!DA.GetData(4, ref fyk))    return;
            if (!DA.GetData(5, ref Emean))  return;
            DA.GetData(6, ref b);
            DA.GetData(7, ref h);

            // ── basic validation ─────────────────────────────────────────────
            if (L <= 0)    { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "L must be > 0"); return; }
            if (Qk < 0)    { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Q_k must be >= 0"); return; }
            if (Gk < 0)    { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "G_k must be >= 0"); return; }
            if (fyk <= 0)  { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "fy_k must be > 0"); return; }
            if (Emean <= 0){ AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "E_mean must be > 0"); return; }

            // clamp load position strictly inside span
            double a = Math.Max(0.001 * L, Math.Min(0.999 * L, xLoad));
            if (Math.Abs(a - xLoad) > 1e-9)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "x_load clamped to [0.001L, 0.999L] to avoid boundary singularities.");

            bool sectionGiven = (b > 0 && h > 0);

            // ── Eurocode sizing ───────────────────────────────────────────────
            double W_el_req = BeamMechanics.WelEurocode(L, Gk, Qk, a, fyk);

            // ── section properties ────────────────────────────────────────────
            double W_el_section = double.NaN;
            double I_mm4        = double.NaN;
            double W_el_form    = W_el_req;

            if (sectionGiven)
            {
                W_el_section = b * h * h / 6.0;     // elastic  [mm³]
                I_mm4        = b * h * h * h / 12.0; // [mm⁴]
                W_el_form    = W_el_section;
            }

            // ── build random variables ────────────────────────────────────────
            var rvs = RandomVariable.BuildEurocodeRVs(fyk, Gk, Qk);

            // ── FORM ──────────────────────────────────────────────────────────
            double beta;
            bool   converged;
            FORM.Run(W_el_form, L, a, rvs, out beta, out converged);

            double Pf = Normal.CDF(0, 1, -beta);

            // ── utilization ───────────────────────────────────────────────────
            double utilization = double.NaN;
            if (sectionGiven)
            {
                double M_Ed = BeamMechanics.MaxMoment(L, Eurocode.gammaG * Gk, Eurocode.gammaQ * Qk, a);
                double M_Rd = W_el_section * fyk / Eurocode.gammaM / 1e6; // kNm
                utilization = M_Ed / M_Rd;
            }

            // ── deflection curve (50 pts) ─────────────────────────────────────
            // If no section given estimate I from a square section with W_el_req
            if (!sectionGiven)
            {
                double h_est = Math.Pow(6.0 * W_el_req, 1.0 / 3.0);
                I_mm4 = h_est * h_est * h_est * h_est / 12.0;
            }

            int    nPts   = 50;
            var    defPts = new List<Point3d>(nPts);
            double maxDef = 0.0;

            for (int i = 0; i < nPts; i++)
            {
                double z   = i * L / (nPts - 1);
                double def = BeamMechanics.DeflectionAt(z, L, Gk, Qk, a, Emean, I_mm4); // mm
                if (def > maxDef) maxDef = def;
                double xMm = z * 1000.0;   // m -> mm along beam axis
                double yMm = -def;          // downward = negative Y
                defPts.Add(new Point3d(xMm, yMm, 0.0));
            }

            // ── warn if not converged ─────────────────────────────────────────
            if (!converged)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "HLRF did not fully converge. Beta estimate is still valid but treat with caution.");

            // ── set outputs ───────────────────────────────────────────────────
            DA.SetDataList(0, defPts);
            DA.SetData    (1, maxDef);
            DA.SetData    (2, utilization);
            DA.SetData    (3, beta);
            DA.SetData    (4, Pf);
            DA.SetData    (5, W_el_req);
            DA.SetData    (6, converged);
        }
    }
}
