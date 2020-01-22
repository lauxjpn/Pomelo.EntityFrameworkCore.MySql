using Pomelo.EntityFrameworkCore.MySql.Storage;
using Pomelo.EntityFrameworkCore.MySql.Storage.Internal;
using Xunit;

namespace Pomelo.EntityFrameworkCore.MySql.FunctionalTests.TestUtilities.Attributes
{
    public class SupportedServerVersionLessThanFactAttribute : FactAttribute
    {
        private readonly ServerVersionSupport _serverVersionSupport;
        private string _skip;

        public virtual string Unsupported { get; set; }

        public override string Skip
        {
            get => !IsSupported() && string.IsNullOrEmpty(_skip)
                ? Unsupported ?? $"Test is supported only on server versions lower than {_serverVersionSupport}."
                : _skip;
            set => _skip = value;
        }

        public SupportedServerVersionLessThanFactAttribute(params string[] versionsOrKeys)
            : this(ServerVersion.GetSupport(versionsOrKeys))
        {
        }

        private SupportedServerVersionLessThanFactAttribute(ServerVersionSupport serverVersionSupport)
        {
            _serverVersionSupport = serverVersionSupport;
        }

        private bool IsSupported()
            => !(_serverVersionSupport?.IsSupported(AppConfig.ServerVersion) ?? true);
    }
}
