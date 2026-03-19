using Grasshopper;
using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.Collections.Generic;


namespace Plugin_test_1
{
    public class Plugin_test_Component1 : GH_Component
    {
        /// <summary>
        /// Each implementation of GH_Component must provide a public 
        /// constructor without any arguments.
        /// Category represents the Tab in which the component will appear, 
        /// Subcategory the panel. If you use non-existing tab or panel names, 
        /// new tabs/panels will automatically be created.
        /// </summary>
        public Plugin_test_Component1()
          : base("Plugin_test_Component1", "Nickname",
            "Description",
            "NTNU", "Propability")
        {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter("Input", "I", "Input number", GH_ParamAccess.item);
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Output", "O", "Output number", GH_ParamAccess.list);
            pManager.AddGenericParameter("Generic", "G", "Generic output", GH_ParamAccess.item);    
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object can be used to retrieve data from input parameters and 
        /// to store data in output parameters.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double input = 0;
            if (!DA.GetData(0, ref input)) return;

            List<string> output = new List<string>();
            
            output.Add("Input was: " + input.ToString());

            double n1 = 12.5;
            n1 = n1 + input;

            output.Add("Input is: " + n1);


            Element e1 = new Element();
            e1.id = (int)input;
            e1.name = "Element 1";
            e1.axis = new Line(new Point3d(0, 0, 0), new Point3d(1, 0, 0));

            DA.SetDataList(0, output);
            DA.SetData(1, e1);
        }

        /// <summary>
        /// Provides an Icon for every component that will be visible in the User Interface.
        /// Icons need to be 24x24 pixels.
        /// You can add image files to your project resources and access them like this:
        /// return Resources.IconForThisComponent;
        /// </summary>
        protected override System.Drawing.Bitmap Icon => null;

        /// <summary>
        /// Each component must have a unique Guid to identify it. 
        /// It is vital this Guid doesn't change otherwise old ghx files 
        /// that use the old ID will partially fail during loading.
        /// </summary>
        public override Guid ComponentGuid => new Guid("70fcaf6f-b11b-43e3-ac20-729d04b3b8b9");
    }
}