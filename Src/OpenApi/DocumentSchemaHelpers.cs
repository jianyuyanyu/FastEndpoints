using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Text;
using System.Text.Json.Nodes;

namespace FastEndpoints.OpenApi;

static class DocumentSchemaHelpers
{
    extension(OpenApiDocument document)
    {
        internal void RemoveUnreferencedSchemas(HashSet<string> referencedSchemas)
        {
            if (document.Components?.Schemas is not { Count: > 0 } schemas)
                return;

            foreach (var key in schemas.Keys.ToArray())
            {
                if (!referencedSchemas.Contains(key))
                    schemas.Remove(key);
            }
        }

        internal void RemoveUnreferencedSchemas()
            => document.RemoveUnreferencedSchemas(document.GetReferencedSchemaRefs());

        internal void RemovePromotedRequestWrapperSchemas(SharedContext sharedCtx, HashSet<string> referencedSchemas)
        {
            if (sharedCtx.PromotedRequestWrapperSchemaRefs.IsEmpty || document.Components?.Schemas is not { Count: > 0 } schemas)
                return;

            foreach (var refId in sharedCtx.PromotedRequestWrapperSchemaRefs.Keys)
            {
                if (!referencedSchemas.Contains(refId))
                    schemas.Remove(refId);
            }
        }

        internal void RemovePromotedRequestWrapperSchemas(SharedContext sharedCtx)
            => document.RemovePromotedRequestWrapperSchemas(sharedCtx, document.GetReferencedSchemaRefs());

        internal void RemoveFormFileSchemas()
        {
            if (document.Components?.Schemas is { Count: > 0 })
            {
                foreach (var (_, iSchema) in document.Components.Schemas)
                {
                    if (iSchema is OpenApiSchema schema)
                        InlineFormFileRefs(schema);
                }
            }

            if (document.Paths is { Count: > 0 })
            {
                foreach (var pathItem in document.Paths.Values)
                {
                    if (pathItem.Operations is null)
                        continue;

                    foreach (var op in pathItem.Operations.Values)
                    {
                        if (op.RequestBody?.Content is { Count: > 0 })
                        {
                            foreach (var content in op.RequestBody.Content.Values)
                                RewriteFormFileMediaType(content);
                        }

                        if (op.Responses is { Count: > 0 })
                        {
                            foreach (var resp in op.Responses.Values)
                            {
                                if (resp is OpenApiResponse { Content.Count: > 0 } concreteResp)
                                {
                                    foreach (var content in concreteResp.Content.Values)
                                        RewriteFormFileMediaType(content);
                                }
                            }
                        }
                    }
                }
            }

            if (document.Components?.Schemas is null)
                return;

            foreach (var key in document.Components.Schemas.Keys.ToArray())
            {
                if (key.Contains("IFormFile", StringComparison.Ordinal))
                    document.Components.Schemas.Remove(key);
            }
        }

        internal void AddOperationSchemaVariants(SharedContext sharedCtx)
        {
            if (sharedCtx.OperationSchemaVariants.IsEmpty)
                return;

            document.Components ??= new();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();

            foreach (var (refId, schema) in sharedCtx.OperationSchemaVariants)
                document.Components.Schemas.TryAdd(refId, schema);
        }

        internal void DeduplicateOperationSchemaVariants(SharedContext sharedCtx)
        {
            if (sharedCtx.OperationSchemaVariants.IsEmpty || document.Components?.Schemas is not { Count: > 0 } schemas)
                return;

            var variantIds = sharedCtx.OperationSchemaVariants.Keys
                                      .Where(schemas.ContainsKey)
                                      .ToHashSet(StringComparer.Ordinal);

            if (variantIds.Count == 0)
                return;

            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var groupedVariantIds = variantIds.GroupBy(GetOperationVariantSourceRefId, StringComparer.Ordinal)
                                      .Select(static g => new OrderedVariantGroup(g.Key, g.Order(StringComparer.Ordinal).ToArray()))
                                      .ToArray();
            var changed = true;
            var aliasRevision = 0;

            while (changed)
            {
                changed = false;
                var signatureCache = new Dictionary<SchemaSignatureCacheKey, string>();

                foreach (var group in groupedVariantIds)
                {
                    var signatureToRefId = new Dictionary<string, string>(StringComparer.Ordinal);

                    if (schemas.TryGetValue(group.SourceRefId, out var sourceSchema) && sourceSchema is OpenApiSchema concreteSourceSchema)
                        signatureToRefId[GetSchemaSignature(group.SourceRefId, concreteSourceSchema, aliases, signatureCache, aliasRevision)] =
                            group.SourceRefId;

                    foreach (var refId in group.VariantIds)
                    {
                        if (aliases.ContainsKey(refId) || !schemas.TryGetValue(refId, out var iSchema) || iSchema is not OpenApiSchema schema)
                            continue;

                        var signature = GetSchemaSignature(refId, schema, aliases, signatureCache, aliasRevision);

                        if (signatureToRefId.TryGetValue(signature, out var canonicalRefId))
                        {
                            aliases[refId] = ResolveAlias(canonicalRefId, aliases);
                            changed = true;
                            aliasRevision++;
                        }
                        else
                            signatureToRefId[signature] = refId;
                    }
                }
            }

            if (aliases.Count == 0)
                return;

            RewriteSchemaRefs(document, aliases);

            foreach (var duplicateRefId in aliases.Keys)
                schemas.Remove(duplicateRefId);
        }

        internal async Task AddMissingSchemas(SharedContext sharedCtx, OpenApiDocumentTransformerContext context, CancellationToken ct)
        {
            if (sharedCtx.MissingSchemaTypes.IsEmpty)
                return;

            document.Components ??= new();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();

            foreach (var (refId, type) in sharedCtx.MissingSchemaTypes)
            {
                if (document.Components.Schemas.ContainsKey(refId))
                    continue;

                var schema = await context.GetOrCreateSchemaAsync(type, parameterDescription: null, ct);
                document.Components.Schemas.TryAdd(refId, schema);
            }
        }

        internal HashSet<string> GetReferencedSchemaRefs()
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
    }

    internal static void SortPaths(this OpenApiDocument document)
    {
        var sorted = document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        document.Paths.Clear();

        foreach (var (path, pathItem) in sorted)
            document.Paths[path] = pathItem;
    }

    internal static void SortResponses(this OpenApiDocument document)
    {
        foreach (var (_, pathItem) in document.Paths)
        {
            if (pathItem.Operations is null)
                continue;

            foreach (var (_, operation) in pathItem.Operations)
            {
                if (operation.Responses is not { Count: > 1 })
                    continue;

                var sorted = operation.Responses.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
                operation.Responses.Clear();

                foreach (var (key, value) in sorted)
                    operation.Responses[key] = value;
            }
        }
    }

    internal static void SortSchemas(this OpenApiDocument document)
    {
        if (document.Components?.Schemas is null)
            return;

        var sorted = document.Components.Schemas.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        document.Components.Schemas.Clear();

        foreach (var (key, schema) in sorted)
            document.Components.Schemas[key] = schema;
    }

    static string GetOperationVariantSourceRefId(string refId)
    {
        var suffixIndex = refId.LastIndexOf("__op", StringComparison.Ordinal);

        return suffixIndex < 0 ? refId : refId[..suffixIndex];
    }

    static string ResolveAlias(string refId, Dictionary<string, string> aliases)
    {
        if (!aliases.TryGetValue(refId, out var targetRefId))
            return refId;

        List<string>? traversedAliases = null;

        do
        {
            (traversedAliases ??= []).Add(refId);
            refId = targetRefId;
        }
        while (aliases.TryGetValue(refId, out targetRefId));

        foreach (var traversedAlias in traversedAliases)
            aliases[traversedAlias] = refId;

        return refId;
    }

    static string GetSchemaSignature(string refId,
                                     IOpenApiSchema schema,
                                     Dictionary<string, string> aliases,
                                     Dictionary<SchemaSignatureCacheKey, string> signatureCache,
                                     int aliasRevision)
    {
        var cacheKey = new SchemaSignatureCacheKey(refId, aliasRevision);

        if (signatureCache.TryGetValue(cacheKey, out var cachedSignature))
            return cachedSignature;

        var builder = new StringBuilder();
        AppendSchemaSignature(builder, schema, aliases, []);

        var signature = builder.ToString();
        signatureCache[cacheKey] = signature;

        return signature;
    }

    static void AppendSchemaSignature(StringBuilder builder,
                                      IOpenApiSchema? schema,
                                      Dictionary<string, string> aliases,
                                      HashSet<IOpenApiSchema> visited)
    {
        switch (schema)
        {
            case null:
                builder.Append("null;");

                return;
            case OpenApiSchemaReference schemaRef:
                builder.Append("ref:").Append(GetReferenceId(schemaRef) is { } refId ? ResolveAlias(refId, aliases) : string.Empty).Append(';');

                return;
            case OpenApiSchema s:
                if (!visited.Add(s))
                {
                    builder.Append("cycle;");

                    return;
                }

                builder.Append("schema{");
                AppendValue(builder, "id", s.Id);
                AppendValue(builder, "title", s.Title);
                AppendValue(builder, "type", s.Type);
                AppendValue(builder, "format", s.Format);
                AppendValue(builder, "description", s.Description);
                AppendValue(builder, "comment", s.Comment);
                AppendValue(builder, "const", s.Const);
                AppendValue(builder, "exclusiveMaximum", s.ExclusiveMaximum);
                AppendValue(builder, "exclusiveMinimum", s.ExclusiveMinimum);
                AppendValue(builder, "maximum", s.Maximum);
                AppendValue(builder, "minimum", s.Minimum);
                AppendValue(builder, "maxLength", s.MaxLength);
                AppendValue(builder, "minLength", s.MinLength);
                AppendValue(builder, "pattern", s.Pattern);
                AppendValue(builder, "multipleOf", s.MultipleOf);
                AppendValue(builder, "readOnly", s.ReadOnly);
                AppendValue(builder, "writeOnly", s.WriteOnly);
                AppendValue(builder, "maxItems", s.MaxItems);
                AppendValue(builder, "minItems", s.MinItems);
                AppendValue(builder, "uniqueItems", s.UniqueItems);
                AppendValue(builder, "maxProperties", s.MaxProperties);
                AppendValue(builder, "minProperties", s.MinProperties);
                AppendValue(builder, "additionalPropertiesAllowed", s.AdditionalPropertiesAllowed);
                AppendValue(builder, "unevaluatedProperties", s.UnevaluatedProperties);
                AppendValue(builder, "deprecated", s.Deprecated);
                AppendJsonNode(builder, "default", s.Default);
                AppendJsonNode(builder, "example", s.Example);
                AppendJsonNodeList(builder, "examples", s.Examples);
                AppendJsonNodeList(builder, "enum", s.Enum);
                AppendStringSet(builder, "required", s.Required);
                AppendStringBoolDictionary(builder, "vocabulary", s.Vocabulary);
                AppendSchema(builder, "not", s.Not, aliases, visited);
                AppendSchema(builder, "items", s.Items, aliases, visited);
                AppendSchema(builder, "additionalProperties", s.AdditionalProperties, aliases, visited);
                AppendSchemaList(builder, "allOf", s.AllOf, aliases, visited);
                AppendSchemaList(builder, "oneOf", s.OneOf, aliases, visited);
                AppendSchemaList(builder, "anyOf", s.AnyOf, aliases, visited);
                AppendSchemaDictionary(builder, "properties", s.Properties, aliases, visited);
                AppendSchemaDictionary(builder, "patternProperties", s.PatternProperties, aliases, visited);
                AppendSchemaDictionary(builder, "definitions", s.Definitions, aliases, visited);
                AppendDiscriminator(builder, s.Discriminator, aliases, visited);
                AppendXml(builder, s.Xml);
                AppendExternalDocs(builder, s.ExternalDocs);
                AppendExtensions(builder, s.Extensions);
                AppendJsonNodeDictionary(builder, "unrecognizedKeywords", s.UnrecognizedKeywords);
                AppendDependentRequired(builder, s.DependentRequired);
                builder.Append('}');
                visited.Remove(s);

                return;
            default:
                builder.Append(schema.GetType().FullName).Append(';');

                return;
        }
    }

    static void AppendValue<T>(StringBuilder builder, string name, T value)
        => builder.Append(name).Append('=').Append(value?.ToString()).Append(';');

    static void AppendJsonNode(StringBuilder builder, string name, JsonNode? node)
    {
        builder.Append(name).Append('=');
        AppendJsonNodeValue(builder, node);
        builder.Append(';');
    }

    static void AppendJsonNodeValue(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");

                return;
            case JsonObject obj:
                builder.Append('{');

                foreach (var (key, value) in obj.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
                {
                    builder.Append(key).Append(':');
                    AppendJsonNodeValue(builder, value);
                    builder.Append(',');
                }

                builder.Append('}');

                return;
            case JsonArray arr:
                builder.Append('[');

                foreach (var value in arr)
                {
                    AppendJsonNodeValue(builder, value);
                    builder.Append(',');
                }

                builder.Append(']');

                return;
            default:
                builder.Append(node.ToJsonString());

                return;
        }
    }

    static void AppendJsonNodeList(StringBuilder builder, string name, IList<JsonNode>? nodes)
    {
        builder.Append(name).Append("=[");

        if (nodes is not null)
        {
            foreach (var node in nodes)
            {
                AppendJsonNodeValue(builder, node);
                builder.Append(',');
            }
        }

        builder.Append("]; ");
    }

    static void AppendStringSet(StringBuilder builder, string name, ISet<string>? values)
    {
        builder.Append(name).Append("=[");

        if (values is not null)
        {
            foreach (var value in values.Order(StringComparer.Ordinal))
                builder.Append(value).Append(',');
        }

        builder.Append("]; ");
    }

    static void AppendStringBoolDictionary(StringBuilder builder, string name, IDictionary<string, bool>? values)
    {
        builder.Append(name).Append("={");

        if (values is not null)
        {
            foreach (var (key, value) in values.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
                builder.Append(key).Append(':').Append(value).Append(',');
        }

        builder.Append("};");
    }

    static void AppendSchema(StringBuilder builder,
                             string name,
                             IOpenApiSchema? schema,
                             Dictionary<string, string> aliases,
                             HashSet<IOpenApiSchema> visited)
    {
        builder.Append(name).Append('=');
        AppendSchemaSignature(builder, schema, aliases, visited);
    }

    static void AppendSchemaList(StringBuilder builder,
                                 string name,
                                 IList<IOpenApiSchema>? schemas,
                                 Dictionary<string, string> aliases,
                                 HashSet<IOpenApiSchema> visited)
    {
        builder.Append(name).Append("=[");

        if (schemas is not null)
        {
            foreach (var schema in schemas)
            {
                AppendSchemaSignature(builder, schema, aliases, visited);
                builder.Append(',');
            }
        }

        builder.Append("]; ");
    }

    static void AppendSchemaDictionary(StringBuilder builder,
                                       string name,
                                       IDictionary<string, IOpenApiSchema>? schemas,
                                       Dictionary<string, string> aliases,
                                       HashSet<IOpenApiSchema> visited)
    {
        builder.Append(name).Append("={");

        if (schemas is not null)
        {
            foreach (var (key, schema) in schemas.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
            {
                builder.Append(key).Append(':');
                AppendSchemaSignature(builder, schema, aliases, visited);
                builder.Append(',');
            }
        }

        builder.Append("};");
    }

    static void AppendDiscriminator(StringBuilder builder,
                                    OpenApiDiscriminator? discriminator,
                                    Dictionary<string, string> aliases,
                                    HashSet<IOpenApiSchema> visited)
    {
        builder.Append("discriminator={");

        if (discriminator is not null)
        {
            AppendValue(builder, "propertyName", discriminator.PropertyName);

            if (discriminator.Mapping is not null)
            {
                foreach (var (key, schema) in discriminator.Mapping.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
                {
                    builder.Append(key).Append(':');
                    AppendSchemaSignature(builder, schema, aliases, visited);
                    builder.Append(',');
                }
            }
        }

        builder.Append("};");
    }

    static void AppendXml(StringBuilder builder, OpenApiXml? xml)
    {
        builder.Append("xml={");

        if (xml is not null)
        {
            AppendValue(builder, "name", xml.Name);
            AppendValue(builder, "namespace", xml.Namespace);
            AppendValue(builder, "prefix", xml.Prefix);
            AppendValue(builder, "attribute", xml.Attribute);
            AppendValue(builder, "wrapped", xml.Wrapped);
        }

        builder.Append("};");
    }

    static void AppendExternalDocs(StringBuilder builder, OpenApiExternalDocs? docs)
    {
        builder.Append("externalDocs={");

        if (docs is not null)
        {
            AppendValue(builder, "description", docs.Description);
            AppendValue(builder, "url", docs.Url);
        }

        builder.Append("};");
    }

    static void AppendExtensions(StringBuilder builder, IDictionary<string, IOpenApiExtension>? extensions)
    {
        builder.Append("extensions={");

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
                builder.Append(key).Append(':').Append(value.GetType().FullName).Append(',');
        }

        builder.Append("};");
    }

    static void AppendJsonNodeDictionary(StringBuilder builder, string name, IDictionary<string, JsonNode>? values)
    {
        builder.Append(name).Append("={");

        if (values is not null)
        {
            foreach (var (key, value) in values.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
            {
                builder.Append(key).Append(':');
                AppendJsonNodeValue(builder, value);
                builder.Append(',');
            }
        }

        builder.Append("};");
    }

    static void AppendDependentRequired(StringBuilder builder, IDictionary<string, HashSet<string>>? values)
    {
        builder.Append("dependentRequired={");

        if (values is not null)
        {
            foreach (var (key, set) in values.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
            {
                builder.Append(key).Append(':');

                foreach (var value in set.Order(StringComparer.Ordinal))
                    builder.Append(value).Append(',');
            }
        }

        builder.Append("};");
    }

    static void RewriteSchemaRefs(OpenApiDocument document, Dictionary<string, string> aliases)
    {
        if (document.Paths is { Count: > 0 })
        {
            foreach (var pathItem in document.Paths.Values)
                RewritePathItemSchemaRefs(pathItem, aliases);
        }

        if (document.Components is not { } components)
            return;

        if (components.Schemas is { Count: > 0 })
        {
            foreach (var (key, schema) in components.Schemas.ToArray())
                components.Schemas[key] = RewriteSchemaRef(schema, aliases)!;
        }

        if (components.Responses is { Count: > 0 })
        {
            foreach (var response in components.Responses.Values)
                RewriteResponseSchemaRefs(response, aliases);
        }

        if (components.Parameters is { Count: > 0 })
        {
            foreach (var parameter in components.Parameters.Values)
                RewriteParameterSchemaRefs(parameter, aliases);
        }

        if (components.RequestBodies is { Count: > 0 })
        {
            foreach (var requestBody in components.RequestBodies.Values)
                RewriteRequestBodySchemaRefs(requestBody, aliases);
        }

        if (components.Headers is { Count: > 0 })
        {
            foreach (var header in components.Headers.Values)
                RewriteHeaderSchemaRefs(header, aliases);
        }

        if (components.Callbacks is { Count: > 0 })
        {
            foreach (var callback in components.Callbacks.Values)
                RewriteCallbackSchemaRefs(callback, aliases);
        }

        if (components.PathItems is { Count: > 0 })
        {
            foreach (var pathItem in components.PathItems.Values)
                RewritePathItemSchemaRefs(pathItem, aliases);
        }
    }

    static void RewritePathItemSchemaRefs(IOpenApiPathItem? pathItem, Dictionary<string, string> aliases)
    {
        if (pathItem?.Parameters is { Count: > 0 })
        {
            foreach (var parameter in pathItem.Parameters)
                RewriteParameterSchemaRefs(parameter, aliases);
        }

        if (pathItem?.Operations is not { Count: > 0 })
            return;

        foreach (var operation in pathItem.Operations.Values)
        {
            if (operation.Parameters is { Count: > 0 })
            {
                foreach (var parameter in operation.Parameters)
                    RewriteParameterSchemaRefs(parameter, aliases);
            }

            RewriteRequestBodySchemaRefs(operation.RequestBody, aliases);

            if (operation.Responses is { Count: > 0 })
            {
                foreach (var response in operation.Responses.Values)
                    RewriteResponseSchemaRefs(response, aliases);
            }

            if (operation.Callbacks is { Count: > 0 })
            {
                foreach (var callback in operation.Callbacks.Values)
                    RewriteCallbackSchemaRefs(callback, aliases);
            }
        }
    }

    static void RewriteResponseSchemaRefs(IOpenApiResponse? response, Dictionary<string, string> aliases)
    {
        if (response is null)
            return;

        if (response.Headers is { Count: > 0 })
        {
            foreach (var header in response.Headers.Values)
                RewriteHeaderSchemaRefs(header, aliases);
        }

        if (response.Content is { Count: > 0 })
        {
            foreach (var mediaType in response.Content.Values)
                RewriteMediaTypeSchemaRefs(mediaType, aliases);
        }
    }

    static void RewriteParameterSchemaRefs(IOpenApiParameter? parameter, Dictionary<string, string> aliases)
    {
        if (parameter is null)
            return;

        if (parameter is OpenApiParameter concreteParameter)
            concreteParameter.Schema = RewriteSchemaRef(parameter.Schema, aliases);

        if (parameter.Content is { Count: > 0 })
        {
            foreach (var mediaType in parameter.Content.Values)
                RewriteMediaTypeSchemaRefs(mediaType, aliases);
        }
    }

    static void RewriteRequestBodySchemaRefs(IOpenApiRequestBody? requestBody, Dictionary<string, string> aliases)
    {
        if (requestBody?.Content is not { Count: > 0 })
            return;

        foreach (var mediaType in requestBody.Content.Values)
            RewriteMediaTypeSchemaRefs(mediaType, aliases);
    }

    static void RewriteHeaderSchemaRefs(IOpenApiHeader? header, Dictionary<string, string> aliases)
    {
        if (header is null)
            return;

        if (header is OpenApiHeader concreteHeader)
            concreteHeader.Schema = RewriteSchemaRef(header.Schema, aliases);

        if (header.Content is { Count: > 0 })
        {
            foreach (var mediaType in header.Content.Values)
                RewriteMediaTypeSchemaRefs(mediaType, aliases);
        }
    }

    static void RewriteCallbackSchemaRefs(IOpenApiCallback? callback, Dictionary<string, string> aliases)
    {
        if (callback?.PathItems is not { Count: > 0 })
            return;

        foreach (var pathItem in callback.PathItems.Values)
            RewritePathItemSchemaRefs(pathItem, aliases);
    }

    static void RewriteMediaTypeSchemaRefs(OpenApiMediaType? mediaType, Dictionary<string, string> aliases)
    {
        if (mediaType is null)
            return;

        mediaType.Schema = RewriteSchemaRef(mediaType.Schema, aliases);

        if (mediaType.Encoding is { Count: > 0 })
        {
            foreach (var encoding in mediaType.Encoding.Values)
                RewriteEncodingSchemaRefs(encoding, aliases);
        }
    }

    static void RewriteEncodingSchemaRefs(OpenApiEncoding? encoding, Dictionary<string, string> aliases)
    {
        if (encoding?.Headers is not { Count: > 0 })
            return;

        foreach (var header in encoding.Headers.Values)
            RewriteHeaderSchemaRefs(header, aliases);
    }

    static IOpenApiSchema? RewriteSchemaRef(IOpenApiSchema? schema, Dictionary<string, string> aliases)
    {
        switch (schema)
        {
            case null:
                return null;
            case OpenApiSchemaReference schemaRef:
            {
                if (GetReferenceId(schemaRef) is { } refId && aliases.TryGetValue(refId, out var canonicalRefId))
                    return new OpenApiSchemaReference(ResolveAlias(canonicalRefId, aliases));

                return schema;
            }
            case OpenApiSchema s:
            {
                if (s.Properties is { Count: > 0 })
                {
                    foreach (var (key, childSchema) in s.Properties.ToArray())
                        s.Properties[key] = RewriteSchemaRef(childSchema, aliases)!;
                }

                s.Items = RewriteSchemaRef(s.Items, aliases);
                s.AdditionalProperties = RewriteSchemaRef(s.AdditionalProperties, aliases);
                s.Not = RewriteSchemaRef(s.Not, aliases);

                if (s.AllOf is { Count: > 0 })
                    RewriteSchemaRefList(s.AllOf, aliases);

                if (s.OneOf is { Count: > 0 })
                    RewriteSchemaRefList(s.OneOf, aliases);

                if (s.AnyOf is { Count: > 0 })
                    RewriteSchemaRefList(s.AnyOf, aliases);

                if (s.PatternProperties is { Count: > 0 })
                {
                    foreach (var (key, childSchema) in s.PatternProperties.ToArray())
                        s.PatternProperties[key] = RewriteSchemaRef(childSchema, aliases)!;
                }

                if (s.Definitions is { Count: > 0 })
                {
                    foreach (var (key, childSchema) in s.Definitions.ToArray())
                        s.Definitions[key] = RewriteSchemaRef(childSchema, aliases)!;
                }

                if (s.Discriminator?.Mapping is { Count: > 0 })
                {
                    foreach (var (key, mappedSchema) in s.Discriminator.Mapping.ToArray())
                    {
                        if (RewriteSchemaRef(mappedSchema, aliases) is OpenApiSchemaReference rewrittenSchemaRef)
                            s.Discriminator.Mapping[key] = rewrittenSchemaRef;
                    }
                }

                return schema;
            }
            default:
                return schema;
        }
    }

    static void RewriteSchemaRefList(IList<IOpenApiSchema> schemas, Dictionary<string, string> aliases)
    {
        for (var i = 0; i < schemas.Count; i++)
            schemas[i] = RewriteSchemaRef(schemas[i], aliases)!;
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

    static void CollectSchemaRefs(IOpenApiSchema? schema, HashSet<string> refs, Queue<string> pendingRefs)
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

    static void InlineFormFileRefs(OpenApiSchema schema)
    {
        if (schema.Properties is { Count: > 0 })
        {
            foreach (var (propName, propSchema) in schema.Properties.ToArray())
            {
                if (RewriteFormFileSchema(propSchema) is { } rewrittenSchema)
                    schema.Properties[propName] = rewrittenSchema;
            }
        }

        schema.Items = RewriteFormFileSchema(schema.Items);
        schema.AdditionalProperties = RewriteFormFileSchema(schema.AdditionalProperties);

        if (schema.AllOf is { Count: > 0 })
            RewriteFormFileSchemaList(schema.AllOf);

        if (schema.OneOf is { Count: > 0 })
            RewriteFormFileSchemaList(schema.OneOf);

        if (schema.AnyOf is { Count: > 0 })
            RewriteFormFileSchemaList(schema.AnyOf);
    }

    static void RewriteFormFileMediaType(OpenApiMediaType content)
    {
        content.Schema = RewriteFormFileSchema(content.Schema);
    }

    static IOpenApiSchema? RewriteFormFileSchema(IOpenApiSchema? schema)
    {
        if (IsFormFileCollectionRef(schema))
            return FormFileArraySchema();

        if (IsFormFileRef(schema))
            return FormFileBinarySchema();

        if (schema is OpenApiSchema concreteSchema)
            InlineFormFileRefs(concreteSchema);

        return schema;
    }

    static void RewriteFormFileSchemaList(IList<IOpenApiSchema> schemas)
    {
        for (var i = 0; i < schemas.Count; i++)
            schemas[i] = RewriteFormFileSchema(schemas[i])!;
    }

    static bool IsFormFileRef(IOpenApiSchema? schema)
        => schema is OpenApiSchemaReference schemaRef && GetReferenceId(schemaRef) is "IFormFile";

    static bool IsFormFileCollectionRef(IOpenApiSchema? schema)
        => schema is OpenApiSchemaReference schemaRef &&
           GetReferenceId(schemaRef) is { } refId &&
           (refId is "IFormFileCollection" ||
            refId.Contains("IFormFileCollection", StringComparison.Ordinal) ||
            refId.Contains("IEnumerableOfIFormFile", StringComparison.Ordinal) ||
            refId.Contains("ListOfIFormFile", StringComparison.Ordinal) ||
            refId.Contains("IFormFile[]", StringComparison.Ordinal));

    static string? GetReferenceId(OpenApiSchemaReference schemaRef)
        => schemaRef.Reference.Id ?? schemaRef.Id;

    static OpenApiSchema FormFileBinarySchema()
        => new() { Type = JsonSchemaType.String, Format = "binary" };

    static OpenApiSchema FormFileArraySchema()
        => new()
        {
            Type = JsonSchemaType.Array,
            Items = FormFileBinarySchema()
        };

    readonly record struct OrderedVariantGroup(string SourceRefId, string[] VariantIds);

    readonly record struct SchemaSignatureCacheKey(string RefId, int AliasRevision);
}
