using Grasshopper.Kernel;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
// The statistics live in Plugin_test_1.Reliability.RandomVariable (DistributionClasses.cs),
// which already converts characteristic value + fractile + COV + distribution into mean/stddev.
// That class name collides with Plugin_test_1.Reliability.FORM.RandomVariable, so we alias it.
using StatRV = Plugin_test_1.Reliability.RandomVariable;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// Converts a Eurocode-style characteristic value into the mean and standard
    /// deviation of the underlying random variable.
    ///
    /// Intended for engineers with no statistics background: enter the characteristic
    /// value, its fractile, and the distribution family from the code. The coefficient
    /// of variation (COV) is optional — if left empty, a typical default is taken from a
    /// variable-category lookup based on the JCSS Probabilistic Model Code and the
    /// EN 1990 background documents.
    ///
    /// Inputs:
    /// - CharValue   : Characteristic value Xk (in your working units).
    /// - Fractile    : (optional) Fractile p, 0 &lt; p &lt; 1. Empty -> taken from Category.
    /// - Distribution: (optional) "Normal", "LogNormal", or "Gumbel". Empty -> taken from Category.
    /// - COV         : (optional) Coefficient of variation. Empty -> taken from Category.
    /// - Category    : Variable category. When set, it fills in COV, fractile and distribution;
    ///                  any wired input overrides the matching category value.
    ///
    /// Outputs:
    /// - Mean   : Mean value in physical space. For Gumbel this is the reference-period
    ///            (T-year) maxima mean; the 1-year mean is reported in Info.
    /// - StdDev : Standard deviation in physical space.
    /// - Info   : Resolved values (with their source) and the category reference table.
    /// </summary>
    public class CharacteristicToMomentsComponent : GH_Component
    {
        public CharacteristicToMomentsComponent()
            : base(
                "Char to Mean/StdDev",
                "Xk->μ,σ",
                "Convert a characteristic value into the mean and standard deviation of a random variable. " +
                "Pick a variable Category to auto-fill COV, fractile and distribution (JCSS / EN 1990 " +
                "background); wire Fractile, Distribution or COV to override any of them.",
                Common.category,
                "FORM · 01 Random Variables")
        { }

        public override Guid ComponentGuid =>
            new Guid("a1b2c3d4-e5f6-4708-9a1b-2c3d4e5f6a70");

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("CHtM");

        // Input indices (kept as named constants so the body stays readable).
        private const int IN_CHAR = 0;
        private const int IN_FRACTILE = 1;
        private const int IN_DIST = 2;
        private const int IN_COV = 3;
        private const int IN_CATEGORY = 4;

        // Output indices.
        private const int OUT_MEAN = 0;
        private const int OUT_STD = 1;
        private const int OUT_INFO = 2;

        /// <summary>
        /// Default statistics per variable category.
        /// Values are indicative typical figures from the JCSS Probabilistic Model Code (Part 3)
        /// and the EN 1990 background (Gulvanessian, Calgaro &amp; Holický, "Designer's Guide to
        /// EN 1990"; JRC "Implementation of Eurocodes" handbooks). They are editable starting
        /// points, NOT exact code-mandated values — override with project-specific data when
        /// available. Climatic actions (snow, wind) are strongly region-dependent.
        /// </summary>
        private struct CategoryDefault
        {
            public double Cov;
            public double Fractile;
            public string Distribution;
            public string Note;

            public CategoryDefault(double cov, double fractile, string dist, string note)
            {
                Cov = cov;
                Fractile = fractile;
                Distribution = dist;
                Note = note;
            }
        }

        // Keys are matched case-insensitively. Engineers copy a name from the Info output.
        // Each entry supplies a baseline COV, fractile AND distribution; any wired input overrides it.
        private static readonly Dictionary<string, CategoryDefault> Defaults =
            new Dictionary<string, CategoryDefault>(StringComparer.OrdinalIgnoreCase)
            {
                ["Steel - structural (yield fy)"] = new CategoryDefault(0.05, 0.01, "LogNormal", "JCSS PMC Pt.3; valid up to fyk ~ 380 MPa. Range ~0.05-0.08."),
                ["Steel - reinforcement (yield)"] = new CategoryDefault(0.05, 0.05, "LogNormal", "JCSS PMC. Reinforcing bar yield strength."),
                ["Steel - Young's modulus E"] = new CategoryDefault(0.03, 0.50, "LogNormal", "JCSS PMC. Low scatter; often treated as deterministic. Fractile = mean."),
                ["Concrete - compressive fc"] = new CategoryDefault(0.15, 0.05, "LogNormal", "JCSS PMC. In-situ ~0.10-0.18; lower with tight QC."),
                ["Timber - bending strength"] = new CategoryDefault(0.25, 0.05, "LogNormal", "JCSS / EN 1995 background. Grade-dependent ~0.15-0.30."),
                ["Permanent load (self-weight) G"] = new CategoryDefault(0.10, 0.50, "Normal", "EN 1990 background (gamma_G=1.35 calibration). Fractile = mean. Self-weight alone ~0.05."),
                ["Imposed load (variable) Q"] = new CategoryDefault(0.26, 0.98, "Gumbel", "50-year maxima, JCSS live-load model. Range ~0.20-0.35."),
                ["Snow load"] = new CategoryDefault(0.30, 0.98, "Gumbel", "Region-dependent. Ground snow ~0.25; can reach 0.5+."),
                ["Wind load"] = new CategoryDefault(0.35, 0.98, "Gumbel", "Region-dependent annual maxima. Typically ~0.20-0.50."),
                ["Resistance model uncertainty"] = new CategoryDefault(0.05, 0.50, "LogNormal", "JCSS. Mean-based (mean ~1.0-1.15). Fractile = mean; set CharValue = mean."),
                ["Load-effect model uncertainty"] = new CategoryDefault(0.10, 0.50, "LogNormal", "JCSS. Mean-based (mean ~1.0). Fractile = mean; set CharValue = mean."),
                ["Geometry / dimensions"] = new CategoryDefault(0.05, 0.50, "Normal", "JCSS PMC Pt.3 dimensions. Fractile = mean. Often small (~0.01-0.05)."),
            };

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("CharValue", "Xk",
                "Characteristic value of the variable (in your working units).",
                GH_ParamAccess.item);

            pManager.AddNumberParameter("Fractile", "p",
                "Fractile of the characteristic value, 0 < p < 1. Leave empty to use the Category value.\n" +
                "Typical: 0.05 material strength, 0.50 permanent load / mean-based, 0.98 variable/climatic load.",
                GH_ParamAccess.item);

            pManager.AddTextParameter("Distribution", "Dist",
                "Distribution type: 'Normal', 'LogNormal', or 'Gumbel'. Leave empty to use the Category value.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter("COV", "V",
                "Coefficient of variation (std/mean). Leave empty to use the Category value.",
                GH_ParamAccess.item);

            pManager.AddTextParameter("Category", "Cat",
                "Variable category. When set, it auto-fills COV, fractile AND distribution.\n" +
                "Any value wired into Fractile / Distribution / COV overrides that category value.\n" +
                "See the Info output for the accepted names. Use 'Custom' for fully manual entry.",
                GH_ParamAccess.item, "Custom");

            // Only CharValue is strictly required; the rest fall back to the category.
            pManager[IN_FRACTILE].Optional = true;
            pManager[IN_DIST].Optional = true;
            pManager[IN_COV].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Mean", "μ", "Mean value in physical space.", GH_ParamAccess.item);
            pManager.AddNumberParameter("StdDev", "σ", "Standard deviation in physical space.", GH_ParamAccess.item);
            pManager.AddTextParameter("Info", "Info", "Accepted categories, their default COVs, and the value used.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Always provide the reference table, even before valid inputs arrive.
            DA.SetData(OUT_INFO, BuildCategoryInfo());

            double charValue = 0.0;
            if (!DA.GetData(IN_CHAR, ref charValue))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Provide a characteristic value (Xk). See the Info output for categories and default COVs.");
                return;
            }

            // Category first: it provides the baseline COV, fractile and distribution.
            string category = "Custom";
            DA.GetData(IN_CATEGORY, ref category);
            category = (category ?? "Custom").Trim();
            bool hasCategory = Defaults.TryGetValue(category, out CategoryDefault def);

            // --- Resolve each value: an explicit wired input overrides the category. ------

            // Fractile
            double fractile = 0.0;
            string fractileSource;
            if (DA.GetData(IN_FRACTILE, ref fractile))
            {
                fractileSource = "input";
            }
            else if (hasCategory)
            {
                fractile = def.Fractile;
                fractileSource = "category";
            }
            else
            {
                fractile = 0.05;
                fractileSource = "assumed 0.05";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No fractile supplied and no recognised category; assuming p = 0.05.");
            }

            // Distribution
            string distStr = null;
            string distSource;
            if (DA.GetData(IN_DIST, ref distStr) && !string.IsNullOrWhiteSpace(distStr))
            {
                distSource = "input";
            }
            else if (hasCategory)
            {
                distStr = def.Distribution;
                distSource = "category";
            }
            else
            {
                distStr = "Normal";
                distSource = "assumed Normal";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No distribution supplied and no recognised category; assuming Normal.");
            }

            // COV
            double cov = 0.0;
            string covSource;
            if (DA.GetData(IN_COV, ref cov))
            {
                covSource = "input";
            }
            else if (hasCategory)
            {
                cov = def.Cov;
                covSource = "category";
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"No COV supplied and category '{category}' is not recognised. " +
                    "Either wire a COV or pick a category from the Info output.");
                return;
            }

            // --- Validate. ---------------------------------------------------------------
            if (!(fractile > 0.0 && fractile < 1.0))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Fractile p must be strictly between 0 and 1.");
                return;
            }
            if (cov < 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "COV cannot be negative.");
                return;
            }

            string distNorm = NormalizeDistribution(distStr);
            if (distNorm == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Distribution '{distStr}' not recognised. Using 'Normal'.");
                distNorm = "normal";
            }
            if ((distNorm == "lognormal" || distNorm == "gumbel") && charValue <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"{distStr} requires a positive characteristic value (Xk > 0).");
                return;
            }

            // --- Compute via the existing, validated statistics class. -------------------
            try
            {
                var rv = new StatRV("Xk->moments", charValue, fractile, cov, distNorm);

                // For Gumbel, report the reference-period (T-year) maxima mean as the headline
                // Mean; the 1-year (annual) mean goes into the Info box. StdDev is invariant.
                bool isGumbel = distNorm == "gumbel";
                double meanOut = isGumbel ? rv.MeanGumbelT : rv.Mean;
                double stdOut = rv.StdDev;

                if (double.IsNaN(meanOut) || double.IsNaN(stdOut) ||
                    double.IsInfinity(meanOut) || double.IsInfinity(stdOut))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Calculation produced a non-finite result. Check that the fractile, COV and distribution are physically consistent.");
                    return;
                }

                DA.SetData(OUT_MEAN, meanOut);
                DA.SetData(OUT_STD, stdOut);

                // Show the resolved trio right under the component.
                Message = $"{distStr}, p={fractile:0.###}, V={cov:0.###}";

                // Replace the table with a result-focused Info string once we have an answer.
                double? annualMean = isGumbel ? rv.Mean : (double?)null;
                DA.SetData(OUT_INFO, BuildResultInfo(
                    charValue, fractile, fractileSource, distStr, distSource, cov, covSource,
                    meanOut, stdOut, annualMean, rv.ReferenceperiodYears));
            }
            catch (Exception ex)
            {
                // The statistics class throws on degenerate combinations (e.g. Normal with
                // 1 + cov*z <= 0, or a degenerate Gumbel denominator). Surface it cleanly.
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        /// <summary>Maps common spellings to the keywords expected by the statistics class.</summary>
        private static string NormalizeDistribution(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            switch (raw.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "").Replace("_", ""))
            {
                case "normal":
                case "gaussian":
                    return "normal";
                case "lognormal":
                case "lognorm":
                    return "lognormal";
                case "gumbel":
                case "gumbelmax":
                case "extremevalue1":
                case "ev1":
                    return "gumbel";
                default:
                    return null;
            }
        }

        private static string BuildCategoryInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---- CATEGORY DEFAULTS (COV / distribution / fractile) ----");
            sb.AppendLine("Type one of these into 'Category' to auto-fill all three.");
            sb.AppendLine("Wire a value into Fractile / Distribution / COV to override any of them.");
            sb.AppendLine();
            foreach (var kv in Defaults)
            {
                sb.AppendLine($"  {kv.Key}");
                sb.AppendLine($"     COV={kv.Value.Cov:0.###} | dist={kv.Value.Distribution} | fractile={kv.Value.Fractile:0.###}");
                sb.AppendLine($"     {kv.Value.Note}");
            }
            sb.AppendLine();
            sb.AppendLine("  Custom  ->  nothing auto-filled; enter values manually (COV required).");
            sb.AppendLine();
            sb.AppendLine("Sources: JCSS Probabilistic Model Code (Part 3); EN 1990 background");
            sb.AppendLine("(Designer's Guide to EN 1990; JRC Eurocodes handbooks).");
            sb.AppendLine("Indicative values only - override with project data. Snow/wind are region-dependent.");
            return sb.ToString();
        }

        private static string BuildResultInfo(
            double xk, double p, string pSource, string dist, string distSource,
            double cov, string covSource, double mean, double std,
            double? annualMean, int refPeriodYears)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---- RESULT ----");
            sb.AppendLine($"  CharValue : {xk:G6}");
            sb.AppendLine($"  Fractile  : {p:0.####}   ({pSource})");
            sb.AppendLine($"  Distrib.  : {dist}   ({distSource})");
            sb.AppendLine($"  COV       : {cov:0.####}   ({covSource})");
            sb.AppendLine("  ---");
            sb.AppendLine($"  Mean      : {mean:G6}");
            sb.AppendLine($"  StdDev    : {std:G6}");
            if (annualMean.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine($"  NOTE (Gumbel): 'Mean' is the {refPeriodYears}-year maxima mean.");
                sb.AppendLine($"  1-year (annual) mean = {annualMean.Value:G6}  (StdDev is period-invariant).");
                sb.AppendLine("  COV is interpreted as the reference-period (T-year) COV.");
            }

            return sb.ToString();
        }
    }
}