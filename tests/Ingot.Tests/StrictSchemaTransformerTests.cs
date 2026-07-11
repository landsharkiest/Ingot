using System.Text.Json;
using Ingot.Internal.Schema;
using Xunit;

namespace Ingot.Tests;

public sealed class StrictSchemaTransformerTests
{
    [Fact]
    public void Transform_VisitsPropertiesDefinitionsAndNestedObjects()
    {
        var schema = Parse(
            """
            {
              "type": "object",
              "properties": {
                "issuedOn": { "type": "string", "format": "date" },
                "customer": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "minLength": 2 }
                  }
                }
              },
              "$defs": {
                "line": {
                  "type": "object",
                  "properties": {
                    "sku": { "type": "string", "pattern": "^[A-Z]+$" }
                  }
                }
              },
              "definitions": {
                "legacy": {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string", "format": "uuid" }
                  }
                }
              }
            }
            """);

        var transformed = StrictSchemaTransformer.Transform(schema);
        var root = transformed;

        AssertStrictObject(root, "issuedOn", "customer");
        Assert.Contains("ISO 8601 date", Property(root, "issuedOn").GetProperty("description").GetString());
        Assert.False(Property(root, "issuedOn").TryGetProperty("format", out _));

        var customer = Property(root, "customer");
        AssertStrictObject(customer, "name");
        Assert.Contains("minLength: 2", Property(customer, "name").GetProperty("description").GetString());
        Assert.False(Property(customer, "name").TryGetProperty("minLength", out _));

        var line = root.GetProperty("$defs").GetProperty("line");
        AssertStrictObject(line, "sku");
        Assert.Contains("pattern:", Property(line, "sku").GetProperty("description").GetString());
        Assert.False(Property(line, "sku").TryGetProperty("pattern", out _));

        var legacy = root.GetProperty("definitions").GetProperty("legacy");
        AssertStrictObject(legacy, "id");
        Assert.Contains("UUID", Property(legacy, "id").GetProperty("description").GetString());
        Assert.False(Property(legacy, "id").TryGetProperty("format", out _));
    }

    [Fact]
    public void Transform_VisitsItemsAdditionalPropertiesAndCompositions()
    {
        var schema = Parse(
            """
            {
              "type": "object",
              "properties": {
                "lines": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "quantity": { "type": "integer", "minimum": 1 }
                    }
                  }
                },
                "metadata": {
                  "type": "object",
                  "additionalProperties": { "type": "string", "format": "uri" }
                },
                "choice": {
                  "anyOf": [
                    { "type": "object", "properties": { "a": { "type": "string", "maxLength": 3 } } },
                    { "allOf": [
                      { "type": "object", "properties": { "b": { "type": "string", "format": "email" } } }
                    ] },
                    { "oneOf": [
                      { "type": "object", "properties": { "c": { "type": "number", "multipleOf": 2 } } }
                    ] }
                  ]
                }
              }
            }
            """);

        var transformed = StrictSchemaTransformer.Transform(schema);

        var item = Property(transformed, "lines").GetProperty("items");
        AssertStrictObject(item, "quantity");
        Assert.Contains("minimum: 1", Property(item, "quantity").GetProperty("description").GetString());

        var dictionaryValue = Property(transformed, "metadata").GetProperty("additionalProperties");
        Assert.Contains("absolute URI", dictionaryValue.GetProperty("description").GetString());
        Assert.False(dictionaryValue.TryGetProperty("format", out _));

        var anyOf = Property(transformed, "choice").GetProperty("anyOf");
        var a = anyOf[0];
        AssertStrictObject(a, "a");
        Assert.False(Property(a, "a").TryGetProperty("maxLength", out _));

        var b = anyOf[1].GetProperty("allOf")[0];
        AssertStrictObject(b, "b");
        Assert.False(Property(b, "b").TryGetProperty("format", out _));

        var c = anyOf[2].GetProperty("oneOf")[0];
        AssertStrictObject(c, "c");
        Assert.False(Property(c, "c").TryGetProperty("multipleOf", out _));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Property(JsonElement schema, string name) =>
        schema.GetProperty("properties").GetProperty(name);

    private static void AssertStrictObject(JsonElement schema, params string[] required)
    {
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(required, schema.GetProperty("required").EnumerateArray().Select(static e => e.GetString()));
    }
}
