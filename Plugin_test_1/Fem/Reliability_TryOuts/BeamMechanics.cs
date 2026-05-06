using System;
using System.Collections.Generic;
using Plugin_test_1.Reliability;

namespace FORMBeam
{
    /// <summary>
    /// Simply-supported beam mechanics: bending moments and deflections.
    /// All inputs/outputs use consistent units:
    ///   lengths in [m], forces in [kN], moments in [kNm], deflections in [mm].
    /// </summary>
    internal static class BeamMechanics
    {
        // ── Bending moment ────────────────────────────────────────────────────

        /// <summary>
        /// Bending moment [kNm] at position z [m] along the beam.
        /// G: permanent load [kNm], Q: point load [kN] at position a [m] from left support.
        /// </summary>
        public static double MomentAt(double z, double L, double G, double Q, double a)
        {
            double R_A = G * L / 2.0 + Q * (L - a) / L;
            double M   = R_A * z - G * z * z / 2.0;
            if (z > a) M -= Q * (z - a);
            return M;
        }

        /// <summary>Maximum bending moment [kNm] — sweeps 101 positions + key points.</summary>
        public static double MaxMoment(double L, double G, double Q, double a)
        {
            double max = double.MinValue;
            // Key structural points
            double[] keyPts = { a, L / 2.0 };
            foreach (double z in keyPts)
                if (z >= 0 && z <= L)
                    max = Math.Max(max, MomentAt(z, L, G, Q, a));

            // Fine sweep
            for (int i = 0; i <= 100; i++)
            {
                double z = i * L / 100.0;
                max = Math.Max(max, MomentAt(z, L, G, Q, a));
            }
            return max;
        }

        // ── Deflection ────────────────────────────────────────────────────────

        /// <summary>
        /// Deflection [mm] at position z [m] — superposition of UDL + point load.
        /// E_MPa [N/mm²], I_mm4 [mm⁴].
        /// </summary>
        public static double DeflectionAt(double z, double L, double G, double Q,
                                          double a, double E_MPa, double I_mm4)
        {
            double EI  = E_MPa * I_mm4;   // N·mm²
            double gN  = G * 1.0;          // kN/m → N/mm  (×1000 N/kN ÷ 1000 mm/m = ×1)
            double qN  = Q * 1e3;          // kN   → N
            double Lmm = L * 1e3;          // m    → mm
            double amm = a * 1e3;
            double bmm = Lmm - amm;
            double zmm = z * 1e3;

            // UDL — standard simply-supported formula
            double vUdl = (gN * zmm / (24.0 * EI))
                        * (Lmm * Lmm * Lmm - 2.0 * Lmm * zmm * zmm + zmm * zmm * zmm);

            // Point load — Macaulay
            double vPt;
            if (zmm <= amm)
                vPt = (qN * bmm * zmm / (6.0 * EI * Lmm))
                    * (Lmm * Lmm - bmm * bmm - zmm * zmm);
            else
                vPt = (qN * amm * (Lmm - zmm) / (6.0 * EI * Lmm))
                    * (2.0 * Lmm * zmm - zmm * zmm - amm * amm);

            return vUdl + vPt;
        }

        // ── Eurocode sizing ───────────────────────────────────────────────────

        /// <summary>Required elastic section modulus [mm³] from Eurocode ULS check.</summary>
        public static double WelEurocode(double L, double Gk, double Qk, double a, double fyk)
        {
            double M_Ed = MaxMoment(L, EurocodeConstants.GammaG * Gk, EurocodeConstants.GammaQ * Qk, a); // kNm
            return EurocodeConstants.GammaM * M_Ed * 1e6 / fyk;                                   // mm³
        }
    }
}
