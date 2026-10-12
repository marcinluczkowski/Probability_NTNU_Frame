using System;
using System.Collections.Generic;
using System.Globalization;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Propability_NTNU_v1.Classes.Reliability;

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
    /// </summary>
    public class ST_EC3_Members : GH_Component
    {
        public ST_EC3_Members()
          : base("EC3 Members", "EC3 Mem",
              "Eurocode check of every member of a linear-elastic truss model. Design forces from " +
              "EN 1990 (6.10a) and (6.10b), Norwegian NA. EN 1993-1-1: tension, compression and flexural " +
              "buckling about both axes, with the bending moments from the rigid joints (switch: Bending).",
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

            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Tags", "Tag", "Element tag of each member, in model order.", GH_ParamAccess.list);
            pManager.AddTextParameter("Mode", "Mode",
                "Governing check of each member: tension, compression, buckling y or buckling z. " +
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
            pManager.AddTextParameter("Report", "Rep", "All checks and warnings.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Max Displacement", "Dmax",
                "Largest displacement [mm] under the characteristic loads (the loads as modelled, every load case x 1.0: " +
                "G + Q). Length of the translation vector, at the nodes and the middles of the members. The same value " +
                "as FORM Members / RBDO Sections give for the same model.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Max Displacement ULS", "DmaxU",
                "Largest displacement [mm] over the design combinations (6.10a) and (6.10b) that the check uses. " +
                "The governing combination is in the Report.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            List<string> lcSpecs = new List<string>();
            List<string> lcrSpecs = new List<string>();
            bool bending = true;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            if (!DA.GetDataList(1, lcSpecs)) { return; }
            DA.GetDataList(2, lcrSpecs);
            DA.GetData(3, ref bending);
            MemberCheckOptions options = bending ? MemberCheckOptions.Default() : MemberCheckOptions.AxialOnly();

            if (gh_mdl == null || gh_mdl.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Valid model required.");
                return;
            }
            TB_Model mdl = gh_mdl.Value;

            // --- load cases and buckling lengths ---
            List<EcLoadCase> loadCases = new List<EcLoadCase>();
            var lcr = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string s in lcSpecs)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    loadCases.Add(EcLoadCase.Parse(s));
                }
                ParseBucklingLengths(lcrSpecs, lcr);
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
                    modes.Add(MemberLimitStateResult.ModeName(g.Mode));
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

            // --- messages ---
            if (over > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{over} member(s) have a utilisation above 1. See the Report.");
            foreach (string wmsg in res.Warnings)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, wmsg);

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

            Message = double.IsNaN(res.MaxUtilisation)
                ? ""
                : "U max = " + res.MaxUtilisation.ToString("F2", CultureInfo.InvariantCulture);

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
            DA.SetData(9, res.Report() + "\n" + ModelDisplacement.Report(mdl, dChar, dUls));
            DA.SetData(10, dChar != null ? dChar.MaxMm : double.NaN);
            DA.SetData(11, dUls != null ? dUls.MaxMm : double.NaN);
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
