using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MySql.Data.MySqlClient;
using Pomelo.EntityFrameworkCore.MySql.FunctionalTests.TestUtilities;
using Xunit;

namespace Pomelo.EntityFrameworkCore.MySql.FunctionalTests
{
    public class ReportedIssuesMySqlTest
        : IClassFixture<ReportedIssuesMySqlTest.ReportedIssuesMySqlFixture>
    {
        [ConditionalFact]
        public virtual void TestIssue831()
        {
            using var context = CreateContext();

            context.Set<test1>()
                .Add(new test1 {gender = "trans"});

            var outerExpection = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
            var innerException = Assert.IsType<MySqlException>(outerExpection.InnerException);
            Assert.Equal(
                innerException.Message,
                "Check constraint 'constraint_gender' is violated.");
        }

        public class test1
        {
            public int id { get; set; }
            public string gender { get; set; }
        }

        public class ReportedIssuesMySqlFixture : SharedStoreFixtureBase<PoolableDbContext>
        {
            protected override void OnModelCreating(
                ModelBuilder modelBuilder,
                DbContext context)
            {
                modelBuilder.Entity<test1>(entity =>
                {
                    entity.HasKey(e => e.id);

                    entity.Property(e => e.gender)
                        .HasMaxLength(6);

                    entity.HasCheckConstraint(
                        "constraint_gender",
                        "`gender` = 'male' or `gender` = 'female'");
                });
            }

            protected override void Seed(PoolableDbContext context)
            {
                context.Set<test1>().AddRange(
                    new test1 { gender = "male" },
                    new test1 { gender = "female" }
                );

                context.SaveChanges();
            }

            protected override ITestStoreFactory TestStoreFactory
                => MySqlTestStoreFactory.Instance;

            protected override string StoreName { get; } = "ReportedIssues";
        }

        public ReportedIssuesMySqlTest(ReportedIssuesMySqlFixture fixture)
            => Fixture = fixture;

        protected ReportedIssuesMySqlFixture Fixture { get; }
        protected DbContext CreateContext() => Fixture.CreateContext();
    }
}
