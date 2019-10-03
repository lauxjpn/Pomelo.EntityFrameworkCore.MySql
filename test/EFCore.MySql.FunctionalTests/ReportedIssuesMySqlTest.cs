using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Pomelo.EntityFrameworkCore.MySql.FunctionalTests.TestUtilities;
using Xunit;

namespace Pomelo.EntityFrameworkCore.MySql.FunctionalTests
{
    public class ReportedIssuesMySqlTest : IClassFixture<ReportedIssuesMySqlTest.ReportedIssuesMySqlFixture>
    {
        public ReportedIssuesMySqlTest(ReportedIssuesMySqlFixture fixture) => Fixture = fixture;

        protected ReportedIssuesMySqlFixture Fixture { get; }
        protected DbContext CreateContext() => Fixture.CreateContext();

        [ConditionalFact]
        public virtual void TestIssue836()
        {
            using var context = CreateContext();

            var query = context.Set<VisuPropertyInstanceEntity>()
                .ToArray();

            Assert.Equal(2, query.Length);
            Assert.Equal(query.First().PropertyTypeUuid, query.Last().PropertyTypeUuid);
        }

        public class VisuPropertyInstanceEntity
        {
            public int Id { get; set; }
            public Guid PropertyTypeUuid { get; set; }

            public virtual PropertyTypeEntity PropertyType { get; set; }
        }

        public class PropertyTypeEntity
        {
            public Guid Uuid { get; set; }
        }

        public class ReportedIssuesMySqlFixture : SharedStoreFixtureBase<PoolableDbContext>
        {
            protected override ITestStoreFactory TestStoreFactory => MySqlTestStoreFactory.Instance;
            protected override string StoreName { get; } = "CustomIssue";

            protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
            {
                modelBuilder.Entity<PropertyTypeEntity>(entity =>
                {
                    entity.HasKey(e => e.Uuid);
                });

                modelBuilder.Entity<VisuPropertyInstanceEntity>(entity =>
                {
                    entity.HasKey(e => e.Id);

                    entity.HasOne(e => e.PropertyType)
                        .WithMany()
                        .HasForeignKey(e => e.PropertyTypeUuid);
                });
            }

            protected override void Seed(PoolableDbContext context)
            {
                var propertyType = new PropertyTypeEntity { Uuid = Guid.NewGuid() };
                context.Set<PropertyTypeEntity>().Add(propertyType);

                context.Set<VisuPropertyInstanceEntity>().AddRange(
                    new VisuPropertyInstanceEntity { PropertyTypeUuid = propertyType.Uuid },
                    new VisuPropertyInstanceEntity { PropertyTypeUuid = propertyType.Uuid }
                );

                context.SaveChanges();
            }
        }
    }
}
