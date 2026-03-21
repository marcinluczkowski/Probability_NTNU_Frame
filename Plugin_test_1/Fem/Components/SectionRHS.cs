using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.Linq;
using System.Windows.Forms;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components
{
    /// <summary>Select RHS from catalog. Right-click to pick size and thickness.</summary>
    public class SectionRHS : GH_Component
    {
        private int _h = 100, _b = 60, _t = 5;

        public SectionRHS()
          : base("Section RHS", "Sec RHS",
              "Select RHS from catalog (hot-finished EN 10210-2). Right-click to choose H×B and thickness.",
              Common.category, Common.sub_sec)
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Material(), "Material", "Mat", "Material (optional; default S355)", GH_ParamAccess.item);
            pManager[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_Section(), "Section", "Sec", "RHS section for FEM", GH_ParamAccess.item);
            pManager.AddCurveParameter("Curves", "Crvs", "Section outline", GH_ParamAccess.list);
            pManager.AddTextParameter("Info", "Info", "Section summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ValidateSelection();
            GH_Material ghMat = null;
            DA.GetData(0, ref ghMat);

            TB_Material mat = ghMat?.Value ?? new TB_Material("S355", 210000, 80769, 78.5, 1.2e-5, 355);

            string tag = $"RHS_{_h}x{_b}x{_t}";
            var sec = new Section_RHS(mat, tag, _h, _b, _t, _t);

            DA.SetData(0, new GH_Section(sec));
            DA.SetDataList(1, sec.Curves);
            DA.SetData(2, $"{tag}\nA = {sec.Area:F1} mm²\nIy = {sec.Iy:F0} mm⁴\nWy = {sec.Wy:F1} mm³");

            Message = $"{_h}×{_b}×{_t}";
        }

        private void ValidateSelection()
        {
            var sizes = RHS_Catalog.AllSizes();
            if (sizes.Count == 0) return;
            if (sizes.All(s => s.H != _h || s.B != _b))
            {
                var first = sizes[0];
                _h = first.H; _b = first.B;
            }
            var thicks = RHS_Catalog.ThicknessesFor(_h, _b);
            if (thicks != null && thicks.Length > 0 && Array.IndexOf(thicks, _t) < 0)
                _t = thicks[0];
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            var sizeMenu = new ToolStripMenuItem("Select Size H×B (mm)");
            foreach (var (H, B) in RHS_Catalog.AllSizes())
            {
                int h = H, b = B;
                var item = new ToolStripMenuItem($"{h} × {b}", null,
                    (s, e) => { _h = h; _b = b; ValidateSelection(); ExpireSolution(true); })
                { Checked = h == _h && b == _b };
                sizeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(sizeMenu);

            var thickMenu = new ToolStripMenuItem("Select Thickness (mm)");
            foreach (int t in RHS_Catalog.ThicknessesFor(_h, _b))
            {
                int tt = t;
                var item = new ToolStripMenuItem($"{tt} mm", null,
                    (s, e) => { _t = tt; ExpireSolution(true); })
                { Checked = tt == _t };
                thickMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(thickMenu);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32("RHS_H", _h);
            writer.SetInt32("RHS_B", _b);
            writer.SetInt32("RHS_T", _t);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            if (reader.ItemExists("RHS_H")) _h = reader.GetInt32("RHS_H");
            if (reader.ItemExists("RHS_B")) _b = reader.GetInt32("RHS_B");
            if (reader.ItemExists("RHS_T")) _t = reader.GetInt32("RHS_T");
            return base.Read(reader);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("R");
        public override Guid ComponentGuid => new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d");
    }
}
