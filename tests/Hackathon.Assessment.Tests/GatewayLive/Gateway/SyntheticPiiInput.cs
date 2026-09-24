using System.Text.Json;

namespace Hackathon.Assessment.Tests.GatewayLive.Gateway;

internal sealed record SyntheticPiiInput(string Kind, string Value)
{
    private static readonly string[] RequiredKinds =
    [
        "email",
        "phone",
        "tckn",
        "secret-like"
    ];

    public static IReadOnlyList<SyntheticPiiInput> LoadFromEnvironment()
    {
        var json = Environment.GetEnvironmentVariable("PII_TEST_INPUTS_JSON");
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new LiveGatewayConfigurationException(
                "BLOCKED: PII_TEST_INPUTS_JSON yok");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new LiveGatewayConfigurationException(
                "BLOCKED: PII_TEST_INPUTS_JSON geçerli JSON değil");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new LiveGatewayConfigurationException(
                    "BLOCKED: PII_TEST_INPUTS_JSON array olmalı");
            }

            var inputs = new List<SyntheticPiiInput>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("kind", out var kindElement)
                    || kindElement.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("value", out var valueElement)
                    || valueElement.ValueKind != JsonValueKind.String)
                {
                    throw new LiveGatewayConfigurationException(
                        "BLOCKED: PII_TEST_INPUTS_JSON kind/value alanlarını içermeli");
                }

                var kind = kindElement.GetString() ?? "";
                var value = valueElement.GetString() ?? "";
                if (!RequiredKinds.Contains(kind, StringComparer.Ordinal)
                    || string.IsNullOrWhiteSpace(value))
                {
                    throw new LiveGatewayConfigurationException(
                        "BLOCKED: PII_TEST_INPUTS_JSON email/phone/tckn/secret-like türlerini içermeli");
                }

                inputs.Add(new SyntheticPiiInput(kind, value));
            }

            foreach (var requiredKind in RequiredKinds)
            {
                if (!inputs.Any(input => input.Kind == requiredKind))
                {
                    throw new LiveGatewayConfigurationException(
                        "BLOCKED: PII_TEST_INPUTS_JSON email/phone/tckn/secret-like türlerini içermeli");
                }
            }

            return inputs;
        }
    }
}
