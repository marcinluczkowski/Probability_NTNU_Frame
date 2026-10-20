using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Grasshopper.Kernel;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    /// <summary>
    /// The deformed shape of a model.
    ///
    /// The model does not need to be solved first: a model without displacements (for example the
    /// optimised Model of RBDO Sections or EC3 Sections, or the output of Assembly) is solved here,
    /// linear static, on a copy. The incoming model is never changed.
    ///
    /// What is shown:
    ///   LC Factors given   the sum of factor x displacement of each load case, in the order of the
    ///                      model's load cases (e.g. 1.2015 and 1.5 for 6.10b with LC0 = G, LC1 = Q)
    ///   LC = -1            every load case x 1.0: the loads as modelled (the characteristic loads,
    ///                      the same as Max Displacement Dmax of the analysis components)
    ///   LC = n             load case n alone
    /// </summary>
    public class ST_DeformedShape : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the _08_DeformedShape class.
        /// </summary>
        public ST_DeformedShape()
          : base("Deformed_Shape", "Deform",
              "Deformed shape. Solves the model (linear static, on a copy) if it has no displacements yet, so the " +
              "optimised Model of RBDO Sections or EC3 Sections can be connected directly.",
              Common.category, Common.sub_post)
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Model, solved or not (an unsolved model is solved here, on a copy).", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Load Case", "LC",
                "Load case to show; -1 = every load case x 1.0 (the loads as modelled). Ignored when LC Factors is given.",
                GH_ParamAccess.item, 0);
            pManager.AddNumberParameter("Factor", "factor", "Scale factor on the displacements in the drawing.", GH_ParamAccess.item, 20.0);
            pManager.AddNumberParameter("LC Factors", "LCf",
                "Optional: one factor per load case of the model, in the model's load-case order (e.g. 1.2015 and 1.5 " +
                "for 6.10b with LC0 = G and LC1 = Q). The shape is then the combination of the load cases.",
                GH_ParamAccess.list);

            pManager[0].Optional = true;
            pManager[3].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.RegisterParam(new Param_TB_Model(), "DefModel", "Def M", "Deformed Model", GH_ParamAccess.item);
            pManager.AddVectorParameter("Disp", "Disp", "Displacement in [m]", GH_ParamAccess.list);
            pManager.AddVectorParameter("Rotation", "Rot", "Rotation in [rad]", GH_ParamAccess.list);
            pManager.AddPointParameter("points", "pts", "points", GH_ParamAccess.list);
            pManager.AddLineParameter("lines", "lns", "lines", GH_ParamAccess.list);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = new GH_TB_Model();
            int lc = 0;
            double factor = 20.0;
            List<double> lcFactors = new List<double>();

            List<Vector3d> dispvecs = new List<Vector3d>();
            List<Vector3d> rotvecs = new List<Vector3d>();
            List<Point3d> pts = new List<Point3d>();
            List<Line> lns = new List<Line>();

            if (!DA.GetData(0, ref gh_mdl) || gh_mdl?.Value == null) { return; }
            DA.GetData(1, ref lc);
            DA.GetData(2, ref factor);
            DA.GetDataList(3, lcFactors);

            // work on a copy: the incoming model (e.g. an optimiser's output) is never changed
            TB_Model mdl = Common.DeepCopy(gh_mdl.Value);
            if (mdl?.Nodes == null || mdl.Nodes.Count == 0) { return; }

            // --- solve the copy if it has no displacements yet ---
            if (mdl.Disps == null || mdl.Disps.Count == 0)
            {
                try
                {
                    new SolveLS(ref mdl);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The model could not be solved: " + ex.Message);
                    return;
                }
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "The model had no displacements: solved here (linear static) on a copy.");
            }

            // --- which load cases, with which factors ---
            int[] LCs = (mdl.LCs != null && mdl.LCs.Length > 0) ? mdl.LCs : new[] { 0 };
            int nLc = Math.Min(LCs.Length, mdl.Disps.Count);
            double[] w = new double[nLc];
            string shown;

            if (lcFactors.Count > 0)
            {
                if (lcFactors.Count != LCs.Length)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"LC Factors has {lcFactors.Count} values but the model has {LCs.Length} load case(s) " +
                        "(" + string.Join(", ", LCs.Select(x => "LC" + x.ToString(CultureInfo.InvariantCulture))) + ").");
                    return;
                }
                for (int i = 0; i < nLc; i++) w[i] = lcFactors[i];
                shown = string.Join(" + ", Enumerable.Range(0, nLc).Where(i => w[i] != 0.0)
                    .Select(i => w[i].ToString("0.####", CultureInfo.InvariantCulture) + " LC" + LCs[i].ToString(CultureInfo.InvariantCulture)));
            }
            else if (lc < 0)
            {
                for (int i = 0; i < nLc; i++) w[i] = 1.0;
                shown = "all load cases x 1.0";
            }
            else
            {
                int lc_id = Array.IndexOf(LCs, lc);
                if (lc_id < 0 || lc_id >= nLc)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"The model has no load case {lc}; showing LC{LCs[0]}. Use -1 for all load cases.");
                    lc_id = 0;
                }
                w[lc_id] = 1.0;
                shown = "LC" + LCs[lc_id].ToString(CultureInfo.InvariantCulture);
            }

            // combined displacement of a node: sum of factor x displacement of each load case
            Func<Node, double[]> dispOf = n =>
            {
                if (n?.Disps == null) return null;
                double[] d = new double[6];
                for (int i = 0; i < nLc; i++)
                {
                    if (w[i] == 0.0) continue;
                    if (i >= n.Disps.Count || n.Disps[i] == null) return null;
                    for (int k = 0; k < 6; k++) d[k] += w[i] * n.Disps[i][k];
                }
                return d;
            };

            double maxDisp = 0.0;
            var nodeDisp = new Dictionary<Node, double[]>();
            foreach (Node n in mdl.Nodes)
            {
                double[] d = dispOf(n);
                if (d == null) continue;
                nodeDisp[n] = d;

                pts.Add(new Point3d(n.Pt.X + d[0] * factor, n.Pt.Y + d[1] * factor, n.Pt.Z + d[2] * factor));
                dispvecs.Add(new Vector3d(d[0], d[1], d[2]));
                rotvecs.Add(new Vector3d(d[3], d[4], d[5]));
                maxDisp = Math.Max(maxDisp, Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]));
            }

            foreach (TB_Element_1D e in mdl.Elem1Ds)
            {
                if (e?.Nodes == null || e.Nodes.Count < 2) continue;
                double[] ds, de;
                if (!nodeDisp.TryGetValue(e.Nodes[0], out ds) || !nodeDisp.TryGetValue(e.Nodes[1], out de)) continue;
                Node sn = e.Nodes[0];
                Node en = e.Nodes[1];

                Line defln = new Line(
                        sn.Pt.X + ds[0] * factor,
                        sn.Pt.Y + ds[1] * factor,
                        sn.Pt.Z + ds[2] * factor,
                        en.Pt.X + de[0] * factor,
                        en.Pt.Y + de[1] * factor,
                        en.Pt.Z + de[2] * factor
                    );

                lns.Add(defln);

                e.OriginalLine = e.Line;
                e.Line = defln;

                Vector3d vz0 = e.Pln.YAxis;
                Vector3d vx1 = e.Line.UnitTangent;
                Vector3d vy1 = Vector3d.CrossProduct(vz0, vx1);
                Vector3d vz1 = Vector3d.CrossProduct(vx1, vy1);

                e.Pln = new Plane(e.Line.From, vy1, vz1);
            }

            mdl.SelectedLC = lc;
            Message = shown + ": max " + (maxDisp * 1e3).ToString("F1", CultureInfo.InvariantCulture) + " mm (nodes)";

            // --- output ---
            DA.SetData(0, new GH_TB_Model(mdl));
            DA.SetDataList(1, dispvecs);
            DA.SetDataList(2, rotvecs);
            DA.SetDataList(3, pts);
            DA.SetDataList(4, lns);
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon => IconHelper.Create("\u0394");

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("ea2b8c47-80af-4780-a62e-a8bdabe17509"); }
        }
    }
}
