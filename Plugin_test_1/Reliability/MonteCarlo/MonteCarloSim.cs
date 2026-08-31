using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Linq;

namespace Plugin_test_1.Reliability.MonteCralo
{
    public class MonteCarloSim : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the MonteCarloSim class.
        /// </summary>
        public MonteCarloSim()
          : base("MonteCarloSim", "Monte Carlo Simulation",
              "Description",
              "Structural", "Reliability")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Characteristic Strength", "Rk", "Characteristic yield strength [MPa] (e.g., 355 for S355)", GH_ParamAccess.item, 355.0);
            pManager.AddNumberParameter("Characteristic permanent UDL", "Gk", "Characteristic permanent distributed load [kN/m]", GH_ParamAccess.item, 10.0);
            pManager.AddNumberParameter("Characteristic variable load", "Qk", "Characteristic point variable load [kN]", GH_ParamAccess.item, 20.0);
            pManager.AddNumberParameter("Section Modulus", "Wel", "Elastic section modulus [mm³] (e.g., 200000 for IPE 200)", GH_ParamAccess.item, 200000.0);
            pManager.AddNumberParameter("Span", "L", "Beam span [m]", GH_ParamAccess.item, 5.0);
            pManager.AddNumberParameter("Load position", "a", "Point load position from left support [m]", GH_ParamAccess.item, 2.5);
            pManager.AddNumberParameter("Seed", "Seed", "Random seed for reproducibility", GH_ParamAccess.item, 42.0);
            pManager.AddNumberParameter("Number of Simulations", "N", "Number of Monte Carlo simulations to run", GH_ParamAccess.item, 100000.0);
            pManager.AddBooleanParameter("Importance Sampling", "IS", "Whether to use importance sampling (not yet implemented)", GH_ParamAccess.item, false);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Estimated Pf", "Pf", "Estimated probability of failure from Monte Carlo simulation", GH_ParamAccess.item);
            pManager.AddNumberParameter("Estimated Beta", "Beta", "Estimated reliability index from Monte Carlo simulation", GH_ParamAccess.item);
            pManager.AddNumberParameter("Failure Count", "NFail", "Number of failure samples (diagnostic)", GH_ParamAccess.item);
            pManager.AddNumberParameter("Total Samples", "N", "Total number of samples run (diagnostic)", GH_ParamAccess.item);
        }
        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double fyk = 355.0, Gk = 10.0, Qk = 20.0;

            DA.GetData(0, ref fyk);
            DA.GetData(1, ref Gk);
            DA.GetData(2, ref Qk);

            // Build random variables using Eurocode calibration
            var rvs = StochasticVariable.BuildEurocodeRVs(fyk, Gk, Qk);

            double Wel = 200000.0, L = 5.0, a = L / 2.0;
            double seedDouble = 42, NDouble = 100000;

            DA.GetData(3, ref Wel);
            DA.GetData(4, ref L);
            DA.GetData(5, ref a);
            DA.GetData(6, ref seedDouble);
            DA.GetData(7, ref NDouble);

            int seed = (int)seedDouble;
            int N = (int)NDouble;

            bool useIS = false;
            DA.GetData(8, ref useIS);

            // Choose a method
            Dictionary<string, object> results;
            if (useIS)
            {
                double beta_FORM;
                bool converged;

                // Use the FORM analysis from Reliability_TryOuts namespace
                Reliability_TryOuts.FORM.Run(Wel, L, a, rvs.Cast<StochasticVariable>().ToArray(), out beta_FORM, out converged);

                // Approximate design point for importance sampling (this is a very crude approximation - ideally should be the actual design point from FORM)
                double[] designPoint = rvs.Select(rv => rv.Mean).ToArray();

                results = MonteCarloCalcs.RunImportanceSampling(Wel, L, a, rvs, designPoint, stdShift: 1.5, N: N, seed: seed);
            }
             else
            // Crude Monte Carlo Simulation
            {
                results = MonteCarloCalcs.RunCrudeMonteCarlo(Wel, L, a, rvs, N, seed);

            }

            // Clamp load position strictly inside span to avoid singularities
            a = Math.Max(0.001 * L, Math.Min(0.999 * L, a));

            // Extract results
            double Pf = (double)results["Pf"];
            double beta = (double)results["Beta"];
            int nFail = (int)(double)results["N_Fail"];

            DA.SetData(0, Pf);
            DA.SetData(1, beta);
            DA.SetData(2, nFail);
            DA.SetData(3, N);

        }

        /// <summary>
        /// Provides an Icon for the component.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                //You can add image files to your project resources and access them like this:
                // return Resources.IconForThisComponent;
                return null;
            }
        }

        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("457400D5-40B8-4E85-9B28-AA49306BDDFD"); }
        }
    }
}