// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Storage.RocksDb
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using App.Metrics;
    using App.Metrics.Timer;
    using Microsoft.Azure.Devices.Edge.Util;
    using RocksDbSharp;

    class ColumnFamilyDbStore : IDbStore
    {
        readonly IRocksDb db;
        private long count;

        public ColumnFamilyDbStore(IRocksDb db, ColumnFamilyHandle handle)
        {
            this.db = Preconditions.CheckNotNull(db, nameof(db));
            this.Handle = Preconditions.CheckNotNull(handle, nameof(handle));

            var iterator = db.NewIterator(this.Handle);
            iterator.SeekToFirst();
            this.count = 0;
            while (iterator.Valid())
            {
                this.count += 1;
                iterator = iterator.Next();
            }
        }

        internal ColumnFamilyHandle Handle { get; }

        public Task Put(byte[] key, byte[] value) => this.Put(key, value, CancellationToken.None);

        public Task<Option<byte[]>> Get(byte[] key) => this.Get(key, CancellationToken.None);

        public Task Remove(byte[] key) => this.Remove(key, CancellationToken.None);

        public Task<bool> Contains(byte[] key) => this.Contains(key, CancellationToken.None);

        public Task<Option<(byte[] key, byte[] value)>> GetFirstEntry() => this.GetFirstEntry(CancellationToken.None);

        public Task<Option<(byte[] key, byte[] value)>> GetLastEntry() => this.GetLastEntry(CancellationToken.None);

        public Task IterateBatch(int batchSize, Func<byte[], byte[], Task> perEntityCallback) => this.IterateBatch(batchSize, perEntityCallback, CancellationToken.None);

        public Task IterateBatch(byte[] startKey, int batchSize, Func<byte[], byte[], Task> perEntityCallback) => this.IterateBatch(startKey, batchSize, perEntityCallback, CancellationToken.None);

        public async Task<Option<byte[]>> Get(byte[] key, CancellationToken cancellationToken)
        {
            Preconditions.CheckNotNull(key, nameof(key));

            Option<byte[]> returnValue;
            Func<byte[]> operation = () => this.db.Get(key, this.Handle);
            byte[] value = await operation.ExecuteUntilCancelled(cancellationToken);
            returnValue = value != null ? Option.Some(value) : Option.None<byte[]>();

            return returnValue;
        }

        public async Task Put(byte[] key, byte[] value, CancellationToken cancellationToken)
        {
            Preconditions.CheckNotNull(key, nameof(key));
            Preconditions.CheckNotNull(value, nameof(value));

            Action operation = () => this.db.Put(key, value, this.Handle);
            await operation.ExecuteUntilCancelled(cancellationToken);
            Interlocked.Increment(ref this.count);
        }

        public async Task Remove(byte[] key, CancellationToken cancellationToken)
        {
            Preconditions.CheckNotNull(key, nameof(key));

            Action operation = () => this.db.Remove(key, this.Handle);
            await operation.ExecuteUntilCancelled(cancellationToken);
            Interlocked.Decrement(ref this.count);
        }

        public async Task<Option<(byte[] key, byte[] value)>> GetLastEntry(CancellationToken cancellationToken)
        {
            using (Iterator iterator = this.db.NewIterator(this.Handle))
            {
                Action operation = () => iterator.SeekToLast();
                await operation.ExecuteUntilCancelled(cancellationToken);
                if (iterator.Valid())
                {
                    byte[] key = iterator.Key();
                    byte[] value = iterator.Value();
                    return Option.Some((key, value));
                }
                else
                {
                    return Option.None<(byte[], byte[])>();
                }
            }
        }

        public async Task<Option<(byte[] key, byte[] value)>> GetFirstEntry(CancellationToken cancellationToken)
        {
            using (Iterator iterator = this.db.NewIterator(this.Handle))
            {
                Action operation = () => iterator.SeekToFirst();
                await operation.ExecuteUntilCancelled(cancellationToken);
                if (iterator.Valid())
                {
                    byte[] key = iterator.Key();
                    byte[] value = iterator.Value();
                    return Option.Some((key, value));
                }
                else
                {
                    return Option.None<(byte[], byte[])>();
                }
            }
        }

        public async Task<bool> Contains(byte[] key, CancellationToken cancellationToken)
        {
            Preconditions.CheckNotNull(key, nameof(key));
            Func<byte[]> operation = () => this.db.Get(key, this.Handle);
            byte[] value = await operation.ExecuteUntilCancelled(cancellationToken);
            return value != null;
        }

        public Task IterateBatch(byte[] startKey, int batchSize, Func<byte[], byte[], Task> callback, CancellationToken cancellationToken)
        {
            Preconditions.CheckNotNull(startKey, nameof(startKey));
            Preconditions.CheckRange(batchSize, 1, nameof(batchSize));
            Preconditions.CheckNotNull(callback, nameof(callback));

            return this.IterateBatch(iterator => iterator.Seek(startKey), batchSize, callback, cancellationToken);
        }

        public Task IterateBatch(int batchSize, Func<byte[], byte[], Task> callback, CancellationToken cancellationToken)
        {
            Preconditions.CheckRange(batchSize, 1, nameof(batchSize));
            Preconditions.CheckNotNull(callback, nameof(callback));

            return this.IterateBatch(iterator => iterator.SeekToFirst(), batchSize, callback, cancellationToken);
        }

        public Task<ulong> Count() => Task.FromResult((ulong)Interlocked.Read(ref this.count));

        public Task<ulong> GetCountFromOffset(byte[] offset)
        {
            var iterator = this.db.NewIterator(this.Handle);
            iterator.Seek(offset);

            ulong count = 0;
            while (iterator.Valid())
            {
                count += 1;
                iterator = iterator.Next();
            }

            return Task.FromResult(count);
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Don't dispose the Db here as we don't know if the caller
                // meant to dispose just the ColumnFamilyDbStore or the DB.
                // this.db?.Dispose();
            }
        }

        async Task IterateBatch(Action<Iterator> seeker, int batchSize, Func<byte[], byte[], Task> callback, CancellationToken cancellationToken)
        {
            // Use tailing iterator to prevent creating a snapshot.
            var readOptions = new ReadOptions();
            readOptions.SetTailing(true);

            using (Iterator iterator = this.db.NewIterator(this.Handle, readOptions))
            {
                int counter = 0;
                for (seeker(iterator); iterator.Valid() && counter < batchSize; iterator.Next(), counter++)
                {
                    byte[] key = iterator.Key();
                    byte[] value = iterator.Value();
                    await callback(key, value);
                }
            }
        }
    }
}
