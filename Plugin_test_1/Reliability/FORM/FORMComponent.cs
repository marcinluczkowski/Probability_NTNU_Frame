using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Plugin_test_1.Reliability.FORM;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// FORM Reliability Solver Component
    /// 
    /// Implements First Order Reliability Method (HL-RF algorithm) for structural reliability analysis.
    /// 
    /// Inputs:
    /// - Model: Pre-solved FEM model with geometry, topology, supports, and reference loads
    /// - Variables: List of RandomVariable definitions (distribution, mean, stddev)
    /// - ParamNames: List of parameter names matching the model ("E", "F", "I", etc.)
    /// - LSFType: Limit state function type ("Deflection" or "Stress")
    /// - Capacity: Allowable value (deflection limit or stress limit)
    /// - Tolerance: Relative |g| convergence tolerance (unit-independent, default 1e-3)
    /// - MaxIter: Maximum iterations (default 50)
    /// - StepTol: Absolute ‖Δu‖ convergence tolerance (default 1e-6)
    /// - PerElement: If true, also run FORM per element (Stress LSF only)
    /// 
    /// Outputs:
    /// - Beta: Reliability index (system / governing limit state)
    /// - Pf: Probability of failure
    /// - MPP_X: Most probable point in physical space
    /// - MPP_U: Most probable point in standard normal space
    /// - Alpha: Sensitivity factors
    /// - Summary: Human-readable result string
    /// - Converged: Boolean indicating if HL-RF converged
    /// - BetaHistory: Reliability index at each iteration (for convergence plot)
    /// - ElementTags / Beta_e / Pf_e / Alpha_e / Converged_e: per-element results (PerElement mode)
    /// </summary>
    public class FORMComponent : GH_Component
    {
        // =====================================================================
        // CONSTRUCTOR AND METADATA
        // =====================================================================

        public FORMComponent()
            : base(
                "FORM Reliability",
                "FORM",
                "First Order Reliability Method (FORM) for structural reliability analysis. " +
                "Computes reliability index β and probability of failure Pf using the HL-RF algorithm.",
                Common.category,
                "FORM · 02 Solver")
        { }

        public override Guid ComponentGuid =>
            new Guid("a1b2c3d4-e5f6-4a5b-9c7d-8e9f0a1b2c3d");

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("FORM");

        // =====================================================================
        // INPUT PARAMETERS
        // =====================================================================

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "M", "Pre-solved FEM model (geometry, topology, supports)", GH_ParamAccess.item);
            pManager.AddParameter(new Param_RandomVariable(), "Variables", "RV", "Random variable definitions", GH_ParamAccess.list);
            pManager.AddTextParameter("ParamNames", "PN", "Parameter names matching model (e.g., 'E', 'F', 'I')", GH_ParamAccess.list);
            pManager.AddTextParameter("LSFType", "LSF", "Limit state function type ('Deflection' or 'Stress')", GH_ParamAccess.item, "Deflection");
            pManager.AddNumberParameter("Capacity", "Cap", "Allowable value (deflection limit in [mm], or stress limit in [MPa])", GH_ParamAccess.item);
            pManager.AddNumberParameter("Tolerance", "Tol", "Relative tolerance on |g| (unit-independent, scaled by a characteristic |g|). Default 1e-3.", GH_ParamAccess.item, 1e-3);
            pManager.AddIntegerParameter("MaxIter", "It", "Maximum iterations (default 50)", GH_ParamAccess.item, 50);
            pManager.AddNumberParameter("StepTol", "sTol", "Absolute tolerance on the U-space step ‖Δu‖ (default 1e-6).", GH_ParamAccess.item, 1e-6);
            pManager.AddBooleanParameter("PerElement", "PE", "If true, also run FORM per element (Stress LSF only) and output β/Pf/α for every element.", GH_ParamAccess.item, false);

            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
            pManager[6].Optional = true;
            pManager[7].Optional = true;
            pManager[8].Optional = true;
        }

        // =====================================================================
        // OUTPUT PARAMETERS
        // =====================================================================

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Beta", "β", "Reliability index", GH_ParamAccess.item);
            pManager.AddNumberParameter("Pf", "Pf", "Probability of failure", GH_ParamAccess.item);
            pManager.AddNumberParameter("MPP_X", "x*", "Most probable point in physical space", GH_ParamAccess.list);
            pManager.AddNumberParameter("MPP_U", "u*", "Most probable point in standard normal space", GH_ParamAccess.list);
            pManager.AddNumberParameter("Alpha", "α", "Sensitivity factors", GH_ParamAccess.list);
            pManager.AddTextParameter("Summary", "S", "Human-readable result summary", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Converged", "✓", "Did FORM converge?", GH_ParamAccess.item);
            pManager.AddNumberParameter("BetaHistory", "βH", "Reliability index at each iteration", GH_ParamAccess.list);
            pManager.AddTextParameter("ElementTags", "eT", "Per-element tags (PerElement mode)", GH_ParamAccess.list);
            pManager.AddNumberParameter("Beta_e", "β_e", "Per-element reliability index (PerElement mode)", GH_ParamAccess.list);
            pManager.AddNumberParameter("Pf_e", "Pf_e", "Per-element probability of failure (PerElement mode)", GH_ParamAccess.list);
            pManager.AddNumberParameter("Alpha_e", "α_e", "Per-element sensitivity factors, one branch per element (PerElement mode)", GH_ParamAccess.tree);
            pManager.AddBooleanParameter("Converged_e", "✓_e", "Per-element convergence flags (PerElement mode)", GH_ParamAccess.list);
        }

        // =====================================================================
        // SOLVE INSTANCE
        // =====================================================================

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── Read and validate inputs ──────────────────────────────────────

            GH_TB_Model ghModel = null;
            var ghVariables = new List<GH_RandomVariable>();
            var ghParamNames = new List<string>();
            string lsfType = "Deflection";
            double capacity = 0.0;
            double relTol = 1e-3;
            int maxIter = 50;
            double stepTol = 1e-6;
            bool perElement = false;

            // Read model
            if (!DA.GetData(0, ref ghModel) || ghModel?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Valid FEM model required.");
                return;
            }
            TB_Model model = ghModel.Value;

            // Read variables
            if (!DA.GetDataList(1, ghVariables) || ghVariables.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least one random variable required.");
                return;
            }

            // Read parameter names
            if (!DA.GetDataList(2, ghParamNames) || ghParamNames.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Parameter names required.");
                return;
            }

            // Validate variable count matches parameter count
            if (ghVariables.Count != ghParamNames.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Variable count ({ghVariables.Count}) must match parameter name count ({ghParamNames.Count}).");
                return;
            }

            // Read LSF type, capacity, tolerances, maxIter, per-element toggle
            DA.GetData(3, ref lsfType);
            bool hasCapacity = DA.GetData(4, ref capacity);
            DA.GetData(5, ref relTol);
            DA.GetData(6, ref maxIter);
            DA.GetData(7, ref stepTol);
            DA.GetData(8, ref perElement);

            // Validate LSF type
            if (string.IsNullOrEmpty(lsfType) || (lsfType != "Deflection" && lsfType != "Stress"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"LSFType '{lsfType}' not recognized. Using 'Deflection'.");
                lsfType = "Deflection";
            }

            // Validate capacity
            bool fyMapped = false;
            foreach (string p in ghParamNames)
            {
                if (p != null && (p.ToLower() == "fy" || p.ToLower() == "yield_strength"))
                    fyMapped = true;
            }

            if (lsfType == "Stress")
            {
                if (fyMapped || !hasCapacity)
                {
                    capacity = -1.0; // Enforce auto-evaluator
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, fyMapped ?
                        "Random variable 'fy' mapped. Overriding manual capacity input to couple structurally." :
                        "No Capacity provided. Using Material Yield Strength (Fy) auto-evaluation for Stress constraint.");
                }
            }
            else // Deflection
            {
                if (!hasCapacity)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Capacity (allowable value) required for Deflection limit state.");
                    return;
                }
                else if (capacity <= 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Capacity must be > 0 for Deflection limit state.");
                    return;
                }
            }

            // ── Extract RandomVariable values ────────────────────────────────

            var variables = new List<RandomVariable>();
            foreach (var ghRv in ghVariables)
            {
                if (ghRv?.Value == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid random variable (null).");
                    return;
                }
                variables.Add(ghRv.Value);
            }

            // ── Build parameter map ──────────────────────────────────────────

            var parameterMap = new Dictionary<string, int>();
            for (int i = 0; i < ghParamNames.Count; i++)
            {
                string paramName = ghParamNames[i];
                if (string.IsNullOrWhiteSpace(paramName))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Parameter name at index {i} is empty.");
                    return;
                }
                parameterMap[paramName] = i;
            }

            // ── Build FEM solver adapter ─────────────────────────────────────

            FEMSolverAdapter adapter;
            try
            {
                adapter = new FEMSolverAdapter(model, parameterMap, capacity, lsfType);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Failed to create adapter: {ex.Message}");
                return;
            }

            // ── Create limit state function ──────────────────────────────────

            Func<double[], double> limitStateFunction = adapter.AsLimitStateFunction();

            // ── Instantiate and run FORM solver ──────────────────────────────

            FORMSolver solver = new FORMSolver();
            FORMResult result;

            double g_at_mean = limitStateFunction(variables.Select(v => v.Mean).ToArray());
            string regime = g_at_mean > 0 ? "safe at mean" : "FAILED at mean (g < 0)";
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"g at mean = {g_at_mean:G6} ({regime})");

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
            $"LSFType='{lsfType}', capacity={capacity}, paramMap={string.Join(",", parameterMap.Keys)}");

            try
            {
                result = solver.Solve(variables, limitStateFunction, epsilon1: relTol, epsilon2: stepTol, maxIterations: maxIter);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"FORM solver failed: {ex.Message}");
                return;
            }

            // ── Check convergence and warn if needed ─────────────────────────

            if (!result.Converged)
            {
                var level = result.ConvergenceMessage.Contains("FOSM estimate")
                    ? GH_RuntimeMessageLevel.Remark
                    : GH_RuntimeMessageLevel.Warning;
                AddRuntimeMessage(level, result.ConvergenceMessage);
            }
            else if (result.ConvergenceMessage.Contains("Over-designed"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, result.ConvergenceMessage);
            }

            // ── Set outputs ──────────────────────────────────────────────────

            DA.SetData(0, result.Beta);
            DA.SetData(1, result.ProbabilityOfFailure);

            // MPP in physical space: transform from standard normal
            var mpp_X = new List<double>();
            for (int i = 0; i < result.MPP_U.Length; i++)
            {
                double x = variables[i].ToX(result.MPP_U[i]);
                mpp_X.Add(x);
            }
            DA.SetDataList(2, mpp_X);

            // MPP in standard normal space
            DA.SetDataList(3, result.MPP_U);

            // Alpha factors
            DA.SetDataList(4, result.AlphaFactors);

            // Summary string
            DA.SetData(5, result.FormatSummary());

            // Converged flag
            DA.SetData(6, result.Converged);

            // Beta history
            DA.SetDataList(7, result.BetaHistory);

            // ── Per-element analysis (optional) ──────────────────────────────
            if (perElement)
            {
                if (lsfType != "Stress")
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "PerElement analysis supports the 'Stress' limit state only. Skipping per-element outputs.");
                }
                else if (adapter.ElementCount == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Model has no 1D elements for per-element analysis.");
                }
                else
                {
                    int m = adapter.ElementCount;
                    List<string> tags = adapter.GetElementTags();

                    var betaE = new List<double>();
                    var pfE = new List<double>();
                    var convE = new List<bool>();
                    var alphaTree = new GH_Structure<GH_Number>();

                    int govIdx = -1;
                    double govBeta = double.MaxValue;

                    for (int e = 0; e < m; e++)
                    {
                        var path = new GH_Path(e);
                        Func<double[], double> lsfE = adapter.AsLimitStateFunctionForElement(e);

                        FORMResult re;
                        try
                        {
                            re = solver.Solve(variables, lsfE, epsilon1: relTol, epsilon2: stepTol, maxIterations: maxIter);
                        }
                        catch (Exception ex)
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Element {tags[e]}: FORM failed ({ex.Message}).");
                            betaE.Add(double.NaN);
                            pfE.Add(double.NaN);
                            convE.Add(false);
                            for (int k = 0; k < variables.Count; k++) alphaTree.Append(new GH_Number(double.NaN), path);
                            continue;
                        }

                        betaE.Add(re.Beta);
                        pfE.Add(re.ProbabilityOfFailure);
                        convE.Add(re.Converged);
                        foreach (double a in re.AlphaFactors) alphaTree.Append(new GH_Number(a), path);

                        if (!double.IsNaN(re.Beta) && re.Beta < govBeta)
                        {
                            govBeta = re.Beta;
                            govIdx = e;
                        }
                    }

                    DA.SetDataList(8, tags);
                    DA.SetDataList(9, betaE);
                    DA.SetDataList(10, pfE);
                    DA.SetDataTree(11, alphaTree);
                    DA.SetDataList(12, convE);

                    if (govIdx >= 0)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            $"Governing element: {tags[govIdx]} (β = {govBeta:F3}, Pf = {pfE[govIdx]:E3}).");
                }
            }
        }
    }
}