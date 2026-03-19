using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Plugin_test_1
{
    public class Element
    {
        public int id;
        public string name;
        public Line axis;


        public Element() 
        {
        }

        public Element(int _id, string _name) 
        {
            id = _id;
            name = _name;
        }

        public Line createAxis() 
        { 
            Line axis1 = new Line(new Point3d(0, 0, 0), new Point3d(1, 0, 0));
            return axis1; 
        }

        public Line createAxisSpecificLength(double _length)
        {
            Line axis1 = new Line(new Point3d(0, 0, 0), new Point3d(_length, 0, 0));
            return axis1;
        }
    }

    public class ProbabilityMath
    {
        public double safetyfactor1 = 1.2;
        public double safetyfactor2 = 2.0;

        public double calculateProbability(double input) 
        {
            double probability = input * safetyfactor1 / safetyfactor2;
            return probability;
        }

    }


}
