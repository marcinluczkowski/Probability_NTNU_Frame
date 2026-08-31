using System;
using System.Collections.Generic;
using System.Linq;

using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Classes.Reliability
{
    /// <summary>
    /// Bridges the FORM solver and the nonlinear FE model.
    ///
    ///   g(x) = threshold - u_resp(x)
    ///
    /// A single nonlinear solve serves BOTH the limit-state value and the DDM
    /// gradient, so the last evaluation is cached and reused when the solver asks
    /// for the gradient at the same x (which HL-RF always does).
    ///
    /// If the FE solve fails to converge - which happens when a trial point pushes
    /// the structure past its limit load - g is returned as double.NaN. The FORM
    /// line search treats NaN as "cannot evaluate here" and backtracks.
    /// </summary>
    public class NL_LimitState
    {
        private readonly TB_Model _baseline;
        private readonly NL_ModelBinding _binding;
        private readonly List<NL_Parameter> _pars;
        private readonly NL_Response _resp;
        private readonly double _threshold;

        private readonly int _loadSteps, _maxIter;
        private readonly double _tol;

        // cache
        private double[] _lastX;
        private double _lastG;
        private double[] _lastGradX;
        private bool _lastOk;

        public int Evaluations { get; private set; }
        public int FailedSolves { get; private set; }

        public NL_LimitState(TB_Model baseline,
                             List<NL_Parameter> pars,
                             NL_Response resp,
                             double threshold,
                             int loadSteps = 10,
                             int maxIter = 50,
                             double tol = 1e-9)
        {
            _baseline = baseline;
            _pars = pars;
            _resp = resp;
            _threshold = threshold;
            _loadSteps = loadSteps;
            _maxIter = maxIter;
            _tol = tol;

            _binding = new NL_ModelBinding(baseline, pars);
        }

        public double[] MeanPoint => _binding.Baseline;

        /// <summary>g(x). Returns NaN if the FE solve did not converge.</summary>
        public double G(double[] x)
        {
            EnsureEvaluated(x);
            return _lastOk ? _lastG : double.NaN;
        }

        /// <summary>dg/dx by DDM. Returns null if the FE solve did not converge.</summary>
        public double[] GradX(double[] x)
        {
            EnsureEvaluated(x);
            return _lastOk ? _lastGradX : null;
        }

        private void EnsureEvaluated(double[] x)
        {
            if (_lastX != null && _lastX.Length == x.Length)
            {
                bool same = true;
                for (int i = 0; i < x.Length; i++)
                    if (_lastX[i] != x[i]) { same = false; break; }
                if (same) return;
            }

            _lastX = (double[])x.Clone();
            Evaluations++;

            TB_Model work = _baseline.DeepCopy();
            _binding.Apply(work, x);
            NL_ModelBinding.ResetResults(work);

            SolveNL slv = new SolveNL(ref work, _loadSteps, _maxIter, _tol);
            if (!slv.Converged)
            {
                _lastOk = false;
                FailedSolves++;
                return;
            }

            double[] u = work.Disps[0];
            double uResp = u[6 * _resp.NodeId + _resp.Dof];
            _lastG = _threshold - uResp;

            NL_DDM ddm = new NL_DDM(work, u, _resp, _pars);
            if (!ddm.Ok) { _lastOk = false; FailedSolves++; return; }

            // g = threshold - u  =>  dg/dx = -du/dx
            _lastGradX = ddm.Gradient.Select(v => -v).ToArray();
            _lastOk = true;
        }

        /// <summary>
        /// Verifies that Apply() writes to the WORKING model and leaves the baseline
        /// untouched. Catches the aliasing failure mode where cached references or
        /// shallow copies cause updates to land in the wrong object.
        /// </summary>
        public string SelfCheck()
        {
            double[] x0 = (double[])_binding.Baseline.Clone();
            double[] x1 = x0.Select(v => v * 1.25 + (Math.Abs(v) < 1e-12 ? 1.0 : 0.0)).ToArray();

            // snapshot the baseline
            double[] before = Snapshot(_baseline);

            TB_Model work = _baseline.DeepCopy();
            _binding.Apply(work, x1);

            double[] after = Snapshot(_baseline);
            double[] workVals = Snapshot(work);

            var problems = new List<string>();

            for (int i = 0; i < before.Length; i++)
            {
                if (Math.Abs(before[i] - after[i]) > 1e-9 * Math.Max(1.0, Math.Abs(before[i])))
                    problems.Add($"  FAIL: baseline '{_pars[i].Name}' was modified " +
                                 $"({before[i]:G6} -> {after[i]:G6}). Apply() is writing to the wrong model.");

                if (Math.Abs(workVals[i] - x1[i]) > 1e-6 * Math.Max(1.0, Math.Abs(x1[i])))
                    problems.Add($"  FAIL: working model '{_pars[i].Name}' is {workVals[i]:G6}, " +
                                 $"expected {x1[i]:G6}. Apply() did not take effect.");
            }

            if (problems.Count == 0)
                return $"Binding OK: all {_pars.Count} parameters wrote to the working model " +
                       "and the baseline is unchanged.";

            return "BINDING PROBLEMS:\n" + string.Join("\n", problems.Take(10));
        }

        /// <summary>Reads the current value of every parameter out of a model.</summary>
        private double[] Snapshot(TB_Model m)
        {
            var vals = new double[_pars.Count];
            for (int p = 0; p < _pars.Count; p++)
            {
                NL_Parameter par = _pars[p];
                switch (par.Type)
                {
                    case NL_ParamType.Modulus:
                        {
                            var e = m.Elem1Ds.FirstOrDefault(el =>
                                string.IsNullOrEmpty(par.SectionTag) ||
                                string.Equals(el.Sec.Tag, par.SectionTag, StringComparison.OrdinalIgnoreCase));
                            vals[p] = e?.Sec.Mat.E ?? double.NaN;
                            break;
                        }
                    case NL_ParamType.NodeCoordinate:
                        {
                            var n = m.Nodes.FirstOrDefault(nd => nd.Id.Value == par.NodeId);
                            vals[p] = n == null ? double.NaN
                                : (par.Component == 0 ? n.Pt.X : par.Component == 1 ? n.Pt.Y : n.Pt.Z);
                            break;
                        }
                    case NL_ParamType.PointLoad:
                        {
                            var l = m.Loads.OfType<TB_Load_Point>()
                                     .FirstOrDefault(q => q.Node != null && q.Node.Id.Value == par.NodeId);
                            vals[p] = l == null ? double.NaN : l.Loads[par.Dof] / par.Scale;
                            break;
                        }
                }
            }
            return vals;
        }

        // ------------------------------------------------------------------
        //  Binding spec parser
        // ------------------------------------------------------------------

        /// <summary>
        /// Parses a compact binding string into an NL_Parameter.
        ///
        ///   "E:STRUT"       modulus of every element with section tag STRUT
        ///   "X:1:0"         original X coordinate of node 1   (comp 0=X 1=Y 2=Z)
        ///   "F:5:0"         point load at node 5, DOF 0
        ///   "F:10:2:-1"     point load at node 10, DOF 2, applied in the -Z sense
        /// </summary>
        public static NL_Parameter ParseBinding(string spec, string name)
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new ArgumentException("Empty binding spec.");

            string[] t = spec.Split(':');
            string kind = t[0].Trim().ToUpperInvariant();

            switch (kind)
            {
                case "E":
                    if (t.Length < 2) throw new ArgumentException($"'{spec}': expected E:<sectionTag>");
                    return NL_Parameter.ForModulus(name, t[1].Trim());

                case "X":
                    if (t.Length < 3) throw new ArgumentException($"'{spec}': expected X:<nodeId>:<comp>");
                    return NL_Parameter.ForCoordinate(name, int.Parse(t[1]), int.Parse(t[2]));

                case "F":
                    if (t.Length < 3) throw new ArgumentException($"'{spec}': expected F:<nodeId>:<dof>[:<scale>]");
                    double sc = (t.Length > 3) ? double.Parse(t[3],
                        System.Globalization.CultureInfo.InvariantCulture) : 1.0;
                    return NL_Parameter.ForLoad(name, int.Parse(t[1]), int.Parse(t[2]), sc);

                default:
                    throw new ArgumentException($"'{spec}': unknown binding kind '{kind}'. Use E, X or F.");
            }
        }
    }
}
