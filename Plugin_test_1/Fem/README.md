# FEM module — Line → Beam analysis

All components are under **NTNU** → **FEM · …**.

## Workflow

1. **Material** — E [MPa], G [MPa], Gamma [kN/m³], Fy [MPa]
2. **Section RHS** or **Section Custom**
   - RHS: right-click to pick from catalog (hot-finished EN 10210-2)
   - Custom: manually input Area [mm²], Iy, Iz, J [mm⁴]
3. **Line to Beam** — Lines + Section → beam elements
4. **Support** — Points + condition string (e.g. `111111` = fully fixed)
5. **Point load** — Position, force [kN], moment [kNm], load case
6. **Assembly** — Elements + Supports + Loads → TB_Model
7. **Solve Linear Static** → solved model
8. **Deformed_Shape** / **Elem1D_Forces** — displacement and force previews

**Units:** Lines in Rhino units (solver uses m for length). Loads in kN/kNm. Sections in mm. Material E in N/mm².
