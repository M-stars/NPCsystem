using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace SocialSimulation.Graph
{
    public enum BlackboardValueType { Boolean, Integer, Float, String, EntityId }
    public enum BlackboardScope { Agent, Goal, Action, Session }
    public enum BlackboardAccess { ReadOnly, ReadWrite }
    public enum BlackboardSavePolicy { Transient, Checkpoint, Persistent }

    [Serializable]
    public sealed class BlackboardVariable
    {
        public string Id;
        public string Name;
        public BlackboardValueType Type;
        public string DefaultValue;
        public BlackboardScope Scope;
        public BlackboardAccess Access = BlackboardAccess.ReadWrite;
        public BlackboardSavePolicy SavePolicy = BlackboardSavePolicy.Checkpoint;
    }

    public sealed class CompiledBlackboardVariable
    {
        public readonly string Id;
        public readonly string Name;
        public readonly BlackboardValueType Type;
        public readonly string DefaultValue;
        public readonly BlackboardScope Scope;
        public readonly BlackboardAccess Access;
        public readonly BlackboardSavePolicy SavePolicy;

        internal CompiledBlackboardVariable(BlackboardVariable source, string normalizedDefault)
        {
            Id = source.Id; Name = source.Name; Type = source.Type;
            DefaultValue = normalizedDefault; Scope = source.Scope;
            Access = source.Access; SavePolicy = source.SavePolicy;
        }
    }

    public sealed class CompiledBlackboardSchema
    {
        public readonly IReadOnlyDictionary<string, CompiledBlackboardVariable> Variables;

        internal CompiledBlackboardSchema(Dictionary<string, CompiledBlackboardVariable> variables)
        { Variables = new ReadOnlyDictionary<string, CompiledBlackboardVariable>(variables); }

        public bool TryGet(string id, out CompiledBlackboardVariable variable)
        { return Variables.TryGetValue(id ?? "", out variable); }
    }

    [Serializable]
    public sealed class BlackboardValueCheckpoint
    {
        public string VariableId;
        public string Value;
    }

    /// <summary>Values belong to one behavior instance; compiled definitions are shared and immutable.</summary>
    public sealed class GraphBlackboard
    {
        private readonly CompiledBlackboardSchema schema;
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        public GraphBlackboard(CompiledBlackboardSchema schema)
        { this.schema = schema ?? throw new ArgumentNullException("schema"); }

        public CompiledBlackboardSchema Schema { get { return schema; } }

        public string GetRaw(string variableId)
        {
            if (!schema.TryGet(variableId, out var variable))
                throw new KeyNotFoundException("Unknown blackboard variable: " + variableId);
            return values.TryGetValue(variable.Id, out var value) ? value : variable.DefaultValue;
        }

        public bool TrySetRaw(string variableId, string value)
        {
            if (!schema.TryGet(variableId, out var variable) || variable.Access != BlackboardAccess.ReadWrite ||
                !BlackboardValueCodec.TryNormalize(variable.Type, value, out var normalized)) return false;
            values[variable.Id] = normalized;
            return true;
        }

        public bool GetBoolean(string id) { return bool.Parse(GetRaw(id)); }
        public int GetInteger(string id) { return int.Parse(GetRaw(id), CultureInfo.InvariantCulture); }
        public double GetFloat(string id) { return double.Parse(GetRaw(id), CultureInfo.InvariantCulture); }
        public string GetString(string id) { return GetRaw(id); }
        public bool SetBoolean(string id, bool value) { return TrySetRaw(id, value ? "true" : "false"); }
        public bool SetInteger(string id, int value)
        { return TrySetRaw(id, value.ToString(CultureInfo.InvariantCulture)); }
        public bool SetFloat(string id, double value)
        { return TrySetRaw(id, value.ToString("R", CultureInfo.InvariantCulture)); }
        public bool SetString(string id, string value) { return TrySetRaw(id, value); }

        public void ClearScope(BlackboardScope scope)
        {
            var remove = new List<string>();
            foreach (var pair in values)
                if (schema.TryGet(pair.Key, out var variable) && variable.Scope == scope)
                    remove.Add(pair.Key);
            foreach (var id in remove) values.Remove(id);
        }

        public List<BlackboardValueCheckpoint> CaptureCheckpoint()
        {
            var result = new List<BlackboardValueCheckpoint>();
            foreach (var pair in values)
                if (schema.TryGet(pair.Key, out var variable) &&
                    variable.SavePolicy != BlackboardSavePolicy.Transient)
                    result.Add(new BlackboardValueCheckpoint { VariableId = pair.Key, Value = pair.Value });
            result.Sort((a, b) => string.CompareOrdinal(a.VariableId, b.VariableId));
            return result;
        }

        public void RestoreCheckpoint(List<BlackboardValueCheckpoint> saved)
        {
            if (saved == null) throw new InvalidOperationException("Blackboard checkpoint is missing.");
            if (values.Count != 0) throw new InvalidOperationException("Restore requires a fresh blackboard.");
            var pending = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in saved)
            {
                if (item == null || !schema.TryGet(item.VariableId, out var variable) ||
                    variable.SavePolicy == BlackboardSavePolicy.Transient ||
                    !BlackboardValueCodec.TryNormalize(variable.Type, item.Value, out var normalized) ||
                    !pending.TryAdd(variable.Id, normalized))
                    throw new InvalidOperationException("Blackboard checkpoint contains an invalid variable.");
            }
            foreach (var pair in pending) values.Add(pair.Key, pair.Value);
        }

        public bool Check(string variableId, string comparison, string expected)
        {
            if (!schema.TryGet(variableId, out var variable) ||
                !BlackboardValueCodec.TryNormalize(variable.Type, expected, out var normalized)) return false;
            var actual = GetRaw(variableId);
            int order;
            if (variable.Type == BlackboardValueType.Integer || variable.Type == BlackboardValueType.Float)
                order = double.Parse(actual, CultureInfo.InvariantCulture)
                    .CompareTo(double.Parse(normalized, CultureInfo.InvariantCulture));
            else order = string.CompareOrdinal(actual, normalized);
            switch (comparison)
            {
                case "eq": return order == 0;
                case "ne": return order != 0;
                case "gt": return order > 0;
                case "gte": return order >= 0;
                case "lt": return order < 0;
                case "lte": return order <= 0;
                default: return false;
            }
        }
    }

    public static class BlackboardValueCodec
    {
        public static bool TryNormalize(BlackboardValueType type, string input, out string normalized)
        {
            normalized = null;
            switch (type)
            {
                case BlackboardValueType.Boolean:
                    if (!bool.TryParse(input, out var boolean)) return false;
                    normalized = boolean ? "true" : "false";
                    return true;
                case BlackboardValueType.Integer:
                    if (!int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return false;
                    normalized = integer.ToString(CultureInfo.InvariantCulture);
                    return true;
                case BlackboardValueType.Float:
                    if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        double.IsNaN(number) || double.IsInfinity(number)) return false;
                    normalized = number.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case BlackboardValueType.EntityId:
                    if (string.IsNullOrWhiteSpace(input)) return false;
                    normalized = input.Trim();
                    return true;
                case BlackboardValueType.String:
                    normalized = input ?? "";
                    return true;
                default: return false;
            }
        }
    }
}
