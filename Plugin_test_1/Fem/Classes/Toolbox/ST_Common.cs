using System.Text.Json;
using System.Text.Json.Serialization;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    public static class Common
    {
        // default class
        // --- field ---
        // --- constructors --- 
        // --- methods ---

        // default component
        // --- variables ---
        // --- input --- 
        // --- solve ---
        // --- output ---

        public static double PRES = 0.001;
        /// <summary>Max distance to snap a load point to nearest node (for floating-point tolerance)</summary>
        public static double LOAD_SNAP_TOL = 0.05;
        public static int BBOX_SEGMENT = 100;
        public static double DIV_DIST_ALONG_AXIS = 1.0;
        public static int DIV_CIRCLE = 18;
        public static double GRAVITY = 9.81; // m/s2

        public static readonly string category = "NTNU";
        public static readonly string sub_mat = "FEM · 01 Material";
        public static readonly string sub_sec = "FEM · 02 Section";
        public static readonly string sub_sup = "FEM · 04 Support";
        public static readonly string sub_load = "FEM · 05 Load";
        public static readonly string sub_elem = "FEM · 03 Element";
        public static readonly string sub_assem = "FEM · 06 Assembly";
        public static readonly string sub_analize = "FEM · 07 Analysis";
        public static readonly string sub_post = "FEM · 09 Post";
        public static readonly string sub_param = "00. Param";
        public static readonly string sub_info = "99. Info";

        public static T DeepCopy<T>(T target)
        {
            if (target is TB_Model mdl)
            {
                return (T)(object)mdl.DeepCopy();
            }

            // fallback for non-graph objects
            var opts = new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.Preserve,
                MaxDepth = 0
            };
            var json = JsonSerializer.Serialize(target, opts);
            return JsonSerializer.Deserialize<T>(json, opts)!;
        }
            

    }
}
