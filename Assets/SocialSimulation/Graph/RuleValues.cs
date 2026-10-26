using System;
using System.Globalization;

namespace SocialSimulation.Graph
{
    public enum RuleValueType { String, Number, Boolean }

    /// <summary>An event field explicitly declares its type and, for numbers, its unit.</summary>
    public sealed class RuleFieldValue
    {
        public readonly RuleValueType Type;
        public readonly string Value;
        public readonly string Unit;

        public RuleFieldValue(RuleValueType type, string value, string unit = "")
        { Type = type; Value = value ?? ""; Unit = unit ?? ""; }

        public static RuleFieldValue Number(double value, string unit = "")
        { return new RuleFieldValue(RuleValueType.Number, value.ToString("R", CultureInfo.InvariantCulture), unit); }

        public static RuleFieldValue Boolean(bool value)
        { return new RuleFieldValue(RuleValueType.Boolean, value ? "true" : "false"); }

        public static RuleFieldValue String(string value)
        { return new RuleFieldValue(RuleValueType.String, value); }
    }

    public static class RuleValueCodec
    {
        public static bool TryGetType(string id, out RuleValueType type)
        {
            switch (id)
            {
                case "string": type = RuleValueType.String; return true;
                case "number": type = RuleValueType.Number; return true;
                case "bool": type = RuleValueType.Boolean; return true;
                default: type = default; return false;
            }
        }

        public static string TypeId(RuleValueType type)
        {
            switch (type)
            {
                case RuleValueType.String: return "string";
                case RuleValueType.Number: return "number";
                case RuleValueType.Boolean: return "bool";
                default: return "unknown";
            }
        }

        public static bool IsComparisonSupported(RuleValueType type, string comparison)
        {
            if (comparison == "equals" || comparison == "notEqual") return true;
            return type == RuleValueType.Number && (comparison == "greater" || comparison == "greaterEqual" ||
                comparison == "less" || comparison == "lessEqual");
        }

        public static bool TryNormalize(RuleValueType type, string raw, out string normalized)
        {
            normalized = null;
            switch (type)
            {
                case RuleValueType.String:
                    normalized = raw ?? "";
                    return true;
                case RuleValueType.Boolean:
                    if (!bool.TryParse(raw, out var boolean)) return false;
                    normalized = boolean ? "true" : "false";
                    return true;
                case RuleValueType.Number:
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        double.IsNaN(number) || double.IsInfinity(number)) return false;
                    normalized = number.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                default: return false;
            }
        }

        /// <summary>Values must be normalized and have equal, explicit types and units before comparison.</summary>
        public static bool Compare(RuleValueType type, string actual, string expected, string comparison)
        {
            int order = type == RuleValueType.Number
                ? double.Parse(actual, CultureInfo.InvariantCulture).CompareTo(double.Parse(expected, CultureInfo.InvariantCulture))
                : string.CompareOrdinal(actual, expected);
            switch (comparison)
            {
                case "equals": return order == 0;
                case "notEqual": return order != 0;
                case "greater": return order > 0;
                case "greaterEqual": return order >= 0;
                case "less": return order < 0;
                case "lessEqual": return order <= 0;
                default: return false;
            }
        }
    }
}
