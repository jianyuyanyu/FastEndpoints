// Original: https://github.com/zymlabs/nswag-fluentvalidation
// MIT License
// Copyright (c) 2019 Zym Labs LLC

using FluentValidation.Validators;
using Microsoft.OpenApi;

namespace FastEndpoints.OpenApi.ValidationProcessor;

[HideFromDocs]
public class RuleContext(OpenApiSchema schema,
                         string propertyKey,
                         IPropertyValidator propertyValidator,
                         bool hasCondition,
                         OpenApiSchema? propertySchema = null)
{
    public OpenApiSchema Schema { get; } = schema;

    readonly OpenApiSchema? _propertySchema = propertySchema;

    public string PropertyKey { get; } = propertyKey;

    public IPropertyValidator PropertyValidator { get; } = propertyValidator;

    public bool HasCondition { get; set; } = hasCondition;

    /// <summary>
    /// tries to resolve the concrete <see cref="OpenApiSchema" /> for <see cref="PropertyKey" /> from <see cref="Schema" />.
    /// </summary>
    public bool TryGetPropertySchema(out OpenApiSchema propertySchema)
    {
        if (_propertySchema is not null)
        {
            propertySchema = _propertySchema;

            return true;
        }

        if (Schema.Properties?.TryGetValue(PropertyKey, out var p) == true && p.ResolveSchema() is { } s)
        {
            propertySchema = s;

            return true;
        }

        propertySchema = null!;

        return false;
    }
}
