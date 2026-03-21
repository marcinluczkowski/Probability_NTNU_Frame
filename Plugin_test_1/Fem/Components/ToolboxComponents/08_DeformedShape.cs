using System;
using System.Collections.Generic;
using System.Linq;

using Grasshopper.Kernel;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    public class ST_DeformedShape : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the _08_DeformedShape class.
        /// </summary>
        public ST_DeformedShape()
          : base("Deformed_Shape", "Deform",
              "Deformed shape",
              Common.category, Common.sub_post)
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model", "Model", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Load Case", "LC", "Load Case", GH_ParamAccess.item, 0);
            pManager.AddNumberParameter("Factor", "factor", "Factor", GH_ParamAccess.item, 20.0);

            pManager[0].Optional = true;
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
            TB_Model mdl = new TB_Model();
            int lc = new int();
            double factor = new double();

            List<Vector3d> dispvecs = new List<Vector3d>();
            List<Vector3d> rotvecs = new List<Vector3d>();
            List<Point3d> pts = new List<Point3d>();
            List<Line> lns = new List<Line>();

            if (!DA.GetData(0, ref gh_mdl) || gh_mdl?.Value == null) { return; }
            DA.GetData(1, ref lc);
            DA.GetData(2, ref factor);

            mdl = Common.DeepCopy(gh_mdl.Value);
            if (mdl?.Nodes == null || mdl.Nodes.Count == 0) { return; }

            int[] LCs = (mdl.Loads != null && mdl.Loads.Count > 0)
                ? mdl.Loads.Where(x => x.Lc.HasValue).Select(x => x.Lc.Value).Distinct().ToArray()
                : new[] { 0 };
            if (LCs.Length == 0) LCs = new[] { 0 };

            int lc_id = Array.IndexOf(LCs, lc);
            if (lc_id < 0) lc_id = 0;

            if (mdl.Disps == null || lc_id >= mdl.Disps.Count) { return; }

            foreach (Node n in mdl.Nodes)
            {
                if (n.Disps == null || lc_id >= n.Disps.Count) continue;
                var d = n.Disps[lc_id];
                Point3d pt = new Point3d(
                    n.Pt.X + d[0] * factor,
                    n.Pt.Y + d[1] * factor,
                    n.Pt.Z + d[2] * factor);

                pts.Add(pt);
                dispvecs.Add(new Vector3d(d[0], d[1], d[2]));
                rotvecs.Add(new Vector3d(d[3], d[4], d[5]));
            }

            foreach (TB_Element_1D e in mdl.Elem1Ds)
            {
                if (e?.Nodes == null || e.Nodes.Count < 2) continue;
                Node sn = e.Nodes[0];
                Node en = e.Nodes[1];
                if (sn?.Disps == null || lc_id >= sn.Disps.Count ||
                    en?.Disps == null || lc_id >= en.Disps.Count) continue;

                Line defln = new Line(
                        sn.Pt.X + sn.Disps[lc_id][0] * factor,
                        sn.Pt.Y + sn.Disps[lc_id][1] * factor,
                        sn.Pt.Z + sn.Disps[lc_id][2] * factor,
                        en.Pt.X + en.Disps[lc_id][0] * factor,
                        en.Pt.Y + en.Disps[lc_id][1] * factor,
                        en.Pt.Z + en.Disps[lc_id][2] * factor
                    );

                lns.Add(defln);

                e.OriginalLine = e.Line;
                e.Line = defln;

                Vector3d vz0 = e.Pln.YAxis;
                Vector3d vy0 = e.Pln.XAxis;
                Vector3d vx0 = e.Pln.ZAxis;

                Vector3d vx1 = e.Line.UnitTangent;
                Vector3d vy1 = Vector3d.CrossProduct(vz0, vx1);
                Vector3d vz1 = Vector3d.CrossProduct(vx1, vy1);

                Plane newPln = new Plane(e.Line.From, vy1, vz1);

                e.Pln = newPln;
            }

            mdl.SelectedLC = lc;

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