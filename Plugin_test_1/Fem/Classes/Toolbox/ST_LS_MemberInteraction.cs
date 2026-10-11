// =============================================================================
//  ST_LS_MemberInteraction.cs
//
//  WHAT IT IS
//  The EN 1993-1-1 check of a member under axial force AND the bending moments
//  that come from the rigid joints of the frame model. One function,
//  MemberInteraction.Utilisation, is used by BOTH
//    - the Eurocode check (EurocodeMemberCheck): design forces, gammaM0, gammaM1
//    - the reliability limit states (LS_MemberLimitStates): g = 1 - U, with the
//      random fy and E, characteristic resistances times thetaR and the member
//      forces times thetaE
//  so the deterministic and the probabilistic design check exactly the same thing.
//
//  CHECKS (moments vary linearly along the member: the model has nodal loads only)
//    cross-section, 6.2.1(7):
//        U = N/(A fy/gM0) + My/(Wy fy/gM0) + Mz/(Wz fy/gM0)
//    member in compression, 6.3.3 with Annex B (method 2):
//        (6.61)  U = N/(chi_y A fy/gM1) + kyy My/(chi_LT Wy fy/gM1) + kyz Mz/(Wz fy/gM1)
//        (6.62)  U = N/(chi_z A fy/gM1) + kzy My/(chi_LT Wy fy/gM1) + kzz Mz/(Wz fy/gM1)
//    My and Mz are the largest end moments, N the compressive force.
//    A member in tension gets the cross-section check and (6.61)/(6.62) with N = 0
//    (lateral-torsional buckling under the moments alone); the larger one counts.
//
//  SETTINGS (MemberCheckOptions), defaults chosen to match Karamba3D's defaults:
//    Elastic = true     elastic section moduli and the class 3 columns of Annex B
//                       (Karamba: "Elast" = true). false: plastic moduli, class 1/2.
//    CmMin = 0.9        lower limit on Cmy, Cmz and CmLT (Karamba: "SwayFrame" = true).
//                       Otherwise Cm = 0.6 + 0.4 psi >= 0.4 (Annex B, table B.3).
//    LateralTorsional   chi_LT from 6.3.2.3 (rolled sections: lambda_LT,0 = 0.4,
//                       beta = 0.75, alpha_LT from the section table), with
//                       M_cr = C1 pi^2 E Iz / L^2 sqrt(Iw/Iz + L^2 G It / (pi^2 E Iz)),
//                       load at the shear centre, k = kw = 1,
//                       C1 = 1.88 - 1.40 psi + 0.52 psi^2 <= 2.70.
//                       Only for open I-sections; closed and custom sections: chi_LT = 1.
//    I-sections are treated as susceptible to torsional deformation (table B.2).
//
//  NOT INCLUDED: shear and torsion (negligible in truss members), the modification
//  factor f of 6.3.2.3(2), second-order effects, class 4 effective properties.
//
//  UNITS: forces [N], moments [N.m] (as the FE end forces), A [mm2], W [mm3],
//  I [mm4], Iw [mm6], fy, E, G [N/mm2], lengths [m].
// =============================================================================

using System;
using System.Globalization;

namespace Propability_NTNU_v1.Classes.Reliability
{
    /// <summary>How members are checked. The defaults include bending and match Karamba3D's defaults.</summary>
    public class MemberCheckOptions
    {
        /// <summary>false: axial force only (the original limit states and Eurocode check).</summary>
        public bool Bending = true;
        /// <summary>true: elastic moduli and Annex B class 3; false: plastic moduli and class 1/2.</summary>
        public bool Elastic = true;
        /// <summary>Lower limit on the equivalent uniform moment factors Cmy, Cmz, CmLT.</summary>
        public double CmMin = 0.9;
        /// <summary>Lateral-torsional buckling of open I-sections (chi_LT); false: chi_LT = 1.</summary>
        public bool LateralTorsional = true;

        public static MemberCheckOptions Default() { return new MemberCheckOptions(); }
        public static MemberCheckOptions AxialOnly() { return new MemberCheckOptions { Bending = false }; }

        public string Describe()
        {
            if (!Bending) return "axial force only (bending from the rigid joints is not checked)";
            CultureInfo ci = CultureInfo.InvariantCulture;
            return "axial force + bending, EN 1993-1-1 6.2.1(7) and 6.3.3 / Annex B, " +
                   (Elastic ? "elastic (class 3)" : "plastic (class 1-2)") +
                   ", Cm >= " + CmMin.ToString("0.0#", ci) +
                   (LateralTorsional ? ", lateral-torsional buckling 6.3.2.3" : ", chi_LT = 1");
        }
    }

    /// <summary>
    /// The end forces of a member that the check needs, in the layout of the FE end-force vector:
    /// N at the end node (tension positive) [N], end moments about local y and z [N.m].
    /// </summary>
    public struct EndForces
    {
        public double N, My1, My2, Mz1, Mz2;

        /// <summary>Rows of the 12-component local end-force vector used: N, My1, My2, Mz1, Mz2.</summary>
        public static readonly int[] Rows = { 6, 4, 10, 5, 11 };

        public static EndForces FromLocal(double[] f)
        {
            return new EndForces { N = f[6], My1 = f[4], My2 = f[10], Mz1 = f[5], Mz2 = f[11] };
        }

        public double this[int j]
        {
            get { switch (j) { case 0: return N; case 1: return My1; case 2: return My2; case 3: return Mz1; default: return Mz2; } }
            set { switch (j) { case 0: N = value; break; case 1: My1 = value; break; case 2: My2 = value; break; case 3: Mz1 = value; break; default: Mz2 = value; break; } }
        }

        public EndForces Scaled(double s)
        {
            return new EndForces { N = s * N, My1 = s * My1, My2 = s * My2, Mz1 = s * Mz1, Mz2 = s * Mz2 };
        }

        /// <summary>Largest end moment about y [N.m], absolute.</summary>
        public double MyMax { get { return Math.Max(Math.Abs(My1), Math.Abs(My2)); } }
        /// <summary>Largest end moment about z [N.m], absolute.</summary>
        public double MzMax { get { return Math.Max(Math.Abs(Mz1), Math.Abs(Mz2)); } }
    }

    /// <summary>Intermediate values of one check, for the reports.</summary>
    public class InteractionDetail
    {
        public double U = double.NaN;
        public double NEd, MyEd, MzEd;                      // [N], [N.m], [N.m]; NEd compression positive in member checks
        public double LambdaY = double.NaN, LambdaZ = double.NaN, ChiY = double.NaN, ChiZ = double.NaN;
        public double LambdaLT = double.NaN, ChiLT = double.NaN, Mcr = double.NaN;
        public double Cmy = double.NaN, Cmz = double.NaN, CmLT = double.NaN;
        public double Kyy = double.NaN, Kyz = double.NaN, Kzy = double.NaN, Kzz = double.NaN;
        /// <summary>Which expression governed, e.g. "6.2.1(7)" or "6.61".</summary>
        public string Governing = "";
    }

    public static class MemberInteraction
    {
        /// <summary>
        /// Utilisation of member md for the given end forces.
        /// mode: Tension (cross-section with the tensile force, and the member checks with N = 0),
        /// Compression (cross-section with the compressive force), BucklingY (6.61), BucklingZ (6.62).
        /// r0 and r1 multiply the characteristic resistances: 1/gammaM0 and 1/gammaM1 in the
        /// Eurocode check, thetaR in the limit states. fy, E and G are the values to use (random or
        /// nominal) [N/mm2].
        /// </summary>
        public static double Utilisation(MemberData md, EndForces f, double fy, double E, double G,
                                         double r0, double r1, MemberCheckOptions o, MemberMode mode,
                                         InteractionDetail d = null)
        {
            double wy = o.Elastic ? md.Wy : md.Wpy;
            double wz = o.Elastic ? md.Wz : md.Wpz;
            double myEd = f.MyMax * 1e3;                    // [N.mm]
            double mzEd = f.MzMax * 1e3;
            double myRk = wy * fy, mzRk = wz * fy;          // [N.mm]
            double nRk = md.A * fy;                          // [N]

            if (d != null) { d.MyEd = f.MyMax; d.MzEd = f.MzMax; }

            if (mode == MemberMode.Tension || mode == MemberMode.Compression)
            {
                double n = (mode == MemberMode.Tension) ? Math.Max(f.N, 0.0) : Math.Max(-f.N, 0.0);
                double cs = n / (nRk * r0) + Ratio(myEd, myRk * r0) + Ratio(mzEd, mzRk * r0);
                if (d != null) { d.NEd = n; d.U = cs; d.Governing = "6.2.1(7)"; }
                if (mode == MemberMode.Compression) return cs;

                // tension: lateral-torsional buckling under the moments alone (6.61/6.62 with N = 0)
                double uy = MemberCheck(md, f, 0.0, fy, E, G, r1, o, true, null);
                double uz = MemberCheck(md, f, 0.0, fy, E, G, r1, o, false, null);
                double u = Math.Max(cs, Math.Max(uy, uz));
                if (d != null && u > cs)
                {
                    MemberCheck(md, f, 0.0, fy, E, G, r1, o, uy >= uz, d);
                    d.NEd = n;
                    d.U = u;
                }
                return u;
            }

            double nc = Math.Max(-f.N, 0.0);
            return MemberCheck(md, f, nc, fy, E, G, r1, o, mode == MemberMode.BucklingY, d);
        }

        /// <summary>(6.61) if aboutY, else (6.62), for the compressive force nc [N] (>= 0).</summary>
        private static double MemberCheck(MemberData md, EndForces f, double nc, double fy, double E, double G,
                                          double r1, MemberCheckOptions o, bool aboutY, InteractionDetail d)
        {
            double wy = o.Elastic ? md.Wy : md.Wpy;
            double wz = o.Elastic ? md.Wz : md.Wpz;
            double myEd = f.MyMax * 1e3, mzEd = f.MzMax * 1e3;
            double myRk = wy * fy, mzRk = wz * fy, nRk = md.A * fy;

            // flexural buckling
            double dChi;
            double ly = MemberResistance.LambdaBar(md.LcrY, md.iy, fy, E);
            double lz = MemberResistance.LambdaBar(md.LcrZ, md.iz, fy, E);
            double chiY = MemberResistance.Chi(ly, md.AlphaY, out dChi);
            double chiZ = MemberResistance.Chi(lz, md.AlphaZ, out dChi);
            double nY = nc / (chiY * nRk * r1);
            double nZ = nc / (chiZ * nRk * r1);

            // equivalent uniform moment factors from the end moments (linear moment diagram)
            double psiY = Psi(-f.My1, f.My2), psiZ = Psi(-f.Mz1, f.Mz2);
            double cmy = Math.Max(o.CmMin, Math.Max(0.4, 0.6 + 0.4 * psiY));
            double cmz = Math.Max(o.CmMin, Math.Max(0.4, 0.6 + 0.4 * psiZ));
            double cmLT = cmy;

            // lateral-torsional buckling
            bool susceptible = md.OpenSection;
            double chiLT = 1.0, lLT = double.NaN, mcr = double.NaN;
            if (o.LateralTorsional && md.OpenSection && md.Iz > 0.0 && myEd > 0.0)
            {
                double L = md.LcrLT * 1e3;                                       // [mm]
                double pe = Math.PI * Math.PI * E * md.Iz / (L * L);            // [N]
                double c1 = Math.Min(1.88 - 1.40 * psiY + 0.52 * psiY * psiY, 2.70);
                double gEff = G;
                mcr = c1 * pe * Math.Sqrt(md.Iw / md.Iz + L * L * gEff * md.It / (Math.PI * Math.PI * E * md.Iz));
                lLT = Math.Sqrt(myRk / mcr);
                chiLT = ChiLT(lLT, md.AlphaLT);
            }

            // interaction factors, Annex B (table B.1, and B.2 for members susceptible to torsional deformation)
            double lyC = Math.Min(ly, 1.0), lzC = Math.Min(lz, 1.0);
            double kyy, kzz, kyz, kzy;
            if (o.Elastic)
            {
                kyy = cmy * (1.0 + 0.6 * lyC * nY);
                kzz = cmz * (1.0 + 0.6 * lzC * nZ);
                kyz = kzz;
                kzy = susceptible ? 1.0 - 0.05 * lzC * nZ / (cmLT - 0.25) : 0.8 * kyy;
            }
            else
            {
                kyy = cmy * (1.0 + (lyC - 0.2) * nY);
                kzz = md.OpenSection ? cmz * (1.0 + (2.0 * lzC - 0.6) * nZ) : cmz * (1.0 + (lzC - 0.2) * nZ);
                kyz = 0.6 * kzz;
                if (!susceptible) kzy = 0.6 * kyy;
                else if (lz >= 0.4) kzy = 1.0 - 0.1 * lzC * nZ / (cmLT - 0.25);
                else kzy = Math.Min(0.6 + lz, 1.0 - 0.1 * lz * nZ / (cmLT - 0.25));
            }

            double mY = Ratio(myEd, chiLT * myRk * r1);
            double mZ = Ratio(mzEd, mzRk * r1);
            double u = aboutY ? nY + kyy * mY + kyz * mZ
                              : nZ + kzy * mY + kzz * mZ;

            if (d != null)
            {
                d.U = u; d.NEd = nc;
                d.LambdaY = ly; d.LambdaZ = lz; d.ChiY = chiY; d.ChiZ = chiZ;
                d.LambdaLT = lLT; d.ChiLT = chiLT; d.Mcr = mcr * 1e-3;     // [N.m]
                d.Cmy = cmy; d.Cmz = cmz; d.CmLT = cmLT;
                d.Kyy = kyy; d.Kyz = kyz; d.Kzy = kzy; d.Kzz = kzz;
                d.Governing = aboutY ? "6.61" : "6.62";
            }
            return u;
        }

        /// <summary>EN 1993-1-1 6.3.2.3: chi_LT for rolled sections, lambda_LT,0 = 0.4, beta = 0.75.</summary>
        public static double ChiLT(double lambdaLT, double alphaLT)
        {
            if (!(lambdaLT > 0.4)) return 1.0;
            double a = (alphaLT > 0.0) ? alphaLT : 0.34;
            double phi = 0.5 * (1.0 + a * (lambdaLT - 0.4) + 0.75 * lambdaLT * lambdaLT);
            double chi = 1.0 / (phi + Math.Sqrt(phi * phi - 0.75 * lambdaLT * lambdaLT));
            return Math.Min(chi, Math.Min(1.0, 1.0 / (lambdaLT * lambdaLT)));
        }

        /// <summary>
        /// psi = smaller / larger end moment of the moment diagram (signed, -1..1); 1 for no moment.
        /// m0 and mL are the moments of the diagram at the start and the end.
        /// </summary>
        public static double Psi(double m0, double mL)
        {
            double a0 = Math.Abs(m0), aL = Math.Abs(mL);
            if (Math.Max(a0, aL) < 1e-12) return 1.0;
            return (a0 >= aL) ? mL / m0 : m0 / mL;
        }

        private static double Ratio(double effect, double resistance)
        {
            return (effect > 0.0) ? effect / resistance : 0.0;
        }
    }
}
