using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Distributions;

namespace Plugin_test_1.Reliability.FORM
{
    // =======================================================================
    //  Nataf transformation
    // =======================================================================

    /// <summary>
    /// Nataf (isoprobabilistic) transformation for correlated non-normal variables.
    ///
    ///     u (iid standard normal)  ->  z = L u (correlated standard normal)  ->  x
    ///     x_i = F_i^-1( Phi(z_i) )
    ///
    /// The physical correlation rho_ij must first be corrected to the equivalent
    /// standard-normal correlation rho0_ij. Closed forms are used where they exist:
    ///
    ///     Normal    - Normal     : rho0 = rho
    ///     LogN      - LogN       : rho0 = ln(1 + rho*d_i*d_j) / sqrt(ln(1+d_i^2) ln(1+d_j^2))
    ///     Normal    - LogN       : rho0 = rho * d_j / sqrt(ln(1+d_j^2))
    ///
    /// Any other pairing falls back to rho0 = rho and is reported in Warnings.
    /// With an identity correlation matrix this class reduces exactly to the
    /// independent case, so it is safe to use unconditionally.
    /// </summary>
    public class NatafTransform
    {
        public int N { get; private set; }
        /// <summary>Lower-triangular Cholesky factor of the corrected correlation matrix.</summary>
        public double[,] L { get; private set; }
        /// <summary>Inverse of L (needed for parameter sensitivities).</summary>
        public double[,] Linv { get; private set; }
        public double[,] R0 { get; private set; }
        public List<string> Warnings { get; } = new List<string>();

        public NatafTransform(List<RandomVariable> vars, double[,] rhoPhysical = null)
        {
            N = vars.Count;
            R0 = new double[N, N];
            for (int i = 0; i < N; i++) R0[i, i] = 1.0;

            if (rhoPhysical != null)
            {
                for (int i = 0; i < N; i++)
                    for (int j = i + 1; j < N; j++)
                    {
                        double rho = rhoPhysical[i, j];
                        if (Math.Abs(rho) < 1e-14) continue;
                        double r0 = CorrectCorrelation(vars[i], vars[j], rho);
                        R0[i, j] = R0[j, i] = r0;
                    }
            }

            L = Cholesky(R0);
            Linv = InvertLowerTriangular(L);
        }

        private double CorrectCorrelation(RandomVariable a, RandomVariable b, double rho)
        {
            bool aN = a.Distribution == DistributionType.Normal;
            bool bN = b.Distribution == DistributionType.Normal;
            bool aL = a.Distribution == DistributionType.LogNormal;
            bool bL = b.Distribution == DistributionType.LogNormal;

            if (aN && bN) return rho;

            if (aL && bL)
            {
                double da = a.StdDev / a.Mean, db = b.StdDev / b.Mean;
                return Math.Log(1.0 + rho * da * db) /
                       Math.Sqrt(Math.Log(1.0 + da * da) * Math.Log(1.0 + db * db));
            }

            if (aN && bL)
            {
                double db = b.StdDev / b.Mean;
                return rho * db / Math.Sqrt(Math.Log(1.0 + db * db));
            }
            if (aL && bN)
            {
                double da = a.StdDev / a.Mean;
                return rho * da / Math.Sqrt(Math.Log(1.0 + da * da));
            }

            Warnings.Add($"No closed-form Nataf correction for {a.Distribution}/{b.Distribution} " +
                         $"({a.Name},{b.Name}); using rho0 = rho.");
            return rho;
        }

        /// <summary>z = L u</summary>
        public double[] ZfromU(double[] u)
        {
            double[] z = new double[N];
            for (int i = 0; i < N; i++)
            {
                double s = 0.0;
                for (int j = 0; j <= i; j++) s += L[i, j] * u[j];
                z[i] = s;
            }
            return z;
        }

        /// <summary>grad_u = L^T * gradZ</summary>
        public double[] GradientUfromZ(double[] gradZ)
        {
            double[] gu = new double[N];
            for (int j = 0; j < N; j++)
            {
                double s = 0.0;
                for (int i = j; i < N; i++) s += L[i, j] * gradZ[i];
                gu[j] = s;
            }
            return gu;
        }

        // --- small dense linear algebra (N is tiny) ---------------------------

        private static double[,] Cholesky(double[,] A)
        {
            int n = A.GetLength(0);
            var L = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double s = A[i, j];
                    for (int k = 0; k < j; k++) s -= L[i, k] * L[j, k];
                    if (i == j)
                    {
                        if (s <= 0)
                            throw new InvalidOperationException(
                                "Correlation matrix is not positive definite. Check the supplied correlations.");
                        L[i, j] = Math.Sqrt(s);
                    }
                    else L[i, j] = s / L[j, j];
                }
            return L;
        }

        private static double[,] InvertLowerTriangular(double[,] L)
        {
            int n = L.GetLength(0);
            var X = new double[n, n];
            for (int c = 0; c < n; c++)
            {
                X[c, c] = 1.0 / L[c, c];
                for (int i = c + 1; i < n; i++)
                {
                    double s = 0.0;
                    for (int k = c; k < i; k++) s += L[i, k] * X[k, c];
                    X[i, c] = -s / L[i, i];
                }
            }
            return X;
        }
    }


    // =======================================================================
    //  Marginal transforms
    // =======================================================================

    internal static class Marginal
    {
        /// <summary>Exact x = F^-1(Phi(z)).</summary>
        public static double XofZ(RandomVariable v, double z)
        {
            switch (v.Distribution)
            {
                case DistributionType.Normal:
                    return v.Mean + v.StdDev * z;

                case DistributionType.LogNormal:
                    {
                        double d = v.StdDev / v.Mean;
                        double zeta2 = Math.Log(1.0 + d * d);
                        double lambda = Math.Log(v.Mean) - 0.5 * zeta2;
                        return Math.Exp(lambda + Math.Sqrt(zeta2) * z);
                    }

                case DistributionType.Gumbel:
                    {
                        double alpha = v.StdDev * Math.Sqrt(6.0) / Math.PI;
                        double un = v.Mean - 0.5772 * alpha;
                        double F = MathNet.Numerics.Distributions.Normal.CDF(0, 1, z);
                        F = Math.Max(1e-15, Math.Min(1.0 - 1e-15, F));
                        return un - alpha * Math.Log(-Math.Log(F));
                    }
            }
            throw new NotSupportedException($"Distribution {v.Distribution} not supported.");
        }

        /// <summary>
        /// dx/dz = phi(z)/f(x). The Rackwitz-Fiessler equivalent normal standard
        /// deviation is exactly this quantity, so the existing verified routine is reused.
        /// </summary>
        public static double dXdZ(RandomVariable v, double x)
        {
            v.ComputeEquivalentNormal(x);
            return v.EquivNormalStdDev;
        }

        /// <summary>dz/dmu at fixed x - needed for parameter sensitivities.</summary>
        public static double dZdMean(RandomVariable v, double x)
        {
            switch (v.Distribution)
            {
                case DistributionType.Normal:
                    return -1.0 / v.StdDev;

                case DistributionType.LogNormal:
                    {
                        double d = v.StdDev / v.Mean;
                        double zeta = Math.Sqrt(Math.Log(1.0 + d * d));
                        return -1.0 / (zeta * v.Mean);
                    }

                default:
                    return -1.0 / v.StdDev;   // approximation
            }
        }
    }


    // =======================================================================
    //  FORM solver: Nataf + improved HL-RF
    // =======================================================================

    /// <summary>
    /// FORM with the Nataf transformation and improved HL-RF (merit-function line
    /// search). Differences from the original FORMSolver:
    ///
    ///   * handles CORRELATED variables (the original assumes independence)
    ///   * replaces the +/-6.0 step clamp with a merit-function line search, which
    ///     preserves the search DIRECTION instead of distorting it
    ///   * accepts an ANALYTICAL gradient delegate (e.g. DDM); falls back to
    ///     central finite differences when none is supplied
    ///   * the limit-state delegate may return double.NaN to signal "could not be
    ///     evaluated here" (e.g. the FE solve passed the limit load); the line
    ///     search then backtracks instead of failing
    /// </summary>
    public class FORMSolverNataf
    {
        public NatafTransform Nataf { get; private set; }

        /// <summary>sigma_i * dbeta/dmu_i at the design point (Fig. 7.4-style measures).</summary>
        public double[] ScaledMeanSensitivity { get; private set; }

        public FORMResult Solve(
            List<RandomVariable> variables,
            Func<double[], double> limitStateFunction,
            double[,] correlation = null,
            Func<double[], double[]> gradientX = null,
            double epsilon1 = 1e-6,
            double epsilon2 = 1e-6,
            int maxIterations = 60)
        {
            if (variables == null || variables.Count == 0)
                throw new ArgumentException("variables list cannot be null or empty.");
            if (limitStateFunction == null)
                throw new ArgumentNullException(nameof(limitStateFunction));

            int n = variables.Count;
            var result = new FORMResult(n);
            Nataf = new NatafTransform(variables, correlation);

            double[] u = new double[n];

            // --- evaluate at a point in u-space --------------------------------
            bool Evaluate(double[] uu, out double g, out double[] gu, out double[] xx)
            {
                double[] z = Nataf.ZfromU(uu);
                xx = new double[n];
                double[] J = new double[n];
                for (int i = 0; i < n; i++)
                {
                    xx[i] = Marginal.XofZ(variables[i], z[i]);
                    J[i] = Marginal.dXdZ(variables[i], xx[i]);
                }

                g = limitStateFunction(xx);
                gu = null;
                if (double.IsNaN(g) || double.IsInfinity(g)) return false;

                double[] gx = gradientX != null
                    ? gradientX(xx)
                    : FiniteDifferenceGradient(limitStateFunction, xx, variables);

                if (gx == null || gx.Any(v => double.IsNaN(v))) return false;

                double[] gz = new double[n];
                for (int i = 0; i < n; i++) gz[i] = gx[i] * J[i];
                gu = Nataf.GradientUfromZ(gz);
                return true;
            }

            if (!Evaluate(u, out double g0, out double[] grad, out double[] x))
                throw new InvalidOperationException("Limit state could not be evaluated at the mean point.");

            double g = g0;
            int iter = 0;
            bool converged = false;
            string msg = "";

            for (iter = 1; iter <= maxIterations; iter++)
            {
                double ng2 = grad.Sum(v => v * v);
                if (ng2 < 1e-30)
                {
                    msg = $"Gradient vanished at iteration {iter}.";
                    break;
                }

                double dot = 0.0;
                for (int i = 0; i < n; i++) dot += grad[i] * u[i];

                // HL-RF target and search direction
                double scalar = (dot - g) / ng2;
                double[] d = new double[n];
                for (int i = 0; i < n; i++) d[i] = scalar * grad[i] - u[i];

                // merit m(u) = 0.5|u|^2 + c|g| with c > |u|/|grad g|
                double normU = Math.Sqrt(u.Sum(v => v * v));
                double c = 2.0 * normU / Math.Sqrt(ng2) + 10.0;
                double m0 = 0.5 * u.Sum(v => v * v) + c * Math.Abs(g);

                double step = 1.0;
                bool accepted = false;
                double[] uTry = null, gradTry = null, xTry = null;
                double gTry = 0.0;

                for (int ls = 0; ls < 12; ls++)
                {
                    uTry = new double[n];
                    for (int i = 0; i < n; i++) uTry[i] = u[i] + step * d[i];

                    if (Evaluate(uTry, out gTry, out gradTry, out xTry))
                    {
                        double mTry = 0.5 * uTry.Sum(v => v * v) + c * Math.Abs(gTry);
                        if (mTry < m0) { accepted = true; break; }
                    }
                    step *= 0.5;
                }

                if (!accepted)
                {
                    msg = $"Line search failed at iteration {iter} (no merit decrease). " +
                          "The limit state may be past the structural limit load.";
                    break;
                }

                u = uTry; g = gTry; grad = gradTry; x = xTry;

                double beta = Math.Sqrt(u.Sum(v => v * v));
                result.BetaHistory.Add(beta);

                double gn = Math.Sqrt(grad.Sum(v => v * v));
                double[] alpha = grad.Select(v => -v / gn).ToArray();
                double au = 0.0;
                for (int i = 0; i < n; i++) au += alpha[i] * u[i];
                double crit2 = Math.Sqrt(Enumerable.Range(0, n)
                                   .Sum(i => Math.Pow(u[i] - au * alpha[i], 2)));
                double crit1 = Math.Abs(g) / Math.Max(Math.Abs(g0), 1e-12);

                if (crit1 < epsilon1 && crit2 < epsilon2)
                {
                    converged = true;
                    msg = $"Converged in {iter} iterations.";
                    break;
                }
            }

            // --- results -------------------------------------------------------
            double betaFinal = Math.Sqrt(u.Sum(v => v * v));
            double gnorm = Math.Sqrt(grad.Sum(v => v * v));

            // If g < 0 at the mean point, the origin lies INSIDE the failure domain,
            // so beta is negative and Pf > 0.5. Without this the Pf is inverted.
            double signBeta = (g0 < 0.0) ? -1.0 : 1.0;
            betaFinal *= signBeta;

            result.Beta = betaFinal;
            result.ProbabilityOfFailure = MathNet.Numerics.Distributions.Normal.CDF(0, 1, -betaFinal);
            result.Converged = converged;
            result.Iterations = iter;
            result.ConvergenceMessage = string.IsNullOrEmpty(msg)
                ? $"Stopped after {iter} iterations." : msg;

            for (int i = 0; i < n; i++)
            {
                result.MPP_U[i] = u[i];
                result.MPP_X[i] = x[i];
                result.AlphaFactors[i] = gnorm > 0 ? -grad[i] / gnorm : 0.0;
            }

            ScaledMeanSensitivity = ComputeMeanSensitivity(variables, u, x, Math.Abs(betaFinal));
            if (signBeta < 0)
                for (int i = 0; i < ScaledMeanSensitivity.Length; i++)
                    ScaledMeanSensitivity[i] *= -1.0;
            return result;
        }

        /// <summary>
        /// sigma_i * dbeta/dmu_i  via the envelope theorem:
        ///     dbeta/dmu_i = uhat . (Linv * e_i) * dz_i/dmu_i
        /// </summary>
        private double[] ComputeMeanSensitivity(List<RandomVariable> vars,
                                                double[] u, double[] x, double beta)
        {
            int n = vars.Count;
            var s = new double[n];
            if (beta < 1e-12) return s;

            double[] uhat = u.Select(v => v / beta).ToArray();

            for (int i = 0; i < n; i++)
            {
                // column i of Linv
                double dotv = 0.0;
                for (int r = i; r < n; r++) dotv += uhat[r] * Nataf.Linv[r, i];

                s[i] = vars[i].StdDev * dotv * Marginal.dZdMean(vars[i], x[i]);
            }
            return s;
        }

        private static double[] FiniteDifferenceGradient(
            Func<double[], double> gfun, double[] x, List<RandomVariable> vars)
        {
            int n = x.Length;
            var grad = new double[n];
            for (int i = 0; i < n; i++)
            {
                double h = vars[i].StdDev * 1e-3 + 1e-9;
                double[] xp = (double[])x.Clone(); xp[i] += h;
                double[] xm = (double[])x.Clone(); xm[i] -= h;
                double gp = gfun(xp), gm = gfun(xm);
                if (double.IsNaN(gp) || double.IsNaN(gm)) return null;
                grad[i] = (gp - gm) / (2.0 * h);
            }
            return grad;
        }
    }
}
