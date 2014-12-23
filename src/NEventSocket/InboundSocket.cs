﻿namespace NEventSocket
{
    using System;
    using System.Net.Sockets;
    using System.Reactive.Linq;
    using System.Reactive.Threading.Tasks;
    using System.Security;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using NEventSocket.FreeSwitch;
    using NEventSocket.FreeSwitch.Applications;
    using NEventSocket.Logging;
    using NEventSocket.Sockets;
    using NEventSocket.Util;

    public class InboundSocket : EventSocket
    {
        private static readonly ILog Log = LogProvider.GetCurrentClassLogger();

        protected InboundSocket(string host, int port, TimeSpan? timeout = null)
            : base(new TcpClient(host, port), timeout)
        {
        }

        public async static Task<InboundSocket> Connect(string host = "localhost", int port = 8021, string password = "ClueCon", TimeSpan? timeout = null)
        {
            var socket = new InboundSocket(host, port, timeout);

            await socket.Messages
                .FirstAsync(x => x.ContentType == ContentTypes.AuthRequest)
                .Do(_ => Log.Trace(() => "Received Auth Request"))
                .Timeout(socket.TimeOut, Observable.Throw<BasicMessage>(new TimeoutException("No Auth Request received within the specified timeout of {0}.".Fmt(socket.TimeOut))))
                .Do(_ => { },
                    ex => Log.ErrorException("Error waiting for AuthRequest.", ex))
                .ToTask();

            var result = await socket.Auth(password);

            if (!result.Success)
            {
                Log.Error("InboundSocket authentication failed ({0}).".Fmt(result.ErrorMessage));
                throw new SecurityException("Invalid password");
            }

            Log.Trace(() => "InboundSocket authentication succeeded.");

            return socket;
        }

        public async Task<CommandReply> Auth(string password)
        {
            await SendAsync(Encoding.ASCII.GetBytes("auth {0}\n\n".Fmt(password)), CancellationToken.None);

            return await Messages
                            .FirstAsync(x => x.ContentType == ContentTypes.CommandReply)
                            .Timeout(TimeOut, Observable.Throw<BasicMessage>(new TimeoutException("No Auth Reply received within the specified timeout of {0}.".Fmt(TimeOut))))
                            .Do(_ => { },
                                ex => Log.ErrorException("Error waiting for Auth Reply.", ex))
                            .Select(x => new CommandReply(x))
                            .Do(result => Log.Trace(() => "CommandReply received [{0}] for auth response".Fmt(result.ReplyText)))
                            .ToTask();
        }

        public Task<OriginateResult> Originate(string endpoint, OriginateOptions options = null, string application = "park")
        {
            if (options == null) options = new OriginateOptions();

            // if no UUID provided, we'll set one now and use that to filter for the correct channel events
            // this way, one inbound socket can originate many calls and we can complete the correct
            // TaskCompletionSource for each originated call.
            if (string.IsNullOrEmpty(options.UUID)) options.UUID = Guid.NewGuid().ToString();

            var originateString = string.Format("{0}{1} &{2}", options, endpoint, application);

             return
                this.BackgroundJob("originate", originateString)
                    .ToObservable()
                    .Merge(
                        Events.FirstAsync(
                            x => x.UUID == options.UUID
                            && (x.EventName == EventName.ChannelAnswer || x.EventName == EventName.ChannelHangup
                                || (options.ReturnRingReady && x.EventName == EventName.ChannelProgress)))
                              .Cast<BasicMessage>())
                    .LastAsync(x => ((x is BackgroundJobResult) && !((BackgroundJobResult)x).Success) || (x is EventMessage))
                    .Select(x => new OriginateResult(x))
                    .ToTask();
        }

        public IDisposable On(string uuid, EventName eventName, Action<EventMessage> handler)
        {
            return this.Events.Where(x => x.UUID == uuid && x.EventName == eventName).Subscribe(handler);
        }
    }
}