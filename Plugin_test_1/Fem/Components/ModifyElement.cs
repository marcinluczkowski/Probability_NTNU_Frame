using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components
{
    /// <summary>
    /// Changes properties of elements before they are assembled, as Karamba3D's Modify Element does.
    ///
    /// Hinges = true makes an element a truss: hinged at both ends, it carries axial force only
    /// (stiffness EA/L along its axis; no bending, shear or torsion). This is Karamba3D's
    /// Bending = false. At a node where only trusses meet nothing resists the node's rotations, so
    /// they are removed from the analysis (Karamba3D does the same); every such node still needs
    /// members or supports that hold it in all three directions. Hinges = false makes it a beam
    /// rigidly connected at both ends again. An input that is not connected leaves that property
    /// as it is.
    ///
    /// The elements are copied: the elements coming in are not changed.
    /// </summary>
    public class ModifyElement : GH_Component
    {
        public ModifyElement()
          : base("Modify Element", "ModElem",
              "Modify elements before assembly, as Karamba3D's Modify Element. Hinges = true: truss element, hinged " +
              "at both ends, axial force only (Karamba3D: Bending = false).",
              Common.category, Common.sub_elem)
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Element1D(), "Element", "Elem",
                "Element to modify (from Line to Beam).", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Hinges", "Hinges",
                "True: hinged at both ends, a truss element that carries axial force only (Karamba3D: Bending = false). " +
                "False: rigidly connected at both ends (a beam). Not connected: left as it is.", GH_ParamAccess.item);
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_Element1D(), "Element", "Elem", "Modified element.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Element_1D ghElem = null;
            if (!DA.GetData(0, ref ghElem) || ghElem?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "An element is required.");
                return;
            }

            // a copy, so that the element upstream keeps its settings
            TB_Element_1D e = ghElem.Value.DeepCopy();

            bool hinges = false;
            if (DA.GetData(1, ref hinges) && hinges != e.Hinges)
                e.SetHinges(hinges);

            Message = e.Hinges ? "Hinges (truss)" : "rigid";
            DA.SetData(0, new GH_Element_1D(e));
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("H");
        public override Guid ComponentGuid => new Guid("a0945380-48a6-4811-9088-28ed39128848");
    }
}
