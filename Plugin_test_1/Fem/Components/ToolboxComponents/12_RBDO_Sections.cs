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
    /// Reliability-based design optimisation of the sections of a linear-elastic truss: the
    /// lightest sections from a catalogue for which the reliability index of EVERY member reaches
    /// the target. The system reliability index of the result is reported, not constrained.
    ///
    /// Every trial design is assessed with the same routine as the FORM Members component
    /// (MemberReliability.Run); the search itself is in SectionOptimiser.Run. Wire the output
    /// Model into FORM Members or EC3 Members to see the result in full.
    /// </summary>
    public class ST_RBDO_Sections : GH_Component
    {
        public ST_RBDO_Sections()
          : base("RBDO Sections", "RBDO",
              "Reliability-based design optimisation: the lightest catalogue sections for which the " +
              "reliability index of every member reaches the target. FORM per member limit state, " +
              "gradients by DDM. The system reliability index is reported, not constrained.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Assembled model. Its geometry, tags, materials, supports and loads are used; the " +
                "sections it comes with are replaced.", GH_ParamAccess.item);
            pManager.AddGenericParameter("Random Variables", "RV",
                "Random variables, in the same order as Bindings.", GH_ParamAccess.list);
            pManager.AddTextParameter("Bindings", "Bind",
                "One spec per variable, as in FORM Members: LC:<lc>[:<ref>], FY[:<tag>], E[:<tag>], TR, TE.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("Target Beta", "Bt",
                "Reliability index that every member has to reach.", GH_ParamAccess.item, 3.8);
            pManager.AddTextParameter("Groups", "Grp",
                "Optional, one per line: \"<tag>:<group>\". All members of a group get the same section. " +
                "<tag> is an element tag and may end in * (\"Brace*:braces\"). The first line that matches " +
                "decides. Members that no line matches are grouped by their own element tag.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Section Table", "Table",
                "Optional: a cross-section table in the Karamba3D text format (header row, units row, one row " +
                "per profile). Default: the built-in HEA list.", GH_ParamAccess.list);
            pManager.AddTextParameter("Candidates", "Cand",
                "Optional: names of the sections of the table that may be used. Default: all I-shaped sections.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Correlations", "Corr",
                "Optional, one per line: \"i,j,rho\" using 0-based variable indices.", GH_ParamAccess.list);
            pManager.AddTextParameter("Buckling Lengths", "Lcr",
                "Optional, one per line: \"<tag>:<Lcr,y>:<Lcr,z>[:<L,LT>]\" in metres (L,LT = length between lateral restraints for lateral-torsional buckling, default Lcr,z). Default: the element length.",
                GH_ParamAccess.list);
            pManager.AddIntegerParameter("Max Iterations", "MaxIt",
                "Maximum HL-RF iterations per limit state.", GH_ParamAccess.item, 300);
            pManager.AddIntegerParameter("System Samples", "Nsys",
                "Samples of the importance sampling estimate of the system failure probability of the " +
                "final design. 0 = Ditlevsen bounds only.", GH_ParamAccess.item, SystemReliability.DefaultSamples);
            pManager.AddIntegerParameter("Exchange Depth", "Exch",
                "After the stepping search, this many groups at a time are made one size heavier to see whether " +
                "the others can then become lighter. 0 = off, 1 = one group at a time, 2 = also every pair of " +
                "groups (finds more, but slow with many groups).", GH_ParamAccess.item, SectionOptimiser.DefaultExchangeDepth);
            pManager.AddBooleanParameter("Bending", "Bend",
                "true (default): members are checked for the axial force AND the bending moments from the rigid " +
                "joints (EN 1993-1-1 6.2.1(7), 6.3.3 with Annex B, lateral-torsional buckling; elastic, Cm >= 0.9, " +
                "as Karamba3D's defaults). false: axial force only.", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Binary Search", "Bin",
                "false (default): start with the lightest candidate in every group and step the failing groups up one " +
                "size at a time. true: first a binary search of the candidate list (lightest to heaviest) in all groups " +
                "at once, about log2(candidates) assessments, then step up where needed. The step down and the exchange " +
                "follow in both cases. Fewer assessments when the sections end far from the lightest candidate.",
                GH_ParamAccess.item, false);
            pManager.AddIntegerParameter("Restarts", "Rst",
                "0 (default): one search. n > 0: after the first search, n restarts: from the best design so far, half of " +
                "the groups (at least 3) get random sizes, the structure is stepped up where a member fails and stepped down " +
                "again, and the result is kept if it is lighter. The exchange step then runs once, on the best design. Finds " +
                "lighter designs where the forces depend strongly on the sections (floor grids); about n times the work.",
                GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Seed", "Seed",
                "Seed of the random choices of the restarts: the same seed gives the same result. Another seed shows whether " +
                "the result depends on the random choices.", GH_ParamAccess.item, 1);

            for (int i = 3; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "The model with the optimised sections, with the group and beta of every member attached (for Member Info).", GH_ParamAccess.item);
            pManager.AddTextParameter("Groups", "Grp", "Name of each group.", GH_ParamAccess.list);
            pManager.AddTextParameter("Sections", "Sec", "Section of each group.", GH_ParamAccess.list);
            pManager.AddTextParameter("Member Sections", "MSec", "Section of each member, in model order.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Weight", "W", "Weight of the optimised structure [kg].", GH_ParamAccess.item);
            pManager.AddNumberParameter("Beta", "B",
                "Reliability index of each member in the optimised structure (NaN: no axial force).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Min Beta", "Bmin", "Lowest member reliability index.", GH_ParamAccess.item);
            pManager.AddNumberParameter("System Beta", "Bsys",
                "Reliability index of the optimised structure as a series system. Reported, not constrained.",
                GH_ParamAccess.item);
            pManager.AddBooleanParameter("Feasible", "OK", "True if every member reaches the target.", GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "Rep", "The search, the result per group and the system reliability.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Initial Weight", "W0",
                "Weight of the input model, with the sections it came with [kg].", GH_ParamAccess.item);
            pManager.AddNumberParameter("Initial Beta", "B0",
                "Reliability index of each member of the input model (NaN: no axial force).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Initial Min Beta", "Bmin0", "Lowest member reliability index of the input model.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Initial System Beta", "Bsys0",
                "Reliability index of the input model as a series system.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Max Displacement", "Dmax",
                "Largest displacement of the optimised structure [mm] under the loads as modelled (every load case x 1.0, " +
                "E of the model); with LC:<lc>:<ref> at the characteristic values these are the characteristic loads G + Q. " +
                "Length of the translation vector, at the nodes and the middles of the members. The same measure as " +
                "EC3 Sections gives, so the two designs compare directly.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Iteration", "It",
                "Iteration history, one entry per assessment of the structure, in the order of the search: the assessment " +
                "number (0 = the lightest candidate in every group; the '#' column of the search in the Report). Every " +
                "trial design is assessed once.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Iteration Weight", "ItW", "Weight of the trial design of each assessment [kg].", GH_ParamAccess.list);
            pManager.AddNumberParameter("Iteration Beta", "ItB",
                "Lowest member reliability index (below the target: not good enough; the system beta is computed for the final design only) of the trial design of each assessment.", GH_ParamAccess.list);
            pManager.AddTextParameter("Iteration Phase", "ItP",
                "Part of the search each assessment belongs to: start; bisect (a round of the binary search, if on); restart (see Restarts); up (the groups with a failing member one size " +
                "heavier); down (one group one size lighter); exchange (one group one size heavier, the others lighter again).",
                GH_ParamAccess.list);
            pManager.AddBooleanParameter("Iteration Kept", "ItK",
                "True for the designs the search moved on to (at that assessment, or later when an exchange was accepted).",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("Current Weight", "CurW",
                "Weight of the design the search stands on after each assessment [kg]: the convergence curve.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Current Beta", "CurB",
                "Lowest member reliability index (below the target: not good enough; the system beta is computed for the final design only) of the design the search stands on after each assessment.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Optimum Iteration", "ItOpt",
                "The assessment of the final design: where the optimum was found. (When it was found inside a restart or an " +
                "exchange, the search moves on to it once that is finished: the Current outputs change a few assessments later.)",
                GH_ParamAccess.item);
            pManager.AddTextParameter("Iteration Sections", "ItSec",
                "One branch per assessment: the section of each group in the trial design, in the order of Groups.",
                GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Iteration Restart", "ItR",
                "Restart number of each assessment: 0 for the first search and the exchange step, 1, 2, ... for the restarts.",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            List<object> rvObjs = new List<object>();
            List<string> bindSpecs = new List<string>();
            double betaTarget = 3.8;
            List<string> groupSpecs = new List<string>();
            List<string> tableLines = new List<string>();
            List<string> candidateNames = new List<string>();
            List<string> corrSpecs = new List<string>();
            List<string> lcrSpecs = new List<string>();
            int maxIt = 300;
            int sysSamples = SystemReliability.DefaultSamples;
            int exchangeDepth = SectionOptimiser.DefaultExchangeDepth;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            if (!DA.GetDataList(1, rvObjs)) { return; }
            if (!DA.GetDataList(2, bindSpecs)) { return; }
            DA.GetData(3, ref betaTarget);
            DA.GetDataList(4, groupSpecs);
            DA.GetDataList(5, tableLines);
            DA.GetDataList(6, candidateNames);
            DA.GetDataList(7, corrSpecs);
            DA.GetDataList(8, lcrSpecs);
            DA.GetData(9, ref maxIt);
            DA.GetData(10, ref sysSamples);
            DA.GetData(11, ref exchangeDepth);
            bool bending = true;
            DA.GetData(12, ref bending);
            bool binarySearch = false;
            DA.GetData(13, ref binarySearch);
            int restarts = 0, seed = 1;
            DA.GetData(14, ref restarts);
            DA.GetData(15, ref seed);
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

            // --- bindings, correlations, buckling lengths, groups, candidates ---
            List<MemberVariable> bindings = new List<MemberVariable>();
            double[,] rho = null;
            var lcr = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            string[] groups;
            List<SectionRecord> candidates;
            var inputWarnings = new List<string>();
            try
            {
                for (int i = 0; i < vars.Count; i++)
                    bindings.Add(MemberVariable.Parse(bindSpecs[i], vars[i].Name ?? ("v" + i)));

                rho = ParseCorrelations(corrSpecs, vars.Count);
                ParseBucklingLengths(lcrSpecs, lcr);
                groups = SectionOptimiser.GroupOfMembers(mdl, groupSpecs);
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
                res = SectionOptimiser.RunReliability(mdl, candidates, groups, vars, bindings, betaTarget,
                                                      rho, lcr, Math.Max(1, maxIt), Math.Max(0, sysSamples),
                                                      Math.Max(0, exchangeDepth), SectionOptimiser.DefaultMaxEvaluations,
                                                      options, binarySearch, Math.Max(0, restarts), seed);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Section optimisation failed: " + ex.Message);
                return;
            }
            sw.Stop();

            // --- results ---
            MemberReliabilityResult rel = res.Final.Reliability;
            SystemReliabilityResult sys = rel.SystemResult;
            double betaSys = (sys != null) ? sys.BetaSystem : double.NaN;

            // --- messages ---
            if (!res.Feasible)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Not every member reaches the target. See the Report.");
            foreach (string wmsg in inputWarnings.Concat(res.Warnings))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, wmsg);

            // --- largest displacement of the optimised structure ---
            DisplacementResult dChar = null;
            try
            {
                dChar = ModelDisplacement.Characteristic(res.Model);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The displacements could not be computed: " + ex.Message);
            }

            string report = res.Report() +
                (sys != null ? "\n" + sys.Report() : "") +
                "\n" + ModelDisplacement.Report(res.Model, dChar) +
                "\nelapsed: " + sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s";

            Message = res.Weight.ToString("F0", CultureInfo.InvariantCulture) + " kg" +
                      (double.IsNaN(rel.MinBeta) ? "" : ", \u03B2 min = " + rel.MinBeta.ToString("F2", CultureInfo.InvariantCulture));

            // --- output ---
            TB_MemberResults.SetFromOptimisation(res, "RBDO Sections");     // groups and betas travel with the model
            DA.SetData(0, new GH_TB_Model(res.Model));
            DA.SetDataList(1, res.Groups);
            DA.SetDataList(2, res.GroupSections);
            DA.SetDataList(3, res.MemberSections);
            DA.SetData(4, res.Weight);
            DA.SetDataList(5, res.Final.Value);
            DA.SetData(6, rel.MinBeta);
            DA.SetData(7, betaSys);
            DA.SetData(8, res.Feasible);
            DA.SetData(9, report);

            MemberReliabilityResult before = res.ReliabilityInitial;
            DA.SetData(10, res.WeightInitial);
            DA.SetDataList(11, GoverningBetas(before));
            DA.SetData(12, before != null ? before.MinBeta : double.NaN);
            DA.SetData(13, (before != null && before.SystemResult != null) ? before.SystemResult.BetaSystem : double.NaN);
            DA.SetData(14, dChar != null ? dChar.MaxMm : double.NaN);
            SetHistory(DA, 15, res);
        }

        /// <summary>The iteration history of the search, as outputs first ... first + 9.</summary>
        private static void SetHistory(IGH_DataAccess DA, int first, SectionOptimisationResult res)
        {
            List<SearchIteration> h = res.History;
            var sections = new DataTree<string>();
            for (int i = 0; i < h.Count; i++)
            {
                var path = new GH_Path(i);
                foreach (int c in h[i].Candidates) sections.Add(res.CandidateNames[c], path);
            }

            DA.SetDataList(first, h.Select(x => x.Index).ToList());
            DA.SetDataList(first + 1, h.Select(x => x.Weight).ToList());
            DA.SetDataList(first + 2, h.Select(x => x.Worst).ToList());
            DA.SetDataList(first + 3, h.Select(x => x.Phase).ToList());
            DA.SetDataList(first + 4, h.Select(x => x.Kept).ToList());
            DA.SetDataList(first + 5, h.Select(x => x.CurrentWeight).ToList());
            DA.SetDataList(first + 6, h.Select(x => x.CurrentWorst).ToList());
            DA.SetData(first + 7, res.OptimumIteration);
            DA.SetDataTree(first + 8, sections);
            DA.SetDataList(first + 9, h.Select(x => x.Restart).ToList());
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

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("RB");

        public override Guid ComponentGuid => new Guid("a4b56519-cc23-42b3-83c4-1bf5548eab76");
    }
}
