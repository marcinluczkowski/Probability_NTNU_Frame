using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Plugin_test_1.Reliability.FORM;
using Propability_NTNU_v1.Classes.Toolbox;

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
    /// - Tolerance: Convergence tolerance (default 1e-6)
    /// - MaxIter: Maximum iterations (default 50)
    /// 
    /// Outputs:
    /// - Beta: Reliability index
    /// - Pf: Probability of failure
    /// - MPP_X: Most probable point in physical space
    /// - MPP_U: Most probable point in standard normal space
    /// - Alpha: Sensitivity factors
    /// - Summary: Human-readable result string
    /// - Converged: Boolean indicating if HL-RF converged
    /// - BetaHistory: Reliability index at each iteration (for convergence plot)
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

        protected override System.Drawing.Bitmap Icon => null;

        // =====================================================================
        // INPUT PARAMETERS
        // =====================================================================

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "M", "Pre-solved FEM model (geometry, topology, supports)", GH_ParamAccess.item);
            pManager.AddParameter(new Param_RandomVariable(), "Variables", "RV", "Random variable definitions", GH_ParamAccess.list);
            pManager.AddTextParameter("ParamNames", "PN", "Parameter names matching model (e.g., 'E', 'F', 'I')", GH_ParamAccess.list);
            pManager.AddTextParameter("LSFType", "LSF", "Limit state function type ('Deflection' or 'Stress')", GH_ParamAccess.item, "Deflection");
            pManager.AddNumberParameter("Capacity", "Cap", "Allowable value (deflection limit or stress limit)", GH_ParamAccess.item);
            pManager.AddNumberParameter("Tolerance", "Tol", "Convergence tolerance (default 1e-6)", GH_ParamAccess.item, 1e-6);
            pManager.AddIntegerParameter("MaxIter", "It", "Maximum iterations (default 50)", GH_ParamAccess.item, 50);

            pManager[5].Optional = true;
            pManager[6].Optional = true;
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
            double tolerance = 1e-6;
            int maxIter = 50;

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

            // Read LSF type, capacity, tolerance, maxIter
            DA.GetData(3, ref lsfType);
            if (!DA.GetData(4, ref capacity))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Capacity (allowable value) required.");
                return;
            }

            DA.GetData(5, ref tolerance);
            DA.GetData(6, ref maxIter);

            // Validate LSF type
            if (lsfType != "Deflection" && lsfType != "Stress")
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"LSFType '{lsfType}' not recognized. Using 'Deflection'.");
                lsfType = "Deflection";
            }

            // Validate capacity
            if (capacity <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Capacity must be > 0.");
                return;
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
            try
            {
                result = solver.Solve(
                    variables,
                    limitStateFunction,
                    epsilon1: tolerance,
                    epsilon2: tolerance,
                    maxIterations: maxIter);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"FORM solver failed: {ex.Message}");
                return;
            }

            // ── Check convergence and warn if needed ─────────────────────────

            if (!result.Converged)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"FORM did not converge in {maxIter} iterations. Results may be unreliable.");
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
        }
    }
}
