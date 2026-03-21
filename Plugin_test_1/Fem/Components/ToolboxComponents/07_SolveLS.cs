using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    public class ST_SolveLS : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the _07_AnalyseLS class.
        /// </summary>
        public ST_SolveLS()
          : base("Solve Linear Static", "Solve LS",
              "Solve Linear Static",
              Common.category, Common.sub_analize)
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model", "Model", GH_ParamAccess.item);

            pManager[0].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model", "Model", GH_ParamAccess.item);

            pManager[0].Optional = true;
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;

            // --- input --- 
            if (!DA.GetData(0, ref gh_mdl)) { return; }

            // --- solve ---

            TB_Model mdl = gh_mdl.Value;
            SolveLS slv = new SolveLS(ref mdl);

            // --- output ---

            DA.SetData(0, new GH_TB_Model(slv.Mdl));
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon => IconHelper.Create("\u03A3");

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("febacd27-51c7-4c77-9dfe-fc3ef20c8075"); }
        }
    }
}