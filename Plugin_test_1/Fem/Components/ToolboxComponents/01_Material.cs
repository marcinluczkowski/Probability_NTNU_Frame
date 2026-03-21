using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
// using Karamba.Properties;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components.ToolboxComponents

{
    public class ST_Material : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the _01_Material class.
        /// </summary>
        public ST_Material()
          : base("Material", "Mat",
              "Material Properties",
              Common.category, Common.sub_mat)
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("tag", "tag", "tag", GH_ParamAccess.item);
            pManager.AddNumberParameter("E", "E", "Modulus of Elasticity [MPa]=[N/mm2]", GH_ParamAccess.item);
            pManager.AddNumberParameter("G", "G", "Shear Modulus [MPa]=[N/mm2]", GH_ParamAccess.item);
            pManager.AddNumberParameter("Gamma", "Gamma", "Unit Weight [kN/m3]", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha", "Alpha", "Coefficient of linear thermal expansion [K^(-1)]", GH_ParamAccess.item);
            pManager.AddNumberParameter("Fy", "Fy", "Yield stringth [MPa]=[N/mm2]", GH_ParamAccess.item);

            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.RegisterParam(new Param_Material(), "Material", "Mat", "Material", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            string tag = null;
            double E = new double();
            double G = new double();
            double gamma = new double();
            double alpha = new double();
            double Fy = new double();

            // --- input --- 
            if (!DA.GetData(0, ref tag)) { return; }
            if (!DA.GetData(1, ref E)) { return; }
            if (!DA.GetData(2, ref G)) { return; }
            if (!DA.GetData(3, ref gamma)) { return; }
            if (!DA.GetData(4, ref alpha)) { return; }
            if (!DA.GetData(5, ref Fy)) { return; }

            // --- solve ---
            GH_Material gh_mat = new GH_Material(new TB_Material(tag, E, G, gamma, alpha, Fy));

            // --- output ---
            DA.SetData(0, gh_mat);

        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon => IconHelper.Create("E");

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("a21b128d-6362-41a7-b588-55853e3cb170"); }
        }
    }
}