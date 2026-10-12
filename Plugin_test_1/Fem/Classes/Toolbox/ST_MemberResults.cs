// =============================================================================
//  ST_MemberResults.cs
//
//  WHAT IT IS
//  Results per member that travel WITH a model (TB_Model.Results), so that a
//  component further down the canvas can show them (Member Info):
//    Group         the design group of each member (from RBDO / EC3 Sections)
//    Beta          the reliability index of each member's governing limit state
//    Utilisation   the Eurocode utilisation of each member's governing check
//  together with where each came from.
//
//  WHO WRITES THEM
//    FORM Members   -> Beta            (its new Model output)
//    EC3 Members    -> Utilisation     (its new Model output)
//    RBDO Sections  -> Group, Beta     (the optimised Model)
//    EC3 Sections   -> Group, Utilisation, and Beta if the implied reliability was run
//  A component that adds a result keeps the ones the model already had, so the
//  chain RBDO Sections -> EC3 Members -> Member Info shows group, beta and U.
//
//  The arrays are in element order. A model built anew (Assembly, a new section
//  optimisation) starts without results; DeepCopy keeps them.
// =============================================================================

using System;
using System.Linq;

using Propability_NTNU_v1.Classes.Reliability;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    [Serializable]
    public class TB_MemberResults
    {
        /// <summary>Group of each member; null if no grouping is known.</summary>
        public string[] Group;
        /// <summary>Beta of the governing limit state of each member (NaN: no limit state); null if not computed.</summary>
        public double[] Beta;
        /// <summary>Eurocode utilisation of each member (NaN: not checked); null if not computed.</summary>
        public double[] Utilisation;

        /// <summary>Where the values came from, e.g. "RBDO Sections (axial force + bending)".</summary>
        public string GroupSource = "", BetaSource = "", UtilisationSource = "";
        /// <summary>Target beta of the run that produced Beta; NaN if none.</summary>
        public double BetaTarget = double.NaN;

        public TB_MemberResults Clone()
        {
            return new TB_MemberResults
            {
                Group = (string[])Group?.Clone(),
                Beta = (double[])Beta?.Clone(),
                Utilisation = (double[])Utilisation?.Clone(),
                GroupSource = GroupSource,
                BetaSource = BetaSource,
                UtilisationSource = UtilisationSource,
                BetaTarget = BetaTarget
            };
        }

        /// <summary>True if the array belongs to a model with n members.</summary>
        public static bool Fits<T>(T[] values, int n)
        {
            return values != null && values.Length == n;
        }

        // ------------------------------------------------------------------
        //  Writing results into a model (keeps what the model already has)
        // ------------------------------------------------------------------

        private static TB_MemberResults Of(TB_Model mdl)
        {
            if (mdl.Results == null) mdl.Results = new TB_MemberResults();
            return mdl.Results;
        }

        /// <summary>The beta of every member's governing limit state.</summary>
        public static void SetBeta(TB_Model mdl, MemberReliabilityResult r, string source, double betaTarget = double.NaN)
        {
            if (mdl == null || r == null) return;
            int n = mdl.Elem1Ds.Count;
            if (r.Governing == null || r.Governing.Length != n) return;

            TB_MemberResults res = Of(mdl);
            res.Beta = r.Governing.Select(g => (g != null && g.HasBeta) ? g.Form.Beta : double.NaN).ToArray();
            res.BetaSource = source + " (" + r.Options.Describe() + ")";
            res.BetaTarget = betaTarget;
        }

        /// <summary>The utilisation of every member's governing check.</summary>
        public static void SetUtilisation(TB_Model mdl, EcCheckResult r, string source)
        {
            if (mdl == null || r == null) return;
            int n = mdl.Elem1Ds.Count;
            if (r.Governing == null || r.Governing.Length != n) return;

            TB_MemberResults res = Of(mdl);
            res.Utilisation = r.Governing.Select(g => (g != null) ? g.Utilisation : double.NaN).ToArray();
            res.UtilisationSource = source + " (" + r.Options.Describe() + ")";
        }

        /// <summary>The group of every member.</summary>
        public static void SetGroups(TB_Model mdl, string[] groups, string source)
        {
            if (mdl == null || groups == null || groups.Length != mdl.Elem1Ds.Count) return;
            TB_MemberResults res = Of(mdl);
            res.Group = (string[])groups.Clone();
            res.GroupSource = source;
        }

        /// <summary>Everything a section optimisation knows about its optimised model.</summary>
        public static void SetFromOptimisation(SectionOptimisationResult opt, string source)
        {
            if (opt == null || opt.Model == null) return;
            SetGroups(opt.Model, opt.MemberGroups, source);
            if (opt.ReliabilityOptimised != null)
                SetBeta(opt.Model, opt.ReliabilityOptimised, source, opt.BetaTarget);
            if (opt.EurocodeOptimised != null)
                SetUtilisation(opt.Model, opt.EurocodeOptimised, source);
        }
    }
}
