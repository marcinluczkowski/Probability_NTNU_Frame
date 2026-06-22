using System;
using Grasshopper.Kernel;
using Plugin_test_1.Reliability.FORM;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// Creates a RandomVariable for use in FORM reliability analysis.
    /// 
    /// Inputs:
    /// - Name: Variable name (e.g., "E", "Load", "Fy")
    /// - Distribution: Distribution type ("Normal", "LogNormal", "Gumbel")
    /// - Mean: Mean value in physical space
    /// - StdDev: Standard deviation in physical space
    /// 
    /// Output:
    /// - RandomVariable: The created random variable object
    /// </summary>
    public class RandomVariableComponent : GH_Component
    {
        public RandomVariableComponent()
            : base(
                "RandomVariable",
                "RV",
                "Create a random variable with distribution parameters for FORM analysis",
                Common.category,
                "FORM · 01 Random Variables")
        { }

        public override Guid ComponentGuid =>
            new Guid("d4c3b2a1-f6e5-5b4a-9d7c-8f9e0b1a2c3d");

        protected override System.Drawing.Bitmap Icon => null;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Name", "N", "Variable name (e.g., 'E', 'Load', 'Fy')", GH_ParamAccess.item);
            pManager.AddTextParameter("Distribution", "Dist", "Distribution type: 'Normal', 'LogNormal', or 'Gumbel'", GH_ParamAccess.item, "Normal");
            pManager.AddNumberParameter("Mean", "μ", "Mean value in physical space", GH_ParamAccess.item);
            pManager.AddNumberParameter("StdDev", "σ", "Standard deviation in physical space", GH_ParamAccess.item);

            // Make inputs optional so we can show the infobox if no inputs are provided
            pManager[0].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddParameter(new Param_RandomVariable(), "RandomVariable", "RV", "The created random variable", GH_ParamAccess.item);
            pManager.AddTextParameter("Info", "Info", "Acceptable variables and typical units", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string name = string.Empty;
            string distStr = "Normal";
            double mean = 0.0;
            double stdDev = 0.0;

            string infoMessage = 
                "---- ACCEPTABLE VARIABLES & UNITS ----\n" +
                "Material Properties:\n" +
                "  E, Youngs_Modulus   [MPa]\n" +
                "  G, Shear_Modulus    [MPa]\n" +
                "  fy, Yield_Strength  [MPa]\n" +
                "  Gamma, Density      [kg/m³]\n\n" +
                "Section Properties:\n" +
                "  Area, A             [mm²]\n" +
                "  Iy, Moment_We...    [mm⁴]\n" +
                "  Iz, Moment_We...    [mm⁴]\n" +
                "  J, Torsional_C...   [mm⁴]\n" +
                "  Wy, Wz              [mm³]\n\n" +
                "Loads:\n" +
                "  Load_<Index>_X/Y/Z  [kN]   (Point loads)\n" +
                "  Load_<Index>_Mx...  [kNm]  (Point moments)\n" +
                "---------------------------------------";

            DA.SetData(1, infoMessage);

            bool hasName = DA.GetData(0, ref name);
            bool hasMean = DA.GetData(2, ref mean);
            bool hasStdDev = DA.GetData(3, ref stdDev);

            // If any essential input is missing, show the info message and exit
            if (!hasName || !hasMean || !hasStdDev || string.IsNullOrWhiteSpace(name))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Provide Name, Mean, and StdDev to create a Random Variable.\nSee 'Info' output for valid names and units.");
                return;
            }

            // Read distribution type
            DA.GetData(1, ref distStr);

            // Read and validate mean
            if (!DA.GetData(2, ref mean))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mean required.");
                return;
            }

            // Read and validate standard deviation
            if (!DA.GetData(3, ref stdDev) || stdDev <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "StdDev must be > 0.");
                return;
            }

            // Parse distribution type
            DistributionType distribution;
            switch (distStr.ToLower())
            {
                case "normal":
                    distribution = DistributionType.Normal;
                    break;
                case "lognormal":
                    distribution = DistributionType.LogNormal;
                    break;
                case "gumbel":
                    distribution = DistributionType.Gumbel;
                    break;
                default:
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Distribution '{distStr}' not recognized. Using 'Normal'.");
                    distribution = DistributionType.Normal;
                    break;
            }

            // Create RandomVariable
            var rv = new RandomVariable(name, distribution, mean, stdDev);
            var ghRv = new GH_RandomVariable(rv);

            // Set output
            DA.SetData(0, ghRv);
        }
    }
}
