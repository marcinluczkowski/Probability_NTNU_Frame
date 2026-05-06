using System;
using MathNet.Numerics.Distributions;
using Plugin_test_1.Reliability;

namespace FORMBeam
{
    /// <summary>
    /// A random variable parameterised by either:
    ///   (charValue, percentile, cov, distType)  — solved for mean/std internally
    ///   (mean, cov, distType)                   — direct parameterisation
    ///
    /// Supports: lognormal | normal | gumbel
    /// Statistical parameters follow Eurocode calibration (PhD_main/FORM_Analysis.py).
    /// 
    /// NOTE: This is a legacy wrapper. Use Plugin_test_1.Reliability.RandomVariable instead.
    /// </summary>
    internal class RandomVariable
    {
        public string Name      { get; }
        public string DistType  { get; }
        public double Mean      { get; private set; }
        public double Std       { get; private set; }
        public double CharValue { get; private set; }

        // lognormal
        private double _muLn, _sigmaLn;
        // gumbel
        private double _location, _locationT, _scale, _meanT;
        private const int RefPeriod    = 50;
        private const double EulerGamma = 0.5772156649015329;

        // ── Constructor: from characteristic value + percentile ───────────────
        public RandomVariable(string name, double charValue, double percentile,
                              double cov, string distType)
        {
            Name     = name;
            DistType = distType.ToLower();
            double p = Math.Max(1e-12, Math.Min(1.0 - 1e-12, percentile));

            switch (DistType)
            {
                case "lognormal":
                {
                    double z = Normal.InvCDF(0, 1, p);
                    _sigmaLn = Math.Sqrt(Math.Log(1.0 + cov * cov));
                    _muLn    = Math.Log(charValue) - _sigmaLn * z;
                    Mean     = Math.Exp(_muLn + 0.5 * _sigmaLn * _sigmaLn);
                    Std      = Mean * cov;
                    break;
                }
                case "normal":
                {
                    double z     = Normal.InvCDF(0, 1, p);
                    double denom = 1.0 + cov * z;
                    if (denom <= 0)
                        throw new ArgumentException(name + ": invalid (1+cov*z) = " + denom);
                    Mean = charValue / denom;
                    Std  = cov * Mean;
                    break;
                }
                case "gumbel":
                {
                    // Eurocode: charValue = 50-year characteristic value
                    _meanT    = charValue / 1.35;
                    Std       = _meanT * cov;
                    Mean      = _meanT + Math.Sqrt(6.0) / Math.PI * Std * Math.Log(1.0 / RefPeriod);
                    double alpha = Math.PI / (Std * Math.Sqrt(6.0));
                    _scale     = 1.0 / alpha;
                    _location  = Mean   - EulerGamma / alpha;
                    _locationT = _location + _scale * Math.Log(RefPeriod);
                    break;
                }
                default:
                    throw new ArgumentException("Unknown dist type: " + distType);
            }
            CharValue = charValue;
        }

        // ── Constructor: from mean directly ──────────────────────────────────
        public RandomVariable(string name, double mean, double cov, string distType)
        {
            Name     = name;
            DistType = distType.ToLower();
            Mean     = mean;

            switch (DistType)
            {
                case "lognormal":
                    _sigmaLn = Math.Sqrt(Math.Log(1.0 + cov * cov));
                    _muLn    = Math.Log(mean) - 0.5 * _sigmaLn * _sigmaLn;
                    Std      = mean * cov;
                    break;
                case "normal":
                    Std = mean * cov;
                    break;
                case "gumbel":
                    Std       = mean * cov;
                    _meanT    = mean + Math.Sqrt(6.0) / Math.PI * Std * Math.Log(RefPeriod);
                    double alpha = Math.PI / (Std * Math.Sqrt(6.0));
                    _scale     = 1.0 / alpha;
                    _location  = mean  - EulerGamma / alpha;
                    _locationT = _meanT - EulerGamma / alpha;
                    break;
                default:
                    throw new ArgumentException("Unknown dist type: " + distType);
            }
            CharValue = mean;
        }

        // ── Distribution methods ──────────────────────────────────────────────

        public double PDF(double x)
        {
            switch (DistType)
            {
                case "lognormal": return LogNormal.PDF(_muLn, _sigmaLn, x);
                case "normal":    return Normal.PDF(Mean, Std, x);
                case "gumbel":    return GumbelPDF(x);
                default: return 0.0;
            }
        }

        public double CDF(double x)
        {
            switch (DistType)
            {
                case "lognormal": return LogNormal.CDF(_muLn, _sigmaLn, x);
                case "normal":    return Normal.CDF(Mean, Std, x);
                case "gumbel":    return GumbelCDF(x);
                default: return 0.0;
            }
        }

        public double PPF(double p)
        {
            p = Math.Max(1e-12, Math.Min(1.0 - 1e-12, p));
            switch (DistType)
            {
                case "lognormal": return Math.Exp(_muLn + _sigmaLn * Normal.InvCDF(0, 1, p));
                case "normal":    return Mean + Std * Normal.InvCDF(0, 1, p);
                case "gumbel":    return GumbelInverseCDF(p);
                default: return 0.0;
            }
        }

        /// <summary>Inverse Rosenblatt: standard-normal u → physical x.</summary>
        public double UtoX(double u) => PPF(Normal.CDF(0, 1, u));

        // ── Gumbel distribution helpers ───────────────────────────────────────
        private double GumbelPDF(double x)
        {
            double z = (x - _locationT) / _scale;
            return (1.0 / _scale) * Math.Exp(-(z + Math.Exp(-z)));
        }

        private double GumbelCDF(double x)
        {
            double z = (x - _locationT) / _scale;
            return Math.Exp(-Math.Exp(-z));
        }

        private double GumbelInverseCDF(double p)
        {
            p = Math.Max(1e-12, Math.Min(1.0 - 1e-12, p));
            return _locationT - _scale * Math.Log(-Math.Log(p));
        }

        // ── Factory: Eurocode-calibrated RV set ───────────────────────────────
        /// <summary>
        /// Builds [fy, G, Q, theta_R, theta_E] with Eurocode-calibrated
        /// statistical parameters, matching PhD_main/FORM_Analysis.py.
        /// </summary>
        public static RandomVariable[] BuildEurocodeRVs(double fyk, double Gk, double Qk)
        {
            return new[]
            {
                new RandomVariable("fy",      fyk,  0.01, 0.05, "lognormal"),
                new RandomVariable("G",       Gk,   0.50, 0.10, "normal"),
                new RandomVariable("Q",       Qk,   0.98, 0.26, "gumbel"),
                new RandomVariable("theta_R", 1.15, 0.05, "lognormal"),  // mean-based
                new RandomVariable("theta_E", 1.00, 0.10, "lognormal"),  // mean-based
            };
        }
    }
}
