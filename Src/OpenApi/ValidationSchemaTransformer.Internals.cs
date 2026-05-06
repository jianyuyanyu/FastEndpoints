using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using FastEndpoints.OpenApi.ValidationProcessor;
using FastEndpoints.OpenApi.ValidationProcessor.Extensions;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace FastEndpoints.OpenApi;

sealed partial class ValidationSchemaTransformer
{
    const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    sealed record CachedValidatorRules(ReadOnlyDictionary<string, List<IValidationRule>> Rules, CachedValidatorRules[] IncludedRules);

    static OpenApiSchema? ResolveForMutation(IOpenApiSchema? schema,
                                             bool localizeReferencedSchemas,
                                             SharedContext sharedCtx,
                                             string operationKey,
                                             string schemaKey,
                                             Action<IOpenApiSchema> replace)
    {
        if (!localizeReferencedSchemas || schema is not OpenApiSchemaReference)
            return schema.ResolveSchema();

        return schema.EnsureSchemaForMutation(sharedCtx, operationKey, schemaKey, replace);
    }

    sealed class ValidationSchemaApplier : IDisposable
    {
        readonly SharedContext _sharedCtx;
        readonly ILogger<ValidationSchemaTransformer>? _logger;
        readonly FluentValidationRule[] _rules;
        readonly ChildValidatorResolver _childResolver;
        readonly bool _localizeReferencedSchemas;
        readonly bool _usePropertyNamingPolicy;
        readonly string _operationKey;
        readonly string _schemaKey;

        public ValidationSchemaApplier(SharedContext sharedCtx,
                                       IServiceResolver serviceResolver,
                                       ILogger<ValidationSchemaTransformer>? logger,
                                       Func<IServiceScope> createScope,
                                       FluentValidationRule[] rules,
                                       bool usePropertyNamingPolicy,
                                       string operationKey,
                                       string schemaKey,
                                       bool localizeReferencedSchemas = false)
        {
            _sharedCtx = sharedCtx;
            _logger = logger;
            _rules = rules;
            _usePropertyNamingPolicy = usePropertyNamingPolicy;
            _operationKey = operationKey;
            _schemaKey = schemaKey;
            _localizeReferencedSchemas = localizeReferencedSchemas;
            _childResolver = new(
                serviceResolver,
                sharedCtx,
                logger,
                createScope,
                ApplyValidator,
                ApplyRulesToSchema,
                usePropertyNamingPolicy,
                operationKey,
                schemaKey,
                localizeReferencedSchemas);
        }

        public void Dispose()
            => _childResolver.Dispose();

        public void ApplyValidatorRules(OpenApiSchema schema, CachedValidatorRules cachedRules, string propertyPrefix, HashSet<Type> activeChildValidators)
        {
            ApplyRulesToSchema(schema, new(cachedRules.Rules), propertyPrefix, activeChildValidators);

            foreach (var includedRules in cachedRules.IncludedRules)
                ApplyValidatorRules(schema, includedRules, propertyPrefix, activeChildValidators);
        }

        void ApplyValidator(OpenApiSchema schema, IValidator validator, string propertyPrefix, HashSet<Type> activeChildValidators)
        {
            var rulesDict = validator.GetDictionaryOfRules(_sharedCtx.NamingPolicy, _usePropertyNamingPolicy, GetValidatorTargetType(validator));
            ApplyRulesToSchema(schema, new(rulesDict), propertyPrefix, activeChildValidators);
            _childResolver.ApplyRulesFromIncludedValidators(schema, validator, propertyPrefix, activeChildValidators, _sharedCtx.NamingPolicy, []);
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ChildValidatorAdaptor<,>))]
        public static IEnumerable<IValidator> GetIncludedValidators(IValidator validator, ILogger<ValidationSchemaTransformer>? logger)
            => ChildValidatorResolver.GetIncludedValidators(validator, logger);

        void ApplyRulesToSchema(OpenApiSchema? schema, ValidationRuleLookup rules, string propertyPrefix, HashSet<Type> activeChildValidators)
        {
            if (schema is null)
                return;

            TryApplyRootValidation(schema, rules, propertyPrefix, activeChildValidators);

            if (schema.Properties is not null)
            {
                foreach (var (propertyName, propertySchema) in schema.Properties.ToArray())
                    TryApplyValidation(schema, rules, propertyName, propertySchema, propertyPrefix, activeChildValidators);
            }

            RecurseComposite(schema.AllOf, rules, propertyPrefix, activeChildValidators);
            RecurseComposite(schema.OneOf, rules, propertyPrefix, activeChildValidators);
            RecurseComposite(schema.AnyOf, rules, propertyPrefix, activeChildValidators);
        }

        void RecurseComposite(IList<IOpenApiSchema>? composite,
                              ValidationRuleLookup rules,
                              string propertyPrefix,
                              HashSet<Type> activeChildValidators)
        {
            if (composite is null)
                return;

            for (var i = 0; i < composite.Count; i++)
            {
                var resolved = ResolveForMutation(
                    composite[i],
                    _localizeReferencedSchemas,
                    _sharedCtx,
                    _operationKey,
                    $"{_schemaKey}.{propertyPrefix}.composite[{i}]",
                    localized => composite[i] = localized);

                if (resolved?.Properties is { Count: > 0 })
                    ApplyRulesToSchema(resolved, rules, propertyPrefix, activeChildValidators);
            }
        }

        void TryApplyValidation(OpenApiSchema schema,
                                ValidationRuleLookup rules,
                                string propertyName,
                                IOpenApiSchema? property,
                                string parameterPrefix,
                                HashSet<Type> activeChildValidators)
        {
            var fullPropertyName = $"{parameterPrefix}{propertyName}";

            if (rules.TryGetValue(fullPropertyName, out var validationRules))
            {
                var localizedPropertySchema = property is null
                                                  ? null
                                                  : ResolveForMutation(
                                                      property,
                                                      _localizeReferencedSchemas,
                                                      _sharedCtx,
                                                      _operationKey,
                                                      $"{_schemaKey}.{fullPropertyName}",
                                                      localized => schema.Properties![propertyName] = localized);

                foreach (var validationRule in validationRules)
                    ApplyValidationRule(schema, validationRule, propertyName, localizedPropertySchema, activeChildValidators);
            }

            if (property is null)
                return;

            var hasNestedObjectRules = rules.HasPrefix($"{fullPropertyName}.");
            var hasNestedArrayRules = rules.HasPrefix($"{fullPropertyName}[].");

            if (!hasNestedObjectRules && !hasNestedArrayRules)
                return;

            var propertySchema = ResolveForMutation(
                property,
                _localizeReferencedSchemas,
                _sharedCtx,
                _operationKey,
                $"{_schemaKey}.{fullPropertyName}",
                localized => schema.Properties![propertyName] = localized);

            if (hasNestedObjectRules &&
                propertySchema is not null &&
                propertySchema.Properties is { Count: > 0 } &&
                propertySchema != schema)
            {
                ApplyRulesToSchema(propertySchema, rules, $"{fullPropertyName}.", activeChildValidators);

                return;
            }

            if (hasNestedArrayRules &&
                propertySchema is { Type: { } type } &&
                type.HasFlag(JsonSchemaType.Array) &&
                ResolveForMutation(
                    propertySchema.Items,
                    _localizeReferencedSchemas,
                    _sharedCtx,
                    _operationKey,
                    $"{_schemaKey}.{fullPropertyName}[]",
                    localized => propertySchema.Items = localized) is { Properties.Count: > 0 } itemsSchema)
                ApplyRulesToSchema(itemsSchema, rules, $"{fullPropertyName}[].", activeChildValidators);
        }

        void TryApplyRootValidation(OpenApiSchema schema,
                                    ValidationRuleLookup rules,
                                    string propertyPrefix,
                                    HashSet<Type> activeChildValidators)
        {
            if (propertyPrefix.Length == 0 ||
                !rules.TryGetValue(propertyPrefix.TrimEnd('.'), out var validationRules))
                return;

            foreach (var validationRule in validationRules)
            {
                foreach (var ruleComponent in validationRule.Components)
                {
                    var propertyValidator = ruleComponent.Validator;

                    if (propertyValidator.Name == "ChildValidatorAdaptor")
                        _childResolver.ApplyChildValidator(schema, propertyValidator, activeChildValidators);
                }
            }
        }

        void ApplyValidationRule(OpenApiSchema schema,
                                 IValidationRule validationRule,
                                 string propertyName,
                                 OpenApiSchema? propertySchema,
                                 HashSet<Type> activeChildValidators)
        {
            foreach (var ruleComponent in validationRule.Components)
            {
                var propertyValidator = ruleComponent.Validator;

                if (propertyValidator.Name == "ChildValidatorAdaptor")
                {
                    _childResolver.ApplyChildValidator(schema, propertyName, propertyValidator, activeChildValidators);

                    continue;
                }

                foreach (var rule in _rules)
                {
                    if (!rule.Matches(propertyValidator))
                        continue;

                    try
                    {
                        rule.Apply(new(schema, propertyName, propertyValidator, ruleComponent.HasCondition(), propertySchema));
                    }
                    catch (Exception ex)
                    {
                        _logger?.FailedToApplyValidationRule(ex, propertyName, propertyValidator.Name);
                    }
                }
            }
        }

        internal readonly struct ValidationRuleLookup(ReadOnlyDictionary<string, List<IValidationRule>> rules)
        {
            readonly HashSet<string> _prefixes = BuildPrefixes(rules.Keys);

            public bool TryGetValue(string propertyName, [NotNullWhen(true)] out List<IValidationRule>? validationRules)
                => rules.TryGetValue(propertyName, out validationRules);

            public bool HasPrefix(string prefix)
                => _prefixes.Contains(prefix);

            static HashSet<string> BuildPrefixes(IEnumerable<string> ruleNames)
            {
                var prefixes = new HashSet<string>(StringComparer.Ordinal);

                foreach (var ruleName in ruleNames)
                {
                    for (var separatorIndex = ruleName.IndexOf('.');
                         separatorIndex >= 0;
                         separatorIndex = ruleName.IndexOf('.', separatorIndex + 1))
                    {
                        prefixes.Add(ruleName[..(separatorIndex + 1)]);
                    }
                }

                return prefixes;
            }
        }
    }

    sealed class ChildValidatorResolver(IServiceResolver serviceResolver,
                                        SharedContext sharedCtx,
                                        ILogger<ValidationSchemaTransformer>? logger,
                                        Func<IServiceScope> createScope,
                                        Action<OpenApiSchema, IValidator, string, HashSet<Type>> applyValidator,
                                        Action<OpenApiSchema?, ValidationSchemaApplier.ValidationRuleLookup, string, HashSet<Type>> applyRulesToSchema,
                                        bool usePropertyNamingPolicy,
                                        string operationKey,
                                        string schemaKey,
                                        bool localizeReferencedSchemas) : IDisposable
    {
        IServiceScope? _scope;

        IServiceProvider Services => (_scope ??= createScope()).ServiceProvider;

        public void Dispose()
            => _scope?.Dispose();

        public static IEnumerable<IValidator> GetIncludedValidators(IValidator validator, ILogger<ValidationSchemaTransformer>? logger)
        {
            if (validator is not IEnumerable<IValidationRule> rules)
                yield break;

            foreach (var rule in rules)
            {
                if (!rule.HasNoCondition() || rule is not IIncludeRule)
                    continue;

                foreach (var component in rule.Components)
                {
                    var validatorType = component.Validator.GetType();

                    if (!validatorType.IsGenericType || validatorType.GetGenericTypeDefinition() != typeof(ChildValidatorAdaptor<,>))
                        continue;

                    if (TryGetIncludedValidator(component.Validator, logger, out var includeValidator))
                        yield return includeValidator;
                }
            }
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ChildValidatorAdaptor<,>))]
        public void ApplyRulesFromIncludedValidators(OpenApiSchema schema,
                                                     IValidator validator,
                                                     string propertyPrefix,
                                                     HashSet<Type> activeChildValidators,
                                                     System.Text.Json.JsonNamingPolicy? namingPolicy,
                                                     HashSet<Type> activeIncludedValidators)
        {
            var validatorType = validator.GetType();

            if (!activeIncludedValidators.Add(validatorType))
                return;

            try
            {
                foreach (var includeValidator in GetIncludedValidators(validator, logger))
                {
                    var includeValidatorType = includeValidator.GetType();

                    if (activeIncludedValidators.Contains(includeValidatorType))
                        continue;

                    var rulesDict = includeValidator.GetDictionaryOfRules(namingPolicy, usePropertyNamingPolicy, GetValidatorTargetType(includeValidator));
                    applyRulesToSchema(schema, new(rulesDict), propertyPrefix, activeChildValidators);
                    ApplyRulesFromIncludedValidators(schema, includeValidator, propertyPrefix, activeChildValidators, namingPolicy, activeIncludedValidators);
                }
            }
            finally
            {
                activeIncludedValidators.Remove(validatorType);
            }
        }

        static bool TryGetIncludedValidator(IPropertyValidator adapter, ILogger<ValidationSchemaTransformer>? logger, [NotNullWhen(true)] out IValidator? validator)
        {
            validator = null;
            var adapterType = adapter.GetType();

            try
            {
                var accessor = ChildValidatorAdapterAccessor.Get(adapterType);

                if (!accessor.IsAvailable)
                    return false;

                var validationContext = accessor.CreateValidationContext(null);

                if (accessor.GetValidator(adapter, validationContext) is not IValidator resolvedValidator)
                    return false;

                validator = resolvedValidator;

                return true;
            }
            catch (Exception ex)
            {
                logger?.ExceptionProcessingValidator(ex, adapterType.Name);

                return false;
            }
        }

        public void ApplyChildValidator(OpenApiSchema schema, string propertyName, IPropertyValidator propertyValidator, HashSet<Type> activeChildValidators)
        {
            if (!TryCreateChildValidator(propertyValidator, activeChildValidators, out var validatorType, out var childValidator))
                return;

            try
            {
                if (schema.Properties?.TryGetValue(propertyName, out var childPropSchema) == true)
                {
                    var childSchema = ResolveForMutation(
                        childPropSchema,
                        localizeReferencedSchemas,
                        sharedCtx,
                        operationKey,
                        $"{schemaKey}.{propertyName}",
                        localized => schema.Properties![propertyName] = localized);

                    if (childSchema is not null)
                    {
                        if (childSchema.Type.HasValue &&
                            childSchema.Type.Value.HasFlag(JsonSchemaType.Array) &&
                            ResolveForMutation(
                                childSchema.Items,
                                localizeReferencedSchemas,
                                sharedCtx,
                                operationKey,
                                $"{schemaKey}.{propertyName}[]",
                                localized => childSchema.Items = localized) is { } itemsSchema)
                            applyValidator(itemsSchema, childValidator, string.Empty, activeChildValidators);
                        else
                            applyValidator(childSchema, childValidator, string.Empty, activeChildValidators);
                    }
                }
            }
            finally
            {
                activeChildValidators.Remove(validatorType);
            }
        }

        public void ApplyChildValidator(OpenApiSchema schema, IPropertyValidator propertyValidator, HashSet<Type> activeChildValidators)
        {
            if (TryCreateChildValidator(propertyValidator, activeChildValidators, out var validatorType, out var childValidator))
            {
                try
                {
                    applyValidator(schema, childValidator, string.Empty, activeChildValidators);
                }
                finally
                {
                    activeChildValidators.Remove(validatorType);
                }
            }
        }

        bool TryCreateChildValidator(IPropertyValidator propertyValidator,
                                     HashSet<Type> activeChildValidators,
                                     [NotNullWhen(true)] out Type? validatorType,
                                     [NotNullWhen(true)] out IValidator? childValidator)
        {
            validatorType = null;
            childValidator = null;

            var propertyValidatorType = propertyValidator.GetType();

            if (propertyValidatorType.Name.StartsWith("PolymorphicValidator"))
            {
                logger?.SwaggerWithFluentValidationIntegrationForPolymorphicValidatorsIsNotSupported(propertyValidatorType.Name);

                return false;
            }

            var validatorTypeObj = propertyValidatorType.GetProperty("ValidatorType")?.GetValue(propertyValidator);

            if (validatorTypeObj is not Type resolvedValidatorType)
                throw new InvalidOperationException("ChildValidatorAdaptor.ValidatorType is null");

            if (resolvedValidatorType.IsInterface || !activeChildValidators.Add(resolvedValidatorType))
                return false;

            validatorType = resolvedValidatorType;
            childValidator = (IValidator)serviceResolver.CreateInstance(resolvedValidatorType, Services);

            return true;
        }
    }

    sealed class ChildValidatorAdapterAccessor
    {
        static readonly ConcurrentDictionary<Type, ChildValidatorAdapterAccessor> _cache = new();

        public bool IsAvailable { get; private init; }
        public required Func<object?, object?> CreateValidationContext { get; init; }
        public required Func<object, object?, object?> GetValidator { get; init; }

        public static ChildValidatorAdapterAccessor Get(Type adapterType)
            => _cache.GetOrAdd(adapterType, Create);

        static ChildValidatorAdapterAccessor Create(Type adapterType)
        {
            var getValidatorMethod = adapterType.GetMethod("GetValidator", PublicInstance);

            if (getValidatorMethod is null)
                return Missing();

            var parameters = getValidatorMethod.GetParameters();

            if (parameters.Length < 2)
                return Missing();

            var contextType = parameters[0].ParameterType;
            var contextCtor = contextType.GetConstructors(PublicInstance)
                                         .FirstOrDefault(c => c.GetParameters().Length == 1);

            if (contextCtor is null)
                return Missing();

            return new()
            {
                IsAvailable = true,
                CreateValidationContext = CompileContextFactory(contextCtor),
                GetValidator = CompileGetValidator(adapterType, getValidatorMethod, parameters)
            };
        }

        static ChildValidatorAdapterAccessor Missing()
            => new()
            {
                IsAvailable = false,
                CreateValidationContext = static _ => null,
                GetValidator = static (_, _) => null
            };

        static Func<object?, object?> CompileContextFactory(ConstructorInfo constructor)
        {
            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var newContext = Expression.New(constructor, Expression.Convert(instanceParam, constructor.GetParameters()[0].ParameterType));
            var body = Expression.Convert(newContext, typeof(object));

            return Expression.Lambda<Func<object?, object?>>(body, instanceParam).Compile();
        }

        static Func<object, object?, object?> CompileGetValidator(Type adapterType, MethodInfo getValidatorMethod, ParameterInfo[] parameters)
        {
            var adapterParam = Expression.Parameter(typeof(object), "adapter");
            var contextParam = Expression.Parameter(typeof(object), "context");
            var typedAdapter = Expression.Convert(adapterParam, adapterType);
            var typedContext = Expression.Convert(contextParam, parameters[0].ParameterType);
            var typedValue = Expression.Constant(null, parameters[1].ParameterType);
            var call = Expression.Call(typedAdapter, getValidatorMethod, typedContext, typedValue);
            var body = Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<object, object?, object?>>(body, adapterParam, contextParam).Compile();
        }
    }

    static Type GetValidatorTargetType(IValidator validator)
        => validator.GetType().GetGenericArgumentsOfType(Types.ValidatorOf1)?[0] ?? validator.GetType();
}
