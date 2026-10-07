using System;
using MathNet.Numerics.Distributions;
using Plugin_test_1.Reliability;

namespace Plugin_test_1.Reliability.Reliability_TryOuts
{
    /// <summary>
    /// FORM Diagnostics - Debug helper to understand HLRF behavior
    /// Add this to understand beta behavior at different utilization levels
    /// </summary>
    public class FORMDiagnostics
    {
        public static void DiagnoseConvergence(double W_el, double L, double a, 
                                              StochasticVariable[] rvs, 
                                              double targetUtilization)
        {
            // This is what your FORM.cs is computing
            Console.WriteLine($"\n=== FORM Diagnostic Analysis ===");
            Console.WriteLine($"Target Utilization: {targetUtilization:F2}");
            Console.WriteLine($"Section Modulus: {W_el:F0} mm³");
            Console.WriteLine($"Span: {L:F2} m, Load position: {a:F2} m");

            // Extract RV parameters
            double fy_mean  = rvs[0].Mean;
            double G_mean   = rvs[1].Mean;
            double Q_mean   = rvs[2].Mean;
            double M_max    = BeamMechanics.MaxMoment(L, G_mean, Q_mean, a);

            Console.WriteLine($"\nRV Mean Values:");
            Console.WriteLine($"  fy = {fy_mean:F1} MPa");
            Console.WriteLine($"  G  = {G_mean:F1} kN/m");
            Console.WriteLine($"  Q  = {Q_mean:F1} kN");
            Console.WriteLine($"  M_max = {M_max:F2} kNm");

            // Compute resistance and load effect at mean
            double R_mean = W_el * fy_mean;     // Resistance capacity in N*mm
            double S_mean = M_max * 1e6;         // Load effect in N*mm
            double G_mean_value = R_mean - S_mean;

            Console.WriteLine($"\nAt Mean Values:");
            Console.WriteLine($"  R (capacity) = {R_mean:E3} N*mm");
            Console.WriteLine($"  S (effect)   = {S_mean:E3} N*mm");
            Console.WriteLine($"  G = R - S = {G_mean_value:E3}");
            Console.WriteLine($"  Status: {(G_mean_value > 0 ? "SAFE" : "UNSAFE")}");

            // What FORM.Run returns
            double beta;
            bool converged;
            FORM.Run(W_el, L, a, rvs, out beta, out converged);

            Console.WriteLine($"\nFORM Results:");
            Console.WriteLine($"  Beta = {beta:F4}");
            Console.WriteLine($"  Pf = {Normal.CDF(0, 1, -beta):E3}");
            Console.WriteLine($"  Converged = {converged}");

            // Analysis
            if (beta < 0)
                Console.WriteLine($"\n⚠️  NEGATIVE BETA: Structure is unsafe. Failure is likely.");
            else if (beta > 0 && beta < 1.5)
                Console.WriteLine($"\n⚠️  LOW BETA: Limited safety margin ({beta:F2}).");
            else if (beta > 1.5 && beta < 3.0)
                Console.WriteLine($"\n✅ MODERATE BETA: Acceptable safety ({beta:F2}).");
            else
                Console.WriteLine($"\n✅ HIGH BETA: Good safety margin ({beta:F2}).");
        }

        /// <summary>
        /// Trace HLRF iterations to see how the algorithm converges
        /// Useful for understanding why beta behaves unexpectedly
        /// </summary>
        public static void TraceHLRFIterations(double W_el, double L, double a,
                                               StochasticVariable[] rvs)
        {
            Console.WriteLine($"\n=== HLRF Iteration Trace ===");
            Console.WriteLine($"Section: W_el={W_el:F0} mm³, Span={L:F2}m, a={a:F2}m");
            Console.WriteLine($"{"Iter",-4} {"u[0]",-10} {"u[1]",-10} {"u[2]",-10} {"G_u",-12} {"||grad||",-12}");
            Console.WriteLine(new string('-', 70));

            // This would require modifying FORM.cs to expose iteration details
            Console.WriteLine("(Requires FORM.cs modification to expose iteration data)");
        }
    }

    /// <summary>
    /// Key Insight: If utilization > 1, your structure is UNSAFE at the mean load level.
    /// 
    /// Utilization = M_Ed / M_Rd
    /// 
    /// At characteristic loads (which FORM uses):
    /// - If Util ≤ 1: Safe (G > 0)
    /// - If Util > 1: Unsafe (G < 0)
    /// 
    /// The HLRF algorithm has quirks:
    /// - It searches for the design point (closest point to origin where G=0)
    /// - If you're already in failure region (G << 0), β can increase
    ///   as you move further into failure (distance to origin increases)
    /// 
    /// SOLUTION: You might want to:
    /// 1. Flip the limit state: G = S - R (instead of R - S)
    ///    This makes β negative for failures
    /// 2. Or understand that β > 0 means "distance to limit state"
    ///    not "safety metric" when G << 0
    /// </summary>
    public static class FORMDiagnosticsNotes { }
}
