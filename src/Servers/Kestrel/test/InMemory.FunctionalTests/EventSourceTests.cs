// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.InMemory.FunctionalTests.TestTransport;
using Microsoft.AspNetCore.Testing;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.InMemory.FunctionalTests
{
    public class EventSourceTests : LoggedTest
    {
        private readonly TestEventListener _listener = new TestEventListener();

        public EventSourceTests()
        {
            _listener.EnableEvents(KestrelEventSource.Log, EventLevel.Verbose);
        }

        [Fact]
        public async Task EmitsStartAndStopEventsWithActivityIds()
        {
            string connectionId = null;
            string requestId = null;
            int port;

            await using (var server = new TestServer(context =>
            {
                connectionId = context.Features.Get<IHttpConnectionFeature>().ConnectionId;
                requestId = context.TraceIdentifier;
                return Task.CompletedTask;
            },
            new TestServiceContext(LoggerFactory),
            listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            }))
            {
                port = server.Port;

                var connectionCount = 0;
                using var connection = server.CreateConnection();

                using var socketsHandler = new SocketsHttpHandler()
                {
                    ConnectCallback = (_, _) =>
                    {
                        if (connectionCount != 0)
                        {
                            throw new InvalidOperationException();
                        }

                        connectionCount++;
                        return new ValueTask<Stream>(connection.Stream);
                    },
                };

                using var httpClient = new HttpClient(socketsHandler);

                using var httpRequsetMessage = new HttpRequestMessage()
                {
                    RequestUri = new Uri("http://localhost/"),
                    Version = new Version(2, 0),
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };

                using var responseMessage = await httpClient.SendAsync(httpRequsetMessage);

                responseMessage.EnsureSuccessStatusCode();
            }

            // capture list here as other tests executing in parallel may log events
            Assert.NotNull(connectionId);
            Assert.NotNull(requestId);

            Guid connectionQueuedStartActivityId = Guid.Empty;

            var events = _listener.EventData.Where(e => e != null && e.Payload.GetValueOrDefault("connectionId") == connectionId).ToList();

            {
                var connectionQueuedStart = Assert.Single(events, e => e.EventName == "ConnectionQueuedStart");
                Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStart.Payload.Keys));
                Assert.Equal($"127.0.0.1:{port}", connectionQueuedStart.Payload["localEndPoint"]);

                Assert.NotEqual(Guid.Empty, connectionQueuedStart.ActivityId);
                Assert.Equal(Guid.Empty, connectionQueuedStart.RelatedActivityId);

                //connectionQueuedStartActivityId = connectionQueuedStart.ActivityId;
            }
            {
                var connectionQueuedStop = Assert.Single(events, e => e.EventName == "ConnectionQueuedStop");
                Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionQueuedStop.Payload.Keys));
                Assert.Equal($"127.0.0.1:{port}", connectionQueuedStop.Payload["localEndPoint"]);

                //Assert.Equal(connectionQueuedStartActivityId, connectionQueuedStop.ActivityId);
                //Assert.Equal(Guid.Empty, connectionQueuedStop.RelatedActivityId);
            }
            {
                var connectionStart = Assert.Single(events, e => e.EventName == "ConnectionStart");
                Assert.All(new[] { "connectionId", "remoteEndPoint", "localEndPoint" }, p => Assert.Contains(p, connectionStart.Payload.Keys));
                Assert.Equal($"127.0.0.1:{port}", connectionStart.Payload["localEndPoint"]);
            }
            {
                var connectionStop = Assert.Single(events, e => e.EventName == "ConnectionStop");
                Assert.All(new[] { "connectionId" }, p => Assert.Contains(p, connectionStop.Payload.Keys));
                Assert.Same(KestrelEventSource.Log, connectionStop.EventSource);
            }
            {
                var requestStart = Assert.Single(events, e => e.EventName == "RequestStart");
                Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestStart.Payload.Keys));
                Assert.Equal(requestId, requestStart.Payload["requestId"]);
                Assert.Same(KestrelEventSource.Log, requestStart.EventSource);
            }
            {
                var requestStop = Assert.Single(events, e => e.EventName == "RequestStop");
                Assert.All(new[] { "connectionId", "requestId" }, p => Assert.Contains(p, requestStop.Payload.Keys));
                Assert.Equal(requestId, requestStop.Payload["requestId"]);
                Assert.Same(KestrelEventSource.Log, requestStop.EventSource);
            }
        }

        private class TestEventListener : EventListener
        {
            private volatile bool _disposed;
            private ConcurrentQueue<EventSnapshot> _events = new ConcurrentQueue<EventSnapshot>();

            public IEnumerable<EventSnapshot> EventData => _events;

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (!_disposed)
                {
                    _events.Enqueue(new EventSnapshot(eventData));
                }
            }

            public override void Dispose()
            {
                _disposed = true;
                base.Dispose();
            }
        }

        private class EventSnapshot
        {
            public EventSnapshot(EventWrittenEventArgs eventWrittenEventArgs)
            {
                EventName = eventWrittenEventArgs.EventName;
                EventSource = eventWrittenEventArgs.EventSource;
                ActivityId = eventWrittenEventArgs.ActivityId;
                RelatedActivityId = eventWrittenEventArgs.RelatedActivityId;

                for (int i = 0; i < eventWrittenEventArgs.PayloadNames.Count; i++)
                {
                    Payload[eventWrittenEventArgs.PayloadNames[i]] = eventWrittenEventArgs.Payload[i] as string;
                }
            }

            public string EventName { get; }
            public EventSource EventSource { get; }
            public Guid ActivityId { get; }
            public Guid RelatedActivityId { get; }
            public Dictionary<string, string> Payload { get; } = new Dictionary<string, string>();
        }

        public override void Dispose()
        {
            _listener.Dispose();
            base.Dispose();
        }
    }
}
