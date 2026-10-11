using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using CSparse;
using CSparse.Storage;
using CSparse.Double;
using CSparse.Double.Factorization;
using Propability_NTNU_v1;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    [Serializable]
    public class TB_Element_1D
    {
        // --- field ---
        public Line Line { get; set; }
        /// <summary>Original undeformed geometry; used for force display when Line is deformed.</summary>
        public Line OriginalLine { get; set; }
        public string Tag { get; private set; }
        public TB_Section Sec { get; set; }
        public List<Node> Nodes { get; private set; }
        public int? Id { get; set; } = null;
        public DenseMatrix EK { get; set; }
        public DenseMatrix TM { get; set; }
        public DenseMatrix EKG { get; set; }
        public Vector3d Vz { get; private set; }
        public List<bool> Hinges { get; } = new List<bool>();
        public double Beta { get; set; }
        public Plane Pln { get; set; }
        public double Weight { get; set; }
        public double Buckling_Length { get; set; }
        

        // --- constructors --- 
        public TB_Element_1D() { }
        public TB_Element_1D(Line _line, string _tag, TB_Section _sec, Vector3d _vz, double _bucklen)
        {
            Line = _line;
            OriginalLine = _line;
            Tag = _tag;
            Sec = _sec;
            Vz = _vz;
            Buckling_Length = _bucklen;
            
            Nodes = new List<Node>();
            Beta = Calc_Beta(_vz);
            Weight = Calc_Weight();

            // Sec.Theta = Beta;

            EK = Calc_ElemStiffMX();
            TM = Calc_TransMX();
            EKG = TM.Transpose().Multiply(EK).Multiply(TM) as DenseMatrix;

        }

        // --- methods ---

        public void SetHingeData(bool smy, bool smz, bool emy, bool emz)
        {
            Hinges.Add(smy);
            Hinges.Add(smz);
            Hinges.Add(emy);
            Hinges.Add(emz);
        }

        public TB_Element_1D DeepCopy()
        {
            var copy = new TB_Element_1D
            {
                Line = Line,
                OriginalLine = OriginalLine,
                Tag = Tag,
                Sec = Sec?.DeepCopy(),
                Nodes = new List<Node>(),
                Id = Id,
                EK = EK != null ? (DenseMatrix)EK.Clone() : null,
                TM = TM != null ? (DenseMatrix)TM.Clone() : null,
                EKG = EKG != null ? (DenseMatrix)EKG.Clone() : null,
                Vz = Vz,
                Beta = Beta,
                Pln = Pln,
                Weight = Weight,
                Buckling_Length = Buckling_Length
            };

            foreach (bool hinge in Hinges)
            {
                copy.Hinges.Add(hinge);
            }

            return copy;
        }

        public override string ToString()
        {
            string txt = "Element_1D: " + Tag + ", " + Sec.Tag;
            return txt;
        }
        public bool IsValid()
        {
            return Tag != "N/A";
        }

        private double Calc_Weight()
        {
            double length = Line.Length; // in meter
            double area = Sec.Area*Math.Pow(10, -6); // mm2 --> m2

            double weight = length * area * Sec.Mat.Gamma * 1000 / Common.GRAVITY; // kN --> kg

            return weight;
        }

        /// <summary>
        /// Roll angle of the element about its axis, from the requested local z-direction.
        /// Default (no z-direction given), as in Karamba3D:
        ///   - non-vertical members: local z in the vertical plane through the member, pointing up,
        ///     local y horizontal;
        ///   - vertical members: local y parallel to the global Y-axis, local z = x cross y.
        /// The angle is SIGNED (positive = rotation about the local x-axis), so a z-direction on
        /// either side of the reference gives the right orientation.
        /// </summary>
        private double Calc_Beta(Vector3d _vz)
        {
            Vector3d vx = Line.UnitTangent;
            bool vertical = Math.Abs(Line.ToX - Line.FromX) < Common.PRES &&
                            Math.Abs(Line.ToY - Line.FromY) < Common.PRES;

            Vector3d vz = _vz;
            bool given = _vz.IsValid && _vz.Length > 0 &&
                         Vector3d.CrossProduct(_vz, vx).Length > 1e-9 * _vz.Length;          // not along the member
            if (!given)
            {
                vz = vertical
                    ? Vector3d.CrossProduct(vx, Vector3d.YAxis)                               // local y = global Y
                    : Vector3d.CrossProduct(vx, Vector3d.CrossProduct(Vector3d.ZAxis, vx));   // local z up
            }

            // the part of vz perpendicular to the member
            vz = vz - (vz * vx) * vx;
            vz.Unitize();
            Vector3d vy = Vector3d.CrossProduct(vz, vx);

            // reference axes of Calc_TransMX at beta = 0, and the signed angle from them
            double beta;
            if (vertical)
                beta = SignedAngle(Vector3d.YAxis, vz, vx);                                   // reference local z = global Y
            else
                beta = SignedAngle(Vector3d.CrossProduct(Vector3d.ZAxis, vx), vy, vx);        // reference local y horizontal

            Pln = new Plane(Line.From, vy, vz);
            return beta;
        }

        /// <summary>Angle from a to b, positive for a right-handed rotation about axis. Both perpendicular to axis.</summary>
        private static double SignedAngle(Vector3d a, Vector3d b, Vector3d axis)
        {
            a.Unitize();
            b.Unitize();
            double cos = a * b;
            double sin = Vector3d.CrossProduct(a, b) * axis;
            double theta = Math.Atan2(sin, cos);
            return double.IsNaN(theta) ? 0.0 : theta;
        }

        public double[] Calc_Forces(int lc_id)
        {
            DenseMatrix U = new DenseMatrix(12, 1);

            double[] d0 = Nodes[0].Disps[lc_id];
            double[] d1 = Nodes[1].Disps[lc_id];
            double[] d_elem = d0.Concat(d1).ToArray();

            for (int i = 0; i < 12; i++)
            {
                U[i, 0] = d_elem[i];
            }

            DenseColumnMajorStorage<double> F = EK.Multiply(TM).Multiply(U);

            return F.Values;
        }

        public DenseMatrix Calc_ElemStiffMX()
        {
            DenseMatrix ESM = new DenseMatrix(12, 12);

            // SI UNIT
            double L = Line.Length; // [m]

            double EA = Sec.Mat.E * Sec.Area;
            // [N/mm2]-->[N/m2], [mm2]-->[m2] 

            double GJ = Sec.Mat.G * Sec.J * Math.Pow(10, -6);
            // [N/mm2]-->[N/m2], [mm4]-->[m4]

            double EIy = Sec.Mat.E * Sec.Iy * Math.Pow(10, -6);
            // [N/mm2]-->[N/m2], [mm4]-->[m4]

            double EIz = Sec.Mat.E * Sec.Iz * Math.Pow(10, -6);
            // [N/mm2]-->[N/m2], [mm4]-->[m4]

            double a = EA / L;
            double b = EIz / Math.Pow(L, 3);
            double c = EIy / Math.Pow(L, 3);
            double d = GJ / L;

            double Lyi = 1.0;
            double Lyj = 1.0;
            double Lzi = 1.0;
            double Lzj = 1.0;

            if (Hinges.Count != 0)
            {
                if (Hinges[0] == false) Lyi = 0.0;
                if (Hinges[1] == false) Lzi = 0.0;
                if (Hinges[2] == false) Lyj = 0.0;
                if (Hinges[3] == false) Lzj = 0.0;
            }

            double Lyb = 1.0 + Lyi + Lyj;
            double Lzb = 1.0 + Lzi + Lzj;

            // axial direction
            ESM[0, 0] =  a;
            ESM[0, 6] = -a;
            ESM[6, 0] = -a;
            ESM[6, 6] =  a;

            // bending around z-axis
            ESM[1, 1] = 6 * b * (Lzi + Lzj + 4 * Lzi * Lzj) / Lzb;
            ESM[1, 7] = -ESM[1, 1];
            ESM[7, 1] = -ESM[1, 1];
            ESM[7, 7] =  ESM[1, 1];
            ESM[1, 5] = 6 * L * b * Lzi * (1 + 2 * Lzj) / Lzb;
            ESM[5, 1] =  ESM[1, 5];
            ESM[5, 7] = -ESM[1, 5];
            ESM[7, 5] = -ESM[1, 5];
            ESM[1, 11] = 6 * L * b * Lzj * (1 + 2 * Lzi) / Lzb;
            ESM[11, 1] =  ESM[1, 11];
            ESM[7, 11] = -ESM[1, 11];
            ESM[11, 7] = -ESM[1, 11];
            ESM[5, 5] = 6 * L * L * b * Lzi * (1 + Lzj) / Lzb;
            ESM[11, 11] = 6 * L * L * b * Lzj * (1 + Lzi) / Lzb;
            ESM[5, 11] = 6 * L * L * b * Lzi * Lzj / Lzb;
            ESM[11, 5] =  ESM[5, 11];

            // bending around y-axis
            ESM[2, 2] = 6 * c * (Lyi + Lyj + 4 * Lyi * Lyj) / Lyb;
            ESM[2, 8] = -ESM[2, 2];
            ESM[8, 2] = -ESM[2, 2];
            ESM[8, 8] =  ESM[2, 2];
            ESM[2, 4] = -6 * L * c * Lyi * (1 + 2 * Lyj) / Lyb;
            ESM[4, 2] = ESM[2, 4]; 
            ESM[4, 8] = -ESM[2, 4];
            ESM[8, 4] = -ESM[2, 4];
            ESM[2, 10] = -6 * L * c * Lyj * (1 + 2 * Lyi) / Lyb;
            ESM[10, 2] = ESM[2, 10];
            ESM[8, 10] = -ESM[2, 10];
            ESM[10, 8] = -ESM[2, 10];
            ESM[4, 4] = 6 * L * L * c * Lyi * (1 + Lyj) / Lyb;
            ESM[10, 10] = 6 * L * L * c * Lyj * (1 + Lyi) / Lyb;
            ESM[4, 10] = 6 * L * L * c * Lyi * Lyj / Lyb;
            ESM[10, 4] = ESM[4, 10];

            // torsion
            ESM[3, 3] =  d;
            ESM[3, 9] = -d;
            ESM[9, 3] = -d;
            ESM[9, 9] =  d;

            return ESM;
        }

        public DenseMatrix Calc_TransMX()
        {
            // double theta = Sec.Theta;
            double theta = Beta;
            double length = Line.Length;

            double l = (Line.ToX - Line.FromX) / length;
            double m = (Line.ToY - Line.FromY) / length;
            double n = (Line.ToZ - Line.FromZ) / length;

            DenseMatrix TM = new DenseMatrix(12, 12);
            DenseMatrix STM1 = new DenseMatrix(3, 3);
            DenseMatrix STM2 = new DenseMatrix(3, 3);

            STM1[0, 0] = 1.0;
            STM1[1, 1] = Math.Cos(theta);
            STM1[1, 2] = Math.Sin(theta);
            STM1[2, 1] = -Math.Sin(theta);
            STM1[2, 2] = Math.Cos(theta);

            // parallel to Z-axis
            if (Math.Abs(l * length) < Common.PRES 
                && Math.Abs(m * length) < Common.PRES)
            {
                STM2[0, 2] = n;
                STM2[1, 0] = n;
                STM2[2, 1] = 1.0;
            }
            else
            {
                double lm = Math.Sqrt(Math.Pow(l, 2) + Math.Pow(m, 2));

                STM2[0, 0] = l;
                STM2[0, 1] = m;
                STM2[0, 2] = n;
                STM2[1, 0] = -m / lm;
                STM2[1, 1] = l / lm;
                STM2[2, 0] = -(l * n) / lm;
                STM2[2, 1] = -(m * n) / lm;
                STM2[2, 2] = lm;

            }

            var STM = STM1.Multiply(STM2);

            for (int k = 0; k < 4; k++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double[] col = STM.Column(j);
                    for (int i = 0; i < 3; i++)
                    {
                        TM[3 * k + i, 3 * k + j] = col[i];
                    }
                }
            }

            return TM;

        }

    }

    public class GH_Element_1D : GH_Goo<TB_Element_1D>
    {
        public GH_Element_1D() { }
        public GH_Element_1D(GH_Element_1D other) : base(other.Value)
        {
            this.Value = other.Value.DeepCopy();
        }
        public GH_Element_1D(TB_Element_1D elem) : base(elem)
        {
            this.Value = elem;
        }
        public override bool IsValid => base.m_value.IsValid();
        public override string TypeName => "Element_1D";
        public override string TypeDescription => "Element_1D";
        public override IGH_Goo Duplicate()
        {
            return new GH_Element_1D(this);
        }
        public override string ToString()
        {
            return Value.ToString();
        }
    }

    public class Param_Element1D : GH_PersistentParam<GH_Element_1D>
    {
        public Param_Element1D() : base(
            new GH_InstanceDescription(
                "Element_1D", "Elem1D", "Element 1D", Common.category, Common.sub_param
                )
            )
        { }

        public override Guid ComponentGuid => new Guid("ae1089f5-e448-4020-ae71-e277ead65cf2");

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("L");

        protected override GH_GetterResult Prompt_Plural(ref List<GH_Element_1D> values)
        {
            return GH_GetterResult.success;
        }

        protected override GH_GetterResult Prompt_Singular(ref GH_Element_1D value)
        {
            return GH_GetterResult.success;
        }


    }




}
