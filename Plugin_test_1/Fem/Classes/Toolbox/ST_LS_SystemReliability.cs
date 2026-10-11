// =============================================================================
//  ST_LS_SystemReliability.cs
//
//  WHAT IT IS
//  The reliability of the structure as a SERIES SYSTEM of its member limit states:
//  the structure is taken to have failed as soon as one member limit state fails
//  (first member failure). For a statically determinate truss this is the collapse
//  of the structure; for a redundant one it is on the safe side.
//
//  WHAT IT USES
//  Only the FORM results of the single limit states: beta_i and the direction
//  cosines alpha_i in standard normal space. Each limit state is replaced by its
//  tangent plane at the design point, i.e. by the linear safety margin
//
//      M_i = beta_i - alpha_i . u            (u standard normal, independent)
//
//  so that  P(failure of i) = Phi(-beta_i)  and the correlation between two margins
//  is  rho_ij = alpha_i . alpha_j.  All limit states must share the same vector of
//  random variables for this to hold; MemberReliability.Run takes care of that.
//
//  WHAT IT GIVES
//    1. Simple bounds:     max P_i  <=  Pf,sys  <=  1 - prod(1 - P_i)
//       (fully dependent ... independent limit states)
//    2. Ditlevsen bounds (Ditlevsen 1979), which also use the joint probabilities
//       P_ij = P(M_i <= 0 and M_j <= 0) = Phi2(-beta_i, -beta_j; rho_ij):
//
//         lower = P_1 + sum_i max(0, P_i - sum_{j<i} P_ij)
//         upper = sum_i P_i - sum_{i>=2} max_{j<i} P_ij
//
//       with the limit states ordered from the most to the least likely failure.
//       They are narrow when one limit state dominates, and wide when many limit
//       states have similar beta and are strongly correlated - which is where an
//       optimised structure ends up.
//    3. An estimate of Pf,sys by importance sampling of the linear margins, with its
//       standard error. The sampling density is a mixture of unit normal densities
//       centred on the design points, weighted by P_i. The random numbers come from
//       a fixed seed, so the same input always gives the same result.
//
//  The system beta that is reported is the sampling estimate, kept inside the
//  Ditlevsen bounds:  beta,sys = -Phi^-1(Pf,sys).
//
//  The bivariate normal probability Phi2 is computed from
//      Phi2(h, k; rho) = Phi(h) Phi(k)
//                        + 1/(2 pi) * int_0^asin(rho) exp(-(h^2 - 2 h k sin t + k^2) / (2 cos^2 t)) dt
//  (Drezner and Wesolowsky 1990) by adaptive Gauss-Legendre quadrature.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using MathNet.Numerics.Distributions;

namespace Propability_NTNU_v1.Classes.Reliability
{
    public class SystemReliabilityResult
    {
        public int NComponents;
        public List<string> Labels = new List<string>();
        /// <summary>Reliability index and failure probability of each limit state, in input order.</summary>
        public double[] Beta = new double[0];
        public double[] Pf = new double[0];
        /// <summary>Limit states from the most to the least likely failure (indices into Beta).</summary>
        public int[] Order = new int[0];
        /// <summary>Correlation of each limit state with the most critical one.</summary>
        public double[] RhoWithCritical = new double[0];

        /// <summary>Simple bounds: fully dependent and independent limit states.</summary>
        public double PfMax = double.NaN;
        public double PfIndependent = double.NaN;

        /// <summary>Ditlevsen bounds on the system failure probability.</summary>
        public double PfLower = double.NaN;
        public double PfUpper = double.NaN;
        /// <summary>The corresponding bounds on the system beta (BetaLower belongs to PfUpper).</summary>
        public double BetaLower = double.NaN;
        public double BetaUpper = double.NaN;

        /// <summary>Importance sampling estimate and its standard error. NaN if not run.</summary>
        public double PfSampling = double.NaN;
        public double PfSamplingStdError = double.NaN;
        public int Samples;

        /// <summary>
        /// The reported values: the sampling estimate kept inside the Ditlevsen bounds, or the
        /// upper bound on Pf if no samples were asked for.
        /// </summary>
        public double PfSystem = double.NaN;
        public double BetaSystem = double.NaN;

        public List<string> Warnings = new List<string>();

        public string Report()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.AppendLine("SYSTEM RELIABILITY (series system: failure of the first member limit state)");
            sb.AppendLine("  Each limit state is replaced by its tangent plane at the design point (FORM).");
            sb.AppendLine("  The correlation between two limit states is alpha_i . alpha_j.");
            sb.AppendLine("  limit states in the system : " + NComponents.ToString(ci));
            if (NComponents == 0) return sb.ToString();

            int first = Order[0];
            sb.AppendLine("  most critical limit state  : " + Label(first) + ", beta " + Beta[first].ToString("F3", ci) +
                          ", Pf " + Pf[first].ToString("E2", ci));
            sb.AppendLine("  simple bounds              : " + PfMax.ToString("E2", ci) + " <= Pf,sys <= " +
                          PfIndependent.ToString("E2", ci));
            sb.AppendLine("  Ditlevsen bounds           : " + PfLower.ToString("E2", ci) + " <= Pf,sys <= " +
                          PfUpper.ToString("E2", ci) + "   (beta,sys " + BetaLower.ToString("F3", ci) + " ... " +
                          BetaUpper.ToString("F3", ci) + ")");

            if (!double.IsNaN(PfSampling))
            {
                double cov = (PfSampling > 0.0) ? PfSamplingStdError / PfSampling : double.NaN;
                sb.AppendLine("  importance sampling        : Pf,sys = " + PfSampling.ToString("E2", ci) +
                              ", standard error " + (100.0 * cov).ToString("F1", ci) + " % (" +
                              Samples.ToString(ci) + " samples)");
                sb.AppendLine("  system beta                : " + BetaSystem.ToString("F3", ci) +
                              "   (sampling estimate, kept inside the Ditlevsen bounds)");
            }
            else
            {
                sb.AppendLine("  system beta                : " + BetaSystem.ToString("F3", ci) +
                              "   (from the upper Ditlevsen bound on Pf; no sampling)");
            }

            int shown = Math.Min(6, NComponents);
            sb.AppendLine("  the " + shown.ToString(ci) + " most critical limit states and their correlation with the first:");
            for (int i = 0; i < shown; i++)
            {
                int k = Order[i];
                sb.AppendLine("    " + Label(k).PadRight(30) + " beta " + Beta[k].ToString("F3", ci).PadLeft(7) +
                              "   rho " + RhoWithCritical[k].ToString("F3", ci));
            }

            foreach (string w in Warnings) sb.AppendLine("  note: " + w);
            return sb.ToString();
        }

        private string Label(int k)
        {
            return (k < Labels.Count && !string.IsNullOrEmpty(Labels[k]))
                ? Labels[k]
                : "limit state " + k.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static class SystemReliability
    {
        /// <summary>Number of samples of the importance sampling estimate when none is given.</summary>
        public const int DefaultSamples = 20000;

        /// <summary>Seed of the random numbers. Fixed, so that a result can be reproduced.</summary>
        public const ulong Seed = 20261007UL;

        // =====================================================================
        //  Series system
        // =====================================================================

        /// <summary>
        /// Failure probability of a series system of FORM-linearised limit states.
        /// beta[i] and alpha[i] are the reliability index and the direction cosines (standard
        /// normal space, unit length) of limit state i; all alpha have the same length.
        /// samples = 0 skips the importance sampling estimate.
        /// </summary>
        public static SystemReliabilityResult SeriesSystem(
            IList<double> beta, IList<double[]> alpha, IList<string> labels = null, int samples = DefaultSamples)
        {
            if (beta == null || alpha == null) throw new ArgumentNullException(nameof(beta));
            if (beta.Count != alpha.Count)
                throw new ArgumentException("One alpha vector is needed for every beta.");

            int m = beta.Count;
            var res = new SystemReliabilityResult();
            res.NComponents = m;
            res.Labels = (labels == null) ? new List<string>() : labels.ToList();
            res.Samples = Math.Max(0, samples);
            if (m == 0) return res;

            int n = alpha[0].Length;
            for (int i = 0; i < m; i++)
            {
                if (alpha[i] == null || alpha[i].Length != n)
                    throw new ArgumentException("All alpha vectors must have the same length.");
                if (double.IsNaN(beta[i]))
                    throw new ArgumentException("A limit state without a beta cannot be part of the system.");
            }

            double[] b = beta.ToArray();
            double[] pf = new double[m];
            for (int i = 0; i < m; i++) pf[i] = Normal.CDF(0.0, 1.0, -b[i]);
            res.Beta = b;
            res.Pf = pf;

            // --- order: most likely failure first (ties: lower beta, then input order) ---
            int[] order = Enumerable.Range(0, m)
                .OrderByDescending(i => pf[i]).ThenBy(i => b[i]).ThenBy(i => i).ToArray();
            res.Order = order;

            // --- simple bounds ---
            res.PfMax = pf[order[0]];
            double logSurvival = 0.0;
            for (int i = 0; i < m; i++) logSurvival += Log1p(-pf[i]);
            res.PfIndependent = -Expm1(logSurvival);

            // --- Ditlevsen bounds ---
            double lower = pf[order[0]];
            double upper = pf[order[0]];
            for (int i = 1; i < m; i++)
            {
                int a = order[i];
                double sum = 0.0, max = 0.0;
                for (int j = 0; j < i; j++)
                {
                    int c = order[j];
                    double pij = BivariateNormalCdf(-b[a], -b[c], Dot(alpha[a], alpha[c]));
                    pij = Math.Min(pij, Math.Min(pf[a], pf[c]));       // a joint probability cannot exceed either
                    sum += pij;
                    if (pij > max) max = pij;
                }
                lower += Math.Max(0.0, pf[a] - sum);
                upper += pf[a] - max;
            }
            res.PfLower = lower;
            res.PfUpper = Math.Min(1.0, upper);
            res.BetaLower = BetaOf(res.PfUpper);
            res.BetaUpper = BetaOf(res.PfLower);

            res.RhoWithCritical = new double[m];
            for (int i = 0; i < m; i++)
                res.RhoWithCritical[i] = Clamp(Dot(alpha[i], alpha[order[0]]), -1.0, 1.0);

            // --- importance sampling estimate ---
            if (res.Samples > 0)
            {
                double stdError;
                res.PfSampling = SampleSeriesSystem(b, alpha, pf, res.Samples, Seed, out stdError);
                res.PfSamplingStdError = stdError;
            }

            // --- reported value ---
            if (!double.IsNaN(res.PfSampling))
            {
                res.PfSystem = Clamp(res.PfSampling, res.PfLower, res.PfUpper);

                double outside = Math.Max(res.PfLower - res.PfSampling, res.PfSampling - res.PfUpper);
                if (outside > 4.0 * res.PfSamplingStdError)
                    res.Warnings.Add("the sampling estimate lies outside the Ditlevsen bounds by more than 4 standard errors; " +
                                     "increase the number of samples.");
            }
            else
            {
                res.PfSystem = res.PfUpper;
            }
            res.BetaSystem = BetaOf(res.PfSystem);
            return res;
        }

        /// <summary>-Phi^-1(pf).</summary>
        public static double BetaOf(double pf)
        {
            if (double.IsNaN(pf)) return double.NaN;
            if (pf <= 0.0) return double.PositiveInfinity;
            if (pf >= 1.0) return double.NegativeInfinity;
            return -Normal.InvCDF(0.0, 1.0, pf);
        }

        // =====================================================================
        //  Importance sampling of the linear safety margins
        // =====================================================================

        /// <summary>
        /// P(at least one alpha_i . u >= beta_i) for standard normal u. The sampling density is
        /// h(u) = sum_k w_k phi(u - beta_k alpha_k) with w_k = P_k / sum P, so the samples fall
        /// around the design points in proportion to their importance. The estimate is the mean
        /// of I(u) phi(u) / h(u), where I = 1 if the system fails at u.
        /// </summary>
        private static double SampleSeriesSystem(double[] beta, IList<double[]> alpha, double[] pf,
                                                 int samples, ulong seed, out double stdError)
        {
            stdError = double.NaN;
            int m = beta.Length;
            int n = alpha[0].Length;

            double total = 0.0;
            for (int k = 0; k < m; k++) total += pf[k];
            if (!(total > 0.0)) return double.NaN;

            // cumulative weights, and log(w_k) - beta_k^2 / 2 for the density ratio
            double[] cum = new double[m];
            double[] logW = new double[m];
            double acc = 0.0;
            for (int k = 0; k < m; k++)
            {
                acc += pf[k] / total;
                cum[k] = acc;
                logW[k] = (pf[k] > 0.0) ? Math.Log(pf[k] / total) - 0.5 * beta[k] * beta[k] : double.NegativeInfinity;
            }

            var rng = new SplitMix64(seed);
            double[] u = new double[n];
            double[] ex = new double[m];
            double s1 = 0.0, s2 = 0.0;

            for (int it = 0; it < samples; it++)
            {
                // pick a limit state with probability w_k
                double r = rng.NextDouble();
                int lo = 0, hi = m - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (cum[mid] < r) lo = mid + 1; else hi = mid;
                }
                int kc = lo;

                // u = its design point + a standard normal vector
                for (int i = 0; i < n; i += 2)
                {
                    double z1, z2;
                    rng.NextNormalPair(out z1, out z2);
                    u[i] = beta[kc] * alpha[kc][i] + z1;
                    if (i + 1 < n) u[i + 1] = beta[kc] * alpha[kc][i + 1] + z2;
                }

                // does the system fail at u, and h(u) / phi(u) = sum_k w_k exp(beta_k alpha_k . u - beta_k^2 / 2)
                bool fail = false;
                double mx = double.NegativeInfinity;
                for (int j = 0; j < m; j++)
                {
                    double d = Dot(alpha[j], u);
                    if (d >= beta[j]) fail = true;
                    double e = logW[j] + beta[j] * d;
                    ex[j] = e;
                    if (e > mx) mx = e;
                }
                if (!fail) continue;

                double ratio = 0.0;
                for (int j = 0; j < m; j++) ratio += Math.Exp(ex[j] - mx);
                double w = Math.Exp(-mx) / ratio;                      // phi(u) / h(u)

                s1 += w;
                s2 += w * w;
            }

            double mean = s1 / samples;
            double variance = Math.Max(0.0, s2 / samples - mean * mean) / samples;
            stdError = Math.Sqrt(variance);
            return mean;
        }

        /// <summary>
        /// SplitMix64 random number generator (Steele, Lea and Flood 2014). Written out here so
        /// that the stream does not depend on the .NET version.
        /// </summary>
        private sealed class SplitMix64
        {
            private ulong _state;

            public SplitMix64(ulong seed) { _state = seed; }

            private ulong NextUInt64()
            {
                unchecked
                {
                    _state += 0x9E3779B97F4A7C15UL;
                    ulong z = _state;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    return z ^ (z >> 31);
                }
            }

            /// <summary>Uniform in (0, 1); never exactly 0 or 1.</summary>
            public double NextDouble()
            {
                return ((NextUInt64() >> 11) + 0.5) / 9007199254740992.0;
            }

            /// <summary>Two independent standard normal numbers (Box-Muller).</summary>
            public void NextNormalPair(out double z1, out double z2)
            {
                double r = Math.Sqrt(-2.0 * Math.Log(NextDouble()));
                double t = 2.0 * Math.PI * NextDouble();
                z1 = r * Math.Cos(t);
                z2 = r * Math.Sin(t);
            }
        }

        // =====================================================================
        //  Bivariate normal probability
        // =====================================================================

        // 10-point Gauss-Legendre rule on [-1, 1]: positive nodes and their weights
        private static readonly double[] GlX =
        {
            0.1488743389816312109, 0.4333953941292471908, 0.6794095682990244062,
            0.8650633666889845107, 0.9739065285171717201
        };
        private static readonly double[] GlW =
        {
            0.2955242247147528702, 0.2692667193099963551, 0.2190863625159820440,
            0.1494513491505805931, 0.0666713443086881376
        };
        private const int MaxDepth = 48;
        private const double RelativeTolerance = 1e-10;

        /// <summary>
        /// P(X &lt;= h and Y &lt;= k) for standard normal X and Y with correlation rho.
        /// Accurate in the lower tails, which is where the failure probabilities are.
        /// </summary>
        public static double BivariateNormalCdf(double h, double k, double rho)
        {
            if (double.IsNaN(h) || double.IsNaN(k) || double.IsNaN(rho)) return double.NaN;

            // fully dependent
            if (rho >= 1.0 - 1e-15) return Normal.CDF(0.0, 1.0, Math.Min(h, k));
            if (rho <= -1.0 + 1e-15) return Math.Max(0.0, Normal.CDF(0.0, 1.0, h) - Normal.CDF(0.0, 1.0, -k));

            double product = Normal.CDF(0.0, 1.0, h) * Normal.CDF(0.0, 1.0, k);
            if (rho == 0.0) return product;

            double a = 0.0, b = Math.Asin(rho), sign = 1.0;
            if (b < a)
            {
                a = b; b = 0.0; sign = -1.0;
            }

            // a first value on 8 panels sets the absolute tolerance of the adaptive rule
            double rough = 0.0;
            for (int i = 0; i < 8; i++)
                rough += Gauss(a + (b - a) * i / 8.0, a + (b - a) * (i + 1) / 8.0, h, k);
            double tol = RelativeTolerance * Math.Abs(rough) + 1e-300;

            double integral = Adaptive(a, b, Gauss(a, b, h, k), tol, h, k, 0);
            double p = product + sign * integral / (2.0 * Math.PI);
            return Clamp(p, 0.0, 1.0);
        }

        /// <summary>
        /// exp(-(h^2 - 2 h k sin t + k^2) / (2 cos^2 t)), with the exponent written so that it
        /// stays accurate where cos t goes to zero (rho close to +1 or -1).
        /// </summary>
        private static double Integrand(double t, double h, double k)
        {
            double s = Math.Sin(t);
            double c = Math.Cos(t);
            double c2 = c * c;
            if (c2 <= 0.0) return 0.0;

            double e = (s >= 0.0)
                ? (h - k) * (h - k) / (2.0 * c2) + h * k / (1.0 + s)
                : (h + k) * (h + k) / (2.0 * c2) - h * k / (1.0 - s);
            return Math.Exp(-e);
        }

        private static double Gauss(double a, double b, double h, double k)
        {
            double mid = 0.5 * (a + b), half = 0.5 * (b - a);
            double total = 0.0;
            for (int i = 0; i < GlX.Length; i++)
            {
                double dx = half * GlX[i];
                total += GlW[i] * (Integrand(mid - dx, h, k) + Integrand(mid + dx, h, k));
            }
            return total * half;
        }

        /// <summary>Halves the interval until the two halves agree with the whole.</summary>
        private static double Adaptive(double a, double b, double whole, double tol, double h, double k, int depth)
        {
            double mid = 0.5 * (a + b);
            double left = Gauss(a, mid, h, k);
            double right = Gauss(mid, b, h, k);
            if (depth >= MaxDepth || Math.Abs(left + right - whole) <= tol) return left + right;

            return Adaptive(a, mid, left, 0.5 * tol, h, k, depth + 1) +
                   Adaptive(mid, b, right, 0.5 * tol, h, k, depth + 1);
        }

        // =====================================================================
        //  Small helpers
        // =====================================================================

        private static double Dot(double[] x, double[] y)
        {
            double s = 0.0;
            for (int i = 0; i < x.Length; i++) s += x[i] * y[i];
            return s;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return (v < lo) ? lo : (v > hi) ? hi : v;
        }

        /// <summary>ln(1 + x), accurate for small x.</summary>
        private static double Log1p(double x)
        {
            if (Math.Abs(x) > 1e-4) return Math.Log(1.0 + x);
            return x * (1.0 - x * (0.5 - x / 3.0));
        }

        /// <summary>exp(x) - 1, accurate for small x.</summary>
        private static double Expm1(double x)
        {
            if (Math.Abs(x) > 1e-4) return Math.Exp(x) - 1.0;
            return x * (1.0 + x * (0.5 + x / 6.0));
        }
    }
}
