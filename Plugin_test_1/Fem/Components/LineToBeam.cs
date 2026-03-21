using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components
{
    /// <summary>Create beam elements from lines. One section applied to all lines.</summary>
    public class LineToBeam : GH_Component
    {
        public LineToBeam()
          : base("Line to Beam", "LnToBeam",
              "Create beam elements from lines with given cross-section.",
              Common.category, Common.sub_elem)
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddLineParameter("Lines", "Lns", "Lines (each becomes one beam element)", GH_ParamAccess.list);
            pManager.AddParameter(new Param_Section(), "Section", "Sec", "Cross-section", GH_ParamAccess.item);
            pManager.AddTextParameter("Tag", "Tag", "Element tag (optional)", GH_ParamAccess.item);
            pManager.AddVectorParameter("Z-direction", "Z", "Local z-axis per element (optional; auto if empty)", GH_ParamAccess.list);
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_Element1D(), "Elements", "Elems", "Beam elements", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var lines = new List<Line>();
            GH_Section ghSec = null;
            string tag = "beam";
            var zs = new List<Vector3d>();

            if (!DA.GetDataList(0, lines) || !DA.GetData(1, ref ghSec) || ghSec?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Lines and Section required.");
                return;
            }
            DA.GetData(2, ref tag);
            DA.GetDataList(3, zs);

            var elems = new List<GH_Element_1D>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                var vz = (i < zs.Count) ? zs[i] : (zs.Count > 0 ? zs[zs.Count - 1] : new Vector3d(0, 0, 0));
                var bucklen = lines[i].Length;
                var e = new TB_Element_1D(lines[i], tag ?? "beam", ghSec.Value, vz, bucklen);
                elems.Add(new GH_Element_1D(e));
            }
            DA.SetDataList(0, elems);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("L");
        public override Guid ComponentGuid => new Guid("c3d4e5f6-a7b8-4c5d-0e1f-2a3b4c5d6e7f");
    }
}
