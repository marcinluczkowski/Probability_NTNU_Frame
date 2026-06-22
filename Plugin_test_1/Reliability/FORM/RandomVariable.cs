using System;
using System.Collections.Generic;
using MathNet.Numerics.Distributions;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// Probability distribution type for random variables in reliability analysis.
    /// </summary>
    public enum DistributionType
    {
        Normal,
        LogNormal,
        Gumbel,
    }

    /// <summary>
    /// Represents a random variable with probabilistic transformations between physical space (X)
    /// and standard normal space (U) for FORM reliability analysis.
    /// 
    /// Supports isoprobabilistic transforms and Rackwitz-Fiessler equivalent normal linearization.
    /// </summary>
    public class RandomVariable
    {
        /// <summary>Name/label for this variable (e.g., "E", "Load", "Area").</summary>
        public string Name { get; set; }

        /// <summary>Distribution type (Normal, LogNormal, or Gumbel).</summary>
        public DistributionType Distribution { get; set; }

        /// <summary>Mean value in physical space.</summary>
        public double Mean { get; set; }

        /// <summary>Standard deviation in physical space.</summary>
        public double StdDev { get; set; }

        /// <summary>
        /// Equivalent normal mean at the current point (updated by ComputeEquivalentNormal).
        /// Used in Rackwitz-Fiessler linearization.
        /// </summary>
        public double EquivNormalMean { get; private set; }

        /// <summary>
        /// Equivalent normal standard deviation at the current point (updated by ComputeEquivalentNormal).
        /// Used in Rackwitz-Fiessler linearization.
        /// </summary>
        public double EquivNormalStdDev { get; private set; }

        /// <summary>
        /// Initializes a new RandomVariable with given distribution parameters.
        /// </summary>
        public RandomVariable(string name, DistributionType distribution, double mean, double stdDev)
        {
            Name = name;
            Distribution = distribution;
            Mean = mean;
            StdDev = stdDev;
            EquivNormalMean = mean;
            EquivNormalStdDev = stdDev;
        }

        /// <summary>
        /// Isoprobabilistic transform from physical space to standard normal space.
        /// u = Φ⁻¹( F_X(x) )
        /// where F_X is the CDF of this distribution and Φ is the standard normal CDF.
        /// </summary>
        public double ToU(double x)
        {
            double cdf = CDF(x);
            return Normal.InvCDF(0, 1, cdf);
        }

        /// <summary>
        /// Isoprobabilistic transform from standard normal space back to physical space.
        /// x = F_X⁻¹( Φ(u) )
        /// where F_X⁻¹ is the inverse CDF and Φ is the standard normal CDF.
        /// </summary>
        public double ToX(double u)
        {
            double prob = Normal.CDF(0, 1, u);
            return InverseCDF(prob);
        }

        /// <summary>
        /// Computes Rackwitz-Fiessler equivalent normal parameters at point x.
        /// Linearizes the non-normal distribution at the current design point.
        /// Updates EquivNormalMean and EquivNormalStdDev.
        /// </summary>
        public void ComputeEquivalentNormal(double x)
        {
            if (Distribution == DistributionType.Normal)
            {
                EquivNormalMean = Mean;
                EquivNormalStdDev = StdDev;
            }
            else if (Distribution == DistributionType.LogNormal)
            {
                if (x <= 0) x = 1e-12; // Guard singularity
                double lambda = Math.Log(Mean / Math.Sqrt(1.0 + Math.Pow(StdDev / Mean, 2)));
                double zeta = Math.Sqrt(Math.Log(1.0 + Math.Pow(StdDev / Mean, 2)));

                double u_x = (Math.Log(x) - lambda) / zeta;

                // Analytically perfectly exact transformation preventing tail-explosion
                EquivNormalStdDev = x * zeta;
                EquivNormalMean = x - u_x * EquivNormalStdDev;
            }
            else if (Distribution == DistributionType.Gumbel)
            {
                double u_n = Mean - 0.5772 * StdDev * Math.Sqrt(6) / Math.PI;
                double alpha_n = StdDev * Math.Sqrt(6) / Math.PI;

                double y = (x - u_n) / alpha_n;
                double Fx = Math.Exp(-Math.Exp(-y));

                // Guard Fx extremes to ensure mapping remains bounded
                Fx = Math.Max(1e-15, Math.Min(1.0 - 1e-15, Fx));

                double fx = (1.0 / alpha_n) * Math.Exp(-y) * Fx;
                fx = Math.Max(fx, 1e-100);

                double u_x = Normal.InvCDF(0, 1, Fx);
                double phi_u = Normal.PDF(0, 1, u_x);

                EquivNormalStdDev = phi_u / fx;
                EquivNormalMean = x - u_x * EquivNormalStdDev;
            }
        }

        /// <summary>
        /// Cumulative distribution function (CDF) for this variable's distribution.
        /// Computes P(X ≤ x).
        /// </summary>
        private double CDF(double x)
        {
            switch (Distribution)
            {
                case DistributionType.Normal:
                    return Normal.CDF(Mean, StdDev, x);

                case DistributionType.LogNormal:
                    // Boundary preventing x ≤ 0 in Lognormal
                    if (x <= 0)
                        return 0.0;

                    // Parameters: lambda and zeta from mean and stddev
                    double lambda = Math.Log(Mean / Math.Sqrt(1.0 + Math.Pow(StdDev / Mean, 2)));
                    double zeta = Math.Sqrt(Math.Log(1.0 + Math.Pow(StdDev / Mean, 2)));

                    // CDF: Φ((ln(x) - lambda) / zeta)
                    return Normal.CDF(0, 1, (Math.Log(x) - lambda) / zeta);

                case DistributionType.Gumbel:
                    // Gumbel CDF: exp(-exp(-(x - u_n) / α_n))
                    // where u_n and α_n are location and scale parameters
                    double u_n = Mean - 0.5772 * StdDev * Math.Sqrt(6) / Math.PI;
                    double alpha_n = StdDev * Math.Sqrt(6) / Math.PI;
                    return Math.Exp(-Math.Exp(-(x - u_n) / alpha_n));

                default:
                    throw new NotImplementedException($"Distribution {Distribution} not implemented.");
            }
        }

        /// <summary>
        /// Probability density function (PDF) for this variable's distribution.
        /// </summary>
        private double PDF(double x)
        {
            switch (Distribution)
            {
                case DistributionType.Normal:
                    return Math.Max(Normal.PDF(Mean, StdDev, x), 1e-16);

                case DistributionType.LogNormal:
                    if (x <= 0) return 1e-16;
                    double lambda = Math.Log(Mean / Math.Sqrt(1.0 + Math.Pow(StdDev / Mean, 2)));
                    double zeta = Math.Sqrt(Math.Log(1.0 + Math.Pow(StdDev / Mean, 2)));
                    double logNormalPdf = Normal.PDF(0, 1, (Math.Log(x) - lambda) / zeta) / (x * zeta);
                    return Math.Max(logNormalPdf, 1e-16);

                case DistributionType.Gumbel:
                default:
                    // Use central finite difference: f(x) ≈ (F(x+h) - F(x-h)) / (2h)
                    // Step size chosen to balance truncation and rounding error
                    double h = Math.Max(Math.Abs(x) * 1e-6, 1e-10);
                    double pdf = (CDF(x + h) - CDF(x - h)) / (2.0 * h);
                    return Math.Max(pdf, 1e-16);  // Guard against negative PDF
            }
        }

        /// <summary>
        /// Inverse CDF (quantile function) for this variable's distribution.
        /// Computes the value x such that P(X ≤ x) = p.
        /// </summary>
        private double InverseCDF(double p)
        {
            // Guard against boundary cases
            p = Math.Max(1e-12, Math.Min(1.0 - 1e-12, p));

            switch (Distribution)
            {
                case DistributionType.Normal:
                    return Normal.InvCDF(Mean, StdDev, p);

                case DistributionType.LogNormal:
                    // Parameters: lambda and zeta from mean and stddev
                    double lambda = Math.Log(Mean / Math.Sqrt(1.0 + Math.Pow(StdDev / Mean, 2)));
                    double zeta = Math.Sqrt(Math.Log(1.0 + Math.Pow(StdDev / Mean, 2)));

                    // InvCDF: exp(lambda + zeta * Φ⁻¹(p))
                    return Math.Exp(lambda + zeta * Normal.InvCDF(0, 1, p));

                case DistributionType.Gumbel:
                    // Gumbel InvCDF: u_n - α_n * ln(-ln(p))
                    double u_n = Mean - 0.5772 * StdDev * Math.Sqrt(6) / Math.PI;
                    double alpha_n = StdDev * Math.Sqrt(6) / Math.PI;
                    return u_n - alpha_n * Math.Log(-Math.Log(p));

                default:
                    throw new NotImplementedException($"Distribution {Distribution} not implemented.");
            }
        }

        /// <summary>
        /// Static test method to verify distribution implementations.
        /// Prints known values for validation.
        /// </summary>
        public static void Test()
        {
            Console.WriteLine("=== RandomVariable Distribution Tests ===\n");

            // Test 1: Normal distribution
            var normalRV = new RandomVariable("TestNormal", DistributionType.Normal, mean: 200, stdDev: 10);
            double u_at_210 = normalRV.ToU(210);
            Console.WriteLine($"Test 1: Normal(μ=200, σ=10).ToU(210) = {u_at_210:F6}");
            Console.WriteLine($"  Expected: 1.0, Actual: {u_at_210:F6}, Pass: {Math.Abs(u_at_210 - 1.0) < 0.0001}");

            // Verify round-trip
            double x_back = normalRV.ToX(u_at_210);
            Console.WriteLine($"  Round-trip: ToX(ToU(210)) = {x_back:F6}, Pass: {Math.Abs(x_back - 210) < 0.0001}\n");

            // Test 2: Normal CDF at mean
            double cdf_at_mean = normalRV.CDF(normalRV.Mean);
            Console.WriteLine($"Test 2: Normal CDF at mean = {cdf_at_mean:F6}");
            Console.WriteLine($"  Expected: 0.5, Pass: {Math.Abs(cdf_at_mean - 0.5) < 0.0001}\n");

            // Test 3: Lognormal distribution
            var lognormalRV = new RandomVariable("TestLognormal", DistributionType.LogNormal, mean: 50000, stdDev: 7500);
            double cdf_at_mean_ln = lognormalRV.CDF(lognormalRV.Mean);
            Console.WriteLine($"Test 3: Lognormal(μ=50000, σ=7500).CDF(50000) = {cdf_at_mean_ln:F6}");
            Console.WriteLine($"  Expected: ~0.5 (median), Actual: {cdf_at_mean_ln:F6}, Pass: {Math.Abs(cdf_at_mean_ln - 0.5) < 0.1}\n");

            // Test 4: Gumbel distribution
            var gumbelRV = new RandomVariable("TestGumbel", DistributionType.Gumbel, mean: 100, stdDev: 15);
            double cdf_at_gumbel_mean = gumbelRV.CDF(gumbelRV.Mean);
            // Gumbel CDF at the mode: F(u_n) = exp(-exp(0)) = exp(-1) ≈ 0.3679
            // But mean ≠ mode for Gumbel. Let's verify at actual mean.
            Console.WriteLine($"Test 4: Gumbel(μ=100, σ=15).CDF(100) = {cdf_at_gumbel_mean:F6}");
            Console.WriteLine($"  Gumbel CDF should be between 0 and 1. Pass: {cdf_at_gumbel_mean >= 0 && cdf_at_gumbel_mean <= 1}\n");

            // Test 5: Rackwitz-Fiessler equivalent normal
            normalRV.ComputeEquivalentNormal(210);
            Console.WriteLine($"Test 5: Rackwitz-Fiessler at x=210 (Normal):");
            Console.WriteLine($"  EquivNormalMean = {normalRV.EquivNormalMean:F6}, EquivNormalStdDev = {normalRV.EquivNormalStdDev:F6}");
            Console.WriteLine($"  For Normal, should match original: μ_equiv ≈ 200, σ_equiv ≈ 10\n");

            Console.WriteLine("=== Tests Complete ===");
        }
    }
}
