// =============================================================================
//  ST_LS_MemberLimitState.cs
//
//  WHAT IT IS
//  Member limit states for a linear-elastic truss model, and the routine that runs
//  FORM on every one of them.
//
//  LIMIT STATES WITH BENDING (default, MemberCheckOptions.Bending = true)
//    g = 1 - U,  U = the EN 1993-1-1 utilisation of ST_LS_MemberInteraction.cs with
//    gamma_M = 1, the characteristic resistances times thetaR and the member end
//    forces (N and the end moments from the rigid joints) times thetaE:
//    tension        cross-section 6.2.1(7) (and lateral-torsional buckling, N = 0)
//    compression    cross-section 6.2.1(7)
//    buckling y     (6.61)       buckling z     (6.62)
//
//  LIMIT STATES, AXIAL ONLY (MemberCheckOptions.Bending = false; N tension positive)
//    tension yield                g = thetaR * fy * A                 - thetaE * N
//    flexural buckling about y    g = thetaR * chi_y(fy, E) * fy * A  + thetaE * N
//    flexural buckling about z    g = thetaR * chi_z(fy, E) * fy * A  + thetaE * N
//  Both forms have the same failure surface when the moments are zero.
//
//  chi (and chi_LT) are the EN 1993-1-1 reduction factors, evaluated with the
//  RANDOM fy and E, so the resistance model is the Eurocode one and its bias and
//  scatter are carried by the model uncertainty thetaR.
//
//  ONE RANDOM VECTOR FOR ALL LIMIT STATES
//  Every limit state is a function of the same vector x. This is what makes the
//  correlation between member failures available later (rho_ij = alpha_i . alpha_j).
//
//  GRADIENTS
//  The member-force part always comes from LS_DDM (direct differentiation of the FE
//  equations: dN/dp, and with bending also the end moments dM/dp). Axial only, the
//  resistance part is differentiated analytically here. With bending, the partial
//  derivatives of the closed-form EN 1993-1-1 expressions (with respect to the end
//  forces, fy, E and thetaR) are taken by central differences - no FE solve.
//
//  SYSTEM
//  After the member limit states, the structure is assessed as a series system of all of
//  them (ST_LS_SystemReliability.cs), from the beta and alpha that FORM found.
//
//  WHICH LIMIT STATES ARE ASSESSED
//  Tension if any load case puts the member in tension, buckling (and, with bending, the
//  compressed cross-section) if any load case puts it in compression. With bending, a member
//  with end moments but no axial force gets the tension (cross-section) check. FORM is run on every one of them and the beta it finds is reported,
//  however large. Only a member that no load case stresses axially has no limit state.
//
//  UNITS: forces [N], A [mm2], fy and E [N/mm2], lengths [m], radii of gyration [mm].
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
    // =======================================================================
    //  Variable bindings
    // =======================================================================

    public enum MemberVarKind
    {
        /// <summary>Scales all loads of one load case.</summary>
        LoadCase,
        /// <summary>Yield strength of a group of members.</summary>
        Yield,
        /// <summary>Young's modulus of a group of members.</summary>
        Modulus,
        /// <summary>Resistance model uncertainty (multiplies the resistance).</summary>
        ResistanceModel,
        /// <summary>Load-effect model uncertainty (multiplies the member force).</summary>
        LoadEffectModel
    }

    /// <summary>What one random variable does in the member limit states.</summary>
    public class MemberVariable
    {
        public string Name = "x";
        public MemberVarKind Kind;
        public int LoadCase = 0;
        public double Reference = 1.0;
        /// <summary>Element tag of the group. Empty = all members.</summary>
        public string ElementTag = "";

        /// <summary>
        /// Parses a compact binding string.
        ///
        ///   "LC:1"        the variable multiplies the loads of load case 1 as modelled
        ///   "LC:1:150"    the loads modelled in load case 1 correspond to the value 150,
        ///                 i.e. loads = (x / 150) * modelled loads
        ///   "FY"          yield strength of all members
        ///   "FY:chord"    yield strength of the members with element tag "chord"
        ///   "E", "E:tag"  Young's modulus of all members / of one group
        ///   "TR"          resistance model uncertainty
        ///   "TE"          load-effect model uncertainty
        /// </summary>
        public static MemberVariable Parse(string spec, string name)
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new ArgumentException("Empty binding spec.");

            string[] t = spec.Split(':');
            string kind = t[0].Trim().ToUpperInvariant();
            string arg = (t.Length > 1) ? t[1].Trim() : "";

            var v = new MemberVariable { Name = name ?? spec };

            switch (kind)
            {
                case "LC":
                    {
                        int lc;
                        if (t.Length < 2 || !int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out lc))
                            throw new ArgumentException($"'{spec}': expected LC:<loadCase>[:<referenceValue>].");

                        double reference = 1.0;
                        if (t.Length > 2 &&
                            !double.TryParse(t[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out reference))
                            throw new ArgumentException($"'{spec}': the reference value '{t[2]}' is not a number.");
                        if (reference == 0.0)
                            throw new ArgumentException($"'{spec}': the reference value cannot be zero.");

                        v.Kind = MemberVarKind.LoadCase;
                        v.LoadCase = lc;
                        v.Reference = reference;
                        return v;
                    }

                case "FY":
                    v.Kind = MemberVarKind.Yield;
                    v.ElementTag = arg;
                    return v;

                case "E":
                    v.Kind = MemberVarKind.Modulus;
                    v.ElementTag = arg;
                    return v;

                case "TR":
                    v.Kind = MemberVarKind.ResistanceModel;
                    return v;

                case "TE":
                    v.Kind = MemberVarKind.LoadEffectModel;
                    return v;

                default:
                    throw new ArgumentException(
                        $"'{spec}': unknown binding kind '{kind}'. Use LC, FY, E, TR or TE.");
            }
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case MemberVarKind.LoadCase:
                    return "load case " + LoadCase.ToString(CultureInfo.InvariantCulture) +
                           " (model = " + Reference.ToString("G6", CultureInfo.InvariantCulture) + ")";
                case MemberVarKind.Yield:
                    return "fy of " + (string.IsNullOrEmpty(ElementTag) ? "all members" : "'" + ElementTag + "'");
                case MemberVarKind.Modulus:
                    return "E of " + (string.IsNullOrEmpty(ElementTag) ? "all members" : "'" + ElementTag + "'");
                case MemberVarKind.ResistanceModel:
                    return "resistance model uncertainty";
                default:
                    return "load-effect model uncertainty";
            }
        }
    }


    // =======================================================================
    //  Member data and resistance model
    // =======================================================================

    public enum MemberMode
    {
        Tension,
        BucklingY,
        BucklingZ,
        /// <summary>Cross-section check under compression and bending (only with bending).</summary>
        Compression
    }

    /// <summary>What the limit states need to know about one member. Read once from the model.</summary>
    public class MemberData
    {
        public int Index;
        public string Tag = "";
        public string SectionTag = "";
        /// <summary>Area [mm2] and elastic section moduli [mm3].</summary>
        public double A, Wy, Wz;
        /// <summary>Plastic section moduli [mm3] (the elastic ones where the section has none).</summary>
        public double Wpy, Wpz;
        /// <summary>Second moment about z [mm4], torsion constant [mm4] and warping constant [mm6].</summary>
        public double Iz, It, Iw;
        /// <summary>Shear modulus of the model material [N/mm2].</summary>
        public double G0;
        /// <summary>Open I-section: lateral-torsional buckling applies and it is susceptible to torsional deformation.</summary>
        public bool OpenSection;
        /// <summary>Imperfection factor for lateral-torsional buckling (0.34, curve b, if the section has none).</summary>
        public double AlphaLT = 0.34;
        /// <summary>Length between lateral restraints for lateral-torsional buckling [m]. Default: Lcr,z.</summary>
        public double LcrLT;
        /// <summary>Yield strength and modulus of the model material [N/mm2].</summary>
        public double Fy0, E0;
        /// <summary>Member length and buckling lengths about y and z [m].</summary>
        public double Length, LcrY, LcrZ;
        /// <summary>Radii of gyration [mm].</summary>
        public double iy, iz;
        /// <summary>EN 1993-1-1 imperfection factors for buckling about y and z.</summary>
        public double AlphaY, AlphaZ;
        /// <summary>True if the section carried no buckling data and curve c was assumed.</summary>
        public bool AssumedBucklingCurve;
    }

    /// <summary>EN 1993-1-1, 6.3.1.2: flexural buckling reduction factor.</summary>
    public static class MemberResistance
    {
        /// <summary>Imperfection factor used when the section has none (buckling curve c).</summary>
        public const double DefaultAlpha = 0.49;

        /// <summary>
        /// Non-dimensional slenderness lambda_bar = (Lcr / i) / (pi * sqrt(E / fy)).
        /// Lcr in [m], i in [mm], fy and E in [N/mm2].
        /// </summary>
        public static double LambdaBar(double lcr, double i, double fy, double E)
        {
            return (lcr * 1000.0 / i) / Math.PI * Math.Sqrt(fy / E);
        }

        /// <summary>
        /// chi = 1 / (Phi + sqrt(Phi^2 - lambda^2)) with Phi = 0.5 (1 + alpha (lambda - 0.2) + lambda^2),
        /// and chi = 1 for lambda &lt;= 0.2. Also returns d(chi)/d(lambda).
        /// </summary>
        public static double Chi(double lambda, double alpha, out double dChi)
        {
            dChi = 0.0;
            if (lambda <= 0.2) return 1.0;

            double phi = 0.5 * (1.0 + alpha * (lambda - 0.2) + lambda * lambda);
            double root = Math.Sqrt(phi * phi - lambda * lambda);
            double chi = 1.0 / (phi + root);
            if (chi >= 1.0) return 1.0;

            double dPhi = 0.5 * (alpha + 2.0 * lambda);
            double dRoot = (phi * dPhi - lambda) / root;
            dChi = -chi * chi * (dPhi + dRoot);
            return chi;
        }
    }


    // =======================================================================
    //  Limit states
    // =======================================================================

    /// <summary>
    /// g(x) and dg/dx for any (member, mode), all sharing one variable vector x and one
    /// linear FE model. The last FE solve is cached inside LS_DDM, so asking for the value
    /// and the gradient at the same x - which HL-RF always does - costs one evaluation.
    /// </summary>
    public class LS_MemberLimitStates
    {
        // --- field ---
        public LS_DDM Fe { get; private set; }
        public List<MemberData> Members { get; private set; }
        public List<MemberVariable> Variables { get; private set; }
        public int NVar { get { return Variables.Count; } }
        /// <summary>Axial only, or axial force + bending (MemberInteraction).</summary>
        public MemberCheckOptions Options { get; private set; }

        private readonly int[] _feVar;        // FE parameter -> variable index
        private readonly bool[] _feIsLoad;    // FE parameter is a load case (else a modulus)
        private readonly int[] _iFy;          // member -> variable holding its fy, -1 = model value
        private readonly int[] _iE;           // member -> variable holding its E,  -1 = model value
        private readonly int _iTR = -1;       // variable holding thetaR, -1 = 1.0
        private readonly int _iTE = -1;       // variable holding thetaE, -1 = 1.0

        // --- constructors ---
        /// <param name="bucklingLengths">Optional: element tag -> { Lcr,y ; Lcr,z [; L,LT] } in [m].</param>
        /// <param name="options">How members are checked; null = with bending (MemberCheckOptions defaults).</param>
        public LS_MemberLimitStates(TB_Model mdl, IList<MemberVariable> variables,
                                    IDictionary<string, double[]> bucklingLengths = null,
                                    MemberCheckOptions options = null)
        {
            Options = options ?? MemberCheckOptions.Default();
            if (mdl == null) throw new ArgumentNullException(nameof(mdl));
            if (mdl.Elem1Ds == null || mdl.Elem1Ds.Count == 0)
                throw new ArgumentException("The model has no elements.");

            Variables = (variables == null) ? new List<MemberVariable>() : variables.ToList();

            // --- members ---
            Members = new List<MemberData>();
            for (int m = 0; m < mdl.Elem1Ds.Count; m++)
                Members.Add(ReadMember(mdl.Elem1Ds[m], m));
            int nMem = Members.Count;

            if (bucklingLengths != null)
            {
                foreach (KeyValuePair<string, double[]> kv in bucklingLengths)
                {
                    if (kv.Value == null || kv.Value.Length < 2 || !(kv.Value[0] > 0.0) || !(kv.Value[1] > 0.0))
                        throw new ArgumentException($"Buckling lengths for '{kv.Key}' must be two positive numbers.");

                    int count = 0;
                    foreach (MemberData md in Members)
                    {
                        if (!TagMatches(md.Tag, kv.Key)) continue;
                        md.LcrY = kv.Value[0];
                        md.LcrZ = kv.Value[1];
                        md.LcrLT = (kv.Value.Length > 2 && kv.Value[2] > 0.0) ? kv.Value[2] : kv.Value[1];
                        count++;
                    }
                    if (count == 0)
                        throw new ArgumentException($"Buckling lengths: no element has the tag '{kv.Key}'.");
                }
            }

            // --- variables ---
            _iFy = Enumerable.Repeat(-1, nMem).ToArray();
            _iE = Enumerable.Repeat(-1, nMem).ToArray();

            var fePars = new List<LS_Parameter>();
            var feVar = new List<int>();
            var feIsLoad = new List<bool>();

            for (int i = 0; i < Variables.Count; i++)
            {
                MemberVariable v = Variables[i];
                switch (v.Kind)
                {
                    case MemberVarKind.LoadCase:
                        fePars.Add(LS_Parameter.ForLoadCase(v.Name, v.LoadCase, v.Reference));
                        feVar.Add(i);
                        feIsLoad.Add(true);
                        break;

                    case MemberVarKind.Modulus:
                        fePars.Add(LS_Parameter.ForModulus(v.Name, v.ElementTag));
                        feVar.Add(i);
                        feIsLoad.Add(false);
                        AssignGroup(_iE, i, v, "E");
                        break;

                    case MemberVarKind.Yield:
                        AssignGroup(_iFy, i, v, "fy");
                        break;

                    case MemberVarKind.ResistanceModel:
                        if (_iTR >= 0)
                            throw new ArgumentException("Only one resistance model uncertainty (TR) can be bound.");
                        _iTR = i;
                        break;

                    case MemberVarKind.LoadEffectModel:
                        if (_iTE >= 0)
                            throw new ArgumentException("Only one load-effect model uncertainty (TE) can be bound.");
                        _iTE = i;
                        break;
                }
            }

            _feVar = feVar.ToArray();
            _feIsLoad = feIsLoad.ToArray();
            Fe = new LS_DDM(mdl, fePars);
        }

        // --- methods ---

        /// <summary>
        /// The scale that makes g of order one: fy(model) * A [N] for the axial limit states,
        /// 1 with bending (g = 1 - U is already dimensionless).
        /// </summary>
        public double Scale(int m)
        {
            return Options.Bending ? 1.0 : Members[m].Fy0 * Members[m].A;
        }

        /// <summary>g(x). Returns NaN where the limit state cannot be evaluated.</summary>
        public double G(double[] x, int m, MemberMode mode)
        {
            double resistance;
            return Evaluate(x, m, mode, null, out resistance);
        }

        /// <summary>g(x) together with the resistance term thetaR * R [N].</summary>
        public double GAndResistance(double[] x, int m, MemberMode mode, out double resistance)
        {
            return Evaluate(x, m, mode, null, out resistance);
        }

        /// <summary>dg/dx. Returns null where the limit state cannot be evaluated.</summary>
        public double[] GradX(double[] x, int m, MemberMode mode)
        {
            double[] grad = new double[NVar];
            double resistance;
            double g = Evaluate(x, m, mode, grad, out resistance);
            return double.IsNaN(g) ? null : grad;
        }

        /// <summary>Axial force of every member at x [N], tension positive. Null if not solvable.</summary>
        public double[] AxialForces(double[] x)
        {
            if (!Prepare(x)) return null;
            return (double[])Fe.N.Clone();
        }

        /// <summary>The FE parameter vector that corresponds to x.</summary>
        public double[] FeParameters(double[] x)
        {
            double[] p = new double[_feVar.Length];
            for (int ip = 0; ip < p.Length; ip++) p[ip] = x[_feVar[ip]];
            return p;
        }

        /// <summary>
        /// Splits the axial force of member m at x into the sum of its tensile and the sum of its
        /// compressive contributions (one contribution per load case; N is linear in the loads).
        /// A member with a tensile part can fail in tension, one with a compressive part can buckle.
        /// </summary>
        public void SignedParts(double[] x, int m, out double tension, out double compression)
        {
            tension = 0.0;
            compression = 0.0;
            if (!Prepare(x)) return;

            double rest = Fe.N[m];                       // what remains is the unbound load cases
            for (int ip = 0; ip < _feVar.Length; ip++)
            {
                if (!_feIsLoad[ip]) continue;
                double c = Fe.dN[m][ip] * x[_feVar[ip]];
                rest -= c;
                if (c > 0.0) tension += c; else compression += c;
            }
            if (rest > 0.0) tension += rest; else compression += rest;
        }

        /// <summary>Runs the FE model at x. False if a modulus is not positive.</summary>
        private bool Prepare(double[] x)
        {
            double[] p = FeParameters(x);
            for (int ip = 0; ip < p.Length; ip++)
            {
                if (double.IsNaN(p[ip])) return false;
                if (!_feIsLoad[ip] && !(p[ip] > 0.0)) return false;
            }
            Fe.Evaluate(p);
            return true;
        }

        private double Evaluate(double[] x, int m, MemberMode mode, double[] grad, out double resistance)
        {
            resistance = double.NaN;

            MemberData md = Members[m];
            double fy = (_iFy[m] >= 0) ? x[_iFy[m]] : md.Fy0;
            double E = (_iE[m] >= 0) ? x[_iE[m]] : md.E0;
            double tR = (_iTR >= 0) ? x[_iTR] : 1.0;
            double tE = (_iTE >= 0) ? x[_iTE] : 1.0;

            // a trial point of the line search may leave the physical domain: signal it with NaN
            if (!(fy > 0.0) || !(E > 0.0) || !(tR > 0.0) || !(tE > 0.0)) return double.NaN;
            if (!Prepare(x)) return double.NaN;

            if (Options.Bending)
                return EvaluateInteraction(m, mode, fy, E, tR, tE, grad, out resistance);

            double N = Fe.N[m];

            // g = tR * R - sign * tE * N, with R the resistance without model uncertainty
            double sign, R, dR_dfy, dR_dE;
            if (mode == MemberMode.Tension)
            {
                sign = 1.0;
                R = fy * md.A;
                dR_dfy = md.A;
                dR_dE = 0.0;
            }
            else
            {
                bool aboutY = (mode == MemberMode.BucklingY);
                double lcr = aboutY ? md.LcrY : md.LcrZ;
                double rad = aboutY ? md.iy : md.iz;
                double alpha = aboutY ? md.AlphaY : md.AlphaZ;

                double lambda = MemberResistance.LambdaBar(lcr, rad, fy, E);
                double dChi;
                double chi = MemberResistance.Chi(lambda, alpha, out dChi);

                sign = -1.0;
                R = chi * fy * md.A;
                // d(lambda)/d(fy) = lambda / (2 fy),  d(lambda)/dE = -lambda / (2 E)
                dR_dfy = md.A * (chi + 0.5 * lambda * dChi);
                dR_dE = -md.A * fy * dChi * 0.5 * lambda / E;
            }

            resistance = tR * R;
            double g = resistance - sign * tE * N;

            if (grad != null)
            {
                for (int i = 0; i < grad.Length; i++) grad[i] = 0.0;

                // member force: DDM
                for (int ip = 0; ip < _feVar.Length; ip++)
                    grad[_feVar[ip]] += -sign * tE * Fe.dN[m][ip];

                // resistance
                if (_iFy[m] >= 0) grad[_iFy[m]] += tR * dR_dfy;
                if (_iE[m] >= 0) grad[_iE[m]] += tR * dR_dE;
                if (_iTR >= 0) grad[_iTR] += R;
                if (_iTE >= 0) grad[_iTE] += -sign * N;
            }
            return g;
        }

        /// <summary>
        /// Limit state with bending: g = 1 - U, U from MemberInteraction.Utilisation with the member
        /// end forces times thetaE, the characteristic resistances times thetaR, and the random fy
        /// and E (G = G_model * E / E_model). FE state already prepared at x.
        ///
        /// Gradient: dU/dp = sum_j dU/dF_j * thetaE * dF_j/dp, with dF_j/dp (N and the end moments)
        /// from the DDM solve and dU/dF_j, dU/dfy, dU/dE, dU/dthetaR by central differences on the
        /// closed-form EN 1993-1-1 expressions (no FE solve involved).
        /// </summary>
        private double EvaluateInteraction(int m, MemberMode mode, double fy, double E, double tR, double tE,
                                           double[] grad, out double resistance)
        {
            MemberData md = Members[m];
            resistance = 1.0;

            EndForces f = EndForces.FromLocal(Fe.LocalEndForces(m));
            EndForces fe = f.Scaled(tE);
            MemberCheckOptions o = Options;
            Func<EndForces, double, double, double, double> U = (ff, fyv, ev, trv) =>
                MemberInteraction.Utilisation(md, ff, fyv, ev, md.G0 * ev / md.E0, trv, trv, o, mode);

            double u = U(fe, fy, E, tR);
            if (double.IsNaN(u) || double.IsInfinity(u)) return double.NaN;
            double g = 1.0 - u;
            if (grad == null) return g;

            for (int i = 0; i < grad.Length; i++) grad[i] = 0.0;

            // dU/dF_j, F = (N, My1, My2, Mz1, Mz2) after thetaE
            double[] dUdF = new double[5];
            double nRef = 1e-3 * md.Fy0 * md.A;                       // [N]
            double mRef = 1e-3 * md.Fy0 * md.Wy * 1e-3;               // [N.m]
            for (int j = 0; j < 5; j++)
            {
                double h = 1e-6 * (Math.Abs(fe[j]) + ((j == 0) ? nRef : mRef));
                EndForces fp = fe, fm = fe;
                fp[j] = fe[j] + h;
                fm[j] = fe[j] - h;
                dUdF[j] = (U(fp, fy, E, tR) - U(fm, fy, E, tR)) / (2.0 * h);
            }

            // member forces: DDM
            double[][] dF = Fe.LocalEndForceGradient(m, EndForces.Rows);
            for (int ip = 0; ip < _feVar.Length; ip++)
            {
                double dU = 0.0;
                for (int j = 0; j < 5; j++) dU += dUdF[j] * tE * dF[EndForces.Rows[j]][ip];
                grad[_feVar[ip]] += -dU;
            }

            // resistance and model uncertainties
            if (_iFy[m] >= 0) grad[_iFy[m]] += -Central(v => U(fe, v, E, tR), fy);
            if (_iE[m] >= 0) grad[_iE[m]] += -Central(v => U(fe, fy, v, tR), E);
            if (_iTR >= 0) grad[_iTR] += -Central(v => U(fe, fy, E, v), tR);
            if (_iTE >= 0)
            {
                double dU = 0.0;
                for (int j = 0; j < 5; j++) dU += dUdF[j] * f[j];
                grad[_iTE] += -dU;
            }
            return g;
        }

        /// <summary>Central difference of a scalar function at v > 0, relative step 1e-6.</summary>
        private static double Central(Func<double, double> fun, double v)
        {
            double h = 1e-6 * Math.Abs(v);
            return (fun(v + h) - fun(v - h)) / (2.0 * h);
        }

        private void AssignGroup(int[] target, int variable, MemberVariable v, string what)
        {
            int count = 0;
            for (int m = 0; m < Members.Count; m++)
            {
                if (!TagMatches(Members[m].Tag, v.ElementTag)) continue;
                if (target[m] >= 0)
                    throw new ArgumentException(
                        $"Variable '{v.Name}': member {m} ('{Members[m].Tag}') already takes its {what} " +
                        $"from '{Variables[target[m]].Name}'.");
                target[m] = variable;
                count++;
            }
            if (count == 0)
                throw new ArgumentException($"Variable '{v.Name}': no element has the tag '{v.ElementTag}'.");
        }

        private static MemberData ReadMember(TB_Element_1D e, int index)
        {
            if (e == null || e.Sec == null || e.Sec.Mat == null)
                throw new ArgumentException($"Element {index} has no section or material.");

            TB_Section sec = e.Sec;
            if (!(sec.Area > 0.0) || !(sec.Mat.Fy > 0.0) || !(sec.Mat.E > 0.0))
                throw new ArgumentException(
                    $"Element {index} ('{e.Tag}'): area, yield strength and modulus must be positive.");

            var md = new MemberData
            {
                Index = index,
                Tag = e.Tag ?? "",
                SectionTag = sec.Tag ?? "",
                A = sec.Area,
                Wy = Math.Abs(sec.Wy),
                Wz = Math.Abs(sec.Wz),
                Wpy = (sec.Wpy > 0.0) ? sec.Wpy : Math.Abs(sec.Wy),
                Wpz = (sec.Wpz > 0.0) ? sec.Wpz : Math.Abs(sec.Wz),
                Iz = sec.Iz,
                It = sec.J,
                G0 = sec.Mat.G,
                Fy0 = sec.Mat.Fy,
                E0 = sec.Mat.E,
                Length = e.Line.Length
            };

            double lcr = (e.Buckling_Length > 0.0) ? e.Buckling_Length : md.Length;
            md.LcrY = lcr;
            md.LcrZ = lcr;
            md.LcrLT = lcr;

            Section_I si = sec as Section_I;
            if (si != null)
            {
                md.OpenSection = true;
                md.Iw = si.Cw;
                if (si.AlphaLT > 0.0) md.AlphaLT = si.AlphaLT;
            }
            if (si != null && si.iy > 0.0 && si.iz > 0.0 && si.AlphaY > 0.0 && si.AlphaZ > 0.0)
            {
                md.iy = si.iy;
                md.iz = si.iz;
                md.AlphaY = si.AlphaY;
                md.AlphaZ = si.AlphaZ;
            }
            else
            {
                if (!(sec.Iy > 0.0) || !(sec.Iz > 0.0))
                    throw new ArgumentException(
                        $"Element {index} ('{e.Tag}'): Iy and Iz must be positive for the buckling limit states.");
                md.iy = Math.Sqrt(sec.Iy / sec.Area);
                md.iz = Math.Sqrt(sec.Iz / sec.Area);
                md.AlphaY = MemberResistance.DefaultAlpha;
                md.AlphaZ = MemberResistance.DefaultAlpha;
                md.AssumedBucklingCurve = true;
            }
            return md;
        }

        private static bool TagMatches(string elementTag, string wanted)
        {
            return string.IsNullOrEmpty(wanted) ||
                   string.Equals(elementTag, wanted, StringComparison.OrdinalIgnoreCase);
        }
    }


    // =======================================================================
    //  FORM on every member limit state
    // =======================================================================

    /// <summary>FORM result of one (member, mode) limit state.</summary>
    public class MemberLimitStateResult
    {
        public int Member;
        public string Tag = "";
        public MemberMode Mode;
        /// <summary>Axial force at the mean point [N], tension positive.</summary>
        public double NMean;
        /// <summary>Demand / resistance at the mean point.</summary>
        public double UtilisationMean;
        /// <summary>Null if FORM could not be run.</summary>
        public FORMResult Form;
        /// <summary>sigma_i * dBeta/dMu_i.</summary>
        public double[] MeanSensitivity;
        public string Status = "";

        public bool HasBeta { get { return Form != null && !double.IsNaN(Form.Beta); } }
        public bool Converged { get { return Form != null && Form.Converged; } }

        public static string ModeName(MemberMode mode)
        {
            switch (mode)
            {
                case MemberMode.Tension: return "tension";
                case MemberMode.BucklingY: return "buckling y";
                case MemberMode.BucklingZ: return "buckling z";
                default: return "compression";
            }
        }
    }

    public class MemberReliabilityResult
    {
        public List<MemberData> Members = new List<MemberData>();
        public List<string> VariableNames = new List<string>();
        public List<string> VariableRoles = new List<string>();
        /// <summary>How the members were checked (axial only, or axial force + bending).</summary>
        public MemberCheckOptions Options = MemberCheckOptions.Default();
        /// <summary>Every limit state that was assessed, in member order.</summary>
        public List<MemberLimitStateResult> LimitStates = new List<MemberLimitStateResult>();
        /// <summary>
        /// The limit state with the lowest beta of each member. Null for a member that no load
        /// case stresses (no axial force, and no moment when bending is checked).
        /// </summary>
        public MemberLimitStateResult[] Governing = new MemberLimitStateResult[0];
        /// <summary>Axial force of each member at the mean point [N].</summary>
        public double[] NMean = new double[0];
        public List<string> Warnings = new List<string>();

        /// <summary>Index of the member with the lowest beta, -1 if none.</summary>
        public int CriticalMember = -1;
        public double MinBeta = double.NaN;

        public bool StiffnessIsConstant;
        public int Factorisations;

        /// <summary>
        /// Axial and bending stress of each member at the mean point [N/mm2]. The bending stress
        /// (largest end moments about y and z, elastic) comes from the rigid joints. It is part of
        /// the limit states only when Options.Bending is true; otherwise it shows how much it matters.
        /// </summary>
        public double[] AxialStress = new double[0];
        public double[] BendingStress = new double[0];
        /// <summary>Bending / axial stress in the member with the lowest beta. NaN if there is none.</summary>
        public double CriticalBendingRatio = double.NaN;

        /// <summary>Worst DDM-vs-finite-difference errors (NaN if the check was not run).</summary>
        public double GradientErrorForces = double.NaN;
        public double GradientErrorLimitStates = double.NaN;

        /// <summary>
        /// The structure as a series system of all limit states that have a beta.
        /// Null if no limit state has one.
        /// </summary>
        public SystemReliabilityResult SystemResult = null;

        public string Report()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.AppendLine("MEMBER RELIABILITY (linear analysis, FORM per member limit state)");
            sb.AppendLine("Members checked for " + Options.Describe() + ".");
            if (Options.Bending)
                sb.AppendLine("g = 1 - U, U = EN 1993-1-1 utilisation with gamma_M = 1, resistances x thetaR, member forces x thetaE.");
            sb.AppendLine();
            sb.AppendLine("Variables:");
            for (int i = 0; i < VariableNames.Count; i++)
                sb.AppendLine("  [" + i.ToString(ci) + "] " + VariableNames[i] + " : " +
                              (i < VariableRoles.Count ? VariableRoles[i] : ""));
            sb.AppendLine();

            sb.AppendLine("Limit states (N at the mean point, tension positive):");
            sb.AppendLine("  member            mode         N [kN]     util    beta        Pf   status");
            foreach (MemberLimitStateResult r in LimitStates)
            {
                string name = "[" + r.Member.ToString(ci) + "] " + r.Tag;
                sb.Append("  " + name.PadRight(17) + " ");
                sb.Append(MemberLimitStateResult.ModeName(r.Mode).PadRight(10) + " ");
                sb.Append((r.NMean * 1e-3).ToString("F1", ci).PadLeft(8) + " ");
                sb.Append(r.UtilisationMean.ToString("F3", ci).PadLeft(8) + " ");
                if (r.HasBeta)
                {
                    sb.Append(r.Form.Beta.ToString("F3", ci).PadLeft(7) + " ");
                    sb.Append(r.Form.ProbabilityOfFailure.ToString("E2", ci).PadLeft(9) + "   ");
                }
                else
                {
                    sb.Append("      -         -   ");
                }
                sb.AppendLine(r.Status);
            }
            sb.AppendLine();

            if (CriticalMember >= 0)
            {
                MemberLimitStateResult g = Governing[CriticalMember];
                sb.AppendLine("Lowest beta: " + MinBeta.ToString("F3", ci) + " in member [" +
                              CriticalMember.ToString(ci) + "] " + g.Tag + " (" +
                              MemberLimitStateResult.ModeName(g.Mode) + ").");
            }

            int unloaded = 0;
            for (int m = 0; m < Governing.Length; m++)
                if (Governing[m] == null) unloaded++;
            if (unloaded > 0)
                sb.AppendLine(unloaded.ToString(ci) + (Options.Bending
                    ? " member(s) are not stressed by any load case and have no limit state."
                    : " member(s) are not stressed axially by any load case and have no axial limit state."));

            if (SystemResult != null)
            {
                sb.AppendLine();
                sb.Append(SystemResult.Report());
            }

            sb.AppendLine();
            sb.AppendLine("FE model: stiffness " + (StiffnessIsConstant
                ? "does not depend on the variables (factorised once)"
                : "depends on the variables") + ", factorisations: " + Factorisations.ToString(ci) + ".");

            if (CriticalMember >= 0 && CriticalMember < BendingStress.Length)
            {
                int worst = 0;
                for (int m = 1; m < BendingStress.Length; m++)
                    if (BendingStress[m] > BendingStress[worst]) worst = m;

                sb.AppendLine(Options.Bending
                    ? "Bending from the rigid joints at the mean point (included in the limit states):"
                    : "Bending from the rigid joints at the mean point (not part of the limit states):");
                sb.AppendLine("  critical member [" + CriticalMember.ToString(ci) + "] " + Members[CriticalMember].Tag +
                              ": axial " + AxialStress[CriticalMember].ToString("F1", ci) + " MPa, bending " +
                              BendingStress[CriticalMember].ToString("F1", ci) + " MPa (" +
                              (100.0 * CriticalBendingRatio).ToString("F0", ci) + " % of the axial stress)");
                sb.AppendLine("  largest bending stress anywhere: " + BendingStress[worst].ToString("F1", ci) +
                              " MPa in member [" + worst.ToString(ci) + "] " + Members[worst].Tag);
            }

            if (!double.IsNaN(GradientErrorForces))
                sb.AppendLine("DDM check at the mean point (worst relative difference to central finite differences): " +
                              "member forces " + GradientErrorForces.ToString("E2", ci) +
                              ", limit states " + GradientErrorLimitStates.ToString("E2", ci) + ".");

            if (Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (string w in Warnings) sb.AppendLine("  " + w);
            }
            return sb.ToString();
        }
    }

    public static class MemberReliability
    {
        /// <summary>
        /// Numerical zero for an axial force, as a fraction of fy * A. A load contribution below it
        /// is round-off of the FE solve, not a force.
        /// </summary>
        public const double ZeroForce = 1e-9;

        /// <summary>
        /// Runs FORM on every limit state of every member: tension where a load case puts the
        /// member in tension, buckling about y and z where one puts it in compression.
        /// vars and bindings are in the same order; correlation is the physical correlation
        /// matrix of the variables or null. systemSamples is the number of samples of the
        /// importance sampling estimate of the system failure probability (0 = bounds only,
        /// negative = no system assessment at all, which is what the section optimisation uses
        /// for its trial designs).
        /// </summary>
        public static MemberReliabilityResult Run(
            TB_Model mdl,
            List<RandomVariable> vars,
            List<MemberVariable> bindings,
            double[,] correlation = null,
            IDictionary<string, double[]> bucklingLengths = null,
            int maxIterations = 300,
            bool checkGradients = false,
            int systemSamples = SystemReliability.DefaultSamples,
            MemberCheckOptions options = null)
        {
            if (vars == null || bindings == null || vars.Count == 0)
                throw new ArgumentException("At least one random variable is required.");
            if (vars.Count != bindings.Count)
                throw new ArgumentException(
                    $"{vars.Count} random variables but {bindings.Count} bindings. They must match 1:1.");

            var ls = new LS_MemberLimitStates(mdl, bindings, bucklingLengths, options);
            int nMem = ls.Members.Count;

            var res = new MemberReliabilityResult();
            res.Options = ls.Options;
            res.Members = ls.Members;
            res.VariableNames = vars.Select(v => v.Name).ToList();
            for (int i = 0; i < vars.Count; i++)
                res.VariableRoles.Add(bindings[i].ToString() + "  |  " + vars[i].Distribution.ToString() +
                                      ", mean " + vars[i].Mean.ToString("G6", CultureInfo.InvariantCulture) +
                                      ", st.dev. " + vars[i].StdDev.ToString("G6", CultureInfo.InvariantCulture));
            res.Governing = new MemberLimitStateResult[nMem];
            res.StiffnessIsConstant = ls.Fe.StiffnessIsConstant;

            double[] xMean = vars.Select(v => v.Mean).ToArray();
            double[] nMean = ls.AxialForces(xMean);
            if (nMean == null)
                throw new InvalidOperationException(
                    "The model could not be evaluated at the mean point. Check the means of the E variables.");
            res.NMean = nMean;
            res.AxialStress = new double[nMem];
            res.BendingStress = new double[nMem];

            // ------------------------------------------------------------------
            //  Pass 1 (FE state at the mean point): which modes, bending share
            // ------------------------------------------------------------------
            var modes = new List<MemberMode>[nMem];
            for (int m = 0; m < nMem; m++)
            {
                MemberData md = ls.Members[m];
                double tol = ZeroForce * md.Fy0 * md.A;

                // tensile and compressive load contributions at the mean point
                double tens, comp;
                ls.SignedParts(xMean, m, out tens, out comp);

                // stresses at the mean point; the bending part comes from the rigid joints
                // (end moments in [N.m], section moduli in [mm3])
                double[] f = ls.Fe.LocalEndForces(m);
                double sigM = 0.0;
                if (md.Wy > 0.0) sigM += Math.Max(Math.Abs(f[4]), Math.Abs(f[10])) * 1e3 / md.Wy;
                if (md.Wz > 0.0) sigM += Math.Max(Math.Abs(f[5]), Math.Abs(f[11])) * 1e3 / md.Wz;
                res.AxialStress[m] = Math.Abs(nMean[m]) / md.A;
                res.BendingStress[m] = sigM;

                modes[m] = new List<MemberMode>();
                if (tens > tol) modes[m].Add(MemberMode.Tension);
                if (comp < -tol)
                {
                    if (ls.Options.Bending) modes[m].Add(MemberMode.Compression);
                    modes[m].Add(MemberMode.BucklingY);
                    modes[m].Add(MemberMode.BucklingZ);
                }
                // with bending, a member with moments but no axial force still has a check
                if (ls.Options.Bending && modes[m].Count == 0 && sigM > ZeroForce * md.Fy0)
                    modes[m].Add(MemberMode.Tension);
            }

            int assumed = ls.Members.Count(mem => mem.AssumedBucklingCurve);
            if (assumed > 0)
                res.Warnings.Add(assumed.ToString(CultureInfo.InvariantCulture) +
                                 " member(s) have a section without buckling data: i = sqrt(I/A) and buckling curve c were assumed.");

            // ------------------------------------------------------------------
            //  Optional: DDM against central finite differences at the mean point
            // ------------------------------------------------------------------
            if (checkGradients)
                CheckGradients(ls, vars, xMean, modes, res);

            // ------------------------------------------------------------------
            //  Pass 2: FORM per limit state
            // ------------------------------------------------------------------
            var nataf = new HashSet<string>();

            for (int m = 0; m < nMem; m++)
            {
                MemberLimitStateResult best = null;

                foreach (MemberMode mode in modes[m])
                {
                    var row = new MemberLimitStateResult();
                    row.Member = m;
                    row.Tag = ls.Members[m].Tag;
                    row.Mode = mode;
                    row.NMean = nMean[m];

                    double resistance;
                    double gMean = ls.GAndResistance(xMean, m, mode, out resistance);
                    row.UtilisationMean = (resistance > 0.0) ? (resistance - gMean) / resistance : double.NaN;

                    int memberIndex = m;
                    MemberMode memberMode = mode;
                    var solver = new FORMSolverNataf();
                    try
                    {
                        FORMResult fr = solver.Solve(
                            vars,
                            x => ls.G(x, memberIndex, memberMode),
                            correlation: correlation,
                            gradientX: x => ls.GradX(x, memberIndex, memberMode),
                            epsilon1: 1e-6, epsilon2: 1e-6, maxIterations: maxIterations,
                            gScale: ls.Scale(m));

                        if (!fr.Converged && OnKink(fr, x => ls.G(x, memberIndex, memberMode) / ls.Scale(memberIndex)))
                        {
                            fr.Converged = true;
                            fr.ConvergenceMessage = "Converged on a kink of the limit state (beta stationary, g = 0); " +
                                                    fr.ConvergenceMessage;
                        }

                        row.Form = fr;
                        row.MeanSensitivity = solver.ScaledMeanSensitivity;
                        row.Status = fr.Converged
                            ? (fr.ConvergenceMessage.StartsWith("Converged on a kink") ? "ok (kink)" : "ok")
                            : fr.ConvergenceMessage;

                        if (solver.Nataf != null)
                            foreach (string w in solver.Nataf.Warnings) nataf.Add(w);
                    }
                    catch (Exception ex)
                    {
                        row.Status = "FORM failed: " + ex.Message;
                    }

                    res.LimitStates.Add(row);

                    // governing: lowest beta, a converged result always preferred to a non-converged one
                    if (!row.HasBeta) continue;
                    if (best == null ||
                        (row.Converged && !best.Converged) ||
                        (row.Converged == best.Converged && row.Form.Beta < best.Form.Beta))
                        best = row;
                }

                res.Governing[m] = best;
                if (best != null && (res.CriticalMember < 0 || best.Form.Beta < res.MinBeta))
                {
                    res.CriticalMember = m;
                    res.MinBeta = best.Form.Beta;
                }
            }

            if (res.CriticalMember >= 0 && res.AxialStress[res.CriticalMember] > 0.0)
                res.CriticalBendingRatio = res.BendingStress[res.CriticalMember] / res.AxialStress[res.CriticalMember];

            // ------------------------------------------------------------------
            //  Pass 3: the structure as a series system of all limit states
            // ------------------------------------------------------------------
            var sysBeta = new List<double>();
            var sysAlpha = new List<double[]>();
            var sysLabels = new List<string>();
            int notConverged = 0, noBeta = 0;

            foreach (MemberLimitStateResult row in res.LimitStates)
            {
                if (!row.HasBeta)
                {
                    noBeta++;
                    continue;
                }
                if (!row.Converged) notConverged++;

                sysBeta.Add(row.Form.Beta);
                sysAlpha.Add((double[])row.Form.AlphaFactors.Clone());
                sysLabels.Add("[" + row.Member.ToString(CultureInfo.InvariantCulture) + "] " + row.Tag + " " +
                              MemberLimitStateResult.ModeName(row.Mode));
            }

            if (sysBeta.Count > 0 && systemSamples >= 0)
            {
                res.SystemResult = SystemReliability.SeriesSystem(sysBeta, sysAlpha, sysLabels, systemSamples);

                if (notConverged > 0)
                    res.SystemResult.Warnings.Add(notConverged.ToString(CultureInfo.InvariantCulture) +
                        " limit state(s) in the system did not converge in FORM; their beta and alpha are approximate.");
                if (noBeta > 0)
                    res.SystemResult.Warnings.Add(noBeta.ToString(CultureInfo.InvariantCulture) +
                        " limit state(s) have no beta and are left out of the system.");
            }

            res.Warnings.AddRange(nataf);
            res.Factorisations = ls.Fe.Factorisations;
            return res;
        }

        /// <summary>
        /// True when HL-RF stopped on a design point that lies on a kink of the limit-state surface:
        /// the last three beta values agree to 1e-6 (relative) and g at the last point is zero
        /// (|g| / scale &lt; 1e-4). The EN 1993-1-1 expressions switch branch at fixed values (the bound
        /// lambda &lt;= 1 in the Annex B interaction factors, Cm = CmMin, lambda_LT = 0.4, which end
        /// moment is the larger), and the closest failure point can sit exactly on such a switch.
        /// There the gradient is not unique, so the angle criterion of HL-RF cannot be met and the
        /// line search stalls, although the distance (beta) has converged.
        /// </summary>
        private static bool OnKink(FORMResult fr, Func<double[], double> gScaled)
        {
            List<double> h = fr.BetaHistory;
            if (h == null || h.Count < 3 || fr.MPP_X == null) return false;
            double b1 = h[h.Count - 1], b2 = h[h.Count - 2], b3 = h[h.Count - 3];
            double spread = Math.Max(Math.Abs(b1 - b2), Math.Abs(b1 - b3));
            if (!(spread <= 1e-6 * Math.Max(1.0, Math.Abs(b1)))) return false;
            double g = gScaled(fr.MPP_X);
            return !double.IsNaN(g) && Math.Abs(g) < 1e-4;
        }

        /// <summary>
        /// Compares the analytical gradients with central finite differences at x. Differences are
        /// weighted by the standard deviation of each variable, which is how they enter FORM.
        /// </summary>
        private static void CheckGradients(LS_MemberLimitStates ls, List<RandomVariable> vars,
                                           double[] x, List<MemberMode>[] modes, MemberReliabilityResult res)
        {
            int nVar = vars.Count;

            // --- member forces: dN/dp ---
            double[] p = ls.FeParameters(x);
            double worstN = 0.0;
            if (p.Length > 0)
            {
                double[][] fd = ls.Fe.FiniteDifference_dN(p);
                ls.Fe.Evaluate(p);
                for (int m = 0; m < ls.Members.Count; m++)
                {
                    double num = 0.0, den = 0.0;
                    for (int ip = 0; ip < p.Length; ip++)
                    {
                        double w = Math.Abs(p[ip]);
                        num = Math.Max(num, Math.Abs(ls.Fe.dN[m][ip] - fd[m][ip]) * w);
                        den = Math.Max(den, Math.Abs(fd[m][ip]) * w);
                    }
                    if (den > 1e-6 * ls.Members[m].Fy0 * ls.Members[m].A) worstN = Math.Max(worstN, num / den);
                }

                // with bending: the end moments as well
                if (ls.Options.Bending)
                {
                    double[][][] fdF = ls.Fe.FiniteDifference_dF(p);
                    ls.Fe.Evaluate(p);
                    for (int m = 0; m < ls.Members.Count; m++)
                    {
                        double[][] ddmF = ls.Fe.LocalEndForceGradient(m, EndForces.Rows);
                        double mRef = 1e-6 * ls.Members[m].Fy0 * ls.Members[m].Wy * 1e-3;     // [N.m]
                        foreach (int r in EndForces.Rows)
                        {
                            if (r == 6) continue;                                            // N: done above
                            double num = 0.0, den = 0.0;
                            for (int ip = 0; ip < p.Length; ip++)
                            {
                                double w = Math.Abs(p[ip]);
                                num = Math.Max(num, Math.Abs(ddmF[r][ip] - fdF[m][r][ip]) * w);
                                den = Math.Max(den, Math.Abs(fdF[m][r][ip]) * w);
                            }
                            if (den > mRef) worstN = Math.Max(worstN, num / den);
                        }
                    }
                }
            }
            res.GradientErrorForces = worstN;

            // --- limit states: dg/dx ---
            double worstG = 0.0;
            for (int m = 0; m < ls.Members.Count; m++)
            {
                foreach (MemberMode mode in modes[m])
                {
                    double[] ddm = ls.GradX(x, m, mode);
                    if (ddm == null) continue;

                    double num = 0.0, den = 0.0;
                    for (int i = 0; i < nVar; i++)
                    {
                        double h = 1e-6 * Math.Max(Math.Abs(x[i]), 1e-12);
                        double[] xp = (double[])x.Clone(); xp[i] += h;
                        double[] xm = (double[])x.Clone(); xm[i] -= h;
                        double fdi = (ls.G(xp, m, mode) - ls.G(xm, m, mode)) / (2.0 * h);

                        double w = vars[i].StdDev;
                        num = Math.Max(num, Math.Abs(ddm[i] - fdi) * w);
                        den = Math.Max(den, Math.Abs(fdi) * w);
                    }
                    if (den > 0.0) worstG = Math.Max(worstG, num / den);
                }
            }
            res.GradientErrorLimitStates = worstG;
        }
    }
}
