﻿using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Replication;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RaftNode
{
    internal sealed class DataModifier : BackgroundService
    {
        private readonly IRaftCluster cluster;
        private readonly IValueProvider valueProvider;

        public DataModifier(IRaftCluster cluster, IValueProvider provider)
        {
            this.cluster = cluster;
            valueProvider = provider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                if (!(cluster.Leader?.IsRemote ?? true))
                {
                    var newValue = await valueProvider.GetValueAsync().ConfigureAwait(false) + 500L;
                    Console.WriteLine("Saving value {0} generated by the leader node", newValue);
                    try
                    {
                        await cluster.WriteAsync(new[] { new Int64LogEntry(newValue) { Term = cluster.Term } }, WriteConcern.LeaderOnly, TimeSpan.FromSeconds(2));
                    }
                    catch (TimeoutException e)
                    {
                        Console.WriteLine("Unable to write value {0} because timeout is occurred: {1}", newValue, e);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Unexpected error {0}", e);
                    }
                }

            }
        }
    }
}
