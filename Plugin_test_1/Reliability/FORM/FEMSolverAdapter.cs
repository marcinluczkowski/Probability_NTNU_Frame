using System;
using System.Collections.Generic;
using System.Linq;
using Propability_NTNU_v1.Classes.Toolbox;

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
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive.", nameof(capacity));

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

            // Step 3: Re-solve the FEM system
            // VERIFIED: SolveLS re-assembles K from scratch on every instantiation (ST_SolveLS.Solve()).
            // Element stiffness reflects updated Sec.Mat properties (ST_Element_1D.Calc_ElemStiffMX()).
            SolveLS solver = new SolveLS(ref workingModel);

            // Step 4: Extract response based on responseType
            double response = ExtractResponse(workingModel);

            // Step 5: Compute limit state function
            // g(x) = capacity - response
            // where response is max deflection, max stress, etc.
            double g = _capacity - response;

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
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area, value, oldSec.Iz, oldSec.J, oldSec.Wy, oldSec.Wz);
                        found = true;
                        break;

                    case "iz":
                    case "moment_of_inertia_z":
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area, oldSec.Iy, value, oldSec.J, oldSec.Wy, oldSec.Wz);
                        found = true;
                        break;

                    case "j":
                    case "torsional_constant":
                        newSec = new Section_Custom(oldSec.Mat, oldSec.Tag, oldSec.Area, oldSec.Iy, oldSec.Iz, value, oldSec.Wy, oldSec.Wz);
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
                        if (load is TB_Load_Point pointLoad && pointLoad.Loads.Count >= 6)
                        {
                            int componentIndex = ComponentNameToIndex(component);
                            if (componentIndex >= 0 && componentIndex < 6)
                            {
                                pointLoad.Loads[componentIndex] = value;
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

            return maxDisp;
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

            // Iterate over all elements and load cases
            foreach (var elem in model.Elem1Ds)
            {
                if (elem?.Nodes == null || elem.Nodes.Count < 2 || elem.Sec == null)
                    continue;

                // Get element forces for each load case
                for (int lcIndex = 0; lcIndex < (model.Disps?.Count ?? 0); lcIndex++)
                {
                    // VERIFIED: elem.Calc_Forces(lcIndex) returns 12-element force/moment array
                    // Indices: [Fx_i, Fy_i, Fz_i, Mx_i, My_i, Mz_i, Fx_j, Fy_j, Fz_j, Mx_j, My_j, Mz_j]
                    // See: ST_Element_1D.Calc_Forces() uses 12x1 force vector from element equilibrium.
                    try
                    {
                        var forces = elem.Calc_Forces(lcIndex);

                        if (forces != null && forces.Length >= 12)
                        {
                            // Extract bending moments (My and Mz at both ends)
                            double my_i = Math.Abs(forces[4]);
                            double mz_i = Math.Abs(forces[5]);
                            double my_j = Math.Abs(forces[10]);
                            double mz_j = Math.Abs(forces[11]);

                            // Compute bending stress: σ = M / W
                            double stress_y_i = elem.Sec.Wy > 0 ? my_i / elem.Sec.Wy : 0;
                            double stress_z_i = elem.Sec.Wz > 0 ? mz_i / elem.Sec.Wz : 0;
                            double stress_y_j = elem.Sec.Wy > 0 ? my_j / elem.Sec.Wy : 0;
                            double stress_z_j = elem.Sec.Wz > 0 ? mz_j / elem.Sec.Wz : 0;

                            // Combined bending stress (simplified)
                            double localMax = Math.Max(
                                Math.Max(stress_y_i, stress_z_i),
                                Math.Max(stress_y_j, stress_z_j));

                            if (localMax > maxStress)
                                maxStress = localMax;
                        }
                    }
                    catch
                    {
                        // If forces cannot be computed for this load case, skip
                        continue;
                    }
                }
            }

            return maxStress;
        }
    }
}
