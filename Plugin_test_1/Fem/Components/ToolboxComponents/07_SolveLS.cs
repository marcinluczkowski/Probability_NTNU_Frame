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
            pManager.AddNumberParameter("Utilization", "U", "Maximum structural utilization from linear results", GH_ParamAccess.item);

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
            double utilization = EvaluateMaxUtilization(slv.Mdl);

            // --- output ---

            DA.SetData(0, new GH_TB_Model(slv.Mdl));
            DA.SetData(1, utilization);
        }

        private static double EvaluateMaxUtilization(TB_Model model)
        {
            double maxU = 0.0;
            int nLc = model?.Disps?.Count ?? 0;
            if (model?.Elem1Ds == null || nLc == 0) return maxU;

            for (int lcId = 0; lcId < nLc; lcId++)
            {
                foreach (var e in model.Elem1Ds)
                {
                    if (e?.Sec?.Mat == null || e.Nodes == null || e.Nodes.Count < 2) continue;
                    if (e.Nodes[0].Disps == null || e.Nodes[1].Disps == null) continue;
                    if (e.Nodes[0].Disps.Count <= lcId || e.Nodes[1].Disps.Count <= lcId) continue;

                    double fy = e.Sec.Mat.Fy;
                    if (fy <= 0) continue;

                    var f = e.Calc_Forces(lcId);
                    double nEd = Math.Max(Math.Abs(f[0]), Math.Abs(f[6]));
                    double myEd = Math.Max(Math.Abs(f[4]), Math.Abs(f[10]));
                    double mzEd = Math.Max(Math.Abs(f[5]), Math.Abs(f[11]));

                    double nRd = fy * e.Sec.Area;
                    double myRd = fy * e.Sec.Wy / 1000.0;
                    double mzRd = fy * e.Sec.Wz / 1000.0;
                    if (nRd <= 0 || myRd <= 0 || mzRd <= 0) continue;

                    double u = nEd / nRd + myEd / myRd + mzEd / mzRd;
                    if (u > maxU) maxU = u;
                }
            }

            return maxU;
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