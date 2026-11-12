using System.Text.Json;

namespace Mimir.Core.Llm;

/// <summary>
/// Valida um JSON contra o subconjunto de JSON Schema que os meus prompts usam:
/// <c>type</c> objeto, texto, booleano, inteiro e lista; <c>required</c>, <c>enum</c>, <c>minLength</c>,
/// <c>minimum</c>, <c>items</c>, <c>maxItems</c> e <c>additionalProperties: false</c>.
/// </summary>
/// <remarks>
/// O provedor pode prometer respeitar o schema, mas a resposta continua vindo de fora do sistema.
/// Por isso a validação acontece aqui, com qualquer provedor. Palavra-chave fora desse subconjunto lança erro,
/// para o schema nunca parecer mais rigoroso do que a validação é.
/// </remarks>
public static class JsonSchemaValidator
{
    private static readonly HashSet<string> SupportedKeywords =
        new(["type", "properties", "required", "additionalProperties", "enum", "minLength", "minimum", "items", "maxItems"], StringComparer.Ordinal);

    /// <returns>A lista de problemas encontrados. Vazia quando o JSON é válido.</returns>
    public static IReadOnlyList<string> Validate(JsonElement value, JsonElement schema)
    {
        var problems = new List<string>();
        ValidateNode(value, schema, "$", problems);
        return problems;
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, string path, List<string> problems)
    {
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(keyword.Name))
            {
                throw new NotSupportedException($"O validador não conhece a palavra-chave '{keyword.Name}' do schema.");
            }
        }

        switch (schema.GetProperty("type").GetString())
        {
            case "object":
                ValidateObject(value, schema, path, problems);
                break;
            case "string":
                ValidateString(value, schema, path, problems);
                break;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problems.Add($"{path}: esperava verdadeiro ou falso.");
                }

                break;
            case "integer":
                ValidateInteger(value, schema, path, problems);
                break;
            case "array":
                ValidateArray(value, schema, path, problems);
                break;
            case var other:
                throw new NotSupportedException($"O validador não conhece o tipo '{other}' do schema.");
        }
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, List<string> problems)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{path}: esperava um objeto.");
            return;
        }

        var properties = schema.GetProperty("properties");

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    problems.Add($"{path}.{name}: campo obrigatório ausente.");
                }
            }
        }

        var closed = schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.False;

        foreach (var property in value.EnumerateObject())
        {
            if (properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateNode(property.Value, propertySchema, $"{path}.{property.Name}", problems);
            }
            else if (closed)
            {
                problems.Add($"{path}.{property.Name}: campo não previsto.");
            }
        }
    }

    private static void ValidateInteger(JsonElement value, JsonElement schema, string path, List<string> problems)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            problems.Add($"{path}: esperava um número inteiro.");
            return;
        }

        if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetInt32())
        {
            problems.Add($"{path}: {number} é menor que o mínimo {minimum.GetInt32()}.");
        }
    }

    private static void ValidateArray(JsonElement value, JsonElement schema, string path, List<string> problems)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{path}: esperava uma lista.");
            return;
        }

        if (schema.TryGetProperty("maxItems", out var maxItems) && value.GetArrayLength() > maxItems.GetInt32())
        {
            problems.Add($"{path}: mais de {maxItems.GetInt32()} itens.");
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateNode(item, items, $"{path}[{index++}]", problems);
            }
        }
    }

    private static void ValidateString(JsonElement value, JsonElement schema, string path, List<string> problems)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{path}: esperava um texto.");
            return;
        }

        var text = value.GetString()!;

        if (schema.TryGetProperty("minLength", out var minLength) && text.Trim().Length < minLength.GetInt32())
        {
            problems.Add($"{path}: texto curto demais.");
        }

        if (schema.TryGetProperty("enum", out var allowed)
            && !allowed.EnumerateArray().Any(option => option.GetString() == text))
        {
            problems.Add($"{path}: '{text}' não é um valor permitido.");
        }
    }
}
