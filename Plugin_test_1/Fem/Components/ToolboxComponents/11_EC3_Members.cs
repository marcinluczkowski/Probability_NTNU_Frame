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
    /// Eurocode check of every member of a linear-elastic truss model: design axial force from
    /// EN 1990 (6.10a) and (6.10b) with the Norwegian NA, resistance from EN 1993-1-1 (tension
    /// and flexural buckling about both axes).
    ///
    /// It uses the same member forces, buckling lengths and resistance formulas as FORM Members,
    /// so the utilisation and the reliability index of a member can be put side by side.
    ///
    /// The work is done in EurocodeMemberCheck.Run so that the section optimisation can call
    /// the same routine; this component only reads the inputs and lays out the outputs.
    ///
    /// Max Displacement (characteristic loads) and Max Displacement ULS (design combinations)
    /// come from ModelDisplacement (ST_LS_Displacement.cs).
    ///
    /// With random variables and bindings (as for FORM Members / RBDO Sections) it also gives the
    /// IMPLIED reliability of the model as it is, without any optimisation: FORM on every member
    /// limit state with those variables, the same routine as FORM Members (MemberReliability.Run).
    /// The output Model is a copy of the input with the utilisation (and the implied beta)
    /// attached, for Member Info and Deformed_Shape.
    /// </summary>
    public class ST_EC3_Members : GH_Component
    {
        public ST_EC3_Members()
          : base("EC3 Members", "EC3 Mem",
              "Eurocode check of every member of a linear-elastic truss model. Design forces from " +
              "EN 1990 (6.10a) and (6.10b), Norwegian NA. EN 1993-1-1: tension, compression and flexural " +
              "buckling about both axes, with the bending moments from the rigid joints (switch: Bending). " +
              "With random variables: the implied reliability of the model as it is.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Assembled model (does NOT need to be solved). The loads are the CHARACTERISTIC loads, " +
                "one load case per action.", GH_ParamAccess.item);
            pManager.AddTextParameter("Load Cases", "LC",
                "One line per load case of the model:\n" +
                "  <lc>:G            permanent action\n" +
                "  <lc>:Q[:<psi0>]   variable action; psi0 = 0.7 if not given\n" +
                "Example: \"0:G\" and \"1:Q:0.7\".",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Buckling Lengths", "Lcr",
                "Optional, one per line: \"<tag>:<Lcr,y>:<Lcr,z>[:<L,LT>]\" in metres (L,LT = length between lateral restraints for lateral-torsional buckling, default Lcr,z), for the members with this " +
                "element tag. Default: the element length about both axes. Use the same as in FORM Members.",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("Bending", "Bend",
                "true (default): members are checked for the axial force AND the bending moments from the rigid " +
                "joints (EN 1993-1-1 6.2.1(7), 6.3.3 with Annex B, lateral-torsional buckling; elastic, Cm >= 0.9, " +
                "as Karamba3D's defaults). false: axial force only.", GH_ParamAccess.item, true);
            pManager.AddGenericParameter("Random Variables", "RV",
                "Optional: random variables for the implied reliability of the model as it is (no optimisation), " +
                "in the same order as Bindings. Use the same ones as for FORM Members / RBDO Sections.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Bindings", "Bind",
                "Optional: one spec per random variable, as in FORM Members: LC:<lc>[:<ref>], FY[:<tag>], E[:<tag>], TR, TE.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Correlations", "Corr",
                "Optional, one per line: \"i,j,rho\" using 0-based variable indices.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Target Beta", "Bt",
                "Target reliability index; only used to count the members below it (Report) and for the beta " +
                "colours of Member Info.", GH_ParamAccess.item, 3.8);

            for (int i = 2; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Tags", "Tag", "Element tag of each member, in model order.", GH_ParamAccess.list);
            pManager.AddTextParameter("Mode", "Mode",
                "Governing check of each member: tension, bending (no axial force), compression, buckling y or buckling z. " +
                "\"no axial force\" for a member that no combination stresses (its Utilisation is NaN).", GH_ParamAccess.list);
            pManager.AddNumberParameter("NEd", "NEd",
                "Design axial force of the governing check [kN], tension positive.", GH_ParamAccess.list);
            pManager.AddNumberParameter("NRd", "NRd", "Design resistance of the governing check [kN].", GH_ParamAccess.list);
            pManager.AddNumberParameter("Utilisation", "U",
                "Utilisation of each member (governing check): NEd / NRd, or the N+M interaction value with Bending.", GH_ParamAccess.list);
            pManager.AddTextParameter("Combination", "Comb", "Governing load combination of each member.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Utilisation Modes", "Um",
                "One branch per member: utilisation for tension, buckling y, buckling z, compression (NaN where not " +
                "checked; compression only with Bending).",
                GH_ParamAccess.tree);
            pManager.AddNumberParameter("N Characteristic", "Nk",
                "One branch per member: axial force under each load case as modelled [kN], in the order of Load Cases.",
                GH_ParamAccess.tree);
            pManager.AddNumberParameter("Max Utilisation", "Umax", "Highest member utilisation.", GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "Rep", "All checks and warnings, and the implied reliability if it was computed.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Max Displacement", "Dmax",
                "Largest displacement [mm] under the characteristic loads (the loads as modelled, every load case x 1.0: " +
                "G + Q). Length of the translation vector, at the nodes and the middles of the members. The same value " +
                "as FORM Members / RBDO Sections give for the same model.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Max Displacement ULS", "DmaxU",
                "Largest displacement [mm] over the design combinations (6.10a) and (6.10b) that the check uses. " +
                "The governing combination is in the Report.", GH_ParamAccess.item);
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "A copy of the input model with the utilisation of every member attached, and the implied beta if it " +
                "was computed: for Member Info, and for Deformed_Shape (which solves it if needed).", GH_ParamAccess.item);
            pManager.AddNumberParameter("Weight", "W", "Weight of the model, with the sections it has [kg].", GH_ParamAccess.item);
            pManager.AddNumberParameter("Implied Beta", "B",
                "Implied reliability index of each member, governing limit state (NaN: no limit state, or no random variables).",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("Implied Min Beta", "Bmin", "Lowest implied member reliability index.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Implied System Beta", "Bsys",
                "Implied reliability index of the model as a series system of all member limit states (first member failure).",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            List<string> lcSpecs = new List<string>();
            List<string> lcrSpecs = new List<string>();
            bool bending = true;
            List<object> rvObjs = new List<object>();
            List<string> bindSpecs = new List<string>();
            List<string> corrSpecs = new List<string>();
            double betaTarget = 3.8;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            if (!DA.GetDataList(1, lcSpecs)) { return; }
            DA.GetDataList(2, lcrSpecs);
            DA.GetData(3, ref bending);
            DA.GetDataList(4, rvObjs);
            DA.GetDataList(5, bindSpecs);
            DA.GetDataList(6, corrSpecs);
            DA.GetData(7, ref betaTarget);
            MemberCheckOptions options = bending ? MemberCheckOptions.Default() : MemberCheckOptions.AxialOnly();

            if (gh_mdl == null || gh_mdl.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Valid model required.");
                return;
            }
            TB_Model mdl = gh_mdl.Value;

            // --- load cases, buckling lengths, random variables ---
            List<EcLoadCase> loadCases = new List<EcLoadCase>();
            var lcr = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            var vars = new List<RandomVariable>();
            var bindings = new List<MemberVariable>();
            double[,] rho = null;
            try
            {
                foreach (string s in lcSpecs)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    loadCases.Add(EcLoadCase.Parse(s));
                }
                ParseBucklingLengths(lcrSpecs, lcr);

                // random variables for the implied reliability (optional)
                foreach (object o in rvObjs)
                {
                    RandomVariable rv = o as RandomVariable;
                    if (rv == null && o is GH_RandomVariable goo) rv = goo.Value;
                    if (rv == null && o is Grasshopper.Kernel.Types.GH_ObjectWrapper w)
                        rv = w.Value as RandomVariable;
                    if (rv == null)
                        throw new ArgumentException("Input RV contains something that is not a RandomVariable.");
                    vars.Add(rv);
                }
                if (vars.Count != bindSpecs.Count)
                    throw new ArgumentException($"{vars.Count} random variables but {bindSpecs.Count} bindings. They must match 1:1.");
                for (int i = 0; i < vars.Count; i++)
                    bindings.Add(MemberVariable.Parse(bindSpecs[i], vars[i].Name ?? ("v" + i)));
                rho = ParseCorrelations(corrSpecs, vars.Count);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            // --- solve ---
            EcCheckResult res;
            try
            {
                res = EurocodeMemberCheck.Run(mdl, loadCases, lcr, null, options);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Eurocode member check failed: " + ex.Message);
                return;
            }

            // --- implied reliability of the model as it is (optional) ---
            MemberReliabilityResult rel = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (vars.Count > 0)
            {
                try
                {
                    rel = MemberReliability.Run(mdl, vars, bindings, rho, lcr, 300, false,
                                                SystemReliability.DefaultSamples, options);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The implied reliability could not be computed: " + ex.Message);
                }
            }
            sw.Stop();

            // --- lay out the results, one entry / branch per member ---
            int nMem = res.Members.Count;

            var tags = new List<string>();
            var modes = new List<string>();
            var nEd = new List<double>();
            var nRd = new List<double>();
            var util = new List<double>();
            var comb = new List<string>();
            DataTree<double> utilModes = new DataTree<double>();
            DataTree<double> nChar = new DataTree<double>();

            int over = 0;

            for (int m = 0; m < nMem; m++)
            {
                GH_Path path = new GH_Path(m);
                EcMemberCheck g = res.Governing[m];

                tags.Add(res.Members[m].Tag);

                if (g == null)
                {
                    modes.Add("no axial force");
                    nEd.Add(0.0);
                    nRd.Add(double.NaN);
                    util.Add(double.NaN);
                    comb.Add("-");
                }
                else
                {
                    modes.Add(MemberLimitStateResult.ModeName(g.Mode, g.NEd));
                    nEd.Add(g.NEd * 1e-3);
                    nRd.Add(g.NRd * 1e-3);
                    util.Add(g.Utilisation);
                    comb.Add(g.Combination);
                    if (g.Utilisation > 1.0) over++;
                }

                // utilisation of each mode: tension, buckling y, buckling z, compression
                double[] um = { double.NaN, double.NaN, double.NaN, double.NaN };
                foreach (EcMemberCheck c in res.Checks)
                {
                    if (c.Member != m) continue;
                    um[(int)c.Mode] = c.Utilisation;
                }
                for (int i = 0; i < um.Length; i++) utilModes.Add(um[i], path);

                for (int i = 0; i < res.NChar[m].Length; i++) nChar.Add(res.NChar[m][i] * 1e-3, path);
            }

            // implied beta of each member (governing limit state)
            var beta = new List<double>();
            int below = 0, notConverged = 0;
            for (int m = 0; m < nMem; m++)
            {
                MemberLimitStateResult gr = (rel != null && m < rel.Governing.Length) ? rel.Governing[m] : null;
                bool has = gr != null && gr.HasBeta;
                beta.Add(has ? gr.Form.Beta : double.NaN);
                if (has && gr.Form.Beta < betaTarget) below++;
                if (has && !gr.Converged) notConverged++;
            }
            double betaSys = (rel != null && rel.SystemResult != null) ? rel.SystemResult.BetaSystem : double.NaN;

            // --- messages ---
            if (over > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{over} member(s) have a utilisation above 1. See the Report.");
            foreach (string wmsg in res.Warnings)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, wmsg);
            if (rel != null)
            {
                if (notConverged > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"FORM did not converge for {notConverged} member(s). See the Report.");
                foreach (string wmsg in rel.Warnings)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Implied reliability: " + wmsg);
                if (rel.SystemResult != null)
                    foreach (string wmsg in rel.SystemResult.Warnings)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "System: " + wmsg);
            }

            // --- largest displacement: characteristic loads and design combinations ---
            DisplacementResult dChar = null, dUls = null;
            try
            {
                dChar = ModelDisplacement.Characteristic(mdl);
                dUls = ModelDisplacement.Design(mdl, loadCases, res.Factors);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The displacements could not be computed: " + ex.Message);
            }

            // --- report ---
            CultureInfo ci = CultureInfo.InvariantCulture;
            string report = res.Report() + "\n" + ModelDisplacement.Report(mdl, dChar, dUls) +
                            "\nWEIGHT: " + mdl.Weight.ToString("F1", ci) + " kg\n";
            if (rel != null)
            {
                report += "\nIMPLIED RELIABILITY (FORM on every member limit state of the model as it is; the same routine as FORM Members)\n" +
                          "  lowest member beta " + rel.MinBeta.ToString("F3", ci) +
                          ", series system beta " + betaSys.ToString("F3", ci) +
                          "; " + below.ToString(ci) + " of " + nMem.ToString(ci) + " members below the target " +
                          betaTarget.ToString("F2", ci) + "\n\n" +
                          rel.Report() +
                          "\nelapsed (implied reliability): " + sw.Elapsed.TotalSeconds.ToString("F2", ci) + " s";
            }

            Message = (double.IsNaN(res.MaxUtilisation) ? "" : "U max = " + res.MaxUtilisation.ToString("F2", ci)) +
                      ((rel == null || double.IsNaN(rel.MinBeta)) ? "" : ", \u03B2 min = " + rel.MinBeta.ToString("F2", ci));

            // --- output model: a copy with the utilisation (and implied beta) attached ---
            TB_Model outMdl = Common.DeepCopy(mdl);
            TB_MemberResults.SetUtilisation(outMdl, res, "EC3 Members");
            if (rel != null) TB_MemberResults.SetBeta(outMdl, rel, "EC3 Members, implied", betaTarget);

            // --- output ---
            DA.SetDataList(0, tags);
            DA.SetDataList(1, modes);
            DA.SetDataList(2, nEd);
            DA.SetDataList(3, nRd);
            DA.SetDataList(4, util);
            DA.SetDataList(5, comb);
            DA.SetDataTree(6, utilModes);
            DA.SetDataTree(7, nChar);
            DA.SetData(8, res.MaxUtilisation);
            DA.SetData(9, report);
            DA.SetData(10, dChar != null ? dChar.MaxMm : double.NaN);
            DA.SetData(11, dUls != null ? dUls.MaxMm : double.NaN);
            DA.SetData(12, new GH_TB_Model(outMdl));
            DA.SetData(13, mdl.Weight);
            DA.SetDataList(14, beta);
            DA.SetData(15, rel != null ? rel.MinBeta : double.NaN);
            DA.SetData(16, betaSys);
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

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("EC3");

        public override Guid ComponentGuid => new Guid("c13e955b-e64d-4443-a93f-5ffc3a588561");
    }
}
