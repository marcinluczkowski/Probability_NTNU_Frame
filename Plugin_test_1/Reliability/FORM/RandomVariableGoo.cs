using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// Grasshopper wrapper for RandomVariable.
    /// Enables RandomVariable objects to be passed between Grasshopper components.
    /// </summary>
    public class GH_RandomVariable : GH_Goo<RandomVariable>
    {
        // --- constructors ---

        /// <summary>Empty constructor (required by Grasshopper).</summary>
        public GH_RandomVariable() { }

        /// <summary>Copy constructor.</summary>
        public GH_RandomVariable(GH_RandomVariable other) : base(other.Value)
        {
            this.Value = other.Value != null ? new RandomVariable(
                other.Value.Name,
                other.Value.Distribution,
                other.Value.Mean,
                other.Value.StdDev) : null;
        }

        /// <summary>Constructor from RandomVariable.</summary>
        public GH_RandomVariable(RandomVariable rv) : base(rv)
        {
            this.Value = rv;
        }

        // --- properties ---

        public override bool IsValid => base.m_value != null;

        public override string TypeName => "RandomVariable";

        public override string TypeDescription => "Random Variable with probability distribution";

        // --- methods ---

        /// <summary>
        /// Creates a duplicate of this Goo object.
        /// </summary>
        public override IGH_Goo Duplicate()
        {
            return new GH_RandomVariable(this);
        }

        /// <summary>
        /// Returns a human-readable string representation.
        /// Format: "DistributionName(param1=value1, param2=value2)"
        /// Example: "Normal(μ=200 GPa, σ=10 GPa)"
        /// </summary>
        public override string ToString()
        {
            if (!IsValid || Value == null)
                return "RandomVariable (null)";

            string distribution = Value.Distribution.ToString();
            string name = Value.Name;
            double mean = Value.Mean;
            double stdDev = Value.StdDev;

            // Format with Greek letters and units context
            return $"{distribution}({name}: μ={mean:G6}, σ={stdDev:G6})";
        }

        /// <summary>
        /// Converts a value to a RandomVariable Goo object.
        /// Not typically implemented; included for completeness.
        /// </summary>
        public override bool CastFrom(object source)
        {
            if (source == null) return false;

            if (source is RandomVariable rv)
            {
                this.Value = rv;
                return true;
            }

            if (source is GH_RandomVariable ghrv)
            {
                this.Value = ghrv.Value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Converts a RandomVariable Goo object to another type.
        /// Not typically implemented; included for completeness.
        /// </summary>
        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q).IsAssignableFrom(typeof(RandomVariable)))
            {
                object obj = this.Value;
                target = (Q)obj;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Grasshopper parameter for RandomVariable input/output.
    /// Allows RandomVariable objects to be used as component inputs/outputs in Grasshopper.
    /// </summary>
    public class Param_RandomVariable : GH_PersistentParam<GH_RandomVariable>
    {
        /// <summary>Constructor initializing the parameter description and metadata.</summary>
        public Param_RandomVariable() : base(
            new GH_InstanceDescription(
                "RandomVariable",
                "RV",
                "Random variable with probability distribution for reliability analysis",
                Common.category,
                "FORM · 01 Random Variables"))
        { }

        /// <summary>
        /// Unique identifier for this parameter type.
        /// Generated GUID ensures this parameter type is recognized by Grasshopper.
        /// </summary>
        public override Guid ComponentGuid => new Guid("f8e7d6c5-b4a3-9211-8f2e-7d6c5b4a3210");

        /// <summary>
        /// Icon for this parameter (single character).
        /// "R" stands for "RandomVariable"/"Reliability".
        /// </summary>
        protected override System.Drawing.Bitmap Icon => IconHelper.Create("R");

        /// <summary>
        /// Prompt behavior when accepting multiple values at once.
        /// Currently accepts without prompting.
        /// </summary>
        protected override GH_GetterResult Prompt_Plural(ref List<GH_RandomVariable> values)
        {
            return GH_GetterResult.success;
        }

        /// <summary>
        /// Prompt behavior when accepting a single value.
        /// Currently accepts without prompting.
        /// </summary>
        protected override GH_GetterResult Prompt_Singular(ref GH_RandomVariable value)
        {
            return GH_GetterResult.success;
        }
    }
}
