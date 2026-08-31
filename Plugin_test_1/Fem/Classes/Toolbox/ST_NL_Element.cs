using System;
using System.Collections.Generic;
using System.Linq;

using Rhino.Geometry;

using CSparse.Double;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    /// <summary>
    /// Geometrically nonlinear element kernel (Total Lagrangian, Green-Lagrangian strain).
    ///
    /// Formulation:
    ///     E_gl = (l^2 - L^2) / (2 L^2)        Green-Lagrangian axial strain
    ///     S    = E_mod * E_gl                 2nd Piola-Kirchhoff stress
    ///     N    = S * A                        axial force
    ///     B    = [-d, d] / L^2                strain-displacement (d = CURRENT vector)
    ///     f    = A * L * S * B                internal force (6 translational DOF)
    ///     Kt   = A*L*E*(B (x) B)  +  (A*S/L)*[[I,-I],[-I,I]]
    ///            \___material___/    \______geometric______/
    ///
    /// The geometric term is the P-delta effect: it softens the structure under
    /// compression (S negative) and is what produces buckling behaviour.
    ///
    /// Bending/torsion are kept LINEAR (small parasitic stiffness) and are taken
    /// from the element's existing EKG with the AXIAL terms removed so that the
    /// nonlinear axial contribution above is not double counted.
    ///
    /// UNITS (matching Calc_ElemStiffMX):
    ///     Sec.Mat.E [N/mm2],  Sec.Area [mm2],  lengths [m]
    ///     => A*E = [N],  f = [N],  Kt = [N/m]
    /// </summary>
    public static class NL_Element
    {
        /// <summary>Translational DOF positions inside the 12-DOF element vector.</summary>
        private static readonly int[] TRANS = { 0, 1, 2, 6, 7, 8 };

        /// <summary>
        /// Local-axis indices of the axial (u_x) terms in the 12x12 local stiffness matrix.
        /// These are removed from the bending matrix so the nonlinear axial law replaces them.
        /// </summary>
        private static readonly int[] AXIAL_LOCAL = { 0, 6 };

        /// <summary>
        /// Bending/torsion stiffness in GLOBAL coordinates, with axial terms stripped.
        /// Cached per element in <paramref name="cache"/> because it never changes.
        /// </summary>
        public static DenseMatrix BendingStiffnessGlobal(TB_Element_1D e,
                                                         Dictionary<TB_Element_1D, DenseMatrix> cache)
        {
            if (cache != null && cache.TryGetValue(e, out DenseMatrix cached)) return cached;

            // Local elastic matrix, then remove pure-axial entries (0,0) (0,6) (6,0) (6,6).
            DenseMatrix ekLocal = (DenseMatrix)e.EK.Clone();
            foreach (int i in AXIAL_LOCAL)
                foreach (int j in AXIAL_LOCAL)
                    ekLocal[i, j] = 0.0;

            DenseMatrix kBend = e.TM.Transpose().Multiply(ekLocal).Multiply(e.TM) as DenseMatrix;

            if (cache != null) cache[e] = kBend;
            return kBend;
        }

        /// <summary>
        /// Computes internal force vector and tangent stiffness for one element
        /// at the given global displacement state.
        /// </summary>
        /// <param name="e">Element (its Nodes[].Pt define the ORIGINAL configuration).</param>
        /// <param name="uElem">12 global displacements: [uxi,uyi,uzi,rxi,ryi,rzi, uxj,...]. Translations in [m].</param>
        /// <param name="fInt">out: 12 internal force components [N, Nm].</param>
        /// <param name="kT">out: 12x12 tangent stiffness [N/m].</param>
        /// <param name="axialForce">out: axial force N [N], tension positive.</param>
        /// <param name="cache">optional cache for the bending matrix.</param>
        public static void Compute(TB_Element_1D e,
                                   double[] uElem,
                                   out double[] fInt,
                                   out DenseMatrix kT,
                                   out double axialForce,
                                   Dictionary<TB_Element_1D, DenseMatrix> cache = null)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            if (uElem == null || uElem.Length != 12)
                throw new ArgumentException("uElem must have 12 entries.", nameof(uElem));

            // --- original geometry -------------------------------------------------
            Point3d Pi = e.Nodes[0].Pt;
            Point3d Pj = e.Nodes[1].Pt;

            double X0 = Pj.X - Pi.X;
            double Y0 = Pj.Y - Pi.Y;
            double Z0 = Pj.Z - Pi.Z;
            double L = Math.Sqrt(X0 * X0 + Y0 * Y0 + Z0 * Z0);   // [m]

            if (L <= 0.0) throw new InvalidOperationException("Zero-length element.");

            // --- current chord vector d = (Pj + uj) - (Pi + ui) --------------------
            double dx = X0 + (uElem[6] - uElem[0]);
            double dy = Y0 + (uElem[7] - uElem[1]);
            double dz = Z0 + (uElem[8] - uElem[2]);

            double l2 = dx * dx + dy * dy + dz * dz;
            double L2 = L * L;

            // --- constitutive ------------------------------------------------------
            double Emod = e.Sec.Mat.E;      // [N/mm2]
            double A = e.Sec.Area;          // [mm2]

            double Egl = (l2 - L2) / (2.0 * L2);   // dimensionless
            double S = Emod * Egl;                 // [N/mm2]
            axialForce = S * A;                    // [N]  (+tension)

            // --- B vector (6), units [1/m] ----------------------------------------
            double invL2 = 1.0 / L2;
            double[] B = { -dx * invL2, -dy * invL2, -dz * invL2,
                            dx * invL2,  dy * invL2,  dz * invL2 };

            // --- internal force on translational DOF, f = A*L*S*B  [N] -------------
            double ALS = A * L * S;
            double[] fAx = new double[6];
            for (int i = 0; i < 6; i++) fAx[i] = ALS * B[i];

            // --- material tangent  A*L*E*(B (x) B)   [N/m] ------------------------
            double ALE = A * L * Emod;
            double[,] kAx = new double[6, 6];
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                    kAx[i, j] = ALE * B[i] * B[j];

            // --- geometric tangent  (A*S/L) * [[I,-I],[-I,I]]   [N/m] -------------
            double g = A * S / L;
            for (int i = 0; i < 3; i++)
            {
                kAx[i, i] += g;
                kAx[i + 3, i + 3] += g;
                kAx[i, i + 3] -= g;
                kAx[i + 3, i] -= g;
            }

            // --- assemble 12-DOF quantities ---------------------------------------
            DenseMatrix kBend = BendingStiffnessGlobal(e, cache);

            kT = (DenseMatrix)kBend.Clone();
            fInt = new double[12];

            // bending/torsion contribution is linear: f = K_bend * u
            for (int i = 0; i < 12; i++)
            {
                double s = 0.0;
                for (int j = 0; j < 12; j++) s += kBend[i, j] * uElem[j];
                fInt[i] = s;
            }

            // scatter nonlinear axial contribution into translational DOF
            for (int a = 0; a < 6; a++)
            {
                fInt[TRANS[a]] += fAx[a];
                for (int b = 0; b < 6; b++)
                    kT[TRANS[a], TRANS[b]] += kAx[a, b];
            }
        }

        /// <summary>
        /// Convenience: extracts the 12 element displacements from a global vector.
        /// </summary>
        public static double[] GatherElementDisp(TB_Element_1D e, double[] uGlobal, int nDof = 6)
        {
            double[] ue = new double[12];
            int si = nDof * e.Nodes[0].Id.Value;
            int ei = nDof * e.Nodes[1].Id.Value;
            Array.Copy(uGlobal, si, ue, 0, 6);
            Array.Copy(uGlobal, ei, ue, 6, 6);
            return ue;
        }
    }
}
