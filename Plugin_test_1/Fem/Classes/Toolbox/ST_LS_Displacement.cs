// =============================================================================
//  ST_LS_Displacement.cs
//
//  WHAT IT IS
//  The largest displacement of a linear-elastic model, for the "Max Displacement"
//  outputs of FORM Members, RBDO Sections, EC3 Members and EC3 Sections.
//
//  WHAT IS MEASURED
//  The length of the translation vector, sqrt(ux^2 + uy^2 + uz^2), at every node
//  and at the middle of every member; the largest of these. Rotations are not
//  displacements and are left out. Like Karamba3D, which checks displacements at
//  the start, middle and end of its beams.
//
//  The middle of a member: with nodal loads only, the deflected axis of a member
//  is the cubic through its end displacements and end rotations, so at mid-length
//
//      u_mid = (u1 + u2) / 2  +  (L / 8) (theta1 - theta2) x e_x
//
//  with e_x the unit vector along the member (theta x e_x is the slope of the
//  deflected axis). The axial part is the average of the two ends.
//
//  The largest VERTICAL displacement |uz| is found the same way; it is what a
//  deflection limit such as L/300 is usually checked against.
//
//  LOADS
//    Characteristic   the loads as modelled: every load case x 1.0, with the E
//                     of the model. For a model that holds the characteristic
//                     loads this is G + Q (with one variable action the
//                     characteristic combination of EN 1990 6.14b). All four
//                     components give this value, so their designs compare
//                     one to one.
//    Design (ULS)     the largest over the EN 1990 (6.10a) and (6.10b)
//                     combinations that the Eurocode check tries
//                     (EurocodeMemberCheck.CombinationsOf). Only the Eurocode
//                     components know which load case is G and which is Q.
//
//  The displacements of the single load cases come from the same FE engine as
//  the member forces (LS_DDM), so one factorisation gives every combination.
//
//  UNITS: displacements in [m] (lengths of the model in m); the components
//  output [mm].
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Classes.Reliability
{
    /// <summary>The largest displacement of a model under one set of loads.</summary>
    public class DisplacementResult
    {
        /// <summary>Largest length of the translation vector [m]. NaN if not computed.</summary>
        public double Max = double.NaN;
        /// <summary>Where Max is: a node index, or -1 if it is at the middle of a member.</summary>
        public int Node = -1;
        /// <summary>Where Max is: a member index if it is at the middle of that member, else -1.</summary>
        public int Member = -1;
        /// <summary>ux, uy, uz at that point [m].</summary>
        public double[] Vector = new double[3];

        /// <summary>Largest |uz| [m], and where (node index, or member index for its middle; the other -1).</summary>
        public double MaxVertical = double.NaN;
        public int NodeVertical = -1;
        public int MemberVertical = -1;

        /// <summary>The loads, e.g. "loads as modelled: 1.00 LC0 + 1.00 LC1" or "6.10b: 1.20 LC0 + 1.50 LC1".</summary>
        public string Loads = "";
        /// <summary>The loads of MaxVertical where they differ from Loads; empty otherwise.</summary>
        public string LoadsVertical = "";

        /// <summary>Largest displacement in [mm]; NaN if not computed.</summary>
        public double MaxMm => Max * 1e3;

        /// <summary>One line for a report.</summary>
        public string Describe(TB_Model mdl)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (double.IsNaN(Max)) return "not computed";

            string s = (Max * 1e3).ToString("F2", ci) + " mm " + Where(mdl, Node, Member) +
                       " (ux " + (Vector[0] * 1e3).ToString("F2", ci) +
                       ", uy " + (Vector[1] * 1e3).ToString("F2", ci) +
                       ", uz " + (Vector[2] * 1e3).ToString("F2", ci) + " mm)";
            if (!double.IsNaN(MaxVertical))
                s += "; largest vertical " + (MaxVertical * 1e3).ToString("F2", ci) + " mm " +
                     Where(mdl, NodeVertical, MemberVertical) +
                     (string.IsNullOrEmpty(LoadsVertical) ? "" : " under " + LoadsVertical);
            return s;
        }

        private static string Where(TB_Model mdl, int node, int member)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (node >= 0) return "at node " + node.ToString(ci);
            if (member >= 0)
            {
                string tag = (mdl != null && member < mdl.Elem1Ds.Count) ? mdl.Elem1Ds[member].Tag : "";
                return "at the middle of member [" + member.ToString(ci) + "] " + tag;
            }
            return "";
        }
    }

    public static class ModelDisplacement
    {
        /// <summary>
        /// The loads as modelled (every load case x 1.0), with the E of the model. For a model
        /// with the characteristic loads: G + Q.
        /// </summary>
        public static DisplacementResult Characteristic(TB_Model mdl)
        {
            if (mdl == null) throw new ArgumentNullException(nameof(mdl));

            // no parameters: every load case is applied as modelled
            var fe = new LS_DDM(mdl, new List<LS_Parameter>());
            fe.Evaluate(new double[0]);

            DisplacementResult r = Largest(mdl, fe.U);
            int[] lcs = mdl.LCs ?? new int[0];
            r.Loads = "loads as modelled" + (lcs.Length == 0 ? " (no loads)"
                : ": " + string.Join(" + ", lcs.Select(lc => "1.00 LC" + lc.ToString(CultureInfo.InvariantCulture))));
            return r;
        }

        /// <summary>
        /// The largest displacement over the EN 1990 (6.10a) and (6.10b) combinations that the
        /// Eurocode check tries, for the declared load cases. factors: null = the Norwegian NA.
        /// </summary>
        public static DisplacementResult Design(TB_Model mdl, IList<EcLoadCase> loadCases, EcFactors factors = null)
        {
            if (mdl == null) throw new ArgumentNullException(nameof(mdl));
            if (loadCases == null || loadCases.Count == 0)
                throw new ArgumentException("Declare every load case as permanent or variable.");
            EcFactors f = factors ?? new EcFactors();

            // one load-case parameter per load case with reference 1: du/dp is the displacement of that load case alone
            var pars = loadCases.Select(lc => LS_Parameter.ForLoadCase(
                "LC" + lc.LoadCase.ToString(CultureInfo.InvariantCulture), lc.LoadCase, 1.0)).ToList();
            var fe = new LS_DDM(mdl, pars);
            fe.Evaluate(Enumerable.Repeat(1.0, pars.Count).ToArray());
            int nDof = fe.U.Length;

            // displacements of the load cases that are not declared (there are none after the
            // Eurocode check, which requires every load case to be declared): applied as modelled
            double[] uRest = (double[])fe.U.Clone();
            for (int i = 0; i < pars.Count; i++)
                for (int d = 0; d < nDof; d++) uRest[d] -= fe.dU[i][d];

            DisplacementResult best = null;
            DisplacementResult bestVertical = null;          // the vertical maximum may come from another combination
            string verticalCombo = "";
            foreach (KeyValuePair<string, double[]> combo in EurocodeMemberCheck.CombinationsOf(loadCases, f))
            {
                double[] u = (double[])uRest.Clone();
                for (int i = 0; i < pars.Count; i++)
                {
                    double w = combo.Value[i];
                    if (w == 0.0) continue;
                    double[] ui = fe.dU[i];
                    for (int d = 0; d < nDof; d++) u[d] += w * ui[d];
                }

                DisplacementResult r = Largest(mdl, u);
                if (best == null || r.Max > best.Max)
                {
                    best = r;
                    best.Loads = combo.Key;
                }
                if (bestVertical == null || r.MaxVertical > bestVertical.MaxVertical)
                {
                    bestVertical = r;
                    verticalCombo = combo.Key;
                }
            }
            if (best == null) return new DisplacementResult();

            best.MaxVertical = bestVertical.MaxVertical;
            best.NodeVertical = bestVertical.NodeVertical;
            best.MemberVertical = bestVertical.MemberVertical;
            if (verticalCombo != best.Loads) best.LoadsVertical = verticalCombo;
            return best;
        }

        /// <summary>The block for a component's Report. design may be null.</summary>
        public static string Report(TB_Model mdl, DisplacementResult characteristic, DisplacementResult design = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("MAX DISPLACEMENT (length of the translation vector, at the nodes and the middles of the members; E of the model)");
            if (characteristic != null)
            {
                sb.AppendLine("  characteristic, " + characteristic.Loads + ":");
                sb.AppendLine("    " + characteristic.Describe(mdl));
            }
            if (design != null)
            {
                sb.AppendLine("  design (ULS), largest over the combinations, " + design.Loads + ":");
                sb.AppendLine("    " + design.Describe(mdl));
            }
            return sb.ToString();
        }

        /// <summary>
        /// The largest translation of the global displacement vector u (6 DOF per node, [m] and
        /// [rad]) over the nodes and the middles of the members.
        /// </summary>
        public static DisplacementResult Largest(TB_Model mdl, double[] u)
        {
            const int N_DOF = 6;
            var r = new DisplacementResult { Max = 0.0, MaxVertical = 0.0 };

            // --- nodes ---
            foreach (Node nd in mdl.Nodes)
            {
                if (!nd.Id.HasValue) continue;
                int b = N_DOF * nd.Id.Value;
                double[] t = { u[b], u[b + 1], u[b + 2] };
                Consider(r, t, nd.Id.Value, -1);
            }

            // --- middles of the members ---
            for (int m = 0; m < mdl.Elem1Ds.Count; m++)
            {
                TB_Element_1D e = mdl.Elem1Ds[m];
                if (e == null || e.Nodes == null || e.Nodes.Count < 2) continue;
                Node n1 = e.Nodes[0], n2 = e.Nodes[1];
                if (!n1.Id.HasValue || !n2.Id.HasValue) continue;

                double dx = n2.Pt.X - n1.Pt.X, dy = n2.Pt.Y - n1.Pt.Y, dz = n2.Pt.Z - n1.Pt.Z;
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (!(len > 0.0)) continue;
                double[] ex = { dx / len, dy / len, dz / len };

                int b1 = N_DOF * n1.Id.Value, b2 = N_DOF * n2.Id.Value;
                double[] dTheta = { u[b1 + 3] - u[b2 + 3], u[b1 + 4] - u[b2 + 4], u[b1 + 5] - u[b2 + 5] };
                double[] slope = Cross(dTheta, ex);               // (theta1 - theta2) x e_x

                double[] t = new double[3];
                for (int i = 0; i < 3; i++)
                    t[i] = 0.5 * (u[b1 + i] + u[b2 + i]) + 0.125 * len * slope[i];
                Consider(r, t, -1, m);
            }
            return r;
        }

        private static void Consider(DisplacementResult r, double[] t, int node, int member)
        {
            double len = Math.Sqrt(t[0] * t[0] + t[1] * t[1] + t[2] * t[2]);
            // a node wins a tie with the middle of a member next to it
            if (len > r.Max * (1.0 + 1e-12) || (r.Node < 0 && r.Member < 0))
            {
                r.Max = len;
                r.Node = node;
                r.Member = member;
                r.Vector = (double[])t.Clone();
            }
            double v = Math.Abs(t[2]);
            if (v > r.MaxVertical * (1.0 + 1e-12) || (r.NodeVertical < 0 && r.MemberVertical < 0))
            {
                r.MaxVertical = v;
                r.NodeVertical = node;
                r.MemberVertical = member;
            }
        }

        private static double[] Cross(double[] a, double[] b)
        {
            return new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0]
            };
        }
    }
}
