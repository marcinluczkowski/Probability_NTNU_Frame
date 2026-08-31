using System;
using System.Collections.Generic;
using System.Linq;

using Grasshopper.Kernel;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Propability_NTNU_v1.Classes.Reliability;

using Plugin_test_1.Reliability.FORM;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    /// <summary>
    /// FORM reliability analysis on a geometrically nonlinear FE model.
    /// Uses the Nataf transformation (correlated variables), improved HL-RF with a
    /// merit-function line search, and DDM analytical gradients.
    /// </summary>
    public class ST_FORM_Nonlinear : GH_Component
    {
        public ST_FORM_Nonlinear()
          : base("FORM Nonlinear", "FORM NL",
              "FORM reliability analysis on a geometrically nonlinear model. " +
              "Limit state: g = threshold - u_response.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Assembled model (does NOT need to be solved).", GH_ParamAccess.item);
            pManager.AddGenericParameter("Random Variables", "RV",
                "Random variables, in the same order as Bindings.", GH_ParamAccess.list);
            pManager.AddTextParameter("Bindings", "Bind",
                "One spec per variable:\n" +
                "  E:<sectionTag>          modulus of a section group\n" +
                "  X:<nodeId>:<comp>       original coordinate (0=X 1=Y 2=Z)\n" +
                "  F:<nodeId>:<dof>[:<s>]  point load component, optional sign s",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Correlations", "Corr",
                "Optional, one per line: \"i,j,rho\" using 0-based variable indices.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter("Response Node", "rNode",
                "Node Id of the response DOF.", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Response DOF", "rDof",
                "0=ux 1=uy 2=uz 3=rx 4=ry 5=rz", GH_ParamAccess.item, 0);
            pManager.AddNumberParameter("Threshold", "u0",
                "Failure threshold. g = u0 - u_response. Model units (m).",
                GH_ParamAccess.item, 0.762);

            pManager.AddBooleanParameter("Use DDM", "DDM",
                "Analytical gradients. False falls back to finite differences (slow).",
                GH_ParamAccess.item, true);
            pManager.AddIntegerParameter("Load Steps", "Steps",
                "Load increments per nonlinear solve.", GH_ParamAccess.item, 10);
            pManager.AddIntegerParameter("Max Iterations", "MaxIt",
                "Maximum HL-RF iterations.", GH_ParamAccess.item, 60);
            pManager.AddBooleanParameter("Run", "Run",
                "Set true to run. FORM is expensive; leave false while wiring up.",
                GH_ParamAccess.item, false);

            for (int i = 3; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Beta", "B", "Reliability index.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Pf", "Pf", "Probability of failure.", GH_ParamAccess.item);
            pManager.AddTextParameter("Names", "Name", "Variable names.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Design Point", "X*",
                "Design point in physical space.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Alpha", "a",
                "Direction cosines (importance factors).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Mean Sensitivity", "dB",
                "sigma_i * dBeta/dMu_i at the design point.", GH_ParamAccess.list);
            pManager.AddTextParameter("Report", "Rep",
                "Convergence, evaluation counts and binding self-check.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            List<object> rvObjs = new List<object>();
            List<string> bindSpecs = new List<string>();
            List<string> corrSpecs = new List<string>();
            int rNode = 0, rDof = 0, steps = 10, maxIt = 60;
            double u0 = 0.762;
            bool useDdm = true, run = false;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            if (!DA.GetDataList(1, rvObjs)) { return; }
            if (!DA.GetDataList(2, bindSpecs)) { return; }
            DA.GetDataList(3, corrSpecs);
            DA.GetData(4, ref rNode);
            DA.GetData(5, ref rDof);
            DA.GetData(6, ref u0);
            DA.GetData(7, ref useDdm);
            DA.GetData(8, ref steps);
            DA.GetData(9, ref maxIt);
            DA.GetData(10, ref run);

            // --- unwrap random variables ---
            List<RandomVariable> vars = new List<RandomVariable>();
            foreach (object o in rvObjs)
            {
                RandomVariable rv = o as RandomVariable;
                if (rv == null && o is GH_RandomVariable goo) rv = goo.Value;
                if (rv == null && o is Grasshopper.Kernel.Types.GH_ObjectWrapper w)
                    rv = w.Value as RandomVariable;
                if (rv == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Input RV contains something that is not a RandomVariable.");
                    return;
                }
                vars.Add(rv);
            }

            if (vars.Count != bindSpecs.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"{vars.Count} random variables but {bindSpecs.Count} bindings. They must match 1:1.");
                return;
            }

            // --- build parameters ---
            List<NL_Parameter> pars = new List<NL_Parameter>();
            try
            {
                for (int i = 0; i < vars.Count; i++)
                    pars.Add(NL_LimitState.ParseBinding(bindSpecs[i], vars[i].Name ?? $"v{i}"));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            // --- correlation matrix ---
            double[,] rho = null;
            if (corrSpecs != null && corrSpecs.Count > 0)
            {
                rho = new double[vars.Count, vars.Count];
                foreach (string s in corrSpecs)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    string[] t = s.Split(',');
                    if (t.Length < 3)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                            $"Correlation '{s}': expected \"i,j,rho\".");
                        return;
                    }
                    int i = int.Parse(t[0]); int j = int.Parse(t[1]);
                    double r = double.Parse(t[2], System.Globalization.CultureInfo.InvariantCulture);
                    rho[i, j] = rho[j, i] = r;
                }
            }

            // --- build limit state ---
            TB_Model mdl = gh_mdl.Value;
            NL_LimitState ls;
            try
            {
                ls = new NL_LimitState(mdl, pars, new NL_Response(rNode, rDof), u0, steps, 50, 1e-9);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            string selfCheck = ls.SelfCheck();
            if (selfCheck.StartsWith("BINDING PROBLEMS"))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, selfCheck);

            // --- mean-point consistency check -----------------------------------
            var meanWarn = new List<string>();
            for (int i = 0; i < vars.Count; i++)
            {
                double mb = ls.MeanPoint[i];
                if (Math.Abs(mb - vars[i].Mean) > 1e-3 * Math.Max(1.0, Math.Abs(vars[i].Mean)))
                    meanWarn.Add($"  '{vars[i].Name}': model has {mb:G6}, variable mean is {vars[i].Mean:G6}");
            }
            if (meanWarn.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Model values differ from variable means:\n" + string.Join("\n", meanWarn.Take(8)));

            if (!run)
            {
                DA.SetData(6, selfCheck + "\n\nSet Run = true to perform the analysis." +
                    (meanWarn.Count > 0 ? "\n\nMean mismatches:\n" + string.Join("\n", meanWarn.Take(8)) : ""));
                return;
            }

            // --- solve ---
            var solver = new FORMSolverNataf();
            FORMResult res;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                res = solver.Solve(
                    vars,
                    x => ls.G(x),
                    correlation: rho,
                    gradientX: useDdm ? (Func<double[], double[]>)(x => ls.GradX(x)) : null,
                    epsilon1: 1e-6, epsilon2: 1e-6, maxIterations: maxIt);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "FORM failed: " + ex.Message);
                return;
            }
            sw.Stop();

            if (!res.Converged)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, res.ConvergenceMessage);

            string report =
                $"{res.ConvergenceMessage}\n" +
                $"beta = {res.Beta:F5}   Pf = {res.ProbabilityOfFailure:E4}\n" +
                $"FE solves: {ls.Evaluations}  (failed: {ls.FailedSolves})\n" +
                $"gradient : {(useDdm ? "DDM (analytical)" : "finite differences")}\n" +
                $"elapsed  : {sw.Elapsed.TotalSeconds:F1} s\n" +
                (solver.Nataf.Warnings.Count > 0
                    ? "Nataf warnings:\n  " + string.Join("\n  ", solver.Nataf.Warnings) + "\n" : "") +
                selfCheck;

            // --- output ---
            DA.SetData(0, res.Beta);
            DA.SetData(1, res.ProbabilityOfFailure);
            DA.SetDataList(2, vars.Select(v => v.Name).ToList());
            DA.SetDataList(3, res.MPP_X);
            DA.SetDataList(4, res.AlphaFactors);
            DA.SetDataList(5, solver.ScaledMeanSensitivity);
            DA.SetData(6, report);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("\u03B2");

        public override Guid ComponentGuid => new Guid("c41e7a58-2d6b-4f19-93a0-8b5e1c47d2f6");
    }
}
