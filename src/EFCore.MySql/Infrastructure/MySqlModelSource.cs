using System.Diagnostics;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Storage.Internal;

namespace Pomelo.EntityFrameworkCore.MySql.Infrastructure
{
    public class MySqlModelSource : ModelSource
    {
        private readonly IMySqlConnectionInfo _connectionInfo;

        public MySqlModelSource(
            [NotNull] ModelSourceDependencies dependencies,
            IMySqlConnectionInfo connectionInfo)
            : base(dependencies)
        {
            _connectionInfo = connectionInfo;
        }

        public override IModel GetModel(DbContext context, IConventionSetBuilder conventionSetBuilder)
        {
            EnsureActualServerVersionIsAvailable(context);
            return base.GetModel(context, conventionSetBuilder);
        }

        private void EnsureActualServerVersionIsAvailable(DbContext context)
        {
            // Open the database connection in case auto detection is being used, so that the actual
            // server version is available when type mappings for the model are being retrieved.
            if (_connectionInfo.ServerVersion.IsDefault)
            {
                var connection = context.GetService<IRelationalConnection>();
                context.GetService<IExecutionStrategyFactory>().Create().Execute(connection, c => c.Open());
            }
        }
    }
}
