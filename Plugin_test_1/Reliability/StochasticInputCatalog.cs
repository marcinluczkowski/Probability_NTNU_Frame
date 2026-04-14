using System;
using System.Collections.Generic;

namespace Plugin_test_1.Reliability
{
    public class StochasticInputData
    {
        public string DistributionType { get; }

        public double? mean { get; }
        public double COV { get; }
        public double Percentile { get; }

        public StochasticInputData(string distributionType, double? mean, double cov, double percentile)
        {
            DistributionType = distributionType;
            mean = null;
            COV = cov;
            Percentile = percentile;
        }
    }

    public static class StochasticInputCatalog
    {
        private static readonly StochasticInputData DefaultMaterial = new StochasticInputData("lognormal", null, 0.1, 0.05);
        private static readonly StochasticInputData DefaultLoad = new StochasticInputData("normal", null, 0.1, 0.05);
        private static readonly Dictionary<string, StochasticInputData> MaterialMap =
            new Dictionary<string, StochasticInputData>(StringComparer.OrdinalIgnoreCase)
            {
                { "steel", new StochasticInputData("lognormal", null, 0.05, 0.01) },
                { "concrete", new StochasticInputData("lognormal", null, 0.10, 0.05) },
                { "timber", new StochasticInputData("lognormal", null, 0.15, 0.05) }
            };

        private static readonly Dictionary<string, StochasticInputData> LoadTypeMap =
            new Dictionary<string, StochasticInputData>(StringComparer.OrdinalIgnoreCase)
            {
                { "permanent", new StochasticInputData("normal", null, 0.1, 0.50) },
                { "variable", new StochasticInputData("gumbel", null, 0.26, 0.98) }
            };

        public static bool HasMaterial(string material)
        {
            if (string.IsNullOrWhiteSpace(material)) return false;
            return MaterialMap.ContainsKey(material.Trim());
        }

        public static bool HasLoadType(string loadType)
        {
            if (string.IsNullOrWhiteSpace(loadType)) return false;
            return LoadTypeMap.ContainsKey(loadType.Trim());
        }

        public static StochasticInputData GetMaterialInput(string material)
        {
            if (string.IsNullOrWhiteSpace(material)) return DefaultMaterial;
            return MaterialMap.TryGetValue(material.Trim(), out var data) ? data : DefaultMaterial;
        }

        public static StochasticInputData GetLoadInput(string loadType)
        {
            if (string.IsNullOrWhiteSpace(loadType)) return DefaultLoad;
            return LoadTypeMap.TryGetValue(loadType.Trim(), out var data) ? data : DefaultLoad;
        }
    }
}
