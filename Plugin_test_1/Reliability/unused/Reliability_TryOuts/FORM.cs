using System;
using MathNet.Numerics.Distributions;
using Plugin_test_1.Reliability;

namespace Plugin_test_1.Reliability.Reliability_TryOuts
{
    /// <summary>
    /// First Order Reliability Method — Hasofer-Lind-Rackwitz-Fiessler (HLRF) algorithm.
    /// Limit state: g = theta_R * W_el * fy  -  theta_E * M_max(G,Q) * 1e6
    /// RV order: [fy, G, Q, theta_R, theta_E]
    /// </summary>
    internal static class FORM
    {
        private const int    MaxIter = 100;
        private const double Tol     = 1e-6;
        private const double Relax   = 0.6;
        private const double PdfEps  = 1e-12;
        private const double FdEps   = 1e-4;   // finite-difference step for dM/dG, dM/dQ

        // ── Limit state in physical space ─────────────────────────────────────
        private static double G_physical(double[] x, double W_el, double L, double a)
        {
            double fy  = x[0], Q = x[1]; // Q = x[2] thR = x[3], thE = x[4]; 
            double M   = BeamMechanics.MaxMoment(L, 0.0, Q, a); // in kNm
            return W_el * fy - M * 1e6;
        }

        // ── Limit state in standard-normal space ──────────────────────────────
        private static double G_u(double[] u, StochasticVariable[] rvs, double W_el, double L, double a)
        {
            double[] x = new double[2];
            for (int i = 0; i < 2; i++) x[i] = rvs[i].UtoX(u[i]);
            return G_physical(x, W_el, L, a);
        }

        // ── Analytic gradient in u-space ──────────────────────────────────────
        // dg/du_i = (dg/dx_i) * (dx_i/du_i)   where dx/du = phi(u)/f(x)
        private static double[] GradU(double[] u, StochasticVariable[] rvs, double W_el, double L, double a)
        {
            int n = rvs.Length;

            double[] x     = new double[n];
            double[] phiU  = new double[n];
            double[] fX    = new double[n];
            double[] dxDu  = new double[n];

            for (int i = 0; i < n; i++)
            {
                x[i]    = rvs[i].UtoX(u[i]);
                phiU[i] = Normal.PDF(0, 1, u[i]);
                fX[i]   = Math.Max(rvs[i].PDF(x[i]), PdfEps);
                dxDu[i] = phiU[i] / fX[i];
            }

            double fy = x[0], Q = x[1]; // Q = x[2], thR = x[3], thE = x[4];
            double M   = BeamMechanics.MaxMoment(L, 0.0, Q, a);

            // Finite difference for dM/dG and dM/dQ
            //double dMdG = (BeamMechanics.MaxMoment(L, 0.0 + FdEps, Q, a)
            //             - BeamMechanics.MaxMoment(L, 0.0 - FdEps, Q, a)) / (2.0 * FdEps);
            double dMdQ = (BeamMechanics.MaxMoment(L, 0.0, Q + FdEps, a)
                         - BeamMechanics.MaxMoment(L, 0.0, Q - FdEps, a)) / (2.0 * FdEps);

            double[] dgDx = new double[2]
            {
                W_el,                    // dg/dfy
               -dMdQ * 1e6              // dg/dQ
            };

            double[] grad = new double[2];
            for (int i = 0; i < 2; i++) grad[i] = dgDx[i] * dxDu[i];

            return grad;
        }

        // ── HLRF iteration ────────────────────────────────────────────────────
        /// <summary>
        /// Runs FORM and returns the reliability index beta.
        /// converged is true if the algorithm met the tolerance criterion.
        /// Note: for heavily over-designed sections converged may be false but
        /// beta is still a numerically valid estimate.
        /// </summary>
        public static double Run(double W_el, double L, double a,
                                 StochasticVariable[] rvs,
                                 out double beta, out bool converged, out double[] alpha)
        {
            int n = rvs.Length;
            double[] u = new double[n]; // start at origin

            converged = false;
            beta      = double.NaN;
            alpha = new double[n];

            for (int iter = 0; iter < MaxIter; iter++)
            {
                double   g    = G_u(u, rvs, W_el, L, a);
                double[] grad = GradU(u, rvs, W_el, L, a);

                // Norm of gradient
                double ng = 0.0;
                for (int i = 0; i < n; i++) ng += grad[i] * grad[i];
                ng = Math.Sqrt(ng);

                if (ng == 0.0 || double.IsNaN(ng) || double.IsNaN(g))
                {
                    beta = double.NaN;
                    return beta;
                }

                // Direction cosines (alpha vector)
                for (int i = 0; i < n; i++) alpha[i] = grad[i] / ng;

                // Beta along search direction
                double dotAU = 0.0;
                for (int i = 0; i < n; i++) dotAU += alpha[i] * u[i];
                double betaNew = -g / ng + dotAU;

                // HLRF update with relaxation
                double[] uNew = new double[n];
                for (int i = 0; i < n; i++)
                    uNew[i] = (1.0 - Relax) * u[i] + Relax * betaNew * alpha[i];

                // Convergence check
                double diff = 0.0;
                for (int i = 0; i < n; i++) diff += (uNew[i] - u[i]) * (uNew[i] - u[i]);
                diff = Math.Sqrt(diff);

                u = uNew;

                if (diff < Tol)
                {
                    converged = true;
                    break;
                }
            }

            // Beta = distance from origin to design point
            double betaSq = 0.0;
            for (int i = 0; i < n; i++) betaSq += u[i] * u[i];
            beta = Math.Sqrt(betaSq);

            // Final alpha = u*/β 
            if (beta > 1e-16)
                for (int i = 0; i < n; i++) alpha[i] = u[i] / beta;

            return beta;
        }

        // Backwards-compatible overload: original signature without alpha
        public static double Run(double W_el, double L, double a,
                                 StochasticVariable[] rvs,
                                 out double beta, out bool converged)
        {
            double[] alpha;
            return Run(W_el, L, a, rvs, out beta, out converged, out alpha);
        }
    }
}
