using System;
using System.Collections.Generic;
using MathNet.Numerics.Distributions;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// FORM (First Order Reliability Method) solver using the HL-RF (Hasofer-Lind-Rackwitz-Fiessler) algorithm.
    /// 
    /// This solver transforms a reliability problem from physical space (X-space) to standard normal space (U-space),
    /// then iteratively finds the Most Probable Point (MPP) on the limit state surface that is closest to the origin
    /// in U-space. The distance to this point (β) is the reliability index.
    /// 
    /// Future Enhancement: Direct differentiation can replace the finite difference gradient computation
    /// in Step 4 by implementing a method with signature:
    ///   private double[] ComputeGradientDirectly(TB_Model model, double[] x, 
    ///                                            List&lt;RandomVariable&gt; variables, 
    ///                                            double g_at_x)
    /// This would use adjoint or semi-analytical derivatives to avoid (N) extra function evaluations per iteration.
    /// </summary>
    public class FORMSolver
    {
        /// <summary>
        /// Solves the reliability problem using the HL-RF algorithm.
        /// 
        /// Algorithm overview:
        /// 1. Start at u = 0 (mean point in standard normal space)
        /// 2. Iterate:
        ///    a. Transform u → x via isoprobabilistic transform
        ///    b. Compute equivalent normal parameters at x (Rackwitz-Fiessler)
        ///    c. Evaluate limit state g(x)
        ///    d. Compute gradient ∇g(x) via finite differences
        ///    e. Transform gradient to U-space
        ///    f. Apply HL-RF update to find new u
        ///    g. Check convergence
        /// 3. Return result with reliability index, MPP, and sensitivities
        /// </summary>
        /// <param name="variables">List of random variables defining the probabilistic space.</param>
        /// <param name="limitStateFunction">
        /// Delegate g(x) that evaluates the limit state function.
        /// Input: double[] x (values in physical space, length = variables.Count)
        /// Output: double g(x) where g > 0 is safe, g = 0 is limit state, g &lt; 0 is failure
        /// This function will be called (N+1) × iterations times due to finite difference gradient.
        /// </param>
        /// <param name="epsilon1">Convergence tolerance for |g(x)|. Default: 1e-6.</param>
        /// <param name="epsilon2">Convergence tolerance for ||u_new - u_old||. Default: 1e-6.</param>
        /// <param name="maxIterations">Maximum number of HL-RF iterations. Default: 50.</param>
        /// <returns>FORMResult object containing reliability index, MPP, probability of failure, and convergence info.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the gradient of the limit state function becomes zero (singular point on limit state surface).
        /// </exception>
        public FORMResult Solve(
            List<RandomVariable> variables,
            Func<double[], double> limitStateFunction,
            double epsilon1 = 1e-6,
            double epsilon2 = 1e-6,
            int maxIterations = 50)
        {
            if (variables == null || variables.Count == 0)
                throw new ArgumentException("variables list cannot be null or empty.");
            if (limitStateFunction == null)
                throw new ArgumentNullException(nameof(limitStateFunction));
            if (maxIterations < 1)
                throw new ArgumentException("maxIterations must be at least 1.", nameof(maxIterations));

            int n = variables.Count;
            var result = new FORMResult(n);

            // Initialize: u = 0 (mean point in standard normal space)
            double[] u = new double[n];
            double[] x = new double[n];
            double[] u_prev = new double[n];
            double[] grad_g_u = new double[n];  // gradient in U-space

            // Iteration loop
            int iter = 0;
            bool converged = false;

            for (iter = 0; iter < maxIterations; iter++)
            {
                // ===== STEP 1: Transform u → x =====
                for (int i = 0; i < n; i++)
                {
                    x[i] = variables[i].ToX(u[i]);
                }

                // ===== STEP 2: Compute Rackwitz-Fiessler equivalent normal parameters =====
                for (int i = 0; i < n; i++)
                {
                    variables[i].ComputeEquivalentNormal(x[i]);
                }

                // ===== STEP 3: Evaluate g(x) =====
                double g = limitStateFunction(x);

                // ===== STEP 4: Compute gradient ∇g in X-space via forward finite differences =====
                // Reuse g(x) computed above as base value
                double[] grad_g_x = new double[n];

                for (int i = 0; i < n; i++)
                {
                    // Compute step size: h = |x[i]| × 1e-5 + 1e-9
                    double h = Math.Abs(x[i]) * 1e-5 + 1e-9;

                    // Create perturbed point: x + h·e_i
                    double[] x_pert = new double[n];
                    Array.Copy(x, x_pert, n);
                    x_pert[i] += h;

                    // Evaluate g at perturbed point
                    double g_pert = limitStateFunction(x_pert);

                    // Finite difference: ∂g/∂x_i ≈ (g(x+h·e_i) - g(x)) / h
                    grad_g_x[i] = (g_pert - g) / h;
                }

                // ===== STEP 5: Transform gradient to U-space =====
                // ∂g/∂u_i = (∂g/∂x_i) × σ_equiv[i]
                for (int i = 0; i < n; i++)
                {
                    grad_g_u[i] = grad_g_x[i] * variables[i].EquivNormalStdDev;
                }

                // ===== STEP 6: HL-RF update =====
                double dot_gu = 0.0;      // ∇g_u · u
                double norm_sq = 0.0;     // ∇g_u · ∇g_u

                for (int i = 0; i < n; i++)
                {
                    dot_gu += grad_g_u[i] * u[i];
                    norm_sq += grad_g_u[i] * grad_g_u[i];
                }

                // Guard: Check if gradient is zero
                if (norm_sq < 1e-14)
                {
                    throw new InvalidOperationException(
                        "Gradient of limit state function is zero at current point. " +
                        "Check your limit state function definition. " +
                        $"Point: x = [{string.Join(", ", x)}]");
                }

                // HL-RF scalar
                double scalar = (dot_gu - g) / norm_sq;

                // Update u_new = scalar × ∇g_u
                double[] u_new = new double[n];
                for (int i = 0; i < n; i++)
                {
                    u_new[i] = scalar * grad_g_u[i];
                }

                // ===== STEP 7: Compute β = ‖u_new‖ =====
                double beta = 0.0;
                for (int i = 0; i < n; i++)
                {
                    beta += u_new[i] * u_new[i];
                }
                beta = Math.Sqrt(beta);

                result.BetaHistory.Add(beta);

                // ===== STEP 8: Check convergence =====
                // Compute displacement norm: ‖u_new - u‖
                double displacement_norm = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double delta_u = u_new[i] - u[i];
                    displacement_norm += delta_u * delta_u;
                }
                displacement_norm = Math.Sqrt(displacement_norm);

                // Convergence criteria: |g| < epsilon1 AND ‖u_new - u‖ < epsilon2
                if (Math.Abs(g) < epsilon1 && displacement_norm < epsilon2)
                {
                    converged = true;
                    result.Converged = true;
                    result.Beta = beta;
                    result.Iterations = iter + 1;
                    result.UpdateConvergenceMessage(maxIterations);
                    break;
                }

                // ===== STEP 9: Update for next iteration =====
                u = u_new;
            }

            // If not converged after max iterations, still use best result
            if (!converged)
            {
                result.Converged = false;
                result.Beta = result.BetaHistory.Count > 0 ? result.BetaHistory[result.BetaHistory.Count - 1] : 0.0;
                result.Iterations = maxIterations;
                result.UpdateConvergenceMessage(maxIterations);
            }

            // ===== Post-convergence: Compute final results =====

            // Transform u → x for MPP_U and MPP_X
            for (int i = 0; i < n; i++)
            {
                result.MPP_U[i] = u[i];
                result.MPP_X[i] = variables[i].ToX(u[i]);
            }

            // Compute alpha factors = u* / β (if β > 0)
            if (result.Beta > 1e-16)
            {
                result.ComputeAlphaFactors();
            }
            else
            {
                // Zero or near-zero beta: alphas are not well-defined
                for (int i = 0; i < n; i++)
                {
                    result.AlphaFactors[i] = 0.0;
                }
            }

            // Compute probability of failure
            result.ComputeProbabilityOfFailure();

            return result;
        }
    }
}
