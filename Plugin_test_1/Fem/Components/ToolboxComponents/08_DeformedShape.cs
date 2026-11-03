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
    ///
    /// Besides the straight lines between the displaced nodes, every member's deformed axis is
    /// drawn as a curve: the end displacements and rotations interpolated along the member (cubic
    /// for the bending deflection, linear for the axial displacement and the twist, the exact
    /// shape of a beam element without loads along it; a truss element (Hinges) stays straight and does
    /// not twist), times the factor. The curves are an
    /// output, and they travel with the deformed model so that Member Info draws the sections
    /// along them.
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
            pManager.AddCurveParameter("Curves", "crv",
                "Deformed axis of every member (bending deflection between the nodes included), times the factor. " +
                "The deformed model carries them, so Member Info draws the sections along them.", GH_ParamAccess.list);
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

            int nEl = mdl.Elem1Ds.Count;
            var axes = new Point3d[nEl][];
            var zs = new Vector3d[nEl][];
            var curves = new List<Curve>();
            for (int ie = 0; ie < nEl; ie++)
            {
                TB_Element_1D e = mdl.Elem1Ds[ie];
                if (e?.Nodes == null || e.Nodes.Count < 2) continue;
                double[] ds, de;
                if (!nodeDisp.TryGetValue(e.Nodes[0], out ds) || !nodeDisp.TryGetValue(e.Nodes[1], out de)) continue;
                Node sn = e.Nodes[0];
                Node en = e.Nodes[1];

                // deformed axis, from the undeformed member and its local z (before they are replaced below)
                Point3d[] axis; Vector3d[] zdir;
                if (DeformedAxis(sn.Pt, en.Pt, e.Pln.YAxis, ds, de, factor, 10, e.Hinges, out axis, out zdir))
                {
                    axes[ie] = axis;
                    zs[ie] = zdir;
                    curves.Add(new Polyline(axis).ToNurbsCurve());
                }

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

            TB_MemberResults.SetDeformed(mdl, axes, zs, "Deformed_Shape: " + shown + ", factor " +
                factor.ToString("0.###", CultureInfo.InvariantCulture));
            mdl.SelectedLC = lc;
            Message = shown + ": max " + (maxDisp * 1e3).ToString("F1", CultureInfo.InvariantCulture) + " mm (nodes)";

            // --- output ---
            DA.SetData(0, new GH_TB_Model(mdl));
            DA.SetDataList(1, dispvecs);
            DA.SetDataList(2, rotvecs);
            DA.SetDataList(3, pts);
            DA.SetDataList(4, lns);
            DA.SetDataList(5, curves);
        }

        /// <summary>
        /// Points along the deformed axis of a member from p1 to p2 (nSeg + 1 points) and the local z-direction
        /// at each. d1, d2: displacements of the end nodes (ux, uy, uz [m], rx, ry, rz [rad], global), times
        /// factor. Transverse displacement: cubic (Hermite) from the end translations and the slopes
        /// theta x e_x; axial displacement and twist: linear. A truss element (straight = true) is hinged at
        /// both ends, so it does not follow the rotations of its nodes: it stays straight between the
        /// displaced nodes and does not twist. False if the member has no length.
        /// </summary>
        public static bool DeformedAxis(Point3d p1, Point3d p2, Vector3d localZ, double[] d1, double[] d2, double factor, int nSeg,
                                        bool straight, out Point3d[] axis, out Vector3d[] zdir)
        {
            axis = null; zdir = null;
            Vector3d along = p2 - p1;
            double len = along.Length;
            if (!(len > 0.0) || d1 == null || d2 == null || d1.Length < 6 || d2.Length < 6) return false;
            Vector3d ex = along / len;
            Vector3d ez = localZ - (localZ * ex) * ex;
            if (!(ez.Length > 1e-9)) ez = Vector3d.CrossProduct(ex, Vector3d.CrossProduct(Vector3d.ZAxis, ex));
            if (!(ez.Length > 1e-9)) ez = Vector3d.CrossProduct(ex, Vector3d.YAxis);
            ez.Unitize();
            Vector3d ey = Vector3d.CrossProduct(ez, ex);

            Vector3d u1 = new Vector3d(d1[0], d1[1], d1[2]) * factor, u2 = new Vector3d(d2[0], d2[1], d2[2]) * factor;
            Vector3d r1 = new Vector3d(d1[3], d1[4], d1[5]) * factor, r2 = new Vector3d(d2[3], d2[4], d2[5]) * factor;
            double a1 = u1 * ex, a2 = u2 * ex;                         // axial
            Vector3d t1 = u1 - a1 * ex, t2 = u2 - a2 * ex;            // transverse
            Vector3d s1 = Vector3d.CrossProduct(r1, ex), s2 = Vector3d.CrossProduct(r2, ex);   // slopes
            double phi1 = r1 * ex, phi2 = r2 * ex;                    // twist
            if (straight)
            {
                // truss: the slope of the chord at both ends (the cubic becomes a straight line), no twist
                s1 = s2 = (t2 - t1) / len;
                phi1 = phi2 = 0.0;
            }

            nSeg = Math.Max(1, nSeg);
            axis = new Point3d[nSeg + 1];
            zdir = new Vector3d[nSeg + 1];
            for (int i = 0; i <= nSeg; i++)
            {
                double x = (double)i / nSeg, x2 = x * x, x3 = x2 * x;
                double h1 = 1 - 3 * x2 + 2 * x3, h2 = x - 2 * x2 + x3, h3 = 3 * x2 - 2 * x3, h4 = x3 - x2;
                Vector3d w = h1 * t1 + (h2 * len) * s1 + h3 * t2 + (h4 * len) * s2 + ((1 - x) * a1 + x * a2) * ex;
                axis[i] = p1 + along * x + w;
                double phi = (1 - x) * phi1 + x * phi2;
                zdir[i] = ez * Math.Cos(phi) - ey * Math.Sin(phi); // local z turned by the twist
            }
            return true;
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
