using Microsoft.OpenApi;

namespace FastEndpoints.OpenApi;

static partial class DocumentSchemaHelpers
{
    extension(OpenApiDocument document)
    {
        internal HashSet<string> GetReferencedSchemaRefs()
            => OpenApiSchemaReferenceCollector.GetReferencedSchemaRefs(document);
    }

    static string? GetReferenceId(OpenApiSchemaReference schemaRef)
        => schemaRef.Reference.Id ?? schemaRef.Id;

}
