using Grasshopper.Kernel;
using Propability_NTNU_v1.Classes.Toolbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Plugin_test_1.Reliability.FORM
{
    /// <summary>
    /// Adapter that bridges the existing FEM solver (TB_Model, SolveLS) to the FORM solver.
    /// 
    /// This adapter allows the FEM solver to be called as a limit state function g(x) where x
    /// is a vector of random variables (material properties, loads, section properties, etc.).
    /// 
    /// Usage pattern:
    /// 1. Create adapter with template model and parameter map
    /// 2. Call AsLimitStateFunction() to get Func&lt;double[], double&gt;
    /// 3. Pass to FORMSolver.Solve()
    /// </summary>
    public class FEMSolverAdapter
    {
        /// <summary>
        /// Template FEM model (geometry, topology, supports remain fixed).
        /// This is deep-copied for each evaluation to avoid state pollution.
        /// </summary>
        private readonly TB_Model _templateModel;

        /// <summary>
        /// Maps variable names (e.g., "E", "F", "Iy") to their indices in the x[] array.
        /// Example: { "E" → 0, "Load_Y" → 1, "Area" → 2 }
        /// </summary>
        private readonly Dictionary<string, int> _parameterMap;

        /// <summary>
        /// Allowable capacity (deflection limit, stress limit, etc.) for the limit state.
        /// Used to compute g = capacity - response.
        /// </summary>
        private readonly double _capacity;

        /// <summary>
        /// Type of response to extract and evaluate.
        /// Options: "MaxDisplacement", "MaxStress", "MaxDeflection"
        /// </summary>
        private readonly string _responseType;

        /// <summary>
        /// Initializes the FEM solver adapter.
        /// </summary>
        /// <param name="templateModel">
        /// Template TB_Model with fixed geometry, connectivity, supports.
        /// Will be deep-copied for each evaluation.
        /// </param>
        /// <param name="parameterMap">
        /// Dictionary mapping variable names to indices in x[] array.
        /// Example: { "E" → 0, "Load_Vertical" → 1, "Area" → 2 }
        /// </param>
        /// <param name="capacity">
        /// Allowable limit (e.g., max deflection in meters, or max stress in Pa).
        /// Used in limit state: g = capacity - response
        /// </param>
        /// <param name="responseType">
        /// Response quantity to evaluate: "MaxDisplacement", "MaxStress", "MaxDeflection"
        /// Default: "MaxDisplacement"
        /// </param>
        public FEMSolverAdapter(
            TB_Model templateModel,
            Dictionary<string, int> parameterMap,
            double capacity,
            string responseType = "MaxDisplacement")
        {
            if (templateModel == null)
                throw new ArgumentNullException(nameof(templateModel));
            if (parameterMap == null)
                throw new ArgumentNullException(nameof(parameterMap));

            _templateModel = templateModel;
            _parameterMap = parameterMap;
            _capacity = capacity;
            _responseType = responseType;
        }

        /// <summary>
        /// Evaluates the limit state function g(x) for a given point in physical space.
        /// 
        /// Process:
        /// 1. Deep-copy the template model
        /// 2. Update random variable parameters from x[]
        /// 3. Re-solve the FEM system
        /// 4. Extract response (max displacement or stress)
        /// 5. Compute limit state: g = capacity - response
        /// 
        /// Returns g where:
        ///   g &gt; 0 → safe region
        ///   g = 0 → limit state (failure surface)
        ///   g &lt; 0 → failure region
        /// </summary>
        /// <param name="x">
        /// Vector of physical variable values, length = number of random variables.
        /// Indices correspond to _parameterMap.
        /// </param>
        /// <returns>Limit state value g(x).</returns>
        public double EvaluateLimitState(double[] x)
        {
            if (x == null)
                throw new ArgumentNullException(nameof(x));

            // Step 1: Deep-copy the template model to avoid state pollution
            TB_Model workingModel = _templateModel.DeepCopy();

            // Step 2: Update parameters from x[] using the parameter map
            UpdateModelParameters(workingModel, x);

            if (workingModel.Elem1Ds != null && workingModel.Elem1Ds.Count > 0)
            {
                var elem0 = workingModel.Elem1Ds[0];
                if (elem0?.Sec != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[DIAG] Mat.Fy   = {elem0.Sec.Mat?.Fy}");
                    System.Diagnostics.Debug.WriteLine($"[DIAG] Sec.Area = {elem0.Sec.Area}");
                    System.Diagnostics.Debug.WriteLine($"[DIAG] Sec.Iy   = {elem0.Sec.Iy}");
                    System.Diagnostics.Debug.WriteLine($"[DIAG] Sec.Wy   = {elem0.Sec.Wy}");
                }
            }

            // Step 3: Re-solve the FEM system
            // VERIFIED: SolveLS re-assembles K from scratch on every instantiation (ST_SolveLS.Solve()).
            // Element stiffness reflects updated Sec.Mat properties (ST_Element_1D.Calc_ElemStiffMX()).
            SolveLS solver = new SolveLS(ref workingModel);

            // Step 4: Extract response based on responseType

            double g;

            // If capacity is uniquely flagged for auto-evaluation
            if (_capacity < 0 && _responseType.Equals("Stress", StringComparison.OrdinalIgnoreCase))
            {
                // g = minimal (Fy - local_stress) across all elements
                double response = ExtractResponse(workingModel);
                g = ExtractAutoStressLimitState(workingModel);
                System.Diagnostics.Debug.WriteLine($"[DIAG-branch] _capacity={_capacity}, _responseType_1='{_responseType}', autoStress={_capacity < 0 && _responseType.Equals("Stress", StringComparison.OrdinalIgnoreCase)}, returned g={g}");
            }
            else
            {
                // g(x) = capacity - response
                // where response is max deflection, max stress, etc.
                double response = ExtractResponse(workingModel);
                g = _capacity - response;
            }

            return g;
        }

        /// <summary>
        /// Returns EvaluateLimitState as a delegate suitable for FORMSolver.Solve().
        /// </summary>
        /// <returns>Func&lt;double[], double&gt; that evaluates the limit state function.</returns>
        public Func<double[], double> AsLimitStateFunction()
        {
            return EvaluateLimitState;
        }

        // =====================================================================
        // PER-ELEMENT LIMIT STATES
        //
        // Each element gets its own smooth limit state g_e(x) = (fy or capacity) - sigma_e,
        // instead of the system-wide min over elements. Running FORM once per element yields
        // a per-element beta / Pf / alpha. A single FEM solve produces the whole stress field,
        // so the cost per evaluation is the same as the system limit state.
        //
        // Stress is in N/mm^2 = MPa (axial N in newtons, Area in mm^2), matching fy in MPa.
        // (This deliberately avoids the spurious /1e6 in ExtractMaxStress.)
        // =====================================================================

        /// <summary>Number of 1D elements in the template model.</summary>
        public int ElementCount => _templateModel?.Elem1Ds?.Count ?? 0;

        /// <summary>
        /// Returns a display tag for each element (element Tag, or "Elem[i]" if blank),
        /// in the same order as the per-element limit-state index.
        /// </summary>
        public List<string> GetElementTags()
        {
            var tags = new List<string>();
            if (_templateModel?.Elem1Ds == null) return tags;

            for (int i = 0; i < _templateModel.Elem1Ds.Count; i++)
            {
                string tag = _templateModel.Elem1Ds[i]?.Tag;
                tags.Add(string.IsNullOrWhiteSpace(tag) ? $"Elem[{i}]" : tag);
            }
            return tags;
        }

        /// <summary>
        /// Evaluates the limit state for a single element:
        ///   auto (capacity &lt; 0, Stress): g = fy_element - sigma_element
        ///   manual:                        g = capacity   - sigma_element
        /// sigma is the maximum over both element ends and all load cases.
        /// </summary>
        public double EvaluateLimitStateForElement(double[] x, int elementIndex)
        {
            if (x == null)
                throw new ArgumentNullException(nameof(x));

            TB_Model workingModel = _templateModel.DeepCopy();
            UpdateModelParameters(workingModel, x);
            // Re-solve (re-assembles and factorizes K from the updated properties).
            SolveLS solver = new SolveLS(ref workingModel);

            if (workingModel.Elem1Ds == null ||
                elementIndex < 0 || elementIndex >= workingModel.Elem1Ds.Count)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));

            return ComputeElementLimitState(workingModel.Elem1Ds[elementIndex], workingModel);
        }

        /// <summary>
        /// Returns the per-element limit state as a delegate for FORMSolver.Solve().
        /// </summary>
        public Func<double[], double> AsLimitStateFunctionForElement(int elementIndex)
        {
            return x => EvaluateLimitStateForElement(x, elementIndex);
        }

        /// <summary>
        /// Computes g for one element (max stress over both ends and all load cases).
        /// Returns +infinity for an invalid element so it never falsely governs.
        /// </summary>
        private double ComputeElementLimitState(TB_Element_1D elem, TB_Model model)
        {
            bool auto = _capacity < 0 && _responseType.Equals("Stress", StringComparison.OrdinalIgnoreCase);

            if (elem?.Nodes == null || elem.Nodes.Count < 2 || elem.Sec == null ||
                (auto && elem.Sec.Mat == null))
                return double.PositiveInfinity;

            double limit = auto ? elem.Sec.Mat.Fy : _capacity;
            double maxStress = 0.0;

            for (int lcIndex = 0; lcIndex < (model.Disps?.Count ?? 0); lcIndex++)
            {
                try
                {
                    var forces = elem.Calc_Forces(lcIndex);
                    if (forces != null && forces.Length >= 12)
                    {
                        double s = ElementStress(elem, forces);
                        if (s > maxStress) maxStress = s;
                    }
                }
                catch { continue; }
            }

            return limit - maxStress;
        }

        /// <summary>
        /// Combined axial + biaxial bending stress at both ends; returns the larger end.
        /// sigma = |N/A| + |My/Wy| + |Mz/Wz|, in MPa (N/mm^2).
        /// </summary>
        private static double ElementStress(TB_Element_1D elem, double[] forces)
        {
            double n_i = forces[0], my_i = forces[4], mz_i = forces[5];
            double n_j = forces[6], my_j = forces[10], mz_j = forces[11];

            double area = elem.Sec.Area, wy = elem.Sec.Wy, wz = elem.Sec.Wz;

            double stress_i = (area > 0 ? Math.Abs(n_i / area) : 0) +
                              (wy > 0 ? Math.Abs(my_i / wy) : 0) +
                              (wz > 0 ? Math.Abs(mz_i / wz) : 0);

            double stress_j = (area > 0 ? Math.Abs(n_j / area) : 0) +
                              (wy > 0 ? Math.Abs(my_j / wy) : 0) +
                              (wz > 0 ? Math.Abs(mz_j / wz) : 0);

            return Math.Max(stress_i, stress_j);
        }

        /// <summary>
        /// Updates the working model parameters based on x[] and the parameter map.
        /// Handles material properties (E, G, Fy), section properties (Area, Iy, Iz, J),
        /// and load magnitudes.
        /// </summary>
        private void UpdateModelParameters(TB_Model model, double[] x)
        {
            foreach (var kvp in _parameterMap)
            {
                string paramName = kvp.Key;
                int index = kvp.Value;

                if (index < 0 || index >= x.Length)
                    throw new IndexOutOfRangeException(
                        $"Parameter '{paramName}' has index {index} but x[] has only {x.Length} elements.");

                double value = x[index];

                // Try to update material properties (E, G, Fy)
                if (UpdateMaterialProperty(model, paramName, value))
                    continue;

                // Try to update section properties (Area, Iy, Iz, J)
                if (UpdateSectionProperty(model, paramName, value))
                    continue;

                // Try to update load magnitudes (e.g., "Load_0_Y" for element 0, component Y)
                if (UpdateLoadProperty(model, paramName, value))
                    continue;

                // If we get here, parameter was not recognized
                throw new ArgumentException(
                    $"Unknown parameter: '{paramName}'. Check parameter map and variable names.");
            }
        }

        /// <summary>
        /// Attempts to update a material property (E, G, Fy) in all elements' materials.
        /// Returns true if the property was found and updated, false otherwise.
        /// VERIFIED: TB_Material properties are immutable (constructor-initialized).
        /// Solution: Create new TB_Material and new Section_Custom, reassign via elem.Sec.
        /// See: ST_Section.cs (TB_Section.Mat has protected setter).
        /// </summary>
        private bool UpdateMaterialProperty(TB_Model model, string paramName, double value)
        {
            if (model.Elem1Ds == null || model.Elem1Ds.Count == 0)
                return false;

            bool found = false;

            foreach (var elem in model.Elem1Ds)
            {
                if (elem?.Sec?.Mat == null)
                    continue;

                TB_Material oldMat = elem.Sec.Mat;
                TB_Material newMat = null;

                if (paramName.ToLower() == "e" && value > 1e6)
                    throw new ArgumentException($"E={value} looks like Pa, not MPa. Divide by 1e6.");

                switch (paramName.ToLower())
                {
                    case "e":
                    case "youngs_modulus":
                    case "elastic_modulus":
                        newMat = new TB_Material(oldMat.Tag, value, oldMat.G, oldMat.Gamma, oldMat.Alpha, oldMat.Fy);
                        found = true;
                        break;

                    case "g":
                    case "shear_modulus":
                        newMat = new TB_Material(oldMat.Tag, oldMat.E, value, oldMat.Gamma, oldMat.Alpha, oldMat.Fy);
                        found = true;
                        break;

                    case "fy":
                    case "yield_strength":
                        newMat = new TB_Material(oldMat.Tag, oldMat.E, oldMat.G, oldMat.Gamma, oldMat.Alpha, value);
                        found = true;
                        break;

                    case "gamma":
                    case "density":
                        newMat = new TB_Material(oldMat.Tag, oldMat.E, oldMat.G, value, oldMat.Alpha, oldMat.Fy);
                        found = true;
                        break;

                    case "alpha":
                    case "thermal_expansion":
                        newMat = new TB_Material(oldMat.Tag, oldMat.E, oldMat.G, oldMat.Gamma, value, oldMat.Fy);
                        found = true;
                        break;
                }

                if (found && newMat != null)
                {
                    // VERIFIED: TB_Section.Mat has protected setter, cannot be mutated directly.
                    // Solution: Create new Section_Custom with updated material and reassign elem.Sec.
                    // See: ST_Section.cs line 14 (public TB_Material Mat { get; protected set; }).
                    TB_Section oldSec = elem.Sec;
                    var newSec = new Section_Custom(
                        newMat,
                        oldSec.Tag,
                        oldSec.Area,
                        oldSec.Iy,
                        oldSec.Iz,
                        oldSec.J,
                        oldSec.Wy,
                        oldSec.Wz);
                    elem.Sec = newSec;

                    // Recompute element stiffness matrix
                    elem.EK = elem.Calc_ElemStiffMX();
                    elem.EKG = elem.TM.Transpose().Multiply(elem.EK).Multiply(elem.TM) as CSparse.Double.DenseMatrix;
                }
            }

            return found;
        }

        /// <summary>
        /// Attempts to update a section property (Area, Iy, Iz, J) in all elements.
        /// Returns true if the property was found and updated, false otherwise.
        /// VERIFIED: TB_Section properties are protected (immutable publicly).
        /// Solution: Create new Section_Custom with updated values and reassign.
        /// See: ST_Section.cs (protected setters on Area, Iy, Iz, J).
        /// </summary>
        private bool UpdateSectionProperty(TB_Model model, string paramName, double value)
        {
            if (model.Elem1Ds == null || model.Elem1Ds.Count == 0)
                return false;

            bool found = false;

            foreach (var elem in model.Elem1Ds)
            {
                if (elem?.Sec == null)
                    continue;

                TB_Section oldSec = elem.Sec;
                Section_Custom newSec = null;

                switch (paramName.ToLower())
                {
                    case "area":
                    case "a":
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, value, oldSec.Iy, oldSec.Iz, oldSec.J, oldSec.Wy, oldSec.Wz);
                        found = true;
                        break;

                    case "iy":
                    case "moment_of_inertia_y":
                        // System.Diagnostics.Debug.WriteLine($"[DIAG-iy] Hit! value={value}, oldSec.Iy={oldSec.Iy}, oldSec.Wy={oldSec.Wy}");
                        double new_wy = oldSec.Wy * (value) / oldSec.Iy;    // scale Wy proportionally: Wy_new = Wy_old * (Iy_new / Iy_old)
                        // System.Diagnostics.Debug.WriteLine($"[DIAG-iy] new_wy = {new_wy}, newSec.Iy will be = {value}");
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area,
                                                    value, oldSec.Iz, oldSec.J,
                                                    new_wy, oldSec.Wz);
                        found = true;
                        break;

                    case "iz":
                    case "moment_of_inertia_z":
                        double new_wz = oldSec.Wz * (value) / oldSec.Iz;  // scale Wz proportionally: Wz_new = Wz_old * (Iz_new / Iz_old)
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area, oldSec.Iy
                                                    ,value, oldSec.J,
                                                    oldSec.Wy,new_wz );
                        found = true;
                        break;

                    case "j":
                    case "torsional_constant":
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area, oldSec.Iy, oldSec.Iz, value, oldSec.Wy, oldSec.Wz);
                        found = true;
                        break;

                    case "wy":
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area, oldSec.Iy, oldSec.Iz, oldSec.J, value, oldSec.Wz);
                        found = true;
                        break;

                    case "wz":
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area, oldSec.Iy, oldSec.Iz, oldSec.J, oldSec.Wy, value);
                        found = true;
                        break;
                }

                if (found && newSec != null)
                {
                    elem.Sec = newSec;
                    // Recompute element stiffness matrix
                    elem.EK = elem.Calc_ElemStiffMX();
                    elem.EKG = elem.TM.Transpose().Multiply(elem.EK).Multiply(elem.TM) as CSparse.Double.DenseMatrix;
                }
            }

            return found;
        }

        /// <summary>
        /// Attempts to update a load property in the model's load list.
        /// Supports patterns like "Load_0_Y" (load at index 0, Y component).
        /// Returns true if the property was found and updated, false otherwise.
        /// 
        /// IMPORTANT: Creates a new TB_Load_Point with updated load values to avoid
        /// mutating the shared load list in the template model.
        /// </summary>
        private bool UpdateLoadProperty(TB_Model model, string paramName, double value)
        {
            if (model.Loads == null || model.Loads.Count == 0)
                return false;

            // Try pattern: "Load_<index>_<component>"
            // where component is X, Y, Z, Mx, My, Mz (indices 0-5)
            string[] parts = paramName.Split('_');
            if (parts.Length >= 3 && parts[0].ToLower() == "load")
            {
                if (int.TryParse(parts[1], out int loadIndex))
                {
                    string component = string.Join("_", parts.Skip(2)).ToLower();

                    if (loadIndex >= 0 && loadIndex < model.Loads.Count)
                    {
                        var load = model.Loads[loadIndex];
                        if (load is TB_Load_Point oldPointLoad && oldPointLoad.Loads.Count >= 6)
                        {
                            int componentIndex = ComponentNameToIndex(component);
                            if (componentIndex >= 0 && componentIndex < 6)
                            {
                                // Create a new point load with updated load component
                                // (avoiding mutation of the original shared load list)
                                // Convert user inputs (kN, kNm) to Base SI (N, Nm) for accurate stiffness integration
                                var newLoads = new List<double>(oldPointLoad.Loads);
                                newLoads[componentIndex] = value * 1000.0;

                                var newPointLoad = new TB_Load_Point(
                                    oldPointLoad.Pt,
                                    new Rhino.Geometry.Vector3d(newLoads[0], newLoads[1], newLoads[2]),
                                    new Rhino.Geometry.Vector3d(newLoads[3], newLoads[4], newLoads[5]),
                                    oldPointLoad.Lc ?? 0);
                                newPointLoad.Node = oldPointLoad.Node;

                                model.Loads[loadIndex] = newPointLoad;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Maps component names to indices in the Loads array.
        /// Indices: 0=Fx, 1=Fy, 2=Fz, 3=Mx, 4=My, 5=Mz
        /// </summary>
        private int ComponentNameToIndex(string componentName)
        {
            return componentName switch
            {
                "x" or "fx" or "force_x" => 0,
                "y" or "fy" or "force_y" => 1,
                "z" or "fz" or "force_z" => 2,
                "mx" or "moment_x" => 3,
                "my" or "moment_y" => 4,
                "mz" or "moment_z" => 5,
                _ => -1
            };
        }

        /// <summary>
        /// Extracts the response quantity from the solved model.
        /// Supports: MaxDisplacement, MaxStress, MaxDeflection
        /// </summary>
        private double ExtractResponse(TB_Model model)
        {
            return _responseType.ToLower() switch
            {
                "maxdisplacement" or "max_displacement" or "deflection" => ExtractMaxDisplacement(model),
                "maxstress" or "max_stress" or "stress" => ExtractMaxStress(model),
                "maxdeflection" or "max_deflection" => ExtractMaxDisplacement(model),
                _ => throw new NotSupportedException(
                    $"Response type '{_responseType}' is not supported. " +
                    "Use 'MaxDisplacement', 'MaxStress', or 'MaxDeflection'.")
            };
        }

        /// <summary>
        /// Computes maximum displacement magnitude across all nodes.
        /// Returns the largest displacement vector norm in meters.
        /// </summary>
        private double ExtractMaxDisplacement(TB_Model model)
        {
            double maxDisp = 0.0;

            if (model?.Nodes == null || model.Nodes.Count == 0)
                return maxDisp;

            // Iterate over all nodes and all load cases
            foreach (var node in model.Nodes)
            {
                if (node?.Disps == null)
                    continue;

                foreach (var dispArray in node.Disps)
                {
                    if (dispArray == null || dispArray.Length < 6)
                        continue;

                    // Displacement is stored as [u_x, u_y, u_z, θ_x, θ_y, θ_z]
                    // Take magnitude of translational displacements only (first 3 components)
                    double disp_mag = Math.Sqrt(
                        dispArray[0] * dispArray[0] +
                        dispArray[1] * dispArray[1] +
                        dispArray[2] * dispArray[2]);

                    if (disp_mag > maxDisp)
                        maxDisp = disp_mag;
                }
            }

            return maxDisp * 1000.0; // Return mm
        }

        /// <summary>
        /// Computes maximum bending stress across all elements.
        /// Uses σ = M / W where M is bending moment and W is section modulus.
        /// Returns the maximum stress in Pa.
        /// </summary>
        private double ExtractMaxStress(TB_Model model)
        {
            double maxStress = 0.0;

            if (model?.Elem1Ds == null || model.Elem1Ds.Count == 0)
                return maxStress;

            foreach (var elem in model.Elem1Ds)
            {
                if (elem?.Nodes == null || elem.Nodes.Count < 2 || elem.Sec == null) continue;

                for (int lcIndex = 0; lcIndex < (model.Disps?.Count ?? 0); lcIndex++)
                {
                    try
                    {
                        var forces = elem.Calc_Forces(lcIndex);
                        if (forces != null && forces.Length >= 12)
                        {
       
                            double n_i = forces[0];
                            double my_i = forces[4];
                            double mz_i = forces[5];

                            double n_j = forces[6];
                            double my_j = forces[10];
                            double mz_j = forces[11];

                            double area = elem.Sec.Area;
                            double wy = elem.Sec.Wy;
                            double wz = elem.Sec.Wz;

                            double stress_i = (area > 0 ? Math.Abs(n_i / area) : 0) + 
                                              (wy > 0 ? Math.Abs(my_i / wy) : 0) + 
                                              (wz > 0 ? Math.Abs(mz_i / wz) : 0);

                            double stress_j = (area > 0 ? Math.Abs(n_j / area) : 0) + 
                                              (wy > 0 ? Math.Abs(my_j / wy) : 0) + 
                                              (wz > 0 ? Math.Abs(mz_j / wz) : 0);

                            double localMax = Math.Max(stress_i, stress_j);

                            if (localMax > maxStress) maxStress = localMax;
                        }
                    }
                    catch { continue; }
                }
            }

            return maxStress / 1e6; // Return MPa
        }

        /// <summary>
        /// Evaluates the most critical element where g = (Fy - local stress).
        /// Looks directly at the material yield strength (Fy) to evaluate capacity.
        /// Returns the absolute minimum limit state value across the structure.
        /// </summary>
        private double ExtractAutoStressLimitState(TB_Model model)
        {
            double min_g = double.MaxValue;

            if (model?.Elem1Ds == null || model.Elem1Ds.Count == 0)
                return min_g;

            foreach (var elem in model.Elem1Ds)
            {
                if (elem?.Nodes == null || elem.Nodes.Count < 2 || elem.Sec?.Mat == null) continue;

                double fy = elem.Sec.Mat.Fy;

                for (int lcIndex = 0; lcIndex < (model.Disps?.Count ?? 0); lcIndex++)
                {
                    try
                    {
                        var forces = elem.Calc_Forces(lcIndex);
                        if (forces != null && forces.Length >= 12)
                        {
                            double n_i = forces[0];
                            double my_i = forces[4];
                            double mz_i = forces[5];

                            double n_j = forces[6];
                            double my_j = forces[10];
                            double mz_j = forces[11];

                            double area = elem.Sec.Area;
                            double wy = elem.Sec.Wy;
                            double wz = elem.Sec.Wz;

                            double stress_i = (area > 0 ? Math.Abs(n_i / area) : 0) + 
                                              (wy > 0 ? Math.Abs(my_i / wy) : 0) + 
                                              (wz > 0 ? Math.Abs(mz_i / wz) : 0);

                            double stress_j = (area > 0 ? Math.Abs(n_j / area) : 0) + 
                                              (wy > 0 ? Math.Abs(my_j / wy) : 0) + 
                                              (wz > 0 ? Math.Abs(mz_j / wz) : 0);

                            double localMaxStress = Math.Max(stress_i, stress_j);

                            // Map local gap gradient back to user expected MPa scaling
                            System.Diagnostics.Debug.WriteLine($"[DIAG-LSF] elem={elem.Sec.Tag}, fy={fy}, my_i={my_i}, my_j={my_j}, wy={wy}, stress_i={stress_i}, stress_j={stress_j}, localMax={localMaxStress}, local_g={(fy - localMaxStress)}");
                            double local_g = fy - localMaxStress;

                            if (local_g < min_g) min_g = local_g;
                        }
                    }
                    catch { continue; }
                }
            }

            return min_g;
        }
    }
}