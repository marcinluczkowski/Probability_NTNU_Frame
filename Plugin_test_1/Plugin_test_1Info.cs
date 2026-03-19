using Grasshopper;
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace Plugin_test_1
{
    public class Plugin_test_1Info : GH_AssemblyInfo
    {
        public override string Name => "Plugin_test_1";

        //Return a 24x24 pixel bitmap to represent this GHA library.
        public override Bitmap Icon => null;

        //Return a short string describing the purpose of this GHA library.
        public override string Description => "";

        public override Guid Id => new Guid("74746568-0080-42d0-95cb-67019d5800e6");

        //Return a string identifying you or your company.
        public override string AuthorName => "";

        //Return a string representing your preferred contact details.
        public override string AuthorContact => "";

        //Return a string representing the version.  This returns the same version as the assembly.
        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}