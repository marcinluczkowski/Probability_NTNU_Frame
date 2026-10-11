using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Propability_NTNU_v1.Classes.Reliability;

using Plugin_test_1.Reliability.FORM;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    /// <summary>
    /// FORM reliability analysis of every member of a linear-elastic truss model.
    ///
    /// Limit states per member: tension yield, flexural buckling about y and about z
    /// (EN 1993-1-1 buckling curves evaluated with the random fy and E). All limit states
    /// share one random vector. The gradients are analytical (DDM on the linear FE model).
    ///
    /// FORM is run on every limit state and the beta it finds is reported, however large.
    ///
    /// The structure is then assessed as a series system of all limit states (first member
    /// failure): Ditlevsen bounds and an importance sampling estimate of the system beta.
    ///
    /// The work is done in MemberReliability.Run so that the section optimisation can call
    /// the same routine; this component only reads the inputs and lays out the outputs.
    /// </summary>
    public class ST_FORM_Members : GH_Component
    {
        public ST_FORM_Members()
          : base("FORM Members", "FORM Mem",
              "FORM reliability analysis of every member of a linear-elastic truss model. " +
              "Limit states: tension, compression and flexural buckling about both axes, with the bending " +
              "moments from the rigid joints (switch: Bending). Gradients by DDM.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Assembled model (does NOT need to be solved). Put each load that has its own random " +
                "variable in its own load case.", GH_ParamAccess.item);
            pManager.AddGenericParameter("Random Variables", "RV",
                "Random variables, in the same order as Bindings.", GH_ParamAccess.list);
            pManager.AddTextParameter("Bindings", "Bind",
                "One spec per variable:\n" +
                "  LC:<lc>[:<ref>]   scales the loads of load case lc. With ref, the modelled loads\n" +
                "                    correspond to the value ref: loads = (x / ref) * modelled loads\n" +
                "  FY[:<tag>]        yield strength of all members / of the members with this element tag\n" +
                "  E[:<tag>]         Young's modulus of all members / of one group\n" +
                "  TR                resistance model uncertainty (multiplies the resistance)\n" +
                "  TE                load-effect model uncertainty (multiplies the member force)",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Correlations", "Corr",
                "Optional, one per line: \"i,j,rho\" using 0-based variable indices.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Buckling Lengths", "Lcr",
                "Optional, one per line: \"<tag>:<Lcr,y>:<Lcr,z>[:<L,LT>]\" in metres (L,LT = length between lateral restraints for lateral-torsional buckling, default Lcr,z), for the members with this " +
                "element tag. Default: the element length about both axes.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("Check DDM", "Chk",
                "Compare the DDM gradients with central finite differences at the mean point and " +
                "report the largest difference.", GH_ParamAccess.item, false);
            pManager.AddIntegerParameter("Max Iterations", "MaxIt",
                "Maximum HL-RF iterations per limit state. Limit states far from failure need many.", GH_ParamAccess.item, 300);
            pManager.AddIntegerParameter("System Samples", "Nsys",
                "Number of samples of the importance sampling estimate of the system failure probability. " +
                "0 = Ditlevsen bounds only.", GH_ParamAccess.item, SystemReliability.DefaultSamples);
            pManager.AddBooleanParameter("Bending", "Bend",
                "true (default): members are checked for the axial force AND the bending moments from the rigid " +
                "joints (EN 1993-1-1 6.2.1(7), 6.3.3 with Annex B, lateral-torsional buckling; elastic, Cm >= 0.9, " +
                "as Karamba3D's defaults). false: axial force only.", GH_ParamAccess.item, true);

            for (int i = 3; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Tags", "Tag", "Element tag of each member, in model order.", GH_ParamAccess.list);
            pManager.AddTextParameter("Mode", "Mode",
                "Governing limit state of each member: tension, compression, buckling y or buckling z. " +
                "\"no axial force\" for a member that no load case stresses (its Beta is NaN).", GH_ParamAccess.list);
            pManager.AddNumberParameter("N", "N",
                "Axial force of each member at the mean point [kN], tension positive.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Beta", "B", "Reliability index of each member (governing limit state).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Pf", "Pf", "Probability of failure of each member.", GH_ParamAccess.list);
            pManager.AddBooleanParameter("Converged", "Conv", "Did FORM converge for the governing limit state?", GH_ParamAccess.list);
            pManager.AddNumberParameter("Alpha", "a",
                "Direction cosines of the governing limit state, one branch per member.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Design Point", "X*",
                "Design point in physical space, one branch per member.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Beta Modes", "Bm",
                "One branch per member: beta for tension, buckling y, buckling z, compression (NaN where not assessed; " +
                "compression only with Bending).",
                GH_ParamAccess.tree);
            pManager.AddTextParameter("Names", "Name", "Variable names, in the order of Alpha and Design Point.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Min Beta", "Bmin", "Lowest member reliability index.", GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "Rep", "All assessed limit states, checks and warnings.", GH_ParamAccess.item);
            pManager.AddNumberParameter("System Beta", "Bsys",
                "Reliability index of the structure as a series system of all member limit states " +
                "(first member failure): the sampling estimate, kept inside the Ditlevsen bounds.", GH_ParamAccess.item);
            pManager.AddNumberParameter("System Beta Bounds", "Bsys+-",
                "Ditlevsen bounds on the system reliability index: lower, upper.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            List<object> rvObjs = new List<object>();
            List<string> bindSpecs = new List<string>();
            List<string> corrSpecs = new List<string>();
            List<string> lcrSpecs = new List<string>();
            bool checkDdm = false;
            int maxIt = 300;
            int sysSamples = SystemReliability.DefaultSamples;
            bool bending = true;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            if (!DA.GetDataList(1, rvObjs)) { return; }
            if (!DA.GetDataList(2, bindSpecs)) { return; }
            DA.GetDataList(3, corrSpecs);
            DA.GetDataList(4, lcrSpecs);
            DA.GetData(5, ref checkDdm);
            DA.GetData(6, ref maxIt);
            DA.GetData(7, ref sysSamples);
            DA.GetData(8, ref bending);
            MemberCheckOptions options = bending ? MemberCheckOptions.Default() : MemberCheckOptions.AxialOnly();

            if (gh_mdl == null || gh_mdl.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Valid model required.");
                return;
            }
            TB_Model mdl = gh_mdl.Value;

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

            // --- bindings, correlations, buckling lengths ---
            List<MemberVariable> bindings = new List<MemberVariable>();
            double[,] rho = null;
            var lcr = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; i < vars.Count; i++)
                    bindings.Add(MemberVariable.Parse(bindSpecs[i], vars[i].Name ?? ("v" + i)));

                rho = ParseCorrelations(corrSpecs, vars.Count);
                ParseBucklingLengths(lcrSpecs, lcr);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            // --- solve ---
            MemberReliabilityResult res;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                res = MemberReliability.Run(mdl, vars, bindings, rho, lcr, Math.Max(1, maxIt), checkDdm,
                                            Math.Max(0, sysSamples), options);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Member reliability analysis failed: " + ex.Message);
                return;
            }
            sw.Stop();

            // --- lay out the results, one entry / branch per member ---
            int nMem = res.Members.Count;
            int nVar = vars.Count;

            var tags = new List<string>();
            var modes = new List<string>();
            var nKn = new List<double>();
            var beta = new List<double>();
            var pf = new List<double>();
            var conv = new List<bool>();
            DataTree<double> alpha = new DataTree<double>();
            DataTree<double> design = new DataTree<double>();
            DataTree<double> betaModes = new DataTree<double>();

            int notConverged = 0;

            for (int m = 0; m < nMem; m++)
            {
                GH_Path path = new GH_Path(m);
                MemberLimitStateResult g = res.Governing[m];

                tags.Add(res.Members[m].Tag);
                nKn.Add(res.NMean[m] * 1e-3);

                if (g == null)
                {
                    modes.Add("no axial force");
                    beta.Add(double.NaN);
                    pf.Add(double.NaN);
                    conv.Add(false);
                    for (int i = 0; i < nVar; i++)
                    {
                        alpha.Add(double.NaN, path);
                        design.Add(double.NaN, path);
                    }
                }
                else
                {
                    modes.Add(MemberLimitStateResult.ModeName(g.Mode));
                    beta.Add(g.Form.Beta);
                    pf.Add(g.Form.ProbabilityOfFailure);
                    conv.Add(g.Converged);
                    if (!g.Converged) notConverged++;
                    for (int i = 0; i < nVar; i++)
                    {
                        alpha.Add(g.Form.AlphaFactors[i], path);
                        design.Add(g.Form.MPP_X[i], path);
                    }
                }

                // beta of each mode: tension, buckling y, buckling z, compression
                double[] bm = { double.NaN, double.NaN, double.NaN, double.NaN };
                foreach (MemberLimitStateResult r in res.LimitStates)
                {
                    if (r.Member != m || !r.HasBeta) continue;
                    bm[(int)r.Mode] = r.Form.Beta;
                }
                for (int i = 0; i < bm.Length; i++) betaModes.Add(bm[i], path);
            }

            // --- messages ---
            if (notConverged > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"FORM did not converge for {notConverged} member(s). See the Report.");
            if (res.CriticalBendingRatio > 0.10)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "In the critical member the bending stress from the rigid joints is " +
                    (100.0 * res.CriticalBendingRatio).ToString("F0", CultureInfo.InvariantCulture) +
                    " % of the axial stress. The limit states use the axial force only.");
            foreach (string wmsg in res.Warnings)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, wmsg);

            string report = res.Report() +
                "\nelapsed: " + sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s";

            SystemReliabilityResult sys = res.SystemResult;
            double betaSys = (sys != null) ? sys.BetaSystem : double.NaN;
            var betaSysBounds = new List<double>
            {
                (sys != null) ? sys.BetaLower : double.NaN,
                (sys != null) ? sys.BetaUpper : double.NaN
            };
            if (sys != null)
                foreach (string wmsg in sys.Warnings)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "System: " + wmsg);

            Message = double.IsNaN(res.MinBeta)
                ? ""
                : "\u03B2 min = " + res.MinBeta.ToString("F2", CultureInfo.InvariantCulture) +
                  (double.IsNaN(betaSys) ? "" : ", \u03B2 sys = " + betaSys.ToString("F2", CultureInfo.InvariantCulture));

            // --- output ---
            DA.SetDataList(0, tags);
            DA.SetDataList(1, modes);
            DA.SetDataList(2, nKn);
            DA.SetDataList(3, beta);
            DA.SetDataList(4, pf);
            DA.SetDataList(5, conv);
            DA.SetDataTree(6, alpha);
            DA.SetDataTree(7, design);
            DA.SetDataTree(8, betaModes);
            DA.SetDataList(9, res.VariableNames);
            DA.SetData(10, res.MinBeta);
            DA.SetData(11, report);
            DA.SetData(12, betaSys);
            DA.SetDataList(13, betaSysBounds);
        }

        /// <summary>"i,j,rho" lines -> symmetric matrix, or null if there are none.</summary>
        private static double[,] ParseCorrelations(List<string> specs, int n)
        {
            if (specs == null) return null;

            double[,] rho = null;
            foreach (string s in specs)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;

                string[] t = s.Split(',');
                int i, j;
                double r;
                if (t.Length < 3 ||
                    !int.TryParse(t[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ||
                    !int.TryParse(t[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out j) ||
                    !double.TryParse(t[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                    throw new ArgumentException($"Correlation '{s}': expected \"i,j,rho\".");
                if (i < 0 || j < 0 || i >= n || j >= n || i == j)
                    throw new ArgumentException($"Correlation '{s}': indices must be two different variables in 0..{n - 1}.");
                if (Math.Abs(r) >= 1.0)
                    throw new ArgumentException($"Correlation '{s}': rho must be between -1 and 1.");

                if (rho == null) rho = new double[n, n];
                rho[i, j] = r;
                rho[j, i] = r;
            }
            return rho;
        }

        /// <summary>"tag:LcrY:LcrZ" lines [m] -> dictionary.</summary>
        private static void ParseBucklingLengths(List<string> specs, Dictionary<string, double[]> target)
        {
            if (specs == null) return;

            foreach (string s in specs)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;

                string[] t = s.Split(':');
                double ly, lz, llt = 0.0;
                if (t.Length < 3 || t.Length > 4 ||
                    !double.TryParse(t[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ly) ||
                    !double.TryParse(t[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lz) ||
                    (t.Length == 4 && !double.TryParse(t[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out llt)))
                    throw new ArgumentException($"Buckling length '{s}': expected \"<tag>:<Lcr,y>:<Lcr,z>[:<L,LT>]\" in metres.");

                target[t[0].Trim()] = (t.Length == 4) ? new[] { ly, lz, llt } : new[] { ly, lz };
            }
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("\u03B2m");

        public override Guid ComponentGuid => new Guid("c6fa9a4e-fe96-449e-875d-69991c3f461e");
    }
}
