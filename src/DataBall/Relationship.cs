// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace squalor.DataBall
{
    /// <summary>
    /// Represents a relationship between columns, defining trigger and dependent fields.
    /// </summary>
    [JsonConverter(typeof(RelationshipJsonConverter))]
    public class Relationship
    {
        /// <summary>
        /// Gets or sets the name of the trigger field that, when changed, affects dependent fields.
        /// </summary>
        [JsonPropertyName("trigger")]
        public string TriggerField { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the list of dependent fields to reset when the trigger field changes.
        /// </summary>
        [JsonPropertyName("reset")]
        public List<string> ResetFields { get; set; } = new();
    }

    internal sealed class RelationshipJsonConverter : JsonConverter<Relationship>
    {
        public override Relationship Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected start of relationship object");

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            var trigger = string.Empty;
            if (TryGetPropertyInsensitive(root, "trigger", out var triggerEl) && triggerEl.ValueKind == JsonValueKind.String)
                trigger = triggerEl.GetString() ?? string.Empty;
            else if (TryGetPropertyInsensitive(root, "TriggerField", out triggerEl) && triggerEl.ValueKind == JsonValueKind.String)
                trigger = triggerEl.GetString() ?? string.Empty;

            List<string> reset;
            if (TryGetPropertyInsensitive(root, "reset", out var resetEl) && resetEl.ValueKind == JsonValueKind.Array)
                reset = ReadStringArray(resetEl);
            else if (TryGetPropertyInsensitive(root, "ResetFields", out resetEl) && resetEl.ValueKind == JsonValueKind.Array)
                reset = ReadStringArray(resetEl);
            else
                reset = new List<string>();

            return new Relationship
            {
                TriggerField = trigger,
                ResetFields = reset
            };
        }

        public override void Write(Utf8JsonWriter writer, Relationship value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("trigger", value.TriggerField ?? string.Empty);
            writer.WritePropertyName("reset");
            writer.WriteStartArray();
            if (value.ResetFields is not null)
            {
                foreach (var field in value.ResetFields)
                    writer.WriteStringValue(field);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static bool TryGetPropertyInsensitive(JsonElement obj, string name, out JsonElement value)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static List<string> ReadStringArray(JsonElement array)
        {
            var list = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString() ?? string.Empty);
            }
            return list;
        }
    }
}
