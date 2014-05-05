﻿using System.Reactive;
using System.Reactive.Linq;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading.Tasks;

namespace System.IoFx.ServiceModel
{
    public static class ChannelExtensions
    {
        public static IObservable<TChannel> GetChannels<TChannel>(this IChannelListener<TChannel> listener) where TChannel : class, IChannel
        {
            //TODO: Need to throttle like max connections etc. 
            return new Acceptor<TChannel>(listener);
        }

        public static IObservable<Message> GetMessages<TChannel>(this TChannel channel) where TChannel : class, IInputChannel
        {
            return new Receiver<TChannel>(channel);
        }

        internal class Acceptor<TChannel> : IDisposable, IObservable<TChannel> where TChannel : class, IChannel
        {
            private readonly IChannelListener<TChannel> _listener;
            private readonly IObservable<TChannel> _observable;

            public Acceptor(IChannelListener<TChannel> listener)
            {
                _listener = listener;
                Func<IObserver<TChannel>, Task<IDisposable>> loop = AcceptLoop;
                _observable = Observable.Create(loop);
            }

            private async Task<IDisposable> AcceptLoop(IObserver<TChannel> channelObserver)
            {
                bool canContinue = true;
                Exception completedException = null;
                Func<Task<TChannel>> acceptFunc = () => Task.Factory.FromAsync<TChannel>(
                                                            _listener.BeginAcceptChannel,
                                                            _listener.EndAcceptChannel, null);

                while (canContinue)
                {
                    try
                    {
                        TChannel channel = await acceptFunc();
                        if (channel == null)
                        {
                            channelObserver.OnCompleted();
                            break;
                        }

                        channelObserver.OnNext(channel);

                    }
                    catch (Exception ex)
                    {
                        if (_listener.State == CommunicationState.Faulted)
                        {
                            channelObserver.OnError(ex);
                        }
                        else
                        {
                            channelObserver.OnCompleted();
                        }

                        canContinue = _listener.State == CommunicationState.Opened;
                    }
                }

                return this;
            }


            public void Dispose()
            {
                _listener.Abort();
            }

            public IDisposable Subscribe(IObserver<TChannel> observer)
            {
                return _observable.Subscribe(observer);
            }
        }

        internal class Receiver<TChannel> : IDisposable, IObservable<Message> where TChannel : class, IInputChannel
        {
            private readonly TChannel _channel;
            private readonly IObservable<Message> _observable;

            public Receiver(TChannel channel)
            {
                if (channel == null)
                {
                    throw new ArgumentNullException("channel");
                }

                _channel = channel;
                Func<IObserver<Message>, Task<IDisposable>> loop = ReceiveLoop;
                _observable = Observable.Create(loop);
            }

            async Task<IDisposable> ReceiveLoop(IObserver<Message> channelObserver)
            {

                try
                {
                    Func<Task<Message>> receiveAsyncFunc = () => Task.Factory.FromAsync<Message>(
                                            _channel.BeginReceive,
                                            _channel.EndReceive, null);

                    Func<Task> openAsyncFunc = () => Task.Factory.FromAsync(
                                                _channel.BeginOpen,
                                                _channel.EndOpen, null);

                    await openAsyncFunc();

                    while (true)
                    {
                        Message message = await receiveAsyncFunc();

                        if (message == null)
                        {
                            _channel.Close();
                            break;
                        }

                        channelObserver.OnNext(message);
                    }

                    channelObserver.OnCompleted();
                }
                catch (Exception ex)
                {                    
                    channelObserver.OnError(ex);
                }

                return this;
            }

            public void Dispose()
            {
                _channel.Abort();
            }

            public IDisposable Subscribe(IObserver<Message> observer)
            {
                return _observable.Subscribe(observer);
            }
        }

        public static IObserver<Message> ReplyOn<TChannel>(
            this TChannel channel) where TChannel : IOutputChannel
        {
            return Observer.Create<Message>(channel.Send);
        }

        public static IObservable<IoPipeline<Message>> OnConnect<TChannel>(
            this IChannelListener<TChannel> listener)
            where TChannel : class, IOutputChannel, IInputChannel
        {

            return listener
                .GetChannels()
                .Select(channel =>
                {
                    var inputs = channel.GetMessages();
                    var outputs = channel.ReplyOn();
                    return channel.CreateIoChannel(inputs, outputs);
                });
        }

        public static void OnConnect<TChannel>(
            this IChannelListener<TChannel> listener,
            Action<IoPipeline<Message>> onNext)
            where TChannel : class, IOutputChannel, IInputChannel
        {
            listener.OnConnect().Subscribe(onNext);
        }

        public static IObservable<IoUnit<Message>> OnMessage(this IObservable<IoPipeline<Message>> channels)
        {
            return from c in channels
                   from m in c
                   select new IoUnit<Message>
                   {
                       Unit = m,
                       Parent = c,
                   };
        }

        public static IObservable<IoUnit<Message>> OnMessage(
            this IChannelListener<IDuplexSessionChannel> listener)
        {
            return listener.OnConnect().OnMessage();
        }

        private static IoChannel<T, TChannel> CreateIoChannel<T, TChannel>(
            this TChannel channel,
            IObservable<T> inputs,
            IObserver<T> outputs)
            where TChannel : class, IChannel
        {
            return new IoChannel<T, TChannel>(inputs, outputs, channel);
        }

        class IoChannel<T, TChannel> : IoPipeline<T> where TChannel : class, IChannel
        {
            public IoChannel(IObservable<T> inputs, IObserver<T> outputs, TChannel channel)
                : base(inputs, outputs)
            {
                this.Channel = channel;
            }

            public TChannel Channel { get; set; }
        }
    }
}