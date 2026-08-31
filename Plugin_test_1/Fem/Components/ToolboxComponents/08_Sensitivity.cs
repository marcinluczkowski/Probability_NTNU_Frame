using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Grasshopper.Kernel;
using Rhino.Geometry;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    /// <summary>
    /// Computes d(response)/d(parameter) by the Direct Differentiation Method
    /// (adjoint form), optionally alongside central finite differences so the
    /// two can be compared for accuracy and speed.
    /// </summary>
    public class ST_Sensitivity : GH_Component
    {
        public ST_Sensitivity()
          : base("Nonlinear Sensitivity (DDM)", "DDM",
              "Analytical gradient of a nodal response with respect to moduli, loads and " +
              "nodal coordinates, computed by the adjoint direct differentiation method. " +
              "Optionally compares against central finite differences.",
              Common.category, Common.sub_analize)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Model ALREADY SOLVED by Solve NL.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Response Node", "rNode",
                "Node Id of the response DOF.", GH_ParamAccess.item, 9);
            pManager.AddIntegerParameter("Response DOF", "rDof",
                "0=ux 1=uy 2=uz 3=rx 4=ry 5=rz", GH_ParamAccess.item, 0);

            pManager.AddTextParameter("Modulus Tags", "ETags",
                "Section tags whose E is a parameter (one per tag).", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Coord Nodes", "cNodes",
                "Node Ids whose original coordinate is a parameter.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Coord Component", "cComp",
                "0=X 1=Y 2=Z for the coordinate parameters.", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Load Nodes", "lNodes",
                "Node Ids whose point load is a parameter.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Load DOFs", "lDofs",
                "Matching DOF index for each load parameter.", GH_ParamAccess.list);

            pManager.AddBooleanParameter("Compare FD", "FD",
                "Also compute central finite differences (SLOW) and report the difference.",
                GH_ParamAccess.item, false);
            pManager.AddNumberParameter("FD Step", "h",
                "Relative finite-difference step.", GH_ParamAccess.item, 1e-6);

            for (int i = 1; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Names", "Name", "Parameter names.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Gradient DDM", "dDDM",
                "Analytical gradient d(response)/d(parameter).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Gradient FD", "dFD",
                "Finite-difference gradient (empty unless Compare FD is true).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Relative Error", "Err",
                "|DDM-FD|/|FD| per parameter.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Response", "R",
                "Response value at the converged state.", GH_ParamAccess.item);
            pManager.AddTextParameter("Report", "Rep",
                "Timing and worst-case error summary.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- variables ---
            GH_TB_Model gh_mdl = null;
            int rNode = 9, rDof = 0, cComp = 0;
            List<string> eTags = new List<string>();
            List<int> cNodes = new List<int>();
            List<int> lNodes = new List<int>();
            List<int> lDofs = new List<int>();
            bool doFd = false;
            double h = 1e-6;

            // --- input ---
            if (!DA.GetData(0, ref gh_mdl)) { return; }
            DA.GetData(1, ref rNode);
            DA.GetData(2, ref rDof);
            DA.GetDataList(3, eTags);
            DA.GetDataList(4, cNodes);
            DA.GetData(5, ref cComp);
            DA.GetDataList(6, lNodes);
            DA.GetDataList(7, lDofs);
            DA.GetData(8, ref doFd);
            DA.GetData(9, ref h);

            TB_Model mdl = gh_mdl.Value;

            if (mdl.Disps == null || mdl.Disps.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Model has no displacements. Run Solve NL first.");
                return;
            }

            // --- build parameter list (order: coords, moduli, loads) ---
            List<NL_Parameter> pars = new List<NL_Parameter>();
            foreach (int n in cNodes)
                pars.Add(NL_Parameter.ForCoordinate($"x{n}", n, cComp));
            foreach (string t in eTags)
                pars.Add(NL_Parameter.ForModulus($"E[{t}]", t));
            for (int i = 0; i < lNodes.Count; i++)
            {
                int dof = (i < lDofs.Count) ? lDofs[i] : 0;
                pars.Add(NL_Parameter.ForLoad($"F{lNodes[i]}_{dof}", lNodes[i], dof));
            }

            if (pars.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No parameters specified.");
                return;
            }

            NL_Response resp = new NL_Response(rNode, rDof);
            double[] u = mdl.Disps[0];

            // --- DDM ---
            Stopwatch sw = Stopwatch.StartNew();
            NL_DDM ddm = new NL_DDM(mdl, u, resp, pars);
            sw.Stop();
            double tDdm = sw.Elapsed.TotalMilliseconds;

            if (!ddm.Ok)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ddm.Message);
                return;
            }

            // --- optional FD ---
            List<double> fd = new List<double>();
            List<double> err = new List<double>();
            double tFd = 0.0;
            double worst = 0.0;

            if (doFd)
            {
                try
                {
                    sw.Restart();
                    double[] g = NL_FiniteDiff.Gradient(mdl, resp, pars, h);
                    sw.Stop();
                    tFd = sw.Elapsed.TotalMilliseconds;

                    double scale = ddm.Gradient.Select(Math.Abs).DefaultIfEmpty(0).Max();
                    for (int i = 0; i < g.Length; i++)
                    {
                        fd.Add(g[i]);
                        double e = Math.Abs(ddm.Gradient[i] - g[i]) /
                                   Math.Max(Math.Abs(g[i]), 1e-30);
                        err.Add(e);
                        if (Math.Abs(g[i]) > 1e-8 * scale) worst = Math.Max(worst, e);
                    }
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "FD comparison failed: " + ex.Message);
                }
            }

            string report = $"parameters: {pars.Count}\nDDM: {tDdm:F1} ms";
            if (doFd && fd.Count > 0)
                report += $"\nFD : {tFd:F1} ms  ({tFd / Math.Max(tDdm, 1e-9):F1}x slower)" +
                          $"\nworst relative error: {worst:E2}";

            // --- output ---
            DA.SetDataList(0, pars.Select(p => p.Name).ToList());
            DA.SetDataList(1, ddm.Gradient);
            DA.SetDataList(2, fd);
            DA.SetDataList(3, err);
            DA.SetData(4, ddm.ResponseValue);
            DA.SetData(5, report);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("\u2202");

        public override Guid ComponentGuid => new Guid("b28d4f16-9c03-4a75-8e61-3d7f52a9c8b1");
    }
}
