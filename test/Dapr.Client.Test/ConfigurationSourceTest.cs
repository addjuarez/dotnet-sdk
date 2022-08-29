﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Moq;
using Xunit;
using Autogenerated = Dapr.Client.Autogen.Grpc.v1;

namespace Dapr.Client.Test
{
    [Obsolete]
    public class ConfigurationSourceTest
    {
        private readonly string StoreName = "testStore";
        private readonly string SubscribeId = "testSubscribe";

        [Fact]
        public async Task TestStreamingConfigurationSourceCanBeRead()
        {
            // Standard values that we don't need to Mock.
            using var cts = new CancellationTokenSource();
            var streamRequest = new Autogenerated.SubscribeConfigurationRequest()
            {
                StoreName = StoreName
            };
            var callOptions = new CallOptions(cancellationToken: cts.Token);
            var item1 = new Autogenerated.ConfigurationItem()
            {
                Value = "testValue1",
                Version = "V1",
            };
            var item2 = new Autogenerated.ConfigurationItem()
            {
                Value = "testValue2",
                Version = "V1",
            };
            var responses = new List<Autogenerated.SubscribeConfigurationResponse>()
            {
                new Autogenerated.SubscribeConfigurationResponse() { Id = SubscribeId },
                new Autogenerated.SubscribeConfigurationResponse() { Id = SubscribeId },
            };
            responses[0].Items.Add("testKey1", item1);
            responses[1].Items.Add("testKey1", item2);

            // Setup the Mock and actions.
            var internalClient = Mock.Of<Autogenerated.Dapr.DaprClient>();
            var responseStream = new TestAsyncStreamReader<Autogenerated.SubscribeConfigurationResponse>(responses, TimeSpan.FromMilliseconds(100));
            var response = new AsyncServerStreamingCall<Autogenerated.SubscribeConfigurationResponse>(responseStream, null, null, null, async () => await Task.Delay(TimeSpan.FromMilliseconds(1)));
            Mock.Get(internalClient).Setup(client => client.SubscribeConfigurationAlpha1(streamRequest, callOptions))
                .Returns(response);

            // Try and actually use the source.
            var source = new SubscribeConfigurationResponse(new DaprSubscribeConfigurationSource(response));
            var readItems = new List<ConfigurationItem>();
            await foreach (var items in source.Source)
            {
                foreach (var item in items)
                {
                    readItems.Add(new ConfigurationItem(item.Key, item.Value, item.Version, item.Metadata));
                }
            }

            var expectedItems = new List<ConfigurationItem>()
            {
                new("testKey1", "testValue1", "V1", null),
                new("testKey2", "testValue2", "V1", null)
            };
            Assert.Equal(SubscribeId, source.Id);
            Assert.Equal(expectedItems.Count, readItems.Count);
            // The gRPC metadata stops us from just doing the direct list comparison.
            for (int i = 0; i < expectedItems.Count; i++)
            {
                Assert.Equal(expectedItems[i].Key, readItems[i].Key);
                Assert.Equal(expectedItems[i].Value, readItems[i].Value);
                Assert.Equal(expectedItems[i].Version, readItems[i].Version);
                Assert.Empty(readItems[i].Metadata);
            }
        }

        private class TestAsyncStreamReader<T> : IAsyncStreamReader<T>
        {
            private IEnumerator<T> enumerator;
            private TimeSpan simulatedWaitTime;

            public TestAsyncStreamReader(IList<T> items, TimeSpan simulatedWaitTime)
            {
                this.enumerator = items.GetEnumerator();
                this.simulatedWaitTime = simulatedWaitTime;
            }

            public T Current => enumerator.Current;

            public async Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                // Add a little delay to pretend we're getting responses from a server stream.
                await Task.Delay(simulatedWaitTime, cancellationToken);
                return enumerator.MoveNext();
            }
        }
    }
}
