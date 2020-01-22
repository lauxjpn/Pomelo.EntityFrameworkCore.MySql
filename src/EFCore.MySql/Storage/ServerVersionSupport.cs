using System.Linq;

namespace Pomelo.EntityFrameworkCore.MySql.Storage
{
    public class ServerVersionSupport
    {
        public ServerVersion[] SupportedServerVersions { get; }

        public ServerVersionSupport(params string[] supportedServerVersions)
            : this(supportedServerVersions.Select(s => new ServerVersion(s)).ToArray())
        {
        }

        public ServerVersionSupport(params ServerVersion[] supportedServerVersions)
        {
            SupportedServerVersions = supportedServerVersions;
        }

        public bool IsSupported(ServerVersion serverVersion)
        {
            if (SupportedServerVersions.Length <= 0)
            {
                return false;
            }

            return SupportedServerVersions
                       .Where(s => serverVersion.Type == s.Type)
                       .OrderByDescending(s => s.Version)
                       .FirstOrDefault()?.Version != null;
        }

        public override string ToString()
            => string.Join(", ", SupportedServerVersions.Select(s => s.ToString()));
    }
}
