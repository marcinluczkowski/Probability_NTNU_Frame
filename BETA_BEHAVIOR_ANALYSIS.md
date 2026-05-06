# Beta Index Behavior at High Utilization: Technical Analysis

## Your Observation
> "With utilization > 2, beta is increasing (shouldn't it be negative?)"

## Short Answer
**Yes, you're right to be suspicious.** This suggests either:
1. The HLRF algorithm is converging to a local non-optimal point
2. The limit state definition needs adjustment
3. The algorithm doesn't handle highly non-linear problems well

---

## Technical Background

### Utilization Definition
```
Utilization = M_Ed / M_Rd  (at design load level)

- Util < 1.0:  Structure is SAFE (Capacity > Demand)
- Util = 1.0:  At limit state (Capacity = Demand)
- Util > 1.0:  Structure is UNSAFE (Demand > Capacity)
- Util > 2.0:  Severely over-stressed
```

### FORM Limit State in Your Code
```csharp
G = θ_R * W_el * fy  -  θ_E * M_max(G,Q) * 1e6

where:
  G = Limit state function
  θ_R, θ_E = Model uncertainties (usually close to 1.0)
  W_el * fy = Resistance capacity [N*mm]
  M_max * 1e6 = Load effect [N*mm]

Interpretation:
  G > 0: Safe (Capacity > Demand)
  G = 0: At limit state (Capacity = Demand)
  G < 0: Unsafe (Demand > Capacity)
```

### What FORM Computes
FORM finds the **design point** = the point on the limit state surface (G=0) closest to the origin in standard normal space.

The **reliability index β** = distance from origin to design point.

```
β = ||u*||  where u* is the design point location

Interpretation:
  β > 0: Design point exists (limit state passes through space)
  β = 0: Origin is on limit state (mean values are at failure)
  β < 0: Mathematically undefined in standard formulation
```

---

## Why Beta Increases at High Utilization

When Utilization > 1 (structure is unsafe):

1. **At the mean load level:** G < 0 (deeply in failure region)
2. **HLRF searches for G = 0:** But there might not be a realistic G=0 surface nearby
3. **Algorithm behavior:**
   - Starts at origin (u = [0,0,0,0,0])
   - Searches along gradient direction
   - Tries to find where G = 0
   - In over-stressed regime, this can cause it to converge to numerical artifacts

4. **Why β increases:**
   - The algorithm moves further from origin
   - Distance ||u*|| increases
   - β = ||u*|| also increases
   - **But this doesn't mean it's safer** - it means the limit state is far away

---

## The Problem: Non-Convexity at High Stress

The limit state surface `G(u) = 0` in the heavily over-stressed region is:
- Highly non-linear
- May have local minima away from the true design point
- Makes HLRF unreliable

### Example Scenario

```
Util = 0.5 (Safe)
  G > 0 at mean
  HLRF finds design point
  β ≈ 2.5 (typical for safe structure)

Util = 1.0 (Marginal)
  G ≈ 0 at mean
  HLRF finds design point at origin
  β ≈ 0 (as expected)

Util = 1.5 (Over-stressed)
  G << 0 at mean
  HLRF searches for G = 0
  Might find local extremum
  β can be any value (unreliable!)

Util = 2.5 (Severely over-stressed)
  G very << 0 at mean
  HLRF converges slowly or to wrong point
  β might increase (counterintuitive!)
```

---

## Solutions

### **Solution 1: Flip the Limit State (RECOMMENDED)**

Change the limit state definition to:
```csharp
// Current (problematic at high utilization):
G = θ_R * W_el * fy  -  θ_E * M_max(G,Q) * 1e6

// Flipped (better for over-stressed designs):
G = θ_E * M_max(G,Q) * 1e6  -  θ_R * W_el * fy

// Or equivalently (simpler):
G = 1.0  -  (θ_E * M_max(G,Q) * 1e6) / (θ_R * W_el * fy)
```

**Advantages:**
- Now: G > 0 means UNSAFE, G < 0 means SAFE
- β can become negative, clearly indicating failure
- Better captures probabilistic intent

**Implementation in FORM.cs:**
```csharp
private static double G_physical(double[] x, double W_el, double L, double a)
{
    double fy  = x[0], G = x[1], Q = x[2], thR = x[3], thE = x[4];
    double M   = BeamMechanics.MaxMoment(L, G, Q, a);

    // FLIPPED: Demand - Capacity (instead of Capacity - Demand)
    return thE * M * 1e6 - thR * W_el * fy;  // Now positive means failure
}
```

**Then interpret:**
- β > 3.0: Safe (failure unlikely)
- β ≈ 0: At limit state
- β < 0: Unsafe (failure likely)

---

### **Solution 2: Use Absolute Distance + Sign Check**

Keep current limit state but track convergence:

```csharp
// In FORM.Run output, also return:
public static double Run(..., out double beta, out bool converged, 
                         out bool isInFailureRegion)
{
    // ... existing code ...

    // Check if mean is in failure region
    double[] xMean = new double[rvs.Length];
    for (int i = 0; i < rvs.Length; i++)
        xMean[i] = rvs[i].Mean;

    double G_mean = G_physical(xMean, ...);
    isInFailureRegion = G_mean < 0;

    // If in failure region, return negative beta
    if (isInFailureRegion)
        beta = -beta;  // Flip sign to indicate failure

    return beta;
}
```

---

### **Solution 3: Switch to Monte Carlo at High Utilization**

For Util > 1.5, FORM becomes unreliable. Use Monte Carlo instead:

```csharp
if (utilization > 1.5)
{
    // Use Monte Carlo Simulation instead of FORM
    beta = MonteCarloReliability(rvs, ...);
}
else
{
    // Use FORM for well-behaved cases
    beta = FORM.Run(...);
}
```

---

## Recommendation

**I recommend Solution 1 (Flip the Limit State):**

1. **More intuitive:** G > 0 = unsafe, G < 0 = safe
2. **Better behavior:** β can go negative naturally
3. **Matches convention:** Resistance - Load Effect is less standard
4. **Easy to implement:** One-line change in G_physical()

**After flipping, expect:**
- Util = 0.5: β ≈ 2.5 (safe)
- Util = 1.0: β ≈ 0 (at limit)
- Util = 1.5: β ≈ -1.0 (unsafe)
- Util = 2.5: β ≈ -3.0 (severely unsafe)

---

## How to Verify Your Fix

Test with known cases:

```csharp
// Test 1: Safe design
var result = FORM.Run(W_el_large, L, a, rvs, out beta, out converged);
Assert.IsTrue(beta > 0, "Safe design should have β > 0");

// Test 2: Marginal design (Util ≈ 1)
var result = FORM.Run(W_el_critical, L, a, rvs, out beta, out converged);
Assert.IsTrue(Math.Abs(beta) < 0.5, "Critical design should have β ≈ 0");

// Test 3: Over-stressed design
var result = FORM.Run(W_el_small, L, a, rvs, out beta, out converged);
Assert.IsTrue(beta < 0, "Over-stressed design should have β < 0");
```

---

## References

- Melchers, R. E. (1999). Structural Reliability Analysis and Prediction
- Haldar, A., & Mahadevan, S. (2000). Probability, Reliability, and Statistical Methods in Engineering Design
- EN 1990: Eurocode 0 – Basis of structural design

---

**Conclusion:** Your intuition about negative β for over-stressed designs is correct. The current behavior is likely an artifact of how HLRF handles non-linear regions. Flipping the limit state function will fix this and make the results more interpretable.
