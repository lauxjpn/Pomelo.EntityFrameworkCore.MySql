// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
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
    private ISqlExpressionFactory _sqlExpressionFactory;
    private IRelationalTypeMappingSource _typeMappingSource;

    public MySqlQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext,
        IMySqlOptions options)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _sqlExpressionFactory = relationalDependencies.SqlExpressionFactory;
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

    protected override ShapedQueryExpression TranslatePrimitiveCollection(SqlExpression sqlExpression, IProperty property, string tableAlias)
    {
        if (!_options.ServerVersion.Supports.JsonTable)
        {
            AddTranslationErrorDetails($"Querying of JSON arrays is not supported in server version {_options.ServerVersion}.");
            return null;
        }

        // Generate the OPENJSON function expression, and wrap it in a SelectExpression.

        // Note that where the elementTypeMapping is known (i.e. collection columns), we immediately generate OPENJSON with a WITH clause
        // (i.e. with a columnInfo), which determines the type conversion to apply to the JSON elements coming out.
        // For parameter collections, the element type mapping will only be inferred and applied later (see
        // SqlServerInferredTypeMappingApplier below), at which point the we'll apply it to add the WITH clause.
        var elementTypeMapping = (RelationalTypeMapping)sqlExpression.TypeMapping?.ElementTypeMapping;

        var openJsonExpression = new SqlServerOpenJsonExpression(
            tableAlias,
            sqlExpression,
            new[] { new PathSegment(_sqlExpressionFactory.Constant("*", RelationalTypeMapping.NullMapping)) },
            elementTypeMapping is not null
                ? new[]
                {
                    new SqlServerOpenJsonExpression.ColumnInfo
                    {
                        Name = "value",
                        TypeMapping = elementTypeMapping,
                        Path = new[] { new PathSegment(_sqlExpressionFactory.Constant(0, _typeMappingSource.FindMapping(typeof(int)))) },
                    }
                }
                : null);

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
            openJsonExpression,
            columnName: "value",
            columnType: elementClrType,
            columnTypeMapping: elementTypeMapping,
            isElementNullable/*,
            identifierColumnName: "key",
            identifierColumnType: typeof(string),
            identifierColumnTypeMapping: _typeMappingSource.FindMapping("nvarchar(4000)")*/);
#pragma warning restore EF1001 // Internal EF Core API usage.

        // OPENJSON doesn't guarantee the ordering of the elements coming out; when using OPENJSON without WITH, a [key] column is returned
        // with the JSON array's ordering, which we can ORDER BY; this option doesn't exist with OPENJSON with WITH, unfortunately.
        // However, OPENJSON with WITH has better performance, and also applies JSON-specific conversions we cannot be done otherwise
        // (e.g. OPENJSON with WITH does base64 decoding for VARBINARY).
        // Here we generate OPENJSON with WITH, but also add an ordering by [key] - this is a temporary invalid representation.
        // In SqlServerQueryTranslationPostprocessor, we'll post-process the expression; if the ORDER BY was stripped (e.g. because of
        // IN, EXISTS or a set operation), we'll just leave the OPENJSON with WITH. If not, we'll convert the OPENJSON with WITH to an
        // OPENJSON without WITH.
        // Note that the OPENJSON 'key' column is an nvarchar - we convert it to an int before sorting.
        // selectExpression.AppendOrdering(
        //     new OrderingExpression(
        //         _sqlExpressionFactory.Convert(
        //             selectExpression.CreateColumnExpression(
        //                 openJsonExpression,
        //                 "key",
        //                 typeof(string),
        //                 typeMapping: _typeMappingSource.FindMapping("nvarchar(4000)"),
        //                 columnNullable: false),
        //             typeof(int),
        //             _typeMappingSource.FindMapping(typeof(int))),
        //         ascending: true));

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
                case TableValuedFunctionExpression { Name: "JSON_TABLE", Schema: null, IsBuiltIn: true } jsonEachExpression
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
            TableValuedFunctionExpression jsonEachExpression,
            RelationalTypeMapping inferredTypeMapping)
        {
            // Constant queryables are translated to VALUES, no need for JSON.
            // Column queryables have their type mapping from the model, so we don't ever need to apply an inferred mapping on them.
            if (jsonEachExpression.Arguments[0] is not SqlParameterExpression parameterExpression)
            {
                return jsonEachExpression;
            }

            if (_typeMappingSource.FindMapping(parameterExpression.Type, Model, inferredTypeMapping) is not MySqlStringTypeMapping
                parameterTypeMapping)
            {
                throw new InvalidOperationException("Type mapping for 'string' could not be found or was not a MySqlStringTypeMapping");
            }

            Check.DebugAssert(parameterTypeMapping.ElementTypeMapping != null, "Collection type mapping missing element mapping.");

            return jsonEachExpression.Update(new[] { parameterExpression.ApplyTypeMapping(parameterTypeMapping) });
        }
    }
}
