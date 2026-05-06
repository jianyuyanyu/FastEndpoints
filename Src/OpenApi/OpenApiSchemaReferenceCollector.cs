using Microsoft.OpenApi;

namespace FastEndpoints.OpenApi;

static class OpenApiSchemaReferenceCollector
{
    internal static HashSet<string> GetReferencedSchemaRefs(OpenApiDocument document)
    {
        var referencedSchemas = new HashSet<string>(StringComparer.Ordinal);
        var pendingSchemas = new Queue<string>();
        CollectReferencedSchemas(document, referencedSchemas, pendingSchemas);

        while (pendingSchemas.Count > 0)
        {
            var refId = pendingSchemas.Dequeue();

            if (document.Components?.Schemas?.TryGetValue(refId, out var s) == true)
                CollectSchemaRefs(s, referencedSchemas, pendingSchemas);
        }

        return referencedSchemas;
    }

    internal static void CollectSchemaRefs(IOpenApiSchema? schema, HashSet<string> refs, Queue<string> pendingRefs)
    {
        switch (schema)
        {
            case null:
                return;
            case OpenApiSchemaReference schemaRef:
            {
                var refId = GetReferenceId(schemaRef);

                if (!string.IsNullOrEmpty(refId) && refs.Add(refId))
                    pendingRefs.Enqueue(refId);

                return;
            }
            case OpenApiSchema s:
            {
                if (s.Properties is { Count: > 0 })
                    CollectSchemaRefs(s.Properties.Values, refs, pendingRefs);

                CollectSchemaRefs(s.Items, refs, pendingRefs);
                CollectSchemaRefs(s.AdditionalProperties, refs, pendingRefs);
                CollectSchemaRefs(s.Not, refs, pendingRefs);

                if (s.AllOf is { Count: > 0 })
                    CollectSchemaRefs(s.AllOf, refs, pendingRefs);

                if (s.OneOf is { Count: > 0 })
                    CollectSchemaRefs(s.OneOf, refs, pendingRefs);

                if (s.AnyOf is { Count: > 0 })
                    CollectSchemaRefs(s.AnyOf, refs, pendingRefs);

                if (s.PatternProperties is { Count: > 0 })
                    CollectSchemaRefs(s.PatternProperties.Values, refs, pendingRefs);

                if (s.Definitions is { Count: > 0 })
                    CollectSchemaRefs(s.Definitions.Values, refs, pendingRefs);

                if (s.Discriminator?.Mapping is { Count: > 0 })
                    CollectSchemaRefs(s.Discriminator.Mapping.Values, refs, pendingRefs);

                break;
            }
        }
    }

    static void CollectReferencedSchemas(OpenApiDocument document, HashSet<string> refs, Queue<string> pendingRefs)
    {
        var walkedResponses = new HashSet<string>(StringComparer.Ordinal);
        var walkedParameters = new HashSet<string>(StringComparer.Ordinal);
        var walkedRequestBodies = new HashSet<string>(StringComparer.Ordinal);
        var walkedHeaders = new HashSet<string>(StringComparer.Ordinal);
        var walkedCallbacks = new HashSet<string>(StringComparer.Ordinal);

        if (document.Paths is { Count: > 0 })
        {
            foreach (var pathItem in document.Paths.Values)
                CollectPathItemRefs(pathItem, document, refs, pendingRefs, walkedResponses, walkedParameters, walkedRequestBodies, walkedHeaders, walkedCallbacks);
        }

        if (document.Components is not { } components)
            return;

        if (components.Responses is { Count: > 0 })
        {
            foreach (var (id, response) in components.Responses)
                CollectResponseRefs(response, document, refs, pendingRefs, walkedResponses, walkedHeaders, id);
        }

        if (components.Parameters is { Count: > 0 })
        {
            foreach (var (id, parameter) in components.Parameters)
                CollectParameterRefs(parameter, document, refs, pendingRefs, walkedParameters, walkedHeaders, id);
        }

        if (components.RequestBodies is { Count: > 0 })
        {
            foreach (var (id, requestBody) in components.RequestBodies)
                CollectRequestBodyRefs(requestBody, document, refs, pendingRefs, walkedRequestBodies, walkedHeaders, id);
        }

        if (components.Headers is { Count: > 0 })
        {
            foreach (var (id, header) in components.Headers)
                CollectHeaderRefs(header, document, refs, pendingRefs, walkedHeaders, id);
        }

        if (components.Callbacks is { Count: > 0 })
        {
            foreach (var (id, callback) in components.Callbacks)
                CollectCallbackRefs(callback, document, refs, pendingRefs, walkedResponses, walkedParameters, walkedRequestBodies, walkedHeaders, walkedCallbacks, id);
        }

        if (components.PathItems is { Count: > 0 })
        {
            foreach (var pathItem in components.PathItems.Values)
                CollectPathItemRefs(pathItem, document, refs, pendingRefs, walkedResponses, walkedParameters, walkedRequestBodies, walkedHeaders, walkedCallbacks);
        }
    }

    static void CollectPathItemRefs(IOpenApiPathItem? pathItem,
                                    OpenApiDocument document,
                                    HashSet<string> refs,
                                    Queue<string> pendingRefs,
                                    HashSet<string> walkedResponses,
                                    HashSet<string> walkedParameters,
                                    HashSet<string> walkedRequestBodies,
                                    HashSet<string> walkedHeaders,
                                    HashSet<string> walkedCallbacks)
    {
        if (pathItem?.Parameters is { Count: > 0 })
        {
            foreach (var parameter in pathItem.Parameters)
                CollectParameterRefs(parameter, document, refs, pendingRefs, walkedParameters, walkedHeaders);
        }

        if (pathItem?.Operations is not { Count: > 0 })
            return;

        foreach (var op in pathItem.Operations.Values)
        {
            if (op.Parameters is { Count: > 0 })
            {
                foreach (var parameter in op.Parameters)
                    CollectParameterRefs(parameter, document, refs, pendingRefs, walkedParameters, walkedHeaders);
            }

            CollectRequestBodyRefs(op.RequestBody, document, refs, pendingRefs, walkedRequestBodies, walkedHeaders);

            if (op.Responses is { Count: > 0 })
            {
                foreach (var resp in op.Responses.Values)
                    CollectResponseRefs(resp, document, refs, pendingRefs, walkedResponses, walkedHeaders);
            }

            if (op.Callbacks is { Count: > 0 })
            {
                foreach (var callback in op.Callbacks.Values)
                    CollectCallbackRefs(callback, document, refs, pendingRefs, walkedResponses, walkedParameters, walkedRequestBodies, walkedHeaders, walkedCallbacks);
            }
        }
    }

    static void CollectResponseRefs(IOpenApiResponse? response,
                                    OpenApiDocument document,
                                    HashSet<string> refs,
                                    Queue<string> pendingRefs,
                                    HashSet<string> walkedResponses,
                                    HashSet<string> walkedHeaders,
                                    string? componentId = null)
    {
        if (response is null)
            return;

        if (componentId is not null && !walkedResponses.Add(componentId))
            return;

        if (response is OpenApiResponseReference responseRef &&
            TryCollectReferencedComponent(responseRef.Reference, document.Components?.Responses, document, refs, pendingRefs, walkedResponses, walkedHeaders))
            return;

        if (response.Headers is { Count: > 0 })
        {
            foreach (var header in response.Headers.Values)
                CollectHeaderRefs(header, document, refs, pendingRefs, walkedHeaders);
        }

        if (response.Content is { Count: > 0 })
        {
            foreach (var mediaType in response.Content.Values)
                CollectMediaTypeRefs(mediaType, document, refs, pendingRefs, walkedHeaders);
        }
    }

    static void CollectParameterRefs(IOpenApiParameter? parameter,
                                     OpenApiDocument document,
                                     HashSet<string> refs,
                                     Queue<string> pendingRefs,
                                     HashSet<string> walkedParameters,
                                     HashSet<string> walkedHeaders,
                                     string? componentId = null)
    {
        if (parameter is null)
            return;

        if (componentId is not null && !walkedParameters.Add(componentId))
            return;

        if (parameter is OpenApiParameterReference parameterRef &&
            TryCollectReferencedComponent(parameterRef.Reference, document.Components?.Parameters, document, refs, pendingRefs, walkedParameters, walkedHeaders))
            return;

        CollectSchemaRefs(parameter.Schema, refs, pendingRefs);

        if (parameter.Content is { Count: > 0 })
        {
            foreach (var mediaType in parameter.Content.Values)
                CollectMediaTypeRefs(mediaType, document, refs, pendingRefs, walkedHeaders);
        }
    }

    static void CollectRequestBodyRefs(IOpenApiRequestBody? requestBody,
                                       OpenApiDocument document,
                                       HashSet<string> refs,
                                       Queue<string> pendingRefs,
                                       HashSet<string> walkedRequestBodies,
                                       HashSet<string> walkedHeaders,
                                       string? componentId = null)
    {
        if (requestBody is null)
            return;

        if (componentId is not null && !walkedRequestBodies.Add(componentId))
            return;

        if (requestBody is OpenApiRequestBodyReference requestBodyRef &&
            TryCollectReferencedComponent(requestBodyRef.Reference, document.Components?.RequestBodies, document, refs, pendingRefs, walkedRequestBodies, walkedHeaders))
            return;

        if (requestBody.Content is { Count: > 0 })
        {
            foreach (var mediaType in requestBody.Content.Values)
                CollectMediaTypeRefs(mediaType, document, refs, pendingRefs, walkedHeaders);
        }
    }

    static void CollectHeaderRefs(IOpenApiHeader? header,
                                  OpenApiDocument document,
                                  HashSet<string> refs,
                                  Queue<string> pendingRefs,
                                  HashSet<string> walkedHeaders,
                                  string? componentId = null)
    {
        if (header is null)
            return;

        if (componentId is not null && !walkedHeaders.Add(componentId))
            return;

        if (header is OpenApiHeaderReference headerRef &&
            TryCollectReferencedComponent(headerRef.Reference, document.Components?.Headers, document, refs, pendingRefs, walkedHeaders))
            return;

        CollectSchemaRefs(header.Schema, refs, pendingRefs);

        if (header.Content is { Count: > 0 })
        {
            foreach (var mediaType in header.Content.Values)
                CollectMediaTypeRefs(mediaType, document, refs, pendingRefs, walkedHeaders);
        }
    }

    static void CollectCallbackRefs(IOpenApiCallback? callback,
                                    OpenApiDocument document,
                                    HashSet<string> refs,
                                    Queue<string> pendingRefs,
                                    HashSet<string> walkedResponses,
                                    HashSet<string> walkedParameters,
                                    HashSet<string> walkedRequestBodies,
                                    HashSet<string> walkedHeaders,
                                    HashSet<string> walkedCallbacks,
                                    string? componentId = null)
    {
        if (callback is null)
            return;

        if (componentId is not null && !walkedCallbacks.Add(componentId))
            return;

        if (callback is OpenApiCallbackReference callbackRef &&
            TryCollectReferencedCallback(
                callbackRef.Reference,
                document,
                refs,
                pendingRefs,
                walkedResponses,
                walkedParameters,
                walkedRequestBodies,
                walkedHeaders,
                walkedCallbacks))
            return;

        if (callback.PathItems is not { Count: > 0 })
            return;

        foreach (var pathItem in callback.PathItems.Values)
            CollectPathItemRefs(pathItem, document, refs, pendingRefs, walkedResponses, walkedParameters, walkedRequestBodies, walkedHeaders, walkedCallbacks);
    }

    static void CollectMediaTypeRefs(OpenApiMediaType? mediaType,
                                     OpenApiDocument document,
                                     HashSet<string> refs,
                                     Queue<string> pendingRefs,
                                     HashSet<string> walkedHeaders)
    {
        if (mediaType is null)
            return;

        CollectSchemaRefs(mediaType.Schema, refs, pendingRefs);

        if (mediaType.Encoding is { Count: > 0 })
        {
            foreach (var encoding in mediaType.Encoding.Values)
                CollectEncodingRefs(encoding, document, refs, pendingRefs, walkedHeaders);
        }
    }

    static void CollectEncodingRefs(OpenApiEncoding? encoding,
                                    OpenApiDocument document,
                                    HashSet<string> refs,
                                    Queue<string> pendingRefs,
                                    HashSet<string> walkedHeaders)
    {
        if (encoding?.Headers is { Count: > 0 })
        {
            foreach (var header in encoding.Headers.Values)
                CollectHeaderRefs(header, document, refs, pendingRefs, walkedHeaders);
        }
    }

    static bool TryCollectReferencedComponent<TComponent>(BaseOpenApiReference reference,
                                                          IDictionary<string, TComponent>? components,
                                                          OpenApiDocument document,
                                                          HashSet<string> refs,
                                                          Queue<string> pendingRefs,
                                                          HashSet<string> walkedRefs,
                                                          HashSet<string>? walkedHeaders = null)
        where TComponent : class
    {
        var id = reference.Id;

        if (string.IsNullOrEmpty(id) || reference.IsExternal || components?.TryGetValue(id, out var component) != true)
            return false;

        switch (component)
        {
            case IOpenApiResponse response:
                CollectResponseRefs(response, document, refs, pendingRefs, walkedRefs, walkedHeaders ?? new(StringComparer.Ordinal), id);

                break;
            case IOpenApiParameter parameter:
                CollectParameterRefs(parameter, document, refs, pendingRefs, walkedRefs, walkedHeaders ?? new(StringComparer.Ordinal), id);

                break;
            case IOpenApiRequestBody requestBody:
                CollectRequestBodyRefs(requestBody, document, refs, pendingRefs, walkedRefs, walkedHeaders ?? new(StringComparer.Ordinal), id);

                break;
            case IOpenApiHeader header:
                CollectHeaderRefs(header, document, refs, pendingRefs, walkedRefs, id);

                break;
            default:
                return false;
        }

        return true;
    }

    static bool TryCollectReferencedCallback(BaseOpenApiReference reference,
                                             OpenApiDocument document,
                                             HashSet<string> refs,
                                             Queue<string> pendingRefs,
                                             HashSet<string> walkedResponses,
                                             HashSet<string> walkedParameters,
                                             HashSet<string> walkedRequestBodies,
                                             HashSet<string> walkedHeaders,
                                             HashSet<string> walkedCallbacks)
    {
        var id = reference.Id;

        if (string.IsNullOrEmpty(id) || reference.IsExternal || document.Components?.Callbacks?.TryGetValue(id, out var callback) != true)
            return false;

        CollectCallbackRefs(callback, document, refs, pendingRefs, walkedResponses, walkedParameters, walkedRequestBodies, walkedHeaders, walkedCallbacks, id);

        return true;
    }

    static void CollectSchemaRefs(IEnumerable<IOpenApiSchema?> schemas, HashSet<string> refs, Queue<string> pendingRefs)
    {
        foreach (var schema in schemas)
            CollectSchemaRefs(schema, refs, pendingRefs);
    }

    static string? GetReferenceId(OpenApiSchemaReference schemaRef)
        => schemaRef.Reference.Id ?? schemaRef.Id;
}
