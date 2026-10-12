using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino.Display;

using Propability_NTNU_v1;
using Propability_NTNU_v1.Classes.Toolbox;
using Propability_NTNU_v1.Classes.Reliability;

namespace Propability_NTNU_v1.Components.ToolboxComponents
{
    /// <summary>
    /// Shows, in the Rhino viewport, a label at the middle of every member with its cross-section,
    /// its group, its reliability index and its utilisation, and colours the members by
    /// cross-section (lightest blue to heaviest red), by beta, by utilisation or by group.
    ///
    /// Where the values come from:
    ///   section       the section of the element in the model
    ///   group         the Groups input if given, else the groups the model carries (RBDO / EC3
    ///                 Sections), else the element tag
    ///   beta, U       the Beta / Utilisation Values inputs if given, else what the model carries
    ///                 (RBDO Sections and EC3 Sections attach them to the optimised model they
    ///                 output), else not shown
    /// Nothing is computed here: the component only reads and draws.
    /// </summary>
    public class ST_Member_Info : GH_Component
    {
        // --- what is drawn ---
        private readonly List<Point3d> _pts = new List<Point3d>();
        private readonly List<string> _texts = new List<string>();
        private readonly List<Line> _lines = new List<Line>();
        private readonly List<Color> _colours = new List<Color>();
        private bool _colourLines;
        private int _textSize = 12;
        private BoundingBox _box = BoundingBox.Empty;

        private static readonly Color TextColour = Color.FromArgb(40, 40, 40);
        private static readonly Color NoValue = Color.FromArgb(150, 150, 150);
        private static readonly Color[] GroupPalette =
        {
            Color.FromArgb(78, 121, 167), Color.FromArgb(242, 142, 43), Color.FromArgb(225, 87, 89),
            Color.FromArgb(118, 183, 178), Color.FromArgb(89, 161, 79), Color.FromArgb(237, 201, 72),
            Color.FromArgb(176, 122, 161), Color.FromArgb(255, 157, 167), Color.FromArgb(156, 117, 95),
            Color.FromArgb(186, 176, 172)
        };
        private static readonly Color Red = Color.FromArgb(215, 48, 39);
        private static readonly Color Orange = Color.FromArgb(244, 165, 66);
        private static readonly Color Green = Color.FromArgb(26, 152, 80);
        private static readonly Color Blue = Color.FromArgb(69, 117, 180);

        public ST_Member_Info()
          : base("Member Info", "MemInfo",
              "Labels every member with its cross-section, group, reliability index and utilisation, and " +
              "colours the members by cross-section (lightest blue to heaviest red), beta, utilisation or group. " +
              "Reads the results that RBDO Sections and " +
              "EC3 Sections attach to the optimised model they output.",
              Common.category, Common.sub_post)
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddParameter(new Param_TB_Model(), "Model", "Model",
                "Model to label: the Model output of RBDO Sections (carries groups and betas) or EC3 Sections " +
                "(carries groups, utilisations, and betas if it was given random variables), or any other model.",
                GH_ParamAccess.item);
            pManager.AddBooleanParameter("Section", "Sec", "Show the cross-section name.", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Group", "Grp", "Show the group name.", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Beta", "B", "Show the reliability index (if known).", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Utilisation", "U", "Show the utilisation (if known).", GH_ParamAccess.item, true);
            pManager.AddIntegerParameter("Colour", "Col",
                "Colour the members: 0 = no colour (labels only), " +
                "1 = by cross-section, from blue (lightest) through light blue, green and orange to red (heaviest), " +
                "2 = by beta (red below the target, orange up to 0.5 above it, green above that), " +
                "3 = by utilisation (blue up to 0.5, green up to 0.9, orange up to 1.0, red above 1.0), " +
                "4 = by group. Grey: no value. The Legend outputs list what each colour means.", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Target Beta", "Bt",
                "Optional: target beta for the colours and the '<' mark. Default: the target the model carries " +
                "(RBDO / EC3 Sections), else 3.8.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Text Size", "Size", "Label height in pixels.", GH_ParamAccess.item, 12);
            pManager.AddTextParameter("Groups", "Grps",
                "Optional, one per line: \"<tag or tag*>:<group>\", as in RBDO / EC3 Sections. Overrides the groups " +
                "the model carries.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Beta Values", "Bv",
                "Optional: one beta per member, in model order (e.g. B of FORM Members). Overrides the model's.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("Utilisation Values", "Uv",
                "Optional: one utilisation per member, in model order (e.g. U of EC3 Members). Overrides the model's.",
                GH_ParamAccess.list);
            pManager.AddTextParameter("Section Scale", "Scale",
                "Optional: section names from lightest to heaviest that fix the colour scale of Colour = 1, e.g. " +
                "HEA100 ... HEA240. Give the same list to the Member Info of the EC3 and of the RBDO design so the " +
                "same section has the same colour in both. Default: the sections of this model, by area.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("Label Spacing", "Space",
                "Optional: smallest distance [model units] between two labels, and between a label and a point " +
                "where members cross (X-bracing). A label that would be closer is moved along its own member " +
                "(to 30 %, 70 %, 20 % or 80 % of its length). Default: a quarter of the median member length. " +
                "0 = every label at the middle of its member.", GH_ParamAccess.item);

            for (int i = 1; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Labels", "Lbl", "The label of each member, as drawn.", GH_ParamAccess.list);
            pManager.AddPointParameter("Points", "Pts", "Label position (member midpoint) of each member.", GH_ParamAccess.list);
            pManager.AddTextParameter("Sections", "Sec", "Cross-section of each member.", GH_ParamAccess.list);
            pManager.AddTextParameter("Groups", "Grp", "Group of each member.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Beta", "B", "Beta of each member (NaN where not known).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Utilisation", "U", "Utilisation of each member (NaN where not known).", GH_ParamAccess.list);
            pManager.AddColourParameter("Colours", "C", "Colour of each member for the chosen colour mode.", GH_ParamAccess.list);
            pManager.AddLineParameter("Lines", "L", "Member lines, e.g. for a custom preview or for baking with the colours.", GH_ParamAccess.list);
            pManager.AddTextParameter("Info", "Info", "Where the groups, betas and utilisations came from.", GH_ParamAccess.item);
            pManager.AddTextParameter("Legend", "Leg", "What each colour of the chosen colour mode means (e.g. the sections, lightest first).", GH_ParamAccess.list);
            pManager.AddColourParameter("Legend Colours", "LegC", "The colour of each Legend entry, e.g. for Grasshopper's Legend component.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            _pts.Clear(); _texts.Clear(); _lines.Clear(); _colours.Clear();
            _box = BoundingBox.Empty;

            // --- input ---
            GH_TB_Model gh_mdl = null;
            bool showSec = true, showGrp = true, showB = true, showU = true;
            int colourMode = 1, textSize = 12;
            double betaTargetIn = double.NaN;
            var groupSpecs = new List<string>();
            var betaIn = new List<double>();
            var utilIn = new List<double>();
            var scaleIn = new List<string>();

            if (!DA.GetData(0, ref gh_mdl)) { return; }
            DA.GetData(1, ref showSec);
            DA.GetData(2, ref showGrp);
            DA.GetData(3, ref showB);
            DA.GetData(4, ref showU);
            DA.GetData(5, ref colourMode);
            bool targetGiven = DA.GetData(6, ref betaTargetIn);
            DA.GetData(7, ref textSize);
            DA.GetDataList(8, groupSpecs);
            DA.GetDataList(9, betaIn);
            DA.GetDataList(10, utilIn);
            DA.GetDataList(11, scaleIn);
            double spacing = double.NaN;
            DA.GetData(12, ref spacing);

            if (gh_mdl == null || gh_mdl.Value == null || gh_mdl.Value.Elem1Ds == null || gh_mdl.Value.Elem1Ds.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Valid model required.");
                return;
            }
            TB_Model mdl = gh_mdl.Value;
            int n = mdl.Elem1Ds.Count;
            TB_MemberResults stored = mdl.Results;
            var info = new List<string>();

            // --- sections ---
            string[] sections = mdl.Elem1Ds.Select(e => e?.Sec?.Tag ?? "-").ToArray();

            // --- groups ---
            string[] groups;
            groupSpecs = groupSpecs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (groupSpecs.Count > 0)
            {
                try { groups = SectionOptimiser.GroupOfMembers(mdl, groupSpecs); }
                catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); return; }
                info.Add("groups: from the Groups input");
            }
            else if (stored != null && TB_MemberResults.Fits(stored.Group, n))
            {
                groups = stored.Group;
                info.Add("groups: " + stored.GroupSource);
            }
            else
            {
                groups = mdl.Elem1Ds.Select(e => e?.Tag ?? "").ToArray();
                info.Add("groups: none in the model, the element tags are shown");
            }

            // --- beta ---
            double[] beta = null;
            if (betaIn.Count > 0)
            {
                if (betaIn.Count != n)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Beta Values: {betaIn.Count} values for {n} members.");
                    return;
                }
                beta = betaIn.ToArray();
                info.Add("beta: from the Beta Values input");
            }
            else if (stored != null && TB_MemberResults.Fits(stored.Beta, n))
            {
                beta = stored.Beta;
                info.Add("beta: " + stored.BetaSource);
            }
            else info.Add("beta: none in the model");

            // --- utilisation ---
            double[] util = null;
            if (utilIn.Count > 0)
            {
                if (utilIn.Count != n)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Utilisation Values: {utilIn.Count} values for {n} members.");
                    return;
                }
                util = utilIn.ToArray();
                info.Add("utilisation: from the Utilisation Values input");
            }
            else if (stored != null && TB_MemberResults.Fits(stored.Utilisation, n))
            {
                util = stored.Utilisation;
                info.Add("utilisation: " + stored.UtilisationSource);
            }
            else info.Add("utilisation: none in the model");

            double betaTarget = targetGiven ? betaTargetIn
                              : (stored != null && !double.IsNaN(stored.BetaTarget)) ? stored.BetaTarget : 3.8;
            info.Add("target beta: " + betaTarget.ToString("0.0#", CultureInfo.InvariantCulture));

            // --- colours ---
            colourMode = Math.Max(0, Math.Min(4, colourMode));
            if (colourMode == 2 && beta == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Colour by beta: the model carries no betas (all grey).");
            if (colourMode == 3 && util == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Colour by utilisation: the model carries no utilisations (all grey).");

            var groupIndex = new Dictionary<string, int>();
            foreach (string g in groups)
                if (!groupIndex.ContainsKey(g ?? "")) groupIndex[g ?? ""] = groupIndex.Count;

            // section scale: lightest blue ... heaviest red, evenly spread over the sections on the scale
            List<string> scale;
            scaleIn = scaleIn.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
            if (scaleIn.Count > 0)
            {
                scale = scaleIn;
                var missing = sections.Distinct().Where(x => !scale.Contains(x)).ToList();
                if (missing.Count > 0 && colourMode == 1)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Section Scale does not contain " + string.Join(", ", missing) + " (shown grey).");
            }
            else
            {
                var area = new Dictionary<string, double>();
                for (int m = 0; m < n; m++)
                    if (!area.ContainsKey(sections[m])) area[sections[m]] = mdl.Elem1Ds[m]?.Sec?.Area ?? 0.0;
                scale = area.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();
            }
            var sectionColour = new Dictionary<string, Color>();
            for (int k = 0; k < scale.Count; k++)
                sectionColour[scale[k]] = Ramp(scale.Count == 1 ? 0.0 : (double)k / (scale.Count - 1));

            // --- labels: where, so that they do not sit on each other or on a crossing ---
            CultureInfo ci = CultureInfo.InvariantCulture;
            var points = new List<Point3d>();
            var memberLines = mdl.Elem1Ds.Select(e => (e.OriginalLine.Length > 1e-12) ? e.OriginalLine : e.Line).ToList();
            if (double.IsNaN(spacing) || spacing < 0.0)
            {
                var lengths = memberLines.Select(l => l.Length).OrderBy(x => x).ToList();
                spacing = 0.25 * lengths[lengths.Count / 2];
            }
            List<Point3d> anchors = PlaceLabels(memberLines, spacing);

            for (int m = 0; m < n; m++)
            {
                Line ln = memberLines[m];
                Point3d mid = anchors[m];

                var parts = new List<string>();
                if (showSec) parts.Add(sections[m]);
                if (showGrp) parts.Add(groups[m]);
                if (showB && beta != null)
                    parts.Add("\u03B2 " + (double.IsNaN(beta[m]) ? "-" : beta[m].ToString("F2", ci) + (beta[m] < betaTarget ? " <" : "")));
                if (showU && util != null)
                    parts.Add("U " + (double.IsNaN(util[m]) ? "-" : util[m].ToString("F2", ci) + (util[m] > 1.0 ? " >1" : "")));

                Color c;
                switch (colourMode)
                {
                    case 1: c = sectionColour.TryGetValue(sections[m], out Color sc) ? sc : NoValue; break;
                    case 2: c = BetaColour(beta, m, betaTarget); break;
                    case 3: c = UtilColour(util, m); break;
                    case 4: c = GroupPalette[groupIndex[groups[m] ?? ""] % GroupPalette.Length]; break;
                    default: c = NoValue; break;
                }

                _pts.Add(mid);
                _texts.Add(string.Join("\n", parts));
                _lines.Add(ln);
                _colours.Add(c);
                points.Add(ln.From);
                points.Add(ln.To);
            }
            _box = new BoundingBox(points);
            _colourLines = colourMode > 0;
            _textSize = Math.Max(6, Math.Min(72, textSize));

            int nBelow = (beta == null) ? 0 : beta.Count(b => !double.IsNaN(b) && b < betaTarget);
            int nOver = (util == null) ? 0 : util.Count(u => !double.IsNaN(u) && u > 1.0);
            var msg = new List<string>();
            if (beta != null) msg.Add(nBelow + " \u03B2 < " + betaTarget.ToString("0.0#", ci));
            if (util != null) msg.Add(nOver + " U > 1");
            Message = string.Join(", ", msg);

            // --- output ---
            DA.SetDataList(0, _texts);
            DA.SetDataList(1, _pts);
            DA.SetDataList(2, sections);
            DA.SetDataList(3, groups);
            DA.SetDataList(4, beta ?? Enumerable.Repeat(double.NaN, n).ToArray());
            DA.SetDataList(5, util ?? Enumerable.Repeat(double.NaN, n).ToArray());
            DA.SetDataList(6, _colours);
            DA.SetDataList(7, _lines);
            DA.SetData(8, string.Join("\n", info));

            // --- legend of the chosen colour mode ---
            var legend = new List<string>();
            var legendColours = new List<Color>();
            string bt = betaTarget.ToString("0.0#", ci), bt5 = (betaTarget + 0.5).ToString("0.0#", ci);
            switch (colourMode)
            {
                case 1:
                    foreach (string sname in scale) { legend.Add(sname); legendColours.Add(sectionColour[sname]); }
                    break;
                case 2:
                    legend.AddRange(new[] { "\u03B2 < " + bt, bt + " \u2264 \u03B2 < " + bt5, "\u03B2 \u2265 " + bt5, "no value" });
                    legendColours.AddRange(new[] { Red, Orange, Green, NoValue });
                    break;
                case 3:
                    legend.AddRange(new[] { "U \u2264 0.5", "0.5 < U \u2264 0.9", "0.9 < U \u2264 1.0", "U > 1.0", "no value" });
                    legendColours.AddRange(new[] { Blue, Green, Orange, Red, NoValue });
                    break;
                case 4:
                    foreach (KeyValuePair<string, int> g in groupIndex) { legend.Add(g.Key); legendColours.Add(GroupPalette[g.Value % GroupPalette.Length]); }
                    break;
            }
            DA.SetDataList(9, legend);
            DA.SetDataList(10, legendColours);
        }

        /// <summary>
        /// Label position of every member. Each label goes to the middle of its member unless that point
        /// is closer than 'spacing' to a label already placed or to a point where two members cross in
        /// their interiors (X-bracing); then it moves along its own member to 30 %, 70 %, 20 % or 80 %
        /// of the length, the first that is free (else the one with the most room).
        /// </summary>
        private static List<Point3d> PlaceLabels(List<Line> lines, double spacing)
        {
            int n = lines.Count;
            var anchors = new List<Point3d>(n);
            if (!(spacing > 0.0))
            {
                foreach (Line l in lines) anchors.Add(l.PointAt(0.5));
                return anchors;
            }

            // points where two members cross away from their ends
            double tol = 1e-3 * spacing;
            var crossings = new List<Point3d>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Point3d p;
                    if (CrossInInterior(lines[i], lines[j], tol, out p)) crossings.Add(p);
                }

            double[] candidates = { 0.5, 0.3, 0.7, 0.2, 0.8 };
            for (int m = 0; m < n; m++)
            {
                Point3d best = lines[m].PointAt(0.5);
                double bestRoom = double.NegativeInfinity;
                foreach (double t in candidates)
                {
                    Point3d p = lines[m].PointAt(t);
                    double room = double.PositiveInfinity;
                    foreach (Point3d q in anchors) room = Math.Min(room, p.DistanceTo(q));
                    foreach (Point3d q in crossings) room = Math.Min(room, p.DistanceTo(q));
                    if (room >= spacing) { best = p; break; }
                    if (room > bestRoom) { bestRoom = room; best = p; }
                }
                anchors.Add(best);
            }
            return anchors;
        }

        /// <summary>
        /// True if segments a and b pass within tol of each other at a point inside both (not near an end):
        /// the closest points of the two lines, parameters in (0.02, 0.98).
        /// </summary>
        private static bool CrossInInterior(Line a, Line b, double tol, out Point3d p)
        {
            p = a.PointAt(0.5);
            Vector3d d1 = a.To - a.From, d2 = b.To - b.From, r = a.From - b.From;
            double aa = d1 * d1, ee = d2 * d2, bb = d1 * d2, cc = d1 * r, ff = d2 * r;
            double denom = aa * ee - bb * bb;
            if (!(denom > 1e-12 * aa * ee)) return false;                     // parallel
            double s = (bb * ff - cc * ee) / denom;
            double t = (aa * ff - bb * cc) / denom;
            if (s <= 0.02 || s >= 0.98 || t <= 0.02 || t >= 0.98) return false;
            Point3d pa = a.PointAt(s), pb = b.PointAt(t);
            if (pa.DistanceTo(pb) > tol) return false;
            p = pa;
            return true;
        }

        /// <summary>Blue (t = 0), light blue, green, orange, red (t = 1), linear between the stops.</summary>
        private static Color Ramp(double t)
        {
            Color[] stops =
            {
                Color.FromArgb(33, 102, 172), Color.FromArgb(103, 169, 207), Color.FromArgb(77, 175, 74),
                Color.FromArgb(253, 141, 60), Color.FromArgb(215, 48, 39)
            };
            t = Math.Max(0.0, Math.Min(1.0, t)) * (stops.Length - 1);
            int i = Math.Min((int)Math.Floor(t), stops.Length - 2);
            double f = t - i;
            Color a = stops[i], b = stops[i + 1];
            return Color.FromArgb((int)Math.Round(a.R + f * (b.R - a.R)),
                                  (int)Math.Round(a.G + f * (b.G - a.G)),
                                  (int)Math.Round(a.B + f * (b.B - a.B)));
        }

        private static Color BetaColour(double[] beta, int m, double target)
        {
            if (beta == null || double.IsNaN(beta[m])) return NoValue;
            if (beta[m] < target) return Red;
            if (beta[m] < target + 0.5) return Orange;
            return Green;
        }

        private static Color UtilColour(double[] util, int m)
        {
            if (util == null || double.IsNaN(util[m])) return NoValue;
            if (util[m] > 1.0) return Red;
            if (util[m] > 0.9) return Orange;
            if (util[m] > 0.5) return Green;
            return Blue;
        }

        // --- drawing ---

        public override bool IsPreviewCapable => true;

        public override BoundingBox ClippingBox => _box;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            // the outputs (points, lines) are not drawn: the labels and coloured members are the preview
            if (_colourLines)
                for (int i = 0; i < _lines.Count; i++)
                    args.Display.DrawLine(_lines[i], _colours[i], 4);

            for (int i = 0; i < _pts.Count; i++)
                if (!string.IsNullOrEmpty(_texts[i]))
                    args.Display.Draw2dText(_texts[i], TextColour, _pts[i], true, _textSize);
        }

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("Info");

        public override Guid ComponentGuid => new Guid("5b0e3c1d-7f42-4a8e-9c61-2d4f8b9e7a13");
    }
}
