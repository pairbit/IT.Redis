﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace StackExchange.Redis.Tests
{
    [Collection(SharedConnectionFixture.Key)]
    public class PubSubMultiserver : TestBase
    {
        public PubSubMultiserver(ITestOutputHelper output, SharedConnectionFixture fixture) : base(output, fixture) { }
        protected override string GetConfiguration() => TestConfig.Current.ClusterServersAndPorts + ",connectTimeout=10000";

        [Fact]
        public void ChannelSharding()
        {
            using var muxer = Create(channelPrefix: Me()) as ConnectionMultiplexer;

            var defaultSlot = muxer.ServerSelectionStrategy.HashSlot(default(RedisChannel));
            var slot1 = muxer.ServerSelectionStrategy.HashSlot((RedisChannel)"hey");
            var slot2 = muxer.ServerSelectionStrategy.HashSlot((RedisChannel)"hey2");

            Assert.NotEqual(defaultSlot, slot1);
            Assert.NotEqual(ServerSelectionStrategy.NoSlot, slot1);
            Assert.NotEqual(slot1, slot2);
        }

        [Fact]
        public async Task ClusterNodeSubscriptionFailover()
        {
            Log("Connecting...");
            using var muxer = Create(allowAdmin: true) as ConnectionMultiplexer;
            var sub = muxer.GetSubscriber();
            var channel = (RedisChannel)Me();

            var count = 0;
            Log("Subscribing...");
            await sub.SubscribeAsync(channel, (_, val) =>
            {
                Interlocked.Increment(ref count);
                Log("Message: " + val);
            });
            Assert.True(sub.IsConnected(channel));

            Log("Publishing (1)...");
            Assert.Equal(0, count);
            var publishedTo = await sub.PublishAsync(channel, "message1");
            // Client -> Redis -> Client -> handler takes just a moment
            await UntilConditionAsync(TimeSpan.FromSeconds(2), () => Volatile.Read(ref count) == 1);
            Assert.Equal(1, count);
            Log($"  Published (1) to {publishedTo} subscriber(s).");
            Assert.Equal(1, publishedTo);

            var endpoint = sub.SubscribedEndpoint(channel);
            var subscribedServer = muxer.GetServer(endpoint);
            var subscribedServerEndpoint = muxer.GetServerEndPoint(endpoint);

            Assert.True(subscribedServer.IsConnected, "subscribedServer.IsConnected");
            Assert.True(subscribedServerEndpoint.IsConnected, "subscribedServerEndpoint.IsConnected");
            Assert.True(subscribedServerEndpoint.IsSubscriberConnected, "subscribedServerEndpoint.IsSubscriberConnected");

            Assert.True(muxer.TryGetSubscription(channel, out var subscription));
            var initialServer = subscription.GetCurrentServer();
            Assert.NotNull(initialServer);
            Assert.True(initialServer.IsConnected);
            Log("Connected to: " + initialServer);

            muxer.AllowConnect = false;
            subscribedServerEndpoint.SimulateConnectionFailure(SimulatedFailureType.AllSubscription);

            Assert.True(subscribedServerEndpoint.IsConnected, "subscribedServerEndpoint.IsConnected");
            Assert.False(subscribedServerEndpoint.IsSubscriberConnected, "subscribedServerEndpoint.IsSubscriberConnected");

            await UntilConditionAsync(TimeSpan.FromSeconds(5), () => subscription.IsConnected);
            Assert.True(subscription.IsConnected);

            var newServer = subscription.GetCurrentServer();
            Assert.NotNull(newServer);
            Assert.NotEqual(newServer, initialServer);
            Log("Now connected to: " + newServer);

            count = 0;
            Log("Publishing (2)...");
            Assert.Equal(0, count);
            publishedTo = await sub.PublishAsync(channel, "message2");
            // Client -> Redis -> Client -> handler takes just a moment
            await UntilConditionAsync(TimeSpan.FromSeconds(2), () => Volatile.Read(ref count) == 1);
            Assert.Equal(1, count);
            Log($"  Published (2) to {publishedTo} subscriber(s).");

            ClearAmbientFailures();
        }

        [Theory]
        [InlineData(CommandFlags.PreferMaster, true)]
        [InlineData(CommandFlags.PreferReplica, true)]
        [InlineData(CommandFlags.DemandMaster, false)]
        [InlineData(CommandFlags.DemandReplica, false)]
        public async Task PrimaryReplicaSubscriptionFailover(CommandFlags flags, bool expectSuccess)
        {
            var config = TestConfig.Current.PrimaryServerAndPort + "," + TestConfig.Current.ReplicaServerAndPort;
            Log("Connecting...");
            using var muxer = Create(configuration: config, shared: false, allowAdmin: true) as ConnectionMultiplexer;
            var sub = muxer.GetSubscriber();
            var channel = (RedisChannel)(Me() + flags.ToString()); // Individual channel per case to not overlap publishers

            var count = 0;
            Log("Subscribing...");
            await sub.SubscribeAsync(channel, (_, val) =>
            {
                Interlocked.Increment(ref count);
                Log("Message: " + val);
            }, flags);
            Assert.True(sub.IsConnected(channel));

            Log("Publishing (1)...");
            Assert.Equal(0, count);
            var publishedTo = await sub.PublishAsync(channel, "message1");
            // Client -> Redis -> Client -> handler takes just a moment
            await UntilConditionAsync(TimeSpan.FromSeconds(2), () => Volatile.Read(ref count) == 1);
            Assert.Equal(1, count);
            Log($"  Published (1) to {publishedTo} subscriber(s).");

            var endpoint = sub.SubscribedEndpoint(channel);
            var subscribedServer = muxer.GetServer(endpoint);
            var subscribedServerEndpoint = muxer.GetServerEndPoint(endpoint);

            Assert.True(subscribedServer.IsConnected, "subscribedServer.IsConnected");
            Assert.True(subscribedServerEndpoint.IsConnected, "subscribedServerEndpoint.IsConnected");
            Assert.True(subscribedServerEndpoint.IsSubscriberConnected, "subscribedServerEndpoint.IsSubscriberConnected");

            Assert.True(muxer.TryGetSubscription(channel, out var subscription));
            var initialServer = subscription.GetCurrentServer();
            Assert.NotNull(initialServer);
            Assert.True(initialServer.IsConnected);
            Log("Connected to: " + initialServer);

            muxer.AllowConnect = false;
            subscribedServerEndpoint.SimulateConnectionFailure(SimulatedFailureType.AllSubscription);

            Assert.True(subscribedServerEndpoint.IsConnected, "subscribedServerEndpoint.IsConnected");
            Assert.False(subscribedServerEndpoint.IsSubscriberConnected, "subscribedServerEndpoint.IsSubscriberConnected");

            if (expectSuccess)
            {
                await UntilConditionAsync(TimeSpan.FromSeconds(5), () => subscription.IsConnected);
                Assert.True(subscription.IsConnected);

                var newServer = subscription.GetCurrentServer();
                Assert.NotNull(newServer);
                Assert.NotEqual(newServer, initialServer);
                Log("Now connected to: " + newServer);
            }
            else
            {
                // This subscription shouldn't be able to reconnect by flags (demanding an unavailable server)
                await UntilConditionAsync(TimeSpan.FromSeconds(5), () => subscription.IsConnected);
                Assert.False(subscription.IsConnected);
                Log("Unable to reconnect (as expected)");

                // Allow connecting back to the original
                muxer.AllowConnect = true;
                await UntilConditionAsync(TimeSpan.FromSeconds(5), () => subscription.IsConnected);
                Assert.True(subscription.IsConnected);

                var newServer = subscription.GetCurrentServer();
                Assert.NotNull(newServer);
                Assert.Equal(newServer, initialServer);
                Log("Now connected to: " + newServer);
            }

            count = 0;
            Log("Publishing (2)...");
            Assert.Equal(0, count);
            publishedTo = await sub.PublishAsync(channel, "message2");
            // Client -> Redis -> Client -> handler takes just a moment
            await UntilConditionAsync(TimeSpan.FromSeconds(2), () => Volatile.Read(ref count) == 1);
            Assert.Equal(1, count);
            Log($"  Published (2) to {publishedTo} subscriber(s).");

            ClearAmbientFailures();
        }
    }
}
