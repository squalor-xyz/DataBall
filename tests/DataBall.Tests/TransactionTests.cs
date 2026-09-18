// SPDX-License-Identifier: Apache-2.0
using System;
using System.Linq;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class TransactionTests
    {
        [Fact]
        public void AddColumn_FailedPopulate_DoesNotLeaveNullColumn()
        {
            using var store = new DuckDbStore();
            store.Execute("CREATE TABLE \"data\" (\"Name\" VARCHAR)");
            store.Execute("INSERT INTO \"data\" VALUES ('A')");
            Assert.Throws<InvalidOperationException>(() => store.InTransaction(() =>
            {
                store.Execute("ALTER TABLE \"data\" ADD COLUMN \"Age\" INTEGER");
                throw new InvalidOperationException("populate failed");
            }));
            Assert.DoesNotContain(
                store.GetColumns(),
                c => c.Name.Equals("Age", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ImportReplace_FailedCreate_DoesNotLeaveEmptyData()
        {
            using var store = new DuckDbStore();
            store.Execute("CREATE TABLE \"data\" (\"Name\" VARCHAR)");
            store.Execute("INSERT INTO \"data\" VALUES ('keep')");
            Assert.Throws<InvalidOperationException>(() => store.InTransaction(() =>
            {
                store.Execute("DROP TABLE IF EXISTS \"data\"");
                throw new InvalidOperationException("create failed");
            }));
            Assert.True(store.DataTableExists());
            var rows = store.Query("SELECT \"Name\" FROM \"data\"");
            Assert.Equal("keep", rows[0]["Name"]?.ToString());
        }
    }
}
