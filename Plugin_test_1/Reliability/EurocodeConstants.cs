namespace Plugin_test_1.Reliability
{
    /// <summary>
    /// Eurocode EN 1990 / EN 1993 partial factors and design constants.
    /// These factors are used in limit state design and reliability calculations.
    /// </summary>
    public static class EurocodeConstants
    {
        /// <summary>Partial factor for permanent loads (ULS).</summary>
        public const double GammaG = 1.35;

        /// <summary>Partial factor for variable loads (ULS).</summary>
        public const double GammaQ = 1.50;

        /// <summary>Partial factor for material properties (steel).</summary>
        public const double GammaM = 1.15;

        /// <summary>
        /// Eurocode reference period for characteristic values.
        /// Most characteristic values are defined at a 50-year return period.
        /// </summary>
        public const int ReferencePeriodsYears = 50;
    }
}
