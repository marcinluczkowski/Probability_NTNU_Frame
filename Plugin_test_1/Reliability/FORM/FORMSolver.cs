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
            double epsilon1 = 1e-3,
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

            // ===== FOSM PRE-SCREENING =====
            // Cheap check before running HL-RF. If the structure is clearly safe
            // or clearly unsafe at the mean, return an estimate rather than iterate.
            double[] meanX = new double[n];
            for (int i = 0; i < n; i++) meanX[i] = variables[i].Mean;

            double g_mean = limitStateFunction(meanX);
            double var_g_FOSM = 0.0;

            for (int i = 0; i < n; i++)
            {
                double h_i = variables[i].StdDev * 1e-3 + 1e-9;
                double[] xp = (double[])meanX.Clone(); xp[i] += h_i;
                double[] xm = (double[])meanX.Clone(); xm[i] -= h_i;
                double dg_dx = (limitStateFunction(xp) - limitStateFunction(xm)) / (2.0 * h_i);
                var_g_FOSM += Math.Pow(dg_dx * variables[i].StdDev, 2);
            }

            double sigma_g_FOSM = Math.Sqrt(var_g_FOSM);
            double beta_FOSM = sigma_g_FOSM > 1e-30 ? g_mean / sigma_g_FOSM : (g_mean > 0 ? 999.0 : -999.0);

            // Early exit if very safe (FORM has no useful precision beyond β ≈ 7)
            if (beta_FOSM > 16.0)
            {
                result.Beta = beta_FOSM;
                result.ProbabilityOfFailure = Normal.CDF(0, 1, -beta_FOSM);
                result.Converged = true;
                result.Iterations = 0;
                result.ConvergenceMessage = $"Over-designed: FOSM β = {beta_FOSM:F2} (Pf < 1e-12). FORM iteration skipped.";
                result.BetaHistory.Add(beta_FOSM);
                for (int i = 0; i < n; i++)
                {
                    result.MPP_U[i] = 0;
                    result.MPP_X[i] = variables[i].Mean;
                    result.AlphaFactors[i] = 0;
                }
                return result;
            }

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
                    // R-F method only instead of full isoprobabilistic transformation
                    x[i] = variables[i].EquivNormalMean + u[i] * variables[i].EquivNormalStdDev;
                }

                // ===== STEP 2: Compute Rackwitz-Fiessler equivalent normal parameters =====
                for (int i = 0; i < n; i++)
                {
                    variables[i].ComputeEquivalentNormal(x[i]);
                }

                // ===== STEP 3: Evaluate g(x) =====
                double g = limitStateFunction(x);

                // ===== STEP 4: Compute gradient ∇g in X-space via central finite differences =====
                double[] grad_g_x = new double[n];

                for (int i = 0; i < n; i++)
                {
                    // h = CoV * mean * 1e-3 + 1e-9 = StdDev * 1e-3 + 1e-9
                    double h = variables[i].StdDev * 1e-3 + 1e-9;

                    // Create perturbed point +h
                    double[] x_pert_plus = new double[n];
                    Array.Copy(x, x_pert_plus, n);
                    x_pert_plus[i] += h;
                    double g_pert_plus = limitStateFunction(x_pert_plus);

                    // Create perturbed point -h
                    double[] x_pert_minus = new double[n];
                    Array.Copy(x, x_pert_minus, n);
                    x_pert_minus[i] -= h;
                    double g_pert_minus = limitStateFunction(x_pert_minus);

                    // Central difference: ∂g/∂x_i ≈ (g(x+h) - g(x-h)) / (2h)
                    grad_g_x[i] = (g_pert_plus - g_pert_minus) / (2.0 * h);
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


                // Guard: gradient below noise floor → fall back to FOSM with warning
                if (norm_sq < 1e-12 * Math.Max(Math.Abs(g) * Math.Abs(g), 1.0))
                {
                    result.Beta = Math.Abs(beta_FOSM);
                    result.ProbabilityOfFailure = Normal.CDF(0, 1, -Math.Abs(beta_FOSM));
                    result.Converged = false;
                    result.Iterations = iter;
                    result.ConvergenceMessage =
                        $"Gradient below noise floor at iteration {iter}. " +
                        $"Reporting FOSM estimate: β ≈ {beta_FOSM:F3}. " +
                        $"Try a smaller section to get a meaningful FORM β";
                    for (int i = 0; i < n; i++)
                    {
                        result.MPP_U[i] = u[i];
                        result.MPP_X[i] = variables[i].ToX(u[i]);
                        result.AlphaFactors[i] = 0;
                    }
                    return result;

                }

                // HL-RF scalar
                double scalar = (dot_gu - g) / norm_sq;

                // Update u_new with relaxation and step bounding to prevent Beta explosions
                double relax = 1.0; // standard relaxation
                double[] u_new = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double target_step = ((dot_gu - g) / norm_sq) * grad_g_u[i];
                    double delta_u = target_step - u[i];

                    // Box bounds on U-space jumps (max 6.0 per iteration) stop $10^6$ infinity jumps 
                    // when navigating extremely noisy or flat limit surfaces early in descent.
                    if (delta_u > 6.0) delta_u = 6.0;
                    if (delta_u < -6.0) delta_u = -6.0;

                    u_new[i] = u[i] + relax * delta_u;
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
                    u = u_new; // Update to final point before breaking
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

            // Signed β: if g(mean) < 0, the origin is in the failure region and Pf = Φ(+β), not Φ(-β)
            double signed_beta = (g_mean >= 0 ? 1.0 : -1.0) * result.Beta;
            result.ProbabilityOfFailure = Normal.CDF(0, 1, -signed_beta);

            // Annotate the convergence message if we're in the failure-dominant regime
            if (g_mean < 0)
            {
                result.ConvergenceMessage += $"  [NOTE: g(mean) = {g_mean:G4} < 0 — mean state is already in failure region; reported Pf reflects this.]";
            }

            return result;
        }
    }
}
