﻿using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SQLitePCL;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.DistributedLocking;
using Umbraco.Cms.Core.DistributedLocking.Exceptions;
using Umbraco.Cms.Core.Exceptions;
using Umbraco.Cms.Persistence.EFCore.Entities;
using Umbraco.Cms.Persistence.EFCore.Scoping;
using Umbraco.Extensions;

namespace Umbraco.Cms.Persistence.EFCore;

public class SqliteEFCoreDistributedLockingMechanism : IDistributedLockingMechanism
{
    private readonly IOptionsMonitor<ConnectionStrings> _connectionStrings;
    private readonly IOptionsMonitor<GlobalSettings> _globalSettings;
    private readonly ILogger<SqliteEFCoreDistributedLockingMechanism> _logger;
    private readonly Lazy<IEFCoreScopeAccessor<UmbracoEFContext>> _efCoreScopeAccessor;

    public SqliteEFCoreDistributedLockingMechanism(
        ILogger<SqliteEFCoreDistributedLockingMechanism> logger,
        Lazy<IEFCoreScopeAccessor<UmbracoEFContext>> efCoreScopeAccessor,
        IOptionsMonitor<GlobalSettings> globalSettings,
        IOptionsMonitor<ConnectionStrings> connectionStrings)
    {
        _logger = logger;
        _efCoreScopeAccessor = efCoreScopeAccessor;
        _connectionStrings = connectionStrings;
        _globalSettings = globalSettings;
    }

    public bool HasActiveRelatedScope => _efCoreScopeAccessor.Value.AmbientScope is not null;

    /// <inheritdoc />
    public bool Enabled => _connectionStrings.CurrentValue.IsConnectionStringConfigured() &&
                           string.Equals(_connectionStrings.CurrentValue.ProviderName, "Microsoft.Data.Sqlite", StringComparison.InvariantCultureIgnoreCase) && _efCoreScopeAccessor.Value.AmbientScope is not null;

    // With journal_mode=wal we can always read a snapshot.
    public IDistributedLock ReadLock(int lockId, TimeSpan? obtainLockTimeout = null)
    {
        obtainLockTimeout ??= _globalSettings.CurrentValue.DistributedLockingReadLockDefaultTimeout;
        return new SqliteDistributedLock(this, lockId, DistributedLockType.ReadLock, obtainLockTimeout.Value);
    }

    // With journal_mode=wal only a single write transaction can exist at a time.
    public IDistributedLock WriteLock(int lockId, TimeSpan? obtainLockTimeout = null)
    {
        obtainLockTimeout ??= _globalSettings.CurrentValue.DistributedLockingWriteLockDefaultTimeout;
        return new SqliteDistributedLock(this, lockId, DistributedLockType.WriteLock, obtainLockTimeout.Value);
    }

    private class SqliteDistributedLock : IDistributedLock
    {
        private readonly SqliteEFCoreDistributedLockingMechanism _parent;
        private readonly TimeSpan _timeout;

        public SqliteDistributedLock(
            SqliteEFCoreDistributedLockingMechanism parent,
            int lockId,
            DistributedLockType lockType,
            TimeSpan timeout)
        {
            _parent = parent;
            _timeout = timeout;
            LockId = lockId;
            LockType = lockType;

            _parent._logger.LogDebug("Requesting {lockType} for id {id}", LockType, LockId);

            try
            {
                switch (lockType)
                {
                    case DistributedLockType.ReadLock:
                        ObtainReadLock();
                        break;
                    case DistributedLockType.WriteLock:
                        ObtainWriteLock();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(lockType), lockType, @"Unsupported lockType");
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_BUSY)
            {
                if (LockType == DistributedLockType.ReadLock)
                {
                    throw new DistributedReadLockTimeoutException(LockId);
                }

                throw new DistributedWriteLockTimeoutException(LockId);
            }

            _parent._logger.LogDebug("Acquired {lockType} for id {id}", LockType, LockId);
        }

        public int LockId { get; }

        public DistributedLockType LockType { get; }

        public void Dispose() =>
            // Mostly no op, cleaned up by completing transaction in scope.
            _parent._logger.LogDebug("Dropped {lockType} for id {id}", LockType, LockId);

        public override string ToString()
            => $"SqliteDistributedLock({LockId})";

        // Can always obtain a read lock (snapshot isolation in wal mode)
        // Mostly no-op just check that we didn't end up ReadUncommitted for real.
        private void ObtainReadLock()
        {
            // IUmbracoDatabase? db = _parent._efCoreScopeAccessor.Value.AmbientScope?.Database;
            var efCoreScope = _parent._efCoreScopeAccessor.Value.AmbientScope;

            if (efCoreScope is null)
            {
                throw new PanicException("No current ambient scope");
            }

            efCoreScope.ExecuteWithContextAsync<Task>(async database =>
            {
                if (database.Database.CurrentTransaction is null)
                {
                    throw new InvalidOperationException(
                        "SqliteDistributedLockingMechanism requires a transaction to function.");
                }
            });
        }

        // Only one writer is possible at a time
        // lock occurs for entire database as opposed to row/table.
        private void ObtainWriteLock()
        {
            IEfCoreScope<UmbracoEFContext>? efCoreScope = _parent._efCoreScopeAccessor.Value.AmbientScope;

            if (efCoreScope is null)
            {
                throw new PanicException("No ambient scope");
            }

            efCoreScope.ExecuteWithContextAsync<Task>(async database =>
            {
                if (database.Database.CurrentTransaction is null)
                {
                    throw new InvalidOperationException(
                        "SqliteDistributedLockingMechanism requires a transaction to function.");
                }

                var query = @$"UPDATE umbracoLock SET value = (CASE WHEN (value=1) THEN -1 ELSE 1 END) WHERE id = {LockId.ToString(CultureInfo.InvariantCulture)}";

                // DbCommand command = db.Database.CreateCommand(db.Database.GetDbConnection(), CommandType.Text, query);

                try
                {
                    var i = await database.Database.ExecuteScalarAsync<int>(query);

                    if (i == 0)
                    {
                        // ensure we are actually locking!
                        throw new ArgumentException($"LockObject with id={LockId} does not exist.");
                    }
                }
                catch (SqliteException ex) when (IsBusyOrLocked(ex))
                {
                    throw new DistributedWriteLockTimeoutException(LockId);
                }
            });
        }

        private bool IsBusyOrLocked(SqliteException ex) =>
            ex.SqliteErrorCode
                is raw.SQLITE_BUSY
                or raw.SQLITE_LOCKED
                or raw.SQLITE_LOCKED_SHAREDCACHE;
    }
}
