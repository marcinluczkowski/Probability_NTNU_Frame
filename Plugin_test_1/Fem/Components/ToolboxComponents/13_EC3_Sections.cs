using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Grasshopper.Kernel;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Propability_NTNU_v1.Classes.Reliability;

using Plugin_test_1.Reliability.FORM;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    /// <summary>
    /// Eurocode design of the sections of a linear-elastic truss: the lightest sections from a
    /// catalogue for which the utilisation of EVERY member stays within the limit. It is the
    /// deterministic counterpart of RBDO Sections and uses the same search (SectionOptimiser.Run),
    /// the same groups and the same candidates, so the two designs can be compared directly.
    ///
    /// Every trial design is checked with the same routine as the EC3 Members component.
    ///
    /// With random variables and bindings (as for RBDO Sections) it also gives the IMPLIED
    /// reliability of the input model and of the Eurocode design: FORM on every member limit
    /// state with those variables, the same routine as FORM Members.
    /// </summary>
    public class ST_EC3_Sections : GH_Component
    {
        public ST_EC3_Sections()
          : base("EC3 Sections", "EC3 Opt",
              "Eurocode design: the lightest catalogue sections for which the utilisation of every member " +
              "stays within the limit. EN 1990 (6.10a) and (6.10b), Norwegian NA; EN 1993-1-1 tension and " +
              "flexural buckling. Same search, groups and candidates as RBDO Sections.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Assembled model with the CHARACTERISTIC loads, one load case per action. Its sections are replaced.",
                GH_ParamAccess.item);
            pManager.AddTextParameter("Load Cases", "LC",
                "One line per load case of the model: \"<lc>:G\" (permanent) or \"<lc>:Q[:<psi0>]\" (variable, " +
                "psi0 = 0.7 if not given).", GH_ParamAccess.list);
            pManager.AddTextParameter("Groups", "Grp",
                "Optional, one per line: \"<tag>:<group>\". All members of a group get the same section. " +
                "<tag> is an element tag and may end in * (\"Brace*:braces\"). The first line that matches " +
                "decides. Members that no line matches are grouped by their own element tag.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Section Table", "Table",
                "Optional: a cross-section table in the Karamba3D text format. Default: the built-in HEA list.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Candidates", "Cand",
                "Optional: names of the sections of the table that may be used. Default: all I-shaped sections.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Buckling Lengths", "Lcr",
                "Optional, one per line: \"<tag>:<Lcr,y>:<Lcr,z>[:<L,LT>]\" in metres (L,LT = length between lateral restraints for lateral-torsional buckling, default Lcr,z). Default: the element length.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("Max Utilisation", "Umax",
                "Utilisation that no member may exceed.", GH_ParamAccess.item, 1.0);
            pManager.AddIntegerParameter("Exchange Depth", "Exch",
                "After the stepping search, this many groups at a time are made one size heavier to see whether " +
                "the others can then become lighter. 0 = off, 1 = one group at a time, 2 = also every pair of " +
                "groups (finds more, but slow with many groups).", GH_ParamAccess.item, SectionOptimiser.DefaultExchangeDepth);
            pManager.AddGenericParameter("Random Variables", "RV",
                "Optional: random variables for the implied reliability of the input model and of the Eurocode " +
                "design, in the same order as Bindings. Use the same ones as for RBDO Sections.", GH_ParamAccess.list);
            pManager.AddTextParameter("Bindings", "Bind",
                "Optional: one spec per random variable, as in FORM Members: LC:<lc>[:<ref>], FY[:<tag>], E[:<tag>], TR, TE.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Correlations", "Corr",
                "Optional, one per line: \"i,j,rho\" using 0-based variable indices.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Target Beta", "Bt",
                "Target reliability index; only used to count the members below it in the Report.", GH_ParamAccess.item, 3.8);
            pManager.AddBooleanParameter("Bending", "Bend",
                "true (default): members are checked for the axial force AND the bending moments from the rigid " +
                "joints (EN 1993-1-1 6.2.1(7), 6.3.3 with Annex B, lateral-torsional buckling; elastic, Cm >= 0.9, " +
                "as Karamba3D's defaults). false: axial force only.", GH_ParamAccess.item, true);

            for (int i = 2; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "The model with the optimised sections.", GH_ParamAccess.item);
            pManager.AddTextParameter("Groups", "Grp", "Name of each group.", GH_ParamAccess.list);
            pManager.AddTextParameter("Sections", "Sec", "Section of each group.", GH_ParamAccess.list);
            pManager.AddTextParameter("Member Sections", "MSec", "Section of each member, in model order.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Weight", "W", "Weight of the optimised structure [kg].", GH_ParamAccess.item);
            pManager.AddNumberParameter("Utilisation", "U",
                "Utilisation of each member in the optimised structure (NaN: no axial force).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Max Utilisation", "Umax", "Highest member utilisation.", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Feasible", "OK", "True if every member is within the limit.", GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "Rep", "The search, the result per group, and before and after.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Initial Weight", "W0",
                "Weight of the input model, with the sections it came with [kg].", GH_ParamAccess.item);
            pManager.AddNumberParameter("Implied Beta", "B",
                "Implied reliability index of each member of the Eurocode design (NaN: no axial force, or no random variables).",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("Initial Implied Beta", "B0",
                "Implied reliability index of each member of the input model.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Implied Min Beta", "Bmin", "Lowest implied member reliability index of the Eurocode design.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Initial Implied Min Beta", "Bmin0", "Lowest implied member reliability index of the input model.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Implied System Beta", "Bsys",
                "Implied reliability index of the Eurocode design as a series system.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Initial Implied System Beta", "Bsys0",
                "Implied reliability index of the input model as a series system.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            List<string> lcSpecs = new List<string>();
            List<string> groupSpecs = new List<string>();
            List<string> tableLines = new List<string>();
            List<string> candidateNames = new List<string>();
            List<string> lcrSpecs = new List<string>();
            double maxUtil = 1.0;
            int exchangeDepth = SectionOptimiser.DefaultExchangeDepth;
            List<object> rvObjs = new List<object>();
            List<string> bindSpecs = new List<string>();
            List<string> corrSpecs = new List<string>();
            double betaTarget = 3.8;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            if (!DA.GetDataList(1, lcSpecs)) { return; }
            DA.GetDataList(2, groupSpecs);
            DA.GetDataList(3, tableLines);
            DA.GetDataList(4, candidateNames);
            DA.GetDataList(5, lcrSpecs);
            DA.GetData(6, ref maxUtil);
            DA.GetData(7, ref exchangeDepth);
            DA.GetDataList(8, rvObjs);
            DA.GetDataList(9, bindSpecs);
            DA.GetDataList(10, corrSpecs);
            DA.GetData(11, ref betaTarget);
            bool bending = true;
            DA.GetData(12, ref bending);
            MemberCheckOptions options = bending ? MemberCheckOptions.Default() : MemberCheckOptions.AxialOnly();

            if (gh_mdl == null || gh_mdl.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Valid model required.");
                return;
            }
            TB_Model mdl = gh_mdl.Value;

            // --- load cases, buckling lengths, groups, candidates ---
            List<EcLoadCase> loadCases = new List<EcLoadCase>();
            var lcr = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            string[] groups;
            List<SectionRecord> candidates;
            var inputWarnings = new List<string>();
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
                groups = SectionOptimiser.GroupOfMembers(mdl, groupSpecs);

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
                candidates = SectionOptimiser.Candidates(tableLines, candidateNames, inputWarnings);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            // --- solve ---
            SectionOptimisationResult res;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                res = SectionOptimiser.RunEurocode(mdl, candidates, groups, loadCases, lcr, maxUtil, null,
                                                   Math.Max(0, exchangeDepth), SectionOptimiser.DefaultMaxEvaluations,
                                                   options);
                if (vars.Count > 0)
                    SectionOptimiser.AddImpliedReliability(res, mdl, vars, bindings, betaTarget, rho, lcr,
                                                           300, SystemReliability.DefaultSamples, options);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Section optimisation failed: " + ex.Message);
                return;
            }
            sw.Stop();

            // --- messages ---
            EcCheckResult ec = res.Final.Eurocode;
            if (!res.Feasible)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Not every member is within the utilisation limit. See the Report.");
            foreach (string wmsg in inputWarnings.Concat(res.Warnings))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, wmsg);

            string report = res.Report() +
                "\nelapsed: " + sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s";

            Message = res.Weight.ToString("F0", CultureInfo.InvariantCulture) + " kg" +
                      (double.IsNaN(ec.MaxUtilisation) ? "" : ", U max = " + ec.MaxUtilisation.ToString("F2", CultureInfo.InvariantCulture)) +
                      ((res.ReliabilityOptimised == null || double.IsNaN(res.ReliabilityOptimised.MinBeta)) ? ""
                          : ", \u03B2 min = " + res.ReliabilityOptimised.MinBeta.ToString("F2", CultureInfo.InvariantCulture));

            // --- output ---
            DA.SetData(0, new GH_TB_Model(res.Model));
            DA.SetDataList(1, res.Groups);
            DA.SetDataList(2, res.GroupSections);
            DA.SetDataList(3, res.MemberSections);
            DA.SetData(4, res.Weight);
            DA.SetDataList(5, res.Final.Value);
            DA.SetData(6, ec.MaxUtilisation);
            DA.SetData(7, res.Feasible);
            DA.SetData(8, report);

            MemberReliabilityResult before = res.ReliabilityInitial, after = res.ReliabilityOptimised;
            DA.SetData(9, res.WeightInitial);
            DA.SetDataList(10, GoverningBetas(after));
            DA.SetDataList(11, GoverningBetas(before));
            DA.SetData(12, after != null ? after.MinBeta : double.NaN);
            DA.SetData(13, before != null ? before.MinBeta : double.NaN);
            DA.SetData(14, (after != null && after.SystemResult != null) ? after.SystemResult.BetaSystem : double.NaN);
            DA.SetData(15, (before != null && before.SystemResult != null) ? before.SystemResult.BetaSystem : double.NaN);
        }

        /// <summary>Beta of the governing limit state of each member; NaN where there is none.</summary>
        private static List<double> GoverningBetas(MemberReliabilityResult r)
        {
            var b = new List<double>();
            if (r == null) return b;
            foreach (MemberLimitStateResult g in r.Governing)
                b.Add((g != null && g.HasBeta) ? g.Form.Beta : double.NaN);
            return b;
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

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("ECo");

        public override Guid ComponentGuid => new Guid("5d9e24ff-0f9c-4e05-adca-38018f78f88b");
    }
}
