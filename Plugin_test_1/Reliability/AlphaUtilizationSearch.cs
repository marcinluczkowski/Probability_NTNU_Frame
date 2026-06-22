using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Rhino.Geometry;

namespace Plugin_test_1.Reliability
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
            pManager.AddBooleanParameter("Error list", "", "", GH_ParamAccess.list);
            pManager.AddNumberParameter("Alpha Resistances List", "", "", GH_ParamAccess.list);
            pManager.AddNumberParameter("Alpha Loads List", "", "", GH_ParamAccess.list);
            pManager.AddBooleanParameter("All Error list", "", "", GH_ParamAccess.list);
            pManager.AddNumberParameter("All Alpha Resistances List", "", "", GH_ParamAccess.list);
            pManager.AddNumberParameter("All Alpha Loads List", "", "", GH_ParamAccess.list);
            pManager.AddParameter(new Param_TB_Model(), "Optimized Model", "Model*", "Optimized model with the best alpha pair design load level", GH_ParamAccess.item);
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
            double aRMin = -1.0;
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

            var mdl = ghModel.Value?.DeepCopy();
            if (mdl == null || mdl.Elem1Ds == null || mdl.Elem1Ds.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Model has no elements.");
                return;
            }

            try
            {
                bool knownMaterial = StochasticInputCatalog.HasMaterial(material);
                bool knownLoadType = StochasticInputCatalog.HasLoadType(loadType);

                var materialInput = StochasticInputCatalog.GetMaterialInput(material);
                var loadInput = StochasticInputCatalog.GetLoadInput(loadType);

                if (!knownMaterial)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Unknown material in stochastic catalog. Using default material COV/percentile.");

                if (!knownLoadType)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Unknown load type in stochastic catalog. Using default load COV/percentile.");

                var resistanceRv = new RandomVariable(
                    string.IsNullOrWhiteSpace(material) ? "material" : material,
                    rk,
                    materialInput.Percentile,
                    materialInput.COV,
                    materialInput.DistributionType);

                var loadRv = new RandomVariable(
                    string.IsNullOrWhiteSpace(loadType) ? "load" : loadType,
                    sk,
                    loadInput.Percentile,
                    loadInput.COV,
                    loadInput.DistributionType);

                var calculator = new DesignValueCalculator();

                double modelLoadReference = GetModelLoadReference(mdl);
                if (modelLoadReference <= 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Model has zero load magnitude. Cannot normalize to characteristic load.");
                    return;
                }

                // Normalize incoming model to characteristic load level so search is decoupled
                // from any upstream pre-scaling (e.g., DesignValues component outputs).
                double characteristicScale = sk / modelLoadReference;
                TB_Model characteristicModel = CreateScaledModel(mdl, characteristicScale);
                _ = new SolveLS(ref characteristicModel);
                double maxDemandCoefficient = ComputeMaxDemandCoefficient(characteristicModel);
                if (maxDemandCoefficient <= 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Characteristic model produced zero demand coefficient.");
                    return;
                }

                bool found = false;
                double bestAlphaR = 0.0;
                double bestAlphaS = 0.0;
                double bestUtil = 0.0;
                double bestYdR = 0.0;
                double bestYdS = 0.0;
                double bestErr = double.MaxValue;
                const double maxUtilizationTolerance = 1.00005;

                List<bool> errors = new List<bool>();
                List<double> good_alpha_Rs = new List<double>();
                List<double> good_alpha_Ss = new List<double>();

                List<bool> all_errors = new List<bool>();
                List<double> all_good_alpha_Rs = new List<double>();
                List<double> all_good_alpha_Ss = new List<double>();

                for (double alphaR = aRMin; alphaR <= aRMax + 1e-12; alphaR += aRStep)
                {
                    for (double alphaS = aSMin; alphaS <= aSMax + 1e-12; alphaS += aSStep)
                    {
                        all_good_alpha_Rs.Add(alphaR);
                        all_good_alpha_Ss.Add(alphaS);
                        bool f = false;

                        double alphaNorm = Math.Sqrt(alphaR * alphaR + alphaS * alphaS); // making sure the point is outside of beta-target 
                        if (alphaNorm <= 1.0)
                        {
                            all_errors.Add(f);
                            continue;
                        }

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

                        // Demand factor from characteristic to design level.
                        double gammaS = loadResult.designvalue_yd / sk;

                        // Reuse solved characteristic model and evaluate utilization algebraically.
                        double util = (gammaS * maxDemandCoefficient) / resistanceResult.designvalue_yd;
                        if (util > maxUtilizationTolerance)
                        {
                            all_errors.Add(f);
                            continue;
                        }

                        double err = Math.Abs(util - targetUtil);
                        bool betterError = err < bestErr;

                        if (betterError)
                        {
                            bestErr = err;
                            bestAlphaR = alphaR;
                            bestAlphaS = alphaS;
                            bestUtil = util;                            
                            bestYdR = resistanceResult.designvalue_yd;
                            bestYdS = loadResult.designvalue_yd;
                            found = true;
                            f = true;
                            errors.Add(found);
                            good_alpha_Rs.Add(bestAlphaR);
                            good_alpha_Ss.Add(bestAlphaS);
                        }

                        all_errors.Add(f);
                    }
                }

                if (!found)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No alpha pair found in search range that satisfies both sqrt(alphaR^2 + alphaS^2) > 1 and utilization <= 1.0005.");
                    return;
                }

                double bestGammaS = bestYdS / sk;
                TB_Model optimizedModel = CreateScaledModel(mdl, bestGammaS);
                _ = new SolveLS(ref optimizedModel);

                DA.SetData(0, bestAlphaR);
                DA.SetData(1, bestAlphaS);
                DA.SetData(2, bestUtil);
                DA.SetData(3, bestYdR);
                DA.SetData(4, bestYdS);
                DA.SetDataList(5, errors);
                DA.SetDataList(6, good_alpha_Rs);
                DA.SetDataList(7, good_alpha_Ss);
                DA.SetDataList(8, all_errors);
                DA.SetDataList(9, all_good_alpha_Rs);
                DA.SetDataList(10, all_good_alpha_Ss);
                DA.SetData(11, new GH_TB_Model(optimizedModel));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        private static TB_Model CreateScaledModel(TB_Model sourceModel, double gammaS)
        {
            var elems = sourceModel.Elem1Ds?.ConvertAll(e => e?.DeepCopy()) ?? new System.Collections.Generic.List<TB_Element_1D>();
            var sups = sourceModel.Sups?.ConvertAll(s => s?.DeepCopy()) ?? new System.Collections.Generic.List<TB_Support>();
            var loads = new System.Collections.Generic.List<TB_Load>();

            if (sourceModel.Loads != null)
            {
                foreach (var l in sourceModel.Loads)
                {
                    if (l is TB_Load_Point pl && pl.Loads.Count >= 6)
                    {
                        var f = new Vector3d(pl.Loads[0] * gammaS, pl.Loads[1] * gammaS, pl.Loads[2] * gammaS);
                        var m = new Vector3d(pl.Loads[3] * gammaS, pl.Loads[4] * gammaS, pl.Loads[5] * gammaS);
                        loads.Add(new TB_Load_Point(pl.Pt, f, m, pl.Lc ?? 0));
                    }
                    else
                    {
                        loads.Add(l?.DeepCopy());
                    }
                }
            }

            return new TB_Model(elems, sups, loads);
        }

        private static double GetModelLoadReference(TB_Model model)
        {
            double maxAbs = 0.0;
            if (model?.Loads == null) return maxAbs;

            foreach (var l in model.Loads)
            {
                if (l is not TB_Load_Point pl || pl.Loads == null) continue;
                int count = Math.Min(6, pl.Loads.Count);
                for (int i = 0; i < count; i++)
                {
                    double v = Math.Abs(pl.Loads[i]);
                    if (v > maxAbs) maxAbs = v;
                }
            }

            return maxAbs;
        }

        private static double ComputeMaxDemandCoefficient(TB_Model model)
        {
            double maxCoeff = 0.0;
            int nLc = (model.Disps == null || model.Disps.Count == 0) ? 0 : model.Disps.Count;

            for (int lcId = 0; lcId < nLc; lcId++)
            {
                foreach (var e in model.Elem1Ds)
                {
                    if (e?.Sec == null || e.Nodes == null || e.Nodes.Count < 2) continue;
                    if (e.Nodes[0].Disps == null || e.Nodes[1].Disps == null) continue;
                    if (e.Nodes[0].Disps.Count <= lcId || e.Nodes[1].Disps.Count <= lcId) continue;

                    var f = e.Calc_Forces(lcId);

                    double nEd = Math.Max(Math.Abs(f[0]), Math.Abs(f[6]));
                    double myEd = Math.Max(Math.Abs(f[4]), Math.Abs(f[10]));
                    double mzEd = Math.Max(Math.Abs(f[5]), Math.Abs(f[11]));

                    double a = e.Sec.Area;
                    double wy = e.Sec.Wy / 1000.0;
                    double wz = e.Sec.Wz / 1000.0;

                    if (a <= 0 || wy <= 0 || wz <= 0) continue;

                    double coeff = nEd / a + myEd / wy + mzEd / wz;
                    if (coeff > maxCoeff) maxCoeff = coeff;
                }
            }

            return maxCoeff;
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("α");

        public override Guid ComponentGuid => new Guid("7F3A32AB-0F80-4B09-B8AF-2B0680645C2A");
    }
}
