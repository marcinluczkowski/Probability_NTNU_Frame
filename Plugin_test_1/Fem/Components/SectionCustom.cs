using System;
using Grasshopper.Kernel;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components
{
    /// <summary>Define cross-section by geometric properties: Area, Iy, Iz, J [mm², mm⁴].</summary>
    public class SectionCustom : GH_Component
    {
        public SectionCustom()
          : base("Section Custom", "Sec Custom",
              "Define section by Area, Iy, Iz, J. All in [mm²] and [mm⁴]. Wy, Wz optional.",
              Common.category, Common.sub_sec)
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Material(), "Material", "Mat", "Material", GH_ParamAccess.item);
            pManager.AddTextParameter("Tag", "Tag", "Section name", GH_ParamAccess.item, "Custom");
            pManager.AddNumberParameter("Area", "A", "Cross-sectional area [mm²]", GH_ParamAccess.item);
            pManager.AddNumberParameter("Iy", "Iy", "Second moment about y [mm⁴]", GH_ParamAccess.item);
            pManager.AddNumberParameter("Iz", "Iz", "Second moment about z [mm⁴]", GH_ParamAccess.item);
            pManager.AddNumberParameter("J", "J", "Torsional constant [mm⁴]", GH_ParamAccess.item);
            pManager.AddNumberParameter("Wy", "Wy", "Elastic modulus Wy [mm³] (optional)", GH_ParamAccess.item);
            pManager.AddNumberParameter("Wz", "Wz", "Elastic modulus Wz [mm³] (optional)", GH_ParamAccess.item);
            pManager[6].Optional = true;
            pManager[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_Section(), "Section", "Sec", "Section for FEM", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Material ghMat = null;
            string tag = "Custom";
            double area = 0, iy = 0, iz = 0, j = 0, wy = 0, wz = 0;

            if (!DA.GetData(0, ref ghMat) || ghMat?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Material required.");
                return;
            }
            DA.GetData(1, ref tag);
            if (!DA.GetData(2, ref area) || !DA.GetData(3, ref iy) || !DA.GetData(4, ref iz) || !DA.GetData(5, ref j))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Area, Iy, Iz, J required.");
                return;
            }
            DA.GetData(6, ref wy);
            DA.GetData(7, ref wz);

            if (area <= 0 || iy <= 0 || iz <= 0 || j <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Area, Iy, Iz, J must be > 0.");
                return;
            }

            var sec = new Section_Custom(ghMat.Value, tag ?? "Custom", area, iy, iz, j, wy, wz);
            DA.SetData(0, new GH_Section(sec));
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("I");
        public override Guid ComponentGuid => new Guid("b2c3d4e5-f6a7-4b5c-9d0e-1f2a3b4c5d6e");
    }
}
