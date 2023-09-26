// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Pomelo.EntityFrameworkCore.MySql.Query.Expressions.Internal;
using Pomelo.EntityFrameworkCore.MySql.Query.ExpressionTranslators.Internal;

namespace Pomelo.EntityFrameworkCore.MySql.Query.ExpressionVisitors.Internal;

/// <summary>
/// Inject parameter inlining expressions where parameters are not supported for some reason.
/// </summary>
public class MySqlParameterInliningExpressionVisitor : ExpressionVisitor
{
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlExpressionFactory _sqlExpressionFactory;
    private readonly IReadOnlyDictionary<string, object> _parametersValues;
    private readonly IMySqlOptions _options;

    private bool _inJsonTableSourceParameterCall;

    public MySqlParameterInliningExpressionVisitor(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlExpressionFactory sqlExpressionFactory,
        IMySqlOptions options,
        IReadOnlyDictionary<string, object> parametersValues)
    {
        _typeMappingSource = typeMappingSource;
        _sqlExpressionFactory = sqlExpressionFactory;
        _parametersValues = parametersValues;
        _options = options;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
        => extensionExpression switch
        {
            MySqlJsonTableExpression jsonTableExpression => VisitJsonTable(jsonTableExpression),
            SqlParameterExpression sqlParameterExpression => VisitSqlParameter(sqlParameterExpression),
            ShapedQueryExpression shapedQueryExpression => shapedQueryExpression.Update(
                Visit(shapedQueryExpression.QueryExpression),
                Visit(shapedQueryExpression.ShaperExpression)),
            _ => base.VisitExtension(extensionExpression)
        };

    protected virtual Expression VisitJsonTable(MySqlJsonTableExpression jsonTableExpression)
    {
        var parentInJsonTableSourceParameterCall = _inJsonTableSourceParameterCall;
        _inJsonTableSourceParameterCall = true;
        var jsonExpression = (SqlExpression)Visit(jsonTableExpression.JsonExpression);
        _inJsonTableSourceParameterCall = parentInJsonTableSourceParameterCall;

        return jsonTableExpression.Update(
            jsonExpression,
            jsonTableExpression.Path,
            jsonTableExpression.ColumnInfos);
    }

    protected virtual Expression VisitSqlParameter(SqlParameterExpression sqlParameterExpression)
    {
        if (_inJsonTableSourceParameterCall &&
            !_options.ServerVersion.Supports.JsonTableImplementationUsingParameterAsSourceWithoutEngineCrash)
        {
            return new MySqlInlinedParameterExpression(
                sqlParameterExpression,
                _sqlExpressionFactory.Constant(
                    _parametersValues[sqlParameterExpression.Name],
                    sqlParameterExpression.TypeMapping));
        }

        return sqlParameterExpression;
    }
}
