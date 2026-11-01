using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Gozcu.Infrastructure.Messaging;

/// <summary>
/// Topic exchange'e bağlı geçici bir kuyruktan olay dinler. Kuyruk sunucu adlıdır, bu bağlantıya özeldir (exclusive)
/// ve bağlantı kapanınca silinir (auto-delete): servisin her örneği olayların kendi kopyasını alır, servis kapalıyken
/// biriken olay olmaz. Bu yüzden olaylar yalnızca "durum değişti, yeniden oku" sinyali olarak kullanılmalı; kaybolan
/// olayları çağıranın periyodik yenilemesi telafi eder.
/// </summary>
public sealed class RabbitMqEventSubscriber
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventSubscriber> _logger;

    public RabbitMqEventSubscriber(RabbitMqConnection connection, IOptions<RabbitMqOptions> options, ILogger<RabbitMqEventSubscriber> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Abonelik kurulamazsa ya da kanal kalıcı olarak kapanırsa yeniden denemeden önce beklenecek süre.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Kanalın açık olup olmadığının denetlenme aralığı.</summary>
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Kanal bu süreden uzun kapalı kalırsa (istemcinin otomatik kurtarması başaramadıysa) abonelik baştan kurulur.
    /// </summary>
    public TimeSpan RecoveryGracePeriod { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// İptal edilene kadar <paramref name="bindingKey"/> ile eşleşen olayları dinler. Hiçbir zaman istisna fırlatmaz
    /// (iptal dışında); bağlantı sorunlarında günlüğe yazıp yeniden dener.
    /// </summary>
    /// <param name="onMessage">Her mesaj için çağrılır: yönlendirme anahtarı ve gövde. Gövde yalnızca çağrı süresince geçerlidir.</param>
    /// <param name="onSubscribed">
    /// Abonelik her kurulduğunda ya da kopup geri geldiğinde çağrılır. Arada kaçırılmış olabilecek olayları telafi etmek
    /// için (ör. kataloğu yeniden okumak) kullanılır.
    /// </param>
    public async Task RunAsync(
        string bindingKey,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> onMessage,
        Func<CancellationToken, Task>? onSubscribed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bindingKey);
        ArgumentNullException.ThrowIfNull(onMessage);

        while (!cancellationToken.IsCancellationRequested)
        {
            IChannel? channel = null;
            try
            {
                var connection = await _connection.GetAsync(cancellationToken).ConfigureAwait(false);
                channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var queue = await channel.QueueDeclareAsync(
                    queue: string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await channel.QueueBindAsync(queue.QueueName, _options.Exchange, bindingKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        await onMessage(delivery.RoutingKey, delivery.Body, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "{RoutingKey} olayı işlenemedi", delivery.RoutingKey);
                    }
                };
                await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("'{BindingKey}' olaylarına abone olundu (kuyruk {Queue})", bindingKey, queue.QueueName);
                await NotifySubscribedAsync(onSubscribed, cancellationToken).ConfigureAwait(false);

                await WatchChannelAsync(channel, bindingKey, onSubscribed, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("'{BindingKey}' olaylarına abone olunamadı ({Message}); {Delay} sonra tekrar denenecek",
                    bindingKey, ex.Message, RetryDelay);
            }
            finally
            {
                await DisposeQuietlyAsync(channel).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Kanal açık olduğu sürece bekler. Bağlantı koparsa istemci kütüphanesi kanalı, kuyruğu ve bağlamayı kendisi
    /// kurtarır; kanal geri açıldığında <paramref name="onSubscribed"/> yeniden çağrılır. Kurtarma
    /// <see cref="RecoveryGracePeriod"/> içinde olmazsa döner ve abonelik baştan kurulur.
    /// </summary>
    private async Task WatchChannelAsync(
        IChannel channel, string bindingKey, Func<CancellationToken, Task>? onSubscribed, CancellationToken cancellationToken)
    {
        var wasOpen = true;
        long? closedSince = null;
        while (true)
        {
            await Task.Delay(MonitorInterval, cancellationToken).ConfigureAwait(false);
            if (channel.IsOpen)
            {
                if (!wasOpen)
                {
                    _logger.LogInformation("'{BindingKey}' aboneliği yeniden kuruldu", bindingKey);
                    await NotifySubscribedAsync(onSubscribed, cancellationToken).ConfigureAwait(false);
                }

                wasOpen = true;
                closedSince = null;
                continue;
            }

            if (wasOpen)
            {
                _logger.LogWarning("'{BindingKey}' aboneliğinin kanalı kapandı; kurtarma bekleniyor", bindingKey);
            }

            wasOpen = false;
            closedSince ??= Environment.TickCount64;
            if (Environment.TickCount64 - closedSince.Value > RecoveryGracePeriod.TotalMilliseconds)
            {
                _logger.LogWarning("'{BindingKey}' aboneliği {Grace} içinde kurtarılamadı; baştan kurulacak", bindingKey, RecoveryGracePeriod);
                return;
            }
        }
    }

    private async Task NotifySubscribedAsync(Func<CancellationToken, Task>? onSubscribed, CancellationToken cancellationToken)
    {
        if (onSubscribed is null)
        {
            return;
        }

        try
        {
            await onSubscribed(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Abonelik bildirimi işlenemedi");
        }
    }

    private static async Task DisposeQuietlyAsync(IChannel? channel)
    {
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Kopmuş bir kanalı kapatırken çıkan hatalar önemsizdir.
        }
    }
}
