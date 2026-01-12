namespace Showcase.EnterpriseMcp.Server.Services;

public interface IJsonSchemaValidator
{
    bool IsValid(string schemaJson, object data);
}

public class JsonSchemaValidator : IJsonSchemaValidator
{
    public bool IsValid(string schemaJson, object data)
    {
        //if (string.IsNullOrEmpty(schemaJson))
        //    return false;
        //// Parse JSON schema
        //var schema = Json.Schema.JsonSchema.FromText(schemaJson);
        //// Serialize data to JSON and parse
        //var json = JsonSerializer.Serialize(data);
        //using var document = JsonDocument.Parse(json);
        //// Validate against schema
        //var validationResult = schema.Validate(document.RootElement);
        //return validationResult.IsValid;
        return true;
    }
}
