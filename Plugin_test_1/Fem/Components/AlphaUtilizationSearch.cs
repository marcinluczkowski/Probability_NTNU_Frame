using System;
using Grasshopper.Kernel;
using Plugin_test_1.Reliability;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Plugin_test_1.Fem.Components
{
    public class AlphaUtilizationSearch : GH_Component
    {
        public AlphaUtilizationSearch()
          : base("Alpha Utilization Search", "AlphaSearch",
              "Search alpha resistance/load values that give target utilization.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model", "Solved or unsolved FEM model", GH_ParamAccess.item);
            pManager.AddTextParameter("Material", "Mat", "Material name (currently mapped to lognormal)", GH_ParamAccess.item, "Steel");
            pManager.AddNumberParameter("Characteristic Strength", "Rk", "Characteristic resistance/strength value", GH_ParamAccess.item, 355.0);
            pManager.AddTextParameter("Load Type", "LoadType", "Load type: variable -> gumbel, permanent/other -> normal", GH_ParamAccess.item, "permanent");
            pManager.AddNumberParameter("Characteristic Load", "Sk", "Characteristic load effect value (same level as model loads)", GH_ParamAccess.item);
            pManager.AddNumberParameter("Target Beta", "Beta", "Target reliability index", GH_ParamAccess.item, 3.8);
            pManager.AddNumberParameter("Target Utilization", "U*", "Target utilization (usually 1.0)", GH_ParamAccess.item, 1.0);
            pManager.AddNumberParameter("AlphaR Min", "aR min", "Lower bound for resistance alpha", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("AlphaR Max", "aR max", "Upper bound for resistance alpha", GH_ParamAccess.item, 1.0);
            pManager.AddNumberParameter("AlphaR Step", "aR step", "Step for resistance alpha", GH_ParamAccess.item, 0.02);
            pManager.AddNumberParameter("AlphaS Min", "aS min", "Lower bound for load alpha", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("AlphaS Max", "aS max", "Upper bound for load alpha", GH_ParamAccess.item, 1.0);
            pManager.AddNumberParameter("AlphaS Step", "aS step", "Step for load alpha", GH_ParamAccess.item, 0.02);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Alpha Resistance", "AlphaR", "Best alpha for resistance", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha Load", "AlphaS", "Best alpha for load", GH_ParamAccess.item);
            pManager.AddNumberParameter("Utilization", "U", "Resulting max utilization", GH_ParamAccess.item);
            pManager.AddNumberParameter("Design Value Resistance", "DVResistance", "Design value for resistance", GH_ParamAccess.item);
            pManager.AddNumberParameter("Design Value Load", "DVLoad", "Design value for load", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_TB_Model ghModel = null;
            string material = "Steel";
            double rk = 355.0;
            string loadType = "permanent";
            double sk = 0.0;
            double beta = 3.8;
            double targetUtil = 1.0;
            double aRMin = 0.0;
            double aRMax = 1.0;
            double aRStep = 0.02;
            double aSMin = 0.0;
            double aSMax = 1.0;
            double aSStep = 0.02;

            if (!DA.GetData(0, ref ghModel) || ghModel?.Value == null) return;
            if (!DA.GetData(1, ref material)) return;
            if (!DA.GetData(2, ref rk)) return;
            if (!DA.GetData(3, ref loadType)) return;
            if (!DA.GetData(4, ref sk)) return;
            if (!DA.GetData(5, ref beta)) return;
            if (!DA.GetData(6, ref targetUtil)) return;
            if (!DA.GetData(7, ref aRMin)) return;
            if (!DA.GetData(8, ref aRMax)) return;
            if (!DA.GetData(9, ref aRStep)) return;
            if (!DA.GetData(10, ref aSMin)) return;
            if (!DA.GetData(11, ref aSMax)) return;
            if (!DA.GetData(12, ref aSStep)) return;

            if (rk <= 0 || sk <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Characteristic strength/load must be > 0.");
                return;
            }

            if (aRStep <= 0 || aSStep <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Alpha step values must be > 0.");
                return;
            }

            if (aRMin > aRMax || aSMin > aSMax)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Alpha min must be <= alpha max.");
                return;
            }

            var mdl = ghModel.Value;
            if (mdl.Elem1Ds == null || mdl.Elem1Ds.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Model has no elements.");
                return;
            }

            try
            {
                if (mdl.Disps == null || mdl.Disps.Count == 0)
                {
                    _ = new SolveLS(ref mdl);
                }

                const double placeholderCov = 0.1; // Placeholder until table values are connected.
                const double placeholderPercentile = 0.05; // Placeholder until table values are connected.

                var resistanceRv = new RandomVariable(
                    string.IsNullOrWhiteSpace(material) ? "material" : material,
                    rk,
                    placeholderPercentile,
                    placeholderCov,
                    "lognormal");

                var loadRv = new RandomVariable(
                    string.IsNullOrWhiteSpace(loadType) ? "load" : loadType,
                    sk,
                    placeholderPercentile,
                    placeholderCov,
                    GetLoadDistributionType(loadType));

                var calculator = new DesignValueCalculator();

                bool found = false;
                double bestAlphaR = 0.0;
                double bestAlphaS = 0.0;
                double bestUtil = 0.0;
                double bestYdR = 0.0;
                double bestYdS = 0.0;
                double bestErr = double.MaxValue;

                for (double alphaR = aRMin; alphaR <= aRMax + 1e-12; alphaR += aRStep)
                {
                    for (double alphaS = aSMin; alphaS <= aSMax + 1e-12; alphaS += aSStep)
                    {
                        if (alphaR < 0 || alphaR > 1 || alphaS < 0 || alphaS > 1)
                            continue;

                        var resistanceResult = calculator.CalculateDesignValue(
                            alphaR,
                            resistanceRv.Mean,
                            resistanceRv.COV,
                            beta,
                            resistanceRv.DistType);

                        var loadResult = calculator.CalculateDesignValue(
                            alphaS,
                            loadRv.Mean,
                            loadRv.COV,
                            beta,
                            loadRv.DistType);

                        double gammaS = loadResult.designvalue_yd / sk;
                        double util = EvaluateMaxUtilization(mdl, gammaS, resistanceResult.designvalue_yd);
                        double err = Math.Abs(util - targetUtil);

                        if (err < bestErr)
                        {
                            bestErr = err;
                            bestAlphaR = alphaR;
                            bestAlphaS = alphaS;
                            bestUtil = util;
                            bestYdR = resistanceResult.designvalue_yd;
                            bestYdS = loadResult.designvalue_yd;
                            found = true;
                        }
                    }
                }

                if (!found)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No alpha pair found in search range.");
                    return;
                }

                DA.SetData(0, bestAlphaR);
                DA.SetData(1, bestAlphaS);
                DA.SetData(2, bestUtil);
                DA.SetData(3, bestYdR);
                DA.SetData(4, bestYdS);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        private static string GetLoadDistributionType(string loadType)
        {
            if (string.IsNullOrWhiteSpace(loadType))
                return "normal";

            return loadType.Trim().ToLowerInvariant() == "variable" ? "gumbel" : "normal";
        }

        private static double EvaluateMaxUtilization(TB_Model model, double gammaS, double fyDesign)
        {
            double maxU = 0.0;
            int nLc = (model.Disps == null || model.Disps.Count == 0) ? 0 : model.Disps.Count;

            for (int lcId = 0; lcId < nLc; lcId++)
            {
                foreach (var e in model.Elem1Ds)
                {
                    if (e?.Sec == null || e.Nodes == null || e.Nodes.Count < 2) continue;
                    if (e.Nodes[0].Disps == null || e.Nodes[1].Disps == null) continue;
                    if (e.Nodes[0].Disps.Count <= lcId || e.Nodes[1].Disps.Count <= lcId) continue;

                    var f = e.Calc_Forces(lcId);

                    double nEd = gammaS * Math.Max(Math.Abs(f[0]), Math.Abs(f[6]));
                    double myEd = gammaS * Math.Max(Math.Abs(f[4]), Math.Abs(f[10]));
                    double mzEd = gammaS * Math.Max(Math.Abs(f[5]), Math.Abs(f[11]));

                    double nRd = fyDesign * e.Sec.Area;
                    double myRd = fyDesign * e.Sec.Wy / 1000.0;
                    double mzRd = fyDesign * e.Sec.Wz / 1000.0;

                    if (nRd <= 0 || myRd <= 0 || mzRd <= 0) continue;

                    double u = nEd / nRd + myEd / myRd + mzEd / mzRd;
                    if (u > maxU) maxU = u;
                }
            }

            return maxU;
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("α");

        public override Guid ComponentGuid => new Guid("7F3A32AB-0F80-4B09-B8AF-2B0680645C2A");
    }
}
