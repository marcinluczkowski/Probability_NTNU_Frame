using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;
using CSparse.Storage;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    [Serializable]
    public class TB_Model
    {
        // --- field ---
        public List<TB_Element_1D> Elem1Ds { get; private set; } = new List<TB_Element_1D>();
        public List<TB_Support> Sups { get; private set; } = new List<TB_Support>();
        public List<TB_Load> Loads { get; private set; } = new List<TB_Load>();
        public int[] LCs { get; private set; } = Array.Empty<int>();
        public int? SelectedLC { get; set; } = null;
        public double Weight { get; private set; }
        public BoundingBox Bbox { get; private set; }
        public List<Node> Nodes { get; private set; }
        public List<bool> Validity { get; private set; } = new List<bool>();
        public DenseMatrix KG { get; set; }
        public DenseMatrix LM { get; set; }
        public List<double[]> Disps { get; private set; } = new List<double[]>();
        /// <summary>Results per member (group, beta, utilisation) written by the analysis components; null if none.</summary>
        public TB_MemberResults Results { get; set; }

        // --- constructors --- 
        public TB_Model() { }
        public TB_Model(List<TB_Element_1D> _elem1Ds, List<TB_Support> _sups, List<TB_Load> _loads)
        {
            Elem1Ds = _elem1Ds ?? new List<TB_Element_1D>();
            Sups = _sups ?? new List<TB_Support>();
            Loads = _loads ?? new List<TB_Load>();

            Weight = Calc_Weight();

            Bbox = CreateBBox();
            Nodes = CreateNodes_SetElemIds();

            Validity.Add(CheckSupports());
            Validity.Add(CheckLoads());
        }

        // --- methods --

        /// <summary>The message the nonlinear analysis gives for a model with truss elements.</summary>
        public const string TrussNonlinearMessage =
            "Truss elements (Hinges) are supported in the linear analysis only (Solve Linear Static, FORM Members, " +
            "EC3 Members, RBDO Sections, EC3 Sections). Use Solve Linear Static, or rigid elements here.";

        /// <summary>Number of truss elements (Hinges = true).</summary>
        public int TrussCount => Elem1Ds?.Count(e => e != null && e.Hinges) ?? 0;

        /// <summary>
        /// The nodes where only truss elements meet. Trusses do not resist the rotation of their nodes,
        /// so nothing holds these nodes' rotations; as in Karamba3D, those rotations are removed from
        /// the analysis (ConstrainedDofs) and are zero in the results.
        /// </summary>
        public List<Node> TrussOnlyNodes()
        {
            if (Nodes == null) return new List<Node>();
            return Nodes.Where(n => n.Elems != null && n.Elems.Count > 0 && n.Elems.All(e => e.Hinges)).ToList();
        }

        /// <summary>
        /// The degrees of freedom held fixed in the analysis, 6 per node (ux uy uz rx ry rz): those of
        /// the supports, and the rotations of the nodes where only trusses meet. Every linear solve
        /// (SolveLS, LS_DDM and with it FORM, EC3, the optimisers and the displacements) uses this list.
        /// </summary>
        public bool[] ConstrainedDofs(int nDof = 6)
        {
            bool[] fixedDof = new bool[(Nodes?.Count ?? 0) * nDof];
            if (Nodes == null) return fixedDof;

            foreach (Node n in Nodes)
            {
                if (n.Sup == null || n.Sup.Conditions == null || !n.Id.HasValue) continue;
                for (int i = 0; i < nDof && i < n.Sup.Conditions.Count; i++)
                    if (n.Sup.Conditions[i]) fixedDof[nDof * n.Id.Value + i] = true;
            }
            foreach (Node n in TrussOnlyNodes())
            {
                if (!n.Id.HasValue) continue;
                for (int i = 3; i < 6 && i < nDof; i++) fixedDof[nDof * n.Id.Value + i] = true;
            }
            return fixedDof;
        }

        /// <summary>"N3 uz" for degree of freedom 2 of node 3.</summary>
        public static string DofName(int nodeId, int dof)
        {
            string[] names = { "ux", "uy", "uz", "rx", "ry", "rz" };
            return "N" + nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                   ((dof >= 0 && dof < names.Length) ? names[dof] : "dof " + dof.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Free degrees of freedom that nothing holds, i.e. where the structure is a mechanism ("kinematic"
        /// in Karamba3D), as "N3 uy" etc. Empty if the structure is stable. A typical case with trusses: a
        /// planar truss whose nodes are not held out of plane, or a node with too few members. The check
        /// assembles the stiffness of the elements as they are and eliminates it with complete pivoting
        /// (scaled to a unit diagonal); a degree of freedom without a pivot belongs to a mechanism. For a
        /// mechanism that involves several nodes, one of their degrees of freedom is named.
        /// </summary>
        public List<string> FindMechanism(int maxReported = 6)
        {
            const int nDof = 6;
            var found = new List<string>();
            if (Nodes == null || Nodes.Count == 0 || Elem1Ds == null) return found;

            int n = Nodes.Count * nDof;
            bool[] fixedDof = ConstrainedDofs(nDof);
            double[,] k = new double[n, n];
            foreach (TB_Element_1D e in Elem1Ds)
            {
                if (e?.EKG == null || e.Nodes == null || e.Nodes.Count < 2) continue;
                int[] dofs = new int[12];
                for (int i = 0; i < nDof; i++)
                {
                    dofs[i] = nDof * e.Nodes[0].Id.Value + i;
                    dofs[nDof + i] = nDof * e.Nodes[1].Id.Value + i;
                }
                for (int i = 0; i < 12; i++)
                    for (int j = 0; j < 12; j++)
                        k[dofs[i], dofs[j]] += e.EKG[i, j];
            }

            // free degrees of freedom; one with no stiffness at all is a mechanism by itself
            var free = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (fixedDof[i]) continue;
                if (k[i, i] > 0.0) free.Add(i);
                else found.Add(DofName(i / nDof, i % nDof));
            }

            // the rest: complete pivoting on the stiffness scaled to a unit diagonal
            int m = free.Count;
            double[,] a = new double[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    a[i, j] = k[free[i], free[j]] / Math.Sqrt(k[free[i], free[i]] * k[free[j], free[j]]);

            int[] perm = Enumerable.Range(0, m).ToArray();
            const double tol = 1e-11;
            int rank = 0;
            for (; rank < m; rank++)
            {
                int pr = rank, pc = rank;
                double best = 0.0;
                for (int i = rank; i < m; i++)
                    for (int j = rank; j < m; j++)
                        if (Math.Abs(a[i, j]) > best) { best = Math.Abs(a[i, j]); pr = i; pc = j; }
                if (best < tol) break;

                // bring the pivot to (rank, rank); the column order names the degrees of freedom
                SwapRows(a, rank, pr, m);
                SwapCols(a, rank, pc, m);
                int t = perm[rank]; perm[rank] = perm[pc]; perm[pc] = t;

                double piv = a[rank, rank];
                for (int i = rank + 1; i < m; i++)
                {
                    double f = a[i, rank] / piv;
                    if (f == 0.0) continue;
                    for (int j = rank; j < m; j++) a[i, j] -= f * a[rank, j];
                }
            }
            for (int i = rank; i < m; i++)
                found.Add(DofName(free[perm[i]] / nDof, free[perm[i]] % nDof));

            if (found.Count > maxReported)
                found = found.Take(maxReported).Concat(new[] { "(" + (found.Count - maxReported).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more)" }).ToList();
            return found;
        }

        private static void SwapRows(double[,] a, int r1, int r2, int m)
        {
            if (r1 == r2) return;
            for (int j = 0; j < m; j++) { double t = a[r1, j]; a[r1, j] = a[r2, j]; a[r2, j] = t; }
        }

        private static void SwapCols(double[,] a, int c1, int c2, int m)
        {
            if (c1 == c2) return;
            for (int i = 0; i < m; i++) { double t = a[i, c1]; a[i, c1] = a[i, c2]; a[i, c2] = t; }
        }

        /// <summary>
        /// One sentence for an error message: the degrees of freedom where the structure is a mechanism,
        /// or an empty string if FindMechanism finds none.
        /// </summary>
        public string MechanismMessage()
        {
            List<string> dofs = FindMechanism();
            if (dofs.Count == 0) return "";
            return "The structure is a mechanism (kinematic): nothing holds " + string.Join(", ", dofs) +
                   (TrussCount > 0
                       ? ". Truss elements (Hinges) carry axial force only: every node needs supports or members that hold it in all three directions (in a planar truss, a support out of plane at every node)."
                       : ". Add supports or members there.");
        }

        private double Calc_Weight()
        {
            double weight = 0.0;
            
            foreach (TB_Element_1D e in Elem1Ds)
            {
                weight += e.Weight;
            }

            return weight;
        }

        private List<Node> CreateNodes_SetElemIds()
        {
            int cnt_elemid = 0;
            List<Node> nodes = new List<Node>();
            foreach (TB_Element_1D elem in Elem1Ds)
            {
                NodeCheckAndRegister(elem, ref nodes);
                elem.Id = cnt_elemid;
                cnt_elemid++;
            }

            return nodes;
        }

        private void NodeCheckAndRegister(TB_Element_1D _elem, ref List<Node> _nodes)
        {
            _elem.Nodes.Clear();

            List<Point3d> pts = new List<Point3d>(2) 
                                    { _elem.Line.From, _elem.Line.To };

            foreach (Point3d p in pts)
            {
                Node nd = Node.FindNode(p, _nodes, Bbox);

                if (nd == null)
                {
                    nd = new Node(p, _nodes.Count, Bbox);
                    _nodes.Add(nd);

                }

                nd.Elems.Add(_elem);

                _elem.Nodes.Add(nd);
            }

        }

        private BoundingBox CreateBBox()
        {
            BoundingBox bb = new BoundingBox();
            IEnumerable<Line> lns = Elem1Ds.Select(x => x.Line);
            foreach (Line l in lns)
            {
                bb.Union(l.From);
                bb.Union(l.To);
            }

            return bb;
        }

        private bool CheckSupports()
        {
            foreach (TB_Support s in Sups)
            {
                Node nd = Node.FindNode(s.Pt, Nodes, Bbox);

                if (nd == null)
                {
                    return false;
                }

                else
                {
                    s.Node = nd;
                    nd.Sup = s;
                }
            }

            return true;
        }

        private bool CheckLoads()
        {
            if (Loads == null || Loads.Count == 0)
            {
                LCs = Array.Empty<int>();
                return true;
            }

            double diag = Bbox.Diagonal.Length;
            double snapTol = Math.Max(Common.LOAD_SNAP_TOL, diag * 0.01);

            foreach (TB_Load l in Loads)
            {
                if (!(l is TB_Load_Point pl)) continue;

                Node nd = Node.FindNode(pl.Pt, Nodes, Bbox);
                if (nd == null)
                    nd = Node.FindNearestNode(pl.Pt, Nodes, snapTol);

                if (nd == null)
                    return false;

                pl.Node = nd;
            }

            LCs = Loads
                .Where(x => x.Lc.HasValue)
                .Select(x => x.Lc.Value)
                .Distinct()
                .ToArray();

            return true;
        }

        public TB_Model DeepCopy()
        {
            var elemCopies = Elem1Ds?.Select(e => e?.DeepCopy()).Where(e => e != null).ToList() ?? new List<TB_Element_1D>();
            var supCopies = Sups?.Select(s => s?.DeepCopy()).Where(s => s != null).ToList() ?? new List<TB_Support>();
            var loadCopies = Loads?.Select(l => l?.DeepCopy()).Where(l => l != null).ToList() ?? new List<TB_Load>();

            var copy = new TB_Model(elemCopies, supCopies, loadCopies)
            {
                SelectedLC = SelectedLC,
                KG = KG != null ? (DenseMatrix)KG.Clone() : null,
                LM = LM != null ? (DenseMatrix)LM.Clone() : null,
                Results = Results?.Clone()
            };

            copy.Disps.Clear();
            if (Disps != null)
            {
                foreach (var d in Disps)
                {
                    copy.Disps.Add(d != null ? (double[])d.Clone() : null);
                }
            }

            if (Nodes != null && copy.Nodes != null)
            {
                int nodeCount = Math.Min(Nodes.Count, copy.Nodes.Count);
                for (int i = 0; i < nodeCount; i++)
                {
                    copy.Nodes[i].Disps.Clear();
                    if (Nodes[i].Disps == null) continue;
                    foreach (var d in Nodes[i].Disps)
                    {
                        copy.Nodes[i].Disps.Add(d != null ? (double[])d.Clone() : null);
                    }
                }
            }

            if (Sups != null && copy.Sups != null)
            {
                int supCount = Math.Min(Sups.Count, copy.Sups.Count);
                for (int i = 0; i < supCount; i++)
                {
                    copy.Sups[i].React.Clear();
                    if (Sups[i].React == null) continue;
                    foreach (var r in Sups[i].React)
                    {
                        copy.Sups[i].React.Add(r != null ? new List<double>(r) : new List<double>());
                    }
                }
            }

            return copy;
        }

        public override string ToString()
        {
            if (Nodes == null)
                return "Model (not assembled)";

            int nSup = Sups?.Count ?? 0;
            int nLd = Loads?.Count ?? 0;
            var sb = new StringBuilder();
            sb.Append("Model | Nodes: ").Append(Nodes.Count);
            sb.Append(" | Supports: ").Append(nSup);
            sb.Append(" | Loads: ").Append(nLd);

            int nTruss = TrussCount;
            if (nTruss > 0)
            {
                int nElem = Elem1Ds?.Count ?? 0;
                List<Node> trussNodes = TrussOnlyNodes();
                sb.AppendLine();
                sb.Append("Truss elements (Hinges): ").Append(nTruss).Append(" of ").Append(nElem)
                  .Append(nTruss == nElem ? " (all)" : "").Append(", axial force only.");
                if (trussNodes.Count > 0)
                {
                    sb.AppendLine();
                    sb.Append("Rotations removed at ").Append(trussNodes.Count)
                      .Append(" node(s) where only trusses meet: ")
                      .Append(string.Join(", ", trussNodes.Select(FormatNodeLabel)));
                }
            }

            if (nSup > 0)
            {
                sb.AppendLine();
                sb.Append("Supports:");
                foreach (var s in Sups)
                {
                    if (s == null) continue;
                    string nid = FormatNodeLabel(s.Node);
                    string val = FormatSupportConditions(s);
                    sb.AppendLine();
                    sb.Append("  ").Append(nid).Append(" : support : ").Append(val);
                }
            }

            if (nLd > 0)
            {
                sb.AppendLine();
                sb.Append("Loads:");
                foreach (var l in Loads)
                {
                    if (l == null) continue;
                    string nid = l is TB_Load_Point pl ? FormatNodeLabel(pl.Node) : "N?";
                    string typ = l.LoadType();
                    if (string.IsNullOrEmpty(typ)) typ = "load";
                    string val = FormatLoadValue(l);
                    sb.AppendLine();
                    sb.Append("  ").Append(nid).Append(" : ").Append(typ).Append(" : ").Append(val);
                }
            }

            return sb.ToString();
        }

        private static string FormatNodeLabel(Node nd)
        {
            if (nd == null) return "N?";
            if (nd.Id.HasValue) return "N" + nd.Id.Value.ToString();
            return "N?";
        }

        private static string FormatSupportConditions(TB_Support s)
        {
            if (s?.Conditions == null || s.Conditions.Count != 6)
                return "(invalid conditions)";
            var chars = new char[6];
            for (int i = 0; i < 6; i++)
                chars[i] = s.Conditions[i] ? '1' : '0';
            return new string(chars) + " (ux,uy,uz,rx,ry,rz)";
        }

        private static string FormatLoadValue(TB_Load l)
        {
            if (l is TB_Load_Point pl && pl.Loads != null && pl.Loads.Count >= 6)
            {
                int lc = pl.Lc ?? 0;
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "LC{0} F[{1:F2},{2:F2},{3:F2}]kN M[{4:F2},{5:F2},{6:F2}]kNm",
                    lc, pl.Loads[0], pl.Loads[1], pl.Loads[2], pl.Loads[3], pl.Loads[4], pl.Loads[5]);
            }
            return l?.ToString() ?? "—";
        }
        public bool IsValid()
        {

            return (Elem1Ds != null) && (Sups != null) && (Loads != null);
        }
    }

    public class GH_TB_Model : GH_Goo<TB_Model>
    {
        public GH_TB_Model() { }
        public GH_TB_Model(GH_TB_Model other) : base(other.Value)
        {
            this.Value = other.Value.DeepCopy();
        }
        public GH_TB_Model(TB_Model mdl) : base(mdl)
        {
            this.Value = mdl;
        }
        public override bool IsValid => base.m_value.IsValid();
        public override string TypeName => "Model";
        public override string TypeDescription => "Model";
        public override IGH_Goo Duplicate()
        {
            return new GH_TB_Model(this);
        }
        public override string ToString()
        {
            return Value.ToString();
        }
    }

    public class Param_TB_Model : GH_PersistentParam<GH_TB_Model>
    {
        public Param_TB_Model() : base(
            new GH_InstanceDescription(
                "Model", "Model", "Model 1D", Common.category, Common.sub_param
                )
            )
        { }

        public override Guid ComponentGuid => new Guid("d05c38a2-1bab-47b2-bd04-d5f28a5d9a5d");

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("M");

        protected override GH_GetterResult Prompt_Plural(ref List<GH_TB_Model> values)
        {
            return GH_GetterResult.success;
        }

        protected override GH_GetterResult Prompt_Singular(ref GH_TB_Model value)
        {
            return GH_GetterResult.success;
        }


    }


}
