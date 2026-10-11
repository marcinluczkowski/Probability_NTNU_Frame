using System;
using System.Collections.Generic;
using MathNet.Numerics.Distributions;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Plugin_test_1.Reliability
{
    internal class StochasticModels
    {
        // From table 3.7 p. 39 from the Reliability Background of the Eurocodes.

        public class ModelUncertainty
        {
            string mode;
            // mode -> distr. type -> mean -> variation .> 
        }
    }

    internal class DesignValueCalculator
    {
        public readonly Dictionary<string, Strategy> _strategies;
        public DesignValueCalculator()
        {
            _strategies = new Dictionary<string, Strategy>(StringComparer.OrdinalIgnoreCase)
            {
                { "normal", new NormalDesign() },
                { "lognormal", new LogNormalDesign() },
                { "gumbel", new GumbelDesign() }
            };
        }

        public DesignValueResult CalculateDesignValue(double alpha, double mean, double cov, double targetbeta, string disttype)
        {
            if (mean <= 0)
                throw new ArgumentException("Mean must be positive", nameof(mean));
            if (cov < 0 || cov > 1)
                throw new ArgumentException("COV must be between 0 and 1", nameof(cov));
            if (targetbeta <= 0)
                throw new ArgumentException("Target beta must be positive", nameof(targetbeta));

            if (!_strategies.TryGetValue(disttype, out var strategy))
                throw new ArgumentException($"Unknown distribution type: {disttype}", nameof(disttype));

            double stddev = mean * cov;
            double yd = strategy.CalculateDesignValues(mean, stddev, cov, targetbeta, alpha);

            return new DesignValueResult
            {
                designvalue_yd = yd,
                alpha = alpha,
                disttype = disttype,
                target_beta = targetbeta,
                safetymargin = Math.Abs(yd - mean)
            };
        }
    }
    public interface Strategy
    {
        double CalculateDesignValues(double mean, double stddev, double cov, double targetbeta, double alpha);
    }

    public class NormalDesign : Strategy
    {
        public double CalculateDesignValues(double mean, double stddev, double cov, double targetbeta, double alpha)
        {
            // Implement the normal distribution design value calculation here
            return mean * (1 + alpha * targetbeta * cov);
        }
    }

    public class LogNormalDesign : Strategy
    {
        public double CalculateDesignValues(double mean, double stddev, double cov, double targetbeta, double alpha)
        {
            // Implement the lognormal distribution design value calculation here
            return mean * Math.Exp(-1 / 2 * Math.Log(1 + cov * cov) + alpha * targetbeta * Math.Sqrt(Math.Log(1 + cov * cov)));
        }
    }

    public class GumbelDesign : Strategy
    {
        public double CalculateDesignValues(double mean, double stddev, double cov, double targetbeta, double alpha)
        {
            // Implement the Gumbel distribution design value calculation here
            double gumbfact = Math.Sqrt(6) / Math.PI;
            return mean * (1 - cov * gumbfact * (0.5772 + Math.Log(-Math.Log(Normal.CDF(0, 1, alpha * targetbeta))))); ;
        }
    }

    public class DesignValueResult
    {
        public double designvalue_yd { get; set; }
        public double alpha { get; set; }
        public string disttype { get; set; }
        public double target_beta { get; set; }
        public double safetymargin { get; set; }
    }
}
