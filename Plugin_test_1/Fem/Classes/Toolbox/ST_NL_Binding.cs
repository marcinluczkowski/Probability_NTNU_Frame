using System;
using System.Collections.Generic;
using System.Linq;

using Rhino.Geometry;

using CSparse.Double;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    /// <summary>
    /// Binds a vector of stochastic variables to a structural model.
    ///
    /// DESIGN NOTES
    /// ------------
    /// 1. ABSOLUTE, NOT INCREMENTAL. Baseline values are captured once at
    ///    construction; Apply() always writes absolute values derived from that
    ///    baseline. Applying the same x twice yields an identical model, and the
    ///    order of parameters never matters. This is what makes repeated probing
    ///    (line-search backtracking, finite differences) safe.
    ///
    /// 2. GROUP TARGETED. Moduli are applied to every element whose Section Tag
    ///    matches, so D1 (struts) and D2 (braces) stay independent. The element
    ///    index lists are cached once so the hot loop does no string comparison.
    ///
    /// 3. MINIMAL REBUILD. Element matrices are recomputed only for what actually
    ///    changed: moduli -> EK; coordinates -> Line, EK, TM, EKG; loads -> nothing.
    ///
    /// PREREQUISITE
    /// ------------
    /// TB_Section.DeepCopy() must clone its Material:
    ///     var copy = (TB_Section)MemberwiseClone();
    ///     copy.Mat = Mat?.DeepCopy();
    ///     return copy;
    /// Without this, sections share TB_Material instances and writing E on one
    /// element silently changes others - including in the ORIGINAL model.
    /// </summary>
    public class NL_ModelBinding
    {
        private readonly List<NL_Parameter> _pars;

        // cached targets
        private readonly Dictionary<int, int[]> _modulusElems = new Dictionary<int, int[]>();
        private readonly Dictionary<int, int> _coordNodeIdx = new Dictionary<int, int>();
        private readonly Dictionary<int, List<int>> _coordElems = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, int> _loadIdx = new Dictionary<int, int>();

        // baseline values, one per parameter
        public double[] Baseline { get; private set; }

        private static readonly System.Reflection.PropertyInfo P_E =
            typeof(TB_Material).GetProperty("E");

        public NL_ModelBinding(TB_Model baselineModel, IList<NL_Parameter> pars)
        {
            _pars = pars.ToList();
            Baseline = new double[_pars.Count];

            for (int p = 0; p < _pars.Count; p++)
            {
                NL_Parameter par = _pars[p];

                switch (par.Type)
                {
                    case NL_ParamType.Modulus:
                        {
                            var idx = new List<int>();
                            for (int i = 0; i < baselineModel.Elem1Ds.Count; i++)
                            {
                                TB_Element_1D e = baselineModel.Elem1Ds[i];
                                if (string.IsNullOrEmpty(par.SectionTag) ||
                                    string.Equals(e.Sec.Tag, par.SectionTag,
                                                  StringComparison.OrdinalIgnoreCase))
                                    idx.Add(i);
                            }
                            if (idx.Count == 0)
                                throw new ArgumentException(
                                    $"Parameter '{par.Name}': no element has section tag '{par.SectionTag}'.");

                            _modulusElems[p] = idx.ToArray();
                            Baseline[p] = baselineModel.Elem1Ds[idx[0]].Sec.Mat.E;

                            // guard against a shared TB_Material across groups
                            VerifyNotAliased(baselineModel, idx, par);
                            break;
                        }

                    case NL_ParamType.NodeCoordinate:
                        {
                            int ni = baselineModel.Nodes.FindIndex(n => n.Id.Value == par.NodeId);
                            if (ni < 0)
                                throw new ArgumentException($"Parameter '{par.Name}': node {par.NodeId} not found.");
                            _coordNodeIdx[p] = ni;

                            var touching = new List<int>();
                            for (int i = 0; i < baselineModel.Elem1Ds.Count; i++)
                            {
                                var e = baselineModel.Elem1Ds[i];
                                if (e.Nodes[0].Id.Value == par.NodeId || e.Nodes[1].Id.Value == par.NodeId)
                                    touching.Add(i);
                            }
                            _coordElems[p] = touching;

                            Point3d q = baselineModel.Nodes[ni].Pt;
                            Baseline[p] = par.Component == 0 ? q.X : (par.Component == 1 ? q.Y : q.Z);
                            break;
                        }

                    case NL_ParamType.PointLoad:
                        {
                            int li = baselineModel.Loads.FindIndex(l =>
                                l is TB_Load_Point q && q.Node != null && q.Node.Id.Value == par.NodeId);
                            if (li < 0)
                                throw new ArgumentException($"Parameter '{par.Name}': no point load at node {par.NodeId}.");
                            _loadIdx[p] = li;
                            Baseline[p] = ((TB_Load_Point)baselineModel.Loads[li]).Loads[par.Dof] / par.Scale;
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// Writes the parameter vector into the model as ABSOLUTE values and
        /// rebuilds only the element matrices that are affected.
        /// </summary>
        public void Apply(TB_Model m, double[] x)
        {
            if (x.Length != _pars.Count)
                throw new ArgumentException($"Expected {_pars.Count} values, got {x.Length}.");

            var dirtyStiffness = new HashSet<int>();   // needs EK rebuilt
            var dirtyGeometry = new HashSet<int>();    // needs Line/EK/TM/EKG rebuilt

            for (int p = 0; p < _pars.Count; p++)
            {
                NL_Parameter par = _pars[p];

                switch (par.Type)
                {
                    case NL_ParamType.Modulus:
                        foreach (int i in _modulusElems[p])
                        {
                            P_E.SetValue(m.Elem1Ds[i].Sec.Mat, x[p], null);
                            dirtyStiffness.Add(i);
                        }
                        break;

                    case NL_ParamType.NodeCoordinate:
                        {
                            int ni = _coordNodeIdx[p];
                            Point3d q = m.Nodes[ni].Pt;
                            if (par.Component == 0) q.X = x[p];
                            else if (par.Component == 1) q.Y = x[p];
                            else q.Z = x[p];
                            m.Nodes[ni].Pt = q;

                            foreach (int i in _coordElems[p]) dirtyGeometry.Add(i);
                            break;
                        }

                    case NL_ParamType.PointLoad:
                        ((TB_Load_Point)m.Loads[_loadIdx[p]]).Loads[par.Dof] = x[p] * par.Scale;
                        break;
                }
            }

            // --- rebuild ------------------------------------------------------
            foreach (int i in dirtyGeometry)
            {
                TB_Element_1D e = m.Elem1Ds[i];
                e.Line = new Line(e.Nodes[0].Pt, e.Nodes[1].Pt);
            }

            foreach (int i in dirtyGeometry.Union(dirtyStiffness))
            {
                TB_Element_1D e = m.Elem1Ds[i];
                e.EK = e.Calc_ElemStiffMX();
                if (dirtyGeometry.Contains(i))
                {
                    e.TM = e.Calc_TransMX();
                    e.EKG = e.TM.Transpose().Multiply(e.EK).Multiply(e.TM) as DenseMatrix;
                }
                else
                {
                    e.EKG = e.TM.Transpose().Multiply(e.EK).Multiply(e.TM) as DenseMatrix;
                }
            }
        }

        /// <summary>Clears stored results so a model can be re-solved cleanly.</summary>
        public static void ResetResults(TB_Model m)
        {
            m.Disps.Clear();
            foreach (Node n in m.Nodes) n.Disps.Clear();
            foreach (TB_Support s in m.Sups) s.React?.Clear();
        }

        /// <summary>
        /// Fails loudly if two modulus parameters would write to the same
        /// TB_Material instance - the classic "global update" trap.
        /// </summary>
        private void VerifyNotAliased(TB_Model m, List<int> idx, NL_Parameter par)
        {
            var mine = new HashSet<TB_Material>(idx.Select(i => m.Elem1Ds[i].Sec.Mat));

            for (int i = 0; i < m.Elem1Ds.Count; i++)
            {
                if (idx.Contains(i)) continue;
                if (mine.Contains(m.Elem1Ds[i].Sec.Mat))
                    throw new InvalidOperationException(
                        $"Parameter '{par.Name}' targets section tag '{par.SectionTag}', but element " +
                        $"{i} (tag '{m.Elem1Ds[i].Sec.Tag}') shares the SAME TB_Material instance. " +
                        "Writing E would change both groups. Fix TB_Section.DeepCopy() to clone Mat, " +
                        "and build each section from its own material instance.");
            }
        }
    }
}
