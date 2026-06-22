using System;
using System.Collections.Generic;
using System.Text;
using MathNet.Numerics;
using MathNet.Numerics.Distributions;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// Encapsulates the results of a FORM (First Order Reliability Method) analysis.
    /// Contains the reliability index, probability of failure, design point coordinates,
    /// sensitivity factors, and convergence information.
    /// </summary>
    public class FORMResult
    {
        /// <summary>
        /// Reliability index β.
        /// β = distance from origin to the limit state surface in standard normal space.
        /// Higher β indicates higher reliability (lower probability of failure).
        /// </summary>
        public double Beta { get; set; }

        /// <summary>
        /// Probability of failure Pf = Φ(-β).
        /// The probability that the limit state function g(X) ≤ 0 (failure).
        /// Ranges from 0 (perfectly safe) to 1 (certain failure).
        /// </summary>
        public double ProbabilityOfFailure { get; set; }

        /// <summary>
        /// Most Probable Point (design point) in standard normal space (U-space).
        /// This is the point on the limit state surface closest to the origin in U-space.
        /// Array length equals number of random variables.
        /// </summary>
        public double[] MPP_U { get; set; }

        /// <summary>
        /// Most Probable Point in physical space (X-space).
        /// This is the design point transformed back to the original variable space.
        /// Array length equals number of random variables.
        /// </summary>
        public double[] MPP_X { get; set; }

        /// <summary>
        /// Direction cosines (sensitivity factors) αᵢ = u*ᵢ / β.
        /// Each α indicates the contribution of variable i to the reliability index.
        /// Sum of squares: Σ(αᵢ)² = 1.
        /// Useful for identifying which variables have the greatest impact on reliability.
        /// Array length equals number of random variables.
        /// </summary>
        public double[] AlphaFactors { get; set; }

        /// <summary>
        /// Number of iterations performed by the FORM algorithm.
        /// </summary>
        public int Iterations { get; set; }

        /// <summary>
        /// Flag indicating whether the FORM algorithm converged to the specified tolerance.
        /// True if converged successfully; false if max iterations exceeded.
        /// </summary>
        public bool Converged { get; set; }

        /// <summary>
        /// History of β values at each iteration.
        /// Useful for convergence visualization and debugging.
        /// </summary>
        public List<double> BetaHistory { get; set; }

        /// <summary>
        /// Human-readable convergence message for Grasshopper panel output.
        /// Examples:
        /// "Converged in 8 iterations"
        /// "Failed to converge: max iterations (50) exceeded"
        /// </summary>
        public string ConvergenceMessage { get; set; }

        /// <summary>
        /// Initializes a new FORMResult with default values.
        /// </summary>
        public FORMResult()
        {
            Beta = 0.0;
            ProbabilityOfFailure = 1.0;
            MPP_U = new double[0];
            MPP_X = new double[0];
            AlphaFactors = new double[0];
            Iterations = 0;
            Converged = false;
            BetaHistory = new List<double>();
            ConvergenceMessage = "Not run";
        }

        /// <summary>
        /// Initializes a new FORMResult with specified array dimensions.
        /// </summary>
        /// <param name="numVariables">Number of random variables in the problem.</param>
        public FORMResult(int numVariables)
        {
            Beta = 0.0;
            ProbabilityOfFailure = 1.0;
            MPP_U = new double[numVariables];
            MPP_X = new double[numVariables];
            AlphaFactors = new double[numVariables];
            Iterations = 0;
            Converged = false;
            BetaHistory = new List<double>();
            ConvergenceMessage = "Not run";
        }

        /// <summary>
        /// Generates a multi-line summary of the FORM results suitable for display
        /// in a Grasshopper panel or console output.
        /// </summary>
        /// <returns>Formatted string with reliability index, failure probability, MPP, and sensitivities.</returns>
        public string FormatSummary()
        {
            var sb = new StringBuilder();

            // Header
            sb.AppendLine("╔════════════════════════════════════════╗");
            sb.AppendLine("║      FORM Reliability Analysis         ║");
            sb.AppendLine("╚════════════════════════════════════════╝");
            sb.AppendLine();

            // Convergence status
            if (Converged)
                sb.AppendLine($"✓ {ConvergenceMessage}");
            else
                sb.AppendLine($"✗ {ConvergenceMessage}");
            sb.AppendLine();

            // Reliability metrics
            sb.AppendLine("─ Reliability Metrics ─");
            sb.AppendLine($"Reliability Index (β):        {Beta:F6}");
            sb.AppendLine($"Probability of Failure (Pf):  {ProbabilityOfFailure:E6}");
            sb.AppendLine($"Annual Pf (assume 50-yr life): {1 - Math.Pow(1 - ProbabilityOfFailure, 50):E6}");
            sb.AppendLine();

            // Most Probable Point (MPP) in X-space
            if (MPP_X != null && MPP_X.Length > 0)
            {
                sb.AppendLine("─ Design Point (X-space) ─");
                for (int i = 0; i < MPP_X.Length; i++)
                {
                    sb.AppendLine($"x*[{i}] = {MPP_X[i].Round(4)}");
                }
                sb.AppendLine();
            }

            // Most Probable Point (MPP) in U-space
            if (MPP_U != null && MPP_U.Length > 0)
            {
                sb.AppendLine("─ Design Point (U-space) ─");
                for (int i = 0; i < MPP_U.Length; i++)
                {
                    sb.AppendLine($"u*[{i}] = {MPP_U[i]:F6}");
                }
                sb.AppendLine();
            }

            // Direction cosines / sensitivity factors
            if (AlphaFactors != null && AlphaFactors.Length > 0)
            {
                sb.AppendLine("─ Sensitivity Factors (α) ─");
                double sumSqAlpha = 0.0;
                for (int i = 0; i < AlphaFactors.Length; i++)
                {
                    double alpha_sq = AlphaFactors[i] * AlphaFactors[i];
                    sumSqAlpha += alpha_sq;
                    sb.AppendLine($"α[{i}] = {AlphaFactors[i]:F6}  (weight: {alpha_sq * 100:F2}%)");
                }
                sb.AppendLine();

                // Verify orthonormality
                if (Math.Abs(sumSqAlpha - 1.0) > 0.01)
                {
                    sb.AppendLine($"⚠ Warning: Σ(α²) = {sumSqAlpha:F6} (should be ≈ 1.0)");
                    sb.AppendLine();
                }
            }

            // Convergence history
            if (BetaHistory != null && BetaHistory.Count > 0)
            {
                sb.AppendLine("─ Convergence History ─");
                sb.AppendLine($"Iterations: {Iterations}");
                sb.AppendLine("β progression:");
                for (int i = 0; i < Math.Min(BetaHistory.Count, 20); i++)
                {
                    sb.AppendLine($"  Iter {i}: β = {BetaHistory[i]:F6}");
                }
                if (BetaHistory.Count > 20)
                {
                    sb.AppendLine($"  ... ({BetaHistory.Count - 20} more iterations)");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Calculates the probability of failure Pf from the reliability index β.
        /// Pf = Φ(-β) where Φ is the standard normal CDF.
        /// Should be called after Beta is set to populate ProbabilityOfFailure.
        /// </summary>
        public void ComputeProbabilityOfFailure()
        {
            // Φ(-β) = 1 - Φ(β) where Φ is standard normal CDF
            ProbabilityOfFailure = Normal.CDF(0, 1, -Beta);
        }

        /// <summary>
        /// Computes the direction cosines (sensitivity factors) from the design point in U-space.
        /// αᵢ = u*ᵢ / β
        /// Call after Beta and MPP_U are set.
        /// Throws InvalidOperationException if Beta ≈ 0.
        /// </summary>
        public void ComputeAlphaFactors()
        {
            if (Math.Abs(Beta) < 1e-16)
            {
                throw new InvalidOperationException(
                    "Cannot compute alpha factors: Beta is zero or nearly zero. " +
                    "The design point may be at the origin (perfect safety).");
            }

            if (MPP_U == null || MPP_U.Length == 0)
            {
                throw new InvalidOperationException(
                    "Cannot compute alpha factors: MPP_U is not set or empty.");
            }

            AlphaFactors = new double[MPP_U.Length];
            for (int i = 0; i < MPP_U.Length; i++)
            {
                AlphaFactors[i] = MPP_U[i] / Beta;
            }
        }

        /// <summary>
        /// Updates the convergence message based on convergence status and iteration count.
        /// Call this after Converged and Iterations are set.
        /// </summary>
        /// <param name="maxIterations">Maximum allowed iterations (used in failure message).</param>
        public void UpdateConvergenceMessage(int maxIterations)
        {
            if (Converged)
            {
                ConvergenceMessage = $"Converged in {Iterations} iteration{(Iterations == 1 ? "" : "s")}";
            }
            else
            {
                ConvergenceMessage = $"Failed to converge: max iterations ({maxIterations}) exceeded";
            }
        }
    }
}
