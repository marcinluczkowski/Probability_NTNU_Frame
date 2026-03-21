using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Propability_NTNU_v1;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    [Serializable]
    public abstract class TB_Load
    {
        // --- field ---
        public int? Lc { get; protected set; } = null;

        // --- constructors --- 
        public TB_Load() { }

        // --- methods ---

        public TB_Load DeepCopy()
        {
            return (TB_Load)base.MemberwiseClone();
        }

        public virtual string LoadType()
        {
            return "";
        }

        public bool IsValid()
        {
            return Lc != null;
        }

    }

    [Serializable]
    public class TB_Load_Point : TB_Load
    {
        // --- field ---
        public Point3d Pt { get; private set; }
        public List<double> Loads { get; } = new List<double>();
        public Node Node { get; set; } = null;

        // --- constructors --- 
        public TB_Load_Point() { }

        public TB_Load_Point(Point3d _pt, Vector3d _fvec, Vector3d _mvec, int _lc)
        {
            // class field
            Pt = _pt;

            Loads.Add(_fvec.X);
            Loads.Add(_fvec.Y);
            Loads.Add(_fvec.Z);
            Loads.Add(_mvec.X);
            Loads.Add(_mvec.Y);
            Loads.Add(_mvec.Z);

            // base class field
            Lc = _lc;

        }

        // --- methods ---
        public override string LoadType()
        {
            return "Point Load";
        }

        public override string ToString()
        {
            string txt = "";
            txt += "Point Load at ";
            txt += Pt.ToString();

            return txt;
        }

    }

    public class GH_Load : GH_Goo<TB_Load>
    {
        public GH_Load() { }
        public GH_Load(GH_Load other) : base(other.Value)
        {
            this.Value = other.Value.DeepCopy();
        }
        public GH_Load(TB_Load load) : base(load)
        {
            this.Value = load;
        }
        public override bool IsValid => base.m_value.IsValid();
        public override string TypeName => "Load";
        public override string TypeDescription => "Load";
        public override IGH_Goo Duplicate()
        {
            return new GH_Load(this);
        }
        public override string ToString()
        {
            return Value.ToString();
        }

    }

    public class Param_Load : GH_PersistentParam<GH_Load>
    {
        public Param_Load() : base(
            new GH_InstanceDescription(
                "Load", "load", "Load", Common.category, Common.sub_param
                )
            )
        { }

        public override Guid ComponentGuid => new Guid("be2fad36-1931-4ae5-b7fa-91005b81e332");

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("F");

        protected override GH_GetterResult Prompt_Plural(ref List<GH_Load> values)
        {
            return GH_GetterResult.success;
        }

        protected override GH_GetterResult Prompt_Singular(ref GH_Load value)
        {
            return GH_GetterResult.success;
        }


    }

}
