using System;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;

namespace Pomelo.EntityFrameworkCore.MySql.Storage.Internal
{
    public class MySqlConnectionInfo : IMySqlConnectionInfo
    {
        private readonly IMySqlOptions _options;
        private ServerVersion _serverVersion;

        public MySqlConnectionInfo([NotNull] IMySqlOptions options)
        {
            _options = options;
        }

        /// <summary>
        /// The actual ServerVersion of the first opened connection or IMySqlOptions.ServerVersion,
        /// if the latter was set explicitly.
        /// </summary>
        /// <remarks>If the property is retrieved before the connection was opened, it
        /// returns the default ServerVersion of IMySqlOptions with the `IsDefault`
        /// property set to `true`.</remarks>
        public virtual ServerVersion ServerVersion => _serverVersion ?? _options.ServerVersion;

        internal static void SetServerVersion(
            [NotNull] MySqlConnection connection,
            [NotNull] IServiceProvider serviceProvider)
        {
            var connectionInfo = (MySqlConnectionInfo)serviceProvider.GetRequiredService<IMySqlConnectionInfo>();

            if (connectionInfo._serverVersion == null)
            {
                connectionInfo._serverVersion = connectionInfo._options.ServerVersion.IsDefault
                    ? new ServerVersion(connection.ServerVersion)
                    : connectionInfo._options.ServerVersion;
            }
        }
    }
}
