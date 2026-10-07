// =============================================================================
//  ST_SectionTable.cs
//
//  WHAT IT IS
//  Reader for cross-section tables in the Karamba3D text format (semicolon-
//  separated: a header row, a units row, then one row per profile).
//
//  WHY IT MATTERS
//  The probabilistic section optimisation is compared against Karamba3D's
//  cross-section optimiser. That comparison is only fair if both tools use the
//  SAME section properties. This file is the single place where Karamba's numbers
//  enter the plugin and are converted to the plugin's units (mm, mm2, mm3, mm4),
//  so a unit or column mistake can only happen - and be fixed - here.
//
//  USED BY
//  HEA_Catalog.cs (parses the embedded HEA rows). Any further family (HEB, IPE,
//  RHS ...) can reuse it by pasting Karamba's rows into a new catalog class.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    /// <summary>
    /// One profile of a cross-section table, already converted to plugin units:
    /// lengths [mm], areas [mm2], section moduli [mm3], second moments [mm4], warping [mm6].
    /// </summary>
    public class SectionRecord
    {
        // --- field ---
        public string Country = "", Family = "", Name = "", Shape = "";
        public bool Obsolete;

        public double H, Tw, BUpper, TUpper, BLower, TLower, R;
        public double A, Ay, Az;
        public double Iy, WyUpper, WyLower, Wply, iy;
        public double Iz, Wz, Wplz, iz;
        public double It, Cw;
        /// <summary>EN 1993-1-1 imperfection factors: buckling about y, about z, lateral-torsional.</summary>
        public double AlphaY, AlphaZ, AlphaLT;

        // --- methods ---
        /// <summary>Governing elastic section modulus about y.</summary>
        public double Wy => Math.Min(Math.Abs(WyUpper), Math.Abs(WyLower));

        public override string ToString() => $"{Name} ({Country} {Family})";
    }

    /// <summary>
    /// Parses Karamba3D cross-section table text into <see cref="SectionRecord"/>s.
    /// Columns are located BY NAME from the header row and units are taken from the
    /// units row, so the reader does not depend on the column order. Accepts raw csv
    /// lines, quoted cells, and lines copied from a Grasshopper panel ("12. ;;...").
    /// </summary>
    public static class SectionTable
    {
        // --- field ---
        private static readonly Regex PanelIndex = new Regex(@"^\s*\d+\.\s+(?=[""';#])");

        // --- methods ---
        public static List<SectionRecord> Parse(IEnumerable<string> lines, out List<string> warnings)
        {
            warnings = new List<string>();
            var records = new List<SectionRecord>();
            if (lines == null) return records;

            Dictionary<string, int> col = null;
            string[] units = null;
            int nSkipped = 0;

            // a single panel item may hold the whole table as multi-line text
            foreach (string raw in lines.Where(l => l != null)
                                        .SelectMany(l => l.Split(new[] { '\r', '\n' },
                                                         StringSplitOptions.RemoveEmptyEntries)))
            {
                string line = PanelIndex.Replace(raw, "");
                if (line.IndexOf(';') < 0) continue;

                string[] c = line.Split(';').Select(Clean).ToArray();

                // header row: the first row that names both "name" and "A"
                if (col == null)
                {
                    if (c.Any(x => x.Equals("name", StringComparison.OrdinalIgnoreCase)) &&
                        c.Any(x => x == "A"))
                    {
                        col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < c.Length; i++)
                            if (c[i].Length > 0 && !col.ContainsKey(c[i])) col[c[i]] = i;
                    }
                    continue;
                }

                // any further row starting with '#' is the units row (or a comment)
                if (c[0].StartsWith("#"))
                {
                    if (units == null) units = c;
                    continue;
                }

                string name = Text(c, col, "name");
                if (name.Length == 0) { nSkipped++; continue; }

                try
                {
                    var r = new SectionRecord
                    {
                        Name = name,
                        Country = Text(c, col, "country"),
                        Family = Text(c, col, "family"),
                        Shape = Text(c, col, "shape"),
                        Obsolete = Text(c, col, "obsolete").Length > 0,

                        H = Num(c, col, units, "h", "mm"),
                        Tw = Num(c, col, units, "t_web", "mm"),
                        BUpper = Num(c, col, units, "b_upper", "mm"),
                        TUpper = Num(c, col, units, "t_upper", "mm"),
                        BLower = Num(c, col, units, "b_lower", "mm"),
                        TLower = Num(c, col, units, "t_lower", "mm"),
                        R = Num(c, col, units, "r", "mm"),

                        A = Num(c, col, units, "A", "cm2"),
                        Ay = Num(c, col, units, "A_y", "cm2"),
                        Az = Num(c, col, units, "A_z", "cm2"),

                        Iy = Num(c, col, units, "Iy", "cm4"),
                        WyUpper = Num(c, col, units, "Wy_u", "cm3"),
                        WyLower = Num(c, col, units, "Wy_l", "cm3"),
                        Wply = Num(c, col, units, "Wply", "cm3"),
                        iy = Num(c, col, units, "i_y", "cm"),

                        Iz = Num(c, col, units, "Iz", "cm4"),
                        Wz = Math.Abs(Num(c, col, units, "Wz", "cm3")),
                        Wplz = Num(c, col, units, "Wplz", "cm3"),
                        iz = Num(c, col, units, "i_z", "cm"),

                        It = Num(c, col, units, "It", "cm4"),
                        Cw = Num(c, col, units, "Cw", "cm6"),

                        AlphaY = Num(c, col, units, "alpha_y", "-"),
                        AlphaZ = Num(c, col, units, "alpha_z", "-"),
                        AlphaLT = Num(c, col, units, "alpha_lt", "-"),
                    };

                    if (r.A <= 0 || r.Iy <= 0 || r.Iz <= 0) { nSkipped++; continue; }
                    records.Add(r);
                }
                catch (FormatException ex)
                {
                    warnings.Add($"'{name}': {ex.Message}");
                }
            }

            if (col == null)
                warnings.Add("No header row found (expected a row containing the columns \"name\" and \"A\").");
            if (nSkipped > 0)
                warnings.Add($"{nSkipped} row(s) skipped (no name, or A/Iy/Iz not positive).");

            return records;
        }

        private static string Clean(string s) => s.Trim().Trim('"', '\'').Trim();

        private static string Text(string[] c, Dictionary<string, int> col, string key)
            => col.TryGetValue(key, out int i) && i < c.Length ? c[i] : "";

        /// <summary>Reads a number and converts it to mm-based units.</summary>
        private static double Num(string[] c, Dictionary<string, int> col, string[] units,
                                  string key, string defaultUnit)
        {
            if (!col.TryGetValue(key, out int i) || i >= c.Length || c[i].Length == 0) return 0.0;

            if (!double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException($"column '{key}' holds '{c[i]}', which is not a number.");

            string unit = (units != null && i < units.Length && units[i].Length > 0) ? units[i] : defaultUnit;
            return v * ToMm(unit, key);
        }

        private static double ToMm(string unit, string key)
        {
            switch (unit.ToLowerInvariant())
            {
                case "-": case "": return 1.0;
                case "mm": case "mm2": case "mm3": case "mm4": case "mm6": return 1.0;
                case "cm": return 1e1;
                case "cm2": return 1e2;
                case "cm3": return 1e3;
                case "cm4": return 1e4;
                case "cm6": return 1e6;
                case "m": return 1e3;
                case "m2": return 1e6;
                case "m3": return 1e9;
                case "m4": return 1e12;
                case "m6": return 1e18;
            }
            throw new FormatException($"column '{key}' has unit '{unit}', which is not supported.");
        }
    }
}
