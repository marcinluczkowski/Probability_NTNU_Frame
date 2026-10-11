// =============================================================================
//  ST_LS_EurocodeCheck.cs
//
//  WHAT IT IS
//  The Eurocode (partial factor) check of every member of a linear-elastic truss
//  model, on the SAME member forces, buckling lengths and resistance formulas as
//  the reliability limit states in ST_LS_MemberLimitState.cs. It is the
//  deterministic counterpart of MemberReliability.Run: one gives beta per member,
//  this one gives the utilisation per member.
//
//  ACTIONS (EN 1990 with the Norwegian NA, table NA.A1.2(B), STR)
//  The loads in the model are the CHARACTERISTIC loads, one load case per action.
//  Every load case is declared permanent (G) or variable (Q, with its psi0).
//  Axial only: for each member the design axial force is the less favourable of
//
//    (6.10a)   sum gammaG Gk  +  sum gammaQ psi0 Qk
//    (6.10b)   sum xi gammaG Gk  +  gammaQ Qk,1  +  sum gammaQ psi0,i Qk,i
//
//  A permanent load case gets gammaG (or xi gammaG) where it is unfavourable for
//  the member and gammaG,inf where it is favourable. A variable load case is left
//  out where it is favourable. In (6.10b) every unfavourable variable load case is
//  tried as the leading one. The member force is linear in the loads, so the
//  combination is done on the member forces of the single load cases: no further
//  FE solve is needed.
//
//  Each permanent load case is treated as one source (EN 1990 table A1.2(B),
//  note 3): all its loads get the same factor.
//
//  RESISTANCE (EN 1993-1-1 with the Norwegian NA), WITH BENDING (default)
//  The axial force and the end moments from the rigid joints, checked by
//  MemberInteraction (ST_LS_MemberInteraction.cs): cross-section 6.2.1(7) and
//  member buckling 6.3.3 (6.61, 6.62) with Annex B and lateral-torsional buckling.
//  N and the moments of one combination act together, so "unfavourable" is not
//  known in advance: every choice of factors in (6.10a) and (6.10b) is tried
//  (CombinationsOf) and the worst is kept for each check.
//
//  RESISTANCE, AXIAL ONLY (MemberCheckOptions.Bending = false)
//    tension             N_pl,Rd = A fy / gammaM0                   (6.2.3)
//    flexural buckling   N_b,Rd  = chi A fy / gammaM1, about y and z (6.3.1)
//  with the nominal fy and E of the material. chi, the slenderness, the buckling
//  lengths and the imperfection factors are the ones the limit states use.
//
//  NOT CHECKED: net-section fracture (N_u,Rd), shear and torsion, class 4 sections
//  (a warning is given) - exactly as in the limit states.
//
//  UNITS: forces [N], A [mm2], fy and E [N/mm2], lengths [m].
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Classes.Reliability
{
    public enum EcAction
    {
        Permanent,
        Variable
    }

    /// <summary>How one load case of the model enters the load combinations.</summary>
    public class EcLoadCase
    {
        /// <summary>psi0 used when none is given (imposed loads of category A to D, snow).</summary>
        public const double DefaultPsi0 = 0.7;

        public int LoadCase = 0;
        public EcAction Action = EcAction.Permanent;
        /// <summary>Combination factor of a variable action. Not used for a permanent action.</summary>
        public double Psi0 = DefaultPsi0;

        /// <summary>
        /// Parses a compact load case string.
        ///
        ///   "0:G"        load case 0 is a permanent action
        ///   "1:Q"        load case 1 is a variable action with psi0 = 0.7
        ///   "1:Q:0.6"    load case 1 is a variable action with psi0 = 0.6
        /// </summary>
        public static EcLoadCase Parse(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new ArgumentException("Empty load case spec.");

            string[] t = spec.Split(':');
            int lc;
            if (t.Length < 2 || !int.TryParse(t[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out lc))
                throw new ArgumentException($"'{spec}': expected <loadCase>:G or <loadCase>:Q[:<psi0>].");

            var c = new EcLoadCase { LoadCase = lc };

            switch (t[1].Trim().ToUpperInvariant())
            {
                case "G":
                case "PERMANENT":
                    c.Action = EcAction.Permanent;
                    if (t.Length > 2)
                        throw new ArgumentException($"'{spec}': a permanent action has no psi0.");
                    return c;

                case "Q":
                case "VARIABLE":
                    c.Action = EcAction.Variable;
                    if (t.Length > 2)
                    {
                        double psi;
                        if (!double.TryParse(t[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out psi))
                            throw new ArgumentException($"'{spec}': psi0 '{t[2]}' is not a number.");
                        if (!(psi >= 0.0) || !(psi <= 1.0))
                            throw new ArgumentException($"'{spec}': psi0 must be between 0 and 1.");
                        c.Psi0 = psi;
                    }
                    return c;

                default:
                    throw new ArgumentException($"'{spec}': unknown action '{t[1].Trim()}'. Use G (permanent) or Q (variable).");
            }
        }

        public override string ToString()
        {
            return (Action == EcAction.Permanent)
                ? "permanent"
                : "variable, psi0 = " + Psi0.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Partial factors. The defaults are the Norwegian NA values in EurocodeConstants.</summary>
    public class EcFactors
    {
        public double GammaG = Plugin_test_1.Reliability.EurocodeConstants.GammaG;
        public double GammaGInf = Plugin_test_1.Reliability.EurocodeConstants.GammaG_fav;
        public double Xi = Plugin_test_1.Reliability.EurocodeConstants.Xi;
        public double GammaQ = Plugin_test_1.Reliability.EurocodeConstants.GammaQ;
        public double GammaM0 = Plugin_test_1.Reliability.EurocodeConstants.GammaM;
        public double GammaM1 = Plugin_test_1.Reliability.EurocodeConstants.GammaM1;
    }

    /// <summary>The check of one (member, mode).</summary>
    public class EcMemberCheck
    {
        public int Member;
        public string Tag = "";
        public MemberMode Mode;
        /// <summary>Design axial force [N], tension positive.</summary>
        public double NEd;
        /// <summary>Design resistance [N], positive.</summary>
        public double NRd;
        public double Utilisation;
        /// <summary>The governing combination, e.g. "6.10b: 1.20 LC0 + 1.50 LC1".</summary>
        public string Combination = "";
        /// <summary>Factor on each load case in the governing combination, in the order of the load cases.</summary>
        public double[] Factors = new double[0];
        /// <summary>Slenderness and reduction factor. NaN for tension.</summary>
        public double LambdaBar = double.NaN;
        public double Chi = double.NaN;
        /// <summary>With bending: largest design end moments about y and z [N.m] and chi_LT (NaN where not used).</summary>
        public double MyEd = 0.0;
        public double MzEd = 0.0;
        public double ChiLT = double.NaN;
        /// <summary>With bending: the expression that governed ("6.2.1(7)", "6.61", "6.62").</summary>
        public string Clause = "";
    }

    public class EcCheckResult
    {
        public List<MemberData> Members = new List<MemberData>();
        public List<EcLoadCase> LoadCases = new List<EcLoadCase>();
        public EcFactors Factors = new EcFactors();
        /// <summary>How the members were checked (axial only, or axial force + bending).</summary>
        public MemberCheckOptions Options = MemberCheckOptions.Default();
        /// <summary>Axial force of each member under each load case as modelled [N], [member][load case].</summary>
        public double[][] NChar = new double[0][];
        /// <summary>Every check that was made, in member order.</summary>
        public List<EcMemberCheck> Checks = new List<EcMemberCheck>();
        /// <summary>The check with the highest utilisation of each member. Null where no combination stresses the member axially.</summary>
        public EcMemberCheck[] Governing = new EcMemberCheck[0];
        /// <summary>Index of the member with the highest utilisation, -1 if none.</summary>
        public int CriticalMember = -1;
        public double MaxUtilisation = double.NaN;
        public List<string> Warnings = new List<string>();

        public string Report()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.AppendLine("EUROCODE MEMBER CHECK (EN 1993-1-1 and EN 1990 with the Norwegian NA, linear analysis)");
            sb.AppendLine();
            sb.AppendLine("Load cases (the modelled loads are the characteristic loads):");
            foreach (EcLoadCase lc in LoadCases)
                sb.AppendLine("  LC" + lc.LoadCase.ToString(ci) + " : " + lc.ToString());
            sb.AppendLine();
            sb.AppendLine("Members checked for " + Options.Describe() + ".");
            sb.AppendLine(Options.Bending
                ? "Combinations: (6.10a) and (6.10b) with every choice of favourable / unfavourable factors; the worst per check."
                : "Combinations: the less favourable of (6.10a) and (6.10b) for each member.");
            sb.AppendLine("  gammaG " + Factors.GammaG.ToString("0.00", ci) + " / " + Factors.GammaGInf.ToString("0.00", ci) +
                          ", xi " + Factors.Xi.ToString("0.00", ci) + ", gammaQ " + Factors.GammaQ.ToString("0.00", ci) +
                          ", gammaM0 " + Factors.GammaM0.ToString("0.00", ci) + ", gammaM1 " + Factors.GammaM1.ToString("0.00", ci));
            sb.AppendLine();

            sb.AppendLine("Checks (N tension positive" + (Options.Bending ? "; My, Mz = largest design end moments" : "") + "):");
            sb.AppendLine(Options.Bending
                ? "  member            mode       NEd [kN]  NRd [kN]  My [kNm]  Mz [kNm]   lambda     chi  chi_LT  clause     util   combination"
                : "  member            mode       NEd [kN]  NRd [kN]   lambda     chi     util   combination");
            foreach (EcMemberCheck c in Checks)
            {
                string name = "[" + c.Member.ToString(ci) + "] " + c.Tag;
                sb.Append("  " + name.PadRight(17) + " ");
                sb.Append(MemberLimitStateResult.ModeName(c.Mode).PadRight(10) + " ");
                sb.Append((c.NEd * 1e-3).ToString("F1", ci).PadLeft(8) + "  ");
                sb.Append((c.NRd * 1e-3).ToString("F1", ci).PadLeft(8) + " ");
                if (Options.Bending)
                {
                    sb.Append((c.MyEd * 1e-3).ToString("F2", ci).PadLeft(9) + " ");
                    sb.Append((c.MzEd * 1e-3).ToString("F2", ci).PadLeft(9) + " ");
                }
                sb.Append((double.IsNaN(c.LambdaBar) ? "-" : c.LambdaBar.ToString("F3", ci)).PadLeft(8) + " ");
                sb.Append((double.IsNaN(c.Chi) ? "-" : c.Chi.ToString("F3", ci)).PadLeft(7) + " ");
                if (Options.Bending)
                {
                    sb.Append((double.IsNaN(c.ChiLT) ? "-" : c.ChiLT.ToString("F3", ci)).PadLeft(7) + "  ");
                    sb.Append(c.Clause.PadRight(8) + " ");
                }
                sb.Append(c.Utilisation.ToString("F3", ci).PadLeft(8) + "   ");
                sb.AppendLine(c.Combination);
            }
            sb.AppendLine();

            if (CriticalMember >= 0)
            {
                EcMemberCheck g = Governing[CriticalMember];
                sb.AppendLine("Highest utilisation: " + MaxUtilisation.ToString("F3", ci) + " in member [" +
                              CriticalMember.ToString(ci) + "] " + g.Tag + " (" +
                              MemberLimitStateResult.ModeName(g.Mode) + ").");

                int over = 0;
                for (int m = 0; m < Governing.Length; m++)
                    if (Governing[m] != null && Governing[m].Utilisation > 1.0) over++;
                sb.AppendLine(over == 0
                    ? "All members satisfy the check."
                    : over.ToString(ci) + " member(s) have a utilisation above 1.");
            }

            int unloaded = 0;
            for (int m = 0; m < Governing.Length; m++)
                if (Governing[m] == null) unloaded++;
            if (unloaded > 0)
                sb.AppendLine(unloaded.ToString(ci) + (Options.Bending
                    ? " member(s) are not stressed by any combination and are not checked."
                    : " member(s) are not stressed axially by any combination and are not checked."));

            sb.AppendLine();
            sb.AppendLine(Options.Bending
                ? "The check includes the bending moments from the rigid joints (as the reliability limit states do)."
                : "The check uses the axial force only (as the reliability limit states do).");

            if (Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (string w in Warnings) sb.AppendLine("  " + w);
            }
            return sb.ToString();
        }
    }

    public static class EurocodeMemberCheck
    {
        /// <summary>
        /// Checks every member: tension where a combination puts it in tension, flexural buckling
        /// about y and z where one puts it in compression. Every load case of the model must be
        /// declared in loadCases. bucklingLengths: optional, element tag -> { Lcr,y ; Lcr,z } in [m].
        /// </summary>
        public static EcCheckResult Run(
            TB_Model mdl,
            IList<EcLoadCase> loadCases,
            IDictionary<string, double[]> bucklingLengths = null,
            EcFactors factors = null,
            MemberCheckOptions options = null)
        {
            if (mdl == null) throw new ArgumentNullException(nameof(mdl));
            if (loadCases == null || loadCases.Count == 0)
                throw new ArgumentException("Declare every load case as permanent or variable, e.g. \"0:G\" and \"1:Q:0.7\".");

            EcFactors f = factors ?? new EcFactors();
            if (!(f.GammaM0 > 0.0) || !(f.GammaM1 > 0.0))
                throw new ArgumentException("The partial factors on the resistance must be positive.");

            // --- every load case of the model must be declared, and only once ---
            var declared = new HashSet<int>();
            foreach (EcLoadCase lc in loadCases)
                if (!declared.Add(lc.LoadCase))
                    throw new ArgumentException($"Load case {lc.LoadCase} is declared more than once.");

            int[] modelLcs = mdl.LCs ?? new int[0];
            List<int> missing = modelLcs.Where(id => !declared.Contains(id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
                throw new ArgumentException(
                    "The model has loads in load case(s) " +
                    string.Join(", ", missing.Select(id => id.ToString(CultureInfo.InvariantCulture))) +
                    " that are not declared as permanent (G) or variable (Q).");

            // --- member forces of the single load cases, from the same FE engine as the limit states ---
            // One load-case variable per load case with reference 1: the variable is then a
            // multiplier on the modelled loads and dN/d(variable) is the member force of that
            // load case alone.
            var variables = new List<MemberVariable>();
            foreach (EcLoadCase lc in loadCases)
                variables.Add(new MemberVariable
                {
                    Name = "LC" + lc.LoadCase.ToString(CultureInfo.InvariantCulture),
                    Kind = MemberVarKind.LoadCase,
                    LoadCase = lc.LoadCase,
                    Reference = 1.0
                });

            var ls = new LS_MemberLimitStates(mdl, variables, bucklingLengths, options);
            int nMem = ls.Members.Count;
            int nLc = loadCases.Count;

            double[] ones = Enumerable.Repeat(1.0, nLc).ToArray();
            if (ls.AxialForces(ones) == null)
                throw new InvalidOperationException("The model could not be evaluated.");

            var res = new EcCheckResult();
            res.Options = ls.Options;
            res.Members = ls.Members;
            res.LoadCases = loadCases.ToList();
            res.Factors = f;
            res.NChar = new double[nMem][];
            res.Governing = new EcMemberCheck[nMem];

            if (ls.Options.Bending)
            {
                RunWithBending(mdl, ls, res, f);
                return res;
            }

            int class4 = 0;

            for (int m = 0; m < nMem; m++)
            {
                MemberData md = ls.Members[m];
                double tol = MemberReliability.ZeroForce * md.Fy0 * md.A;

                double[] nk = new double[nLc];
                for (int i = 0; i < nLc; i++) nk[i] = ls.Fe.dN[m][i];
                res.NChar[m] = nk;

                EcMemberCheck best = null;

                // --- tension: the combination with the largest tensile force ---
                double[] facT;
                string combT;
                double tension = DesignEffect(nk, +1.0, res.LoadCases, f, out facT, out combT);
                if (tension > tol)
                {
                    var c = new EcMemberCheck
                    {
                        Member = m, Tag = md.Tag, Mode = MemberMode.Tension,
                        NEd = tension,
                        NRd = md.A * md.Fy0 / f.GammaM0,
                        Combination = combT, Factors = facT
                    };
                    c.Utilisation = c.NEd / c.NRd;
                    res.Checks.Add(c);
                    best = c;
                }

                // --- buckling: the combination with the largest compressive force ---
                double[] facC;
                string combC;
                double compression = DesignEffect(nk, -1.0, res.LoadCases, f, out facC, out combC);
                if (compression > tol)
                {
                    double nSquash = md.A * md.Fy0 / f.GammaM0;       // cross-section resistance, 6.2.4

                    for (int axis = 0; axis < 2; axis++)
                    {
                        bool aboutY = (axis == 0);
                        double lambda = MemberResistance.LambdaBar(
                            aboutY ? md.LcrY : md.LcrZ, aboutY ? md.iy : md.iz, md.Fy0, md.E0);
                        double dChi;
                        double chi = MemberResistance.Chi(lambda, aboutY ? md.AlphaY : md.AlphaZ, out dChi);

                        var c = new EcMemberCheck
                        {
                            Member = m, Tag = md.Tag,
                            Mode = aboutY ? MemberMode.BucklingY : MemberMode.BucklingZ,
                            NEd = -compression,
                            NRd = Math.Min(chi * md.A * md.Fy0 / f.GammaM1, nSquash),
                            Combination = combC, Factors = facC,
                            LambdaBar = lambda, Chi = chi
                        };
                        c.Utilisation = compression / c.NRd;
                        res.Checks.Add(c);
                        if (best == null || c.Utilisation > best.Utilisation) best = c;
                    }

                    if (IsClass4InCompression(mdl.Elem1Ds[m].Sec)) class4++;
                }

                res.Governing[m] = best;
                if (best != null && (res.CriticalMember < 0 || best.Utilisation > res.MaxUtilisation))
                {
                    res.CriticalMember = m;
                    res.MaxUtilisation = best.Utilisation;
                }
            }

            int assumed = ls.Members.Count(mem => mem.AssumedBucklingCurve);
            if (assumed > 0)
                res.Warnings.Add(assumed.ToString(CultureInfo.InvariantCulture) +
                                 " member(s) have a section without buckling data: i = sqrt(I/A) and buckling curve c were assumed.");
            if (class4 > 0)
                res.Warnings.Add(class4.ToString(CultureInfo.InvariantCulture) +
                                 " compressed member(s) have a class 4 section. The gross area was used; the effective area is not implemented.");
            return res;
        }

        /// <summary>
        /// The check with bending: for every member and every combination of CombinationsOf, the
        /// design end forces (N and the end moments, superposed from the single load cases) go into
        /// MemberInteraction.Utilisation with gammaM0 and gammaM1. A combination with a tensile (or
        /// zero) design force is checked in tension, one with a compressive force in compression
        /// (cross-section) and buckling about y and z. The worst combination is kept per check.
        /// </summary>
        private static void RunWithBending(TB_Model mdl, LS_MemberLimitStates ls, EcCheckResult res, EcFactors f)
        {
            int nMem = ls.Members.Count;
            int nLc = res.LoadCases.Count;
            List<KeyValuePair<string, double[]>> combos = CombinationsOf(res.LoadCases, f);
            var order = new[] { MemberMode.Tension, MemberMode.Compression, MemberMode.BucklingY, MemberMode.BucklingZ };
            int class4 = 0;

            for (int m = 0; m < nMem; m++)
            {
                MemberData md = ls.Members[m];
                double tolN = MemberReliability.ZeroForce * md.Fy0 * md.A;               // [N]
                double tolM = MemberReliability.ZeroForce * md.Fy0 * md.Wy * 1e-3;       // [N.m]

                // end forces of the single load cases (the variables are multipliers with reference 1)
                double[][] dF = ls.Fe.LocalEndForceGradient(m, EndForces.Rows);
                var fk = new EndForces[nLc];
                for (int i = 0; i < nLc; i++)
                    for (int j = 0; j < 5; j++) { EndForces e = fk[i]; e[j] = dF[EndForces.Rows[j]][i]; fk[i] = e; }

                double[] nk = new double[nLc];
                for (int i = 0; i < nLc; i++) nk[i] = fk[i].N;
                res.NChar[m] = nk;

                var best = new Dictionary<MemberMode, EcMemberCheck>();
                foreach (KeyValuePair<string, double[]> combo in combos)
                {
                    EndForces fd = new EndForces();
                    for (int i = 0; i < nLc; i++)
                        for (int j = 0; j < 5; j++) fd[j] = fd[j] + combo.Value[i] * fk[i][j];

                    MemberMode[] modes;
                    if (fd.N < -tolN) modes = new[] { MemberMode.Compression, MemberMode.BucklingY, MemberMode.BucklingZ };
                    else if (fd.N > tolN || fd.MyMax > tolM || fd.MzMax > tolM) modes = new[] { MemberMode.Tension };
                    else continue;                                                       // nothing to check

                    foreach (MemberMode mode in modes)
                    {
                        var d = new InteractionDetail();
                        double u = MemberInteraction.Utilisation(md, fd, md.Fy0, md.E0, md.G0,
                            1.0 / f.GammaM0, 1.0 / f.GammaM1, ls.Options, mode, d);

                        EcMemberCheck c;
                        if (best.TryGetValue(mode, out c) && !(u > c.Utilisation)) continue;

                        c = new EcMemberCheck
                        {
                            Member = m, Tag = md.Tag, Mode = mode,
                            NEd = fd.N,
                            Utilisation = u,
                            MyEd = fd.MyMax, MzEd = fd.MzMax,
                            Combination = combo.Key,
                            Factors = (double[])combo.Value.Clone(),
                            Clause = d.Governing,
                            ChiLT = d.ChiLT
                        };
                        if (mode == MemberMode.BucklingY || mode == MemberMode.BucklingZ)
                        {
                            bool aboutY = (mode == MemberMode.BucklingY);
                            c.LambdaBar = aboutY ? d.LambdaY : d.LambdaZ;
                            c.Chi = aboutY ? d.ChiY : d.ChiZ;
                            c.NRd = c.Chi * md.A * md.Fy0 / f.GammaM1;
                        }
                        else
                        {
                            c.NRd = md.A * md.Fy0 / f.GammaM0;
                        }
                        best[mode] = c;
                    }
                }

                EcMemberCheck governing = null;
                foreach (MemberMode mode in order)
                {
                    EcMemberCheck c;
                    if (!best.TryGetValue(mode, out c)) continue;
                    res.Checks.Add(c);
                    if (governing == null || c.Utilisation > governing.Utilisation) governing = c;
                }
                if (best.ContainsKey(MemberMode.Compression) && IsClass4InCompression(mdl.Elem1Ds[m].Sec)) class4++;

                res.Governing[m] = governing;
                if (governing != null && (res.CriticalMember < 0 || governing.Utilisation > res.MaxUtilisation))
                {
                    res.CriticalMember = m;
                    res.MaxUtilisation = governing.Utilisation;
                }
            }

            int assumed = ls.Members.Count(mem => mem.AssumedBucklingCurve);
            if (assumed > 0)
                res.Warnings.Add(assumed.ToString(CultureInfo.InvariantCulture) +
                                 " member(s) have a section without buckling data: i = sqrt(I/A) and buckling curve c were assumed.");
            if (class4 > 0)
                res.Warnings.Add(class4.ToString(CultureInfo.InvariantCulture) +
                                 " compressed member(s) have a class 4 section. The gross properties were used; effective properties are not implemented.");
            int noLtb = ls.Members.Count(mem => !mem.OpenSection);
            if (noLtb > 0 && ls.Options.LateralTorsional)
                res.Warnings.Add(noLtb.ToString(CultureInfo.InvariantCulture) +
                                 " member(s) do not have an I-section: no lateral-torsional buckling (chi_LT = 1).");
        }

        /// <summary>
        /// Every combination of EN 1990 (6.10a) and (6.10b) for the declared load cases: each
        /// permanent load case unfavourable (gammaG, or xi gammaG in 6.10b) or favourable
        /// (gammaG,inf); each variable load case absent or accompanying (gammaQ psi0); in (6.10b)
        /// each variable load case in turn the leading one (gammaQ). With N and moments together
        /// "unfavourable" is not known in advance, so all choices are tried.
        /// </summary>
        public static List<KeyValuePair<string, double[]>> CombinationsOf(IList<EcLoadCase> lcs, EcFactors f)
        {
            int n = lcs.Count;
            if (n > 14)
                throw new ArgumentException("With bending, at most 14 load cases can be combined (every factor choice is tried).");

            var perm = new List<int>();
            var vari = new List<int>();
            for (int i = 0; i < n; i++)
                if (lcs[i].Action == EcAction.Permanent) perm.Add(i); else vari.Add(i);

            var list = new List<KeyValuePair<string, double[]>>();
            for (int eq = 0; eq < 2; eq++)
            {
                bool b = (eq == 1);
                var leads = new List<int>();
                if (b && vari.Count > 0) leads.AddRange(vari); else leads.Add(-1);

                foreach (int lead in leads)
                {
                    List<int> acc = vari.Where(i => i != lead).ToList();
                    for (int pm = 0; pm < (1 << perm.Count); pm++)
                        for (int am = 0; am < (1 << acc.Count); am++)
                        {
                            double[] fac = new double[n];
                            for (int k = 0; k < perm.Count; k++)
                                fac[perm[k]] = ((pm >> k) & 1) == 1 ? (b ? f.Xi * f.GammaG : f.GammaG) : f.GammaGInf;
                            for (int k = 0; k < acc.Count; k++)
                                fac[acc[k]] = ((am >> k) & 1) == 1 ? f.GammaQ * lcs[acc[k]].Psi0 : 0.0;
                            if (lead >= 0) fac[lead] = f.GammaQ;
                            list.Add(new KeyValuePair<string, double[]>((b ? "6.10b: " : "6.10a: ") + Describe(fac, lcs), fac));
                        }
                }
            }
            return list;
        }

        /// <summary>
        /// The largest design value of sign * N over (6.10a) and (6.10b), where nk holds the member
        /// force of each load case under the characteristic loads. sign = +1 looks for tension,
        /// sign = -1 for compression. Returns the factor on each load case and a description.
        /// </summary>
        public static double DesignEffect(double[] nk, double sign, IList<EcLoadCase> lcs, EcFactors f,
                                          out double[] factors, out string combination)
        {
            int n = lcs.Count;

            // --- (6.10a): gammaG G + gammaQ psi0 Q ---
            double[] fa = new double[n];
            double ea = 0.0;
            for (int i = 0; i < n; i++)
            {
                double e = sign * nk[i];                         // > 0: unfavourable
                if (lcs[i].Action == EcAction.Permanent) fa[i] = (e > 0.0) ? f.GammaG : f.GammaGInf;
                else fa[i] = (e > 0.0) ? f.GammaQ * lcs[i].Psi0 : 0.0;
                ea += fa[i] * e;
            }

            // --- (6.10b): xi gammaG G + gammaQ Q1 + gammaQ psi0 Qi, every unfavourable Q tried as Q1 ---
            double[] fb0 = new double[n];                        // all variable actions accompanying
            double eb0 = 0.0;
            int leading = -1;
            double gain = 0.0;                                   // what making one action the leading one adds
            for (int i = 0; i < n; i++)
            {
                double e = sign * nk[i];
                if (lcs[i].Action == EcAction.Permanent)
                {
                    fb0[i] = (e > 0.0) ? f.Xi * f.GammaG : f.GammaGInf;
                }
                else
                {
                    fb0[i] = (e > 0.0) ? f.GammaQ * lcs[i].Psi0 : 0.0;
                    double g = (e > 0.0) ? f.GammaQ * (1.0 - lcs[i].Psi0) * e : 0.0;
                    if (e > 0.0 && (leading < 0 || g > gain))
                    {
                        leading = i;
                        gain = g;
                    }
                }
                eb0 += fb0[i] * e;
            }
            double[] fb = (double[])fb0.Clone();
            double eb = eb0;
            if (leading >= 0)
            {
                fb[leading] = f.GammaQ;
                eb = eb0 + gain;
            }

            bool useA = ea > eb;
            factors = useA ? fa : fb;
            combination = (useA ? "6.10a: " : "6.10b: ") + Describe(factors, lcs);
            return useA ? ea : eb;
        }

        /// <summary>"1.20 LC0 + 1.50 LC1"; load cases with a zero factor are left out.</summary>
        private static string Describe(double[] factors, IList<EcLoadCase> lcs)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            for (int i = 0; i < factors.Length; i++)
            {
                if (factors[i] == 0.0) continue;
                parts.Add(factors[i].ToString("0.00", ci) + " LC" + lcs[i].LoadCase.ToString(ci));
            }
            return (parts.Count == 0) ? "-" : string.Join(" + ", parts);
        }

        /// <summary>
        /// EN 1993-1-1 table 5.2, uniform compression: class 4 if the web is more slender than
        /// 42 epsilon or an outstand flange than 14 epsilon. Only for the section types that carry
        /// their dimensions; false otherwise.
        /// </summary>
        private static bool IsClass4InCompression(TB_Section sec)
        {
            if (sec == null || sec.Mat == null || !(sec.Mat.Fy > 0.0)) return false;
            double eps = Math.Sqrt(235.0 / sec.Mat.Fy);

            Section_I si = sec as Section_I;
            if (si != null && si.Tw > 0.0 && si.Tf > 0.0)
            {
                double web = (si.H - 2.0 * si.Tf - 2.0 * si.R) / si.Tw;
                double flange = 0.5 * (si.W - si.Tw - 2.0 * si.R) / si.Tf;
                return web > 42.0 * eps || flange > 14.0 * eps;
            }

            Section_RHS rhs = sec as Section_RHS;
            if (rhs != null && rhs.Tw > 0.0 && rhs.Tf > 0.0)
            {
                double web = (rhs.H - 3.0 * rhs.Tf) / rhs.Tw;
                double flange = (rhs.W - 3.0 * rhs.Tw) / rhs.Tf;
                return web > 42.0 * eps || flange > 42.0 * eps;
            }
            return false;
        }
    }
}
