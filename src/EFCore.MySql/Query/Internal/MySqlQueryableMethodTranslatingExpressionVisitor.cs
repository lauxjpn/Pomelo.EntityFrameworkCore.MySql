// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Pomelo.EntityFrameworkCore.MySql.Query.ExpressionTranslators.Internal;
using Pomelo.EntityFrameworkCore.MySql.Storage.Internal;

namespace Pomelo.EntityFrameworkCore.MySql.Query.Internal;

public class MySqlQueryableMethodTranslatingExpressionVisitor : RelationalQueryableMethodTranslatingExpressionVisitor
{
    private readonly IMySqlOptions _options;
    private readonly MySqlSqlExpressionFactory _sqlExpressionFactory;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public MySqlQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext,
        IMySqlOptions options)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _sqlExpressionFactory = (MySqlSqlExpressionFactory)relationalDependencies.SqlExpressionFactory;
        _typeMappingSource = relationalDependencies.TypeMappingSource;
        _options = options;
    }

    protected MySqlQueryableMethodTranslatingExpressionVisitor(
        MySqlQueryableMethodTranslatingExpressionVisitor parentVisitor)
        : base(parentVisitor)
    {
        _sqlExpressionFactory = parentVisitor._sqlExpressionFactory;
        _typeMappingSource = parentVisitor._typeMappingSource;
        _options = parentVisitor._options;
    }

    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => new MySqlQueryableMethodTranslatingExpressionVisitor(this);

    protected override bool IsValidSelectExpressionForExecuteDelete(
        SelectExpression selectExpression,
        StructuralTypeShaperExpression shaper,
        [NotNullWhen(true)] out TableExpression tableExpression)
    {
        if (selectExpression.Offset == null
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Having == null
            && (selectExpression.Tables.Count == 1 || selectExpression.Orderings.Count == 0))
        {
            TableExpressionBase table;
            if (selectExpression.Tables.Count == 1)
            {
                table = selectExpression.Tables[0];
            }
            else
            {
                var projectionBindingExpression = (ProjectionBindingExpression)shaper.ValueBufferExpression;
                var entityProjectionExpression = (StructuralTypeProjectionExpression)selectExpression.GetProjection(projectionBindingExpression);
                var column = entityProjectionExpression.BindProperty(shaper.StructuralType.GetProperties().First());
                table = column.Table;
                if (table is JoinExpressionBase joinExpressionBase)
                {
                    table = joinExpressionBase.Table;
                }
            }

            if (table is TableExpression te)
            {
                tableExpression = te;
                return true;
            }
        }

        tableExpression = null;
        return false;
    }

    protected override bool IsValidSelectExpressionForExecuteUpdate(
        SelectExpression selectExpression,
        TableExpressionBase targetTable,
        [NotNullWhen(true)] out TableExpression tableExpression)
    {
        if (selectExpression is
            {
                Offset: null,
                IsDistinct: false,
                GroupBy: [],
                Having: null,
                Orderings: []
            })
        {
            TableExpressionBase table;
            if (selectExpression.Tables.Count == 1)
            {
                table = selectExpression.Tables[0];
            }
            else
            {
                table = targetTable;

                if (selectExpression.Tables.Count > 1 &&
                    table is JoinExpressionBase joinExpressionBase)
                {
                    table = joinExpressionBase.Table;
                }
            }

            if (table is TableExpression te)
            {
                tableExpression = te;
                return true;
            }
        }

        tableExpression = null;
        return false;
    }

    protected override ShapedQueryExpression TranslateAny(ShapedQueryExpression source, LambdaExpression predicate)
    {
        // Simplify x.Array.Any() => json_array_length(x.Array) > 0 instead of WHERE EXISTS (SELECT 1 FROM json_each(x.Array))
        if (predicate is null
            && source.QueryExpression is SelectExpression
            {
                Tables: [TableValuedFunctionExpression { Name: "JSON_TABLE", Schema: null, IsBuiltIn: true, Arguments: [var array] }],
                GroupBy: [],
                Having: null,
                IsDistinct: false,
                Limit: null,
                Offset: null
            })
        {
            var translation =
                _sqlExpressionFactory.GreaterThan(
                    _sqlExpressionFactory.NullableFunction(
                        "JSON_LENGTH",
                        new[] { array },
                        typeof(int)),
                    _sqlExpressionFactory.Constant(0));

            return source.UpdateQueryExpression(_sqlExpressionFactory.Select(translation));
        }

        return base.TranslateAny(source, predicate);
    }

    protected override ShapedQueryExpression TranslateElementAtOrDefault(
        ShapedQueryExpression source,
        Expression index,
        bool returnDefault)
    {
        // TODO: Make sure we want to actually transform to JSON_VALUE, #30981
        if (!returnDefault
            && source.QueryExpression is SelectExpression
            {
                Tables: [MySqlJsonTableExpression { Arguments: [var jsonArrayColumn] } openJsonExpression],
                GroupBy: [],
                Having: null,
                IsDistinct: false,
                Limit: null,
                Offset: null,
                // We can only apply the indexing if the JSON array is ordered by its natural ordered, i.e. by the "key" column that
                // we created in TranslateCollection. For example, if another ordering has been applied (e.g. by the JSON elements
                // themselves), we can no longer simply index into the original array.
                Orderings:
                [
                    {
                        Expression: SqlUnaryExpression
                        {
                            OperatorType: ExpressionType.Convert,
                            Operand: ColumnExpression { Name: "key", Table: var orderingTable }
                        }
                    }
                ]
            } selectExpression
            && TranslateExpression(index) is { } translatedIndex
            && orderingTable == openJsonExpression)
        {
            // Index on JSON array

            // Extract the column projected out of the source, and simplify the subquery to a simple JsonScalarExpression
            var shaperExpression = source.ShaperExpression;
            if (shaperExpression is UnaryExpression { NodeType: ExpressionType.Convert } unaryExpression
                && unaryExpression.Operand.Type.IsNullableType()
                && unaryExpression.Operand.Type.UnwrapNullableType() == unaryExpression.Type)
            {
                shaperExpression = unaryExpression.Operand;
            }

            if (shaperExpression is ProjectionBindingExpression projectionBindingExpression
                && selectExpression.GetProjection(projectionBindingExpression) is SqlExpression projection)
            {
                // OPENJSON's value column is an nvarchar(max); if this is a collection column whose type mapping is know, the projection
                // contains a CAST node which we unwrap
                var projectionColumn = projection switch
                {
                    ColumnExpression c => c,
                    SqlUnaryExpression { OperatorType: ExpressionType.Convert, Operand: ColumnExpression c } => c,
                    _ => null
                };

                if (projectionColumn is not null)
                {
                    // If the inner expression happens to itself be a JsonScalarExpression, simply append the two paths to avoid creating
                    // JSON_VALUE within JSON_VALUE.
                    var (json, path) = jsonArrayColumn is JsonScalarExpression innerJsonScalarExpression
                        ? (innerJsonScalarExpression.Json,
                            innerJsonScalarExpression.Path.Append(new PathSegment(translatedIndex)).ToArray())
                        : (jsonArrayColumn, new PathSegment[] { new(translatedIndex) });

                    var translation = new JsonScalarExpression(
                        json,
                        path,
                        projection.Type,
                        projection.TypeMapping,
                        projectionColumn.IsNullable);

                    return source.UpdateQueryExpression(_sqlExpressionFactory.Select(translation));
                }
            }
        }

        return base.TranslateElementAtOrDefault(source, index, returnDefault);
    }

    protected override ShapedQueryExpression TransformJsonQueryToTable(JsonQueryExpression jsonQueryExpression)
    {
        var entityType = jsonQueryExpression.EntityType;
        var textTypeMapping = _typeMappingSource.FindMapping(typeof(string));

        // TODO: Refactor this out
        // Calculate the table alias for the json_each expression based on the last named path segment
        // (or the JSON column name if there are none)
        var lastNamedPathSegment = jsonQueryExpression.Path.LastOrDefault(ps => ps.PropertyName is not null);
        var tableAlias = char.ToLowerInvariant((lastNamedPathSegment.PropertyName ?? jsonQueryExpression.JsonColumn.Name)[0]).ToString();

        // Handling a non-primitive JSON array is complicated on SQLite; unlike SQL Server OPENJSON and PostgreSQL jsonb_to_recordset,
        // SQLite's json_each can only project elements of the array, and not properties within those elements. For example:
        // SELECT value FROM json_each('[{"a":1,"b":"foo"}, {"a":2,"b":"bar"}]')
        // This will return two rows, each with a string column representing an array element (i.e. {"a":1,"b":"foo"}). To decompose that
        // into a and b columns, a further extraction is needed:
        // SELECT value ->> 'a' AS a, value ->> 'b' AS b FROM json_each('[{"a":1,"b":"foo"}, {"a":2,"b":"bar"}]')

        // We therefore generate a minimal subquery projecting out all the properties and navigations, wrapped by a SelectExpression
        // containing that:
        // SELECT ...
        // FROM (SELECT value ->> 'a' AS a, value ->> 'b' AS b FROM json_each(<JSON column>, <path>)) AS j
        // WHERE j.a = 8;

        // Unfortunately, while the subquery projects the entity, our EntityProjectionExpression currently supports only bare
        // ColumnExpression (the above requires JsonScalarExpression). So we hack as if the subquery projects an anonymous type instead,
        // with a member for each JSON property that needs to be projected. We then wrap it with a SelectExpression the projects a proper
        // EntityProjectionExpression.

        var jsonEachExpression = new MySqlJsonTableExpression(tableAlias, jsonQueryExpression.JsonColumn, jsonQueryExpression.Path);

#pragma warning disable EF1001 // Internal EF Core API usage.
        var selectExpression = new SelectExpression(
            jsonQueryExpression,
            jsonEachExpression,
            "key",
            typeof(int),
            _typeMappingSource.FindMapping(typeof(int))!);
#pragma warning restore EF1001 // Internal EF Core API usage.

        selectExpression.AppendOrdering(
            new OrderingExpression(
                selectExpression.CreateColumnExpression(
                    jsonEachExpression,
                    "key",
                    typeof(int),
                    typeMapping: _typeMappingSource.FindMapping(typeof(int)),
                    columnNullable: false),
                ascending: true));

        var propertyJsonScalarExpression = new Dictionary<ProjectionMember, Expression>();

        var jsonColumn = selectExpression.CreateColumnExpression(
            jsonEachExpression, "value", typeof(string), _typeMappingSource.FindMapping(typeof(string))); // TODO: nullable?

        var containerColumnName = entityType.GetContainerColumnName();
        Check.DebugAssert(containerColumnName is not null, "JsonQueryExpression to entity type without a container column name");

        // First step: build a SelectExpression that will execute json_each and project all properties and navigations out, e.g.
        // (SELECT value ->> 'a' AS a, value ->> 'b' AS b FROM json_each(c."JsonColumn", '$.Something.SomeCollection')

        // We're only interested in properties which actually exist in the JSON, filter out uninteresting shadow keys
        foreach (var property in GetAllPropertiesInHierarchy(entityType))
        {
            if (property.GetJsonPropertyName() is string jsonPropertyName)
            {
                // HACK: currently the only way to project multiple values from a SelectExpression is to simulate a Select out to an anonymous
                // type; this requires the MethodInfos of the anonymous type properties, from which the projection alias gets taken.
                // So we create fake members to hold the JSON property name for the alias.
                var projectionMember = new ProjectionMember().Append(new FakeMemberInfo(jsonPropertyName));

                propertyJsonScalarExpression[projectionMember] = new JsonScalarExpression(
                    jsonColumn,
                    new[] { new PathSegment(property.GetJsonPropertyName()!) },
                    property.ClrType.UnwrapNullableType(),
                    property.GetRelationalTypeMapping(),
                    property.IsNullable);
            }
        }

        foreach (var navigation in GetAllNavigationsInHierarchy(jsonQueryExpression.EntityType)
                     .Where(
                         n => n.ForeignKey.IsOwnership
                             && n.TargetEntityType.IsMappedToJson()
                             && n.ForeignKey.PrincipalToDependent == n))
        {
            var jsonNavigationName = navigation.TargetEntityType.GetJsonPropertyName();
            Check.DebugAssert(jsonNavigationName is not null, "Invalid navigation found on JSON-mapped entity");

            var projectionMember = new ProjectionMember().Append(new FakeMemberInfo(jsonNavigationName));

            propertyJsonScalarExpression[projectionMember] = new JsonScalarExpression(
                jsonColumn,
                new[] { new PathSegment(jsonNavigationName) },
                typeof(string),
                textTypeMapping,
                !navigation.ForeignKey.IsRequiredDependent);
        }

        selectExpression.ReplaceProjection(propertyJsonScalarExpression);

        // Second step: push the above SelectExpression down to a subquery, and project an entity projection from the outer
        // SelectExpression, i.e.
        // SELECT "t"."a", "t"."b"
        // FROM (SELECT value ->> 'a' ... FROM json_each(...))

        selectExpression.PushdownIntoSubquery();
        var subquery = selectExpression.Tables[0];

#pragma warning disable EF1001 // Internal EF Core API usage.
        var newOuterSelectExpression = new SelectExpression(
            jsonQueryExpression,
            subquery,
            "key",
            typeof(int),
            _typeMappingSource.FindMapping(typeof(int))!);
#pragma warning restore EF1001 // Internal EF Core API usage.

        newOuterSelectExpression.AppendOrdering(
            new OrderingExpression(
                selectExpression.CreateColumnExpression(
                    subquery,
                    "key",
                    typeof(int),
                    typeMapping: _typeMappingSource.FindMapping(typeof(int)),
                    columnNullable: false),
                ascending: true));

        return new ShapedQueryExpression(
            newOuterSelectExpression,
            new RelationalStructuralTypeShaperExpression(
                jsonQueryExpression.EntityType,
                new ProjectionBindingExpression(
                    newOuterSelectExpression,
                    new ProjectionMember(),
                    typeof(ValueBuffer)),
                false));

        // TODO: Move these to IEntityType?
        static IEnumerable<IProperty> GetAllPropertiesInHierarchy(IEntityType entityType)
            => entityType.GetAllBaseTypes().Concat(entityType.GetDerivedTypesInclusive())
                .SelectMany(t => t.GetDeclaredProperties());

        static IEnumerable<INavigation> GetAllNavigationsInHierarchy(IEntityType entityType)
            => entityType.GetAllBaseTypes().Concat(entityType.GetDerivedTypesInclusive())
                .SelectMany(t => t.GetDeclaredNavigations());
    }

    protected override ShapedQueryExpression TranslatePrimitiveCollection(SqlExpression sqlExpression, IProperty property, string tableAlias)
    {
        if (!_options.ServerVersion.Supports.JsonTable)
        {
            AddTranslationErrorDetails($"Querying of JSON arrays is not supported in server version {_options.ServerVersion}.");
            return null;
        }

        // Generate the JSON_TABLE() function expression, and wrap it in a SelectExpression.

        // Note that where the elementTypeMapping is known (i.e. collection columns), we immediately generate JSON_TABLE() with a COLUMNS clause
        // (i.e. with a columnInfo), which determines the type conversion to apply to the JSON elements coming out.
        // For parameter collections, the element type mapping will only be inferred and applied later (see
        // MySqlInferredTypeMappingApplier below), at which point the we'll apply it to add the COLUMNS clause.
        var elementTypeMapping = (RelationalTypeMapping)sqlExpression.TypeMapping?.ElementTypeMapping;

        var jsonTableExpression = new MySqlJsonTableExpression(
            tableAlias,
            sqlExpression,
            new[] { new PathSegment(_sqlExpressionFactory.Constant("*", RelationalTypeMapping.NullMapping)) },
            elementTypeMapping is not null
                ? new[]
                {
                    new MySqlJsonTableExpression.ColumnInfo
                    {
                        Name = "value",
                        TypeMapping = elementTypeMapping,
                        Path = new[] { new PathSegment(_sqlExpressionFactory.Constant(0, _typeMappingSource.FindMapping(typeof(int)))) },
                    }
                }
                : null);

        // Enabling this can crash MySQL 8.0 after some time:
        // if (elementTypeMapping is null)
        //     return null;

        var elementClrType = sqlExpression.Type.GetSequenceType();

        // If this is a collection property, get the element's nullability out of metadata. Otherwise, this is a parameter property, in
        // which case we only have the CLR type (note that we cannot produce different SQLs based on the nullability of an *element* in
        // a parameter collection - our caching mechanism only supports varying by the nullability of the parameter itself (i.e. the
        // collection).
        // TODO: if property is non-null, GetElementType() should never be null, but we have #31469 for shadow properties
        var isElementNullable = property?.GetElementType() is null
            ? elementClrType.IsNullableType()
            : property.GetElementType()!.IsNullable;

#pragma warning disable EF1001 // Internal EF Core API usage.
        var selectExpression = new SelectExpression(
            jsonTableExpression,
            columnName: "value",
            columnType: elementClrType,
            columnTypeMapping: elementTypeMapping,
            isElementNullable,
            identifierColumnName: "key",
            identifierColumnType: typeof(int),
            identifierColumnTypeMapping: _typeMappingSource.FindMapping(typeof(uint)));
#pragma warning restore EF1001 // Internal EF Core API usage.

        // JSON_TABLE() doesn't guarantee the ordering of the elements coming out; when using JSON_TABLE() without COLUMNS, a [key] column is returned
        // with the JSON array's ordering, which we can ORDER BY; this option doesn't exist with JSON_TABLE() with COLUMNS, unfortunately.
        // However, JSON_TABLE() with COLUMNS has better performance, and also applies JSON-specific conversions we cannot be done otherwise
        // (e.g. JSON_TABLE() with COLUMNS does base64 decoding for VARBINARY).
        // Here we generate JSON_TABLE() with COLUMNS, but also add an ordering by [key] - this is a temporary invalid representation.
        // In MySqlQueryTranslationPostprocessor, we'll post-process the expression; if the ORDER BY was stripped (e.g. because of
        // IN, EXISTS or a set operation), we'll just leave the JSON_TABLE() with COLUMNS. If not, we'll convert the JSON_TABLE() with COLUMNS to an
        // JSON_TABLE() without COLUMNS.
        // Note that the JSON_TABLE() 'key' column is an nvarchar - we convert it to an int before sorting.
        selectExpression.AppendOrdering(
            new OrderingExpression(
                selectExpression.CreateColumnExpression(
                    jsonTableExpression,
                    "key",
                    typeof(int),
                    typeMapping: _typeMappingSource.FindMapping(typeof(int)),
                    columnNullable: false),
                ascending: true));

        var shaperExpression = (Expression)new ProjectionBindingExpression(selectExpression, new ProjectionMember(), elementClrType.MakeNullable());
        if (shaperExpression.Type != elementClrType)
        {
            Check.DebugAssert(
                elementClrType.MakeNullable() == shaperExpression.Type,
                "expression.Type must be nullable of targetType");

            shaperExpression = Expression.Convert(shaperExpression, elementClrType);
        }

        return new ShapedQueryExpression(selectExpression, shaperExpression);
    }

    protected override Expression ApplyInferredTypeMappings(
        Expression expression,
        IReadOnlyDictionary<(TableExpressionBase, string), RelationalTypeMapping> inferredTypeMappings)
        => new MySqlInferredTypeMappingApplier(
            RelationalDependencies.Model, _typeMappingSource, _sqlExpressionFactory, inferredTypeMappings).Visit(expression);

    /// <summary>
    /// Wraps the given expression with any SQL logic necessary to convert a value coming out of a JSON document into the relational value
    /// represented by the given type mapping.
    /// </summary>
    private static SqlExpression ApplyJsonSqlConversion(
        SqlExpression expression,
        ISqlExpressionFactory sqlExpressionFactory,
        RelationalTypeMapping typeMapping,
        bool isNullable)
        => typeMapping switch
        {
            // TODO: EF Core 8 - Decide between UNHEX() and FROM_BASE64().
            ByteArrayTypeMapping => sqlExpressionFactory.Function("FROM_BASE64", new[] { expression }, isNullable, new[] { true }, typeof(byte[]), typeMapping),
            _ => expression
        };

    protected class MySqlInferredTypeMappingApplier : RelationalInferredTypeMappingApplier
    {
        private readonly IRelationalTypeMappingSource _typeMappingSource;
        private readonly ISqlExpressionFactory _sqlExpressionFactory;
        private Dictionary<TableExpressionBase, RelationalTypeMapping> _currentSelectInferredTypeMappings;

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public MySqlInferredTypeMappingApplier(
            IModel model,
            IRelationalTypeMappingSource typeMappingSource,
            ISqlExpressionFactory sqlExpressionFactory,
            IReadOnlyDictionary<(TableExpressionBase, string), RelationalTypeMapping> inferredTypeMappings)
            : base(model, sqlExpressionFactory, inferredTypeMappings)
        {
            (_typeMappingSource, _sqlExpressionFactory) = (typeMappingSource, sqlExpressionFactory);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override Expression VisitExtension(Expression expression)
        {
            switch (expression)
            {
                case MySqlJsonTableExpression { Name: "JSON_TABLE", Schema: null, IsBuiltIn: true } jsonEachExpression
                    when TryGetInferredTypeMapping(jsonEachExpression, "value", out var typeMapping):
                    return ApplyTypeMappingsOnJsonEachExpression(jsonEachExpression, typeMapping);

                // Above, we applied the type mapping the the parameter that JSON_TABLE accepts as an argument.
                // But the inferred type mapping also needs to be applied as a SQL conversion on the column projections coming out of the
                // SelectExpression containing the JSON_TABLE call. So we set state to know about JSON_TABLE tables and their type mappings
                // in the immediate SelectExpression, and continue visiting down (see ColumnExpression visitation below).
                case SelectExpression selectExpression:
                {
                    Dictionary<TableExpressionBase, RelationalTypeMapping> previousSelectInferredTypeMappings = null;

                    foreach (var table in selectExpression.Tables)
                    {
                        if (table is TableValuedFunctionExpression { Name: "JSON_TABLE", Schema: null, IsBuiltIn: true } jsonEachExpression
                            && TryGetInferredTypeMapping(jsonEachExpression, "value", out var inferredTypeMapping))
                        {
                            if (previousSelectInferredTypeMappings is null)
                            {
                                previousSelectInferredTypeMappings = _currentSelectInferredTypeMappings;
                                _currentSelectInferredTypeMappings = new Dictionary<TableExpressionBase, RelationalTypeMapping>();
                            }

                            _currentSelectInferredTypeMappings![jsonEachExpression] = inferredTypeMapping;
                        }
                    }

                    var visited = base.VisitExtension(expression);

                    _currentSelectInferredTypeMappings = previousSelectInferredTypeMappings;

                    return visited;
                }

                // Note that we match also ColumnExpressions which already have a type mapping, i.e. coming out of column collections (as
                // opposed to parameter collections, where the type mapping needs to be inferred). This is in order to apply SQL conversion
                // logic later in the process, see note in TranslateCollection.
                case ColumnExpression { Name: "value" } columnExpression
                    when _currentSelectInferredTypeMappings?.TryGetValue(columnExpression.Table, out var inferredTypeMapping) is true:
                    return ApplyJsonSqlConversion(
                        columnExpression.ApplyTypeMapping(inferredTypeMapping),
                        _sqlExpressionFactory,
                        inferredTypeMapping,
                        columnExpression.IsNullable);

                default:
                    return base.VisitExtension(expression);
            }
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected virtual TableValuedFunctionExpression ApplyTypeMappingsOnJsonEachExpression(
            MySqlJsonTableExpression jsonTableExpression,
            RelationalTypeMapping inferredTypeMapping)
        {
            // Constant queryables are translated to VALUES, no need for JSON.
            // Column queryables have their type mapping from the model, so we don't ever need to apply an inferred mapping on them.
            if (jsonTableExpression.JsonExpression is not SqlParameterExpression parameterExpression)
            {
                return jsonTableExpression;
            }

            if (_typeMappingSource.FindMapping(parameterExpression.Type, Model, inferredTypeMapping) is not MySqlStringTypeMapping
                parameterTypeMapping)
            {
                throw new InvalidOperationException("Type mapping for 'string' could not be found or was not a MySqlStringTypeMapping");
            }

            Check.DebugAssert(parameterTypeMapping.ElementTypeMapping != null, "Collection type mapping missing element mapping.");

            return jsonTableExpression.Update(
                parameterExpression.ApplyTypeMapping(parameterTypeMapping),
                jsonTableExpression.Path,
                new[]
                {
                    new MySqlJsonTableExpression.ColumnInfo
                    {
                        Name = "value",
                        TypeMapping = (RelationalTypeMapping)parameterTypeMapping.ElementTypeMapping,
                        Path = new[] { new PathSegment(_sqlExpressionFactory.Constant(0, _typeMappingSource.FindMapping(typeof(int)))) },
                    }
                });
        }
    }

    private sealed class FakeMemberInfo : MemberInfo
    {
        public FakeMemberInfo(string name)
            => Name = name;

        public override string Name { get; }

        public override object[] GetCustomAttributes(bool inherit)
            => throw new NotSupportedException();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
            => throw new NotSupportedException();
        public override bool IsDefined(Type attributeType, bool inherit)
            => throw new NotSupportedException();
        public override Type DeclaringType => throw new NotSupportedException();
        public override MemberTypes MemberType => throw new NotSupportedException();
        public override Type ReflectedType => throw new NotSupportedException();
    }
}
