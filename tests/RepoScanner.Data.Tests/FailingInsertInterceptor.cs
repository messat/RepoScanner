using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RepoScanner.Data.Tests
{
    // Makes every INSERT into one table fail, as if the database had rejected it. Used to prove a
    // repository method saves all or nothing: when part of the write fails, none of it is kept.
    internal sealed class FailingInsertInterceptor : DbCommandInterceptor
    {
        private readonly string _insertIntoTable;

        public FailingInsertInterceptor(string tableName)
        {
            // SQLite quotes table names with double quotes: INSERT INTO "ScanRuns" ...
            _insertIntoTable = $"INSERT INTO \"{tableName}\"";
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            FailIfInsert(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailIfInsert(command);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            FailIfInsert(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailIfInsert(command);
            return new ValueTask<InterceptionResult<int>>(result);
        }

        private void FailIfInsert(DbCommand command)
        {
            if (command.CommandText.Contains(_insertIntoTable, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Simulated failure: {_insertIntoTable}");
            }
        }
    }
}
