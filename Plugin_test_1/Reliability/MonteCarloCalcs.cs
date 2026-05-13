using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Distributions;
using FORMBeam;

namespace Plugin_test_1.Reliability
{
    /// <summary>
    /// Monte Carlo simulation engine with support for crude MC and importance sampling.
    /// 
    /// Methods are static and reusable in Grasshopper components.
    /// Uses Plugin_test_1.Reliability.RandomVariable class.
    /// </summary>
    public static class MonteCarloCalcs
    {
        // Default constants
        private const int MCS_DEFAULT_N = 1000000;  // 1 million samples
        private const int MCS_SEED = 42;            // Reproducible random seed
        private static readonly Random _rng = new Random(MCS_SEED);

        /// <summary>
        /// Draw N independent samples from random variables using inverse transform.
        /// 
        /// Parameters:
        ///   rvs      : Random variables in FORM order [fy, G, Q, theta_R, theta_E]
        ///   N        : Number of samples
        ///   seed     : Random seed for reproducibility
        /// 
        /// Returns:
        ///   samples : 2D array of shape (rvs.Count, N)
        ///           Row i contains N samples of rvs[i]
        /// </summary>
        public static double[,] SampleRandomVariables(RandomVariable[] rvs, int N, int seed = MCS_SEED)
        {
            Random rng = new Random(seed);
            int n_rv = rvs.Length;
            double[,] samples = new double[n_rv, N];

            // For each random variable, draw N samples via inverse-CDF
            for (int i = 0; i < n_rv; i++)
            {
                for (int j = 0; j < N; j++)
                {
                    double u = rng.NextDouble();  // U(0,1)
                    samples[i, j] = rvs[i].PPF(u);  // Inverse-CDF (PPF)
                }
            }

            return samples;
        }

        /// <summary>
        /// Evaluate limit state function in physical space (vectorised).
        /// 
        /// g(X) = θ_R · W · f_y  −  θ_E · M_max(G, Q, L, a)
        /// 
        /// Uses BeamMechanics.MaxMoment() to compute the actual maximum moment,
        /// then converts from [kNm] to [Nmm] for consistency with section modulus [mm³].
        /// 
        /// Parameters:
        ///   samples : 2D array, shape (5, N) — rows: [fy, G, Q, theta_R, theta_E]
        ///   W_mm3   : Section modulus [mm³]
        ///   L_m     : Span length [m]
        ///   a       : Load position [m] — for moment coefficient calculation
        /// 
        /// Returns:
        ///   g : 1D array, shape (N,) — limit state values for all samples
        /// </summary>
        public static double[] EvaluateLimitState(double[,] samples, double W_mm3, double L_m, double a)
        {
            int N = samples.GetLength(1);
            double[] g = new double[N];

            // Vectorised evaluation
            for (int j = 0; j < N; j++)
            {
                double fy = samples[0, j];
                double G = samples[1, j];
                double Q = samples[2, j];
                double thR = samples[3, j];
                double thE = samples[4, j];

                // Use BeamMechanics to compute max moment [kNm], then convert to [Nmm]
                double M_kNm = BeamMechanics.MaxMoment(L_m, G, Q, a);
                double M_Nmm = M_kNm * 1e6;

                g[j] = thR * fy * W_mm3 - thE * M_Nmm;
            }

            return g;
        }

        /// <summary>
        /// Run crude Monte Carlo simulation.
        /// 
        /// Returns results dictionary with keys:
        ///   Pf       – Probability of failure
        ///   Beta     – Reliability index −Φ⁻¹(Pf)
        ///   Pf_Std   – Standard deviation of Pf estimator
        ///   Pf_CoV   – Coefficient of variation
        ///   N        – Number of samples
        ///   N_Fail   – Count of failure samples (g ≤ 0)
        /// </summary>
        public static Dictionary<string, object> RunCrudeMonteCarlo(
            double W_mm3, double L_m, double a,
            RandomVariable[] rvs,
            int N = -1, int seed = MCS_SEED)
        {
            if (N <= 0) N = MCS_DEFAULT_N;

            // Sample random variables
            double[,] samples = SampleRandomVariables(rvs, N, seed);
            double[] g = EvaluateLimitState(samples, W_mm3, L_m, a);

            // Count failures (g ≤ 0)
            int n_fail = 0;
            for (int i = 0; i < N; i++)
            {
                if (g[i] <= 0.0) n_fail++;
            }

            double Pf = (double)n_fail / N;

            // Variance of Pf estimator (Bernoulli)
            double Pf_var = Pf * (1.0 - Pf) / N;
            double Pf_std = Math.Sqrt(Pf_var);
            double Pf_cov = Pf > 0 && Pf < 1.0 ? Pf_std / Pf : 0.0;

            // Reliability index using MathNet.Numerics
            // β = −Φ⁻¹(Pf) where Φ⁻¹ is the inverse normal CDF
            double beta;
            if (Pf > 0.0 && Pf < 1.0)
            {
                // Normal probability range
                beta = -Normal.InvCDF(0, 1, Pf);
            }
            else if (Pf >= 1.0)
            {
                // All samples failed - very unsafe
                beta = double.NegativeInfinity;
            }
            else if (Pf == 0.0)
            {
                // No failures in sample - but this doesn't mean beta is infinite
                // Use lower bound: at least -ln(1/N) / sqrt(2)
                beta = -Math.Log(1.0 / N) / Math.Sqrt(2.0);
            }
            else
            {
                beta = double.PositiveInfinity;
            }

            return new Dictionary<string, object>
            {
                { "Pf", Pf },
                { "Beta", beta },
                { "Pf_Std", Pf_std },
                { "Pf_CoV", Pf_cov },
                { "N", N },
                { "N_Fail", (double)n_fail }
            };
        }

        /// <summary>
        /// Run importance sampling Monte Carlo simulation.
        /// 
        /// Shifts distribution centers toward failure region (design point approximation).
        /// Applies likelihood ratio correction to unbiased the estimator.
        /// 
        /// Parameters:
        ///   W_mm3        : Section modulus [mm³]
        ///   L_m          : Span [m]
        ///   a            : Load position [m]
        ///   rvs          : Original distributions
        ///   designPoint  : Approximate design point (from FORM) [fy, G, Q, theta_R, theta_E]
        ///   stdShift     : How many standard deviations to shift (typically 1.0-2.0)
        ///   N            : Number of samples
        ///   seed         : Random seed
        /// 
        /// Returns: Same dictionary as crude MC but with reduced variance
        /// </summary>
        public static Dictionary<string, object> RunImportanceSampling(
            double W_mm3, double L_m, double a,
            RandomVariable[] rvs,
            double[] designPoint,
            double stdShift = 1.0,
            int N = -1, int seed = MCS_SEED)
        {
            if (N <= 0) N = MCS_DEFAULT_N;
            if (designPoint == null || designPoint.Length != 5)
                throw new ArgumentException("designPoint must have 5 elements");

            int n_rv = rvs.Length;

            // Sample from importance distribution (shifted normals)
            double[,] samples = new double[n_rv, N];
            double[] likelihood_ratios = new double[N];

            for (int j = 0; j < N; j++)
            {
                double lr = 1.0;  // Likelihood ratio accumulator

                for (int i = 0; i < n_rv; i++)
                {
                    // Sample from standard normal using MathNet.Numerics
                    // Use inverse CDF transform: Φ⁻¹(U) where U ~ U(0,1)
                    double u = _rng.NextDouble();
                    double u_std = Normal.InvCDF(0, 1, u);

                    // Shift toward design point
                    double x_importance = designPoint[i] + stdShift * rvs[i].StdDev * u_std;

                    samples[i, j] = x_importance;

                    // Likelihood ratio: pdf_original(x) / pdf_importance(x)
                    double pdf_orig = rvs[i].PDF(x_importance);
                    double pdf_importance = Normal.PDF(0, 1, u_std);

                    if (pdf_importance > 1e-10)
                        lr *= pdf_orig / pdf_importance;
                    else
                        lr = 0.0;
                }

                likelihood_ratios[j] = lr;
            }

            // Evaluate limit state
            double[] g = EvaluateLimitState(samples, W_mm3, L_m, a);

            // Count weighted failures
            double weighted_fail = 0.0;
            double weight_sum = 0.0;

            for (int j = 0; j < N; j++)
            {
                if (g[j] <= 0.0)
                    weighted_fail += likelihood_ratios[j];
                weight_sum += likelihood_ratios[j];
            }

            // Unbiased Pf estimate
            double Pf = weight_sum > 0 ? weighted_fail / weight_sum : 0.0;

            // Variance estimation (more complex for importance sampling)
            double var_sum = 0.0;
            double mean_indicator = weighted_fail / weight_sum;
            for (int j = 0; j < N; j++)
            {
                double indicator = g[j] <= 0.0 ? 1.0 : 0.0;
                double weighted_indicator = indicator * likelihood_ratios[j];
                var_sum += (weighted_indicator - mean_indicator) * (weighted_indicator - mean_indicator);
            }
            double Pf_var = var_sum / (N * weight_sum * weight_sum);
            double Pf_std = Math.Sqrt(Pf_var);
            double Pf_cov = Pf > 0 ? Pf_std / Pf : double.PositiveInfinity;

            // Reliability index using MathNet.Numerics
            // β = −Φ⁻¹(Pf) where Φ⁻¹ is the inverse normal CDF
            double beta;
            if (Pf > 0.0 && Pf < 1.0)
            {
                beta = -Normal.InvCDF(0, 1, Pf);
            }
            else if (Pf >= 1.0)
            {
                beta = double.NegativeInfinity;
            }
            else
            {
                beta = double.PositiveInfinity;
            }

            return new Dictionary<string, object>
            {
                { "Pf", Pf },
                { "Beta", beta },
                { "Pf_Std", Pf_std },
                { "Pf_CoV", Pf_cov },
                { "N", N },
                { "N_Fail", weighted_fail }  // Weighted count
            };
        }

        // ────────────────────────────────────────────────────────────────────
        // HELPER FUNCTION REFERENCE (Using MathNet.Numerics instead)
        // ────────────────────────────────────────────────────────────────────

        // NOTE: The following functions are now provided by MathNet.Numerics:
        //
        // 1. GAUSSIAN SAMPLING (Box-Muller Transform)
        //    Instead of:  double u_std = GaussianRandom(rng);
        //    Use:         double u_std = Normal.Sample();
        //    
        //    Box-Muller implementation (for reference):
        //      double u1 = rng.NextDouble();
        //      double u2 = rng.NextDouble();
        //      return sqrt(-2*ln(u1)) * cos(2π*u2);
        //
        // 2. STANDARD NORMAL PDF
        //    Instead of:  double pdf = GaussianPDF(z);
        //    Use:         double pdf = Normal.PDF(0, 1, z);
        //    
        //    Formula (for reference):
        //      φ(z) = (1/sqrt(2π)) * exp(-z²/2)
        //
        // 3. INVERSE NORMAL CDF (Acklam's Algorithm)
        //    Instead of:  double quantile = InverseCDF_Standard(p);
        //    Use:         double quantile = Normal.InvCDF(0, 1, p);
        //    
        //    Acklam's algorithm uses polynomial approximation with 6 coefficients
        //    for high accuracy across the entire domain [0, 1].
    }
}
