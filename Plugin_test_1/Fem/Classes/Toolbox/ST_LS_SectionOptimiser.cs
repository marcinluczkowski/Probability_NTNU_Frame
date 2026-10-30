// =============================================================================
//  ST_LS_SectionOptimiser.cs
//
//  WHAT IT IS
//  Discrete section optimisation of a truss: the lightest sections from a catalogue
//  for which EVERY member satisfies a criterion. Two criteria use the same search:
//
//    reliability (RBDO)   beta of every member >= target        (MemberReliability.Run)
//    Eurocode             utilisation of every member <= limit   (EurocodeMemberCheck.Run)
//
//  so the reliability-based design and the Eurocode design of one structure can be
//  put side by side.
//
//  DESIGN VARIABLES
//  One section per GROUP of members. All members of a group get the same section.
//  The candidates are the sections of a table in the Karamba3D format (the built-in
//  HEA list by default), ordered by increasing area.
//
//  SEARCH
//    1. Start with the lightest candidate in every group.
//    2. Step up: assess the structure; every group with a member that fails the
//       criterion moves to the next heavier candidate. Repeat until all members pass.
//       Or, with the binary search switched on, first a bisection of the candidate list
//       in all groups at once (2b below), then step up from its result if needed.
//    3. Step down: take the groups from the heaviest to the lightest and try the next
//       lighter candidate in one group at a time; keep it if all members still pass.
//       Repeat until no group can be made lighter.
//    3b. Restarts (optional, 'Restarts' > 0): from the best design so far, a few groups
//       (by default half of them, at least 3) are given random sizes, between the lightest
//       candidate and two sizes above the heaviest one in use. The structure is then
//       stepped up where a member fails (as in 2) and stepped down (as in 3). The result
//       is kept if it is lighter. Repeated 'Restarts' times. The random choices follow a
//       seed, so a run can be repeated exactly.
//    4. Exchange, on the best design: make ONE group one size heavier and step the other
//       groups down again. If the structure ends up lighter, keep that design and go back
//       to 3. Stop when no group gives a lighter structure this way. With an exchange
//       depth of 2 the same is then tried with every PAIR of groups, and so on.
//
//  Why restarts: in a structure whose forces depend strongly on the sections (a floor
//  grid in bending), the stepping search follows one path and can end at a design from
//  which no single group can move, although a quite different design (e.g. a one-way
//  load path) is lighter. Restarting from changed designs lets the search reach those.
//  In the tests (20 Oct) the restarts found the lightest design of the whole catalogue
//  range on the 5 x 5 m grid (checked against all designs) and changed nothing on the
//  X-braced truss.
//
//    2b. Binary search (optional): every group keeps an interval of candidates, from the
//       lightest that may still be enough to the lightest known to be enough (at first
//       the heaviest). Each round assesses the structure with every group at the middle of
//       its interval: a group whose members all pass continues in the lower half, a group
//       with a failing member in the upper half. After about log2(number of candidates)
//       rounds every group is at the lightest candidate that passed. In an indeterminate
//       truss a group's result depends on the other groups' sections, which change between
//       rounds, so the result is assessed once more and stepped up where a member fails;
//       steps 3 and 4 follow as usual. The binary search needs fewer assessments than
//       stepping up when the sections end far from the lightest candidate; near it the two
//       are about the same.
//
//  Step 4 is needed in a statically indeterminate truss, where the member forces
//  depend on the sections: a slightly heavier group can relieve another group
//  enough for that one to drop a size (e.g. stiffer verticals and lighter diagonals
//  in a cross-braced truss). Steps 2 and 3 alone cannot find such a design, because
//  they never make a group heavier that already passes.
//
//  Every assessment is a complete analysis of the structure with the trial sections.
//  The result is a design in which no group can be made lighter on its own, and in
//  which making any one group (any pair with depth 2) one size heavier does not lead
//  to a lighter structure. For a statically determinate truss (member forces
//  independent of the sections) that is the lightest design there is. For an
//  indeterminate one it is not guaranteed to be: designs that need more groups to
//  become heavier at the same time than the exchange depth, or a group by more than
//  one size at once, are not examined. The number of assessments grows quickly with
//  the depth and the number of groups.
//  A heavier candidate is assumed not to be weaker, which holds within one family of
//  sections (e.g. HEA).
//
//  ITERATION HISTORY (phases: start, bisect, up, down, restart, exchange)
//  Every assessment of the structure is recorded in order (SectionOptimisationResult.History):
//  the trial design, its weight, its worst member value (lowest beta / highest utilisation),
//  the part of the search it belongs to, whether the search moved on to it, and the design the
//  search stands on after it. OptimumIteration is the assessment of the final design (where the
//  optimum was found); the search may move on to it a few assessments later, when the restart
//  or exchange that found it is finished (OptimumAccepted). Each design is assessed once, so the history has as many
//  entries as there were assessments; designs the search returns to are not repeated, and Kept
//  marks the entry of a design the search moved on to, at that assessment or later.
//
//  BEFORE AND AFTER
//  The input model (with the sections it comes with) is assessed as well, so the
//  report compares weight, utilisation and reliability before and after the
//  optimisation. For the Eurocode design the reliability is the IMPLIED one: FORM on
//  the same random variables as the reliability-based design (AddImpliedReliability).
//
//  MEMBER CHECKS: both criteria use MemberCheckOptions (default: axial force + the
//  bending moments from the rigid joints, ST_LS_MemberInteraction.cs; or axial only).
//
//  NOT INCLUDED: self-weight is not a load in the model, so the loads do not change
//  with the sections. A member that no load case stresses gets the lightest candidate.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Propability_NTNU_v1.Classes.Toolbox;

using Plugin_test_1.Reliability.FORM;

namespace Propability_NTNU_v1.Classes.Reliability
{
    /// <summary>What the search needs to know about one trial design.</summary>
    public class MemberAssessment
    {
        /// <summary>Per member: the criterion is satisfied (also true for a member without a limit state).</summary>
        public bool[] Ok = new bool[0];
        /// <summary>Per member: the value compared with the limit (beta or utilisation). NaN if there is none.</summary>
        public double[] Value = new double[0];
        /// <summary>Per member: why it is not ok when the value itself does not say so (e.g. FORM did not converge).</summary>
        public string[] Note = new string[0];

        /// <summary>The underlying results. Only the one of the criterion in use is set.</summary>
        public MemberReliabilityResult Reliability = null;
        public EcCheckResult Eurocode = null;

        public bool AllOk { get { return Ok.All(v => v); } }
    }

    /// <summary>One assessment of the structure during the search (one entry of the iteration history).</summary>
    public class SearchIteration
    {
        /// <summary>0, 1, 2, ...: the assessment number. 0 = the lightest candidate in every group.</summary>
        public int Index;
        /// <summary>Part of the search: "start", "bisect", "up", "down", "restart" or "exchange" (see the header of this file).</summary>
        public string Phase = "";
        /// <summary>Restart number (1, 2, ...) for the assessments of a restart, 0 otherwise.</summary>
        public int Restart;
        /// <summary>Candidate index of each group in this trial design (0 = lightest).</summary>
        public int[] Candidates = new int[0];
        /// <summary>Weight of this trial design [kg].</summary>
        public double Weight;
        /// <summary>Worst member value of this trial design: the lowest beta, or the highest utilisation.</summary>
        public double Worst;
        /// <summary>Every member of this trial design satisfies the criterion.</summary>
        public bool Feasible;
        /// <summary>The search moved on to this design (it became the current design), at this assessment or later.</summary>
        public bool Kept;
        /// <summary>The design the search stands on after this assessment: its weight [kg] and worst value.</summary>
        public double CurrentWeight = double.NaN;
        public double CurrentWorst = double.NaN;
        /// <summary>Candidate index of each group of the current design, as text ("0,3,2,...").</summary>
        public string CurrentKey = "";
    }

    public class SectionOptimisationResult
    {
        /// <summary>Text of the criterion, e.g. "beta >= 3.80 in every member".</summary>
        public string Criterion = "";
        /// <summary>True for beta (the worst value is the lowest), false for utilisation.</summary>
        public bool HigherIsBetter = true;

        /// <summary>All members satisfy the criterion in the final design.</summary>
        public bool Feasible;
        /// <summary>The model with the final sections.</summary>
        public TB_Model Model;

        public List<string> Groups = new List<string>();
        /// <summary>Section name, number of members and weight [kg] of each group.</summary>
        public List<string> GroupSections = new List<string>();
        public List<int> GroupSizes = new List<int>();
        public List<double> GroupWeights = new List<double>();
        /// <summary>Group and section name of each member, in model order.</summary>
        public string[] MemberGroups = new string[0];
        public string[] MemberSections = new string[0];

        /// <summary>Weight of the final design, and of the lightest candidate everywhere [kg].</summary>
        public double Weight;
        public double WeightLightest;
        /// <summary>Weight of the input model, with the sections it came with [kg].</summary>
        public double WeightInitial = double.NaN;

        /// <summary>
        /// The input model and the final design assessed with each criterion that is available:
        /// Eurocode check (Eurocode design) and FORM (reliability-based design, or the implied
        /// reliability of the Eurocode design). Null where not assessed.
        /// </summary>
        public EcCheckResult EurocodeInitial = null;
        public EcCheckResult EurocodeOptimised = null;
        public MemberReliabilityResult ReliabilityInitial = null;
        public MemberReliabilityResult ReliabilityOptimised = null;
        /// <summary>Target beta used to count the members below it. NaN if none.</summary>
        public double BetaTarget = double.NaN;
        /// <summary>Number of complete assessments of the structure.</summary>
        public int Evaluations;
        public List<string> CandidateNames = new List<string>();

        /// <summary>Assessment of the final design.</summary>
        public MemberAssessment Final;
        /// <summary>Every assessment of the search, in order.</summary>
        public List<SearchIteration> History = new List<SearchIteration>();
        /// <summary>Index in History of the assessment of the final design (where the optimum was found); -1 if none.</summary>
        public int OptimumIteration = -1;
        /// <summary>Index in History from which on the search stands on the final design; -1 if none.</summary>
        public int OptimumAccepted = -1;
        /// <summary>Restarts made, groups changed in each, and the seed of the random choices.</summary>
        public int Restarts, RestartGroups, Seed;
        /// <summary>Weight [kg] at the end of the first search (index 0) and of every restart, before the exchange step.</summary>
        public List<double> SearchWeights = new List<double>();
        public List<string> Log = new List<string>();
        public List<string> Warnings = new List<string>();

        public string Report()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.AppendLine("SECTION OPTIMISATION");
            sb.AppendLine("Criterion : " + Criterion);
            sb.AppendLine("Candidates: " + CandidateNames.Count.ToString(ci) + " sections by increasing area, " +
                          (CandidateNames.Count > 0 ? CandidateNames[0] + " ... " + CandidateNames[CandidateNames.Count - 1] : "-"));
            sb.AppendLine("Groups    : " + Groups.Count.ToString(ci) + " (" + MemberGroups.Length.ToString(ci) + " members)");
            sb.AppendLine();

            sb.AppendLine("Search (weight in kg, 'worst' is the " + (HigherIsBetter ? "lowest beta" : "highest utilisation") + "):");
            foreach (string line in Log) sb.AppendLine("  " + line);
            if (Restarts > 0 && SearchWeights.Count > 0)
            {
                double best = SearchWeights.Min();
                int hit = SearchWeights.Count(w => Math.Abs(w - best) <= 1e-9 * Math.Max(1.0, best));
                sb.AppendLine("  restarts: " + Restarts.ToString(ci) + " (" + RestartGroups.ToString(ci) + " group(s) given random sizes each time, seed " +
                              Seed.ToString(ci) + "); the lightest design before the exchange step (" + best.ToString("F1", ci) + " kg) was reached by " +
                              hit.ToString(ci) + " of the " + SearchWeights.Count.ToString(ci) + " searches (the first and the restarts); their results ranged " +
                              SearchWeights.Min().ToString("F1", ci) + " - " + SearchWeights.Max().ToString("F1", ci) + " kg.");
            }
            if (History.Count > 0)
                sb.AppendLine("  '#' is the assessment number (the Iteration outputs). " + History.Count.ToString(ci) +
                              " assessments; the final design was assessed at #" + OptimumIteration.ToString(ci) +
                              (OptimumAccepted != OptimumIteration
                                  ? " and the search moved on to it at #" + OptimumAccepted.ToString(ci) + ", when the restart or exchange that found it was finished."
                                  : " and the search moved on to it there."));
            sb.AppendLine();

            sb.AppendLine(Feasible
                ? "Result: every member satisfies the criterion."
                : "Result: NOT every member satisfies the criterion. See the warnings.");
            sb.AppendLine("Weight " + Weight.ToString("F1", ci) + " kg (lightest candidate everywhere: " +
                          WeightLightest.ToString("F1", ci) + " kg), " + Evaluations.ToString(ci) + " assessments of the structure.");
            sb.AppendLine();

            sb.AppendLine("  group             members  section     weight [kg]     worst   in member");
            for (int g = 0; g < Groups.Count; g++)
            {
                int worst = WorstMember(g);
                sb.Append("  " + Groups[g].PadRight(17) + " ");
                sb.Append(GroupSizes[g].ToString(ci).PadLeft(7) + "  ");
                sb.Append(GroupSections[g].PadRight(10) + " ");
                sb.Append(GroupWeights[g].ToString("F1", ci).PadLeft(12) + " ");
                if (worst < 0)
                    sb.AppendLine("        -   (not stressed)");
                else
                    sb.AppendLine(Final.Value[worst].ToString("F3", ci).PadLeft(9) + "   [" + worst.ToString(ci) + "] " +
                                  Model.Elem1Ds[worst].Tag +
                                  (string.IsNullOrEmpty(Final.Note[worst]) ? "" : "  (" + Final.Note[worst] + ")"));
            }

            sb.AppendLine();
            sb.Append(BeforeAndAfter());

            if (Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (string w in Warnings) sb.AppendLine("  " + w);
            }
            return sb.ToString();
        }

        /// <summary>The input model against the optimised design, for every assessment that was made.</summary>
        public string BeforeAndAfter()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            bool implied = !HigherIsBetter;          // Eurocode design: its reliability is the implied one

            sb.AppendLine("Before and after the optimisation:");
            sb.AppendLine("  " + "".PadRight(32) + "input model".PadLeft(12) + "optimised".PadLeft(13));
            sb.AppendLine(Row("weight [kg]", WeightInitial, Weight, "F1"));

            if (EurocodeInitial != null && EurocodeOptimised != null)
                sb.AppendLine(Row("highest utilisation (Eurocode)", EurocodeInitial.MaxUtilisation, EurocodeOptimised.MaxUtilisation, "F3"));

            if (ReliabilityInitial != null && ReliabilityOptimised != null)
            {
                string pre = implied ? "implied " : "";
                sb.AppendLine(Row(pre + "lowest member beta", ReliabilityInitial.MinBeta, ReliabilityOptimised.MinBeta, "F3"));
                sb.AppendLine(Row(pre + "system beta",
                                  ReliabilityInitial.SystemResult != null ? ReliabilityInitial.SystemResult.BetaSystem : double.NaN,
                                  ReliabilityOptimised.SystemResult != null ? ReliabilityOptimised.SystemResult.BetaSystem : double.NaN, "F3"));
                if (!double.IsNaN(BetaTarget))
                    sb.AppendLine(Row("members with beta < " + BetaTarget.ToString("0.00", ci),
                                      CountBelow(ReliabilityInitial, BetaTarget), CountBelow(ReliabilityOptimised, BetaTarget), "F0"));

                foreach (var pair in new[] { Tuple.Create("input model", ReliabilityInitial), Tuple.Create("optimised", ReliabilityOptimised) })
                {
                    MemberReliabilityResult r = pair.Item2;
                    if (r.CriticalMember < 0) continue;
                    MemberLimitStateResult g = r.Governing[r.CriticalMember];
                    sb.AppendLine("  lowest " + pre + "beta, " + pair.Item1 + ": [" + r.CriticalMember.ToString(ci) + "] " + g.Tag +
                                  " (" + MemberLimitStateResult.ModeName(g.Mode, g.NMean) + "), section " + r.Members[r.CriticalMember].SectionTag);
                }
            }
            else if (implied)
            {
                sb.AppendLine("  (no random variables given: the implied reliability is not computed)");
            }
            return sb.ToString();
        }

        private static string Row(string label, double before, double after, string format)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            Func<double, string> f = v => double.IsNaN(v) ? "-" : v.ToString(format, ci);
            return "  " + label.PadRight(32) + f(before).PadLeft(12) + f(after).PadLeft(13);
        }

        /// <summary>Members whose governing beta is below the target (members without a limit state are not counted).</summary>
        private static double CountBelow(MemberReliabilityResult r, double target)
        {
            int n = 0;
            foreach (MemberLimitStateResult g in r.Governing)
                if (g != null && g.HasBeta && g.Form.Beta < target) n++;
            return n;
        }

        /// <summary>The member of group g with the worst value, -1 if no member of the group has a value.</summary>
        private int WorstMember(int g)
        {
            int worst = -1;
            for (int m = 0; m < MemberGroups.Length; m++)
            {
                if (MemberGroups[m] != Groups[g] || Final == null) continue;
                if (!Final.Ok[m] && double.IsNaN(Final.Value[m])) return m;        // failed without a value
                if (double.IsNaN(Final.Value[m])) continue;
                if (worst < 0 ||
                    (HigherIsBetter ? Final.Value[m] < Final.Value[worst] : Final.Value[m] > Final.Value[worst]))
                    worst = m;
            }
            return worst;
        }
    }

    public static class SectionOptimiser
    {
        /// <summary>Upper limit on the number of assessments of the structure in one search.</summary>
        public const int DefaultMaxEvaluations = 20000;

        /// <summary>Number of groups that may become one size heavier at the same time in the exchange step.</summary>
        public const int DefaultExchangeDepth = 1;

        // =====================================================================
        //  Candidates and groups
        // =====================================================================

        /// <summary>
        /// The candidate sections, ordered by increasing area.
        /// tableLines: a cross-section table in the Karamba3D text format; null or empty = the
        /// built-in HEA list. names: optional, the sections of the table to use; null or empty = all.
        /// Only I-shaped sections are used.
        /// </summary>
        public static List<SectionRecord> Candidates(IList<string> tableLines, IList<string> names, List<string> warnings)
        {
            List<SectionRecord> all;
            bool ownTable = tableLines != null && tableLines.Any(l => !string.IsNullOrWhiteSpace(l));
            if (ownTable)
            {
                List<string> tableWarnings;
                all = SectionTable.Parse(tableLines, out tableWarnings);
                if (warnings != null) warnings.AddRange(tableWarnings.Select(w => "Section table: " + w));
            }
            else
            {
                all = HEA_Catalog.Entries.ToList();
            }

            int notI = all.Count(r => !IsIShape(r));
            if (notI > 0 && warnings != null)
                warnings.Add(notI.ToString(CultureInfo.InvariantCulture) + " section(s) of the table are not I-shaped and are not used.");
            all = all.Where(IsIShape).ToList();

            List<SectionRecord> picked;
            var wanted = (names == null) ? new List<string>() : names.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (wanted.Count == 0)
            {
                picked = all;
            }
            else
            {
                picked = new List<SectionRecord>();
                foreach (string name in wanted)
                {
                    SectionRecord rec = all.FirstOrDefault(r => Key(r.Name) == Key(name));
                    if (rec == null)
                        throw new ArgumentException($"Candidate '{name.Trim()}' is not an I-shaped section of the table.");
                    if (!picked.Contains(rec)) picked.Add(rec);
                }
            }

            if (picked.Count == 0)
                throw new ArgumentException("There are no candidate sections.");

            // by increasing area; the order of the table decides between equal areas
            return picked.Select((r, i) => new { r, i }).OrderBy(x => x.r.A).ThenBy(x => x.i).Select(x => x.r).ToList();
        }

        /// <summary>
        /// The group of every member. Each line of groupSpecs is "pattern:group", where pattern is
        /// an element tag, optionally ending in * (any tag that starts with what precedes it).
        /// The first line that matches decides. A member that no line matches forms a group with
        /// the other members that have the same element tag.
        /// </summary>
        public static string[] GroupOfMembers(TB_Model mdl, IList<string> groupSpecs)
        {
            if (mdl == null || mdl.Elem1Ds == null || mdl.Elem1Ds.Count == 0)
                throw new ArgumentException("The model has no elements.");

            var patterns = new List<KeyValuePair<string, string>>();
            if (groupSpecs != null)
            {
                foreach (string spec in groupSpecs)
                {
                    if (string.IsNullOrWhiteSpace(spec)) continue;
                    int cut = spec.LastIndexOf(':');
                    if (cut <= 0 || cut == spec.Length - 1)
                        throw new ArgumentException($"Group '{spec}': expected \"<tag or tag*>:<group>\".");
                    string pattern = spec.Substring(0, cut).Trim();
                    string group = spec.Substring(cut + 1).Trim();
                    if (pattern.Length == 0 || group.Length == 0)
                        throw new ArgumentException($"Group '{spec}': expected \"<tag or tag*>:<group>\".");
                    patterns.Add(new KeyValuePair<string, string>(pattern, group));
                }
            }

            foreach (KeyValuePair<string, string> pattern in patterns)
                if (!mdl.Elem1Ds.Any(e => Matches(e.Tag ?? "", pattern.Key)))
                    throw new ArgumentException($"Group '{pattern.Key}:{pattern.Value}': no element tag matches '{pattern.Key}'.");

            string[] groups = new string[mdl.Elem1Ds.Count];
            for (int m = 0; m < groups.Length; m++)
            {
                string tag = mdl.Elem1Ds[m].Tag ?? "";
                groups[m] = tag;
                foreach (KeyValuePair<string, string> pattern in patterns)
                {
                    if (!Matches(tag, pattern.Key)) continue;
                    groups[m] = pattern.Value;
                    break;
                }
            }
            return groups;
        }

        /// <summary>
        /// A copy of the model in which every member has the section of its group. Geometry, tags,
        /// orientation, buckling lengths, materials, supports and loads are kept.
        /// </summary>
        public static TB_Model WithSections(TB_Model mdl, string[] groupOfMember,
                                            IDictionary<string, SectionRecord> sectionOfGroup)
        {
            // one section object per (profile, material), shared by the members that use it
            var made = new List<Tuple<SectionRecord, TB_Material, TB_Section>>();

            var elems = new List<TB_Element_1D>();
            for (int m = 0; m < mdl.Elem1Ds.Count; m++)
            {
                TB_Element_1D e = mdl.Elem1Ds[m];
                if (e == null || e.Sec == null || e.Sec.Mat == null)
                    throw new ArgumentException($"Element {m} has no section or material.");

                SectionRecord rec = sectionOfGroup[groupOfMember[m]];
                TB_Material mat = e.Sec.Mat;

                TB_Section sec = null;
                foreach (Tuple<SectionRecord, TB_Material, TB_Section> t in made)
                    if (ReferenceEquals(t.Item1, rec) && ReferenceEquals(t.Item2, mat)) sec = t.Item3;
                if (sec == null)
                {
                    sec = new Section_I(mat, rec);
                    made.Add(Tuple.Create(rec, mat, sec));
                }

                elems.Add(new TB_Element_1D(e.Line, e.Tag, sec, e.Vz, e.Buckling_Length));
            }

            List<TB_Support> sups = (mdl.Sups ?? new List<TB_Support>()).Where(s => s != null).Select(s => s.DeepCopy()).ToList();
            List<TB_Load> loads = (mdl.Loads ?? new List<TB_Load>()).Where(l => l != null).Select(l => l.DeepCopy()).ToList();

            var copy = new TB_Model(elems, sups, loads);
            if (copy.Validity.Any(v => !v))
                throw new InvalidOperationException("The model could not be rebuilt: a support or a load does not sit on a node.");
            return copy;
        }

        // =====================================================================
        //  The search
        // =====================================================================

        /// <summary>
        /// Finds the lightest sections for which assess() reports every member as ok.
        /// candidates must be ordered by increasing area; groupOfMember gives the group of every
        /// member; assess analyses one trial model. exchangeDepth: how many groups may become one
        /// size heavier at the same time in the exchange step (0 = no exchange step).
        /// </summary>
        public static SectionOptimisationResult Run(
            TB_Model mdl,
            IList<SectionRecord> candidates,
            string[] groupOfMember,
            Func<TB_Model, MemberAssessment> assess,
            string criterion,
            bool higherIsBetter,
            int exchangeDepth = DefaultExchangeDepth,
            int maxEvaluations = DefaultMaxEvaluations,
            bool binarySearch = false,
            int restarts = 0,
            int seed = 1,
            int restartGroups = 0)
        {
            if (mdl == null) throw new ArgumentNullException(nameof(mdl));
            if (candidates == null || candidates.Count == 0) throw new ArgumentException("There are no candidate sections.");
            if (groupOfMember == null || groupOfMember.Length != mdl.Elem1Ds.Count)
                throw new ArgumentException("One group is needed for every member.");
            if (assess == null) throw new ArgumentNullException(nameof(assess));

            CultureInfo ci = CultureInfo.InvariantCulture;
            int nMem = mdl.Elem1Ds.Count;
            int nCand = candidates.Count;

            // groups in the order in which they first appear in the model
            List<string> groups = groupOfMember.Distinct().ToList();
            int nGroup = groups.Count;
            int[] groupIndex = groupOfMember.Select(g => groups.IndexOf(g)).ToArray();

            var res = new SectionOptimisationResult();
            res.Criterion = criterion;
            res.HigherIsBetter = higherIsBetter;
            res.Groups = groups;
            res.MemberGroups = (string[])groupOfMember.Clone();
            res.CandidateNames = candidates.Select(c => c.Name).ToList();

            // --- one trial design = one candidate per group ---
            Func<int[], Trial> build = idx =>
            {
                var pick = new Dictionary<string, SectionRecord>();
                for (int g = 0; g < nGroup; g++) pick[groups[g]] = candidates[idx[g]];

                var t = new Trial();
                t.Model = WithSections(mdl, groupOfMember, pick);
                t.Assessment = assess(t.Model);
                if (t.Assessment == null || t.Assessment.Ok.Length != nMem || t.Assessment.Value.Length != nMem)
                    throw new InvalidOperationException("The assessment did not return one result per member.");
                if (t.Assessment.Note == null || t.Assessment.Note.Length != nMem) t.Assessment.Note = new string[nMem];
                return t;
            };

            // What the search needs of a trial design is remembered, so that no design is assessed
            // twice. Null when the limit on the number of assessments is reached.
            maxEvaluations = Math.Max(1, maxEvaluations);
            var known = new Dictionary<string, Summary>();
            Func<int[], string> keyOf = idx => string.Join(",", idx.Select(i => i.ToString(ci)));
            string phase = "start";                          // part of the search, for the history
            int restartNo = 0;                               // restart number, for the history
            Func<int[], Summary> evaluate = idx =>
            {
                string key = keyOf(idx);
                Summary sum;
                if (known.TryGetValue(key, out sum)) return sum;
                if (res.Evaluations >= maxEvaluations) return null;

                Trial t = build(idx);
                res.Evaluations++;

                sum = new Summary();
                sum.Index = res.History.Count;
                sum.Weight = t.Model.Weight;
                sum.Failing = FailingGroups(t.Assessment, groupIndex, nGroup);
                sum.Worst = Worst(t.Assessment, higherIsBetter);
                known[key] = sum;

                // history: this trial, and the design the search stands on (unchanged until it moves on)
                var it = new SearchIteration
                {
                    Index = sum.Index,
                    Phase = phase,
                    Restart = restartNo,
                    Candidates = (int[])idx.Clone(),
                    Weight = sum.Weight,
                    Worst = sum.Worst,
                    Feasible = sum.Failing.Count == 0,
                };
                if (res.History.Count > 0)
                {
                    SearchIteration prev = res.History[res.History.Count - 1];
                    it.CurrentWeight = prev.CurrentWeight;
                    it.CurrentWorst = prev.CurrentWorst;
                    it.CurrentKey = prev.CurrentKey;
                }
                res.History.Add(it);
                return sum;
            };

            // the search moves on to design idx: from the latest assessment on, it is the current design
            Action<int[], Summary> moveTo = (idx, s) =>
            {
                if (s == null || res.History.Count == 0) return;
                SearchIteration last = res.History[res.History.Count - 1];
                last.CurrentKey = keyOf(idx);
                last.CurrentWeight = s.Weight;
                last.CurrentWorst = s.Worst;
                if (s.Index >= 0 && s.Index < res.History.Count) res.History[s.Index].Kept = true;   // where it was assessed
            };

            int[] current = new int[nGroup];                 // lightest candidate everywhere
            Summary trial = evaluate(current);
            moveTo(current, trial);
            res.WeightLightest = trial.Weight;
            res.WeightInitial = mdl.Weight;

            double[] groupLength = new double[nGroup];       // [m], for the weight of a change of section
            for (int m = 0; m < nMem; m++) groupLength[groupIndex[m]] += mdl.Elem1Ds[m].Line.Length;

            int step = 0;
            bool feasible = false, outOfBudget = false;
            res.Log.Add("step     #  " + "action".PadRight(41) + " " + "weight".PadLeft(9) + " " + "worst".PadLeft(10) + "   groups not ok");

            string action = "start: " + candidates[0].Name + " in every group";

            // ------------------------------------------------------------------
            //  Binary search (optional): all groups bisect their candidate list at once
            // ------------------------------------------------------------------
            if (binarySearch && nCand > 1 && trial.Failing.Count > 0)
            {
                res.Log.Add(LogLine(step, action, trial, trial.Failing.Count.ToString(ci)));

                // [lo, hi]: the lightest candidate that may still be enough .. the lightest known
                // (or, at first, assumed) to be enough. The lightest design is assessed already.
                int[] lo = new int[nGroup];
                int[] hi = new int[nGroup];
                for (int g = 0; g < nGroup; g++)
                {
                    bool fails = trial.Failing.Contains(g);
                    lo[g] = fails ? 1 : 0;
                    hi[g] = fails ? nCand - 1 : 0;
                }

                phase = "bisect";
                int round = 0;
                while (Enumerable.Range(0, nGroup).Any(g => lo[g] < hi[g]))
                {
                    int[] mid = new int[nGroup];
                    for (int g = 0; g < nGroup; g++) mid[g] = (lo[g] < hi[g]) ? (lo[g] + hi[g]) / 2 : hi[g];

                    Summary s = evaluate(mid);
                    if (s == null)
                    {
                        outOfBudget = true;
                        break;
                    }
                    round++;
                    step++;
                    int open = 0, lower = 0;
                    for (int g = 0; g < nGroup; g++)
                    {
                        if (lo[g] >= hi[g]) continue;
                        open++;
                        if (s.Failing.Contains(g)) lo[g] = mid[g] + 1;
                        else { hi[g] = mid[g]; lower++; }
                    }
                    moveTo(mid, s);
                    res.Log.Add(LogLine(step, Shorten("bisect " + round.ToString(ci) + ": " + open.ToString(ci) + " open group(s), " +
                                                      lower.ToString(ci) + " to the lower half", 41),
                                        s, s.Failing.Count.ToString(ci)));
                }

                if (!outOfBudget)
                {
                    Summary found = evaluate(hi);
                    if (found == null) outOfBudget = true;
                    else
                    {
                        current = hi;
                        trial = found;
                        moveTo(current, trial);
                        step++;
                        action = "bisection result (lightest that passed)";
                    }
                }
                phase = "up";
            }

            // ------------------------------------------------------------------
            //  Step up until every member passes
            // ------------------------------------------------------------------
            while (!outOfBudget)
            {
                List<int> failing = trial.Failing;
                res.Log.Add(LogLine(step, action, trial, failing.Count.ToString(ci)));

                if (failing.Count == 0)
                {
                    feasible = true;
                    break;
                }

                List<int> movable = failing.Where(g => current[g] < nCand - 1).ToList();
                if (movable.Count == 0)
                {
                    res.Warnings.Add("No design satisfies the criterion: the heaviest candidate (" + candidates[nCand - 1].Name +
                                     ") is not enough in group(s) " + string.Join(", ", failing.Select(g => groups[g])) + ".");
                    break;
                }

                foreach (int g in movable) current[g]++;
                phase = "up";
                Summary heavier = evaluate(current);
                if (heavier == null)
                {
                    foreach (int g in movable) current[g]--;       // not assessed: stay with the last design
                    outOfBudget = true;
                    break;
                }

                trial = heavier;
                moveTo(current, heavier);
                action = "up: " + Shorten(string.Join(", ", movable.Select(g => groups[g] + " " + candidates[current[g]].Name)), 37);
                step++;
            }

            // ------------------------------------------------------------------
            //  Step down: from a design in which every member passes, the next lighter candidate
            //  in one group at a time, heaviest group first, until no group can be made lighter.
            //  frozen: groups that are left alone (null = none). Returns the candidate of each group.
            // ------------------------------------------------------------------
            Func<int[], bool[], bool, int[]> stepDown = (start, frozen, log) =>
            {
                int[] idx = (int[])start.Clone();
                bool again = true;
                while (again && !outOfBudget)
                {
                    again = false;

                    // heaviest group first (area times length)
                    int[] at = idx;
                    List<int> order = Enumerable.Range(0, nGroup)
                        .OrderByDescending(g => candidates[at[g]].A * groupLength[g]).ThenBy(g => g).ToList();

                    foreach (int g in order)
                    {
                        if ((frozen != null && frozen[g]) || idx[g] == 0) continue;

                        idx[g]--;
                        Summary lighter = evaluate(idx);
                        if (lighter == null)
                        {
                            idx[g]++;
                            outOfBudget = true;
                            break;
                        }

                        bool ok = lighter.Failing.Count == 0;
                        if (log)
                        {
                            step++;
                            string what = "down: " + groups[g] + " " + candidates[idx[g] + 1].Name + " -> " + candidates[idx[g]].Name;
                            res.Log.Add(LogLine(step, Shorten(what, 41), lighter,
                                ok ? "0   kept" : lighter.Failing.Count.ToString(ci) + "   rejected"));
                        }

                        if (ok)
                        {
                            again = true;
                            if (log) moveTo(idx, lighter);         // the main search moves on (not inside an exchange)
                        }
                        else idx[g]++;
                    }
                }
                return idx;
            };

            phase = "down";
            if (feasible && !outOfBudget) current = stepDown(current, null, true);

            // ------------------------------------------------------------------
            //  Restarts (optional): from the best design so far, some groups get random sizes;
            //  step up where a member fails, step down; kept if lighter.
            // ------------------------------------------------------------------
            if (feasible && !outOfBudget)
            {
                Summary bestSum = evaluate(current);                 // assessed already
                if (bestSum != null) res.SearchWeights.Add(bestSum.Weight);
                int nKick = restartGroups > 0 ? Math.Min(restartGroups, nGroup) : DefaultRestartGroups(nGroup);
                res.Restarts = Math.Max(0, restarts);
                res.RestartGroups = nKick;
                res.Seed = seed;
                var rnd = new Random(seed);

                for (int r = 1; r <= restarts && !outOfBudget && bestSum != null; r++)
                {
                    phase = "restart";
                    restartNo = r;

                    // the best design, with nKick groups at random sizes up to two above the heaviest in use
                    int kmax = Math.Min(nCand - 1, current.Max() + 2);
                    int[] idx = (int[])current.Clone();
                    List<int> changed = Enumerable.Range(0, nGroup).OrderBy(x => rnd.Next()).Take(nKick).OrderBy(x => x).ToList();
                    foreach (int g in changed) idx[g] = rnd.Next(0, kmax + 1);

                    Summary s = evaluate(idx);
                    bool ok = s != null;

                    // step up where a member fails
                    while (ok && s.Failing.Count > 0)
                    {
                        List<int> movable = s.Failing.Where(g => idx[g] < nCand - 1).ToList();
                        if (movable.Count == 0) { ok = false; break; }
                        foreach (int g in movable) idx[g]++;
                        s = evaluate(idx);
                        if (s == null) ok = false;
                    }
                    if (outOfBudget || s == null) { outOfBudget = true; break; }

                    string what = "restart " + r.ToString(ci) + ": " + string.Join(", ", changed.Select(g => groups[g])) + " random";
                    if (!ok)
                    {
                        step++;
                        res.Log.Add(LogLine(step, Shorten(what, 41), s, s.Failing.Count.ToString(ci) + "   no design found"));
                        continue;
                    }

                    // step down, then keep it if it is lighter
                    int[] down = stepDown(idx, null, false);
                    if (outOfBudget) break;
                    Summary end = evaluate(down);
                    if (end == null) { outOfBudget = true; break; }
                    res.SearchWeights.Add(end.Weight);

                    bool lighter = end.Failing.Count == 0 && end.Weight < bestSum.Weight * (1.0 - 1e-12);
                    step++;
                    res.Log.Add(LogLine(step, Shorten(what, 41), end, lighter ? "0   kept" : "0   not lighter"));
                    if (lighter)
                    {
                        current = down;
                        bestSum = end;
                        moveTo(down, end);
                    }
                }
                restartNo = 0;
            }
            phase = "exchange";

            // ------------------------------------------------------------------
            //  Exchange: one group (with depth 2 also every pair of groups, ...) one size heavier,
            //  the others stepped down again. Kept if the structure becomes lighter; the search
            //  then continues from the new design.
            // ------------------------------------------------------------------
            int exchangesTried = 0;
            bool improved = feasible && exchangeDepth > 0;
            while (improved && !outOfBudget)
            {
                improved = false;

                Summary baseTrial = evaluate(current);
                if (baseTrial == null)
                {
                    outOfBudget = true;
                    break;
                }
                double baseWeight = baseTrial.Weight;

                int[] now = current;
                List<int> raisable = Enumerable.Range(0, nGroup).Where(g => now[g] < nCand - 1).ToList();

                for (int size = 1; size <= Math.Min(exchangeDepth, raisable.Count) && !improved && !outOfBudget; size++)
                {
                    // every set of 'size' groups, the cheapest increase first
                    List<int[]> sets = Combinations(raisable, size)
                        .OrderBy(set => set.Sum(g => (candidates[now[g] + 1].A - candidates[now[g]].A) * groupLength[g]))
                        .ToList();

                    foreach (int[] set in sets)
                    {
                        int[] raised = (int[])current.Clone();
                        bool[] frozen = new bool[nGroup];
                        foreach (int g in set)
                        {
                            raised[g]++;
                            frozen[g] = true;
                        }

                        Summary r = evaluate(raised);
                        if (r == null)
                        {
                            outOfBudget = true;
                            break;
                        }
                        exchangesTried++;
                        if (r.Failing.Count > 0) continue;          // a heavier group can make another one fail

                        int[] reduced = stepDown(raised, frozen, false);
                        if (outOfBudget) break;

                        Summary result = evaluate(reduced);
                        if (result == null)
                        {
                            outOfBudget = true;
                            break;
                        }
                        if (!(result.Weight < baseWeight * (1.0 - 1e-12))) continue;

                        // lighter: describe the exchange and continue from the new design
                        var parts = new List<string>();
                        for (int k = 0; k < nGroup; k++)
                            if (reduced[k] != current[k])
                                parts.Add(groups[k] + " " + candidates[current[k]].Name + " -> " + candidates[reduced[k]].Name);
                        step++;
                        res.Log.Add(LogLine(step, Shorten("exchange: " + string.Join(", ", set.Select(g => groups[g])) + " up", 41),
                                            result, "0   kept"));
                        res.Log.Add("           " + string.Join("; ", parts));

                        moveTo(reduced, result);
                        phase = "down";
                        current = stepDown(reduced, null, true);
                        phase = "exchange";
                        improved = true;
                        break;
                    }
                }
            }
            if (feasible && exchangeDepth > 0)
                res.Log.Add("exchanges tried (up to " + exchangeDepth.ToString(ci) +
                            " group(s) one size heavier, the others stepped down again): " + exchangesTried.ToString(ci));

            // where the optimum was found (its assessment), and from where on the search stands on it
            string finalKey = keyOf(current);
            Summary finalSum;
            if (known.TryGetValue(finalKey, out finalSum)) res.OptimumIteration = finalSum.Index;
            for (int i = res.History.Count - 1; i >= 0 && res.History[i].CurrentKey == finalKey; i--)
                res.OptimumAccepted = i;

            if (outOfBudget)
                res.Warnings.Add("The search stopped after " + maxEvaluations.ToString(ci) +
                                 " assessments of the structure; the result is the last design that was assessed.");

            // ------------------------------------------------------------------
            //  Result: the design the search ended on, in full
            // ------------------------------------------------------------------
            Trial last = build(current);
            res.Feasible = feasible && last.Assessment.AllOk;
            res.Model = last.Model;
            res.Final = last.Assessment;
            res.Weight = last.Model.Weight;
            res.MemberSections = last.Model.Elem1Ds.Select(e => e.Sec.Tag).ToArray();

            double[] finalWeights = GroupWeights(last.Model, groupIndex, nGroup);
            for (int g = 0; g < nGroup; g++)
            {
                res.GroupSections.Add(candidates[current[g]].Name);
                res.GroupSizes.Add(groupIndex.Count(v => v == g));
                res.GroupWeights.Add(finalWeights[g]);
            }

            for (int m = 0; m < nMem; m++)
                if (!res.Final.Ok[m])
                    res.Warnings.Add("Member [" + m.ToString(ci) + "] " + last.Model.Elem1Ds[m].Tag + " (" + res.MemberSections[m] +
                                     ") does not satisfy the criterion" +
                                     (double.IsNaN(res.Final.Value[m]) ? "" : ": " + res.Final.Value[m].ToString("F3", ci)) +
                                     (string.IsNullOrEmpty(res.Final.Note[m]) ? "." : " (" + res.Final.Note[m] + ")."));
            return res;
        }

        // =====================================================================
        //  The two criteria
        // =====================================================================

        /// <summary>
        /// Reliability-based design: beta of the governing limit state of every member >= betaTarget.
        /// A limit state for which FORM did not converge counts as not verified. The system beta is
        /// computed for the final design only and is not a constraint.
        /// </summary>
        public static SectionOptimisationResult RunReliability(
            TB_Model mdl,
            IList<SectionRecord> candidates,
            string[] groupOfMember,
            List<RandomVariable> vars,
            List<MemberVariable> bindings,
            double betaTarget,
            double[,] correlation = null,
            IDictionary<string, double[]> bucklingLengths = null,
            int maxIterations = 300,
            int systemSamples = SystemReliability.DefaultSamples,
            int exchangeDepth = DefaultExchangeDepth,
            int maxEvaluations = DefaultMaxEvaluations,
            MemberCheckOptions options = null,
            bool binarySearch = false,
            int restarts = 0,
            int seed = 1,
            int restartGroups = 0)
        {
            if (double.IsNaN(betaTarget)) throw new ArgumentException("The target beta is not a number.");
            MemberCheckOptions opt = options ?? MemberCheckOptions.Default();

            Func<MemberReliabilityResult, MemberAssessment> toAssessment = r =>
            {
                int n = r.Members.Count;
                var a = new MemberAssessment { Ok = new bool[n], Value = new double[n], Note = new string[n], Reliability = r };
                for (int m = 0; m < n; m++)
                {
                    a.Value[m] = double.NaN;
                    a.Ok[m] = true;

                    // every limit state of the member must be verified, not only the governing one
                    bool any = false;
                    foreach (MemberLimitStateResult row in r.LimitStates)
                    {
                        if (row.Member != m) continue;
                        any = true;
                        if (!row.HasBeta)
                        {
                            a.Ok[m] = false;
                            a.Note[m] = MemberLimitStateResult.ModeName(row.Mode, row.NMean) + ": " + row.Status;
                        }
                        else if (!row.Converged)
                        {
                            a.Ok[m] = false;
                            a.Note[m] = MemberLimitStateResult.ModeName(row.Mode, row.NMean) + ": FORM did not converge";
                        }
                    }
                    if (!any) continue;                         // not stressed: nothing to verify

                    MemberLimitStateResult g = r.Governing[m];
                    if (g != null)
                    {
                        a.Value[m] = g.Form.Beta;
                        if (!(g.Form.Beta >= betaTarget)) a.Ok[m] = false;
                    }
                }
                return a;
            };

            SectionOptimisationResult res = Run(
                mdl, candidates, groupOfMember,
                trial => toAssessment(MemberReliability.Run(trial, vars, bindings, correlation, bucklingLengths,
                                                            maxIterations, false, -1, opt)),
                "beta >= " + betaTarget.ToString("0.00##", CultureInfo.InvariantCulture) +
                " in every member (the system beta is reported, not constrained); members checked for " + opt.Describe(),
                true, exchangeDepth, maxEvaluations, binarySearch, restarts, seed, restartGroups);

            // the final design once more, now with the system reliability, and the input model for comparison
            res.Final = toAssessment(MemberReliability.Run(res.Model, vars, bindings, correlation, bucklingLengths,
                                                           maxIterations, false, Math.Max(0, systemSamples), opt));
            res.Warnings.AddRange(res.Final.Reliability.Warnings);

            res.ReliabilityOptimised = res.Final.Reliability;
            res.ReliabilityInitial = MemberReliability.Run(mdl, vars, bindings, correlation, bucklingLengths,
                                                           maxIterations, false, Math.Max(0, systemSamples), opt);
            res.BetaTarget = betaTarget;
            return res;
        }

        /// <summary>
        /// Eurocode design: utilisation of every member &lt;= maxUtilisation in the less favourable of
        /// (6.10a) and (6.10b).
        /// </summary>
        public static SectionOptimisationResult RunEurocode(
            TB_Model mdl,
            IList<SectionRecord> candidates,
            string[] groupOfMember,
            IList<EcLoadCase> loadCases,
            IDictionary<string, double[]> bucklingLengths = null,
            double maxUtilisation = 1.0,
            EcFactors factors = null,
            int exchangeDepth = DefaultExchangeDepth,
            int maxEvaluations = DefaultMaxEvaluations,
            MemberCheckOptions options = null,
            bool binarySearch = false,
            int restarts = 0,
            int seed = 1,
            int restartGroups = 0)
        {
            if (!(maxUtilisation > 0.0)) throw new ArgumentException("The utilisation limit must be positive.");
            MemberCheckOptions opt = options ?? MemberCheckOptions.Default();

            Func<TB_Model, MemberAssessment> assess = trial =>
            {
                EcCheckResult r = EurocodeMemberCheck.Run(trial, loadCases, bucklingLengths, factors, opt);
                int n = r.Members.Count;
                var a = new MemberAssessment { Ok = new bool[n], Value = new double[n], Note = new string[n], Eurocode = r };
                for (int m = 0; m < n; m++)
                {
                    EcMemberCheck g = r.Governing[m];
                    a.Value[m] = (g == null) ? double.NaN : g.Utilisation;
                    a.Ok[m] = (g == null) || g.Utilisation <= maxUtilisation;
                }
                return a;
            };

            SectionOptimisationResult res = Run(
                mdl, candidates, groupOfMember, assess,
                "Eurocode utilisation <= " + maxUtilisation.ToString("0.00##", CultureInfo.InvariantCulture) +
                " in every member; members checked for " + opt.Describe(),
                false, exchangeDepth, maxEvaluations, binarySearch, restarts, seed, restartGroups);
            res.Warnings.AddRange(res.Final.Eurocode.Warnings);

            res.EurocodeOptimised = res.Final.Eurocode;
            res.EurocodeInitial = EurocodeMemberCheck.Run(mdl, loadCases, bucklingLengths, factors, opt);
            return res;
        }

        /// <summary>
        /// The implied reliability of a design that was optimised with another criterion (the
        /// Eurocode design): FORM on every member limit state of the input model and of the
        /// optimised design, with the same random variables, bindings and buckling lengths as a
        /// reliability-based design would use. betaTarget is only used to count the members below it.
        /// </summary>
        public static void AddImpliedReliability(
            SectionOptimisationResult res,
            TB_Model initial,
            List<RandomVariable> vars,
            List<MemberVariable> bindings,
            double betaTarget,
            double[,] correlation = null,
            IDictionary<string, double[]> bucklingLengths = null,
            int maxIterations = 300,
            int systemSamples = SystemReliability.DefaultSamples,
            MemberCheckOptions options = null)
        {
            if (res == null || res.Model == null) throw new ArgumentNullException(nameof(res));
            if (initial == null) throw new ArgumentNullException(nameof(initial));

            res.ReliabilityInitial = MemberReliability.Run(initial, vars, bindings, correlation, bucklingLengths,
                                                           maxIterations, false, Math.Max(0, systemSamples), options);
            res.ReliabilityOptimised = MemberReliability.Run(res.Model, vars, bindings, correlation, bucklingLengths,
                                                             maxIterations, false, Math.Max(0, systemSamples), options);
            res.BetaTarget = betaTarget;
        }

        // =====================================================================
        //  Helpers
        // =====================================================================

        /// <summary>A trial design in full.</summary>
        private class Trial
        {
            public TB_Model Model;
            public MemberAssessment Assessment;
        }

        /// <summary>Groups given random sizes in a restart when not set: half of the groups, at least 3 (or all, if fewer).</summary>
        public static int DefaultRestartGroups(int nGroup)
        {
            return Math.Min(nGroup, Math.Max(3, (nGroup + 1) / 2));
        }

        /// <summary>What the search keeps of a trial design.</summary>
        private class Summary
        {
            /// <summary>Assessment number (index in the history).</summary>
            public int Index = -1;
            public double Weight;
            /// <summary>Groups with a member that does not satisfy the criterion.</summary>
            public List<int> Failing;
            public double Worst;
        }

        /// <summary>All sets of 'size' different items, each in the order of the list.</summary>
        private static List<int[]> Combinations(List<int> items, int size)
        {
            var sets = new List<int[]>();
            if (size < 1 || size > items.Count) return sets;

            int[] pos = Enumerable.Range(0, size).ToArray();
            while (true)
            {
                sets.Add(pos.Select(p => items[p]).ToArray());

                int k = size - 1;
                while (k >= 0 && pos[k] == items.Count - size + k) k--;
                if (k < 0) break;
                pos[k]++;
                for (int j = k + 1; j < size; j++) pos[j] = pos[j - 1] + 1;
            }
            return sets;
        }

        private static List<int> FailingGroups(MemberAssessment a, int[] groupIndex, int nGroup)
        {
            bool[] fails = new bool[nGroup];
            for (int m = 0; m < groupIndex.Length; m++)
                if (!a.Ok[m]) fails[groupIndex[m]] = true;
            return Enumerable.Range(0, nGroup).Where(g => fails[g]).ToList();
        }

        private static double[] GroupWeights(TB_Model mdl, int[] groupIndex, int nGroup)
        {
            double[] w = new double[nGroup];
            for (int m = 0; m < groupIndex.Length; m++) w[groupIndex[m]] += mdl.Elem1Ds[m].Weight;
            return w;
        }

        /// <summary>The worst value over the members: lowest if higher is better, else highest. NaN if none.</summary>
        private static double Worst(MemberAssessment a, bool higherIsBetter)
        {
            double worst = double.NaN;
            foreach (double v in a.Value)
            {
                if (double.IsNaN(v)) continue;
                if (double.IsNaN(worst) || (higherIsBetter ? v < worst : v > worst)) worst = v;
            }
            return worst;
        }

        private static string LogLine(int step, string action, Summary t, string tail)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            return step.ToString(ci).PadLeft(4) + "  " + ("#" + t.Index.ToString(ci)).PadLeft(4) + "  " + action.PadRight(41) + " " +
                   t.Weight.ToString("F1", ci).PadLeft(9) + " " +
                   (double.IsNaN(t.Worst) ? "-" : t.Worst.ToString("F3", ci)).PadLeft(10) + "   " + tail;
        }

        private static string Shorten(string s, int length)
        {
            return (s.Length <= length) ? s : s.Substring(0, length - 3) + "...";
        }

        private static bool IsIShape(SectionRecord r)
        {
            return string.Equals((r.Shape ?? "").Trim(), "I", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Name without spaces, upper case: "hea 200" and "HEA200" are the same.</summary>
        private static string Key(string s)
        {
            return new string((s ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        }

        /// <summary>Element tag against a pattern: equal, or the pattern ends in * and the tag starts with the rest.</summary>
        private static bool Matches(string tag, string pattern)
        {
            if (pattern.EndsWith("*"))
                return tag.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(tag, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
