using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    /// <summary>
    /// Geometrically nonlinear static solver (Newton-Raphson).
    /// Sits alongside ST_SolveLS - wire the same model into both to compare.
    /// </summary>
    public class ST_SolveNL : GH_Component
    {
        public ST_SolveNL()
          : base("Solve Nonlinear Static", "Solve NL",
              "Geometrically nonlinear static analysis (Total Lagrangian, Newton-Raphson). " +
              "Captures P-delta softening and buckling behaviour.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model", "Model", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Load Steps", "Steps",
                "Number of load increments. Increase near the limit load.", GH_ParamAccess.item, 10);
            pManager.AddIntegerParameter("Max Iterations", "MaxIt",
                "Maximum Newton iterations per load step.", GH_ParamAccess.item, 50);
            pManager.AddNumberParameter("Tolerance", "Tol",
                "Relative residual convergence tolerance.", GH_ParamAccess.item, 1e-9);

            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model", "Model", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Converged", "OK",
                "True if every load case converged.", GH_ParamAccess.item);
            pManager.AddTextParameter("Status", "Msg",
                "Solver status message.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Iterations", "It",
                "Newton iterations used, per load case.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Residual", "Res",
                "Final relative residual, per load case.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Axial Forces", "N",
                "Element axial forces for the first load case [N], tension positive.",
                GH_ParamAccess.list);

            pManager[0].Optional = true;
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            int steps = 10;
            int maxIt = 50;
            double tol = 1e-9;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            DA.GetData(1, ref steps);
            DA.GetData(2, ref maxIt);
            DA.GetData(3, ref tol);

            if (steps < 1) steps = 1;
            if (maxIt < 1) maxIt = 1;
            if (tol <= 0) tol = 1e-9;

            // --- solve ---
            TB_Model mdl = gh_mdl.Value;
            SolveNL slv = new SolveNL(ref mdl, steps, maxIt, tol);

            if (!slv.Converged)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, slv.Message);

            List<double> axial = new List<double>();
            if (slv.AxialForces.Count > 0 && slv.AxialForces[0] != null)
                axial.AddRange(slv.AxialForces[0]);

            // --- output ---
            DA.SetData(0, new GH_TB_Model(slv.Mdl));
            DA.SetData(1, slv.Converged);
            DA.SetData(2, slv.Message);
            DA.SetDataList(3, slv.Iterations);
            DA.SetDataList(4, slv.FinalResidual);
            DA.SetDataList(5, axial);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("\u2207");

        public override Guid ComponentGuid => new Guid("7f3c9e21-4b6a-42d8-9c17-5e8a2d6b1f04");
    }
}
