# Git Workflow: Save Current State Before FORM Beta Fix

## Step 1: Create and Switch to New Branch

Run this in PowerShell:
```powershell
cd "C:\Users\juliakg\source\repos\Propability_NTNU_v1"
git checkout -b feature/form-beta-fix-jg
```

**What this does:**
- Creates a new branch called `feature/form-beta-fix-jg`
- Switches to that branch
- All changes from `develop_3_JG` are carried over

## Step 2: Stage and Commit Current Changes

```powershell
# Stage all changes and untracked files
git add -A

# View what will be committed
git status

# Commit with descriptive message
git commit -m "feat: integrate Reliability_TryOuts components and fix MathNet dependencies

- Add EurocodeConstants class for centralized partial factors
- Add BuildEurocodeRVs factory method to RandomVariable
- Fix RandomVariable, FORM, FORMBeamComponent to use MathNet.Numerics
- Create ReliabilityIndexCalculator component for FORM-based reliability
- Add integration documentation and analysis tools
- All code builds successfully

Related to: Reliability_TryOuts integration and FORM beta index behavior analysis"
```

## Step 3: Verify Commit

```powershell
# See your new commit
git log --oneline -5

# Should show your commit at the top
```

## Step 4: Push to Remote (Optional - Recommended)

```powershell
# Push the new branch to GitHub
git push -u origin feature/form-beta-fix-jg

# This creates the branch on GitHub so it's backed up
```

## Step 5: Ready for FORM Beta Fix

Once committed, you're safe to make the FORM.cs changes. If something goes wrong:
```powershell
# Revert all changes to previous commit
git reset --hard HEAD~1
```

---

## Current Changes Summary

**Modified Files:**
- ✅ ST_Element_1D.cs (deep copy fix)
- ✅ ST_Model.cs (deep copy fix)
- ✅ AlphaUtilizationSearch.cs (use deep copy)
- ✅ Plugin_test_1.csproj (build config)
- ✅ DistributionClasses.cs (add BuildEurocodeRVs)

**New Files:**
- ✅ ReliabilityIndexCalculator.cs (FORM component)
- ✅ EurocodeConstants.cs (partial factors)
- ✅ Reliability_TryOuts/* (integrated legacy code)
- ✅ Documentation files

All changes are functional and build successfully.

---

## Next Steps After Commit

Once you've committed, I can help you with:
1. **Implement FORM beta fix** - Flip limit state function
2. **Test the changes** - Verify beta behavior at different utilization levels
3. **Update documentation** - Explain the beta index interpretation
4. **Create pull request** - To merge back to develop_3_JG when ready

---

**Branch naming convention used:**
- `feature/` = New feature development
- `form-beta-fix` = What the branch does
- `-jg` = Your initials (Julia?)

This keeps history clean and searchable.
