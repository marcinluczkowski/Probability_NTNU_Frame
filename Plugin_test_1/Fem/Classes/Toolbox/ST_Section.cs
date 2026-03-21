using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Propability_NTNU_v1;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    [Serializable]
    public class TB_Section
    {
        public string Tag { get; protected set; } = "N/A";
        public TB_Material Mat { get; protected set; }
        public string Type { get; protected set; }
        public double Area { get; protected set; }
        public double Iy { get; protected set; }
        public double Iz { get; protected set; }
        public double J { get; protected set; }
        public double Wy { get; protected set; }
        public double Wz { get; protected set; }
        public double Wpy { get; protected set; }
        public double Wpz { get; protected set; }
        public double Theta { get; set; }
        public List<Curve> Curves { get; protected set; } = new List<Curve>();
        public List<Point3d> Pts { get; protected set; } = new List<Point3d>();

        public TB_Section() { }

        public TB_Section DeepCopy() => (TB_Section)MemberwiseClone();

        public virtual string GetDims() => "";
        public override string ToString() => $"Cross-Section, {Type}, {Tag}, {Mat?.Tag}";
        public bool IsValid() => Tag != "N/A";
    }

    /// <summary>RHS (Rectangular Hollow Section) — used by RHS Catalog.</summary>
    [Serializable]
    public class Section_RHS : TB_Section
    {
        public double H { get; private set; }
        public double W { get; private set; }
        public double Tw { get; private set; }
        public double Tf { get; private set; }

        public Section_RHS() { }
        public Section_RHS(TB_Material mat, string tag, double h, double w, double tw, double tf, double theta = 0)
        {
            H = h; W = w; Tw = tw; Tf = tf;
            Tag = tag; Mat = mat; Theta = theta; Type = "RHS";
            Area = W * H - (W - 2.0 * Tw) * (H - 2.0 * Tf);
            Iy = (W * Math.Pow(H, 3) - (W - 2.0 * Tw) * Math.Pow(H - 2.0 * Tf, 3)) / 12.0;
            Iz = (H * Math.Pow(W, 3) - (H - 2.0 * Tf) * Math.Pow(W - 2.0 * Tw, 3)) / 12.0;
            Wy = Iy / (0.5 * H); Wz = Iz / (0.5 * W);
            Wpy = W * Tf * (H - Tf) + Math.Pow(H - 2 * Tf, 2) * Tw / 2;
            Wpz = H * Tw * (W - Tw) + Math.Pow(W - 2 * Tw, 2) * Tf / 2;
            J = 2.0 * Math.Pow((W - Tw) * (H - Tf), 2) / ((W - Tw) / Tf + (H - Tf) / Tw);
            Curves.AddRange(DrawCurves());
        }

        private List<Curve> DrawCurves()
        {
            var crvs = new List<Curve>();
            var pts = new[] {
                new Point3d(-0.5*W*0.001, 0.5*H*0.001, 0),
                new Point3d(0.5*W*0.001, 0.5*H*0.001, 0),
                new Point3d(0.5*W*0.001, -0.5*H*0.001, 0),
                new Point3d(-0.5*W*0.001, -0.5*H*0.001, 0),
                new Point3d(-0.5*W*0.001, 0.5*H*0.001, 0)
            };
            Pts = new List<Point3d>(pts).GetRange(0, 4);
            crvs.Add(new Polyline(pts).ToNurbsCurve());
            var pts2 = new[] {
                new Point3d((-0.5*W+Tw)*0.001, (0.5*H-Tf)*0.001, 0),
                new Point3d((0.5*W-Tw)*0.001, (0.5*H-Tf)*0.001, 0),
                new Point3d((0.5*W-Tw)*0.001, (-0.5*H+Tf)*0.001, 0),
                new Point3d((-0.5*W+Tw)*0.001, (-0.5*H+Tf)*0.001, 0),
                new Point3d((-0.5*W+Tw)*0.001, (0.5*H-Tf)*0.001, 0)
            };
            crvs.Add(new Polyline(pts2).ToNurbsCurve());
            return crvs;
        }
        public override string GetDims() => $"{Type}, H={H}, W={W}, tw={Tw}, tf={Tf}";
    }

    /// <summary>Custom section — user provides Area, Iy, Iz, J manually [mm², mm⁴, mm⁴, mm⁴].</summary>
    [Serializable]
    public class Section_Custom : TB_Section
    {
        public Section_Custom() { }
        public Section_Custom(TB_Material mat, string tag, double area, double iy, double iz, double j,
            double wy = 0, double wz = 0)
        {
            Tag = tag; Mat = mat; Type = "Custom";
            Area = area; Iy = iy; Iz = iz; J = j;
            Wy = wy > 0 ? wy : (iy > 0 && iz > 0 ? Math.Sqrt(iy * iz / Area) : 0);
            Wz = wz > 0 ? wz : Wy;
            Wpy = Wy; Wpz = Wz;
        }
        public override string GetDims() => $"{Type}, A={Area:F0}, Iy={Iy:F0}, Iz={Iz:F0}, J={J:F0}";
    }

    public class GH_Section : GH_Goo<TB_Section>
    {
        public GH_Section() { }
        public GH_Section(GH_Section other) : base(other.Value) => Value = other.Value.DeepCopy();
        public GH_Section(TB_Section sec) : base(sec) => Value = sec;
        public override bool IsValid => Value?.IsValid() ?? false;
        public override string TypeName => "Section";
        public override string TypeDescription => "Cross-Section";
        public override IGH_Goo Duplicate() => new GH_Section(this);
        public override string ToString() => Value?.ToString() ?? "null";
    }

    public class Param_Section : GH_PersistentParam<GH_Section>
    {
        public Param_Section() : base(
            new GH_InstanceDescription("Section", "Sec", "Cross-Section", Common.category, Common.sub_param)) { }
        public override Guid ComponentGuid => new Guid("6b9d24ab-0c3a-4b3d-aad5-0a009d222836");
        protected override System.Drawing.Bitmap Icon => IconHelper.Create("I");
        protected override GH_GetterResult Prompt_Plural(ref List<GH_Section> values) => GH_GetterResult.success;
        protected override GH_GetterResult Prompt_Singular(ref GH_Section value) => GH_GetterResult.success;
    }
}
