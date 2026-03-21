using System;
using System.Collections.Generic;
using System.Linq;

using Grasshopper.Kernel;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    public class ST_Assembly : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the _06_Assembly class.
        /// </summary>
        public ST_Assembly()
          : base("Assembly", "Assembly",
              "Assemble beam elements, supports and loads into FEM model.",
              Common.category, Common.sub_assem)
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_Element1D(), "Elements", "Elems", "Beam elements (from Line to Beam)", GH_ParamAccess.list);
            pManager.AddParameter(new Param_Support(), "Supports", "Sups", "Support conditions at nodes", GH_ParamAccess.list);
            pManager.AddParameter(new Param_Load(), "Loads", "Loads", "Point loads [kN]", GH_ParamAccess.list);

            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.RegisterParam(new Param_TB_Model(), "Model", "Model", "Model", GH_ParamAccess.item);
            pManager.AddNumberParameter("Weight", "Weight", "Weight in [kg]", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ClearRuntimeMessages();

            // --- variables ---
            List<GH_Element_1D> gh_elem1Ds = new List<GH_Element_1D>();
            List<GH_Support> gh_sups = new List<GH_Support>();
            List<GH_Load> gh_loads = new List<GH_Load>();

            // --- input --- 
            if (!DA.GetDataList(0, gh_elem1Ds)) { return; }
            if (!DA.GetDataList(1, gh_sups)) { return; }
            if (!DA.GetDataList(2, gh_loads)) { return; }

            List<TB_Element_1D> elem1Ds = gh_elem1Ds.Where(x => x?.Value != null).Select(x => x.Value).ToList();
            List<TB_Support> sups = gh_sups.Where(x => x?.Value != null).Select(x => x.Value).ToList();
            List<TB_Load> loads = gh_loads.Where(x => x?.Value != null).Select(x => x.Value).ToList();

            // --- solve ---
            GH_TB_Model gh_mdl = new GH_TB_Model(new TB_Model(elem1Ds, sups, loads));

            if (gh_mdl.Value.Validity[0] == false)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Check Support position.");
            if (gh_mdl.Value.Validity[1] == false)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Load position must be at a beam node (endpoint). Connect beam line endpoints or ensure points match line ends.");

            // --- output ---
            DA.SetData(0, gh_mdl);
            DA.SetData(1, gh_mdl.Value.Weight);
        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon => IconHelper.Create("A");

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("14cd3362-59e2-4929-bc03-5f6e8328acad"); }
        }
    }
}