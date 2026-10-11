namespace Plugin_test_1.Reliability
{
    /// <summary>
    /// Partial factors and design constants for Eurocode calculations.
    /// Values from the Norwegian NA:
    /// NS-EN 1990: ULS (STR) is the less favourable of expressions 6.10a and 6.10b
    /// NS-EN 1993-1-1: gamma_M0 = gamma_M1 = 1.05, gamma_M2 = 1.25
    /// </summary>
    public static class EurocodeConstants
    {
        // - Actions (EN 1990, Table A1.2(A)) - 

        // 6.10: GammaG * Gk + GammaQ * Qk 
        // 
        // - actions (EN 1990, Table A1.2(B)) - 
        // 6.10a: GammaG * Gk + GammaQ * psi0 * Qk
        // 6.10b: Xi * GammaG * Gk + GammaQ * Qk
        //
        // psi0 depends on load category, not a constant.
        
        /// <summary>Partial factor for unfavorable permanent loads (ULS).</summary>
        public const double GammaG = 1.35;

        /// <summary>Partial factor for favorable permanent loads (ULS).</summary>
        public const double GammaG_fav = 1.0;

        /// <summary>Reduction factor Xi on unfavorable peromanent actions in 6.10b - NA-EN 1990.</summary>
        public const double Xi = 0.89;

        /// <summary>Partial factor for variable loads (ULS).</summary>
        public const double GammaQ = 1.50;

        // - Resistance (SN-EN 1993-1-1, 8.1) -

        /// <summary>Partial factor for resistance of cross-sections (steel, yielding).</summary>
        public const double GammaM = 1.05;

        /// <summary>Partial factor for instability assessed by member checks (steel).</summary>
        public const double GammaM1 = 1.05;

        /// <summary>Partial factor for resistance of cross-section in tension to fracture (steel).</summary>
        public const double GammaM2 = 1.25;

        /// <summary>
        /// Reference perios in years of the reliability target. 
        /// Beta = 3.8 for RC2 in EN 1990, Annex A.1.2.2.
        /// Also the variable-load maxima used in the reliability analysis.
        /// </summary>
        public const int ReferencePeriodsYears = 50;
    }
}
