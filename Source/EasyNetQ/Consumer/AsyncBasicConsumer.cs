using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyNetQ.Events;
using EasyNetQ.Internals;
using EasyNetQ.Logging;
using EasyNetQ.Topology;
using Microsoft.Extensions.ObjectPool;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace EasyNetQ.Consumer;

internal class AsyncBasicConsumer : AsyncDefaultBasicConsumer, IDisposable
{
    private static readonly DefaultObjectPool<MessageProperties> messagePropertiesPool = new(
        new MessagePropertiesPooledObjectPolicy(), 64
    );


    private sealed class MessagePropertiesPooledObjectPolicy : IPooledObjectPolicy<MessageProperties>
    {
        public MessageProperties Create() => new();

        public bool Return(MessageProperties properties)
        {
            properties.Reset();
            return true;
        }
    }


    private readonly CancellationTokenSource cts = new();
    private readonly AsyncCountdownEvent onTheFlyMessages = new();

    private readonly IEventBus eventBus;
    private readonly IHandlerRunner handlerRunner;
    private readonly MessageHandler messageHandler;
    private readonly ILogger logger;
    private readonly Queue queue;
    private readonly bool autoAck;

    private volatile bool disposed;

    public AsyncBasicConsumer(
        ILogger logger,
        IModel model,
        in Queue queue,
        bool autoAck,
        IEventBus eventBus,
        IHandlerRunner handlerRunner,
        MessageHandler messageHandler
    ) : base(model)
    {
        this.logger = logger;
        this.queue = queue;
        this.autoAck = autoAck;
        this.eventBus = eventBus;
        this.handlerRunner = handlerRunner;
        this.messageHandler = messageHandler;
    }

    public Queue Queue => queue;

    /// <inheritdoc />
    public override async Task OnCancel(params string[] consumerTags)
    {
        await base.OnCancel(consumerTags).ConfigureAwait(false);

        if (logger.IsInfoEnabled())
        {
            logger.InfoFormat(
                "Consumer with consumerTags {consumerTags} has cancelled",
                string.Join(", ", consumerTags)
            );
        }
    }

    public override async Task HandleBasicDeliver(
        string consumerTag,
        ulong deliveryTag,
        bool redelivered,
        string exchange,
        string routingKey,
        IBasicProperties properties,
        ReadOnlyMemory<byte> body
    )
    {
        if (cts.IsCancellationRequested)
            return;

        if (logger.IsDebugEnabled())
        {
            logger.DebugFormat(
                "Message delivered to consumer {consumerTag} with deliveryTag {deliveryTag}",
                consumerTag,
                deliveryTag
            );
        }

        onTheFlyMessages.Increment();
        try
        {
            var messageBody = body;
            var messageReceivedInfo = new MessageReceivedInfo(
                consumerTag, deliveryTag, redelivered, exchange, routingKey, queue.Name
            );
            var messageProperties = messagePropertiesPool.Get();
            try
            {
                messageProperties.CopyFrom(properties);

                eventBus.Publish(new DeliveredMessageEvent(messageReceivedInfo, messageProperties, messageBody));
                var context = new ConsumerExecutionContext(
                    messageHandler, messageReceivedInfo, messageProperties, messageBody
                );
                var ackStrategy = await handlerRunner.InvokeUserMessageHandlerAsync(
                    context, cts.Token
                ).ConfigureAwait(false);

                if (!autoAck)
                {
                    var ackResult = Ack(ackStrategy, messageReceivedInfo);
                    eventBus.Publish(new AckEvent(messageReceivedInfo, messageProperties, messageBody, ackResult));
                }
            }
            finally
            {
                messagePropertiesPool.Return(messageProperties);
            }
        }
        finally
        {
            onTheFlyMessages.Decrement();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        cts.Cancel();
        onTheFlyMessages.Wait();
        cts.Dispose();
        onTheFlyMessages.Dispose();
        eventBus.Publish(new ConsumerModelDisposedEvent(ConsumerTags));
    }

    private AckResult Ack(AckStrategy ackStrategy, MessageReceivedInfo receivedInfo)
    {
        try
        {
            return ackStrategy(Model, receivedInfo.DeliveryTag);
        }
        catch (AlreadyClosedException alreadyClosedException)
        {
            logger.Info(
                alreadyClosedException,
                "Failed to ACK or NACK, message will be retried, receivedInfo={receivedInfo}",
                receivedInfo
            );
        }
        catch (IOException ioException)
        {
            logger.Info(
                ioException,
                "Failed to ACK or NACK, message will be retried, receivedInfo={receivedInfo}",
                receivedInfo
            );
        }
        catch (Exception exception)
        {
            logger.Error(
                exception,
                "Unexpected exception when attempting to ACK or NACK, receivedInfo={receivedInfo}",
                receivedInfo
            );
        }

        return AckResult.Exception;
    }
}
