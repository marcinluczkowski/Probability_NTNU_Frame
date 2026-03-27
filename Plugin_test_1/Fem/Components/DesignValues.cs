using System;

using Grasshopper.Kernel;
using Plugin_test_1.Reliability;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Plugin_test_1.Fem.Components
{
    public class DesignValues : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the DesignValues class.
        /// </summary>
        public DesignValues()
          : base("DesignValues", "DV",
              "Calculate the design values based on alpha values",
              Common.category, Common.sub_load)
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Material", "Mat", "Material name (currently mapped to lognormal)", GH_ParamAccess.item);
            pManager.AddNumberParameter("Characteristic Strength", "Rk", "Characteristic resistance/strength value", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha Resistance", "AlphaR", "Alpha value for resistance side", GH_ParamAccess.item);
            pManager.AddTextParameter("Load Type", "LoadType", "Load type: variable -> gumbel, permanent/other -> normal", GH_ParamAccess.item);
            pManager.AddNumberParameter("Characteristic Load", "Sk", "Characteristic load effect value", GH_ParamAccess.item);
            pManager.AddNumberParameter("Alpha Load", "AlphaS", "Alpha value for load side", GH_ParamAccess.item);
            pManager.AddNumberParameter("Target Beta", "Beta", "Target reliability index", GH_ParamAccess.item, 3.8);

        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            // Output the calculated design value for the load and resistance to be later used in FEM analysis (e.g., design load)
            pManager.AddNumberParameter("Design Value Resistance", "DVResistance", "Calculated design value for the load effect", GH_ParamAccess.item);
            pManager.AddNumberParameter("Design Value Load", "DVLoad", "Calculated design value for the load effect", GH_ParamAccess.item);
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object is used to retrieve from inputs and store in outputs.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string material = "Steel";
            double characteristicStrength = 355.0;
            double alphaResistance = 0.0;
            string loadType = string.Empty;
            double characteristicLoad = 0.0;
            double alphaLoad = 0.0;
            double targetBeta = 3.8;

            if (!DA.GetData(0, ref material)) return;
            if (!DA.GetData(1, ref characteristicStrength)) return;
            if (!DA.GetData(2, ref alphaResistance)) return;
            if (!DA.GetData(3, ref loadType)) return;
            if (!DA.GetData(4, ref characteristicLoad)) return;
            if (!DA.GetData(5, ref alphaLoad)) return;
            if (!DA.GetData(6, ref targetBeta)) return;

            if (characteristicStrength <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Characteristic strength must be > 0.");
                return;
            }

            if (characteristicLoad <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Characteristic load must be > 0.");
                return;
            }

            try
            {
                bool knownMaterial = StochasticInputCatalog.HasMaterial(material);
                bool knownLoadType = StochasticInputCatalog.HasLoadType(loadType);

                var materialInput = StochasticInputCatalog.GetMaterialInput(material);
                var loadInput = StochasticInputCatalog.GetLoadInput(loadType);

                if (!knownMaterial)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Unknown material in stochastic catalog. Using default material COV/percentile.");

                if (!knownLoadType)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Unknown load type in stochastic catalog. Using default load COV/percentile.");

                var resistanceRv = new RandomVariable(
                    string.IsNullOrWhiteSpace(material) ? "material" : material,
                    characteristicStrength,
                    materialInput.Percentile,
                    materialInput.COV,
                    materialInput.DistributionType);

                var loadRv = new RandomVariable(
                    string.IsNullOrWhiteSpace(loadType) ? "load" : loadType,
                    characteristicLoad,
                    loadInput.Percentile,
                    loadInput.COV,
                    loadInput.DistributionType);

                var calculator = new DesignValueCalculator();

                var resistanceResult = calculator.CalculateDesignValue(
                    alphaResistance,
                    resistanceRv.Mean,
                    resistanceRv.COV,
                    targetBeta,
                    resistanceRv.DistType);

                var loadResult = calculator.CalculateDesignValue(
                    alphaLoad,
                    loadRv.Mean,
                    loadRv.COV,
                    targetBeta,
                    loadRv.DistType);

                DA.SetData(0, resistanceResult.designvalue_yd);
                DA.SetData(1, loadResult.designvalue_yd);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
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
            get { return new Guid("46909727-3EE5-4E84-9EE7-5684659F8361"); }
        }
    }
}