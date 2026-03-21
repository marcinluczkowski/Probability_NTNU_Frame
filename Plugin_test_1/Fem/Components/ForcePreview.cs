using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.Display;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components
{
    /// <summary>Sample forces/moments along beams. Buttons 1-2: numeric labels. Buttons 3-8: graph curves.</summary>
    public class ForcePreview : GH_Component
    {
        public bool ShowForces { get; set; }
        public bool ShowMoments { get; set; }
        public bool ShowGraphN { get; set; }
        public bool ShowGraphVy { get; set; }
        public bool ShowGraphVz { get; set; }
        public bool ShowGraphMx { get; set; }
        public bool ShowGraphMy { get; set; }
        public bool ShowGraphMz { get; set; }
        /// <summary>When true, chart offset is as vertical as possible (project +Z onto plane ⊥ beam); columns use +X in that plane.</summary>
        public bool ChartOrientToZ { get; set; } = true;

        private struct ForceLabel
        {
            public Point3d Position;
            public string Text;
            public Color Colour;
        }
        private List<ForceLabel> _labels = new List<ForceLabel>();
        private double _textHeight = 0.3;
        private double _scale = 1.0;
        private int _remapMode = 2;

        private readonly Dictionary<int, List<Mesh>> _graphMeshes = new Dictionary<int, List<Mesh>>();
        private static readonly int[] _graphKeys = { 0, 1, 2, 3, 4, 5 }; // N, Vy, Vz, Mx, My, Mz

        public ForcePreview()
          : base("Force Preview", "ForcePreview",
              "Sample forces and bending moments. Buttons 1-2: labels; 3-8: graph meshes; 9: Z-oriented charts (2D trusses).",
              Common.category, Common.sub_post)
        { }

        public override void CreateAttributes()
        {
            m_attributes = new ForcePreviewAttributes(this);
        }

        public override bool IsPreviewCapable => true;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);

            bool anyLabels = ShowForces || ShowMoments;
            bool anyGraph = ShowGraphN || ShowGraphVy || ShowGraphVz || ShowGraphMx || ShowGraphMy || ShowGraphMz;

            if (anyLabels && _labels != null)
            {
                double h = _textHeight * Math.Max(0.1, _scale);
                foreach (var lbl in _labels)
                {
                    var plane = new Plane(lbl.Position, Vector3d.XAxis, Vector3d.YAxis);
                    args.Display.Draw3dText(lbl.Text, lbl.Colour, plane, (float)h, "Arial");
                }
            }

            if (anyGraph && _graphMeshes != null)
            {
                var toShow = new[] { ShowGraphN, ShowGraphVy, ShowGraphVz, ShowGraphMx, ShowGraphMy, ShowGraphMz };
                var mat = new DisplayMaterial
                {
                    Diffuse = Color.FromArgb(200, 170, 170, 170),
                    Transparency = 0.45
                };
                for (int k = 0; k < 6; k++)
                {
                    if (!toShow[k] || !_graphMeshes.TryGetValue(k, out var meshes)) continue;
                    foreach (var m in meshes)
                    {
                        if (m != null && m.Vertices.Count >= 2)
                            args.Display.DrawMeshShaded(m, mat);
                    }
                }
            }
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean("ShowForces", ShowForces);
            writer.SetBoolean("ShowMoments", ShowMoments);
            writer.SetBoolean("ShowGraphN", ShowGraphN);
            writer.SetBoolean("ShowGraphVy", ShowGraphVy);
            writer.SetBoolean("ShowGraphVz", ShowGraphVz);
            writer.SetBoolean("ShowGraphMx", ShowGraphMx);
            writer.SetBoolean("ShowGraphMy", ShowGraphMy);
            writer.SetBoolean("ShowGraphMz", ShowGraphMz);
            writer.SetBoolean("ChartOrientToZ", ChartOrientToZ);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            if (reader.ItemExists("ShowForces")) ShowForces = reader.GetBoolean("ShowForces");
            if (reader.ItemExists("ShowMoments")) ShowMoments = reader.GetBoolean("ShowMoments");
            if (reader.ItemExists("ShowGraphN")) ShowGraphN = reader.GetBoolean("ShowGraphN");
            if (reader.ItemExists("ShowGraphVy")) ShowGraphVy = reader.GetBoolean("ShowGraphVy");
            if (reader.ItemExists("ShowGraphVz")) ShowGraphVz = reader.GetBoolean("ShowGraphVz");
            if (reader.ItemExists("ShowGraphMx")) ShowGraphMx = reader.GetBoolean("ShowGraphMx");
            if (reader.ItemExists("ShowGraphMy")) ShowGraphMy = reader.GetBoolean("ShowGraphMy");
            if (reader.ItemExists("ShowGraphMz")) ShowGraphMz = reader.GetBoolean("ShowGraphMz");
            if (reader.ItemExists("ChartOrientToZ")) ChartOrientToZ = reader.GetBoolean("ChartOrientToZ");
            return base.Read(reader);
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "M", "Solved FEM model", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Load Case", "LC", "Load case index", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Sample Count", "N", "Number of sample points per element (including ends)", GH_ParamAccess.item, 3);
            pManager.AddNumberParameter("Parameters", "t", "Optional: parameters 0..1 along element (overrides Sample Count)", GH_ParamAccess.list);
            pManager.AddNumberParameter("Text Height", "H", "Label height in model units", GH_ParamAccess.item, 0.3);
            pManager.AddNumberParameter("Scale", "S", "Scale factor for labels and graph curves", GH_ParamAccess.item, 1.0);
            pManager.AddIntegerParameter("Remap", "RMap",
                "Graph value mapping: 0 = raw (no range norm, use Scale). 1 = neg→[-1,0] pos→[0,1] (zero fixed). 2 = symmetric [-1,1] (zero fixed).",
                GH_ParamAccess.item, 2);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("Points", "Pts", "Sample points {elem}(t)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("N", "N", "Axial force [kN] {elem}(t)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Vy", "Vy", "Shear Vy [kN] {elem}(t)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Vz", "Vz", "Shear Vz [kN] {elem}(t)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Mx", "Mx", "Torsion Mx [kNm] {elem}(t)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("My", "My", "Moment My [kNm] {elem}(t)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Mz", "Mz", "Moment Mz [kNm] {elem}(t)", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_TB_Model ghMdl = null;
            if (!DA.GetData(0, ref ghMdl) || ghMdl?.Value == null) { return; }

            var mdl = ghMdl.Value;
            if (mdl.Elem1Ds == null || mdl.Elem1Ds.Count == 0) { return; }

            int lcReq = 0;
            int sampleCount = 3;
            List<double> customParams = new List<double>();
            double textH = 0.3;
            double scale = 1.0;
            int remapMode = 2;

            DA.GetData(1, ref lcReq);
            DA.GetData(2, ref sampleCount);
            DA.GetDataList(3, customParams);
            DA.GetData(4, ref textH);
            DA.GetData(5, ref scale);
            DA.GetData(6, ref remapMode);

            sampleCount = Math.Max(2, sampleCount);
            _textHeight = Math.Max(0.01, textH);
            _scale = Math.Max(0.01, scale);
            _remapMode = Math.Max(0, Math.Min(2, remapMode));

            int[] LCs = (mdl.Loads != null && mdl.Loads.Count > 0)
                ? mdl.Loads.Where(x => x.Lc.HasValue).Select(x => x.Lc.Value).Distinct().ToArray()
                : new[] { 0 };
            if (LCs.Length == 0) LCs = new[] { 0 };

            int lcId = Array.IndexOf(LCs, lcReq);
            if (lcId < 0) lcId = 0;

            double[] tVals = customParams != null && customParams.Count > 0
                ? customParams.Where(x => x >= 0 && x <= 1).ToArray()
                : Enumerable.Range(0, sampleCount).Select(i => sampleCount <= 1 ? 0.5 : (double)i / (sampleCount - 1)).ToArray();
            if (tVals.Length == 0) tVals = new[] { 0.0, 0.5, 1.0 };

            _labels.Clear();
            foreach (var k in _graphKeys)
                _graphMeshes[k] = new List<Mesh>();

            var ptsTree = new GH_Structure<GH_Point>();
            var nTree = new GH_Structure<GH_Number>();
            var vyTree = new GH_Structure<GH_Number>();
            var vzTree = new GH_Structure<GH_Number>();
            var mxTree = new GH_Structure<GH_Number>();
            var myTree = new GH_Structure<GH_Number>();
            var mzTree = new GH_Structure<GH_Number>();

            var elemList = mdl.Elem1Ds.Where(x => x != null).ToList();

            // First pass: collect all values per quantity for global min/max
            var allN = new List<double>();
            var allVy = new List<double>();
            var allVz = new List<double>();
            var allMx = new List<double>();
            var allMy = new List<double>();
            var allMz = new List<double>();

            var perElemData = new List<(TB_Element_1D elem, List<double> N, List<double> Vy, List<double> Vz, List<double> Mx, List<double> My, List<double> Mz, List<Point3d> pts)>();

            for (int eIdx = 0; eIdx < elemList.Count; eIdx++)
            {
                var elem = elemList[eIdx];
                if (elem.Nodes == null || elem.Nodes.Count < 2) continue;
                if (lcId >= (elem.Nodes[0].Disps?.Count ?? 0)) continue;

                double[] F;
                try { F = elem.Calc_Forces(lcId); }
                catch { continue; }

                var path = new GH_Path(eIdx);
                var ln = elem.OriginalLine.Length > 1e-12 ? elem.OriginalLine : elem.Line;

                var nVals = new List<double>();
                var vyVals = new List<double>();
                var vzVals = new List<double>();
                var mxVals = new List<double>();
                var myVals = new List<double>();
                var mzVals = new List<double>();
                var pts = new List<Point3d>();

                foreach (double t in tVals)
                {
                    double tt = Math.Max(0, Math.Min(1, t));
                    Point3d pt = ln.PointAt(tt);

                    double N = -F[0] * 1e-3;
                    double Vy = -F[1] * 1e-3;
                    double Vz = -F[2] * 1e-3;
                    double Mx = -F[3] * 1e-3;
                    double My = ((1 - tt) * (-F[4]) + tt * F[10]) * 1e-3;
                    double Mz = ((1 - tt) * (-F[5]) + tt * F[11]) * 1e-3;

                    ptsTree.Append(new GH_Point(pt), path);
                    nTree.Append(new GH_Number(N), path);
                    vyTree.Append(new GH_Number(Vy), path);
                    vzTree.Append(new GH_Number(Vz), path);
                    mxTree.Append(new GH_Number(Mx), path);
                    myTree.Append(new GH_Number(My), path);
                    mzTree.Append(new GH_Number(Mz), path);

                    nVals.Add(N); vyVals.Add(Vy); vzVals.Add(Vz);
                    mxVals.Add(Mx); myVals.Add(My); mzVals.Add(Mz);
                    pts.Add(pt);
                    allN.Add(N); allVy.Add(Vy); allVz.Add(Vz);
                    allMx.Add(Mx); allMy.Add(My); allMz.Add(Mz);

                    if (ShowForces || ShowMoments)
                    {
                        var parts = new List<string>();
                        if (ShowForces) parts.Add($"N={N:F1} Vy={Vy:F1} Vz={Vz:F1}");
                        if (ShowMoments) parts.Add($"My={My:F2} Mz={Mz:F2}");
                        _labels.Add(new ForceLabel { Position = pt, Text = string.Join("\n", parts), Colour = Color.DarkSlateGray });
                    }
                }

                perElemData.Add((elem, nVals, vyVals, vzVals, mxVals, myVals, mzVals, pts));
            }

            var allByK = new[] { allN, allVy, allVz, allMx, allMy, allMz };
            var perpOffsets = new[] { 0, 0.08, 0.16, 0.24, 0.32, 0.4 };

            foreach (var data in perElemData)
            {
                var elem = data.elem;
                var ln = elem.OriginalLine.Length > 1e-12 ? elem.OriginalLine : elem.Line;
                Vector3d tangent = ln.UnitTangent;
                GetChartBasis(tangent, ChartOrientToZ, out Vector3d perp, out Vector3d perp2);

                double len = ln.Length;
                double baseScale = _scale * len * 0.15;

                for (int k = 0; k < 6; k++)
                {
                    bool show = k == 0 ? ShowGraphN : k == 1 ? ShowGraphVy : k == 2 ? ShowGraphVz : k == 3 ? ShowGraphMx : k == 4 ? ShowGraphMy : ShowGraphMz;
                    if (!show) continue;

                    var vals = k == 0 ? data.N : k == 1 ? data.Vy : k == 2 ? data.Vz : k == 3 ? data.Mx : k == 4 ? data.My : data.Mz;
                    var allK = allByK[k];
                    var basePts = new List<Point3d>();
                    var topPts = new List<Point3d>();
                    for (int i = 0; i < data.pts.Count; i++)
                    {
                        double r = RemapForChart(vals[i], allK, _remapMode);
                        Vector3d offset = perp * (baseScale * r) + perp2 * (baseScale * perpOffsets[k]);
                        basePts.Add(data.pts[i]);
                        topPts.Add(data.pts[i] + offset);
                    }
                    if (basePts.Count >= 2)
                    {
                        var mesh = new Mesh();
                        for (int i = 0; i < basePts.Count; i++)
                        {
                            mesh.Vertices.Add(basePts[i]);
                            mesh.Vertices.Add(topPts[i]);
                        }
                        for (int i = 0; i < basePts.Count - 1; i++)
                        {
                            int a = i * 2;
                            int b = a + 1;
                            int c = (i + 1) * 2;
                            int d = c + 1;
                            mesh.Faces.AddFace(a, b, d, c);
                        }
                        mesh.Compact();
                        _graphMeshes[k].Add(mesh);
                    }
                }
            }

            DA.SetDataTree(0, ptsTree);
            DA.SetDataTree(1, nTree);
            DA.SetDataTree(2, vyTree);
            DA.SetDataTree(3, vzTree);
            DA.SetDataTree(4, mxTree);
            DA.SetDataTree(5, myTree);
            DA.SetDataTree(6, mzTree);
        }

        /// <summary>Maps a value to chart offset factor. Mode 0: raw v. Mode 1: neg in [-1,0], pos in [0,1]. Mode 2: [-1,1] symmetric, zero fixed.</summary>
        internal static double RemapForChart(double v, List<double> all, int mode)
        {
            if (all == null || all.Count == 0) return 0;
            switch (mode)
            {
                case 0:
                    return v;
                case 1:
                {
                    if (Math.Abs(v) < 1e-15) return 0;
                    if (v < 0)
                    {
                        var negs = all.Where(x => x < 0).ToList();
                        if (negs.Count == 0) return 0;
                        double minNeg = negs.Min();
                        if (minNeg >= -1e-15) return 0;
                        return -v / minNeg;
                    }
                    var pos = all.Where(x => x > 0).ToList();
                    if (pos.Count == 0) return 0;
                    double maxPos = pos.Max();
                    if (maxPos <= 1e-15) return 0;
                    return v / maxPos;
                }
                default:
                {
                    double min = all.Min();
                    double max = all.Max();
                    double maxAbs = Math.Max(Math.Abs(min), Math.Abs(max));
                    if (maxAbs < 1e-15) return 0;
                    double r = v / maxAbs;
                    return r < -1 ? -1 : (r > 1 ? 1 : r);
                }
            }
        }

        /// <summary>
        /// Chart lies in plane spanned by tangent and perp. Value offset is along perp.
        /// Z mode: perp = projection of +Z onto plane ⊥ tangent (max vertical in cross-section, not world Z unless beam is horizontal).
        /// Vertical member: use projection of +X instead; perp2 = tangent × perp.
        /// </summary>
        internal static void GetChartBasis(Vector3d tangent, bool orientToZ, out Vector3d perp, out Vector3d perp2)
        {
            tangent.Unitize();
            if (!orientToZ)
            {
                perp = Vector3d.CrossProduct(tangent, Vector3d.ZAxis);
                if (perp.Length < 1e-6) perp = Vector3d.CrossProduct(tangent, Vector3d.YAxis);
                perp.Unitize();
                perp2 = Vector3d.CrossProduct(tangent, perp);
                perp2.Unitize();
                return;
            }

            Vector3d z = Vector3d.ZAxis;
            Vector3d v = z - (z * tangent) * tangent;
            if (v.Length < 1e-8)
            {
                Vector3d x = Vector3d.XAxis;
                v = x - (x * tangent) * tangent;
                if (v.Length < 1e-8)
                    v = Vector3d.CrossProduct(tangent, Vector3d.YAxis);
            }
            v.Unitize();
            perp = v;
            perp2 = Vector3d.CrossProduct(tangent, perp);
            if (perp2.Length < 1e-8)
                perp2 = Vector3d.CrossProduct(perp, tangent);
            perp2.Unitize();
        }

        protected override Bitmap Icon => IconHelper.Create("V");
        public override Guid ComponentGuid => new Guid("d4e5f6a7-b8c9-4d0e-1f2a-3b4c5d6e7f8a");
    }
}
