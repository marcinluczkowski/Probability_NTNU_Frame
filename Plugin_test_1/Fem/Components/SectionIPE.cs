// =============================================================================
//  SectionIPE.cs
//
//  WHAT IT IS
//  The Grasshopper component "Section IPE". Same as SectionHEA.cs, for the IPE
//  catalog (IPE_Catalog.cs): pick an IPE profile and get a Section for the FE model.
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
    /// <summary>Select IPE from catalog. Right-click to pick the profile, or supply its name.</summary>
    public class SectionIPE : GH_Component
    {
        private string _name = "IPE200";

        public SectionIPE()
          : base("Section IPE", "Sec IPE",
              "Select IPE from catalog (EU, properties as in the Karamba3D cross-section table). " +
              "Right-click to choose the profile, or supply its name.",
              Common.category, Common.sub_sec)
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Material(), "Material", "Mat", "Material (optional; default S355)", GH_ParamAccess.item);
            pManager.AddTextParameter("Name", "Name", "Profile name, e.g. IPE200 (optional; overrides the right-click selection)", GH_ParamAccess.item);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_Section(), "Section", "Sec", "IPE section for FEM", GH_ParamAccess.item);
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
            SectionRecord rec = IPE_Catalog.Find(hasName ? nameIn : _name);
            if (rec == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Profile '{nameIn}' is not in the IPE catalog. Available: {string.Join(", ", IPE_Catalog.AllNames())}");
                return;
            }

            TB_Material mat = ghMat?.Value ?? new TB_Material("S355", 210000, 80769, 78.5, 1.2e-5, 355);
            var sec = new Section_I(mat, rec);

            // --- output ---
            DA.SetData(0, new GH_Section(sec));
            DA.SetDataList(1, sec.Curves);
            DA.SetData(2, $"{sec.Tag}\nA = {sec.Area:F1} mm\u00b2\nIy = {sec.Iy:F0} mm\u2074\nIz = {sec.Iz:F0} mm\u2074\n" +
                          $"Wy = {sec.Wy:F1} mm\u00b3\nWz = {sec.Wz:F1} mm\u00b3\n" +
                          $"iy = {sec.iy:F1} mm\niz = {sec.iz:F1} mm\n" +
                          $"alpha_y = {sec.AlphaY}\nalpha_z = {sec.AlphaZ}\nalpha_LT = {sec.AlphaLT}");

            Message = sec.Tag;
        }

        private void ValidateSelection()
        {
            if (IPE_Catalog.Entries.Length == 0) return;
            if (IPE_Catalog.Find(_name) == null)
                _name = IPE_Catalog.Entries[0].Name;
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            var profileMenu = new ToolStripMenuItem("Select Profile");
            foreach (string name in IPE_Catalog.AllNames())
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
            writer.SetString("IPE_Name", _name ?? "");
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            if (reader.ItemExists("IPE_Name")) _name = reader.GetString("IPE_Name");
            return base.Read(reader);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("I");
        public override Guid ComponentGuid => new Guid("4b6a5d96-8a89-499e-9258-d9ffc0b0c660");
    }
}
