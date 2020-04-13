using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DotNext.Net.Cluster.Consensus.Raft.Udp
{
    using IO;
    using IO.Log;
    using TransportServices;

    [ExcludeFromCodeCoverage]
    public sealed class UdpSocketTests : Test
    {
        private sealed class BufferedEntry : BinaryTransferObject, IRaftLogEntry
        {
            internal BufferedEntry(long term, DateTimeOffset timestamp, bool isSnapshot, byte[] content)
                : base(content)
            {
                Term = term;
                Timestamp = timestamp;
                IsSnapshot = isSnapshot;
            }

            public long Term { get; }


            public DateTimeOffset Timestamp { get; }

            public bool IsSnapshot { get; }

        }

        public enum ReceiveEntriesBehavior
        {
            ReceiveAll = 0,
            ReceiveFirst,
            DropAll,
            DropFirst
        }

        private sealed class SimpleServerExchangePool : Assert, ILocalMember, IExchangePool
        {
            internal readonly IList<BufferedEntry> ReceivedEntries = new List<BufferedEntry>();
            internal ReceiveEntriesBehavior Behavior;

            internal SimpleServerExchangePool(bool smallAmountOfMetadata = false)
            {
                var metadata = ImmutableDictionary.CreateBuilder<string, string>();
                if(smallAmountOfMetadata)
                    metadata.Add("a", "b");
                else
                {
                    var rnd = new Random();
                    const string AllowedChars = "abcdefghijklmnopqrstuvwxyz1234567890";
                    for(var i = 0; i < 20; i++)
                        metadata.Add(string.Concat("key", i.ToString()), rnd.NextString(AllowedChars, 20));
                }
                Metadata = metadata.ToImmutableDictionary();
            }

            IPEndPoint ILocalMember.Address => throw new NotImplementedException();

            bool ILocalMember.IsLeader(IRaftClusterMember member) => throw new NotImplementedException();

            Task<bool> ILocalMember.ResignAsync(CancellationToken token) => Task.FromResult(true);

            async Task<Result<bool>> ILocalMember.ReceiveEntriesAsync<TEntry>(EndPoint sender, long senderTerm, ILogEntryProducer<TEntry> entries, long prevLogIndex, long prevLogTerm, long commitIndex, CancellationToken token)
            {
                Equal(42L, senderTerm);
                Equal(1, prevLogIndex);
                Equal(56L, prevLogTerm);
                Equal(10, commitIndex);
                byte[] buffer;
                switch(Behavior)
                {
                    case ReceiveEntriesBehavior.ReceiveAll:
                        while(await entries.MoveNextAsync())
                        {
                            True(entries.Current.Length.HasValue);
                            buffer = await entries.Current.ToByteArrayAsync(token);
                            ReceivedEntries.Add(new BufferedEntry(entries.Current.Term, entries.Current.Timestamp, entries.Current.IsSnapshot, buffer));
                        }
                        break;
                    case ReceiveEntriesBehavior.DropAll:
                        break;
                    case ReceiveEntriesBehavior.ReceiveFirst:
                        True(await entries.MoveNextAsync());
                        buffer = await entries.Current.ToByteArrayAsync(token);
                        ReceivedEntries.Add(new BufferedEntry(entries.Current.Term, entries.Current.Timestamp, entries.Current.IsSnapshot, buffer));
                        break;
                    case ReceiveEntriesBehavior.DropFirst:
                        True(await entries.MoveNextAsync());
                        True(await entries.MoveNextAsync());
                        buffer = await entries.Current.ToByteArrayAsync(token);
                        ReceivedEntries.Add(new BufferedEntry(entries.Current.Term, entries.Current.Timestamp, entries.Current.IsSnapshot, buffer));
                        break;
                }
                
                return new Result<bool>(43L, true);
            }

            async Task<Result<bool>> ILocalMember.ReceiveSnapshotAsync<TSnapshot>(EndPoint sender, long senderTerm, TSnapshot snapshot, long snapshotIndex, CancellationToken token)
            {
                Equal(42L, senderTerm);
                Equal(10, snapshotIndex);
                True(snapshot.IsSnapshot);
                var buffer = await snapshot.ToByteArrayAsync(token);
                ReceivedEntries.Add(new BufferedEntry(snapshot.Term, snapshot.Timestamp, snapshot.IsSnapshot, buffer));
                return new Result<bool>(43L, true);
            }

            Task<Result<bool>> ILocalMember.ReceiveVoteAsync(EndPoint sender, long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
            {
                True(token.CanBeCanceled);
                Equal(42L, term);
                Equal(1L, lastLogIndex);
                Equal(56L, lastLogTerm);
                return Task.FromResult(new Result<bool>(43L, true));
            }

            public bool TryRent(PacketHeaders headers, out IExchange exchange)
            {
                exchange = new ServerExchange(this);
                return true;
            }

            public IReadOnlyDictionary<string, string> Metadata { get; }

            void IExchangePool.Release(IExchange exchange)
                => ((ServerExchange)exchange).Reset();
        }

        [Fact]
        public static async Task ConnectionError()
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 35665), 2, ArrayPool<byte>.Shared, NullLoggerFactory.Instance) 
            { 
                DatagramSize = UdpSocket.MaxDatagramSize,
                DontFragment = false
            };
            using var timeoutTokenSource = new CancellationTokenSource(500);
            client.Start();
            var exchange = new VoteExchange(10L, 20L, 30L);
            client.Enqueue(exchange, timeoutTokenSource.Token);
            switch(Environment.OSVersion.Platform)
            {
                case PlatformID.Unix:
                    await ThrowsAsync<SocketException>(() => exchange.Task);
                    break;
                case PlatformID.Win32NT:
                    await ThrowsAsync<TaskCanceledException>(() => exchange.Task);
                    break;
            }
        }

        [Fact]
        public static async Task RequestResponse()
        {
            var timeout = TimeSpan.FromSeconds(20);
            //prepare server
            var serverAddr = new IPEndPoint(IPAddress.Loopback, 3789);
            using var server = new UdpServer(serverAddr, 2, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                ReceiveTimeout = timeout,
                DatagramSize = UdpSocket.MaxDatagramSize,
                DontFragment = false
            };
            server.Start(new SimpleServerExchangePool());
            //prepare client
            using var client = new UdpClient(serverAddr, 2, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                DatagramSize = UdpSocket.MaxDatagramSize,
                DontFragment = false
            };
            client.Start();
            //Vote request
            CancellationTokenSource timeoutTokenSource;
            Result<bool> result;
            using(timeoutTokenSource = new CancellationTokenSource(timeout))
            {
                var exchange = new VoteExchange(42L, 1L, 56L);
                client.Enqueue(exchange, timeoutTokenSource.Token);
                result = await exchange.Task;
                True(result.Value);
                Equal(43L, result.Term);
            }
            //Resign request
            using(timeoutTokenSource = new CancellationTokenSource(timeout))
            {
                var exchange = new ResignExchange();
                client.Enqueue(exchange, timeoutTokenSource.Token);
                True(await exchange.Task);
            }
            //Heartbeat request
            using(timeoutTokenSource = new CancellationTokenSource(timeout))
            {
                var exchange = new HeartbeatExchange(42L, 1L, 56L, 10L);
                client.Enqueue(exchange, timeoutTokenSource.Token);
                result = await exchange.Task;
                True(result.Value);
                Equal(43L, result.Term);
            }
            client.Shutdown(SocketShutdown.Both);
        }

        [Fact]
        public static async Task StressTest()
        {
            var timeout = TimeSpan.FromSeconds(20);
            //prepare server
            var serverAddr = new IPEndPoint(IPAddress.Loopback, 3789);
            using var server = new UdpServer(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                ReceiveTimeout = timeout,
                DatagramSize =  UdpSocket.MaxDatagramSize,
                DontFragment = false
            };
            server.Start(new SimpleServerExchangePool());
            //prepare client
            using var client = new UdpClient(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                DatagramSize = UdpSocket.MaxDatagramSize,
                DontFragment = false
            };
            client.Start();
            ICollection<Task<Result<bool>>> tasks = new LinkedList<Task<Result<bool>>>();
            using(var timeoutTokenSource = new CancellationTokenSource(timeout))
            {
                for(var i = 0; i < 100; i++)
                {
                    var exchange = new VoteExchange(42L, 1L, 56L);
                    client.Enqueue(exchange, timeoutTokenSource.Token);
                    tasks.Add(exchange.Task);
                }
                await Task.WhenAll(tasks);
            }
            foreach(var task in tasks)
            {
                True(task.Result.Value);
                Equal(43L, task.Result.Term);
            }
            client.Shutdown(SocketShutdown.Both);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public static async Task MetadataRequestResponse(bool smallAmountOfMetadata)
        {
            var timeout = TimeSpan.FromSeconds(20);
            //prepare server
            var serverAddr = new IPEndPoint(IPAddress.Loopback, 3789);
            using var server = new UdpServer(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                ReceiveTimeout = timeout,
                DatagramSize = UdpSocket.MinDatagramSize,
                DontFragment = true
            };
            var exchangePool = new SimpleServerExchangePool(smallAmountOfMetadata);
            server.Start(exchangePool);
            //prepare client
            using var client = new UdpClient(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                DatagramSize = UdpSocket.MinDatagramSize,
                DontFragment = true
            };
            client.Start();
            var exchange = new MetadataExchange(CancellationToken.None);
            client.Enqueue(exchange, default);
            Equal(exchangePool.Metadata, await exchange.Task);
            client.Shutdown(SocketShutdown.Both);
        }

        private static void Equal(in BufferedEntry x, in BufferedEntry y)
        {
            Equal(x.Term, y.Term);
            Equal(x.Timestamp, y.Timestamp);
            Equal(x.IsSnapshot, y.IsSnapshot);
            True(x.Content.IsSingleSegment);
            True(y.Content.IsSingleSegment);
            True(x.Content.FirstSpan.SequenceEqual(y.Content.FirstSpan));
        }

        [Theory]
        [InlineData(512, ReceiveEntriesBehavior.ReceiveAll)]
        [InlineData(512, ReceiveEntriesBehavior.ReceiveFirst)]
        [InlineData(512, ReceiveEntriesBehavior.DropAll)]
        [InlineData(512, ReceiveEntriesBehavior.DropFirst)]
        [InlineData(50, ReceiveEntriesBehavior.ReceiveAll)]
        [InlineData(50, ReceiveEntriesBehavior.ReceiveFirst)]
        [InlineData(50, ReceiveEntriesBehavior.DropAll)]
        [InlineData(50, ReceiveEntriesBehavior.DropFirst)]
        public static async Task SendingLogEntries(int payloadSize, ReceiveEntriesBehavior behavior)
        {
            var timeout = TimeSpan.FromSeconds(20);
            using var timeoutTokenSource = new CancellationTokenSource(timeout);
            //prepare server
            var serverAddr = new IPEndPoint(IPAddress.Loopback, 3789);
            using var server = new UdpServer(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                DatagramSize = UdpSocket.MinDatagramSize,
                ReceiveTimeout = timeout,
                DontFragment = true
            };
            var exchangePool = new SimpleServerExchangePool(false) { Behavior = behavior };
            server.Start(exchangePool);
            //prepare client
            using var client = new UdpClient(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                DatagramSize = UdpSocket.MinDatagramSize,
                DontFragment = true
            };
            client.Start();
            var buffer = new byte[533];
            var rnd = new Random();
            rnd.NextBytes(buffer);
            var entry1 = new BufferedEntry(10L, DateTimeOffset.Now, false, buffer);
            buffer = new byte[payloadSize];
            rnd.NextBytes(buffer);
            var entry2 = new BufferedEntry(11L, DateTimeOffset.Now, true, buffer);

            await using var exchange = new EntriesExchange<BufferedEntry, BufferedEntry[]>(42L, new[]{ entry1, entry2 }, 1, 56, 10);
            client.Enqueue(exchange, timeoutTokenSource.Token);
            var result = await exchange.Task;
            Equal(43L, result.Term);
            True(result.Value);
            switch(behavior)
            {
                case ReceiveEntriesBehavior.ReceiveAll:
                    Equal(2, exchangePool.ReceivedEntries.Count);
                    Equal(entry1, exchangePool.ReceivedEntries[0]);
                    Equal(entry2, exchangePool.ReceivedEntries[1]);
                    break;
                case ReceiveEntriesBehavior.ReceiveFirst:
                    Equal(1, exchangePool.ReceivedEntries.Count);
                    Equal(entry1, exchangePool.ReceivedEntries[0]);
                    break;
                case ReceiveEntriesBehavior.DropFirst:
                    Equal(1, exchangePool.ReceivedEntries.Count);
                    Equal(entry2, exchangePool.ReceivedEntries[0]);
                    break;
                case ReceiveEntriesBehavior.DropAll:
                    Empty(exchangePool.ReceivedEntries);
                    break;
            }
        }

        [Theory]
        [InlineData(512)]
        [InlineData(50)]
        public static async Task SendingSnapshot(int payloadSize)
        {
            var timeout = TimeSpan.FromSeconds(20);
            using var timeoutTokenSource = new CancellationTokenSource(timeout);
            //prepare server
            var serverAddr = new IPEndPoint(IPAddress.Loopback, 3789);
            using var server = new UdpServer(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                DatagramSize =  UdpSocket.MinDatagramSize,
                ReceiveTimeout = timeout,
                DontFragment = true
            };
            var exchangePool = new SimpleServerExchangePool(false);
            server.Start(exchangePool);
            //prepare client
            using var client = new UdpClient(serverAddr, 100, ArrayPool<byte>.Shared, NullLoggerFactory.Instance)
            {
                DatagramSize = UdpSocket.MinDatagramSize,
                DontFragment = true
            };
            client.Start();
            var buffer = new byte[payloadSize];
            new Random().NextBytes(buffer);
            var snapshot = new BufferedEntry(10L, DateTimeOffset.Now, true, buffer);
            await using var exchange = new SnapshotExchange(42L, snapshot, 10L);
            client.Enqueue(exchange, timeoutTokenSource.Token);
            var result = await exchange.Task;
            Equal(43L, result.Term);
            True(result.Value);
            NotEmpty(exchangePool.ReceivedEntries);
            Equal(snapshot, exchangePool.ReceivedEntries[0]);
        }
    }
}