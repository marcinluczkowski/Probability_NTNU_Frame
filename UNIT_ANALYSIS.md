# Unit Analysis: Monte Carlo Simulation

## Current Unit Flow Analysis

### 1. **BeamMechanics.cs** (Reference Implementation)
```
Inputs:
  L      [m]       - Beam span
  G      [kN/m]    - Distributed load (UDL) ← KEY: This is per unit length!
  Q      [kN]      - Point load
  a      [m]       - Load position

Output:
  M_max  [kNm]     - Maximum bending moment
```

**Evidence:** Line 20 docstring says "G: permanent load [kNm]" but this is MISLEADING.
The actual code at line 22-23:
```csharp
double R_A = G * L / 2.0 + Q * (L - a) / L;
double M   = R_A * z - G * z * z / 2.0;
```
- If G were [kNm], then `G * L / 2.0` would give [kNm·m] (WRONG)
- But if G is [kN/m], then `G * L / 2.0` gives [kN·m] = [kNm] (CORRECT)
- Similarly: `G * z * z / 2.0` = [kN/m] × [m²] = [kNm] ✓

**CONCLUSION: BeamMechanics expects G in [kN/m], not [kNm]**

---

### 2. **FORMBeamComponent.cs** Input Parameters
```
Line 36: pManager.AddNumberParameter("G_k",     "Gk",      "Characteristic UDL [kN/m]", ...)
Line 37: pManager.AddNumberParameter("Q_k",     "Qk",      "Characteristic point load [kN]", ...)
```
✓ **CORRECT**: Gk is labeled as [kN/m], Qk as [kN]

Line 57-58 calls:
```csharp
var rvs = RandomVariable.BuildEurocodeRVs(fyk, Gk, Qk);
```

---

### 3. **RandomVariable.BuildEurocodeRVs()** Documentation
```
/// Parameters:
///   fyk  : Characteristic yield strength [MPa]
///   Gk   : Characteristic permanent load [kN/m] or [kN]     ← AMBIGUOUS!
///   Qk   : Characteristic variable load [kN/m] or [kN]     ← AMBIGUOUS!
```

The docstring is vague but based on usage with `FORMBeamComponent`, it should be:
- Gk: [kN/m] - distributed load
- Qk: [kN] - point load

---

### 4. **MonteCarloSim.cs** Input Parameters
```
Line 29: pManager.AddNumberParameter("Characteristic permanent load", "Gk", 
         "Characteristic permanent load **effect value**", ...)
Line 30: pManager.AddNumberParameter("Characteristic variable load", "Qk",
         "Characteristic variable load **effect value**", ...)
```

**PROBLEM**: No units specified! The default values are:
- Line 59: `double Gk = 10.0;`
- Line 60: `double Qk = 20.0;`

But what are the units? [kN/m]? [kN]? [MN]?

---

### 5. **MonteCarloCalcs.EvaluateLimitState()** Calculation
```csharp
double M_kNm = BeamMechanics.MaxMoment(L_m, G, Q, a);
double M_Nmm = M_kNm * 1e6;

g[j] = thR * fy * W_mm3 - thE * M_Nmm;
```

**Unit analysis of limit state:**
```
thR * fy * W_mm3  = [dimensionless] × [MPa] × [mm³]
                  = [dimensionless] × [N/mm²] × [mm³]
                  = [N·mm]

thE * M_Nmm       = [dimensionless] × [Nmm]
                  = [N·mm]
```

✓ **BOTH SIDES IN [N·mm]** - CONSISTENT!

---

## Unit Mismatch Issues Found

### **ISSUE 1: BeamMechanics.MomentAt() Docstring is Wrong**
- **Current docstring** (line 20): "G: permanent load [kNm]"
- **Should be**: "G: permanent load [kN/m] (distributed load per unit length)"

### **ISSUE 2: MonteCarloSim.cs Missing Unit Specifications**
- **Current**: Gk and Qk inputs have no units documented
- **Should specify**: 
  - Gk: [kN/m] - Characteristic permanent distributed load
  - Qk: [kN] - Characteristic point variable load

### **ISSUE 3: Potential Semantic Confusion in Input Names**
- "Characteristic permanent load" could mean [kN/m] or [kN]
- "Characteristic variable load" could mean [kN/m] or [kN]
- **Recommendation**: More explicit naming

---

## Verification: Do the Units Actually Work?

Let's trace a sample calculation with reasonable beam dimensions:

```
Inputs:
  Gk = 10.0 [kN/m]          → Load effect (UDL)
  Qk = 20.0 [kN]            → Load effect (point load)
  L = 5.0 [m]               → Span
  Wel = 1e4 = 10,000 [mm³]  → Section modulus
  fyk = 355 [MPa]           → Yield strength

Expected moment from FORM:
  M_max ≈ 10 * (5²/8) + 20 * (2.5 * 2.5 / 5)
        ≈ 10 * 3.125 + 20 * 1.25
        ≈ 31.25 + 25 = 56.25 [kNm]
  M_Nmm = 56.25 * 1e6 = 56.25e6 [Nmm]

Resistance side (at mean values):
  fy_mean ≈ 355 * 1.05 ≈ 372.75 [MPa]
  thR_mean ≈ 1.15
  thR * fy * Wel = 1.15 * 372.75 * 10,000 ≈ 4.29e6 [N·mm]

Demand side:
  thE_mean ≈ 1.0
  thE * M = 1.0 * 56.25e6 = 56.25e6 [N·mm]

Limit state:
  g = 4.29e6 - 56.25e6 = -51.96e6  [N·mm]  (FAILURE!)
```

This is HIGHLY UNSAFE! The resistance is much smaller than demand. 

**This suggests either:**
1. The default inputs (Gk=10.0, Qk=20.0) are unrealistically high loads
2. Or the section modulus default (Wel=1e4) is unrealistically small
3. Or there's a unit scale mismatch

---

## Recommended Fixes

### Fix 1: Correct BeamMechanics Docstring
```csharp
/// <summary>
/// Bending moment [kNm] at position z [m] along the beam.
/// G: permanent load [kN/m] (distributed), Q: point load [kN] at position a [m].
/// </summary>
```

### Fix 2: Update MonteCarloSim Input Descriptions
```csharp
pManager.AddNumberParameter("Characteristic permanent load", "Gk", 
    "Characteristic permanent UDL [kN/m]", GH_ParamAccess.item, 10.0);
pManager.AddNumberParameter("Characteristic variable load", "Qk",
    "Characteristic point variable load [kN]", GH_ParamAccess.item, 20.0);
pManager.AddNumberParameter("Section Modulus", "Wel",
    "Elastic section modulus [mm³]", GH_ParamAccess.item, 100000.0);  // More realistic
```

### Fix 3: Update RandomVariable.BuildEurocodeRVs Docstring
```csharp
/// Parameters:
///   fyk  : Characteristic yield strength [MPa]
///   Gk   : Characteristic permanent UDL [kN/m]
///   Qk   : Characteristic point variable load [kN]
```

### Fix 4: Verify Limit State Orientation
The current limit state is: `g = thR * fy * Wel - thE * M`
- When g > 0: **Safe** (resistance exceeds demand)
- When g < 0: **Failure** (demand exceeds resistance)

This matches the code in FORM.cs, so sign convention is correct.

---

## Summary

✓ The **calculation units are internally consistent** ([N·mm] on both sides)
✗ The **documentation is incomplete/misleading** (Gk units not specified, BeamMechanics docstring wrong)
⚠ The **default values may be unrealistic** (Wel=10,000 mm³ is very small; typical IPE 200 has ~200 cm³ = 2e5 mm³)

**Action**: Run the component with more realistic section modulus values and verify the outputs!
