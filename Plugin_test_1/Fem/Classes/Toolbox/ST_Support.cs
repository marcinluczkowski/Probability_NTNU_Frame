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
    public class TB_Support
    {
        // --- field ---
        public Point3d Pt { get; private set; }

        public List<bool> Conditions { get; private set; } = null;
        public Node Node { get; set; } = null;
        // public List<double[]> React { get; private set; } = new List<double[]>();
        // List[loadcase][6 forces]
        public List<List<double>> React { get; private set; } = new List<List<double>>();

        // --- constructors --- 
        public TB_Support() { }

        public TB_Support(Point3d _pt, string _conditions)
        {
            Pt = _pt;
            Conditions = new List<bool>();

            ProcessConditions(_conditions);

        }

        // --- methods ---
        public void InitializeReact(int num_lcs)
        {
            List<List<double>> reacts = new List<List<double>>();
            for (int i = 0; i < num_lcs; i++)
            {
                reacts.Add(new List<double>(6) { 0, 0, 0, 0, 0, 0 }); 
            }

            React = reacts;
        }

        private void ProcessConditions(string _conditions)
        {
            if (_conditions.Length != 6)
            {
                Conditions = null;
                return;
            }

            char[] cs = _conditions.ToCharArray();

            for(int i=0; i<6; i++)
            {
                if (cs[i] == '0')
                {
                    Conditions.Add(false);
                }
                else if (cs[i] == '1')
                {
                    Conditions.Add(true);
                }
                else
                {
                    Conditions = null;
                    break;
                }
            }

        }

        public TB_Support DeepCopy()
        {
            return (TB_Support)base.MemberwiseClone();
        }
        public override string ToString()
        {
            string cond;

            if (Conditions != null)
            {
                cond = String.Join(",", Conditions);
            }

            else
            {
                cond = "error in support condition";
            }

            string txt = "Support, " + cond;
            return txt;
        }
        public bool IsValid()
        {
            return Conditions != null;
        }

    }

    public class GH_Support : GH_Goo<TB_Support>
    {
        public GH_Support() { }
        public GH_Support(GH_Support other) : base(other.Value)
        {
            this.Value = other.Value.DeepCopy();
        }
        public GH_Support(TB_Support sup) : base(sup)
        {
            this.Value = sup;
        }
        public override bool IsValid => base.m_value.IsValid();
        public override string TypeName => "Support";
        public override string TypeDescription => "Support";
        public override IGH_Goo Duplicate()
        {
            return new GH_Support(this);
        }
        public override string ToString()
        {
            return Value.ToString();
        }
    }

    public class Param_Support : GH_PersistentParam<GH_Support>
    {
        public Param_Support() : base(
            new GH_InstanceDescription(
                "Support", "Sup", "Support conditions", Common.category, Common.sub_param
                )
            )
        { }

        public override Guid ComponentGuid => new Guid("36e45102-c52d-4799-a541-fb5bf2b33b19");

        protected override System.Drawing.Bitmap Icon => IconHelper.Create("S");

        protected override GH_GetterResult Prompt_Plural(ref List<GH_Support> values)
        {
            return GH_GetterResult.success;
        }

        protected override GH_GetterResult Prompt_Singular(ref GH_Support value)
        {
            return GH_GetterResult.success;
        }


    }

}
