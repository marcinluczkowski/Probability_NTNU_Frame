using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FORM = Plugin_test_1.Reliability.FORM;
using RV = Plugin_test_1.Reliability.FORM.RandomVariable;

namespace Plugin_test_1.Reliability.Tests
{
    /// <summary>
    /// Unit test for FORM core using an analytic simply-supported beam example.
    /// 
    /// This test does NOT require the FEM solver; it uses a closed-form limit state function
    /// to verify FORM algorithm correctness.
    /// 
    /// Problem: Simply-supported beam with point load at midspan
    /// Random variables:
    ///   E ~ Normal(200e9 Pa, 10e9 Pa)      - Young's modulus
    ///   F ~ Normal(50000 N, 7500 N)        - Point load magnitude
    ///   I ~ Normal(8.33e-5 m^4, 4.17e-6 m^4) - Moment of inertia
    /// 
    /// Limit state (deflection): g(x) = L/300 - F*L³/(48*E*I)
    /// where L = 5.0 m (span)
    /// 
    /// Expected FORM results (at β ≈ 2.75):
    ///   β ≈ 2.75
    ///   Pf ≈ 0.003 (0.3%)
    ///   α_F ≈ 0.986 (force is dominant)
    ///   α_E ≈ -0.115 (stiffness reduces limit state - inverse effect)
    ///   α_I ≈ -0.115 (inertia reduces limit state - inverse effect)
    /// </summary>
    [TestClass]
    public class FORMCoreTest
    {
        /// <summary>
        /// Runs the analytic beam test.
        /// </summary>
        [TestMethod]
        public void RunAnalyticBeamTest()
        {
            // Run test without UI (MessageBox interferes with test discovery)
            string result = RunAnalyticBeamTestInternal();
            
            // Parse results and assert
            VerifyResults(result);
        }

        /// <summary>
        /// Runs the analytic beam test and returns a summary string.
        /// Call from Grasshopper button or test runner:
        ///   var result = FORMCoreTest.RunAnalyticBeamTest();
        ///   MessageBox.Show(result);
        /// </summary>
        /// <returns>Human-readable test summary with pass/fail indicators.</returns>
        public static string RunAnalyticBeamTestInternal()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== FORM Core Unit Test (Analytic Simply-Supported Beam) ===");
            sb.AppendLine();

            try
            {
                // ===== Setup: Random Variables =====
                sb.AppendLine("SETUP: Random Variables");
                sb.AppendLine("  E ~ Normal(200e9 Pa, 10e9 Pa)");
                sb.AppendLine("  F ~ Normal(50000 N, 7500 N)");
                sb.AppendLine("  I ~ Normal(8.33e-5 m⁴, 4.17e-6 m⁴)");
                sb.AppendLine();

                var variables = new List<RV>
                {
                    new RV("E", FORM.DistributionType.Normal, 200e9, 10e9),
                    new RV("F", FORM.DistributionType.Normal, 50000.0, 7500.0),
                    new RV("I", FORM.DistributionType.Normal, 8.33e-5, 4.17e-6)
                };

                // ===== Limit State Function (closed form) =====
                // g(x) = L/300 - F*L³/(48*E*I)
                // where L = 5.0 m
                // This is deflection limit state: safe if deflection < L/300
                double L = 5.0;
                double deflectionLimit = L / 300.0;  // ~0.01667 m

                Func<double[], double> limitStateFunction = (x) =>
                {
                    double E = x[0];  // Young's modulus
                    double F = x[1];  // Point load
                    double I = x[2];  // Moment of inertia

                    // Deflection at midspan: δ = F*L³/(48*E*I)
                    double deflection = (F * L * L * L) / (48.0 * E * I);

                    // Limit state: g = capacity - response = deflectionLimit - deflection
                    double g = deflectionLimit - deflection;

                    return g;
                };

                sb.AppendLine($"Limit State: g(x) = {deflectionLimit:E3} - F·L³/(48·E·I)");
                sb.AppendLine();

                // ===== Run FORM Solver =====
                sb.AppendLine("SOLVING: FORM with HL-RF Algorithm");
                sb.AppendLine("  Convergence tolerance: 1e-6");
                sb.AppendLine("  Max iterations: 100");
                sb.AppendLine();

                var solver = new FORM.FORMSolver();
                FORM.FORMResult result = solver.Solve(variables, limitStateFunction, 1e-6, 1e-6, 100);

                // ===== Results =====
                sb.AppendLine("RESULTS:");
                sb.AppendLine($"  β (Reliability Index):    {result.Beta:F4}");
                sb.AppendLine($"  Pf (Probability Failure): {result.ProbabilityOfFailure:E6}");
                sb.AppendLine($"  Converged:                {result.Converged}");
                sb.AppendLine($"  Iterations:               {result.Iterations}");
                sb.AppendLine();

                // ===== Design Point (MPP) =====
                sb.AppendLine("DESIGN POINT (MPP):");
                sb.AppendLine("  Physical space (x*):");
                sb.AppendLine($"    E  = {result.MPP_X[0]:E6} Pa");
                sb.AppendLine($"    F  = {result.MPP_X[1]:E6} N");
                sb.AppendLine($"    I  = {result.MPP_X[2]:E6} m⁴");
                sb.AppendLine();
                sb.AppendLine("  Standard normal space (u*):");
                sb.AppendLine($"    u_E = {result.MPP_U[0]:F4}");
                sb.AppendLine($"    u_F = {result.MPP_U[1]:F4}");
                sb.AppendLine($"    u_I = {result.MPP_U[2]:F4}");
                sb.AppendLine();

                // ===== Sensitivity Factors (Alpha) =====
                sb.AppendLine("SENSITIVITY FACTORS (α = u* / β):");
                sb.AppendLine($"  α_E = {result.AlphaFactors[0]:F4}  (E elasticity)");
                sb.AppendLine($"  α_F = {result.AlphaFactors[1]:F4}  (F load)");
                sb.AppendLine($"  α_I = {result.AlphaFactors[2]:F4}  (I inertia)");
                sb.AppendLine();

                // ===== Validation Against Expected Values =====
                sb.AppendLine("VALIDATION (Expected ± 1%):");
                sb.AppendLine();

                const double expectedBeta = 2.75;
                const double expectedPf = 0.003;
                const double expectedAlphaF = 0.986;
                double tolerance = 0.01;  // 1%

                bool betaOK = Math.Abs(result.Beta - expectedBeta) / expectedBeta < tolerance;
                bool pfOK = Math.Abs(result.ProbabilityOfFailure - expectedPf) / expectedPf < tolerance;
                bool alphaFOK = Math.Abs(result.AlphaFactors[1] - expectedAlphaF) / expectedAlphaF < tolerance;

                sb.AppendLine($"  β:    Expected {expectedBeta:F3}, Got {result.Beta:F4}  [{(betaOK ? "PASS" : "FAIL")}]");
                sb.AppendLine($"  Pf:   Expected {expectedPf:E3}, Got {result.ProbabilityOfFailure:E6}  [{(pfOK ? "PASS" : "FAIL")}]");
                sb.AppendLine($"  α_F:  Expected {expectedAlphaF:F3}, Got {result.AlphaFactors[1]:F4}  [{(alphaFOK ? "PASS" : "FAIL")}]");
                sb.AppendLine();

                bool allPass = betaOK && pfOK && alphaFOK;
                sb.AppendLine(allPass ? "✓ OVERALL: PASS" : "✗ OVERALL: FAIL");
                sb.AppendLine();

                // ===== Convergence History =====
                sb.AppendLine("CONVERGENCE HISTORY (β at each iteration):");
                for (int i = 0; i < result.BetaHistory.Count; i++)
                {
                    sb.AppendLine($"  Iter {i + 1:D2}: β = {result.BetaHistory[i]:F6}");
                }
                sb.AppendLine();

                // ===== Summary Formatting =====
                sb.AppendLine("FORM SUMMARY (formatted for display):");
                sb.AppendLine(result.FormatSummary());
                sb.AppendLine();

                return sb.ToString();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ERROR: {ex.Message}");
                sb.AppendLine($"Stack trace: {ex.StackTrace}");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Verifies test results meet expected criteria
        /// </summary>
        private void VerifyResults(string output)
        {
            // Check that output contains success markers
            Assert.IsTrue(output.Contains("[PASS]"), 
                "Test validation failed - not all checks marked [PASS]");
            Assert.IsTrue(output.Contains("✓ OVERALL: PASS"), 
                "Overall test result was not PASS");
        }
    }
}
