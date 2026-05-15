using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Plugin_test_1.Reliability;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Rhino.Geometry;
using MathNet.Numerics.Distributions;

namespace Plugin_test_1.Fem.Components
{
    public class ReliabilityIndexCalculator : GH_Component
    {
        public ReliabilityIndexCalculator()
          : base("Reliability Index Calculator", "RelIndex",
              "Calculates the reliability index (beta) and probability of failure using FORM.",
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
            pManager.AddBooleanParameter("Include Model Uncertainties", "ModelUnc", "Include theta_R (resistance) and theta_E (execution) uncertainties", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Reliability Index", "Beta", "Calculated Reliability Index (Beta)", GH_ParamAccess.item);
            pManager.AddNumberParameter("Probability of Failure", "Pf", "Calculated Probability of Failure", GH_ParamAccess.item);
            pManager.AddNumberParameter("Utilization", "U", "Utilization ratio = Demand / Capacity at characteristic load level", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha Resistance", "AlphaR", "FORM direction cosine for resistance", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha Load", "AlphaS", "FORM direction cosine for load", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha theta_R", "AlphaThR", "FORM direction cosine for resistance model uncertainty", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha theta_E", "AlphaThE", "FORM direction cosine for execution model uncertainty", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_TB_Model ghModel = null;
            string material = "Steel";
            double rk = 355.0;
            string loadType = "permanent";
            double sk = 0.0;
            bool includeModelUnc = true;

            if (!DA.GetData(0, ref ghModel) || ghModel?.Value == null) return;
            if (!DA.GetData(1, ref material)) return;
            if (!DA.GetData(2, ref rk)) return;
            if (!DA.GetData(3, ref loadType)) return;
            if (!DA.GetData(4, ref sk)) return;
            DA.GetData(5, ref includeModelUnc);

            if (rk <= 0 || sk <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Characteristic strength/load must be > 0.");
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

                double modelLoadReference = GetModelLoadReference(mdl);
                if (modelLoadReference <= 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Model has zero load magnitude. Cannot normalize to characteristic load.");
                    return;
                }

                double characteristicScale = sk / modelLoadReference; // Multiplier to scale the largest load in the model to the characteristic load checked by the user 
                TB_Model characteristicModel = CreateScaledModel(mdl, characteristicScale); // Scale the model loads to match the characteristic load level
                _ = new SolveLS(ref characteristicModel); // Solve the model to get displacements and forces for the scaled loads
                double maxDemandCoefficient = ComputeMaxDemandCoefficient(characteristicModel); // Calculate the maximum demand coefficient from the solved model, which is used to adjust the load distribution in the reliability analysis

                if (maxDemandCoefficient <= 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Characteristic model produced zero demand coefficient.");
                    return;
                }

                // Calculate utilization at characteristic load level
                double utilization = maxDemandCoefficient / rk;

                // Adjust scale due to demand coefficient
                // The limit state function is G = R - S or G = theta_R * R - theta_E * c * S
                // Where Resistance R simplifies to resistance distribution
                // And Load S simplifies to MaxDemandCoefficient / sk * Load_distribution

                double loadMultiplier = maxDemandCoefficient / sk; // How much stress the most stressed part of the structure feels, lets us plug any value of the random load variable into the limit state function. (stress demand = loadMultiplier * random load variable)

                // FORM implementation with or without model uncertainties
                double beta;
                double pf;
                double alphaR, alphaS;

                if (includeModelUnc)
                {
                    beta = CalculateFORMWithModelUncertainties(resistanceRv, loadRv, loadMultiplier, 
                                                               out pf, out alphaR, out alphaS, 
                                                               out double alphaThR, out double alphaThE);

                    DA.SetData(0, beta);
                    DA.SetData(1, pf);
                    DA.SetData(2, utilization);
                    DA.SetData(3, alphaR);
                    DA.SetData(4, alphaS);
                    DA.SetData(5, alphaThR);
                    DA.SetData(6, alphaThE);
                }
                else
                {
                    beta = CalculateFORM(resistanceRv, loadRv, loadMultiplier, out pf, out alphaR, out alphaS);

                    DA.SetData(0, beta);
                    DA.SetData(1, pf);
                    DA.SetData(2, utilization);
                    DA.SetData(3, alphaR);
                    DA.SetData(4, alphaS);
                    DA.SetData(5, 0.0);
                    DA.SetData(6, 0.0);
                }
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        private double CalculateFORM(RandomVariable R, RandomVariable S, double loadMultiplier, out double pf, out double alphaR, out double alphaS)
        {
            // First Order Reliability Method (FORM)
            // Limit state function G(R, S) = R - c*S = 0

            // Transform to Standard Normal Space
            // Init with mean values
            double uR = Normal.InvCDF(0, 1, R.CDF(R.Mean));
            double uS = Normal.InvCDF(0, 1, S.CDF(S.Mean));

            double beta = 0;
            double beta_old = 0;
            int maxIter = 100;
            double tol = 1e-5;

            // Declare alphaR/alphaS outside the loop so we can return the final values
            alphaR = 0;
            alphaS = 0;

            for (int i = 0; i < maxIter; i++)
            {
                // Transform u_i back to Physical space x_i to evaluate limit state function and its gradient at the design point
                double xR = R.UtoX(uR);
                double xS = S.UtoX(uS);

                // Equivalent Normal formulation at design point 
                // sigma_N = phi(u) / f(x)
                // mu_N = x - u * sigma_N

                double pdfR = R.PDF(xR); // Probability density of R at the design point in physical space
                double sigmaN_R = pdfR > 0 ? Normal.PDF(0, 1, uR) / pdfR : 0.001; // Avoid division by zero, assign a small value if pdf is zero, Sigma_N represents the equivalent std. dev. of the normal distribution at the design point, which is used to compute the gradient in standard normal space
                double muN_R = xR - uR * sigmaN_R; // Equivalent mean in normal space, used for updating beta

                double pdfS = S.PDF(xS); // Probability density of S at the design point in physical space
                double sigmaN_S = pdfS > 0 ? Normal.PDF(0, 1, uS) / pdfS : 0.001; // Avoid division by zero, assign a small value if pdf is zero, Sigma_N represents the equivalent std. dev. of the normal distribution at the design point, which is used to compute the gradient in standard normal space
                double muN_S = xS - uS * sigmaN_S; // Equivalent mean in normal space, used for updating beta

                // Gradient of Limit State G in normal space
                // dG/duR = dG/dxR * dxR/duR = 1 * sigmaN_R
                // dG/duS = dG/dxS * dxS/duS = -loadMultiplier * sigmaN_S

                double dG_duR = sigmaN_R; //Rosenblatt transformation gradient component for R, represents how changes in the standard normal variable uR affect the limit state function G through the equivalent normal distribution at the design point
                double dG_duS = -loadMultiplier * sigmaN_S; //Rosenblatt transformation gradient component for S, represents how changes in the standard normal variable uS affect the limit state function G through the equivalent normal distribution at the design point

                // Norm of gradient
                double gradNorm = Math.Sqrt(dG_duR * dG_duR + dG_duS * dG_duS);

                // Direction cosines (alpha)
                alphaR = dG_duR / gradNorm; // Direction cosine for R, represents the contribution of the random variable R to the reliability index, normalized by the gradient norm
                alphaS = dG_duS / gradNorm; // Direction cosine for S, represents the contribution of the random variable S to the reliability index, normalized by the gradient norm

                // Evaluate Limit state function
                double G = xR - loadMultiplier * xS;

                // Update beta (linearized G = 0)
                beta = (muN_R - loadMultiplier * muN_S) / gradNorm;

                // Update design point in standard normal space
                uR = alphaR * beta;
                uS = alphaS * beta;

                if (Math.Abs(beta - beta_old) < tol)
                    break;

                beta_old = beta;
            }

            pf = Normal.CDF(0, 1, -beta);
            return Math.Max(0, beta); // If negative, means failure is likely
        }

        /// <summary>
        /// Extended FORM with model uncertainties (theta_R, theta_E).
        /// Limit state: G = theta_R * R - theta_E * c * S
        /// </summary>
        private double CalculateFORMWithModelUncertainties(RandomVariable R, RandomVariable S, double loadMultiplier,
                                                           out double pf, out double alphaR, out double alphaS,
                                                           out double alphaThR, out double alphaThE)
        {
            // Model uncertainties (Eurocode calibrated)
            var thetaR = new RandomVariable("theta_R", 1.15, 0.05, 0.05, "lognormal", 1.15);  // Resistance model uncertainty
            var thetaE = new RandomVariable("theta_E", 1.00, 0.50, 0.10, "lognormal", 1.00);  // Execution model uncertainty

            // Transform to Standard Normal Space — init with mean values
            double uR = Normal.InvCDF(0, 1, R.CDF(R.Mean));
            double uS = Normal.InvCDF(0, 1, S.CDF(S.Mean));
            double uThR = Normal.InvCDF(0, 1, thetaR.CDF(thetaR.Mean));
            double uThE = Normal.InvCDF(0, 1, thetaE.CDF(thetaE.Mean));

            double beta = 0;
            double beta_old = 0;
            int maxIter = 100;
            double tol = 1e-5;

            alphaR = 0;
            alphaS = 0;
            alphaThR = 0;
            alphaThE = 0;

            for (int i = 0; i < maxIter; i++)
            {
                // Transform back to physical space
                double xR = R.UtoX(uR);
                double xS = S.UtoX(uS);
                double xThR = thetaR.UtoX(uThR);
                double xThE = thetaE.UtoX(uThE);

                // Equivalent normal parameters at design point
                double sigmaN_R = R.PDF(xR) > 0 ? Normal.PDF(0, 1, uR) / R.PDF(xR) : 0.001;
                double muN_R = xR - uR * sigmaN_R;

                double sigmaN_S = S.PDF(xS) > 0 ? Normal.PDF(0, 1, uS) / S.PDF(xS) : 0.001;
                double muN_S = xS - uS * sigmaN_S;

                double sigmaN_ThR = thetaR.PDF(xThR) > 0 ? Normal.PDF(0, 1, uThR) / thetaR.PDF(xThR) : 0.001;
                double muN_ThR = xThR - uThR * sigmaN_ThR;

                double sigmaN_ThE = thetaE.PDF(xThE) > 0 ? Normal.PDF(0, 1, uThE) / thetaE.PDF(xThE) : 0.001;
                double muN_ThE = xThE - uThE * sigmaN_ThE;

                // Gradient: G = theta_R * R - theta_E * c * S
                double dG_duR = xThR * sigmaN_R;
                double dG_duS = -loadMultiplier * xThE * sigmaN_S;
                double dG_duThR = xR * sigmaN_ThR;
                double dG_duThE = -loadMultiplier * xS * sigmaN_ThE;

                // Norm of gradient
                double gradNorm = Math.Sqrt(dG_duR * dG_duR + dG_duS * dG_duS + dG_duThR * dG_duThR + dG_duThE * dG_duThE);

                if (gradNorm < 1e-12)
                {
                    pf = double.NaN;
                    return double.NaN;
                }

                // Direction cosines
                alphaR = dG_duR / gradNorm;
                alphaS = dG_duS / gradNorm;
                alphaThR = dG_duThR / gradNorm;
                alphaThE = dG_duThE / gradNorm;

                // Limit state function
                double G = xThR * xR - loadMultiplier * xThE * xS;

                // Update beta
                beta = (muN_ThR * muN_R - loadMultiplier * muN_ThE * muN_S) / gradNorm;

                // Update design point
                uR = alphaR * beta;
                uS = alphaS * beta;
                uThR = alphaThR * beta;
                uThE = alphaThE * beta;

                if (Math.Abs(beta - beta_old) < tol)
                    break;

                beta_old = beta;
            }

            pf = Normal.CDF(0, 1, -beta);
            return Math.Max(0, beta);
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
                    double myEd = Math.Max(Math.Abs(f[4]), Math.Abs(f[10]));  // in kNm
                    double mzEd = Math.Max(Math.Abs(f[5]), Math.Abs(f[11]));  // in kNm

                    double a = e.Sec.Area;
                    double wy = e.Sec.Wy;  // in mm³
                    double wz = e.Sec.Wz;  // in mm³

                    if (a <= 0 || wy <= 0 || wz <= 0) continue;

                    // Stress = nEd/a + myEd*1e6/wy + mzEd*1e6/wz [MPa]
                    // (kNm converted to N·mm: kNm * 1e6 = N·mm)
                    double coeff = nEd / a + myEd * 1e6 / wy + mzEd * 1e6 / wz;
                    if (coeff > maxCoeff) maxCoeff = coeff;
                }
            }

            return maxCoeff;
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("β");

        public override Guid ComponentGuid => new Guid("9A91FC07-DDB6-4C5C-B306-0BA10CFBDBA5");
    }
}