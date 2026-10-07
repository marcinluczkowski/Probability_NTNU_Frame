// =============================================================================
//  SectionHEA.cs
//
//  WHAT IT IS
//  The Grasshopper component "Section HEA". Counterpart of SectionRHS.cs: pick an
//  HEA profile from the catalog and get a Section for the FE model.
//
//  WHY IT MATTERS
//  This is the point where the design variable enters the model. Changing the
//  profile here changes the section of every element it is wired to, and with it
//  the reliability index. The "Name" input exists for that reason: it lets a
//  value list, a slider-driven list item, or later the optimiser choose the
//  profile, instead of only the right-click menu.
// =============================================================================

using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.Linq;
using System.Windows.Forms;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components
{
    /// <summary>Select HEA from catalog. Right-click to pick the profile, or supply its name.</summary>
    public class SectionHEA : GH_Component
    {
        private string _name = "HEA200";

        public SectionHEA()
          : base("Section HEA", "Sec HEA",
              "Select HEA from catalog (EU, properties as in the Karamba3D cross-section table). " +
              "Right-click to choose the profile, or supply its name.",
              Common.category, Common.sub_sec)
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Material(), "Material", "Mat", "Material (optional; default S355)", GH_ParamAccess.item);
            pManager.AddTextParameter("Name", "Name", "Profile name, e.g. HEA200 (optional; overrides the right-click selection)", GH_ParamAccess.item);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_Section(), "Section", "Sec", "HEA section for FEM", GH_ParamAccess.item);
            pManager.AddCurveParameter("Curves", "Crvs", "Section outline", GH_ParamAccess.list);
            pManager.AddTextParameter("Info", "Info", "Section summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_Material ghMat = null;
            string nameIn = null;

            // --- input ---
            DA.GetData(0, ref ghMat);
            bool hasName = DA.GetData(1, ref nameIn) && !string.IsNullOrWhiteSpace(nameIn);

            // --- solve ---
            ValidateSelection();
            SectionRecord rec = HEA_Catalog.Find(hasName ? nameIn : _name);
            if (rec == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Profile '{nameIn}' is not in the HEA catalog. Available: {string.Join(", ", HEA_Catalog.AllNames())}");
                return;
            }

            TB_Material mat = ghMat?.Value ?? new TB_Material("S355", 210000, 80769, 78.5, 1.2e-5, 355);
            var sec = new Section_I(mat, rec);

            // --- output ---
            DA.SetData(0, new GH_Section(sec));
            DA.SetDataList(1, sec.Curves);
            DA.SetData(2, $"{sec.Tag}\nA = {sec.Area:F1} mm²\nIy = {sec.Iy:F0} mm⁴\nIz = {sec.Iz:F0} mm⁴\n" +
                          $"Wy = {sec.Wy:F1} mm³\nWz = {sec.Wz:F1} mm³\n" +
                          $"iy = {sec.iy:F1} mm\niz = {sec.iz:F1} mm\n" +
                          $"alpha_y = {sec.AlphaY}\nalpha_z = {sec.AlphaZ}");

            Message = sec.Tag;
        }

        private void ValidateSelection()
        {
            if (HEA_Catalog.Entries.Length == 0) return;
            if (HEA_Catalog.Find(_name) == null)
                _name = HEA_Catalog.Entries[0].Name;
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            var profileMenu = new ToolStripMenuItem("Select Profile");
            foreach (string name in HEA_Catalog.AllNames())
            {
                string n = name;
                var item = new ToolStripMenuItem(n, null,
                    (s, e) => { _name = n; ExpireSolution(true); })
                { Checked = n == _name };
                profileMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(profileMenu);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("HEA_Name", _name ?? "");
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            if (reader.ItemExists("HEA_Name")) _name = reader.GetString("HEA_Name");
            return base.Read(reader);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("H");
        public override Guid ComponentGuid => new Guid("a6028f46-b279-4c35-8b7d-cbf460df1f6a");
    }
}
