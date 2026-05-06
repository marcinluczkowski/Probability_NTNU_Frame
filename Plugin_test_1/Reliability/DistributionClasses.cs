using MathNet.Numerics.Distributions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Plugin_test_1.Reliability
{
    public class RandomVariable
    {
        public const double Euler_Gamma = 0.5772156649;
        public readonly Random _random = new Random();

        public string Name;
        public double CharValue;
        public double Percentile;
        public double COV;
        public string DistType;
        public double Mean;
        public double StdDev;

        public double MuLognormal;
        public double SigmaLognormal;
        public double LocationGumbel;
        public double ScaleGumbel;
        public double MeanGumbelT;              // Mean at reference period (T-year)
        public double LocationGumbelT;          // Location at reference period (T-year)

        public int ReferenceperiodYears = 50;   // T-years

        public RandomVariable(string name, double charValue, double percentile, double cov, string distType, double mean = double.NaN)
        {
            Name = name;
            CharValue = charValue;
            Percentile = percentile;
            COV = cov;
            DistType = distType.ToLower();
            Mean = mean;

            CalculateParameters();
        }

        public void CalculateParameters()
        {
            if (!double.IsNaN(Mean))
            {
                CalculateParametersFromMean();
            }
            else
            {
                CalculateParametersFromPercentile();
            }
        }

        public void CalculateParametersFromMean()
        {
            double mean = Mean;
            
            if (DistType == "lognormal")
            {
                double sigmaLn = Math.Sqrt(Math.Log(1 + COV * COV));
                double muLn = Math.Log(mean) - 0.5 * sigmaLn * sigmaLn;

                MuLognormal = muLn;
                SigmaLognormal = sigmaLn;
                StdDev = mean * COV;

                if (CharValue == 0)
                    CharValue = mean;

                Percentile = CDF(CharValue);
            }
            else if (DistType == "gumbel")
            {
                StdDev = mean * COV;
                MeanGumbelT = mean + Math.Sqrt(6) / Math.PI * StdDev * Math.Log(ReferenceperiodYears);

                double alpha = Math.PI / (StdDev * Math.Sqrt(6));
                double u = mean - Euler_Gamma / alpha;
                double uT = MeanGumbelT - Euler_Gamma / alpha;

                ScaleGumbel = 1.0 / alpha;
                LocationGumbel = u;
                LocationGumbelT = uT;

                if (CharValue == 0)
                    CharValue = mean;

                Percentile = CDF(CharValue);
            }
            else if (DistType == "normal")
            {
                StdDev = mean * COV;

                if (CharValue == 0)
                    CharValue = mean;

                Percentile = CDF(CharValue);
            }
            else
            {
                throw new InvalidOperationException($"Unknown dist_type: {DistType}");
            }
        }

        private void CalculateParametersFromPercentile()
        {
            double p = Math.Max(1e-12, Math.Min(1 - 1e-12, Percentile));

            if (DistType == "lognormal")
            {
                double z = Normal.InvCDF(0, 1, p);
                double sigmaLn = Math.Sqrt(Math.Log(1 + COV * COV));
                double muLn = Math.Log(CharValue) - sigmaLn * z;

                MuLognormal = muLn;
                SigmaLognormal = sigmaLn;
                Mean = Math.Exp(muLn + 0.5 * sigmaLn * sigmaLn);
                StdDev = Mean * COV;
            }
            else if (DistType == "normal")
            {
                double z = Normal.InvCDF(0, 1, p);
                double denom = 1.0 + COV * z;

                if (denom <= 0) 
                    throw new InvalidOperationException($"Normal RV: invalid (1 + cov*z) = {denom}");

                double mu = CharValue / denom;
                double sigma = COV * mu;

                Mean = mu;
                StdDev = sigma;
            }
            else if (DistType == "gumbel")
            {
                Mean = CharValue / 1.35;
                StdDev = Mean * COV;

                double beta = StdDev * Math.Sqrt(6) / Math.PI;
                double uT = Mean - 0.5772156649 * beta; // Euler gamma

                // 3. Convert to 1-year (if needed)
                double u1 = uT - beta * Math.Log(ReferenceperiodYears);
                double Mean1 = u1 + 0.5772156649 * beta;

                LocationGumbel = u1;
                LocationGumbelT = uT;
                ScaleGumbel = beta;
            }
            else
            {
                throw new InvalidOperationException($"Unknown dist_type: {DistType}");
            }
        }

        private double GumbelPDF(double x)
        {
            double z = (x - LocationGumbelT) / ScaleGumbel;
            return (1.0 / ScaleGumbel) * Math.Exp(-(z + Math.Exp(-z)));
        }

        private double GumbelCDF(double x)
        {
            double z = (x - LocationGumbelT) / ScaleGumbel;
            return Math.Exp(-Math.Exp(-z));
        }

        private double GumbelInverseCDF(double p)
        {
            if (p <= 0 || p >= 1)
                throw new ArgumentOutOfRangeException(nameof(p), "p must be in (0, 1)");

            return LocationGumbelT - ScaleGumbel * Math.Log(-Math.Log(p)); //unsure if this is 100% correct for inverse CDF
        }

        public double PDF(double x)
        {
            switch (DistType)
            {
                case "lognormal":
                    return LogNormal.PDF(MuLognormal, SigmaLognormal, x);
                case "gumbel":
                    return GumbelPDF(x);
                case "normal":
                    return Normal.PDF(Mean, StdDev, x);
                default:
                    throw new InvalidOperationException($"Unknown dist_type: {DistType}");
            }
        }

        public double CDF(double x)
        {
            switch (DistType)
            {
                case "lognormal":
                    return LogNormal.CDF(MuLognormal, SigmaLognormal, x);
                case "gumbel":
                    return GumbelCDF(x);
                case "normal":
                    return Normal.CDF(Mean, StdDev, x);
                default:
                    throw new InvalidOperationException($"Unknown dist_type: {DistType}");
            }
        }

        public double PPF(double u)
        {
            u = Math.Max(1e-12, Math.Min(1 - 1e-12, u));

            switch (DistType)
            {
                case "lognormal":
                    return Math.Exp(MuLognormal + SigmaLognormal * Normal.InvCDF(0, 1, u));
                case "gumbel":
                    return GumbelInverseCDF(u);
                case "normal":
                    return Normal.InvCDF(Mean, StdDev, u);
                default:
                    throw new InvalidOperationException($"Unknown dist_type: {DistType}");
            }
        }

        public double UtoX(double u)
        {
            return PPF(Normal.CDF(0, 1, u));
        }

        public double Sample()
        {
            switch (DistType)
            {
                case "lognormal":
                    return LogNormal.Sample(_random, MuLognormal, SigmaLognormal);
                case "gumbel":
                    return GumbelInverseCDF(_random.NextDouble());
                case "normal":
                    return Normal.Sample(_random, Mean, StdDev);
                default:
                    throw new InvalidOperationException($"Unknown dist_type: {DistType}");
            }
        }

        /// <summary>
        /// Factory method: builds a Eurocode-calibrated set of 5 random variables
        /// matching PhD calibration (FORM_Analysis.py): [fy, G, Q, theta_R, theta_E].
        /// 
        /// Parameters:
        ///   fyk  : Characteristic yield strength [MPa]
        ///   Gk   : Characteristic permanent load [kN/m] or [kN]
        ///   Qk   : Characteristic variable load [kN/m] or [kN]
        /// 
        /// Returns:
        ///   Array of 5 RandomVariables: [fy, G, Q, theta_R, theta_E]
        /// </summary>
        public static RandomVariable[] BuildEurocodeRVs(double fyk, double Gk, double Qk)
        {
            return new[]
            {
                // Material: lognormal, 5% COV
                new RandomVariable("fy", fyk, 0.50, 0.05, "lognormal"),

                // Permanent load: normal, 10% COV
                new RandomVariable("G", Gk, 0.50, 0.10, "normal"),

                // Variable load: gumbel, 26% COV, 98th percentile
                new RandomVariable("Q", Qk, 0.98, 0.26, "gumbel"),

                // Resistance model uncertainty: lognormal, 5% COV (mean-based)
                new RandomVariable("theta_R", 1.15, 0.05, 0.05, "lognormal", 1.15),

                // Execution model uncertainty: lognormal, 10% COV (mean-based)
                new RandomVariable("theta_E", 1.00, 0.50, 0.10, "lognormal", 1.00),
            };
        }
    }
}
