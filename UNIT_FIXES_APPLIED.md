# Unit Analysis & Fixes Applied

## Issues Found

### 1. **BeamMechanics.MomentAt() Docstring Error**
- **Problem**: Docstring said "G: permanent load [kNm]" but the code actually expects [kN/m]
- **Evidence**: The formula `R_A = G * L / 2.0` treats G as a distributed load
  - If G were in [kNm]: R_A would be [kNm·m] ❌ (wrong units)
  - If G is in [kN/m]: R_A is [kN] ✓ (correct for reaction)
- **Fixed**: Updated docstring to clarify "G: permanent load [kN/m] (distributed load per unit length)"

### 2. **MonteCarloSim Input Labels Ambiguous**
- **Problem**: Inputs labeled "Characteristic permanent load" and "Characteristic variable load" with no units specified
- **Fixed**: 
  - Updated to "Characteristic permanent UDL [kN/m]"
  - Updated to "Characteristic point variable load [kN]"
  - Changed description for Rk to include "[MPa]" explicitly

### 3. **RandomVariable.BuildEurocodeRVs() Docstring Ambiguous**
- **Problem**: Documentation said Gk/Qk could be "[kN/m] or [kN]" (ambiguous)
- **Fixed**: Clarified as:
  - Gk: "Characteristic permanent UDL [kN/m]"
  - Qk: "Characteristic point variable load [kN]"

### 4. **MonteCarloSim Default Values Unrealistic**
- **Problem**: 
  - Wel default was 1e4 = 10,000 mm³ (unrealistically small for a beam section)
  - N default was 10,000 (too low for reliable Monte Carlo)
- **Fixed**:
  - Wel default now 200,000 mm³ (typical for IPE 200: 200 cm³ = 2e5 mm³)
  - N default now 100,000 (better statistical convergence)
  - Added realistic defaults: L=5m, a=L/2 (midspan load)

---

## Unit System Verification

### Limit State Calculation
```csharp
g = thR * fy * Wel - thE * M_Nmm

Left side (Resistance):
  thR [dimensionless] × fy [MPa] × Wel [mm³]
  = [dimensionless] × [N/mm²] × [mm³]
  = [N·mm]

Right side (Demand):
  thE [dimensionless] × M_Nmm [Nmm]
  = [dimensionless] × [N·mm]
  = [N·mm]
```
✓ **Both sides are [N·mm]** — units are consistent!

### Moment Calculation Chain
```
BeamMechanics.MaxMoment(L, G, Q, a)
  Input:  L [m], G [kN/m], Q [kN], a [m]
  Output: M_max [kNm]

In MonteCarloCalcs:
  M_Nmm = M_kNm * 1e6
  → converts [kNm] to [Nmm]
```
✓ **Conversion factor 1e6 is correct** (1 kNm = 1000 N × 1000 mm = 1e6 Nmm)

---

## Testing Recommendations

### Test Case 1: Safe Design
```
Gk = 5.0 [kN/m]       (low permanent load)
Qk = 5.0 [kN]         (low variable load)
Wel = 200,000 [mm³]   (large section)
L = 5.0 [m]
a = 2.5 [m]           (midspan)

Expected: High beta (> 2.0), low Pf (< 0.05)
```

### Test Case 2: Critical Design
```
Gk = 15.0 [kN/m]      (higher permanent load)
Qk = 30.0 [kN]        (higher variable load)
Wel = 100,000 [mm³]   (smaller section)
L = 6.0 [m]
a = 3.0 [m]

Expected: Medium beta (1-2), medium Pf (0.01-0.05)
```

### Test Case 3: Unsafe Design
```
Gk = 20.0 [kN/m]      (high permanent load)
Qk = 50.0 [kN]        (high variable load)
Wel = 50,000 [mm³]    (very small section)
L = 8.0 [m]
a = 4.0 [m]

Expected: Low beta (< 1.0), high Pf (> 0.05)
```

---

## Code Changes Summary

| File | Change | Status |
|------|--------|--------|
| `BeamMechanics.cs` | Fixed docstring G units from [kNm] to [kN/m] | ✓ Done |
| `DistributionClasses.cs` | Clarified Gk=[kN/m], Qk=[kN] in docstring | ✓ Done |
| `MonteCarloSim.cs` | Updated input labels with explicit units | ✓ Done |
| `MonteCarloSim.cs` | Changed Wel default 1e4 → 200000 mm³ | ✓ Done |
| `MonteCarloSim.cs` | Changed N default 10000 → 100000 samples | ✓ Done |
| `MonteCarloSim.cs` | Added load position clamping for singularities | ✓ Done |

---

## Next Steps

1. **Test the component** with the new defaults and realistic inputs
2. **Verify Monte Carlo outputs** produce meaningful beta/Pf values
3. **Compare with FORM** results from FORMBeamComponent to ensure consistency
4. **Document expected ranges** for beta and Pf in component descriptions
